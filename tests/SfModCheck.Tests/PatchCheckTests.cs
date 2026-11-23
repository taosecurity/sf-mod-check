// patch check (0.15.0): is a conflict patch still up to date? Each finding kind and each exit code, from made-up
// plugins: a patch is built with `patch`, then the load order or a mod is changed and `patch check` reads the result.
using System.Text.Json;
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using Xunit;

namespace SfModCheck.Tests;

public class PatchCheckTests
{
    static readonly WriteOptions NoGap = new() { GapCheck = false, FixEffects = false };

    sealed class Game : Sandbox
    {
        public readonly string Data;
        public Game()
        {
            Data = Sub("Data");
            var b = Plugin.New("Base.esm", PluginStyle.Full, "test", "made-up base game");
            var misc = b.MiscItems.AddNew("B_Misc");
            misc.Value = 5;
            misc.Weight = 1;
            using var g = new GameData(Data);
            PluginWriter.Write(b, Data, g, NoGap);
        }

        /// <summary>A mod that overrides B_Misc; with own, it also adds a record of its own.</summary>
        public void Mod(string name, Action<MiscItem> change, string? own = null)
        {
            var mod = Plugin.New(name, PluginStyle.Light, "test", "made-up mod");
            using var g = new GameData(Data);
            change(mod.MiscItems.GetOrAddAsOverride(g.Open("Base.esm").MiscItems.First(m => m.EditorID == "B_Misc")));
            if (own != null) mod.MiscItems.AddNew(own);
            PluginWriter.Write(mod, Data, g, NoGap);
        }

        /// <summary>A mod with only a record of its own (it edits nothing the patch holds).</summary>
        public void Own(string name)
        {
            var mod = Plugin.New(name, PluginStyle.Light, "test", "made-up mod");
            mod.MiscItems.AddNew(Path.GetFileNameWithoutExtension(name) + "_Own");
            using var g = new GameData(Data);
            PluginWriter.Write(mod, Data, g, NoGap);
        }

        public string Out => Path.Combine(Dir, "patch");
        public string PatchFile => Path.Combine(Out, "ConflictPatch.esm");
        public string Manifest => Path.Combine(Out, "patch-sources.json");

        public void Patch(params string[] active)
        {
            var txt = Path.Combine(Dir, "Plugins.txt");
            File.WriteAllLines(txt, active.Select(a => "*" + a));
            var (code, stdout, stderr) = ExeTests.RunSplit("patch", "--data", Data, "--plugins", txt, "--out", Out, "--quiet");
            Assert.True(code == 0, stdout + stderr);
            Assert.True(File.Exists(Manifest));
        }

        /// <summary>patch check with this load order (the patch's folder as --extra-data, as before installing it).</summary>
        public (int Code, string Stdout, string Stderr) Check(string[] active, params string[] extra)
        {
            var txt = Path.Combine(Dir, "Plugins-now.txt");
            File.WriteAllLines(txt, active.Select(a => "*" + a));
            return ExeTests.RunSplit(new[] { "patch", "check", PatchFile, "--data", Data, "--extra-data", Out, "--plugins", txt, "--quiet" }.Concat(extra).ToArray());
        }
    }

    static readonly string[] Built = ["Base.esm", "ModA.esm", "ModB.esm"];
    static readonly string[] WithPatch = ["Base.esm", "ModA.esm", "ModB.esm", "ConflictPatch.esm"];

    static Game Ready()
    {
        var game = new Game();
        game.Mod("ModA.esm", m => m.Value = 10);
        game.Mod("ModB.esm", m => m.Weight = 2);
        game.Patch(Built);
        return game;
    }

