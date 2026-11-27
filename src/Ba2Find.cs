// sf-mod-check ba2 find (0.27.0): which loaded archives and loose folders hold a file name (or a glob of them), in
// the order the game loads them, and which copy wins. Design: NOTES.md "0.27.0", part C.
//   sf-mod-check ba2 find <pattern> [--plugins <Plugins.txt> | --mo2 <folder> [--profile <name>]] [--data <Data folder>]
//                         [--extra-data <folder>] [--ini <file>]... [--language <xx>] [--archives <folder>] [--max <n>]
//                         [--json] [--quiet] [--verbose] [--log <file>]
// The archives are found exactly as check and conflict find them (AssetIndex.Build: the INIs' archives, then each
// plugin's "<plugin> - *.ba2" in plugin order, other-language voices skipped), and each one's name table is read once.
// The winner of a name: a loose file in the first folder that has it beats every archive; else the last archive in
// load order that holds it. A texture (DX10) archive serves only .dds names. --archives adds the .ba2 files of a
// folder that the load order didn't load, listed as "not loaded"; they never win.
// Exit 0 = at least one name matched, 1 = nothing matched, 2 = could not run.
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SfModCheck.Conflict;

namespace SfModCheck;

public static class Ba2Find
{
    public const string Usage =
        "usage: sf-mod-check ba2 find <pattern> [--plugins <Plugins.txt> | --mo2 <MO2 folder> [--profile <name>]] [--data <Data folder>]\n" +
        "                             [--extra-data <folder>] [--ini <file>]... [--language <xx>] [--archives <folder>] [--max <n>]\n" +
        "                             [--json] [--quiet] [--verbose] [--log <file>]\n" +
        "  Lists which archives (.ba2) the game loads and which loose folders hold a file, in load order, and marks the\n" +
        "  copy the game uses: a loose file beats every archive, and a later archive beats an earlier one. A loose file\n" +
        "  replaces an archived copy only when an INI's [Archive] section sets bInvalidateOlderFiles=1 and has an empty\n" +
        "  sResourceDataDirsFinal= line. Without both, a note says the game may use the archived copy.\n" +
        "  <pattern>     a file name as the game sees it (\"sound\\voice\\mymod.esm\\npcmguard\\0001a2b3_1.wem\"), or a glob:\n" +
        "                * matches any characters (folders included) and ? one character. Case doesn't matter, and / is\n" +
        "                the same as \\. Quote it so the shell doesn't expand it.\n" +
        "  --plugins     the load order (default: the game's own Plugins.txt); --mo2 reads an MO2 profile instead\n" +
        "  --extra-data  a folder searched before Data for archives and loose files\n" +
        "  --ini         another INI whose [Archive] lists and loose-file keys count (after Starfield.ini and StarfieldCustom.ini)\n" +
        "  --language    the voice language whose archives load (default en); other languages' voice archives are skipped\n" +
        "  --archives    also search every .ba2 in this folder that the load order doesn't load (shown as not loaded;\n" +
        "                they never win). Use it to search a mod's archives before it is installed.\n" +
        "  --max         list at most this many file names (default 100; 0 lists them all). The counts stay complete.\n" +
        "A texture (DX10) archive serves only .dds files; another name in one is listed but can't win.\n" +
        "Exit 0 = at least one file matched, 1 = nothing matched, 2 = could not run.";

    public const int DefaultMax = 100;

    /// <summary>One copy of a matched name: in an archive (Archive set) or loose (Path set).</summary>
    internal sealed record Holder(string Kind, string? Archive, string? Path, string Why, string? Type, bool Loaded, bool Served)
    {
        public bool Winner { get; set; }
    }

