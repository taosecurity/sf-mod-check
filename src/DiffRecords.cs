// diff (0.11.0) step 2: the plugin header compare and the raw record index. Each plugin is walked once with RawWalker
// (memory-mapped, record headers only), each record's body is unpacked when compressed, its EditorID read and its
// bytes hashed, and the record is keyed by its global FormKey: the file it belongs to plus its local ID. The master
// index in a stored FormID is looked up in the plugin's own master list for that FormID's style (full, medium or
// light; Starfield keeps one list per style), so a reordered or extended master list causes no false changes.
// NOTES.md "0.11.0 diff relay" has the design and the CSE check of the key rule.
using System.Security.Cryptography;
using System.Text;

namespace SfModCheck;

/// <summary>A record's global key: File is the lower-case master name, or "" for the plugin itself (so two versions
/// with different file names still pair their own records); Local is masked the way Mutagen's FormKey has it.</summary>
public readonly record struct RecKey(string File, uint Local)
{
    public bool Own => File.Length == 0;
    public string Show(string self) => $"{Local:X6}:{(Own ? self : File)}";
}

/// <summary>One record as the index keeps it: type, the FormID as stored, EditorID (null = none), a hash of what counts
/// for "changed by bytes" (the record flags without the compressed bit, and the unpacked body), and where it is. Cell is
/// the key of the cell the record sits under (placed objects, navmeshes and the other cell children), or null. World
/// (0.26.0, summary) is the key of the worldspace it sits under, or null; Flags is the record header's flags as stored.</summary>
public readonly record struct RecInfo(string Sig, uint FormId, string? EditorId, UInt128 Hash, long Offset, RecKey? Cell = null,
                                      RecKey? World = null, uint Flags = 0);

/// <summary>Added, removed, changed, noise, renumbered and same counts for one record type, own records or overrides.
/// Changed = changed by bytes until the field compare (DiffFields.Apply) moves the records whose fields are all equal
/// to Noise; noise is no difference.</summary>
public sealed class DiffCounts
{
    public int Added, Removed, Changed, Noise, Renumbered, Same;
    public bool Any => Added + Removed + Changed + Renumbered > 0;
}

