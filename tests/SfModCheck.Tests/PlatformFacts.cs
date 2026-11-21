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
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        finally
        {
            try { if (Directory.Exists(Path.Combine(dir, "l"))) Directory.Delete(Path.Combine(dir, "l")); Directory.Delete(dir, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* a temp folder left behind */ }
        }
    }
}
