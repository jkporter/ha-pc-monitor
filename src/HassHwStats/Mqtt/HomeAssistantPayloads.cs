using System.Globalization;
using System.Text;
using System.Text.Json;
using HassHwStats.Core;

namespace HassHwStats.Mqtt;

/// <summary>Builds MQTT topics and payloads for Home Assistant MQTT discovery. Pure functions so they can be tested.</summary>
public sealed class HomeAssistantPayloads(string discoveryPrefix, string baseTopic, string nodeName, HaDevice pcDevice, string swVersion)
{
    public string NodeId { get; } = Slug.Make(nodeName);

    public string AvailabilityTopic => $"{baseTopic}/{NodeId}/status";
    public string HomeAssistantStatusTopic => $"{discoveryPrefix}/status";

    public string StateTopic(HaDevice device) => $"{baseTopic}/{NodeId}/{device.Id}/state";

    public string DiscoveryTopic(SensorDefinition def) =>
        $"{discoveryPrefix}/{(def.Kind == EntityKind.BinarySensor ? "binary_sensor" : "sensor")}/{NodeId}/{def.Key}/config";

    private string DeviceIdentifier(HaDevice d) => $"{NodeId}_{d.Id}";

    public string DiscoveryPayload(SensorDefinition def)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("name", def.Name);
            w.WriteString("unique_id", $"{NodeId}_{def.Key}");
            w.WriteString("state_topic", StateTopic(def.Device));
            if (def.Kind == EntityKind.BinarySensor)
            {
                w.WriteString("value_template", $"{{{{ 'ON' if value_json.{def.Key} else 'OFF' }}}}");
            }
            else
            {
                w.WriteString("value_template", $"{{{{ value_json.{def.Key} }}}}");
            }

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

            w.WriteStartObject("device");
            w.WriteStartArray("identifiers");
            w.WriteStringValue(DeviceIdentifier(def.Device));
            w.WriteEndArray();
            w.WriteString("name", def.Device.Name);
            if (def.Device.Model is not null) w.WriteString("model", def.Device.Model);
            if (def.Device.Manufacturer is not null) w.WriteString("manufacturer", def.Device.Manufacturer);
            if (def.Device.SwVersion is not null) w.WriteString("sw_version", def.Device.SwVersion);
            if (def.Device.SerialNumber is not null) w.WriteString("serial_number", def.Device.SerialNumber);
            if (def.Device.Id != pcDevice.Id) w.WriteString("via_device", DeviceIdentifier(pcDevice));
            w.WriteEndObject();

            w.WriteStartObject("origin");
            w.WriteString("name", "HassHwStats");
            w.WriteString("sw", swVersion);
            w.WriteEndObject();

            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

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
