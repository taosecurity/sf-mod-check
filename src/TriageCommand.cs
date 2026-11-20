// Command "triage" (0.8.0): a first-pass, offline report on someone else's mod, before you use or rebuild it.
//   sf-mod-check triage <mod.zip | .7z | .rar | folder | plugin> [--game <folder>] [--out <folder>] [--manalyze <exe>]
//                     [--quiet] [--verbose] [--log <file>]
// It is triage, not a verdict: each finding is a NOTE (information) or a LOOK (worth a person's look), with the file and
// the reason. It writes <out>\<mod>-triage.html, .txt and .json and nothing else (an archive is unpacked into a temp
// folder that is always removed). It never uploads anything and never runs the mod's code.
// Exit 0 = report written, 2 = bad input or setup. There is no "fail" exit.
// The sections live in TriageFiles.cs (files, replaces vanilla), TriagePlugins.cs, TriageScripts.cs and
// TriageBinaries.cs; the report writer in TriageReport.cs.
using SharpCompress.Archives;
using SharpCompress.Common;

namespace SfModCheck;

public enum TriageLevel { Note, Look }

/// <summary>Path's name helpers for triage's paths, which use backslashes (Ba2Names.Normalize) on every system. On
/// Linux and macOS a backslash isn't a separator, so Path.GetFileName("data\mod.esm") gave the whole string (found by
/// CI on ubuntu, 2026-11-20). These treat both slashes as separators; on Windows they give what Path gives.</summary>
public static class RelPath
{
    // only a relative path (triage's own "data\mod.esm" form) is read with backslashes as separators; a full path of
    // the file system is passed to Path as it is (a backslash is a legal name character on Linux; review)
    static string? Unix(string? s) => s == null || Path.IsPathRooted(s) ? s : s.Replace('\\', '/');
    static string? Back(string? s, string? original) => s != null && original != null && !Path.IsPathRooted(original) && original.Contains('\\') && !OperatingSystem.IsWindows() ? s.Replace('/', '\\') : s;
    public static string? FileName(string? s) => Path.GetFileName(Unix(s));
    public static string FileNameWithoutExtension(string? s) => Path.GetFileNameWithoutExtension(Unix(s)) ?? "";
    public static string Extension(string? s) => Path.GetExtension(Unix(s)) ?? "";
    public static string? DirectoryName(string? s) => Back(Path.GetDirectoryName(Unix(s)), s);
}

/// <summary>One line of the report. Section is one of the TriageSections names; File is the mod-relative path
/// (Ba2Names.Normalize form; "" when the finding is about the whole mod).</summary>
public sealed record TriageFinding(TriageLevel Level, string Section, string File, string Text);

public static class TriageSections
{
    public const string Files = "Files";
    public const string ReplacesVanilla = "Replaces vanilla";
    public const string Plugins = "Plugins";
    public const string Scripts = "Scripts";
    public const string Binaries = "Binaries";
    public static readonly string[] Order = [Files, ReplacesVanilla, Plugins, Scripts, Binaries];
}

public enum TriageInputKind { Zip, SevenZip, Rar, Folder, Plugin }

public sealed class TriageOptions
{
    public string Input { get; set; } = "";
    /// <summary>The game's Data folder (from --game, or Steam's usual one).</summary>
    public string DataDir { get; set; } = Cli.SteamData;
    public bool GameGiven { get; set; }
    public string? Out { get; set; }
    public string? Manalyze { get; set; }
}

public sealed class TriageContext
{
    public required TriageOptions Options { get; init; }
    public required TriageInputKind Kind { get; init; }
    /// <summary>The full path of what the user gave (the archive, folder or plugin).</summary>
    public required string InputPath { get; init; }
    /// <summary>The folder the mod's files are in: the unpacked archive, the given folder, or a plugin's own folder.</summary>
    public required string Root { get; init; }
    /// <summary>Every file of the mod, as Ba2Names.Normalize paths relative to Root ("scripts\foo\bar.pex").</summary>
    public required List<string> Files { get; init; }
    /// <summary>"data\" when every file sits under a top folder named Data (a zip made from the game folder), else "".
    /// DataPath strips it, so paths compare with the game's own.</summary>
    public required string DataPrefix { get; init; }
    /// <summary>The game's Data folder (may not exist: sections then say what they couldn't compare).</summary>
    public required string DataDir { get; init; }
    /// <summary>The game install folder (the one above Data), or null when the Data folder has no parent.</summary>
    public string? GameDir => Path.GetDirectoryName(Path.GetFullPath(DataDir).TrimEnd('\\', '/'));
    public bool HasGame => hasGame.Value;   // worked out once, thread-safe (it is read per file)
    readonly Lazy<bool> hasGame;
    public TriageContext() => hasGame = new Lazy<bool>(() => TriageCommand.HasStarfield(DataDir));

