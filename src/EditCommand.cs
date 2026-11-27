// `sf-mod-check edit` (0.25.0), plan step 4: the refusals for the input and --out, and the proof. The edit is written
// to a temporary file (PluginEdit.WriteTemp), and kept at --out only when all three proofs pass:
//   diff  - DiffRecords and DiffFields against the input show only the intended changes;
//   gap   - no gap error or warning that the input doesn't have;
//   check - no check ERROR (assets left out) that the input doesn't have.
// Otherwise the temporary file is deleted and the first unexpected difference is reported. The command line, --json and
// the report come in plan step 5. Design: NOTES.md, "## 0.25.0 (SmcDialogue)", "Step 1 design", "The proof".
using System.Text;
using System.Text.RegularExpressions;
using BuildKit;

namespace SfModCheck;

/// <summary>One proof's result: its name (diff, gap, check), whether it passed, and why (the first unexpected
/// difference when it failed, a short summary when it passed).</summary>
public sealed record ProofResult(string Name, bool Passed, string Reason);

/// <summary>What an edit run did: the edit itself, each proof, and whether the file was kept at Out.</summary>
public sealed class EditOutcome
{
    public required string Input { get; init; }
    public required string Out { get; init; }
    public required EditResult Edit { get; init; }
    public List<ProofResult> Proof { get; } = [];
    public bool Kept { get; set; }
    /// <summary>The first proof that failed, or null.</summary>
    public ProofResult? Failed => Proof.FirstOrDefault(p => !p.Passed);
}

public static class EditProof
{
    /// <summary>A test hook: changes the bytes after the edit and before they are written, so a test can force a bad
    /// write and see the proof catch it (design test 11). Never set outside the tests.</summary>
    [ThreadStatic] internal static Func<byte[], byte[]>? CorruptForTest;

    static readonly string[] ArchiveExtensions = [".zip", ".7z", ".rar"];
    static bool IsArchiveName(string part) => ArchiveExtensions.Any(e => part.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    static bool HasWildcard(string p) => p.IndexOfAny(['*', '?']) >= 0;
    /// <summary>Any part of the path (folders included) named like an archive: a path "through" a .zip.</summary>
    static bool ThroughArchive(string full) => full.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Any(IsArchiveName);
    const StringComparison PathCase = StringComparison.OrdinalIgnoreCase;

    /// <summary>Why the input can't be edited (throws EditRefusedException), or returns its full path.</summary>
    public static string CheckInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new EditRefusedException("No plugin was given.", "Give the plugin to edit, for example `sf-mod-check edit MyMod.esm --out MyMod-fixed.esm --set-light`.");
        if (HasWildcard(input))
            throw new EditRefusedException($"The input {SafeText.OneLine(input)} has a wildcard; edit takes one plugin.", "Name one plugin file.");
        string full;
        try { full = Path.GetFullPath(input); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new EditRefusedException($"The input {SafeText.OneLine(input)} isn't a usable path ({e.Message}).", "Give the plugin's path.");
        }
        if (ThroughArchive(full))
            throw new EditRefusedException($"The input {SafeText.OneLine(full)} is an archive or inside one.", "Unpack the archive first, then edit the plugin from the unpacked folder.");
        if (Directory.Exists(full))
            throw new EditRefusedException($"The input {SafeText.OneLine(full)} is a folder; edit takes one plugin.", "Name one plugin file in it.");
        if (!File.Exists(full))
            throw new EditRefusedException($"The input {SafeText.OneLine(full)} doesn't exist.", "Check the path (a path with spaces needs quotes around it).");
        Span<byte> magic = stackalloc byte[4];
        int n;
        using (var f = File.OpenRead(full)) n = f.Read(magic);
        bool zip = n >= 2 && magic[0] == (byte)'P' && magic[1] == (byte)'K';
        bool sevenZip = n >= 4 && magic.SequenceEqual((ReadOnlySpan<byte>)[0x37, 0x7A, 0xBC, 0xAF]);
        bool rar = n >= 4 && magic.SequenceEqual("Rar!"u8);
        if (zip || sevenZip || rar)
            throw new EditRefusedException($"The input {SafeText.OneLine(full)} is an archive (a zip, 7z or rar), not a plugin.", "Unpack it first, then edit the plugin from the unpacked folder.");
        if (!GameData.IsPlainPluginName(Path.GetFileName(full)))
            throw new EditRefusedException($"The input {SafeText.OneLine(full)} isn't named like a plugin.", "Give a .esm, .esp or .esl file.");
        return full;
    }

