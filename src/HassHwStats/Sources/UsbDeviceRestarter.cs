using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace HassHwStats.Sources;

/// <summary>
/// Restarts the WireView's USB serial device, the same as "pnputil /restart-device" or disable/enable in Device Manager.
/// After S3 resume Windows sometimes leaves the usbser device "working" in PnP while every open fails with
/// ERROR_GEN_FAILURE (31); a device restart brings the COM port back. Needs admin rights (the service and the
/// scheduled task both run elevated).
/// </summary>
[SupportedOSPlatform("windows")]
public static class UsbDeviceRestarter
{
    /// <summary>PnP instance IDs (e.g. USB\VID_0483&amp;PID_5740\xxxx) of WireView devices, the one behind <paramref name="port"/> first.</summary>
    public static List<string> FindInstanceIds(string? port)
    {
        var matches = new List<(string Id, bool OnPort)>();
        var prefix = $"VID_{WireViewProtocol.UsbVid:X4}&PID_{WireViewProtocol.UsbPid:X4}";
        foreach (var bus in new[] { "USB", "USBSER" })
        {
            using var root = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{bus}");
            if (root is null) continue;
            foreach (var dev in root.GetSubKeyNames().Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                using var devKey = root.OpenSubKey(dev);
                if (devKey is null) continue;
                foreach (var instance in devKey.GetSubKeyNames())
                {
                    using var parameters = devKey.OpenSubKey($@"{instance}\Device Parameters");
                    if (parameters?.GetValue("PortName") is not string name) continue;
                    matches.Add(($@"{bus}\{dev}\{instance}", port is not null && string.Equals(name, port, StringComparison.OrdinalIgnoreCase)));
                }
            }
        }
        // Prefer the instance that owns the port; otherwise every WireView serial instance.
        return matches.Any(m => m.OnPort) ? matches.Where(m => m.OnPort).Select(m => m.Id).ToList() : matches.Select(m => m.Id).ToList();
    }

    /// <summary>Restart the device(s) behind <paramref name="port"/>. Returns a short description of what happened.</summary>
    public static (bool Ok, string Message) RestartForPort(string? port)
    {
        var ids = FindInstanceIds(port);
        if (ids.Count == 0) return (false, "no WireView USB device found in the device registry");
        var results = new List<string>();
        bool any = false;
        foreach (var id in ids)
        {
            var (ok, msg) = Restart(id);
            any |= ok;
            results.Add($"{id}: {msg}");
        }
        return (any, string.Join("; ", results));
    }

    public static (bool Ok, string Message) Restart(string instanceId)
    {
        try
        {
            PropChange(instanceId);
            return (true, "restarted");
        }
        catch (Exception ex)
        {
            // Fall back to pnputil (Windows 10 2004 and newer), which does the same through the PnP service.
            var (ok, output) = RunPnpUtil(instanceId);
            return ok ? (true, $"restarted with pnputil (SetupAPI failed: {ex.Message})") : (false, $"SetupAPI: {ex.Message}; pnputil: {output}");
        }
    }

    // ---- SetupAPI: SetupDiChangeState(DICS_PROPCHANGE), what devcon restart does ----

