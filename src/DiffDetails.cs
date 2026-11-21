// diff (0.11.0) step 4: the detail views. Placed objects (and the other records that sit under a cell: navmeshes and
// the like) added, removed, changed and moved, grouped by their cell; leveled lists whose entries changed (entries
// added and removed); base-object swaps (a placed object whose base changed). EditorIDs come from the plugin itself
// first, then from the masters, found in the mod or the Data folder (--data, default Steam's); a master found in
// neither leaves its records as FormKeys, and the report says so. NOTES.md "0.11.0 diff relay" has the design.
using System.Text;
using System.Text.Json.Nodes;

namespace SfModCheck;

/// <summary>EditorIDs for the record keys a report shows: the plugin's own index first (new version, then old), then
/// a raw read of the master the key names (only the records asked for are unpacked).</summary>
public sealed class DiffNames
{
    readonly Dictionary<RecKey, string?> names = [];
    /// <summary>Masters whose records were asked for but that weren't found (FormKeys only for those).</summary>
    public SortedSet<string> MissingMasters { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Masters that were read for EditorIDs, with where they were found.</summary>
    public List<(string Master, string Path)> Read { get; } = [];

    public string? Of(RecKey? k) => k is { } key && names.TryGetValue(key, out var n) ? n : null;

    /// <summary>Looks up every key. masterPath finds a master file by name (null = not found); masterHeader finds a
    /// header by name, for the styles of a master's own masters.</summary>
    public static DiffNames Resolve(PluginDiff d, IEnumerable<RecKey> keys, Func<string, string?> masterPath, Func<string, RawHeader?> masterHeader)
    {
        var n = new DiffNames();
        var wanted = new Dictionary<string, HashSet<uint>>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in keys.Distinct())
        {
            if (d.New.Records.TryGetValue(k, out var r) || d.Old.Records.TryGetValue(k, out r)) { n.names[k] = r.EditorId; continue; }
            if (k.Own || k.File.StartsWith('(')) { n.names[k] = null; continue; }
            if (!wanted.TryGetValue(k.File, out var set)) wanted[k.File] = set = [];
            set.Add(k.Local);
        }
        foreach (var (file, locals) in wanted.OrderBy(w => w.Key, StringComparer.OrdinalIgnoreCase))
        {
            var path = BuildKit.GameData.IsPlainPluginName(file) ? masterPath(file) : null;
            if (path == null) { n.MissingMasters.Add(file); Log.Write($"diff: EditorIDs: master {file} not found, {locals.Count} record(s) shown by FormKey only"); continue; }
            var found = ReadMaster(path, locals, masterHeader, out var error);
            if (found == null) { n.MissingMasters.Add(file); Log.Write($"diff: EditorIDs: master {file} at {path} can't be read: {error}"); continue; }
            n.Read.Add((file, path));
            Log.Write($"diff: EditorIDs: read {path} for {locals.Count} record(s), {found.Count} found");
            foreach (var l in locals) n.names[new RecKey(file.ToLowerInvariant(), l)] = found.GetValueOrDefault(l);
        }
        return n;
    }

