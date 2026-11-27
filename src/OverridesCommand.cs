// sf-mod-check overrides (0.26.0): the records two or more non-official plugins hold (override, or add and another
// overrides), by record type, with the plugins named in load order and the winner marked. Read only.
//   sf-mod-check overrides [--plugins <Plugins.txt> | --mo2 <MO2 folder> [--profile <name>] | --plugin <plugin or archive> ...]
//                          [--data <Data folder>] [--extra-data <folder or archive>] [--masters-dir <folder>]
//                          [--type DIAL,INFO,...] [--min <n>] [--records <file>] [--max <n>] [--json] [--quiet] [--verbose] [--log <file>]
// Each non-official plugin is read once (RecordWalk.Each, conflict's per-group fallback). --records reads only the
// plugins that may hold the listed records (FindCommand.Copies) and shows the official copies too.
// Exit 0 = records listed, 1 = none matched, 2 = could not run. Text ends with one RESULT line; --json prints one object.
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using SfModCheck.Conflict;

namespace SfModCheck;

public static class OverridesCommand
{
    public const string Usage =
        "usage: sf-mod-check overrides [--plugins <Plugins.txt> | --mo2 <MO2 folder> [--profile <name>] | --plugin <plugin or archive>...]\n" +
        "                              [--data <Data folder>] [--extra-data <folder or archive>] [--masters-dir <folder>]...\n" +
        "                              [--type <SIG>[,<SIG>...]] [--min <n>] [--records <file>] [--max <n>]\n" +
        "                              [--json] [--quiet] [--verbose] [--log <file>]\n" +
        "  Lists the records that two or more non-official plugins hold: a mod's override of a record, or a mod's own new\n" +
        "  record that another mod overrides. Each record names its plugins in load order; the last one wins in game.\n" +
        "  Official plugins (Starfield.esm, the DLCs, Creations by Bethesda) are never counted.\n" +
        "  --plugin   a plugin (.esm, .esp or .esl) or a mod archive (.zip, .7z or .rar) read with its masters and no\n" +
        "             Plugins.txt. Give it more than once to compare mods that aren't installed: each input's masters,\n" +
        "             then the input, in the order given. When two inputs hold a plugin of the same name, the later one is\n" +
        "             used; an input whose master isn't found is left out with a note and the rest run.\n" +
        "  --type     only these record types, comma-separated four-letter signatures: DIAL,INFO\n" +
        "  --min      list a record when at least this many non-official plugins hold it (default 2; 1 with --records)\n" +
        "  --records  a text file of records to look up (\"who edits these\"): one FormKey (064F3C:Starfield.esm) or console\n" +
        "             FormID (0001295A) per line; # lines and blank lines are skipped. Only the plugins that may hold them\n" +
        "             are read, and the official copies are listed too (marked official)\n" +
        "  --max      list at most this many records (default 200; 0 lists them all)\n" +
        "Exit 0 = records listed, 1 = none matched, 2 = could not run.";

    public const int DefaultMax = 200;

    /// <summary>One plugin's copy of a record.</summary>
    internal sealed record Holder(PluginInfo Plugin, bool Adds);

    /// <summary>One record and the plugins that hold it, in load order.</summary>
    internal sealed class Entry
    {
        public required FormKey Key;
        public required string Sig;
        public string? EditorId;
        public List<Holder> Holders = new();
        public int ModHolders => Holders.Count(h => !h.Plugin.Official);
    }

