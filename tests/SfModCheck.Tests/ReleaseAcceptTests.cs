// --accept and --accept-file (0.15.2): one known finding is accepted exactly, still prints, and no longer fails the run.
using Mutagen.Bethesda.Starfield;
using SfModCheck;
using Xunit;

namespace SfModCheck.Tests;

public class ReleaseAcceptTests
{
    static ReleaseFinding Missing(string plugin, string script) =>
        new(Level.Problem, "script-missing", $"{plugin} attaches script {script}, but ... is in none of ...") { Plugin = plugin, Subject = script };

    [Fact]
    public void A_sound_event_past_the_line_cap_can_still_be_accepted()
    {
        // code-review round 1 (0.23.0): release dropped the 26th and later events before the accepts ran
        static string Guid(int i) => $"{{{i:X8}-0000-0000-0000-000000000000}}";
        var findings = Enumerable.Range(1, 30).Select(i => new ReleaseFinding(Level.Warning, "sound-event-missing", $"Made.esm: event {i}")
            { Plugin = "Made.esm", Subject = Guid(i) }).ToList();
        var accept = ReleaseAccept.Parse($"sound-event-missing:Made.esm:{Guid(27)}", "--accept", out var error);
        Assert.True(accept != null, error);
        var shown = ReleaseCommand.CapSoundEvents(ReleaseAccept.Apply(findings, [accept!]));
        Assert.DoesNotContain(shown, f => f.Rule == "accept");                          // the entry matched
        Assert.Single(shown, f => f.Accepted != null && f.Subject == Guid(27));
        Assert.Equal(25, shown.Count(f => f.Accepted == null && f.Subject != null));   // 25 lines, then the count of the other 4
        Assert.Single(shown, f => f.Subject == null && f.Text.StartsWith("Made.esm: 4 more sound event(s)"));
    }

    [Theory]
    [InlineData("script-missing:Other.esm", "three parts")]
    [InlineData("script-missing::Toggle", "empty part")]
    [InlineData("script-missing:Other.esm:", "empty part")]
    [InlineData("script-missing:Other.esm:Tog*", "wildcard")]
    [InlineData("script-missing:*.esm:Toggle", "wildcard")]
    [InlineData("plugin-check:Other.esm:Toggle", "can't be accepted")]
    [InlineData("script-missing:Other:Toggle", "file name only")]
    [InlineData(@"script-missing:Data\Other.esm:Toggle", "file name only")]
    public void A_malformed_entry_is_refused_with_the_entry_and_the_fix(string entry, string why)
    {
        Assert.Null(ReleaseAccept.Parse(entry, "--accept", out var error));
        Assert.Contains(why, error);
        Assert.Contains(entry.Trim(), error);
        Assert.Contains("<rule>:<plugin>:<detail>", error);
    }

    [Fact]
    public void A_script_name_with_a_namespace_keeps_its_colon()
    {
        var e = ReleaseAccept.Parse(" Script-Missing:Other.esm:OtherNs:Toggle ", "--accept", out var error);
        Assert.Null(error);
        Assert.Equal(("script-missing", "Other.esm", "OtherNs:Toggle"), (e!.Rule, e.Plugin, e.Detail));
    }

    [Fact]
    public void Only_the_exact_rule_plugin_and_detail_are_accepted_and_an_unused_entry_warns()
    {
        var entries = new[]
        {
            ReleaseAccept.Parse("script-missing:other.esm:otherns:toggle", "--accept", out _)!,   // case doesn't matter
            ReleaseAccept.Parse("script-missing:Other.esm:Gone", "--accept", out _)!,             // matches nothing
        };
        var findings = new List<ReleaseFinding>
        {
            Missing("Other.esm", "OtherNs:Toggle"),
            Missing("Other.esm", "OtherNs:ToggleTwo"),   // another script: not accepted
            Missing("Mine.esm", "OtherNs:Toggle"),       // another plugin: not accepted
            new(Level.Problem, "script-missing", "Other.esm attaches script OtherNs:Toggle (no plugin or detail recorded)"),
        };
        var r = ReleaseAccept.Apply(findings, entries);
        Assert.Single(r, f => f.Accepted != null);
        Assert.Equal("OtherNs:Toggle", r.Single(f => f.Accepted != null).Subject);
        Assert.Equal("Other.esm", r.Single(f => f.Accepted != null).Plugin);
        var w = Assert.Single(r, f => f.Rule == "accept");
        Assert.Equal(Level.Warning, w.Level);
        Assert.StartsWith("accept entry matched nothing: script-missing:Other.esm:Gone", w.Text);
    }

    [Fact]
    public void A_path_detail_matches_with_either_slash()
    {
        var e = ReleaseAccept.Parse("model-missing:Other.esm:meshes/other/thing.nif", "--accept", out _)!;
        var f = new ReleaseFinding(Level.Warning, "model-missing", "x") { Plugin = "Other.esm", Subject = @"meshes\other\thing.nif" };
        Assert.NotNull(ReleaseAccept.Apply([f], [e])[0].Accepted);
    }

    [Fact]
    public void An_accept_file_skips_comments_and_names_the_line_of_a_bad_entry()
    {
        using var s = new Sandbox();
        var good = Path.Combine(s.Dir, "accept list.txt");
        File.WriteAllLines(good, ["# known gaps in Other.esm", "", "script-missing:Other.esm:OtherNs:Toggle   # the author's script", "  script-missing:Other.esm:Door  "]);
        var list = ReleaseAccept.ReadFile(good, out var error);
        Assert.Null(error);
        Assert.Equal(["OtherNs:Toggle", "Door"], list!.Select(x => x.Detail));
        Assert.Equal("accept list.txt line 3", list[0].Source);

        var bad = Path.Combine(s.Dir, "bad.txt");
        File.WriteAllLines(bad, ["script-missing:Other.esm:Door", "script-missing:Other.esm"]);
        Assert.Null(ReleaseAccept.ReadFile(bad, out error));
        Assert.Contains("bad.txt line 2", error);

        Assert.Null(ReleaseAccept.ReadFile(Path.Combine(s.Dir, "none.txt"), out error));
        Assert.Contains("no such file", error);
    }

