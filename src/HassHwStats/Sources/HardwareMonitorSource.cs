using System.Text.RegularExpressions;
using HassHwStats.Core;
using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using SensorValue = HassHwStats.Core.SensorValue;

namespace HassHwStats.Sources;

/// <summary>CPU, GPU, RAM (and optionally motherboard, storage, network) sensors from LibreHardwareMonitor.</summary>
public sealed class HardwareMonitorSource(HardwareOptions options, string nodeName, ILogger<HardwareMonitorSource> log) : ISensorSource
{
    private Computer? _computer;
    private readonly bool _all = string.Equals(options.Mode, "All", StringComparison.OrdinalIgnoreCase);
    private readonly Regex[] _include = options.Include.Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToArray();
    private readonly Regex[] _exclude = options.Exclude.Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToArray();
    private readonly Dictionary<string, SensorDefinition?> _defs = new();
    private readonly Dictionary<string, HaDevice> _devices = new();

    public string Name => "LibreHardwareMonitor";

    public void Start()
    {
        _computer = new Computer
        {
            IsCpuEnabled = options.Cpu,
            IsGpuEnabled = options.Gpu,
            IsMemoryEnabled = options.Memory,
            IsMotherboardEnabled = options.Motherboard,
            IsStorageEnabled = options.Storage,
            IsNetworkEnabled = options.Network,
        };
        _computer.Open();
        foreach (var hw in _computer.Hardware)
            log.LogInformation("Found {Type}: {Name} ({Sensors} sensors)", hw.HardwareType, hw.Name, CountSensors(hw));
        if (!_computer.Hardware.Any(h => h.HardwareType == HardwareType.Cpu && h.Sensors.Any(s => s.SensorType == SensorType.Temperature)))
            log.LogWarning("No CPU temperature sensors found. Run as administrator and install PawnIO (https://pawnio.eu) for CPU temperatures and power.");
    }

    private static int CountSensors(IHardware hw) => hw.Sensors.Length + hw.SubHardware.Sum(CountSensors);

    public IReadOnlyList<SensorValue> Poll()
    {
        if (_computer is null) return [];
        var values = new List<SensorValue>(128);
        foreach (var hw in _computer.Hardware)
        {
            hw.Update();
            foreach (var sub in hw.SubHardware) sub.Update();
            Collect(hw, hw, values);
            if (!_all && hw.HardwareType == HardwareType.Cpu) AddCpuClockSummary(hw, values);
        }
        return values;
    }

    private void Collect(IHardware root, IHardware hw, List<SensorValue> values)
    {
        foreach (var s in hw.Sensors)
        {
            var def = Definition(root, s);
            if (def is not null) values.Add(new SensorValue(def, s.Value is { } v && float.IsFinite(v) ? (double)v : null));
        }
        foreach (var sub in hw.SubHardware) Collect(root, sub, values);
    }

    private SensorDefinition? Definition(IHardware root, ISensor s)
    {
        var id = s.Identifier.ToString();
        if (_defs.TryGetValue(id, out var cached)) return cached;

        var path = $"{s.Hardware.Name}/{s.SensorType}/{s.Name}";
        bool publish = !_exclude.Any(r => r.IsMatch(path)) && (_all || _include.Any(r => r.IsMatch(path)) || IsCurated(root.HardwareType, s));
        SensorDefinition? def = null;
        if (publish)
        {
            var (unit, cls, icon, precision, stateClass) = Describe(s.SensorType);
            def = new SensorDefinition(Slug.Make(id), SensorName(root, s), Device(root), unit, cls, stateClass, icon, precision,
                Group: $"{GroupLabel(root.HardwareType)}.{(IsPerCore(root.HardwareType, s) ? "core" : "main")}.{s.SensorType}");
            log.LogDebug("Publishing {Path} as {Key}", path, def.Key);
        }
        _defs[id] = def;
        return def;
    }

    /// <summary>Avoid HA names like "GPU Core GPU Core": the device name already says which hardware it is.</summary>
    private static string SensorName(IHardware root, ISensor s)
    {
        string kind = s.SensorType switch
        {
            SensorType.Load => "load",
            SensorType.Clock => "clock",
            SensorType.Temperature => "temperature",
            SensorType.Power => "power",
            SensorType.Voltage => "voltage",
            SensorType.Current => "current",
            SensorType.Fan => "fan speed",
            SensorType.Control => "fan duty",
            _ => "",
        };
        string name = s.Name;
        if (s.SensorType is SensorType.Data or SensorType.SmallData) kind = "";
        var full = kind.Length == 0 || name.Contains(kind, StringComparison.OrdinalIgnoreCase) ? name : $"{name} {kind}";
        // Sub-hardware (e.g. a Super I/O chip on the motherboard) gets its name in front.
        return s.Hardware != root ? $"{s.Hardware.Name} {full}" : full;
    }

