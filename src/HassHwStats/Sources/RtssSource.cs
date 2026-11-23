using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using HassHwStats.Core;
using Microsoft.Extensions.Logging;

namespace HassHwStats.Sources;

/// <summary>
/// Frame rate and frame time from RivaTuner Statistics Server's shared memory ("RTSSSharedMemoryV2").
/// RTSS ships with MSI Afterburner. Its shared memory lives in the logged-in user's session, so this only
/// works when the app runs in that session (the scheduled-task install), not as a session-0 Windows service.
/// </summary>
public sealed class RtssSource(HaDevice pcDevice, ILogger<RtssSource> log) : ISensorSource
{
    private const uint Signature = 0x52545353; // 'RTSS'
    private const int AppNameOffset = 4, AppNameLength = 260;
    private const int Time0Offset = 268, Time1Offset = 272, FramesOffset = 276, FrameTimeOffset = 280;

    private readonly SensorDefinition _fps = new("fps", "Frame rate", pcDevice, "fps", null, "measurement", "mdi:speedometer", 0, Group: "fps");
    private readonly SensorDefinition _frameTime = new("frame_time", "Frame time", pcDevice, "ms", "duration", "measurement", "mdi:timer-outline", 1, Group: "fps");
    private readonly SensorDefinition _app = new("game", "Game", pcDevice, Icon: "mdi:gamepad-variant", StateClass: null, Group: "fps");
    private readonly SensorDefinition _status = new("rtss_status", "FPS source", pcDevice, Icon: "mdi:information-outline",
        StateClass: null, EntityCategory: "diagnostic", Group: "fps.diag");
    private string? _lastState;
    private string? _error;

    public string Name => "RTSS";

    public void Start() { }

    public IReadOnlyList<SensorValue> Poll()
    {
        _error = null;
        bool running;
        (string Name, double Fps, double? FrameTimeMs)? best;
        try
        {
            (running, best) = ReadForegroundApp();
        }
        catch (Exception ex)
        {
            (running, best) = (false, null);
            _error = ex.Message;
        }

        string state, status;
        if (_error is not null) (state, status) = ("error", $"Cannot read RTSS shared memory: {_error}");
        else if (!running) (state, status) = ("missing", "RTSS not running. Start RivaTuner Statistics Server (or MSI Afterburner) for FPS.");
        else if (best is null) (state, status) = ("idle", "RTSS running, no game detected");
        else (state, status) = ("game", $"Reading {best.Value.Name}");
        LogTransition(state, status);

        // Without RTSS there is no frame data at all, so the entities are unavailable rather than 0.
        if (state is "error" or "missing")
            return [new(_status, status), new(_fps, null), new(_frameTime, null), new(_app, null)];
        if (best is null)
            return [new(_status, status), new(_fps, 0.0), new(_frameTime, null), new(_app, "None")];
        var (name, fps, frameTimeMs) = best.Value;
        return [new(_status, status), new(_fps, fps), new(_frameTime, frameTimeMs), new(_app, name)];
    }

    private void LogTransition(string state, string status)
    {
        // Log when RTSS appears, disappears or fails, not every poll (and not every game change).
        var bucket = state == "game" ? "idle" : state;
        if (bucket == _lastState) return;
        bool first = _lastState is null;
        _lastState = bucket;
        if (bucket == "missing") log.LogWarning("RTSS shared memory (RTSSSharedMemoryV2) not found: RTSS is not running, so FPS is unavailable");
        else if (bucket == "error") log.LogWarning("{Status}", status);
        else log.LogInformation(first ? "RTSS found; reading FPS" : "RTSS is running again; reading FPS");
    }

    private (bool Running, (string Name, double Fps, double? FrameTimeMs)? Best) ReadForegroundApp()
    {
        MemoryMappedFile mmf;
        try
        {
            mmf = MemoryMappedFile.OpenExisting("RTSSSharedMemoryV2", MemoryMappedFileRights.Read);
        }
        catch (Exception ex) when (ex is FileNotFoundException or PlatformNotSupportedException)
        {
            return (false, null); // RTSS not running (named mappings only exist on Windows)
        }

        using (mmf)
        using (var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
        {
            if (view.ReadUInt32(0) != Signature) throw new InvalidDataException("unexpected signature (RTSS starting or too old?)");
            uint version = view.ReadUInt32(4);
            if (version < 0x00020000) throw new InvalidDataException($"shared memory version {version:X} is older than 2.0; update RTSS");
            int entrySize = (int)view.ReadUInt32(8);
            long arrOffset = view.ReadUInt32(12);
            int count = (int)view.ReadUInt32(16);

            uint foregroundPid = ForegroundProcessId();
            uint now = (uint)Environment.TickCount;
            (string, double, double?)? best = null;
            uint bestTime = 0;
            bool bestIsForeground = false;

            for (int i = 0; i < count; i++)
            {
                long e = arrOffset + (long)i * entrySize;
                uint pid = view.ReadUInt32(e);
                if (pid == 0) continue;
                uint t0 = view.ReadUInt32(e + Time0Offset), t1 = view.ReadUInt32(e + Time1Offset);
                uint frames = view.ReadUInt32(e + FramesOffset);
                if (t1 <= t0 || frames == 0) continue;
                if (unchecked(now - t1) > 3000) continue; // stale: game minimised or closed

                double fps = 1000.0 * frames / (t1 - t0);
                uint frameTimeUs = view.ReadUInt32(e + FrameTimeOffset);
                double? frameTime = frameTimeUs > 0 ? frameTimeUs / 1000.0 : (fps > 0 ? 1000.0 / fps : null);

                bool isForeground = pid == foregroundPid;
                if (best is not null && (bestIsForeground && !isForeground || (bestIsForeground == isForeground && t1 <= bestTime))) continue;

                var nameBytes = new byte[AppNameLength];
                view.ReadArray(e + AppNameOffset, nameBytes, 0, AppNameLength);
                int nul = Array.IndexOf(nameBytes, (byte)0);
                var path = System.Text.Encoding.Default.GetString(nameBytes, 0, nul < 0 ? AppNameLength : nul);
                best = (Path.GetFileNameWithoutExtension(path), fps, frameTime);
                bestTime = t1;
                bestIsForeground = isForeground;
            }
            return (true, best);
        }
    }

    private static uint ForegroundProcessId()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public void Dispose() { }
}
