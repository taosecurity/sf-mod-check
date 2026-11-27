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
using SfModCheck.Conflict;

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
    /// <summary>0.22.0 (code review round 2): the entries in both versions (the old list less EntriesRemoved, the new
    /// less EntriesAdded), so the detail can still show a change inside a kept entry (its conditions or chance).</summary>
    internal (List<object> Old, List<object> New)? EntriesKept { get; set; }
    /// <summary>A placed object whose base object changed: the old and the new base (null when not a swap).</summary>
    public (RecKey? Old, RecKey? New)? BaseSwap { get; set; }
    /// <summary>0.22.0: the field-by-field detail of the change, as triage has it since 0.17.0 (TriageDetail): one line
    /// per changed value or list entry, a reordered list and dropped or added translations named. DetailNotShown counts
    /// the lines past TriageDetail.MaxPerRecord.</summary>
    public List<(string Field, DetailLine Line)> Detail { get; } = [];
    public int DetailNotShown { get; set; }
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
    internal static RecKey KeyOf(FormKey fk, ModKey self) =>
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
            error = $"Mutagen can't open {Path.GetFileName(ix.Path)} ({e.GetType().Name}: {SafeText.OneLine(e.Message)})";
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
            error = $"Mutagen stopped reading {Path.GetFileName(ix.Path)} ({e.GetType().Name}: {SafeText.OneLine(e.Message)})";
            return null;
        }
        return found;
    }

    /// <summary>The top-level fields compared: conflict's (FieldCompare.Fields: bookkeeping such as the form version and
    /// version control, and child records, left out; child records are compared as records of their own).</summary>
    public static PropertyInfo[] Compared(Type t) => Conflict.FieldCompare.Fields(t).Where(p => !DeepCompare.IsPadding(t, p.Name)).ToArray();

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

    /// <summary>The path the detail gives the leveled-list entries in both versions (numbered among themselves) when
    /// entries were also added or removed.</summary>
    public const string KeptEntries = "Entries (in both versions)";

    /// <summary>A form link's key as the record index has it, or null for a null link.</summary>
    static RecKey? LinkKey(object? v, ModKey self) =>
        v is IFormLinkGetter fl && !fl.IsNull ? KeyOf(fl.FormKey, self) : null;

    static object? Member(object o, string name) => o.GetType().GetProperty(name)?.GetValue(o);

    /// <summary>A leveled list's entries as (level, reference, count), read by name so every leveled type with an
    /// Entries list (LVLI, LVLN, LVLB, LVLP and others) works; null when the record has none that read this way.</summary>
    static List<(LeveledEntry Key, object Entry)>? EntriesOf(IMajorRecordGetter r, ModKey self)
    {
        if (Member(r, "Entries") is not System.Collections.IEnumerable list) return null;
        var l = new List<(LeveledEntry, object)>();
        foreach (var e in list)
        {
            if (e == null) continue;
            var t = e.GetType();
            if (t.GetProperty("Reference") is not { } refProp) return null;
            static int Num(object? v) => v == null ? 0 : Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture);
            l.Add((new LeveledEntry(Num(Member(e, "Level")), LinkKey(refProp.GetValue(e), self), Num(Member(e, "Count"))), e));
        }
        return l;
    }

    /// <summary>Fills the entries only in the new version and only in the old one (as multisets: an entry listed twice
    /// and then once is one removed).</summary>
    static void Entries(ChangedRecord c, IMajorRecordGetter a, IMajorRecordGetter b, string oldPath, string newPath, string name)
    {
        List<(LeveledEntry Key, object Entry)>? ea, eb;
        try
        {
            ea = EntriesOf(a, ModKey.FromFileName(Path.GetFileName(oldPath)));
            eb = EntriesOf(b, ModKey.FromFileName(Path.GetFileName(newPath)));
        }
        catch (Exception e) when (Fatal.Recoverable(e))
        {
            Log.Write($"diff: {c.Old.Sig} {c.Key.Show(name)}: entries can't be read ({e.GetType().Name}: {SafeText.OneLine(e.Message)})");
            return;
        }
        if (ea == null || eb == null) return;
        // Pair the old and the new entries as multisets by (level, reference, count). Within one key, an entry equal in
        // every field is paired first, then the rest in order, so an unchanged entry isn't paired with a removed one.
        // The kept pairs are listed in the new order on both sides, so the detail compares each entry with its own old
        // copy even when the kept entries moved (code review 0.22.0 round 3).
        var matchedOld = new int[eb.Count];
        Array.Fill(matchedOld, -1);
        var usedOld = new bool[ea.Count];
        for (int pass = 0; pass < 2; pass++)
            for (int j = 0; j < eb.Count; j++)
            {
                if (matchedOld[j] >= 0) continue;
                for (int i = 0; i < ea.Count; i++)
                {
                    if (usedOld[i] || !ea[i].Key.Equals(eb[j].Key)) continue;
                    if (pass == 0 && !Equals(ea[i].Entry, eb[j].Entry)) continue;
                    usedOld[i] = true; matchedOld[j] = i; break;
                }
            }
        var keptOld = new List<object>();
        var keptNew = new List<object>();
        var added = new List<LeveledEntry>();
        for (int j = 0; j < eb.Count; j++)
            if (matchedOld[j] >= 0) { keptOld.Add(ea[matchedOld[j]].Entry); keptNew.Add(eb[j].Entry); }
            else added.Add(eb[j].Key);
        var removed = new List<LeveledEntry>();
        for (int i = 0; i < ea.Count; i++) if (!usedOld[i]) removed.Add(ea[i].Key);
        c.EntriesAdded = added;
        c.EntriesRemoved = removed;
        c.EntriesKept = (keptOld, keptNew);
        c.OtherEntryChanges = c.EntriesAdded.Count == 0 && c.EntriesRemoved.Count == 0;
    }

    /// <summary>Fills the old and the new base object of a placed object whose Base field differs.</summary>
    static void BaseSwap(ChangedRecord c, IMajorRecordGetter a, IMajorRecordGetter b, string oldPath, string newPath, string name)
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
            Log.Write($"diff: {c.Old.Sig} {c.Key.Show(name)}: base object can't be read ({e.GetType().Name}: {SafeText.OneLine(e.Message)})");
        }
    }

    /// <summary>0.22.0: fills c.Detail with TriageDetail's walk of each field that differs, and (when only one version is
    /// localized) of the equal fields too, for the translation kinds. A field the walk can't read keeps its name in
    /// c.Fields and gets no detail lines (logged).</summary>
    static void FieldDetail(ChangedRecord c, IMajorRecordGetter a, IMajorRecordGetter b, string? translations, string name)
    {
        var detail = new TriageDetail();
        var differ = c.Fields.ToHashSet(StringComparer.Ordinal);
        foreach (var p in Compared(a.GetType()))
        {
            bool changed = differ.Contains(p.Name);
            if (!changed && translations is null) continue;
            // Already reported in their own lines: the leveled-list entries added and removed, and a base-object swap.
            // For the entries, only those in both versions are walked, so a change inside a kept entry (its conditions or
            // chance) still shows (code review 0.22.0 round 2).
            bool keptOnly = p.Name == "Entries" && c.EntriesAdded != null && !c.OtherEntryChanges && c.EntriesKept != null;
            if (p.Name == "Base" && c.BaseSwap != null) continue;
            try
            {
                object? va = p.GetValue(a), vb = p.GetValue(b);
                if (keptOnly)
                {
                    var (ko, kn) = c.EntriesKept!.Value;
                    var (kl, _) = detail.Field(KeptEntries, ko, kn, translations);
                    foreach (var l in kl) c.Detail.Add((p.Name, l));
                    continue;
                }
                if (p.Name == "MajorRecordFlagsRaw" && va is int fa && vb is int fb) { va = fa & ~CompressedFlag; vb = fb & ~CompressedFlag; }
                var (lines, _) = detail.Field(p.Name, va, vb, translations, knownEqual: !changed);
                foreach (var l in lines) c.Detail.Add((p.Name, l));
            }
            catch (Exception e) when (Fatal.Recoverable(e))
            {
                Log.Write($"diff: {c.Old.Sig} {c.Key.Show(name)}: no detail for {p.Name} ({e.GetType().Name}: {SafeText.OneLine(e.Message)})");
            }
        }
        c.DetailNotShown = detail.NotShown;
        Log.Write($"diff: {c.Old.Sig} {c.Key.Show(name)}: {c.Detail.Count} detail line(s)" +
                  (c.DetailNotShown > 0 ? $", {c.DetailNotShown} more past the limit of {TriageDetail.MaxPerRecord}" : "") +
                  (translations is null ? "" : $"; one version is localized and the other isn't, so equal fields were walked for translations"));
    }

    /// <summary>Compares the fields of every record in d.Changed. A record whose fields are all equal moves to d.Noise
    /// (its type's Changed count goes down and Noise up); the rest keep the fields that differ. When a plugin can't be
    /// read with Mutagen, or a record isn't found there, the record stays changed with NotCompared saying why.</summary>
    /// <param name="name">The plugin as the rest of the log shows it (its spelling on disk); default the new file's name.</param>
    public static void Apply(PluginDiff d, string? name = null)
    {
        name ??= Path.GetFileName(d.New.Path);
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
                Log.Write($"diff: {name}: fields not compared: {why}");
                return;
            }
            var translations = TriageDetail.TranslationKind((d.Old.Header.Flags & Flags.Localized) != 0, (d.New.Header.Flags & Flags.Localized) != 0);
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
                if (c.Fields.Contains("Entries")) Entries(c, a, b, d.Old.Path, d.New.Path, name);
                if (c.Fields.Contains("Base")) BaseSwap(c, a, b, d.Old.Path, d.New.Path, name);
                if (c.Fields.Count > 0) { FieldDetail(c, a, b, translations, name); keep.Add(c); continue; }
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
