// The raw writer behind `sf-mod-check edit` (0.25.0). It copies a plugin group by group: an untouched record,
// compressed or not, is copied byte for byte; a changed record gets its new size, and so does every GRUP around it; a
// changed compressed record is decompressed, edited and recompressed with its 4-byte size first. Mutagen never writes.
// Design: NOTES.md, "## 0.25.0 (SmcDialogue)", "Step 1 design".
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;

namespace SfModCheck;

/// <summary>An edit that can't be made: what is wrong and how to fix it. The command prints both and exits 2.</summary>
public sealed class EditRefusedException(string reason, string fix) : Exception(reason + " " + fix)
{
    public string Reason { get; } = reason;
    public string Fix { get; } = fix;
}

/// <summary>One `--set-text`: the record as given on the command line (EditorID, FormKey or console FormID), the
/// response number (from 1, as `find --full` and `lines` show them) and the new text.</summary>
public sealed record TextEdit(string Record, int Response, string Text);

/// <summary>The edits of one run (they can be combined).</summary>
public sealed class EditPlan
{
    /// <summary>`--drop-master`: the master's file name, or null.</summary>
    public string? DropMaster;
    public bool SetLight;
    public List<TextEdit> Texts = new();
}

/// <summary>What one edit changed, for the report: the edit ("set-light", "set-text"), the record it touched (a
/// FormKey, or null for the header) and a short description.</summary>
public sealed record EditChange(string Edit, string? Record, string What);

/// <summary>One --set-text as made, for the proof: the record's stored FormID in the output, its type, the field
/// (NAM1 or DESC) and which of them (from 0), and the bytes written there.</summary>
public sealed record TextTarget(uint OutFormId, string Sig, string Field, int Index, byte[] Bytes);

public sealed record EditResult(byte[] Output, List<EditChange> Changes)
{
    /// <summary>The --set-text edits as made (empty without --set-text).</summary>
    public List<TextTarget> Texts { get; init; } = [];
    /// <summary>--drop-master: a stored FormID of the input as the output stores it (null without --drop-master). The
    /// proof uses it to match the input's gap and check messages, which name stored FormIDs, to the output's.</summary>
    public Func<uint, uint>? Remap { get; init; }
}

public static class PluginEdit
{
    static readonly HashSet<int> FormIdGroups = [1, 6, 7, 8, 9, 10];

    /// <summary>A change to one record: its new stored FormID and, if the body changes, a function from the old
    /// (decompressed) body to the new one.</summary>
    public readonly record struct RecordChange(uint FormId, Func<byte[], byte[]>? Body);

    /// <summary>Applies the plan to a plugin's bytes. selfName is the plugin's file name (what FormKeys name it by);
    /// masterStyle gives a master's style by name (null = not found, taken as full). --drop-master also needs
    /// inputPath (the same bytes on disk, which Mutagen reads for the FormLink use check) and masterPath (a master's
    /// file by name, null = not found). Throws EditRefusedException.</summary>
    public static EditResult Apply(byte[] input, string selfName, Func<string, Style?> masterStyle, EditPlan plan,
        string? inputPath = null, Func<string, string?>? masterPath = null)
    {
        if (!plan.SetLight && plan.Texts.Count == 0 && plan.DropMaster == null)
            throw new EditRefusedException("No edit was given.", "Name at least one of --drop-master, --set-light or --set-text.");
        var h = ReadHeader(input);
        var ids = new IdMap(selfName, h.Style, h.Masters.Select(m => (m, masterStyle(m) ?? Style.Full)));
        var records = Index(input, h.Size);
        Log.Write($"edit: {selfName} is {h.Style}, {h.Masters.Count} masters, {records.Count} records");
        var changes = new List<EditChange>();
        var recordEdits = new Dictionary<long, RecordChange>();
        var texts = new List<TextTarget>();
        var flags = h.Flags;

        Drop? drop = plan.DropMaster == null ? null : PrepareDrop(input, h, ids, records, selfName, masterStyle, plan.DropMaster, inputPath, masterPath);

        if (plan.SetLight)
        {
            if (h.Style != Style.Full)
                throw new EditRefusedException($"{selfName} is already a {h.Style.ToString().ToLowerInvariant()} plugin.",
                    "--set-light only turns a full plugin into a light one; leave it out.");
            var outside = records.Where(r => ids.Decode(r.FormId) is var d && d.File == selfName && d.Local is < 0x800 or > 0xFFF).ToList();
            if (outside.Count > 0)
            {
                var first = outside[0];
                throw new EditRefusedException(
                    $"{selfName} has {outside.Count} own record{(outside.Count == 1 ? "" : "s")} with an ID outside 000800 to 000FFF (the first is {first.Sig} {ids.Decode(first.FormId).Local:X6}), and a light plugin can only hold those.",
                    "Keep it a full plugin, or renumber its records into 000800 to 000FFF in the Creation Kit or xEdit (Compact FormIDs for ESL) first.");
            }
            flags |= Flags.Light;
            changes.Add(new("set-light", null, "type: full -> light"));
            Log.Write($"edit: set-light, {records.Count(r => ids.Decode(r.FormId).File == selfName)} own records all inside 000800 to 000FFF");
        }

        if (plan.Texts.Count > 0)
        {
            if ((h.Flags & Flags.Localized) != 0)
            {
                var stem = Path.GetFileNameWithoutExtension(selfName);
                throw new EditRefusedException($"{selfName} is localized: its text lives in Strings\\{stem}_<language>.STRINGS, .DLSTRINGS and .ILSTRINGS, not in the plugin.",
                    "Edit the line in those string files (for example with xEdit or a strings editor) instead.");
            }
            foreach (var t in plan.Texts)
            {
                var rec = FindTarget(input, records, ids, selfName, t.Record);
                if (recordEdits.ContainsKey(rec.Offset))
                    throw new EditRefusedException($"{SafeText.OneLine(t.Record)} is named by more than one --set-text.", "Give each record once per run; for a second response of the same INFO, run edit again on the new file.");
                var body = Body(input, rec);
                var (field, index, count) = TextField(rec, body, t.Response);
                if (index < 0)
                    throw new EditRefusedException(
                        rec.Sig == "MESG" ? $"MESG {SafeText.OneLine(t.Record)} has no description (DESC) to replace."
                            : $"INFO {SafeText.OneLine(t.Record)} has {count} response{(count == 1 ? "" : "s")}, so it has no response {t.Response}.",
                        rec.Sig == "MESG" ? "Add the description in the Creation Kit first, or pick another record."
                            : count == 0 ? "This INFO has no response text (NAM1) to replace: add the response in the Creation Kit first, or pick another INFO."
                            : $"Give --response 1{(count > 1 ? $" to {count}" : "")} (numbered as `sf-mod-check find --full` and `lines` show them).");
                var key = KeyOf(ids, rec.FormId);
                var newBytes = Encoding.UTF8.GetBytes(t.Text + "\0");
                recordEdits[rec.Offset] = new(rec.FormId, b => ReplaceField(b, field, index, newBytes));
                texts.Add(new(drop?.Remap(rec.FormId) ?? rec.FormId, rec.Sig, field, index, newBytes));
                changes.Add(new("set-text", key, rec.Sig == "INFO" ? $"response {t.Response} text (NAM1)" : "description (DESC)"));
                Log.Write($"edit: set-text on {rec.Sig} {key} at offset {rec.Offset}, {field} #{index + 1}, {newBytes.Length - 1} bytes of UTF-8");
            }
        }

        byte[] outHeader;
        if (drop == null) outHeader = input.AsSpan(0, 24 + (int)h.Size).ToArray();
        else
        {
            // the header without the dropped MAST (and the DATA right after it), its ONAM renumbered
            var fields = Subrecords.Parse(input.AsSpan(24, (int)h.Size), out _);
            var kept = new List<(string Type, byte[] Data)>();
            int mast = -1;
            for (int i = 0; i < fields.Count; i++)
            {
                var (t, d) = fields[i];
                if (t == "MAST" && ++mast == drop.MastIndex)
                {
                    if (i + 1 < fields.Count && fields[i + 1].Type == "DATA") i++;
                    continue;
                }
                if (t == "ONAM")
                {
                    d = (byte[])d.Clone();
                    for (int j = 0; j + 4 <= d.Length; j += 4)
                    {
                        var v = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(j));
                        var n = drop.Remap(v);
                        if (n != v) { BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(j), n); drop.Onam++; }
                    }
                }
                kept.Add((t, d));
            }
            var body = PackFields(kept);
            outHeader = new byte[24 + body.Length];
            input.AsSpan(0, 24).CopyTo(outHeader);
            BinaryPrimitives.WriteUInt32LittleEndian(outHeader.AsSpan(4), (uint)body.Length);
            body.CopyTo(outHeader, 24);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(outHeader.AsSpan(8), flags);

