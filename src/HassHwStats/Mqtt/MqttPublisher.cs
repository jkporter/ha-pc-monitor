using System.Text;
using HassHwStats.Core;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;

namespace HassHwStats.Mqtt;

/// <summary>Keeps an MQTT connection, publishes HA discovery (retained) and the state of every device each interval.</summary>
public sealed class MqttPublisher : IAsyncDisposable
{
    private readonly MqttOptions _options;
    private readonly HomeAssistantPayloads _payloads;
    private readonly ILogger _log;
    private readonly IMqttClient _client;
    private readonly MqttClientOptions _clientOptions;
    private readonly Dictionary<string, SensorDefinition> _announced = new();
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
            lock (_gate) _announced.Clear(); // a fresh session re-announces everything
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
                lock (_gate) _announced.Clear();
                await PublishAsync(_payloads.AvailabilityTopic, "online", retain: true, ct);
            }

            bool announcedAny = false;
            foreach (var v in values)
            {
                var def = v.Definition;
                bool isNew;
                lock (_gate) isNew = _announced.TryAdd(def.Key, def);
                if (!isNew) continue;
                await PublishAsync(_payloads.DiscoveryTopic(def), _payloads.DiscoveryPayload(def), retain: true, ct);
                announcedAny = true;
            }

            // Give HA a moment to create freshly discovered entities before their first state arrives.
            if (announcedAny) await Task.Delay(500, ct);

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
