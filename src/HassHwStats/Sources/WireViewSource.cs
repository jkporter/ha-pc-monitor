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
///
/// The USB link drops when Windows suspends the device or the PC sleeps: the COM port disappears and comes back,
/// sometimes under a different number, leaving the old handle dead. The source therefore reconnects on its own:
/// after a wake-up, after I/O errors, and after repeated timeouts or garbage frames, re-scanning for the port by
/// its USB ID each time and backing off from 2 s to 30 s between attempts.
/// </summary>
public sealed class WireViewSource(
    WireViewOptions options,
    ILogger<WireViewSource> log,
    Func<string, ISerialLink>? openLink = null,
    Func<IReadOnlyList<string>>? portNames = null,
    Func<IReadOnlyList<string>, string?>? findPort = null) : ISensorSource, IPowerAware
{
    private readonly Func<IReadOnlyList<string>, string?> _findPort = findPort ?? FindPort;
    private readonly Func<string, ISerialLink> _openLink = openLink ?? (name => new SerialPortLink(name));
    private readonly Func<IReadOnlyList<string>> _portNames = portNames ?? SerialPort.GetPortNames;

    private static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ResumeSettle = TimeSpan.FromSeconds(3);
    private const int MaxTimeoutsInARow = 2;
    private const int MaxBadFramesInARow = 5;
    private static readonly TimeSpan HwinfoStaleAfter = TimeSpan.FromSeconds(15);

    private readonly bool _useUsb = !string.Equals(options.Source, "Hwinfo", StringComparison.OrdinalIgnoreCase);
    private readonly bool _useHwinfo = !string.Equals(options.Source, "Usb", StringComparison.OrdinalIgnoreCase);

    private ISerialLink? _port;
    private string? _portName;
    private string _usbStatus = "Starting";
    private readonly Backoff _backoff = new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));
    private volatile bool _resumed;
    private string? _reconnectReason;
    private int _timeoutsInARow, _badFramesInARow;
    private bool _everConnected;
    private string? _lastLogged;
    private string? _activeVia;
    private WireViewEntities _entities = new(new HaDevice("wireview", "WireView Pro II", "WireView Pro II", "Thermal Grizzly"));

    public string Name => "WireView Pro II";

    public void Start()
    {
        log.LogInformation("WireView source mode: {Mode}", options.Source);
        if (_useUsb) TryConnect();
    }

    public IReadOnlyList<SensorValue> Poll() => _entities.Complete(PollCore(), options.PublishPins);

    public void OnResume() => _resumed = true;

    private IReadOnlyList<SensorValue> PollCore()
    {
        if (_resumed)
        {
            _resumed = false;
            if (_useUsb)
            {
                // The old handle is almost certainly dead; give USB a moment to re-enumerate, then reconnect.
                log.LogInformation("PC woke from sleep; reopening the WireView connection");
                Close();
                _reconnectReason = "PC woke from sleep";
                _backoff.RestartAfter(DateTime.UtcNow, ResumeSettle);
            }
        }

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

        var now = DateTime.UtcNow;
        if (_useUsb && _backoff.IsDue(now))
        {
            if (TryConnect())
            {
                _backoff.Reset();
                var usb = PollUsb();
                if (usb is not null) return usb;
            }
            else
            {
                _backoff.Failed(now);
            }
        }

        SetActive(null);
        var parts = new List<string>();
        if (_useUsb)
        {
            var retry = $"retrying in {Math.Ceiling(_backoff.Remaining(DateTime.UtcNow).TotalSeconds):0} s";
            parts.Add(_reconnectReason is not null && _everConnected
                ? $"USB: reconnecting ({_reconnectReason}), attempt {Math.Max(1, _backoff.Attempt)}, {retry}. Last error: {_usbStatus}"
                : $"USB: {_usbStatus} ({retry})");
        }
        if (_useHwinfo && hwinfoStatus is not null) parts.Add($"HWiNFO: {hwinfoStatus}");
        var text = (_reconnectReason is not null && _everConnected ? "Reconnecting. " : "Not connected. ") + string.Join(" | ", parts);
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
            _timeoutsInARow = 0;
            if (r is null)
            {
                // Misaligned read; the next transfer flushes input and realigns. Persistent garbage means a broken link.
                if (++_badFramesInARow >= MaxBadFramesInARow) return Lost($"{MaxBadFramesInARow} unreadable frames in a row");
                log.LogDebug("WireView frame failed plausibility check, skipping");
                return [_entities.StatusValue($"Connected over USB ({_portName})"), new(_entities.Connected, true)];
            }
            _badFramesInARow = 0;
            return _entities.Map(r, options.PublishPins, $"Connected over USB ({_portName})");
        }
        catch (TimeoutException ex)
        {
            // One slow reply can happen; several in a row means the device stopped answering (typical after sleep).
            if (++_timeoutsInARow >= MaxTimeoutsInARow) return Lost($"device stopped answering ({ex.Message})");
            return [_entities.StatusValue($"Connected over USB ({_portName}), slow reply"), new(_entities.Connected, true)];
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return Lost(ex.Message);
        }
    }

    private List<SensorValue>? Lost(string why)
    {
        _usbStatus = $"connection on {_portName} lost: {why}";
        _reconnectReason = "connection lost";
        log.LogWarning("WireView {Status}; reconnecting", _usbStatus);
        Close();
        _backoff.RestartAfter(DateTime.UtcNow, TimeSpan.FromSeconds(1));
        return null;
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
        var present = _portNames().Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p).ToList();
        // Re-scan every time: after a USB reset the WireView can come back under a different COM number.
        string? found = _findPort(present);
        string? name = options.Port is { Length: > 0 } p && present.Contains(p, StringComparer.OrdinalIgnoreCase) ? p : found ?? (options.Port is { Length: > 0 } q ? q : null);
        if (options.Port is { Length: > 0 } cfg && found is not null && !string.Equals(cfg, found, StringComparison.OrdinalIgnoreCase) && !present.Contains(cfg, StringComparer.OrdinalIgnoreCase))
            log.LogInformation("Configured port {Configured} is gone; the WireView is now on {Found}", cfg, found);
        if (name is null)
        {
            _usbStatus = present.Count == 0
                ? "no COM ports at all. Is the WireView's USB cable plugged into a motherboard USB header?"
                : $"no COM port with USB ID 0483:5740 (ports present: {string.Join(", ", present)}). Set WireView.Port if one of these is it.";
            return false;
        }

        ISerialLink port;
        try
        {
            port = _openLink(name);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
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
            if (_everConnected) log.LogInformation("WireView reconnected on {Port} after: {Reason}", name, _reconnectReason ?? "disconnect");
            _port = port;
            _portName = name;
            _usbStatus = "connected";
            _everConnected = true;
            _reconnectReason = null;
            _timeoutsInARow = _badFramesInARow = 0;
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
    private static string? FindPort(IReadOnlyList<string> present)
    {
        if (!OperatingSystem.IsWindows()) return null;
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

    private static byte[] Transfer(ISerialLink port, byte[] command, int replySize)
    {
        port.DiscardInBuffer();
        port.Write(command, 0, command.Length);
        var data = ReadUpTo(port, replySize);
        if (data.Length != replySize)
            throw new TimeoutException($"short reply to command {Convert.ToHexString(command)}: {data.Length}/{replySize} bytes");
        return data;
    }

    private static byte[] ReadUpTo(ISerialLink port, int count)
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
        var port = _port;
        _port = null;
        if (port is null) return;
        // Closing a handle whose USB device vanished can block for a long time in the driver; don't stall polling on it.
        var closing = Task.Run(() =>
        {
            try { port.Close(); } catch { /* already gone */ }
            try { port.Dispose(); } catch { /* already gone */ }
        });
        if (!closing.Wait(TimeSpan.FromSeconds(2))) log.LogDebug("Closing the old WireView port is taking long; continuing without waiting");
    }

    public void Dispose() => Close();
}

