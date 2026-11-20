// Triage section "Plugins" (0.8.0, chunk C2). Ported from ModImpactCheck (Runner, OnamCheck, ScriptCheck; folded in
// 2026-11-19) onto sfmodcheck's own classes. For each plugin of the mod:
//   header      kind (full, medium, light; master flag), masters and the missing ones, unused masters, own FormID range
//               against the flag's limit, record counts, text that isn't valid UTF-8 (raw read, no Mutagen)
//   records     added, overridden and deleted, by record type and by the plugin each override's record comes from (raw)
//   check       sfmodcheck's own checks (Checker.Run without the assets step): ITMs (Overrides.FindItms), links, ids...
//               ERRORs become LOOK lines and WARNINGs NOTE lines, one per check with a count; every line in a table
//   comparison  the official masters plus the mod in one load order, ConflictAnalyzer limited to the mod's records:
//               official changes the mod undoes (a later official master changed a record the mod overrides from an
//               older copy; tools\mod-overlap\stale.cs did this by hand) and the fields the mod changes in official records
//   ONAM        a master-flagged plugin that edits temporary references of full masters must list them in ONAM
//   scripts     every script a record attaches must be in the mod (loose or in an archive that loads with one of its
//               plugins) or in the game's own archives (VanillaIndex); loose files in the player's Data don't count
// Without the game only what the plugins show on their own is reported, with one line saying what wasn't compared.
// Memory: NAPGR 0.1.6 (283 overridden navmeshes and references) once ran the old ModImpactCheck to 14.6 GB. The comparison
// reads one record type at a time and stops at MemoryLimitBytes of managed memory (ConflictAnalyzer's --max-memory
// check); the report then lists the section as incomplete.
using System.Text;
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using SfModCheck.Conflict;
using Impact = SfModCheck.Conflict.Impact;

namespace SfModCheck;

public static class TriagePlugins
{
    const string S = TriageSections.Plugins;
    /// <summary>Most rows per table (the rest are counted in a NOTE).</summary>
    public const int MaxRows = 2000;
    /// <summary>The comparison stops when managed memory passes this (unless conflict's --max-memory is already set).</summary>
    public static long MemoryLimitBytes = 3_000_000_000L;

    static readonly HashSet<string> PluginExts = new(StringComparer.Ordinal) { ".esm", ".esp", ".esl" };
    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    static bool IsOfficial(string name) => Checker.IsOfficial(name) || LoadOrderSetup.IsOfficial(name);

    /// <summary>What one plugin shows on its own (raw read).</summary>
    public sealed class Facts
    {
        public string Name = "", Rel = "", Full = "";
        public RawHeader? Header;
        public string? Unreadable;
        public readonly List<string> MissingMasters = new();
        /// <summary>Masters that are another mod's plugin, found in the game's Data folder (not in this mod).</summary>
        public readonly List<string> FromData = new();
        public readonly Dictionary<string, Style> MasterStyles = new(StringComparer.OrdinalIgnoreCase);
        public int Records, Own, Overrides, Structural;
        /// <summary>Records whose FormID names no master (an index past the plugin's master lists).</summary>
        public int Undecodable;
        public uint MinOwn = uint.MaxValue, MaxOwn;
        /// <summary>record type -> new, overridden, deleted</summary>
        public readonly SortedDictionary<string, int[]> ByType = new(StringComparer.Ordinal);
        /// <summary>(origin plugin, record type) -> overridden, deleted</summary>
        public readonly SortedDictionary<(string Origin, string Sig), int[]> ByOrigin = new();
        public readonly List<(string Sig, string Id, string Origin)> DeletedOverrides = new();
        public int DeletedOwn;
        /// <summary>Each override: its record type, the plugin its record comes from, and the local ID as Mutagen's
        /// FormKey has it (masked to the origin's style).</summary>
        public readonly List<(string Sig, string Origin, uint Local)> OverrideKeys = new();
        /// <summary>Files the plugin's record FormIDs name.</summary>
        public readonly HashSet<string> Used = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<(string Where, string Field, string Hex)> BadText = new();
        public Style Style => Header?.Style ?? Style.Full;
        public bool IsMaster => ((Header?.Flags ?? 0) & Flags.Master) != 0;
    }

    public sealed record OnamResult(bool IsMaster, int TempOverrides, int Listed, List<(string Sig, uint FormId, string Master)> Missing);

    public static IEnumerable<TriageFinding> Run(TriageContext c)
    {
        var f = new List<TriageFinding>();
        void Note(string file, string text) => f.Add(new TriageFinding(TriageLevel.Note, S, file, text));
        void Look(string file, string text) => f.Add(new TriageFinding(TriageLevel.Look, S, file, text));

        // ---- the plugins (one per name: a second copy in another folder, such as a FOMOD option, is named and skipped) ----
        var plugins = new List<Facts>();
        foreach (var rel in c.Files.Where(x => PluginExts.Contains(Path.GetExtension(x))))
        {
            var name = Path.GetFileName(rel);
            if (plugins.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } first)
            {
                Note(rel, $"has the same name as {first.Rel}, so only that one is checked here (only one of them can be installed)");
                continue;
            }
            var full = c.Full(rel);
            plugins.Add(new Facts { Name = RealName(full) ?? name, Rel = rel, Full = full });
        }
        if (plugins.Count == 0)
        {
            Note("", "the mod has no plugins (.esm, .esp or .esl), so there are no records to check");
            return f;
        }
        var byName = plugins.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        bool InMod(string name) => byName.ContainsKey(name);
        string? Locate(string name)
        {
            if (byName.TryGetValue(name, out var p)) return p.Full;
            if (!GameData.IsPlainPluginName(name)) return null;   // a name from the file: never a path (BuildKit's rule)
            var d = Path.Combine(c.DataDir, name);
            return File.Exists(d) ? d : null;
        }
        if (!c.HasGame)
            Note("", $"no game found ({c.DataDir} has no Starfield.esm), so the plugins weren't compared with the game: ITMs, official " +
                     "changes undone, changed fields, unused masters and missing scripts aren't checked. What each plugin shows on its own is below. " +
                     "Give --game \"<the game's folder>\" to compare.");

        var headers = c.Report.Table(S, "Plugin headers", "Plugin", "Kind", "Masters", "Records", "New", "Overrides", "Deleted", "Own FormIDs");
        var types = c.Report.Table(S, "Records by type", "Plugin", "Record type", "New", "Overridden", "Deleted");
        var origins = c.Report.Table(S, "Overrides by origin", "Plugin", "Origin plugin", "Record type", "Overridden", "Deleted");
        var deletedT = c.Report.Table(S, "Deleted records of other plugins", "Plugin", "Record type", "FormID", "Origin plugin");
        var textT = c.Report.Table(S, "Text that isn't valid UTF-8", "Plugin", "Record", "Field", "Bytes");
        var onamT = c.Report.Table(S, "Temporary references missing from ONAM", "Plugin", "Record type", "FormID", "Master");