        Func<RawRecord, ReadOnlyMemory<byte>, RecordChange?> onRecord = drop == null
            ? (rec, _) => recordEdits.TryGetValue(rec.Offset, out var c) ? c : null
            : (rec, _) =>
            {
                // every record: its ID renumbered, then its payload (link windows), then a --set-text on top
                var text = recordEdits.TryGetValue(rec.Offset, out var c) ? c.Body : null;
                var nf = drop.Remap(rec.FormId);
                if (nf != rec.FormId) drop.RecordIds++;
                return new RecordChange(nf, b => { var r = drop.Payload(rec, b); return text == null ? r : text(r); });
            };
        Func<uint, int, uint>? onLabel = drop == null ? null : (label, _) =>
        {
            var n = drop.Remap(label);
            if (n != label) drop.Labels++;
            return n;
        };
        var output = Rewrite(input, outHeader, onRecord, onLabel);
        if (drop != null)
        {
            changes.Insert(0, new("drop-master", null,
                $"master {drop.Name} removed; renumbered {drop.RecordIds} record IDs, {drop.Labels} group labels, {drop.Onam} ONAM entries and {drop.Values} FormIDs in fields" +
                (drop.LookAlikes > 0 ? $"; left {drop.LookAlikes} look-alike value{(drop.LookAlikes == 1 ? "" : "s")} alone" : "")));
            Log.Write($"edit: drop-master {drop.Name}: {changes[0].What}");
        }
        return new(output, changes) { Texts = texts, Remap = drop == null ? null : drop.Remap };
    }

    /// <summary>A test hook (0.28.0): the number of probes one probe pass may use, in place of the dropped master's
    /// local range (4,095, 65,535 or 16,777,215), so a test can force more than one pass. Never set outside the tests.</summary>
    [ThreadStatic] internal static int? ProbeRangeForTest;
    /// <summary>The number of probe passes the last --drop-master on this thread made (for the tests and the log).</summary>
    [ThreadStatic] internal static int ProbePasses;

    /// <summary>A --drop-master in progress: the old and new ID maps, the plugin's link set (file, local ID as stored)
    /// and the counts for the report. 0.28.0: Probe finds which candidate windows Mutagen reads as links, and Payload
    /// renumbers only those.</summary>
    sealed class Drop(string name, int mastIndex, Style droppedStyle, IdMap oldIds, IdMap newIds, HashSet<(string, uint)> links, Func<string, uint, bool> namesRecord)
    {
        public readonly string Name = name;
        public readonly int MastIndex = mastIndex;
        public int RecordIds, Labels, Onam, Values, LookAlikes;
        /// <summary>The candidate windows (record offset in the input, field number, byte) whose probe Mutagen read as a link.</summary>
        readonly HashSet<(long Rec, int Field, int At)> confirmed = [];
        const string Keep = "Keep the master. If you are sure it is unused, remove it in xEdit and check the plugin there.";

        /// <summary>A stored FormID re-encoded for the new master list, in the style it is written in. A value that
        /// names no file, or the dropped master, is left alone (the use check refused the second already).</summary>
        public uint Remap(uint v)
        {
            var (file, local, written) = oldIds.Decode(v);
            if (file == null || file.Equals(Name, StringComparison.OrdinalIgnoreCase)) return v;
            return Encode(newIds, file, local, written) ?? v;
        }

        /// <summary>Whether a 4-byte value is one of the plugin's links as stored (decodes to a linked (file, ID)).</summary>
        public bool IsLink(uint v) => oldIds.Decode(v) is var d && d.File != null && links.Contains((d.File.ToLowerInvariant(), d.Local));

        /// <summary>The record's fields and its candidate windows: each 4-byte window (field number, byte) whose value
        /// is one of the plugin's link values and moves with the drop. Every byte offset is looked at. Refused (nothing
        /// guessed): a value that would be renumbered and names a real record but isn't one of Mutagen's link values (a
        /// FormID in a field Mutagen doesn't read), and two overlapping link windows where either would be renumbered
        /// (it can't be told which one is the FormID).</summary>
        public (List<(string Type, byte[] Data)> Fields, List<(int Field, int At)> Candidates) Scan(RawRecord rec, byte[] body)
        {
            var fields = Subrecords.Parse(body, out var why);
            if (why != null) throw new EditRefusedException($"{rec.Sig} {rec.FormId:X8}'s fields don't add up ({why}).", "Run `sf-mod-check check` on the plugin; the record is damaged.");
            var found = new List<(int Field, int At)>();
            for (int f = 0; f < fields.Count; f++)
            {
                var d = fields[f].Data;
                int lastEnd = -1, lastAt = -1;   // the last link window seen in this field
                bool lastMoves = false;
                // where the link windows start: a window that overlaps one can't be a FormID of its own
                var isLink = new bool[Math.Max(0, d.Length - 3)];
                for (int j = 0; j < isLink.Length; j++) isLink[j] = IsLink(BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(j)));
                bool OverlapsLink(int j)
                {
                    for (int k = Math.Max(0, j - 3); k <= Math.Min(isLink.Length - 1, j + 3); k++)
                        if (k != j && isLink[k]) return true;
                    return false;
                }
                for (int j = 0; j + 4 <= d.Length; j++)
                {
                    var v = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(j));
                    if (!isLink[j])
                    {
                        // a value that isn't a link but would be renumbered and names a real record: refused, unless it
                        // overlaps a link window (that window is the FormID) or is the plugin itself written in a style
                        // it never uses for its own IDs (a light or medium style other than its own)
                        if (!OverlapsLink(j) && oldIds.Decode(v) is var u && u.File != null
                            && !(u.File == oldIds.Self && u.Written != Style.Full && u.Written != oldIds.SelfStyle)
                            && Remap(v) != v && namesRecord(u.File, u.Local))
                            throw Unread(rec, fields[f].Type, j, v);
                        continue;
                    }
                    var n = Remap(v);
                    // two link windows that overlap: one of them is a number that only looks like a FormID
                    if (j < lastEnd && (n != v || lastMoves))
                        throw new EditRefusedException(
                            $"{rec.Sig} {rec.FormId:X8}'s {fields[f].Type} field holds two overlapping values that both look like FormIDs (at bytes {lastAt} and {j}), so it can't be told which one to renumber.",
                            Keep);
                    lastEnd = j + 4; lastAt = j; lastMoves = n != v;
                    if (n != v) found.Add((f, j));
                }
            }
            return (fields, found);
        }

        /// <summary>The refusal for a value that names a real record in a place Mutagen doesn't read as a link.</summary>
        EditRefusedException Unread(RawRecord rec, string field, int at, uint v)
        {
            var u = oldIds.Decode(v);
            var shownStyle = u.File == oldIds.Self ? oldIds.SelfStyle : StyleOf(oldIds, u.File!);
            return new EditRefusedException(
                $"{rec.Sig} {rec.FormId:X8} stores {v:X8} in its {field} field at byte {at}, which names a record of {u.File} ({IdMap.Mask(u.Local, shownStyle):X6}), but Mutagen doesn't read that field as a link, so it can't be told whether it is a FormID to renumber.",
                Keep);
        }

        /// <summary>The body with the link windows renumbered; the same array when nothing changes. Only the candidate
        /// windows whose probe Mutagen read as a link are renumbered (0.28.0). Any other candidate is a number that
        /// only looks like a link value: refused when it names a real record of the plugin or a master, else left as it
        /// is and counted.</summary>
        public byte[] Payload(RawRecord rec, byte[] body)
        {
            var (fields, candidates) = Scan(rec, body);
            bool changed = false;
            var copies = new Dictionary<int, byte[]>();
            foreach (var (f, j) in candidates)
            {
                var v = BinaryPrimitives.ReadUInt32LittleEndian(fields[f].Data.AsSpan(j));
                if (!confirmed.Contains((rec.Offset, f, j)))
                {
                    var u = oldIds.Decode(v);
                    if (namesRecord(u.File!, u.Local)) throw Unread(rec, fields[f].Type, j, v);
                    LookAlikes++;
                    Log.Write($"edit: drop-master: left {v:X8} alone in {rec.Sig} {rec.FormId:X8}'s {fields[f].Type} field at byte {j}: Mutagen doesn't read it as a link, and it names no record");
                    continue;
                }
                if (!copies.TryGetValue(f, out var copy)) copies[f] = copy = (byte[])fields[f].Data.Clone();
                BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(j), Remap(v));
                Values++;
            }
            foreach (var (f, copy) in copies) { fields[f] = (fields[f].Type, copy); changed = true; }
            return changed ? PackFields(fields) : body;
        }

        /// <summary>The probe read (0.28.0; NOTES.md "## 0.28.0 (Followups)", "(b)"). Every candidate window of every
        /// record gets a probe value, a FormID of the dropped master with a local ID of its own (nothing links to the
        /// dropped master, as the use check proved), in a copy of the plugin made in memory; Mutagen reads that copy, and
        /// each probe it reports as a link of the record it was put in marks its window as a real link. More candidates
        /// than the master's local range holds are probed in passes. Refused: a probe read that throws, and a probe
        /// Mutagen reports only in another record (the links can't be told apart from other values then).</summary>
        public void Probe(byte[] input, byte[] header, List<RawRecord> records, string selfName, LoadOrder<IModMasterStyledGetter> styles)
        {
            var all = new List<(RawRecord Rec, int Field, int At)>();
            foreach (var r in records)
                foreach (var (f, j) in Scan(r, Body(input, r)).Candidates) all.Add((r, f, j));
            ProbePasses = 0;
            if (all.Count == 0) { Log.Write($"edit: drop-master {Name}: no candidate link windows, no probe read"); return; }
            int range = ProbeRangeForTest ?? droppedStyle switch { Style.Light => 0xFFF, Style.Medium => 0xFFFF, _ => 0xFFFFFF };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int start = 0; start < all.Count; start += range)
            {
                ProbePasses++;
                var pass = all.Skip(start).Take(range).Select((c, i) => (c.Rec, c.Field, c.At, Local: (uint)(i + 1))).ToList();
                var byRec = pass.GroupBy(c => c.Rec.Offset).ToDictionary(g => g.Key, g => g.ToList());
                var probe = Rewrite(input, header, (rec, stored) => byRec.TryGetValue(rec.Offset, out var list)
                    ? new RecordChange(rec.FormId, b =>
                    {
                        var fields = Subrecords.Parse(b, out _);
                        foreach (var g in list.GroupBy(c => c.Field))
                        {
                            var d = (byte[])fields[g.Key].Data.Clone();
                            foreach (var c in g) BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(c.At), Encode(oldIds, Name, c.Local, droppedStyle)!.Value);
                            fields[g.Key] = (fields[g.Key].Type, d);
                        }
                        return PackFields(fields);
                    })
                    : null);

                // which probes Mutagen reads as links, by the record they are reported in
                var seen = new Dictionary<(string, uint), HashSet<uint>>();
                string? at = null;
                try
                {
                    using var ms = new MemoryStream(probe, writable: false);
                    using var mod = StarfieldMod.CreateFromBinaryOverlay(ms, StarfieldRelease.Starfield, ModKey.FromFileName(selfName), new BinaryReadParameters { MasterFlagsLookup = styles });
                    foreach (var rec in mod.EnumerateMajorRecords())
                    {
                        at = rec.EditorID is { Length: > 0 } eid ? $"{eid} [{rec.FormKey}]" : rec.FormKey.ToString();
                        var key = ((rec.FormKey.ModKey == mod.ModKey ? selfName : rec.FormKey.ModKey.FileName.String).ToLowerInvariant(), rec.FormKey.ID);
                        foreach (var l in rec.EnumerateFormLinks())
                        {
                            if (l.IsNull || !l.FormKey.ModKey.FileName.String.Equals(Name, StringComparison.OrdinalIgnoreCase)) continue;
                            if (!seen.TryGetValue(key, out var set)) seen[key] = set = [];
                            set.Add(l.FormKey.ID);
                        }
                        at = null;
                    }
                }
                catch (Exception e) when (Fatal.Recoverable(e) && e is not EditRefusedException)
                {
                    throw new EditRefusedException(
                        $"The links of {(at != null ? SafeText.OneLine(at) : "a record")} can't be told apart from other values: Mutagen can't read the probe copy ({e.GetType().Name}: {SafeText.OneLine(e.Message)}).",
                        Keep);
                }
                var anywhere = seen.Values.SelectMany(s => s).ToHashSet();
                int hits = 0;
                foreach (var c in pass)
                {
                    var d = oldIds.Decode(c.Rec.FormId);
                    if (d.File != null && seen.TryGetValue((d.File.ToLowerInvariant(), d.Local), out var own) && own.Contains(c.Local))
                    {
                        confirmed.Add((c.Rec.Offset, c.Field, c.At));
                        hits++;
                    }
                    else if (anywhere.Contains(c.Local))
                        throw new EditRefusedException(
                            $"The links of {c.Rec.Sig} {c.Rec.FormId:X8} can't be told apart from other values: Mutagen reads the value at byte {c.At} of its field {c.Field + 1} as a link of another record.",
                            Keep);
                }
                Log.Write($"edit: drop-master {Name}: probe pass {ProbePasses}: {pass.Count} candidate windows, {hits} read by Mutagen as links");
            }
            Log.Write($"edit: drop-master {Name}: {all.Count} candidate windows in {ProbePasses} probe pass{(ProbePasses == 1 ? "" : "es")}, {confirmed.Count} are links, {sw.Elapsed.TotalSeconds:F1} s");
        }
    }

    /// <summary>A FormID in the given written style for the map's file list (null when the file isn't in it).</summary>
    static uint? Encode(IdMap ids, string file, uint local, Style written)
    {
        var list = ids.List(written);
        int idx = file.Equals(ids.Self, StringComparison.OrdinalIgnoreCase) ? list.Count
            : list.Select((m, i) => (m, i)).FirstOrDefault(x => x.m.Equals(file, StringComparison.OrdinalIgnoreCase), (null!, -1)).Item2;
        if (idx < 0) return null;
        return written switch
        {
            Style.Light => 0xFE000000u | ((uint)idx << 12) | (local & 0xFFF),
            Style.Medium => 0xFD000000u | ((uint)idx << 16) | (local & 0xFFFF),
            _ => ((uint)idx << 24) | (local & 0xFFFFFF),
        };
    }

    static MasterStyle MutagenStyle(Style s) => s switch { Style.Light => MasterStyle.Small, Style.Medium => MasterStyle.Medium, _ => MasterStyle.Full };

    /// <summary>The --drop-master checks: the name is a master, its file is there, and neither Mutagen (FormLinks and
    /// record keys) nor the raw scan (record IDs, FormID group labels, ONAM, field windows that name a record the
    /// master holds) finds a use. Builds the link set for the renumbering.</summary>
    static Drop PrepareDrop(byte[] input, Header h, IdMap ids, List<RawRecord> records, string selfName, Func<string, Style?> masterStyle,
        string name, string? inputPath, Func<string, string?>? masterPath)
    {
        var k = h.Masters.FindIndex(m => m.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (k < 0)
            throw new EditRefusedException($"{SafeText.OneLine(name)} isn't a master of {selfName} (its masters: {(h.Masters.Count == 0 ? "none" : string.Join(", ", h.Masters.Select(SafeText.OneLine)))}).",
                "Give the master's file name as the header lists it (`sf-mod-check find --plugin <plugin>` shows them).");
        var master = h.Masters[k];
        var mPath = masterPath?.Invoke(master);
        if (mPath == null || !File.Exists(mPath))
            throw new EditRefusedException($"{master} can't be found, so its records can't be told apart from numbers that only look like its FormIDs.",
                "Put the master in the Data folder or name its folder with --masters-dir, then run the edit again.");
        if (inputPath == null) throw new EditRefusedException("--drop-master needs the plugin's path for its Mutagen check.", "Give the plugin as a file path.");
        var ds = ids.List(Style.Light).Contains(master) ? Style.Light : ids.List(Style.Medium).Contains(master) ? Style.Medium : Style.Full;

        // the master's own record IDs (raw header index), masked to its style
        var (own, mStyle) = MasterRecords(master, mPath, masterStyle);
        Log.Write($"edit: drop-master {master}: {ds} master, index {ids.List(ds).ToList().IndexOf(master)} in its list, {own.Count} records in {mPath}");

        // for the payload scan: whether a stored value names a real record of the plugin or of a master (read when
        // first needed; a master that can't be found is a refusal, since its records can't be told apart)
        var selfOwn = records.Select(r => ids.Decode(r.FormId)).Where(d => d.File == selfName).Select(d => IdMap.Mask(d.Local, h.Style)).ToHashSet();
        var known = new Dictionary<string, (HashSet<uint> Own, Style Style)>(StringComparer.OrdinalIgnoreCase) { [master] = (own, mStyle) };
        bool NamesRecord(string file, uint local)
        {
            if (file == selfName) return selfOwn.Contains(IdMap.Mask(local, h.Style));
            if (!known.TryGetValue(file, out var m))
            {
                var p = masterPath?.Invoke(file);
                if (p == null || !File.Exists(p))
                    throw new EditRefusedException($"{file} can't be found, so a stored value that may be one of its FormIDs can't be checked before it is renumbered.",
                        "Put the master in the Data folder or name its folder with --masters-dir, then run the edit again.");
                known[file] = m = MasterRecords(file, p, masterStyle);
                Log.Write($"edit: drop-master: read {m.Own.Count} record IDs of {file} ({p}) for the payload scan");
            }
            return m.Own.Contains(IdMap.Mask(local, m.Style));
        }

        // 1. Mutagen: links and record keys of the plugin, and those that point at the master
        var links = new HashSet<(string, uint)>();
        int mutagenUses = 0;
        LoadOrder<IModMasterStyledGetter> styles;
        try
        {
            // inside the try: a master name Mutagen can't take (no plugin extension, listed twice) is a refusal (0.28.0 code review round 1)
            styles = new LoadOrder<IModMasterStyledGetter>(h.Masters.Select(m =>
                (IModMasterStyledGetter)new KeyedMasterStyle(ModKey.FromFileName(m), MutagenStyle(masterStyle(m) ?? Style.Full))));
            using var mod = StarfieldMod.CreateFromBinaryOverlay(inputPath, StarfieldRelease.Starfield, new BinaryReadParameters { MasterFlagsLookup = styles });
            void Add(FormKey fk)
            {
                var file = fk.ModKey == mod.ModKey ? selfName : fk.ModKey.FileName.String;
                if (file.Equals(master, StringComparison.OrdinalIgnoreCase)) mutagenUses++;
                links.Add((file.ToLowerInvariant(), fk.ID));
            }
            foreach (var rec in mod.EnumerateMajorRecords())
            {
                Add(rec.FormKey);
                foreach (var l in rec.EnumerateFormLinks())
                    if (!l.IsNull) Add(l.FormKey);
            }
        }
        catch (Exception e) when (Fatal.Recoverable(e) && e is not EditRefusedException)
        {
            throw new EditRefusedException($"Mutagen can't read {selfName} ({e.GetType().Name}: {SafeText.OneLine(e.Message)}), so its links can't be listed.",
                "Run `sf-mod-check check` on it; a plugin Mutagen can't read can't have a master dropped safely.");
        }
        // Mutagen gives a light or medium file's IDs in their short form; the decoded stored local is the same
        // number, since Decode keeps only the style's bits

        // 2. raw: every stored ID that decodes to the master and names one of its records
        int rawUses = 0;
        bool Uses(uint v) => ids.Decode(v) is var d && d.File == master && own.Contains(IdMap.Mask(d.Local, mStyle));
        foreach (var r in records)
        {
            if (Uses(r.FormId)) rawUses++;
            var fields = Subrecords.Parse(Body(input, r), out _);
            foreach (var (_, data) in fields)
                for (int j = 0; j + 4 <= data.Length; j++)
                    if (Uses(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(j)))) rawUses++;
        }
        Rewrite(input, input.AsSpan(0, 24 + (int)h.Size).ToArray(), (_, _) => null, (label, _) => { if (Uses(label)) rawUses++; return label; });
        foreach (var (t, data) in Subrecords.Parse(input.AsSpan(24, (int)h.Size), out _))
            if (t == "ONAM")
                for (int j = 0; j + 4 <= data.Length; j += 4)
                    if (Uses(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(j)))) rawUses++;
        Log.Write($"edit: drop-master {master}: Mutagen finds {mutagenUses} uses among {links.Count} linked IDs, the raw scan finds {rawUses}");

        if (mutagenUses > 0 && rawUses > 0)
            throw new EditRefusedException($"{master} is still used: Mutagen finds {mutagenUses} link{(mutagenUses == 1 ? "" : "s")} or records pointing at it, and the raw scan finds {rawUses} stored FormID{(rawUses == 1 ? "" : "s")}.",
                $"Keep the master, or remove what points at it first (`sf-mod-check find --refs` on its records lists them).");
        if (mutagenUses != rawUses)
            throw new EditRefusedException($"The two use checks disagree on {master}: Mutagen finds {mutagenUses} use{(mutagenUses == 1 ? "" : "s")}, the raw scan finds {rawUses}.",
                "Keep the master: a use one reader can't see is still a use. If you are sure, remove the master in xEdit and check the plugin there.");

        var newIds = new IdMap(selfName, h.Style, h.Masters.Where((_, i) => i != k).Select(m => (m, masterStyle(m) ?? Style.Full)));
        var drop = new Drop(master, k, ds, ids, newIds, links, NamesRecord);
        // 3. the probe read (0.28.0): which candidate windows Mutagen reads as links
        drop.Probe(input, input.AsSpan(0, 24 + (int)h.Size).ToArray(), records, selfName, styles);
        return drop;
    }

    /// <summary>A master's own record IDs (raw header index), masked to its style, and the style.</summary>
    static (HashSet<uint> Own, Style Style) MasterRecords(string master, string path, Func<string, Style?> masterStyle)
    {
        var head = RawHeader.Read(path) ?? throw new EditRefusedException($"{master} ({path}) is not a plugin.", "Point at the real master file.");
        var own = new HashSet<uint>();
        var mIds = new IdMap(master, head.Style, head.Masters.Select(m => (m, masterStyle(m) ?? Style.Full)));
        using (var mf = new RawFile(path))
            new RawWalker(mf, deep: false, (_, _, _) => { })
            {
                OnRecord = rec =>
                {
                    var d = mIds.Decode(rec.FormId);
                    if (d.File == master) own.Add(IdMap.Mask(d.Local, head.Style));
                },
            }.Walk();
        return (own, head.Style);
    }

    /// <summary>Copies every group and record after the header. onRecord returns null to copy the record as is;
    /// onLabel (optional) renumbers the label of a FormID group (types 1, 6, 7, 8, 9 and 10).</summary>
    public static byte[] Rewrite(byte[] buf, byte[] newHeader, Func<RawRecord, ReadOnlyMemory<byte>, RecordChange?> onRecord, Func<uint, int, uint>? onLabel = null)
    {
        var hsize = (long)BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4));
        var outS = new MemoryStream(buf.Length + 4096);
        outS.Write(newHeader);
        Copy(buf, 24 + hsize, buf.Length, outS, onRecord, onLabel, "top level");
        return outS.ToArray();
    }

    static void Copy(byte[] buf, long pos, long end, MemoryStream o, Func<RawRecord, ReadOnlyMemory<byte>, RecordChange?> onRecord, Func<uint, int, uint>? onLabel, string where)
    {
        while (pos < end)
        {
            if (end - pos < 24) throw Broken(pos, $"{end - pos} stray bytes at the end of {where}");
            var sig = Encoding.Latin1.GetString(buf, (int)pos, 4);
            var size = (long)BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan((int)pos + 4));
            if (sig == "GRUP")
            {
                if (size < 24 || pos + size > end) throw Broken(pos, $"group size {size} does not fit in {where}");
                var hdr = buf.AsSpan((int)pos, 24).ToArray();
                var type = BinaryPrimitives.ReadInt32LittleEndian(hdr.AsSpan(12));
                var label = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(8));
                if (onLabel != null && FormIdGroups.Contains(type)) BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(8), onLabel(label, type));
                var start = o.Position;
                o.Write(hdr);
                Copy(buf, pos + 24, pos + size, o, onRecord, onLabel, type == 0 ? $"GRUP {Encoding.Latin1.GetString(hdr, 8, 4)}" : $"GRUP type {type} {label:X8}");
                var written = o.Position - start;
                if (written > uint.MaxValue) throw new EditRefusedException("The edited plugin would be over 4 GB.", "Starfield plugins can't be that big; make a smaller edit.");
                var back = o.Position;
                o.Position = start + 4;
                o.Write(BitConverter.GetBytes((uint)written));
                o.Position = back;
                pos += size;
                continue;
            }
            if (pos + 24 + size > end) throw Broken(pos, $"{sig} record size {size} runs past the end of {where}");
            var rec = new RawRecord(sig, BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan((int)pos + 8)), BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan((int)pos + 12)),
                pos, (uint)size, BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan((int)pos + 20)));
            var stored = new ReadOnlyMemory<byte>(buf, (int)pos + 24, (int)size);
            var change = onRecord(rec, stored);
            if (change is not { } c)
            {
                o.Write(buf, (int)pos, 24 + (int)size);
            }
            else
            {
                var hdr = buf.AsSpan((int)pos, 24).ToArray();
                byte[] data = stored.ToArray();
                if (c.Body != null)
                {
                    var body = Body(buf, rec);
                    var nb = c.Body(body);
                    if (!nb.AsSpan().SequenceEqual(body))
                        data = (rec.Flags & Flags.Compressed) != 0 ? Compress(nb) : nb;
                }
                BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(4), (uint)data.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(12), c.FormId);
                o.Write(hdr);
                o.Write(data);
            }
            pos += 24 + size;
        }
    }

    static EditRefusedException Broken(long off, string what) =>
        new($"The plugin can't be read to the end: {what} (offset {off}).", "Run `sf-mod-check check` on it; a damaged file has to be fixed or downloaded again before it can be edited.");

    /// <summary>The decompressed body of a record (its stored bytes when it isn't compressed).</summary>
    public static byte[] Body(byte[] buf, RawRecord rec)
    {
        var stored = buf.AsSpan((int)rec.Offset + 24, (int)rec.Size);
        if ((rec.Flags & Flags.Compressed) == 0) return stored.ToArray();
        return Subrecords.Decompress(stored, out var why)
            ?? throw new EditRefusedException($"{rec.Sig} {rec.FormId:X8} is compressed but {why}.", "Run `sf-mod-check check` on the plugin; the record is damaged.");
    }

    /// <summary>zlib with the uncompressed size first, as plugins store compressed records.</summary>
    public static byte[] Compress(byte[] body)
    {
        var z = new MemoryStream();
        z.Write(BitConverter.GetBytes((uint)body.Length));
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(body);
        return z.ToArray();
    }

    /// <summary>Joins fields back into a body; a field over 65535 bytes gets an XXXX field first.</summary>
    public static byte[] PackFields(IEnumerable<(string Type, byte[] Data)> fields)
    {
        var o = new MemoryStream();
        foreach (var (t, d) in fields)
        {
            if (d.Length > 0xFFFF)
            {
                o.Write("XXXX"u8);
                o.Write(BitConverter.GetBytes((ushort)4));
                o.Write(BitConverter.GetBytes((uint)d.Length));
                o.Write(Encoding.Latin1.GetBytes(t));
                o.Write(BitConverter.GetBytes((ushort)0));
            }
            else
            {
                o.Write(Encoding.Latin1.GetBytes(t));
                o.Write(BitConverter.GetBytes((ushort)d.Length));
            }
            o.Write(d);
        }
        return o.ToArray();
    }

    sealed record Header(uint Flags, uint Size, List<string> Masters)
    {
        public Style Style => (Flags & SfModCheck.Flags.Light) != 0 ? Style.Light : (Flags & SfModCheck.Flags.Medium) != 0 ? Style.Medium : Style.Full;
    }

    static Header ReadHeader(byte[] b)
    {
        if (b.Length < 24 || Encoding.Latin1.GetString(b, 0, 4) != "TES4")
            throw new EditRefusedException("The input is not a plugin: it doesn't start with a TES4 header.", "Give a Starfield .esm, .esp or .esl file.");
        var size = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4));
        if (24 + (long)size > b.Length) throw Broken(0, $"the TES4 header ({size} bytes) runs past the end of the file");
        var fields = Subrecords.Parse(b.AsSpan(24, (int)size), out var why);
        if (why != null) throw Broken(0, "TES4 header: " + why);
        return new(BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8)), size,
            fields.Where(f => f.Type == "MAST").Select(f => Encoding.UTF8.GetString(f.Data).TrimEnd('\0')).ToList());
    }

    /// <summary>Every record (headers only), in file order; throws on a layout it can't follow.</summary>
    static List<RawRecord> Index(byte[] buf, uint hsize)
    {
        var list = new List<RawRecord>();
        Rewrite(buf, buf.AsSpan(0, 24 + (int)hsize).ToArray(), (rec, _) => { list.Add(rec); return null; });
        return list;
    }

    /// <summary>The FormKey (as text) of a stored FormID.</summary>
    static string KeyOf(IdMap ids, uint fid)
    {
        var (file, local, _) = ids.Decode(fid);
        return file == null ? $"{fid:X8} (no such master)" : $"{local & 0xFFFFFF:X6}:{file}";
    }

    /// <summary>Finds the one INFO or MESG stored in this plugin that the user named: an EditorID, a FormKey
    /// (000800:MyMod.esm, own record or override) or a console FormID (8 hex digits; only the local part counts, and it
    /// names an own record, since the load-order index can't be known from one plugin).</summary>
    static RawRecord FindTarget(byte[] buf, List<RawRecord> records, IdMap ids, string selfName, string spec)
    {
        var q = spec.Trim();
        // "Name", 'Name', eid:Name and editorid:Name are always an EditorID (as in find, lines and scenes)
        var forced = FindCommand.ForcedEditorId(q);
        if (forced != null) q = forced;
        else if (FindCommand.EmptyForcedEditorId(q))
            throw new EditRefusedException($"--set-text {SafeText.OneLine(q)} names no EditorID: the EditorID after it is empty.",
                "Give the EditorID after eid: (for example eid:MyMod_GreetingInfo), a FormKey, or a console FormID.");
        List<RawRecord> hits;
        string what;
        if (forced == null && System.Text.RegularExpressions.Regex.IsMatch(q, @"^(0x)?[0-9A-Fa-f]{8}\z"))
        {
            var fid = Convert.ToUInt32(q.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? q[2..] : q, 16);
            var local = (fid >> 24) switch { 0xFE => fid & 0xFFF, 0xFD => fid & 0xFFFF, _ => fid & 0xFFFFFF };
            what = $"console FormID {fid:X8}";
            // a console FormID keeps only its local part, so refuse when that part also matches an override this plugin carries
            var overrides = records.Where(r => ids.Decode(r.FormId) is var d && d.File != null && d.File != selfName
                                               && IdMap.Mask(d.Local, StyleOf(ids, d.File)) == IdMap.Mask(local, StyleOf(ids, d.File)))
                                   .Select(r => KeyOf(ids, r.FormId)).Distinct().ToList();
            if (overrides.Count > 0)
                throw new EditRefusedException($"--set-text {fid:X8} could mean an override {selfName} carries ({string.Join(", ", overrides.Take(5))}), and a console FormID keeps only its local part.",
                    "Name the record by its FormKey (six hex digits, a colon and the plugin's file name) or its EditorID.");
            hits = records.Where(r => ids.Decode(r.FormId) is var d && d.File == selfName && IdMap.Mask(d.Local, ids.SelfStyle) == IdMap.Mask(local, ids.SelfStyle)).ToList();
        }
        else if (forced == null && q.Contains(':'))
        {
            if (!FormKey.TryFactory(q, out var key))
                throw new EditRefusedException($"{SafeText.OneLine(q)} isn't a FormKey.", "Give six hex digits, a colon and the plugin's file name (for example 000800:MyMod.esm), an EditorID, or a console FormID.");
            what = $"record {key}";
            hits = records.Where(r =>
            {
                var d = ids.Decode(r.FormId);
                if (d.File == null || !d.File.Equals(key.ModKey.FileName.String, StringComparison.OrdinalIgnoreCase)) return false;
                var style = d.File == selfName ? ids.SelfStyle : StyleOf(ids, d.File);
                return IdMap.Mask(d.Local, style) == key.ID;
            }).ToList();
        }
        else
        {
            what = $"EditorID {SafeText.OneLine(q)}";
            // every record with that EditorID; an INFO or MESG wins over another type with the same name
            var named = records.Where(r => string.Equals(RecordIndex.EditorId(Body(buf, r)), q, StringComparison.OrdinalIgnoreCase)).ToList();
            hits = named.Any(r => r.Sig is "INFO" or "MESG") ? named.Where(r => r.Sig is "INFO" or "MESG").ToList() : named;
        }
        if (hits.Count == 0)
            throw new EditRefusedException($"{selfName} holds no {what}.",
                "Check the name with `sf-mod-check find <record> --plugin <plugin>`; --set-text edits a record stored in this plugin (its own, or an override it carries).");
        if (hits.Count > 1)
            throw new EditRefusedException($"{selfName} holds {hits.Count} records for {what}.", "Name the record by its FormKey (six hex digits, a colon and the plugin's file name).");
        var rec = hits[0];
        if (rec.Sig is not ("INFO" or "MESG"))
            throw new EditRefusedException($"{what} is a {rec.Sig} record; --set-text changes only a dialogue line (INFO) or a message (MESG).", "Name an INFO or a MESG.");
        return rec;
    }

    static Style StyleOf(IdMap ids, string file)
    {
        foreach (var s in new[] { Style.Full, Style.Medium, Style.Light })
            if (ids.List(s).Any(m => m.Equals(file, StringComparison.OrdinalIgnoreCase))) return s;
        return Style.Full;
    }

    /// <summary>The field --set-text replaces: an INFO's NAM1 of response n (each response starts with TRDA; a body
    /// without TRDA is numbered by NAM1), or a MESG's DESC. index = -1 when there is no such field; count = responses.</summary>
    static (string Field, int Index, int Count) TextField(RawRecord rec, byte[] body, int response)
    {
        var fields = Subrecords.Parse(body, out var why);
        if (why != null) throw new EditRefusedException($"{rec.Sig} {rec.FormId:X8}'s fields don't add up ({why}).", "Run `sf-mod-check check` on the plugin; the record is damaged.");
        if (rec.Sig == "MESG")
        {
            var has = fields.Any(f => f.Type == "DESC");
            if (response != 1) throw new EditRefusedException("A message (MESG) has one description, so --response can only be 1.", "Leave out --response.");
            return ("DESC", has ? 0 : -1, has ? 1 : 0);
        }
        int trda = fields.Count(f => f.Type == "TRDA"), nam1 = fields.Count(f => f.Type == "NAM1");
        if (trda > 0 && trda != nam1)
            throw new EditRefusedException($"INFO {rec.FormId:X8} has {trda} responses but {nam1} response texts (NAM1), so response {response} can't be told apart.",
                "Give each response a text in the Creation Kit first, then edit it here.");
        return ("NAM1", response >= 1 && response <= nam1 ? response - 1 : -1, nam1);
    }

    /// <summary>The body with the n-th (from 0) field of this type replaced.</summary>
    static byte[] ReplaceField(byte[] body, string type, int index, byte[] data)
    {
        var fields = Subrecords.Parse(body, out _);
        int seen = 0;
        for (int i = 0; i < fields.Count; i++)
            if (fields[i].Type == type && seen++ == index) { fields[i] = (type, data); break; }
        return PackFields(fields);
    }

    /// <summary>Writes the edited bytes to a new temporary file next to `out`: a new folder &lt;out&gt;.tmp-&lt;random&gt;
    /// holding the file under fileName: --out's file name since 0.28.0, so the proof's gap and check see the plugin by
    /// the name it is kept under, and the input's name for the diff proof's own copy (Mutagen keys own records by the
    /// file name, so the field compare needs the input's name on both sides). The file is created with CreateNew, so nothing is
    /// overwritten. Returns the file's path; the caller proves it, then moves it to out and calls DeleteTemp.</summary>
    public static string WriteTemp(string outPath, string fileName, byte[] data)
    {
        if (!BuildKit.GameData.IsPlainPluginName(fileName)) throw new ArgumentException("not a plain plugin file name", nameof(fileName));
        var full = Path.GetFullPath(outPath);
        string dir;
        int tries = 0;
        do dir = full + ".tmp-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        while ((Directory.Exists(dir) || File.Exists(dir)) && ++tries < 10);
        if (Directory.Exists(dir) || File.Exists(dir)) throw new IOException($"can't find a free temporary name next to {full}");
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, fileName);
        // CreateNew fails if the name exists, so nothing of someone else's is touched; once this run created the file,
        // a failed write deletes it again
        FileStream s;
        try { s = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
        catch { TryDeleteDir(dir); throw; }
        try
        {
            using (s) s.Write(data);
        }
        catch
        {
            DeleteTemp(tmp);
            throw;
        }
        Log.Write($"edit: wrote {data.Length} bytes to {tmp}");
        return tmp;
    }

    /// <summary>Deletes a temporary file WriteTemp made, then its folder if that is empty. Never throws.</summary>
    public static void DeleteTemp(string tmp)
    {
        try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Write($"edit: can't delete {tmp}: {e.Message}"); }
        if (Path.GetDirectoryName(tmp) is { } dir) TryDeleteDir(dir);
    }

    static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Write($"edit: can't delete {dir}: {e.Message}"); }
    }
}
