// diff field detail (0.22.0 step 6, item G): each changed record gets triage's field-by-field lines (TriageDetail):
// changed values named, a reordered list and dropped translations named; unchanged records, noise and the summary
// lines stay as they were. Made-up plugins only.
using System.Text.Json.Nodes;
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Starfield;
using Noggog;
using SfModCheck;
using SfModCheck.Conflict;
using Xunit;

namespace SfModCheck.Tests;

public class DiffDetailTests
{
    static readonly WriteOptions NoGap = new() { GapCheck = false, FixEffects = false };

    /// <summary>Mod.esm (localized or not) with keywords 0x800 and 0x801, misc 0x810 (name, weight, value, keywords in
    /// the given order) and misc 0x811 that never changes.</summary>
    static string Build(Sandbox s, string sub, float weight = 1, int value = 5, bool swapKeywords = false, bool localized = false,
        string name = "Lucky Rock", int keywords = 2)
    {
        var mod = Plugin.New("Mod.esm", PluginStyle.Full, "test", "diff detail test", localized);
        var k1 = mod.Keywords.AddNew(new FormKey(mod.ModKey, 0x800)); k1.EditorID = "MOD_KwA";
        var k2 = mod.Keywords.AddNew(new FormKey(mod.ModKey, 0x801)); k2.EditorID = "MOD_KwB";
        var m = mod.MiscItems.AddNew(new FormKey(mod.ModKey, 0x810));
        m.EditorID = "MOD_Rock";
        m.Name = localized ? Localized.Text(name) : name;
        m.Weight = weight; m.Value = value;
        var kws = new List<IFormLinkGetter<IKeywordGetter>> { k1.ToLink<IKeywordGetter>(), k2.ToLink<IKeywordGetter>() };
        for (int i = 2; i < keywords; i++)
        {
            var k = mod.Keywords.AddNew(new FormKey(mod.ModKey, 0x900u + (uint)i)); k.EditorID = $"MOD_Kw{i}";
            kws.Add(k.ToLink<IKeywordGetter>());
        }
        if (swapKeywords) kws.Reverse();
        m.Keywords = new ExtendedList<IFormLinkGetter<IKeywordGetter>>(kws);
        var same = mod.MiscItems.AddNew(new FormKey(mod.ModKey, 0x811));
        same.EditorID = "MOD_Same"; same.Weight = 3;
        var dir = s.Sub(sub);
        using var g = new GameData(s.Sub("data"));
        PluginWriter.Write(mod, dir, g, NoGap);
        return Path.Combine(dir, "Mod.esm");
    }

    static PluginDiff Diff(string a, string b)
    {
        var oi = RecordIndex.Build(a, _ => null, out var e1); Assert.Null(e1);
        var ni = RecordIndex.Build(b, _ => null, out var e2); Assert.Null(e2);
        var d = PluginDiff.Compare(oi!, ni!);
        DiffFields.Apply(d);
        return d;
    }

    static (int Code, string Stdout, string Stderr) Run(params string[] args) => ExeTests.RunSplit(args);

    static JsonNode Json(string stdout) => JsonNode.Parse(stdout[..stdout.LastIndexOf("RESULT:", StringComparison.Ordinal)])!;

    [Fact]
    public void Changed_fields_are_named_with_their_values_and_unchanged_records_get_nothing()
    {
        using var s = new Sandbox();
        var d = Diff(Build(s, "a"), Build(s, "b", weight: 2, value: 9));
        var c = Assert.Single(d.Changed);   // MOD_Same and the keywords are unchanged
        Assert.Equal("MOD_Rock", c.New.EditorId);
        Assert.Equal(["Value", "Weight"], c.Fields.Order());
        Assert.Contains(c.Detail, x => x.Field == "Weight" && x.Line.Path == "Weight" && x.Line.Kind == TriageDetail.Changed && x.Line.Before == "1" && x.Line.After == "2");
        Assert.Contains(c.Detail, x => x.Field == "Value" && x.Line.Kind == TriageDetail.Changed && x.Line.Before == "5" && x.Line.After == "9");
        Assert.Equal(2, c.Detail.Count);
        Assert.Equal(0, c.DetailNotShown);
    }

    [Fact]
    public void A_reordered_list_is_named_once()
    {
        using var s = new Sandbox();
        var c = Assert.Single(Diff(Build(s, "a"), Build(s, "b", swapKeywords: true)).Changed);
        Assert.Equal(["Keywords"], c.Fields);
        var (field, line) = Assert.Single(c.Detail);
        Assert.Equal(("Keywords", "Keywords", TriageDetail.Reordered), (field, line.Path, line.Kind));
    }

