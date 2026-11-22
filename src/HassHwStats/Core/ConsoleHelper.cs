using System.Runtime.InteropServices;

namespace HassHwStats.Core;

/// <summary>The app is a WinExe so it runs without a window; diagnostic modes borrow or open a console.</summary>
internal static class ConsoleHelper
{
    /// <returns>true when a new console window was opened (so the caller should wait before closing it).</returns>
    public static bool Ensure()
    {
        if (AttachConsole(-1)) { Reopen(); return false; }
        AllocConsole();
        Reopen();
        return true;
    }

    private static void Reopen()
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(stdout);
        Console.SetError(stdout);
    }

    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int processId);
    [DllImport("kernel32.dll")] private static extern bool AllocConsole();
}
