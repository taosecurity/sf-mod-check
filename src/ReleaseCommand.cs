// Command "release" (0.7.0): one OK or FAIL on a mod's release zip(s).
//   sfmodcheck release <zip>... [--data <Data folder>] [--masters-dir <folder>]... [--master-archive <ba2>]... [--dmmdeps <exe>]
//                      [--papyrus <exe> --ck-sources <ContentResources.zip>] [--psc <folder>]... [--psc-import <folder>]...
//                      [--profile basic|house|creations] [--creations-master <plugin>]... [--tested <zip|ba2>]... [--stage <folder>]
//                      [--accept <rule>:<plugin>:<detail>]... [--accept-file <file>]...   (0.15.2: ReleaseAccept.cs)
//                      [--quiet] [--verbose] [--log <file>]
// Each zip is unpacked into a temp folder that is always removed; nothing else is written. Zips inside a game install
// are refused. Output: PROBLEM / REVIEW / WARNING / NOTE lines, then "RESULT: OK|FAIL (n problems, n to review, n
// warnings)". Exit 0 = OK, 1 = FAIL, 2 = can't run.
// Profiles: basic (default) fails only on what breaks the mod in game; a house rule's problem (RuleKind.House: one
// plugin per zip, script sources shipped, root .md names, private files, BlueprintShips master) prints as a WARNING.
// house fails on those too (what our own builds use).
// The steps live in ReleaseRules.cs (zip layout, names, voice archives), ReleaseVoice.cs (voice files for dialogue, 0.18.0), ReleaseNeeds.cs (scripts and files the plugin
// needs, DMMDeps, the plugin check), GapCheck.cs (data Mutagen 0.54.4 loses, missing ONAM) and ReleaseLint.cs
// (optional Papyrus lint).
using System.IO.Compression;

namespace SfModCheck;

public enum Level { Problem, Review, Warning, Note }

/// <summary>Engine: breaks the mod for any player. House: a packaging rule of ours (a WARNING in the basic profile).</summary>
public enum RuleKind { Engine, House }

/// <summary>One line of the report. Rule is a short tag such as "plugin-name" or "dmmdeps".</summary>
public sealed record ReleaseFinding(Level Level, string Rule, string Text, RuleKind Kind = RuleKind.Engine)
{
    /// <summary>The plugin's file name, for a finding about one plugin's missing file (an --accept entry names it; 0.15.2).</summary>
    public string? Plugin { get; init; }
    /// <summary>The finding's detail for --accept: the script name (script-missing) or the missing file's path.</summary>
    public string? Subject { get; init; }
    /// <summary>The --accept entry that accepted this finding: it prints as ACCEPTED and doesn't count (0.15.2).</summary>
    public AcceptEntry? Accepted { get; init; }
}

public sealed class ReleaseOptions
{
    public List<string> Zips { get; } = new();
    public string DataDir { get; set; } = Cli.SteamData;
    public bool DataGiven { get; set; }
    public List<string> MastersDirs { get; } = new();
    /// <summary>Archives of the mod this one patches: the scripts, models, materials, textures and sounds in them count as present.</summary>
    public List<string> MasterArchives { get; } = new();
    public string? DmmDeps { get; set; }
    public string? Papyrus { get; set; }
    public string? CkSources { get; set; }
    public string Profile { get; set; } = "basic";
    /// <summary>--creations-master: other authors' plugins the user has seen on Creations (creations profile).</summary>
    public List<string> CreationsMasters { get; } = new();
    /// <summary>--tested: zips or .ba2 archives of the build that was tested in game (ReleaseTested).</summary>
    public List<string> Tested { get; } = new();
    /// <summary>--stage: the folder the release was built from, checked against its archives (ReleaseTested).</summary>
    public string? Stage { get; set; }
    /// <summary>--psc: script source roots linted instead of each zip's Scripts\Source (ReleaseLint).</summary>
    public List<string> Psc { get; } = new();
    /// <summary>--psc-import: extra import folders for the lint, after the vanilla sources (ReleaseLint).</summary>
    public List<string> PscImport { get; } = new();
    /// <summary>--size-total (creations profile): every input of the run counts as one Creation for the 2 GB rule.</summary>
    public bool SizeTotal { get; set; }
    /// <summary>--accept and --accept-file entries: known findings that print as ACCEPTED and don't count (ReleaseAccept).</summary>
    public List<AcceptEntry> Accept { get; } = new();
}

