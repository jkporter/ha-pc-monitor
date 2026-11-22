using System.Globalization;
using System.Text;
using System.Text.Json;
using HassHwStats.Core;

namespace HassHwStats.Mqtt;

/// <summary>
/// Builds MQTT topics and payloads for Home Assistant MQTT discovery. Uses device-based discovery: one retained
/// config per device at {prefix}/device/{node}_{device}/config that lists every entity under "components".
/// Pure functions so they can be tested.
/// </summary>
public sealed class HomeAssistantPayloads(string discoveryPrefix, string baseTopic, string nodeName, HaDevice pcDevice, string swVersion)
{
    public const string ProjectUrl = "https://github.com/jkporter/ha-pc-monitor";

    public string NodeId { get; } = Slug.Make(nodeName);

    public string AvailabilityTopic => $"{baseTopic}/{NodeId}/status";
    public string HomeAssistantStatusTopic => $"{discoveryPrefix}/status";

    public string StateTopic(HaDevice device) => $"{baseTopic}/{NodeId}/{device.Id}/state";

    public string DeviceDiscoveryTopic(HaDevice device) => $"{discoveryPrefix}/device/{DeviceIdentifier(device)}/config";

    /// <summary>Where versions before 0.4 published single-component discovery for this entity.</summary>
    public string LegacyDiscoveryTopic(SensorDefinition def) =>
        $"{discoveryPrefix}/{Platform(def)}/{NodeId}/{def.Key}/config";

    /// <summary>Subscription filter matching every legacy single-component config this node may have left behind.</summary>
    public string LegacyDiscoveryFilter => $"{discoveryPrefix}/+/{NodeId}/+/config";

    public string UniqueId(SensorDefinition def) => $"{NodeId}_{def.Key}";

    private static string Platform(SensorDefinition def) => def.Kind == EntityKind.BinarySensor ? "binary_sensor" : "sensor";

    private string DeviceIdentifier(HaDevice d) => $"{NodeId}_{d.Id}";

    /// <summary>The device discovery payload: device, origin, shared state topic and one component per entity.</summary>
    public string DevicePayload(HaDevice device, IEnumerable<SensorDefinition> defs)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();

            w.WriteStartObject("device");
            w.WriteStartArray("identifiers");
            w.WriteStringValue(DeviceIdentifier(device));
            w.WriteEndArray();
            w.WriteString("name", device.Name);
            if (device.Model is not null) w.WriteString("model", device.Model);
            if (device.Manufacturer is not null) w.WriteString("manufacturer", device.Manufacturer);
            if (device.SwVersion is not null) w.WriteString("sw_version", device.SwVersion);
            if (device.SerialNumber is not null) w.WriteString("serial_number", device.SerialNumber);
            if (device.Id != pcDevice.Id) w.WriteString("via_device", DeviceIdentifier(pcDevice));
            w.WriteEndObject();

            w.WriteStartObject("origin");
            w.WriteString("name", "HassHwStats");
            w.WriteString("sw_version", swVersion);
            w.WriteString("support_url", ProjectUrl);
            w.WriteEndObject();

            // Shared by every component.
            w.WriteString("state_topic", StateTopic(device));
            w.WriteNumber("qos", 1);

            w.WriteStartObject("components");
            foreach (var def in defs.OrderBy(d => d.Key, StringComparer.Ordinal))
            {
                w.WriteStartObject(def.Key);
                WriteComponent(w, def);
                w.WriteEndObject();
            }
            w.WriteEndObject();

            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private void WriteComponent(Utf8JsonWriter w, SensorDefinition def)
    {
        w.WriteString("platform", Platform(def));
        w.WriteString("name", def.Name);
        w.WriteString("unique_id", UniqueId(def));
        w.WriteString("value_template", def.Kind == EntityKind.BinarySensor
            ? $"{{{{ 'ON' if value_json.{def.Key} else 'OFF' }}}}"
            : $"{{{{ value_json.{def.Key} }}}}");

        // Online only when the PC is up AND this value was present in the latest reading.
        w.WriteStartArray("availability");
        w.WriteStartObject();
        w.WriteString("topic", AvailabilityTopic);
        w.WriteEndObject();
        w.WriteStartObject();
        w.WriteString("topic", StateTopic(def.Device));
        w.WriteString("value_template", $"{{{{ 'online' if value_json.{def.Key} is defined and value_json.{def.Key} is not none else 'offline' }}}}");
        w.WriteEndObject();
        w.WriteEndArray();
        w.WriteString("availability_mode", "all");

        if (def.Unit is not null) w.WriteString("unit_of_measurement", def.Unit);
        if (def.DeviceClass is not null) w.WriteString("device_class", def.DeviceClass);
        if (def.StateClass is not null && def.Kind == EntityKind.Sensor) w.WriteString("state_class", def.StateClass);
        if (def.Icon is not null) w.WriteString("icon", def.Icon);
        if (def.Precision is { } p && def.Kind == EntityKind.Sensor && def.Unit is not null) w.WriteNumber("suggested_display_precision", p);
        if (def.EntityCategory is not null) w.WriteString("entity_category", def.EntityCategory);
    }

    /// <summary>Sent to a legacy single-component topic before the device config takes over its entity.</summary>
    public const string MigratePayload = """{"migrate_discovery":true}""";

    /// <summary>One JSON object per HA device: { "key": value, ... }. Numbers are rounded to the sensor's precision.</summary>
    public static string StatePayload(IEnumerable<SensorValue> values)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (var v in values)
            {
                var key = v.Definition.Key;
                switch (v.Value)
                {
                    case null:
                        w.WriteNull(key);
                        break;
                    case bool b:
                        w.WriteBoolean(key, b);
                        break;
                    case string s:
                        w.WriteString(key, s);
                        break;
                    case IConvertible c when IsNumber(c):
                        var d = c.ToDouble(CultureInfo.InvariantCulture);
                        if (!double.IsFinite(d)) { w.WriteNull(key); break; }
                        if (v.Definition.Precision is { } p) d = Math.Round(d, Math.Min(p + 1, 6), MidpointRounding.AwayFromZero);
                        w.WriteNumber(key, d);
                        break;
                    default:
                        w.WriteString(key, v.Value.ToString());
                        break;
                }
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static bool IsNumber(IConvertible c) => c.GetTypeCode() is >= TypeCode.SByte and <= TypeCode.Decimal;
}
