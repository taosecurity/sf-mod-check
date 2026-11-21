// diff (0.11.0) step 3: the field compare. Each record that changed by bytes (DiffRecords.cs) is read in both versions
// with Mutagen and compared field by field with conflict's rule set, leaving out the members Mutagen names as padding
// (DeepCompare.DiffIgnoringPadding). The top-level fields that differ are named; a record whose bytes differ but whose
// fields are all equal is resave noise (padding, field order, -0 and +0) and is counted as noise, not as a change. NOTES.md "0.11.0 diff relay" has the design.
using System.Reflection;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;

namespace SfModCheck;

/// <summary>One record changed by bytes. After the field compare, Fields names the top-level fields that differ, or
/// NotCompared says why the fields weren't compared (the record then still counts as changed).</summary>
public sealed class ChangedRecord
{
    public required RecKey Key { get; init; }
    public required RecInfo Old { get; init; }
    public required RecInfo New { get; init; }
    public List<string> Fields { get; } = [];
    public string? NotCompared { get; set; }
    /// <summary>The bytes are equal: only the parent cell changed (a move), so Mutagen isn't asked.</summary>
    public bool SameBytes { get; init; }
    /// <summary>The record sits under another cell than before.</summary>
    public bool Moved => Old.Cell != New.Cell;
    /// <summary>A leveled list whose entries changed: the entries only in the new version and only in the old one
    /// (null when the record has no entries or they weren't compared). OtherEntryChanges: the (level, reference, count)
    /// lists are the same, so something else in the entries changed (conditions, chance, order).</summary>
    public List<LeveledEntry>? EntriesAdded { get; set; }
    public List<LeveledEntry>? EntriesRemoved { get; set; }
    public bool OtherEntryChanges { get; set; }
    /// <summary>A placed object whose base object changed: the old and the new base (null when not a swap).</summary>
    public (RecKey? Old, RecKey? New)? BaseSwap { get; set; }
}

/// <summary>One leveled-list entry as the diff shows it: level, the record it gives, and how many.</summary>
public readonly record struct LeveledEntry(int Level, RecKey? Ref, int Count);

public static class DiffFields
{
    const int CompressedFlag = 0x40000;

    /// <summary>The master styles Mutagen needs to place the plugin's FormIDs, from the styles the record index already
    /// found (a master not found was taken as full there, so it is here too and both passes key records alike).</summary>
    static LoadOrder<IModMasterStyledGetter> Styles(RecordIndex ix) =>
        new(ix.MasterStyles.Select(kv => (IModMasterStyledGetter)new KeyedMasterStyle(ModKey.FromFileName(kv.Key), kv.Value switch
        {
            Style.Light => MasterStyle.Small,
            Style.Medium => MasterStyle.Medium,
            _ => MasterStyle.Full,
        })));

    /// <summary>The key the record index gives a record Mutagen read: "" for the plugin's own records.</summary>
    static RecKey KeyOf(FormKey fk, ModKey self) =>
        fk.ModKey == self ? new RecKey("", fk.ID) : new RecKey(fk.ModKey.FileName.String.ToLowerInvariant(), fk.ID);

    /// <summary>Reads the wanted records of one plugin with Mutagen. Returns null and the reason when it can't be opened.</summary>
    static Dictionary<RecKey, IMajorRecordGetter>? Read(RecordIndex ix, HashSet<RecKey> wanted, out IDisposable? handle, out string? error)
    {
        handle = null; error = null;
        IStarfieldModDisposableGetter mod;
        try
        {
            mod = StarfieldMod.CreateFromBinaryOverlay(ix.Path, StarfieldRelease.Starfield, new BinaryReadParameters { MasterFlagsLookup = Styles(ix) });
        }
        catch (Exception e) when (Fatal.Recoverable(e))
        {
            error = $"Mutagen can't open {Path.GetFileName(ix.Path)} ({e.GetType().Name}: {Conflict.Impact.OneLine(e.Message)})";
            return null;
        }
        handle = mod;
        var found = new Dictionary<RecKey, IMajorRecordGetter>();
        try
        {
            foreach (var rec in mod.EnumerateMajorRecords())
            {
                var k = KeyOf(rec.FormKey, mod.ModKey);
                if (wanted.Contains(k)) found.TryAdd(k, rec);   // the first of two with one key, as the index keeps it
            }
        }
        catch (Exception e) when (Fatal.Recoverable(e))
        {
            error = $"Mutagen stopped reading {Path.GetFileName(ix.Path)} ({e.GetType().Name}: {Conflict.Impact.OneLine(e.Message)})";
            return null;
        }
        return found;
    }

