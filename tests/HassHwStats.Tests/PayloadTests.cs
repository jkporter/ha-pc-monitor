using System.Text.Json;
using HassHwStats.Core;
using HassHwStats.Mqtt;
using Xunit;

namespace HassHwStats.Tests;

public class PayloadTests
{
    private static readonly HaDevice Pc = new("pc", "Gaming PC", "Windows", null, "0.1.0");
    private static readonly HaDevice Gpu = new("gpu_nvidia_0", "Gaming PC GPU", "NVIDIA GeForce RTX 5090", "NVIDIA");
    private readonly HomeAssistantPayloads _p = new("homeassistant", "hass-hw-stats", "Gaming PC", Pc, "0.1.0");

    [Fact]
    public void Slugs() => Assert.Equal("gpu_nvidia_0_temperature_2", Slug.Make("/gpu-nvidia/0/temperature/2"));

    [Fact]
    public void Topics()
    {
        var def = new SensorDefinition("gpu_temp", "GPU Core temperature", Gpu, "°C", "temperature");
        Assert.Equal("homeassistant/sensor/gaming_pc/gpu_temp/config", _p.DiscoveryTopic(def));
        Assert.Equal("hass-hw-stats/gaming_pc/gpu_nvidia_0/state", _p.StateTopic(Gpu));
        Assert.Equal("hass-hw-stats/gaming_pc/status", _p.AvailabilityTopic);
        var bin = def with { Key = "fault", Kind = EntityKind.BinarySensor };
        Assert.StartsWith("homeassistant/binary_sensor/", _p.DiscoveryTopic(bin));
    }

    [Fact]
    public void Discovery_payload_has_device_and_availability()
    {
        var def = new SensorDefinition("gpu_temp", "GPU Core temperature", Gpu, "°C", "temperature", Precision: 1);
        using var doc = JsonDocument.Parse(_p.DiscoveryPayload(def));
        var r = doc.RootElement;
        Assert.Equal("gaming_pc_gpu_temp", r.GetProperty("unique_id").GetString());
        Assert.Equal("{{ value_json.gpu_temp }}", r.GetProperty("value_template").GetString());
        Assert.Equal("°C", r.GetProperty("unit_of_measurement").GetString());
        Assert.Equal("measurement", r.GetProperty("state_class").GetString());
        Assert.Equal(1, r.GetProperty("suggested_display_precision").GetInt32());
        Assert.Equal("all", r.GetProperty("availability_mode").GetString());
        Assert.Equal(2, r.GetProperty("availability").GetArrayLength());
        var dev = r.GetProperty("device");
        Assert.Equal("gaming_pc_gpu_nvidia_0", dev.GetProperty("identifiers")[0].GetString());
        Assert.Equal("gaming_pc_pc", dev.GetProperty("via_device").GetString());
    }

    [Fact]
    public void Pc_device_has_no_via_device()
    {
        var def = new SensorDefinition("fps", "Frame rate", Pc, "fps");
        using var doc = JsonDocument.Parse(_p.DiscoveryPayload(def));
        Assert.False(doc.RootElement.GetProperty("device").TryGetProperty("via_device", out _));
    }

    [Fact]
    public void State_payload_rounds_and_handles_types()
    {
        var t = new SensorDefinition("t", "t", Gpu, "°C", Precision: 1);
        var f = new SensorDefinition("f", "f", Gpu, Kind: EntityKind.BinarySensor);
        var s = new SensorDefinition("s", "s", Gpu);
        var n = new SensorDefinition("n", "n", Gpu);
        var json = HomeAssistantPayloads.StatePayload([new(t, 61.23456), new(f, true), new(s, "None"), new(n, null), new(new SensorDefinition("i", "i", Gpu), 42)]);
        Assert.Equal("""{"t":61.23,"f":true,"s":"None","n":null,"i":42}""", json);
    }
}
