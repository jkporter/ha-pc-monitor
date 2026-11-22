using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.IO.Ports;
using HassHwStats.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace HassHwStats.Sources;

/// <summary>
/// Reads a Thermal Grizzly WireView Pro II, either directly over its USB serial port or through HWiNFO's
/// shared memory. The USB port is exclusive: when the Thermal Grizzly app or HWiNFO holds it, "Auto" mode
/// switches to HWiNFO if HWiNFO is publishing the WireView, and otherwise reports why in the Status entity.
/// </summary>
public sealed class WireViewSource(WireViewOptions options, ILogger<WireViewSource> log) : ISensorSource
{
    private static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan HwinfoStaleAfter = TimeSpan.FromSeconds(15);

    private readonly bool _useUsb = !string.Equals(options.Source, "Hwinfo", StringComparison.OrdinalIgnoreCase);
    private readonly bool _useHwinfo = !string.Equals(options.Source, "Usb", StringComparison.OrdinalIgnoreCase);

    private SerialPort? _port;
    private string? _portName;
    private string _usbStatus = "Starting";
    private readonly Stopwatch _sinceAttempt = new();
    private string? _lastLogged;
    private string? _activeVia;
    private WireViewEntities _entities = new(new HaDevice("wireview", "WireView Pro II", "WireView Pro II", "Thermal Grizzly"));

    public string Name => "WireView Pro II";

    public void Start()
    {
        log.LogInformation("WireView source mode: {Mode}", options.Source);
        if (_useUsb) TryConnect();
    }

    public IReadOnlyList<SensorValue> Poll()
    {
        if (_useUsb && _port is not null)
        {
            var usb = PollUsb();
            if (usb is not null) return usb;
        }

        // USB not connected. Prefer HWiNFO if it has the device (it then holds the USB port anyway).
        string? hwinfoStatus = null;
        if (_useHwinfo)
        {
            var (values, status) = PollHwinfo();
            if (values is not null) return values;
            hwinfoStatus = status;
        }

        if (_useUsb && (!_sinceAttempt.IsRunning || _sinceAttempt.Elapsed >= RetryEvery) && TryConnect())
        {
            var usb = PollUsb();
            if (usb is not null) return usb;
        }

        SetActive(null);
        var parts = new List<string>();
        if (_useUsb) parts.Add($"USB: {_usbStatus}");
        if (_useHwinfo && hwinfoStatus is not null) parts.Add($"HWiNFO: {hwinfoStatus}");
        var text = "Not connected. " + string.Join(" | ", parts);
        LogOnce(text);
        return [_entities.StatusValue(text), new SensorValue(_entities.Connected, false)];
    }

    private List<SensorValue>? PollUsb()
    {
        try
        {
            var frame = Transfer(_port!, WireViewProtocol.CmdSensors, WireViewProtocol.SensorSize);
            var r = WireViewProtocol.ParseSensors(frame);
            SetActive($"USB {_portName}");
            if (r is null)
            {
                // Misaligned read; the next transfer flushes input and realigns.
                log.LogDebug("WireView frame failed plausibility check, skipping");
                return [_entities.StatusValue($"Connected over USB ({_portName})"), new(_entities.Connected, true)];
            }
            return _entities.Map(r, options.PublishPins, $"Connected over USB ({_portName})");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or UnauthorizedAccessException)
        {
            _usbStatus = $"connection on {_portName} lost: {ex.Message}";
            log.LogWarning("WireView {Status}", _usbStatus);
            Close();
            return null;
        }
    }

    private (List<SensorValue>? Values, string Status) PollHwinfo()
    {
        byte[] block;
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(HwinfoSharedMemory.MappingName, MemoryMappedFileRights.Read);
            using var view = mmf.CreateViewStream(0, 0, MemoryMappedFileAccess.Read);
            var header = new byte[HwinfoSharedMemory.HeaderSize];
            view.ReadExactly(header);
            long need = HwinfoSharedMemory.RequiredLength(header);
            if (need < 0) return (null, "shared memory has an unexpected format");
            if (need > view.Length) return (null, "shared memory is smaller than its header says");
            block = new byte[need];
            view.Position = 0;
            view.ReadExactly(block);
        }
        catch (FileNotFoundException)
        {
            return (null, "not running, or Shared Memory Support is off");
        }
        catch (Exception ex)
        {
            return (null, $"cannot read shared memory ({ex.Message})");
        }