    /// <summary>Why --out can't be used (throws EditRefusedException), or returns its full path. edit never writes over
    /// anything: --out must be a new plugin file name in a folder that exists, and never the input.</summary>
    public static string CheckOut(string? outPath, string inputFull)
    {
        const string example = "for example --out MyMod-fixed.esm";
        if (string.IsNullOrWhiteSpace(outPath))
            throw new EditRefusedException("--out is missing; edit never changes the input, it writes a checked new copy.", $"Give --out a new file name, {example}.");
        if (HasWildcard(outPath))
            throw new EditRefusedException($"--out {SafeText.OneLine(outPath)} has a wildcard.", $"Give one new file name, {example}.");
        string full;
        try { full = Path.GetFullPath(outPath); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new EditRefusedException($"--out {SafeText.OneLine(outPath)} isn't a usable path ({e.Message}).", $"Give a new file name, {example}.");
        }
        var shown = SafeText.OneLine(full);
        if (OperatingSystem.IsWindows() && DiffCommand.HasStreamColon(full))
            throw new EditRefusedException($"--out {shown} has a ':' after the drive, which writes into another file or folder (an NTFS stream).", $"Give a plain path, {example}.");
        if (ThroughArchive(full))
            throw new EditRefusedException($"--out {shown} is an archive or inside one; edit writes a plugin file.", $"Give a plugin file name in a folder, {example}.");
        if (Directory.Exists(full))
            throw new EditRefusedException($"--out {shown} is a folder.", $"Give a file name in it, for example --out \"{SafeText.OneLine(Path.Combine(full, "MyMod-fixed.esm"))}\".");
        if (string.Equals(full, inputFull, PathCase))
            throw new EditRefusedException($"--out {shown} is the input; edit never changes the input.", $"Give a new file name, {example}.");
        if (DiffCommand.RealPath(full, out var why) is not { } real)
            throw new EditRefusedException($"--out {shown} can't be checked ({why}), so edit can't make sure it isn't the input.", $"Give a plain path, {example}.");
        if (DiffCommand.RealPath(inputFull) is not { } realIn || string.Equals(real, realIn, PathCase))
            throw new EditRefusedException($"--out {shown} is the input (through a link), or the input's real path can't be checked; edit never changes the input.", $"Give a new file name in a plain folder, {example}.");
        if (File.Exists(full) || File.Exists(real) || Directory.Exists(real))
            throw new EditRefusedException($"--out {shown} already exists, and edit never overwrites a file.", "Give a new file name, or move the old file away first.");
        if (!GameData.IsPlainPluginName(Path.GetFileName(full)))
            throw new EditRefusedException($"--out {shown} isn't named like a plugin.", $"Give a .esm, .esp or .esl file name, {example}.");
        var dir = Path.GetDirectoryName(full);
        if (dir == null || !Directory.Exists(dir))
            throw new EditRefusedException($"The folder for --out {shown} doesn't exist.", "Make the folder first, or give --out a file in a folder that exists.");
        return full;
    }

    /// <summary>The first folder holding a master by name: mastersDirs (or the input's folder when none is given), then
    /// the Data folder. Null when it isn't found or the name isn't a plain plugin file name.</summary>
    public static Func<string, string?> MasterFinder(IReadOnlyList<string> dirs) => name =>
    {
        if (!GameData.IsPlainPluginName(name)) return null;
        foreach (var d in dirs)
        {
            var p = Path.Combine(d, name);
            if (File.Exists(p)) return p;
        }
        return null;
    };