    /// <summary>The result of a tally: records listed (sorted), records not found (--records), unreadable spots.</summary>
    internal sealed record Tally(List<Entry> Records, List<string> NotFound, List<string> Unread);

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
        ["tool"] = "sf-mod-check overrides", ["version"] = version, ["result"] = "cannot run", ["error"] = why,
    }.ToJsonString(Indented);

    static readonly Regex ConsoleId = new(@"^(0x)?[0-9A-Fa-f]{8}\z", RegexOptions.CultureInvariant);

    /// <summary>A --records line: a FormKey, a console FormID (as a uint), or null when it is neither.</summary>
    internal static (FormKey? Key, uint? Fid) ParseRecordLine(string line)
    {
        if (ConsoleId.IsMatch(line))
            return (null, uint.Parse(line.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? line[2..] : line, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        return FormKey.TryFactory(line, out var k) ? (k, null) : (null, null);
    }

    static int RunLogged(string[] a, string version, string byline, out bool resultPrinted, out string cantWhy)
    {
        resultPrinted = false;
        cantWhy = "bad arguments; see the message above";
        string? typeArg = null, recordsFile = null, logFile = null;
        bool quiet = false, verbose = false, json = false;
        int? min = null;
        int max = DefaultMax;
        using var input = new LoadOrderInput(allowPlugin: true, ArchiveInput.Use.PluginsOnly, "overrides", manyPlugins: true);
        for (int i = 0; i < a.Length; i++)
        {
            if (input.TryOption(a, ref i, Usage) is { } taken) { if (!taken) return 2; continue; }
            switch (a[i])
            {
                case "--max":
                    if (Cli.Value(a, ref i, "a number of records (0 = all)", Usage) is not { } mx) return 2;
                    if (!int.TryParse(mx, NumberStyles.None, CultureInfo.InvariantCulture, out max))
                    {
                        Console.Error.WriteLine($"--max needs a whole number of records, 0 or more (0 lists them all), not \"{SafeText.OneLine(mx)}\".");
                        cantWhy = "--max needs a whole number";
                        return 2;
                    }
                    break;
                case "--min":
                    if (Cli.Value(a, ref i, "a number of plugins (1 or more)", Usage) is not { } mn) return 2;
                    if (!int.TryParse(mn, NumberStyles.None, CultureInfo.InvariantCulture, out var mv) || mv < 1)
                    {
                        Console.Error.WriteLine($"--min needs a whole number of plugins, 1 or more, not \"{SafeText.OneLine(mn)}\".");
                        cantWhy = "--min needs a whole number, 1 or more";
                        return 2;
                    }
                    min = mv; break;
                case "--type":
                    if (Cli.Value(a, ref i, "record types, comma-separated (DIAL,INFO)", Usage) is not { } t) return 2;
                    typeArg = t; break;
                case "--records":
                    if (Cli.Value(a, ref i, "a text file of FormKeys or FormIDs, one per line", Usage) is not { } rf) return 2;
                    recordsFile = rf; break;
                case "--log":
                    if (Cli.Value(a, ref i, "a file name", Usage) is not { } lf) return 2;
                    logFile = lf; break;
                case "--json": json = true; break;
                case "--verbose": verbose = true; break;
                case "--quiet": quiet = true; break;
                case "-h": case "--help": Console.WriteLine(Usage); return 0;
                default:
                    Console.Error.WriteLine(a[i].StartsWith("--") ? $"unknown option {a[i]}" : $"overrides takes no record or plugin name on its own: got {SafeText.OneLine(a[i])} (give a plugin with --plugin, records with --records)");
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }
        HashSet<string>? types = null;
        if (typeArg != null)
        {
            types = new(StringComparer.Ordinal);
            foreach (var part in typeArg.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var t = part.ToUpperInvariant();
                if (!FindCommand.Signature.IsMatch(t) || !FindCommand.KnownSigs.Value.Contains(t))
                {
                    Console.Error.WriteLine($"--type: \"{SafeText.OneLine(part)}\" isn't a Starfield record type. Known types: {string.Join(", ", FindCommand.KnownSigs.Value.Order(StringComparer.Ordinal))}.");
                    cantWhy = $"unknown record type {SafeText.OneLine(t)}";
                    return 2;
                }
                types.Add(t);
            }
            if (types.Count == 0)
            {
                Console.Error.WriteLine("--type needs at least one record type, for example --type DIAL,INFO.");
                cantWhy = "--type with no record type";
                return 2;
            }
        }
        // --records: read the file before loading anything, so a bad line stops the run at once
        List<(int Line, FormKey? Key, uint? Fid, string Text)>? wanted = null;
        if (recordsFile != null)
        {
            string[] lines;
            try { lines = File.ReadAllLines(recordsFile); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Console.Error.WriteLine($"can't read --records {SafeText.OneLine(recordsFile)}: {SafeText.OneLine(e.Message)} Check the path (a path with spaces needs quotes).");
                cantWhy = "the --records file can't be read";
                return 2;
            }
            wanted = new();
            for (int n = 0; n < lines.Length; n++)
            {
                var l = lines[n].Trim();
                if (l.Length == 0 || l.StartsWith('#')) continue;
                var (k, f) = ParseRecordLine(l);
                if (k == null && f == null)
                {
                    Console.Error.WriteLine($"--records {SafeText.OneLine(Path.GetFileName(recordsFile))} line {n + 1}: \"{SafeText.OneLine(l)}\" is neither a FormKey (064F3C:Starfield.esm) nor a console FormID (0001295A).");
                    cantWhy = $"--records line {n + 1} isn't a FormKey or FormID";
                    return 2;
                }
                if (f != null && input.Plugin != null)
                {
                    Console.Error.WriteLine($"--records line {n + 1}: {SafeText.OneLine(l)} is a console FormID, which depends on the game's whole load order, and --plugin loads only its inputs and their masters. " +
                                            "Give the record's FormKey (for example 000800:MyMod.esm), or leave out --plugin and give --plugins or --mo2.");
                    cantWhy = $"a console FormID with --plugin (--records line {n + 1})";
                    return 2;
                }
                wanted.Add((n + 1, k, f, l));
            }
            if (wanted.Count == 0)
            {
                Console.Error.WriteLine($"--records {SafeText.OneLine(Path.GetFileName(recordsFile))} lists no record (only blank and # lines). Put one FormKey or console FormID on each line.");
                cantWhy = "the --records file lists no record";
                return 2;
            }
        }
        int minHolders = min ?? (wanted != null ? 1 : 2);

        if (!quiet && !json) Console.Error.WriteLine(byline);
        if (verbose) Log.ToStderr();
        if (logFile != null && Log.ToFile(logFile) is { } logError) { Console.Error.WriteLine(logError); cantWhy = "the log file can't be written"; return 2; }
        Log.Write($"sf-mod-check {version} overrides on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, .NET {Environment.Version}");
        Log.Write($"arguments: {string.Join(" ", a.Select(x => x.Contains(' ') ? $"\"{x}\"" : x))}");
        Log.Write($"working folder: {Environment.CurrentDirectory}");
        if (!input.Resolve(out var why)) { cantWhy = why; return 2; }

        var sw = Stopwatch.StartNew();
        LoadOrderSetup lo;
        try { lo = input.Build(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            cantWhy = $"the load order can't be read: {e.GetType().Name}";
            Console.Error.WriteLine($"can't read the load order: {e.Message} Check that this account can read that file or folder, close any program that has a plugin open (xEdit, the Creation Kit), and try again.");
            Log.Write($"stopped: reading the load order: {e}");
            return 2;
        }
        try
        {
            Log.Write($"loaded {lo.Plugins.Count} plugins, {lo.Problems.Count} load-order problems");
            foreach (var p in lo.Problems) Log.Write($"load-order problem: {p}");
            if (!quiet && !json) Console.Error.WriteLine($"Loaded {lo.Plugins.Count} plugins ({lo.Problems.Count} load-order problems) in {sw.Elapsed.TotalSeconds:F0} s");
            Tally tally;
            if (wanted != null)
            {
                var keys = new List<(int Line, string Text, FormKey Key)>();
                var notFound = new List<string>();
                foreach (var w in wanted)
                {
                    if (w.Key is { } k) { keys.Add((w.Line, w.Text, k)); continue; }
                    if (FindCommand.FromConsoleId(w.Fid!.Value, lo.Plugins, out var fidWhy) is { } fk) keys.Add((w.Line, w.Text, fk));
                    else notFound.Add($"{SafeText.OneLine(w.Text)} (line {w.Line}): {fidWhy}");
                }
                tally = ByRecords(keys, lo.Plugins, types, minHolders, notFound);
            }
            else tally = Count(lo.Plugins, types, minHolders);
            var unread = lo.Problems.Select(p => $"load-order problem: {p}").Concat(tally.Unread).ToList();
            foreach (var u in tally.Unread) Console.Error.WriteLine($"note: {u}");

            var shown = max == 0 ? tally.Records : tally.Records.Take(max).ToList();
            var byType = tally.Records.GroupBy(e => e.Sig).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => (Type: g.Key, Count: g.Count())).ToList();
            var source = lo.OrderSource;
            var held = $"held by {minHolders} or more non-official plugin{(minHolders == 1 ? "" : "s")}";
            resultPrinted = true;
            if (json)
                Console.WriteLine(Json(version, source, lo.Plugins.Count, types, minHolders, recordsFile, tally, shown, byType, input.LeftOut, unread));
            else
            {
                if (!quiet)
                    Console.WriteLine($"sf-mod-check {version} overrides: records {held}" +
                                      (types != null ? $", of type {string.Join(", ", types.Order(StringComparer.Ordinal))}" : "") +
                                      (recordsFile != null ? $", from the {wanted!.Count} in {SafeText.OneLine(Path.GetFileName(recordsFile))}" : "") +
                                      $", in {lo.Plugins.Count} plugins ({SafeText.OneLine(source)})");
                foreach (var g in shown.GroupBy(e => e.Sig))
                {
                    var all = tally.Records.Count(e => e.Sig == g.Key);
                    Console.WriteLine($"{g.Key} ({all} record{(all == 1 ? "" : "s")})");
                    foreach (var e in g) Console.WriteLine($"  {Line(e)}");
                }
                if (tally.Records.Count > shown.Count)
                    Console.WriteLine($"... and {tally.Records.Count - shown.Count} more (raise --max to see them; --max 0 lists them all)");
                foreach (var nf in tally.NotFound) Console.WriteLine($"not found: {nf}");
                foreach (var l in input.LeftOut) Console.WriteLine($"left out: {l.Input}: {l.Why}");
                foreach (var u in unread) Console.WriteLine($"not read: {u}");
                var nfText = tally.NotFound.Count > 0 ? $"; {tally.NotFound.Count} not found" : "";
                Console.WriteLine(tally.Records.Count == 0
                    ? $"RESULT: no record is {held}{nfText}"
                    : $"RESULT: {tally.Records.Count} record{(tally.Records.Count == 1 ? "" : "s")} {held} ({string.Join(", ", byType.Select(t => $"{t.Type} {t.Count}"))}){nfText}");
            }
            Log.Write($"result: {tally.Records.Count} records, {tally.NotFound.Count} not found, exit code {(tally.Records.Count > 0 ? 0 : 1)}, {sw.Elapsed.TotalSeconds:F1} s");
            return tally.Records.Count > 0 ? 0 : 1;
        }
        finally { foreach (var p in lo.Plugins) p.Mod.Dispose(); }
    }

    /// <summary>A record's text line: FormKey, EditorID, then each holder in load order with (adds), (official) and
    /// (wins) marks.</summary>
    internal static string Line(Entry e)
    {
        var parts = e.Holders.Select((h, i) =>
        {
            var marks = new List<string>();
            if (h.Adds) marks.Add("adds");
            if (h.Plugin.Official) marks.Add("official");
            if (i == e.Holders.Count - 1) marks.Add("wins");
            return SafeText.OneLine(h.Plugin.FileName) + (marks.Count > 0 ? $" ({string.Join(", ", marks)})" : "");
        });
        return $"{e.Key}{(e.EditorId is { Length: > 0 } eid ? $" {SafeText.OneLine(eid)}" : "")}: {string.Join(", ", parts)}";
    }

    static readonly Dictionary<Type, (string Sig, HashSet<string>? All)> sigCache = new();

    /// <summary>A record's signature (first of its type's) and every signature its type has, cached per type.</summary>
    static (string Sig, HashSet<string>? All) SigOf(IMajorRecordGetter r)
    {
        var t = r.GetType();
        lock (sigCache)
        {
            if (sigCache.TryGetValue(t, out var s)) return s;
            var all = Checker.SigsOf(t);
            return sigCache[t] = (all?.Order(StringComparer.Ordinal).FirstOrDefault() ?? Analyzer.TypeShort(t), all);
        }
    }

    static bool TypeWanted(HashSet<string>? all, string sig, HashSet<string>? types) =>
        types == null || (all != null ? all.Overlaps(types) : types.Contains(sig));

    static string? EditorIdOf(IMajorRecordGetter r)
    {
        try { return r.EditorID; }
        catch (Exception e) when (Fatal.Recoverable(e)) { return null; }
    }

    /// <summary>The tally (0.26.0): each non-official plugin read once in load order; a record is listed when at least
    /// min distinct non-official plugins hold it.</summary>
    internal static Tally Count(List<PluginInfo> plugins, HashSet<string>? types, int min)
    {
        var entries = new Dictionary<FormKey, Entry>();
        var unread = new List<string>();
        var reported = new HashSet<(string, string)>();
        foreach (var p in plugins.Where(p => !p.Official).OrderBy(p => p.Index))
        {
            Analyzer.Where = $"reading {p.FileName}";
            var seen = new HashSet<FormKey>();
            int count = 0;
            var clean = RecordWalk.Each(p, rec =>
            {
                if (!seen.Add(rec.FormKey)) return;
                var (sig, all) = SigOf(rec);
                if (!TypeWanted(all, sig, types)) return;
                count++;
                if (!entries.TryGetValue(rec.FormKey, out var e)) entries[rec.FormKey] = e = new Entry { Key = rec.FormKey, Sig = sig };
                e.Holders.Add(new(p, rec.FormKey.ModKey == p.Mod.ModKey));
                if (EditorIdOf(rec) is { Length: > 0 } eid) e.EditorId = eid;   // the latest copy's EditorID
            }, (what, err) =>
            {
                while (err is System.Reflection.TargetInvocationException { InnerException: { } inner }) err = inner;
                if (reported.Add((p.FileName.ToLowerInvariant(), what)))
                    unread.Add($"{p.FileName}: its {what} records could not be read, so they are not counted ({Impact.Summary(err)})");
            });
            Log.Write($"overrides: {p.FileName}: {count} records{(types != null ? " of the wanted types" : "")}{(clean ? "" : ", read group by group")}");
        }
        var listed = entries.Values.Where(e => e.ModHolders >= min).ToList();
        Log.Write($"overrides: {entries.Count} records in the non-official plugins, {listed.Count} held by {min} or more");
        return new(Sort(listed), new(), unread);
    }

    /// <summary>--records (0.26.0): every copy of each listed record, official ones too, from only the plugins that may
    /// hold it; listed when at least min non-official plugins hold it.</summary>
    internal static Tally ByRecords(List<(int Line, string Text, FormKey Key)> keys, List<PluginInfo> plugins, HashSet<string>? types, int min, List<string> notFound)
    {
        var unread = new List<string>();
        var matches = FindCommand.Copies(keys.Select(k => k.Key).ToHashSet(), new(), plugins, unread).ToDictionary(m => m.Key);
        var listed = new List<Entry>();
        foreach (var (line, text, key) in keys.DistinctBy(k => k.Key))
        {
            if (!matches.TryGetValue(key, out var m))
            {
                notFound.Add($"{SafeText.OneLine(text)} (line {line}): " +
                             (plugins.Any(p => p.FileName.Equals(key.ModKey.FileName, StringComparison.OrdinalIgnoreCase))
                                 ? $"no plugin holds {key}" : $"{SafeText.OneLine(key.ModKey.FileName)} isn't in this load order"));
                continue;
            }
            var (sig, all) = SigOf(m.Winner.Rec);
            if (!TypeWanted(all, sig, types)) continue;
            var e = new Entry { Key = key, Sig = sig, EditorId = EditorIdOf(m.Winner.Rec) };
            foreach (var (p, _) in m.Copies.DistinctBy(c => c.Plugin.Index)) e.Holders.Add(new(p, key.ModKey == p.Mod.ModKey));
            if (e.ModHolders >= min) listed.Add(e);
        }
        return new(Sort(listed), notFound, unread);
    }

    /// <summary>By type, then the first non-official holder's place in the load order, then FormKey.</summary>
    static List<Entry> Sort(List<Entry> list) => list
        .OrderBy(e => e.Sig, StringComparer.Ordinal)
        .ThenBy(e => e.Holders.Where(h => !h.Plugin.Official).Select(h => h.Plugin.Index).DefaultIfEmpty(int.MaxValue).First())
        .ThenBy(e => e.Key.ToString(), StringComparer.Ordinal).ToList();

    static string Json(string version, string source, int pluginCount, HashSet<string>? types, int min, string? recordsFile, Tally tally,
                       List<Entry> shown, List<(string Type, int Count)> byType, List<LoadOrderInput.LeftOutInput> leftOut, List<string> unread)
    {
        var bt = new JsonObject();
        foreach (var (t, n) in byType) bt[t] = n;
        var root = new JsonObject
        {
            ["tool"] = "sf-mod-check overrides",
            ["version"] = version,
            ["loadOrder"] = new JsonObject { ["source"] = source, ["plugins"] = pluginCount },
            ["filter"] = new JsonObject
            {
                ["types"] = types == null ? null : new JsonArray(types.Order(StringComparer.Ordinal).Select(t => (JsonNode)t).ToArray()),
                ["min"] = min,
                ["records"] = recordsFile == null ? null : Path.GetFileName(recordsFile),
            },
            ["total"] = tally.Records.Count,
            ["shown"] = shown.Count,
            ["byType"] = bt,
            ["records"] = new JsonArray(shown.Select(e => (JsonNode)new JsonObject
            {
                ["formKey"] = e.Key.ToString(),
                ["editorId"] = e.EditorId,
                ["type"] = e.Sig,
                ["origin"] = e.Key.ModKey.FileName.String,
                ["holders"] = new JsonArray(e.Holders.Select(h => (JsonNode)new JsonObject
                {
                    ["plugin"] = h.Plugin.FileName, ["adds"] = h.Adds, ["official"] = h.Plugin.Official,
                }).ToArray()),
                ["winner"] = e.Holders[^1].Plugin.FileName,
            }).ToArray()),
            ["notFound"] = new JsonArray(tally.NotFound.Select(n => (JsonNode)n).ToArray()),
            ["leftOut"] = new JsonArray(leftOut.Select(l => (JsonNode)new JsonObject { ["input"] = l.Input, ["why"] = l.Why }).ToArray()),
            ["unread"] = new JsonArray(unread.Select(u => (JsonNode)u).ToArray()),
            ["result"] = tally.Records.Count > 0 ? "found" : "none",
        };
        return root.ToJsonString(Indented);
    }
}
