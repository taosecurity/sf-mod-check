// release inputs besides zips (0.15.0): a stage folder used in place, loose .ba2 and plugin files, and --size-total for
// the creations profile. Everything is made up here: no game files.
using System.IO.Compression;
using System.Text;
using SfModCheck;
using Xunit;

namespace SfModCheck.Tests;

[Collection(ReleaseStatics.Name)]   // changes ReleaseCreations.SizeLimit
public class ReleaseInputsTests
{
    static byte[] T(string s) => Encoding.UTF8.GetBytes(s);

    static string Zip(Sandbox s, string name, params (string Path, byte[] Data)[] entries)
    {
        var zip = Path.Combine(s.Sub("zips"), name);
        using var z = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var (p, d) in entries)
        {
            using var w = z.CreateEntry(p).Open();
            w.Write(d);
        }
        return zip;
    }

    static string Put(string dir, string rel, byte[] data)
    {
        var path = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        return path;
    }

    /// <summary>A fake Data folder with a Starfield.esm, so "inside the game install" can be tested.</summary>
    static string Data(Sandbox s)
    {
        var data = s.Sub(Path.Combine("Game", "Data"));
        File.WriteAllBytes(Path.Combine(data, "Starfield.esm"), [0]);
        return data;
    }

    /// <summary>Every file under dir with its size and modified time, to prove a run wrote nothing there.</summary>
    static List<string> Snapshot(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => $"{Path.GetRelativePath(dir, f)}|{new FileInfo(f).Length}|{File.GetLastWriteTimeUtc(f).Ticks}")
            .Order(StringComparer.Ordinal).ToList();

    static string Rules(List<ReleaseFinding> f) => string.Join("\n", f.Select(x => $"  {x.Level} [{x.Rule}] {x.Text}"));

    [Fact]
    public void A_folder_skips_the_zip_only_rules_with_one_note()
    {
        using var s = new Sandbox();
        var stage = s.Sub("My Stage");
        Put(stage, "MyMod.esm", ReleaseRulesTests.Plugin());
        Put(stage, "MyModExtra.esm", ReleaseRulesTests.Plugin());
        Put(stage, "README.md", T("hello"));
        var pkg = ReleaseCommand.FromFolder(stage);
        Assert.False(pkg.IsZip);
        Assert.Equal("My Stage", pkg.Name);
        Assert.Equal(Path.GetFullPath(stage), pkg.Root);   // used in place
        Assert.Equal(2, pkg.Plugins.Count);
        Assert.Contains("readme.md", pkg.Files);

        var f = ReleaseRules.Run(new ReleaseContext { Options = new ReleaseOptions(), Packages = [pkg], Work = s.Sub("work") }).ToList();
        Assert.DoesNotContain(f, x => x.Rule is "one-plugin" or "root-md");
        var note = Assert.Single(f, x => x.Rule == "not-a-zip");
        Assert.Equal(Level.Note, note.Level);
        Assert.Contains("My Stage: not a zip", note.Text);

        // the same files in a zip still get both rules, and no not-a-zip NOTE
        var zip = Zip(s, "MyMod-1.0.0.zip", ("MyMod.esm", ReleaseRulesTests.Plugin()), ("MyModExtra.esm", ReleaseRulesTests.Plugin()), ("README.md", T("hello")));
        var z = ReleaseCommand.Unpack(zip, Path.Combine(s.Dir, "out"));
        var g = ReleaseRules.Run(new ReleaseContext { Options = new ReleaseOptions(), Packages = [z], Work = s.Sub("work2") }).ToList();
        Assert.Contains(g, x => x.Rule == "one-plugin");
        Assert.Contains(g, x => x.Rule == "root-md");
        Assert.DoesNotContain(g, x => x.Rule == "not-a-zip");
    }

    [Fact]
    public void A_folder_run_writes_nothing_into_the_folder()
    {
        using var s = new Sandbox();
        var data = Data(s);
        var stage = s.Sub("My Stage");
        Put(stage, "MyMod.esm", ReleaseRulesTests.Plugin());
        Put(stage, "Scripts/MyMod/Quest.pex", [1, 2, 3]);
        Put(stage, "Scripts/Source/MyMod/Quest.psc", T("ScriptName MyMod:Quest"));
        var before = Snapshot(stage);

        var (code, stdout, stderr) = ExeTests.RunSplit("release", stage, "--data", data, "--tested", Zip(s, "MyMod-0.9.0.zip", ("MyMod.esm", ReleaseRulesTests.Plugin())));
        Assert.True(code is 0 or 1, $"exit {code}\n{stdout}\n{stderr}");
        Assert.Contains("release (basic profile): My Stage (", stdout);
        Assert.Contains("[not-a-zip] My Stage: not a zip", stdout);
        Assert.Equal(before, Snapshot(stage));
    }

    [Fact]
    public void Loose_ba2_and_plugin_inputs_are_one_package()
    {
        using var s = new Sandbox();
        var data = Data(s);
        var dir = s.Sub("build");
        var plugin = Put(dir, "MyMod.esm", ReleaseRulesTests.Plugin());
        var other = Put(s.Sub("elsewhere"), "MyMod - Main.ba2", T("not a real archive"));

        var (code, stdout, stderr) = ExeTests.RunSplit("release", plugin, other, "--data", data);
        Assert.True(code is 0 or 1, $"exit {code}\n{stdout}\n{stderr}");
        Assert.Contains("release (basic profile): loose files (2 files, 1 plugins, 1 archives)", stdout);
        Assert.Contains("[not-a-zip] loose files: not a zip", stdout);
        // the archive sits beside its plugin in the package, so archive-name is satisfied; it can't be read, so that is reported
        Assert.DoesNotContain("[archive-name]", stdout);
        Assert.Contains("[ba2-unreadable] loose files:MyMod - Main.ba2", stdout);
        Assert.True(File.Exists(other));   // copied, never moved

        // a zip and a folder in one run: each is its own package
        var stage = s.Sub("Stage");
        Put(stage, "Addon.esm", ReleaseRulesTests.Plugin());
        var zip = Zip(s, "Other-1.0.0.zip", ("Other.esm", ReleaseRulesTests.Plugin()));
        (code, stdout, _) = ExeTests.RunSplit("release", zip, stage, "--data", data);
        Assert.True(code is 0 or 1, stdout);
        Assert.Contains("release (basic profile): Other-1.0.0.zip, Stage (", stdout);
    }

    [Fact]
    public void Bad_inputs_are_exit_2()
    {
        using var s = new Sandbox();
        var data = Data(s);
        var a = Put(s.Sub("a"), "MyMod.esm", ReleaseRulesTests.Plugin());
        var b = Put(s.Sub("b"), "mymod.esm", ReleaseRulesTests.Plugin());
        var (code, stdout, stderr) = ExeTests.RunSplit("release", a, b, "--data", data);
        Assert.Equal(2, code);
        Assert.Contains("RESULT: CANNOT CHECK (two loose files are named MyMod.esm)", stdout);
        Assert.Contains("separate runs", stderr);

        var inside = s.Sub(Path.Combine("Game", "Stage"));
        (code, stdout, _) = ExeTests.RunSplit("release", inside, "--data", data);
        Assert.Equal(2, code);
        Assert.Contains("RESULT: CANNOT CHECK (Stage is inside the game install)", stdout);
        (code, stdout, _) = ExeTests.RunSplit("release", Path.GetDirectoryName(data)!, "--data", data);   // the install folder itself
        Assert.Equal(2, code);
        Assert.Contains("is inside the game install", stdout);

        (code, stdout, stderr) = ExeTests.RunSplit("release", Path.Combine(s.Dir, "nothing here"), "--data", data);
        Assert.Equal(2, code);
        Assert.Contains("no such file or folder", stderr);

        (code, _, stderr) = ExeTests.RunSplit("release", a, "--data", data, "--size-total");
        Assert.Equal(2, code);
        Assert.Contains("--size-total works only with --profile creations", stderr);
    }

    [Fact]
    public void A_folder_with_too_many_files_is_refused()
    {
        using var s = new Sandbox();
        var stage = s.Sub("Big");
        for (int i = 0; i < 4; i++) Put(stage, $"f{i}.txt", [0]);
        var old = ReleaseCommand.MaxEntries;
        ReleaseCommand.MaxEntries = 3;
        try { Assert.Contains("more than the 3 files", Assert.Throws<InvalidDataException>(() => ReleaseCommand.FromFolder(stage)).Message); }
        finally { ReleaseCommand.MaxEntries = old; }
    }

    [Fact]
    public void Size_total_counts_the_whole_run_as_one_creation()
    {
        using var s = new Sandbox();
        var a = ReleaseCommand.Unpack(Zip(s, "A-0.1.0.zip", ("A.esm", ReleaseRulesTests.Plugin("Starfield.esm"))), Path.Combine(s.Dir, "out", "a"));
        var stage = s.Sub("B Stage");
        Put(stage, "B.esm", ReleaseRulesTests.Plugin("Starfield.esm"));
        var b = ReleaseCommand.FromFolder(stage);
        long each = new FileInfo(a.Plugins[0]).Length;
        Assert.Equal(each, new FileInfo(b.Plugins[0]).Length);
        var old = ReleaseCreations.SizeLimit;
        ReleaseCreations.SizeLimit = each + each / 2;   // each input is under it, both together are over
        try
        {
            List<ReleaseFinding> Run(bool total) =>
                ReleaseCreations.Run(new ReleaseContext { Options = new ReleaseOptions { Profile = "creations", SizeTotal = total }, Packages = [a, b], Work = s.Sub("work") })
                    .Where(x => x.Rule == "creations-size").ToList();

            var apart = Run(false);
            Assert.Equal(2, apart.Count);
            Assert.All(apart, x => Assert.Equal(Level.Note, x.Level));

            var together = Run(true);
            var one = Assert.Single(together);
            Assert.True(one.Level == Level.Problem, Rules(together));
            Assert.StartsWith("all 2 inputs together (--size-total): the plugins and archives total", one.Text);
        }
        finally { ReleaseCreations.SizeLimit = old; }
        // at the real limit the same run is one NOTE
        var note = Assert.Single(ReleaseCreations.Run(new ReleaseContext { Options = new ReleaseOptions { Profile = "creations", SizeTotal = true }, Packages = [a, b], Work = s.Sub("work3") }),
            x => x.Rule == "creations-size");
        Assert.Equal(Level.Note, note.Level);
        Assert.Contains("(a Creation may hold 2 GB)", note.Text);
    }

    [Fact]
    public void Inputs_and_size_total_are_in_the_usage_readme_agents_and_changelog()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "ReleaseCommand.cs"))) dir = dir.Parent;
        Assert.True(dir != null, "src\\ReleaseCommand.cs not found above " + AppContext.BaseDirectory);
        var readme = File.ReadAllText(Path.Combine(dir!.FullName, "README.md"));
        var agents = File.ReadAllText(Path.Combine(dir.FullName, "AGENTS.md"));
        var changelog = File.ReadAllText(Path.Combine(dir.FullName, "CHANGELOG.md"));
        var help = ExeTests.RunSplit("release", "--help");
        Assert.Equal(0, help.Code);
        foreach (var text in new[] { "release <zip|folder|ba2|plugin>...", "--size-total" })
        {
            Assert.Contains(text, ReleaseCommand.Usage);
            Assert.Contains(text, help.Stdout);
            Assert.Contains(text, readme);
            Assert.Contains(text, agents);
            Assert.Contains(text, changelog);
        }
        Assert.Contains("| `not-a-zip` |", readme);
    }
}
