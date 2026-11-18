// The two comparers as they were in 0.5.1 (Overrides.DeepEq for check's itm rule, FieldCompare.DeepEq/DeepHash for
// conflict), kept word for word as a reference so a test can prove the merged DeepCompare (0.5.2) gives the same answer,
// or throws the same error, on every value. Only change: DeepHash's catch uses 0.5.2's fatal-error rule (item 2).
using System.Collections;
using System.Reflection;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;

namespace SfModCheck.Tests;

public static class Old051
{
    // ---------- Overrides (check's itm rule) ----------
    static readonly HashSet<string> Ignored = new(StringComparer.Ordinal)
    {
        "VersionControl", "Version2", "FormVersion", "Timestamp", "TemporaryTimestamp", "UnknownGroupData",
        "TemporaryUnknownGroupData", "IsCompressed", "MajorRecordFlagsRaw",
        "Registration", "StaticRegistration", "ContainedFormLinks",
    };

    static PropertyInfo[] Compared(Type t) => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && !Ignored.Contains(p.Name) && !HoldsRecords(p.PropertyType))
        .OrderBy(p => typeof(Mutagen.Bethesda.Strings.ITranslatedStringGetter).IsAssignableFrom(p.PropertyType) ? 1 : 0)
        .ToArray();

    static bool HoldsRecords(Type t)
    {
        if (typeof(IMajorRecordGetter).IsAssignableFrom(t) || typeof(IMajorRecordGetterEnumerable).IsAssignableFrom(t)) return true;
        if (t == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(t)) return false;
        var elem = t.IsArray ? t.GetElementType() : t.GetInterfaces().Append(t)
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0];
        return elem != null && (typeof(IMajorRecordGetter).IsAssignableFrom(elem) || typeof(IMajorRecordGetterEnumerable).IsAssignableFrom(elem));
    }

    public static bool ItmSameFields(object a, object b, int depth)
    {
        foreach (var p in Compared(a.GetType()))
            if (!ItmEq(p.GetValue(a), p.GetValue(b), depth + 1)) return false;
        foreach (var f in a.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            if (!ItmEq(f.GetValue(a), f.GetValue(b), depth + 1)) return false;
        return true;
    }

    public static bool ItmEq(object? a, object? b, int depth)
    {
        if (a is null || b is null) return a is null && b is null;
        if (Overrides.BlobEq(a, b) is bool blobs) return blobs;
        if (a is Mutagen.Bethesda.Strings.ITranslatedStringGetter ta && b is Mutagen.Bethesda.Strings.ITranslatedStringGetter tb)
            return string.Equals(ta.String, tb.String, StringComparison.Ordinal);
        if (a.GetType() != b.GetType()) return false;
        if (a is ICellGridGetter ga && b is ICellGridGetter gb) return Overrides.CellGridEq(ga, gb);
        if (a.GetType().IsValueType || a is string || a is Type) return Equals(a, b);
        if (ReferenceEquals(a, b)) return true;
        if (depth > 12) return false;
        if (a is IEnumerable ea && b is IEnumerable eb)
        {
            var la = ea.Cast<object?>().ToList(); var lb = eb.Cast<object?>().ToList();
            if (la.Count != lb.Count) return false;
            for (int i = 0; i < la.Count; i++) if (!ItmEq(la[i], lb[i], depth + 1)) return false;
            return true;
        }
        if (Compared(a.GetType()).Length == 0 && a.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance).Length == 0)
            return Equals(a, b);
        return ItmSameFields(a, b, depth);
    }

    // ---------- FieldCompare (conflict) ----------
    public static bool ConflictEq(object? a, object? b, int depth, bool ignoreUnknown = false)
    {
        if (Equals(a, b)) return true;
        if (a is null || b is null || depth > 12) return false;
        if (a is Mutagen.Bethesda.Strings.ITranslatedStringGetter ta && b is Mutagen.Bethesda.Strings.ITranslatedStringGetter tb)
            return string.Equals(ta.String, tb.String, StringComparison.Ordinal);
        if (a is string || b is string || a.GetType() != b.GetType()) return false;
        if (Overrides.BlobEq(a, b) is bool blobs) return blobs;
        if (a is ICellGridGetter ga && b is ICellGridGetter gb) return Overrides.CellGridEq(ga, gb);
        if (a is IEnumerable ea && b is IEnumerable eb)
        {
            var la = ea.Cast<object?>().ToList(); var lb = eb.Cast<object?>().ToList();
            return la.Count == lb.Count && la.Zip(lb).All(z => ConflictEq(z.First, z.Second, depth + 1, ignoreUnknown));
        }
        var t = a.GetType();
        if (t.IsValueType) return false;
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead || p.GetIndexParameters().Length > 0 || SkipNested(t, p)) continue;
            if (ignoreUnknown && p.Name.StartsWith("Unknown")) continue;
            if (!ConflictEq(p.GetValue(a), p.GetValue(b), depth + 1, ignoreUnknown)) return false;
        }
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            if (!(ignoreUnknown && f.Name.StartsWith("Unknown")) && !ConflictEq(f.GetValue(a), f.GetValue(b), depth + 1, ignoreUnknown)) return false;
        return true;
    }

    public static int ConflictHash(object? v, int depth, bool ignoreUnknown = false)
    {
        if (v is null) return 0;
        if (depth > 12) return 1;
        if (v is string s) return s.GetHashCode();
        if (v is Mutagen.Bethesda.Strings.ITranslatedStringGetter ts) return (ts.String ?? "").GetHashCode();
        var t = v.GetType();
        if (Overrides.BlobHash(v) is int blobHash) return blobHash;
        if (t.IsValueType && v is not IEnumerable) return v.GetHashCode();
        var h = new HashCode();
        h.Add(t);
        try
        {
            if (v is IEnumerable e)
            {
                foreach (var x in e) h.Add(ConflictHash(x, depth + 1, ignoreUnknown));
                return h.ToHashCode();
            }
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0 || SkipNested(t, p)) continue;
                if (ignoreUnknown && p.Name.StartsWith("Unknown")) continue;
                h.Add(ConflictHash(p.GetValue(v), depth + 1, ignoreUnknown));
            }
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                if (!(ignoreUnknown && f.Name.StartsWith("Unknown"))) h.Add(ConflictHash(f.GetValue(v), depth + 1, ignoreUnknown));
        }
        catch (Exception e) when (Fatal.Recoverable(e)) { return 2; }
        return h.ToHashCode();
    }

    static bool SkipNested(Type t, PropertyInfo p) =>
        p.Name is "StaticRegistration" or "Registration"
        || (p.Name == "Flags" && t.Name.Contains("CellGrid"));
}