    /// <summary>An archive to search, in order: loaded ones first (load order), then the --archives ones.</summary>
    sealed record Source(string Path, string Type, string Why, bool Loaded);

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
        ["tool"] = "sf-mod-check ba2 find", ["version"] = version, ["result"] = "cannot run", ["error"] = why,
    }.ToJsonString(Indented);

    /// <summary>Why a pattern can't be used, or null. A name that leaves Data (rooted, a drive, ..) is never a file the
    /// game loads, and isn't looked up loose.</summary>
    internal static string? BadPattern(string normalized)
    {
        if (normalized.Length == 0) return "it is empty";
        if (normalized.Contains(':') || Path.IsPathRooted(normalized)) return "it names a drive or a rooted path, not a file under Data";
        if (normalized.Split('\\').Any(s => s == "..")) return "it has a .. part, which leaves Data";
        if (normalized.EndsWith('\\')) return "it ends in a folder separator; add a file name or *";
        return null;
    }

    static int RunLogged(string[] a, string version, string byline, out bool resultPrinted, out string cantWhy)
    {
        resultPrinted = false;
        cantWhy = "bad arguments; see the message above";
        string? pattern = null, archivesDir = null, logFile = null;
        var inis = new List<string>();
        string language = "en";
        bool quiet = false, verbose = false, json = false;
        int max = DefaultMax;
        using var input = new LoadOrderInput(command: "ba2 find");
        for (int i = 0; i < a.Length; i++)
        {
            if (input.TryOption(a, ref i, Usage) is { } taken) { if (!taken) return 2; continue; }
            switch (a[i])
            {
                case "--ini":
                    if (Cli.Value(a, ref i, "an INI file", Usage) is not { } ini) return 2;
                    inis.Add(ini); break;
                case "--language":
                    if (Cli.Value(a, ref i, "a language code such as en or de", Usage) is not { } lang) return 2;
                    if (lang.Length is < 2 or > 6 || !lang.All(char.IsAsciiLetter))
                    {
                        Console.Error.WriteLine($"--language needs a voice language code as in the archive names (en, de, fr, es, it, ja, pl, ptbr, zhhans), not \"{SafeText.OneLine(lang)}\"");
                        cantWhy = "--language needs a language code";
                        return 2;
                    }
                    language = lang; break;
                case "--archives":
                    if (Cli.Value(a, ref i, "a folder of .ba2 files", Usage) is not { } ad) return 2;
                    archivesDir = ad; break;
                case "--max":
                    if (Cli.Value(a, ref i, "a number of file names (0 = all)", Usage) is not { } mx) return 2;
                    if (!int.TryParse(mx, NumberStyles.None, CultureInfo.InvariantCulture, out max))
                    {
                        Console.Error.WriteLine($"--max needs a whole number of file names, 0 or more (0 lists them all), not \"{SafeText.OneLine(mx)}\".");
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
                    if (a[i].StartsWith("--") || pattern != null)
                    {
                        Console.Error.WriteLine(a[i].StartsWith("--") ? $"unknown option {a[i]}" : $"ba2 find takes one pattern; got another: {SafeText.OneLine(a[i])}");
                        Console.Error.WriteLine(Usage);
                        return 2;
                    }
                    pattern = a[i];
                    break;
            }
        }
        if (string.IsNullOrWhiteSpace(pattern))
        {
            Console.Error.WriteLine("ba2 find needs a file name or a glob, for example: sf-mod-check ba2 find \"sound\\voice\\mymod.esm\\*\"");
            Console.Error.WriteLine(Usage);
            cantWhy = "no pattern given";
            return 2;
        }
        var wanted = Ba2Names.Normalize(pattern.Trim());
        if (BadPattern(wanted) is { } bad)
        {
            Console.Error.WriteLine($"ba2 find: the pattern \"{SafeText.OneLine(pattern)}\" can't be used: {bad}. Give a name under Data, such as \"meshes\\mymod\\*.nif\".");
            cantWhy = "the pattern can't be used";
            return 2;
        }
        if (archivesDir != null && !Directory.Exists(archivesDir))
        {
            Console.Error.WriteLine($"ba2 find: there is no folder {SafeText.OneLine(archivesDir)} for --archives. Give the folder that holds the .ba2 files (a path with spaces needs quotes).");
            cantWhy = "the --archives folder doesn't exist";
            return 2;
        }
        bool glob = wanted.IndexOfAny(['*', '?']) >= 0;
        var match = Ba2Command.Glob(wanted);

        if (!quiet && !json) Console.Error.WriteLine(byline);
        if (verbose) Log.ToStderr();
        if (logFile != null && Log.ToFile(logFile) is { } logError) { Console.Error.WriteLine(logError); cantWhy = "the log file can't be written"; return 2; }
        Log.Write($"sf-mod-check {version} ba2 find on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, .NET {Environment.Version}");
        Log.Write($"arguments: {string.Join(" ", a.Select(x => x.Contains(' ') ? $"\"{x}\"" : x))}");
        if (!input.Resolve(out var why)) { cantWhy = why; return 2; }

        var sw = Stopwatch.StartNew();
        LoadOrderSetup lo;
        try { lo = input.Build(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            cantWhy = $"the load order can't be read: {e.GetType().Name}";
            Console.Error.WriteLine($"can't read the load order: {e.Message} Check that this account can read that file or folder, and try again.");
            Log.Write($"stopped: reading the load order: {e}");
            return 2;
        }
        List<string> plugins;
        try { plugins = lo.Plugins.Select(p => p.FileName).ToList(); }
        finally { foreach (var p in lo.Plugins) p.Mod.Dispose(); }
        Log.Write($"loaded {plugins.Count} plugins ({SafeText.OneLine(lo.OrderSource)}), {lo.Problems.Count} load-order problems");
        foreach (var p in lo.Problems) Log.Write($"load-order problem: {p}");

        // the archives, as check and conflict find them (types only: the names are read once below)
        var iniFiles = AssetIndex.DefaultInis(input.DataDir, inis);
        var index = AssetIndex.Build(plugins, input.AssetDirs, iniFiles, language, names: false);
        var looseReplaces = AssetIndex.LooseFilesReplaceArchives(iniFiles);
        var cmp = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var sources = index.ArchivesInLoadOrder().Select(x => new Source(x.Path, x.Type, x.Why, true)).ToList();
        var unreadable = index.Unreadable.Select(u => (Archive: u.Archive, Why: u.Why)).ToList();
        if (archivesDir != null)
        {
            var seen = new HashSet<string>(sources.Select(s => Path.GetFullPath(s.Path)), cmp);
            var extra = Directory.EnumerateFiles(archivesDir, "*.ba2", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, AttributesToSkip = 0, IgnoreInaccessible = true })
                .Where(f => !seen.Contains(Path.GetFullPath(f))).Order(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var f in extra)
            {
                try
                {
                    var type = Ba2Names.Type(f);
                    if (type is not ("GNRL" or "DX10")) { unreadable.Add((Path.GetFileName(f), $"its type \"{SafeText.OneLine(type)}\" is not GNRL or DX10")); continue; }
                    sources.Add(new Source(f, type, $"not loaded: in --archives {Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(archivesDir)))}", false));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { unreadable.Add((Path.GetFileName(f), e.Message)); }
            }
            Log.Write($"--archives {archivesDir}: {extra.Count} archives the load order doesn't load");
        }

        // name -> its holders, archives in order
        var found = new SortedDictionary<string, List<Holder>>(StringComparer.Ordinal);
        int archivesRead = 0;
        var archivesWithMatch = new HashSet<string>(cmp);
        foreach (var s in sources)
        {
            var name = Path.GetFileName(s.Path);
            List<string> names;
            try { names = Ba2Names.Read(s.Path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                unreadable.Add((name, e.Message));
                Log.Write($"ba2 find: archive {name} could not be read: {e.Message}");
                continue;
            }
            archivesRead++;
            int hits = 0;
            foreach (var n in names.Distinct(StringComparer.Ordinal))
            {
                if (glob ? !match.IsMatch(n) : n != wanted) continue;
                hits++;
                bool served = s.Type != "DX10" || n.EndsWith(".dds", StringComparison.Ordinal);
                if (!found.TryGetValue(n, out var list)) found[n] = list = new();
                list.Add(new Holder("archive", name, null, s.Why, s.Type, s.Loaded, served));
            }
            if (hits > 0) archivesWithMatch.Add(s.Path);
            Log.Write($"ba2 find: {name} ({s.Type}{(s.Loaded ? "" : ", not loaded")}): {names.Count:N0} names, {hits:N0} matched");
        }

        // loose files: the first folder (--extra-data, MO2's folders) wins over a later one (Data last)
        var looseHits = new Dictionary<string, List<(string Dir, string Path)>>(StringComparer.Ordinal);
        var loosewith = new HashSet<string>(cmp);
        foreach (var dir in index.LooseDirs)
        {
            var t = Stopwatch.StartNew();
            int hits = 0;
            foreach (var (rel, full) in Loose(index, dir, wanted, glob, match))
            {
                if (!looseHits.TryGetValue(rel, out var l)) looseHits[rel] = l = new();
                if (l.Any(x => cmp.Equals(x.Dir, dir))) continue;   // two spellings of one name (Linux): the first one
                l.Add((dir, full));
                hits++;
            }
            if (hits > 0) loosewith.Add(dir);
            Log.Write($"ba2 find: loose folder {dir}: {hits:N0} matched ({t.Elapsed.TotalSeconds:F1} s)");
        }
        foreach (var (rel, list) in looseHits)
        {
            if (!found.TryGetValue(rel, out var holders)) found[rel] = holders = new();
            // shown lowest priority first, so the winner is the last loose line
            var loose = Enumerable.Reverse(list).Select(x => new Holder("loose", null, x.Path, $"loose file in {x.Dir}", null, true, true)).ToList();
            var at = holders.FindIndex(h => !h.Loaded);
            holders.InsertRange(at < 0 ? holders.Count : at, loose);
        }
        foreach (var holders in found.Values)
        {
            var win = holders.LastOrDefault(h => h.Kind == "loose") ?? holders.LastOrDefault(h => h.Kind == "archive" && h.Loaded && h.Served);
            if (win != null) win.Winner = true;
        }
        // a loose copy wins over a loaded archive's copy only with the loose-file INI setting (see the note below)
        int looseOverArchive = found.Values.Count(hs => hs.Any(h => h.Kind == "loose") && hs.Any(h => h.Kind == "archive" && h.Loaded && h.Served));
        var looseNote = looseReplaces || looseOverArchive == 0 ? null
            : $"note: {looseOverArchive:N0} name{(looseOverArchive == 1 ? " is" : "s are")} both loose and in a loaded archive. A loose file replaces the archived copy only when an INI sets [Archive] bInvalidateOlderFiles=1 and sResourceDataDirsFinal= (the usual setup for loose-file mods), and the INIs read don't, so the game may use the archived copy instead (add yours with --ini{(input.Mo2 != null ? "; an MO2 profile's own INI files aren't read" : "")})";

        var shown = max == 0 ? found.ToList() : found.Take(max).ToList();
        var total = found.Count;
        resultPrinted = true;
        var summary = $"{total:N0} file{(total == 1 ? "" : "s")} matched in {archivesWithMatch.Count:N0} of {archivesRead:N0} archive{(archivesRead == 1 ? "" : "s")} and {loosewith.Count:N0} loose folder{(loosewith.Count == 1 ? "" : "s")}";
        if (json)
            Console.WriteLine(Json(version, pattern, lo.OrderSource, shown, total, archivesRead, index.Skipped, unreadable, looseReplaces));
        else
        {
            if (!quiet)
                Console.WriteLine($"sf-mod-check {version} ba2 find: {SafeText.OneLine(pattern)} in {plugins.Count} plugins ({SafeText.OneLine(lo.OrderSource)})");
            foreach (var (n, holders) in shown)
            {
                Console.WriteLine(SafeText.OneLine(n));
                for (int k = 0; k < holders.Count; k++) Console.WriteLine($"  {k + 1}. {Line(holders[k])}");
            }
            if (total > shown.Count) Console.WriteLine($"... and {total - shown.Count:N0} more (raise --max to see them; --max 0 lists them all)");
            foreach (var (u, w) in unreadable) Console.WriteLine($"unreadable: {SafeText.OneLine(u)}: {SafeText.OneLine(w)}");
            if (looseNote != null) Console.WriteLine(looseNote);
            Console.WriteLine(total == 0
                ? $"RESULT: NOT FOUND (no file matches {SafeText.OneLine(pattern)} in {archivesRead:N0} archive{(archivesRead == 1 ? "" : "s")} and {index.LooseDirs.Count:N0} loose folder{(index.LooseDirs.Count == 1 ? "" : "s")})"
                : $"RESULT: {summary}");
        }
        Log.Write($"result: {summary}, {unreadable.Count} unreadable, exit code {(total > 0 ? 0 : 1)}, {sw.Elapsed.TotalSeconds:F1} s");
        return total > 0 ? 0 : 1;
    }

    static string Line(Holder h)
    {
        var text = h.Kind == "loose"
            ? $"loose: {SafeText.OneLine(h.Path!)}"
            : $"{SafeText.OneLine(h.Archive!)} ({SafeText.OneLine(h.Why)}{(h.Type == "DX10" ? ", texture archive" : "")}{(h.Served ? "" : ", not served from a texture archive")})";
        return h.Winner ? $"{text}   <- wins" : text;
    }

    /// <summary>The loose files under dir that the pattern names, as (normalized name, full path). An exact name is
    /// looked up as the assets check looks it up; a glob walks only below its fixed folder prefix (all of dir when it
    /// starts with a wildcard), recursively, without following links.</summary>
    static IEnumerable<(string Rel, string Full)> Loose(AssetIndex index, string dir, string wanted, bool glob, System.Text.RegularExpressions.Regex match)
    {
        if (!Directory.Exists(dir)) yield break;
        if (!glob)
        {
            // a part ending in a dot or a space never counts loose, as in the assets check (Windows trims it)
            if (AssetIndex.OddPart(wanted)) yield break;
            string? f = null;
            try { f = index.LooseFile(dir, wanted); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { }
            if (f != null) yield return (wanted, f);
            yield break;
        }
        var parts = wanted.Split('\\');
        var fixedParts = parts.TakeWhile(p => p.IndexOfAny(['*', '?']) < 0).ToList();
        if (fixedParts.Count == parts.Length) fixedParts.RemoveAt(fixedParts.Count - 1);   // not reached: a glob has a wildcard
        var starts = Folders(dir, fixedParts);
        if (fixedParts.Count == 0) Log.Write($"ba2 find: the pattern starts with a wildcard, so all of {dir} is walked");
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, MatchCasing = MatchCasing.CaseInsensitive,
        };
        var results = new List<(string, string)>();
        foreach (var start in starts)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(start, "*", opts))
                {
                    var rel = Ba2Names.Normalize(Path.GetRelativePath(dir, f));
                    if (!AssetIndex.OddPart(rel) && match.IsMatch(rel)) results.Add((rel, f));   // as the exact-name path
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Write($"ba2 find: {start} could not be listed ({e.Message})"); }
        }
        foreach (var r in results.OrderBy(r => r.Item1, StringComparer.Ordinal).ThenBy(r => r.Item2, StringComparer.Ordinal)) yield return r;
    }

    /// <summary>The folders under dir that a fixed folder path names, each part matched without regard to case (several
    /// on Linux when two folders differ only in case). Links aren't followed.</summary>
    static List<string> Folders(string dir, List<string> parts)
    {
        var at = new List<string> { dir };
        var ci = new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var part in parts)
        {
            if (part is "" or ".") continue;
            var next = new List<string>();
            foreach (var d in at)
            {
                try { next.AddRange(Directory.EnumerateDirectories(d, part, ci).Where(x => string.Equals(Path.GetFileName(x), part, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { Log.Write($"ba2 find: {d} could not be listed ({e.Message})"); }
            }
            at = next;
            if (at.Count == 0) break;
        }
        return at;
    }

    static string Json(string version, string pattern, string source, List<KeyValuePair<string, List<Holder>>> shown, int total, int archivesRead,
                       List<(string Archive, string Why)> skipped, List<(string Archive, string Why)> unreadable, bool looseReplaces)
    {
        var files = new JsonArray();
        foreach (var (n, holders) in shown)
        {
            var hs = new JsonArray();
            foreach (var h in holders)
            {
                var o = new JsonObject { ["kind"] = h.Kind };
                if (h.Kind == "archive") o["archive"] = h.Archive;
                else o["path"] = h.Path;
                o["why"] = h.Why;
                o["type"] = h.Type;
                o["loaded"] = h.Loaded;
                o["served"] = h.Served;
                o["winner"] = h.Winner;
                hs.Add(o);
            }
            files.Add(new JsonObject { ["name"] = n, ["holders"] = hs });
        }
        return new JsonObject
        {
            ["tool"] = "sf-mod-check ba2 find", ["version"] = version, ["pattern"] = pattern, ["loadOrder"] = source,
            ["files"] = files, ["listed"] = shown.Count, ["total"] = total, ["archivesRead"] = archivesRead,
            ["looseFilesReplaceArchives"] = looseReplaces,
            ["skipped"] = new JsonArray(skipped.Select(s => (JsonNode)new JsonObject { ["archive"] = s.Archive, ["why"] = s.Why }).ToArray()),
            ["unreadable"] = new JsonArray(unreadable.Select(s => (JsonNode)new JsonObject { ["archive"] = s.Archive, ["why"] = s.Why }).ToArray()),
        }.ToJsonString(Indented);
    }
}
