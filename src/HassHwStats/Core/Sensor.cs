namespace HassHwStats.Core;

/// <summary>A Home Assistant device that groups entities (CPU, GPU, WireView, ...).</summary>
public sealed record HaDevice(string Id, string Name, string? Model = null, string? Manufacturer = null, string? SwVersion = null, string? SerialNumber = null);

public enum EntityKind { Sensor, BinarySensor }

/// <summary>
/// One entity published to Home Assistant. <see cref="Key"/> must be stable across runs:
/// it becomes the entity's unique_id and its key in the JSON state payload.
/// <see cref="Group"/> is a dotted tag such as "cpu.core.Load" or "wireview.pin", used to lay out the generated dashboard.
/// </summary>
public sealed record SensorDefinition(
    string Key,
    string Name,
    HaDevice Device,
    string? Unit = null,
    string? DeviceClass = null,
    string? StateClass = "measurement",
    string? Icon = null,
    int? Precision = null,
    EntityKind Kind = EntityKind.Sensor,
    string? EntityCategory = null,
    string? Group = null);

/// <summary>A value for a sensor. Value may be a number, string or bool (binary sensors), or null for unknown.</summary>
public readonly record struct SensorValue(SensorDefinition Definition, object? Value);

/// <summary>A source of readings (LibreHardwareMonitor, WireView, RTSS...).</summary>
public interface ISensorSource : IDisposable
{
    string Name { get; }

    /// <summary>Called once before the first poll. Throwing disables the source but does not stop the service.</summary>
    void Start();

    /// <summary>Read the current values. May return a different set of sensors over time (hot-plug).</summary>
    IReadOnlyList<SensorValue> Poll();
}
