using System.Collections.Concurrent;
using System.Text;
using HassHwStats.Core;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;

namespace HassHwStats.Mqtt;

/// <summary>
/// Keeps an MQTT connection, publishes HA device discovery (retained) and the state of every device each interval.
/// Migrates entities left behind by the single-component discovery of versions before 0.4 without changing their entity IDs.
/// </summary>
public sealed class MqttPublisher : IAsyncDisposable
{
    private readonly MqttOptions _options;
    private readonly HomeAssistantPayloads _payloads;
    private readonly ILogger _log;
    private readonly IMqttClient _client;
    private readonly MqttClientOptions _clientOptions;
    // Every entity seen per device during this run. A device config always lists all of them, because HA removes
    // entities that disappear from a device config.
    private readonly Dictionary<string, Dictionary<string, SensorDefinition>> _components = new();
    private readonly Dictionary<string, string> _publishedConfig = new();
    private readonly ConcurrentDictionary<string, byte> _legacyTopics = new();
    private readonly object _gate = new();
    private volatile bool _rediscover;
    private DateTime _nextConnectAttempt = DateTime.MinValue;

    public MqttPublisher(MqttOptions options, HomeAssistantPayloads payloads, ILogger<MqttPublisher> log)
    {
        _options = options;
        _payloads = payloads;
        _log = log;
        var factory = new MqttClientFactory();
        _client = factory.CreateMqttClient();

        var b = new MqttClientOptionsBuilder()
            .WithTcpServer(options.Host, options.Port)
            .WithClientId($"hass-hw-stats-{payloads.NodeId}")
            .WithCleanSession()
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
            .WithWillTopic(payloads.AvailabilityTopic)
            .WithWillPayload("offline")
            .WithWillRetain()
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);
        if (!string.IsNullOrEmpty(options.Username)) b = b.WithCredentials(options.Username, options.Password);
        if (options.UseTls)
        {
            b = b.WithTlsOptions(t =>
            {
                t.UseTls();
                if (options.AllowUntrustedCertificate) t.WithCertificateValidationHandler(_ => true);
            });
        }
        _clientOptions = b.Build();

