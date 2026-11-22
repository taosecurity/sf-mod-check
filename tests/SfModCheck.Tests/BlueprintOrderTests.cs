// 0.12.0 (LoadOrderFollowups blueprint relay, step 2, 2026-11-21): the official BlueprintShips-*.esm files load after
// every plugin, oldest file first (modified time, then name), as the game does. Each test builds a tiny made-up game
// whose files carry the official names (Starfield.esm, SFBGS050.esm and their BlueprintShips files). No game files.
using System.Text.Json;
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Starfield;
using Noggog;
using SfModCheck.Conflict;
using Xunit;

namespace SfModCheck.Tests;

public class BlueprintOrderTests
{
    static readonly WriteOptions NoGap = new() { GapCheck = false, FixEffects = false };

    /// <summary>Starfield.esm (made up) with three keywords and one ship-like GBFM (B_Ship, keyword K0), plus
    /// SFBGS050.esm (empty, mastering Starfield.esm).</summary>
    sealed class Game : Sandbox
    {
        public readonly string Data;
        public Game()
        {
            Data = Sub("Data");
            var b = Plugin.New("Starfield.esm", PluginStyle.Full, "test", "made-up base game");
            var k = Enumerable.Range(0, 3).Select(i => b.Keywords.AddNew($"B_K{i}")).ToList();
            var ship = b.GenericBaseForms.AddNew("B_Ship");
            ship.Components.Add(Keywords(k[0]));
            using (var g = new GameData(Data)) PluginWriter.Write(b, Data, g, NoGap);
            Full("SFBGS050.esm", (m, open) => m.Keywords.AddNew("TA_K"));
        }

        /// <summary>Writes a full plugin (the official files are full masters).</summary>
        public void Full(string name, Action<StarfieldMod, Func<string, IStarfieldModGetter>> build) => Write(name, PluginStyle.Full, build);

        /// <summary>Writes a light mod.</summary>
        public void Mod(string name, Action<StarfieldMod, Func<string, IStarfieldModGetter>> build) => Write(name, PluginStyle.Light, build);

        void Write(string name, PluginStyle style, Action<StarfieldMod, Func<string, IStarfieldModGetter>> build)
        {
            var mod = Plugin.New(name, style, "test", "made-up plugin");
            using var g = new GameData(Data);
            build(mod, n => g.Open(n));
            PluginWriter.Write(mod, Data, g, NoGap);
        }

        public void Age(string name, DateTime utc) => File.SetLastWriteTimeUtc(Path.Combine(Data, name), utc);

        public string PluginsTxt(params string[] lines)
        {
            var txt = Path.Combine(Dir, "Plugins.txt");
            File.WriteAllLines(txt, lines);
            return txt;
        }

