using HassHwStats.Core;

namespace HassHwStats.Sources;

/// <summary>The Home Assistant entities for one WireView Pro II, shared by the USB and HWiNFO readers.</summary>
public sealed class WireViewEntities
{
    public WireViewEntities(HaDevice device)
    {
        Device = device;
        var d = device;
        SensorDefinition S(string key, string name, string? unit, string? cls, int? precision = null, string? icon = null, string? stateClass = "measurement", string? category = null)
            => new($"wireview_{key}", name, d, unit, cls, stateClass, icon, precision, EntityCategory: category,
                Group: key.StartsWith("pin") ? "wireview.pin" : category is not null ? "wireview.diag" : "wireview");

        Status = S("status", "Status", null, null, icon: "mdi:information-outline", stateClass: null, category: "diagnostic");
        Connected = new("wireview_connected", "Connected", d, DeviceClass: "connectivity", StateClass: null, Kind: EntityKind.BinarySensor, EntityCategory: "diagnostic", Group: "wireview.diag");
        TotalPower = S("total_power", "Power", "W", "power", 1);
        TotalCurrent = S("total_current", "Current", "A", "current", 2);
        AvgVoltage = S("avg_voltage", "Voltage", "V", "voltage", 3);
        TempIn = S("temp_in", "Connector input temperature", "°C", "temperature", 1);
        TempOut = S("temp_out", "Connector output temperature", "°C", "temperature", 1);
        TempExt1 = S("temp_ext1", "External temperature 1", "°C", "temperature", 1);
        TempExt2 = S("temp_ext2", "External temperature 2", "°C", "temperature", 1);
        Fan = S("fan_duty", "Fan", "%", null, 0, "mdi:fan");
        Cable = S("cable_rating", "Cable rating", "W", "power", 0, "mdi:power-plug", stateClass: null, category: "diagnostic");
        MaxPin = S("max_pin_current", "Highest pin current", "A", "current", 2);
        Imbalance = S("current_imbalance", "Current imbalance", "%", null, 0, "mdi:scale-unbalanced");
        Fault = new("wireview_fault", "Fault", d, DeviceClass: "problem", StateClass: null, Kind: EntityKind.BinarySensor, Group: "wireview");
        ActiveFaults = S("active_faults", "Active faults", null, null, icon: "mdi:alert", stateClass: null);
        LoggedFaults = S("logged_faults", "Logged faults", null, null, icon: "mdi:alert-circle-outline", stateClass: null, category: "diagnostic");
        PinV = Enumerable.Range(1, 6).Select(i => S($"pin{i}_voltage", $"Pin {i} voltage", "V", "voltage", 3)).ToArray();
        PinA = Enumerable.Range(1, 6).Select(i => S($"pin{i}_current", $"Pin {i} current", "A", "current", 2)).ToArray();
        PinW = Enumerable.Range(1, 6).Select(i => S($"pin{i}_power", $"Pin {i} power", "W", "power", 1)).ToArray();
    }

    public HaDevice Device { get; }
    public SensorDefinition Status { get; }
    public SensorDefinition Connected { get; }
    public SensorDefinition TotalPower { get; }
    public SensorDefinition TotalCurrent { get; }
    public SensorDefinition AvgVoltage { get; }
    public SensorDefinition TempIn { get; }
    public SensorDefinition TempOut { get; }
    public SensorDefinition TempExt1 { get; }
    public SensorDefinition TempExt2 { get; }
    public SensorDefinition Fan { get; }
    public SensorDefinition Cable { get; }
    public SensorDefinition MaxPin { get; }
    public SensorDefinition Imbalance { get; }
    public SensorDefinition Fault { get; }
    public SensorDefinition ActiveFaults { get; }
    public SensorDefinition LoggedFaults { get; }
    public SensorDefinition[] PinV { get; }
    public SensorDefinition[] PinA { get; }
    public SensorDefinition[] PinW { get; }

    /// <summary>HA limits a state to 255 characters.</summary>
    public SensorValue StatusValue(string text) => new(Status, text.Length <= 255 ? text : text[..252] + "...");

