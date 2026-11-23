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

    // Since 0.5.2 the comparison itself is DeepCompare (DeepCompare.cs), shared with check's itm rule; conflict keeps its
    // own settings there (Equals first, then the walk). ignoreUnknown: skip Mutagen's undocumented "Unknown*" members.
    public static bool DeepEq(object? a, object? b, int depth, bool ignoreUnknown = false) =>
        (ignoreUnknown ? DeepCompare.ConflictIgnoringUnknown : DeepCompare.Conflict).Eq(a, b, depth);

    // A hash that follows DeepEq's walk, for grouping long lists: values DeepEq finds equal by walking get the same hash.
    public static int DeepHash(object? v, int depth, bool ignoreUnknown = false) =>
        (ignoreUnknown ? DeepCompare.ConflictIgnoringUnknown : DeepCompare.Conflict).Hash(v, depth);

    /// <summary>The properties conflict's comparison walks on a nested value: every readable, non-indexer public one
    /// except Mutagen's registration data (and a cell grid's raw Flags, compared through CellGridEq instead).</summary>
    public static PropertyInfo[] NestedProperties(Type t) => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && !SkipNested(t, p)).ToArray();

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
        catch (Exception e) when (Fatal.Recoverable(e)) { SfModCheck.Overrides.OnUnreadable?.Invoke(); return $"(unreadable: {e.GetType().Name})"; }
    }

    static string Dump(object? v, int depth = 0)
    {
        if (v is null) return "null";
        if (v is Mutagen.Bethesda.Plugins.IFormLinkGetter fl) return fl.FormKey.ToString();
        if (v is Mutagen.Bethesda.Strings.ITranslatedStringGetter ts) return $"\"{SafeText.OneLine(ts.String)}\"";
        if (v is Mutagen.Bethesda.Starfield.ICellGridGetter g) return $"CellGrid{{Point={g.Point}, LandFlags={SfModCheck.Overrides.LandFlagsText(g)}}}";   // without the junk bytes (0.5.0)
        var t = v.GetType();
        if (v is IEnumerable e and not string)
            return "[" + string.Join("; ", e.Cast<object?>().Take(20).Select(x => Dump(x, depth + 1))) + "]";
        if (v is string || t.IsPrimitive || t.IsEnum || depth > 3 || t.Namespace?.StartsWith("Mutagen") != true) return v.ToString() ?? "";
        return t.Name.Replace("BinaryOverlay", "") + "{" + string.Join(", ", t.GetProperties().Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && !SkipNested(t, p))
            .Select(p => p.Name + "=" + Dump(p.GetValue(v), depth + 1))) + "}";
    }
}
