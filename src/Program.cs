// sfmodcheck: read-only checks of Starfield mods. Command "check" covers the part of xEdit's "Check for Errors" our
// build gates use: it prints ERROR / WARNING lines, a HINT line per check that found something, and one RESULT line.
// Exit 0 = no errors, 1 = errors, 2 = could not run.
//   sfmodcheck check <plugin> [--data <Data folder>] [--masters-dir <folder>]... [--max <lines per check>] [--quiet] [--no-hints]
//                    [--no-assets] [--ini <file>]... [--language <code>]
//                             [--verbose] [--log <file>]        (troubleshooting log: Log.cs)
//   sfmodcheck conflict [--plugins <Plugins.txt>] [--data D] [--extra-data F] [--out F] [--max-memory GB] [--quiet]
//                       [--verbose] [--log <file>]        (whole load order: ConflictCommand.cs; exit 0, 2 or 3)
//   sfmodcheck <plugin> [...]                  (same as check: older callers, and a plugin dropped on the exe)
//   sfmodcheck --version
// Hidden maintenance modes (run on the official masters after a game update):
//   sfmodcheck --census <plugin>...      (raw layout counts; used to set the structure rules)
//   sfmodcheck --learn-link-types <out> [Data]     (record types each Mutagen link type points to)
//   sfmodcheck --learn-conditions <out> [Data]     (condition form parameters in the official masters)
//   sfmodcheck --learn-scene-quirks <out> [Data]   (official scenes the scenes rule must skip)
// Until 2026-11-09 (0.3.0) this program was called StarfieldPluginCheck.
using System.Diagnostics;
using SfModCheck;

// The version comes from the csproj's <Version>, the only place it is set (0.5.4: Program.cs kept its own constant
// until then, and 0.5.3's release candidate said 0.5.2).
var Version = ProgramVersion.Current;
// Attribution (the author's request, 2026-11-11): stderr at the start of a check (not with --quiet), and --version.
var Byline = "sfmodcheck " + Version + " by Richard Bejtlich - GPL-3.0 - https://github.com/taosecurity/sfmodcheck";
const string DefaultData = Cli.SteamData;
const string Usage =
    "usage: sfmodcheck check <plugin> [--data <Data folder>] [--masters-dir <folder>]... [--max <lines per check>] [--quiet] [--no-hints]\n" +
    "                                 [--no-assets] [--ini <file>]... [--language <code>] [--verbose] [--log <file>]\n" +
    "       sfmodcheck conflict [--plugins <Plugins.txt>] [--data <Data folder>] [--extra-data <folder>] [--out <folder>]\n" +
    "                           [--max-memory <GB>] [--quiet] [--verbose] [--log <file>]\n" +
    "       sfmodcheck release <zip>... [--profile basic|house] [--dmmdeps <exe>] [...]   (sfmodcheck release --help)\n" +
    "       sfmodcheck --version";
// A plugin past this many records (the header's count) gets the memory note on stderr. Mods stay far below it; the
// official Starfield.esm has about 3.8 million and needs over 30 GB of memory to check.
const int LargeRecordCount = 500_000;

SafeConsoleWriter.Install();   // plugin text never reaches the terminal raw (0.4.1)
var code = Dispatch(args);
if (Interactive.OwnsConsole())
{
    // Started by dropping a plugin on the exe (or by double-clicking it): keep the window open so the result can be read.
    Console.Error.WriteLine();
    Console.Error.Write("Press Enter to close this window.");
    Console.ReadLine();
}
return code;

