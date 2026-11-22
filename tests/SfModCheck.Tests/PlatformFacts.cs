// Tests that only mean something on one kind of system (0.10.0, a 0.6.1 leftover). xunit 2.9 has no runtime skip, so a
// test that returned early on the wrong system showed as passed; these attributes set Skip instead, so the run reports
// it as skipped, with the reason (as NeedsStarfieldFact does).
using Xunit;

namespace SfModCheck.Tests;

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute(string why = "Windows only")
    {
        if (!OperatingSystem.IsWindows()) Skip = why;
    }
}

public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute(string why = "Linux and macOS only")
    {
        if (OperatingSystem.IsWindows()) Skip = why;
    }
}

/// <summary>Linux and macOS, not as root: for tests of folders the account can't read, which root reads anyway
/// (0.11.0 code-review round 7: an early return showed as passed in root containers). Code-review, 0.11.0 VMAD order:
/// no P/Invoke (a "libc" import can miss on glibc systems), and a probe folder with no permissions is tried, so an
/// account that reads it anyway (CAP_DAC_OVERRIDE in a container) skips too instead of failing.</summary>
public sealed class UnixNotRootFactAttribute : FactAttribute
{
    static readonly string? why = OperatingSystem.IsWindows() ? "Linux and macOS only"
        : Environment.IsPrivilegedProcess ? "running as root, which can read every folder"
        : LockedFolderProbe();

    public UnixNotRootFactAttribute()
    {
        if (why is not null) Skip = why;
    }

    /// <summary>Null when a folder with no permissions can't be read (the tests can run), else why they skip.</summary>
    static string? LockedFolderProbe()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sf-mod-check-lockprobe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.SetUnixFileMode(dir, UnixFileMode.None);
            try { Directory.GetFileSystemEntries(dir); return "this account can read folders it has no permission for (a capability such as CAP_DAC_OVERRIDE)"; }
            catch (UnauthorizedAccessException) { return null; }
        }
        // the probe itself failed (code-review, 0.11.0 VMAD order round 2: say so, not the capability reason)
        catch (Exception e) { return $"the permission probe in {Path.GetTempPath()} failed ({e.GetType().Name}: {e.Message})"; }
        finally
        {
            try { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); Directory.Delete(dir, true); }
            catch (Exception) { /* a temp folder left behind */ }
        }
    }
}

/// <summary>Needs folder links (a junction on Windows, a symbolic link elsewhere): skipped, with the reason, where they
/// can't be made (0.11.0 code-review round 3: a test that returned early showed as passed).</summary>
public sealed class LinksFactAttribute : FactAttribute
{
    static readonly bool canLink = Probe();

    public LinksFactAttribute()
    {
        if (!canLink) Skip = "folder links can't be made here (no rights)";
    }

    static bool Probe()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sf-mod-check-linkprobe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "t"));
            return FollowUps091Tests.TryLink(Path.Combine(dir, "l"), Path.Combine(dir, "t"), folder: true);
        }
        // any failure (cmd.exe can't start: Win32Exception) means "can't link here", never a type-initializer error that
        // turns every [LinksFact] test into a discovery error (code-review round 4)
        catch (Exception) { return false; }
        finally
        {
            try { if (Directory.Exists(Path.Combine(dir, "l"))) Directory.Delete(Path.Combine(dir, "l")); Directory.Delete(dir, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* a temp folder left behind */ }
        }
    }
}
