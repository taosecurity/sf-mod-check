// itm: overrides that are identical to the master version they override ("identical to master", ITM). Such a record
// changes nothing itself, but it still wins over earlier mods' edits to the same record, so it can quietly undo them.
// The comparison comes from an earlier stand-alone ITM checker (2026-10-28).
//   - the master version is the winning one among the plugin's masters (Mutagen link cache)
//   - every public field is compared by value: lists item by item, structs by Equals, byte blobs byte by byte
//   - child records (a cell's placed references, a quest's topics and scenes, worldspace blocks) are separate records
//     with their own overrides, so they are left out of their parent's comparison
//   - header bookkeeping is ignored: version control, form version, timestamps, and the Compressed flag
//     (compressing a record doesn't change it). Deleted and partial-form flags do count.
using System.Collections;
using System.Reflection;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using Noggog;

namespace SfModCheck;

public static class Overrides
{
    const int CompressedFlag = 0x00040000;
    static readonly bool Profile = Environment.GetEnvironmentVariable("SFMODCHECK_TIMING") == "1";

    static readonly HashSet<string> Ignored = new(StringComparer.Ordinal)
    {
        "VersionControl", "Version2", "FormVersion", "Timestamp", "TemporaryTimestamp", "UnknownGroupData",
        "TemporaryUnknownGroupData", "IsCompressed", "MajorRecordFlagsRaw",
        // derived or registration data, the same for both sides or not record content
        "Registration", "StaticRegistration", "ContainedFormLinks",
    };

