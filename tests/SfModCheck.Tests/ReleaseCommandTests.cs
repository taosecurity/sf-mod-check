// The release command's frame (0.7.0, chunk C0): unpacking, profiles, report and exit codes. The steps themselves have
// their own test files.
using System.IO.Compression;
using SfModCheck;
using Xunit;

namespace SfModCheck.Tests;

public class ReleaseCommandTests
{
    static string MakeZip(Sandbox s, string name, params (string Path, string Text)[] entries)
    {
        var zip = Path.Combine(s.Sub("zips"), name);
        using var z = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var (p, t) in entries)
        {
            using var w = new StreamWriter(z.CreateEntry(p).Open());
            w.Write(t);
        }
        return zip;
    }

    [Fact]
    public void Unpack_lists_files_plugins_and_archives()
    {
        using var s = new Sandbox();
        var zip = MakeZip(s, "Mod-0.1.0.zip", ("MyMod.esm", "x"), ("MyMod - Main.ba2", "x"), ("Scripts/Source/Foo.psc", "x"));
        var p = ReleaseCommand.Unpack(zip, Path.Combine(s.Dir, "out"));
        Assert.Equal(["mymod - main.ba2", "mymod.esm", "scripts\\source\\foo.psc"], p.Files);
        Assert.Single(p.Plugins);
        Assert.EndsWith("MyMod.esm", p.Plugins[0]);
        Assert.Single(p.Archives);
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("/rooted.txt")]
    [InlineData("C:/x.txt")]
    public void Unpack_refuses_entries_outside_the_folder(string entry)
    {
        using var s = new Sandbox();
        var zip = MakeZip(s, "Bad.zip", (entry, "x"));
        Assert.Throws<InvalidDataException>(() => ReleaseCommand.Unpack(zip, Path.Combine(s.Dir, "out")));
    }

    [Fact]
    public void Unpack_refuses_a_name_twice()
    {
        using var s = new Sandbox();
        var zip = MakeZip(s, "Dup.zip", ("README-A.md", "x"), ("readme-a.md", "y"));
        Assert.Throws<InvalidDataException>(() => ReleaseCommand.Unpack(zip, Path.Combine(s.Dir, "out")));
    }

    [Fact]
    public void Basic_profile_turns_house_problems_into_warnings_only()
    {
        var house = new ReleaseFinding(Level.Problem, "one-plugin", "two plugins", RuleKind.House);
        var engine = new ReleaseFinding(Level.Problem, "plugin-name", "dropped", RuleKind.Engine);
        Assert.Equal(Level.Warning, ReleaseCommand.Apply(house, "basic").Level);
        Assert.Equal(Level.Problem, ReleaseCommand.Apply(house, "house").Level);
        Assert.Equal(Level.Problem, ReleaseCommand.Apply(engine, "basic").Level);
        Assert.Equal(Level.Review, ReleaseCommand.Apply(house with { Level = Level.Review }, "basic").Level);
    }

    [Fact]
    public void Exe_gives_OK_on_a_clean_zip_and_2_on_bad_input()
    {
        using var s = new Sandbox();
        var data = s.Sub("Data");
        var zip = MakeZip(s, "Mod-0.1.0.zip", ("README-Mod.md", "x"));
        var (code, stdout, _) = ExeTests.RunSplit("release", zip, "--data", data, "--quiet");
        Assert.True(code == 0 || code == 1, stdout);   // the steps decide OK or FAIL; the frame must give a RESULT
        Assert.Matches(@"^RESULT: (OK|FAIL) \(\d+ problems, \d+ to review, \d+ warnings\)$", ExeTests.ResultLine(stdout));

        Assert.Equal(2, ExeTests.RunSplit("release", Path.Combine(s.Dir, "missing.zip"), "--data", data).Code);
        Assert.Equal(2, ExeTests.RunSplit("release", zip, "--profile", "strict", "--data", data).Code);
        Assert.Equal(2, ExeTests.RunSplit("release").Code);
    }

    [Fact]
    public void An_unreadable_archive_is_one_problem_not_two()
    {
        var list = new List<ReleaseFinding>
        {
            new(Level.Problem, "ba2-unreadable", "Mod.zip:Mod - Main.ba2: can't read the archive to check its sound files (bad)"),
            new(Level.Problem, "script-missing", @"archive C:\tmp\zip0\Mod - Main.ba2 could not be read (bad), so its files count as missing: rebuild"),
            new(Level.Problem, "script-missing", @"archive C:\tmp\Other.ba2 could not be read (bad), so its files count as missing: rebuild"),
        };
        var r = ReleaseCommand.DedupeUnreadable(list);
        Assert.Equal(2, r.Count);
        Assert.DoesNotContain(r, f => f.Text.Contains("zip0"));
    }

    [Fact]
    public void A_shorter_archive_name_does_not_hide_another_archives_problem()
    {
        var list = new List<ReleaseFinding>
        {
            new(Level.Problem, "ba2-unreadable", "Mod.zip:BigMain.ba2: can't read the archive to check its sound files (bad)"),
            new(Level.Problem, "script-missing", @"archive C:\x\Main.ba2 could not be read (bad), so its files count as missing: rebuild"),
        };
        Assert.Equal(2, ReleaseCommand.DedupeUnreadable(list).Count);
    }

    [Fact]
    public void A_zip_that_unpacks_to_more_than_the_cap_is_refused()
    {
        using var s = new Sandbox();
        var zip = MakeZip(s, "Big-0.1.0.zip", ("a.bin", new string('x', 5000)));
        var old = ReleaseCommand.MaxUnpackedBytes;
        ReleaseCommand.MaxUnpackedBytes = 1000;
        try { Assert.Throws<InvalidDataException>(() => ReleaseCommand.Unpack(zip, Path.Combine(s.Dir, "out"))); }
        finally { ReleaseCommand.MaxUnpackedBytes = old; }
    }
}
