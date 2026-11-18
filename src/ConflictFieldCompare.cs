// Moved from StarfieldConflictCheck (FieldCompare.cs, commit d336108) into sfmodcheck on 2026-11-16.
// Field-by-field comparison of two versions of the same record (DeepEq/Dump come from itm-check).
using System.Collections;
using System.Reflection;
using Mutagen.Bethesda.Plugins.Records;

namespace SfModCheck.Conflict;


public static class FieldCompare
{
    // Bookkeeping that differs between plugins without changing the record.
    static readonly HashSet<string> Ignored =
    [
        "VersionControl", "Version2", "FormVersion", "Timestamp", "TemporaryTimestamp", "UnknownGroupData",
        "TemporaryUnknownGroupData", "IsCompressed", "FormKey", "Registration", "StaticRegistration",
        "PersistentTimestamp", "PersistentUnknownGroupData", "SubCellsTimestamp", "SubCellsUnknown",
        "TopCellTimestamp", "TopCellUnknown",
        // Typed views of MajorRecordFlagsRaw, which is compared instead.
        "StarfieldMajorRecordFlags", "MajorFlags",
        // Worldspace data the Creation Kit regenerates; plugins rarely carry it and the engine copes.
        "OffsetData", "CellSizeData", "LargeReferences",
    ];
    public static IReadOnlyCollection<string> IgnoredFields => Ignored;
    const int CompressedFlag = 0x40000;
    public const int PartialFormFlag = 0x4000;

    static readonly Dictionary<Type, PropertyInfo[]> PropCache = new();

