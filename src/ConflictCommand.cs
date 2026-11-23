// sfmodcheck conflict: reads a whole load order and reports the changes it throws away (official changes undone by
// mods, one mod's changes lost to another, ITMs, load-order problems), with a recommendation for each. Read-only:
// it writes only its report folder. Moved in from the author's older private tool on 2026-11-16 (0.5.0).
//   sfmodcheck conflict [--plugins <Plugins.txt>] [--data <Data folder>] [--extra-data <folder>] [--out <folder>]
//                       [--max-memory <GB>] [--no-assets] [--no-priorities] [--ini <file>]... [--language <code>]
//                       [--quiet] [--verbose] [--log <file>]
// Exit 0 = report written, 2 = could not run, 3 = stopped at --max-memory. Every run ends with one RESULT line.
using System.Diagnostics;
using System.Globalization;
using SfModCheck.Conflict;

namespace SfModCheck;

public static class ConflictCommand
{
    public const string Usage =
        "usage: sf-mod-check conflict [--plugins <Plugins.txt>] [--data <Data folder>] [--extra-data <folder>] [--out <folder>]\n" +
        "                           [--max-memory <GB>] [--no-assets] [--no-priorities] [--ini <file>]... [--language <code>]\n" +
        "                           [--quiet] [--verbose] [--log <file>]";

    // The game's own load order file (the game writes it; Vortex and the in-game Creations menu update it).
    static string GamePluginsTxt() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Starfield", "Plugins.txt");

    public static int Run(string[] a, string version, string byline)
    {
        try
        {
            var code = RunLogged(a, version, byline, out var resultPrinted, out var cantWhy);
            if (code == 2 && !resultPrinted) Console.WriteLine($"RESULT: CANNOT CHECK ({cantWhy})");
            return code;
        }
        catch (Exception e)
        {
            // name the plugin and record it stopped at (0.5.1), and the record Mutagen names in its exception, if any
            // Running out of memory ends here too (Fatal.cs): the plugins are closed by now, and Fatal.ResultLine frees
            // the rest before the line is built.
            Console.WriteLine(Fatal.ResultLine(e, () =>
            {
                var where = Analyzer.Where.Length > 0 ? $" while {Analyzer.Where}" : "";
                var rec = Analyzer.RecordOf(e) is { Length: > 0 } r ? $" at {r}" : "";
                return $"RESULT: CANNOT CHECK ({e.GetType().Name}{rec}{where}: {SafeText.OneLine(e.Message.Split('\n')[0])})";
            }));
            Fatal.AfterResult(() =>
            {
                Console.Error.WriteLine(Log.On ? "The log has the full error." : "For the full error, run again with --log sf-mod-check-log.txt and look at that file.");
                Log.Write($"stopped: {e}");
            });
            return 2;
        }
        finally { Log.Close(); }
    }

    /// <summary>The RESULT line's assets part (0.6.0): ", 3 missing assets (1 script, 2 models)" and any unreadable
    /// archives; nothing when none are missing (so a load order with none reads as before).</summary>
    internal static string MissingAssetsPart(Analysis a)
    {
        if (!a.AssetsChecked) return "";
        var part = a.MissingAssets.Count > 0
            ? $", {a.MissingAssets.Count} missing asset{(a.MissingAssets.Count == 1 ? "" : "s")} ({Report.Kinds(a)})"
            : "";
        if (a.UnreadableArchives.Count is var u and > 0) part += $", {u} archive{(u == 1 ? "" : "s")} that can't be read";
        return part;
    }

    /// <summary>The RESULT line's archive-limit part (0.6.0): ", WARNING 261 general archives (over 256)" per registry
    /// over the limit; nothing otherwise, so ordinary runs read as in 0.5.3.</summary>
    internal static string ArchiveLimitPart(Analysis a)
    {
        if (!a.ArchivesCounted) return "";
        var part = "";
        if (a.GnrlArchives > AssetIndex.ArchiveSlots) part += $", WARNING {a.GnrlArchives} general archives (over {AssetIndex.ArchiveSlots})";
        if (a.Dx10Archives > AssetIndex.ArchiveSlots) part += $", WARNING {a.Dx10Archives} texture archives (over {AssetIndex.ArchiveSlots})";
        // with --no-assets the assets part is empty, so archives left out of the counts are named here (code review 0.6.0)
        if (!a.AssetsChecked && a.UnreadableArchives.Count is var u and > 0) part += $", {u} archive{(u == 1 ? "" : "s")} that can't be read (not counted)";
        return part;
    }

