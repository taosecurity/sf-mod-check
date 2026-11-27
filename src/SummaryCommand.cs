// sf-mod-check summary (0.26.0): what one plugin adds and changes. Read only.
//   sf-mod-check summary <plugin or archive> [--data <Data folder>] [--masters-dir <folder>]... [--max <n>] [--json]
//                        [--quiet] [--verbose] [--log <file>]
// The input goes through --plugin's path (LoadOrderInput, A.7), so the masters are found the same way; an archive with
// several plugins gets one summary per plugin, in the archive's load order. Per plugin: its header and masters, the
// records per type from one raw walk (new, override, deleted overrides), the ITMs (Overrides.FindItms with the masters'
// link cache, as check builds it), and the top-level fields the overrides change, from the same master lookup
// (FindItms' compared callback). NOTES.md "0.26.0 (SmcLookups)", part C, has the design.
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
        "  plugin's.\n" +
        "  <plugin or archive>  a plugin (.esm, .esp or .esl) or a mod archive (.zip, .7z or .rar). An archive with several\n" +
        "               plugins gets one summary per plugin, in load order. The masters are looked up in the plugin's own\n" +
        "               folder, then each --masters-dir, then the game's Data folder.\n" +
        "  --max        examples listed per type and per field (default 5; 0 lists them all)\n" +
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
    }

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
                foreach (var s in summaries) PrintText(s, max);
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
        $"{s.Itms.Count} ITM, {Plural(s.Fields.Count, "field", "fields")} changed in {Plural(s.ChangedOverrides, "override", "overrides")}";

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

    /// <summary>The changed fields, most overrides first, then by type and field name.</summary>
    static IEnumerable<KeyValuePair<(string Type, string Field), List<FieldExample>>> FieldsOrdered(PluginSummary s) =>
        s.Fields.OrderByDescending(f => f.Value.Count).ThenBy(f => f.Key.Type, StringComparer.Ordinal).ThenBy(f => f.Key.Field, StringComparer.Ordinal);

    static IEnumerable<T> Cap<T>(IEnumerable<T> items, int max) => max == 0 ? items : items.Take(max);

    static void PrintText(PluginSummary s, int max)
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
            ["unread"] = new JsonArray(s.Unread.Select(u => (JsonNode)u).ToArray()),
        };
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
