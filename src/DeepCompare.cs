// One field-by-field comparer for two versions of a value (0.5.2). Until 0.5.1 there were two: check's itm rule
// (Overrides.DeepEq, the package-check gate) and conflict's (FieldCompare.DeepEq), both grown from the same itm-check
// code. They differed in six rules, and each command's answers depend on its own set, so the rules are options here and
// each command keeps exactly the answers it gave before (proved on the official masters and a 149-plugin load order).
//
// Mutagen's list types compare by reference, so lists and nested objects are walked by value. The order of the steps
// matters: reads are lazy, a damaged field throws when it is read, and a step that answers early never reads the rest.
using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Mutagen.Bethesda.Starfield;
using Mutagen.Bethesda.Strings;

namespace SfModCheck;

public sealed class DeepCompare
{
    /// <summary>Deeper than this, two values count as different (a guard; real records stay far above it).</summary>
    public const int MaxDepth = 12;

    // ---- the six rules where the two commands differ ----

    /// <summary>Rule 1. Ask the value's own Equals before anything else (conflict). Mutagen's Equals runs its own deep
    /// comparison at every level of the walk, which check's itm rule avoids for speed (a set of armor object templates:
    /// 2 s for one record). With Equals first, the depth limit comes straight after it; without it, the depth limit
    /// comes after the value-type step, as itm-check had it.</summary>
    public bool EqualsFirst { get; init; }

    /// <summary>Rule 2. Walk a list that is a value type (Mutagen's memory slices) entry by entry (conflict), instead of
    /// asking its Equals (check). Byte blobs are compared by their bytes either way.</summary>
    public bool WalkValueTypeLists { get; init; }

    /// <summary>Rule 3. Compare a System.Type value with Equals (check) rather than walking its properties.</summary>
    public bool TypesByEquals { get; init; }

    /// <summary>Rule 4. An object with nothing to walk (no compared properties, no public fields) keeps its data out of
    /// sight, so only its own Equals can tell (check). Conflict calls two such objects equal: its Equals-first step has
    /// already said they differ by Equals, and the walk finds nothing.</summary>
    public bool EmptyByEquals { get; init; }

    /// <summary>Rule 5. The properties compared on a type, in order. Check skips bookkeeping and child records at every
    /// level and compares texts last; conflict skips only Mutagen's registration data (check: Overrides.FindCompared;
    /// conflict: FieldCompare.NestedProperties).</summary>
    public required Func<Type, PropertyInfo[]> SelectProperties { get; init; }

    /// <summary>Rule 6. Leave out Mutagen's undocumented "Unknown*" members (padding the Creation Kit fills with junk);
    /// conflict uses it to tell an "undocumented fields only" difference apart.</summary>
    public bool IgnoreUnknown { get; init; }

    /// <summary>Rule 7 (diff, 0.11.0). Leave out the members Mutagen names as padding: "Unused", "Unused1", "Unused2"
    /// and so on (the 3 padding bytes after a condition's flags, a leveled-list entry's bytes 2-3 and 11). Longer
    /// names such as UnusedConditions or UnusedWorldspaceParent hold data and stay compared.</summary>
    public bool IgnorePadding { get; init; }

    /// <summary>Whether a member name is one of Mutagen's padding names (rule 7).</summary>
    public static bool IsPadding(string name) =>
        name.StartsWith("Unused", StringComparison.Ordinal) && name.AsSpan(6).IndexOfAnyExceptInRange('0', '9') < 0;

    bool Skipped(string name) => (IgnoreUnknown && name.StartsWith("Unknown")) || (IgnorePadding && IsPadding(name));

    /// <summary>Rule 8 (diff, 0.11.0). Compare a list of scripts, or of script properties, by name instead of by
    /// position: Papyrus finds scripts and properties by name, so a resave that only reorders them changes nothing in
    /// game (the user's decision, 2026-11-21). Each entry of one list is matched with an equal entry of the same name
    /// in the other (entries that share a name, such as one script attached twice, are matched by value), so a
    /// changed name, flag or value, or a script or property added or removed, is still a difference. Names are
    /// compared exactly: a change of letter case alone counts as a change, although the game ignores case (a careful
    /// choice, stated in the README and CHANGELOG). Only these lists are matched by name: a record's or an alias's
    /// Scripts, a script's Properties, and the Members of one element of a struct array. Never an array property's
    /// elements (Objects, Structs), whose order is the array's order, nor a struct property's Members, which Mutagen
    /// reads as one entry (code-review, 0.11.0 VMAD order).</summary>
    public bool ScriptsByName { get; init; }