    /// <summary>The RESULT line's errors part: the read problems (records not compared), and apart from them the
    /// assets step's own notes (it stopped, or passed --max-memory during its lookups), which don't mean records went
    /// uncompared (code review 0.6.0). Both are listed in the report's "Tool limits hit".</summary>
    internal static string ErrorsPart(Analysis a)
    {
        var read = a.Errors.Count - a.AssetNotes;
        var part = read > 0 ? $", {read} read problems (records not compared; see the report's \"Tool limits hit\")" : "";
        if (a.AssetNotes > 0) part += $", {a.AssetNotes} assets-check note{(a.AssetNotes == 1 ? "" : "s")} (see the report's \"Tool limits hit\")";
        return part;
    }

    /// <summary>The RESULT line's field-priority part (0.10.0): "2 benign entries shown apart (1 critical lost-change
    /// entry), " with priorities on; nothing with --no-priorities, so that line reads as in 0.9.1.</summary>
    internal static string BenignPart(Analysis a)
    {
        if (!a.PrioritiesOn) return "";
        var b = a.Benign.Count;
        var c = a.Lost.Count(l => l.Fields.Any(a.CriticalFields.Contains));
        return $"{b} benign entr{(b == 1 ? "y" : "ies")} shown apart" +
               (c > 0 ? $" ({c} critical lost-change entr{(c == 1 ? "y" : "ies")})" : "") + ", ";
    }

    /// <summary>Applies the built-in field priorities to the analysis and logs what moved and why.</summary>
    static void ApplyPriorities(Analysis analysis)
    {
        Analyzer.Where = "applying the field priorities";
        var table = FieldPriorityTable.Load();
        var before = analysis.Lost.Count;
        table.Apply(analysis);
        var critical = analysis.Lost.Count(l => l.Fields.Any(analysis.CriticalFields.Contains));
        Log.Write($"field priorities: on (xEdit's, data\\field-priorities.txt): {before} lost-change entries before; {analysis.Lost.Count} kept " +
                  $"({critical} with a critical field, listed first), {analysis.Benign.Count} benign entries shown apart");
        foreach (var l in analysis.Benign)
            Log.Write($"field priorities: shown apart: {l.RecordType} {SafeText.OneLine(l.EditorID)} [{l.FormKey}] {l.Winner} over {l.Loser}: " +
                      string.Join(", ", l.Fields.Select(f => $"{f.Field} ({table.NameOf(l, f)})")));
        foreach (var l in analysis.Lost.Where(l => l.Fields.Any(analysis.CriticalFields.Contains)))
            Log.Write($"field priorities: critical: {l.RecordType} {SafeText.OneLine(l.EditorID)} [{l.FormKey}] {l.Winner} over {l.Loser}: " +
                      string.Join(", ", l.Fields.Where(analysis.CriticalFields.Contains).Select(f => f.Field)) +
                      (l.Injected == true ? $" (injected: {l.FormKey.ModKey.FileName} holds no version)"
                       : l.Injected is null && FieldPriorityTable.IsInjected(l) ? $" (taken as injected: {l.FormKey.ModKey.FileName} or its version of the record couldn't be read in full)" : ""));
    }

    static readonly string[] ReportFiles ={ "report.md", "report.html", "conflicts.json" };