    /// <summary>The EditorIDs of a master's own records with the given local IDs (masked to the master's style).</summary>
    static Dictionary<uint, string?>? ReadMaster(string path, HashSet<uint> locals, Func<string, RawHeader?> masterHeader, out string? error)
    {
        error = null;
        RawHeader? h;
        try { h = RawHeader.Read(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { error = e.Message; return null; }
        if (h == null) { error = "it doesn't start with a TES4 header"; return null; }
        var ids = new IdMap("", h.Style, h.Masters.Select(m => (m, (BuildKit.GameData.IsPlainPluginName(m) ? masterHeader(m)?.Style : null) ?? Style.Full)));
        var found = new Dictionary<uint, string?>();
        try
        {
            using var f = new RawFile(path);
            var w = new RawWalker(f, deep: false, (_, _, _) => { });
            w.OnRecord = rec =>
            {
                var (file, local, _) = ids.Decode(rec.FormId);
                if (file is not "") return;
                var l = IdMap.Mask(local, h.Style);
                if (!locals.Contains(l) || found.ContainsKey(l)) return;
                ReadOnlySpan<byte> body = f.Span(rec.Offset + 24, (int)rec.Size);
                if ((rec.Flags & Flags.Compressed) != 0) body = Subrecords.Decompress(body, out _) ?? [];
                found[l] = RecordIndex.EditorId(body);
            };
            w.Walk();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { error = e.Message; return null; }
        return found;
    }
}

/// <summary>What changed under one cell: counts per record type for added, removed, changed (fields, or moved in)
/// and moved out, and the records themselves.</summary>
public sealed class CellChanges
{
    public required RecKey Cell { get; init; }
    public SortedDictionary<string, int> Added { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, int> Removed { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, int> Changed { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, int> MovedOut { get; } = new(StringComparer.Ordinal);
    /// <summary>Change: "added", "removed", "changed" or "moved out"; From: the old cell of a record moved in; To: the
    /// new cell of a record moved out.</summary>
    public List<(string Change, RecKey Key, RecInfo Rec, RecKey? From, RecKey? To)> Records { get; } = [];
    public int Total => Records.Count;

    /// <summary>"+NAVM 2, +REFR 197, -REFR 87, ~REFR 8, >REFR 1" (+ added, - removed, ~ changed, > moved out).</summary>
    public string Summary()
    {
        var parts = new List<string>();
        foreach (var (mark, counts) in new[] { ("+", Added), ("-", Removed), ("~", Changed), (">", MovedOut) })
            foreach (var (sig, n) in counts) parts.Add($"{mark}{sig} {n:N0}");
        return string.Join(", ", parts);
    }
}

/// <summary>The detail views of one plugin's diff (built after the field compare).</summary>
public sealed class DiffDetails
{
    /// <summary>Cells with changes under them, most changes first.</summary>
    public List<CellChanges> Cells { get; } = [];
    /// <summary>Changed leveled lists whose entries were compared.</summary>
    public List<ChangedRecord> LeveledLists { get; } = [];
    /// <summary>Changed placed objects whose base object changed.</summary>
    public List<ChangedRecord> BaseSwaps { get; } = [];
    public DiffNames Names { get; set; } = new();

    public static DiffDetails Build(PluginDiff d)
    {
        var x = new DiffDetails();
        var cells = new Dictionary<RecKey, CellChanges>();
        CellChanges At(RecKey k) => cells.TryGetValue(k, out var c) ? c : cells[k] = new CellChanges { Cell = k };
        static void Count(SortedDictionary<string, int> m, string sig) => m[sig] = m.GetValueOrDefault(sig) + 1;
        foreach (var (k, r) in d.Added)
            if (r.Cell is { } c) { var cc = At(c); Count(cc.Added, r.Sig); cc.Records.Add(("added", k, r, null, null)); }
        foreach (var (k, r) in d.Removed)
            if (r.Cell is { } c) { var cc = At(c); Count(cc.Removed, r.Sig); cc.Records.Add(("removed", k, r, null, null)); }
        foreach (var ch in d.Changed)
        {
            if (ch.New.Cell is { } c) { var cc = At(c); Count(cc.Changed, ch.New.Sig); cc.Records.Add(("changed", ch.Key, ch.New, ch.Moved ? ch.Old.Cell : null, null)); }
            if (ch.Moved && ch.Old.Cell is { } oc) { var cc = At(oc); Count(cc.MovedOut, ch.Old.Sig); cc.Records.Add(("moved out", ch.Key, ch.Old, null, ch.New.Cell)); }
            if (ch.EntriesAdded != null) x.LeveledLists.Add(ch);
            if (ch.BaseSwap != null) x.BaseSwaps.Add(ch);
        }
        x.Cells.AddRange(cells.Values.OrderByDescending(c => c.Total).ThenBy(c => c.Cell.File, StringComparer.Ordinal).ThenBy(c => c.Cell.Local));
        return x;
    }

    /// <summary>Every key the views show (cells, records, entry references, base objects), for DiffNames.</summary>
    public IEnumerable<RecKey> Keys()
    {
        foreach (var c in Cells)
        {
            yield return c.Cell;
            foreach (var r in c.Records) { if (r.From is { } f) yield return f; if (r.To is { } t) yield return t; }
        }
        foreach (var l in LeveledLists)
            foreach (var e in l.EntriesAdded!.Concat(l.EntriesRemoved!))
                if (e.Ref is { } k) yield return k;
        foreach (var s in BaseSwaps)
        {
            if (s.BaseSwap!.Value.Old is { } o) yield return o;
            if (s.BaseSwap!.Value.New is { } n) yield return n;
        }
    }

    // ---- report ----

    /// <summary>Caps of the text report (the JSON and --all list everything).</summary>
    public const int CellsShown = 40, ListsShown = 25, EntriesShown = 20, SwapsShown = 50;

    string Named(RecKey? k, string self) => k is not { } key ? "(none)" : Names.Of(key) is { Length: > 0 } n ? $"{key.Show(self)} {n}" : key.Show(self);

    static string Entry(LeveledEntry e, Func<RecKey?, string> named) => $"level {e.Level}, {named(e.Ref)}, count {e.Count}";

    public void Text(StringBuilder sb, string self, bool all)
    {
        string N(RecKey? k) => Named(k, self);
        if (Names.MissingMasters.Count > 0)
            sb.AppendLine($"  note: EditorIDs of records in {string.Join(", ", Names.MissingMasters)} aren't shown (not found in the mod or the Data " +
                          "folder), so those records are shown by FormKey only. Give --data the game's Data folder.");
        if (Cells.Count > 0)
        {
            int n = Cells.Sum(c => c.Total);
            sb.AppendLine($"  by cell ({Cells.Count:N0} cell{(Cells.Count == 1 ? "" : "s")}, {n:N0} record{(n == 1 ? "" : "s")}; + added, - removed, ~ changed, > moved out):");
            foreach (var c in all ? Cells : Cells.Take(CellsShown))
            {
                sb.AppendLine($"    {N(c.Cell)}: {c.Summary()}");
                if (!all) continue;
                foreach (var r in c.Records)
                    sb.AppendLine($"      {r.Change} {r.Rec.Sig} {r.Key.Show(self)}{(r.Rec.EditorId is { Length: > 0 } e ? " " + e : "")}" +
                                  (r.From is { } f ? $" (moved from {N(f)})" : r.To is { } t ? $" (to {N(t)})" : ""));
            }
            if (!all && Cells.Count > CellsShown) sb.AppendLine($"    ... and {Cells.Count - CellsShown:N0} more cells (--all lists them all, with each cell's records)");
            else if (!all) sb.AppendLine("    (--all lists each cell's records)");
        }
        if (LeveledLists.Count > 0)
        {
            sb.AppendLine($"  leveled lists with changed entries ({LeveledLists.Count:N0}):");
            foreach (var l in all ? LeveledLists : LeveledLists.Take(ListsShown))
            {
                sb.AppendLine($"    {l.Old.Sig} {l.Key.Show(self)} {l.New.EditorId ?? "(no EditorID)"}{(l.Key.Own ? "" : " (override)")}: " +
                              (l.OtherEntryChanges ? "the same entries by level, reference and count; something else in them changed (conditions, chance or order)"
                                                   : $"{l.EntriesAdded!.Count:N0} added, {l.EntriesRemoved!.Count:N0} removed"));
                var lines = l.EntriesAdded!.Select(e => "+ " + Entry(e, N)).Concat(l.EntriesRemoved!.Select(e => "- " + Entry(e, N))).ToList();
                foreach (var line in all ? lines : lines.Take(EntriesShown)) sb.AppendLine($"      {line}");
                if (!all && lines.Count > EntriesShown) sb.AppendLine($"      ... and {lines.Count - EntriesShown:N0} more entries (--all lists them all)");
            }
            if (!all && LeveledLists.Count > ListsShown) sb.AppendLine($"    ... and {LeveledLists.Count - ListsShown:N0} more leveled lists (--all lists them all)");
        }
        if (BaseSwaps.Count > 0)
        {
            sb.AppendLine($"  base-object swaps ({BaseSwaps.Count:N0}; placed objects whose base object changed):");
            foreach (var s in all ? BaseSwaps : BaseSwaps.Take(SwapsShown))
                sb.AppendLine($"    {s.New.Sig} {s.Key.Show(self)}{(s.New.EditorId is { Length: > 0 } e ? " " + e : "")}" +
                              $"{(s.New.Cell is { } c ? $" in {N(c)}" : "")}: {N(s.BaseSwap!.Value.Old)} -> {N(s.BaseSwap!.Value.New)}");
            if (!all && BaseSwaps.Count > SwapsShown) sb.AppendLine($"    ... and {BaseSwaps.Count - SwapsShown:N0} more (--all lists them all)");
        }
    }

    JsonObject Ref(RecKey? k, string self) => new() { ["key"] = k?.Show(self), ["editor_id"] = Names.Of(k) };

    static JsonObject CountMap(SortedDictionary<string, int> m) => new(m.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));

    public void Json(JsonObject plugin, string self)
    {
        plugin["editor_ids_missing"] = new JsonArray(Names.MissingMasters.Select(m => (JsonNode)m).ToArray());
        plugin["cells"] = new JsonArray(Cells.Select(c => (JsonNode)new JsonObject
        {
            ["cell"] = c.Cell.Show(self), ["editor_id"] = Names.Of(c.Cell),
            ["added"] = CountMap(c.Added), ["removed"] = CountMap(c.Removed), ["changed"] = CountMap(c.Changed), ["moved_out"] = CountMap(c.MovedOut),
            ["records"] = new JsonArray(c.Records.Select(r => (JsonNode)new JsonObject
            {
                ["change"] = r.Change, ["key"] = r.Key.Show(self), ["type"] = r.Rec.Sig, ["editor_id"] = r.Rec.EditorId,
                ["moved_from"] = r.From?.Show(self), ["moved_to"] = r.To?.Show(self),
            }).ToArray()),
        }).ToArray());
        JsonArray Entries(List<LeveledEntry> l) => new(l.Select(e => (JsonNode)new JsonObject
            { ["level"] = e.Level, ["ref"] = e.Ref?.Show(self), ["editor_id"] = Names.Of(e.Ref), ["count"] = e.Count }).ToArray());
        plugin["leveled_lists"] = new JsonArray(LeveledLists.Select(l => (JsonNode)new JsonObject
        {
            ["key"] = l.Key.Show(self), ["type"] = l.Old.Sig, ["editor_id"] = l.New.EditorId, ["override"] = !l.Key.Own,
            ["added"] = Entries(l.EntriesAdded!), ["removed"] = Entries(l.EntriesRemoved!), ["other_entry_changes"] = l.OtherEntryChanges,
        }).ToArray());
        plugin["base_swaps"] = new JsonArray(BaseSwaps.Select(s => (JsonNode)new JsonObject
        {
            ["key"] = s.Key.Show(self), ["type"] = s.New.Sig, ["editor_id"] = s.New.EditorId, ["cell"] = s.New.Cell?.Show(self),
            ["old"] = Ref(s.BaseSwap!.Value.Old, self), ["new"] = Ref(s.BaseSwap!.Value.New, self),
        }).ToArray());
    }
}
