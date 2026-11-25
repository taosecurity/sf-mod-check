// triage (0.17.0): the full difference between two versions of one field, as one line per changed value or list entry.
// Until 0.16.x the "Changes to official records" table showed each changed field's whole value before and after, cut at
// 160 characters, so a change deep inside a big record (a quest's aliases or stages) couldn't be seen. The walk names
// two common, often harmless kinds of difference: a list with the same entries in another order, and the same English
// text where one plugin keeps the translations (a localized plugin) and the other doesn't.
//
// It walks the same members as conflict's comparison (DeepCompare.Conflict) and calls two values equal exactly when that
// comparison does, so it never finds a change triage's summary table doesn't (except the translation kinds, which that
// comparison can't see: it reads only the English text).
using System.Collections;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Strings;

namespace SfModCheck.Conflict;

/// <summary>One line of a field's detail: where in the field (Path, such as "Aliases[ID 3].Factions"), what kind of
/// difference (<see cref="TriageDetail.Changed"/> and the other kinds), and the values before and after (one line each,
/// at most <see cref="TriageDetail.MaxValue"/> characters).</summary>
public record DetailLine(string Path, string Kind, string Before, string After);

public sealed class TriageDetail
{
    public const string Changed = "changed", Added = "added", Removed = "removed", Reordered = "same entries, other order",
        TranslationsDropped = "same English text, translations dropped", TranslationsAdded = "same English text, translations added";

    /// <summary>Detail lines kept per record (all its fields together); the rest are counted.</summary>
    public const int MaxPerRecord = 200;
    /// <summary>Characters kept of each value; a longer one says how many it left out.</summary>
    public const int MaxValue = 2000;

    // List members tried, in this order, as the key that matches entries of two lists (an alias's ID, a stage's index).
    static readonly string[] Keys = ["ID", "Index", "Stage", "Name", "EditorID", "ScriptName", "Actor", "Ref", "Reference", "Marker", "Target", "Item", "Keyword"];

    readonly DeepCompare cmp = DeepCompare.Conflict;
    readonly List<DetailLine> lines = new();
    /// <summary>Lines found past <see cref="MaxPerRecord"/>.</summary>
    public int NotShown { get; private set; }
    // The translation kind of the field being walked (set by Field), or null when both plugins are localized alike.
    string? translationKind;

    /// <summary>The translation kind for a change built on a version from a plugin that is (or isn't) localized (header
    /// flag 0x80), made by a plugin that is (or isn't): <see cref="TranslationsDropped"/> when only the base is,
    /// <see cref="TranslationsAdded"/> when only the new one is, else null (equal values then hold no difference).</summary>
    public static string? TranslationKind(bool baseLocalized, bool newLocalized) =>
        baseLocalized == newLocalized ? null : baseLocalized ? TranslationsDropped : TranslationsAdded;

    /// <summary>The lines this record has given so far, and how many past the limit.</summary>
    public int Count => lines.Count + NotShown;

    /// <summary>Walks one field (its name is the path's start) and returns the lines it added within the record's limit,
    /// and how many more it found past the limit.
    /// translations: <see cref="TranslationKind"/>'s answer for the two plugins.</summary>
    public (List<DetailLine> Lines, int NotShown) Field(string field, object? before, object? after, string? translations)
    {
        translationKind = translations;
        int start = lines.Count, hidden = NotShown;
        Walk(field, before, after, 0);
        return (lines.GetRange(start, lines.Count - start), NotShown - hidden);
    }

    void Add(string path, string kind, string before, string after)
    {
        if (lines.Count < MaxPerRecord) lines.Add(new(path, kind, before, after));
        else NotShown++;
    }

