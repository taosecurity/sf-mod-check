// sf-mod-check summary (0.26.0): what one plugin adds and changes. Read only.
//   sf-mod-check summary <plugin or archive> [--data <Data folder>] [--masters-dir <folder>]... [--max <n>] [--json]
//                        [--quiet] [--verbose] [--log <file>]
// The input goes through --plugin's path (LoadOrderInput, A.7), so the masters are found the same way; an archive with
// several plugins gets one summary per plugin, in the archive's load order. Per plugin: its header and masters, the
// records per type from one raw walk (new, override, deleted overrides), the ITMs (Overrides.FindItms with the masters'
// link cache, as check builds it), and the top-level fields the overrides change, from the same master lookup
// (FindItms' compared callback), and the cells touched and placed references per worldspace and in the interiors (from
// the same raw walk, with each base's type from a header-only walk of the master it comes from). NOTES.md "0.26.0 (SmcLookups)", part C, has the design.
// Exit 0 = summary made, 2 = could not run. Text ends with one RESULT line; --json prints one object.
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using SfModCheck.Conflict;

namespace SfModCheck;

public static class SummaryCommand
{
    public const string Usage =
        "usage: sf-mod-check summary <plugin or archive> [--data <Data folder>] [--masters-dir <folder>]... [--max <n>]\n" +
        "                            [--json] [--quiet] [--verbose] [--log <file>]\n" +
        "  Says what one plugin adds and changes: its masters, its records by type (new, override, deleted), the overrides\n" +
        "  identical to their master version (ITM), and the fields its overrides change, with the master's value and the\n" +
        "  plugin's; then the cells it touches in each worldspace and in the interiors, and its placed references there\n" +
        "  (new, override, deleted) by the record type of their base.\n" +
        "  <plugin or archive>  a plugin (.esm, .esp or .esl) or a mod archive (.zip, .7z or .rar). An archive with several\n" +
        "               plugins gets one summary per plugin, in load order. The masters are looked up in the plugin's own\n" +
        "               folder, then each --masters-dir, then the game's Data folder.\n" +
        "  --max        examples listed per type and per field (default 5), and cells listed per worldspace (default 20);\n" +
        "               0 lists them all. --json always lists every cell.\n" +
        "Exit 0 = summary made, 2 = could not run.";

    public const int DefaultMax = 5;
    /// <summary>How many characters of a field's value an example shows.</summary>
    public const int ValueWidth = 120;

    /// <summary>One record type's counts in one plugin.</summary>
    internal sealed class TypeCounts
    {
        public int New, Override, Deleted, Itm;
        public int Total => New + Override;
    }

    /// <summary>One changed field of one override: the record, and the master's and the plugin's value.</summary>
    internal sealed record FieldExample(string FormKey, string? EditorId, string From, string To);

    /// <summary>One plugin's summary.</summary>
    internal sealed class PluginSummary
    {
        public required string Name;
        public required Style Style;
        public string? FromArchive;
        public List<(string Name, Style? Style, string Where)> Masters = new();
        public int Records, Duplicates;
        public SortedDictionary<string, TypeCounts> Types = new(StringComparer.Ordinal);
        public List<(string Type, string FormKey, string? EditorId)> Itms = new();
        public int NotCompared, NoMaster;
        /// <summary>(type, field) to the overrides that change it.</summary>
        public Dictionary<(string Type, string Field), List<FieldExample>> Fields = new();
        public List<string> Unread = new();
        public int New => Types.Values.Sum(t => t.New);
        public int Override => Types.Values.Sum(t => t.Override);
        public int Deleted => Types.Values.Sum(t => t.Deleted);
        public int ChangedOverrides;
        /// <summary>C.5 and C.6: the worldspaces whose cells the plugin touches, and the interior cells.</summary>
        public List<Place> Worlds = new();
        public Place Interiors = new() { FormKey = null };
        public int CellCount => Worlds.Sum(w => w.Cells.Count) + Interiors.Cells.Count;
    }

    /// <summary>One touched cell: the plugin has its CELL record or a record under it. Records counts both.</summary>
    internal sealed class CellTouch
    {
        public required string FormKey;
        public string? EditorId;
        public bool Own;
        public int Records;
    }

    /// <summary>The placed references in a worldspace or in the interiors, counted by their base's record type.</summary>
    internal sealed class PlacedCounts
    {
        public int New, Override, Deleted;
        public Dictionary<string, int> ByBase = new(StringComparer.Ordinal);
    }

    /// <summary>A worldspace (FormKey set) or the interiors (FormKey null): its touched cells and placed references.</summary>
    internal sealed class Place
    {
        public required string? FormKey;
        public string? EditorId;
        public bool Own;
        public List<CellTouch> Cells = new();
        public PlacedCounts Placed = new();
        public int Records => Cells.Sum(c => c.Records);
    }

