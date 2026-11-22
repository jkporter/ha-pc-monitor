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
        Assert.Equal("homeassistant/device/gaming_pc_gpu_nvidia_0/config", _p.DeviceDiscoveryTopic(Gpu));
        Assert.Equal("homeassistant/sensor/gaming_pc/gpu_temp/config", _p.LegacyDiscoveryTopic(def));
        Assert.Equal("homeassistant/+/gaming_pc/+/config", _p.LegacyDiscoveryFilter);
        Assert.Equal("hass-hw-stats/gaming_pc/gpu_nvidia_0/state", _p.StateTopic(Gpu));
        Assert.Equal("hass-hw-stats/gaming_pc/status", _p.AvailabilityTopic);
        var bin = def with { Key = "fault", Kind = EntityKind.BinarySensor };
        Assert.StartsWith("homeassistant/binary_sensor/", _p.LegacyDiscoveryTopic(bin));
    }

    [Fact]
    public void Device_payload_lists_components()
    {
        var temp = new SensorDefinition("gpu_temp", "GPU Core temperature", Gpu, "°C", "temperature", Precision: 1);
        var fault = new SensorDefinition("gpu_fault", "Fault", Gpu, DeviceClass: "problem", StateClass: null, Kind: EntityKind.BinarySensor);
        using var doc = JsonDocument.Parse(_p.DevicePayload(Gpu, [temp, fault]));
        var r = doc.RootElement;

        var dev = r.GetProperty("device");
        Assert.Equal("gaming_pc_gpu_nvidia_0", dev.GetProperty("identifiers")[0].GetString());
        Assert.Equal("Gaming PC GPU", dev.GetProperty("name").GetString());
        Assert.Equal("gaming_pc_pc", dev.GetProperty("via_device").GetString());
        Assert.Equal("HassHwStats", r.GetProperty("origin").GetProperty("name").GetString());
        Assert.Equal("hass-hw-stats/gaming_pc/gpu_nvidia_0/state", r.GetProperty("state_topic").GetString());

        var cmps = r.GetProperty("components");
        var t = cmps.GetProperty("gpu_temp");
        Assert.Equal("sensor", t.GetProperty("platform").GetString());
        // Same unique_id as the old single-component config, so HA keeps the entity ID and history.
        Assert.Equal("gaming_pc_gpu_temp", t.GetProperty("unique_id").GetString());
        Assert.Equal("{{ value_json.gpu_temp }}", t.GetProperty("value_template").GetString());
        Assert.Equal("°C", t.GetProperty("unit_of_measurement").GetString());
        Assert.Equal("measurement", t.GetProperty("state_class").GetString());
        Assert.Equal(1, t.GetProperty("suggested_display_precision").GetInt32());
        Assert.Equal("all", t.GetProperty("availability_mode").GetString());
        Assert.Equal(2, t.GetProperty("availability").GetArrayLength());
        Assert.False(t.TryGetProperty("device", out _));

        var f = cmps.GetProperty("gpu_fault");
        Assert.Equal("binary_sensor", f.GetProperty("platform").GetString());
        Assert.False(f.TryGetProperty("state_class", out _));
    }

    [Fact]
    public void Pc_device_has_no_via_device()
    {
        var def = new SensorDefinition("fps", "Frame rate", Pc, "fps");
        using var doc = JsonDocument.Parse(_p.DevicePayload(Pc, [def]));
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
