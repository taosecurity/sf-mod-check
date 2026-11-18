// 0.5.2 item 1: check's itm comparer and conflict's comparer are one class (DeepCompare) with six options. These tests
// prove each command still gets exactly its 0.5.1 answer: the old code (Old051Comparers.cs) and the new one are run on
// the same pairs of values, awkward made-up ones and every field of real records read from test plugins, and must agree
// on every pair: the same true or false, or the same error.
using System.Reflection;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using Mutagen.Bethesda.Strings;
using Noggog;
using SfModCheck.Conflict;
using Xunit;

namespace SfModCheck.Tests;

public class DeepCompareTests
{
    static string Outcome(Func<object> f)
    {
        try { return f().ToString() ?? "null"; }
        catch (Exception e)
        {
            while (e is TargetInvocationException { InnerException: { } inner }) e = inner;
            return "throws " + e.GetType().Name;
        }
    }

    // ---- made-up values that hit each rule ----
    sealed class Opaque { }                                         // nothing public: rule 4
    sealed class OpaqueEq { public override bool Equals(object? o) => o is OpaqueEq; public override int GetHashCode() => 1; }
    sealed class WithUnknown { public int A { get; init; } public int Unknown { get; init; } public int UnknownField; }   // rule 6
    sealed class HasType { public Type T { get; init; } = typeof(int); }                                               // rule 3
    sealed class Node { public int V { get; init; } public Node? Next { get; init; } }                                 // depth limit
    sealed class HasField { public int X; }
    sealed class Boom { public int A => 1; public int B => throw new InvalidOperationException("damaged"); }
    sealed class HasTimestamp { public int Timestamp { get; init; } public int A { get; init; } }                      // rule 5 (itm skips it at every level)
    struct Pair { public int X; public int Y; }
    // Equals that says no even to itself, with a damaged part: conflict (Equals first) walks it and throws
    sealed class NeverEqual { public override bool Equals(object? o) => false; public override int GetHashCode() => 0; public int B => throw new InvalidOperationException("damaged"); }

    static Node Chain(int n, int last) { Node? x = null; for (int i = 0; i < n; i++) x = new Node { V = i == 0 ? last : i, Next = x }; return x!; }

    static List<object?> Corpus() =>
    [
        null, 1, 1, 2, 1.0f, float.NaN, float.NaN, -0.0f, 0.0f, "a", "a", "A", typeof(int), typeof(int), typeof(long),
        new byte[] { 1, 2 }, new byte[] { 1, 2 }, new byte[] { 1, 3 },
        new ReadOnlyMemorySlice<byte>(new byte[] { 1, 2 }), new MemorySlice<byte>(new byte[] { 1, 2 }),
        new ReadOnlyMemorySlice<float>(new float[] { 1, 2 }), new ReadOnlyMemorySlice<float>(new float[] { 1, 2 }),   // rule 2
        new ReadOnlyMemorySlice<float>(new float[] { 1, 3 }),
        new List<int> { 1, 2 }, new List<int> { 1, 2 }, new List<int> { 2, 1 }, new[] { 1, 2 }, new List<object?> { null }, new List<object?>(),
        new Opaque(), new Opaque(), new OpaqueEq(), new OpaqueEq(),
        new WithUnknown { A = 1, Unknown = 2 }, new WithUnknown { A = 1, Unknown = 3 }, new WithUnknown { A = 1, Unknown = 2, UnknownField = 9 },
        new HasType(), new HasType(), new HasType { T = typeof(long) },
        Chain(3, 0), Chain(3, 0), Chain(3, 9), Chain(16, 0), Chain(16, 0), Chain(16, 7),
        new HasField { X = 1 }, new HasField { X = 1 }, new HasField { X = 2 },
        new Boom(), new Boom(), new NeverEqual(),
        new HasTimestamp { Timestamp = 1, A = 1 }, new HasTimestamp { Timestamp = 2, A = 1 },
        new Pair { X = 1, Y = 2 }, new Pair { X = 1, Y = 2 }, new Pair { X = 1, Y = 3 },
        new TranslatedString(Language.English, "x"), new TranslatedString(Language.English, "x"), new TranslatedString(Language.English, "y"),
        new CellGrid { Point = new P2Int(1, 2), Flags = (CellGrid.Flag)0x0000_0101 }, new CellGrid { Point = new P2Int(1, 2), Flags = (CellGrid.Flag)0x0000_0201 },
        new CellGrid { Point = new P2Int(1, 2), Flags = (CellGrid.Flag)0x0000_0102 },
        FormKey.Factory("000801:TestFull.esm"), FormKey.Factory("000801:TestFull.esm"), FormKey.Factory("000802:TestFull.esm"),
        new P3Float(1, 2, 3), new P3Float(1, 2, 3),
        new Keyword(FormKey.Factory("000801:TestFull.esm"), StarfieldRelease.Starfield) { EditorID = "K" },
        new Keyword(FormKey.Factory("000801:TestFull.esm"), StarfieldRelease.Starfield) { EditorID = "K" },
        new Keyword(FormKey.Factory("000801:TestFull.esm"), StarfieldRelease.Starfield) { EditorID = "L" },
    ];