    /// <summary>The placed record types (the ones RawWalker allows in a cell's refs groups, less navmeshes); the base
    /// is each one's NAME field.</summary>
    internal static readonly HashSet<string> PlacedSigs = new(StringComparer.Ordinal)
        { "REFR", "ACHR", "PGRE", "PHZD", "PMIS", "PARW", "PBAR", "PBEA", "PCON", "PFLA" };
    public const string BaseNotFound = "(base not found)";
    public const string NoBase = "(no base in the record)";
    /// <summary>The cells listed per worldspace in text when --max isn't given (JSON lists them all).</summary>
    public const int DefaultCellMax = 20;

    public static int Run(string[] a, string version, string byline)
    {
        bool json = a.Contains("--json");
        try
        {
            var code = RunLogged(a, version, byline, out var resultPrinted, out var cantWhy);
            if (code == 2 && !resultPrinted) CannotRun(json, version, cantWhy);
            return code;
        }
        catch (Exception e)
        {
            Console.WriteLine(Fatal.ResultLine(e, () => json
                ? CannotRunJson(version, $"{e.GetType().Name}: {SafeText.OneLine(e.Message.Split('\n')[0])}")
                : $"RESULT: CANNOT CHECK ({e.GetType().Name}: {SafeText.OneLine(e.Message.Split('\n')[0])})"));
            Fatal.AfterResult(() =>
            {
                Console.Error.WriteLine(Log.On ? "The log has the full error." : "For the full error, run again with --log sf-mod-check-log.txt and look at that file.");
                Log.Write($"stopped: {e}");
            });
            return 2;
        }
        finally { Log.Close(); }
    }

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    static void CannotRun(bool json, string version, string why) =>
        Console.WriteLine(json ? CannotRunJson(version, why) : $"RESULT: CANNOT CHECK ({why})");

    static string CannotRunJson(string version, string why) => new JsonObject
    {
        ["tool"] = "sf-mod-check summary", ["version"] = version, ["result"] = "cannot run", ["error"] = why,
    }.ToJsonString(Indented);

    static int RunLogged(string[] a, string version, string byline, out bool resultPrinted, out string cantWhy)
    {
        resultPrinted = false;
        cantWhy = "bad arguments; see the message above";
        string? target = null, logFile = null;
        bool quiet = false, verbose = false, json = false;
        int max = DefaultMax;
        bool maxGiven = false;
        using var input = new LoadOrderInput(allowPlugin: true, ArchiveInput.Use.PluginsOnly, "summary");
        for (int i = 0; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "--data": case "--masters-dir":
                    if (input.TryOption(a, ref i, Usage) != true) return 2;
                    continue;
                case "--plugin": case "--plugins": case "--mo2": case "--profile": case "--extra-data":
                    Console.Error.WriteLine($"summary doesn't take {a[i]}: give the plugin or archive itself as the first argument " +
                                            "(sf-mod-check summary MyMod.esm), and say where its masters are with --masters-dir or --data.");
                    cantWhy = $"summary doesn't take {a[i]}";
                    return 2;
                case "--max":
                    if (Cli.Value(a, ref i, "a number of examples (0 = all)", Usage) is not { } mx) return 2;
                    if (!int.TryParse(mx, NumberStyles.None, CultureInfo.InvariantCulture, out max))
                    {
                        Console.Error.WriteLine($"--max needs a whole number of examples, 0 or more (0 lists them all), not \"{SafeText.OneLine(mx)}\".");
                        cantWhy = "--max needs a whole number";
                        return 2;
                    }
                    maxGiven = true;
                    break;
                case "--log":
                    if (Cli.Value(a, ref i, "a file name", Usage) is not { } lf) return 2;
                    logFile = lf; break;
                case "--json": json = true; break;
                case "--verbose": verbose = true; break;
                case "--quiet": quiet = true; break;
                case "-h": case "--help": Console.WriteLine(Usage); return 0;
                default:
                    if (a[i].StartsWith("--"))
                    {
                        Console.Error.WriteLine($"unknown option {a[i]}");
                        Console.Error.WriteLine(Usage);
                        return 2;
                    }
                    if (target != null)
                    {
                        Console.Error.WriteLine($"summary takes one plugin or archive: got {SafeText.OneLine(target)} and {SafeText.OneLine(a[i])}. Run it once for each.");
                        cantWhy = "more than one plugin or archive given";
                        return 2;
                    }
                    target = a[i];
                    break;
            }
        }
        if (target == null)
        {
            Console.Error.WriteLine("summary needs a plugin or a mod archive: sf-mod-check summary MyMod.esm");
            Console.Error.WriteLine(Usage);
            cantWhy = "no plugin or archive given";
            return 2;
        }
        var pluginArg = new[] { "--plugin", target };
        int at = 0;
        if (input.TryOption(pluginArg, ref at, Usage) != true) return 2;

