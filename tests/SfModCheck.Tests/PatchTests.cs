// sf-mod-check patch (0.9.0) on made-up load orders: each test builds a tiny game in a sandbox, runs the built program
// and reads the patch it writes. No game files are needed.
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using Noggog;
using Xunit;

namespace SfModCheck.Tests;

public class PatchTests
{
    static readonly WriteOptions NoGap = new() { GapCheck = false, FixEffects = false };

    /// <summary>Base.esm (full) with two misc items, a quest with one dialogue topic, and an interior cell with a
    /// placed object.</summary>
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
            var misc2 = b.MiscItems.AddNew("B_Misc2");
            misc2.Value = 7;
            misc2.Weight = 3;
            var quest = b.Quests.AddNew("B_Quest");

            quest.DialogTopics.Add(new DialogTopic(b) { EditorID = "B_Topic", Quest = quest.ToLink<IQuestGetter>(), Priority = 50 });
            var cell = new Cell(b) { EditorID = "B_Cell" };
            cell.Temporary.Add(new PlacedObject(b) { Base = misc.ToLink<IPlaceableObjectGetter>() });
            b.Cells.Add(new CellBlock { SubBlocks = new ExtendedList<CellSubBlock> { new CellSubBlock { Cells = new ExtendedList<Cell> { cell } } } });
            misc.Keywords = [b.Keywords.AddNew("B_K0").ToLink<IKeywordGetter>()];
            b.Keywords.AddNew("B_K1");   // spare keywords two mods can both add (merged-list report text)
            b.Keywords.AddNew("B_K2");
            using var g = new GameData(Data);
            PluginWriter.Write(b, Data, g, NoGap);
        }

        public void Mod(string name, Action<StarfieldMod, IStarfieldModGetter> build)
        {
            var mod = Plugin.New(name, PluginStyle.Light, "test", "made-up mod");
            using var g = new GameData(Data);
            build(mod, g.Open("Base.esm"));
            PluginWriter.Write(mod, Data, g, NoGap);
        }

        public string Txt(params string[] active)
        {
            var txt = Path.Combine(Dir, "Plugins.txt");
            File.WriteAllLines(txt, active.Select(a => "*" + a));
            return txt;
        }

        public string Out => Path.Combine(Dir, "patch");

        public (int Code, string Stdout, string Stderr) Patch(string[] active, params string[] extra) =>
            ExeTests.RunSplit(new[] { "patch", "--data", Data, "--plugins", Txt(active), "--out", Out, "--quiet" }.Concat(extra).ToArray());

        readonly List<GameData> opened = new();

        /// <summary>Opens the written patch (from the patch folder) with its masters' flags known; closed with the game.</summary>
        public IStarfieldModGetter Open(string file)
        {
            var g = new GameData(Data, [Path.GetDirectoryName(file)!]);
            opened.Add(g);
            return g.Open(Path.GetFileName(file));
        }

        public override void Dispose()
        {
            foreach (var g in opened) g.Dispose();
            base.Dispose();
        }
    }

    static MiscItem Misc(StarfieldMod mod, IStarfieldModGetter b, string edid = "B_Misc") =>
        mod.MiscItems.GetOrAddAsOverride(b.MiscItems.First(m => m.EditorID == edid));

    [Fact]
    public void Forwardable_changes_from_two_mods_end_up_in_one_patch()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b) => Misc(m, b).Value = 10);
        game.Mod("ModB.esm", (m, b) => Misc(m, b).Weight = 2);
        var (code, stdout, stderr) = game.Patch(new[] { "Base.esm", "ModA.esm", "ModB.esm" });
        Assert.True(code == 0, stdout + stderr);
        Assert.StartsWith("RESULT: OK, patch ", ExeTests.ResultLine(stdout));
        Assert.Contains("1 records, 1 fields forwarded", ExeTests.ResultLine(stdout));
        var patch = game.Open(Path.Combine(game.Out, "ConflictPatch.esm"));
        var misc = Assert.Single(patch.MiscItems);
        Assert.Equal((10, 2f), (misc.Value, misc.Weight));
        Assert.Equal(new[] { "Base.esm", "ModA.esm", "ModB.esm" }, patch.ModHeader.MasterReferences.Select(r => r.Master.FileName.String));
        Assert.True(patch.ModHeader.Flags.HasFlag(StarfieldModHeader.HeaderFlag.Light));
        Assert.Equal(1, patch.EnumerateMajorRecords().Count());
        // the load order with the patch at the end, ready for a conflict run
        var withPatch = File.ReadAllLines(Path.Combine(game.Out, "Plugins-with-patch.txt"));
        Assert.Equal("*ConflictPatch.esm", withPatch[^1]);
        Assert.Contains("Value from ModA.esm", File.ReadAllText(Path.Combine(game.Out, "patch-report.md")));
    }

    [Fact]
    public void Two_mods_that_add_to_one_list_both_keep_their_entries()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b) => Misc(m, b).Keywords!.Add(m.Keywords.AddNew("A_K").ToLink<IKeywordGetter>()));
        game.Mod("ModB.esm", (m, b) => Misc(m, b).Keywords!.Add(m.Keywords.AddNew("B_K").ToLink<IKeywordGetter>()));
        game.Mod("ModC.esm", (m, b) => Misc(m, b).Value = 99);   // wins the record, didn't touch the keywords
        var (code, stdout, stderr) = game.Patch(new[] { "Base.esm", "ModA.esm", "ModB.esm", "ModC.esm" });
        Assert.True(code == 0, stdout + stderr);
        var misc = Assert.Single(game.Open(Path.Combine(game.Out, "ConflictPatch.esm")).MiscItems);
        Assert.Equal(99, misc.Value);
        var kws = misc.Keywords!.Select(k => k.FormKey.ModKey.FileName.String).ToList();
        Assert.Equal(new[] { "Base.esm", "ModA.esm", "ModB.esm" }, kws.Order());
        Assert.DoesNotContain("kept neither", File.ReadAllText(Path.Combine(game.Out, "patch-report.md")));
    }

    static IFormLinkGetter<IKeywordGetter> BaseKeyword(IStarfieldModGetter b, string edid) =>
        b.Keywords.First(k => k.EditorID == edid).ToLink<IKeywordGetter>();

    [Fact]
    public void A_merged_list_line_says_what_the_merge_really_added()
    {
        using var game = new Game();
        // ModA adds B_K1, which the winner ModD's list already has: the merge adds nothing for ModA.
        game.Mod("ModA.esm", (m, b) => Misc(m, b).Keywords!.Add(BaseKeyword(b, "B_K1")));
        // ModC adds a keyword of its own: the merge adds that one entry.
        game.Mod("ModC.esm", (m, b) => Misc(m, b).Keywords!.Add(m.Keywords.AddNew("C_K").ToLink<IKeywordGetter>()));
        game.Mod("ModD.esm", (m, b) => { var x = Misc(m, b); x.Keywords!.Add(BaseKeyword(b, "B_K1")); x.Keywords!.Add(BaseKeyword(b, "B_K2")); });
        var (code, stdout, stderr) = game.Patch(new[] { "Base.esm", "ModA.esm", "ModC.esm", "ModD.esm" });
        Assert.True(code == 0, stdout + stderr);
        var misc = Assert.Single(game.Open(Path.Combine(game.Out, "ConflictPatch.esm")).MiscItems);
        Assert.Equal(4, misc.Keywords!.Count);
        var report = File.ReadAllLines(Path.Combine(game.Out, "patch-report.md"));
        var a = report.Where(l => l.Contains("ModA.esm") && l.Contains("eyword")).ToList();
        var c = report.Where(l => l.Contains("ModC.esm") && l.Contains("eyword")).ToList();
        Assert.True(a.Any(l => l.Contains("nothing new over ModD.esm's list")), string.Join("\n", report));
        Assert.DoesNotContain(a, l => l.Contains("adds"));
        Assert.True(c.Any(l => l.Contains("adds 1 entry") && l.Contains("C_K")), string.Join("\n", report));
    }

    [Fact]
    public void After_the_patch_conflict_finds_no_lost_change()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b) => Misc(m, b).Value = 10);
        game.Mod("ModB.esm", (m, b) => Misc(m, b).Weight = 2);
        Assert.Equal(0, game.Patch(new[] { "Base.esm", "ModA.esm", "ModB.esm" }).Code);
        var report = Path.Combine(game.Dir, "report");
        var (code, stdout, _) = ExeTests.RunSplit("conflict", "--data", game.Data, "--plugins", Path.Combine(game.Out, "Plugins-with-patch.txt"),
            "--extra-data", game.Out, "--out", report, "--quiet");
        Assert.Equal(0, code);
        Assert.StartsWith("RESULT: OK, 0 lost-change entries", ExeTests.ResultLine(stdout));
        // and the patch passes check
        var (checkCode, checkOut, checkErr) = ExeTests.RunSplit("check", Path.Combine(game.Out, "ConflictPatch.esm"), "--data", game.Data, "--quiet", "--no-assets");
        Assert.True(checkCode == 0, checkOut + checkErr);
    }

    [Fact]
    public void A_choice_keeps_the_winner_unless_picked()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b) => Misc(m, b).Value = 10);
        game.Mod("ModB.esm", (m, b) => Misc(m, b).Value = 20);
        var active = new[] { "Base.esm", "ModA.esm", "ModB.esm" };
        var (code, stdout, _) = game.Patch(active);
        Assert.Equal(0, code);
        Assert.StartsWith("RESULT: OK, nothing to patch", ExeTests.ResultLine(stdout));
        Assert.False(File.Exists(Path.Combine(game.Out, "ConflictPatch.esm")));
        var report = File.ReadAllText(Path.Combine(game.Out, "patch-report.md"));
        var fk = "000800:Base.esm";
        Assert.Contains($"--pick {fk}:Value=ModA.esm", report);

        (code, stdout, _) = game.Patch(active, "--pick", $"{fk}:Value=ModA.esm");
        Assert.Equal(0, code);
        Assert.Contains("1 picks used", ExeTests.ResultLine(stdout));
        var patch = game.Open(Path.Combine(game.Out, "ConflictPatch.esm"));
        Assert.Equal(10, Assert.Single(patch.MiscItems).Value);
    }

    [Fact]
    public void Only_limits_the_patch_to_the_named_mods()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b) => Misc(m, b).Value = 10);
        game.Mod("ModB.esm", (m, b) => Misc(m, b).Weight = 2);
        game.Mod("ModC.esm", (m, b) => Misc(m, b, "B_Misc2").Value = 70);
        game.Mod("ModD.esm", (m, b) => Misc(m, b, "B_Misc2").Weight = 30);
        var (code, stdout, stderr) = game.Patch(new[] { "Base.esm", "ModA.esm", "ModB.esm", "ModC.esm", "ModD.esm" }, "--only", "ModD.esm");
        Assert.True(code == 0, stdout + stderr);
        var patch = game.Open(Path.Combine(game.Out, "ConflictPatch.esm"));
        var misc = Assert.Single(patch.MiscItems);
        Assert.Equal("B_Misc2", misc.EditorID);
        Assert.Equal((70, 30f), (misc.Value, misc.Weight));
        Assert.DoesNotContain(patch.ModHeader.MasterReferences, r => r.Master.FileName == "ModA.esm");
    }

    [Fact]
    public void A_patched_quest_carries_no_dialogue()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b) =>
        {
            var kw = m.Keywords.AddNew("A_Keyword");
            var q = m.Quests.GetOrAddAsOverride(b.Quests.First());
            q.Keywords = [kw.ToLink<IKeywordGetter>()];
        });
        game.Mod("ModB.esm", (m, b) =>
        {
            var q = m.Quests.GetOrAddAsOverride(b.Quests.First());
            q.Name = "Renamed";
        });
        var (code, stdout, stderr) = game.Patch(new[] { "Base.esm", "ModA.esm", "ModB.esm" });
        Assert.True(code == 0, stdout + stderr);
        var patch = game.Open(Path.Combine(game.Out, "ConflictPatch.esm"));
        var quest = Assert.Single(patch.Quests);
        Assert.Equal("A_Keyword", Assert.Single(quest.Keywords!).FormKey.ModKey.FileName == "ModA.esm" ? "A_Keyword" : "?");
        Assert.Equal("Renamed", quest.Name?.String);
        Assert.Empty(quest.DialogTopics);
    }

    [Fact]
    public void An_older_copy_of_the_patch_in_the_load_order_is_left_out()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b) => Misc(m, b).Value = 10);
        game.Mod("ModB.esm", (m, b) => Misc(m, b).Weight = 2);
        // an old patch that sets something else, installed at the end
        game.Mod("ConflictPatch.esm", (m, b) => Misc(m, b).Weight = 99);
        var (code, stdout, stderr) = ExeTests.RunSplit("patch", "--data", game.Data, "--plugins", game.Txt("Base.esm", "ModA.esm", "ModB.esm", "ConflictPatch.esm"), "--out", game.Out);
        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("already has ConflictPatch.esm", stderr);
        var patch = game.Open(Path.Combine(game.Out, "ConflictPatch.esm"));
        Assert.Equal((10, 2f), (Assert.Single(patch.MiscItems).Value, patch.MiscItems.First().Weight));
        Assert.DoesNotContain(patch.ModHeader.MasterReferences, r => r.Master.FileName == "ConflictPatch.esm");
    }

    [Fact]
    public void Nothing_to_patch_without_conflicts()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b) => Misc(m, b).Value = 10);
        var (code, stdout, _) = game.Patch(new[] { "Base.esm", "ModA.esm" });
        Assert.Equal(0, code);
        Assert.StartsWith("RESULT: OK, nothing to patch", ExeTests.ResultLine(stdout));
    }

    [Theory]
    [InlineData("Patch-ShatteredSpace.esm")]
    [InlineData("BlueprintShips-Patch.esm")]
    [InlineData("..\\Patch.esm")]
    [InlineData("Patch.txt")]
    public void A_bad_patch_name_is_refused(string name)
    {
        using var game = new Game();
        var (code, stdout, _) = game.Patch(new[] { "Base.esm" }, "--name", name);
        Assert.Equal(2, code);
        Assert.StartsWith("RESULT: CANNOT BUILD", ExeTests.ResultLine(stdout));
    }

    [Fact]
    public void The_patch_is_never_written_into_the_Data_folder()
    {
        using var game = new Game();
        var (code, _, stderr) = ExeTests.RunSplit("patch", "--data", game.Data, "--plugins", game.Txt("Base.esm"), "--out", game.Data, "--quiet");
        Assert.Equal(2, code);
        Assert.Contains("is the Data folder", stderr);
    }

    [Theory]
    [InlineData("064F3C:Starfield.esm=ModA.esm", "064F3C:Starfield.esm", null, "ModA.esm")]
    [InlineData("064F3C:Starfield.esm:Value=ModA.esm", "064F3C:Starfield.esm", "Value", "ModA.esm")]
    [InlineData(" 000800:Base.esm:Keywords = My Mod.esp", "000800:Base.esm", "Keywords", "My Mod.esp")]
    public void Picks_are_read(string text, string fk, string? field, string plugin)
    {
        var p = PatchCommand.ParsePick(text);
        Assert.NotNull(p);
        Assert.Equal((FormKey.Factory(fk), field, plugin), (p!.FormKey, p.Field, p.Plugin));
    }

    [Theory]
    [InlineData("064F3C:Starfield.esm")]
    [InlineData("064F3C:Starfield.esm=")]
    [InlineData("nonsense=ModA.esm")]
    [InlineData("064F3C:Starfield.esm:Va lue=ModA.esm")]
    [InlineData("064F3C:Starfield.esm=..\\ModA.esm")]
    public void Bad_picks_are_refused(string text) => Assert.Null(PatchCommand.ParsePick(text));

    [Fact]
    public void Records_inside_cells_and_dialogue_are_left_to_xEdit()
    {
        Assert.NotNull(PatchCommand.WhyNotBuilt(typeof(IPlacedObjectGetter)));
        Assert.NotNull(PatchCommand.WhyNotBuilt(typeof(ICellGetter)));
        Assert.NotNull(PatchCommand.WhyNotBuilt(typeof(IDialogResponsesGetter)));
        Assert.Null(PatchCommand.WhyNotBuilt(typeof(IMiscItemGetter)));
        Assert.Null(PatchCommand.WhyNotBuilt(typeof(IQuestGetter)));
    }

    // Relay step 1 (2026-11-20): on Zacha-Test the patch merged the navmesh info map from 22 plugins and the result
    // held fewer navmeshes than Starfield.esm's own copy. NAVI is never built.
    [Fact]
    public void The_navmesh_info_map_is_not_built()
    {
        var why = PatchCommand.WhyNotBuilt(typeof(INavigationMeshInfoMapGetter));
        Assert.NotNull(why);
        Assert.Contains("navmesh info map", why);
        Assert.NotNull(PatchCommand.WhyNotBuilt(typeof(NavigationMeshInfoMap)));
    }
}
