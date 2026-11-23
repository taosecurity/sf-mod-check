// release --tested and --stage (0.15.0, plan step 6). Every script is made up and built byte by byte (PexBytes in
// PexTests.cs), every archive by TriageScriptsTests.Ba2; plugins are plain bytes (only compared, never parsed here).
using System.IO.Compression;
using SfModCheck;
using Xunit;

namespace SfModCheck.Tests;

public class ReleaseTestedTests
{
    /// <summary>A script with two functions; `value` is the number one of them returns. `shuffled` writes the functions in
    /// the other order and names the temporary differently, as another build of the same source does.</summary>
    static byte[] Script(int value, bool shuffled = false)
    {
        var temp = shuffled ? "::temp7" : "::temp0";
        var a = new PFn("Alpha", "Int", Locals: [(temp, "Int")], Code: [PIns.Assign(temp, PV.Int(value)), PIns.Return(PV.Id(temp))]);
        var b = new PFn("Beta", Code: [PIns.CallStatic("Debug", "Trace", "::nonevar", PV.Str("MyMod: hello"))]);
        var bytes = PexBytes.Build([new PObj("MyMod:Thing", "Quest",
            Vars: shuffled ? [("Count", "Int", PV.Int(3)), ("Name", "String", PV.Str("x"))] : [("Name", "String", PV.Str("x")), ("Count", "Int", PV.Int(3))],
            States: [("", shuffled ? [b, a] : [a, b])])]);
        if (shuffled) bytes[8] ^= 0x55;   // another compile time
        return bytes;
    }