        if (!quiet && !json) Console.Error.WriteLine(byline);
        if (verbose) Log.ToStderr();
        if (logFile != null && Log.ToFile(logFile) is { } logError) { Console.Error.WriteLine(logError); cantWhy = "the log file can't be written"; return 2; }
        Log.Write($"sf-mod-check {version} summary on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, .NET {Environment.Version}");
        Log.Write($"arguments: {string.Join(" ", a.Select(x => x.Contains(' ') ? $"\"{x}\"" : x))}");
        Log.Write($"working folder: {Environment.CurrentDirectory}");
        if (!input.Resolve(out var why)) { cantWhy = why; return 2; }

        var sw = Stopwatch.StartNew();
        LoadOrderSetup lo;
        try { lo = input.Build(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            cantWhy = $"the plugin and its masters can't be read: {e.GetType().Name}";
            Console.Error.WriteLine($"can't read the plugin and its masters: {e.Message} Check that this account can read them, close any program that has a plugin open (xEdit, the Creation Kit), and try again.");
            Log.Write($"stopped: reading the load order: {e}");
            return 2;
        }
        try
        {
            Log.Write($"loaded {lo.Plugins.Count} plugins, {lo.Problems.Count} load-order problems");
            foreach (var p in lo.Problems) Log.Write($"load-order problem: {p}");
            // the plugins to summarize: an archive's plugins in load order, else the one plugin
            var wanted = input.ArchivePlugins.Count > 0
                ? new HashSet<string>(input.ArchivePlugins, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>([Path.GetFileName(input.Plugin!)], StringComparer.OrdinalIgnoreCase);
            var targets = lo.Plugins.Where(p => wanted.Contains(p.FileName)).ToList();
            if (targets.Count == 0)
            {
                cantWhy = "the plugin can't be opened";
                Console.Error.WriteLine($"can't open {string.Join(", ", wanted.Select(SafeText.OneLine))}: {string.Join("; ", lo.Problems.Select(SafeText.OneLine))}");
                Log.Write("stopped: no wanted plugin opened");
                return 2;
            }
            var archiveName = input.PluginArchive is { } arc ? Path.GetFileName(arc) : null;
            var summaries = new List<PluginSummary>();
            foreach (var t in targets)
            {
                Analyzer.Where = $"summarizing {t.FileName}";
                if (Summarize(t, lo, archiveName, wanted, out var s, out var err) is false)
                {
                    cantWhy = err!;
                    Console.Error.WriteLine($"can't summarize {SafeText.OneLine(t.FileName)}: {err}");
                    Log.Write($"stopped: {t.FileName}: {err}");
                    return 2;
                }
                summaries.Add(s!);
                Log.Write($"summary: {t.FileName}: {s!.Records} records, {s.Itms.Count} ITM, {s.Fields.Count} changed fields, {sw.Elapsed.TotalSeconds:F1} s");
            }
            foreach (var p in lo.Problems) if (!quiet && !json) Console.Error.WriteLine($"note: load-order problem: {SafeText.OneLine(p)}");
            resultPrinted = true;
            if (json) Console.WriteLine(Json(version, summaries, max, lo.Problems));
            else
            {
                if (!quiet) Console.WriteLine($"sf-mod-check {version} summary of {SafeText.OneLine(lo.OrderSource)}");
                foreach (var s in summaries) PrintText(s, max, maxGiven ? max : DefaultCellMax);
                Console.WriteLine("RESULT: " + string.Join("; ", summaries.Select(ResultPart)));
            }
            Log.Write($"result: {summaries.Count} plugin(s) summarized, exit code 0, {sw.Elapsed.TotalSeconds:F1} s");
            return 0;
        }
        finally { foreach (var p in lo.Plugins) p.Mod.Dispose(); }
    }

    static string Plural(int n, string one, string many) => n == 1 ? $"{n} {one}" : $"{n} {many}";

    static string ResultPart(PluginSummary s) =>
        $"{SafeText.OneLine(s.Name)}: {Plural(s.Records, "record", "records")} ({s.New} new, {Plural(s.Override, "override", "overrides")}, {s.Deleted} deleted), " +
        $"{s.Itms.Count} ITM, {Plural(s.Fields.Count, "field", "fields")} changed in {Plural(s.ChangedOverrides, "override", "overrides")}" +
        (s.CellCount > 0 ? $", {Plural(s.CellCount, "cell", "cells")} touched" : "");

    static string StyleName(Style s) => s switch { Style.Light => "light", Style.Medium => "medium", _ => "full" };

    /// <summary>The master names of p and of their masters, from the load order.</summary>
    static HashSet<string> MasterClosure(PluginInfo p, LoadOrderSetup lo)
    {
        var byName = lo.Plugins.ToDictionary(x => x.FileName, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(string n)
        {
            if (!seen.Add(n)) return;
            if (byName.TryGetValue(n, out var m)) foreach (var mm in m.Masters) Visit(mm);
        }
        foreach (var m in p.Masters) Visit(m);
        return seen;
    }

    /// <summary>The top-level fields an override changes against its master version: DiffFields' field list (bookkeeping
    /// and child records left out, padding ignored), the record flags without the compressed and partial-form bits, and
    /// for a partial form only the fields it provides (an empty field there keeps the earlier value).</summary>
    internal static List<string> ChangedFields(IMajorRecordGetter ov, IMajorRecordGetter master)
    {
        var l = new List<string>();
        bool partial = FieldCompare.IsPartial(ov);
        foreach (var p in DiffFields.Compared(ov.GetType()))
        {
            try
            {
                object? vo = FieldCompare.Get(p, ov), vm = FieldCompare.Get(p, master);
                if (partial && FieldCompare.IsEmpty(vo)) continue;
                if (!DeepCompare.DiffIgnoringPadding.Eq(vo, vm, 0)) l.Add(p.Name);
            }
            catch (Exception e) when (Fatal.Recoverable(e)) { l.Add($"{p.Name} (unreadable)"); }
        }
        return l;
    }

    static string? EditorIdOf(IMajorRecordGetter r)
    {
        try { return r.EditorID; }
        catch (Exception e) when (Fatal.Recoverable(e)) { return null; }
    }

    /// <summary>One plugin's summary; false with the reason when its file can't be read as a plugin.</summary>
    internal static bool Summarize(PluginInfo t, LoadOrderSetup lo, string? archiveName, HashSet<string> fromArchive, out PluginSummary? s, out string? error)
    {
        s = null;
        var byName = lo.Plugins.ToDictionary(x => x.FileName, StringComparer.OrdinalIgnoreCase);
        RawHeader? HeaderOf(string name) => byName.TryGetValue(name, out var m) ? RawHeader.Read(m.Path) : null;
        // C.2: one raw walk of the plugin, which also counts records Mutagen can't read
        var ix = RecordIndex.Build(t.Path, HeaderOf, out error);
        if (ix == null) return false;
        s = new PluginSummary
        {
            Name = t.FileName, Style = ix.Header.Style, Records = ix.Records.Count, Duplicates = ix.Duplicates,
            FromArchive = archiveName != null && fromArchive.Contains(t.FileName) ? archiveName : null,
        };
        // C.1: the masters in MAST order, each with its style and where it was found
        foreach (var m in ix.Header.Masters)
        {
            if (!byName.TryGetValue(m, out var mp)) { s.Masters.Add((m, null, "not found")); continue; }
            var where = archiveName != null && fromArchive.Contains(m) ? $"in {archiveName}" : $"in {Path.GetDirectoryName(mp.Path)}";
            s.Masters.Add((m, ix.MasterStyles.TryGetValue(m, out var st) ? st : null, where));
        }
        foreach (var (key, rec) in ix.Records)
        {
            if (!s.Types.TryGetValue(rec.Sig, out var c)) s.Types[rec.Sig] = c = new TypeCounts();
            if (key.Own) c.New++;
            else
            {
                c.Override++;
                if ((rec.Flags & Flags.Deleted) != 0) c.Deleted++;
            }
        }
        Log.Write($"summary: {t.FileName}: raw walk: {ix.Walked} records, {ix.Duplicates} duplicates, {ix.PastEnd} past the master list");
        Places(t, ix, byName, HeaderOf, s);

        // C.3 and C.4: the overrides as Mutagen reads them, compared with their master versions
        var self = t.Mod.ModKey;
        var overrides = new List<IMajorRecordGetter>();
        var seen = new HashSet<Mutagen.Bethesda.Plugins.FormKey>();
        var reported = new HashSet<string>(StringComparer.Ordinal);
        var summary = s;
        RecordWalk.Each(t, rec =>
        {
            if (rec.FormKey.ModKey != self && seen.Add(rec.FormKey)) overrides.Add(rec);
        }, (what, err) =>
        {
            while (err is System.Reflection.TargetInvocationException { InnerException: { } inner }) err = inner;
            if (reported.Add(what)) summary.Unread.Add($"its {what} records could not be read, so their ITMs and changed fields are not counted ({SfModCheck.Conflict.Impact.Summary(err)})");
        });
        string SigOf(IMajorRecordGetter r) =>
            ix.Records.TryGetValue(DiffFields.KeyOf(r.FormKey, self), out var info) ? info.Sig
            : Checker.SigsOf(r.GetType())?.Order(StringComparer.Ordinal).FirstOrDefault() ?? Analyzer.TypeShort(r.GetType());
        bool IsDeleted(IMajorRecordGetter r) =>
            ix.Records.TryGetValue(DiffFields.KeyOf(r.FormKey, self), out var info) && (info.Flags & Flags.Deleted) != 0;

        var closure = MasterClosure(t, lo);
        var cache = lo.Plugins.Where(p => closure.Contains(p.FileName)).Select(p => (IStarfieldModGetter)p.Mod).ToImmutableLinkCache();
        int pairs = 0;
        void Compared(IMajorRecordGetter ov, IMajorRecordGetter master)
        {
            pairs++;
            if (IsDeleted(ov)) return;   // counted as deleted; its empty fields are not changes
            var changed = ChangedFields(ov, master);
            if (changed.Count == 0) return;
            summary.ChangedOverrides++;
            var sig = SigOf(ov);
            var fk = ov.FormKey.ToString();
            var eid = EditorIdOf(ov) ?? EditorIdOf(master);
            foreach (var f in changed)
            {
                if (!summary.Fields.TryGetValue((sig, f), out var list)) summary.Fields[(sig, f)] = list = new();
                var name = f.EndsWith(" (unreadable)", StringComparison.Ordinal) ? f[..^" (unreadable)".Length] : f;
                list.Add(new(fk, eid, Value(master, name), Value(ov, name)));
            }
        }
        // the value cut here, after OneLine (which would turn Show's own ellipsis into a dot)
        static string Value(IMajorRecordGetter r, string field)
        {
            var v = SafeText.OneLine(FieldCompare.Show(r, field, ValueWidth * 2));
            return v.Length > ValueWidth ? v[..(ValueWidth - 3)] + "..." : v;
        }
        try
        {
            var (itms, notCompared) = Overrides.FindItms(t.Mod, overrides, cache, Compared);
            s.NotCompared = notCompared;
            s.NoMaster = Math.Max(0, overrides.Count - pairs - notCompared);
            foreach (var r in itms)
            {
                var sig = SigOf(r);
                s.Itms.Add((sig, r.FormKey.ToString(), EditorIdOf(r)));
                if (s.Types.TryGetValue(sig, out var c)) c.Itm++;
            }
        }
        catch (Exception e) when (Fatal.Recoverable(e))
        {
            Log.Write($"summary: {t.FileName}: the ITM and field compare stopped: {e}");
            s.Unread.Add($"the ITMs and changed fields could not be worked out ({e.GetType().Name}: {SafeText.OneLine(e.Message.Split('\n')[0])})");
        }
        Log.Write($"summary: {t.FileName}: {overrides.Count} overrides read by Mutagen, {pairs} with a master version, {s.Itms.Count} ITM, {s.NotCompared} not compared");
        return true;
    }

    /// <summary>The stored FormID in a placed record's NAME field (its base), keyed as the plugin stores it, or null when
    /// the record has no NAME (a deleted reference often keeps none) or its body doesn't unpack.</summary>
    static RecKey? BaseOf(RawFile f, RecInfo rec, RecordIndex ix)
    {
        var size = f.U32(rec.Offset + 4);
        if (size > int.MaxValue || rec.Offset + 24 + size > f.Length) return null;
        ReadOnlySpan<byte> body = f.Span(rec.Offset + 24, (int)size);
        if ((rec.Flags & Flags.Compressed) != 0)
        {
            if (Subrecords.Decompress(body, out _) is not { } d) return null;
            body = d;
        }
        foreach (var (type, data) in Subrecords.Parse(body, out _))
            if (type == "NAME" && data.Length >= 4) return ix.KeyOf(BitConverter.ToUInt32(data, 0), out _);
        return null;
    }

    /// <summary>C.5 and C.6: the cells the plugin touches (its CELL record, or any record under the cell), per worldspace
    /// and in the interiors, with the record count in each; and the placed references in each, new, override and
    /// deleted, counted by their base's record type. Everything comes from the plugin's raw walk (ix), the NAME field of
    /// each placed record, and one header-only walk of each master that is the origin of a base, cell or worldspace the
    /// plugin doesn't hold itself (RecordIndex.FindOwn, which reads only the wanted records). A deleted override with no
    /// NAME is counted by its master version's base, from one walk of its origin master before the others.</summary>
    static void Places(PluginInfo t, RecordIndex ix, Dictionary<string, PluginInfo> byName, Func<string, RawHeader?> headerOf, PluginSummary s)
    {
        var sw = Stopwatch.StartNew();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in ix.Header.Masters) names.TryAdd(m, m);
        string Show(RecKey k) => $"{k.Local:X6}:{(k.Own ? t.FileName : names.TryGetValue(k.File, out var n) ? n : k.File)}";

        var cells = new Dictionary<RecKey, (RecKey? World, int Records)>();
        var placed = new List<(RecKey Key, RecInfo Info)>();
        foreach (var (key, rec) in ix.Records)
        {
            if (rec.Sig == "CELL") { cells.TryGetValue(key, out var c); cells[key] = (rec.World ?? c.World, c.Records + 1); }
            else if (rec.Cell is { } ck) { cells.TryGetValue(ck, out var c); cells[ck] = (c.World ?? rec.World, c.Records + 1); }
            if (PlacedSigs.Contains(rec.Sig)) placed.Add((key, rec));
        }
        if (cells.Count == 0 && placed.Count == 0) return;

        var bases = new List<(RecKey Ref, RecInfo Info, RecKey? Base)>(placed.Count);
        using (var f = new RawFile(t.Path))
            foreach (var (key, rec) in placed) bases.Add((key, rec, BaseOf(f, rec, ix)));

        // what to look up in the masters: origin file -> local ID -> what to read of it
        var found = new Dictionary<string, Dictionary<uint, (string Sig, string? EditorId, RecKey? Base)>>(StringComparer.OrdinalIgnoreCase);
        var wanted = new Dictionary<string, Dictionary<uint, RecordIndex.Want>>(StringComparer.OrdinalIgnoreCase);
        void Want(RecKey k, RecordIndex.Want what)
        {
            if (k.Own) return;
            if (!wanted.TryGetValue(k.File, out var d)) wanted[k.File] = d = new();
            d[k.Local] = d.GetValueOrDefault(k.Local) | what;
        }
        void LookUp()
        {
            foreach (var (file, d) in wanted)
            {
                if (!byName.TryGetValue(file, out var mp))
                {
                    Log.Write($"summary: {t.FileName}: {d.Count} records come from {file}, which isn't in the load order");
                    continue;
                }
                var fsw = Stopwatch.StartNew();
                var r = RecordIndex.FindOwn(mp.Path, headerOf, d, out var err);
                if (r != null)
                {
                    if (!found.TryGetValue(file, out var into)) found[file] = into = new();
                    foreach (var (l, x) in r) into[l] = x;
                }
                if (err != null) s.Unread.Add($"{mp.FileName} was not read to the end, so some bases, cells or worldspaces from it may show as not found ({SafeText.OneLine(err)})");
                Log.Write($"summary: {t.FileName}: looked up {d.Count} records in {mp.FileName}: {r?.Count ?? 0} found, {fsw.Elapsed.TotalSeconds:F1} s");
            }
            wanted.Clear();
        }
        // first, a deleted override usually keeps no NAME (Mutagen writes none): its base is the master version's
        foreach (var b in bases) if (b.Base == null && !b.Ref.Own) Want(b.Ref, RecordIndex.Want.Base);
        if (wanted.Count > 0)
        {
            LookUp();
            var selfName = t.FileName.ToLowerInvariant();
            for (int i = 0; i < bases.Count; i++)
                if (bases[i].Base == null && !bases[i].Ref.Own && found.TryGetValue(bases[i].Ref.File, out var d) && d.TryGetValue(bases[i].Ref.Local, out var x)
                    && x.Base is { } mb)
                    bases[i] = bases[i] with { Base = mb.File == selfName ? new RecKey("", mb.Local) : mb };
        }
        bool HasEdid(RecKey k) => ix.Records.TryGetValue(k, out var i) && i.EditorId != null;
        foreach (var b in bases) if (b.Base is { } bk) Want(bk, RecordIndex.Want.Type);
        foreach (var (ck, c) in cells)
        {
            if (!HasEdid(ck)) Want(ck, RecordIndex.Want.EditorId);
            if (c.World is { } wk && !HasEdid(wk)) Want(wk, RecordIndex.Want.EditorId);
        }
        LookUp();
        (string Sig, string? EditorId)? Lookup(RecKey k) =>
            k.Own ? (ix.Records.TryGetValue(k, out var i) ? (i.Sig, i.EditorId) : null)
            : found.TryGetValue(k.File, out var d) && d.TryGetValue(k.Local, out var x) ? (x.Sig, x.EditorId) : null;
        string? EdidOf(RecKey k) => ix.Records.TryGetValue(k, out var i) && i.EditorId != null ? i.EditorId : Lookup(k)?.EditorId;

        var worlds = new Dictionary<RecKey, Place>();
        Place PlaceOf(RecKey? w)
        {
            if (w is not { } wk) return s.Interiors;
            if (!worlds.TryGetValue(wk, out var p)) worlds[wk] = p = new Place { FormKey = Show(wk), EditorId = EdidOf(wk), Own = wk.Own };
            return p;
        }
        foreach (var (ck, c) in cells)
            PlaceOf(c.World).Cells.Add(new CellTouch { FormKey = Show(ck), EditorId = EdidOf(ck), Own = ck.Own, Records = c.Records });
        foreach (var (rk, info, bk) in bases)
        {
            var pc = PlaceOf(info.Cell is { } ck && cells.TryGetValue(ck, out var c) ? c.World : info.World).Placed;
            if (rk.Own) pc.New++;
            else
            {
                pc.Override++;
                if ((info.Flags & Flags.Deleted) != 0) pc.Deleted++;
            }
            var type = bk is not { } b ? NoBase : Lookup(b)?.Sig ?? BaseNotFound;
            pc.ByBase[type] = pc.ByBase.GetValueOrDefault(type) + 1;
        }
        s.Worlds = worlds.Values.OrderByDescending(w => w.Records).ThenBy(w => w.FormKey, StringComparer.Ordinal).ToList();
        foreach (var p in s.Worlds.Append(s.Interiors))
            p.Cells.Sort((a, b) => a.Records != b.Records ? b.Records.CompareTo(a.Records) : string.CompareOrdinal(a.FormKey, b.FormKey));
        Log.Write($"summary: {t.FileName}: {cells.Count} cells touched in {worlds.Count} worldspaces and the interiors, {placed.Count} placed references, " +
                  $"bases looked up in {found.Count} masters, {sw.Elapsed.TotalSeconds:F1} s");
    }

    static string ByBaseText(PlacedCounts p) =>
        string.Join(", ", p.ByBase.OrderByDescending(b => b.Value).ThenBy(b => b.Key, StringComparer.Ordinal).Select(b => $"{b.Key} {b.Value}"));

    static void PrintPlace(Place p, int cellMax)
    {
        var name = p.FormKey == null ? "Interiors" : $"Worldspace {p.FormKey}{(p.EditorId is { Length: > 0 } e ? " " + SafeText.OneLine(e) : "")} ({(p.Own ? "new" : "override")})";
        Console.WriteLine($"    {name}: {Plural(p.Cells.Count, "cell", "cells")}, {Plural(p.Records, "record", "records")}");
        foreach (var c in Cap(p.Cells, cellMax))
            Console.WriteLine($"      {c.FormKey}{(c.EditorId is { Length: > 0 } ce ? " " + SafeText.OneLine(ce) : "")} ({(c.Own ? "new" : "override")}): {Plural(c.Records, "record", "records")}");
        if (cellMax > 0 && p.Cells.Count > cellMax) Console.WriteLine($"      ... and {p.Cells.Count - cellMax} more cells (raise --max to see them; --max 0 lists them all)");
        var pc = p.Placed;
        if (pc.New + pc.Override > 0)
            Console.WriteLine($"      Placed references: {pc.New} new, {Plural(pc.Override, "override", "overrides")}" +
                              (pc.Deleted > 0 ? $" ({pc.Deleted} deleted)" : "") + $"; by base type: {ByBaseText(pc)}");
    }

    /// <summary>The changed fields, most overrides first, then by type and field name.</summary>
    static IEnumerable<KeyValuePair<(string Type, string Field), List<FieldExample>>> FieldsOrdered(PluginSummary s) =>
        s.Fields.OrderByDescending(f => f.Value.Count).ThenBy(f => f.Key.Type, StringComparer.Ordinal).ThenBy(f => f.Key.Field, StringComparer.Ordinal);

    static IEnumerable<T> Cap<T>(IEnumerable<T> items, int max) => max == 0 ? items : items.Take(max);

    static void PrintText(PluginSummary s, int max, int cellMax)
    {
        Console.WriteLine();
        Console.WriteLine($"{SafeText.OneLine(s.Name)} ({StyleName(s.Style)} plugin{(s.FromArchive != null ? $", from {SafeText.OneLine(s.FromArchive)}" : "")})");
        Console.WriteLine(s.Masters.Count == 0 ? "  Masters: none" : $"  Masters ({s.Masters.Count}):");
        foreach (var (name, style, where) in s.Masters)
            Console.WriteLine($"    {SafeText.OneLine(name)} ({(style is { } st ? StyleName(st) : "style unknown")}), {SafeText.OneLine(where)}");
        Console.WriteLine($"  Records: {Plural(s.Records, "record", "records")} ({s.New} new, {Plural(s.Override, "override", "overrides")}, {s.Deleted} of them deleted)" +
                          (s.Duplicates > 0 ? $"; {Plural(s.Duplicates, "record", "records")} more with the FormID of an earlier one" : ""));
        foreach (var (sig, c) in s.Types.OrderByDescending(t => t.Value.Total).ThenBy(t => t.Key, StringComparer.Ordinal))
            Console.WriteLine($"    {sig}: {c.New} new, {Plural(c.Override, "override", "overrides")}" +
                              (c.Deleted > 0 ? $" ({c.Deleted} deleted)" : "") + (c.Itm > 0 ? $", {c.Itm} ITM" : ""));
        Console.WriteLine($"  ITMs (overrides identical to their master version): {s.Itms.Count}");
        foreach (var g in s.Itms.GroupBy(i => i.Type).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"    {g.Key} ({g.Count()})");
            foreach (var i in Cap(g, max)) Console.WriteLine($"      {i.FormKey}{(i.EditorId is { Length: > 0 } e ? " " + SafeText.OneLine(e) : "")}");
            if (max > 0 && g.Count() > max) Console.WriteLine($"      ... and {g.Count() - max} more (raise --max to see them; --max 0 lists them all)");
        }
        if (s.NoMaster > 0) Console.WriteLine($"  {Plural(s.NoMaster, "override has", "overrides have")} no master version of the same type (a record the masters no longer have, or one of another type)");
        if (s.NotCompared > 0) Console.WriteLine($"  {Plural(s.NotCompared, "override", "overrides")} couldn't be compared with the master version (run with --log for the reason)");
        Console.WriteLine($"  Changed fields: {Plural(s.Fields.Count, "field", "fields")} changed in {Plural(s.ChangedOverrides, "override", "overrides")}");
        foreach (var ((type, field), list) in FieldsOrdered(s))
        {
            Console.WriteLine($"    {type} {field}: {Plural(list.Count, "override", "overrides")}");
            foreach (var x in Cap(list, max))
                Console.WriteLine($"      {x.FormKey}{(x.EditorId is { Length: > 0 } e ? " " + SafeText.OneLine(e) : "")}: {x.From} -> {x.To}");
            if (max > 0 && list.Count > max) Console.WriteLine($"      ... and {list.Count - max} more (raise --max to see them; --max 0 lists them all)");
        }
        var interior = s.Interiors.Cells.Count > 0 || s.Interiors.Placed.New + s.Interiors.Placed.Override > 0;
        Console.WriteLine(s.CellCount == 0 && !interior && s.Worlds.Count == 0 ? "  Cells touched: none"
            : $"  Cells touched: {Plural(s.CellCount, "cell", "cells")} ({Plural(s.Worlds.Count, "worldspace", "worldspaces")}, {s.Interiors.Cells.Count} interior)");
        foreach (var w in s.Worlds) PrintPlace(w, cellMax);
        if (interior) PrintPlace(s.Interiors, cellMax);
        foreach (var u in s.Unread) Console.WriteLine($"  not read: {SafeText.OneLine(u)}");
    }

    static string Json(string version, List<PluginSummary> summaries, int max, IReadOnlyList<string> problems)
    {
        JsonNode Plugin(PluginSummary s) => new JsonObject
        {
            ["name"] = s.Name,
            ["style"] = StyleName(s.Style),
            ["fromArchive"] = s.FromArchive,
            ["masters"] = new JsonArray(s.Masters.Select(m => (JsonNode)new JsonObject
            {
                ["name"] = m.Name, ["style"] = m.Style is { } st ? StyleName(st) : null, ["found"] = m.Where != "not found",
            }).ToArray()),
            ["records"] = s.Records,
            ["types"] = new JsonArray(s.Types.OrderByDescending(t => t.Value.Total).ThenBy(t => t.Key, StringComparer.Ordinal).Select(t => (JsonNode)new JsonObject
            {
                ["type"] = t.Key, ["new"] = t.Value.New, ["override"] = t.Value.Override, ["deleted"] = t.Value.Deleted, ["itm"] = t.Value.Itm,
            }).ToArray()),
            ["itms"] = new JsonObject
            {
                ["count"] = s.Itms.Count,
                ["notCompared"] = s.NotCompared,
                ["noMaster"] = s.NoMaster,
                ["examples"] = new JsonArray(s.Itms.GroupBy(i => i.Type).OrderBy(g => g.Key, StringComparer.Ordinal).SelectMany(g => Cap(g, max))
                    .Select(i => (JsonNode)new JsonObject { ["formKey"] = i.FormKey, ["editorId"] = i.EditorId, ["type"] = i.Type }).ToArray()),
            },
            ["changedOverrides"] = s.ChangedOverrides,
            ["fields"] = new JsonArray(FieldsOrdered(s).Select(f => (JsonNode)new JsonObject
            {
                ["type"] = f.Key.Type, ["field"] = f.Key.Field, ["count"] = f.Value.Count,
                ["examples"] = new JsonArray(Cap(f.Value, max).Select(x => (JsonNode)new JsonObject
                {
                    ["formKey"] = x.FormKey, ["editorId"] = x.EditorId, ["from"] = x.From, ["to"] = x.To,
                }).ToArray()),
            }).ToArray()),
            ["worldspaces"] = new JsonArray(s.Worlds.Select(w => (JsonNode)new JsonObject
            {
                ["formKey"] = w.FormKey, ["editorId"] = w.EditorId, ["own"] = w.Own, ["cells"] = Cells(w), ["placed"] = Placed(w.Placed),
            }).ToArray()),
            ["interiors"] = new JsonObject { ["cells"] = Cells(s.Interiors), ["placed"] = Placed(s.Interiors.Placed) },
            ["unread"] = new JsonArray(s.Unread.Select(u => (JsonNode)u).ToArray()),
        };
        static JsonArray Cells(Place p) => new(p.Cells.Select(c => (JsonNode)new JsonObject
        {
            ["formKey"] = c.FormKey, ["editorId"] = c.EditorId, ["own"] = c.Own, ["records"] = c.Records,
        }).ToArray());
        static JsonObject Placed(PlacedCounts p)
        {
            var byBase = new JsonObject();
            foreach (var (k, v) in p.ByBase.OrderByDescending(b => b.Value).ThenBy(b => b.Key, StringComparer.Ordinal)) byBase[k] = v;
            return new JsonObject { ["new"] = p.New, ["override"] = p.Override, ["deleted"] = p.Deleted, ["byBase"] = byBase };
        }
        var root = new JsonObject
        {
            ["tool"] = "sf-mod-check summary",
            ["version"] = version,
            ["plugins"] = new JsonArray(summaries.Select(Plugin).ToArray()),
            ["loadOrderProblems"] = new JsonArray(problems.Select(p => (JsonNode)p).ToArray()),
            ["result"] = "done",
        };
        return root.ToJsonString(Indented);
    }
}