    public List<SensorValue> Map(WireViewReading r, bool pins, string status)
    {
        var list = new List<SensorValue>(40)
        {
            StatusValue(status),
            new(Connected, true),
            new(TotalPower, r.TotalPower),
            new(TotalCurrent, r.TotalCurrent),
            new(AvgVoltage, r.AverageVoltage),
            new(TempIn, r.TempIn),
            new(TempOut, r.TempOut),
            new(TempExt1, r.TempExt1),
            new(TempExt2, r.TempExt2),
            new(Fan, r.FanDuty),
            new(Cable, r.CableWatts),
            new(MaxPin, r.MaxPinCurrent),
            new(Imbalance, r.CurrentImbalancePercent),
            new(Fault, r.ActiveFaults != 0),
            new(ActiveFaults, WireViewProtocol.DescribeFaults(r.ActiveFaults)),
            new(LoggedFaults, WireViewProtocol.DescribeFaults(r.LoggedFaults)),
        };
        if (pins)
        {
            foreach (var p in r.Pins)
            {
                list.Add(new(PinV[p.Number - 1], p.Voltage));
                list.Add(new(PinA[p.Number - 1], p.Current));
                list.Add(new(PinW[p.Number - 1], p.Power));
            }
        }
        return list;
    }

    /// <summary>Map the WireView sensor's readings from an HWiNFO snapshot. Labels follow HWiNFO 8.42+.</summary>
    public List<SensorValue> MapHwinfo(IReadOnlyList<HwinfoSharedMemory.Reading> readings, bool pins, string status)
    {
        var list = new List<SensorValue> { StatusValue(status), new(Connected, true) };
        var pinCurrents = new double?[6];
        var flags = new List<bool>();
        int extTemps = 0;

        foreach (var r in readings)
        {
            var label = r.Label.Trim();
            if (label.StartsWith("Pin ", StringComparison.OrdinalIgnoreCase) && label.Length > 5 && char.IsDigit(label[4]))
            {
                int n = label[4] - '0';
                if (n is < 1 or > 6) continue;
                var kind = label[5..].Trim().ToLowerInvariant();
                if (kind.StartsWith("current")) pinCurrents[n - 1] = r.Value;
                if (!pins) continue;
                if (kind.StartsWith("voltage")) list.Add(new(PinV[n - 1], r.Value));
                else if (kind.StartsWith("current")) list.Add(new(PinA[n - 1], r.Value));
                else if (kind.StartsWith("power")) list.Add(new(PinW[n - 1], r.Value));
                continue;
            }
            switch (label)
            {
                case "Total Current": list.Add(new(TotalCurrent, r.Value)); continue;
                case "Total Power": list.Add(new(TotalPower, r.Value)); continue;
                case "Average Pin Voltage": list.Add(new(AvgVoltage, r.Value)); continue;
                case "Temperature In": list.Add(new(TempIn, r.Value)); continue;
                case "Temperature Out": list.Add(new(TempOut, r.Value)); continue;
            }
            switch (r.Type)
            {
                case HwinfoSharedMemory.ReadingType.Other when r.Unit is "Yes/No" or "" or "Yes / No":
                    flags.Add(r.Value != 0);
                    break;
                case HwinfoSharedMemory.ReadingType.Temperature:
                    // Any other temperature is one of the external 10k NTC probes.
                    extTemps++;
                    if (extTemps <= 2) list.Add(new(extTemps == 1 ? TempExt1 : TempExt2, r.Value));
                    break;
                case HwinfoSharedMemory.ReadingType.Fan or HwinfoSharedMemory.ReadingType.Usage when label.Contains("Fan", StringComparison.OrdinalIgnoreCase):
                    list.Add(new(Fan, r.Value));
                    break;
            }
        }

        var currents = pinCurrents.Where(c => c.HasValue).Select(c => c!.Value).ToList();
        if (currents.Count > 0)
        {
            double max = currents.Max(), min = currents.Min();
            list.Add(new(MaxPin, max));
            list.Add(new(Imbalance, max <= 0.05 ? 0 : (max - min) / max * 100.0));
        }
        if (flags.Count >= 6)
        {
            ushort active = Mask(flags.Take(6));
            list.Add(new(Fault, active != 0));
            list.Add(new(ActiveFaults, WireViewProtocol.DescribeFaults(active)));
        }
        if (flags.Count >= 12) list.Add(new(LoggedFaults, WireViewProtocol.DescribeFaults(Mask(flags.Skip(6).Take(6)))));
        return list;
    }

    private static ushort Mask(IEnumerable<bool> bits)
    {
        int m = 0, i = 0;
        foreach (var b in bits) { if (b) m |= 1 << i; i++; }
        return (ushort)m;
    }
}