    static int Disagreements(object? a, object? b, int depth, List<string> report)
    {
        int bad = 0;
        void Same(string what, string oldAnswer, string newAnswer)
        {
            if (oldAnswer == newAnswer) return;
            bad++;
            if (report.Count < 20) report.Add($"{what} depth {depth}: {a?.GetType().Name} vs {b?.GetType().Name}: 0.5.1 {oldAnswer}, now {newAnswer}");
        }
        Same("itm", Outcome(() => Old051.ItmEq(a, b, depth)), Outcome(() => DeepCompare.Itm.Eq(a, b, depth)));
        Same("conflict", Outcome(() => Old051.ConflictEq(a, b, depth)), Outcome(() => FieldCompare.DeepEq(a, b, depth)));
        Same("conflict ignoring unknown", Outcome(() => Old051.ConflictEq(a, b, depth, true)), Outcome(() => FieldCompare.DeepEq(a, b, depth, ignoreUnknown: true)));
        Same("hash", Outcome(() => Old051.ConflictHash(a, depth)), Outcome(() => FieldCompare.DeepHash(a, depth)));
        Same("hash ignoring unknown", Outcome(() => Old051.ConflictHash(a, depth, true)), Outcome(() => FieldCompare.DeepHash(a, depth, ignoreUnknown: true)));
        return bad;
    }

    [Fact]
    public void Both_commands_give_their_0_5_1_answer_on_made_up_values()
    {
        var corpus = Corpus();
        var report = new List<string>();
        int bad = 0, pairs = 0;
        var answers = new HashSet<string>();
        foreach (var depth in new[] { 0, 1, 11, 12, 13 })
            foreach (var a in corpus)
                foreach (var b in corpus)
                {
                    pairs++;
                    bad += Disagreements(a, b, depth, report);
                    answers.Add("itm " + Outcome(() => DeepCompare.Itm.Eq(a, b, depth)) + " conflict " + Outcome(() => FieldCompare.DeepEq(a, b, depth)));
                }
        Assert.True(bad == 0, string.Join("\n", report));
        // the corpus reaches the places where the two commands answer differently, and errors
        Assert.Contains("itm False conflict True", answers);
        Assert.Contains("itm True conflict False", answers);
        Assert.Contains(answers, x => x.Contains("throws"));
        Assert.True(pairs > 10_000);
    }

    // Each option on its own: the answer that differs between the two settings (the rules in DeepCompare.cs).
    [Fact]
    public void The_six_rules_are_where_the_two_commands_differ()
    {
        var (s1, s2) = (new ReadOnlyMemorySlice<float>(new float[] { 1, 2 }), new ReadOnlyMemorySlice<float>(new float[] { 1, 2 }));
        Assert.False(DeepCompare.Itm.Eq(s1, s2, 1));          // rule 2: a value-type list by Equals (two arrays)
        Assert.True(DeepCompare.Conflict.Eq(s1, s2, 1));      // walked entry by entry
        Assert.True(DeepCompare.Itm.Eq(new HasType(), new HasType(), 1));    // rule 3 (both walk to the same Type)
        Assert.False(DeepCompare.Itm.Eq(new Opaque(), new Opaque(), 1));     // rule 4: nothing to walk, Equals says no
        Assert.True(DeepCompare.Conflict.Eq(new Opaque(), new Opaque(), 1)); // nothing to walk: equal
        Assert.True(DeepCompare.Itm.Eq(new HasTimestamp { Timestamp = 1 }, new HasTimestamp { Timestamp = 2 }, 1));    // rule 5
        Assert.False(DeepCompare.Conflict.Eq(new HasTimestamp { Timestamp = 1 }, new HasTimestamp { Timestamp = 2 }, 1));
        Assert.False(DeepCompare.Conflict.Eq(new WithUnknown { Unknown = 1 }, new WithUnknown { Unknown = 2 }, 1));     // rule 6
        Assert.True(DeepCompare.ConflictIgnoringUnknown.Eq(new WithUnknown { Unknown = 1 }, new WithUnknown { Unknown = 2 }, 1));
        // rule 1: with Equals first, the depth limit comes before the text step
        var (t1, t2) = (new TranslatedString(Language.English, "x"), new TranslatedString(Language.French, "x"));
        Assert.True(DeepCompare.Itm.Eq(t1, t2, 13));
        Assert.Equal(Old051.ConflictEq(t1, t2, 13), DeepCompare.Conflict.Eq(t1, t2, 13));
    }