    [Fact]
    public void Dropped_translations_are_named_beside_the_real_change()
    {
        using var s = new Sandbox();
        var c = Assert.Single(Diff(Build(s, "a", localized: true), Build(s, "b", weight: 2)).Changed);
        Assert.Equal(["Weight"], c.Fields);   // the field list is today's: the English text is the same
        Assert.Contains(c.Detail, x => x.Field == "Weight" && x.Line.Kind == TriageDetail.Changed);
        var name = Assert.Single(c.Detail, x => x.Field == "Name");
        Assert.Equal((TriageDetail.TranslationsDropped, "\"Lucky Rock\"", "\"Lucky Rock\""), (name.Line.Kind, name.Line.Before, name.Line.After));

        // the other way round: translations added
        var back = Assert.Single(Diff(Build(s, "c", weight: 2), Build(s, "d", localized: true)).Changed);
        Assert.Equal(TriageDetail.TranslationsAdded, Assert.Single(back.Detail, x => x.Field == "Name").Line.Kind);
    }

    [Fact]
    public void Exe_text_adds_detail_lines_under_each_changed_record_and_keeps_the_summary_lines()
    {
        using var s = new Sandbox();
        var a = Build(s, "a");
        var b = Build(s, "b", weight: 2, swapKeywords: true);
        var r = Run("diff", a, b, "--quiet", "--data", s.Sub("nodata"));
        Assert.Equal(1, r.Code);
        // today's lines, unchanged
        Assert.Equal("RESULT: DIFFERENCES (1 plugin compared, 1 changed, 0 added, 0 removed; records: 0 added, 0 removed, 1 changed, 0 renumbered, 0 noise)",
            ExeTests.ResultLine(r.Stdout));
        Assert.Contains("MISC  own: 1 changed  |  overrides: -", r.Stdout);
        Assert.Contains("  changed records (1; the fields that differ):", r.Stdout);
        var lines = r.Stdout.Replace("\r", "").Split('\n').ToList();
        int at = lines.IndexOf("    MISC 000810:Mod.esm MOD_Rock: Keywords, Weight");
        Assert.True(at >= 0, r.Stdout);
        // then the detail, indented under it
        Assert.Equal("      Keywords: same entries, other order", lines[at + 1]);
        Assert.Equal("      Weight: changed: 1 -> 2", lines[at + 2]);
        Assert.DoesNotContain("MOD_Same", r.Stdout);
    }

    [Fact]
    public void Exe_text_caps_detail_lines_and_all_and_json_show_them_all()
    {
        using var s = new Sandbox();
        // 14 keywords cut to 2 (a "removed" line each) plus name, weight and value changes: more lines than the text shows
        var a = Build(s, "a", keywords: 14, weight: 1, value: 1, name: "Old name");
        var b = Build(s, "b", keywords: 2, weight: 2, value: 2, name: "New name");
        var d = Diff(a, b);
        var c = Assert.Single(d.Changed, x => x.New.EditorId == "MOD_Rock");
        Assert.True(c.Detail.Count > DiffReport.DetailShown, $"{c.Detail.Count} detail lines");

        var text = Run("diff", a, b, "--quiet", "--data", s.Sub("nodata"));
        Assert.Contains($"      ... and {c.Detail.Count - DiffReport.DetailShown:N0} more detail lines (--all or --json lists them all)", text.Stdout);
        var all = Run("diff", a, b, "--quiet", "--all", "--data", s.Sub("nodata"));
        Assert.DoesNotContain("more detail line", all.Stdout);
        Assert.Contains("      Name: changed: \"Old name\" -> \"New name\"", all.Stdout);

        var json = Run("diff", a, b, "--quiet", "--json", "--data", s.Sub("nodata"));
        var rec = json.Stdout.Length > 0 ? Json(json.Stdout)["plugins"]![0]!["changed"]!.AsArray().Single(x => (string)x!["editor_id"]! == "MOD_Rock")! : null;
        Assert.NotNull(rec);
        // today's keys are still there; detail is new
        foreach (var k in new[] { "key", "type", "editor_id", "override", "fields", "not_compared" }) Assert.True(rec!.AsObject().ContainsKey(k), k);
        var detail = rec!["detail"]!.AsArray();
        Assert.Equal(c.Detail.Count, detail.Count);
        Assert.Contains(detail, x => (string)x!["field"]! == "Weight" && (string)x["kind"]! == TriageDetail.Changed && (string)x["before"]! == "1" && (string)x["after"]! == "2");
        Assert.Equal(0, (int)rec["detail_not_shown"]!);
    }

    [Fact]
    public void Noise_records_get_no_detail_and_stay_noise()
    {
        // the same record with only translations dropped: the English text is equal, so it stays noise as before
        using var s = new Sandbox();
        var d = Diff(Build(s, "a", localized: true), Build(s, "b"));
        Assert.DoesNotContain(d.Changed, c => c.New.EditorId == "MOD_Rock");
        Assert.Contains(d.Noise, c => c.New.EditorId == "MOD_Rock");
        Assert.All(d.Noise, c => Assert.Empty(c.Detail));
    }
}
