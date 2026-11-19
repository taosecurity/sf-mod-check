// The release command's optional Papyrus lint (0.7.0, chunk C4): when it runs, the papyrus.exe hash pin, the vanilla
// sources cache (extraction and zip-slip refusal), papyrus.toml, and turning papyrus's JSON into report lines. None of
// these need papyrus.exe or the Creation Kit; the real run at the end is a manual check, switched on with
// SFMODCHECK_PAPYRUS_REAL (see there).
using System.IO.Compression;
using System.Text;
using SfModCheck;
using Xunit;

namespace SfModCheck.Tests;

public class ReleaseLintTests
{
    static string MakeZip(string dir, string name, params (string Path, string Text)[] entries)
    {
        Directory.CreateDirectory(dir);
        var zip = Path.Combine(dir, name);
        using var z = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var (p, t) in entries)
        {
            using var w = z.CreateEntry(p).Open();
            w.Write(Encoding.UTF8.GetBytes(t));
        }
        return zip;
    }

    static ReleaseContext Ctx(Sandbox s, ReleaseOptions o, params string[] zips) => new()
    {
        Options = o,
        Packages = zips.Select((z, i) => ReleaseCommand.Unpack(z, Path.Combine(s.Dir, "out", Guid.NewGuid().ToString("N")[..8], $"zip{i}"))).ToList(),
        Work = s.Sub("work-" + Guid.NewGuid().ToString("N")[..8]),
    };

    static string List(IEnumerable<ReleaseFinding> f) => string.Join("\n", f.Select(x => $"  {x.Level} [{x.Rule}] {x.Text}"));

    const string ModScript = "ScriptName ZZT:Foo extends Quest\n";

    [Fact]
    public void Nothing_runs_without_both_options()
    {
        using var s = new Sandbox();
        var zip = MakeZip(s.Sub("zips"), "Mod-1.0.0.zip", ("Scripts/Source/ZZT/Foo.psc", ModScript));
        var exe = Path.Combine(s.Dir, "papyrus.exe");
        File.WriteAllText(exe, "not papyrus");
        foreach (var o in new[] { new ReleaseOptions(), new ReleaseOptions { Papyrus = exe }, new ReleaseOptions { CkSources = zip } })
            Assert.Empty(ReleaseLint.Run(Ctx(s, o, zip)).ToList());
    }

    [Fact]
    public void A_wrong_papyrus_build_is_refused_and_the_lint_skipped()
    {
        using var s = new Sandbox();
        var zip = MakeZip(s.Sub("zips"), "Mod-1.0.0.zip", ("Scripts/Source/ZZT/Foo.psc", ModScript));
        var ck = MakeZip(s.Sub("ck"), "ContentResources.zip", ("Scripts/Source/Quest.psc", "ScriptName Quest\n"));
        var exe = Path.Combine(s.Dir, "papyrus.exe");
        File.WriteAllText(exe, "not papyrus");
        var f = ReleaseLint.Run(Ctx(s, new ReleaseOptions { Papyrus = exe, CkSources = ck }, zip)).ToList();
        var one = Assert.Single(f);
        Assert.Equal(Level.Warning, one.Level);
        Assert.Equal("papyrus-lint", one.Rule);
        Assert.Equal(RuleKind.Engine, one.Kind);
        Assert.Contains("isn't the tested papyrus-tools v0.16.3 build", one.Text);
        Assert.False(Directory.Exists(Path.Combine(LearnedCache.Root, "vanilla-psc")), "nothing extracted for a refused exe");
    }

    [Fact]
    public void A_zip_without_sources_gets_a_note_and_nothing_else_runs()
    {
        using var s = new Sandbox();
        var zip = MakeZip(s.Sub("zips"), "NoSrc-1.0.0.zip", ("Scripts/ZZT/Foo.pex", "pex"));
        var exe = Path.Combine(s.Dir, "papyrus.exe");
        File.WriteAllText(exe, "not papyrus");
        var f = ReleaseLint.Run(Ctx(s, new ReleaseOptions { Papyrus = exe, CkSources = zip }, zip)).ToList();
        var one = Assert.Single(f);
        Assert.Equal(Level.Note, one.Level);
        Assert.Equal("no script sources in NoSrc-1.0.0.zip; Papyrus lint skipped for it", one.Text);
    }

    [Fact]
    public void Vanilla_sources_are_extracted_once_per_zip_version()
    {
        using var s = new Sandbox();
        var ck = MakeZip(s.Sub("ck"), "ContentResources.zip",
            ("Scripts/Source/Quest.psc", "ScriptName Quest\n"),
            ("Scripts\\Source\\Fragments\\Quests\\QF_X.psc", "ScriptName Fragments:Quests:QF_X\n"),
            ("Scripts/Source/Starfield_Papyrus_Flags.flg", "flags"),
            ("Scripts/Source/readme.txt", "not a source"),
            ("Meshes/foo.nif", "mesh"));
        var dest = ReleaseLint.VanillaSources(ck);
        var fi = new FileInfo(ck);
        Assert.Equal(Path.Combine(LearnedCache.Root, "vanilla-psc", $"{fi.Length}-{new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds()}"), dest);
        Assert.True(File.Exists(Path.Combine(dest, "Quest.psc")));
        Assert.True(File.Exists(Path.Combine(dest, "Fragments", "Quests", "QF_X.psc")));
        Assert.True(File.Exists(Path.Combine(dest, "Starfield_Papyrus_Flags.flg")));
        Assert.False(File.Exists(Path.Combine(dest, "readme.txt")));
        Assert.False(Directory.Exists(dest + ".partial"));

        // the cache is used as is the second time (a marker file survives)
        File.WriteAllText(Path.Combine(dest, "marker"), "x");
        Assert.Equal(dest, ReleaseLint.VanillaSources(ck));
        Assert.True(File.Exists(Path.Combine(dest, "marker")));

        // another version of the zip (other modified time) gets its own folder
        File.SetLastWriteTimeUtc(ck, fi.LastWriteTimeUtc.AddHours(-5));
        var dest2 = ReleaseLint.VanillaSources(ck);
        Assert.NotEqual(dest, dest2);
        Assert.True(File.Exists(Path.Combine(dest2, "Quest.psc")));
    }

    [Fact]
    public void A_vanilla_entry_outside_the_folder_is_refused()
    {
        using var s = new Sandbox();
        var ck = MakeZip(s.Sub("ck"), "ContentResources.zip",
            ("Scripts/Source/Quest.psc", "ScriptName Quest\n"),
            ("Scripts/Source/../../../evil.psc", "ScriptName Evil\n"));
        var e = Assert.Throws<InvalidDataException>(() => ReleaseLint.VanillaSources(ck));
        Assert.Contains("points outside", e.Message);
        var cache = Path.Combine(LearnedCache.Root, "vanilla-psc");
        Assert.Empty(Directory.GetDirectories(cache));   // the .partial folder is gone, nothing looks finished
        Assert.False(File.Exists(Path.Combine(LearnedCache.Root, "evil.psc")));
        Assert.False(File.Exists(Path.Combine(s.Dir, "evil.psc")));
    }

    [Fact]
    public void A_zip_without_vanilla_sources_is_refused()
    {
        using var s = new Sandbox();
        var ck = MakeZip(s.Sub("ck"), "Other.zip", ("Meshes/foo.nif", "mesh"));
        var e = Assert.Throws<InvalidDataException>(() => ReleaseLint.VanillaSources(ck));
        Assert.Contains("ContentResources.zip", e.Message);
    }

    [Fact]
    public void Toml_matches_reference_output()
    {
        var t = ReleaseLint.Toml(@"C:\Cache\vanilla-psc\1-2", 2);
        Assert.Equal(
            "game = \"starfield\"\ninclude-fragments = true\nimports = [\"C:/Cache/vanilla-psc/1-2\"]\nsources = [\"Source0\", \"Source1\"]\n\n" +
            "[lints]\n\"naming/locals\" = \"allow\"\n\"naming/variables\" = \"allow\"\n\"naming/functions\" = \"allow\"\n\"names/case\" = \"allow\"\n", t);
    }

    [Fact]
    public void Json_findings_become_reference_output_lines()
    {
        var proj = Path.Combine(Path.GetTempPath(), "sfmc-lint-proj");
        string P(params string[] parts) => JsonEsc(Path.Combine([proj, .. parts]));
        var json = "[\n" +
            "  {\"code\": \"PAP015\", \"column\": 9, \"endColumn\": 10, \"endLine\": 7, \"line\": 7,\n" +
            "   \"message\": \"the variable `y` is never used. Fixable with `papyrus check --fix`.\", \"name\": \"locals/unused\",\n" +
            $"   \"path\": \"{P("Source0", "ZZT", "BadScript.psc")}\", \"severity\": \"warning\"}},\n" +
            "  {\"code\": \"PAP008\", \"line\": 10, \"message\": \"`undefinedThing` is not declared\", \"name\": \"members/unknown\",\n" +
            $"   \"path\": \"{P("Source1", "BadTop.psc")}\", \"severity\": \"error\"}},\n" +
            "  {\"code\": \"PAP001\", \"message\": \"elsewhere\", \"path\": \"D:/other/X.psc\"}\n" +
            "]";
        var lines = ReleaseLint.Lines(json, proj);
        Assert.NotNull(lines);
        var sep = Path.DirectorySeparatorChar;
        Assert.Equal(
        [
            $"papyrus warning PAP015 locals/unused: ZZT{sep}BadScript.psc:7: the variable `y` is never used. Fixable with `papyrus check --fix`.  [papyrus explain PAP015]",
            "papyrus error PAP008 members/unknown: BadTop.psc:10: `undefinedThing` is not declared  [papyrus explain PAP008]",
            "papyrus ? PAP001 : D:/other/X.psc:?: elsewhere  [papyrus explain PAP001]",
        ], lines);

        Assert.Empty(ReleaseLint.Lines("", proj)!);
        Assert.Empty(ReleaseLint.Lines("[]\n", proj)!);
        Assert.Null(ReleaseLint.Lines("error: no papyrus.toml", proj));
        Assert.Null(ReleaseLint.Lines("{\"code\": \"PAP001\"}", proj));
    }

    static string JsonEsc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // Manual check against the earlier Python check (not a unit test): set SFMODCHECK_PAPYRUS_REAL to an output file and
    // SFMODCHECK_PAPYRUS_REAL_SRC to a mod's Scripts\Source folder. The test zips that folder, lints it with the real
    // papyrus.exe (papyrus-tools) and ContentResources.zip (Steam CK), and writes the report lines to the file.
    // Without the variables (or the files) it does nothing.
    [Fact]
    public void Real_run_when_asked()
    {
        var outFile = Environment.GetEnvironmentVariable("SFMODCHECK_PAPYRUS_REAL");
        var src = Environment.GetEnvironmentVariable("SFMODCHECK_PAPYRUS_REAL_SRC");
        var exe = Environment.GetEnvironmentVariable("SFMODCHECK_PAPYRUS_EXE") ?? "";
        const string ck = @"C:\Program Files (x86)\Steam\steamapps\common\Starfield\Tools\ContentResources.zip";
        if (outFile == null || src == null || !File.Exists(exe) || !File.Exists(ck) || !Directory.Exists(src)) return;
        using var s = new Sandbox();
        var zip = Path.Combine(s.Sub("zips"), "Real-0.0.1.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
                z.CreateEntryFromFile(f, "Scripts/Source/" + Path.GetRelativePath(src, f).Replace('\\', '/'));
        var found = ReleaseLint.Run(Ctx(s, new ReleaseOptions { Papyrus = exe, CkSources = ck }, zip)).ToList();
        File.WriteAllLines(outFile, found.Select(f => $"{f.Level} {f.Text}"));
        Assert.Contains(found, f => f.Level == Level.Note && f.Text.StartsWith("papyrus lint: "));
    }
}