        var snap = HwinfoSharedMemory.Parse(block);
        if (snap is null) return (null, "shared memory has an unexpected format");
        int idx = -1;
        for (int i = 0; i < snap.Sensors.Count; i++)
        {
            if (snap.Sensors[i].Name.Contains("WireView", StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
        }
        if (idx < 0) return (null, "running, but it has no WireView sensor (needs HWiNFO 8.42+ and the WireView app closed)");

        var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(snap.PollTime);
        if (age > HwinfoStaleAfter)
            return (null, "data is stale. In the free HWiNFO, Shared Memory Support switches itself off after 12 hours; turn it back on");

        SetActive("HWiNFO");
        var readings = snap.Readings.Where(r => r.SensorIndex == idx).ToList();
        return (_entities.MapHwinfo(readings, options.PublishPins, "Connected through HWiNFO"), "ok");
    }

    private void SetActive(string? via)
    {
        if (via == _activeVia) return;
        _activeVia = via;
        if (via is not null)
        {
            _lastLogged = null;
            log.LogInformation("Reading WireView Pro II through {Via}", via);
        }
    }

    private void LogOnce(string message)
    {
        if (message == _lastLogged) return;
        _lastLogged = message;
        log.LogWarning("WireView Pro II: {Message}", message);
    }

    private bool TryConnect()
    {
        _sinceAttempt.Restart();
        var present = SerialPort.GetPortNames().Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p).ToList();
        string? name = options.Port is { Length: > 0 } p ? p : FindPort(present);
        if (name is null)
        {
            _usbStatus = present.Count == 0
                ? "no COM ports at all. Is the WireView's USB cable plugged into a motherboard USB header?"
                : $"no COM port with USB ID 0483:5740 (ports present: {string.Join(", ", present)}). Set WireView.Port if one of these is it.";
            return false;
        }

        var port = new SerialPort(name, WireViewProtocol.BaudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            ReadTimeout = (int)IoTimeout.TotalMilliseconds,
            WriteTimeout = (int)IoTimeout.TotalMilliseconds,
            DtrEnable = true,
        };
        try
        {
            port.Open();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            port.Dispose();
            _usbStatus = ex is UnauthorizedAccessException
                ? $"{name} is in use by another program. Close the Thermal Grizzly WireView app, or let HWiNFO read it (Shared Memory Support on)."
                : $"cannot open {name}: {ex.Message}";
            return false;
        }

        try
        {
            port.DiscardInBuffer();
            // A freshly powered device sends a greeting when RTS is raised; an already initialised one stays quiet.
            port.RtsEnable = true;
            Thread.Sleep(10);
            var greeting = ReadUpTo(port, WireViewProtocol.Greeting.Length + 1);
            Thread.Sleep(10);
            port.RtsEnable = false;
            var text = System.Text.Encoding.ASCII.GetString(greeting).TrimEnd('\0');
            if (greeting.Length > 0 && !text.StartsWith(WireViewProtocol.Greeting, StringComparison.Ordinal))
                log.LogDebug("Unexpected WireView greeting \"{Text}\", trying the vendor command anyway", text);
            Thread.Sleep(50);

            var vendor = Transfer(port, WireViewProtocol.CmdVendor, WireViewProtocol.VendorSize);
            if (!WireViewProtocol.IsVendorOk(vendor))
                throw new InvalidDataException($"unexpected vendor reply {Convert.ToHexString(vendor)}. Only the WireView Pro II (and its Noctua Edition) is supported");
            var uid = Convert.ToHexString(Transfer(port, WireViewProtocol.CmdUid, WireViewProtocol.UidSize));
            string build = "";
            try { build = WireViewProtocol.ParseBuild(Transfer(port, WireViewProtocol.CmdBuild, WireViewProtocol.BuildSize)); }
            catch (TimeoutException) { /* older firmware: build info is optional */ }
            port.Write(WireViewProtocol.CmdScreenResume, 0, WireViewProtocol.CmdScreenResume.Length);

            var sw = string.IsNullOrEmpty(build) ? $"fw {vendor[2]}" : $"fw {vendor[2]} ({build})";
            _entities = new WireViewEntities(_entities.Device with { Model = WireViewProtocol.ModelName(vendor[1]), SwVersion = sw, SerialNumber = uid });
            _port = port;
            _portName = name;
            _usbStatus = "connected";
            log.LogInformation("Connected to {Model} on {Port}, UID {Uid}, {Sw}", WireViewProtocol.ModelName(vendor[1]), name, uid, sw);
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidDataException or InvalidOperationException)
        {
            port.Dispose();
            _usbStatus = ex is TimeoutException
                ? $"{name} opened but the WireView did not answer ({ex.Message}). Update it to firmware v03+ with the Thermal Grizzly app, or set WireView.Port if {name} is a different device."
                : $"handshake on {name} failed: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// COM port of the WireView, from the Windows device registry. Matches both a plain CDC device
    /// (USB\VID_0483&amp;PID_5740\...) and a composite one (USB\VID_0483&amp;PID_5740&amp;MI_00\...).
    /// </summary>
    private static string? FindPort(List<string> present)
    {
        var set = present.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var prefix = $"VID_{WireViewProtocol.UsbVid:X4}&PID_{WireViewProtocol.UsbPid:X4}";
        foreach (var root in new[] { @"SYSTEM\CurrentControlSet\Enum\USB", @"SYSTEM\CurrentControlSet\Enum\USBSER" })
        {
            using var usb = Registry.LocalMachine.OpenSubKey(root);
            if (usb is null) continue;
            foreach (var dev in usb.GetSubKeyNames().Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                using var devKey = usb.OpenSubKey(dev);
                if (devKey is null) continue;
                foreach (var instance in devKey.GetSubKeyNames())
                {
                    using var parameters = devKey.OpenSubKey($@"{instance}\Device Parameters");
                    if (parameters?.GetValue("PortName") is string name && set.Contains(name)) return name;
                }
            }
        }
        return null;
    }

    private static byte[] Transfer(SerialPort port, byte[] command, int replySize)
    {
        port.DiscardInBuffer();
        port.Write(command, 0, command.Length);
        var data = ReadUpTo(port, replySize);
        if (data.Length != replySize)
            throw new TimeoutException($"short reply to command {Convert.ToHexString(command)}: {data.Length}/{replySize} bytes");
        return data;
    }

    private static byte[] ReadUpTo(SerialPort port, int count)
    {
        var buf = new byte[count];
        int got = 0;
        var deadline = Stopwatch.StartNew();
        while (got < count && deadline.Elapsed < IoTimeout)
        {
            try
            {
                int n = port.Read(buf, got, count - got);
                if (n == 0) Thread.Sleep(1);
                got += n;
            }
            catch (TimeoutException)
            {
                break;
            }
        }
        return buf[..got];
    }

    private void Close()
    {
        try { _port?.Close(); } catch { /* already gone */ }
        _port?.Dispose();
        _port = null;
        _sinceAttempt.Restart();
    }

    public void Dispose() => Close();
}
