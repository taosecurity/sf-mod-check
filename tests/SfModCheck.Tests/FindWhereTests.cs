// sf-mod-check 0.27.0: find --where and --editorid-match (FindWhere.cs; the 0.27.0 design notes, parts B and D). All
// plugins are made up: Base.esm holds three NPCs, a race, two keywords, two factions and two misc items; A.esm changes
// two of the NPCs (one loses its Unique flag, one is a partial form without a name). No game files are needed.
using System.Text.Json;
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Starfield;
using Noggog;
using Xunit;

namespace SfModCheck.Tests;

/// <summary>The made-up game, built once for every --where test (the runs only read it).</summary>
public sealed class WhereGame : Sandbox
{
    static readonly WriteOptions NoGap = new() { GapCheck = false, FixEffects = false };
    public readonly string Data, BasePath;
    public readonly FormKey Boss, Plain, Later, Race, KOne, Crime, Coin, Gem;

    public WhereGame()
    {
        Data = Sub("Data");
        var b = Plugin.New("Base.esm", PluginStyle.Full, "test", "made-up base game");
        var race = b.Races.AddNew("W_Race");
        var kOne = b.Keywords.AddNew("W_KeyOne");
        var kTwo = b.Keywords.AddNew("W_KeyTwo");
        var crime = b.Factions.AddNew("W_CrimeFaction");
        var other = b.Factions.AddNew("W_OtherFaction");
        var boss = b.Npcs.AddNew("W_Boss");
        boss.Name = "Big Boss";
        boss.Flags = Npc.Flag.Unique | Npc.Flag.Essential;
        boss.Race.SetTo(race);
        boss.Keywords = new ExtendedList<IFormLinkGetter<IKeywordGetter>> { kOne.ToLink<IKeywordGetter>() };
        boss.Factions.Add(new RankPlacement { Faction = crime.ToLink<IFactionGetter>(), Rank = 1 });
        boss.Factions.Add(new RankPlacement { Faction = other.ToLink<IFactionGetter>(), Rank = 0 });
        var plain = b.Npcs.AddNew("W_Plain");
        plain.Race.SetTo(race);
        plain.Keywords = new ExtendedList<IFormLinkGetter<IKeywordGetter>> { kTwo.ToLink<IKeywordGetter>() };
        plain.Factions.Add(new RankPlacement { Faction = other.ToLink<IFactionGetter>(), Rank = 2 });
        var later = b.Npcs.AddNew("W_Later");
        later.Name = "Later Boss";
        later.Flags = Npc.Flag.Unique;
        var coin = b.MiscItems.AddNew("W_Coin");
        coin.Value = 10;
        coin.Weight = 0.1f;
        coin.Name = "Gold Coin";
        var gem = b.MiscItems.AddNew("W_Gem");
        gem.Value = 250;
        gem.Weight = 0.5f;
        (Boss, Plain, Later, Race, KOne, Crime, Coin, Gem) = (boss.FormKey, plain.FormKey, later.FormKey, race.FormKey, kOne.FormKey, crime.FormKey, coin.FormKey, gem.FormKey);
        using (var g = new GameData(Data)) PluginWriter.Write(b, Data, g, NoGap);
        BasePath = Path.Combine(Data, "Base.esm");

        // A.esm: W_Later loses its Unique flag; W_Boss is overridden as a partial form that leaves out its name
        using (var g = new GameData(Data))
        {
            var cache = g.Open("Base.esm").ToImmutableLinkCache();
            var a = Plugin.New("A.esm", PluginStyle.Light, "test", "made-up mod A");
            var l = (Npc)cache.ResolveAllContexts<INpc, INpcGetter>(Later).First().GetOrAddAsOverride(a);
            l.Flags = 0;
            var bo = (Npc)cache.ResolveAllContexts<INpc, INpcGetter>(Boss).First().GetOrAddAsOverride(a);
            bo.Name = null;
            bo.MajorRecordFlagsRaw |= 0x4000;
            PluginWriter.Write(a, Data, g, NoGap);
        }
    }

    public string PluginsTxt()
    {
        var txt = Path.Combine(Dir, "Plugins.txt");
        File.WriteAllLines(txt, ["*Base.esm", "*A.esm"]);
        return txt;
    }

    public (int Code, string Stdout, string Stderr) Run(params string[] args) =>
        ExeTests.RunSplit(["find", .. args, "--data", Data, "--plugins", PluginsTxt(), "--quiet"]);
}