    private const int DIF_PROPERTYCHANGE = 0x12;
    private const int DICS_PROPCHANGE = 3;
    private const int DICS_FLAG_GLOBAL = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public int DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_CLASSINSTALL_HEADER
    {
        public int cbSize;
        public int InstallFunction;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_PROPCHANGE_PARAMS
    {
        public SP_CLASSINSTALL_HEADER ClassInstallHeader;
        public int StateChange;
        public int Scope;
        public int HwProfile;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiOpenDeviceInfoW(IntPtr set, string instanceId, IntPtr hwndParent, int openFlags, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiSetClassInstallParamsW(IntPtr set, ref SP_DEVINFO_DATA data, ref SP_PROPCHANGE_PARAMS parameters, int size);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiCallClassInstaller(int installFunction, IntPtr set, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    private static void PropChange(string instanceId)
    {
        var set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (!SetupDiOpenDeviceInfoW(set, instanceId, IntPtr.Zero, 0, ref data)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var p = new SP_PROPCHANGE_PARAMS
            {
                ClassInstallHeader = new SP_CLASSINSTALL_HEADER { cbSize = Marshal.SizeOf<SP_CLASSINSTALL_HEADER>(), InstallFunction = DIF_PROPERTYCHANGE },
                StateChange = DICS_PROPCHANGE,
                Scope = DICS_FLAG_GLOBAL,
                HwProfile = 0,
            };
            if (!SetupDiSetClassInstallParamsW(set, ref data, ref p, Marshal.SizeOf<SP_PROPCHANGE_PARAMS>())) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, set, ref data)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static (bool Ok, string Output) RunPnpUtil(string instanceId)
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "pnputil.exe"))
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("/restart-device");
            psi.ArgumentList.Add(instanceId);
            using var p = Process.Start(psi)!;
            var output = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
            if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return (false, "timed out"); }
            return (p.ExitCode == 0, output.Length > 200 ? output[..200] : output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}

/// <summary>
/// Last resort: power-cycle the USB hub port the WireView is plugged into (IOCTL_USB_HUB_CYCLE_PORT), which makes
/// Windows see an unplug and replug. Used only when a device restart did not bring the port back.
/// </summary>
[SupportedOSPlatform("windows")]
public static class UsbHubPortCycler
{
    private const int CR_SUCCESS = 0;
    private const uint IOCTL_USB_HUB_CYCLE_PORT = 0x220444;
    private const int CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0;
    private static readonly Guid GuidDevInterfaceUsbHub = new("f18a0e88-c30c-11d0-8815-00a0c906bed8");
    private const int SPDRP_ADDRESS = 0x1C;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out int devInst, string deviceId, int flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out int parent, int devInst, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_IDW(int devInst, char[] buffer, int bufferLen, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List_SizeW(out int len, ref Guid classGuid, string deviceId, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_ListW(ref Guid classGuid, string deviceId, char[] buffer, int bufferLen, int flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Registry_PropertyW(int devInst, int property, out int regType, out int buffer, ref int length, int flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle device, uint code, ref UsbCyclePortParams inBuf, int inSize,
        ref UsbCyclePortParams outBuf, int outSize, out int returned, IntPtr overlapped);

    [StructLayout(LayoutKind.Sequential)]
    private struct UsbCyclePortParams
    {
        public int ConnectionIndex;
        public int StatusReturned;
    }

    private const int CM_DRP_ADDRESS = SPDRP_ADDRESS + 1; // CM_DRP_* = SPDRP_* + 1

    private static string DeviceId(int devInst)
    {
        var buf = new char[512];
        return CM_Get_Device_IDW(devInst, buf, buf.Length, 0) == CR_SUCCESS ? new string(buf).TrimEnd('\0') : "";
    }

    public static (bool Ok, string Message) CycleForPort(string? port)
    {
        var ids = UsbDeviceRestarter.FindInstanceIds(port);
        if (ids.Count == 0) return (false, "no WireView USB device found in the device registry");
        try
        {
            if (CM_Locate_DevNodeW(out int dev, ids[0], 0) != CR_SUCCESS) return (false, $"device {ids[0]} is not present");
            // A composite device's serial interface (…&MI_00) sits under the USB device itself; the hub is one level above that.
            if (DeviceId(dev).Contains("&MI_", StringComparison.OrdinalIgnoreCase) && CM_Get_Parent(out int usbDev, dev, 0) == CR_SUCCESS) dev = usbDev;

            int len = sizeof(int);
            if (CM_Get_DevNode_Registry_PropertyW(dev, CM_DRP_ADDRESS, out _, out int portNumber, ref len, 0) != CR_SUCCESS || portNumber <= 0)
                return (false, "could not read the USB port number");
            if (CM_Get_Parent(out int hub, dev, 0) != CR_SUCCESS) return (false, "could not find the parent USB hub");
            var hubId = DeviceId(hub);

            var guid = GuidDevInterfaceUsbHub;
            if (CM_Get_Device_Interface_List_SizeW(out int size, ref guid, hubId, CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != CR_SUCCESS || size <= 1)
                return (false, $"hub {hubId} has no device interface");
            var list = new char[size];
            if (CM_Get_Device_Interface_ListW(ref guid, hubId, list, size, CM_GET_DEVICE_INTERFACE_LIST_PRESENT) != CR_SUCCESS)
                return (false, $"cannot list interfaces of hub {hubId}");
            var hubPath = new string(list).Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (hubPath is null) return (false, $"hub {hubId} has no device interface");

            using var h = CreateFileW(hubPath, 0x40000000 /* GENERIC_WRITE */, 0x2 /* FILE_SHARE_WRITE */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
            if (h.IsInvalid) return (false, $"cannot open hub: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            var p = new UsbCyclePortParams { ConnectionIndex = portNumber };
            if (!DeviceIoControl(h, IOCTL_USB_HUB_CYCLE_PORT, ref p, Marshal.SizeOf<UsbCyclePortParams>(), ref p, Marshal.SizeOf<UsbCyclePortParams>(), out _, IntPtr.Zero))
                return (false, $"cycle port {portNumber} on {hubId} failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            return (true, $"power-cycled port {portNumber} on {hubId}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
