// Command "release" (0.7.0): one OK or FAIL on a mod's release zip(s).
//   sfmodcheck release <zip>... [--data <Data folder>] [--masters-dir <folder>]... [--master-archive <ba2>]... [--dmmdeps <exe>]
//                      [--papyrus <exe> --ck-sources <ContentResources.zip>] [--profile basic|house]
//                      [--quiet] [--verbose] [--log <file>]
// Each zip is unpacked into a temp folder that is always removed; nothing else is written. Zips inside a game install
// are refused. Output: PROBLEM / REVIEW / WARNING / NOTE lines, then "RESULT: OK|FAIL (n problems, n to review, n
// warnings)". Exit 0 = OK, 1 = FAIL, 2 = can't run.
// Profiles: basic (default) fails only on what breaks the mod in game; a house rule's problem (RuleKind.House: one
// plugin per zip, script sources shipped, root .md names, private files, BlueprintShips master) prints as a WARNING.
// house fails on those too (what our own builds use).
// The steps live in ReleaseRules.cs (zip layout, names, voice archives), ReleaseNeeds.cs (scripts and files the plugin
// needs, DMMDeps, the plugin check), GapCheck.cs (data Mutagen 0.54.4 loses, missing ONAM) and ReleaseLint.cs
// (optional Papyrus lint).
using System.IO.Compression;

namespace SfModCheck;

public enum Level { Problem, Review, Warning, Note }

/// <summary>Engine: breaks the mod for any player. House: a packaging rule of ours (a WARNING in the basic profile).</summary>
public enum RuleKind { Engine, House }

/// <summary>One line of the report. Rule is a short tag such as "plugin-name" or "dmmdeps".</summary>
public sealed record ReleaseFinding(Level Level, string Rule, string Text, RuleKind Kind = RuleKind.Engine);

public sealed class ReleaseOptions
{
    public List<string> Zips { get; } = new();
    public string DataDir { get; set; } = Cli.SteamData;
    public bool DataGiven { get; set; }
    public List<string> MastersDirs { get; } = new();
    /// <summary>Archives of the mod this one patches: the scripts and models in them count as present.</summary>
    public List<string> MasterArchives { get; } = new();
    public string? DmmDeps { get; set; }
    public string? Papyrus { get; set; }
    public string? CkSources { get; set; }
    public string Profile { get; set; } = "basic";
}

/// <summary>One unpacked zip.</summary>
public sealed class ReleasePackage
{
    public required string ZipPath { get; init; }
    /// <summary>The folder the zip was unpacked into (maps to the game's Data folder).</summary>
    public required string Root { get; init; }
    /// <summary>Every file in the zip, as Ba2Names.Normalize paths relative to Root ("scripts\foo\bar.pex").</summary>
    public required List<string> Files { get; init; }
    /// <summary>Full paths of the plugins (.esm .esp .esl) in the zip, at any depth.</summary>
    public required List<string> Plugins { get; init; }
    /// <summary>Full paths of the archives (.ba2) in the zip, at any depth.</summary>
    public required List<string> Archives { get; init; }
    public string Name => Path.GetFileName(ZipPath);
}

public sealed class ReleaseContext
{
    public required ReleaseOptions Options { get; init; }
    public required List<ReleasePackage> Packages { get; init; }
    /// <summary>A temp folder steps may write scratch files into (removed at the end).</summary>
    public required string Work { get; init; }
    public string DataDir => Options.DataDir;
    public IEnumerable<string> AllPlugins => Packages.SelectMany(p => p.Plugins);
}

public static class ReleaseCommand
{
    public const string Usage =
        "usage: sfmodcheck release <zip>... [--data <Data folder>] [--masters-dir <folder>]... [--master-archive <ba2>]... [--dmmdeps <exe>]\n" +
        "                          [--papyrus <exe> --ck-sources <ContentResources.zip>] [--profile basic|house]\n" +
        "                          [--quiet] [--verbose] [--log <file>]";

    static readonly string[] PluginExts = [".esm", ".esp", ".esl"];

    /// <summary>The most a zip may unpack to (a damaged or crafted zip could fill the disk). 12 GB is far above any release.</summary>
    public static long MaxUnpackedBytes = 12L * 1024 * 1024 * 1024;

    /// <summary>The steps, in report order. Tests can swap them.</summary>
    public static List<Func<ReleaseContext, IEnumerable<ReleaseFinding>>> Steps { get; } =
        [ReleaseRules.Run, ReleaseNeeds.Run, GapCheck.Run, ReleaseLint.Run];

