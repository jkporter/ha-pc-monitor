using System.Text;
using System.Text.RegularExpressions;

namespace HassHwStats.Core;

/// <summary>
/// Builds a Home Assistant dashboard (raw configuration editor YAML) from the entities this PC actually publishes.
/// Entity IDs are predicted the way HA names MQTT entities that belong to a device: domain.slug(device name + " " + entity name).
/// </summary>
public static class DashboardWriter
{
    public static string EntityId(SensorDefinition d) =>
        $"{(d.Kind == EntityKind.BinarySensor ? "binary_sensor" : "sensor")}.{Slug.Make($"{d.Device.Name} {d.Name}")}";

    public static string Build(string title, IReadOnlyCollection<SensorDefinition> defs)
    {
        var y = new Yaml();
        y.Line($"title: {Q(title)}");
        y.Line("views:");
        y.Indent++;

        // ---- Overview ----
        y.Line("- title: Overview");
        y.Line("  path: overview");
        y.Line("  icon: mdi:monitor-dashboard");
        y.Line("  cards:");
        y.Indent += 2;

        var fps = defs.Where(d => d.Group == "fps").ToList();
        if (fps.Count > 0)
        {
            y.Line("- type: glance");
            y.Line("  title: Now playing");
            Entities(y, fps, "  ");
        }

        var gauges = new List<(SensorDefinition Def, string Name, int Max, (int Yellow, int Red)? Sev)>();
        void Gauge(SensorDefinition? d, string name, int max, (int, int)? sev = null) { if (d is not null) gauges.Add((d, name, max, sev)); }
        Gauge(Find(defs, "cpu.main.Load", "CPU Total"), "CPU load", 100);
        Gauge(Find(defs, "gpu.main.Load", "GPU Core"), "GPU load", 100);
        Gauge(defs.FirstOrDefault(d => d.Key == "wireview_total_power"), "12V-2x6 power", 600, (450, 550));
        if (gauges.Count > 0)
        {
            y.Line("- type: grid");
            y.Line($"  columns: {gauges.Count}");
            y.Line("  square: false");
            y.Line("  cards:");
            foreach (var g in gauges)
            {
                y.Line($"    - type: gauge");
                y.Line($"      entity: {EntityId(g.Def)}");
                y.Line($"      name: {Q(g.Name)}");
                y.Line($"      min: 0");
                y.Line($"      max: {g.Max}");
                if (g.Sev is { } s) y.Line($"      severity: {{ green: 0, yellow: {s.Yellow}, red: {s.Red} }}");
            }
        }

        EntitiesCard(y, "GPU", defs.Where(d => d.Group?.StartsWith("gpu.") == true));
        EntitiesCard(y, "CPU", defs.Where(d => d.Group?.StartsWith("cpu.main.") == true));
        EntitiesCard(y, "RAM", defs.Where(d => d.Group?.StartsWith("ram.") == true));
        foreach (var other in defs.Where(d => d.Group is { } g && !g.StartsWith("cpu.") && !g.StartsWith("gpu.") && !g.StartsWith("ram.") && !g.StartsWith("wireview") && g != "fps")
                     .GroupBy(d => d.Device.Name))
            EntitiesCard(y, other.Key, other);
        y.Indent -= 2;

        // ---- CPU cores ----
        var cores = defs.Where(d => d.Group?.StartsWith("cpu.core.") == true).ToList();
        if (cores.Count > 0)
        {
            y.Line("- title: CPU cores");
            y.Line("  path: cpu-cores");
            y.Line("  icon: mdi:cpu-64-bit");
            y.Line("  cards:");
            y.Indent += 2;
            foreach (var (type, label) in new[] { ("Load", "Core load"), ("Clock", "Core clock"), ("Temperature", "Core temperature"), ("Power", "Core power") })
            {
                var list = cores.Where(d => d.Group == $"cpu.core.{type}").OrderBy(d => d.Name, NaturalOrder.Instance).ToList();
                if (list.Count == 0) continue;
                y.Line("- type: entities");
                y.Line($"  title: {Q(label)}");
                y.Line("  state_color: true");
                y.Line("  entities:");
                foreach (var d in list)
                {
                    y.Line($"    - entity: {EntityId(d)}");
                    y.Line($"      name: {Q(ShortCoreName(d.Name))}");
                }
            }
            foreach (var (type, label) in new[] { ("Clock", "Core clocks, last hour"), ("Load", "Core load, last hour") })
            {
                var list = cores.Where(d => d.Group == $"cpu.core.{type}").OrderBy(d => d.Name, NaturalOrder.Instance).ToList();
                if (list.Count == 0) continue;
                y.Line("- type: history-graph");
                y.Line($"  title: {Q(label)}");
                y.Line("  hours_to_show: 1");
                y.Line("  entities:");
                foreach (var d in list) y.Line($"    - {EntityId(d)}");
            }
            y.Indent -= 2;
        }

        // ---- WireView ----
        var wv = defs.Where(d => d.Group?.StartsWith("wireview") == true).ToList();
        if (wv.Count > 0)
        {
            y.Line("- title: WireView");
            y.Line("  path: wireview");
            y.Line("  icon: mdi:power-plug");
            y.Line("  cards:");
            y.Indent += 2;
            EntitiesCard(y, wv[0].Device.Name, wv.Where(d => d.Group is "wireview" or "wireview.diag"));
            var pins = wv.Where(d => d.Group == "wireview.pin" && d.Key.EndsWith("_current")).OrderBy(d => d.Key).ToList();
            if (pins.Count > 0)
            {
                y.Line("- type: grid");
                y.Line("  title: Pin current");
                y.Line("  columns: 3");
                y.Line("  square: false");
                y.Line("  cards:");
                foreach (var p in pins)
                    y.Line($"    - {{ type: gauge, entity: {EntityId(p)}, name: {Q(p.Name.Replace(" current", ""))}, min: 0, max: 10, severity: {{ green: 0, yellow: 8, red: 9.2 }} }}");
            }
            var hist = new[] { "wireview_total_power", "wireview_max_pin_current", "wireview_current_imbalance", "wireview_temp_out" }
                .Select(k => wv.FirstOrDefault(d => d.Key == k)).Where(d => d is not null).ToList();
            if (hist.Count > 0)
            {
                y.Line("- type: history-graph");
                y.Line("  title: Last 2 hours");
                y.Line("  hours_to_show: 2");
                y.Line("  entities:");
                foreach (var d in hist) y.Line($"    - {EntityId(d!)}");
            }
            y.Indent -= 2;
        }

        return y.ToString();
    }