    /// <summary>The overrides identical to their master version, and how many couldn't be compared.</summary>
    /// <param name="compared">0.26.0 (summary): called with (override, master version) for each override whose master
    /// version was found and is of the same type, including the parents the ITM test keeps without comparing (a cell
    /// with placed references), so a caller can name the fields that changed with the same master lookup. An exception
    /// it throws counts the override as not compared. check and triage pass nothing, so nothing changes for them.</param>
    public static (List<IMajorRecordGetter> Itms, int NotCompared) FindItms(IMajorRecordGetterEnumerable plugin,
        IEnumerable<IMajorRecordGetter> overrides, ILinkCache masters, Action<IMajorRecordGetter, IMajorRecordGetter>? compared = null)
    {
        var itms = new List<IMajorRecordGetter>();
        int failed = 0, noMaster = 0, otherType = 0, flagsDiffer = 0, comparedCount = 0, parents = 0;
        ModKey? modKey = (plugin as IModGetter)?.ModKey;   // a cell of the plugin's own is new: no master version expected
        bool timing = Environment.GetEnvironmentVariable("SFMODCHECK_TIMING") == "1";
        var tResolve = new System.Diagnostics.Stopwatch();
        var perType = new Dictionary<string, (double Resolve, double Compare)>();

        // Records inside a cell (placed references, navmeshes, landscape) are found through their parent cell: asking
        // the link cache for one of them makes Mutagen index every one in Starfield.esm first (a mod with 283 overrides, 2026-11-09: 12 s
        // for its placed references and navmeshes, against about 2.5 s for the cells).
        var parentCell = new Dictionary<FormKey, FormKey>();
        foreach (var cell in plugin.EnumerateMajorRecords<ICellGetter>())
            foreach (var child in cell.EnumerateMajorRecords())
                if (child.FormKey != cell.FormKey) parentCell.TryAdd(child.FormKey, cell.FormKey);
        // Every master's version of a cell, the winning one first. Cells themselves are looked up here too, not with
        // TryResolve: Mutagen keeps a separate cell index for each of the two calls, about 2 GB each for Starfield.esm,
        // so a mod that overrides cells and their children needed both (a test mod: 4.35 GB peak; 0.10.0 relay step 13).
        var cellVersions = new Dictionary<FormKey, List<ICellGetter>>();
        List<ICellGetter> CellVersions(FormKey cellKey)
        {
            if (!cellVersions.TryGetValue(cellKey, out var vs)) cellVersions[cellKey] = vs = masters.ResolveAll<ICellGetter>(cellKey).ToList();
            return vs;
        }
        var cellChildren = new Dictionary<FormKey, List<Dictionary<FormKey, IMajorRecordGetter>>>();
        IMajorRecordGetter? InCells(FormKey cellKey, FormKey fk)
        {
            if (!cellChildren.TryGetValue(cellKey, out var versions))
            {
                versions = CellVersions(cellKey)
                    .Select(c => { var d = new Dictionary<FormKey, IMajorRecordGetter>(); foreach (var ch in c.EnumerateMajorRecords()) d.TryAdd(ch.FormKey, ch); return d; })
                    .ToList();
                cellChildren[cellKey] = versions;
            }
            foreach (var v in versions) if (v.TryGetValue(fk, out var r)) return r;
            return null;
        }

        foreach (var rec in overrides)
        {
            try
            {
                // A parent that holds child records in the plugin (a cell with placed references, a worldspace with
                // cells, a quest or topic with dialogue) has to be there for its children, even when it changed nothing
                // itself; xEdit's "Remove ITM" keeps those too. (Sweep 2026-11-09 over three real
                // plugins, a conflict patch among them.)
                bool parent = rec is IMajorRecordGetterEnumerable holder && holder.EnumerateMajorRecords().Any(c => c.FormKey != rec.FormKey);
                if (parent && compared == null) { parents++; continue; }
                var started = tResolve.Elapsed;
                tResolve.Start();
                IMajorRecordGetter? orig;
                bool viaCell = parentCell.TryGetValue(rec.FormKey, out var ck);
                if (viaCell)
                {
                    orig = InCells(ck, rec.FormKey);
                    // The cell came back with no master version at all although the plugin's cell overrides one: the
                    // lookup failed, so this record couldn't be compared (a WARNING, not a silent "different"). With
                    // the .NET heap capped at 1 GB, ResolveAll on a test mod's cells came back empty for 269 children
                    // (found in the 0.8.0 triage work, 2026-11-19). A direct TryResolve isn't tried: it makes Mutagen index
                    // every placed reference in Starfield.esm (see above), and under that cap it came back empty too.
                    // A child missing from a cell that was found (moved or injected) stays "not identical", as before.
                    if (orig == null && cellChildren.TryGetValue(ck, out var vs) && vs.Count == 0 && ck.ModKey != modKey)
                    { tResolve.Stop(); if (parent) parents++; else failed++; continue; }
                }
                else if (rec is ICellGetter) orig = CellVersions(rec.FormKey).FirstOrDefault();   // the winning version, as TryResolve gives
                else orig = masters.TryResolve(rec.FormKey, rec.Registration.GetterType, out var o) ? o : null;
                tResolve.Stop();
                // only with a callback: a parent is handed over for its fields, then kept as before (never an ITM)
                if (parent)
                {
                    parents++;
                    if (orig != null && orig.GetType() == rec.GetType()) compared!(rec, orig);
                    continue;
                }
                var resolveSecs = (tResolve.Elapsed - started).TotalSeconds;
                bool same = false;
                IMajorRecordGetter? pair = null;
                var cmp = System.Diagnostics.Stopwatch.StartNew();
                // not found, or another type: the links rule reports injected and type-changing overrides
                if (orig == null) noMaster++;
                else if (orig.GetType() != rec.GetType()) otherType++;
                else if ((rec.MajorRecordFlagsRaw & ~CompressedFlag) != (orig.MajorRecordFlagsRaw & ~CompressedFlag)) { flagsDiffer++; pair = orig; }
                else { comparedCount++; same = SameFields(rec, orig, 0); pair = orig; }
                if (same) itms.Add(rec);
                if (pair != null) compared?.Invoke(rec, pair);
                if (timing)
                {
                    var tn = rec.Registration.ClassType.Name;
                    var (r0, c0) = perType.GetValueOrDefault(tn);
                    perType[tn] = (r0 + resolveSecs, c0 + cmp.Elapsed.TotalSeconds);
                }
            }
            catch (Exception e) when (Fatal.Recoverable(e)) { tResolve.Stop(); failed++; }
        }
        Log.Write($"itm: {parents} parents kept, {noMaster} with no master version found, {otherType} of another type, " +
                  $"{flagsDiffer} with other flags, {comparedCount} compared field by field, {itms.Count} identical, {failed} couldn't be compared");
        if (timing)
            Console.Error.WriteLine("itm: " + string.Join(", ", perType.OrderByDescending(k => k.Value.Resolve + k.Value.Compare)
                .Select(k => $"{k.Key} {k.Value.Resolve:F2}+{k.Value.Compare:F2} s")));
        return (itms, failed);
    }