int Dispatch(string[] args)
{
    // SFMODCHECK_CACHE_DIR moves both caches, for every mode (the tests use it; so can a portable install).
    if (Environment.GetEnvironmentVariable("SFMODCHECK_CACHE_DIR") is { Length: > 0 } cacheDir)
    {
        try { LearnedCache.Root = Path.GetFullPath(cacheDir); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Console.Error.WriteLine($"SFMODCHECK_CACHE_DIR is not a usable folder path (\"{cacheDir}\": {e.Message}). Fix or clear that environment variable.");
            Console.WriteLine("RESULT: CANNOT CHECK (SFMODCHECK_CACHE_DIR is not a usable folder path)");
            return 2;
        }
    }
    if (args.Length == 1 && args[0] == "--version")
    {
        Console.WriteLine(Byline);
        Console.WriteLine("Reads plugins with Mutagen (https://github.com/Mutagen-Modding/Mutagen), by Noggog and the Mutagen contributors.");
        Console.WriteLine("Free software: you can share and change it under the GNU GPL version 3. It comes with no warranty.");
        return 0;
    }

    if (args.Length >= 2 && args[0] == "--learn-scene-quirks")
    {
        Rules.LearnSceneQuirks(args.Length > 2 ? args[2] : DefaultData, args[1]);
        return 0;
    }

    if (args.Length >= 2 && args[0] == "--learn-conditions")
    {
        Rules.LearnConditions(args.Length > 2 ? args[2] : DefaultData, args[1]);
        return 0;
    }

    // Hidden: the gap check alone, with mutagen-gap-check.py's own arguments (tools\gap_parity.py compares the two).
    if (args.Length > 0 && args[0] == "--gap-check") return GapCheck.Cli(args.Skip(1).ToArray());

    if (args.Length >= 2 && args[0] == "--census")
    {
        foreach (var path in args.Skip(1))
        {
            var sw = Stopwatch.StartNew();
            using var f = new RawFile(path);
            int problems = 0;
            var w = new RawWalker(f, deep: true, (sev, off, text) => { if (problems++ < 20) Console.WriteLine($"  {sev} @{off:X}: {text}"); });
            w.Walk();
            Console.WriteLine($"{Path.GetFileName(path)}: {w.Records} records, {w.Groups} groups, {problems} problems, {sw.Elapsed.TotalSeconds:F1} s");
            foreach (var (k, v) in w.Census.OrderBy(k => k.Key))
                if (!k.Contains("in type 0") || k.StartsWith("group")) Console.WriteLine($"    {k}: {v}");
        }
        return 0;
    }

    if (args.Length >= 2 && args[0] == "--learn-link-types")
    {
        Checker.LearnLinkTypes(args.Length > 2 ? args[2] : DefaultData, args[1]);
        return 0;
    }

    // Commands: "check" (one plugin), "conflict" (a whole load order, ConflictCommand.cs, since 0.5.0) and "release"
    // (release zips, ReleaseCommand.cs, since 0.7.0). A first argument that isn't a command is read as the plugin, so
    // callers written for StarfieldPluginCheck keep working.
    if (args.Length > 0 && args[0] == "conflict") return ConflictCommand.Run(args.Skip(1).ToArray(), Version, Byline);
    if (args.Length > 0 && args[0] == "release") return ReleaseCommand.Run(args.Skip(1).ToArray(), Version, Byline);
    var rest = args.Length > 0 && args[0] == "check" ? args.Skip(1).ToArray() : args;
    return Check(rest);
}

int Check(string[] a)
{
    try
    {
        var code = CheckLogged(a, out var resultPrinted, out var cantWhy);
        if (code == 2 && !resultPrinted) Console.WriteLine($"RESULT: CANNOT CHECK ({cantWhy})");
        return code;
    }
    catch (Exception e)
    {
        // Anything not caught on the way (a locked or damaged file, a bad path, running out of memory): still one RESULT
        // line and exit 2 (Fatal.cs).
        Console.WriteLine(Fatal.ResultLine(e, () => $"RESULT: CANNOT CHECK ({e.GetType().Name}: {e.Message})"));
        Fatal.AfterResult(() =>
        {
            Console.Error.WriteLine(Log.On ? "The log has the full error." : "For the full error, run again with --log sfmodcheck-log.txt and look at that file.");
            Log.Write($"stopped: {e}");
        });
        return 2;
    }
    finally { Log.Close(); }
}