        // ---- raw facts ----
        foreach (var p in plugins)
        {
            try { ReadFacts(p, Locate, InMod); }
            catch (Exception e) when (Fatal.Recoverable(e)) { p.Unreadable ??= $"{e.GetType().Name}: {e.Message}"; }
            Log.Write($"triage plugins: {p.Rel}: {(p.Header == null ? "no TES4 header" : $"{Kind(p)}, {p.Records} records, {p.Own} new, {p.Overrides} overrides")}");
            if (p.Header == null)
            {
                Look(p.Rel, $"isn't a plugin the game can read: {p.Unreadable ?? "the file doesn't start with a TES4 header"}");
                continue;
            }
            HeaderLines(c, p, headers, Note, Look);
            foreach (var (sig, n) in p.ByType) types.Add(p.Name, sig, n[0].ToString("N0"), n[1].ToString("N0"), n[2].ToString("N0"));
            foreach (var ((origin, sig), n) in p.ByOrigin) origins.Add(p.Name, origin, sig, n[0].ToString("N0"), n[1].ToString("N0"));
            RecordLines(p, deletedT, Note, Look);
            if (p.BadText.Count > 0)
            {
                Note(p.Rel, $"{p.BadText.Count:N0} text field{(p.BadText.Count == 1 ? " isn't" : "s aren't")} valid UTF-8 (the game reads plugin text as UTF-8, " +
                            $"so {(p.BadText.Count == 1 ? "it shows" : "they show")} as garbled characters), e.g. {string.Join(", ", p.BadText.Take(3).Select(b => $"{b.Field} of {b.Where}"))}");
                foreach (var b in p.BadText.Take(MaxRows)) textT.Add(p.Name, b.Where, b.Field, b.Hex);
            }
            try
            {
                var o = Onam(p);
                foreach (var m in o.Missing.Take(MaxRows)) onamT.Add(p.Name, m.Sig, m.FormId.ToString("X8"), m.Master);
                if (o.IsMaster && o.Missing.Count > 0)
                    Look(p.Rel, $"edits {o.Missing.Count:N0} temporary reference{(o.Missing.Count == 1 ? "" : "s")} of full masters that its ONAM list doesn't name " +
                                $"(e.g. {string.Join(", ", o.Missing.Take(3).Select(m => $"{m.Sig} {m.FormId:X8} of {m.Master}"))}): in a master-flagged plugin the game " +
                                "ignores those edits, so the references stay as the master has them");
                else if (o.TempOverrides > 0)
                    Note(p.Rel, o.IsMaster
                        ? $"edits {o.TempOverrides:N0} temporary reference{(o.TempOverrides == 1 ? "" : "s")} of full masters, all listed in its ONAM field ({o.Listed:N0} entries)"
                        : $"edits {o.TempOverrides:N0} temporary reference{(o.TempOverrides == 1 ? "" : "s")} of full masters; it isn't master-flagged, so ONAM doesn't apply");
            }
            catch (Exception e) when (Fatal.Recoverable(e)) { Note(p.Rel, $"its ONAM list couldn't be checked ({e.GetType().Name}: {e.Message})"); }
        }
        var readable = plugins.Where(p => p.Header != null).ToList();
        if (readable.Count == 0) return f;

        // ---- sfmodcheck's own checks ----
        var checkT = c.Report.Table(S, "sf-mod-check check findings", "Plugin", "Severity", "Check", "Text");
        var itmT = c.Report.Table(S, "Identical to master (ITM)", "Plugin", "Record");
        var dirs = readable.Select(p => Path.GetDirectoryName(Path.GetFullPath(p.Full))!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var p in readable) CheckLines(c, p, dirs, checkT, itmT, Note, Look);
        // the check's masters and link caches are gone now; their memory is given back before the game is read again
        GC.Collect();
        GC.WaitForPendingFinalizers();

        if (!c.HasGame) return f;

        // ---- one load order (the official masters and the mod): links, scripts, the comparison ----
        Compare(c, readable, Locate, Note, Look);
        return f;
    }

    // ---------------------------------------------------------------- raw facts

    /// <summary>Reads a plugin's header and every record raw: counts, own FormIDs, deleted records, the files its record
    /// FormIDs name, and text fields that aren't valid UTF-8. locate finds a master by name (the mod first, then Data);
    /// inMod tells the mod's own plugins.</summary>
    public static void ReadFacts(Facts p, Func<string, string?> locate, Func<string, bool> inMod)
    {
        p.Header = RawHeader.Read(p.Full);
        if (p.Header == null) return;
        var h = p.Header;
        foreach (var m in h.Masters)
        {
            var path = locate(m);
            var mh = path == null ? null : RawHeader.Read(path);
            if (mh == null) { p.MissingMasters.Add(m); continue; }
            p.MasterStyles[m] = mh.Style;
            if (!inMod(m) && !IsOfficial(m)) p.FromData.Add(m);
        }
        var ids = new IdMap(p.Name, h.Style, h.Masters.Select(m => (m, p.MasterStyles.GetValueOrDefault(m, Style.Full))));
        bool localized = (h.Flags & Flags.Localized) != 0;
        using var file = new RawFile(p.Full);
        // the header's own text: author (CNAM) and description (SNAM)
        if (file.Length >= 24 && 24 + (long)file.U32(4) <= file.Length)
            foreach (var (t, d) in Subrecords.Parse(file.Span(24, (int)file.U32(4)), out _))
                if (t is "CNAM" or "SNAM" && !ValidUtf8(d)) p.BadText.Add(("the header", t, Hex(d)));
        var w = new RawWalker(file, deep: true, (_, _, _) => p.Structural++)
        {
            OnRecord = rec =>
            {
                var (origin, local, _) = ids.Decode(rec.FormId);
                if (origin == null) { p.Undecodable++; return; }   // a master index past the list: the ids check reports it
                bool deleted = (rec.Flags & Flags.Deleted) != 0;
                p.Used.Add(origin);
                var t = Count(p.ByType, rec.Sig, 3);
                if (origin.Equals(p.Name, StringComparison.OrdinalIgnoreCase))
                {
                    p.Own++;
                    p.MinOwn = Math.Min(p.MinOwn, local);
                    p.MaxOwn = Math.Max(p.MaxOwn, local);
                    t[0]++;
                    if (deleted) p.DeletedOwn++;
                }
                else
                {
                    p.Overrides++;
                    p.OverrideKeys.Add((rec.Sig, origin, IdMap.Mask(local, p.MasterStyles.GetValueOrDefault(origin, Style.Full))));
                    t[1]++;
                    var o = Count(p.ByOrigin, (origin, rec.Sig), 2);
                    o[0]++;
                    if (deleted) { o[1]++; p.DeletedOverrides.Add((rec.Sig, $"{local:X6}", origin)); }
                }
                if (deleted) t[2]++;
            },
            OnFields = (rec, fields) =>
            {
                foreach (var (t, d) in fields)
                    if ((t == "EDID" || (!localized && t is "FULL" or "DESC")) && !ValidUtf8(d))
                        p.BadText.Add(($"{rec.Sig} {rec.FormId:X8}", t, Hex(d)));
            },
        };
        w.Walk();
        p.Records = w.Records;
        if (w.StoppedEarly != null) p.Unreadable = $"the records stop being readable part way ({w.StoppedEarly})";
    }