    /// <summary>Deletes the three report files after a failed write; returns those still there (a missing file or folder
    /// is no error). fatal: after running out of memory, any error deleting one file moves on to the next (review 0.5.3).</summary>
    static List<string> DeleteReportFiles(string outDir, bool fatal)
    {
        // Never throws a recoverable error, so the files already known to be left are never lost; out of memory is best
        // effort (the fatal caller swallows anything and goes on with the first error).
        var left = new List<string>(ReportFiles.Length);
        // Path.Combine of two non-null strings never throws on .NET (it checks only for null), so the path is built
        // outside the try (0.5.4: building it inside left a null-path branch that could never run).
        foreach (var f in ReportFiles)
        {
            var path = Path.Combine(outDir, f);
            try { File.Delete(path); }
            // no folder, so no file: not an error, like a missing file (0.5.4: was logged). Linux and macOS may give another
            // IOException for a path whose parent isn't a folder, so the folder itself is asked (review).
            catch (IOException x) when (x is DirectoryNotFoundException || !Directory.Exists(outDir)) { }
            catch (Exception x) when (fatal || Fatal.Recoverable(x))
            {
                if (!fatal)
                {
                    // the full exception (0.5.4): the type and message alone left out where it failed
                    try { Log.Write($"report cleanup: {path}: {x}"); }
                    catch (Exception z) when (Fatal.Recoverable(z)) { }
                }
                try { if (File.Exists(path)) left.Add(path); } catch (Exception y) when (fatal || Fatal.Recoverable(y)) { left.Add(path); }
            }
        }
        return left;
    }