int CheckLogged(string[] a, out bool resultPrinted, out string cantWhy)
{
    resultPrinted = false;
    cantWhy = "bad arguments; see the message above";
    string? plugin = null;
    var o = new Options { Plugin = "" };
    bool quiet = false, hints = true, dataGiven = false, verbose = false;
    string? logFile = null;
    // An option's value; null (after an error naming the option and the fix) when it is missing.
    string? Value(ref int i, string what) => Cli.Value(a, ref i, what, Usage);
    for (int i = 0; i < a.Length; i++)
    {
        switch (a[i])
        {
            case "--data":
                if (Value(ref i, "the game's Data folder") is not { } d) return 2;
                o.DataDir = d; dataGiven = true; break;
            case "--masters-dir":
                if (Value(ref i, "a folder") is not { } m) return 2;
                o.MastersDirs.Add(m); break;
            case "--max":
                if (Value(ref i, "a number") is not { } n) return 2;
                if (!int.TryParse(n, out var max) || max < 0)
                {
                    Console.Error.WriteLine($"--max needs a whole number of lines (0 or more), not \"{n}\", for example: --max 20");
                    return 2;
                }
                o.MaxPerCheck = max; break;
            case "--log":
                if (Value(ref i, "a file name") is not { } lf) return 2;
                logFile = lf; break;
            case "--verbose": verbose = true; break;
            case "--quiet": quiet = true; break;
            case "--no-hints": hints = false; break;
            case "--no-assets": o.NoAssets = true; break;
            case "--ini":
                if (Value(ref i, "an INI file") is not { } ini) return 2;
                o.Inis.Add(ini); break;
            case "--language":
                if (Value(ref i, "a language code such as en or de") is not { } lang) return 2;
                if (lang.Length is < 2 or > 6 || !lang.All(char.IsAsciiLetter))   // 6: zhhans (code review 2026-11-18)
                {
                    Console.Error.WriteLine($"--language needs a voice language code as in the archive names (en, de, fr, es, it, ja, pl, ptbr, zhhans), not \"{lang}\"");
                    return 2;
                }
                o.Language = lang; break;
            case "-h": case "--help": plugin = null; i = a.Length; break;
            default:
                if (a[i].StartsWith("--"))
                {
                    Console.Error.WriteLine($"unknown option {a[i]}");
                    Console.Error.WriteLine(Usage);
                    return 2;
                }
                if (a[i].Trim().Length == 0)
                {
                    Console.Error.WriteLine("the plugin name is empty: give the path of the .esm to check, for example: " +
                                            "sfmodcheck check \"C:\\Program Files (x86)\\Steam\\steamapps\\common\\Starfield\\Data\\MyMod.esm\"");
                    return 2;
                }
                if (plugin != null)
                {
                    Console.Error.WriteLine($"only one plugin per run: got {plugin} and {a[i]}. Run sfmodcheck once for each plugin " +
                                            "(a path with spaces needs quotes around it).");
                    return 2;
                }
                plugin = a[i];
                break;
        }
    }
    if (plugin == null)
    {
        Console.Error.WriteLine(Usage);
        if (a.Length == 0) Console.Error.WriteLine("To check a plugin, drag its .esm file onto sfmodcheck.exe, or run: sfmodcheck check MyMod.esm");
        return 2;
    }
    if (!quiet) Console.Error.WriteLine(Byline);
    if (verbose) Log.ToStderr();
    if (logFile != null && Log.ToFile(logFile) is { } logError) { Console.Error.WriteLine(logError); cantWhy = "the log file can't be written"; return 2; }
    Log.Write($"sfmodcheck {Version} on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, .NET {Environment.Version}");
    Log.Write($"arguments: {string.Join(" ", a.Select(x => x.Contains(' ') ? $"\"{x}\"" : x))}");
    Log.Write($"working folder: {Environment.CurrentDirectory}");

    if (!File.Exists(plugin))
    {
        var full = Path.GetFullPath(plugin);
        var why = Directory.Exists(full) ? "that is a folder, not a plugin file" : "no such file";
        cantWhy = $"{Path.GetFileName(full)}: {why}";
        Console.Error.WriteLine($"can't check {full}: {why}. Check the path and the file name (a path with spaces needs quotes around it).");
        Log.Write($"stopped: plugin {full}: {why}");
        return 2;
    }
    o.Plugin = plugin;
    Log.Write($"plugin: {Log.Describe(plugin)}");

    // No --data and no game in Steam's usual place: a plugin sitting in a game Data folder (Game Pass and other
    // installs) uses that folder.
    string dataNote = "";
    string dataWhy = dataGiven ? "from --data" : "Steam's usual folder (no --data given)";
    if (!dataGiven && !File.Exists(Path.Combine(o.DataDir, "Starfield.esm")))
    {
        var own = Path.GetDirectoryName(Path.GetFullPath(plugin))!;
        if (File.Exists(Path.Combine(own, "Starfield.esm"))) { o.DataDir = own; dataNote = $"Data folder: {own} (the plugin's own folder)"; dataWhy = "the plugin's own folder (it holds Starfield.esm; Steam's usual folder doesn't)"; }
    }
    foreach (var md in o.MastersDirs)
        if (!Directory.Exists(md))
        {
            Console.Error.WriteLine($"can't use --masters-dir {Path.GetFullPath(md)}: that folder doesn't exist. Check the path " +
                                    "(a path with spaces needs quotes around it).");
            Log.Write($"stopped: --masters-dir {Path.GetFullPath(md)} doesn't exist");
            cantWhy = $"the --masters-dir folder {Path.GetFullPath(md)} doesn't exist";
            return 2;
        }
    if (!dataGiven && !Directory.Exists(o.DataDir))
    {
        Console.WriteLine($"RESULT: CANNOT CHECK {Path.GetFileName(plugin)} (no Data folder: Steam's usual folder {o.DataDir} doesn't exist; give --data)");
        Console.Error.WriteLine($"can't find the game's Data folder: Steam's usual folder {o.DataDir} doesn't exist and the plugin's own folder " +
                                "has no Starfield.esm. Run again with --data \"<the game's Data folder>\" (Game Pass: ...\\Starfield\\Content\\Data).");
        Log.Write($"stopped: no Data folder (Steam's usual folder missing, no --data)");
        resultPrinted = true;
        return 2;
    }
    if (dataGiven && !Directory.Exists(o.DataDir))
    {
        resultPrinted = true;
        Console.WriteLine($"RESULT: CANNOT CHECK {Path.GetFileName(plugin)} (the Data folder {Path.GetFullPath(o.DataDir)} doesn't exist)");
        Console.Error.WriteLine($"can't use the Data folder {Path.GetFullPath(o.DataDir)}: it doesn't exist. Give --data the game's Data folder, " +
                                "the one that holds Starfield.esm (a path with spaces needs quotes around it).");
        Log.Write($"stopped: Data folder {Path.GetFullPath(o.DataDir)} doesn't exist");
        return 2;
    }
    var hasStarfield = File.Exists(Path.Combine(o.DataDir, "Starfield.esm"));
    Log.Write($"Data folder: {Path.GetFullPath(o.DataDir)}: {dataWhy}; Starfield.esm {(hasStarfield ? "found there" : "NOT there")}");
    var masterDirs = Checker.MasterDirs(o);
    Log.Write($"masters are looked for in: {string.Join(", ", masterDirs.Select(Path.GetFullPath))} ({(o.MastersDirs.Count > 0 ? "--masters-dir" : "the plugin's own folder")}), then the Data folder");
    Log.Write($"cache folder: {LearnedCache.Root}{(Environment.GetEnvironmentVariable("SFMODCHECK_CACHE_DIR") is { Length: > 0 } ? " (from SFMODCHECK_CACHE_DIR)" : "")}");
    RawHeader? header;
    try { header = RawHeader.Read(plugin); }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
    {
        Console.Error.WriteLine($"can't read {Path.GetFullPath(plugin)}: {e.Message} Close any program that has it open (xEdit, the Creation Kit) and try again.");
        Log.Write($"stopped: can't read the plugin header: {e}");
        cantWhy = $"{Path.GetFileName(plugin)} can't be read: {e.GetType().Name}";
        return 2;
    }
    if (!hasStarfield && header?.Masters.Any(m => m.Equals("Starfield.esm", StringComparison.OrdinalIgnoreCase)) == true)
        Console.Error.WriteLine($"note: no Starfield.esm in {Path.GetFullPath(o.DataDir)}. If your game is installed somewhere else " +
                                "(Game Pass, another Steam library), run again with --data \"<the game's Data folder>\".");

    if (Checker.IsOfficial(Path.GetFileName(plugin)) || header?.NumRecords > LargeRecordCount)
        Console.Error.WriteLine($"note: {Path.GetFileName(plugin)} is very large; checking it can take 30 GB of memory or more. " +
                                "sfmodcheck is meant for mods, not Bethesda's own masters.");

    Result r;
    try { r = Checker.Run(o); }
    catch (Exception e)
    {
        // Read errors are skipped and listed inside the check; what reaches here stopped it, running out of memory
        // included (Fatal.cs): never a result that looks whole.
        resultPrinted = true;
        Console.WriteLine(Fatal.ResultLine(e, () => $"RESULT: CANNOT CHECK {Path.GetFileName(plugin)} ({e.GetType().Name}: {e.Message})"));
        Fatal.AfterResult(() =>
        {
            Console.Error.WriteLine(Log.On ? "The log has the full error." : "For the full error, run again with --log sfmodcheck-log.txt and look at that file.");
            Log.Write($"stopped: {e}");
        });
        return 2;
    }

    if (!quiet) Console.WriteLine($"sfmodcheck {Version} check: {Path.GetFileName(plugin)} ({r.Summary})");
    if (dataNote != "") Console.WriteLine(dataNote);
    var groups = r.Findings.GroupBy(f => (f.Severity, f.Check)).OrderBy(g => g.Key.Severity == "ERROR" ? 0 : 1).ToList();
    for (int gi = 0; gi < groups.Count; gi++)
    {
        var g = groups[gi];
        foreach (var f in g.Take(o.MaxPerCheck)) Console.WriteLine($"{f.Severity} [{f.Check}] {f.Text}");
        if (g.Count() > o.MaxPerCheck) Console.WriteLine($"{g.Key.Severity} [{g.Key.Check}] ... and {g.Count() - o.MaxPerCheck} more");
        // The hint comes once, after the check's last group (a check can have an ERROR group and a WARNING group).
        if (hints && !groups.Skip(gi + 1).Any(x => x.Key.Check == g.Key.Check) && Hints.For(g.Key.Check) is { } hint)
            Console.WriteLine($"HINT [{g.Key.Check}] {hint}");
    }
    var note = r.CannotCheckLinks ? "; links not checked" : "";
    if (r.ScenesNotChecked > 0) note += $"; {r.ScenesNotChecked} official scene{(r.ScenesNotChecked == 1 ? "" : "s")} not checked ({r.ScenesNotCheckedWhy})";
    var verdict = r.Errors > 0 ? "FAILED" : "OK";
    Console.WriteLine($"RESULT: {verdict}, {r.Errors} errors, {r.Warnings} warnings{note} ({Path.GetFileName(plugin)}: {r.Records} records, {r.Links} links; {r.Elapsed.TotalSeconds:F1} s)");
    if (Log.On)
    {
        foreach (var g in r.Findings.GroupBy(f => (f.Severity, f.Check)).OrderBy(g => g.Key.Check))
            Log.Write($"findings: {g.Count()} {g.Key.Severity} [{g.Key.Check}]");
        if (r.CannotCheckLinks) Log.Write("links not checked: a master is missing or couldn't be opened (see the [header] or [read] ERROR)");
        Log.Write($"result: {verdict}, {r.Errors} errors, {r.Warnings} warnings, exit code {(r.Errors > 0 ? 1 : 0)}, {r.Elapsed.TotalSeconds:F1} s");
    }
    return r.Errors > 0 ? 1 : 0;
}

namespace SfModCheck
{
    /// <summary>The program's version, read from the assembly (0.5.4): the build writes the csproj's &lt;Version&gt; into
    /// AssemblyInformationalVersion, and the .NET SDK's SourceLink adds "+&lt;commit&gt;", which is cut off.</summary>
    public static class ProgramVersion
    {
        public static string Current { get; } = Of(typeof(ProgramVersion).Assembly);

        public static string Of(System.Reflection.Assembly a) =>
            Clean(System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(a)?.InformationalVersion
                  ?? (a.GetName().Version is { } v ? (v.Build >= 0 ? v.ToString(3) : v.ToString()) : "unknown"));   // ToString(3) throws on a two-part version (review)

        /// <summary>"0.5.3+5a98c1a..." becomes "0.5.3"; a version without a "+" is left as it is.</summary>
        public static string Clean(string informational) =>
            informational.IndexOf('+') is var i and >= 0 ? informational[..i] : informational;
    }
}
