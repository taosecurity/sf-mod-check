// 0.10.0 item 6a: the 0.6.1 leftovers (NOTES "Release day: Linux CI failure and fix (2026-11-19)", "Left for 0.6.1",
// and the PAQO stack-trace decision in RELAY-2026-11-19.md).
using Xunit;

namespace SfModCheck.Tests;

public class Leftovers061Tests
{
    // Mutagen prints an error it caught with Console.WriteLine(exception) (its scene reader's "Unexpected header type:
    // PAQO"). The stdout writer sends exception objects to the divert sink (stderr and the log in the program) and
    // keeps every string line, escaped as before.
    [Fact]
    public void An_exception_mutagen_prints_leaves_stdout_and_strings_stay()
    {
        var stdout = new StringWriter();
        var diverted = new List<string>();
        var w = new SafeConsoleWriter(stdout, diverted.Add);
        var e = new ArgumentException("Unexpected header type: PAQO");
        w.WriteLine((object)e);
        w.Write((object)e);
        w.WriteLine("RESULT: OK, 0 errors");
        w.WriteLine((object)"a string as object\u001b");
        w.WriteLine((object)42);
        Assert.Equal("RESULT: OK, 0 errors\na string as object\\x1B\n42\n", stdout.ToString().Replace("\r\n", "\n"));
        Assert.Equal(2, diverted.Count);
        Assert.All(diverted, d => Assert.StartsWith("System.ArgumentException: Unexpected header type: PAQO", d));
        // the stderr writer (no sink) prints exceptions as it always did
        var stderr = new StringWriter();
        new SafeConsoleWriter(stderr).WriteLine((object)e);
        Assert.StartsWith("System.ArgumentException: Unexpected header type: PAQO", stderr.ToString());
    }

    // "\x.ba2" and "/x.ba2" in an INI's archive list mean under Data, on every system; the INI path is tested end to end.
    [Theory]
    [InlineData("\\Extra - Main.ba2")]
    [InlineData("/Extra - Main.ba2")]
    [InlineData("Extra - Main.ba2")]
    public void An_ini_archive_name_with_one_leading_slash_is_under_data(string listed)
    {
        using var s = new Sandbox();
        var data = s.Sub("Data");
        File.WriteAllBytes(Path.Combine(data, "Extra - Main.ba2"), AssetsTests.Ba2("GNRL", "meshes\\x.nif"));
        var ini = Path.Combine(s.Dir, "StarfieldCustom.ini");
        File.WriteAllText(ini, $"[Archive]\nsResourceArchiveList={listed}\n");
        var index = AssetIndex.Build(Array.Empty<string>(), new[] { data }, new[] { ini }, "en");
        Assert.Contains("Extra - Main.ba2", index.Gnrl);
        Assert.True(index.Has("meshes\\x.nif"));
        Assert.DoesNotContain(index.Skipped, x => x.Why.Contains("not found"));
    }

    // A "\\server\share" name (either slash) has no Linux or macOS path: it isn't looked up under Data. (On Windows it is
    // a network path looked up as given; the test doesn't touch the network.)
    [UnixFact("off Windows only: on Windows the name is a real network path")]
    public void A_network_name_is_not_looked_up_under_data_off_windows()
    {
        using var s = new Sandbox();
        var data = s.Sub("Data");
        Directory.CreateDirectory(Path.Combine(data, "server", "share"));
        File.WriteAllBytes(Path.Combine(data, "server", "share", "x.ba2"), new byte[4]);
        var index = AssetIndex.Build(Array.Empty<string>(), new[] { data }, Array.Empty<string>(), "en");
        Assert.NotNull(index.LooseFile(data, "server\\share\\x.ba2"));
        Assert.Null(index.LooseFile(data, "\\\\server\\share\\x.ba2"));
        Assert.Null(index.LooseFile(data, "//server/share/x.ba2"));
        Assert.Null(index.LooseFile(data, "\\/server\\share\\x.ba2"));
        Assert.Null(index.LooseFile(data, "C:\\Data\\server\\share\\x.ba2"));
    }

    // Off Windows a sub-folder that can't be listed made its loose files count as missing with no reason; now the log
    // names the folder. Write and execute without read: the folder can't be listed, but files in it can still be opened.
    [UnixFact("folder permissions are Linux and macOS ones here")]
    public void An_unlistable_sub_folder_is_named_in_the_log()
    {
        using var s = new Sandbox();
        var data = s.Sub("Data");
        var scripts = Path.Combine(data, "Scripts");
        Directory.CreateDirectory(scripts);
        File.WriteAllBytes(Path.Combine(scripts, "Foo.pex"), new byte[4]);
        var mode = File.GetUnixFileMode(scripts);
        File.SetUnixFileMode(scripts, UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            // root (some containers) lists any folder: nothing would fail, so there's nothing to test
            try { Directory.EnumerateFileSystemEntries(scripts).ToList(); return; } catch (UnauthorizedAccessException) { }
            var index = AssetIndex.Build(Array.Empty<string>(), new[] { data }, Array.Empty<string>(), "en");
            Assert.False(index.Has("scripts\\foo.pex"));
            Assert.Equal(new[] { scripts }, index.Unlistable);
        }
        finally { File.SetUnixFileMode(scripts, mode); }
    }

    // A Data or --extra-data folder that can't be listed stops the assets step (conflict's "WARNING: the assets check
    // stopped (...)", assetsFailed in the report). conflict can't reach that point through its own run here, because the
    // load order lists the same folders first, so the test checks the step's error is one that handler takes.
    [UnixFact("folder permissions are Linux and macOS ones here")]
    public void An_unlistable_archive_folder_stops_the_assets_step_with_a_recoverable_error()
    {
        using var s = new Sandbox();
        var data = s.Sub("Data");
        File.WriteAllBytes(Path.Combine(data, "Mod - Main.ba2"), AssetsTests.Ba2("GNRL", "meshes\\x.nif"));
        var mode = File.GetUnixFileMode(data);
        File.SetUnixFileMode(data, UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            try { Directory.EnumerateFileSystemEntries(data).ToList(); return; } catch (UnauthorizedAccessException) { }   // root
            var e = Assert.Throws<UnauthorizedAccessException>(() => AssetIndex.Build(new[] { "Mod.esm" }, new[] { data }, Array.Empty<string>(), "en"));
            Assert.True(Fatal.Recoverable(e));
        }
        finally { File.SetUnixFileMode(data, mode); }
    }
}
