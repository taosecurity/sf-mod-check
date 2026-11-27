// sf-mod-check 0.27.0 code review round 1: find --where and --editorid-match on a partial form without an EditorID, a
// deleted winner, and a form list (whose entries link to any record type). All plugins are made up: Base.esm holds a
// keyword, an NPC, two misc items and a form list; A.esm overrides the NPC as a partial form without an EditorID or a
// name, and deletes one misc item. Round 2 adds an NPC that A.esm renames with a partial form and a keyword that A.esm
// overrides as a partial form without an EditorID, linked from a misc item. No game files are needed.
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Starfield;
using Xunit;

namespace SfModCheck.Tests;

public sealed class WhereReviewGame : Sandbox
{
    static readonly WriteOptions NoGap = new() { GapCheck = false, FixEffects = false };
    public readonly string Data;
    public readonly FormKey Key, Part, Kept, Gone, List, Ren, PKey;

    public WhereReviewGame()
    {
        Data = Sub("Data");
        var b = Plugin.New("Base.esm", PluginStyle.Full, "test", "made-up base game");
        var key = b.Keywords.AddNew("R_Key");
        b.Keywords.AddNew("R_" + new string('a', 64));   // for the regex timeout
        var part = b.Npcs.AddNew("R_Part");
        part.Name = "Part Guy";
        var kept = b.MiscItems.AddNew("R_Kept");
        kept.Value = 5;
        var gone = b.MiscItems.AddNew("R_Gone");
        gone.Value = 7;
        var list = b.FormLists.AddNew("R_List");
        list.Items.Add(key.ToLink<IStarfieldMajorRecordGetter>());
        (Key, Part, Kept, Gone, List) = (key.FormKey, part.FormKey, kept.FormKey, gone.FormKey, list.FormKey);
        // round 2: an NPC that A.esm renames with a partial form, and a keyword that A.esm overrides as a partial form
        // without an EditorID, which a misc item's (typed) Keywords list links to
        var ren = b.Npcs.AddNew("R_Ren");
        ren.Name = "Ren Guy";
        var pkey = b.Keywords.AddNew("R_PKey");
        var tagged = b.MiscItems.AddNew("R_Tagged");
        tagged.Keywords = [pkey.ToLink<IKeywordGetter>()];
        (Ren, PKey) = (ren.FormKey, pkey.FormKey);
        using (var g = new GameData(Data)) PluginWriter.Write(b, Data, g, NoGap);

        using (var g = new GameData(Data))
        {
            var cache = g.Open("Base.esm").ToImmutableLinkCache();
            var a = Plugin.New("A.esm", PluginStyle.Light, "test", "made-up mod A");
            var p = (Npc)cache.ResolveAllContexts<INpc, INpcGetter>(Part).First().GetOrAddAsOverride(a);
            p.EditorID = null;
            p.Name = null;
            p.MajorRecordFlagsRaw |= 0x4000;
            var d = (MiscItem)cache.ResolveAllContexts<IMiscItem, IMiscItemGetter>(Gone).First().GetOrAddAsOverride(a);
            d.IsDeleted = true;
            var r = (Npc)cache.ResolveAllContexts<INpc, INpcGetter>(Ren).First().GetOrAddAsOverride(a);
            r.EditorID = "R_Renamed";
            r.Name = null;
            r.MajorRecordFlagsRaw |= 0x4000;
            var k = (Keyword)cache.ResolveAllContexts<IKeyword, IKeywordGetter>(PKey).First().GetOrAddAsOverride(a);
            k.EditorID = null;
            k.MajorRecordFlagsRaw |= 0x4000;
            PluginWriter.Write(a, Data, g, NoGap);
        }
    }

    public (int Code, string Stdout, string Stderr) Run(params string[] args)
    {
        var txt = Path.Combine(Dir, "Plugins.txt");
        File.WriteAllLines(txt, ["*Base.esm", "*A.esm"]);
        return ExeTests.RunSplit(["find", .. args, "--data", Data, "--plugins", txt, "--quiet"]);
    }
}