/// <summary>The record index of one plugin.</summary>
public sealed class RecordIndex
{
    public required string Path { get; init; }
    public required RawHeader Header { get; init; }
    public Dictionary<RecKey, RecInfo> Records { get; } = [];
    /// <summary>Master name to the style its own header says, or Full when the master wasn't found (Unknown lists those).</summary>
    public Dictionary<string, Style> MasterStyles { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Masters whose header wasn't found: their style was taken as full.</summary>
    public List<string> UnknownMasters { get; } = [];
    /// <summary>Records whose FormID names a master index past the end of its list (keyed by the stored FormID).</summary>
    public int PastEnd;
    /// <summary>Records with the same key as an earlier one in this plugin (only the first is kept).</summary>
    public int Duplicates;
    /// <summary>Compressed records that don't unpack: compared by their stored bytes instead.</summary>
    public int Unpackable;
    public int Walked;
    IdMap ids = null!;

    /// <summary>The key of a FormID as this plugin stores it: the master it names in the master list of its style, or
    /// the plugin itself (""). pastEnd: the index names no master, so the key is the stored FormID under "(no ... master)".</summary>
    public RecKey KeyOf(uint storedFormId, out bool pastEnd)
    {
        var (file, local, written) = ids.Decode(storedFormId);
        pastEnd = file == null;
        if (file == null) return new($"(no {written.ToString().ToLowerInvariant()} master)", storedFormId);
        if (file.Length == 0) return new("", IdMap.Mask(local, Header.Style));
        return new(file.ToLowerInvariant(), IdMap.Mask(local, MasterStyles[file]));
    }

    /// <summary>Indexes one plugin. masterHeader finds a master's header by name (null = not found). Returns null and
    /// the reason when the file can't be read as a plugin.</summary>
    public static RecordIndex? Build(string path, Func<string, RawHeader?> masterHeader, out string? error)
    {
        error = null;
        var h = RawHeader.Read(path);
        if (h == null) { error = $"{path} is not a plugin: it doesn't start with a TES4 header. Check that it is a Starfield plugin; if it is, the download is damaged, so download that version again."; return null; }
        var ix = new RecordIndex { Path = path, Header = h };
        foreach (var m in h.Masters)
        {
            if (ix.MasterStyles.ContainsKey(m)) continue;
            var mh = BuildKit.GameData.IsPlainPluginName(m) ? masterHeader(m) : null;
            ix.MasterStyles[m] = mh?.Style ?? Style.Full;
            if (mh == null) ix.UnknownMasters.Add(m);
        }
        ix.ids = new IdMap("", h.Style, h.Masters.Select(m => (m, ix.MasterStyles[m])));
        using var f = new RawFile(path);
        var w = new RawWalker(f, deep: false, (_, _, _) => { });
        w.OnRecord = rec =>
        {
            ix.Walked++;
            var key = ix.KeyOf(rec.FormId, out var pastEnd);
            if (pastEnd) ix.PastEnd++;
            var info = Read(f, rec, ref ix.Unpackable);
            if (w.CurrentCell is { } cell) info = info with { Cell = ix.KeyOf(cell, out _) };
            if (w.CurrentWorld is { } world) info = info with { World = ix.KeyOf(world, out _) };
            if (!ix.Records.TryAdd(key, info)) ix.Duplicates++;
        };
        w.Walk();
        if (w.StoppedEarly != null) { error = $"{path} can't be read to the end ({w.StoppedEarly}); the file is damaged. Download that version again, or ask its author for a working copy."; return null; }
        return ix;
    }

    static RecInfo Read(RawFile f, RawRecord rec, ref int unpackable)
    {
        var stored = f.Span(rec.Offset + 24, (int)rec.Size);
        ReadOnlySpan<byte> body = stored;
        bool packed = false;   // compressed and won't unpack: compared by stored bytes, no EditorID (the bytes aren't fields)
        if ((rec.Flags & Flags.Compressed) != 0)
        {
            if (Subrecords.Decompress(stored, out _) is { } d) body = d;
            else { unpackable++; packed = true; }
        }
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> flags = stackalloc byte[4];
        BitConverter.TryWriteBytes(flags, rec.Flags & ~Flags.Compressed);
        sha.AppendData(flags);
        sha.AppendData(body);
        Span<byte> hash = stackalloc byte[32];
        sha.GetHashAndReset(hash);
        return new(string.Intern(rec.Sig), rec.FormId, packed ? null : EditorId(body), BitConverter.ToUInt128(hash), rec.Offset, Flags: rec.Flags);
    }

    /// <summary>The EDID field's text in a record body, or null.</summary>
    public static string? EditorId(ReadOnlySpan<byte> body)
    {
        int i = 0;
        uint? big = null;
        while (body.Length - i >= 6)
        {
            var size = (long)BitConverter.ToUInt16(body.Slice(i + 4, 2));
            bool edid = body[i] == 'E' && body[i + 1] == 'D' && body[i + 2] == 'I' && body[i + 3] == 'D';
            if (body[i] == 'X' && body[i + 1] == 'X' && body[i + 2] == 'X' && body[i + 3] == 'X' && size == 4 && body.Length - i >= 10)
            {
                big = BitConverter.ToUInt32(body.Slice(i + 6, 4));
                i += 10;
                continue;
            }
            if (big != null) { size = big.Value; big = null; }
            if (i + 6 + size > body.Length) return null;
            if (edid) return Encoding.UTF8.GetString(body.Slice(i + 6, (int)size)).TrimEnd('\0');
            i += 6 + (int)size;
        }
        return null;
    }
}

/// <summary>One header difference; Real ones make the plugin count as changed, the rest (form version, the HEDR
/// record count, next object ID and version) are shown for information.</summary>
public sealed record HeaderChange(string Text, bool Real);

/// <summary>What changed between two versions of one plugin, by records.</summary>
public sealed class PluginDiff
{
    public required RecordIndex Old { get; init; }
    public required RecordIndex New { get; init; }
    public List<HeaderChange> Header { get; } = [];
    /// <summary>Per record type: (own records, overrides).</summary>
    public SortedDictionary<string, (DiffCounts Own, DiffCounts Overrides)> Types { get; } = new(StringComparer.Ordinal);
    public List<(RecKey Key, RecInfo Rec)> Added { get; } = [];
    public List<(RecKey Key, RecInfo Rec)> Removed { get; } = [];
    /// <summary>Records changed by bytes; after DiffFields.Apply, only the real changes (with their fields).</summary>
    public List<ChangedRecord> Changed { get; } = [];
    /// <summary>Records whose bytes differ but whose fields are all equal (filled by DiffFields.Apply).</summary>
    public List<ChangedRecord> Noise { get; } = [];
    /// <summary>Why the fields weren't compared at all (a plugin Mutagen can't read), or null.</summary>
    public string? FieldsNote { get; set; }
    /// <summary>The detail views (cells, leveled lists, base swaps), filled after the field compare.</summary>
    public DiffDetails Details { get; set; } = new();
    /// <summary>Own records with a new FormID: same type and EditorID, one removed and one added.</summary>
    public List<(RecKey OldKey, RecKey NewKey, RecInfo Old, RecInfo New)> Renumbered { get; } = [];