    /// <summary>The properties compared on a type: readable, not indexers, not ignored, not child records. Texts come
    /// last: the first text read from a master loads that master's whole strings file (about 1 s; a large mod's
    /// factions 2026-11-09), and a record that differs elsewhere never needs it. A new array each call: DeepCompare.Itm
    /// calls it once per type and keeps the list (0.5.3: it was cached twice; read the cached list through
    /// DeepCompare.Itm.Properties).</summary>
    internal static PropertyInfo[] FindCompared(Type t) => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && !Ignored.Contains(p.Name) && !HoldsRecords(p.PropertyType))
        .OrderBy(p => typeof(Mutagen.Bethesda.Strings.ITranslatedStringGetter).IsAssignableFrom(p.PropertyType) ? 1 : 0)
        .ToArray();

    /// <summary>A child record, a list of them, or a group/block that holds them.</summary>
    static bool HoldsRecords(Type t)
    {
        if (typeof(IMajorRecordGetter).IsAssignableFrom(t) || typeof(IMajorRecordGetterEnumerable).IsAssignableFrom(t)) return true;
        if (t == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(t)) return false;
        var elem = t.IsArray ? t.GetElementType() : t.GetInterfaces().Append(t)
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0];
        return elem != null && (typeof(IMajorRecordGetter).IsAssignableFrom(elem) || typeof(IMajorRecordGetterEnumerable).IsAssignableFrom(elem));
    }

    static bool SameFields(object a, object b, int depth)
    {
        foreach (var p in DeepCompare.Itm.Properties(a.GetType()))   // the comparer's own lists, so the top level can't drift from it
        {
            var sw = depth == 0 && Profile ? System.Diagnostics.Stopwatch.StartNew() : null;
            var eq = DeepCompare.Itm.Eq(p.GetValue(a), p.GetValue(b), depth + 1);
            if (sw != null && sw.Elapsed.TotalSeconds > 0.2) Console.Error.WriteLine($"itm: slow field {a.GetType().Name}.{p.Name} {sw.Elapsed.TotalSeconds:F2} s");
            if (!eq) return false;
        }
        foreach (var f in DeepCompare.Itm.Fields(a.GetType()))
            if (!DeepCompare.Itm.Eq(f.GetValue(a), f.GetValue(b), depth + 1)) return false;
        return true;
    }

    /// <summary>Two cell grids (XCLC) match when their point and land flags (the low byte) match; the 3 unused bytes are ignored.</summary>
    public static bool CellGridEq(ICellGridGetter a, ICellGridGetter b) =>
        a.Point.Equals(b.Point) && LandFlags(a) == LandFlags(b);

    /// <summary>A cell grid's land flags: the low byte of Mutagen's int Flags (the other 3 bytes are unused, often junk).</summary>
    public static uint LandFlags(ICellGridGetter g) => unchecked((uint)(int)g.Flags) & 0xFF;

    /// <summary>LandFlags for the reports' text ("0x01"), or "unreadable" when the field can't be read (0.5.1): the text
    /// then still names the grid point. Comparisons keep calling LandFlags, whose exception is counted as a read failure.</summary>
    public static string LandFlagsText(ICellGridGetter g)
    {
        try { return $"0x{LandFlags(g):X2}"; }
        catch (Exception e) when (Fatal.Recoverable(e)) { OnUnreadable?.Invoke(); return "unreadable"; }
    }

    /// <summary>Set by a caller that counts the parts of a value it couldn't read while describing it (conflict's "Tool
    /// limits hit"); called by LandFlagsText and the conflict report's describers when a read fails (0.5.1).</summary>
    [ThreadStatic] public static Action? OnUnreadable;

    /// <summary>Byte blobs (unknown data, navmesh geometry) compare by their bytes, not by slice positions in two files;
    /// null when a and b aren't the same kind of blob.</summary>
    public static bool? BlobEq(object a, object b) =>
        a.GetType() == b.GetType() && TryBlob(a, out var sa) && TryBlob(b, out var sb) ? sa.SequenceEqual(sb) : (bool?)null;

    /// <summary>The bytes of a byte blob (ReadOnlyMemorySlice, MemorySlice or byte[]); false when v isn't one. The one
    /// place that knows the blob types (BlobEq, BlobHash, the conflict report's text; code review 0.5.1).</summary>
    public static bool TryBlob(object v, out ReadOnlySpan<byte> bytes)
    {
        switch (v)
        {
            case ReadOnlyMemorySlice<byte> r: bytes = r.Span; return true;
            case MemorySlice<byte> m: bytes = m.Span; return true;
            case byte[] b: bytes = b; return true;
            default: bytes = default; return false;
        }
    }

    /// <summary>A hash of a byte blob's bytes (equal for every pair BlobEq calls equal); null when v isn't a blob.</summary>
    public static int? BlobHash(object v)
    {
        if (!TryBlob(v, out var s)) return null;
        var h = new HashCode();
        h.AddBytes(s);
        return h.ToHashCode();
    }

}
