using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
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
    public async Task Publishes_discovery_state_and_availability()
    {
        if (!BrokerAvailable()) return;

        var received = new ConcurrentDictionary<string, string>();
        var factory = new MqttClientFactory();
        using var sub = factory.CreateMqttClient();
        sub.ApplicationMessageReceivedAsync += e =>
        {
            received[e.ApplicationMessage.Topic] = e.ApplicationMessage.ConvertPayloadToString();
            return Task.CompletedTask;
        };
        await sub.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", 1883).Build());
        await sub.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter("it-hw/#").WithTopicFilter("it-ha/#").Build());

        var pc = new HaDevice("pc", "Test Rig", "Windows", null, "0.1.0");
        var wv = new HaDevice("wireview_abc", "WireView Pro II", "WireView Pro II", "Thermal Grizzly");
        var payloads = new HomeAssistantPayloads("it-ha", "it-hw", "Test Rig", pc, "0.1.0");
        var publisher = new MqttPublisher(new MqttOptions { Host = "127.0.0.1", DiscoveryPrefix = "it-ha", BaseTopic = "it-hw" }, payloads, NullLogger<MqttPublisher>.Instance);

        var power = new SensorDefinition("wireview_total_power", "Power", wv, "W", "power", Precision: 1);
        var fps = new SensorDefinition("fps", "Frame rate", pc, "fps", Precision: 0);
        await publisher.PublishReadingsAsync([new(power, 412.3456), new(fps, 143.7)], CancellationToken.None);

        await WaitFor(() => received.ContainsKey("it-hw/test_rig/wireview_abc/state") && received.ContainsKey("it-hw/test_rig/pc/state"));
        Assert.Equal("online", received["it-hw/test_rig/status"]);
        Assert.Contains("\"unique_id\":\"test_rig_wireview_total_power\"", received["it-ha/sensor/test_rig/wireview_total_power/config"]);
        Assert.Equal("""{"wireview_total_power":412.35}""", received["it-hw/test_rig/wireview_abc/state"]);
        Assert.Equal("""{"fps":143.7}""", received["it-hw/test_rig/pc/state"]);

        // HA restart -> discovery is re-sent on the next cycle.
        received.TryRemove("it-ha/sensor/test_rig/fps/config", out _);
        await sub.PublishAsync(new MqttApplicationMessageBuilder().WithTopic("it-ha/status").WithPayload("online").Build());
        await Task.Delay(300);
        await publisher.PublishReadingsAsync([new(fps, 60)], CancellationToken.None);
        await WaitFor(() => received.ContainsKey("it-ha/sensor/test_rig/fps/config"));

        await publisher.DisposeAsync();
        await WaitFor(() => received["it-hw/test_rig/status"] == "offline");
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(5)) Assert.Fail("timed out waiting for MQTT messages");
            await Task.Delay(50);
        }
    }
}
