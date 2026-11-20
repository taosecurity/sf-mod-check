// sfmodcheck release: the Mutagen gap check (chunk C3), a port of the author's gap-check script (with the
// ONAM part of the author's ONAM script). For every record a plugin overrides it compares the plugin's copy with the
// master's copy, field by field on the raw bytes (no Mutagen objects), and reports:
//   ERROR    a loss Mutagen 0.54.4 is known to cause (the author's notes on what Mutagen 0.54.4 loses): HAZD OPDS dropped,
//            fewer MOLM material swap entries than the master, INFO TROI dropped, XTV2 traversal flags changed, SFTR
//            fields the master leaves out but the plugin has (or an all-empty FNAM), and overridden temporary
//            references of full masters missing from the TES4 ONAM list (the game ignores those edits).
//   WARNING  any other field type the master's copy has and the plugin's copy lacks (may be on purpose); not for
//            partial forms (flag 0x4000: absent = keep the master's value), NAVI, or a worldspace's OFST/CLSZ/RNAM.
// Records either side deletes are skipped; new records can't be traced to a source and aren't checked. Read-only.
// The lines are the Python's, word for word (tools\gap_parity.py compares the two on every test and passed zip).
using System.Globalization;
using System.Text;

namespace SfModCheck;

public static class GapCheck
{
    public const string Rule = "mutagen-gap";
    /// <summary>The Python prints at most this many warnings, then "... n more warnings".</summary>
    public const int MaxWarnings = 200;

    /// <summary>A master the plugin (or one of its masters) names isn't in any of the folders searched.</summary>
    public sealed class MasterNotFoundException(string master, IReadOnlyList<string> dirs)
        : Exception($"master {master} not found in {string.Join(" ", dirs)}")
    {
        public string Master { get; } = master;
    }

    public sealed class Result
    {
        public required string Plugin { get; init; }
        public int Own, Overrides, Compared;
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();

        /// <summary>"X.esm: 1,234 own records, 5 overrides, 5 compared with their master's copy"</summary>
        public string Summary => string.Format(CultureInfo.InvariantCulture,
            "{0}: {1:N0} own records, {2:N0} overrides, {3:N0} compared with their master's copy", Plugin, Own, Overrides, Compared);

        /// <summary>The lines mutagen-gap-check.py prints (without --quiet).</summary>
        public List<string> Lines()
        {
            var l = new List<string> { Summary };
            l.AddRange(Errors.Select(e => "  ERROR   " + e));
            l.AddRange(Warnings.Take(MaxWarnings).Select(w => "  WARNING " + w));
            if (Warnings.Count > MaxWarnings) l.Add($"  ... {Warnings.Count - MaxWarnings} more warnings");
            l.Add($"  {Errors.Count} error(s), {Warnings.Count} warning(s)");
            return l;
        }
    }

    // ---------------------------------------------------------------- the release step

    public static IEnumerable<ReleaseFinding> Run(ReleaseContext c)
    {
        var dirs = c.AllPlugins.Select(p => Path.GetDirectoryName(Path.GetFullPath(p))!)
            .Concat(c.Options.MastersDirs).Append(c.DataDir)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Log.Write($"gap check: masters searched in {string.Join(" | ", dirs)}");
        var findings = new List<ReleaseFinding>();
        foreach (var plugin in c.AllPlugins)
        {
            var name = Path.GetFileName(plugin);
            Result r;
            try { r = Check(plugin, dirs); }
            catch (MasterNotFoundException e)
            {
                Log.Write($"gap check {name}: {e.Message}");
                findings.Add(new(Level.Warning, Rule, $"gap check could not run on {name}: master {e.Master} not found " +
                    "(put the master in the zip, or give its folder with --masters-dir)"));
                continue;
            }
            catch (Exception e) when (Fatal.Recoverable(e))
            {
                Log.Write($"gap check {name}: {e}");
                findings.Add(new(Level.Warning, Rule, $"gap check could not run on {name}: {e.Message}"));
                continue;
            }
            Log.Write($"gap check {r.Summary}: {r.Errors.Count} errors, {r.Warnings.Count} warnings");
            findings.AddRange(r.Errors.Select(e => new ReleaseFinding(Level.Problem, Rule, $"{name}: {e}")));
            findings.AddRange(r.Warnings.Take(MaxWarnings).Select(w => new ReleaseFinding(Level.Warning, Rule, $"{name}: {w}")));
            if (r.Warnings.Count > MaxWarnings)
                findings.Add(new(Level.Note, Rule, $"{name}: {r.Warnings.Count - MaxWarnings} more gap check warnings not shown (run with --log to see them)"));
            foreach (var w in r.Warnings.Skip(MaxWarnings)) Log.Write($"gap check {name}: WARNING {w}");
            findings.Add(new(Level.Note, Rule, r.Summary));
        }
        return findings;
    }

