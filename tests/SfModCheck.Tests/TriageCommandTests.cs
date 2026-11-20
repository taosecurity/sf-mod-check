// The triage command's frame (0.8.0, chunk C0): inputs (zip, 7z, folder, plugin), the report writer and exit codes.
// The sections have their own test files.
using System.IO.Compression;
using System.Text.Json;
using SharpCompress.Writers.SevenZip;
using SfModCheck;
using Xunit;

namespace SfModCheck.Tests;

public class TriageCommandTests
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

    static string Make7z(Sandbox s, string name, params (string Path, string Text)[] entries)
    {
        var path = Path.Combine(s.Sub("zips"), name);
        using (var f = File.Create(path))
        using (var w = new SevenZipWriter(f, new SevenZipWriterOptions()))
            foreach (var (p, t) in entries)
                w.Write(p, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(t)), null);
        return path;
    }

    [Fact]
    public void Gather_reads_a_zip_a_folder_and_a_plugin()
    {
        using var s = new Sandbox();
        var zip = MakeZip(s, "Mod-1.0.zip", ("Data/Mod.esm", "x"), ("Data/Mod - Main.ba2", "x"));
        var (root, files) = TriageCommand.Gather(TriageInputKind.Zip, zip, Path.Combine(s.Dir, "out"));
        Assert.Equal(["data\\mod - main.ba2", "data\\mod.esm"], files);
        Assert.True(File.Exists(Path.Combine(root, "Data", "Mod.esm")));

        var folder = s.Sub("modfolder");
        Directory.CreateDirectory(Path.Combine(folder, "Scripts"));
        File.WriteAllText(Path.Combine(folder, "Scripts", "Foo.pex"), "x");
        File.WriteAllText(Path.Combine(folder, "Foo.esm"), "x");
        File.WriteAllText(Path.Combine(folder, "Foo - Main.ba2"), "x");
        File.WriteAllText(Path.Combine(folder, "Other - Main.ba2"), "x");
        Assert.Equal(["foo - main.ba2", "foo.esm", "other - main.ba2", "scripts\\foo.pex"],
            TriageCommand.Gather(TriageInputKind.Folder, folder, "unused").Files);

        // a plugin brings only the archives that share its name
        var (proot, pfiles) = TriageCommand.Gather(TriageInputKind.Plugin, Path.Combine(folder, "Foo.esm"), "unused");
        Assert.Equal(folder, proot);
        Assert.Equal(["foo - main.ba2", "foo.esm"], pfiles);
    }

    [Fact]
    public void Seven_zip_is_unpacked_with_the_zip_guards()
    {
        using var s = new Sandbox();
        var ok = Make7z(s, "Mod.7z", ("Mod.esm", "x"), ("Scripts/Bar.pex", "yy"));
        Assert.Equal(["mod.esm", "scripts\\bar.pex"], TriageCommand.UnpackOther(ok, Path.Combine(s.Dir, "a")));
        Assert.Equal("yy", File.ReadAllText(Path.Combine(s.Dir, "a", "Scripts", "Bar.pex")));

        var evil = Make7z(s, "Evil.7z", ("../evil.txt", "x"));
        Assert.Throws<InvalidDataException>(() => TriageCommand.UnpackOther(evil, Path.Combine(s.Dir, "b")));
        Assert.False(File.Exists(Path.Combine(s.Dir, "evil.txt")));

        var dup = Make7z(s, "Dup.7z", ("A.txt", "x"), ("a.txt", "y"));
        Assert.Throws<InvalidDataException>(() => TriageCommand.UnpackOther(dup, Path.Combine(s.Dir, "c")));
    }

    [Fact]
    public void A_non_solid_archive_is_read_entry_by_entry_with_the_same_guards()
    {
        // 0.8.0 code review: SharpCompress reads only solid archives and .7z in one pass; a normal .rar is not solid.
        // No free tool writes .rar, so a zip (never solid) takes the same entry-by-entry path here.
        using var s = new Sandbox();
        var ok = MakeZip(s, "Mod.zip", ("Mod.esm", "x"), ("Scripts/Bar.pex", "yy"));
        Assert.Equal(["mod.esm", "scripts\\bar.pex"], TriageCommand.UnpackOther(ok, Path.Combine(s.Dir, "a")));
        Assert.Equal("yy", File.ReadAllText(Path.Combine(s.Dir, "a", "Scripts", "Bar.pex")));
        var evil = MakeZip(s, "Evil.zip", ("../evil.txt", "x"));
        Assert.Throws<InvalidDataException>(() => TriageCommand.UnpackOther(evil, Path.Combine(s.Dir, "b")));
        Assert.False(File.Exists(Path.Combine(s.Dir, "evil.txt")));
    }

    [Theory]
    [InlineData("SFBGS003.esm", true)]
    [InlineData("SFBGS00A.esm", true)]
    [InlineData("BlueprintShips-SFBGS050.esm", true)]
    [InlineData("sfta01.esm", true)]
    [InlineData("ShatteredSpace.esm", true)]
    [InlineData("SFBGS003Fixes.esm", false)]   // a mod: the old prefix rule took it for Bethesda's (0.8.0 code review)
    [InlineData("BlueprintShips-MyShips.esm", false)]
    [InlineData("sfta01Patch.esm", false)]
    public void Official_names_are_matched_exactly(string name, bool official) => Assert.Equal(official, Checker.IsOfficial(name));

    [Fact]
    public void The_report_never_lands_in_any_game_install_by_default()
    {
        // 0.8.0 code review: only the --game (or Steam) install was kept out; a Game Pass Data folder got the report
        using var s = new Sandbox();
        var gp = Path.Combine(s.Sub("XboxGames"), "Starfield", "Content", "Data");
        Directory.CreateDirectory(gp);
        File.WriteAllText(Path.Combine(gp, "Starfield.esm"), "x");
        var plugin = Path.Combine(gp, "Foo.esm");
        File.WriteAllText(plugin, "x");
        Assert.Equal(Environment.CurrentDirectory, TriageCommand.DefaultOut(plugin, Path.Combine(s.Dir, "no-steam")));
        var other = s.Sub("Downloads");
        Assert.Equal(other, TriageCommand.DefaultOut(Path.Combine(other, "Mod.zip"), Path.Combine(s.Dir, "no-steam")));
    }

    [Fact]
    public void Asi_plugins_count_as_binaries()
    {
        var c = new TriageContext
        {
            Options = new TriageOptions(), Kind = TriageInputKind.Folder, InputPath = "x", Root = "r",
            Files = ["hook.asi", "sfse\\plugins\\a.dll", "tool.exe", "readme.txt"], DataPrefix = "", DataDir = "d", Work = "w", Report = new TriageReport(),
        };
        Assert.Equal(3, c.Binaries.Count);
        Assert.Contains(c.Binaries, b => b.EndsWith("hook.asi"));
    }

    [Theory]
    [InlineData("Some Mod-1.2.zip", "Some Mod-1.2-triage")]
    [InlineData("Foo.7z", "Foo-triage")]
    [InlineData("Foo.esm", "Foo.esm-triage")]
    [InlineData("ModFolder", "ModFolder-triage")]
    public void Report_names(string input, string stem) => Assert.Equal(stem, TriageCommand.ReportStem(input));

    [Fact]
    public void Report_escapes_mod_text_and_says_it_is_not_a_verdict()
    {
        var r = new TriageReport { InputName = "<b>Mod</b>.zip", Tool = "sf-mod-check test" };
        r.Findings.Add(new TriageFinding(TriageLevel.Look, TriageSections.Scripts, "scripts\\<x>.pex", "calls <script>alert(1)</script>"));
        r.Findings.Add(new TriageFinding(TriageLevel.Note, TriageSections.Files, "", "line one\nline\u001b[2J two"));
        var t = r.Table(TriageSections.Files, "Every file", "File", "Size");
        t.Add("a\u0007.esm", "12");
        Assert.Throws<ArgumentException>(() => t.Add("only one cell"));

        var html = r.Html();
        Assert.Contains("Triage, not a verdict.", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<b>Mod</b>", html);
        Assert.Contains("&lt;script&gt;", html);

        var text = r.Text();
        Assert.Contains(TriageReport.Banner, text);
        // character checks: xunit's culture-aware string search skips control characters
        Assert.False(text.Contains('\u001b'), "escape character left in the text");
        Assert.False(text.Contains('\u0007'), "bell character left in the text");
        Assert.Contains("LOOK  scripts\\<x>.pex: calls", text);
        // LOOK lines come before NOTE lines, and a section with nothing says so
        Assert.Contains("== Binaries ==", text);
        Assert.Contains("(nothing found)", text);

        using var j = JsonDocument.Parse(r.Json());
        Assert.True(j.RootElement.GetProperty("triage_not_a_verdict").GetBoolean());
        Assert.Equal("LOOK", j.RootElement.GetProperty("findings")[0].GetProperty("level").GetString());
        Assert.Equal("Every file", j.RootElement.GetProperty("tables")[0].GetProperty("title").GetString());
    }

    [Fact]
    public void Exe_writes_three_reports_and_exits_0()
    {
        using var s = new Sandbox();
        var zip = MakeZip(s, "Mod-0.1.0.zip", ("Mod.esm", "x"), ("README.md", "hi"));
        var out_ = Path.Combine(s.Dir, "report");
        var (code, stdout, stderr) = ExeTests.RunSplit("triage", zip, "--game", MakeGame(s), "--out", out_, "--quiet");
        Assert.True(code == 0, stdout + stderr);
        Assert.Matches(@"^RESULT: REPORT WRITTEN \(\d+ to look at, \d+ notes\) .+Mod-0\.1\.0-triage\.html$", ExeTests.ResultLine(stdout));
        foreach (var ext in new[] { ".html", ".txt", ".json" })
            Assert.True(File.Exists(Path.Combine(out_, "Mod-0.1.0-triage" + ext)), ext);
        // nothing else is written next to the input
        Assert.Equal(["Mod-0.1.0.zip"], Directory.GetFiles(Path.GetDirectoryName(zip)!).Select(Path.GetFileName));
        var json = File.ReadAllText(Path.Combine(out_, "Mod-0.1.0-triage.json"));
        Assert.Contains("\"sha256\"", json);
    }

    [Fact]
    public void Exe_default_out_is_next_to_the_input()
    {
        using var s = new Sandbox();
        var zip = MakeZip(s, "Mod.zip", ("Mod.esm", "x"));
        var (code, stdout, stderr) = ExeTests.RunSplit("triage", zip, "--game", MakeGame(s), "--quiet");
        Assert.True(code == 0, stdout + stderr);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(zip)!, "Mod-triage.html")));
    }

    [Fact]
    public void Exe_gives_2_on_bad_input()
    {
        using var s = new Sandbox();
        var game = MakeGame(s);
        var zip = MakeZip(s, "Mod.zip", ("Mod.esm", "x"));
        var junk = Path.Combine(s.Dir, "notes.txt");
        File.WriteAllText(junk, "x");
        var bad = Path.Combine(s.Dir, "Broken.zip");
        File.WriteAllText(bad, "not a zip");

        Assert.Equal(2, ExeTests.RunSplit("triage").Code);
        Assert.Equal(2, ExeTests.RunSplit("triage", Path.Combine(s.Dir, "missing.zip"), "--game", game).Code);
        Assert.Equal(2, ExeTests.RunSplit("triage", junk, "--game", game).Code);
        Assert.Equal(2, ExeTests.RunSplit("triage", bad, "--game", game).Code);
        Assert.Equal(2, ExeTests.RunSplit("triage", zip, zip, "--game", game).Code);
        Assert.Equal(2, ExeTests.RunSplit("triage", zip, "--game", s.Sub("nogame")).Code);
        Assert.Equal(2, ExeTests.RunSplit("triage", zip, "--game", game, "--manalyze", Path.Combine(s.Dir, "nope.exe")).Code);
        Assert.Equal(2, ExeTests.RunSplit("triage", zip, "--bogus").Code);
        var (code, stdout, _) = ExeTests.RunSplit("triage", Path.Combine(game, "Data"), "--game", game);
        Assert.Equal(2, code);
        Assert.Contains("the game's Data folder", ExeTests.ResultLine(stdout));
    }

    [Fact]
    public void Game_folder_can_be_the_install_its_Data_or_Game_Pass_Content()
    {
        using var s = new Sandbox();
        var steam = MakeGame(s);
        Assert.Equal(Path.Combine(steam, "Data"), TriageCommand.FindData(steam));
        Assert.Equal(Path.Combine(steam, "Data"), TriageCommand.FindData(Path.Combine(steam, "Data")));
        var gp = s.Sub("XboxGames");
        Directory.CreateDirectory(Path.Combine(gp, "Content", "Data"));
        File.WriteAllText(Path.Combine(gp, "Content", "Data", "Starfield.esm"), "x");
        Assert.Equal(Path.Combine(gp, "Content", "Data"), TriageCommand.FindData(gp));
        Assert.Null(TriageCommand.FindData(s.Sub("empty")));
    }

    [Fact]
    public void A_section_that_stops_leaves_the_rest_and_is_named_at_the_top()
    {
        using var s = new Sandbox();
        var zip = MakeZip(s, "Mod.zip", ("Mod.esm", "x"));
        var out_ = Path.Combine(s.Dir, "r");
        var saved = TriageCommand.Sections.ToList();
        try
        {
            TriageCommand.Sections.Clear();
            TriageCommand.Sections.Add(("Broken", _ => throw new InvalidOperationException("boom")));
            TriageCommand.Sections.Add(("Fine", c => [new TriageFinding(TriageLevel.Note, TriageSections.Files, "", $"{c.Files.Count} files")]));
            var code = TriageCommand.Run([zip, "--game", MakeGame(s), "--out", out_, "--quiet"], "test", "test");
            Assert.Equal(0, code);
            var text = File.ReadAllText(Path.Combine(out_, "Mod-triage.txt"));
            Assert.Contains("INCOMPLETE        Broken: stopped early (InvalidOperationException: boom)", text);
            Assert.Contains("NOTE  1 files", text);
        }
        finally
        {
            TriageCommand.Sections.Clear();
            TriageCommand.Sections.AddRange(saved);
        }
    }

    /// <summary>A made-up game install: Game\Data\Starfield.esm (a stand-in file, never read as a plugin by the frame).</summary>
    static string MakeGame(Sandbox s)
    {
        var game = s.Sub("Game");
        Directory.CreateDirectory(Path.Combine(game, "Data"));
        File.WriteAllText(Path.Combine(game, "Data", "Starfield.esm"), "x");
        return game;
    }
}