    private HaDevice Device(IHardware root)
    {
        var id = root.Identifier.ToString();
        if (_devices.TryGetValue(id, out var d)) return d;
        string? maker = root.HardwareType switch
        {
            HardwareType.GpuNvidia => "NVIDIA",
            HardwareType.GpuAmd => "AMD",
            HardwareType.GpuIntel => "Intel",
            HardwareType.Cpu when root.Name.Contains("AMD", StringComparison.OrdinalIgnoreCase) => "AMD",
            HardwareType.Cpu when root.Name.Contains("Intel", StringComparison.OrdinalIgnoreCase) => "Intel",
            _ => null,
        };
        string label = root.HardwareType switch
        {
            HardwareType.Cpu => "CPU",
            HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => "GPU",
            HardwareType.Memory => "RAM",
            _ => root.HardwareType.ToString(),
        };
        // Device name e.g. "Gaming-PC GPU"; the model carries the full product name.
        d = new HaDevice(Slug.Make(id), $"{nodeName} {label}", root.Name, maker);
        if (_devices.Values.Any(x => x.Name == d.Name)) d = d with { Name = $"{nodeName} {root.Name}" };
        _devices[id] = d;
        return d;
    }

    // Overlay-style defaults. Names follow LibreHardwareMonitor's sensor names for Intel/AMD CPUs and NVIDIA/AMD GPUs.
    private static readonly Regex CpuLoad = new(@"^CPU (Total|Core Max)$");
    private static readonly Regex CpuTemp = new(@"^(Core \(Tctl/Tdie\)|CPU Package|Core Max|Core Average|CCD\d+ \(Tdie\))$");
    private static readonly Regex CpuPower = new(@"^(Package|CPU Package|CPU Cores)$");
    private static readonly Regex CpuVoltage = new(@"^(CPU Core|Core \(SVI\d TFN\))$");
    private static readonly Regex GpuLoad = new(@"^(GPU|D3D) (Core|Memory Controller|Video Engine|Memory|Bus|3D|Video Decode|Video Encode)$");
    private static readonly Regex GpuClock = new(@"^GPU (Core|Memory|Shader)$");
    private static readonly Regex GpuTemp = new(@"^GPU (Core|Hot ?Spot|Memory Junction|Memory)$");
    private static readonly Regex GpuPower = new(@"^GPU (Package|Power|Board Power|Core)$");
    private static readonly Regex GpuMemory = new(@"^(GPU|D3D Dedicated) Memory (Used|Total|Free)$");
    private static readonly Regex RamLoad = new(@"^Memory$");
    private static readonly Regex RamData = new(@"^Memory (Used|Available)$");

    private static string GroupLabel(HardwareType t) => t switch
    {
        HardwareType.Cpu => "cpu",
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => "gpu",
        HardwareType.Memory => "ram",
        _ => t.ToString().ToLowerInvariant(),
    };

    // Per-core sensor names: "CPU Core #3" / "CPU Core #3 Thread #2" (load), "Core #3" / "P-Core #3" / "E-Core #1"
    // (clock, temperature), "Core #3 (SMU)" (AMD per-core power).
    private static readonly Regex CoreLoad = new(@"^CPU (P-|E-)?Core #\d+( Thread #\d+)?$");
    private static readonly Regex CoreSensor = new(@"^(P-|E-)?Core #\d+$");
    private static readonly Regex CorePower = new(@"^Core #\d+ \(SMU\)$");

    private static bool IsPerCore(HardwareType type, ISensor s) =>
        type == HardwareType.Cpu && s.SensorType switch
        {
            SensorType.Load => CoreLoad.IsMatch(s.Name),
            SensorType.Clock or SensorType.Temperature => CoreSensor.IsMatch(s.Name),
            SensorType.Power => CorePower.IsMatch(s.Name),
            _ => false,
        };

