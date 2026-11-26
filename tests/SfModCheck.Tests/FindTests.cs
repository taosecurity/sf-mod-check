// sf-mod-check find (0.16.0) on a made-up load order: a full base plugin, a medium and a light mod that override its
// misc item, and an extra folder. Each test runs the built program. None of this exists in 0.15.2 (no find command).
using System.Text.Json;
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Starfield;
using Mutagen.Bethesda.Plugins.Records;
using SfModCheck.Conflict;
using Xunit;

namespace SfModCheck.Tests;

public class FindTests
{
    static readonly WriteOptions NoGap = new() { GapCheck = false, FixEffects = false };

    /// <summary>FindBase.esm (full): misc item FB_Misc "Made-up Widget" (value 5) and keyword FB_Keyword.
    /// FindMedium.esm (medium) overrides FB_Misc (value 7) and adds the keyword Dup; FindLight.esm (light) overrides FB_Misc
    /// (value 9) and adds its own keyword Dup, a second record with that EditorID. Plugins.txt lists all three.</summary>
    sealed class Game : Sandbox
    {
        public readonly string Data, PluginsTxt;
        public readonly FormKey BaseMisc, BaseKeyword, MediumDup, LightDup;

        public Game()
        {
            Data = Sub("Data");
            var b = Plugin.New("FindBase.esm", PluginStyle.Full, "test", "made-up base");
            var misc = b.MiscItems.AddNew("FB_Misc");
            misc.Name = "Made-up Widget";
            misc.Value = 5;
            BaseMisc = misc.FormKey;
            BaseKeyword = b.Keywords.AddNew("FB_Keyword").FormKey;
            using (var g = new GameData(Data)) PluginWriter.Write(b, Data, g, NoGap);
            MediumDup = Mod("FindMedium.esm", PluginStyle.Medium, 7);
            LightDup = Mod("FindLight.esm", PluginStyle.Light, 9);
            PluginsTxt = Path.Combine(Dir, "Plugins.txt");
            File.WriteAllLines(PluginsTxt, ["*FindBase.esm", "*FindMedium.esm", "*FindLight.esm"]);
        }

        /// <summary>A mod on FindBase.esm that sets FB_Misc's value and adds a keyword with the EditorID Dup.</summary>
        FormKey Mod(string name, PluginStyle style, int value, string? dir = null)
        {
            dir ??= Data;
            var mod = Plugin.New(name, style, "test", "made-up mod");
            using var g = new GameData(Data);
            var baseMod = g.Open("FindBase.esm");
            mod.MiscItems.GetOrAddAsOverride(baseMod.MiscItems.First()).Value = value;
            var dup = mod.Keywords.AddNew("Dup");
            PluginWriter.Write(mod, dir, g, NoGap);
            return dup.FormKey;
        }

        public FormKey ExtraMod(string dir) => Mod("FindExtra.esm", PluginStyle.Light, 11, dir);

        public (int Code, string Stdout, string Stderr) Find(params string[] args) =>
            ExeTests.RunSplit(new[] { "find" }.Concat(args).Concat(["--data", Data, "--plugins", PluginsTxt]).ToArray());
    }

    [Fact]
    public void An_EditorID_finds_the_record_its_definer_its_winner_and_the_other_copies()
    {
        using var game = new Game();
        // The CHANGELOG and README example is a real run on these made-up files: set SFMODCHECK_FIND_EXAMPLE to a
        // folder to keep a copy of them (Data\ and Plugins.txt).
        if (Environment.GetEnvironmentVariable("SFMODCHECK_FIND_EXAMPLE") is { Length: > 0 } keep)
        {
            Directory.CreateDirectory(Path.Combine(keep, "Data"));
            foreach (var f in Directory.EnumerateFiles(game.Data)) File.Copy(f, Path.Combine(keep, "Data", Path.GetFileName(f)), true);
            File.Copy(game.PluginsTxt, Path.Combine(keep, "Plugins.txt"), true);
        }
        var (code, stdout, _) = game.Find("fb_misc");   // letter case ignored
        Assert.Equal(0, code);
        Assert.Contains($"FB_Misc [{game.BaseMisc}] MISC \"Made-up Widget\"", stdout);
        Assert.Contains("  defined in: FindBase.esm", stdout);
        Assert.Contains("  winner: FindLight.esm", stdout);
        Assert.Contains("  also in: FindMedium.esm", stdout);
        Assert.Contains("    Value: 9", stdout);   // the winning copy's fields
        Assert.Contains("RESULT: FOUND 1 record (", stdout);
    }

    [Fact]
    public void A_FormKey_finds_the_same_record()
    {
        using var game = new Game();
        var (code, stdout, _) = game.Find(game.BaseMisc.ToString());
        Assert.Equal(0, code);
        Assert.Contains($"FB_Misc [{game.BaseMisc}] MISC", stdout);
        Assert.Contains("  winner: FindLight.esm", stdout);
    }