    /// <summary>The folders searched for masters, in order: --masters-dir (else the input's folder), then the Data folder.</summary>
    public static List<string> MasterDirs(string inputFull, IReadOnlyList<string> mastersDirs, string dataDir) =>
        (mastersDirs.Count > 0 ? mastersDirs.Select(Path.GetFullPath) : [Path.GetDirectoryName(inputFull)!])
            .Append(Path.GetFullPath(dataDir)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Refuses or applies the plan, writes the result to a temporary file, proves it and keeps it at --out only
    /// when every proof passes. Throws EditRefusedException for a refusal (nothing is written then).</summary>
    public static EditOutcome Run(string input, string? outPath, EditPlan plan, string dataDir, IReadOnlyList<string> mastersDirs)
    {
        var inFull = CheckInput(input);
        var outFull = CheckOut(outPath, inFull);
        var name = Path.GetFileName(inFull);
        var outName = Path.GetFileName(outFull);
        CheckOutName(inFull, outName, plan);
        var dirs = MasterDirs(inFull, mastersDirs, dataDir);
        var find = MasterFinder(dirs);
        var bytes = File.ReadAllBytes(inFull);
        var result = PluginEdit.Apply(bytes, name, m => find(m) is { } p ? RawHeader.Read(p)?.Style : null, plan, inFull, find);

        // the input's side of gap and check first: if they can't run, nothing is written
        // check searches these before the Data folder (it adds the Data folder itself)
        var masterOnly = mastersDirs.Count > 0 ? mastersDirs.Select(Path.GetFullPath).ToList() : [Path.GetDirectoryName(inFull)!];
        GapCheck.Result gapIn;
        try { gapIn = GapCheck.Check(inFull, dirs); }
        catch (GapCheck.MasterNotFoundException e)
        {
            throw new EditRefusedException($"The proof can't run: {e.Message}.", "Give the folder that holds it with --masters-dir, or --data for the game's Data folder.");
        }
        var checkIn = CheckErrors(inFull, dataDir, masterOnly);

        var written = CorruptForTest?.Invoke((byte[])result.Output.Clone()) ?? result.Output;
        var outcome = new EditOutcome { Input = inFull, Out = outFull, Edit = result };
        // 0.28.0: the temporary file has --out's name, so gap and check prove the file under the name it is kept as
        var tmp = PluginEdit.WriteTemp(outFull, outName, written);
        try
        {
            // the diff compares fields through Mutagen, which names own records by the file name, so it reads a second
            // temporary copy under the input's name (its own new folder), deleted as soon as the diff is done
            outcome.Proof.Add(Guard("diff", () =>
            {
                if (outName.Equals(name, PathCase)) return DiffProof(inFull, tmp, plan, result, find);
                var asInput = PluginEdit.WriteTemp(outFull, name, written);
                try { return DiffProof(inFull, asInput, plan, result, find); }
                finally { PluginEdit.DeleteTemp(asInput); }
            }));
            outcome.Proof.Add(Guard("gap", () => GapProof(gapIn, tmp, dirs, name, outName, result.Remap)));
            outcome.Proof.Add(Guard("check", () => CheckProof(checkIn, tmp, dataDir, masterOnly, name, outName, result.Remap)));
            foreach (var p in outcome.Proof) Log.Write($"edit: proof {p.Name}: {(p.Passed ? "passed" : "FAILED")}: {p.Reason}");
            if (outcome.Failed == null)
            {
                File.Move(tmp, outFull, overwrite: false);
                outcome.Kept = true;
                Log.Write($"edit: kept {outFull}");
            }
        }
        finally
        {
            PluginEdit.DeleteTemp(tmp);   // after a move only the empty folder is left
        }
        return outcome;
    }

    static ProofResult Guard(string name, Func<ProofResult> run)
    {
        try { return run(); }
        catch (Exception e) when (Fatal.Recoverable(e))
        {
            return new(name, false, $"the {name} proof couldn't run ({e.GetType().Name}: {SafeText.OneLine(e.Message)})");
        }
    }

    static string Shown(RecKey k, RecInfo r, string self) =>
        $"{r.Sig} {(r.EditorId is { Length: > 0 } e ? SafeText.OneLine(e) + " " : "")}[{k.Local:X6}:{(k.Own ? self : k.File)}]";

    /// <summary>diff: the header changes only as the edits say, no record is added, removed or renumbered, and the only
    /// records whose fields change are the --set-text targets, in their one field. Records whose bytes change but whose
    /// fields are all equal (noise) are allowed with --drop-master only (its renumbering), and for a --set-text target.</summary>
    static ProofResult DiffProof(string inFull, string tmp, EditPlan plan, EditResult result, Func<string, string?> find)
    {
        RawHeader? Header(string m) => find(m) is { } p ? RawHeader.Read(p) : null;
        var self = Path.GetFileName(inFull);
        var oldIx = RecordIndex.Build(inFull, Header, out var e1) ?? throw new InvalidDataException(e1);
        var newIx = RecordIndex.Build(tmp, Header, out var e2);
        if (newIx == null) return new("diff", false, $"the new file can't be read back: {e2}");
        var d = PluginDiff.Compare(oldIx, newIx);
        DiffFields.Apply(d, self);

        foreach (var h in d.Header)
        {
            bool ok = plan.SetLight && h.Text == "type: full -> light"
                || plan.DropMaster != null && h.Text.Equals("master removed: " + plan.DropMaster, StringComparison.OrdinalIgnoreCase);
            if (!ok) return new("diff", false, $"the header changed in a way the edits don't make: {h.Text}");
        }
        if (plan.SetLight && !d.Header.Any(h => h.Text == "type: full -> light")) return new("diff", false, "the light flag isn't set in the new file");
        if (plan.DropMaster != null && !d.Header.Any(h => h.Text.StartsWith("master removed: ", StringComparison.Ordinal))) return new("diff", false, $"{plan.DropMaster} is still a master of the new file");
        if (newIx.PastEnd != oldIx.PastEnd || newIx.Duplicates != oldIx.Duplicates || newIx.Unpackable != oldIx.Unpackable || newIx.Walked != oldIx.Walked)
            return new("diff", false, $"the new file's records don't line up with the input's ({newIx.Walked} records walked, {oldIx.Walked} in the input; past the master list {newIx.PastEnd} and {oldIx.PastEnd}; duplicates {newIx.Duplicates} and {oldIx.Duplicates}; compressed records that don't unpack {newIx.Unpackable} and {oldIx.Unpackable})");
        if (d.Removed.Count > 0) return new("diff", false, $"a record is gone from the new file: {Shown(d.Removed[0].Key, d.Removed[0].Rec, self)}");
        if (d.Added.Count > 0) return new("diff", false, $"the new file has a record the input doesn't: {Shown(d.Added[0].Key, d.Added[0].Rec, self)}");
        if (d.Renumbered.Count > 0) return new("diff", false, $"a record has a new FormID: {Shown(d.Renumbered[0].OldKey, d.Renumbered[0].Old, self)} -> {d.Renumbered[0].NewKey.Local:X6}");
        if (d.FieldsNote != null) return new("diff", false, $"the fields couldn't be compared: {d.FieldsNote}");

        var targets = result.Texts.ToDictionary(t => newIx.KeyOf(t.OutFormId, out _));
        var oldBytes = File.ReadAllBytes(inFull);
        var newBytes = File.ReadAllBytes(tmp);
        if (HeaderFieldProblem(oldBytes, oldIx, newBytes, newIx) is { } hp) return new("diff", false, hp);
        var seen = new HashSet<RecKey>();
        foreach (var c in d.Changed.Concat(d.Noise))
        {
            var isNoise = d.Noise.Contains(c);
            if (targets.TryGetValue(c.Key, out var t))
            {
                seen.Add(c.Key);
                if (c.NotCompared != null) return new("diff", false, $"{Shown(c.Key, c.New, self)}: {c.NotCompared}");
                var allowed = t.Sig == "INFO" ? "Responses" : "Description";
                if (c.Fields.FirstOrDefault(f => f != allowed) is { } other)
                    return new("diff", false, $"{Shown(c.Key, c.New, self)} changed in {other}, not only its text");
                if (RawTextProblem(oldBytes, c.Old, newBytes, c.New, t, plan.DropMaster != null) is { } why)
                    return new("diff", false, $"{Shown(c.Key, c.New, self)}: {why}");
                continue;
            }
            if (isNoise && plan.DropMaster != null) continue;   // the renumbering: bytes differ, every field equal
            if (c.NotCompared != null) return new("diff", false, $"{Shown(c.Key, c.New, self)} changed and {c.NotCompared}");
            return new("diff", false, isNoise
                ? $"{Shown(c.Key, c.New, self)} changed by bytes (its fields read the same), and no edit touches it"
                : $"{Shown(c.Key, c.New, self)} changed in {string.Join(", ", c.Fields)}, and no edit touches it");
        }
        // code review round 3: a --set-text target the diff saw no change in must already hold the new text
        foreach (var (key, t) in targets)
        {
            if (seen.Contains(key)) continue;
            if (!newIx.Records.TryGetValue(key, out var n) || !oldIx.Records.TryGetValue(key, out var o))
                return new("diff", false, $"the --set-text target {t.Sig} {t.OutFormId:X8} isn't in the new file");
            if (RawTextProblem(oldBytes, o, newBytes, n, t, plan.DropMaster != null) is { } why)
                return new("diff", false, $"{Shown(key, n, self)}: {why}");
        }
        var counts = d.Total(true); var ovr = d.Total(false);
        return new("diff", true, $"only the intended changes: {d.Header.Count} header change{(d.Header.Count == 1 ? "" : "s")}, " +
                                 $"{d.Changed.Count} changed record{(d.Changed.Count == 1 ? "" : "s")}, {d.Noise.Count} renumbered without a change in meaning, " +
                                 $"{counts.Same + ovr.Same} the same");
    }

    /// <summary>The raw check of a --set-text target (the new text is never compared through Mutagen as a string,
    /// which reads it as Windows-1252): the field holds exactly the bytes asked for, and every other field is the same
    /// (byte for byte, or with --drop-master the same type and size, the renumbering's change).</summary>
    static string? RawTextProblem(byte[] oldFile, RecInfo o, byte[] newFile, RecInfo n, TextTarget t, bool dropped)
    {
        static List<(string Type, byte[] Data)>? Fields(byte[] file, long off)
        {
            var size = (int)BitConverter.ToUInt32(file, (int)off + 4);
            var body = file.AsSpan((int)off + 24, size).ToArray();
            if ((BitConverter.ToUInt32(file, (int)off + 8) & Flags.Compressed) != 0)
                body = Subrecords.Decompress(body, out _) ?? [];
            var f = Subrecords.Parse(body, out var why);
            return why == null ? f : null;
        }
        var a = Fields(oldFile, o.Offset);
        var b = Fields(newFile, n.Offset);
        if (a == null || b == null) return "its fields can't be read";
        if (a.Count != b.Count) return $"it has {b.Count} fields, the input {a.Count}";
        int seen = 0;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Type != b[i].Type) return $"field {i + 1} is {b[i].Type}, the input's {a[i].Type}";
            if (b[i].Type == t.Field && seen++ == t.Index)
            {
                if (!b[i].Data.AsSpan().SequenceEqual(t.Bytes)) return $"its {t.Field} doesn't hold the new text as written";
                continue;
            }
            if (a[i].Data.AsSpan().SequenceEqual(b[i].Data)) continue;
            if (dropped && a[i].Data.Length == b[i].Data.Length) continue;
            return $"its {b[i].Type} field (field {i + 1}) changed too";
        }
        return null;
    }

    /// <summary>The header's fields other than the master list (each MAST and the DATA after it) are the same: byte for
    /// byte, except ONAM, whose entries must name the same records (--drop-master renumbers them) (code review round 3).</summary>
    static string? HeaderFieldProblem(byte[] oldFile, RecordIndex oldIx, byte[] newFile, RecordIndex newIx)
    {
        static List<(string Type, byte[] Data)>? Fields(byte[] file)
        {
            var size = (int)BitConverter.ToUInt32(file, 4);
            if (24 + (long)size > file.Length) return null;
            var all = Subrecords.Parse(file.AsSpan(24, size).ToArray(), out var why);
            if (why != null) return null;
            var list = new List<(string Type, byte[] Data)>();
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Type == "MAST") { if (i + 1 < all.Count && all[i + 1].Type == "DATA") i++; continue; }
                list.Add(all[i]);
            }
            return list;
        }
        var a = Fields(oldFile);
        var b = Fields(newFile);
        if (a == null || b == null) return "the header's fields can't be read";
        if (a.Count != b.Count) return $"the header has {b.Count} fields besides the masters, the input's {a.Count}";
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Type != b[i].Type) return $"header field {i + 1} is {b[i].Type}, the input's {a[i].Type}";
            if (a[i].Type == "ONAM")
            {
                if (a[i].Data.Length != b[i].Data.Length) return "the header's ONAM list has a different length";
                for (int j = 0; j + 4 <= a[i].Data.Length; j += 4)
                {
                    var ov = BitConverter.ToUInt32(a[i].Data, j);
                    var nv = BitConverter.ToUInt32(b[i].Data, j);
                    if (oldIx.KeyOf(ov, out _) != newIx.KeyOf(nv, out _))
                        return $"ONAM entry {j / 4 + 1} names another record in the new file ({ov:X8} -> {nv:X8})";
                }
                continue;
            }
            if (!a[i].Data.AsSpan().SequenceEqual(b[i].Data)) return $"the header's {a[i].Type} field changed";
        }
        return null;
    }

    static readonly Regex Hex8 = new(@"\b[0-9A-Fa-f]{8}\b", RegexOptions.CultureInvariant);
    static readonly Regex MasterCount = new(@"\(\d+ (full|medium|light) masters\)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    // check's structure messages read "at 0x<file offset>:", and an edit moves the offsets (code review round 4)
    static readonly Regex Offset = new(@"\bat 0x[0-9A-Fa-f]+\b", RegexOptions.CultureInvariant);

    /// <summary>A message with the plugin's name taken out and, with --drop-master, the input's stored FormIDs written
    /// as the output stores them, so a message that names the same thing matches.</summary>
    public static string Normal(string msg, string name, Func<uint, uint>? remap) => Normal(msg, [name], remap);   // public for the tests

    /// <summary>Normal with several plugin names taken out, the longest first (the input's and --out's names on both
    /// sides, so a master whose name ends in one of them reads the same on both sides; 0.28.0 code review round 1).</summary>
    public static string Normal(string msg, IEnumerable<string> names, Func<uint, uint>? remap)
    {
        var s = msg;
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(n => n.Length))
            s = s.Replace(name, "<plugin>", StringComparison.OrdinalIgnoreCase);
        s = Offset.Replace(s, "at 0x<offset>");
        if (remap != null) s = Hex8.Replace(s, m => remap(Convert.ToUInt32(m.Value, 16)).ToString("X8"));
        // --drop-master: check's "(5 full masters)" reads "(4 full masters)" in the output (code review round 3)
        s = MasterCount.Replace(s, m => $"(N {m.Groups[1].Value} masters)");
        return s.ToUpperInvariant();
    }

    /// <summary>Whether a proof message of the output (the file under outName) is one the input (under name) had
    /// already, as Normal writes them (public for the tests).</summary>
    public static Func<string, bool> KnownIn(IReadOnlyList<string> before, string name, string outName, Func<uint, uint>? remap)
    {
        string[] names = [name, outName];
        var known = before.Select(e => Normal(e, names, null)).Concat(before.Select(e => Normal(e, names, remap))).ToHashSet(StringComparer.Ordinal);
        return after => known.Contains(Normal(after, names, null));
    }

    /// <summary>The 0.28.0 refusals of --out's file name, before anything is written: a name that is one of the
    /// output's own masters, an .esl name for an output that isn't light (the game loads every .esl as light), and a
    /// name that isn't .esl for an .esl input that is light only by its extension.</summary>
    static void CheckOutName(string inFull, string outName, EditPlan plan)
    {
        if (RawHeader.Read(inFull) is not { } head) return;   // not a plugin: Apply refuses it with its own message
        var masters = head.Masters.Where(m => plan.DropMaster == null || !m.Equals(plan.DropMaster, PathCase));
        if (masters.Any(m => m.Equals(outName, PathCase)))
            throw new EditRefusedException($"--out {SafeText.OneLine(outName)} is the name of one of the new file's masters, and a plugin can't be its own master.",
                "Give --out another file name, for example MyMod-fixed.esm.");
        // the game loads an .esl as light with or without the flag (0.28.0 code review round 1)
        bool eslIn = Path.GetExtension(inFull).Equals(".esl", PathCase), eslOut = Path.GetExtension(outName).Equals(".esl", PathCase);
        // a medium plugin can't take --set-light (Apply refuses it), so only a full one is advised to (round 5)
        bool medium = head.Style == Style.Medium;
        if (eslIn && !eslOut && head.Style != Style.Light && !plan.SetLight)
            throw new EditRefusedException($"{SafeText.OneLine(Path.GetFileName(inFull))} {(medium ? "is a medium plugin" : "has no light flag")}, but its .esl name makes the game load it as a light plugin; --out {SafeText.OneLine(outName)} would make the new file a {(medium ? "medium" : "full")} plugin, so plugins that use it would point at the wrong records.",
                medium ? "Keep the .esl extension for --out." : "Add --set-light to set the flag, or keep the .esl extension for --out.");
        if (eslOut && !eslIn && head.Style != Style.Light && !plan.SetLight)
            throw new EditRefusedException($"--out {SafeText.OneLine(outName)} ends in .esl, which the game always loads as a light plugin, but the new file is a {head.Style.ToString().ToLowerInvariant()} plugin, so its own FormIDs would point at the wrong records.",
                medium ? "Keep the input's extension (.esm or .esp)." : "Add --set-light (if its own records fit in 000800 to 000FFF), or keep the input's extension (.esm or .esp).");
    }

    static ProofResult GapProof(GapCheck.Result before, string tmp, IReadOnlyList<string> dirs, string name, string outName, Func<uint, uint>? remap)
    {
        var after = GapCheck.Check(tmp, dirs);
        var known = KnownIn(before.Errors.Select(e => "E " + e).Concat(before.Warnings.Select(w => "W " + w)).ToList(), name, outName, remap);
        foreach (var (sev, m) in after.Errors.Select(e => ("E", e)).Concat(after.Warnings.Select(w => ("W", w))))
            if (!known(sev + " " + m))
                return new("gap", false, $"a new gap {(sev == "E" ? "error" : "warning")}: {SafeText.OneLine(m)}");
        return new("gap", true, $"no new loss ({after.Errors.Count} error(s) and {after.Warnings.Count} warning(s), all in the input too)");
    }

    static List<string> CheckErrors(string plugin, string dataDir, List<string> mastersDirs) =>
        Checker.Run(new Options { Plugin = plugin, DataDir = dataDir, MastersDirs = mastersDirs, NoAssets = true, MaxPerCheck = int.MaxValue })
            .Findings.Where(f => f.Severity == "ERROR").Select(f => f.Check + ": " + f.Text).ToList();

    static ProofResult CheckProof(List<string> before, string tmp, string dataDir, List<string> mastersDirs, string name, string outName, Func<uint, uint>? remap)
    {
        var after = CheckErrors(tmp, dataDir, mastersDirs);
        var known = KnownIn(before, name, outName, remap);
        foreach (var e in after)
            if (!known(e))
                return new("check", false, $"a new check ERROR: {SafeText.OneLine(e)}");
        return new("check", true, $"no new ERROR ({after.Count} in all, each in the input too)");
    }
}