    private bool IsCurated(HardwareType type, ISensor s)
    {
        var n = s.Name;
        if (options.PerCoreCpu && IsPerCore(type, s)) return true;
        switch (type)
        {
            case HardwareType.Cpu:
                return s.SensorType switch
                {
                    SensorType.Load => CpuLoad.IsMatch(n),
                    SensorType.Temperature => CpuTemp.IsMatch(n),
                    SensorType.Power => CpuPower.IsMatch(n),
                    SensorType.Voltage => CpuVoltage.IsMatch(n),
                    _ => false,
                };
            case HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel:
                return s.SensorType switch
                {
                    SensorType.Load => GpuLoad.IsMatch(n),
                    SensorType.Clock => GpuClock.IsMatch(n),
                    SensorType.Temperature => GpuTemp.IsMatch(n),
                    SensorType.Power => GpuPower.IsMatch(n),
                    SensorType.Fan or SensorType.Control => true,
                    SensorType.SmallData => GpuMemory.IsMatch(n),
                    _ => false,
                };
            case HardwareType.Memory:
                if (s.Hardware.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)) return false;
                return s.SensorType switch
                {
                    SensorType.Load => RamLoad.IsMatch(n),
                    SensorType.Data => RamData.IsMatch(n),
                    _ => false,
                };
            case HardwareType.Motherboard:
                return s.SensorType is SensorType.Temperature or SensorType.Fan;
            case HardwareType.Storage:
                return s.SensorType == SensorType.Temperature || (s.SensorType == SensorType.Load && n == "Used Space");
            case HardwareType.Network:
                return s.SensorType == SensorType.Throughput;
            default:
                return false;
        }
    }

    /// <summary>A per-core clock list is too noisy for HA; publish the highest and average core clock instead.</summary>
    private void AddCpuClockSummary(IHardware cpu, List<SensorValue> values)
    {
        var clocks = cpu.Sensors
            .Where(s => s.SensorType == SensorType.Clock && s.Name.Contains("Core #") && s.Value is > 0)
            .Select(s => (double)s.Value!.Value)
            .ToList();
        if (clocks.Count == 0) return;
        var dev = Device(cpu);
        var max = GetDerived($"{dev.Id}_clock_core_max", "Core clock (highest)", dev);
        var avg = GetDerived($"{dev.Id}_clock_core_avg", "Core clock (average)", dev);
        values.Add(new SensorValue(max, clocks.Max()));
        values.Add(new SensorValue(avg, clocks.Average()));
    }

    private SensorDefinition GetDerived(string key, string name, HaDevice dev)
    {
        if (_defs.TryGetValue(key, out var d) && d is not null) return d;
        d = new SensorDefinition(key, name, dev, "MHz", "frequency", "measurement", null, 0, Group: "cpu.main.Clock");
        _defs[key] = d;
        return d;
    }

    private static (string? Unit, string? DeviceClass, string? Icon, int? Precision, string? StateClass) Describe(SensorType t) => t switch
    {
        SensorType.Voltage => ("V", "voltage", null, 3, "measurement"),
        SensorType.Current => ("A", "current", null, 2, "measurement"),
        SensorType.Power => ("W", "power", null, 1, "measurement"),
        SensorType.Clock => ("MHz", "frequency", null, 0, "measurement"),
        SensorType.Temperature => ("°C", "temperature", null, 1, "measurement"),
        SensorType.Load => ("%", null, "mdi:gauge", 1, "measurement"),
        SensorType.Frequency => ("Hz", "frequency", null, 0, "measurement"),
        SensorType.Fan => ("RPM", null, "mdi:fan", 0, "measurement"),
        SensorType.Flow => ("L/h", "volume_flow_rate", null, 1, "measurement"),
        SensorType.Control => ("%", null, "mdi:fan", 0, "measurement"),
        SensorType.Level => ("%", null, "mdi:battery", 1, "measurement"),
        SensorType.Factor => (null, null, null, 3, "measurement"),
        SensorType.Data => ("GB", "data_size", "mdi:memory", 2, "measurement"),
        SensorType.SmallData => ("MB", "data_size", "mdi:memory", 0, "measurement"),
        SensorType.Throughput => ("B/s", "data_rate", null, 0, "measurement"),
        SensorType.TimeSpan => ("s", "duration", null, 0, "measurement"),
        SensorType.Energy => ("mWh", "energy", null, 0, "total_increasing"),
        SensorType.Noise => ("dB", "sound_pressure", null, 1, "measurement"),
        SensorType.Humidity => ("%", "humidity", null, 1, "measurement"),
        _ => (null, null, null, 2, "measurement"),
    };

    public void Dispose() => _computer?.Close();
}
