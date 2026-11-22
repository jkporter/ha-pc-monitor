using HassHwStats.Core;
using HassHwStats.Sources;
using Xunit;

namespace HassHwStats.Tests;

public class DashboardTests
{
    [Fact]
    public void Builds_views_with_predicted_entity_ids()
    {
        var pc = new HaDevice("pc", "JKP-Gaming");
        var cpu = new HaDevice("amdcpu_0", "JKP-Gaming CPU", "AMD Ryzen 7 9800X3D");
        var gpu = new HaDevice("gpu_nvidia_0", "JKP-Gaming GPU", "NVIDIA GeForce RTX 5090");
        var defs = new List<SensorDefinition>
        {
            new("fps", "Frame rate", pc, "fps", Group: "fps"),
            new("amdcpu_0_load_0", "CPU Total load", cpu, "%", Group: "cpu.main.Load"),
            new("gpu_nvidia_0_load_0", "GPU Core load", gpu, "%", Group: "gpu.main.Load"),
            new("gpu_nvidia_0_temperature_0", "GPU Core temperature", gpu, "°C", Group: "gpu.main.Temperature"),
        };
        for (int i = 10; i >= 1; i--)
        {
            defs.Add(new($"amdcpu_0_load_{i + 1}", $"CPU Core #{i} load", cpu, "%", Group: "cpu.core.Load"));
            defs.Add(new($"amdcpu_0_clock_{i}", $"Core #{i} clock", cpu, "MHz", Group: "cpu.core.Clock"));
        }
        defs.AddRange(typeof(WireViewEntities).GetProperties()
            .Select(p => p.GetValue(new WireViewEntities(new HaDevice("wireview", "WireView Pro II"))))
            .SelectMany(v => v switch { SensorDefinition d => [d], SensorDefinition[] a => a, _ => Array.Empty<SensorDefinition>() }));

        var yaml = DashboardWriter.Build("JKP-Gaming", defs);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "hasshwstats-dashboard-test.yaml"), yaml);

        Assert.Contains("entity: sensor.jkp_gaming_cpu_cpu_total_load", yaml);
        Assert.Contains("entity: sensor.jkp_gaming_gpu_gpu_core_load", yaml);
        Assert.Contains("entity: sensor.jkp_gaming_frame_rate", yaml);
        Assert.Contains("entity: sensor.wireview_pro_ii_power", yaml);
        Assert.Contains("entity: binary_sensor.wireview_pro_ii_fault", yaml);
        Assert.Contains("entity: sensor.wireview_pro_ii_pin_6_current", yaml);
        Assert.Contains("- title: CPU cores", yaml);
        Assert.Contains("name: \"Core #3\"", yaml);
        // Natural order: core 2 before core 10.
        Assert.True(yaml.IndexOf("sensor.jkp_gaming_cpu_core_2_clock") < yaml.IndexOf("sensor.jkp_gaming_cpu_core_10_clock"));
    }
}