        public (int Code, string Stdout, string Stderr, JsonElement Json) Conflict(string txt, params string[] extra)
        {
            var outDir = Path.Combine(Dir, "report");
            var (code, stdout, stderr) = ExeTests.RunSplit(new[] { "conflict", "--data", Data, "--plugins", txt, "--out", outDir, "--quiet" }.Concat(extra).ToArray());
            var json = File.Exists(Path.Combine(outDir, "conflicts.json"))
                ? JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "conflicts.json"))).RootElement.Clone()
                : default;
            return (code, stdout, stderr, json);
        }
    }

    static KeywordFormComponent Keywords(IKeywordGetter k) =>
        new() { Keywords = new ExtendedList<IFormLinkGetter<IKeywordGetter>> { k.ToLink<IKeywordGetter>() } };

    static GenericBaseForm Ship(StarfieldMod into, IStarfieldModGetter b, int keyword)
    {
        var s = into.GenericBaseForms.GetOrAddAsOverride(b.GenericBaseForms.First());
        s.Components.Clear();
        s.Components.Add(Keywords(b.Keywords.Skip(keyword).First()));
        return s;
    }

    static List<string> Order(JsonElement json) =>
        json.GetProperty("loadOrder").EnumerateArray().Select(p => p.GetProperty("FileName").GetString() ?? "").ToList();

    static List<string> Problems(JsonElement json) =>
        json.GetProperty("loadOrderProblems").EnumerateArray().Select(p => p.GetString() ?? "").ToList();

    [Fact]
    public void A_mod_change_to_a_ship_that_a_BlueprintShips_file_also_changes_loses_to_that_file()
    {
        using var game = new Game();
        game.Full("BlueprintShips-Starfield.esm", (m, open) => Ship(m, open("Starfield.esm"), 2));
        game.Mod("ModA.esm", (m, open) => Ship(m, open("Starfield.esm"), 1));
        // Listing the BlueprintShips file early doesn't move it: the game places it itself.
        var txt = game.PluginsTxt("*Starfield.esm", "*BlueprintShips-Starfield.esm", "*ModA.esm");
        // The CHANGELOG and README example is a real run on these made-up files: set SFMODCHECK_BLUEPRINT_EXAMPLE to a
        // folder to keep a copy of them (Data\ and Plugins.txt).
        if (Environment.GetEnvironmentVariable("SFMODCHECK_BLUEPRINT_EXAMPLE") is { Length: > 0 } keep)
        {
            Directory.CreateDirectory(Path.Combine(keep, "Data"));
            foreach (var f in Directory.EnumerateFiles(game.Data))
            {
                var dest = Path.Combine(keep, "Data", Path.GetFileName(f));
                File.Copy(f, dest, true);
                File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(f));
            }
            File.Copy(txt, Path.Combine(keep, "Plugins.txt"), true);
        }
        var (code, stdout, _, json) = game.Conflict(txt);
        Assert.True(code == 0, stdout);
        Assert.Equal(["Starfield.esm", "SFBGS050.esm", "ModA.esm", "BlueprintShips-Starfield.esm"], Order(json));
        Assert.Equal(Enumerable.Range(0, 4), json.GetProperty("loadOrder").EnumerateArray().Select(p => p.GetProperty("Index").GetInt32()));
        var lost = Assert.Single(json.GetProperty("lost").EnumerateArray());
        Assert.Equal(("ModA.esm", "BlueprintShips-Starfield.esm", "GenericBaseForm"),
            (lost.GetProperty("Loser").GetString(), lost.GetProperty("Winner").GetString(), lost.GetProperty("RecordType").GetString()));
        Assert.Empty(Problems(json));
        // Step 3: the advice says nothing can bring the change back, and offers no move or patch.
        var report = File.ReadAllText(Path.Combine(game.Dir, "report", "report.md"));
        Assert.Contains("loads after every mod, so no load order or patch brings this change back; the edit ModA.esm makes to this ship or cell doesn't reach the game.", report);
        Assert.Contains("No load order or patch brings this change back.", report);
        Assert.DoesNotContain("Move ModA.esm", report);
        Assert.DoesNotContain("A small local patch", report);
        Assert.DoesNotContain("A patch can restore it", report);
        Assert.Equal("BlueprintShips-Starfield.esm (base game ship blueprints) loads after every mod, so no load order or patch brings this change back; " +
                     "the edit ModA.esm makes to these 2 ships or cells doesn't reach the game.",
            Recommend.BlueprintAdvice("BlueprintShips-Starfield.esm", "ModA.esm", 2));
        // Step 4b (SWG's StarStationPolvo row): both plugins changed the field, but nothing can be chosen, so it is
        // "Lost" with nothing to do, never a "choice".
        Assert.Contains("there is nothing to do.", report);
        Assert.DoesNotContain("so it is a choice", report);
        Assert.Contains("| Components | Lost |", report);
        Assert.DoesNotContain("| Choice |", report);
        var html = File.ReadAllText(Path.Combine(game.Dir, "report", "report.html"));
        Assert.Contains("<span class=\"badge minor\">Lost</span>", html);
        Assert.DoesNotContain("choice</span>", html);
    }

    /// <summary>A navmesh info map in the made-up base game, then copies of it with one more entry each.</summary>
    static void Navi(StarfieldMod into, IStarfieldModGetter? baseGame, uint navmesh)
    {
        var navi = baseGame is null
            ? into.NavigationMeshInfoMaps.AddNew("B_Navi")
            : into.NavigationMeshInfoMaps.GetOrAddAsOverride(baseGame.NavigationMeshInfoMaps.First());
        if (baseGame is null) navi.NavMeshVersion = 15;
        var mesh = new FormKey(ModKey.FromFileName("Starfield.esm"), navmesh);
        navi.MapInfos.Add(new NavigationMapInfo { NavigationMesh = mesh.ToLink<INavigationMeshGetter>(), Data = new byte[20], Unknown = Bytes.Traversal(navmesh, navmesh) });
    }

    [Fact]
    public void The_navmesh_info_map_keeps_its_own_rule_when_a_BlueprintShips_file_has_the_last_copy()
    {
        using var game = new Game();
        // the base game gets a navmesh info map after the fact (Game() wrote Starfield.esm already)
        var b = Plugin.New("Starfield.esm", PluginStyle.Full, "test", "made-up base game");
        b.Keywords.AddNew("B_K0"); b.Keywords.AddNew("B_K1"); b.Keywords.AddNew("B_K2");
        b.GenericBaseForms.AddNew("B_Ship");
        Navi(b, null, 0x900);
        using (var g = new GameData(game.Data)) PluginWriter.Write(b, game.Data, g, NoGap);
        game.Full("SFBGS050.esm", (m, open) => m.Keywords.AddNew("TA_K"));
        game.Full("BlueprintShips-Starfield.esm", (m, open) => Navi(m, open("Starfield.esm"), 0x901));
        game.Mod("ModA.esm", (m, open) => Navi(m, open("Starfield.esm"), 0x902));
        var txt = game.PluginsTxt("*ModA.esm");
        var (code, stdout, stderr, json) = game.Conflict(txt);
        Assert.True(code == 0, stdout + stderr);
        var lost = Assert.Single(json.GetProperty("lost").EnumerateArray());
        Assert.Equal(("ModA.esm", "BlueprintShips-Starfield.esm", "NavigationMeshInfoMap"),
            (lost.GetProperty("Loser").GetString(), lost.GetProperty("Winner").GetString(), lost.GetProperty("RecordType").GetString()));
        var report = File.ReadAllText(Path.Combine(game.Dir, "report", "report.md"));
        // conflict: the NAVI note, not the BlueprintShips "doesn't reach the game" advice, and no move or patch prompt
        Assert.Contains("so its copy of the navmesh info map loads last. Each plugin's copy lists only its own navmeshes", report);
        Assert.Contains("Each plugin's copy of the navmesh info map lists only its own navmeshes, so it isn't patched.", report);
        Assert.DoesNotContain("doesn't reach the game", report);
        Assert.DoesNotContain("No load order or patch brings this change back", report);
        Assert.DoesNotContain("| Lost |", report);
        Assert.DoesNotContain("Move ModA.esm", report);
        Assert.DoesNotContain("A small local patch", report);

        // patch: not built, with the navmesh info map's reason, not the BlueprintShips one
        var outDir = Path.Combine(game.Dir, "patch");
        var (pcode, pout, _) = ExeTests.RunSplit("patch", "--data", game.Data, "--plugins", txt, "--out", outDir, "--quiet");
        Assert.True(pcode == 0, pout);
        Assert.Contains("1 records not built", ExeTests.ResultLine(pout));
        var preport = File.ReadAllText(Path.Combine(outDir, "patch-report.md"));
        Assert.Contains("the navmesh info map isn't patched", preport);
        Assert.DoesNotContain("this patch too", preport);
        Assert.DoesNotContain("no patch can restore these", preport);
    }

    [Fact]
    public void Patch_leaves_out_a_change_lost_to_a_BlueprintShips_file()
    {
        using var game = new Game();
        game.Full("BlueprintShips-Starfield.esm", (m, open) => Ship(m, open("Starfield.esm"), 2));
        game.Mod("ModA.esm", (m, open) => Ship(m, open("Starfield.esm"), 1));
        var txt = game.PluginsTxt("*ModA.esm");
        var outDir = Path.Combine(game.Dir, "patch");
        var (code, stdout, _) = ExeTests.RunSplit("patch", "--data", game.Data, "--plugins", txt, "--out", outDir, "--quiet");
        Assert.True(code == 0, stdout);
        var result = ExeTests.ResultLine(stdout);
        Assert.StartsWith("RESULT: OK, nothing to patch", result);
        Assert.Contains("1 records not built", result);
        Assert.False(File.Exists(Path.Combine(outDir, "ConflictPatch.esm")));
        var report = File.ReadAllText(Path.Combine(outDir, "patch-report.md"));
        Assert.Contains("## Not built (1): no patch can restore these", report);
        Assert.Contains("loads after every mod, this patch too, so no patch or load order brings this change back", report);
        Assert.DoesNotContain("do these by hand in xEdit", report);
    }

    [Fact]
    public void BlueprintShips_files_load_last_oldest_file_first()
    {
        using var game = new Game();
        game.Full("BlueprintShips-Starfield.esm", (m, open) => Ship(m, open("Starfield.esm"), 2));
        game.Full("BlueprintShips-SFBGS050.esm", (m, open) => m.Keywords.AddNew("BP050_K"));
        game.Mod("ModA.esm", (m, open) => Ship(m, open("Starfield.esm"), 1));
        game.Mod("ModB.esm", (m, open) => m.Keywords.AddNew("ModB_K"));
        // The Terran Armada file is older here, so it loads first of the two, and the base game's file wins.
        game.Age("BlueprintShips-Starfield.esm", new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        game.Age("BlueprintShips-SFBGS050.esm", new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var txt = game.PluginsTxt("*ModA.esm", "*ModB.esm");
        var (code, stdout, stderr, json) = game.Conflict(txt, "--verbose");
        Assert.True(code == 0, stdout);
        Assert.Equal(["Starfield.esm", "SFBGS050.esm", "ModA.esm", "ModB.esm", "BlueprintShips-SFBGS050.esm", "BlueprintShips-Starfield.esm"], Order(json));
        Assert.Contains("load order: BlueprintShips-SFBGS050.esm goes after every plugin (BlueprintShips file 1 of 2, modified 2024-06-01 00:00:00 UTC)", stderr);
        Assert.Contains("load order: BlueprintShips-Starfield.esm goes after every plugin (BlueprintShips file 2 of 2, modified 2025-06-01 00:00:00 UTC)", stderr);

        // Same dates: by name.
        game.Age("BlueprintShips-SFBGS050.esm", new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var lo = LoadOrderSetup.Build(game.Data, txt);
        try { Assert.Equal(["BlueprintShips-SFBGS050.esm", "BlueprintShips-Starfield.esm"], lo.Plugins.TakeLast(2).Select(p => p.FileName)); }
        finally { foreach (var p in lo.Plugins) p.Mod.Dispose(); }

        // Newer Terran Armada file: it goes last.
        game.Age("BlueprintShips-SFBGS050.esm", new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        lo = LoadOrderSetup.Build(game.Data, txt);
        try { Assert.Equal(["BlueprintShips-Starfield.esm", "BlueprintShips-SFBGS050.esm"], lo.Plugins.TakeLast(2).Select(p => p.FileName)); }
        finally { foreach (var p in lo.Plugins) p.Mod.Dispose(); }
    }

    [Fact]
    public void A_BlueprintShips_file_whose_plugin_is_turned_off_is_left_out()
    {
        using var game = new Game();
        game.Full("BlueprintShips-SFBGS050.esm", (m, open) => m.Keywords.AddNew("BP050_K"));
        game.Mod("ModA.esm", (m, open) => m.Keywords.AddNew("ModA_K"));
        var txt = game.PluginsTxt("SFBGS050.esm", "*ModA.esm");
        var (code, stdout, stderr, json) = game.Conflict(txt, "--verbose");
        Assert.True(code == 0, stdout);
        Assert.Equal(["Starfield.esm", "ModA.esm"], Order(json));
        Assert.Contains("load order: BlueprintShips-SFBGS050.esm left out: its plugin SFBGS050.esm is turned off in Plugins.txt", stderr);
    }

    [Fact]
    public void A_mod_with_a_BlueprintShips_master_gets_a_problem_and_still_loads_before_it()
    {
        using var game = new Game();
        // The BlueprintShips file adds a ship of its own (keyword K0); ModM changes that ship's keyword to K1.
        game.Full("BlueprintShips-Starfield.esm", (m, open) =>
            m.GenericBaseForms.AddNew("BP_Ship").Components.Add(Keywords(open("Starfield.esm").Keywords.First())));
        game.Mod("ModM.esm", (m, open) =>
        {
            var s = m.GenericBaseForms.GetOrAddAsOverride(open("BlueprintShips-Starfield.esm").GenericBaseForms.First());
            s.Components.Clear();
            s.Components.Add(Keywords(open("Starfield.esm").Keywords.Skip(1).First()));
        });
        Assert.Contains("BlueprintShips-Starfield.esm", LoadOrderSetup.ModHeaderMasters(Path.Combine(game.Data, "ModM.esm")));
        var txt = game.PluginsTxt("*ModM.esm");
        var (code, stdout, stderr, json) = game.Conflict(txt, "--verbose");
        Assert.True(code == 0, stdout);
        Assert.Equal(["Starfield.esm", "SFBGS050.esm", "ModM.esm", "BlueprintShips-Starfield.esm"], Order(json));
        var problem = Assert.Single(Problems(json));
        Assert.Equal("ModM.esm: has the master BlueprintShips-Starfield.esm, but the game loads BlueprintShips files after every mod, " +
                     "so this plugin loads before its master; its records were judged in the game's order", problem);
        Assert.Contains("load order: ModM.esm has the BlueprintShips master BlueprintShips-Starfield.esm; BlueprintShips-Starfield.esm stays after every plugin", stderr);
        // ModM's edit of the BlueprintShips file's own ship loses to that file in the game, judged against that file's
        // own version (step 3: the earliest copy, ModM's, is not taken as the original).
        Assert.True(json.GetProperty("lost").GetArrayLength() == 1, stderr);
        var lost = Assert.Single(json.GetProperty("lost").EnumerateArray());
        Assert.Equal(("ModM.esm", "BlueprintShips-Starfield.esm", "GenericBaseForm"),
            (lost.GetProperty("Loser").GetString(), lost.GetProperty("Winner").GetString(), lost.GetProperty("RecordType").GetString()));
        var field = Assert.Single(lost.GetProperty("Fields").EnumerateArray());
        Assert.Equal("Components", field.GetProperty("Field").GetString());
        // judged against the BlueprintShips file's version: the winner didn't change this field itself
        Assert.False(field.GetProperty("WinnerChangedToo").GetBoolean());
        Assert.Empty(json.GetProperty("itms").EnumerateArray());
        var report = File.ReadAllText(Path.Combine(game.Dir, "report", "report.md"));
        Assert.Contains("the edit ModM.esm makes to this ship or cell doesn't reach the game.", report);
    }
}