    public bool Differences => Header.Any(h => h.Real) || Types.Values.Any(t => t.Own.Any || t.Overrides.Any);

    public DiffCounts CountsOf(string sig, bool own) => Counts(sig, own);

    DiffCounts Counts(string sig, bool own)
    {
        if (!Types.TryGetValue(sig, out var t)) Types[sig] = t = (new DiffCounts(), new DiffCounts());
        return own ? t.Own : t.Overrides;
    }

    public DiffCounts Total(bool own)
    {
        var c = new DiffCounts();
        foreach (var t in Types.Values)
        {
            var x = own ? t.Own : t.Overrides;
            c.Added += x.Added; c.Removed += x.Removed; c.Changed += x.Changed; c.Noise += x.Noise; c.Renumbered += x.Renumbered; c.Same += x.Same;
        }
        return c;
    }

    public static PluginDiff Compare(RecordIndex oldIx, RecordIndex newIx)
    {
        var d = new PluginDiff { Old = oldIx, New = newIx };
        d.Header.AddRange(HeaderChanges(oldIx.Header, newIx.Header));
        var added = new List<(RecKey, RecInfo)>();
        var removed = new List<(RecKey, RecInfo)>();
        foreach (var (k, o) in oldIx.Records)
        {
            if (!newIx.Records.TryGetValue(k, out var n)) { removed.Add((k, o)); continue; }
            if (n.Sig != o.Sig) { removed.Add((k, o)); added.Add((k, n)); continue; }   // a record that changed type: the old one is gone, a new one came
            // a placed object moved to another cell keeps its bytes (the cell is the group it sits in), so a move is a change too
            bool movedCell = n.Cell != o.Cell;
            if (n.Hash == o.Hash && !movedCell) d.Counts(o.Sig, k.Own).Same++;
            else { d.Counts(o.Sig, k.Own).Changed++; d.Changed.Add(new ChangedRecord { Key = k, Old = o, New = n, SameBytes = n.Hash == o.Hash }); }
        }
        foreach (var (k, n) in newIx.Records)
            if (!oldIx.Records.ContainsKey(k)) added.Add((k, n));

        // renumbered: an own record removed and an own record added with the same type and EditorID, when that
        // EditorID names one record on each side
        static Dictionary<(string, string), List<(RecKey, RecInfo)>> ByName(IEnumerable<(RecKey K, RecInfo R)> l) =>
            l.Where(x => x.K.Own && !string.IsNullOrEmpty(x.R.EditorId)).GroupBy(x => (x.R.Sig, x.R.EditorId!)).ToDictionary(g => g.Key, g => g.ToList());
        var gone = ByName(removed);
        var came = ByName(added);
        var moved = new HashSet<RecKey>();
        var movedNew = new HashSet<RecKey>();
        foreach (var (name, olds) in gone.OrderBy(g => g.Key))
        {
            if (olds.Count != 1 || !came.TryGetValue(name, out var news) || news.Count != 1) continue;
            var (ok, orec) = olds[0]; var (nk, nrec) = news[0];
            d.Renumbered.Add((ok, nk, orec, nrec));
            d.Counts(orec.Sig, true).Renumbered++;
            moved.Add(ok); movedNew.Add(nk);
        }
        foreach (var (k, r) in removed.Where(x => !moved.Contains(x.Item1)).OrderBy(x => x.Item1.File, StringComparer.Ordinal).ThenBy(x => x.Item1.Local))
        {
            d.Removed.Add((k, r));
            d.Counts(r.Sig, k.Own).Removed++;
        }
        foreach (var (k, r) in added.Where(x => !movedNew.Contains(x.Item1)).OrderBy(x => x.Item1.File, StringComparer.Ordinal).ThenBy(x => x.Item1.Local))
        {
            d.Added.Add((k, r));
            d.Counts(r.Sig, k.Own).Added++;
        }
        d.Changed.Sort((a, b) => a.Key.File != b.Key.File ? string.CompareOrdinal(a.Key.File, b.Key.File) : a.Key.Local.CompareTo(b.Key.Local));
        return d;
    }

