using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using HassHwStats.Core;
using HassHwStats.Mqtt;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using Xunit;

namespace HassHwStats.Tests;

/// <summary>Runs against a real broker when one listens on localhost:1883 (e.g. mosquitto); otherwise skipped silently.</summary>
public class MqttIntegrationTests
{
    private static bool BrokerAvailable()
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync("127.0.0.1", 1883).Wait(500) && c.Connected;
        }
        catch { return false; }
    }

    [Fact]
    public async Task Publishes_device_discovery_and_migrates_legacy_configs()
    {
        if (!BrokerAvailable()) return;
        var prefix = $"it-ha-{Guid.NewGuid():N}"[..14];
        var baseTopic = prefix + "-hw";

        // Every message in arrival order, plus the latest per topic.
        var log = new ConcurrentQueue<(string Topic, string Payload, bool Retain)>();
        var latest = new ConcurrentDictionary<string, string>();
        var factory = new MqttClientFactory();
        using var sub = factory.CreateMqttClient();
        sub.ApplicationMessageReceivedAsync += e =>
        {
            var body = e.ApplicationMessage.ConvertPayloadToString() ?? "";
            log.Enqueue((e.ApplicationMessage.Topic, body, e.ApplicationMessage.Retain));
            latest[e.ApplicationMessage.Topic] = body;
            return Task.CompletedTask;
        };
        await sub.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", 1883).Build());

        // A retained single-component config left behind by v0.3.
        var legacyTopic = $"{prefix}/sensor/test_rig/wireview_total_power/config";
        await sub.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(legacyTopic)
            .WithPayload("""{"name":"Power","unique_id":"test_rig_wireview_total_power"}""").WithRetainFlag().Build());
        await sub.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter($"{prefix}/#").WithTopicFilter($"{baseTopic}/#").Build());

        var pc = new HaDevice("pc", "Test Rig", "Windows", null, "0.4.0");
        var wv = new HaDevice("wireview", "WireView Pro II", "WireView Pro II", "Thermal Grizzly");
        var payloads = new HomeAssistantPayloads(prefix, baseTopic, "Test Rig", pc, "0.4.0");
        var publisher = new MqttPublisher(new MqttOptions { Host = "127.0.0.1", DiscoveryPrefix = prefix, BaseTopic = baseTopic }, payloads, NullLogger<MqttPublisher>.Instance);

        var power = new SensorDefinition("wireview_total_power", "Power", wv, "W", "power", Precision: 1);
        var pin1 = new SensorDefinition("wireview_pin1_current", "Pin 1 current", wv, "A", "current", Precision: 2);
        var fps = new SensorDefinition("fps", "Frame rate", pc, "fps", Precision: 0);
        await publisher.PublishReadingsAsync([new(power, 412.3456), new(pin1, 8.123), new(fps, 143.7)], CancellationToken.None);

        var deviceTopic = $"{prefix}/device/test_rig_wireview/config";
        await WaitFor(() => latest.ContainsKey($"{baseTopic}/test_rig/wireview/state") && latest.GetValueOrDefault(legacyTopic) == "");

        // Order matters for HA: migrate the old config, publish the device config, then clear the old one.
        var msgs = log.ToList();
        int migrate = msgs.FindIndex(m => m.Topic == legacyTopic && m.Payload == HomeAssistantPayloads.MigratePayload);
        int device = msgs.FindIndex(m => m.Topic == deviceTopic);
        int clear = msgs.FindIndex(m => m.Topic == legacyTopic && m.Payload == "");
        Assert.True(migrate >= 0 && device > migrate && clear > device, $"migrate={migrate} device={device} clear={clear}");

        using (var doc = JsonDocument.Parse(latest[deviceTopic]))
        {
            var cmps = doc.RootElement.GetProperty("components");
            Assert.Equal("test_rig_wireview_total_power", cmps.GetProperty("wireview_total_power").GetProperty("unique_id").GetString());
            Assert.True(cmps.TryGetProperty("wireview_pin1_current", out _));
        }
        Assert.True(latest.ContainsKey($"{prefix}/device/test_rig_pc/config"));
        Assert.Equal("online", latest[$"{baseTopic}/test_rig/status"]);
        Assert.Equal("""{"wireview_total_power":412.35,"wireview_pin1_current":8.123}""", latest[$"{baseTopic}/test_rig/wireview/state"]);
        Assert.Equal("""{"fps":143.7}""", latest[$"{baseTopic}/test_rig/pc/state"]);

        // A later reading with fewer values must not drop components from the device config.
        var before = latest[deviceTopic];
        await publisher.PublishReadingsAsync([new(power, 100)], CancellationToken.None);
        await Task.Delay(300);
        Assert.Equal(before, latest[deviceTopic]);

        // HA restart -> device config is re-sent.
        latest.TryRemove(deviceTopic, out _);
        await sub.PublishAsync(new MqttApplicationMessageBuilder().WithTopic($"{prefix}/status").WithPayload("online").Build());
        await Task.Delay(300);
        await publisher.PublishReadingsAsync([new(power, 101)], CancellationToken.None);
        await WaitFor(() => latest.ContainsKey(deviceTopic));

        await publisher.DisposeAsync();
        await WaitFor(() => latest[$"{baseTopic}/test_rig/status"] == "offline");

        // Clean up retained messages on the shared test broker.
        foreach (var t in new[] { deviceTopic, $"{prefix}/device/test_rig_pc/config", $"{baseTopic}/test_rig/status" })
            await sub.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(t).WithPayload("").WithRetainFlag().Build());
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(8)) Assert.Fail("timed out waiting for MQTT messages");
            await Task.Delay(50);
        }
    }
}