    // 0.18.0 (GapFix): find --json NOT FOUND carries the same reason as the text RESULT line
    [Fact]
    public void Json_not_found_for_an_FF_console_FormID_names_the_save_game_reason()
    {
        using var game = new Game();
        var (code, stdout, _) = game.Find("FF000123", "--json");
        Assert.Equal(1, code);
        Assert.Contains("FormIDs that start with FF belong to the save game", stdout);
    }

    [Fact]
    public void Console_FormIDs_pick_full_medium_and_light_plugins_by_load_order_index()
    {
        using var game = new Game();
        // FindBase.esm is full plugin 00; FindMedium.esm is medium plugin FD 00; FindLight.esm is light plugin FE 000.
        var (code, stdout, _) = game.Find($"00{game.BaseMisc.ID:X6}");
        Assert.Equal(0, code);
        Assert.Contains($"FB_Misc [{game.BaseMisc}]", stdout);
        (code, stdout, _) = game.Find($"0xFD00{game.MediumDup.ID:X4}");
        Assert.Equal(0, code);
        Assert.Contains($"Dup [{game.MediumDup}] KYWD", stdout);
        (code, stdout, _) = game.Find($"FE000{game.LightDup.ID:X3}");
        Assert.Equal(0, code);
        Assert.Contains($"Dup [{game.LightDup}] KYWD", stdout);
        (code, stdout, _) = game.Find($"FE001{game.LightDup.ID:X3}");   // there is no second light plugin
        Assert.Equal(1, code);
        Assert.Contains("this load order has 1 light plugins, so none has index 1", stdout);
    }

    // code review round 2 (0.16.0): the game loads a .esl file as light whatever its header flags say
    [Fact]
    public void A_esl_file_without_the_light_flag_counts_as_a_light_plugin_for_console_FormIDs()
    {
        using var game = new Game();
        var esl = Plugin.New("FindEsl.esm", PluginStyle.Full, "test", "made-up mod");
        esl.Keywords.AddNew("EslKeyword");
        using (var g = new GameData(game.Data)) PluginWriter.Write(esl, game.Data, g, NoGap);
        File.Move(Path.Combine(game.Data, "FindEsl.esm"), Path.Combine(game.Data, "FindEsl.esl"));
        File.WriteAllLines(game.PluginsTxt, ["*FindBase.esm", "*FindMedium.esm", "*FindEsl.esl", "*FindLight.esm"]);
        // FindEsl.esl is light plugin FE 000, so FindLight.esm is FE 001
        var (code, stdout, _) = game.Find($"FE001{game.LightDup.ID:X3}");
        Assert.Equal(0, code);
        Assert.Contains($"Dup [{game.LightDup}] KYWD", stdout);
    }

    [Fact]
    public void Type_keeps_only_records_of_that_type()
    {
        using var game = new Game();
        Assert.Equal(0, game.Find("FB_Misc", "--type", "misc").Code);
        var (code, stdout, _) = game.Find("FB_Misc", "--type", "KYWD");
        Assert.Equal(1, code);
        Assert.Contains("RESULT: NOT FOUND (no record with EditorID FB_Misc of type KYWD in 3 plugins)", stdout);
        var (bad, _, err) = game.Find("FB_Misc", "--type", "ABCD");
        Assert.Equal(2, bad);
        Assert.Contains("--type needs a Starfield record type", err);
    }

    [Fact]
    public void Not_found_is_exit_1_with_a_hint()
    {
        using var game = new Game();
        var (code, stdout, _) = game.Find("FB_Misk");
        Assert.Equal(1, code);
        Assert.Contains("HINT: EditorIDs must match exactly", stdout);
        Assert.Contains("give its Plugins.txt with --plugins", stdout);
        Assert.Contains("RESULT: NOT FOUND (no record with EditorID FB_Misk in 3 plugins)", stdout);
        (code, stdout, _) = game.Find("000800:NotLoaded.esm");
        Assert.Equal(1, code);
        Assert.Contains("NotLoaded.esm isn't in this load order", stdout);
    }

    [Fact]
    public void Two_records_with_one_EditorID_are_both_printed()
    {
        using var game = new Game();
        var (code, stdout, _) = game.Find("Dup");
        Assert.Equal(0, code);
        Assert.Contains($"Dup [{game.MediumDup}] KYWD", stdout);
        Assert.Contains($"Dup [{game.LightDup}] KYWD", stdout);
        Assert.Contains("RESULT: FOUND 2 records", stdout);
    }