    // Every field of real records, as Mutagen reads them from plugin files (binary overlays, the objects both commands
    // see), compared between two builds of the test plugin that differ in a few places, and between two reads of the same file.
    [Fact]
    public void Both_commands_give_their_0_5_1_answer_on_every_field_of_real_records()
    {
        using var w = new World();
        var clean = w.Build("clean");
        var changed = w.Build("changed", m =>
        {
            foreach (var q in m.Quests) { ((QuestReferenceAlias)q.Aliases![0]).Name = "Other"; q.Scenes[0].Conditions.Clear(); }
            foreach (var t in m.Quests.SelectMany(q => q.DialogTopics)) t.Responses[0].Responses[0].ResponseText = "bye";
            foreach (var n in m.NavigationMeshInfoMaps) n.MapInfos[0].Data = new byte[] { 1, 2, 3 };
            foreach (var x in m.MiscItems) x.Keywords!.RemoveAt(0);
        });
        using var a = w.Game.OpenPlugin(clean);
        using var a2 = w.Game.OpenPlugin(clean);
        using var b = w.Game.OpenPlugin(changed);
        var masters = new[] { w.FullMod, w.MediumMod, w.LightMod };
        var report = new List<string>();
        int bad = 0, fields = 0;
        var answers = new HashSet<string>();
        void Compare(IMajorRecordGetter x, IMajorRecordGetter y)
        {
            var t = x.GetType();
            var props = DeepCompare.Itm.Properties(t).Concat(FieldCompare.Fields(t)).Concat(FieldCompare.NestedProperties(t)).Distinct();
            foreach (var p in props)
            {
                object? va, vb;
                try { va = p.GetValue(x); vb = y.GetType() == t ? p.GetValue(y) : null; } catch (Exception) { continue; }
                fields++;
                foreach (var depth in new[] { 0, 1 }) bad += Disagreements(va, vb, depth, report);
                answers.Add(Outcome(() => FieldCompare.DeepEq(va, vb, 0)));
            }
            bad += Outcome(() => Old051.ItmSameFields(x, y, 0)) == Outcome(() => ItmTop(x, y)) ? 0 : 1;
        }
        foreach (var r in a.EnumerateMajorRecords())
        {
            foreach (var other in new IModGetter[] { a, a2, b })
                if (other.EnumerateMajorRecords().FirstOrDefault(o => o.FormKey == r.FormKey) is { } o) Compare(r, o);
            // and against each master record of the same type
            foreach (var m in masters)
                foreach (var o in m.EnumerateMajorRecords().Where(o => o.GetType() == r.GetType()).Take(3)) Compare(r, o);
        }
        Assert.True(bad == 0, string.Join("\n", report));
        Assert.True(fields > 300, $"only {fields} fields compared");
        Assert.Contains("True", answers);
        Assert.Contains("False", answers);
    }

    // check's top level (Overrides.SameFields is private): the same walk over the record's own properties and fields
    static bool ItmTop(IMajorRecordGetter a, IMajorRecordGetter b)
    {
        foreach (var p in DeepCompare.Itm.Properties(a.GetType())) if (!DeepCompare.Itm.Eq(p.GetValue(a), p.GetValue(b), 1)) return false;
        foreach (var f in a.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance)) if (!DeepCompare.Itm.Eq(f.GetValue(a), f.GetValue(b), 1)) return false;
        return true;
    }
}