    /// <summary>The hidden mode tools\gap_parity.py drives, with mutagen-gap-check.py's own arguments:
    /// &lt;plugin&gt; [--data &lt;Data folder&gt;] [--masters-dir &lt;dir&gt;] [--quiet]. Exit 1 = errors, 2 = can't run.</summary>
    public static int Cli(string[] a)
    {
        if (a.Length == 0 || a[0].StartsWith('-'))
        {
            Console.Error.WriteLine("usage: sf-mod-check --gap-check <plugin> [--data <Data folder>] [--masters-dir <dir>] [--quiet]");
            return 2;
        }
        string data = SfModCheck.Cli.SteamData;
        string? mdir = null;
        bool quiet = false;
        for (int i = 1; i < a.Length; i++)
        {
            if (a[i] is "--data" or "--masters-dir")
            {
                if (i + 1 >= a.Length) { Console.Error.WriteLine($"{a[i]} needs a folder"); return 2; }
                if (a[i] == "--data") data = a[++i]; else mdir = a[++i];
            }
            else if (a[i] == "--quiet") quiet = true;
        }
        Result r;
        try { r = Check(a[0], mdir == null ? [data] : [mdir, data]); }
        catch (MasterNotFoundException e) { Console.WriteLine(e.Message); return 1; }
        catch (Exception e) when (Fatal.Recoverable(e)) { Console.Error.WriteLine($"can't check {a[0]}: {e.Message}"); return 2; }
        var lines = r.Lines();
        if (quiet && r.Errors.Count > 0) lines = lines.Where(l => !l.StartsWith("  WARNING ") && !l.StartsWith("  ... ")).ToList();
        foreach (var l in lines) Console.WriteLine(l);
        return r.Errors.Count > 0 ? 1 : 0;
    }

    // ---------------------------------------------------------------- the check

    sealed record Rec(uint FormId, string Sig, uint Flags, List<(string Type, byte[] Data)> Fields);

    sealed class Header
    {
        public uint Flags;
        public List<string> Masters = new();
        public HashSet<uint> Onam = new();
    }

    static Header ReadHeader(string path)
    {
        using var s = File.OpenRead(path);
        var head = new byte[24];
        if (s.Read(head, 0, 24) != 24 || Encoding.Latin1.GetString(head, 0, 4) != "TES4")
            throw new InvalidDataException($"{Path.GetFileName(path)} is not a plugin (no TES4 header)");
        var size = BitConverter.ToUInt32(head, 4);
        if (size > s.Length - 24) throw new InvalidDataException($"{Path.GetFileName(path)}: the TES4 header runs past the end of the file");
        var body = new byte[size];
        s.ReadExactly(body);
        var h = new Header { Flags = BitConverter.ToUInt32(head, 8) };
        foreach (var (t, d) in Subrecords.Parse(body, out _))
        {
            if (t == "MAST")
            {
                var z = Array.IndexOf(d, (byte)0);
                h.Masters.Add(Encoding.Latin1.GetString(d, 0, z < 0 ? d.Length : z));
            }
            else if (t == "ONAM")
            {
                for (int i = 0; i + 4 <= d.Length; i += 4) h.Onam.Add(BitConverter.ToUInt32(d, i));
            }
        }
        return h;
    }

    static Style StyleOf(uint flags) => (flags & Flags.Light) != 0 ? Style.Light : (flags & Flags.Medium) != 0 ? Style.Medium : Style.Full;