    [Fact]
    public void Docs_name_the_0152_accept_options()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "ReleaseAccept.cs"))) dir = dir.Parent;
        Assert.True(dir != null, "src/ReleaseAccept.cs not found above " + AppContext.BaseDirectory);
        string Doc(string n) => File.ReadAllText(Path.Combine(dir!.FullName, n)).Replace("\r\n", "\n");
        foreach (var (name, text) in new[] { ("usage", ReleaseCommand.Usage), ("README.md", Doc("README.md")), ("AGENTS.md", Doc("AGENTS.md")) })
        {
            Assert.True(text.Contains("--accept"), $"{name} doesn't name --accept");
            Assert.True(text.Contains("--accept-file"), $"{name} doesn't name --accept-file");
        }
        foreach (var rule in ReleaseAccept.Rules)
        {
            Assert.Contains($"`{rule}`", Doc("README.md"));
            Assert.Contains($"`{rule}`", Doc("AGENTS.md"));
        }
        Assert.Contains("| `accept` | never (WARNING) |", Doc("README.md"));
        var changes = Doc("CHANGELOG.md").Split("\n## 0.15.1")[0];
        Assert.Contains("## 0.15.2", changes);
        Assert.Contains("ACCEPTED [script-missing]", changes);
        Assert.Contains("accept entry matched nothing", changes);
    }

    [Fact]
    public void Exe_passes_with_the_accept_entries_and_fails_without_them()
    {
        using var s = new Sandbox();
        var data = Path.Combine(s.Sub("Game"), "Data");   // the mod folder must be outside the game install
        ReleaseNeedsTests.Write(data, "Starfield.esm", (_, _) => { });
        var mod = Path.Combine(s.Sub("build"), "AcMod");
        ReleaseNeedsTests.Write(mod, "AcMod.esm", (_, misc) => misc.VirtualMachineAdapter = new VirtualMachineAdapter
        {
            Scripts = { new ScriptEntry { Name = "AcMod:Toggle" }, new ScriptEntry { Name = "DoorTest" } },
        });

        var (code, stdout, stderr) = ExeTests.RunSplit("release", mod, "--data", data, "--quiet");
        Assert.True(code == 1, stdout + stderr);
        Assert.Equal(2, stdout.Split('\n').Count(l => l.StartsWith("PROBLEM  [script-missing]")));

        // one entry on the command line and one in a file (a path with spaces): OK, both still printed as ACCEPTED
        var file = Path.Combine(s.Sub("my lists"), "accept.txt");
        File.WriteAllLines(file, ["# known: DoorTest ships nowhere", "script-missing:AcMod.esm:DoorTest"]);
        (code, stdout, stderr) = ExeTests.RunSplit("release", mod, "--data", data, "--quiet",
            "--accept", "script-missing:AcMod.esm:AcMod:Toggle", "--accept-file", file);
        Assert.True(code == 0, stdout + stderr);
        Assert.DoesNotContain("PROBLEM  [script-missing]", stdout);
        Assert.Contains(stdout.Split('\n'), l => l.StartsWith("ACCEPTED [script-missing] AcMod.esm attaches script AcMod:Toggle") &&
                                                l.Contains("accepted by --accept script-missing:AcMod.esm:AcMod:Toggle"));
        Assert.Contains(stdout.Split('\n'), l => l.StartsWith("ACCEPTED [script-missing] AcMod.esm attaches script DoorTest") &&
                                                l.Contains("accepted by accept.txt line 2"));
        Assert.Matches(@"^RESULT: OK \(0 problems, \d+ to review, \d+ warnings, 2 accepted\)$", ExeTests.ResultLine(stdout));

        // an entry that matches nothing is a WARNING, and the other finding still fails the run
        (code, stdout, stderr) = ExeTests.RunSplit("release", mod, "--data", data, "--quiet", "--accept", "script-missing:AcMod.esm:Elsewhere");
        Assert.True(code == 1, stdout + stderr);
        Assert.Contains("WARNING  [accept] accept entry matched nothing: script-missing:AcMod.esm:Elsewhere", stdout);

        // a malformed entry stops the run before anything is checked
        (code, stdout, stderr) = ExeTests.RunSplit("release", mod, "--data", data, "--accept", "script-missing:*:DoorTest");
        Assert.Equal(2, code);
        Assert.Contains("wildcard", stderr);
        Assert.Equal("RESULT: CANNOT CHECK (bad arguments; see the message above)", ExeTests.ResultLine(stdout));
    }

    [Theory]   // bug hunt 2026-11-23: the example after a missing value was "<value>" and the entry example came twice
    [InlineData("--accept", "--accept needs an entry <rule>:<plugin>:<detail> after it, for example: --accept script-missing:OtherMod.esm:OtherMod:Toggle")]
    [InlineData("--accept-file", "--accept-file needs a text file of --accept entries (one per line) after it, for example: --accept-file known-gaps.txt")]
    public void Accept_option_with_no_value_exits_2_with_a_real_example(string option, string expected)
    {
        using var s = new Sandbox();
        var (code, stdout, stderr) = ExeTests.RunSplit("release", s.Sub("mod"), option);
        Assert.Equal(2, code);
        Assert.Contains(expected, stderr);
        Assert.DoesNotContain("<value>", stderr);
    }
}