    static int RunLogged(string[] a, string version, string byline, out bool resultPrinted, out string cantWhy)
    {
        Analyzer.MaxManagedBytes = 0; Analyzer.Progress = null; Analyzer.Where = "";   // fresh settings for every run in one process (0.5.0)
        resultPrinted = false;
        cantWhy = "bad arguments; see the message above";
        string? pluginsTxt = null, dataDir = null, extraDir = null, outDir = null, logFile = null;
        double maxGb = 0;
        bool quiet = false, verbose = false, noAssets = false, noPriorities = false;
        var inis = new List<string>();
        var language = "en";
        string? Value(ref int i, string what) => Cli.Value(a, ref i, what, Usage);
        for (int i = 0; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "--plugins":
                    if (Value(ref i, "a Plugins.txt file") is not { } p) return 2;
                    pluginsTxt = p; break;
                case "--data":
                    if (Value(ref i, "the game's Data folder") is not { } d) return 2;
                    dataDir = d; break;
                case "--extra-data":
                    if (Value(ref i, "a folder") is not { } x) return 2;
                    extraDir = x; break;
                case "--out":
                    if (Value(ref i, "a folder") is not { } o) return 2;
                    outDir = o; break;
                case "--max-memory":
                    if (Value(ref i, "a number of GB") is not { } m) return 2;
                    if (!double.TryParse(m, NumberStyles.Float, CultureInfo.InvariantCulture, out maxGb) || !double.IsFinite(maxGb) || maxGb < 0.000001 || maxGb > 1_000_000)   // under 1 KB rounds toward no limit; tiny values stop at once on purpose (tests use 0.000001)
                    {
                        Console.Error.WriteLine($"--max-memory needs a number of GB from 0.000001 to 1000000, not \"{m}\", for example: --max-memory 8");
                        return 2;
                    }
                    break;
                case "--log":
                    if (Value(ref i, "a file name") is not { } lf) return 2;
                    logFile = lf; break;
                case "--verbose": verbose = true; break;
                case "--quiet": quiet = true; break;
                case "--no-assets": noAssets = true; break;
                case "--no-priorities": noPriorities = true; break;
                case "--ini":
                    if (Value(ref i, "an INI file") is not { } ini) return 2;
                    inis.Add(ini); break;
                case "--language":
                    if (Value(ref i, "a language code such as en or de") is not { } lang) return 2;
                    if (lang.Length is < 2 or > 6 || !lang.All(char.IsAsciiLetter))   // 6: zhhans (code review 2026-11-18)
                    {
                        Console.Error.WriteLine($"--language needs a voice language code as in the archive names (en, de, fr, es, it, ja, pl, ptbr, zhhans), not \"{lang}\"");
                        return 2;
                    }
                    language = lang; break;
                case "-h": case "--help":   // help that was asked for: stdout, exit 0, no RESULT line (0.13.0)
                    Console.WriteLine(Usage);
                    return 0;
                default:
                    Console.Error.WriteLine(a[i].StartsWith("--") ? $"unknown option {a[i]}" : $"unexpected argument {a[i]} (conflict reads the whole load order; give its Plugins.txt with --plugins)");
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }
        if (!quiet) Console.Error.WriteLine(byline);
        if (verbose) Log.ToStderr();
        if (logFile != null && Log.ToFile(logFile) is { } logError) { Console.Error.WriteLine(logError); cantWhy = "the log file can't be written"; return 2; }
        Log.Write($"sf-mod-check {version} conflict on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, .NET {Environment.Version}");
        Log.Write($"arguments: {string.Join(" ", a.Select(x => x.Contains(' ') ? $"\"{x}\"" : x))}");
        Log.Write($"working folder: {Environment.CurrentDirectory}");

        // Data folder: --data, else Steam's usual folder.
        var dataWhy = dataDir != null ? "from --data" : "Steam's usual folder (no --data given)";
        dataDir ??= Cli.SteamData;
        if (!Directory.Exists(dataDir))
        {
            cantWhy = $"the Data folder {Path.GetFullPath(dataDir)} doesn't exist";
            Console.Error.WriteLine($"can't find the game's Data folder: {Path.GetFullPath(dataDir)} doesn't exist ({dataWhy}). Run again with " +
                                    "--data \"<the game's Data folder>\" (Game Pass: ...\\Starfield\\Content\\Data; a path with spaces needs quotes).");
            Log.Write($"stopped: no Data folder at {Path.GetFullPath(dataDir)} ({dataWhy})");
            return 2;
        }
        dataDir = Path.GetFullPath(dataDir);
        Log.Write($"Data folder: {dataDir}: {dataWhy}; Starfield.esm {(TriageCommand.HasStarfield(dataDir) ? "found there" : "NOT there")}");
        if (!TriageCommand.HasStarfield(dataDir))
            Console.Error.WriteLine($"note: no Starfield.esm in {dataDir}. If that isn't your game's Data folder, run again with --data \"<the game's Data folder>\".");

        // Load order: --plugins, else the game's own Plugins.txt. Without either it stops: guessing an order (every
        // plugin in Data, alphabetical) gives wrong answers and can need a lot of memory.
        string pluginsWhy;
        if (pluginsTxt != null) pluginsWhy = "from --plugins";
        else if (File.Exists(GamePluginsTxt())) { pluginsTxt = GamePluginsTxt(); pluginsWhy = "the game's own Plugins.txt (no --plugins given)"; }
        else
        {
            cantWhy = "no load order: give --plugins";
            Console.Error.WriteLine($"can't find your load order: no --plugins given and no game Plugins.txt at {GamePluginsTxt()}. Run again with " +
                                    "--plugins \"<the Plugins.txt your mod manager uses>\" (MO2 keeps one per profile, in the profile's folder).");
            Log.Write($"stopped: no --plugins and no {GamePluginsTxt()}");
            return 2;
        }
        if (!File.Exists(pluginsTxt))
        {
            cantWhy = $"the Plugins.txt {Path.GetFullPath(pluginsTxt)} doesn't exist";
            Console.Error.WriteLine($"can't read the load order: {Path.GetFullPath(pluginsTxt)} doesn't exist. Give --plugins the Plugins.txt your mod " +
                                    "manager uses (the game's is %LOCALAPPDATA%\\Starfield\\Plugins.txt; MO2 keeps one per profile).");
            Log.Write($"stopped: no Plugins.txt at {Path.GetFullPath(pluginsTxt)}");
            return 2;
        }
        pluginsTxt = Path.GetFullPath(pluginsTxt);
        Log.Write($"load order: {pluginsTxt}: {pluginsWhy}");
        if (extraDir != null)
        {
            if (!Directory.Exists(extraDir))
            {
                cantWhy = $"the --extra-data folder {Path.GetFullPath(extraDir)} doesn't exist";
                Console.Error.WriteLine($"can't use --extra-data {Path.GetFullPath(extraDir)}: that folder doesn't exist. Check the path (a path with spaces needs quotes).");
                Log.Write($"stopped: --extra-data {Path.GetFullPath(extraDir)} doesn't exist");
                return 2;
            }
            extraDir = Path.GetFullPath(extraDir);
            Log.Write($"extra plugin folder (its files win over Data's): {extraDir}");
        }
        outDir = Path.GetFullPath(outDir ?? "sf-mod-check-conflict");
        Log.Write($"report folder: {outDir}");
        if (maxGb > 0) { Analyzer.MaxManagedBytes = (long)(maxGb * 1e9); Log.Write($"memory limit: {maxGb.ToString(CultureInfo.InvariantCulture)} GB"); }

        var sw = Stopwatch.StartNew();
        Analyzer.Info = quiet ? _ => { } : Console.Error.WriteLine;
        if (Log.On) Analyzer.Progress = Log.Write;
        LoadOrderSetup lo;
        try { lo = LoadOrderSetup.Build(dataDir, pluginsTxt, extraDir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            cantWhy = $"the load order can't be read: {e.GetType().Name}";
            Console.Error.WriteLine($"can't read the load order: {e.Message} Close any program that has a plugin open (xEdit, the Creation Kit) and try again.");
            Log.Write($"stopped: reading the load order: {e}");
            return 2;
        }
        try
        {
            var mods = lo.Plugins.Count(p => !p.Official);
            Log.Write($"loaded {lo.Plugins.Count} plugins ({lo.Plugins.Count - mods} official, {mods} mods), {lo.Problems.Count} load-order problems");
            foreach (var p in lo.Problems) Log.Write($"load-order problem: {p}");   // one line already (LoadOrderSetup.Build)
            if (!quiet) Console.Error.WriteLine($"Loaded {lo.Plugins.Count} plugins ({lo.Problems.Count} load-order problems) in {sw.Elapsed.TotalSeconds:F0} s");

            Analysis analysis;
            try { analysis = Analyzer.Run(lo.Plugins); }
            catch (MemoryLimitException e)
            {
                resultPrinted = true;
                Console.WriteLine($"RESULT: STOPPED ({SafeText.OneLine(e.Message)}; no report written)");
                Console.Error.WriteLine("Try a smaller load order, or raise --max-memory if this computer has the memory.");
                Log.Write($"stopped at the memory limit: {e.Message}");
                return 3;
            }
            // Field priorities (0.10.0): here only, never in Analyzer.Run, so patch and triage are unchanged.
            if (noPriorities) Log.Write("field priorities: off (--no-priorities); every differing field counts as a lost change, as in 0.9.1");
            else ApplyPriorities(analysis);
            // the assets check (0.6.0): every active plugin's archives load; each mod plugin's scripts, models, materials,
            // textures and sounds are looked up in them and in loose files (the --extra-data folder first, then Data)
            // the archive counts per registry are made either way (--no-assets reads only each archive's header)
            var assetDirs = (extraDir != null ? new[] { extraDir, dataDir } : new[] { dataDir }).ToList();
            // A failure here (a folder this account can't list, a disk error) is noted in the report's errors and the
            // report is still written: the slow analysis is done (code review 2026-11-18).
            try
            {
                if (noAssets)
                {
                    Log.Write("assets: skipped (--no-assets); counting archives only");
                    Analyzer.Where = "counting the archives the game loads";
                    Assets.CountArchives(lo.Plugins, assetDirs, AssetIndex.DefaultInis(dataDir, inis), language, analysis);
                }
                else
                {
                    Analyzer.Where = "checking scripts, models, materials, textures and sounds (the assets check)";
                    Assets.CheckLoadOrder(lo.Plugins, assetDirs, AssetIndex.DefaultInis(dataDir, inis), language, analysis);
                    if (!quiet) Console.Error.WriteLine($"Checked scripts, models, materials, textures and sounds: {analysis.MissingAssets.Count} missing, {analysis.ArchivesRead} archives read");
                }
            }
            catch (Exception e) when (Fatal.Recoverable(e))
            {
                analysis.AssetsFailed = $"{e.GetType().Name}: {SafeText.OneLine(e.Message)}";
                analysis.Errors.Add($"the {(noAssets ? "archive count" : "assets check")} stopped while {Analyzer.Where} ({analysis.AssetsFailed}), " +
                                    "so its results are missing or incomplete");
                analysis.AssetNotes++;
                Log.Write($"assets: stopped ({analysis.AssetsFailed})");
                // a warning, like the archive-limit lines below: shown with --quiet too (code review 0.6.0)
                Console.Error.WriteLine($"WARNING: the {(noAssets ? "archive count" : "assets check")} stopped ({analysis.AssetsFailed}); the report notes it");
            }
            foreach (var w in Assets.ArchiveLimitWarnings(analysis)) Console.Error.WriteLine($"WARNING: {w}");
            Analyzer.Where = $"writing the report to {outDir}";
            Recommend.ReportDir = outDir;
            Recommend.DataDir = dataDir;
            Recommend.PluginsTxt = pluginsTxt;
            try { Report.Write(outDir, lo, analysis); }
            catch (Exception e)
            {
                // Any failure part way (out of memory, a full disk, a file another program holds open) would leave new
                // and old report files mixed: never leave a report that looks whole (code review 0.5.2; every failure
                // since 0.5.3, IO errors included). A file that can't be deleted is named, so nobody trusts it.
                // Out of memory: as little as possible (the deletes and one fixed note, best effort), so the first error
                // is the one that goes on.
                if (Fatal.IsFatal(e))
                {
                    try
                    {
                        if (DeleteReportFiles(outDir, fatal: true).Count > 0)
                            Console.Error.WriteLine("note: some report files could not be removed and may be old or incomplete");
                    }
                    catch (Exception) { }   // never replace the first error
                    throw;
                }
                // recoverable errors stay inside; running out of memory here stops the run, as everywhere (Fatal.cs)
                var left = DeleteReportFiles(outDir, fatal: false);
                if (left.Count > 0)
                {
                    Console.Error.WriteLine($"note: these report files could not be removed and may be old or incomplete: {string.Join(", ", left)}");
                    Log.Write($"report files left after the failure: {string.Join(", ", left)}");
                }
                if (e is not (IOException or UnauthorizedAccessException)) throw;
                cantWhy = $"the report can't be written to {outDir}";
                Console.Error.WriteLine($"can't write the report to {outDir}: {e.Message} Give --out a folder you can write to.");
                Log.Write($"stopped: writing the report: {e}");
                return 2;
            }

            var official = analysis.Lost.Count(l => l.LoserOfficial);
            if (!quiet) Console.WriteLine($"sf-mod-check {version} conflict: {lo.Plugins.Count} plugins ({lo.Plugins.Count - mods} official, {mods} mods), {analysis.RecordsOverriddenByMods} records overridden by mods");
            foreach (var err in analysis.Errors.Take(5)) Console.Error.WriteLine($"note: {err}");
            if (analysis.Errors.Count > 5) Console.Error.WriteLine($"note: ... and {analysis.Errors.Count - 5} more (the report's \"Tool limits hit\" section)");
            Console.WriteLine($"RESULT: OK, {analysis.Lost.Count} lost-change entries ({official} official, {analysis.Lost.Count - official} mod vs mod), " +
                              BenignPart(analysis) +
                              $"{analysis.Itms.Count} ITMs, {lo.Problems.Count} load-order problems" +
                              $"{(analysis.TypeChanges.Count is var tcn and > 0 ? $", {tcn} override{(tcn == 1 ? "" : "s")} that change{(tcn == 1 ? "s" : "")} a record's type" : "")}" +
                              MissingAssetsPart(analysis) + ArchiveLimitPart(analysis) +
                              ErrorsPart(analysis) + " " +
                              $"(report: {Path.Combine(outDir, "report.html")}; {sw.Elapsed.TotalSeconds:F0} s)");
            Fatal.AfterResult(() => Log.Write($"result: {analysis.Lost.Count} lost-change entries, {analysis.Itms.Count} ITMs, {lo.Problems.Count} load-order problems, " +
                      $"{analysis.TypeChanges.Count} type-changing overrides, " +
                      (analysis.AssetsChecked ? $"{analysis.MissingAssets.Count} missing assets, " : "assets not checked, ") +
                      $"{analysis.GnrlArchives} general and {analysis.Dx10Archives} texture archives, " +
                      $"{analysis.Errors.Count} errors, exit code 0, {sw.Elapsed.TotalSeconds:F1} s, peak working set {Process.GetCurrentProcess().PeakWorkingSet64 / 1e9:F1} GB"));   // after the RESULT line: nothing may print a second one
            return 0;
        }
        finally { foreach (var p in lo.Plugins) p.Mod.Dispose(); }
    }
}