    /// <summary>Every record of a file (or only those whose stored FormID is in want), in file order, with its fields.
    /// Also the stored FormIDs of reference records in temporary children groups that index a master (onam-check.py).</summary>
    static List<Rec> Records(string path, ICollection<uint>? want, List<uint>? tempRefs = null, int masterCount = 0)
    {
        var list = new List<Rec>();
        using var f = new RawFile(path);
        if (f.Length < 24) throw new InvalidDataException($"{Path.GetFileName(path)} is too short to be a plugin");
        void Walk(long pos, long end, bool inTemp)
        {
            while (pos + 24 <= end)
            {
                var sig = f.Sig(pos);
                var size = f.U32(pos + 4);
                if (sig == "GRUP")
                {
                    if (size < 24) throw new InvalidDataException($"{Path.GetFileName(path)}: group at {pos} has size {size}");
                    var gtype = f.I32(pos + 12);
                    Walk(pos + 24, Math.Min(pos + size, end), gtype == 9 || (inTemp && gtype is not (6 or 8)));
                    pos += size;
                    continue;
                }
                var flags = f.U32(pos + 8);
                var fid = f.U32(pos + 12);
                if (tempRefs != null && inTemp && RefSigs.Contains(sig) && (fid >> 24) < masterCount) tempRefs.Add(fid);
                if (want == null || want.Contains(fid))
                {
                    var len = (int)Math.Min(size, end - pos - 24);
                    ReadOnlySpan<byte> body = f.Span(pos + 24, len);
                    if ((flags & Flags.Compressed) != 0)
                        body = Subrecords.Decompress(body, out var why)
                               ?? throw new InvalidDataException($"{Path.GetFileName(path)}: {sig} {fid:X8} is compressed but {why}");
                    list.Add(new Rec(fid, sig, flags, Subrecords.Parse(body, out _)));
                }
                pos += 24 + size;
            }
        }
        Walk(24 + f.U32(4), f.Length, false);
        return list;
    }

    static readonly HashSet<string> RefSigs = ["REFR", "ACHR", "PGRE", "PMIS", "PARW", "PBEA", "PFLA", "PCON", "PBAR", "PHZD"];