public class FindWhereTests(WhereGame game) : IClassFixture<WhereGame>
{
    /// <summary>The EditorIDs of the records listed (the lines that start a match), in order.</summary>
    static List<string> Listed(string stdout) =>
        stdout.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith("W_", StringComparison.Ordinal)).Select(l => l.Split(' ')[0]).ToList();

    (int Code, List<string> Ids, string Out) Where(params string[] args)
    {
        var (code, stdout, stderr) = game.Run(args);
        return (code, Listed(stdout), stdout + stderr);
    }

    [Fact]
    public void Text_fields_compare_with_equals_contains_and_not_equals_letter_case_ignored()
    {
        var (code, ids, o) = Where("--type", "NPC_", "--where", "EditorID = w_plain");
        Assert.True(code == 0, o);
        Assert.Equal(["W_Plain"], ids);
        (_, ids, o) = Where("--type", "NPC_", "--where", "EditorID ~ A");
        Assert.Equal(["W_Plain", "W_Later"], ids);
        // != counts an empty field as not equal: W_Plain has no name
        (_, ids, o) = Where("--type", "NPC_", "--where", "Name != \"Big Boss\"");
        Assert.Equal(["W_Plain", "W_Later"], ids);   // by the winning plugin, then FormKey
    }

    [Fact]
    public void Numbers_compare_as_numbers_and_floats_match_their_written_value()
    {
        var (code, ids, o) = Where("--type", "MISC", "--where", "Value = 10.0");
        Assert.True(code == 0, o);
        Assert.Equal(["W_Coin"], ids);
        (_, ids, o) = Where("--type", "MISC", "--where", "Weight = 0.1");
        Assert.Equal(["W_Coin"], ids);
        (_, ids, o) = Where("--type", "MISC", "--where", "Value ~ 25");
        Assert.Equal(["W_Gem"], ids);
        Assert.Contains("  Value: 250", o);
    }

    [Fact]
    public void A_link_matches_by_EditorID_FormKey_console_FormID_or_as_find_prints_it()
    {
        foreach (var v in new[] { "w_race", Game(game.Race), $"{game.Race.ID:X8}", $"W_Race [{game.Race}]" })
        {
            var (code, ids, o) = Where("--type", "NPC_", "--where", $"Race = {v}");
            Assert.True(code == 0, $"{v}: {o}");
            Assert.Equal(["W_Plain", "W_Boss"], ids);   // W_Boss is won by A.esm, later in the load order
        }
        var (_, ids2, o2) = Where("--type", "NPC_", "--where", "Race ~ Rac");
        Assert.Equal(["W_Plain", "W_Boss"], ids2);
        Assert.Contains($"  Race: W_Race [{game.Race}]", o2);
        static string Game(FormKey k) => k.ToString();
    }

    [Fact]
    public void Has_tests_a_flag_and_any_list_entry()
    {
        // W_Later is Unique in Base.esm, but A.esm (the winner) clears its flags: only the winning copy counts
        var (code, ids, o) = Where("--type", "NPC_", "--where", "Flags has unique");
        Assert.True(code == 0, o);
        Assert.Equal(["W_Boss"], ids);
        Assert.Contains("  Flags: Essential, Unique", o);
        (_, ids, _) = Where("--type", "NPC_", "--where", "Flags ~ Essential");
        Assert.Equal(["W_Boss"], ids);
        (_, ids, _) = Where("--type", "NPC_", "--where", "Keywords has W_KeyTwo");
        Assert.Equal(["W_Plain"], ids);
        (_, ids, _) = Where("--type", "NPC_", "--where", $"Keywords = {game.KOne}");
        Assert.Equal(["W_Boss"], ids);
    }

    [Fact]
    public void The_plugin_alone_tests_its_own_copy()
    {
        // with --plugin Base.esm the load order is Base.esm alone, so W_Later's Unique copy is the winner there
        var (code, stdout, stderr) = ExeTests.RunSplit("find", "--type", "NPC_", "--where", "Flags has Unique", "--plugin", game.BasePath, "--data", game.Data, "--quiet");
        Assert.True(code == 0, stdout + stderr);
        Assert.Equal(["W_Boss", "W_Later"], Listed(stdout));
    }

    [Fact]
    public void Exists_skips_empty_fields_and_a_partial_form_keeps_the_earlier_value()
    {
        // A.esm's partial W_Boss leaves out its name: the name from Base.esm still counts
        var (code, ids, o) = Where("--type", "NPC_", "--where", "Name exists");
        Assert.True(code == 0, o);
        Assert.Equal(["W_Boss", "W_Later"], ids.Order().ToList());
        (_, ids, o) = Where("--type", "NPC_", "--where", "Name = big boss");
        Assert.Equal(["W_Boss"], ids);
        Assert.Contains("(NPC_, winner A.esm)", o);
    }

    [Fact]
    public void A_dotted_path_into_a_list_matches_when_any_entry_does()
    {
        var (code, ids, o) = Where("--type", "NPC_", "--where", "Factions.Faction = W_CrimeFaction");
        Assert.True(code == 0, o);
        Assert.Equal(["W_Boss"], ids);
        (_, ids, _) = Where("--type", "NPC_", "--where", "factions.rank = 2");
        Assert.Equal(["W_Plain"], ids);
        (_, ids, o) = Where("--type", "NPC_", "--where", "Factions.Faction ~ Faction", "--where", "Flags has Essential");
        Assert.Equal(["W_Boss"], ids);   // several --where are all required
        Assert.Contains($"  Factions.Faction: W_CrimeFaction [{game.Crime}]; W_OtherFaction", o);
    }

    [Fact]
    public void Unknown_fields_parts_and_flags_are_refused_before_any_plugin_is_read()
    {
        // the Data folder doesn't exist, so reaching the load order would fail with another message
        var missing = Path.Combine(game.Dir, "NoSuchData");
        var (code, stdout, stderr) = ExeTests.RunSplit("find", "--type", "NPC_", "--where", "Flagz has Unique", "--data", missing, "--quiet");
        Assert.Equal(2, code);
        Assert.Contains("NPC_ has no field \"Flagz\". Its fields: ", stderr);
        Assert.Contains(", Factions, ", stderr);
        Assert.DoesNotContain("Unused", stderr);   // padding isn't a field
        (code, _, stderr) = ExeTests.RunSplit("find", "--type", "NPC_", "--where", "Factions.Fction = X", "--data", missing, "--quiet");
        Assert.Equal(2, code);
        Assert.Contains("Factions has no part \"Fction\". Its parts: Faction, Rank", stderr);
        (code, _, stderr) = ExeTests.RunSplit("find", "--type", "NPC_", "--where", "Flags has Uniq", "--data", missing, "--quiet");
        Assert.Equal(2, code);
        Assert.Contains("Flags has no flag \"Uniq\". Its flags: ", stderr);
        (code, _, stderr) = ExeTests.RunSplit("find", "--type", "NPC_", "--where", "Name has Boss", "--data", missing, "--quiet");
        Assert.Equal(2, code);
        Assert.Contains("has works on flags and lists; Name is text, use = or ~", stderr);
        (code, _, stderr) = ExeTests.RunSplit("find", "--type", "NPC_", "--where", "Name.Part = X", "--data", missing, "--quiet");
        Assert.Equal(2, code);
        Assert.Contains("Name is text and has no parts", stderr);
    }

    [Fact]
    public void A_filter_that_breaks_the_grammar_is_refused()
    {
        foreach (var w in new[] { "Flags", "Flags >> Unique", "Flags has", "Name exists now", "Name = \"\"", "Na-me = x" })
        {
            var (code, stdout, stderr) = game.Run("--type", "NPC_", "--where", w);
            Assert.True(code == 2, $"{w}: {stdout}{stderr}");
            Assert.Contains("--where needs \"<field> <op> <value>\"", stderr);
        }
    }

    [Fact]
    public void The_option_mixes_that_dont_fit_are_refused()
    {
        var (code, _, stderr) = game.Run("W_Boss", "--type", "NPC_", "--where", "Name exists");
        Assert.Equal(2, code);
        Assert.Contains("--where lists every record that matches; leave out the record (W_Boss), or leave out --where.", stderr);
        (code, _, stderr) = game.Run("--where", "Name exists");
        Assert.Equal(2, code);
        Assert.Contains("--where needs --type <SIG>", stderr);
        foreach (var opt in new[] { "--full", "--versions", "--refs", "--layout" })
        {
            (code, _, stderr) = game.Run("--type", "NPC_", "--where", "Name exists", opt);
            Assert.Equal(2, code);
            Assert.Contains($"{opt} doesn't go with --where", stderr);
        }
        (code, _, stderr) = game.Run("--editorid-match", "W_(");
        Assert.Equal(2, code);
        Assert.Contains("--editorid-match needs a .NET regular expression", stderr);
    }

    [Fact]
    public void EditorID_match_works_alone_with_a_type_and_with_where()
    {
        var (code, ids, o) = Where("--editorid-match", "^w_(boss|LATER)$");
        Assert.True(code == 0, o);
        Assert.Equal(["W_Boss", "W_Later"], ids);
        Assert.Contains("W_Later [", o);
        (_, ids, _) = Where("--editorid-match", "^W_(Key|Gem)");
        Assert.Equal(["W_KeyOne", "W_KeyTwo", "W_Gem"], ids);
        (_, ids, _) = Where("--editorid-match", "^W_", "--type", "MISC");
        Assert.Equal(["W_Coin", "W_Gem"], ids);
        (_, ids, _) = Where("--editorid-match", "Boss", "--type", "NPC_", "--where", "Flags has Unique");
        Assert.Equal(["W_Boss"], ids);
    }

    [Fact]
    public void Max_caps_the_list_and_no_match_is_exit_1()
    {
        var (code, ids, o) = Where("--editorid-match", "^W_", "--max", "2");
        Assert.True(code == 0, o);
        Assert.Equal(2, ids.Count);
        Assert.Contains("... and 8 more (--max 0 lists them all)", o);
        Assert.Contains("10 records match", o);
        (code, ids, o) = Where("--type", "NPC_", "--where", "Name = Nobody");
        Assert.Equal(1, code);
        Assert.Empty(ids);
        Assert.Contains("RESULT: NOT FOUND (no NPC_ where Name = Nobody in 2 plugins)", o);
    }

    [Fact]
    public void The_JSON_lists_the_filters_and_each_match_with_its_field_values()
    {
        var (code, stdout, stderr) = game.Run("--type", "NPC_", "--where", "Flags has Unique", "--where", "Keywords exists", "--json");
        Assert.True(code == 0, stdout + stderr);
        using var doc = JsonDocument.Parse(stdout);
        var r = doc.RootElement;
        Assert.Equal("sf-mod-check find", r.GetProperty("tool").GetString());
        Assert.Equal("NPC_", r.GetProperty("type").GetString());
        Assert.Equal("found", r.GetProperty("result").GetString());
        Assert.Equal(1, r.GetProperty("total").GetInt32());
        Assert.Equal(1, r.GetProperty("listed").GetInt32());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("editorIdMatch").ValueKind);
        var w = r.GetProperty("where");
        Assert.Equal(2, w.GetArrayLength());
        Assert.Equal("Flags", w[0].GetProperty("field").GetString());
        Assert.Equal("has", w[0].GetProperty("op").GetString());
        Assert.Equal("Unique", w[0].GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.Null, w[1].GetProperty("value").ValueKind);
        var m = r.GetProperty("matches")[0];
        Assert.Equal(game.Boss.ToString(), m.GetProperty("formKey").GetString());
        Assert.Equal("W_Boss", m.GetProperty("editorId").GetString());
        Assert.Equal("NPC_", m.GetProperty("type").GetString());
        Assert.Equal("A.esm", m.GetProperty("winner").GetString());
        Assert.Equal("Essential, Unique", m.GetProperty("fields").GetProperty("Flags").GetString());
        Assert.StartsWith("W_KeyOne [", m.GetProperty("fields").GetProperty("Keywords").GetString());
        Assert.Equal(0, r.GetProperty("unread").GetArrayLength());
    }

    [Fact]
    public void Record_types_map_to_their_Mutagen_classes_and_unnamed_flag_bits_print_in_hex()
    {
        Assert.Equal(typeof(Npc), FindWhere.Types.Value["NPC_"].Class);
        Assert.Equal(typeof(INpcGetter), FindWhere.Types.Value["NPC_"].Getter);
        Assert.Equal(typeof(MiscItem), FindWhere.Types.Value["MISC"].Class);
        Assert.Equal(typeof(Cell), FindWhere.Types.Value["CELL"].Class);
        Assert.Equal("Essential, Unique, 0x40000000", FindWhere.EnumText((Npc.Flag)0x40000022));
        Assert.Equal("Essential, Unique", FindWhere.EnumText(Npc.Flag.Unique | Npc.Flag.Essential));
    }
}