/// <summary>One input: an unpacked zip, a folder used in place, or the run's loose .ba2 and plugin files.</summary>
public sealed class ReleasePackage
{
    /// <summary>The zip's path; for a folder, the folder's path; for loose files, "loose files".</summary>
    public required string ZipPath { get; init; }
    /// <summary>False for a folder or the loose files: the zip-only rules (one plugin per zip, root .md names) skip it.</summary>
    public bool IsZip { get; init; } = true;
    /// <summary>The folder the zip was unpacked into (maps to the game's Data folder).</summary>
    public required string Root { get; init; }
    /// <summary>Every file in the zip, as Ba2Names.Normalize paths relative to Root ("scripts\foo\bar.pex").</summary>
    public required List<string> Files { get; init; }
    /// <summary>Full paths of the plugins (.esm .esp .esl) in the zip, at any depth.</summary>
    public required List<string> Plugins { get; init; }
    /// <summary>Full paths of the archives (.ba2) in the zip, at any depth.</summary>
    public required List<string> Archives { get; init; }
    public string Name => Path.GetFileName(ZipPath.TrimEnd('\\', '/'));
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
        "usage: sf-mod-check release <zip|folder|ba2|plugin>... [--data <Data folder>] [--masters-dir <folder>]... [--master-archive <ba2>]... [--dmmdeps <exe>]\n" +
        "                          [--papyrus <exe> --ck-sources <ContentResources.zip>] [--psc <folder>]... [--psc-import <folder>]...\n" +
        "                          [--profile basic|house|creations] [--creations-master <plugin>]... [--size-total]\n" +
        "                          [--tested <zip|ba2>]... [--stage <folder>]\n" +
        "                          [--accept <rule>:<plugin>:<detail>]... [--accept-file <file>]...\n" +
        "                          [--quiet] [--verbose] [--log <file>]";

    static readonly string[] PluginExts = [".esm", ".esp", ".esl"];

    /// <summary>The most a zip may unpack to (a damaged or crafted zip could fill the disk). 12 GB is far above any release.</summary>
    public static long MaxUnpackedBytes = 12L * 1024 * 1024 * 1024;

    /// <summary>The most entries (files and folder records) an archive may hold: a crafted archive of millions of empty
    /// files would take very long to unpack and clean up (0.8.0 follow-up). 200,000 is far above any mod (the largest
    /// texture packs hold tens of thousands of files).</summary>
    public static int MaxEntries = 200_000;

    /// <summary>The refusal text for an archive with more than MaxEntries entries, with the fix (code review of 0.9.1).</summary>
    public static string TooManyEntries(long count, string what) =>
        $"it holds {count:N0} entries, more than the {MaxEntries:N0} this check allows; a mod's {what} holds far fewer. " +
        "If it really is one mod, unpack it yourself and run sf-mod-check triage on the unpacked folder; " +
        "if it is your own release, make the archive hold only the mod's files (not a whole Data folder)";

    /// <summary>The steps, in report order. Tests can swap them.</summary>
    public static List<Func<ReleaseContext, IEnumerable<ReleaseFinding>>> Steps { get; } =
        [ReleaseRules.Run, ReleaseVoice.Run, ReleaseNeeds.Run, GapCheck.Run, ReleaseTested.Run, ReleaseLint.Run, ReleaseCreations.Run];

    public static int Run(string[] a, string version, string byline)
    {
        try { return RunLogged(a, version, byline); }
        catch (Exception e)
        {
            Console.WriteLine(Fatal.ResultLine(e, () => $"RESULT: CANNOT CHECK ({e.GetType().Name}: {SafeText.OneLine(e.Message)})"));
            Fatal.AfterResult(() =>
            {
                Console.Error.WriteLine(Log.On ? "The log has the full error." : "For the full error, run again with --log sf-mod-check-log.txt and look at that file.");
                Log.Write($"stopped: {e}");
            });
            return 2;
        }
        finally { Log.Close(); }
    }

    static string Num(int n, string one) => $"{n} {one}{(n == 1 ? "" : "s")}";

