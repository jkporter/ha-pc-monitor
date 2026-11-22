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
    private bool _warned;

    public string Name => "RTSS";

    public void Start() { }

    public IReadOnlyList<SensorValue> Poll()
    {
        var best = ReadForegroundApp();
        if (best is null)
            return [new(_fps, 0.0), new(_frameTime, null), new(_app, "None")];
        var (name, fps, frameTimeMs) = best.Value;
        return [new(_fps, fps), new(_frameTime, frameTimeMs), new(_app, name)];
    }

    private (string Name, double Fps, double? FrameTimeMs)? ReadForegroundApp()
    {
        MemoryMappedFile mmf;
        try
        {
            mmf = MemoryMappedFile.OpenExisting("RTSSSharedMemoryV2", MemoryMappedFileRights.Read);
        }
        catch (FileNotFoundException)
        {
            return null; // RTSS not running
        }
        catch (Exception ex) when (!_warned)
        {
            _warned = true;
            log.LogWarning("Cannot open RTSS shared memory: {Message}", ex.Message);
            return null;
        }
        catch
        {
            return null;
        }

        using (mmf)
        using (var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
        {
            if (view.ReadUInt32(0) != Signature) return null;
            uint version = view.ReadUInt32(4);
            if (version < 0x00020000) return null;
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
            return best;
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
