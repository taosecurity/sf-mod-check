// Small helpers for running sfmodcheck by hand (step 5, 2026-11-09). None of them changes a finding.
using System.Runtime.InteropServices;

namespace SfModCheck;

public static class Interactive
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint GetConsoleProcessList(uint[] processList, uint processCount);

    /// <summary>True when sfmodcheck has a console window to itself: Windows made one for it because a plugin was
    /// dropped on the exe or the exe was double-clicked. False when run from a prompt, a script or another program
    /// (they share their console or redirect the output), and always false off Windows.</summary>
    public static bool OwnsConsole()
    {
        if (!OperatingSystem.IsWindows() || Console.IsInputRedirected || Console.IsOutputRedirected || Console.IsErrorRedirected)
            return false;
        try { return GetConsoleProcessList(new uint[2], 2) == 1; }
        catch { return false; }
    }

}