    /// <summary>The file's name as it is on disk ("TrMod.esm"; c.Files holds lower-case paths), or null.</summary>
    static string? RealName(string full)
    {
        try
        {
            var dir = Path.GetDirectoryName(full);
            var name = Path.GetFileName(full);
            if (dir == null || !Directory.Exists(dir)) return null;
            return Directory.EnumerateFiles(dir).Select(Path.GetFileName).FirstOrDefault(n => string.Equals(n, name, StringComparison.Ordinal))
                ?? Directory.EnumerateFiles(dir).Select(Path.GetFileName).FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    static int[] Count<TK>(SortedDictionary<TK, int[]> d, TK key, int n) where TK : notnull
    {
        if (!d.TryGetValue(key, out var a)) d[key] = a = new int[n];
        return a;
    }

    static bool ValidUtf8(byte[] d)
    {
        var n = d.Length;
        while (n > 0 && d[n - 1] == 0) n--;
        try { StrictUtf8.GetString(d, 0, n); return true; }
        catch (DecoderFallbackException) { return false; }
    }

    static string Hex(byte[] d) => Convert.ToHexString(d, 0, Math.Min(d.Length, 32)) + (d.Length > 32 ? "..." : "");

    static string Kind(Facts p) =>
        p.Style switch { Style.Light => "light", Style.Medium => "medium", _ => "full" } + (p.IsMaster ? " master" : " plugin (not master-flagged)");

    static void HeaderLines(TriageContext c, Facts p, TriageTable headers, Action<string, string> note, Action<string, string> look)
    {
        var h = p.Header!;
        var (_, last) = Plugin.IdRange(p.Style switch { Style.Light => PluginStyle.Light, Style.Medium => PluginStyle.Medium, _ => PluginStyle.Full });
        var styleWord = p.Style.ToString().ToLowerInvariant();
        var range = p.Own == 0 ? "none" : $"{p.MinOwn:X6}-{p.MaxOwn:X6} ({p.Own:N0}; the {styleWord} range ends at {last:X6})";
        var deleted = p.DeletedOwn + p.DeletedOverrides.Count;
        headers.Add(p.Name, Kind(p), h.Masters.Count == 0 ? "none" : string.Join(", ", h.Masters), p.Records.ToString("N0"), p.Own.ToString("N0"),
            p.Overrides.ToString("N0"), deleted.ToString("N0"), range);
        note(p.Rel, $"{Kind(p)}, {p.Records:N0} records: {p.Own:N0} new, {p.Overrides:N0} overrides of other plugins' records" +
                    (deleted > 0 ? $", {deleted:N0} deleted" : "") +
                    $"; masters: {(h.Masters.Count == 0 ? "none" : string.Join(", ", h.Masters.Select(LoadOrderSetup.Display)))}" +
                    (p.Own > 0 ? $"; own FormIDs {p.MinOwn:X6} to {p.MaxOwn:X6} (the {styleWord} range ends at {last:X6})" : ""));
        if (p.Own > 0 && p.MaxOwn > last)
            look(p.Rel, $"its own FormIDs go up to {p.MaxOwn:X6}, past the end of the {styleWord} range ({last:X6}): the game can't give those records " +
                        "their own IDs, so they clash with other records");
        else if (p.Style != Style.Light && p.Own > 0 && p.MaxOwn <= 0xFFF)
            note(p.Rel, $"its {p.Own:N0} own record{(p.Own == 1 ? "" : "s")} fit the light range (FormIDs up to 000FFF), so it could be flagged light " +
                        $"and not take a {p.Style.ToString().ToLowerInvariant()} plugin slot");
        if (p.Unreadable != null) look(p.Rel, $"can't be read to the end: {p.Unreadable}; the counts here cover only the records before that point");
        foreach (var m in p.MissingMasters)
        {
            if (!c.HasGame && IsOfficial(m)) continue;   // the one note above says nothing was compared with the game
            if (!GameData.IsPlainPluginName(m))
            {
                look(p.Rel, $"lists a master \"{GameData.Printable(m)}\" whose name isn't a plain plugin file name (it holds a folder, drive or network path), so it wasn't looked up");
                continue;
            }
            look(p.Rel, $"needs the master {LoadOrderSetup.Display(m)}, which is neither in the mod nor in {(c.HasGame ? "the game's Data folder" : "the given game folder")}: " +
                        "the game won't load this plugin without it (the counts here take it as a full plugin; if it is light or medium, " +
                        "some of its records may be counted as this plugin's own)");
        }
        foreach (var m in p.FromData)
            note(p.Rel, $"needs {m}, another mod's plugin (found in the game's Data folder, not in this mod): it is a requirement to install first");
    }

    static void RecordLines(Facts p, TriageTable deletedT, Action<string, string> note, Action<string, string> look)
    {
        foreach (var d in p.DeletedOverrides.Take(MaxRows)) deletedT.Add(p.Name, d.Sig, d.Id, d.Origin);
        foreach (var g in p.DeletedOverrides.GroupBy(d => d.Origin, StringComparer.OrdinalIgnoreCase))
        {
            var n = g.Count();
            var sigs = string.Join(", ", g.GroupBy(d => d.Sig).OrderByDescending(x => x.Count()).Select(x => $"{x.Count():N0} {x.Key}"));
            var text = $"deletes {n:N0} record{(n == 1 ? "" : "s")} of {LoadOrderSetup.Display(g.Key)} ({sigs}; e.g. {string.Join(", ", g.Take(3).Select(d => $"{d.Sig} {d.Id}"))}): " +
                       "a quest or another mod that still uses a deleted record can crash the game" +
                       // the tip fits placed references only (NAPGR 0.1.6 deletes navmeshes alone; C5 acceptance)
                       (g.Any(d => d.Sig is "REFR" or "ACHR" or "PGRE" or "PMIS" or "PHZD" or "PARW" or "PBAR" or "PBEA" or "PCON" or "PFLA")
                           ? " (xEdit's Undelete and Disable is the usual way to remove placed references)" : "");
            if (IsOfficial(g.Key)) look(p.Rel, text); else note(p.Rel, text);
        }
        if (p.DeletedOwn > 0) note(p.Rel, $"{p.DeletedOwn:N0} of its own new records are marked deleted (the game leaves them out)");
        if (p.Overrides > 0)
        {
            var by = p.ByOrigin.GroupBy(k => k.Key.Origin, StringComparer.OrdinalIgnoreCase).Select(g => (g.Key, N: g.Sum(x => x.Value[0])))
                .OrderByDescending(x => x.N).ToList();
            note(p.Rel, $"overrides {p.Overrides:N0} record{(p.Overrides == 1 ? "" : "s")} of other plugins ({string.Join(", ", by.Select(x => $"{x.N:N0} of {x.Key}"))}; " +
                        "by type in the table): where another mod changes the same records, the one that loads later wins");
        }
    }

    // ---------------------------------------------------------------- ONAM

    static readonly HashSet<string> RefSigs = ["REFR", "ACHR", "PGRE", "PMIS", "PARW", "PBEA", "PFLA", "PCON", "PBAR", "PHZD"];

    /// <summary>The temporary references of full masters a plugin overrides, and which its TES4 ONAM list leaves out
    /// (port of ModImpactCheck's OnamCheck and tools\onam-check.py, with IdMap for Starfield's per-style master lists).
    /// Light masters' references are left out (the Creation Kit doesn't list them); a master that can't be found counts
    /// as full. Needs ReadFacts first (the masters' styles).</summary>
    public static OnamResult Onam(Facts p)
    {
        var h = p.Header ?? throw new InvalidDataException($"{p.Name} has no TES4 header");
        var onam = new HashSet<uint>();
        using var f = new RawFile(p.Full);
        if (f.Length < 24) throw new InvalidDataException($"{p.Name} is too short to be a plugin");
        var hsize = (long)f.U32(4);
        if (24 + hsize > f.Length) throw new InvalidDataException($"{p.Name}: the TES4 header runs past the end of the file");
        foreach (var (t, d) in Subrecords.Parse(f.Span(24, (int)hsize), out _))
            if (t == "ONAM") for (int i = 0; i + 4 <= d.Length; i += 4) onam.Add(BitConverter.ToUInt32(d, i));
        var ids = new IdMap(p.Name, h.Style, h.Masters.Select(m => (m, p.MasterStyles.GetValueOrDefault(m, Style.Full))));
        var temps = new List<(string Sig, uint Fid, string Master)>();
        void Walk(long pos, long end, bool inTemp, int depth)
        {
            if (depth > 16) return;
            while (pos + 24 <= end)
            {
                var sig = f.Sig(pos);
                var size = (long)f.U32(pos + 4);
                if (sig == "GRUP")
                {
                    if (size < 24 || pos + size > end) return;   // the structure check reports it
                    var gtype = f.I32(pos + 12);
                    Walk(pos + 24, pos + size, gtype == 9 || (inTemp && gtype is not (6 or 8)), depth + 1);
                    pos += size;
                    continue;
                }
                var fid = f.U32(pos + 12);
                if (inTemp && RefSigs.Contains(sig))
                {
                    var (file, _, written) = ids.Decode(fid);
                    if (file != null && written == Style.Full && !file.Equals(p.Name, StringComparison.OrdinalIgnoreCase)
                        && p.MasterStyles.GetValueOrDefault(file, Style.Full) != Style.Light)
                        temps.Add((sig, fid, file));
                }
                pos += 24 + size;
            }
        }
        Walk(24 + hsize, f.Length, false, 0);
        var missing = temps.Where(x => !onam.Contains(x.Fid)).ToList();
        return new OnamResult((h.Flags & Flags.Master) != 0, temps.Count, onam.Count, missing);
    }

    // ---------------------------------------------------------------- sfmodcheck check

    static string WithoutItmTail(string text)
    {
        var i = text.LastIndexOf(" is identical", StringComparison.Ordinal);
        return i > 0 ? text[..i] : text;
    }

    static void CheckLines(TriageContext c, Facts p, List<string> dirs, TriageTable checkT, TriageTable itmT,
                           Action<string, string> note, Action<string, string> look)
    {
        Result r;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // the assets step is left out: scripts are checked below against the mod and the game's own archives only
            var data = Directory.Exists(c.DataDir) ? c.DataDir : dirs[0];
            r = Checker.Run(new Options { Plugin = p.Full, DataDir = data, MastersDirs = dirs, NoAssets = true });
        }
        catch (Exception e) when (Fatal.Recoverable(e))
        {
            Log.Write($"triage plugins: check {p.Name} stopped: {e}");
            note(p.Rel, $"sf-mod-check's plugin check stopped on it ({e.GetType().Name}: {Impact.OneLine(e.Message)})");
            return;
        }
        Log.Write($"triage plugins: check {p.Name}: {r.Summary}, {r.Errors} errors, {r.Warnings} warnings, {sw.Elapsed.TotalSeconds:F1} s, " +
                  $"peak working set {System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 / 1e9:F2} GB");
        int rows = 0;
        foreach (var x in r.Findings)
            if (rows++ < MaxRows) checkT.Add(p.Name, x.Severity, x.Check, x.Text);
        if (rows > MaxRows) note(p.Rel, $"sf-mod-check's plugin check gave {rows:N0} lines; the table lists the first {MaxRows:N0} (run sf-mod-check check on the plugin for all)");

        var itms = r.Findings.Where(x => x.Check == "itm" && x.Text.EndsWith("(ITM)", StringComparison.Ordinal)).ToList();
        foreach (var x in itms.Take(MaxRows)) itmT.Add(p.Name, WithoutItmTail(x.Text));
        if (itms.Count > 0)
            look(p.Rel, $"{itms.Count:N0} override{(itms.Count == 1 ? " is" : "s are")} identical to the master version (ITM): " +
                        (itms.Count == 1 ? "it changes nothing, but still wins over earlier mods' edits to the same record, so it can quietly undo them"
                                         : "they change nothing, but still win over earlier mods' edits to the same records, so they can quietly undo them") +
                        $" (e.g. {string.Join(", ", itms.Take(3).Select(x => WithoutItmTail(x.Text)))})");
        else if (!r.CannotCheckLinks && p.Overrides > 0) note(p.Rel, $"none of its {p.Overrides:N0} overrides is identical to the master version (no ITMs)");

        // said elsewhere in this section: missing masters (header lines), deleted records (record lines), ITMs (above)
        bool Elsewhere(Finding x) => (x.Check == "records" && x.Text.Contains(" deleted ")) || (x.Check == "itm" && itms.Contains(x))
                                     || (x.Check == "header" && (x.Text.Contains("is not installed") || x.Text.Contains("not a plain plugin")));
        foreach (var g in r.Findings.Where(x => !Elsewhere(x)).GroupBy(x => (x.Severity, x.Check))
                     .OrderBy(g => g.Key.Severity == "ERROR" ? 0 : 1).ThenBy(g => g.Key.Check, StringComparer.Ordinal))
        {
            var n = g.Count();
            var text = $"sf-mod-check's plugin check: {n:N0} {(g.Key.Severity == "ERROR" ? "error" : "warning")}{(n == 1 ? "" : "s")} in its {g.Key.Check} check " +
                       $"({Impact.OneLine(g.First().Text)}{(n > 1 ? $"; all {n:N0} are in the table" : "")})";
            if (g.Key.Severity == "ERROR") look(p.Rel, text); else note(p.Rel, text);
        }
        if (r.CannotCheckLinks && c.HasGame && p.MissingMasters.Count == 0)
            note(p.Rel, "sf-mod-check's plugin check couldn't read the records with their masters, so links and ITMs weren't checked");
    }

    // ---------------------------------------------------------------- the comparison

    /// <summary>Records that live in cells and worldspaces (and the navmesh index): compared raw, field type by field type.
    /// Reading them through Mutagen means reading every such record of the game (NAPGR 0.1.6's navmeshes and references
    /// kept over 5 GB that way).</summary>
    public static readonly HashSet<string> CellBound = new(StringComparer.Ordinal)
        { "CELL", "WRLD", "LAND", "NAVM", "NAVI", "REFR", "ACHR", "PGRE", "PMIS", "PARW", "PBEA", "PFLA", "PCON", "PBAR", "PHZD" };

    /// <summary>One change for the report: a field the mod changed in an official record, or an official change it undoes.</summary>
    sealed record Change(string Plugin, string Record, string Type, string Key, string Other, string Field, string Before, string After);

    static void Compare(TriageContext c, List<Facts> plugins, Func<string, string?> locate, Action<string, string> note, Action<string, string> look)
    {
        var modNames = plugins.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // another mod's plugins this one needs (found in Data) go first, then the mod's own, each after its masters
        var listed = new List<string>();
        void Add(string name, HashSet<string> chain)
        {
            if (listed.Contains(name, StringComparer.OrdinalIgnoreCase) || !chain.Add(name) || IsOfficial(name)) return;
            if (locate(name) is not { } path) return;
            try { foreach (var m in LoadOrderSetup.ModHeaderMasters(path)) Add(m, chain); }
            catch (Exception e) when (Fatal.Recoverable(e)) { Log.Write($"triage plugins: masters of {name} not read: {e.Message}"); }
            listed.Add(name);
        }
        foreach (var p in plugins) Add(p.Name, new(StringComparer.OrdinalIgnoreCase));
        var txt = Path.Combine(c.Work, "triage-plugins.txt");
        File.WriteAllLines(txt, listed.Select(n => "*" + n));
        // the mod's plugins as the second folder (its files win over Data's); copied together when they sit apart
        var dirs = plugins.Select(p => Path.GetDirectoryName(p.Full)!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var extra = dirs[0];
        if (dirs.Count > 1)
        {
            extra = Path.Combine(c.Work, "plugins");
            Directory.CreateDirectory(extra);
            foreach (var p in plugins) File.Copy(p.Full, Path.Combine(extra, p.Name), true);
        }
        Log.Write($"triage plugins: load order from {c.DataDir} and {extra}: {string.Join(", ", listed)}");

        LoadOrderSetup lo;
        try { lo = LoadOrderSetup.Build(c.DataDir, txt, extra); }
        catch (Exception e) when (Fatal.Recoverable(e))
        {
            note("", $"the game's plugins couldn't be loaded ({e.GetType().Name}: {Impact.OneLine(e.Message)}), so nothing was compared with them");
            return;
        }
        var oldLimit = Analyzer.MaxManagedBytes;
        var oldInfo = Analyzer.Info;
        var oldProgress = Analyzer.Progress;
        try
        {
            foreach (var pr in lo.Problems)
            {
                Log.Write($"triage plugins: load order: {pr}");
                if (plugins.FirstOrDefault(p => pr.StartsWith(p.Name + ":", StringComparison.OrdinalIgnoreCase)) is { } who)
                    note(who.Rel, $"left out of the comparison with the game:{pr[(who.Name.Length + 1)..]}");
                else if (pr.StartsWith("Starfield.esm:", StringComparison.OrdinalIgnoreCase))
                    note("", $"the game's Starfield.esm couldn't be opened ({pr}), so nothing was compared with the game");
            }
            var ours = lo.Plugins.Where(p => modNames.Contains(p.FileName)).ToList();
            if (ours.Count == 0 || !lo.Plugins.Any(p => p.FileName.Equals("Starfield.esm", StringComparison.OrdinalIgnoreCase))) return;

            UnusedMasters(plugins, ours, lo, note);
            Scripts(c, plugins, ours, lo, note, look);
            GC.Collect();

            var changes = new List<Change>();
            var undone = new List<Change>();
            var oursFacts = plugins.Where(p => ours.Any(o => o.FileName.Equals(p.Name, StringComparison.OrdinalIgnoreCase))).ToList();

            // 1. records outside cells: ConflictAnalyzer, field by field, limited to the mod's records
            var keys = new HashSet<Mutagen.Bethesda.Plugins.FormKey>();
            foreach (var p in oursFacts)
                foreach (var (sig, origin, local) in p.OverrideKeys)
                    if (!CellBound.Contains(sig) && Mutagen.Bethesda.Plugins.ModKey.TryFromFileName(origin, out var mk))
                        keys.Add(new Mutagen.Bethesda.Plugins.FormKey(mk, local));
            if (keys.Count > 0)
            {
                Analyzer.MaxManagedBytes = oldLimit > 0 ? oldLimit : MemoryLimitBytes;
                Analyzer.Info = s => Log.Write("triage plugins: " + s);
                Analyzer.Progress = s => { Log.Write("triage plugins: " + s); GC.Collect(); };   // once per record type: nothing of the last type is kept
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var a = Analyzer.Run(lo.Plugins, ours.Select(p => p.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase), keys);
                    Log.Write($"triage plugins: {keys.Count} records compared through Mutagen in {sw.Elapsed.TotalSeconds:F1} s, " +
                              $"peak working set {System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 / 1e9:F2} GB");
                    FromAnalysis(a, modNames, changes, undone, (name, text) => note(plugins.First(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Rel, text), note);
                }
                catch (MemoryLimitException e)
                {
                    GC.Collect();
                    c.Report.Incomplete.Add($"Plugins: the comparison with the game stopped ({e.Message}), so some official changes undone and changed fields aren't listed");
                    note("", $"the comparison with the game stopped ({e.Message}): official changes undone and changed fields outside cells aren't listed");
                }
                catch (Exception e) when (Fatal.Recoverable(e))
                {
                    Log.Write($"triage plugins: comparison stopped: {e}");
                    note("", $"the comparison with the game stopped while {Analyzer.Where} ({e.GetType().Name}: {Impact.OneLine(e.Message)})");
                }
                finally { Analyzer.Where = ""; }
            }

            // 2. records in cells and worldspaces: raw
            try { RawCompare(lo, oursFacts, changes, undone, (name, text) => note(plugins.First(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Rel, text)); }
            catch (Exception e) when (Fatal.Recoverable(e))
            {
                Log.Write($"triage plugins: raw comparison stopped: {e}");
                note("", $"the comparison of records in cells and worldspaces stopped ({e.GetType().Name}: {Impact.OneLine(e.Message)}), so those aren't listed");
            }
            Log.Write($"triage plugins: comparison done, peak working set {System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 / 1e9:F2} GB");
            Results(c, plugins, changes, undone, note, look);
        }
        finally
        {
            Analyzer.MaxManagedBytes = oldLimit; Analyzer.Info = oldInfo; Analyzer.Progress = oldProgress;
            foreach (var p in lo.Plugins) p.Mod.Dispose();
        }
    }

    static string Rec(string? editorId, string type, string formKey) => $"{editorId ?? "(no EditorID)"} [{type}:{formKey}]";

    static void FromAnalysis(Analysis a, HashSet<string> modNames, List<Change> changes, List<Change> undone,
                             Action<string, string> notePlugin, Action<string, string> note)
    {
        // official changes the mod undoes: a later official master changed the record, and the mod (which doesn't have it
        // as a master) overrides it from an older copy, so the game takes the mod's values for those fields
        foreach (var l in a.Lost.Where(l => l.LoserOfficial && modNames.Contains(l.Winner)))
            foreach (var fl in l.Fields)
                undone.Add(new(l.Winner, Rec(l.EditorID, l.RecordType, l.FormKey.ToString()), l.RecordType, l.FormKey.ToString(), l.Loser, fl.Field, fl.LoserValue, fl.WinnerValue));
        // two plugins of this mod on the same record
        foreach (var g in a.Lost.Where(l => !l.LoserOfficial && modNames.Contains(l.Loser) && modNames.Contains(l.Winner)).GroupBy(l => (l.Winner, l.Loser)))
        {
            var e = g.First();
            notePlugin(g.Key.Loser, $"{g.Count():N0} of its changed record{(g.Count() == 1 ? " is" : "s are")} also changed by {g.Key.Winner}, which loads later and wins " +
                              $"(e.g. {Rec(e.EditorID, e.RecordType, e.FormKey.ToString())}): the mod's own plugins overlap");
        }
        // a record the mod deletes is listed with the deleted records, not field by field
        var deleted = a.Changes.Where(ch => ch.Field == "IsDeleted").Select(ch => (ch.Plugin, ch.FormKey)).ToHashSet();
        foreach (var ch in a.Changes.Where(ch => modNames.Contains(ch.Plugin) && IsOfficial(ch.Origin) && !deleted.Contains((ch.Plugin, ch.FormKey))))
            changes.Add(new(ch.Plugin, Rec(ch.EditorID, ch.RecordType, ch.FormKey.ToString()), ch.RecordType, ch.FormKey.ToString(),
                ch.Base == "" ? "(none)" : ch.Base, ch.Field, ch.BaseValue, ch.NewValue));
        if (a.ChangesNotListed > 0) note("", $"{a.ChangesNotListed:N0} more changed fields weren't kept (over {Analysis.MaxChanges:N0})");
        foreach (var e in a.Errors.Take(10)) note("", $"the comparison with the game: {e}");
        if (a.Errors.Count > 10) note("", $"the comparison with the game: {a.Errors.Count - 10:N0} more such lines (run with --log to see them)");
        if (a.UnreadableFields.Count > 0)
            note("", $"the comparison couldn't read {a.UnreadableFields.Values.Sum():N0} field value(s) ({string.Join(", ", a.UnreadableFields.Keys.Take(5))}), so those weren't compared");
    }

    static void Results(TriageContext c, List<Facts> plugins, List<Change> changes, List<Change> undone, Action<string, string> note, Action<string, string> look)
    {
        string RelOf(string name) => plugins.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Rel ?? name;

        var undoneT = c.Report.Table(S, "Official changes undone", "Plugin", "Record", "Official plugin", "Field", "Official value", "This plugin's value");
        foreach (var u in undone.Take(MaxRows)) undoneT.Add(u.Plugin, u.Record, u.Other, u.Field, Cut(u.Before), Cut(u.After));
        foreach (var g in undone.GroupBy(u => (u.Plugin, u.Other)))
        {
            var recs = g.Select(u => u.Key).Distinct().ToList();
            var n = recs.Count;
            var fields = g.GroupBy(u => u.Field).OrderByDescending(x => x.Count()).Take(5).Select(x => x.Key);
            look(RelOf(g.Key.Plugin), $"undoes {LoadOrderSetup.Display(g.Key.Other)}'s changes to {n:N0} record{(n == 1 ? "" : "s")}: it overrides " +
                                      $"{(n == 1 ? "a record" : "records")} {g.Key.Other} also changed, without having {g.Key.Other} as a master, so the game uses this " +
                                      $"plugin's older values for {string.Join(", ", fields)} (e.g. {string.Join(", ", g.Select(u => u.Record).Distinct().Take(3))}). " +
                                      "Often the mod was made before that update; a patch that carries both changes keeps both");
        }
        if (undone.Count > MaxRows) note("", $"{undone.Count:N0} undone official fields; the table lists the first {MaxRows:N0}");

        var changesT = c.Report.Table(S, "Changes to official records", "Plugin", "Record", "Built on", "Field", "Before", "After");
        foreach (var ch in changes.Take(MaxRows)) changesT.Add(ch.Plugin, ch.Record, ch.Other, ch.Field, Cut(ch.Before), Cut(ch.After));
        foreach (var g in changes.GroupBy(ch => ch.Plugin, StringComparer.OrdinalIgnoreCase))
        {
            var n = g.Count();
            var recs = g.Select(ch => ch.Key).Distinct().Count();
            var top = g.GroupBy(ch => (ch.Type, ch.Field)).OrderByDescending(x => x.Count()).Take(5).Select(x => $"{x.Key.Type}.{x.Key.Field} x{x.Count():N0}");
            note(RelOf(g.Key), $"changes {n:N0} field{(n == 1 ? "" : "s")} in {recs:N0} official record{(recs == 1 ? "" : "s")}; most often {string.Join(", ", top)} " +
                               "(each change, with the value before and after, is in the table)");
        }
        if (changes.Count > MaxRows) note("", $"{changes.Count:N0} changed fields in official records; the table lists the first {MaxRows:N0}");
    }

    // ---------------------------------------------------------------- raw comparison (records in cells and worldspaces)

    /// <summary>One version of a record, read raw: its plugin, deleted flag, EditorID, and each field's type, size and a
    /// hash of its bytes (no bytes kept).</summary>
    sealed record RawVersion(string Plugin, bool Official, bool Deleted, bool Partial, string? EditorId, List<(string Type, int Hash, int Size)> Fields);

    /// <summary>Starfield's partial form flag: the record holds only the fields it changes (FieldCompare.PartialFormFlag).</summary>
    const uint PartialForm = 0x4000;

    // fields left out: a worldspace's offset, cell-size and reference tables (the Creation Kit leaves them out of
    // overrides and the game rebuilds them; GapCheck skips them too)
    static readonly HashSet<string> SkipWorld = new(StringComparer.Ordinal) { "OFST", "CLSZ", "RNAM" };

    static readonly Dictionary<string, string> FieldWords = new(StringComparer.Ordinal)
    {
        ["EDID"] = "EditorID", ["FULL"] = "name", ["NVNM"] = "navmesh geometry", ["XCLC"] = "cell grid", ["NAME"] = "base object",
        ["DATA"] = "data", ["XSCL"] = "scale", ["XTEL"] = "teleport destination", ["XESP"] = "enable parent",
        ["XLRT"] = "location ref type", ["XLRL"] = "location", ["NNAM"] = "navmesh data", ["XEZN"] = "encounter zone",
    };

    static string FieldName(string type) => FieldWords.TryGetValue(type, out var w) ? $"{type} ({w})" : type;

    /// <summary>Compares the mod's overrides of records in cells and worldspaces with the game's versions, raw: which field
    /// types the mod changed against the version it was built on (the last of its masters that has the record), and which
    /// later official changes it undoes (an official version from a plugin that isn't one of its masters, changed in a
    /// field where the mod keeps the older value).</summary>
    static void RawCompare(LoadOrderSetup lo, List<Facts> ours, List<Change> changes, List<Change> undone, Action<string, string> notePlugin)
    {
        var want = new HashSet<(string, uint)>();
        foreach (var p in ours)
            foreach (var (sig, origin, local) in p.OverrideKeys)
                if (CellBound.Contains(sig)) want.Add((origin.ToLowerInvariant(), local));
        if (want.Count == 0) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var versions = new Dictionary<(string, uint), List<RawVersion>>();
        var styleOf = new Dictionary<string, Style>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in lo.Plugins)
        {
            var path = info.Path;
            var h = RawHeader.Read(path);
            if (h == null) continue;
            styleOf[info.FileName] = h.Style;
            var ids = new IdMap(info.FileName, h.Style, h.Masters.Select(m => (m, styleOf.GetValueOrDefault(m, Style.Full))));
            using var f = new RawFile(path);
            new RawWalker(f, deep: false, (_, _, _) => { })
            {
                OnRecord = rec =>
                {
                    var (file, local, _) = ids.Decode(rec.FormId);
                    if (file == null) return;
                    var key = (file.ToLowerInvariant(), IdMap.Mask(local, styleOf.GetValueOrDefault(file, Style.Full)));
                    if (!want.Contains(key)) return;
                    if (!versions.TryGetValue(key, out var list)) versions[key] = list = new();
                    if (list.Count > 0 && list[^1].Plugin == info.FileName) return;
                    list.Add(ReadVersion(f, rec, info));
                },
            }.Walk();
        }
        Log.Write($"triage plugins: raw comparison read {want.Count} records' versions from {lo.Plugins.Count} plugins in {sw.Elapsed.TotalSeconds:F1} s");

        var mastersOf = lo.Plugins.ToDictionary(p => p.FileName, p => p.Masters, StringComparer.OrdinalIgnoreCase);
        HashSet<string> AllMasters(string name)
        {
            var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Walk(string n) { foreach (var m in mastersOf.GetValueOrDefault(n) ?? []) if (all.Add(m)) Walk(m); }
            Walk(name);
            return all;
        }
        foreach (var p in ours)
        {
            var masters = AllMasters(p.Name);
            foreach (var (sig, origin, local) in p.OverrideKeys.Where(k => CellBound.Contains(k.Sig)).Distinct())
            {
                if (!versions.TryGetValue((origin.ToLowerInvariant(), local), out var vs)) continue;
                var mine = vs.FindLast(v => v.Plugin.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
                if (mine == null || mine.Deleted) continue;   // a deleted record is listed with the deleted records
                var at = vs.IndexOf(mine);
                var baseIdx = vs.FindLastIndex(at, v => masters.Contains(v.Plugin));
                if (baseIdx < 0) continue;
                var bv = vs[baseIdx];
                var key = $"{local:X6}:{origin}";
                var name = Rec(mine.EditorId ?? bv.EditorId, sig, key);
                if (!IsOfficial(origin)) continue;   // changes to another mod's records aren't listed (only official ones)
                foreach (var t in Differ(bv, mine, sig))
                    changes.Add(new(p.Name, name, sig, key, bv.Plugin, FieldName(t), Describe(bv, t), Describe(mine, t)));
                // later official versions the mod's copy doesn't build on
                var later = vs.Skip(baseIdx + 1).Take(at - baseIdx - 1).Where(v => v.Official && !v.Deleted && !masters.Contains(v.Plugin)).ToList();
                if (sig == "NAVI")
                {
                    // The navmesh info map: every navmesh mod overrides it, Bethesda's own add-ons each carry a full copy and
                    // still work together, so the game merges its entries (QuickCheck NAPG EVALUATION.md). Not an undone change.
                    if (later.Count > 0)
                        notePlugin(p.Name, $"overrides the navmesh info map ({name}), as every navmesh mod does; {later.Count:N0} official file" +
                                           $"{(later.Count == 1 ? "" : "s")} that aren't its masters carry it too ({string.Join(", ", later.Take(3).Select(v => v.Plugin))}" +
                                           $"{(later.Count > 3 ? " and more" : "")}). The game merges its entries, so this isn't listed as an undone change");
                    continue;
                }
                foreach (var v in later)
                    foreach (var t in Differ(bv, v, sig))
                    {
                        if (mine.Partial && !mine.Fields.Any(x => x.Type == t)) continue;   // a partial form keeps the value it leaves out
                        if (!Same(v, mine, t)) undone.Add(new(p.Name, name, sig, key, v.Plugin, FieldName(t), Describe(v, t), Describe(mine, t)));
                    }
            }
        }
    }

    static RawVersion ReadVersion(RawFile f, RawRecord rec, PluginInfo info)
    {
        var fields = new List<(string, int, int)>();
        string? edid = null;
        if (rec.Size <= int.MaxValue && rec.Offset + 24 + rec.Size <= f.Length)
        {
            ReadOnlySpan<byte> body = f.Span(rec.Offset + 24, (int)rec.Size);
            byte[]? unpacked = null;
            if ((rec.Flags & Flags.Compressed) != 0) unpacked = Subrecords.Decompress(body, out _);
            if ((rec.Flags & Flags.Compressed) == 0 || unpacked != null)
                foreach (var (t, d) in Subrecords.Parse(unpacked ?? body, out _))
                {
                    if (t == "EDID") { var n = Array.IndexOf(d, (byte)0); edid = Encoding.UTF8.GetString(d, 0, n < 0 ? d.Length : n); }
                    // a cell grid's 3 unused bytes hold junk in Bethesda's files (DeepCompare's CellGridEq): X, Y and the land flags only
                    var span = t == "XCLC" && d.Length >= 9 ? d.AsSpan(0, 9) : d.AsSpan();
                    var hc = new HashCode();
                    hc.AddBytes(span);
                    fields.Add((t, hc.ToHashCode(), d.Length));
                }
        }
        return new RawVersion(info.FileName, info.Official, (rec.Flags & Flags.Deleted) != 0, (rec.Flags & PartialForm) != 0, edid, fields);
    }

    static List<(int Hash, int Size)> Values(RawVersion v, string type) => v.Fields.Where(x => x.Type == type).Select(x => (x.Hash, x.Size)).ToList();

    static bool Same(RawVersion a, RawVersion b, string type) => Values(a, type).SequenceEqual(Values(b, type));

    /// <summary>The field types whose values differ between two versions, in the order they first appear. When b is a
    /// partial form only the fields it holds count (the others keep a's values).</summary>
    static IEnumerable<string> Differ(RawVersion a, RawVersion b, string sig) =>
        (b.Partial ? b.Fields.Select(x => x.Type) : a.Fields.Select(x => x.Type).Concat(b.Fields.Select(x => x.Type))).Distinct()
            .Where(t => !(sig == "WRLD" && SkipWorld.Contains(t)) && !Same(a, b, t));

    static string Describe(RawVersion v, string type)
    {
        var vals = Values(v, type);
        if (vals.Count == 0) return "(none)";
        return vals.Count == 1 ? $"{vals[0].Size:N0} bytes, #{(uint)vals[0].Hash:X8}" : $"{vals.Count:N0} entries, {vals.Sum(x => x.Size):N0} bytes";
    }

    static string Cut(string s) => s.Length > 160 ? s[..160] + "..." : s;

    // ---------------------------------------------------------------- unused masters (Mutagen links)

    static void UnusedMasters(List<Facts> facts, List<PluginInfo> ours, LoadOrderSetup lo, Action<string, string> note)
    {
        var mastersOf = lo.Plugins.ToDictionary(p => p.FileName, p => p.Masters, StringComparer.OrdinalIgnoreCase);
        foreach (var info in ours)
        {
            var p = facts.First(x => x.Name.Equals(info.FileName, StringComparison.OrdinalIgnoreCase));
            var used = new HashSet<string>(p.Used, StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var rec in info.Mod.EnumerateMajorRecords())
                    try { foreach (var l in rec.EnumerateFormLinks()) if (!l.IsNull) used.Add(l.FormKey.ModKey.FileName); }
                    catch (Exception e) when (Fatal.Recoverable(e)) { Log.Write($"triage plugins: links of a record in {p.Name} not read: {e.Message}"); }
            }
            catch (Exception e) when (Fatal.Recoverable(e))
            {
                Log.Write($"triage plugins: records of {p.Name} not all read: {e.Message}");
                note(p.Rel, "its records couldn't all be read, so unused masters weren't looked for");
                continue;
            }
            var masters = p.Header!.Masters;
            // a master another master needs has to stay, used or not
            var neededBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in masters)
                foreach (var mm in mastersOf.GetValueOrDefault(m) ?? [])
                    neededBy.TryAdd(mm, m);
            // records whose FormIDs name no master may be meant for any of them (the ids check reports them)
            if (p.Undecodable > 0) continue;
            foreach (var m in masters.Where(m => !used.Contains(m)))
                note(p.Rel, neededBy.TryGetValue(m, out var by)
                    ? $"lists {m} as a master without using its records; {by} needs it, so it has to stay"
                    : $"lists {m} as a master, but none of its records or links uses that file (an unused master): the game still needs it installed to load this plugin");
        }
    }

    // ---------------------------------------------------------------- scripts

    static void Scripts(TriageContext c, List<Facts> facts, List<PluginInfo> ours, LoadOrderSetup lo, Action<string, string> note, Action<string, string> look)
    {
        var table = c.Report.Table(S, "Attached scripts not found", "Plugin", "Script", "File the game looks for", "Used by");
        // the mod's loose scripts, and the scripts in its archives that load (named after one of its plugins)
        var have = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rel in c.Files)
        {
            var gp = TriageFiles.GamePath(c, rel);
            if (gp.EndsWith(".pex", StringComparison.Ordinal)) have.Add(gp);
        }
        var stems = facts.Select(p => Path.GetFileNameWithoutExtension(p.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in c.Files.Where(x => x.EndsWith(".ba2", StringComparison.Ordinal)))
        {
            var n = Path.GetFileNameWithoutExtension(rel);
            var dash = n.IndexOf(" - ", StringComparison.Ordinal);
            if (dash <= 0 || !stems.Contains(n[..dash])) continue;   // no plugin of the mod loads it (the Files section says so)
            try { foreach (var e in Ba2Names.Read(c.Full(rel))) if (e.EndsWith(".pex", StringComparison.Ordinal)) have.Add(e); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { Log.Write($"triage plugins: {rel}: {e.Message}"); }
        }
        var vanilla = VanillaIndex.Get(c.DataDir);
        bool Has(string pex) => have.Contains(pex) || vanilla.ArchiveOf(pex) != null;

        // The game's own copy of an override, to tell a script Bethesda's record names too (carried over) from the mod's
        // own. Records inside cells (placed objects, navmeshes) aren't looked up: finding one makes Mutagen index every
        // such record of the game (the memory the old ModImpactCheck ran out of).
        ILinkCache? official = null;
        bool lookups = true;
        IMajorRecordGetter? Original(IMajorRecordGetter rec)
        {
            if (!lookups) return null;
            if (rec.Registration.ClassType.Name.StartsWith("Placed", StringComparison.Ordinal) || rec is INavigationMeshGetter || rec.Registration.ClassType.Name == "Landscape") return null;
            if (GC.GetTotalMemory(false) > MemoryLimitBytes) { lookups = false; official = null; Log.Write("triage plugins: script lookups stopped (memory)"); return null; }
            official ??= lo.Plugins.Where(p => p.Official).Select(p => (IStarfieldModGetter)p.Mod).ToImmutableLinkCache<IStarfieldMod, IStarfieldModGetter>();
            return official.TryResolve(rec.FormKey, rec.Registration.GetterType, out var o) ? o : null;
        }

        foreach (var info in ours)
        {
            var p = facts.First(x => x.Name.Equals(info.FileName, StringComparison.OrdinalIgnoreCase));
            var missing = new SortedDictionary<string, (string Script, List<string> Users)>(StringComparer.Ordinal);
            int uses = 0, inherited = 0;
            var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var rec in info.Mod.EnumerateMajorRecords())
                {
                    List<string> names;
                    try { names = Assets.Scripts(rec).ToList(); }
                    catch (Exception e) when (Fatal.Recoverable(e)) { continue; }
                    foreach (var s in names)
                    {
                        uses++;
                        distinct.Add(s);
                        var pex = Assets.PexPath(s);
                        if (Has(pex)) continue;
                        if (rec.FormKey.ModKey != info.Mod.ModKey)
                        {
                            try
                            {
                                if (Original(rec) is { } o && Assets.Scripts(o).Any(x => Assets.PexPath(x) == pex)) { inherited++; continue; }
                            }
                            catch (Exception e) when (Fatal.Recoverable(e)) { Log.Write($"triage plugins: game copy of {Checker.Name(rec)} not read: {e.Message}"); }
                        }
                        if (!missing.TryGetValue(pex, out var m)) missing[pex] = m = (s, new());
                        if (m.Users.Count < 50) m.Users.Add(Checker.Name(rec));
                    }
                }
            }
            catch (Exception e) when (Fatal.Recoverable(e))
            {
                note(p.Rel, $"its records couldn't all be read, so attached scripts were checked only up to that point ({e.GetType().Name})");
            }
            foreach (var (pex, (script, users)) in missing.Take(MaxRows))
                table.Add(p.Name, script, pex, Assets.Users(users.Distinct().ToList()));
            if (missing.Count > 0)
                look(p.Rel, $"{missing.Count:N0} attached script{(missing.Count == 1 ? " is" : "s are")} in neither the mod (loose or in its archives) nor the game's archives, " +
                            $"so {(missing.Count == 1 ? "it doesn't" : "they don't")} run: " +
                            string.Join(", ", missing.Take(3).Select(m => $"{m.Value.Script} (used by {Assets.Users(m.Value.Users.Distinct().ToList())})")) +
                            (missing.Count > 3 ? $" and {missing.Count - 3:N0} more in the table" : ""));
            else if (uses > 0)
                note(p.Rel, $"its records attach {distinct.Count:N0} different script{(distinct.Count == 1 ? "" : "s")} ({uses:N0} uses); each is in the mod or the game's archives");
            if (inherited > 0)
                note(p.Rel, $"{inherited:N0} attached script use{(inherited == 1 ? " names a script" : "s name scripts")} found nowhere, but the game's own copy of the record " +
                            "names the same script, so the mod only carries it over (not listed above)");
        }
    }
}