    // Top-level fields worth comparing. Child records (cell refs, quest topics, worldspace blocks) are records in their own right.
    public static PropertyInfo[] Fields(Type t)
    {
        lock (PropCache)
        {
            if (PropCache.TryGetValue(t, out var cached)) return cached;
            var props = t.GetProperties()
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && !Ignored.Contains(p.Name) && !HoldsRecords(p.PropertyType))
                .ToArray();
            return PropCache[t] = props;
        }
    }

    static bool HoldsRecords(Type t)
    {
        if (typeof(IMajorRecordGetter).IsAssignableFrom(t)) return true;
        var elem = t.IsGenericType && typeof(IEnumerable).IsAssignableFrom(t) ? t.GetGenericArguments().LastOrDefault() : null;
        return elem is not null && (typeof(IMajorRecordGetter).IsAssignableFrom(elem) || elem.Name.Contains("Block"));
    }

    public static object? Get(PropertyInfo p, object rec)
    {
        var v = p.GetValue(rec);
        if (p.Name == "MajorRecordFlagsRaw" && v is int flags) return flags & ~(CompressedFlag | PartialFormFlag);
        return v;
    }

    // Starfield partial forms carry only the fields they change; an absent field means "keep the earlier value".
    public static bool IsPartial(IMajorRecordGetter r) => (r.MajorRecordFlagsRaw & PartialFormFlag) != 0;

    public static bool IsEmpty(object? v) => v switch
    {
        null => true,
        string s => s.Length == 0,
        Mutagen.Bethesda.Plugins.IFormLinkGetter fl => fl.IsNull,
        IEnumerable e => !e.Cast<object?>().Any(),
        _ when v.GetType().IsValueType => v.Equals(Activator.CreateInstance(v.GetType())),  // 0, false, first enum value
        _ => false,
    };

    // The value this version contributes for a field, or null if it is a partial form that leaves the field alone.
    public static bool Provides(IMajorRecordGetter r, PropertyInfo p, out object? value)
    {
        value = Get(p, r);
        return !(IsPartial(r) && IsEmpty(value));
    }

    // Mutagen's list types compare by reference, so walk lists and nested objects by value.
    // ignoreUnknown: skip Mutagen's undocumented "Unknown*" members (padding the Creation Kit fills with junk).
    public static bool DeepEq(object? a, object? b, int depth, bool ignoreUnknown = false)
    {
        if (Equals(a, b)) return true;
        if (a is null || b is null || depth > 12) return false;
        // names and texts by the text itself, whether stored in the plugin or in .strings files (as Overrides.DeepEq; 0.5.0)
        if (a is Mutagen.Bethesda.Strings.ITranslatedStringGetter ta && b is Mutagen.Bethesda.Strings.ITranslatedStringGetter tb)
            return string.Equals(ta.String, tb.String, StringComparison.Ordinal);
        if (a is string || b is string || a.GetType() != b.GetType()) return false;
        if (SfModCheck.Overrides.BlobEq(a, b) is bool blobs) return blobs;   // shared with the itm check
        // XCLC: junk in the 3 unused bytes after the land flags is not a difference (same rule as the itm check, 0.5.0)
        if (a is Mutagen.Bethesda.Starfield.ICellGridGetter ga && b is Mutagen.Bethesda.Starfield.ICellGridGetter gb)
            return SfModCheck.Overrides.CellGridEq(ga, gb);
        if (a is IEnumerable ea && b is IEnumerable eb)
        {
            var la = ea.Cast<object?>().ToList(); var lb = eb.Cast<object?>().ToList();
            return la.Count == lb.Count && la.Zip(lb).All(z => DeepEq(z.First, z.Second, depth + 1, ignoreUnknown));
        }
        var t = a.GetType();
        // Structs (FormKey, P3Float, ...) define value equality and keep their data in fields, so Equals is the answer.
        if (t.IsValueType) return false;
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead || p.GetIndexParameters().Length > 0 || SkipNested(t, p)) continue;
            if (ignoreUnknown && p.Name.StartsWith("Unknown")) continue;
            if (!DeepEq(p.GetValue(a), p.GetValue(b), depth + 1, ignoreUnknown)) return false;
        }
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            if (!(ignoreUnknown && f.Name.StartsWith("Unknown")) && !DeepEq(f.GetValue(a), f.GetValue(b), depth + 1, ignoreUnknown)) return false;
        return true;
    }

    // A hash that follows DeepEq's walk (lists in order, then public properties and fields), for grouping long lists.
    // Values DeepEq finds equal by walking get the same hash.
    public static int DeepHash(object? v, int depth, bool ignoreUnknown = false)
    {
        if (v is null) return 0;
        if (depth > 12) return 1;
        if (v is string s) return s.GetHashCode();
        if (v is Mutagen.Bethesda.Strings.ITranslatedStringGetter ts) return (ts.String ?? "").GetHashCode();   // as DeepEq
        var t = v.GetType();
        if (SfModCheck.Overrides.BlobHash(v) is int blobHash) return blobHash;
        if (t.IsValueType && v is not IEnumerable) return v.GetHashCode();   // slices hash by entries, as DeepEq walks them (0.5.0)
        var h = new HashCode();
        h.Add(t);
        try
        {
            if (v is IEnumerable e)
            {
                foreach (var x in e) h.Add(DeepHash(x, depth + 1, ignoreUnknown));
                return h.ToHashCode();
            }
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0 || SkipNested(t, p)) continue;
                if (ignoreUnknown && p.Name.StartsWith("Unknown")) continue;
                h.Add(DeepHash(p.GetValue(v), depth + 1, ignoreUnknown));
            }
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                if (!(ignoreUnknown && f.Name.StartsWith("Unknown"))) h.Add(DeepHash(f.GetValue(v), depth + 1, ignoreUnknown));
        }
        catch (Exception) { return 2; }
        return h.ToHashCode();
    }

    // Registration metadata, and XCLC flags, which hold leftover junk bytes that differ in every official file.
    // Since 0.5.0 DeepEq compares a CellGrid's real land-flag byte itself (Overrides.CellGridEq) before this walk;
    // DeepHash still skips Flags, which keeps equal grids on equal hashes.
    static bool SkipNested(Type t, PropertyInfo p) =>
        p.Name is "StaticRegistration" or "Registration"
        || (p.Name == "Flags" && t.Name.Contains("CellGrid"));

    public static string Show(IMajorRecordGetter rec, string field, int max = 600)
    {
        try
        {
            var p = rec.GetType().GetProperty(field);
            var s = p is null ? "?" : Dump(Get(p, rec)).Replace("\n", " ").Replace("|", "/");
            return s.Length > max ? s[..max] + "…" : s;
        }
        catch (Exception e) { SfModCheck.Overrides.OnUnreadable?.Invoke(); return $"(unreadable: {e.GetType().Name})"; }
    }

    static string Dump(object? v, int depth = 0)
    {
        if (v is null) return "null";
        if (v is Mutagen.Bethesda.Plugins.IFormLinkGetter fl) return fl.FormKey.ToString();
        if (v is Mutagen.Bethesda.Strings.ITranslatedStringGetter ts) return $"\"{Impact.OneLine(ts.String)}\"";
        if (v is Mutagen.Bethesda.Starfield.ICellGridGetter g) return $"CellGrid{{Point={g.Point}, LandFlags={SfModCheck.Overrides.LandFlagsText(g)}}}";   // without the junk bytes (0.5.0)
        var t = v.GetType();
        if (v is IEnumerable e and not string)
            return "[" + string.Join("; ", e.Cast<object?>().Take(20).Select(x => Dump(x, depth + 1))) + "]";
        if (v is string || t.IsPrimitive || t.IsEnum || depth > 3 || t.Namespace?.StartsWith("Mutagen") != true) return v.ToString() ?? "";
        return t.Name.Replace("BinaryOverlay", "") + "{" + string.Join(", ", t.GetProperties().Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && !SkipNested(t, p))
            .Select(p => p.Name + "=" + Dump(p.GetValue(v), depth + 1))) + "}";
    }
}
