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