    static string StyleName(Style s) => s.ToString().ToLowerInvariant();

    /// <summary>The header differences: type, flags and masters are real; the rest is information.</summary>
    public static List<HeaderChange> HeaderChanges(RawHeader a, RawHeader b)
    {
        var l = new List<HeaderChange>();
        if (a.Style != b.Style) l.Add(new($"type: {StyleName(a.Style)} -> {StyleName(b.Style)}", true));
        var styleBits = Flags.Light | Flags.Medium;
        if ((a.Flags & ~styleBits) != (b.Flags & ~styleBits)) l.Add(new($"flags: {a.Flags:X8} -> {b.Flags:X8}", true));
        var am = a.Masters; var bm = b.Masters;
        foreach (var m in am.Where(m => !bm.Contains(m, StringComparer.OrdinalIgnoreCase))) l.Add(new($"master removed: {m}", true));
        foreach (var m in bm.Where(m => !am.Contains(m, StringComparer.OrdinalIgnoreCase))) l.Add(new($"master added: {m}", true));
        var keptA = am.Where(m => bm.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList();
        var keptB = bm.Where(m => am.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList();
        if (!keptA.SequenceEqual(keptB, StringComparer.OrdinalIgnoreCase)) l.Add(new($"masters reordered: {string.Join(", ", keptA)} -> {string.Join(", ", keptB)}", true));
        if (a.FormVersion != b.FormVersion) l.Add(new($"form version: {a.FormVersion} -> {b.FormVersion}", false));
        if (a.NumRecords != b.NumRecords) l.Add(new($"HEDR record count: {a.NumRecords?.ToString() ?? "none"} -> {b.NumRecords?.ToString() ?? "none"}", false));
        if (a.NextObjectId != b.NextObjectId) l.Add(new($"HEDR next object ID: {a.NextObjectId:X} -> {b.NextObjectId:X}", false));
        if (a.Version != b.Version) l.Add(new($"HEDR version: {a.Version} -> {b.Version}", false));
        return l;
    }
}