    /// <summary>The top-level fields compared: conflict's (FieldCompare.Fields: bookkeeping such as the form version and
    /// version control, and child records, left out; child records are compared as records of their own).</summary>
    public static PropertyInfo[] Compared(Type t) => Conflict.FieldCompare.Fields(t).Where(p => !DeepCompare.IsPadding(p.Name)).ToArray();

    /// <summary>The top-level fields where a and b differ. A field that can't be read counts as different
    /// ("Name (unreadable)"). The record flags are compared without the compressed bit; the partial-form bit counts.</summary>
    public static List<string> Differ(IMajorRecordGetter a, IMajorRecordGetter b)
    {
        var l = new List<string>();
        if (a.GetType() != b.GetType()) { l.Add("(record type)"); return l; }
        foreach (var p in Compared(a.GetType()))
        {
            try
            {
                object? va = p.GetValue(a), vb = p.GetValue(b);
                if (p.Name == "MajorRecordFlagsRaw" && va is int fa && vb is int fb) { if ((fa & ~CompressedFlag) != (fb & ~CompressedFlag)) l.Add(p.Name); continue; }
                if (!DeepCompare.DiffIgnoringPadding.Eq(va, vb, 0)) l.Add(p.Name);
            }
            catch (Exception e) when (Fatal.Recoverable(e)) { l.Add($"{p.Name} (unreadable)"); }
        }
        return l;
    }

    /// <summary>The name the field list gives a move to another cell.</summary>
    public const string ParentCell = "(parent cell)";

    /// <summary>A form link's key as the record index has it, or null for a null link.</summary>
    static RecKey? LinkKey(object? v, ModKey self) =>
        v is IFormLinkGetter fl && !fl.IsNull ? KeyOf(fl.FormKey, self) : null;

    static object? Member(object o, string name) => o.GetType().GetProperty(name)?.GetValue(o);