/// <summary>The few serial-port operations the WireView needs; lets tests simulate the device and a vanishing port.</summary>
public interface ISerialLink : IDisposable
{
    void DiscardInBuffer();
    bool RtsEnable { set; }
    void Write(byte[] buffer, int offset, int count);
    /// <summary>Reads up to count bytes; throws <see cref="TimeoutException"/> when nothing arrives within the read timeout.</summary>
    int Read(byte[] buffer, int offset, int count);
    void Close();
}

/// <summary>An opened WireView COM port: 115200 8N1, DTR on, 1 s timeouts.</summary>
public sealed class SerialPortLink : ISerialLink
{
    private readonly SerialPort _port;

    public SerialPortLink(string name)
    {
        _port = new SerialPort(name, WireViewProtocol.BaudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            ReadTimeout = 1000,
            WriteTimeout = 1000,
            DtrEnable = true,
        };
        try
        {
            _port.Open();
        }
        catch
        {
            _port.Dispose();
            throw;
        }
    }

    public void DiscardInBuffer() => _port.DiscardInBuffer();
    public bool RtsEnable { set => _port.RtsEnable = value; }
    public void Write(byte[] buffer, int offset, int count) => _port.Write(buffer, offset, count);
    public int Read(byte[] buffer, int offset, int count) => _port.Read(buffer, offset, count);
    public void Close() => _port.Close();
    public void Dispose() => _port.Dispose();
}