    /// <summary>Whether a member's list is matched by name (rule 8); holder: the type that holds the member.</summary>
    static bool NamedList(Type holder, string member) => member is "Scripts" or "Properties"
        || (member == "Members" && typeof(IScriptEntryStructsGetter).IsAssignableFrom(holder));

    /// <summary>A script's or script property's name; null for anything else.</summary>
    static string? NameOf(object? o) => o switch
    {
        IScriptEntryGetter s => s.Name ?? "",
        IScriptPropertyGetter p => p.Name ?? "",
        _ => null,
    };

    /// <summary>Whether rule 8 applies to a list: two or more entries, all scripts or all script properties.</summary>
    static bool AllNamed(List<object?> l) =>
        l.Count >= 2 && (l.All(o => o is IScriptEntryGetter) || l.All(o => o is IScriptPropertyGetter));

    /// <summary>Rule 8 for two lists of one length: every entry of la has its own equal entry of the same name in lb.
    /// Greedy matching is enough because Eq is an equivalence (equal values are interchangeable).</summary>
    bool NamedEq(List<object?> la, List<object?> lb, int depth)
    {
        var free = new Dictionary<string, List<object?>>(StringComparer.Ordinal);
        foreach (var y in lb) (free.TryGetValue(NameOf(y)!, out var c) ? c : free[NameOf(y)!] = []).Add(y);
        foreach (var x in la)
        {
            if (!free.TryGetValue(NameOf(x)!, out var c)) return false;
            int i = c.FindIndex(y => Eq(x, y, depth + 1));
            if (i < 0) return false;
            c.RemoveAt(i);
        }
        return true;
    }

    readonly ConcurrentDictionary<Type, PropertyInfo[]> props = new();
    readonly ConcurrentDictionary<Type, FieldInfo[]> fields = new();

    public PropertyInfo[] Properties(Type t) => props.GetOrAdd(t, t =>
        IgnoreUnknown || IgnorePadding ? SelectProperties(t).Where(p => !Skipped(p.Name)).ToArray() : SelectProperties(t));

    public FieldInfo[] Fields(Type t) => fields.GetOrAdd(t, t => t.GetFields(BindingFlags.Public | BindingFlags.Instance)
        .Where(f => !Skipped(f.Name)).ToArray());

    /// <summary>Whether a and b hold the same value. depth: how deep the walk is (0 for a record's field).</summary>
    public bool Eq(object? a, object? b, int depth) => Eq(a, b, depth, false);

    /// <summary>As <see cref="Eq(object?, object?, int)"/>; <paramref name="named"/>: a and b are held by a member whose
    /// list rule 8 may put in name order.</summary>
    bool Eq(object? a, object? b, int depth, bool named)
    {
        if (EqualsFirst && Equals(a, b)) return true;
        if (a is null || b is null) return a is null && b is null;
        if (EqualsFirst && depth > MaxDepth) return false;
        // names and texts: the text itself (default language), whether it's stored in the plugin or in .strings files.
        // Walking a translated string's properties loads every language's strings files (1-2 s per record).
        if (a is ITranslatedStringGetter ta && b is ITranslatedStringGetter tb)
            return string.Equals(ta.String, tb.String, StringComparison.Ordinal);
        if (a is string || b is string) return a is string sa && b is string sb && string.Equals(sa, sb, StringComparison.Ordinal);
        if (a.GetType() != b.GetType()) return false;
        // byte blobs (unknown data, navmesh geometry): compare bytes, not slice positions in two different files
        if (Overrides.BlobEq(a, b) is bool blobs) return blobs;
        // XCLC (cell grid) = X, Y, one byte of land flags, 3 unused bytes (xEdit wbDefinitionsCommon.pas). Mutagen 0.54.4
        // reads flags + unused as one 4-byte value, and Bethesda leaves junk in the unused bytes (Starfield.esm cell
        // 0151D8: A9CE0000), so compare only the point and the low byte (0.5.0).
        if (a is ICellGridGetter ga && b is ICellGridGetter gb) return Overrides.CellGridEq(ga, gb);
        var t = a.GetType();
        bool lists = a is IEnumerable && b is IEnumerable;
        // simple values: Equals is the answer (with Equals first it has already said no)
        if (t.IsValueType && !(WalkValueTypeLists && lists)) return !EqualsFirst && Equals(a, b);
        if (TypesByEquals && a is Type) return Equals(a, b);
        if (ReferenceEquals(a, b)) return true;   // (with Equals first, object.Equals has already said yes to the same instance)
        if (!EqualsFirst && depth > MaxDepth) return false;
        if (lists)
        {
            var la = ((IEnumerable)a).Cast<object?>().ToList(); var lb = ((IEnumerable)b).Cast<object?>().ToList();
            if (la.Count != lb.Count) return false;
            if (named && AllNamed(la) && AllNamed(lb)) return NamedEq(la, lb, depth);
            for (int i = 0; i < la.Count; i++) if (!Eq(la[i], lb[i], depth + 1)) return false;
            return true;
        }
        var ps = Properties(t);
        var fs = Fields(t);
        if (ps.Length == 0 && fs.Length == 0) return !EmptyByEquals || Equals(a, b);
        foreach (var p in ps) if (!Eq(p.GetValue(a), p.GetValue(b), depth + 1, ScriptsByName && NamedList(t, p.Name))) return false;
        foreach (var f in fs) if (!Eq(f.GetValue(a), f.GetValue(b), depth + 1, ScriptsByName && NamedList(t, f.Name))) return false;
        return true;
    }