public class FindWhereReviewTests(WhereReviewGame game) : IClassFixture<WhereReviewGame>
{
    [Fact]
    public void A_partial_winner_without_an_EditorID_keeps_the_earlier_EditorID_and_name_and_is_the_winner()
    {
        var (code, o, e) = game.Run("--editorid-match", "^R_Part$");
        Assert.True(code == 0, o + e);
        Assert.Contains($"R_Part [{game.Part}] \"Part Guy\" (NPC_, winner A.esm)", o);
        (code, o, e) = game.Run("--type", "NPC_", "--editorid-match", "^R_Part$");
        Assert.True(code == 0, o + e);
        Assert.Contains($"R_Part [{game.Part}] \"Part Guy\" (NPC_, winner A.esm)", o);
        (_, o, _) = game.Run("--editorid-match", "^R_Part$", "--json");
        Assert.Contains("\"editorId\": \"R_Part\"", o);
        Assert.Contains("\"winner\": \"A.esm\"", o);
    }

    [Fact]
    public void A_deleted_winner_is_never_a_match()
    {
        var (code, o, e) = game.Run("--type", "MISC", "--where", "Value != 0");
        Assert.True(code == 0, o + e);
        Assert.Contains("R_Kept [", o);
        Assert.DoesNotContain("R_Gone", o);
        (_, o, _) = game.Run("--editorid-match", "^R_(Kept|Gone)$");
        Assert.Contains("R_Kept [", o);
        Assert.DoesNotContain("R_Gone", o);
        (code, o, _) = game.Run("--type", "MISC", "--where", "Value = 7");
        Assert.Equal(1, code);
    }

    [Fact]
    public void A_form_list_entry_matches_by_EditorID_and_by_part_of_it()
    {
        var (code, o, e) = game.Run("--type", "FLST", "--where", "Items has R_Key");
        Assert.True(code == 0, o + e);
        Assert.Contains($"R_List [{game.List}]", o);
        (code, o, e) = game.Run("--type", "FLST", "--where", "Items ~ r_k");
        Assert.True(code == 0, o + e);
        (code, o, e) = game.Run("--type", "FLST", "--where", "Items = R_Kept");
        Assert.Equal(1, code);
        (code, o, e) = game.Run("--type", "FLST", "--where", "Items != R_Key");
        Assert.Equal(1, code);
    }

    [Fact]
    public void A_partial_winner_with_a_new_EditorID_keeps_the_earlier_name_with_and_without_a_type()
    {
        foreach (var args in new[] { new[] { "--editorid-match", "^R_Renamed$" }, new[] { "--type", "NPC_", "--editorid-match", "^R_Renamed$" } })
        {
            var (code, o, e) = game.Run(args);
            Assert.True(code == 0, o + e);
            Assert.Contains($"R_Renamed [{game.Ren}] \"Ren Guy\" (NPC_, winner A.esm)", o);
        }
        var (c2, o2, _) = game.Run("--editorid-match", "^R_Ren$");
        Assert.True(c2 == 1, o2);   // the winning copy's EditorID is the new one
    }

    [Fact]
    public void A_typed_link_to_a_partial_form_without_an_EditorID_matches_and_shows_the_earlier_EditorID()
    {
        var (code, o, e) = game.Run("--type", "MISC", "--where", "Keywords has R_PKey");
        Assert.True(code == 0, o + e);
        Assert.Contains("R_Tagged [", o);
        Assert.Contains($"Keywords: R_PKey [{game.PKey}]", o);
        (code, o, e) = game.Run("--type", "FLST", "--where", "Items has R_Key");
        Assert.True(code == 0, o + e);
        Assert.Contains($"Items: R_Key [{game.Key}]", o);   // a form list's entry shows the EditorID it matched by
    }

    [Fact]
    public void A_regex_timeout_without_a_type_is_refused_not_skipped()
    {
        // R_ and 64 a's: this pattern backtracks on it for far longer than 2 seconds
        var (code, o, e) = game.Run("--editorid-match", "^R_(a|aa)*c$");
        Assert.True(code == 2, o + e);
        Assert.Contains("took longer than 2 seconds", e);
        Assert.DoesNotContain("couldn't be read in full", e);
    }
}