    /// <summary>The full path of a file in the game's Data folder, its name matched ignoring case; null when none.</summary>
    public string? DataFind(string fileName)
    {
        var direct = Path.Combine(DataDir, fileName);
        if (File.Exists(direct)) return direct;
        if (OperatingSystem.IsWindows()) return null;
        try { return Directory.EnumerateFiles(DataDir).FirstOrDefault(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>True when the game's Data folder holds a file of this name, ignoring case on every system (names in
    /// Files are lower case; on Linux "starfield - misc.ba2" isn't "Starfield - Misc.ba2").</summary>
    public bool DataHas(string fileName)
    {
        if (File.Exists(Path.Combine(DataDir, fileName))) return true;
        if (OperatingSystem.IsWindows()) return false;
        try { return Directory.EnumerateFiles(DataDir).Any(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
    /// <summary>A temp folder sections may write scratch files into (removed at the end).</summary>
    public required string Work { get; init; }
    public required TriageReport Report { get; init; }

    /// <summary>The full path of a file of the mod. Files holds lower-case names, so where file names are case-sensitive
    /// (Linux, macOS) each part of the path is found by name ignoring case, an exact match first (found by CI on
    /// ubuntu, 2026-11-20: every section missed "Scripts/Foo.pex" as "scripts/foo.pex").</summary>
    public string Full(string rel)
    {
        var direct = Path.Combine(Root, rel.Replace('\\', Path.DirectorySeparatorChar));
        if (OperatingSystem.IsWindows()) return direct;   // elsewhere always the full lookup: a link at any level is refused (security review: a linked parent folder)
        lock (resolved)
        {
            if (resolved.TryGetValue(rel, out var hit)) return hit;
            // every folder whose name matches ignoring case is tried in turn ("Scripts" and "scripts" can both exist),
            // the exact spelling first; each folder is listed once (review of the Linux fix)
            var found = Find(Root, rel.Split('\\'), 0);
            // not there (or only behind a link, which is never followed): a path that doesn't exist, so the caller
            // reports the file as missing (review: the exact-name fallback could be the link itself)
            if (found == null) return File.Exists(direct) || Directory.Exists(direct) ? Path.Combine(Root, ".sf-mod-check-link-not-followed", rel.Replace('\\', Path.DirectorySeparatorChar)) : direct;
            return resolved[rel] = found;
        }
    }

    // Two passes over every folder whose name matches: a file anywhere first, a folder at the last part only when no
    // file matches in any of them (reviews of the Linux fix). Links are never followed.
    string? Find(string dir, string[] parts, int i) => Find(dir, parts, i, folderOk: false) ?? Find(dir, parts, i, folderOk: true);

    string? Find(string dir, string[] parts, int i, bool folderOk)
    {
        if (i == parts.Length) return dir;
        if (i == parts.Length - 1)
            return Matches(dir, parts[i]).FirstOrDefault(e => !IsLink(e) && (File.Exists(e) || folderOk && Directory.Exists(e)));
        foreach (var entry in Matches(dir, parts[i]))
        {
            bool folder;
            try { folder = Directory.Exists(entry) && !IsLink(entry); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { folder = false; }   // gone or denied: no match
            if (folder && Find(entry, parts, i + 1, folderOk) is { } f) return f;
        }
        return null;
    }

    /// <summary>True for a link (symlink, junction); also when its attributes can't be read (then it isn't followed).</summary>
    static bool IsLink(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>The entries of a folder whose name matches ignoring case, the exact spelling first (one listing per folder).</summary>
    IEnumerable<string> Matches(string dir, string name)
    {
        if (!listings.TryGetValue(dir, out var map))
        {
            map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var e in Directory.EnumerateFileSystemEntries(dir))
                {
                    var n = Path.GetFileName(e);
                    if (!map.TryGetValue(n, out var l)) map[n] = l = new List<string>();
                    l.Add(e);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            listings[dir] = map;
        }
        return map.TryGetValue(name, out var list) ? list.OrderBy(e => Path.GetFileName(e) == name ? 0 : 1) : [];
    }
    readonly Dictionary<string, string> resolved = new(StringComparer.Ordinal);
    readonly Dictionary<string, Dictionary<string, List<string>>> listings = new(StringComparer.Ordinal);
    public string DataPath(string rel) => DataPrefix.Length > 0 && rel.StartsWith(DataPrefix, StringComparison.Ordinal) ? rel[DataPrefix.Length..] : rel;

    IEnumerable<string> WithExt(params string[] exts) =>
        Files.Where(f => exts.Contains(Path.GetExtension(f), StringComparer.Ordinal)).Select(Full);
    /// <summary>Full paths of the plugins (.esm .esp .esl).</summary>
    public List<string> Plugins => WithExt(".esm", ".esp", ".esl").ToList();
    /// <summary>Full paths of the archives (.ba2).</summary>
    public List<string> Archives => WithExt(".ba2").ToList();
    /// <summary>Full paths of the loose compiled scripts (.pex); scripts inside archives are the Scripts section's job.</summary>
    public List<string> Scripts => WithExt(".pex").ToList();
    /// <summary>Full paths of the binaries (.dll .exe .asi: an ASI plugin is a DLL an ASI loader puts into the game).</summary>
    public List<string> Binaries => WithExt(".dll", ".exe", ".asi").ToList();
}

public static class TriageCommand
{
    public const string Usage =
        "usage: sf-mod-check triage <mod.zip | mod.7z | mod.rar | mod folder | plugin> [--game <folder>] [--out <folder>]\n" +
        "                         [--manalyze <manalyze.exe>] [--quiet] [--verbose] [--log <file>]";

    /// <summary>The sections, in report order. Tests can swap them.</summary>
    public static List<(string Name, Func<TriageContext, IEnumerable<TriageFinding>> Run)> Sections { get; } =
    [
        ("Files", TriageFiles.Run),
        ("Plugins", TriagePlugins.Run),
        ("Scripts", TriageScripts.Run),
        ("Binaries", TriageBinaries.Run),
    ];

    public static int Run(string[] a, string version, string byline)
    {
        try { return RunLogged(a, version, byline); }
        catch (Exception e)
        {
            Console.WriteLine(Fatal.ResultLine(e, () => $"RESULT: NO REPORT ({e.GetType().Name}: {e.Message})"));
            Fatal.AfterResult(() =>
            {
                Console.Error.WriteLine(Log.On ? "The log has the full error." : "For the full error, run again with --log sf-mod-check-log.txt and look at that file.");
                Log.Write($"stopped: {e}");
            });
            return 2;
        }
        finally { Log.Close(); }
    }

    static int CantRun(string why, string detail)
    {
        Console.Error.WriteLine(detail);
        Log.Write($"stopped: {detail}");
        Console.WriteLine($"RESULT: NO REPORT ({why})");
        return 2;
    }

    static int RunLogged(string[] a, string version, string byline)
    {
        var o = new TriageOptions();
        bool quiet = false, verbose = false;
        string? logFile = null, game = null;
        string? Value(ref int i, string what) => Cli.Value(a, ref i, what, Usage);
        for (int i = 0; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "--game":
                    if (Value(ref i, "the game's folder (or its Data folder)") is not { } g) return 2;
                    game = g; break;
                case "--out":
                    if (Value(ref i, "a folder for the report") is not { } of) return 2;
                    o.Out = of; break;
                case "--manalyze":
                    if (Value(ref i, "the path of manalyze.exe") is not { } ma) return 2;
                    o.Manalyze = ma; break;
                case "--log":
                    if (Value(ref i, "a file name") is not { } lf) return 2;
                    logFile = lf; break;
                case "--verbose": verbose = true; break;
                case "--quiet": quiet = true; break;
                case "-h": case "--help": Console.Error.WriteLine(Usage); return 2;
                default:
                    if (a[i].StartsWith("--"))
                    {
                        Console.Error.WriteLine($"unknown option {a[i]}");
                        Console.Error.WriteLine(Usage);
                        return 2;
                    }
                    if (a[i].Trim().Length == 0)
                    {
                        Console.Error.WriteLine("the mod's name is empty: give the path of the mod's download, for example: sf-mod-check triage SomeMod-1.2.zip");
                        return 2;
                    }
                    if (o.Input != "")
                    {
                        Console.Error.WriteLine($"one mod per run: got {o.Input} and {a[i]}. Run triage once for each (a path with spaces needs quotes around it).");
                        return 2;
                    }
                    o.Input = a[i];
                    break;
            }
        }
        if (o.Input == "")
        {
            Console.Error.WriteLine(Usage);
            Console.Error.WriteLine("To look over a mod you downloaded, before you install it, run: sf-mod-check triage SomeMod-1.2.zip");
            return 2;
        }
        if (!quiet) Console.Error.WriteLine(byline);
        if (verbose) Log.ToStderr();
        if (logFile != null && Log.ToFile(logFile) is { } logError) return CantRun("the log file can't be written", logError);
        Log.Write($"sf-mod-check {version} triage on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, .NET {Environment.Version}");
        Log.Write($"arguments: {string.Join(" ", a.Select(x => x.Contains(' ') ? $"\"{x}\"" : x))}");

        // ---- the game ----
        if (game != null)
        {
            if (FindData(game) is not { } d)
                return CantRun("no Starfield.esm in the --game folder",
                    $"can't use --game {Path.GetFullPath(game)}: it has no Starfield.esm, in itself, in Data or in Content\\Data. Give the game's folder or its Data folder " +
                    "(Steam: ...\\steamapps\\common\\Starfield; Game Pass: ...\\XboxGames\\Starfield\\Content).");
            o.DataDir = d; o.GameGiven = true;
        }
        var hasGame = HasStarfield(o.DataDir);
        Log.Write($"game Data folder: {Path.GetFullPath(o.DataDir)} ({(o.GameGiven ? "from --game" : "Steam's usual folder")}; Starfield.esm {(hasGame ? "found" : "NOT found")})");
        if (!hasGame)
            Console.Error.WriteLine($"note: no game found in Steam's usual folder ({o.DataDir}), so nothing is compared with the game's own files. " +
                                    "Give --game \"<the game's folder>\" to compare (Game Pass: ...\\XboxGames\\Starfield\\Content).");
        if (o.Manalyze != null && !File.Exists(o.Manalyze))
            return CantRun("--manalyze file not found", $"can't use --manalyze {Path.GetFullPath(o.Manalyze)}: no such file. Give the path of manalyze.exe, or leave the option out.");

        // ---- the input ----
        var input = Path.GetFullPath(o.Input);
        TriageInputKind kind;
        if (Directory.Exists(input))
        {
            // the game's Data folder, its install folder or Game Pass's Content folder (third code review), or a folder in them
            var inGame = FindData(input) != null ? true : InstallCheck(input, o.DataDir);
            if (inGame == true)
                return CantRun("that is the game's own folder",
                    $"won't triage {input}: it is the game's install or a folder inside it. Give the mod's own folder, its download, or one plugin.");
            if (inGame == null)
                return CantRun("the folder's path can't be checked",
                    $"won't triage {input}: its path couldn't be checked against the game's install. Copy the mod to a plain folder such as C:\\Mods\\SomeMod and try again.");
            kind = TriageInputKind.Folder;
        }
        else if (!File.Exists(input))
            return CantRun($"{Shown(input)}: no such file", $"can't triage {input}: no such file or folder. Check the path (a path with spaces needs quotes around it).");
        else if (KindOf(input) is { } k) kind = k;
        else
            return CantRun($"{Shown(input)}: not a mod download or plugin",
                $"can't triage {input}: give a .zip, .7z or .rar download, a mod folder, or a plugin (.esm .esp .esl).");
        Log.Write($"input: {kind} {Log.Describe(input)}");

        // an example that works in any shell: a full path, not %USERPROFILE% (only cmd.exe expands it; third code review)
        var example = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) is { Length: > 0 } docs ? docs : Path.GetTempPath(), "Triage");
        if ((o.Out ?? DefaultOut(input, o.DataDir)) is not { } outChoice)
            return CantRun("give --out: the input and the current folder are both inside the game",
                $"won't write the report into the game's install: {input} and the current folder ({Environment.CurrentDirectory}) are both inside it. " +
                $"Give --out a folder of your own, for example --out \"{example}\".");
        var outDir = Path.GetFullPath(outChoice);
        // never in the game, --out included (a relative --out run from the game folder lands there)
        switch (InstallCheck(outDir, o.DataDir))
        {
            case true:
                return CantRun("--out is inside the game",
                    $"won't write the report to {outDir}: it is inside the game's install. Give --out a folder of your own, for example --out \"{example}\".");
            case null:
                return CantRun("the report folder's path can't be checked",
                    $"won't write the report to {outDir}: its path couldn't be checked against the game's install. Give --out a plain folder, for example --out \"{example}\".");
        }
        try { Directory.CreateDirectory(outDir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return CantRun("the report folder can't be made", $"can't write the report to {outDir}: {e.Message} Give --out a folder you can write to.");
        }
        Log.Write($"report folder: {outDir}{(o.Out == null ? " (default)" : " (from --out)")}");

        // ---- unpack ----
        var work = Directory.CreateTempSubdirectory("sf-mod-check-triage-").FullName;
        Log.Write($"work folder: {work}");
        try
        {
            string root;
            List<string> files;
            try { (root, files) = Gather(kind, input, Path.Combine(work, "mod")); }
            catch (Exception e) when (IsFileSystem(e))
            {
                // A file-system error: it can be the PC (a lock, a permission, a full disk) or the archive (a path too long
                // to make, a file and a folder of the same name), so the message names both (tenth code review).
                var shown = Shown(input);
                return kind switch
                {
                    TriageInputKind.Folder => CantRun($"a file in {shown} can't be read",
                        $"can't read a file in {input}: {e.Message} Check that you can open the files there and that no other program has them locked."),
                    TriageInputKind.Plugin => CantRun($"the folder of {shown} can't be read",
                        $"can't list the folder of {input} (to find its archives): {e.Message} Check that you can open that folder."),
                    _ => CantRun($"{shown} couldn't be unpacked",
                        $"couldn't unpack {input}: {e.Message} Either the PC stopped it (the file is open in another program, can't be read, or the " +
                        $"temp folder {Path.GetTempPath()} is full) or the archive holds a path Windows can't make (too long, or a file and a folder " +
                        "with the same name). Try again with the file closed elsewhere and free space in the temp folder; if it still fails, the archive is the cause."),
                };
            }
            catch (Exception e) when (e is InvalidDataException or InvalidFormatException or ArchiveException or NotSupportedException or IOException or UnauthorizedAccessException)
            {
                return CantRun($"{Shown(input)} can't be read", $"can't read {input}: {e.Message}");
            }
            Log.Write($"mod files: {files.Count} under {root}");

            var report = new TriageReport
            {
                Tool = $"sf-mod-check {version}",
                InputName = Shown(input),
                InputPath = input,
                InputKind = kind,
                DataDir = Path.GetFullPath(o.DataDir),
                HasGame = hasGame,
            };
            if (kind != TriageInputKind.Folder) report.SetInputFile(input);
            var ctx = new TriageContext
            {
                Options = o, Kind = kind, InputPath = input, Root = root, Files = files,
                DataPrefix = files.Count > 0 && files.All(f => f.StartsWith("data\\", StringComparison.Ordinal)) ? "data\\" : "",
                DataDir = o.DataDir, Work = work, Report = report,
            };

            foreach (var (name, run) in Sections)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try { report.Findings.AddRange(run(ctx)); }
                catch (Exception e) when (Fatal.Recoverable(e))
                {
                    // One section that stops leaves the others whole; the report says at the top what is missing.
                    report.Incomplete.Add($"{name}: stopped early ({e.GetType().Name}: {e.Message})");
                    Log.Write($"section {name} stopped: {e}");
                }
                Log.Write($"section {name}: {sw.Elapsed.TotalSeconds:F1} s");
            }

            var stem = Path.Combine(outDir, ReportStem(report.InputName));
            var written = report.WriteAll(stem);
            Log.Write($"written: {string.Join(", ", written)}");

            int looks = report.Findings.Count(f => f.Level == TriageLevel.Look), notes = report.Findings.Count - looks;
            if (!quiet)
            {
                Console.WriteLine($"sf-mod-check {version} triage: {report.InputName} ({files.Count} files; triage, not a verdict)");
                foreach (var f in report.Findings.Where(f => f.Level == TriageLevel.Look))
                    Console.WriteLine($"LOOK [{f.Section}] {(f.File != "" ? f.File + ": " : "")}{f.Text}");
                foreach (var i in report.Incomplete) Console.WriteLine($"INCOMPLETE {i}");
            }
            Console.WriteLine($"RESULT: REPORT WRITTEN ({looks} to look at, {notes} notes{(report.Incomplete.Count > 0 ? $", {report.Incomplete.Count} sections incomplete" : "")}) {written[0]}");
            Log.Write("result: report written, exit code 0");
            return 0;
        }
        finally
        {
            try { Directory.Delete(work, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Write($"couldn't remove the work folder {work}: {e.Message}"); }
        }
    }

    /// <summary>The Data folder for --game: the folder itself, its Data, or its Content\Data (Game Pass), whichever holds
    /// Starfield.esm; null when none does.</summary>
    public static string? FindData(string game)
    {
        foreach (var d in new[] { game, Path.Combine(game, "Data"), Path.Combine(game, "Content", "Data") })
            if (HasStarfield(d)) return Path.GetFullPath(d);
        return null;
    }

    /// <summary>True when the folder holds Starfield.esm, its name matched ignoring case (Linux and macOS too; review of
    /// the Linux fix). Never throws.</summary>
    public static bool HasStarfield(string dir)
    {
        try
        {
            if (File.Exists(Path.Combine(dir, "Starfield.esm"))) return true;
            return !OperatingSystem.IsWindows() && Directory.Exists(dir) &&
                   Directory.EnumerateFiles(dir, "starfield.esm", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, AttributesToSkip = 0 }).Any();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    /// <summary>The input's name for messages: its last part, without a trailing slash; the whole path for a drive root
    /// ("D:\", whose name is empty).</summary>
    public static string Shown(string input) =>
        Path.GetFileName(input.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : input;

    public static TriageInputKind? KindOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".zip" => TriageInputKind.Zip,
        ".7z" => TriageInputKind.SevenZip,
        ".rar" => TriageInputKind.Rar,
        ".esm" or ".esp" or ".esl" => TriageInputKind.Plugin,
        _ => null,
    };

    /// <summary>"Some Mod-1.2.zip" -> "Some Mod-1.2"; a plugin keeps its extension in the name ("Foo.esm" -> "Foo.esm"),
    /// so a plugin's report never lands on top of its download's.</summary>
    public static string ReportStem(string inputName)
    {
        var stem = KindOf(inputName) is TriageInputKind.Plugin or null ? inputName : Path.GetFileNameWithoutExtension(inputName);
        foreach (var c in Path.GetInvalidFileNameChars()) stem = stem.Replace(c, '_');
        return stem + "-triage";
    }

    /// <summary>Next to the input, unless that is inside a game install (a plugin in Data): then the current folder, unless
    /// that is inside one too: then null (the caller asks for --out), so the report never lands in the game.</summary>
    public static string? DefaultOut(string input, string dataDir, string? currentDir = null)
    {
        var cwd = currentDir ?? Environment.CurrentDirectory;
        var parent = Path.GetDirectoryName(input.TrimEnd('\\', '/')) ?? cwd;
        if (!InGameInstall(parent, dataDir)) return parent;
        return InGameInstall(cwd, dataDir) ? null : cwd;   // 0.8.0 code review: run from the game's folder
    }

    /// <summary>True when the folder is inside a game install: it, or a folder above it, holds Starfield.esm (directly or
    /// in Data), or it is under the install of dataDir. Any install counts, not only the one --game names (0.8.0 code
    /// review: a plugin in a Game Pass Data folder got its report beside it).</summary>
    public static bool InGameInstall(string folder, string dataDir) =>
        InstallCheck(folder, dataDir) ?? true;   // can't tell: count it as the game, so nothing is written there by mistake

    /// <summary>InGameInstall, with null when the path can't be checked (for the message: "couldn't be checked" is not
    /// "it is the game"; fourth code review).</summary>
    public static bool? InstallCheck(string folder, string dataDir)
    {
        try
        {
            // the three layouts FindData accepts: Data itself, the install (Data\), Game Pass (Content\Data\)
            for (var d = new DirectoryInfo(Path.GetFullPath(folder)); d != null; d = d.Parent)
                if (FindData(d.FullName) != null) return true;
            var data = Path.GetFullPath(dataDir).TrimEnd('\\', '/');
            if (!HasStarfield(data)) return false;
            // The install is above a folder named Data (Steam), and above Content too (Game Pass). A game folder by
            // any other name is the install itself, and a drive root never is (fourth code review: --game D:\Data
            // made all of D: "the game").
            var install = data;
            if (Path.GetFileName(data).Equals("Data", StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(data) is { } up)
            {
                install = up;
                if (Path.GetFileName(up).Equals("Content", StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(up) is { } up2 && !IsRoot(up2)) install = up2;
            }
            if (IsRoot(install)) install = data;   // D:\Data: the drive is not the game (D:\Content\Data keeps D:\Content)
            static bool IsRoot(string p) => Path.GetPathRoot(p) is { } r && p.TrimEnd('\\', '/').Equals(r.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            return (Path.GetFullPath(folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar).StartsWith(install.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException) { return null; }
    }

    /// <summary>The mod's root folder and its files. Archives are unpacked into dest; a folder is read where it is; a
    /// plugin brings the archives beside it that share its name ("Foo - Main.ba2").</summary>
    public static (string Root, List<string> Files) Gather(TriageInputKind kind, string input, string dest)
    {
        switch (kind)
        {
            case TriageInputKind.Zip:
                var p = ReleaseCommand.Unpack(input, dest);
                return (p.Root, p.Files);
            case TriageInputKind.SevenZip:
            case TriageInputKind.Rar:
                return (Path.GetFullPath(dest), UnpackOther(input, dest));
            case TriageInputKind.Folder:
            {
                var root = input.TrimEnd('\\', '/');
                var files = new List<string>();
                var opts = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
                foreach (var f in Directory.EnumerateFiles(root, "*", opts))
                    files.Add(Ba2Names.Normalize(Path.GetRelativePath(root, f)));
                files.Sort(StringComparer.Ordinal);
                return (root, files);
            }
            default:
            {
                var dir = Path.GetDirectoryName(input)!;
                var name = Path.GetFileName(input);
                var stem = Path.GetFileNameWithoutExtension(input);
                var files = new List<string> { Ba2Names.Normalize(name) };
                foreach (var b in Directory.EnumerateFiles(dir, "*.ba2", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, AttributesToSkip = 0 }))
                {
                    var bn = Path.GetFileName(b);
                    if (bn.StartsWith(stem + " - ", StringComparison.OrdinalIgnoreCase)) files.Add(Ba2Names.Normalize(bn));
                }
                files.Sort(StringComparer.Ordinal);
                return (dir, files);
            }
        }
    }

    /// <summary>Unpacks a .7z or .rar into dest with the same guards as ReleaseCommand.Unpack: no entry outside dest, no
    /// name twice, the same size cap, and a running count so an entry can't unpack past what it declares. Encrypted
    /// archives are refused (triage never asks for a password).</summary>
    public static List<string> UnpackOther(string archive, string dest)
    {
        // How an error while unpacking is told (decided 2026-11-19 after eight review rounds pulled both ways):
        //  - a guard's own refusal (a marked InvalidDataException) and a file-system error pass through as they are;
        //  - an encryption error means a password;
        //  - any other non-fatal error means the archive can't be read. A damaged archive makes the decoders throw
        //    almost anything (IndexOutOfRange, NullReference, InvalidOperation ...), so no list of types is complete. A
        //    bug of ours can then read as "can't be read", but every case is logged once in full, and every one ends
        //    the run with exit 2 and no report.
        try { return UnpackOtherCore(archive, dest); }
        catch (Exception e) when (IsRefusal(e) || IsFileSystem(e))
        {
            Log.Write($"triage: unpacking {archive} stopped: {e}");
            throw;
        }
        catch (Exception e) when (Fatal.Recoverable(e))
        {
            Log.Write($"triage: unpacking {archive} stopped: {e}");
            if (e is SharpCompress.Common.CryptographicException or System.Security.Cryptography.CryptographicException)
                throw new InvalidDataException($"it is password-protected or its encrypted data is damaged; triage doesn't open encrypted archives ({e.Message})", e);
            // the type is in the message, so a bug of ours (a NullReferenceException) isn't read as plain damage
            throw new InvalidDataException($"it can't be read as an archive ({e.GetType().Name}: {e.Message})", e);
        }
    }

    /// <summary>A file-system error, not a damaged archive: a file or folder not found, a path too long, no permission,
    /// the archive locked by another program, or the disk full. It can be about the archive file, the temp folder it
    /// is unpacked into, or an entry's path there; the caller names it as it is.</summary>
    public static bool IsFileSystem(Exception e) =>
        e is FileNotFoundException or DirectoryNotFoundException or PathTooLongException || IsPcProblem(e);

    /// <summary>A problem of the PC, not of the archive's contents: no permission, a missing drive, the file locked by
    /// another program, or the disk full (Windows codes, and on Linux and macOS the errno that .NET puts in HResult:
    /// ENOSPC 28, EDQUOT 122 or 69). An unpacker lets these through as themselves; anything else about an entry (a
    /// path too long, a missing folder) is the archive's (sixteenth review).</summary>
    public static bool IsPcProblem(Exception e) =>
        e is UnauthorizedAccessException or DriveNotFoundException
        || (e is IOException and not (FileNotFoundException or DirectoryNotFoundException or PathTooLongException or EndOfStreamException) &&
            (OperatingSystem.IsWindows()
                ? (e.HResult & 0xFFFF) is 32 or 33 or 39 or 112 or 59 or 64 or 19 or 21   // sharing, lock; disk full; network dropped; write-protected, not ready
                : e.HResult is 28 or 30 or 5 || e.HResult == (OperatingSystem.IsMacOS() ? 69 : 122) || e.HResult == (OperatingSystem.IsMacOS() ? 35 : 11)));   // ENOSPC, EROFS, EIO; EDQUOT and EAGAIN (a lock) per OS

    const string GuardMark = "sf-mod-check unpack guard";

    /// <summary>A guard of the unpack refusing the archive (an entry outside the folder, a name twice, the size cap):
    /// an InvalidDataException (a sealed type) marked in its Data, so UnpackOther passes it through as it is.</summary>
    static InvalidDataException Refused(string message)
    {
        var e = new InvalidDataException(message);
        e.Data[GuardMark] = true;
        return e;
    }

    /// <summary>True for an exception one of the unpack's own guards threw.</summary>
    public static bool IsRefusal(Exception e) => e is InvalidDataException && e.Data.Contains(GuardMark);

    static List<string> UnpackOtherCore(string archive, string dest)
    {
        Directory.CreateDirectory(dest);
        var root = Path.GetFullPath(dest).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> clashFiles = new(StringComparer.Ordinal), clashDirs = new(StringComparer.Ordinal);
        long max = ReleaseCommand.MaxUnpackedBytes;
        using var a = ArchiveFactory.OpenArchive(archive, new SharpCompress.Readers.ReaderOptions());
        var entries = a.Entries.Where(e => !e.IsDirectory).ToList();
        if (a.IsEncrypted || entries.Any(e => e.IsEncrypted))
            throw Refused("it is password-protected; triage doesn't open encrypted archives");
        long total = entries.Sum(e => Math.Max(0, e.Size)), written = 0;
        if (total > max)
            throw Refused($"it unpacks to {total / (1024 * 1024 * 1024.0):F1} GB, more than the {max / (1024 * 1024 * 1024)} GB this check allows");
        var buffer = new byte[81920];
        // A solid archive or a .7z is read in one pass (entry by entry would decompress it again for every file);
        // SharpCompress allows that pass only for those, so any other archive (a normal .rar) is read entry by entry
        // (0.8.0 code review). Both write through Write, with the same guards.
        if (a.IsSolid || a.Type == ArchiveType.SevenZip)
        {
            using var reader = a.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                if (reader.Entry.IsDirectory) continue;
                WriteOne(reader.Entry, reader.OpenEntryStream);
            }
        }
        else
            foreach (var e in entries)
                WriteOne(e, e.OpenEntryStream);
        files.Sort(StringComparer.Ordinal);
        return files;

        // The first error in an entry (a guard's refusal, a full disk, a decoder error) is what the user sees, even when
        // closing the entry's stream afterwards fails too (SharpCompress reads the rest of the entry on close).
        void WriteOne(IEntry e, Func<Stream> open)
        {
            Exception? first = null;
            try
            {
                using var input = open();
                try { Write(e, input); }
                catch (Exception x) { first = x; }   // fatal ones too: kept over a later close error (eleventh review)
            }
            catch (Exception x) when (first != null)
            {
                // the first error wins, unless the close error is fatal and the first one wasn't. After a fatal first
                // error nothing is built here (a log line could itself run out of memory and replace it).
                // (after a fatal first error the close error is dropped unlogged, on purpose)
                if (Fatal.Recoverable(first))
                {
                    Log.Write($"triage: after an error in {e.Key} ({first.GetType().Name}: {first.Message}), closing its stream failed too: {x}");
                    if (!Fatal.Recoverable(x)) throw;
                }
            }
            if (first != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(first);
        }

        void Write(IEntry e, Stream input)
        {
            var key = e.Key ?? "";
            var name = key.Replace('\\', '/');
            // the path checks first, for every entry (twelfth review: a "../../x/" folder record was skipped, not refused)
            if (name.Length == 0 || name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(x => x == ".."))
                throw Refused($"the entry \"{key}\" points outside the archive's own folders");
            if (ReleaseCommand.ChangedByWindows(name))
                throw Refused($"the entry \"{key}\" has a name Windows changes (a \".\" part, or one ending in a dot or space)");
            // A folder record some tools write as a plain entry ("Data/"): nothing to write (tenth review: it was
            // refused as a clash with the folder its own files made). Only an empty one: its stream is read, not its
            // declared size (twelfth review), so an entry named like a folder can't hide data.
            if (name.EndsWith('/'))
            {
                if (input.ReadByte() >= 0) throw Refused($"the entry \"{key}\" is named like a folder but holds data");
                return;
            }
            var norm = Ba2Names.Normalize(name);
            if (!seen.Add(norm)) throw Refused($"the archive holds \"{key}\" twice (names differ only in case or slashes)");
            if (ReleaseCommand.NameClash(norm, clashFiles, clashDirs) is { } clash) throw Refused($"the entry \"{key}\" {clash}");
            var target = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw Refused($"the entry \"{key}\" points outside the archive's own folders");
            // a file and a folder of the same name ("Data" and "Data/b.txt") is a broken archive, not a permission problem
            if (Directory.Exists(target))
                throw Refused($"the entry \"{key}\" has the same name as a folder in the archive");
            for (var d = Path.GetDirectoryName(target); d != null && d.Length >= root.Length; d = Path.GetDirectoryName(d))
                if (File.Exists(d)) throw Refused($"the entry \"{key}\" sits in a folder that the archive also holds as a file");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
            {
                long wrote = 0;
                int n;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    wrote += n; written += n;
                    if ((e.Size >= 0 && wrote > e.Size) || written > max)
                        throw Refused($"the entry \"{key}\" unpacks to more than the archive says it holds");
                    output.Write(buffer, 0, n);
                }
                if (e.Size >= 0 && wrote != e.Size)
                    throw Refused($"the entry \"{key}\" unpacked to a different size than the archive says");
            }
            files.Add(norm);
        }
    }
}