    /// <summary>A value on one line, cut at <see cref="MaxValue"/> characters with how many were left out.</summary>
    public static string Show(object? v)
    {
        string s;
        try { s = v is null ? "(not set)" : SafeText.OneLine(FieldCompare.Dump(v, 0, int.MaxValue)); }
        catch (Exception e) when (Fatal.Recoverable(e)) { SfModCheck.Overrides.OnUnreadable?.Invoke(); return $"(unreadable: {e.GetType().Name})"; }
        return Cap(s);
    }

    /// <summary>Text cut at <see cref="MaxValue"/> characters, saying how many it left out.</summary>
    public static string Cap(string s) => s.Length > MaxValue ? $"{s[..MaxValue]}… ({s.Length - MaxValue:N0} more characters)" : s;

    bool Eq(object? a, object? b, int depth) => cmp.Eq(a, b, depth);

    void Walk(string path, object? a, object? b, int depth)
    {
        bool equal = Eq(a, b, depth);
        if (equal && translationKind is null) return;
        if (a is null || b is null)
        {
            if (!equal) Add(path, a is null ? Added : Removed, Show(a), Show(b));
            return;
        }
        if (a is ITranslatedStringGetter ta && b is ITranslatedStringGetter tb)
        {
            if (!equal) Add(path, Changed, Show(a), Show(b));
            else if (!string.IsNullOrEmpty(ta.String)) Add(path, translationKind!, Show(a), Show(b));
            return;
        }
        int before = lines.Count + NotShown;
        var t = a.GetType();
        bool leaf = a is string || b is string || t != b.GetType() || t.IsPrimitive || t.IsEnum || a is IFormLinkGetter
                    || SfModCheck.Overrides.BlobEq(a, b) is not null || depth > DeepCompare.MaxDepth
                    // a list of any kind (a plain List<T> too) is walked; other values only when they are Mutagen's
                    || (a is not IEnumerable && t.Namespace?.StartsWith("Mutagen", StringComparison.Ordinal) != true);
        if (!leaf && a is IEnumerable ea && b is IEnumerable eb) Lists(path, ea.Cast<object?>().ToList(), eb.Cast<object?>().ToList(), equal, depth);
        else if (!leaf && !t.IsValueType)
        {
            foreach (var p in cmp.Properties(t)) Walk($"{path}.{p.Name}", p.GetValue(a), p.GetValue(b), depth + 1);
            foreach (var f in cmp.Fields(t)) Walk($"{path}.{f.Name}", f.GetValue(a), f.GetValue(b), depth + 1);
        }
        // a difference the walk couldn't place below this value (a simple value, or one only its own Equals tells apart)
        if (!equal && lines.Count + NotShown == before) Add(path, Changed, Show(a), Show(b));
    }

    void Lists(string path, List<object?> la, List<object?> lb, bool equal, int depth)
    {
        if (equal)   // only to find translation kinds: entries are equal in order
        {
            for (int i = 0; i < la.Count; i++) Walk($"{path}[{Label(la[i], i)}]", la[i], lb[i], depth + 1);
            return;
        }
        // same entries, other order: one line, not a line per moved entry
        if (la.Count == lb.Count && Match(la, lb) is { } m && m.All(x => x >= 0))
        {
            int first = Enumerable.Range(0, la.Count).First(i => !Eq(la[i], lb[i], depth + 1));
            Add(path, Reordered, Show(la), Show(lb) + $" (first different at entry {first + 1} of {la.Count})");
            return;
        }
        // entries matched by a key member every entry has, distinct in each list
        if (KeyOf(la, lb) is { } key)
        {
            var bByKey = lb.ToDictionary(x => KeyValue(x, key)!, x => x);
            var aKeys = new HashSet<string>(la.Select(x => KeyValue(x, key)!));
            foreach (var x in la)
            {
                var k = KeyValue(x, key)!;
                if (bByKey.TryGetValue(k, out var y)) Walk($"{path}[{key} {k}]", x, y, depth + 1);
                else Add($"{path}[{key} {k}]", Removed, Show(x), "(not set)");
            }
            foreach (var y in lb)
                if (!aKeys.Contains(KeyValue(y, key)!)) Add($"{path}[{key} {KeyValue(y, key)}]", Added, "(not set)", Show(y));
            return;
        }
        if (la.Count == lb.Count)
        {
            for (int i = 0; i < la.Count; i++) Walk($"{path}[{i}]", la[i], lb[i], depth + 1);
            return;
        }
        // different lengths and no key: entries without an equal partner were removed or added
        var match = Match(la, lb) ?? Enumerable.Repeat(-1, la.Count).ToArray();
        var used = new HashSet<int>(match.Where(x => x >= 0));
        for (int i = 0; i < la.Count; i++) if (match[i] < 0) Add($"{path}[{i}]", Removed, Show(la[i]), "(not set)");
        for (int j = 0; j < lb.Count; j++) if (!used.Contains(j)) Add($"{path}[{j}]", Added, "(not set)", Show(lb[j]));
    }