    /// <summary>A leveled list's entries as (level, reference, count), read by name so every leveled type with an
    /// Entries list (LVLI, LVLN, LVLB, LVLP and others) works; null when the record has none that read this way.</summary>
    static List<LeveledEntry>? EntriesOf(IMajorRecordGetter r, ModKey self)
    {
        if (Member(r, "Entries") is not System.Collections.IEnumerable list) return null;
        var l = new List<LeveledEntry>();
        foreach (var e in list)
        {
            if (e == null) continue;
            var t = e.GetType();
            if (t.GetProperty("Reference") is not { } refProp) return null;
            static int Num(object? v) => v == null ? 0 : Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture);
            l.Add(new LeveledEntry(Num(Member(e, "Level")), LinkKey(refProp.GetValue(e), self), Num(Member(e, "Count"))));
        }
        return l;
    }

    /// <summary>Fills the entries only in the new version and only in the old one (as multisets: an entry listed twice
    /// and then once is one removed).</summary>
    static void Entries(ChangedRecord c, IMajorRecordGetter a, IMajorRecordGetter b, string oldPath, string newPath)
    {
        List<LeveledEntry>? ea, eb;
        try
        {
            ea = EntriesOf(a, ModKey.FromFileName(Path.GetFileName(oldPath)));
            eb = EntriesOf(b, ModKey.FromFileName(Path.GetFileName(newPath)));
        }
        catch (Exception e) when (Fatal.Recoverable(e))
        {
            Log.Write($"diff: {c.Old.Sig} {c.Key.Show(System.IO.Path.GetFileName(newPath))}: entries can't be read ({e.GetType().Name}: {Conflict.Impact.OneLine(e.Message)})");
            return;
        }
        if (ea == null || eb == null) return;
        static List<LeveledEntry> Minus(List<LeveledEntry> x, List<LeveledEntry> y)
        {
            var left = y.GroupBy(e => e).ToDictionary(g => g.Key, g => g.Count());
            var l = new List<LeveledEntry>();
            foreach (var e in x)
                if (left.TryGetValue(e, out var n) && n > 0) left[e] = n - 1;
                else l.Add(e);
            return l;
        }
        c.EntriesAdded = Minus(eb, ea);
        c.EntriesRemoved = Minus(ea, eb);
        c.OtherEntryChanges = c.EntriesAdded.Count == 0 && c.EntriesRemoved.Count == 0;
    }

    /// <summary>Fills the old and the new base object of a placed object whose Base field differs.</summary>
    static void BaseSwap(ChangedRecord c, IMajorRecordGetter a, IMajorRecordGetter b, string oldPath, string newPath)
    {
        try
        {
            var pa = a.GetType().GetProperty("Base"); var pb = b.GetType().GetProperty("Base");
            if (pa == null || pb == null) return;
            c.BaseSwap = (LinkKey(pa.GetValue(a), ModKey.FromFileName(Path.GetFileName(oldPath))),
                          LinkKey(pb.GetValue(b), ModKey.FromFileName(Path.GetFileName(newPath))));
        }
        catch (Exception e) when (Fatal.Recoverable(e))
        {
            Log.Write($"diff: {c.Old.Sig} {c.Key.Show(System.IO.Path.GetFileName(newPath))}: base object can't be read ({e.GetType().Name}: {Conflict.Impact.OneLine(e.Message)})");
        }
    }

    /// <summary>Compares the fields of every record in d.Changed. A record whose fields are all equal moves to d.Noise
    /// (its type's Changed count goes down and Noise up); the rest keep the fields that differ. When a plugin can't be
    /// read with Mutagen, or a record isn't found there, the record stays changed with NotCompared saying why.</summary>
    public static void Apply(PluginDiff d)
    {
        if (d.Changed.Count == 0) return;
        foreach (var c in d.Changed.Where(c => c.SameBytes)) c.Fields.Add(ParentCell);   // a move only: nothing for Mutagen to compare
        var wanted = d.Changed.Where(c => !c.SameBytes).Select(c => c.Key).ToHashSet();
        if (wanted.Count == 0) return;
        IDisposable? ho = null, hn = null;
        try
        {
            string? why = null;
            Dictionary<RecKey, IMajorRecordGetter>? news = null;
            var olds = Read(d.Old, wanted, out ho, out var eo);
            if (olds == null) why = $"the old version: {eo}";
            else
            {
                news = Read(d.New, wanted, out hn, out var en);
                if (news == null) why = $"the new version: {en}";
            }
            if (why != null)
            {
                foreach (var c in d.Changed.Where(c => !c.SameBytes)) c.NotCompared = $"fields not compared: {why}";
                d.FieldsNote = why;
                Log.Write($"diff: {Path.GetFileName(d.New.Path)}: fields not compared: {why}");
                return;
            }
            var keep = new List<ChangedRecord>();
            foreach (var c in d.Changed)
            {
                if (c.SameBytes) { keep.Add(c); continue; }
                if (!olds!.TryGetValue(c.Key, out var a) || !news!.TryGetValue(c.Key, out var b))
                {
                    c.NotCompared = "fields not compared: Mutagen didn't find the record in " + (olds.ContainsKey(c.Key) ? "the new version" : "the old version");
                    keep.Add(c);
                    continue;
                }
                c.Fields.AddRange(Differ(a, b));
                if (c.Moved) c.Fields.Add(ParentCell);
                if (c.Fields.Contains("Entries")) Entries(c, a, b, d.Old.Path, d.New.Path);
                if (c.Fields.Contains("Base")) BaseSwap(c, a, b, d.Old.Path, d.New.Path);
                if (c.Fields.Count > 0) { keep.Add(c); continue; }
                var counts = d.CountsOf(c.Old.Sig, c.Key.Own);
                counts.Changed--; counts.Noise++;
                d.Noise.Add(c);
            }
            d.Changed.Clear();
            d.Changed.AddRange(keep);
        }
        finally
        {
            ho?.Dispose();
            hn?.Dispose();
        }
    }
}