    public static int Run(string[] a, string version, string byline)
    {
        try { return RunLogged(a, version, byline); }
        catch (Exception e)
        {
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

    static int CantRun(string why, string detail)
    {
        Console.Error.WriteLine(detail);
        Log.Write($"stopped: {detail}");
        Console.WriteLine($"RESULT: CANNOT CHECK ({why})");
        return 2;
    }

    static int RunLogged(string[] a, string version, string byline)
    {
        var o = new ReleaseOptions();
        bool quiet = false, verbose = false;
        string? logFile = null;
        string? Value(ref int i, string what) => Cli.Value(a, ref i, what, Usage);
        for (int i = 0; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "--data":
                    if (Value(ref i, "the game's Data folder") is not { } d) return 2;
                    o.DataDir = d; o.DataGiven = true; break;
                case "--masters-dir":
                    if (Value(ref i, "a folder") is not { } m) return 2;
                    o.MastersDirs.Add(m); break;
                case "--master-archive":
                    if (Value(ref i, "the path of a .ba2") is not { } ma) return 2;
                    o.MasterArchives.Add(ma); break;
                case "--dmmdeps":
                    if (Value(ref i, "the path of dmmdeps-fat.exe") is not { } dm) return 2;
                    o.DmmDeps = dm; break;
                case "--papyrus":
                    if (Value(ref i, "the path of papyrus.exe (papyrus-tools)") is not { } pa) return 2;
                    o.Papyrus = pa; break;
                case "--ck-sources":
                    if (Value(ref i, "the Creation Kit's Tools\\ContentResources.zip") is not { } ck) return 2;
                    o.CkSources = ck; break;
                case "--profile":
                    if (Value(ref i, "basic or house") is not { } pr) return 2;
                    if (pr is not ("basic" or "house"))
                    {
                        Console.Error.WriteLine($"--profile is basic (only problems that break the mod in game fail) or house (our packaging rules fail too), not \"{pr}\"");
                        return 2;
                    }
                    o.Profile = pr; break;
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
                        Console.Error.WriteLine("a zip name is empty: give the path of the mod's release zip, for example: sfmodcheck release MyMod-1.0.0.zip");
                        return 2;
                    }
                    o.Zips.Add(a[i]);
                    break;
            }
        }
        if (o.Zips.Count == 0)
        {
            Console.Error.WriteLine(Usage);
            Console.Error.WriteLine("To check a mod before you upload it, run: sfmodcheck release MyMod-1.0.0.zip");
            return 2;
        }
        if (!quiet) Console.Error.WriteLine(byline);
        if (verbose) Log.ToStderr();
        if (logFile != null && Log.ToFile(logFile) is { } logError) return CantRun("the log file can't be written", logError);
        Log.Write($"sfmodcheck {version} release on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, .NET {Environment.Version}");
        Log.Write($"arguments: {string.Join(" ", a.Select(x => x.Contains(' ') ? $"\"{x}\"" : x))}");
        Log.Write($"profile: {o.Profile}");

        // ---- inputs ----
        foreach (var z in o.Zips)
        {
            var full = Path.GetFullPath(z);
            if (!File.Exists(full))
                return CantRun($"{Path.GetFileName(full)}: no such file",
                    $"can't check {full}: {(Directory.Exists(full) ? "that is a folder, not a zip" : "no such file")}. Check the path (a path with spaces needs quotes around it).");
            if (InsideGameInstall(full, o.DataDir))
                return CantRun($"{Path.GetFileName(full)} is inside the game install",
                    $"won't check {full}: it is inside the game's install folder. Check the zip you are about to upload, from your build or download folder.");
            Log.Write($"zip: {Log.Describe(full)}");
        }
        if (!Directory.Exists(o.DataDir))
            return CantRun("no Data folder",
                o.DataGiven ? $"can't use the Data folder {Path.GetFullPath(o.DataDir)}: it doesn't exist. Give --data the game's Data folder, the one that holds Starfield.esm."
                            : $"can't find the game's Data folder: Steam's usual folder {o.DataDir} doesn't exist. Run again with --data \"<the game's Data folder>\" (Game Pass: ...\\Starfield\\Content\\Data).");
        if (!File.Exists(Path.Combine(o.DataDir, "Starfield.esm")))
            Console.Error.WriteLine($"note: no Starfield.esm in {Path.GetFullPath(o.DataDir)}; give --data the game's Data folder if it is somewhere else.");
        foreach (var md in o.MastersDirs)
            if (!Directory.Exists(md))
                return CantRun($"the --masters-dir folder {Path.GetFullPath(md)} doesn't exist",
                    $"can't use --masters-dir {Path.GetFullPath(md)}: that folder doesn't exist. Check the path (a path with spaces needs quotes around it).");
        foreach (var (opt, path) in new[] { ("--dmmdeps", o.DmmDeps), ("--papyrus", o.Papyrus), ("--ck-sources", o.CkSources) }
                     .Concat(o.MasterArchives.Select(m => ("--master-archive", (string?)m))))
            if (path != null && !File.Exists(path))
                return CantRun($"{opt} file not found", $"can't use {opt} {Path.GetFullPath(path)}: no such file.");
        if ((o.Papyrus == null) != (o.CkSources == null))
            Console.Error.WriteLine("note: the Papyrus lint needs both --papyrus and --ck-sources; it is skipped.");

        // ---- unpack ----
        var work = Directory.CreateTempSubdirectory("sfmodcheck-release-").FullName;
        Log.Write($"work folder: {work}");
        try
        {
            var packages = new List<ReleasePackage>();
            for (int i = 0; i < o.Zips.Count; i++)
            {
                var full = Path.GetFullPath(o.Zips[i]);
                try { packages.Add(Unpack(full, Path.Combine(work, $"zip{i}"))); }
                catch (InvalidDataException e)
                {
                    return CantRun($"{Path.GetFileName(full)} can't be unpacked", $"can't unpack {full}: {e.Message}");
                }
                Log.Write($"unpacked {packages[^1].Name}: {packages[^1].Files.Count} files, {packages[^1].Plugins.Count} plugins, {packages[^1].Archives.Count} archives");
            }
            var ctx = new ReleaseContext { Options = o, Packages = packages, Work = work };

            var findings = new List<ReleaseFinding>();
            foreach (var step in Steps)
                findings.AddRange(step(ctx));
            findings = DedupeUnreadable(findings);

            // ---- report ----
            var shown = findings.Select(f => Apply(f, o.Profile)).ToList();
            int problems = shown.Count(f => f.Level == Level.Problem), review = shown.Count(f => f.Level == Level.Review),
                warnings = shown.Count(f => f.Level == Level.Warning);
            if (!quiet)
                Console.WriteLine($"sfmodcheck {version} release ({o.Profile} profile): {string.Join(", ", packages.Select(p => p.Name))} " +
                                  $"({packages.Sum(p => p.Files.Count)} files, {packages.Sum(p => p.Plugins.Count)} plugins, {packages.Sum(p => p.Archives.Count)} archives)");
            foreach (var lvl in new[] { Level.Problem, Level.Review, Level.Warning, Level.Note })
                foreach (var f in shown.Where(f => f.Level == lvl))
                    Console.WriteLine($"{Label(lvl)} [{f.Rule}] {f.Text}");
            Console.WriteLine($"RESULT: {(problems > 0 ? "FAIL" : "OK")} ({problems} problems, {review} to review, {warnings} warnings)");
            Log.Write($"result: {(problems > 0 ? "FAIL" : "OK")}, exit code {(problems > 0 ? 1 : 0)}");
            return problems > 0 ? 1 : 0;
        }
        finally
        {
            try { Directory.Delete(work, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Write($"couldn't remove the work folder {work}: {e.Message}"); }
        }
    }

    static readonly System.Text.RegularExpressions.Regex UnreadableArchive = new(@"^archive (.+?) could not be read \(", System.Text.RegularExpressions.RegexOptions.Singleline);

    /// <summary>The last part of a path written with either kind of slash (the same on every system).</summary>
    static string LastName(string path) => path[(path.LastIndexOfAny(['\\', '/']) + 1)..];

    /// <summary>An archive that can't be read is reported by the zip rules (ba2-unreadable) and again by the needs step
    /// (script-missing). Keep the first and drop the repeat, so one broken archive is one PROBLEM.</summary>
    public static List<ReleaseFinding> DedupeUnreadable(List<ReleaseFinding> findings)
    {
        // the ba2-unreadable text starts "<zip>:<archive path>: can't read ..."; keep the archive's file name
        var rules = findings.Where(f => f.Rule == "ba2-unreadable")
            .Select(f => System.Text.RegularExpressions.Regex.Match(f.Text, @"^(.+?): can't read the archive"))
            .Where(m => m.Success)
            // the text before ": can't read" is "<zip>:<archive path>"; an unpacked path never holds a colon
            .Select(m => LastName(m.Groups[1].Value[(m.Groups[1].Value.LastIndexOf(':') + 1)..])).ToList();
        if (rules.Count == 0) return findings;
        return findings.Where(f =>
        {
            if (f.Rule != "script-missing") return true;
            var m = UnreadableArchive.Match(f.Text);
            if (!m.Success) return true;
            var file = LastName(m.Groups[1].Value.Trim());
            return !rules.Any(r => r.Equals(file, StringComparison.OrdinalIgnoreCase));
        }).ToList();
    }

    /// <summary>In the basic profile a house rule's problem is shown as a WARNING, marked so.</summary>
    public static ReleaseFinding Apply(ReleaseFinding f, string profile) =>
        profile == "basic" && f.Kind == RuleKind.House && f.Level == Level.Problem
            ? f with { Level = Level.Warning, Text = f.Text + " (a failure with --profile house)" }
            : f;

    public static string Label(Level l) => l switch
    {
        Level.Problem => "PROBLEM ",
        Level.Review => "REVIEW  ",
        Level.Warning => "WARNING ",
        _ => "NOTE    ",
    };

    /// <summary>True if the path is inside the game's install (the folder above Data).</summary>
    static bool InsideGameInstall(string full, string dataDir)
    {
        try
        {
            var data = Path.GetFullPath(dataDir).TrimEnd('\\', '/');
            // Only a Data folder that holds Starfield.esm is known to be the game's: any other folder named Data could be
            // the user's own (the run prints a note when Starfield.esm isn't there).
            if (!File.Exists(Path.Combine(data, "Starfield.esm"))) return false;
            var install = Path.GetDirectoryName(data);
            if (install == null) return false;
            return full.StartsWith(install.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>Unpacks a zip into dest. Refuses entries that would land outside dest (".." or a rooted path) and
    /// duplicate names, rather than skipping them, so a damaged or crafted zip never gets an OK.</summary>
    public static ReleasePackage Unpack(string zip, string dest)
    {
        Directory.CreateDirectory(dest);
        var root = Path.GetFullPath(dest).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var files = new List<string>();
        var plugins = new List<string>();
        var archives = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using (var z = ZipFile.OpenRead(zip))
        {
            long total = z.Entries.Sum(e => e.Length), written = 0;
            if (total > MaxUnpackedBytes)
                throw new InvalidDataException($"it unpacks to {total / (1024 * 1024 * 1024.0):F1} GB, more than the {MaxUnpackedBytes / (1024 * 1024 * 1024)} GB this check allows; a release zip is far smaller than that");
            foreach (var e in z.Entries)
            {
                var name = e.FullName.Replace('\\', '/');
                if (name.EndsWith('/')) continue;   // a folder entry
                if (name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(p => p == ".."))
                    throw new InvalidDataException($"the entry \"{e.FullName}\" points outside the zip's own folders");
                var norm = Ba2Names.Normalize(name);
                if (!seen.Add(norm)) throw new InvalidDataException($"the zip holds \"{e.FullName}\" twice (names differ only in case or slashes)");
                var target = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"the entry \"{e.FullName}\" points outside the zip's own folders");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                // copy with a running count, so an entry that inflates past what the zip declares (or past the cap in
                // total) stops being written at once instead of filling the disk first
                using (var input = e.Open())
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
                {
                    var buffer = new byte[81920];
                    long wrote = 0, n;
                    while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        wrote += n; written += n;
                        if (wrote > e.Length || written > MaxUnpackedBytes)
                            throw new InvalidDataException($"the entry \"{e.FullName}\" unpacks to more than the zip says it holds");
                        output.Write(buffer, 0, (int)n);
                    }
                    if (wrote != e.Length)
                        throw new InvalidDataException($"the entry \"{e.FullName}\" unpacked to a different size than the zip says");
                }
                files.Add(norm);
                // full paths keep the zip's own spelling (Linux file names are case-sensitive)
                if (PluginExts.Contains(Path.GetExtension(norm))) plugins.Add(target);
                else if (norm.EndsWith(".ba2", StringComparison.Ordinal)) archives.Add(target);
            }
        }
        files.Sort(StringComparer.Ordinal);
        plugins.Sort(StringComparer.OrdinalIgnoreCase);
        archives.Sort(StringComparer.OrdinalIgnoreCase);
        return new ReleasePackage
        {
            ZipPath = zip,
            Root = root.TrimEnd(Path.DirectorySeparatorChar),
            Files = files,
            Plugins = plugins,
            Archives = archives,
        };
    }
}