    static int CantRun(string why, string detail)
    {
        Console.Error.WriteLine(detail);
        Log.Write($"stopped: {detail}");
        Console.WriteLine($"RESULT: CANNOT CHECK ({SafeText.OneLine(why)})");
        return 2;
    }

    /// <summary>A bad argument: the message is already on stderr; stdout gets the RESULT line, as for the other
    /// commands (0.13.0 bug hunt).</summary>
    static int BadArgs()
    {
        Console.WriteLine("RESULT: CANNOT CHECK (bad arguments; see the message above)");
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
                    if (Value(ref i, "the game's Data folder") is not { } d) return BadArgs();
                    o.DataDir = d; o.DataGiven = true; break;
                case "--masters-dir":
                    if (Value(ref i, "a folder") is not { } m) return BadArgs();
                    o.MastersDirs.Add(m); break;
                case "--master-archive":
                    if (Value(ref i, "the path of a .ba2") is not { } ma) return BadArgs();
                    o.MasterArchives.Add(ma); break;
                case "--dmmdeps":
                    if (Value(ref i, "the path of dmmdeps-fat.exe") is not { } dm) return BadArgs();
                    o.DmmDeps = dm; break;
                case "--papyrus":
                    if (Value(ref i, "the path of papyrus.exe (papyrus-tools)") is not { } pa) return BadArgs();
                    o.Papyrus = pa; break;
                case "--ck-sources":
                    if (Value(ref i, "the Creation Kit's Tools\\ContentResources.zip") is not { } ck) return BadArgs();
                    o.CkSources = ck; break;
                case "--psc":
                    if (Value(ref i, "a script source folder (the one above the namespace folders)") is not { } ps) return BadArgs();
                    o.Psc.Add(ps); break;
                case "--psc-import":
                    if (Value(ref i, "a folder of other scripts' sources to import") is not { } pi) return BadArgs();
                    o.PscImport.Add(pi); break;
                case "--profile":
                    if (Value(ref i, "basic, house or creations") is not { } pr) return BadArgs();
                    if (pr is not ("basic" or "house" or "creations"))
                    {
                        Console.Error.WriteLine($"--profile is basic (only problems that break the mod in game fail), house (our packaging rules fail too) or creations (basic plus Bethesda Creations' rules for PC, Xbox and PlayStation), not \"{pr}\"");
                        return BadArgs();
                    }
                    o.Profile = pr; break;
                case "--creations-master":
                    if (Value(ref i, "a plugin's file name, for example OtherMod.esm") is not { } cm) return BadArgs();
                    o.CreationsMasters.Add(cm); break;
                case "--tested":
                    if (Value(ref i, "the zip or .ba2 of the build you tested") is not { } te) return BadArgs();
                    if (!te.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !te.EndsWith(".ba2", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.Error.WriteLine($"--tested takes the .zip or a .ba2 of the build you tested, not \"{te}\"");
                        return BadArgs();
                    }
                    o.Tested.Add(te); break;
                case "--stage":
                    if (Value(ref i, "the folder the release was built from") is not { } st) return BadArgs();
                    if (o.Stage != null)
                    {
                        Console.Error.WriteLine("--stage is given twice: give the one folder the release was built from");
                        return BadArgs();
                    }
                    o.Stage = st; break;
                case "--size-total": o.SizeTotal = true; break;
                case "--accept":
                    if (Value(ref i, "an entry <rule>:<plugin>:<detail>") is not { } ac) return BadArgs();
                    if (ReleaseAccept.Parse(ac, "--accept", out var acError) is not { } entry) { Console.Error.WriteLine(acError); return BadArgs(); }
                    o.Accept.Add(entry); break;
                case "--accept-file":
                    if (Value(ref i, "a text file of --accept entries (one per line)") is not { } af) return BadArgs();
                    if (ReleaseAccept.ReadFile(af, out var afError) is not { } entries) { Console.Error.WriteLine(afError); return BadArgs(); }
                    o.Accept.AddRange(entries); break;
                case "--log":
                    if (Value(ref i, "a file name") is not { } lf) return BadArgs();
                    logFile = lf; break;
                case "--verbose": verbose = true; break;
                case "--quiet": quiet = true; break;
                case "-h": case "--help": Console.WriteLine(Usage); return 0;   // asked-for help: stdout, exit 0 (0.13.0)
                default:
                    if (a[i].StartsWith("--"))
                    {
                        Console.Error.WriteLine($"unknown option {a[i]}");
                        Console.Error.WriteLine(Usage);
                        return BadArgs();
                    }
                    if (a[i].Trim().Length == 0)
                    {
                        Console.Error.WriteLine("a zip name is empty: give the path of the mod's release zip, for example: sf-mod-check release MyMod-1.0.0.zip");
                        return BadArgs();
                    }
                    o.Zips.Add(a[i]);
                    break;
            }
        }
        if (o.Zips.Count == 0)
        {
            Console.Error.WriteLine(Usage);
            Console.Error.WriteLine("To check a mod before you upload it, run: sf-mod-check release MyMod-1.0.0.zip");
            return BadArgs();
        }
        if (o.SizeTotal && o.Profile != "creations")
        {
            Console.Error.WriteLine("--size-total works only with --profile creations (it counts every input as one Creation for the 2 GB rule); add --profile creations or leave --size-total out");
            return BadArgs();
        }
        if (!quiet) Console.Error.WriteLine(byline);
        if (verbose) Log.ToStderr();
        if (logFile != null && Log.ToFile(logFile) is { } logError) return CantRun("the log file can't be written", logError);
        Log.Write($"sf-mod-check {version} release on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, .NET {Environment.Version}");
        Log.Write($"arguments: {string.Join(" ", a.Select(x => x.Contains(' ') ? $"\"{x}\"" : x))}");
        Log.Write($"profile: {o.Profile}");

        // ---- inputs ----
        // 0.15.0: a folder is used in place, a .ba2 or a plugin goes into the run's "loose files" package, anything
        // else is opened as a zip
        var zips = new List<string>();
        var folders = new List<string>();
        var loose = new List<string>();
        foreach (var z in o.Zips)
        {
            var full = Path.GetFullPath(z);
            bool isDir = Directory.Exists(full);
            if (!isDir && !File.Exists(full))
                return CantRun($"{Path.GetFileName(full)}: no such file",
                    $"can't check {full}: no such file or folder. Give a release zip, a stage folder, a .ba2 or a plugin; a path with spaces needs quotes around it.");
            if (InsideGameInstall(isDir ? full.TrimEnd('\\', '/') + Path.DirectorySeparatorChar : full, o.DataDir))   // the install folder itself too
                return CantRun($"{Path.GetFileName(full.TrimEnd('\\', '/'))} is inside the game install",
                    $"won't check {full}: it is inside the game's install folder. Check the files you are about to upload, from your build or download folder.");
            if (isDir) { folders.Add(full); Log.Write($"folder (used in place, read only): {full}"); }
            else if (IsLoose(full)) { loose.Add(full); Log.Write($"loose file: {Log.Describe(full)}"); }
            else { zips.Add(full); Log.Write($"zip: {Log.Describe(full)}"); }
        }
        var looseNames = loose.GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (looseNames != null)
            return CantRun($"two loose files are named {looseNames.Key}",
                $"can't check {string.Join(" and ", looseNames)}: loose .ba2 and plugin files of one run go into one package, so two of the same name would clash. Check them in separate runs, or give the folder each one is in.");
        if (!Directory.Exists(o.DataDir))
            return CantRun("no Data folder",
                o.DataGiven ? $"can't use the Data folder {Path.GetFullPath(o.DataDir)}: it doesn't exist. Give --data the game's Data folder, the one that holds Starfield.esm."
                            : $"can't find the game's Data folder: Steam's usual folder {o.DataDir} doesn't exist. Run again with --data \"<the game's Data folder>\" (Game Pass: ...\\Starfield\\Content\\Data).");
        if (!TriageCommand.HasStarfield(o.DataDir))
            Console.Error.WriteLine($"note: no Starfield.esm in {Path.GetFullPath(o.DataDir)}; give --data the game's Data folder if it is somewhere else.");
        foreach (var md in o.MastersDirs)
            if (!Directory.Exists(md))
                return CantRun($"the --masters-dir folder {Path.GetFullPath(md)} doesn't exist",
                    $"can't use --masters-dir {Path.GetFullPath(md)}: that folder doesn't exist. Check the path (a path with spaces needs quotes around it).");
        foreach (var (opt, path) in new[] { ("--dmmdeps", o.DmmDeps), ("--papyrus", o.Papyrus), ("--ck-sources", o.CkSources) }
                     .Concat(o.MasterArchives.Select(m => ("--master-archive", (string?)m))))
            if (path != null && !File.Exists(path))
                return CantRun($"{opt} file not found", $"can't use {opt} {Path.GetFullPath(path)}: no such file.");
        foreach (var (opt, dir) in o.Psc.Select(p => ("--psc", p)).Concat(o.PscImport.Select(p => ("--psc-import", p))))
        {
            var full = Path.GetFullPath(dir);
            if (!Directory.Exists(full))
                return CantRun($"the {opt} folder {full} doesn't exist",
                    $"can't use {opt} {full}: {(File.Exists(full) ? "that is a file, not a folder" : "that folder doesn't exist")}. " +
                    (opt == "--psc" ? "Give --psc the script source folder, the one above the namespace folders (for example Scripts\\Source)."
                                    : "Give --psc-import a folder of the other mod's script sources.") +
                    " A path with spaces needs quotes around it.");
            Log.Write($"{opt}: {full}");
        }
        foreach (var t in o.Tested)
        {
            var full = Path.GetFullPath(t);
            if (!File.Exists(full))
                return CantRun($"--tested {Path.GetFileName(full)}: no such file",
                    $"can't use --tested {full}: no such file. Give the zip (or a .ba2) of the build you tested in game.");
            if (InsideGameInstall(full, o.DataDir))
                return CantRun($"--tested {Path.GetFileName(full)} is inside the game install",
                    $"won't use --tested {full}: it is inside the game's install folder. Give the copy of the tested build from your build or download folder.");
            Log.Write($"tested: {Log.Describe(full)}");
        }
        if (o.Stage != null)
        {
            var full = Path.GetFullPath(o.Stage);
            if (!Directory.Exists(full))
                return CantRun("the --stage folder doesn't exist",
                    $"can't use --stage {full}: {(File.Exists(full) ? "that is a file, not a folder" : "that folder doesn't exist")}. Give the folder the release was built from (the one that maps to Data).");
            if (InsideGameInstall(full.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, o.DataDir))   // the install folder itself too
                return CantRun("the --stage folder is inside the game install",
                    $"won't use --stage {full}: it is inside the game's install folder. Give the build folder the release was made from.");
            Log.Write($"stage: {full}");
        }
        if ((o.Papyrus == null) != (o.CkSources == null))
            Console.Error.WriteLine("note: the Papyrus lint needs both --papyrus and --ck-sources; it is skipped.");

        // ---- unpack ----
        var work = Directory.CreateTempSubdirectory("sf-mod-check-release-").FullName;
        Log.Write($"work folder: {work}");
        try
        {
            var packages = new List<ReleasePackage>();
            for (int i = 0; i < zips.Count; i++)
            {
                var full = zips[i];
                try { packages.Add(Unpack(full, Path.Combine(work, $"zip{i}"))); }
                catch (InvalidDataException e)
                {
                    return CantRun($"{Path.GetFileName(full)} can't be unpacked", $"can't unpack {full}: {e.Message}");
                }
                catch (Exception e) when (TriageCommand.IsPcProblem(e))
                {
                    // the PC stopped it, not the zip (sixteenth review): name the zip and the fix
                    Log.Write($"unpacking {full} stopped: {e}");
                    return CantRun($"{Path.GetFileName(full)} couldn't be unpacked (a problem of this PC)",
                        $"couldn't unpack {full}: {e.Message} Close any program that has the zip open, check that you can read it, and free " +
                        $"space in the temp folder ({Path.GetTempPath()}), then run again.");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // any other file error (a path too long, a share that dropped): the PC or the zip (seventeenth review)
                    Log.Write($"unpacking {full} stopped: {e}");
                    return CantRun($"{Path.GetFileName(full)} couldn't be unpacked",
                        $"couldn't unpack {full}: {e.Message} Either the PC stopped it (the file moved, a network drive dropped, the temp folder " +
                        $"{Path.GetTempPath()} is full) or the zip holds a path this system can't make (too long). Run again; if it fails the same way, " +
                        "shorten the folder names in the zip.");
                }
                Log.Write($"unpacked {packages[^1].Name}: {packages[^1].Files.Count} files, {packages[^1].Plugins.Count} plugins, {packages[^1].Archives.Count} archives");
            }
            foreach (var dir in folders)
            {
                ReleasePackage pkg;
                try { pkg = FromFolder(dir); }
                catch (InvalidDataException e) { return CantRun($"the folder {Path.GetFileName(dir.TrimEnd('\\', '/'))} can't be checked", $"can't check the folder {dir}: {e.Message}"); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Log.Write($"reading the folder {dir} stopped: {e}");
                    return CantRun($"the folder {Path.GetFileName(dir.TrimEnd('\\', '/'))} couldn't be read",
                        $"couldn't read the folder {dir}: {e.Message} Check that you can open every file in it and that no program has one locked, then run again.");
                }
                packages.Add(pkg);
                Log.Write($"folder {pkg.Name}: {pkg.Files.Count} files, {pkg.Plugins.Count} plugins, {pkg.Archives.Count} archives");
            }
            if (loose.Count > 0)
            {
                var into = Path.Combine(work, "loose");
                try
                {
                    Directory.CreateDirectory(into);
                    foreach (var f in loose) File.Copy(f, Path.Combine(into, Path.GetFileName(f)));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Log.Write($"copying the loose files stopped: {e}");
                    return CantRun("the loose files couldn't be copied",
                        $"couldn't copy the loose files into the temp folder {into}: {e.Message} Close any program that has them open and free space in {Path.GetTempPath()}, then run again.");
                }
                var pkg = FromFolder(into, LooseName);
                packages.Add(pkg);
                Log.Write($"{LooseName}: {pkg.Plugins.Count} plugins, {pkg.Archives.Count} archives (copied into the work folder)");
            }
            var ctx = new ReleaseContext { Options = o, Packages = packages, Work = work };

            var findings = new List<ReleaseFinding>();
            foreach (var step in Steps)
                findings.AddRange(step(ctx));
            findings = DedupeUnreadable(findings);

            // ---- report ----
            var shown = ReleaseAccept.Apply(findings.Select(f => Apply(f, o.Profile)).ToList(), o.Accept);
            var counted = shown.Where(f => f.Accepted == null).ToList();
            int problems = counted.Count(f => f.Level == Level.Problem), review = counted.Count(f => f.Level == Level.Review),
                warnings = counted.Count(f => f.Level == Level.Warning), accepted = shown.Count - counted.Count;
            if (!quiet)
                Console.WriteLine($"sf-mod-check {version} release ({o.Profile} profile): {SafeText.OneLine(string.Join(", ", packages.Select(p => p.Name)))} " +
                                  $"({Num(packages.Sum(p => p.Files.Count), "file")}, {Num(packages.Sum(p => p.Plugins.Count), "plugin")}, {Num(packages.Sum(p => p.Archives.Count), "archive")})");
            foreach (var lvl in new[] { Level.Problem, Level.Review, Level.Warning, Level.Note })
                foreach (var f in counted.Where(f => f.Level == lvl))
                    Console.WriteLine($"{Label(lvl)} [{f.Rule}] {SafeText.OneLine(f.Text)}");   // every step's names, one rule (0.13.0)
            // accepted findings still print, last, with the entry that accepted them (0.15.2)
            foreach (var f in shown.Where(f => f.Accepted != null))
                Console.WriteLine($"ACCEPTED [{f.Rule}] {SafeText.OneLine(f.Text)} (was a {Label(f.Level).Trim()}; accepted by {SafeText.OneLine(f.Accepted!.Source)} {SafeText.OneLine(f.Accepted.Text)})");
            Console.WriteLine($"RESULT: {(problems > 0 ? "FAIL" : "OK")} ({problems} problems, {review} to review, {warnings} warnings" +
                              (accepted > 0 ? $", {accepted} accepted)" : ")"));
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

    /// <summary>In the basic and creations profiles a house rule's problem is shown as a WARNING, marked so.</summary>
    public static ReleaseFinding Apply(ReleaseFinding f, string profile) =>
        profile != "house" && f.Kind == RuleKind.House && f.Level == Level.Problem
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
            if (!TriageCommand.HasStarfield(data)) return false;
            var install = Path.GetDirectoryName(data);
            if (install == null) return false;
            return full.StartsWith(install.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>True for an entry path Windows would write under another name: a "." part, or a part ending in a dot or
    /// a space ("a.esm." is written as a.esm). Two entries could then land on one file, or a plugin be missed by its
    /// extension (fifteenth review). Only the parts before a final "/" count.</summary>
    public static bool ChangedByWindows(string name) =>
        name.TrimEnd('/').Split('/').Any(p => p.Length == 0 || p == "." || p.EndsWith('.') || p.EndsWith(' ')   // also an empty part ("Data//a.esm")
                                             || DeviceNames.Contains(p.Split('.')[0].TrimEnd(' '))   // "NUL .txt" too
                                             || p.Any(c => c < 0x20 || "<>\"|?*".Contains(c)));   // characters Windows can't write in a name (eighteenth review)

    static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "CONIN$", "CONOUT$",
        "COM0", "LPT0", "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³",   // CON, NUL, COM1 ...: devices, not files
    };

    /// <summary>A file and a folder of the same name across an archive's entries, compared by normalized name (lower
    /// case), so it is found on every system, not only where the file system ignores case (seventeenth review). Adds
    /// norm (a Ba2Names.Normalize path) to the sets; returns why it clashes, or null.</summary>
    public static string? NameClash(string norm, HashSet<string> files, HashSet<string> dirs)
    {
        if (dirs.Contains(norm)) return "has the same name as a folder in the archive";
        var parts = norm.Split('\\');
        var prefix = "";
        for (int i = 0; i < parts.Length - 1; i++)
        {
            prefix = i == 0 ? parts[0] : prefix + "\\" + parts[i];
            if (files.Contains(prefix)) return "sits in a folder that the archive also holds as a file";
        }
        prefix = "";
        for (int i = 0; i < parts.Length - 1; i++) { prefix = i == 0 ? parts[0] : prefix + "\\" + parts[i]; dirs.Add(prefix); }
        files.Add(norm);
        return null;
    }

    /// <summary>True when the entry's stream gives at least one byte (its declared length can be wrong).</summary>
    static bool ReadsAByte(ZipArchiveEntry e)
    {
        using var s = e.Open();
        return s.ReadByte() >= 0;
    }

    /// <summary>The package name of a run's loose .ba2 and plugin inputs (0.15.0).</summary>
    public const string LooseName = "loose files";

    /// <summary>A .ba2 or a plugin given as an input (0.15.0): it goes into the run's loose-files package.</summary>
    public static bool IsLoose(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".ba2" or ".esm" or ".esp" or ".esl";

    /// <summary>A folder as a package, used in place (0.15.0): nothing is written into it. Every release step writes only
    /// into the run's work folder (DMMDeps, the lint and --tested copy what they need there). Refuses a folder with more
    /// than MaxEntries files, as for a zip (InvalidDataException).</summary>
    public static ReleasePackage FromFolder(string dir, string? name = null)
    {
        var root = Path.GetFullPath(dir).TrimEnd('\\', '/');
        var files = new List<string>();
        var plugins = new List<string>();
        var archives = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (files.Count >= MaxEntries)
                throw new InvalidDataException($"it holds more than the {MaxEntries:N0} files this check allows; a mod's stage folder holds far fewer. " +
                                               "Give the folder that holds only this mod's files (not a whole Data folder)");
            var norm = Ba2Names.Normalize(Path.GetRelativePath(root, f));
            if (!seen.Add(norm)) continue;   // two names that differ only in case (Linux): the game sees one
            files.Add(norm);
            if (PluginExts.Contains(Path.GetExtension(norm))) plugins.Add(f);
            else if (norm.EndsWith(".ba2", StringComparison.Ordinal)) archives.Add(f);
        }
        files.Sort(StringComparer.Ordinal);
        plugins.Sort(StringComparer.OrdinalIgnoreCase);
        archives.Sort(StringComparer.OrdinalIgnoreCase);
        return new ReleasePackage
        {
            ZipPath = name ?? root,
            IsZip = false,
            Root = root,
            Files = files,
            Plugins = plugins,
            Archives = archives,
        };
    }

    /// <summary>Unpacks a zip into dest. Refuses entries that would land outside dest (".." or a rooted path), empty
    /// names, duplicate names, names Windows changes and a file and a folder of the same name, rather than skipping them,
    /// so a damaged or crafted zip never gets an OK. Throws InvalidDataException for anything about the zip's contents
    /// (those refusals, and any other error reading or writing an entry, naming it), and lets a problem of the PC through
    /// as itself (TriageCommand.IsFileSystem: a file or folder not found, a path too long, and IsPcProblem's no
    /// permission, a lock, the disk full, a network drive that dropped). Callers catch InvalidDataException and
    /// IOException / UnauthorizedAccessException (0.8.0 review).</summary>
    public static ReleasePackage Unpack(string zip, string dest)
    {
        Directory.CreateDirectory(dest);
        var root = Path.GetFullPath(dest).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var files = new List<string>();
        var plugins = new List<string>();
        var archives = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> clashFiles = new(StringComparer.Ordinal), clashDirs = new(StringComparer.Ordinal);
        using (var z = ZipFile.OpenRead(zip))
        {
            if (z.Entries.Count > MaxEntries) throw new InvalidDataException(TooManyEntries(z.Entries.Count, "zip"));
            long total = z.Entries.Sum(e => e.Length), written = 0;
            if (total > MaxUnpackedBytes)
                throw new InvalidDataException($"it unpacks to {total / (1024 * 1024 * 1024.0):F1} GB, more than the {MaxUnpackedBytes / (1024 * 1024 * 1024)} GB this check allows; a release zip is far smaller than that");
            foreach (var e in z.Entries)
            try
            {
                var name = e.FullName.Replace('\\', '/');
                // the path check first, folder entries included (0.8.0 review: "../../x/" was skipped, not refused)
                if (name.Length == 0 || name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(p => p == ".."))
                    throw new InvalidDataException($"the entry \"{e.FullName}\" points outside the zip's own folders");
                if (ChangedByWindows(name))
                    throw new InvalidDataException($"the entry \"{e.FullName}\" has a name Windows changes (a \".\" part, or one ending in a dot or space); rename it in the zip");
                if (name.EndsWith('/'))
                {
                    // a folder entry: nothing to write, unless it holds data (then something would hide behind a
                    // folder name; 0.8.0 review)
                    if (e.Length > 0 || ReadsAByte(e))
                        throw new InvalidDataException($"the entry \"{e.FullName}\" is named like a folder but holds data");
                    continue;
                }
                var norm = Ba2Names.Normalize(name);
                if (!seen.Add(norm)) throw new InvalidDataException($"the zip holds \"{e.FullName}\" twice (names differ only in case or slashes)");
                if (NameClash(norm, clashFiles, clashDirs) is { } clash)
                    throw new InvalidDataException($"the entry \"{e.FullName}\" {clash}; rename one of them in the zip");
                var target = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"the entry \"{e.FullName}\" points outside the zip's own folders");
                // a file and a folder of the same name, as the 7z/rar unpacker refuses (sixteenth review)
                if (Directory.Exists(target))
                    throw new InvalidDataException($"the entry \"{e.FullName}\" has the same name as a folder in the zip; rename one of them in the zip");
                for (var d = Path.GetDirectoryName(target); d != null && d.Length >= root.Length; d = Path.GetDirectoryName(d))
                    if (File.Exists(d)) throw new InvalidDataException($"the entry \"{e.FullName}\" sits in a folder that the zip also holds as a file; rename one of them in the zip");
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
            // a problem of the PC (disk full, a lock, no permission) goes on as itself, so the user isn't told the zip
            // is broken (fifteenth review); anything else about an entry becomes a refusal that says what to do
            catch (Exception x) when ((x is IOException or NotSupportedException or ArgumentException) && !TriageCommand.IsFileSystem(x))   // as UnpackOther: a path too long or a missing folder goes on as itself
            {
                throw new InvalidDataException($"the entry \"{e.FullName}\" can't be unpacked ({x.GetType().Name}: {x.Message}). The zip holds a " +
                                               "name this system can't make (a file and a folder of the same name, a reserved name) or an entry " +
                                               "it can't decode; rename or remove that entry in the zip and pack it again", x);
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