    /// <summary>A hash that follows Eq's walk (lists in order, then the same properties and fields), for grouping long
    /// lists: values Eq finds equal by walking get the same hash. Meant for the walking options (conflict's): with
    /// EmptyByEquals or TypesByEquals two equal values may still hash apart, which only costs a slower search.
    /// 0 = null, 1 = past the depth limit, 2 = a part couldn't be read.</summary>
    public int Hash(object? v, int depth) => Hash(v, depth, false);

    int Hash(object? v, int depth, bool named)
    {
        if (v is null) return 0;
        if (depth > MaxDepth) return 1;
        if (v is string s) return s.GetHashCode();
        if (v is ITranslatedStringGetter ts) return (ts.String ?? "").GetHashCode();   // as Eq
        var t = v.GetType();
        if (Overrides.BlobHash(v) is int blobHash) return blobHash;
        if (t.IsValueType && v is not IEnumerable) return v.GetHashCode();   // slices hash by entries, as Eq walks them (0.5.0)
        var h = new HashCode();
        h.Add(t);
        try
        {
            if (v is IEnumerable e)
            {
                var l = e.Cast<object?>().ToList();
                if (named && AllNamed(l))
                {
                    // rule 8: the same (name, hash) pairs in any order give one hash; each entry is hashed once
                    foreach (var (n, x) in l.Select(x => (NameOf(x)!, Hash(x, depth + 1)))
                        .OrderBy(p => p.Item1, StringComparer.Ordinal).ThenBy(p => p.Item2))
                    { h.Add(n, StringComparer.Ordinal); h.Add(x); }
                }
                else foreach (var x in l) h.Add(Hash(x, depth + 1));
                return h.ToHashCode();
            }
            foreach (var p in Properties(t)) h.Add(Hash(p.GetValue(v), depth + 1, ScriptsByName && NamedList(t, p.Name)));
            foreach (var f in Fields(t)) h.Add(Hash(f.GetValue(v), depth + 1, ScriptsByName && NamedList(t, f.Name)));
        }
        catch (Exception e) when (Fatal.Recoverable(e)) { return 2; }
        return h.ToHashCode();
    }

    // ---- the two commands' settings ----

    /// <summary>check's itm rule (the package-check gate): Equals only for simple values, child records and bookkeeping
    /// skipped at every level, texts compared last.</summary>
    public static readonly DeepCompare Itm = new()
    {
        EqualsFirst = false, WalkValueTypeLists = false, TypesByEquals = true, EmptyByEquals = true,
        SelectProperties = Overrides.FindCompared,
    };

    /// <summary>conflict: Equals first, then the walk.</summary>
    public static readonly DeepCompare Conflict = new()
    {
        EqualsFirst = true, WalkValueTypeLists = true, TypesByEquals = false, EmptyByEquals = false,
        SelectProperties = SfModCheck.Conflict.FieldCompare.NestedProperties,
    };

    /// <summary>diff's noise test: conflict's rules, leaving out Mutagen's padding members (rule 7) and comparing
    /// scripts and script properties by name, not by position (rule 8). Undocumented "Unknown*" members stay
    /// compared: they may hold real data.</summary>
    public static readonly DeepCompare DiffIgnoringPadding = new()
    {
        EqualsFirst = true, WalkValueTypeLists = true, TypesByEquals = false, EmptyByEquals = false, IgnorePadding = true, ScriptsByName = true,
        SelectProperties = SfModCheck.Conflict.FieldCompare.NestedProperties,
    };

    /// <summary>conflict, leaving out Mutagen's "Unknown*" members.</summary>
    public static readonly DeepCompare ConflictIgnoringUnknown = new()
    {
        EqualsFirst = true, WalkValueTypeLists = true, TypesByEquals = false, EmptyByEquals = false, IgnoreUnknown = true,
        SelectProperties = SfModCheck.Conflict.FieldCompare.NestedProperties,
    };
}