    /// <summary>For each entry of la, the index of an equal entry of lb, each used once (-1: none); null when the lists are
    /// too long to match (more than 2,000 entries). Entries are grouped by DeepCompare's hash first.</summary>
    int[]? Match(List<object?> la, List<object?> lb)
    {
        if (la.Count > 2000 || lb.Count > 2000) return null;
        var free = new Dictionary<int, List<int>>();
        var wild = new List<int>();   // entries whose hash couldn't be made (2): compared with every free entry
        for (int j = 0; j < lb.Count; j++)
        {
            var h = cmp.Hash(lb[j], 1);
            if (h == 2) wild.Add(j); else (free.TryGetValue(h, out var c) ? c : free[h] = new()).Add(j);
        }
        var used = new HashSet<int>();
        var result = new int[la.Count];
        for (int i = 0; i < la.Count; i++)
        {
            var h = cmp.Hash(la[i], 1);
            IEnumerable<int> cands = h == 2 ? Enumerable.Range(0, lb.Count) : (free.GetValueOrDefault(h) ?? new()).Concat(wild);
            result[i] = -1;
            foreach (var j in cands)
                if (!used.Contains(j) && Eq(la[i], lb[j], 1)) { result[i] = j; used.Add(j); break; }
        }
        return result;
    }

    /// <summary>The first key member every entry of both lists has (a string, number or link), with no repeats in either list.</summary>
    static string? KeyOf(List<object?> la, List<object?> lb)
    {
        if (la.Count == 0 && lb.Count == 0) return null;
        if (la.Concat(lb).Any(x => x is null || x.GetType().IsPrimitive || x is string)) return null;
        foreach (var k in Keys)
        {
            var ka = la.Select(x => KeyValue(x, k)).ToList();
            var kb = lb.Select(x => KeyValue(x, k)).ToList();
            if (ka.Concat(kb).Any(v => v is null)) continue;
            if (ka.Distinct().Count() == ka.Count && kb.Distinct().Count() == kb.Count) return k;
        }
        return null;
    }

    /// <summary>An entry's key member as text, or null when it has none (or it's empty).</summary>
    static string? KeyValue(object? x, string key)
    {
        if (x is null) return null;
        object? v;
        try { v = x.GetType().GetProperty(key)?.GetValue(x); } catch (Exception e) when (Fatal.Recoverable(e)) { return null; }
        return v switch
        {
            string s when s.Length > 0 => SafeText.OneLine(s),
            IFormLinkGetter fl when !fl.IsNull => fl.FormKey.ToString(),
            _ when v is not null && (v.GetType().IsPrimitive || v.GetType().IsEnum) => v.ToString(),
            _ => null,
        };
    }

    /// <summary>A list entry's label in a path: its key value when it has one of the usual key members, else its position.</summary>
    static string Label(object? x, int i)
    {
        foreach (var k in Keys.Take(3)) if (KeyValue(x, k) is { } v) return $"{k} {v}";
        return i.ToString();
    }
}