    private static SensorDefinition? Find(IEnumerable<SensorDefinition> defs, string group, string nameStart) =>
        defs.FirstOrDefault(d => d.Group == group && d.Name.StartsWith(nameStart, StringComparison.OrdinalIgnoreCase));

    private static void EntitiesCard(Yaml y, string title, IEnumerable<SensorDefinition> defs)
    {
        var list = defs.ToList();
        if (list.Count == 0) return;
        y.Line("- type: entities");
        y.Line($"  title: {Q(title)}");
        Entities(y, list, "  ");
    }

    private static void Entities(Yaml y, IEnumerable<SensorDefinition> defs, string pad)
    {
        y.Line($"{pad}entities:");
        foreach (var d in defs)
        {
            y.Line($"{pad}  - entity: {EntityId(d)}");
            y.Line($"{pad}    name: {Q(d.Name)}");
        }
    }

    private static string ShortCoreName(string name) =>
        Regex.Replace(name.Replace("CPU ", ""), @"\s+(load|clock|temperature|power)$", "", RegexOptions.IgnoreCase);

    private static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private sealed class Yaml
    {
        private readonly StringBuilder _sb = new();
        public int Indent;
        public void Line(string text) => _sb.Append(' ', Indent * 2).Append(text).Append('\n');
        public override string ToString() => _sb.ToString();
    }

    /// <summary>Sorts "Core #2" before "Core #10".</summary>
    private sealed class NaturalOrder : IComparer<string>
    {
        public static readonly NaturalOrder Instance = new();
        private static readonly Regex Digits = new(@"\d+");
        public int Compare(string? a, string? b) =>
            string.CompareOrdinal(Digits.Replace(a ?? "", m => m.Value.PadLeft(6, '0')), Digits.Replace(b ?? "", m => m.Value.PadLeft(6, '0')));
    }
}