    [Fact]
    public void Json_prints_one_object_with_every_copy()
    {
        using var game = new Game();
        var (code, stdout, _) = game.Find("FB_Misc", "--json");
        Assert.Equal(0, code);
        var j = JsonDocument.Parse(stdout).RootElement;   // nothing else on stdout
        Assert.Equal("found", j.GetProperty("result").GetString());
        Assert.Equal(3, j.GetProperty("loadOrder").GetProperty("plugins").GetInt32());
        var m = Assert.Single(j.GetProperty("matches").EnumerateArray());
        Assert.Equal(game.BaseMisc.ToString(), m.GetProperty("formKey").GetString());
        Assert.Equal("MISC", m.GetProperty("type").GetString());
        Assert.Equal("Made-up Widget", m.GetProperty("name").GetString());
        Assert.Equal("FindBase.esm", m.GetProperty("definedIn").GetString());
        Assert.Equal("FindLight.esm", m.GetProperty("winner").GetString());
        Assert.Equal(["FindBase.esm", "FindMedium.esm", "FindLight.esm"], m.GetProperty("plugins").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal("9", m.GetProperty("fields").GetProperty("Value").GetString());

        (code, stdout, _) = game.Find("Nothing_Here", "--json");
        Assert.Equal(1, code);
        j = JsonDocument.Parse(stdout).RootElement;
        Assert.Equal("not found", j.GetProperty("result").GetString());
        Assert.Empty(j.GetProperty("matches").EnumerateArray());
        Assert.Contains("EditorIDs must match exactly", j.GetProperty("hint").GetString());
    }

    [Fact]
    public void A_plugin_in_the_extra_data_folder_wins_over_Data()
    {
        using var game = new Game();
        var extra = game.Sub("extra");
        game.ExtraMod(extra);
        File.AppendAllLines(game.PluginsTxt, ["*FindExtra.esm"]);
        var (code, stdout, _) = game.Find("FB_Misc", "--extra-data", extra);
        Assert.Equal(0, code);
        Assert.Contains("  winner: FindExtra.esm", stdout);
        Assert.Contains("  also in: FindMedium.esm, FindLight.esm", stdout);
        Assert.Contains("    Value: 11", stdout);
    }

    // ---- 0.20.0: find --full (each fails on 0.19.0, where --full is an unknown option) ----

    /// <summary>FindLvl.esm (master FindBase.esm) with leveled item FL_List: entry 0 is level 0, count 1, FB_Misc;
    /// entry 1 is level 5, count 2, FB_Misc. Added to Plugins.txt.</summary>
    static FormKey AddLeveledList(Game game)
    {
        var mod = Plugin.New("FindLvl.esm", PluginStyle.Full, "test", "made-up leveled list");
        using var g = new GameData(game.Data);
        g.Open("FindBase.esm");
        var list = mod.LeveledItems.AddNew("FL_List");
        list.Entries = [Entry(0, 1), Entry(5, 2)];
        PluginWriter.Write(mod, game.Data, g, NoGap);
        File.AppendAllLines(game.PluginsTxt, ["*FindLvl.esm"]);
        return list.FormKey;

        LeveledItemEntry Entry(short level, short count)
        {
            var e = new LeveledItemEntry { Level = level, Count = count };
            e.Reference.SetTo(game.BaseMisc);
            return e;
        }
    }

    [Fact]
    public void Full_prints_every_nested_value_with_zeros_in_a_load_order()
    {
        using var game = new Game();
        var lvl = AddLeveledList(game);
        var (code, stdout, _) = game.Find("FL_List", "--full");
        Assert.Equal(0, code);
        var lines = stdout.Replace("\r", "").Split('\n');
        Assert.Contains("  tree:", lines);
        Assert.DoesNotContain("  fields:", lines);   // --full replaces the fields block
        int at = Array.IndexOf(lines, "    Entries: (2 entries)");
        Assert.True(at > 0, stdout);
        Assert.Equal("      [0]:", lines[at + 1]);
        Assert.Contains("        Level: 0", lines);   // a zero prints (the fields view drops it)
        Assert.Contains("        Level: 5", lines);
        Assert.Contains("        Count: 2", lines);
        Assert.Contains($"        Reference: FB_Misc [{game.BaseMisc}]", lines);
        Assert.DoesNotContain(lines, l => l.TrimStart().StartsWith("Unused"));   // padding members are left out
        Assert.Contains("RESULT: FOUND 1 record (", stdout);

        // zeros at the top level too: FB_Misc's weight is 0
        (code, stdout, _) = game.Find("FB_Misc", "--full");
        Assert.Equal(0, code);
        Assert.Contains("    Value: 9", stdout);
        Assert.Contains("    Weight: 0", stdout);

        // --json --full: "tree" next to the unchanged "fields"; without --full there is no "tree" key
        (code, stdout, _) = game.Find(lvl.ToString(), "--full", "--json");
        Assert.Equal(0, code);
        var m = Assert.Single(JsonDocument.Parse(stdout).RootElement.GetProperty("matches").EnumerateArray());
        Assert.Equal("(2 entries)", m.GetProperty("fields").GetProperty("Entries").GetString());
        var entries = m.GetProperty("tree").GetProperty("Entries").EnumerateArray().ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal("0", entries[0].GetProperty("Level").GetString());
        Assert.Equal("2", entries[1].GetProperty("Count").GetString());
        Assert.False(m.TryGetProperty("treeNotShown", out _));
        (code, stdout, _) = game.Find(lvl.ToString(), "--json");
        m = Assert.Single(JsonDocument.Parse(stdout).RootElement.GetProperty("matches").EnumerateArray());
        Assert.False(m.TryGetProperty("tree", out _));
    }

    [Fact]
    public void Without_full_the_text_output_is_unchanged()
    {
        using var game = new Game();
        var (code, stdout, _) = game.Find("FB_Misc", "--quiet");
        Assert.Equal(0, code);
        var text = System.Text.RegularExpressions.Regex.Replace(stdout.Replace("\r", ""), @"\(\d+ s\)", "(N s)");
        Assert.Equal(
            "\n" +
            $"FB_Misc [{game.BaseMisc}] MISC \"Made-up Widget\"\n" +
            "  defined in: FindBase.esm\n" +
            "  winner: FindLight.esm\n" +
            "  also in: FindMedium.esm\n" +
            "  fields:\n" +
            "    Value: 9\n" +
            "RESULT: FOUND 1 record (N s)\n", text);
    }

    [Fact]
    public void Full_tree_caps_cut_depth_nodes_and_lines_and_count_what_they_left_out()
    {
        var mod = Plugin.New("CapTest.esm", PluginStyle.Full, "test", "made-up");
        var misc = mod.MiscItems.AddNew("CT_Misc");
        var list = mod.LeveledItems.AddNew("CT_List");
        list.Entries = new Noggog.ExtendedList<LeveledItemEntry>(Enumerable.Range(0, 30).Select(i =>
        {
            var e = new LeveledItemEntry { Level = (short)i, Count = 1 };
            e.Reference.SetTo(misc.FormKey);
            return e;
        }));
        static string Link(Mutagen.Bethesda.Plugins.IFormLinkGetter l) => $"[{l.FormKey}]";
        static int Count(List<TreeNode> ns) => ns.Sum(n => 1 + (n.Children != null ? Count(n.Children) : 0));
        var (depth, lines, nodes) = (FindTree.MaxDepth, FindTree.MaxLines, FindTree.MaxNodes);
        try
        {
            var (all, none) = FindTree.Build(list, Link);
            Assert.Equal(0, none);
            int total = Count(all);
            Assert.Equal(total, FindTree.Lines(all, 0, "").Count);   // one line per node, no cut line

            FindTree.MaxNodes = 10;
            var (cut, notShown) = FindTree.Build(list, Link);
            Assert.Equal(10, Count(cut));
            Assert.Equal(total, Count(cut) + notShown);   // every node either shown or counted
            Assert.True(FindTree.Json(cut).ContainsKey("Entries"));   // the JSON prints from the same cut nodes

            FindTree.MaxNodes = nodes;
            FindTree.MaxLines = 5;
            var text = FindTree.Lines(all, 0, "  ");
            Assert.Equal(6, text.Count);
            Assert.Equal($"  ... and {total - 5} more lines not shown (--json --full has up to 20,000 nodes)", text[^1]);

            FindTree.MaxLines = lines;
            FindTree.MaxDepth = 1;   // Entries is at level 0, each entry at level 1
            var (shallow, _) = FindTree.Build(list, Link);
            var entries = shallow.Single(n => n.Name == "Entries");
            Assert.Equal(30, entries.Children!.Count);
            Assert.All(entries.Children, e => Assert.Equal("(deeper than 1 levels, not shown)", e.Text));
        }
        finally { (FindTree.MaxDepth, FindTree.MaxLines, FindTree.MaxNodes) = (depth, lines, nodes); }
    }

    // ---- 0.20.0: find --versions (each fails on 0.19.0, where --versions is an unknown option) ----

    /// <summary>Three more copies of FB_Misc after FindLight.esm: FindChild.esm (masters FindBase.esm and
    /// FindMedium.esm) sets value 8 on FindMedium.esm's copy; FindIdent.esm (master FindBase.esm) copies FindBase.esm's
    /// copy unchanged; FindPart.esm (master FindBase.esm) is a partial form that carries only value 12.</summary>
    static void AddVersionMods(Game game)
    {
        using (var g = new GameData(game.Data))
        {
            g.Open("FindBase.esm");
            var medium = g.Open("FindMedium.esm");
            var child = Plugin.New("FindChild.esm", PluginStyle.Full, "test", "made-up child of FindMedium");
            child.MiscItems.GetOrAddAsOverride(medium.MiscItems.First()).Value = 8;
            // the record names only FindBase.esm, so FindMedium.esm is listed as a master by hand
            PluginWriter.Write(child, game.Data, g, new WriteOptions { GapCheck = false, FixEffects = false, Masters = [ModKey.FromFileName("FindBase.esm"), ModKey.FromFileName("FindMedium.esm")] });
        }
        using (var g = new GameData(game.Data))
        {
            var baseMod = g.Open("FindBase.esm");
            var ident = Plugin.New("FindIdent.esm", PluginStyle.Full, "test", "made-up identical copy");
            ident.MiscItems.GetOrAddAsOverride(baseMod.MiscItems.First());
            PluginWriter.Write(ident, game.Data, g, NoGap);
        }
        using (var g = new GameData(game.Data))
        {
            g.Open("FindBase.esm");
            var part = Plugin.New("FindPart.esm", PluginStyle.Full, "test", "made-up partial form");
            var misc = new MiscItem(game.BaseMisc, StarfieldRelease.Starfield) { Value = 12 };
            misc.MajorRecordFlagsRaw |= SfModCheck.Conflict.FieldCompare.PartialFormFlag;
            part.MiscItems.Add(misc);
            part.ModHeader.MasterReferences.Add(new MasterReference { Master = ModKey.FromFileName("FindBase.esm") });
            PluginWriter.Write(part, game.Data, g, NoGap);
        }
        File.AppendAllLines(game.PluginsTxt, ["*FindChild.esm", "*FindIdent.esm", "*FindPart.esm"]);
    }

    [Fact]
    public void Versions_lists_every_copy_with_its_changes_and_the_missing_master_notes()
    {
        using var game = new Game();
        AddVersionMods(game);
        var (code, stdout, stderr) = game.Find("FB_Misc", "--versions");
        Assert.True(code == 0, stdout + stderr);
        var lines = stdout.Replace("\r", "").Split('\n');
        int at = Array.IndexOf(lines, "  versions: 6 copies, the last one wins");
        Assert.True(at > 0, stdout);
        Assert.True(Array.IndexOf(lines, "  fields:") is var f && f > 0 && f < at, stdout);   // after the field block
        Assert.Equal("    1. FindBase.esm: defines it", lines[at + 1]);
        Assert.Equal("    2. FindMedium.esm: built on FindBase.esm, 1 change", lines[at + 2]);
        Assert.Equal("         Value: changed: 5 -> 7", lines[at + 3]);
        Assert.Equal("    3. FindLight.esm: built on FindBase.esm, 1 change", lines[at + 4]);
        Assert.Equal("         Value: changed: 5 -> 9", lines[at + 5]);
        Assert.Equal("         note: FindLight.esm doesn't have FindMedium.esm as a master, so it may undo FindMedium.esm's changes (sf-mod-check conflict says which)", lines[at + 6]);
        Assert.True(lines.Contains("    4. FindChild.esm: built on FindMedium.esm, 1 change"), stdout);
        Assert.True(lines.Contains("         Value: changed: 7 -> 8"), stdout);   // against FindMedium.esm, its master
        Assert.True(lines.Contains("    5. FindIdent.esm: built on FindBase.esm, no changes (an identical copy)"), stdout);
        Assert.Contains(lines, l => l.StartsWith("    6. FindPart.esm (winner, partial form): built on FindBase.esm, "));
        Assert.Contains("         Value: changed: 5 -> 12", lines);
        // a partial form answers only for what it carries: no line for the name it leaves out
        Assert.DoesNotContain(lines.SkipWhile(l => !l.StartsWith("    6.")), l => l.Contains("Name:"));
        foreach (var other in new[] { "FindMedium.esm", "FindLight.esm", "FindChild.esm" })
            Assert.Contains($"         note: FindIdent.esm doesn't have {other} as a master, so it may undo {other}'s changes (sf-mod-check conflict says which)", lines);
        Assert.Contains("RESULT: FOUND 1 record, 6 copies (", stdout);

        // --json: "versions" only with the flag
        (code, stdout, _) = game.Find("FB_Misc", "--versions", "--json");
        Assert.Equal(0, code);
        var m = Assert.Single(JsonDocument.Parse(stdout).RootElement.GetProperty("matches").EnumerateArray());
        var vs = m.GetProperty("versions").EnumerateArray().ToList();
        Assert.Equal(6, vs.Count);
        Assert.Equal("defines", vs[0].GetProperty("role").GetString());
        Assert.Equal("FindMedium.esm", vs[3].GetProperty("builtOn").GetString());
        var change = Assert.Single(vs[3].GetProperty("changes").EnumerateArray());
        Assert.Equal("Value", change.GetProperty("field").GetString());
        Assert.Equal("7", change.GetProperty("before").GetString());
        Assert.Equal("8", change.GetProperty("after").GetString());
        Assert.Equal("identical", vs[4].GetProperty("noChanges").GetString());
        Assert.Equal(["FindMedium.esm", "FindLight.esm", "FindChild.esm"], vs[4].GetProperty("missingMasters").EnumerateArray().Select(x => x.GetString()));
        Assert.True(vs[5].GetProperty("winner").GetBoolean());
        Assert.True(vs[5].GetProperty("partial").GetBoolean());
        (code, stdout, _) = game.Find("FB_Misc", "--json");
        m = Assert.Single(JsonDocument.Parse(stdout).RootElement.GetProperty("matches").EnumerateArray());
        Assert.False(m.TryGetProperty("versions", out _));
        // without the flag the RESULT line has no copies clause
        (_, stdout, _) = game.Find("FB_Misc");
        Assert.DoesNotContain("copies", stdout);
    }

    [Fact]
    public void Versions_and_conflict_pick_the_same_base_for_every_field()
    {
        using var game = new Game();
        AddVersionMods(game);
        var lo = LoadOrderSetup.Build(game.Data, game.PluginsTxt);
        try
        {
            var copies = new List<(PluginInfo, Mutagen.Bethesda.Plugins.Records.IMajorRecordGetter)>();
            foreach (var p in lo.Plugins)
                foreach (var r in p.Mod.EnumerateMajorRecords())
                    if (r.FormKey == game.BaseMisc) copies.Add((p, r));
            var vs = RecordVersions.Compare(game.BaseMisc, copies, lo.Plugins, out var differ);
            Assert.False(differ);
            int compared = 0;
            foreach (var v in vs.Skip(1))
            {
                var analysis = SfModCheck.Conflict.Analyzer.Run(lo.Plugins, only: new HashSet<string> { v.Plugin });
                var conflictChanges = analysis.Changes.Where(c => c.FormKey == game.BaseMisc && c.Plugin == v.Plugin).ToList();
                // the same fields changed, each against the same plugin's copy
                Assert.Equal(conflictChanges.Select(c => (c.Field, c.Base)).OrderBy(x => x.Field),
                             v.Changes.Select(c => (c.Field, c.Base)).Distinct().OrderBy(x => x.Field));
                compared += conflictChanges.Count;
            }
            Assert.Equal(4, compared);   // Medium, Light, Child and Part each change Value; Ident changes nothing
        }
        finally { foreach (var p in lo.Plugins) p.Mod.Dispose(); }
    }

    /// <summary>The --refs mods (0.20.0), all pointing at FB_Misc. FindRefA.esm (master FindBase.esm): the cell RA_Cell
    /// with a reference whose base is FB_Misc, and three form lists that hold FB_Misc (RA_ListKept, RA_ListWalk,
    /// RA_ListDropped). FindRefC.esm has only FindRefA.esm as a master and empties RA_ListDropped (it is read for
    /// FormKeys only). FindRefD.esm (masters FindBase.esm and FindRefA.esm) keeps RA_ListKept's link and swaps
    /// RA_ListWalk's for FB_Keyword. Returns the reference's and the cell's FormKeys.</summary>
    static (FormKey Ref, FormKey Cell, FormKey Kept) AddRefMods(Game game)
    {
        FormKey refKey, cellKey, kept;
        using (var g = new GameData(game.Data))
        {
            var baseMod = g.Open("FindBase.esm");
            var a = Plugin.New("FindRefA.esm", PluginStyle.Full, "test", "made-up referrers");
            var misc = baseMod.MiscItems.First();
            var cell = new Cell(a) { EditorID = "RA_Cell" };
            var placed = new PlacedObject(a) { Base = misc.ToLink<IPlaceableObjectGetter>() };
            cell.Temporary.Add(placed);
            a.Cells.Add(new CellBlock { SubBlocks = new Noggog.ExtendedList<CellSubBlock> { new CellSubBlock { Cells = new Noggog.ExtendedList<Cell> { cell } } } });
            foreach (var n in new[] { "RA_ListKept", "RA_ListWalk", "RA_ListDropped" })
                a.FormLists.AddNew(n).Items.Add(misc.ToLink<IStarfieldMajorRecordGetter>());
            kept = a.FormLists.First(l => l.EditorID == "RA_ListKept").FormKey;
            (refKey, cellKey) = (placed.FormKey, cell.FormKey);
            PluginWriter.Write(a, game.Data, g, NoGap);
        }
        using (var g = new GameData(game.Data))
        {
            var a = g.Open("FindRefA.esm");
            var c = Plugin.New("FindRefC.esm", PluginStyle.Light, "test", "made-up override without the target's plugin");
            c.FormLists.GetOrAddAsOverride(a.FormLists.First(l => l.EditorID == "RA_ListDropped")).Items.Clear();
            PluginWriter.Write(c, game.Data, g, NoGap);
        }
        using (var g = new GameData(game.Data))
        {
            var baseMod = g.Open("FindBase.esm");
            var a = g.Open("FindRefA.esm");
            var d = Plugin.New("FindRefD.esm", PluginStyle.Full, "test", "made-up later referrers");
            d.FormLists.GetOrAddAsOverride(a.FormLists.First(l => l.EditorID == "RA_ListKept"));
            var walk = d.FormLists.GetOrAddAsOverride(a.FormLists.First(l => l.EditorID == "RA_ListWalk"));
            walk.Items.Clear();
            walk.Items.Add(baseMod.Keywords.First().ToLink<IStarfieldMajorRecordGetter>());
            PluginWriter.Write(d, game.Data, g, new WriteOptions { GapCheck = false, FixEffects = false, Masters = [ModKey.FromFileName("FindBase.esm"), ModKey.FromFileName("FindRefA.esm")] });
        }
        File.AppendAllLines(game.PluginsTxt, ["*FindRefA.esm", "*FindRefC.esm", "*FindRefD.esm"]);
        return (refKey, cellKey, kept);
    }

    [Fact]
    public void Refs_counts_a_reference_not_its_cell_and_only_winning_copies_that_keep_the_link()
    {
        using var game = new Game();
        var (refKey, cellKey, kept) = AddRefMods(game);
        var (code, stdout, stderr) = game.Find("FB_Misc", "--refs");
        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("Reading the links in 6 plugins (5 of them can point at it)", stderr);
        var lines = stdout.Replace("\r", "").Split('\n');
        int at = Array.IndexOf(lines, "  referenced by: 2 records (winning copies)");
        Assert.True(at > 0, stdout);
        Assert.True(Array.IndexOf(lines, "  fields:") is var f && f > 0 && f < at, stdout);   // after the field block
        Assert.Equal($"    RA_ListKept [{kept}] FLST, winner FindRefD.esm", lines[at + 1]);
        Assert.Equal($"    (no EditorID) [{refKey}] REFR in RA_Cell [{cellKey}], winner FindRefA.esm", lines[at + 2]);
        // RA_ListDropped (FindRefC.esm, FormKeys only) and RA_ListWalk (FindRefD.esm) lost the link in a later copy
        Assert.Equal("    note: 2 more records point at it only in a copy a later plugin overrides (--verbose names them)", lines[at + 3]);
        Assert.DoesNotContain(lines, l => l.Contains("RA_ListWalk") || l.Contains("RA_ListDropped") || l.StartsWith("    RA_Cell"));
        Assert.Contains("RESULT: FOUND 1 record; 2 records point at it (", stdout);

        // --verbose names the overridden ones; --versions and --refs combine, versions first
        (code, stdout, stderr) = game.Find("FB_Misc", "--refs", "--versions", "--verbose");
        Assert.Equal(0, code);
        Assert.Contains("pointed at it in FindRefA.esm; FindRefC.esm's copy overrides it without the link", stderr);
        Assert.Contains("only in FindRefA.esm's copy; FindRefD.esm's copy wins without the link", stderr);
        Assert.Contains("refs: FindRefC.esm (5/6): 1 records (FormKeys only)", stderr);
        Assert.True(stdout.IndexOf("  versions:", StringComparison.Ordinal) < stdout.IndexOf("  referenced by:", StringComparison.Ordinal), stdout);
        Assert.Contains("RESULT: FOUND 1 record, 3 copies; 2 records point at it (", stdout);

        // --max limits the lines, not the count
        (code, stdout, _) = game.Find("FB_Misc", "--refs", "--max", "1");
        Assert.Equal(0, code);
        Assert.Contains("  referenced by: 2 records (winning copies)", stdout);
        Assert.Contains("    ... and 1 more (--max 0 lists them all)", stdout);
        Assert.DoesNotContain("REFR in RA_Cell", stdout);

        // --json: "refs" only with the flag
        (code, stdout, _) = game.Find("FB_Misc", "--refs", "--json", "--max", "0");
        Assert.Equal(0, code);
        var m = Assert.Single(JsonDocument.Parse(stdout).RootElement.GetProperty("matches").EnumerateArray());
        var refs = m.GetProperty("refs");
        Assert.Equal(2, refs.GetProperty("total").GetInt32());
        Assert.Equal(2, refs.GetProperty("listed").GetInt32());
        Assert.Equal(2, refs.GetProperty("onlyInOverridden").GetInt32());
        var rs = refs.GetProperty("records").EnumerateArray().ToList();
        Assert.Equal("REFR", rs[1].GetProperty("type").GetString());
        Assert.Equal(cellKey.ToString(), rs[1].GetProperty("in").GetString());
        Assert.Equal(JsonValueKind.Null, rs[0].GetProperty("in").ValueKind);
        Assert.Equal("FindRefD.esm", rs[0].GetProperty("winner").GetString());
        (_, stdout, _) = game.Find("FB_Misc", "--json");
        m = Assert.Single(JsonDocument.Parse(stdout).RootElement.GetProperty("matches").EnumerateArray());
        Assert.False(m.TryGetProperty("refs", out _));

        // a record nothing points at is still found
        (code, stdout, _) = game.Find("FB_Keyword", "--refs");
        Assert.Equal(0, code);
        Assert.Contains("  referenced by: 1 record (winning copies)", stdout);   // RA_ListWalk's FindRefD.esm copy
        Assert.Contains("RESULT: FOUND 1 record; 1 record points at it (", stdout);

        // --max without the listing or --refs is still refused
        (code, stdout, stderr) = game.Find("FB_Misc", "--max", "3");
        Assert.Equal(2, code);
        Assert.Contains("or the referring records --refs lists", stderr);
    }

    [Fact]
    public void Refs_finds_a_link_held_in_a_script_struct_member()
    {
        using var game = new Game();
        using (var g = new GameData(game.Data))
        {
            var misc = g.Open("FindBase.esm").MiscItems.First();
            var s = Plugin.New("FindRefS.esm", PluginStyle.Full, "test", "made-up quests with struct properties");
            ScriptObjectProperty Target() => new() { Name = "Target", Object = new FormLink<IStarfieldMajorRecordGetter>(misc.FormKey) };
            var q = s.Quests.AddNew("RS_Quest");
            q.VirtualMachineAdapter = new QuestAdapter
            {
                Scripts = { new ScriptEntry { Name = "SfmTest:Holder", Properties =
                    { new ScriptStructListProperty { Name = "Stages", Structs = { new ScriptEntryStructs { Members = { Target() } } } } } } },
            };
            var p = s.Quests.AddNew("RS_Plain");
            p.VirtualMachineAdapter = new QuestAdapter
            {
                Scripts = { new ScriptEntry { Name = "SfmTest:Holder2", Properties =
                    { new ScriptStructProperty { Name = "One", Members = { new ScriptEntry { Properties = { Target() } } } } } } },
            };
            PluginWriter.Write(s, game.Data, g, new WriteOptions { GapCheck = false, FixEffects = false, Masters = [ModKey.FromFileName("FindBase.esm")] });
        }
        File.AppendAllLines(game.PluginsTxt, ["*FindRefS.esm"]);
        var (code, stdout, stderr) = game.Find("FB_Misc", "--refs");
        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("  referenced by: 2 records (winning copies)", stdout);
        Assert.Contains("RS_Quest [", stdout);
        Assert.Contains("RS_Plain [", stdout);
    }

    [Fact]
    public void Refs_stops_past_its_memory_limit_and_restores_the_old_limit()
    {
        using var game = new Game();
        var lo = LoadOrderSetup.Build(game.Data, game.PluginsTxt);
        var (maxBefore, bytesBefore) = (Analyzer.MaxManagedBytes, Analyzer.ManagedBytes);
        try
        {
            Analyzer.MaxManagedBytes = 123;
            Analyzer.ManagedBytes = () => Analyzer.Where.Contains("for --refs") ? long.MaxValue : 0;
            var unread = new List<string>();
            var e = Assert.Throws<MemoryLimitException>(() => RecordRefs.Scan([game.BaseMisc], lo.Plugins, unread));
            Assert.StartsWith("memory passed 16.0 GB while reading links in ", e.Message);
            Assert.Equal(123, Analyzer.MaxManagedBytes);
        }
        finally
        {
            (Analyzer.MaxManagedBytes, Analyzer.ManagedBytes) = (maxBefore, bytesBefore);
            foreach (var p in lo.Plugins) p.Mod.Dispose();
        }
    }

    [Fact]
    public void Bad_input_is_exit_2_and_help_is_exit_0()
    {
        using var game = new Game();
        var (code, stdout, _) = ExeTests.RunSplit("find", "--help");
        Assert.Equal(0, code);
        Assert.StartsWith("usage: sf-mod-check find", stdout);
        (code, stdout, _) = ExeTests.RunSplit("find", "FB_Misc", "--data", game.Data, "--plugins", Path.Combine(game.Dir, "missing.txt"));
        Assert.Equal(2, code);
        Assert.Contains("RESULT: CANNOT CHECK (the Plugins.txt", stdout);
        (code, stdout, _) = ExeTests.RunSplit("find", "--data", game.Data, "--plugins", game.PluginsTxt);
        Assert.Equal(2, code);
        Assert.Contains("RESULT: CANNOT CHECK (no record given)", stdout);
        (code, stdout, _) = ExeTests.RunSplit("find", "FB_Misc", "--json", "--data", Path.Combine(game.Dir, "nope"), "--plugins", game.PluginsTxt);
        Assert.Equal(2, code);
        Assert.Equal("cannot run", JsonDocument.Parse(stdout).RootElement.GetProperty("result").GetString());
    }
}