    static string[] Lines(string stdout, string rule) =>
        stdout.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Contains($"[{rule}]")).ToArray();

    [Fact]
    public void A_patch_built_on_this_load_order_is_up_to_date()
    {
        using var game = Ready();
        var (code, stdout, stderr) = game.Check(WithPatch);
        Assert.True(code == 0, stdout + stderr);
        Assert.Equal("RESULT: UP TO DATE", ExeTests.ResultLine(stdout));
    }

    [Fact]
    public void A_master_that_changed_a_patched_record_means_rebuild()
    {
        using var game = Ready();
        game.Mod("ModA.esm", m => m.Value = 11);
        var (code, stdout, _) = game.Check(WithPatch);
        Assert.Equal(1, code);
        var line = Assert.Single(Lines(stdout, "updated"));
        Assert.StartsWith("REVIEW [updated] master ModA.esm changed since the build", line);
        Assert.Contains("000800:Base.esm", line);
        Assert.Equal("RESULT: REBUILD (1 reason)", ExeTests.ResultLine(stdout));
    }

    [Fact]
    public void A_master_that_changed_only_other_records_is_a_note()
    {
        using var game = Ready();
        game.Mod("ModA.esm", m => m.Value = 10, own: "A_Extra");   // same B_Misc, one record more
        var (code, stdout, stderr) = game.Check(WithPatch);
        Assert.True(code == 0, stdout + stderr);
        Assert.StartsWith("NOTE [updated] master ModA.esm changed since the build", Assert.Single(Lines(stdout, "updated")));
        Assert.Equal("RESULT: UP TO DATE", ExeTests.ResultLine(stdout));
    }

    [Fact]
    public void A_master_that_is_gone_or_off_is_a_problem()
    {
        using var game = Ready();
        var (code, stdout, _) = game.Check(["Base.esm", "ModA.esm", "ConflictPatch.esm"]);   // ModB turned off
        Assert.Equal(1, code);
        Assert.Contains("PROBLEM [removed] master ModB.esm isn't active in Plugins.txt", stdout);
        File.Delete(Path.Combine(game.Data, "ModB.esm"));
        (code, stdout, _) = game.Check(WithPatch);
        Assert.Equal(1, code);
        Assert.Contains("PROBLEM [removed] master ModB.esm isn't in the Data folder", stdout);
    }

    [Fact]
    public void A_master_listed_after_the_patch_has_moved()
    {
        using var game = Ready();
        var (code, stdout, _) = game.Check(["Base.esm", "ModA.esm", "ConflictPatch.esm", "ModB.esm"]);
        Assert.Equal(1, code);
        Assert.StartsWith("REVIEW [moved] master ModB.esm now loads after ConflictPatch.esm", Assert.Single(Lines(stdout, "moved")));
    }

    /// <summary>An official file loads before every mod wherever Plugins.txt lists it, so it never moves after the
    /// patch (found on a real load order, 2026-11-23: sfta02.esm and sfta05.esm were called moved).</summary>
    [Fact]
    public void An_official_master_listed_after_the_patch_hasnt_moved()
    {
        using var game = new Game();
        game.Mod("ModA.esm", m => m.Value = 10);
        game.Mod("sfta09.esm", m => m.Weight = 2);   // an official file's name: it loads before the mods
        game.Patch("Base.esm", "sfta09.esm", "ModA.esm");
        var man = File.ReadAllText(game.Manifest);
        Assert.Contains("\"sfta09.esm\"", man);   // a master of the patch
        var (code, stdout, stderr) = game.Check(["Base.esm", "ModA.esm", "ConflictPatch.esm", "sfta09.esm"]);
        Assert.True(code == 0, stdout + stderr);
        Assert.Empty(Lines(stdout, "moved"));
        Assert.Equal("RESULT: UP TO DATE", ExeTests.ResultLine(stdout));
    }

    /// <summary>A BlueprintShips file loads after every plugin, so as a master of the patch it is never "moved"
    /// (0.15.0 code review round 1: every patch with a BlueprintShips master read REBUILD on every run).</summary>
    [Fact]
    public void A_blueprint_ships_master_hasnt_moved()
    {
        using var game = new Game();
        const string bp = "BlueprintShips-sfta09.esm";   // an official BlueprintShips name: it loads last
        var ships = Plugin.New(bp, PluginStyle.Full, "test", "made-up ship blueprints");
        var kw = ships.Keywords.AddNew("BP_Keyword");
        using (var g = new GameData(game.Data)) PluginWriter.Write(ships, game.Data, g, NoGap);
        // ModA gives B_Misc a keyword the blueprint file defines; the patch copies it, so that file is its master
        var modA = Plugin.New("ModA.esm", PluginStyle.Light, "test", "made-up mod");
        using (var g = new GameData(game.Data))
        {
            g.Open(bp);
            modA.MiscItems.GetOrAddAsOverride(g.Open("Base.esm").MiscItems.First(m => m.EditorID == "B_Misc")).Keywords = [kw.ToLink()];
            PluginWriter.Write(modA, game.Data, g, NoGap);
        }
        game.Mod("ModB.esm", m => m.Weight = 2);
        game.Patch("Base.esm", bp, "ModA.esm", "ModB.esm");
        Assert.Contains($"\"{bp}\"", File.ReadAllText(game.Manifest));   // a master of the patch
        var (code, stdout, stderr) = game.Check(["Base.esm", bp, "ModA.esm", "ModB.esm", "ConflictPatch.esm"]);
        Assert.True(code == 0, stdout + stderr);
        Assert.Empty(Lines(stdout, "moved"));
        Assert.Equal("RESULT: UP TO DATE", ExeTests.ResultLine(stdout));
    }

    [Fact]
    public void A_new_plugin_that_edits_a_patched_record_is_a_new_editor()
    {
        using var game = Ready();
        game.Mod("ModC.esm", m => m.Value = 30);
        game.Own("ModD.esm");   // edits nothing the patch holds: no finding
        var (code, stdout, _) = game.Check(["Base.esm", "ModA.esm", "ModB.esm", "ModC.esm", "ModD.esm", "ConflictPatch.esm"]);
        Assert.Equal(1, code);
        var line = Assert.Single(Lines(stdout, "new-editor"));
        Assert.StartsWith("REVIEW [new-editor] new plugin ModC.esm changes 1 record ConflictPatch.esm holds (000800:Base.esm)", line);
        Assert.DoesNotContain("ModD.esm", stdout);
    }

    [Fact]
    public void A_plugin_from_the_build_that_now_edits_a_patched_record()
    {
        using var game = new Game();
        game.Mod("ModA.esm", m => m.Value = 10);
        game.Mod("ModB.esm", m => m.Weight = 2);
        game.Own("ModX.esm");
        game.Patch("Base.esm", "ModA.esm", "ModB.esm", "ModX.esm");
        game.Mod("ModX.esm", m => m.Value = 99);   // an update that now touches B_Misc
        // still before the patch: it changed since the build
        var (code, stdout, _) = game.Check(["Base.esm", "ModA.esm", "ModB.esm", "ModX.esm", "ConflictPatch.esm"]);
        Assert.Equal(1, code);
        Assert.StartsWith("REVIEW [new-editor] plugin ModX.esm now changes 1 record", Assert.Single(Lines(stdout, "new-editor")));
        // after the patch: it moved, and its copy wins
        (code, stdout, _) = game.Check(["Base.esm", "ModA.esm", "ModB.esm", "ConflictPatch.esm", "ModX.esm"]);
        Assert.Equal(1, code);
        Assert.StartsWith("REVIEW [moved] plugin ModX.esm now loads after ConflictPatch.esm", Assert.Single(Lines(stdout, "moved")));
    }

    [Fact]
    public void Another_spelling_of_the_patch_is_a_duplicate()
    {
        using var game = Ready();
        File.Copy(game.PatchFile, Path.Combine(game.Out, "Conflict Patch.esm"));
        var (code, stdout, _) = game.Check([.. WithPatch, "Conflict Patch.esm"]);
        Assert.Equal(1, code);
        var line = Assert.Single(Lines(stdout, "duplicate"));
        Assert.StartsWith("REVIEW [duplicate] Conflict Patch.esm has the same name as ConflictPatch.esm", line);
        Assert.Contains("it loads after ConflictPatch.esm", line);
        Assert.Empty(Lines(stdout, "new-editor"));   // another build of the patch isn't a new mod
    }

    [Fact]
    public void A_patch_file_that_isnt_the_manifests_build_is_patch_changed()
    {
        using var game = Ready();
        var old = File.ReadAllText(game.Manifest);
        game.Mod("ModA.esm", m => m.Value = 11);
        game.Patch(Built);                              // a new patch file ...
        File.WriteAllText(game.Manifest, old);          // ... with the old manifest
        var (code, stdout, _) = game.Check(WithPatch);
        Assert.Equal(1, code);
        Assert.StartsWith("REVIEW [patch-changed] ConflictPatch.esm in the load order", Assert.Single(Lines(stdout, "patch-changed")));
    }

    [Fact]
    public void A_patch_that_isnt_active_is_a_note_and_nothing_else()
    {
        using var game = Ready();
        game.Mod("ModA.esm", m => m.Value = 11);   // would be [updated], but nothing is checked
        var (code, stdout, stderr) = game.Check(Built);
        Assert.True(code == 0, stdout + stderr);
        Assert.StartsWith("NOTE [patch-off] ConflictPatch.esm isn't in the load order (it isn't active in Plugins.txt)", Assert.Single(Lines(stdout, "patch-off")));
        Assert.Empty(Lines(stdout, "updated"));
        Assert.StartsWith("RESULT: UP TO DATE (the patch isn't in the load order", ExeTests.ResultLine(stdout));
    }

    [Fact]
    public void Json_has_the_state_and_every_finding()
    {
        using var game = Ready();
        game.Mod("ModA.esm", m => m.Value = 11);
        game.Mod("ModC.esm", m => m.Value = 30);
        var json = Path.Combine(game.Dir, "check output.json");
        var (code, _, _) = game.Check(["Base.esm", "ModA.esm", "ModB.esm", "ModC.esm", "ConflictPatch.esm"], "--json", json);
        Assert.Equal(1, code);
        var r = JsonDocument.Parse(File.ReadAllText(json)).RootElement;
        Assert.Equal("ConflictPatch.esm", r.GetProperty("patch").GetString());
        Assert.Equal("rebuild", r.GetProperty("state").GetString());
        Assert.Equal("RESULT: REBUILD (2 reasons)", r.GetProperty("result").GetString());
        var f = r.GetProperty("findings").EnumerateArray().ToList();
        Assert.Equal(new[] { "updated", "new-editor" }, f.Select(x => x.GetProperty("rule").GetString()));
        Assert.All(f, x => Assert.Equal("REVIEW", x.GetProperty("level").GetString()));
        Assert.Equal("ModC.esm", f[1].GetProperty("plugin").GetString());
        Assert.Equal("000800:Base.esm", Assert.Single(f[1].GetProperty("records").EnumerateArray()).GetString());
    }

    [Fact]
    public void What_it_cant_check_is_exit_2()
    {
        using var game = Ready();
        // no manifest beside the patch (and --json still says why)
        var bare = Path.Combine(game.Sub("bare"), "ConflictPatch.esm");
        File.Copy(game.PatchFile, bare);
        var json = Path.Combine(game.Dir, "cant.json");
        var (code, stdout, stderr) = ExeTests.RunSplit("patch", "check", bare, "--data", game.Data, "--plugins", Path.Combine(game.Dir, "Plugins.txt"), "--json", json, "--quiet");
        Assert.Equal(2, code);
        Assert.Equal("RESULT: CANNOT CHECK (no patch-sources.json)", ExeTests.ResultLine(stdout));
        Assert.Contains("patch-sources.json doesn't exist", stderr);
        Assert.Equal("cannot check", JsonDocument.Parse(File.ReadAllText(json)).RootElement.GetProperty("state").GetString());
        // a schema it doesn't know
        File.WriteAllText(game.Manifest, File.ReadAllText(game.Manifest).Replace("\"schema\": 1", "\"schema\": 9"));
        (code, stdout, _) = game.Check(WithPatch);
        Assert.Equal(2, code);
        Assert.Equal("RESULT: CANNOT CHECK (manifest schema 9 is unknown)", ExeTests.ResultLine(stdout));
        // not JSON
        File.WriteAllText(game.Manifest, "{ not json");
        (code, stdout, _) = game.Check(WithPatch);
        Assert.Equal(2, code);
        Assert.Equal("RESULT: CANNOT CHECK (the manifest can't be read)", ExeTests.ResultLine(stdout));
        // a patch that doesn't exist, an unknown option, no patch at all, a missing Plugins.txt
        (code, stdout, _) = ExeTests.RunSplit("patch", "check", Path.Combine(game.Dir, "nope.esm"), "--quiet");
        Assert.Equal(2, code);
        Assert.Equal("RESULT: CANNOT CHECK (the patch doesn't exist)", ExeTests.ResultLine(stdout));
        (code, stdout, _) = ExeTests.RunSplit("patch", "check", game.PatchFile, "--bogus");
        Assert.Equal(2, code);
        Assert.Equal("RESULT: CANNOT CHECK (bad arguments; see the message above)", ExeTests.ResultLine(stdout));
        (code, _, _) = ExeTests.RunSplit("patch", "check");
        Assert.Equal(2, code);
    }

    [Fact]
    public void A_missing_load_order_is_exit_2()
    {
        using var game = Ready();
        var (code, stdout, _) = ExeTests.RunSplit("patch", "check", game.PatchFile, "--data", game.Data, "--plugins", Path.Combine(game.Dir, "no such Plugins.txt"), "--quiet");
        Assert.Equal(2, code);
        Assert.StartsWith("RESULT: CANNOT CHECK (the Plugins.txt ", ExeTests.ResultLine(stdout));
    }

    [Fact]
    public void Help_prints_the_usage_and_exits_0()
    {
        foreach (var h in new[] { "--help", "-h" })
        {
            var (code, stdout, _) = ExeTests.RunSplit("patch", "check", h);
            Assert.Equal(0, code);
            Assert.Contains("usage: sf-mod-check patch check <patch.esm>", stdout);
            Assert.DoesNotContain("RESULT:", stdout);
        }
        var (pc, pout, _) = ExeTests.RunSplit("patch", "--help");
        Assert.Equal(0, pc);
        Assert.Contains("sf-mod-check patch check <patch.esm>", pout);
    }

    [Fact]
    public void Patch_check_is_in_the_readme_agents_and_changelog()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "PatchCheck.cs"))) dir = dir.Parent;
        Assert.True(dir != null, "src\\PatchCheck.cs not found above " + AppContext.BaseDirectory);
        string Doc(string n) => File.ReadAllText(Path.Combine(dir!.FullName, n));
        var readme = Doc("README.md");
        var agents = Doc("AGENTS.md");
        var changes = Doc("CHANGELOG.md").Split("\n## 0.14.0")[0];
        foreach (var text in new[] { readme, agents, changes })
            Assert.Contains("patch check", text);
        foreach (var rule in new[] { "removed", "moved", "updated", "new-editor", "duplicate", "patch-changed", "patch-off" })
        {
            Assert.True(readme.Contains("`[" + rule + "]`"), $"README.md doesn't name patch check's [{rule}]");
            Assert.True(agents.Contains("`[" + rule + "]`"), $"AGENTS.md doesn't name patch check's [{rule}]");
        }
        foreach (var o in new[] { "--manifest", "--json", "--extra-data", "--plugins" })
        {
            Assert.Contains(o, PatchCheck.Usage);
            Assert.True(readme.Contains("`" + o), $"README.md doesn't mention patch check's {o}");
        }
        foreach (var r in new[] { "RESULT: UP TO DATE", "RESULT: REBUILD", "RESULT: CANNOT CHECK" })
        {
            Assert.Contains(r, readme);
            Assert.Contains(r, agents);
        }
    }
}