    /// <summary>Looks a master up in each folder in turn (case-insensitive on every system).</summary>
    static string? Find(string name, IReadOnlyList<string> dirs)
    {
        foreach (var d in dirs)
        {
            var p = Path.Combine(d, name);
            if (File.Exists(p)) return p;
            if (!OperatingSystem.IsWindows() && Directory.Exists(d))
            {
                var hit = Directory.EnumerateFiles(d).FirstOrDefault(x => string.Equals(Path.GetFileName(x), name, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit;
            }
        }
        return null;
    }

    /// <summary>The gap check on one plugin. Masters are looked up in masterDirs in order (the Python: --masters-dir,
    /// then the Data folder; give the Data folder last). Throws MasterNotFoundException when a master (of the plugin
    /// or of one of its masters) is in none of them.</summary>
    public static Result Check(string plugin, IReadOnlyList<string> masterDirs)
    {
        var flagCache = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        string MasterPath(string name) => Find(name, masterDirs) ?? throw new MasterNotFoundException(name, masterDirs);
        uint FlagOf(string name)
        {
            if (!flagCache.TryGetValue(name, out var fl)) flagCache[name] = fl = ReadHeader(MasterPath(name)).Flags;
            return fl;
        }
        IdMap MapOf(string name, uint flags, List<string> masters) =>
            new(name.ToLowerInvariant(), StyleOf(flags), masters.Select(m => (m.ToLowerInvariant(), StyleOf(FlagOf(m)))).ToList());
        static (string File, uint Local) Decode(IdMap m, uint fid)
        {
            var (file, local, _) = m.Decode(fid);
            return (file ?? m.Self, local);   // past the end of its list: the plugin itself, as the Python has it
        }

        var ph = ReadHeader(plugin);
        var pname = Path.GetFileName(plugin);
        var pmap = MapOf(pname, ph.Flags, ph.Masters);
        var result = new Result { Plugin = pname };

        // the plugin's overrides, in file order: (origin file lower, local ID) -> record
        var tempRefs = new List<uint>();
        var overrides = new Dictionary<(string, uint), Rec>();
        foreach (var r in Records(plugin, null, tempRefs, ph.Masters.Count))
        {
            var key = Decode(pmap, r.FormId);
            if (key.File == pmap.Self) { result.Own++; continue; }
            overrides[key] = r;
        }
        result.Overrides = overrides.Count;

        // The copy each override replaces is the one in the LAST master (in master order) that has the record.
        var best = new Dictionary<(string, uint), (string Master, Rec Rec)>();
        foreach (var mname in ph.Masters)
        {
            var mh = ReadHeader(MasterPath(mname));
            var mm = MapOf(mname, mh.Flags, mh.Masters);
            var mstyle = StyleOf(mh.Flags);
            var want = new Dictionary<uint, (string, uint)>();
            var lists = new[] { Style.Full, Style.Medium, Style.Light }.ToDictionary(s => s, s => mm.List(s).ToList());
            foreach (var key in overrides.Keys)
            {
                var (file, local) = key;
                if (file == mm.Self)   // the master's own record: its own style, or Mutagen's full style
                {
                    want[((uint)mm.List(Style.Full).Count << 24) | local] = key;
                    if (mstyle == Style.Medium) want[0xFD000000 | ((uint)mm.List(Style.Medium).Count << 16) | (local & 0xFFFF)] = key;
                    else if (mstyle == Style.Light) want[0xFE000000 | ((uint)mm.List(Style.Light).Count << 12) | (local & 0xFFF)] = key;
                    continue;
                }
                foreach (var s in new[] { Style.Full, Style.Medium, Style.Light })   // an override inside this master of an earlier file's record
                {
                    var i = (uint)lists[s].IndexOf(file);
                    if (i == uint.MaxValue) continue;
                    want[s switch
                    {
                        Style.Full => (i << 24) | local,
                        Style.Medium => 0xFD000000 | (i << 16) | (local & 0xFFFF),
                        _ => 0xFE000000 | (i << 12) | (local & 0xFFF),
                    }] = key;
                }
            }
            if (want.Count == 0) continue;
            foreach (var r in Records(MasterPath(mname), want.Keys))
                best[want[r.FormId]] = (mname, r);
        }

        foreach (var (key, mine) in overrides)
        {
            if (!best.TryGetValue(key, out var theirs))
            {
                result.Warnings.Add($"{mine.Sig} {mine.FormId:X8}: overridden record not found in any master (master changed?)");
                continue;
            }
            if ((mine.Flags & Flags.Deleted) != 0 || (theirs.Rec.Flags & Flags.Deleted) != 0) continue;
            result.Compared++;
            var edid = theirs.Rec.Fields.FirstOrDefault(x => x.Type == "EDID").Data ?? [];
            var z = Array.IndexOf(edid, (byte)0);
            var ed = Encoding.Latin1.GetString(edid, 0, z < 0 ? edid.Length : z);
            var where = $"{theirs.Rec.Sig} {mine.FormId:X8} ({theirs.Master} {ed})".Replace(" )", ")");
            CheckPair(theirs.Rec.Sig, mine.Fields, theirs.Rec.Fields, result.Errors, result.Warnings, where, (mine.Flags & 0x4000) != 0);
        }

        // ONAM: overridden temporary references of full masters must be listed (the Creation Kit leaves light
        // masters' refs out, so those aren't checked).
        var lacking = tempRefs.Where(fid => !ph.Onam.Contains(fid) && (FlagOf(ph.Masters[(int)(fid >> 24)]) & Flags.Light) == 0).ToList();
        if (lacking.Count > 0)
            result.Errors.Add($"ONAM: {lacking.Count} overridden temporary reference(s) of full masters not listed in ONAM " +
                              $"(e.g. {string.Join(", ", lacking.Take(5).Select(x => x.ToString("X8")))}); the game ignores those edits");
        return result;
    }

    static Dictionary<string, int> Counts(List<(string Type, byte[] Data)> fields)
    {
        var d = new Dictionary<string, int>();   // keeps first-seen order, as Python's dict does
        foreach (var (t, _) in fields) d[t] = d.GetValueOrDefault(t) + 1;
        return d;
    }

    /// <summary>Total material swap entries over all MOLM fields (each: uint16 count, then 4-byte FormIDs).</summary>
    static int MolmSwaps(List<(string Type, byte[] Data)> fields) =>
        fields.Where(f => f.Type == "MOLM" && f.Data.Length >= 2).Sum(f => (int)BitConverter.ToUInt16(f.Data, 0));

    /// <summary>Flag value of each XTV2 traversal entry: 40 bytes, int32 flags at 0x28, a 4-byte FormID unless bit 0x4
    /// is set, then 8 bytes (56 or 52 bytes per entry); trailing zero bytes end the list.</summary>
    static List<uint> Xtv2Flags(byte[] v)
    {
        var o = new List<uint>();
        int i = 0;
        while (i + 52 <= v.Length && v.AsSpan(i, 16).IndexOfAnyExcept((byte)0) >= 0)
        {
            var fl = BitConverter.ToUInt32(v, i + 40);
            o.Add(fl);
            i += (fl & 4) != 0 ? 52 : 56;
        }
        return o;
    }

    static readonly string[] SftrFields = ["CNAM", "DNAM", "FNAM", "NAM1"];

    static void CheckPair(string sig, List<(string Type, byte[] Data)> mine, List<(string Type, byte[] Data)> theirs,
                          List<string> errors, List<string> warnings, string where, bool partial)
    {
        var mc = Counts(mine);
        var tc = Counts(theirs);
        int M(string t) => mc.GetValueOrDefault(t);
        int T(string t) => tc.GetValueOrDefault(t);
        // known Mutagen losses
        if (sig == "HAZD" && T("OPDS") > 0 && M("OPDS") == 0)
            errors.Add($"{where}: OPDS dropped (Mutagen's hazard has no OPDS field)");
        // swap entries, not MOLM fields: repeated MOLM fields form one list
        int ms = MolmSwaps(mine), ts = MolmSwaps(theirs);
        if (0 < ms && ms < ts)
            errors.Add($"{where}: {ts - ms} MOLM material swap(s) dropped (master {ts}, plugin {ms})");
        bool troiLost = sig == "INFO" && T("TROI") > 0 && M("TROI") == 0;
        if (troiLost)
        {
            var knock = T("NAM9") > 0 && M("NAM9") == 0 ? " and NAM9 (text hash) after it" : "";
            errors.Add($"{where}: TROI dropped{knock} (official Mutagen 0.54.4 doesn't know TROI, so a plugin written with it loses the field; " +
                       "write the plugin with a build that keeps it)");
        }
        if (sig is "REFR" or "ACHR" or "CELL")
        {
            var tx = theirs.Where(f => f.Type == "XTV2").Select(f => f.Data).ToList();
            var mx = mine.Where(f => f.Type == "XTV2").Select(f => f.Data).ToList();
            foreach (var (a, b) in tx.Zip(mx))
            {
                var lost = Xtv2Flags(a).Zip(Xtv2Flags(b)).Where(p => p.First != p.Second).ToList();
                if (lost.Count == 0) continue;
                var (x, y) = lost[0];
                errors.Add($"{where}: XTV2 traversal flags changed in {lost.Count} entr{(lost.Count == 1 ? "y" : "ies")} " +
                           $"(e.g. 0x{x:x} -> 0x{y:x}; official Mutagen 0.54.4 keeps only bit 0x4)");
                break;
            }
        }
        if (sig == "SFTR")
        {
            var added = mc.Keys.Where(t => !tc.ContainsKey(t) && SftrFields.Contains(t)).ToList();
            if (added.Count > 0)
                errors.Add($"{where}: surface tree fields {string.Join(", ", added)} added that the master leaves out " +
                           "(Mutagen fills always-present fields)");
            else if (mine.Any(f => f.Type == "FNAM" && f.Data.AsSpan().IndexOfAnyExcept((byte)0) < 0))
                errors.Add($"{where}: surface pattern block (FNAM) is all empty links; no vanilla surface tree has " +
                           "one (Mutagen's filler for an override that left the field out)");
        }
        // everything else that went missing; not for partial forms or the navmesh index NAVI
        if (partial || sig == "NAVI") return;
        var known = new HashSet<string> { "OPDS", "MOLM", "TROI" };
        if (troiLost) known.Add("NAM9");
        // Creation Kit plugins leave a worldspace's offset/cell-size/reference tables out of their overrides too
        if (sig == "WRLD") known.UnionWith(["OFST", "CLSZ", "RNAM"]);
        var lostFields = tc.Where(kv => M(kv.Key) < kv.Value && !known.Contains(kv.Key))
            .Select(kv => kv.Key + (kv.Value - M(kv.Key) > 1 ? $" x{kv.Value - M(kv.Key)}" : ""))
            .OrderBy(s => s, StringComparer.Ordinal).ToList();
        if (lostFields.Count > 0 && mine.Count > 0)   // an empty override (deleted/stub) is reported elsewhere
            warnings.Add($"{where}: fields the master has but this plugin lacks: {string.Join(", ", lostFields)}");
    }
}