        _client.ApplicationMessageReceivedAsync += e =>
        {
            var topic = e.ApplicationMessage.Topic;
            if (topic != payloads.HomeAssistantStatusTopic && !topic.Contains("/device/"))
            {
                // A retained single-component config from an older version (ignore our own migrate/clear messages).
                var body = e.ApplicationMessage.ConvertPayloadToString();
                if (!string.IsNullOrEmpty(body) && body != HomeAssistantPayloads.MigratePayload) _legacyTopics.TryAdd(topic, 0);
                return Task.CompletedTask;
            }
            // Home Assistant restarted: it forgets non-retained state, so announce everything again.
            if (e.ApplicationMessage.Topic == payloads.HomeAssistantStatusTopic && e.ApplicationMessage.ConvertPayloadToString() == "online")
            {
                _log.LogInformation("Home Assistant came online, re-sending discovery");
                _rediscover = true;
            }
            return Task.CompletedTask;
        };
        _client.DisconnectedAsync += e =>
        {
            if (e.ClientWasConnected) _log.LogWarning("Disconnected from MQTT broker: {Reason}", e.Exception?.Message ?? e.Reason.ToString());
            return Task.CompletedTask;
        };
    }

    public bool IsConnected => _client.IsConnected;

    private async Task<bool> EnsureConnectedAsync(CancellationToken ct)
    {
        if (_client.IsConnected) return true;
        if (DateTime.UtcNow < _nextConnectAttempt) return false;
        try
        {
            var result = await _client.ConnectAsync(_clientOptions, ct);
            if (result.ResultCode != MqttClientConnectResultCode.Success)
            {
                _log.LogError("MQTT broker refused the connection: {Code} {Reason}", result.ResultCode, result.ReasonString);
                _nextConnectAttempt = DateTime.UtcNow.AddSeconds(30);
                return false;
            }
            _log.LogInformation("Connected to MQTT broker {Host}:{Port}", _options.Host, _options.Port);
            await _client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter(_payloads.HomeAssistantStatusTopic).Build(), ct);
            await PublishAsync(_payloads.AvailabilityTopic, "online", retain: true, ct);
            // Retained legacy configs arrive right after subscribing; give them a moment.
            await _client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter(_payloads.LegacyDiscoveryFilter).Build(), ct);
            await Task.Delay(1000, ct);
            if (!_legacyTopics.IsEmpty) _log.LogInformation("Found {Count} single-component discovery configs from an older version; migrating them to device discovery", _legacyTopics.Count);
            lock (_gate) _publishedConfig.Clear(); // a fresh session re-announces everything
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("Cannot connect to MQTT broker {Host}:{Port}: {Message}", _options.Host, _options.Port, ex.Message);
            _nextConnectAttempt = DateTime.UtcNow.AddSeconds(15);
            return false;
        }
    }

    public async Task PublishReadingsAsync(IReadOnlyList<SensorValue> values, CancellationToken ct)
    {
        if (!await EnsureConnectedAsync(ct)) return;
        try
        {
            if (_rediscover)
            {
                _rediscover = false;
                lock (_gate) _publishedConfig.Clear();
                await PublishAsync(_payloads.AvailabilityTopic, "online", retain: true, ct);
            }

            bool announcedAny = false;
            var migrated = new List<string>();
            foreach (var group in values.GroupBy(v => v.Definition.Device.Id))
            {
                var device = group.Last().Definition.Device; // latest details (e.g. firmware after connecting)
                string payload;
                List<SensorDefinition> defs;
                lock (_gate)
                {
                    if (!_components.TryGetValue(device.Id, out var comps)) _components[device.Id] = comps = new();
                    foreach (var v in group) comps[v.Definition.Key] = v.Definition;
                    defs = comps.Values.ToList();
                    payload = _payloads.DevicePayload(device, defs);
                    if (_publishedConfig.TryGetValue(device.Id, out var last) && last == payload) continue;
                }

                // HA's documented migration: tell each old config to hand its entity over, then publish the device config.
                var legacy = defs.Select(_payloads.LegacyDiscoveryTopic).Where(_legacyTopics.ContainsKey).ToList();
                foreach (var topic in legacy) await PublishAsync(topic, HomeAssistantPayloads.MigratePayload, retain: true, ct);
                if (legacy.Count > 0) await Task.Delay(500, ct);

                await PublishAsync(_payloads.DeviceDiscoveryTopic(device), payload, retain: true, ct);
                lock (_gate) _publishedConfig[device.Id] = payload;
                migrated.AddRange(legacy);
                announcedAny = true;
            }

            // Give HA a moment to create freshly discovered entities before their first state arrives.
            if (announcedAny) await Task.Delay(500, ct);

            // Then clear the old retained configs so they are gone for good.
            foreach (var topic in migrated)
            {
                await PublishAsync(topic, "", retain: true, ct);
                _legacyTopics.TryRemove(topic, out _);
            }
            if (migrated.Count > 0) _log.LogInformation("Migrated {Count} entities to device discovery", migrated.Count);

            foreach (var group in values.GroupBy(v => v.Definition.Device.Id))
            {
                var device = group.First().Definition.Device;
                await PublishAsync(_payloads.StateTopic(device), HomeAssistantPayloads.StatePayload(group), retain: false, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("MQTT publish failed: {Message}", ex.Message);
        }
    }

    private Task PublishAsync(string topic, string payload, bool retain, CancellationToken ct) =>
        _client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(Encoding.UTF8.GetBytes(payload))
            .WithRetainFlag(retain)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build(), ct);

    public async ValueTask DisposeAsync()
    {
        if (_client.IsConnected)
        {
            try
            {
                await PublishAsync(_payloads.AvailabilityTopic, "offline", retain: true, CancellationToken.None);
                await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().WithReason(MqttClientDisconnectOptionsReason.NormalDisconnection).Build());
            }
            catch { /* shutting down */ }
        }
        _client.Dispose();
    }
}
