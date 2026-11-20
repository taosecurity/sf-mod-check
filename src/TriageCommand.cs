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
    public bool HasGame => File.Exists(Path.Combine(DataDir, "Starfield.esm"));
    /// <summary>A temp folder sections may write scratch files into (removed at the end).</summary>
    public required string Work { get; init; }
    public required TriageReport Report { get; init; }

    public string Full(string rel) => Path.Combine(Root, rel.Replace('\\', Path.DirectorySeparatorChar));
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
        var hasGame = File.Exists(Path.Combine(o.DataDir, "Starfield.esm"));
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
            return CantRun($"{Path.GetFileName(input)}: no such file", $"can't triage {input}: no such file or folder. Check the path (a path with spaces needs quotes around it).");
        else if (KindOf(input) is { } k) kind = k;
        else
            return CantRun($"{Path.GetFileName(input)}: not a mod download or plugin",
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
            catch (Exception e) when (e is InvalidDataException or InvalidFormatException or ArchiveException or NotSupportedException or IOException or UnauthorizedAccessException)
            {
                return CantRun($"{Path.GetFileName(input)} can't be read", $"can't read {input}: {e.Message}");
            }
            Log.Write($"mod files: {files.Count} under {root}");

            var report = new TriageReport
            {
                Tool = $"sf-mod-check {version}",
                InputName = Path.GetFileName(input.TrimEnd('\\', '/')),
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
            if (File.Exists(Path.Combine(d, "Starfield.esm"))) return Path.GetFullPath(d);
        return null;
    }

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
            if (!File.Exists(Path.Combine(data, "Starfield.esm"))) return false;
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
                foreach (var b in Directory.EnumerateFiles(dir, "*.ba2"))
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
        // SharpCompress's own refusals become "can't be read"; the same exception from our code stays a real error
        // (0.8.0 code review)
        // Our own code in there throws only InvalidDataException (its guards). Everything SharpCompress throws derives from
        // SharpCompressException; its CryptographicException means headers encrypted with a password (fourth code
        // review: not .NET's type of that name). An InvalidOperationException counts only when it comes from
        // SharpCompress or a .NET method it calls on a damaged file (third review); one from our own code stays an error.
        // Fifth review: decided by where the error was raised, not by its type. Anything raised while SharpCompress code
        // was running (its own exceptions, and .NET ones it hits on a damaged file: InvalidOperationException,
        // ObjectDisposedException, .NET's CryptographicException from AES) means the archive can't be read; an
        // encryption error means a password. Our own guards' InvalidDataException passes through unchanged.
        try { return UnpackOtherCore(archive, dest); }
        catch (Exception e) when (e is not InvalidDataException && Fatal.Recoverable(e) && (e is SharpCompressException || RaisedIn(e, "SharpCompress")))
        {
            Log.Write($"triage: {archive}: {e}");
            if (e is SharpCompress.Common.CryptographicException or System.Security.Cryptography.CryptographicException)
                throw new InvalidDataException($"it is password-protected or its encrypted data is damaged; triage doesn't open encrypted archives ({e.Message})", e);
            throw new InvalidDataException($"it can't be read as an archive ({e.Message})", e);
        }
    }

    /// <summary>True when a frame of the exception's stack belongs to the named assembly (the error came from inside it,
    /// even when a .NET method it called raised it).</summary>
    static bool RaisedIn(Exception e, string assembly)
    {
        try
        {
            return new System.Diagnostics.StackTrace(e, false).GetFrames()
                .Any(f => f.GetMethod()?.DeclaringType?.Assembly.GetName().Name == assembly);
        }
        catch (Exception) { return false; }
    }

    static List<string> UnpackOtherCore(string archive, string dest)
    {
        Directory.CreateDirectory(dest);
        var root = Path.GetFullPath(dest).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long max = ReleaseCommand.MaxUnpackedBytes;
        using var a = ArchiveFactory.OpenArchive(archive, new SharpCompress.Readers.ReaderOptions());
        var entries = a.Entries.Where(e => !e.IsDirectory).ToList();
        if (a.IsEncrypted || entries.Any(e => e.IsEncrypted))
            throw new InvalidDataException("it is password-protected; triage doesn't open encrypted archives");
        long total = entries.Sum(e => Math.Max(0, e.Size)), written = 0;
        if (total > max)
            throw new InvalidDataException($"it unpacks to {total / (1024 * 1024 * 1024.0):F1} GB, more than the {max / (1024 * 1024 * 1024)} GB this check allows");
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
                using var input = reader.OpenEntryStream();
                Write(reader.Entry, input);
            }
        }
        else
            foreach (var e in entries)
            {
                using var input = e.OpenEntryStream();
                Write(e, input);
            }
        files.Sort(StringComparer.Ordinal);
        return files;

        void Write(IEntry e, Stream input)
        {
            var key = e.Key ?? "";
            var name = key.Replace('\\', '/');
            if (name.Length == 0 || name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(x => x == ".."))
                throw new InvalidDataException($"the entry \"{key}\" points outside the archive's own folders");
            var norm = Ba2Names.Normalize(name);
            if (!seen.Add(norm)) throw new InvalidDataException($"the archive holds \"{key}\" twice (names differ only in case or slashes)");
            var target = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"the entry \"{key}\" points outside the archive's own folders");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
            {
                long wrote = 0;
                int n;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    wrote += n; written += n;
                    if ((e.Size >= 0 && wrote > e.Size) || written > max)
                        throw new InvalidDataException($"the entry \"{key}\" unpacks to more than the archive says it holds");
                    output.Write(buffer, 0, n);
                }
                if (e.Size >= 0 && wrote != e.Size)
                    throw new InvalidDataException($"the entry \"{key}\" unpacked to a different size than the archive says");
            }
            files.Add(norm);
        }
    }
}