    static string Zip(Sandbox s, string name, params (string Path, byte[] Data)[] entries)
    {
        var zip = Path.Combine(s.Sub("zips"), name);
        using var z = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var (p, d) in entries)
            using (var w = z.CreateEntry(p).Open()) w.Write(d);
        return zip;
    }

    int runs;

    List<ReleaseFinding> Run(Sandbox s, string zip, string[] tested, string? stage = null)
    {
        var run = $"run{++runs}";   // each run its own folders, as each release run has
        var p = ReleaseCommand.Unpack(zip, Path.Combine(s.Dir, run, "zip0"));
        var o = new ReleaseOptions { Stage = stage };
        o.Tested.AddRange(tested);
        return ReleaseTested.Run(new ReleaseContext { Options = o, Packages = [p], Work = s.Sub(Path.Combine(run, "work")) }).ToList();
    }

    static string List(IEnumerable<ReleaseFinding> f) => string.Join("\n", f.Select(x => $"  {x.Level} [{x.Rule}] {x.Text}"));

    static void Has(List<ReleaseFinding> f, Level level, string rule, string text) =>
        Assert.True(f.Any(x => x.Level == level && x.Rule == rule && x.Text.Contains(text)), $"wanted {level} [{rule}] ~'{text}', got:\n{List(f)}");

    [Fact]
    public void Another_build_of_the_same_source_lists_the_same_and_a_changed_one_does_not()
    {
        var one = Pex.Listing(Pex.Read(Script(1), "a.pex"));
        Assert.NotEqual(Script(1), Script(1, shuffled: true));   // the bytes differ
        Assert.Equal(one, Pex.Listing(Pex.Read(Script(1, shuffled: true), "b.pex")));
        Assert.NotEqual(one, Pex.Listing(Pex.Read(Script(2), "c.pex")));
        Assert.Contains(one, l => l.Contains("ident:::t1"));   // the temporary is renamed
        Assert.DoesNotContain(one, l => l.Contains("tester") || l.Contains("testbox"));   // the user and machine are left out
        // debug line info is compared unless left out
        var noDebug = PexBytes.Build([new PObj("MyMod:Thing")], debug: false);
        Assert.DoesNotContain(Pex.Listing(Pex.Read(noDebug, "d.pex")), l => l.StartsWith("debug "));
    }

    [Fact]
    public void Scripts_same_changed_missing_and_new_and_the_plugin()
    {
        using var s = new Sandbox();
        var tested = Zip(s, "MyMod-1.0.0.zip",
            ("MyMod.esm", [1, 2, 3]),
            ("MyMod - Main.ba2", TriageScriptsTests.Ba2(("scripts/mymod/same.pex", Script(1), true), ("scripts/mymod/changed.pex", Script(1), false),
                                                         ("scripts/mymod/gone.pex", Script(1), true))));
        var release = Zip(s, "MyMod-1.0.1.zip",
            ("MyMod.esm", [1, 2, 3]),
            ("MyMod - Main.ba2", TriageScriptsTests.Ba2(("scripts/mymod/same.pex", Script(1, shuffled: true), true),
                                                         ("scripts/mymod/changed.pex", Script(2), true))),
            ("Scripts/MyMod/Added.pex", Script(5)));
        var f = Run(s, release, [tested]);
        Has(f, Level.Note, "tested", @"script same as the tested build: scripts\mymod\same.pex");
        Has(f, Level.Review, "tested", @"script differs from the tested build (expected only if its source changed): scripts\mymod\changed.pex");
        Has(f, Level.Review, "tested", @"script in the tested build, missing now: scripts\mymod\gone.pex");
        Has(f, Level.Review, "tested", @"script not in the tested build (new since the test): scripts\mymod\added.pex");
        Has(f, Level.Note, "tested", "plugin is byte-identical to the tested build: MyMod.esm");
        Assert.DoesNotContain(f, x => x.Level == Level.Problem);

        var changedPlugin = Zip(s, "MyMod-1.0.2.zip", ("MyMod.esm", [1, 2, 4]));
        Has(Run(s, changedPlugin, [tested]), Level.Review, "tested", "plugin differs from the tested build (expected only if its records changed): MyMod.esm");
        var renamed = Zip(s, "Other-1.0.0.zip", ("Other.esm", [1]));
        Has(Run(s, renamed, [tested]), Level.Review, "tested", "no plugin named Other.esm in the tested build; plugin not compared");
    }

    [Fact]
    public void A_tested_archive_alone_compares_scripts_only_and_a_damaged_script_is_a_problem()
    {
        using var s = new Sandbox();
        var ba2 = Path.Combine(s.Sub("tested"), "MyMod - Main.ba2");
        File.WriteAllBytes(ba2, TriageScriptsTests.Ba2(("scripts/mymod/a.pex", Script(1), true), ("scripts/mymod/b.pex", Script(1), false)));
        var damaged = Script(1)[..40];
        var release = Zip(s, "MyMod-1.0.1.zip", ("MyMod.esm", [9]),
            ("MyMod - Main.ba2", TriageScriptsTests.Ba2(("scripts/mymod/a.pex", Script(1, shuffled: true), false), ("scripts/mymod/b.pex", damaged, false))));
        var f = Run(s, release, [ba2]);
        Has(f, Level.Note, "tested", @"script same as the tested build: scripts\mymod\a.pex");
        Has(f, Level.Problem, "tested", "script can't be read for the tested-build comparison");
        Assert.DoesNotContain(f, x => x.Text.Contains("plugin"));   // a .ba2 has no plugin to compare

        // a tested zip that isn't a zip: one PROBLEM naming it
        var bad = Path.Combine(s.Sub("bad"), "Broken.zip");
        File.WriteAllText(bad, "not a zip");
        Has(Run(s, release, [bad]), Level.Problem, "tested", "the tested build can't be read, so no tested-build comparison ran: Broken.zip");
    }

    [Fact]
    public void Stage_files_missing_from_the_archives_and_archive_files_not_staged()
    {
        using var s = new Sandbox();
        var stage = s.Sub("stage");
        void Put(string rel) { var p = Path.Combine(stage, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, "x"); }
        Put(@"Meshes\MyMod\Packed.nif");
        Put(@"Meshes\MyMod\Forgot.nif");                 // in no archive: PROBLEM
        Put(@"Interface\MyMod\Loose.swf");               // shipped loose in the zip: fine
        Put(@"Scripts\Source\MyMod\Thing.psc");          // sources never go in an archive
        Put("MyMod.esm"); Put("MyMod - Main.ba2"); Put("MyMod.achlist"); Put("MyMod_deps.csv");
        var release = Zip(s, "MyMod-1.0.0.zip", ("MyMod.esm", [1]), ("Interface/MyMod/Loose.swf", [1]),
            ("MyMod - Main.ba2", TriageScriptsTests.Ba2(("meshes/mymod/packed.nif", [1], false), ("meshes/mymod/old.nif", [1], false))));
        var f = Run(s, release, [], stage);
        Has(f, Level.Problem, "stage", @"staged, missing from the archives: meshes\mymod\forgot.nif");
        Has(f, Level.Review, "stage", @"in an archive, not staged (a stale archive?): meshes\mymod\old.nif");
        Assert.Equal(2, f.Count);   // nothing about the sources, plugin, archive, achlist, DMMDeps list or loose file

        File.Delete(Path.Combine(stage, @"Meshes\MyMod\Forgot.nif"));
        Put(@"Meshes\MyMod\Old.nif");
        Has(Run(s, release, [], stage), Level.Note, "stage", "stage matches the archives: 2 files");

        var noArchive = Zip(s, "Plain-1.0.0.zip", ("Plain.esm", [1]));
        Has(Run(s, noArchive, [], stage), Level.Note, "stage", "the stage check was skipped");
    }

    [Fact]
    public void A_tested_file_or_stage_inside_the_game_folder_or_missing_is_exit_2()
    {
        using var s = new Sandbox();
        var data = s.Sub(Path.Combine("Game", "Data"));
        File.WriteAllBytes(Path.Combine(data, "Starfield.esm"), [0]);
        var inside = Path.Combine(s.Sub(Path.Combine("Game", "Backup")), "MyMod-1.0.0.zip");
        File.WriteAllBytes(inside, [0]);
        var release = Zip(s, "MyMod-1.0.1.zip", ("MyMod.esm", [1]));

        var (code, stdout, stderr) = ExeTests.RunSplit("release", release, "--data", data, "--tested", inside);
        Assert.Equal(2, code);
        Assert.Contains("RESULT: CANNOT CHECK (--tested MyMod-1.0.0.zip is inside the game install)", stdout);
        Assert.Contains("from your build or download folder", stderr);

        (code, stdout, _) = ExeTests.RunSplit("release", release, "--data", data, "--stage", data);
        Assert.Equal(2, code);
        Assert.Contains("RESULT: CANNOT CHECK (the --stage folder is inside the game install)", stdout);

        Assert.Equal(2, ExeTests.RunSplit("release", release, "--data", data, "--tested", Path.Combine(s.Dir, "nope.zip")).Code);
        Assert.Equal(2, ExeTests.RunSplit("release", release, "--data", data, "--stage", Path.Combine(s.Dir, "nope")).Code);
        Assert.Equal(2, ExeTests.RunSplit("release", release, "--data", data, "--tested", "MyMod.esm").Code);   // not a zip or .ba2
        Assert.Equal(2, ExeTests.RunSplit("release", release, "--stage", "a", "--stage", "b").Code);
    }

    [Fact]
    public void Tested_and_stage_are_in_the_usage_readme_agents_and_changelog()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "ReleaseTested.cs"))) dir = dir.Parent;
        Assert.True(dir != null, "src\\ReleaseTested.cs not found above " + AppContext.BaseDirectory);
        var readme = File.ReadAllText(Path.Combine(dir!.FullName, "README.md"));
        var agents = File.ReadAllText(Path.Combine(dir.FullName, "AGENTS.md"));
        var changelog = File.ReadAllText(Path.Combine(dir.FullName, "CHANGELOG.md"));
        var help = ExeTests.RunSplit("release", "--help");
        Assert.Equal(0, help.Code);
        foreach (var o in new[] { "--tested <zip|ba2>", "--stage <folder>" })
        {
            Assert.Contains(o, ReleaseCommand.Usage);
            Assert.Contains(o, help.Stdout);
            Assert.Contains(o, readme);
            Assert.Contains("`" + o.Split(' ')[0], agents);
            Assert.Contains("`release " + o, changelog);
        }
        foreach (var rule in new[] { "| `tested` |", "| `stage` |" }) Assert.Contains(rule, readme);
    }
}
