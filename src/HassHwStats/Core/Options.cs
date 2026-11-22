namespace HassHwStats.Core;

public sealed class AppOptions
{
    /// <summary>Name of this PC in Home Assistant. Defaults to the machine name.</summary>
    public string? NodeName { get; set; }

    /// <summary>How often values are read and published.</summary>
    public int IntervalMs { get; set; } = 2000;

    public MqttOptions Mqtt { get; set; } = new();
    public HardwareOptions Hardware { get; set; } = new();
    public WireViewOptions WireView { get; set; } = new();
    public RtssOptions Rtss { get; set; } = new();

    /// <summary>Write dashboard.yaml next to the exe with this PC's real entity IDs, ready to paste into HA.</summary>
    public bool WriteDashboard { get; set; } = true;
}

public sealed class MqttOptions
{
    public string Host { get; set; } = "homeassistant.local";
    public int Port { get; set; } = 1883;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool UseTls { get; set; }
    /// <summary>Accept a broker certificate that does not validate (self-signed). Only use on your own LAN.</summary>
    public bool AllowUntrustedCertificate { get; set; }
    public string DiscoveryPrefix { get; set; } = "homeassistant";
    public string BaseTopic { get; set; } = "hass-hw-stats";
}

public sealed class HardwareOptions
{
    public bool Enabled { get; set; } = true;
    public bool Cpu { get; set; } = true;
    public bool Gpu { get; set; } = true;
    public bool Memory { get; set; } = true;
    public bool Motherboard { get; set; }
    public bool Storage { get; set; }
    public bool Network { get; set; }

    /// <summary>
    /// "Curated" publishes the overlay-style set (load, clocks, temps, power, fans, VRAM).
    /// "All" publishes every sensor LibreHardwareMonitor finds (can be hundreds).
    /// </summary>
    public string Mode { get; set; } = "Curated";

    /// <summary>In Curated mode, also publish load, clock and temperature (and AMD per-core power) for every CPU core.</summary>
    public bool PerCoreCpu { get; set; } = true;

    /// <summary>Regexes matched against "Hardware name/SensorType/Sensor name". A match is always published.</summary>
    public List<string> Include { get; set; } = [];

    /// <summary>Regexes matched against the same path. A match is never published (wins over Include).</summary>
    public List<string> Exclude { get; set; } = [];
}

public sealed class WireViewOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// "Auto": USB if the port is free, otherwise HWiNFO shared memory if HWiNFO is reading the WireView.
    /// "Usb": only the USB serial port. "Hwinfo": only HWiNFO (8.42+, Shared Memory Support on).
    /// </summary>
    public string Source { get; set; } = "Auto";

    /// <summary>COM port, e.g. "COM5". Leave empty to find the device by its USB ID (0483:5740).</summary>
    public string? Port { get; set; }
    public bool PublishPins { get; set; } = true;
}

public sealed class RtssOptions
{
    /// <summary>Read FPS/frame time from RivaTuner Statistics Server (bundled with MSI Afterburner) when it is running.</summary>
    public bool Enabled { get; set; } = true;
}
