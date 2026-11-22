// Command "diff" (0.11.0): compares two versions of a plugin, or of a mod (a .zip, .7z or .rar download, or a folder),
// and says what changed between them.
//   sf-mod-check diff <old> <new> [--data <Data folder>] [--json] [--out <file>] [--all] [--quiet] [--verbose] [--log <file>]
// It is read-only: it never writes next to its inputs, only to --out and --log, and an archive is unpacked into a temp
// folder that is always removed. Exit 0 = no real differences, 1 = differences, 2 = bad input or setup.
// Design: NOTES.md "0.11.0 diff relay". This file holds the arguments, the input checks, the plugin pairing, the run
// and the report (text, JSON, RESULT line); DiffRecords.cs the header compare and record index, DiffFields.cs the
// field compare and noise, DiffDetails.cs the cell, leveled-list and swap views, DiffFiles.cs loose files, archives, scripts.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SfModCheck;

public sealed class DiffOptions
{
    public string Old { get; set; } = "";
    public string New { get; set; } = "";
    /// <summary>The game's Data folder (for master EditorIDs), from --data or Steam's usual one.</summary>
    public string DataDir { get; set; } = Cli.SteamData;
    public bool DataGiven { get; set; }
    public bool Json { get; set; }
    public string? Out { get; set; }
    public bool Verbose { get; set; }
    public string? Log { get; set; }
    public bool Quiet { get; set; }
    /// <summary>--all: the text report lists everything (no caps), and each cell's records.</summary>
    public bool All { get; set; }
}

/// <summary>What the argument parser found: options, or an error text (exit 2), or a request for help.</summary>
public sealed record DiffParse(DiffOptions? Options, string? Error, bool Help = false);

/// <summary>One side of the diff: what the user gave and the files in it.</summary>
public sealed class DiffInput
{
    public required string Path { get; init; }
    public required TriageInputKind Kind { get; init; }
    /// <summary>True for a mod package (an archive or a folder), false for one plugin.</summary>
    public bool IsPackage => Kind != TriageInputKind.Plugin;
    /// <summary>The folder the files are in (the unpacked archive, the folder, or the plugin's own folder).</summary>
    public string Root { get; set; } = "";
    /// <summary>Every file, as Ba2Names.Normalize paths relative to Root (a plugin input: only the plugin).</summary>
    public List<string> Files { get; set; } = [];
    /// <summary>Files that weren't read, each with why (links, case twins), as triage tells them.</summary>
    public List<(string File, string Why)> Skipped { get; } = [];
    /// <summary>Full path of a file in Files (finds the real spelling on Linux and macOS).</summary>
    public Func<string, string> Full { get; set; } = rel => rel;
}

/// <summary>A plugin in both inputs (both paths set), or only in the new one (Added: OldRel null) or the old one
/// (Removed: NewRel null). Rel paths are the inputs' Files entries; Name is the file name both share.</summary>
public sealed record PluginPair(string Name, string? OldRel, string? NewRel);

/// <summary>Why <see cref="DiffCommand.RealPath(string, out string?, out PathProblem)"/> couldn't work out a path, so a
/// message can give the advice that fits (code-review round 7).</summary>
public enum PathProblem { None, Invalid, NoAccess, LinkError, MountedVolume, DevicePath, TooManyLinks }

public sealed class DiffPairing
{
    public List<PluginPair> Paired { get; } = [];
    public List<PluginPair> Added { get; } = [];
    public List<PluginPair> Removed { get; } = [];
    /// <summary>Set when an input holds two plugins with the same file name in different folders: they can't be paired
    /// by name, so the run stops (exit 2) with this text.</summary>
    public string? Error { get; set; }
}

public static class DiffCommand
{
    public const string Usage =
        "usage: sf-mod-check diff <old> <new> [--data <Data folder>] [--json] [--out <file>] [--all] [--quiet] [--verbose] [--log <file>]\n" +
        "         <old> and <new> are two plugins (.esm .esp .esl), or two mods: each a .zip, .7z or .rar download or a folder.\n" +
        "         Plugins in two mods are paired by file name; one in only one of them is listed as added or removed.\n" +
        "         The report counts records per type, names the fields that changed, groups placed objects by cell, and\n" +
        "         lists leveled-list entries added and removed and base-object swaps. For two mods it also compares the\n" +
        "         loose files (by SHA-256), each archive's files (paired by name) and the objects, states and functions of\n" +
        "         changed scripts (.pex; names only, never code).\n" +
        "         --data      the game's Data folder, for the EditorIDs of records in the masters (default: Steam's usual folder)\n" +
        "         --json      the report as JSON instead of text (always complete)\n" +
        "         --out       write the report to this file (stdout then gets only the RESULT line)\n" +
        "         --all       list everything in the text report (long lists are cut short otherwise), and each cell's records\n" +
        "         Records whose bytes differ but whose fields are all equal count as noise, which is no difference.\n" +
        "         Scripts and script properties are matched by name, so a new order alone is noise.\n" +
        "         exit 0 = no real differences (noise only counts as none), 1 = differences, 2 = bad input or setup";

    public const string Example = "sf-mod-check diff \"C:\\Mods\\MyMod-1.0.zip\" \"C:\\Mods\\MyMod-1.1.zip\"";

    /// <summary>The JSON report's shape version (NOTES.md "0.11.0 diff relay"; bumped on any breaking change).</summary>
    public const int JsonSchema = 1;

    // ---- arguments ----

    public static DiffParse Parse(string[] a)
    {
        var o = new DiffOptions();
        var inputs = new List<string>();
        for (int i = 0; i < a.Length; i++)
        {
            string? Value(string what, out string? error)
            {
                error = null;
                if (i + 1 < a.Length && !a[i + 1].StartsWith("--") && a[i + 1].Trim().Length > 0) return a[++i];
                error = $"{a[i]} needs {what} after it{(i + 1 < a.Length && a[i + 1].Trim().Length == 0 ? " (it was given an empty value)" : "")}, " +
                        $"for example: {a[i]} {OptionExample(a[i])}";
                return null;
            }
            string? err;
            switch (a[i])
            {
                case "--data":
                    if (Value("the game's Data folder", out err) is not { } d) return new(null, err);
                    o.DataDir = d; o.DataGiven = true; break;
                case "--out":
                    if (Value("a file name for the report", out err) is not { } of) return new(null, err);
                    o.Out = of; break;
                case "--log":
                    if (Value("a file name", out err) is not { } lf) return new(null, err);
                    o.Log = lf; break;
                case "--json": o.Json = true; break;
                case "--verbose": o.Verbose = true; break;
                case "--quiet": o.Quiet = true; break;
                case "--all": o.All = true; break;
                case "-h": case "--help": return new(null, null, Help: true);
                default:
                    if (a[i].StartsWith("--")) return new(null, $"unknown option {a[i]}");
                    if (a[i].Trim().Length == 0)
                        return new(null, $"an input is empty: give the old and the new version, for example: {Example}");
                    inputs.Add(a[i]);
                    break;
            }
        }
        if (inputs.Count == 0) return new(null, $"give the old and the new version to compare, for example: {Example}");
        if (inputs.Count == 1) return new(null, $"only one input ({inputs[0]}): give the old and the new version, for example: {Example}");
        if (inputs.Count > 2)
            return new(null, $"diff compares two versions, not {inputs.Count} ({string.Join(", ", inputs)}). Give the old one first, then the new one " +
                             "(a path with spaces needs quotes around it).");
        o.Old = inputs[0];
        o.New = inputs[1];
        return new(o, null);
    }

    static string OptionExample(string option) => option switch
    {
        "--out" => "diff-report.txt",
        _ => Cli.Example(option),
    };

    /// <summary>What kind of input a path is: a folder, an archive or a plugin by its extension; null when it is none of
    /// them (or doesn't exist).</summary>
    public static TriageInputKind? KindOf(string path)
    {
        if (Directory.Exists(path)) return TriageInputKind.Folder;
        if (!File.Exists(path)) return null;
        return TriageCommand.KindOf(path);
    }

    /// <summary>Checks the two inputs and --out before anything is read. Returns an error text naming the file and the
    /// fix, or null.</summary>
    public static string? CheckInputs(DiffOptions o, out TriageInputKind oldKind, out TriageInputKind newKind)
    {
        oldKind = newKind = default;
        foreach (var (path, which) in new[] { (o.Old, "old"), (o.New, "new") })
        {
            var full = System.IO.Path.GetFullPath(path);
            if (!File.Exists(full) && !Directory.Exists(full))
                return $"can't compare: the {which} version {full} doesn't exist. Check the path (a path with spaces needs quotes around it).";
            if (KindOf(full) is not { } k)
                return $"can't compare {full}: give a plugin (.esm .esp .esl), a .zip, .7z or .rar download, or a mod folder.";
            if (which == "old") oldKind = k; else newKind = k;
        }
        bool oldPlugin = oldKind == TriageInputKind.Plugin, newPlugin = newKind == TriageInputKind.Plugin;
        if (oldPlugin != newPlugin)
            return $"can't compare a plugin with a whole mod ({TriageCommand.Shown(o.Old)} and {TriageCommand.Shown(o.New)}). Give two plugins, " +
                   "or two mods (each a .zip, .7z, .rar or folder).";
        if (o.Out != null && OutProblem(o.Out, o.Old, o.New) is { } outError) return outError;
        if (o.DataGiven && !Directory.Exists(o.DataDir))
            return $"can't use --data {System.IO.Path.GetFullPath(o.DataDir)}: that folder doesn't exist. Give the game's Data folder, the one that holds " +
                   "Starfield.esm (a path with spaces needs quotes around it), or leave --data out.";
        return null;
    }

    /// <summary>The refusal checks always ignore case, on Linux too: a folder that ignores case (WSL's /mnt/c, NTFS or
    /// exFAT mounts) would otherwise let a path into an input. A wrong refusal costs a message; a wrong pass writes into
    /// an input.</summary>
    const StringComparison PathCase = StringComparison.OrdinalIgnoreCase;

    /// <summary>Where a path really goes, with every symbolic link and junction on the way followed (in the parts that
    /// exist, also inside a link's own target), for comparing paths only, never for opening them. ".." is applied the way
    /// the system does: by text on Windows (before any link is followed), and to the real folder on Linux and macOS.
    /// Null when that can't be worked out: a link to a mounted volume (\\?\Volume{...}) or another device path, a chain of
    /// more than 40 links or more than 400 links in all, an invalid path, a part whose attributes can't be read (so it may
    /// be a link), or an error reading a link. A \\?\, \??\ or \\.\
    /// prefix is dropped on Windows. A link whose target doesn't exist yet is followed too. Hard links and 8.3 short names aren't detected.</summary>
    public static string? RealPath(string path) => RealPath(path, out _, out _);
    /// <summary>As <see cref="RealPath(string)"/>; when it returns null, <paramref name="why"/> says why, for the error
    /// message (code-review round 4: one fixed message named the wrong cause for an access error).</summary>
    public static string? RealPath(string path, out string? why) => RealPath(path, out why, out _);
    /// <summary>As <see cref="RealPath(string, out string?)"/>, with the cause also as a code, so the advice in a message
    /// is picked by the code, never by the text (code-review round 7: the text holds paths and system messages).</summary>
    public static string? RealPath(string path, out string? why, out PathProblem kind)
    {
        string? reason = null;
        var reasonKind = PathProblem.None;
        int links = 0;   // code-review round 5: a budget for the whole call, so links whose targets repeat links can't run for ever
        try
        {
            var r = Walk(OperatingSystem.IsWindows() ? NoPrefix(System.IO.Path.GetFullPath(path)) : Absolute(path), 0);
            why = reason;
            kind = reasonKind;
            return r;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            why = $"the path isn't valid ({e.Message})";
            kind = PathProblem.Invalid;
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            // a last resort: the parts and links are caught where they're read, with their names
            why = "a folder or link on the way can't be read (access denied)";
            kind = PathProblem.NoAccess;
            return null;
        }
        catch (IOException e)
        {
            why = $"a link on the way can't be read ({e.Message})";
            kind = PathProblem.LinkError;
            return null;
        }

        string? Fail(PathProblem k, string text) { reasonKind = k; reason = text; return null; }

        static string Absolute(string p) => System.IO.Path.IsPathRooted(p) ? p : System.IO.Path.Combine(Environment.CurrentDirectory, p);

        string? Walk(string p, int depth)
        {
            // code-review round 5: \\?\GLOBALROOT, \\.\PhysicalDrive0 and other device paths that NoPrefix can't turn into
            // a drive letter never match one, so they can't be checked
            if (OperatingSystem.IsWindows() && IsDevicePath(p))
                return IsVolumeTarget(p) ? Fail(PathProblem.MountedVolume, "it goes through a mounted volume")
                                         : Fail(PathProblem.DevicePath, $"it is a device path ({p})");
            var root = System.IO.Path.GetPathRoot(p) ?? "";
            var cur = root;
            bool exists = true;   // once a part doesn't exist, nothing after it can be a link
            // an array: Split(char, char, options) would bind to Split(char, int count, options) and stop at 47 parts
            foreach (var part in p[root.Length..].Split(new[] { System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == ".") continue;
                if (part == "..") { cur = System.IO.Path.GetDirectoryName(cur) ?? cur; continue; }
                var next = System.IO.Path.Combine(cur, part);
                if (exists)
                {
                    FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
                    exists = info.Exists;
                    // code-review round 5: a link whose target doesn't exist yet is followed too, since writing through it
                    // creates the target. Attributes are -1 when nothing is there (LinkTarget would throw on Linux then).
                    // code-review round 6: a part that exists but whose attributes can't be read (no permission) can't be
                    // told apart from a link, so the path is refused, naming that part (a wrong refusal only costs a message)
                    FileAttributes attrs;
                    try { attrs = info.Attributes; }
                    // code-review round 7: the system's access message only repeats the path; an I/O error keeps its text
                    catch (UnauthorizedAccessException)
                    {
                        return Fail(PathProblem.NoAccess, $"{next} on the way can't be read (access denied), so diff can't tell whether it is a link");
                    }
                    catch (IOException e)
                    {
                        return Fail(PathProblem.LinkError, $"{next} on the way can't be read ({e.Message}), so diff can't tell whether it is a link");
                    }
                    bool isLink = (int)attrs != -1 && attrs.HasFlag(FileAttributes.ReparsePoint);
                    // LinkTarget by hand, not ResolveLinkTarget: Windows refuses to follow a junction that way ("untrusted
                    // mount point"). The target is walked again, so links inside it are followed too.
                    string? target = null;
                    // code-review (0.11.0 VMAD order): a link whose target can't be read is named, like a part above
                    if (isLink)
                    {
                        try { target = info.LinkTarget; }
                        catch (UnauthorizedAccessException)
                        {
                            return Fail(PathProblem.NoAccess, $"the link {next} on the way can't be read (access denied)");
                        }
                        catch (IOException e)
                        {
                            return Fail(PathProblem.LinkError, $"the link {next} on the way can't be read ({e.Message})");
                        }
                    }
                    if (target is not null)
                    {
                        // the depth limit is per chain (a link whose target holds a link, and so on), so loops end and a
                        // long path with many separate links still passes; the total budget stops targets that repeat links
                        if (depth >= 40) return Fail(PathProblem.TooManyLinks, "it goes through a chain of more than 40 links");
                        if (++links > 400) return Fail(PathProblem.TooManyLinks, "it goes through more than 400 links");
                        if (IsVolumeTarget(target)) return Fail(PathProblem.MountedVolume, "it goes through a mounted volume");
                        var t = System.IO.Path.IsPathRooted(target) ? target : System.IO.Path.Combine(cur, target);
                        if (Walk(OperatingSystem.IsWindows() ? NoPrefix(System.IO.Path.GetFullPath(t)) : t, depth + 1) is not { } resolved) return null;
                        next = resolved;
                    }
                }
                cur = next;
            }
            return cur;
        }
    }

    /// <summary>A Windows volume GUID path (\\?\Volume{...} or \??\Volume{...}), which a folder mount point leads to and
    /// which never matches a drive letter. Elsewhere "Volume{" is an ordinary folder name.</summary>
    internal static bool IsVolumeTarget(string target) =>
        OperatingSystem.IsWindows() &&
        DevicePrefixes.Any(pre => target.StartsWith(pre + "Volume{", StringComparison.OrdinalIgnoreCase));

    static readonly string[] DevicePrefixes = [@"\\?\", @"\??\", @"\\.\"];

    /// <summary>A Windows path that still starts with \\?\, \??\ or \\.\ after <see cref="NoPrefix"/> (a volume GUID,
    /// GLOBALROOT or another device), which never matches a drive-letter path.</summary>
    internal static bool IsDevicePath(string p) => DevicePrefixes.Any(pre => p.StartsWith(pre, StringComparison.Ordinal));

    /// <summary>Drops a Windows \\?\, \??\ or \\.\ prefix so the path compares with its plain form: \\?\C:\x is C:\x,
    /// \\?\UNC\server\share is \\server\share. Volume GUID and other device paths are left as they are.</summary>
    internal static string NoPrefix(string p)
    {
        foreach (var pre in DevicePrefixes)
        {
            if (!p.StartsWith(pre, StringComparison.Ordinal)) continue;
            var rest = p[pre.Length..];
            if (rest.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + rest[4..];
            if (rest.Length >= 2 && char.IsAsciiLetter(rest[0]) && rest[1] == ':') return rest;
        }
        return p;
    }

    /// <summary>A ':' after the path's root (Windows): an NTFS stream name on a file or a folder.</summary>
    internal static bool HasStreamColon(string full) =>
        full[(System.IO.Path.GetPathRoot(full) ?? "").Length..].Contains(':');

    /// <summary>What to do about a path <see cref="RealPath(string, out string?, out PathProblem)"/> couldn't resolve,
    /// picked by its cause (code-review round 6: the mounted-volume advice was given for every cause; round 7: by the
    /// code, not the text, and for --out and --log too). <paramref name="who"/> is "the input", "--out" or "--log".</summary>
    internal static string PathHint(PathProblem kind, string who) => kind switch
    {
        PathProblem.MountedVolume => $"Use a drive-letter path for {who} (a mounted volume needs a drive letter for that)",
        PathProblem.DevicePath => $"Use a drive-letter path for {who}, not a device path",
        PathProblem.TooManyLinks => $"Use the real path for {who}, without the links",
        PathProblem.Invalid => $"Check the path given for {who} for a typing mistake",
        PathProblem.NoAccess => $"Make sure this account can read every folder on the path for {who}, or use another path",
        PathProblem.LinkError => $"Check the links and the drive on the path for {who} (a broken link, or a drive that can't be reached), or use another path",
        _ => $"Use a plain path for {who}",
    };

    /// <summary>The full path for a message, or the typed text when the system rejects it (never throws).</summary>
    static string SafeFull(string p)
    {
        try { return System.IO.Path.GetFullPath(p); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return p; }
    }

    /// <summary>Why --log can't be used, or null. Checked before the log file is opened, so a bad --log never truncates an
    /// input: the same rules as --out, and --out and --log must be different files.</summary>
    public static string? LogProblem(DiffOptions o)
    {
        if (o.Log == null) return null;
        if (OutProblem(o.Log, o.Old, o.New, "--log") is { } e) return e;
        if (o.Out != null)
        {
            try
            {
                if (RealPath(o.Out) is { } ro && RealPath(o.Log) is { } rl && string.Equals(ro, rl, PathCase))
                    return $"can't use one file for both --out and --log ({System.IO.Path.GetFullPath(o.Out)}). Give them different files, " +
                           "for example --out diff-report.txt --log diff-log.txt";
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { /* OutProblem reports a bad --out */ }
        }
        return null;
    }

    /// <summary>Why --out (or, with option "--log", the log file) can't be used, or null: diff never writes over a plugin,
    /// an archive or one of its inputs, and never inside an input folder (it is read-only).</summary>
    public static string? OutProblem(string outFile, string oldInput, string newInput, string option = "--out")
    {
        static bool WriteProtectedName(string p) =>
            System.IO.Path.GetExtension(p).ToLowerInvariant() is ".esm" or ".esp" or ".esl" or ".ba2" or ".zip" or ".7z" or ".rar" or ".pex";
        var what = option == "--log" ? "the log" : "the report";
        var example = option == "--log" ? "--log diff-log.txt" : "--out diff-report.txt";
        var exampleName = option == "--log" ? "diff-log.txt" : "diff-report.txt";
        string full;
        try { full = System.IO.Path.GetFullPath(outFile); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"can't use {option} \"{outFile}\": {e.Message} Give {option} a file name, for example {example}";
        }
        if (Directory.Exists(full))
            return $"can't use {option} {full}: that is a folder. Give {option} a file name, for example {option} \"{System.IO.Path.Combine(full, exampleName)}\"";
        // code-review rounds 4 and 5: on Windows "A.esm:r.txt" is a stream inside A.esm, and "mod::$INDEX_ALLOCATION\r.txt"
        // opens the folder mod itself, so a ':' anywhere after the drive is refused
        if (OperatingSystem.IsWindows() && HasStreamColon(full))
            return $"won't write {what} to {full}: a ':' after the drive writes into another file or folder (an NTFS stream). " +
                   $"Give {option} a plain path, for example {example}";
        if (WriteProtectedName(full))
            return $"won't write {what} to {full}: that name is a plugin, archive or script, and {what} would overwrite it. " +
                   $"Give {option} a .txt{(option == "--log" ? "" : " or .json")} file, for example {example}";
        // symbolic links and junctions are followed on both sides, so a path through a link to an input folder is refused too
        if (RealPath(outFile, out var whyNot, out var notKind) is not { } real)
            return $"can't use {option} {full}: {whyNot}, so diff can't check that it stays out of its inputs. " +
                   $"{PathHint(notKind, option)}, for example {example}";
        if (WriteProtectedName(real))
            return $"won't write {what} to {full}: its real path is {real}, a plugin, archive or script, and {what} would overwrite it. " +
                   $"Give {option} a .txt{(option == "--log" ? "" : " or .json")} file, for example {example}";
        if (OperatingSystem.IsWindows() && HasStreamColon(real))
            return $"won't write {what} to {full}: its real path is {real}, and a ':' after the drive writes into another file or folder " +
                   $"(an NTFS stream). Give {option} a plain path, for example {example}";
        foreach (var input in new[] { oldInput, newInput })
        {
            // code-review round 4: an input that can't be resolved (a mounted volume on its way) is refused, not compared
            // by its typed path, which an output reaching the same folder by a drive letter would never match
            if (RealPath(input, out var whyInput, out var inKind) is not { } inReal)
                return $"can't use {option} {full}: the input {SafeFull(input)} can't be checked ({whyInput}), so diff " +
                       $"can't make sure {what} stays out of it. {PathHint(inKind, "the input")}, or leave out {option}.";
            // code-review round 5: never trim a root (C:\ would become C:, the current folder on drive C; / would become "")
            var inRoot = System.IO.Path.GetPathRoot(inReal) ?? "";
            var inFull = inReal.Length > inRoot.Length ? inReal.TrimEnd('\\', '/') : inReal;
            if (inFull.Length < inRoot.Length) inFull = inRoot;
            if (string.Equals(real, inFull, PathCase))
                return $"won't write {what} to {full}: that is one of the inputs. Give {option} another file.";
            var inside = System.IO.Path.EndsInDirectorySeparator(inFull) ? inFull : inFull + System.IO.Path.DirectorySeparatorChar;
            if (Directory.Exists(inFull) && real.StartsWith(inside, PathCase))
                return $"won't write {what} to {full}: it is inside the input folder {inFull}, and diff never writes into its inputs. " +
                       $"Give {option} a file outside it.";
        }
        var dir = System.IO.Path.GetDirectoryName(full);
        if (dir != null && !Directory.Exists(dir))
            return $"can't write {what} to {full}: the folder {dir} doesn't exist. Make the folder, or give {option} a file in a folder that exists.";
        return null;
    }

    // ---- pairing ----

    static bool IsPlugin(string rel) => RelPath.Extension(rel).ToLowerInvariant() is ".esm" or ".esp" or ".esl";

    /// <summary>Pairs the plugins of two inputs by file name (ignoring case and folders). A plugin in only one input is
    /// added or removed. Two plugins with one file name in the same input can't be paired: Error says which.</summary>
    public static DiffPairing PairPlugins(IEnumerable<string> oldFiles, IEnumerable<string> newFiles)
    {
        var p = new DiffPairing();
        Dictionary<string, string>? Index(IEnumerable<string> files, string which)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rel in files.Where(IsPlugin))
            {
                var name = RelPath.FileName(rel)!;
                if (map.TryGetValue(name, out var other))
                {
                    p.Error = $"the {which} version holds two plugins named {name} ({other} and {rel}), so they can't be paired by name. " +
                              "Compare the two plugins you mean directly: sf-mod-check diff <old plugin> <new plugin>";
                    return null;
                }
                map[name] = rel;
            }
            return map;
        }
        if (Index(oldFiles, "old") is not { } o || Index(newFiles, "new") is not { } n) return p;
        foreach (var (name, rel) in o.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            if (n.TryGetValue(name, out var nrel)) p.Paired.Add(new(name, rel, nrel));
            else p.Removed.Add(new(name, rel, null));
        foreach (var (name, rel) in n.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            if (!o.ContainsKey(name)) p.Added.Add(new(name, null, rel));
        return p;
    }

    /// <summary>Two plugins given directly are a pair whatever their names (MyMod-1.0.esm and MyMod.esm).</summary>
    public static DiffPairing PairTwoPlugins(string oldRel, string newRel)
    {
        var p = new DiffPairing();
        var on = RelPath.FileName(oldRel)!; var nn = RelPath.FileName(newRel)!;
        p.Paired.Add(new(string.Equals(on, nn, StringComparison.OrdinalIgnoreCase) ? nn : $"{on} -> {nn}", oldRel, newRel));
        return p;
    }

    /// <summary>The pairing with each plugin named by its own spelling on disk (the new version's when it has one), not
    /// the lower-case name Files holds ("MyMod.esm", not "mymod.esm").</summary>
    public static DiffPairing Spelled(DiffPairing p, DiffInput oldIn, DiffInput newIn)
    {
        // Full gives the lower-case path on Windows (any spelling opens there), so the name comes from the folder's listing
        static string Own(DiffInput i, string rel)
        {
            var full = i.Full(rel);
            if (!OperatingSystem.IsWindows()) return System.IO.Path.GetFileName(full);   // Full found the real spelling (and the kept one of case twins)
            try
            {
                if (System.IO.Path.GetDirectoryName(full) is { } dir &&
                    Directory.EnumerateFiles(dir, System.IO.Path.GetFileName(full), new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, AttributesToSkip = 0 })
                        .FirstOrDefault() is { } hit)
                    return System.IO.Path.GetFileName(hit);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
            return System.IO.Path.GetFileName(full);
        }
        var s = new DiffPairing { Error = p.Error };
        s.Paired.AddRange(p.Paired.Select(x => x with { Name = Own(newIn, x.NewRel!) }));
        s.Removed.AddRange(p.Removed.Select(x => x with { Name = Own(oldIn, x.OldRel!) }));
        s.Added.AddRange(p.Added.Select(x => x with { Name = Own(newIn, x.NewRel!) }));
        return s;
    }

    // ---- the run ----

    public static int Run(string[] a, string version, string byline)
    {
        try { return RunLogged(a, version, byline); }
        catch (Exception e)
        {
            Console.WriteLine(Fatal.ResultLine(e, () => $"RESULT: CANNOT DIFF ({e.GetType().Name}: {e.Message})"));
            Fatal.AfterResult(() =>
            {
                Console.Error.WriteLine(Log.On ? "The log has the full error." : "For the full error, run again with --log sf-mod-check-log.txt and look at that file.");
                SfModCheck.Log.Write($"stopped: {e}");
            });
            return 2;
        }
        finally { SfModCheck.Log.Close(); }
    }

    static int CantRun(string why, string detail)
    {
        Console.Error.WriteLine(detail);
        SfModCheck.Log.Write($"stopped: {detail}");
        Console.WriteLine($"RESULT: CANNOT DIFF ({why})");
        return 2;
    }

    static int RunLogged(string[] a, string version, string byline)
    {
        var parsed = Parse(a);
        if (parsed.Help) { Console.Error.WriteLine(Usage); return 2; }
        if (parsed.Options is not { } o)
        {
            Console.Error.WriteLine(parsed.Error);
            Console.Error.WriteLine(Usage);
            Console.WriteLine("RESULT: CANNOT DIFF (bad arguments; see the message above)");
            return 2;
        }
        if (!o.Quiet) Console.Error.WriteLine(byline);
        if (o.Verbose) SfModCheck.Log.ToStderr();
        if (LogProblem(o) is { } logProblem) return CantRun("the log file can't be used", logProblem);
        if (o.Log != null && SfModCheck.Log.ToFile(o.Log) is { } logError) return CantRun("the log file can't be written", logError);
        SfModCheck.Log.Write($"sf-mod-check {version} diff on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, .NET {Environment.Version}");
        SfModCheck.Log.Write($"arguments: {string.Join(" ", a.Select(x => x.Contains(' ') ? $"\"{x}\"" : x))}");

        if (CheckInputs(o, out var oldKind, out var newKind) is { } inputError) return CantRun("bad input; see the message above", inputError);
        var hasGame = TriageCommand.HasStarfield(o.DataDir);
        SfModCheck.Log.Write($"Data folder: {System.IO.Path.GetFullPath(o.DataDir)} ({(o.DataGiven ? "from --data" : "Steam's usual folder")}; Starfield.esm {(hasGame ? "found" : "NOT found")})");

        var work = Directory.CreateTempSubdirectory("sf-mod-check-diff-").FullName;
        SfModCheck.Log.Write($"work folder: {work}");
        try
        {
            var oldIn = new DiffInput { Path = System.IO.Path.GetFullPath(o.Old), Kind = oldKind };
            var newIn = new DiffInput { Path = System.IO.Path.GetFullPath(o.New), Kind = newKind };
            foreach (var (input, sub) in new[] { (oldIn, "old"), (newIn, "new") })
            {
                SfModCheck.Log.Write($"{sub} input: {input.Kind} {SfModCheck.Log.Describe(input.Path)}");
                try { Gather(input, System.IO.Path.Combine(work, sub), o.DataDir); }
                catch (Exception e) when (TriageCommand.IsFileSystem(e) || e is InvalidDataException or SharpCompress.Common.InvalidFormatException
                                              or SharpCompress.Common.ArchiveException or NotSupportedException or IOException or UnauthorizedAccessException)
                {
                    return CantRun($"{TriageCommand.Shown(input.Path)} can't be read", $"can't read the {sub} version {input.Path}: {e.Message}");
                }
                SfModCheck.Log.Write($"{sub} input: {input.Files.Count} files under {input.Root}; {input.Skipped.Count} skipped");
                foreach (var (f, why) in input.Skipped) SfModCheck.Log.Write($"{sub} input: {f}: {why}");
            }

            var pairing = oldIn.IsPackage ? PairPlugins(oldIn.Files, newIn.Files) : PairTwoPlugins(oldIn.Files[0], newIn.Files[0]);
            if (pairing.Error != null) return CantRun("two plugins share a name; see the message above", pairing.Error);
            if (oldIn.IsPackage) pairing = Spelled(pairing, oldIn, newIn);
            foreach (var pp in pairing.Paired) SfModCheck.Log.Write($"paired: {pp.OldRel} with {pp.NewRel}");
            foreach (var pp in pairing.Removed) SfModCheck.Log.Write($"only in the old version: {pp.OldRel} (listed as removed; its records aren't compared)");
            foreach (var pp in pairing.Added) SfModCheck.Log.Write($"only in the new version: {pp.NewRel} (listed as added; its records aren't compared)");

            var report = new DiffReport { Tool = $"sf-mod-check {version}", Old = oldIn, New = newIn, Pairing = pairing, All = o.All };
            foreach (var pp in pairing.Paired)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var oldIx = RecordIndex.Build(oldIn.Full(pp.OldRel!), MasterLookup(oldIn, o.DataDir, "old"), out var oldErr);
                if (oldIx == null) return CantRun($"{pp.Name} can't be read", $"can't compare {pp.Name}: {oldErr}");
                var newIx = RecordIndex.Build(newIn.Full(pp.NewRel!), MasterLookup(newIn, o.DataDir, "new"), out var newErr);
                if (newIx == null) return CantRun($"{pp.Name} can't be read", $"can't compare {pp.Name}: {newErr}");
                foreach (var (ix, sub) in new[] { (oldIx, "old"), (newIx, "new") })
                {
                    SfModCheck.Log.Write($"{pp.Name} ({sub}): {ix.Header.Style.ToString().ToLowerInvariant()}, {ix.Header.Masters.Count} master(s) " +
                                         $"({string.Join(", ", ix.Header.Masters.Select(m => $"{m} {ix.MasterStyles[m].ToString().ToLowerInvariant()}"))}), " +
                                         $"{ix.Walked} records, {ix.Records.Count(r => r.Key.Own)} own");
                    if (ix.UnknownMasters.Count > 0) SfModCheck.Log.Write($"{pp.Name} ({sub}): masters not found, taken as full: {string.Join(", ", ix.UnknownMasters)}");
                    if (ix.PastEnd > 0) SfModCheck.Log.Write($"{pp.Name} ({sub}): {ix.PastEnd} records name a master index past the end of its list (keyed by their stored FormID)");
                    if (ix.Duplicates > 0) SfModCheck.Log.Write($"{pp.Name} ({sub}): {ix.Duplicates} records repeat an earlier record's FormID (only the first is compared)");
                    if (ix.Unpackable > 0) SfModCheck.Log.Write($"{pp.Name} ({sub}): {ix.Unpackable} compressed records don't unpack (compared by their stored bytes)");
                }
                var d = PluginDiff.Compare(oldIx, newIx);
                foreach (var h in d.Header) SfModCheck.Log.Write($"{pp.Name}: header: {h.Text}{(h.Real ? "" : " (information only, not a difference)")}");
                foreach (var (ok, nk, orec, _) in d.Renumbered)
                    SfModCheck.Log.Write($"{pp.Name}: renumbered {orec.Sig} {orec.EditorId}: {ok.Show(pp.Name)} -> {nk.Show(pp.Name)} (same type and EditorID)");
                var byBytes = d.Changed.Count;
                SfModCheck.Log.Write($"{pp.Name}: {byBytes} records changed by bytes; reading them with Mutagen to compare their fields ({sw.Elapsed.TotalSeconds:F1} s)");
                DiffFields.Apply(d, pp.Name);
                foreach (var c in d.Changed)
                    SfModCheck.Log.Write($"{pp.Name}: changed {c.Old.Sig} {c.Key.Show(pp.Name)} {c.New.EditorId ?? "(no EditorID)"}: " +
                                         (c.NotCompared ?? string.Join(", ", c.Fields)));
                foreach (var c in d.Noise)
                    SfModCheck.Log.Write($"{pp.Name}: noise {c.Old.Sig} {c.Key.Show(pp.Name)} {c.New.EditorId ?? "(no EditorID)"}: the bytes differ, every field is equal");
                d.Details = DiffDetails.Build(d);
                var newPaths = MasterPaths(newIn, o.DataDir); var oldPaths = MasterPaths(oldIn, o.DataDir);
                var oldHeaders = MasterLookup(oldIn, o.DataDir, "old"); var newHeaders = MasterLookup(newIn, o.DataDir, "new");
                d.Details.Names = DiffNames.Resolve(d, d.Details.Keys(), m => newPaths(m) ?? oldPaths(m), m => newHeaders(m) ?? oldHeaders(m));
                SfModCheck.Log.Write($"{pp.Name}: details: {d.Details.Cells.Count} cells with changes, {d.Details.LeveledLists.Count} leveled lists with changed entries, " +
                                     $"{d.Details.BaseSwaps.Count} base-object swaps; EditorIDs read from {(d.Details.Names.Read.Count == 0 ? "no master" : string.Join(", ", d.Details.Names.Read.Select(r => r.Master)))}" +
                                     (d.Details.Names.MissingMasters.Count > 0 ? $"; masters not found for EditorIDs: {string.Join(", ", d.Details.Names.MissingMasters)}" : "") +
                                     (d.Details.Names.DamagedMasters.Count > 0 ? $"; damaged masters (some EditorIDs missing): {string.Join(", ", d.Details.Names.DamagedMasters)}" : ""));
                report.Plugins.Add(new(pp, d.Differences ? "changed" : "same", d));
                var own = d.Total(true); var ovr = d.Total(false);
                SfModCheck.Log.Write($"{pp.Name}: {(d.Differences ? "changed" : "same")}; own records {own.Added} added, {own.Removed} removed, {own.Changed} changed, " +
                                     $"{own.Noise} noise, {own.Renumbered} renumbered, {own.Same} same; overrides {ovr.Added} added, {ovr.Removed} removed, " +
                                     $"{ovr.Changed} changed, {ovr.Noise} noise, {ovr.Same} same; {d.Header.Count} header differences ({sw.Elapsed.TotalSeconds:F1} s)");
            }

            if (oldIn.IsPackage)
            {
                var fsw = System.Diagnostics.Stopwatch.StartNew();
                report.Files = FilesDiff.Compare(oldIn, newIn);
                var (fa, fr, fc) = report.Files.FileTotals();
                SfModCheck.Log.Write($"files: {fa} added, {fr} removed, {fc} changed (loose and in archives); {report.Files.Archives.Count} archives, " +
                                     $"{report.Files.Scripts.Count} changed scripts compared ({fsw.Elapsed.TotalSeconds:F1} s)");
            }
            else SfModCheck.Log.Write("files: two plugins given directly, so there are no other files to compare");

            var text = o.Json ? report.Json() : report.Text();
            if (o.Out != null)
            {
                try { File.WriteAllText(o.Out, text, new UTF8Encoding(false)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    return CantRun("the report can't be written", $"can't write the report to {System.IO.Path.GetFullPath(o.Out)}: {e.Message} Give --out a file you can write to.");
                }
                SfModCheck.Log.Write($"report written to {System.IO.Path.GetFullPath(o.Out)}");
            }
            else Console.Write(text);
            var code = report.Differences ? 1 : 0;
            Console.WriteLine(report.ResultLine(o.Out != null ? System.IO.Path.GetFullPath(o.Out) : null));
            SfModCheck.Log.Write($"result: {(report.Differences ? "differences" : "no differences")}, exit code {code}");
            return code;
        }
        finally
        {
            try { Directory.Delete(work, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { SfModCheck.Log.Write($"couldn't remove the work folder {work}: {e.Message}"); }
        }
    }

    /// <summary>Fills an input's Root, Files, Skipped and Full: a plugin is itself alone; an archive is unpacked into dest
    /// with triage's guards; a folder is read where it is, links never followed.</summary>
    public static void Gather(DiffInput input, string dest, string dataDir)
    {
        if (input.Kind == TriageInputKind.Plugin)
        {
            input.Root = System.IO.Path.GetDirectoryName(input.Path)!;
            var name = System.IO.Path.GetFileName(input.Path);
            input.Files = [name];
            var path = input.Path;
            input.Full = _ => path;
            return;
        }
        var caseKept = new Dictionary<string, string>(StringComparer.Ordinal);
        var (root, files) = TriageCommand.Gather(input.Kind, input.Path, dest, input.Skipped, caseKept);
        var ctx = new TriageContext
        {
            Options = new TriageOptions { Input = input.Path, DataDir = dataDir },
            Kind = input.Kind, InputPath = input.Path, Root = root, Files = files, DataPrefix = "",
            DataDir = dataDir, Work = dest, Report = new TriageReport(), Skipped = input.Skipped, CaseKept = caseKept,
        };
        ctx.SetAsideLinked();
        input.Root = root;
        input.Files = ctx.Files;
        input.Full = ctx.Full;
    }

    /// <summary>Finds a master's header for the record key (its style picks which master list a FormID indexes): first a
    /// plugin with that file name in the same input (a mod package's files, or the folder of a plugin given directly),
    /// then the Data folder. Null when neither has it (the master is then taken as full, and the log says so).</summary>
    public static Func<string, RawHeader?> MasterLookup(DiffInput input, string dataDir, string which)
    {
        var where = Candidates(input, dataDir);
        var cache = new Dictionary<string, RawHeader?>(StringComparer.OrdinalIgnoreCase);
        return name =>
        {
            if (cache.TryGetValue(name, out var hit)) return hit;
            RawHeader? h = null;
            foreach (var c in where(name))
            {
                if (!File.Exists(c)) continue;
                try { h = RawHeader.Read(c); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { SfModCheck.Log.Write($"{which} input: master {name} at {c} can't be read: {e.Message}"); }
                if (h != null) { SfModCheck.Log.Write($"{which} input: master {name}: {h.Style.ToString().ToLowerInvariant()} ({c})"); break; }
            }
            return cache[name] = h;
        };
    }

    /// <summary>Where a master may be, in order: a plugin with that name in the mod (or the folder of a plugin given
    /// directly), then the Data folder.</summary>
    static Func<string, List<string>> Candidates(DiffInput input, string dataDir)
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (input.IsPackage)
            foreach (var rel in input.Files.Where(IsPlugin)) byName.TryAdd(RelPath.FileName(rel)!, input.Full(rel));
        return name =>
        {
            var l = new List<string>();
            if (byName.TryGetValue(name, out var inMod)) l.Add(inMod);
            if (!input.IsPackage) l.Add(InFolder(input.Root, name));
            l.Add(InFolder(dataDir, name));
            return l;
        };
    }

    /// <summary>The path of the file <paramref name="name"/> in <paramref name="dir"/>, its case ignored: record keys hold
    /// lower-case master names, and Linux and macOS folders are case-sensitive. The plain combined path when no file in
    /// the folder matches (or the folder can't be listed), so a caller's File.Exists check still says no.</summary>
    public static string InFolder(string dir, string name)
    {
        var plain = System.IO.Path.Combine(dir, name);
        if (File.Exists(plain) || string.IsNullOrEmpty(dir) || name.IndexOfAny(['*', '?', '/', '\\']) >= 0) return plain;
        try
        {
            if (!Directory.Exists(dir)) return plain;
            return Directory.EnumerateFiles(dir, name, new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, AttributesToSkip = 0 })
                .Order(StringComparer.Ordinal).FirstOrDefault() ?? plain;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return plain; }
    }

    /// <summary>The file a master is read from for EditorIDs (the same places as MasterLookup), or null.</summary>
    public static Func<string, string?> MasterPaths(DiffInput input, string dataDir)
    {
        var where = Candidates(input, dataDir);
        return name => where(name).FirstOrDefault(File.Exists);
    }

    /// <summary>True when two files hold the same bytes (size first, then SHA256).</summary>
    public static bool SameBytes(string a, string b)
    {
        var fa = new FileInfo(a); var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return false;
        using var sa = File.OpenRead(a);
        using var sb = File.OpenRead(b);
        return SHA256.HashData(sa).AsSpan().SequenceEqual(SHA256.HashData(sb));
    }
}

/// <summary>The diff report: what the run found, as text or JSON (NOTES.md "0.11.0 diff relay" has the shapes).</summary>
public sealed class DiffReport
{
    public required string Tool { get; init; }
    public required DiffInput Old { get; init; }
    public required DiffInput New { get; init; }
    public required DiffPairing Pairing { get; init; }
    /// <summary>--all: no caps in the text report.</summary>
    public bool All { get; init; }
    /// <summary>Each paired plugin, its status ("same": no real header change and no record added, removed, changed by
    /// bytes or renumbered; else "changed") and the record compare.</summary>
    public List<(PluginPair Pair, string Status, PluginDiff Diff)> Plugins { get; } = [];

    /// <summary>Loose files, archives and scripts (mod inputs only; null for two plugins given directly).</summary>
    public FilesDiff? Files { get; set; }

    public bool Differences => Pairing.Added.Count > 0 || Pairing.Removed.Count > 0 || Plugins.Any(p => p.Status != "same") || Files?.Differences == true;

    static string Plural(int n, string what) => $"{n:N0} {what}{(n == 1 ? "" : "s")}";

    /// <summary>"732 added, 265 removed, 500 changed" for the nonzero counts, or "no differences".</summary>
    static string Counts(DiffCounts c, bool own)
    {
        var parts = new List<string>();
        if (c.Added > 0) parts.Add($"{c.Added:N0} added");
        if (c.Removed > 0) parts.Add($"{c.Removed:N0} removed");
        if (c.Changed > 0) parts.Add($"{c.Changed:N0} changed");
        if (own && c.Renumbered > 0) parts.Add($"{c.Renumbered:N0} renumbered");
        if (c.Noise > 0) parts.Add($"{c.Noise:N0} noise");
        return parts.Count == 0 ? "-" : string.Join(", ", parts);
    }

    public string Text()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Tool} diff: {TriageCommand.Shown(Old.Path)} -> {TriageCommand.Shown(New.Path)}");
        sb.AppendLine($"old: {Old.Path} ({Kind(Old.Kind)})");
        sb.AppendLine($"new: {New.Path} ({Kind(New.Kind)})");
        sb.AppendLine();
        sb.AppendLine("== Plugins ==");
        foreach (var (pp, status, _) in Plugins) sb.AppendLine($"  {status,-8} {pp.Name}");
        foreach (var pp in Pairing.Removed) sb.AppendLine($"  removed  {pp.Name} (only in the old version: {FilesDiff.Spell(Old, pp.OldRel!)})");
        foreach (var pp in Pairing.Added) sb.AppendLine($"  added    {pp.Name} (only in the new version: {FilesDiff.Spell(New, pp.NewRel!)})");
        if (Plugins.Count == 0 && Pairing.Removed.Count == 0 && Pairing.Added.Count == 0) sb.AppendLine("  (no plugins in either version)");
        foreach (var (pp, status, d) in Plugins) PluginText(sb, pp, d, All);
        Files?.Text(sb, All);
        return sb.ToString();
    }

    static void PluginText(StringBuilder sb, PluginPair pp, PluginDiff d, bool all)
    {
        sb.AppendLine();
        sb.AppendLine($"== {pp.Name} ==");
        var oh = d.Old.Header; var nh = d.New.Header;
        sb.AppendLine($"  old: {oh.Style.ToString().ToLowerInvariant()}, {Plural(oh.Masters.Count, "master")}, {Plural(d.Old.Walked, "record")} ({d.Old.Records.Count(r => r.Key.Own):N0} own)");
        sb.AppendLine($"  new: {nh.Style.ToString().ToLowerInvariant()}, {Plural(nh.Masters.Count, "master")}, {Plural(d.New.Walked, "record")} ({d.New.Records.Count(r => r.Key.Own):N0} own)");
        if (d.Header.Count == 0) sb.AppendLine("  header: the same");
        else foreach (var h in d.Header) sb.AppendLine($"  header: {h.Text}{(h.Real ? "" : " (information only)")}");
        foreach (var (ix, which) in new[] { (d.Old, "old"), (d.New, "new") })
        {
            if (ix.UnknownMasters.Count > 0)
                sb.AppendLine($"  note: the {which} version's master{(ix.UnknownMasters.Count == 1 ? "" : "s")} {string.Join(", ", ix.UnknownMasters)} " +
                              "wasn't found (not in the mod or the Data folder), so it was taken as a full master; if it is medium or light, " +
                              "own records and overrides may be told apart wrongly. Give --data the game's Data folder.");
            if (ix.PastEnd > 0) sb.AppendLine($"  note: {Plural(ix.PastEnd, "record")} in the {which} version name a master that isn't in its list (compared by stored FormID).");
            if (ix.Duplicates > 0) sb.AppendLine($"  note: {Plural(ix.Duplicates, "record")} in the {which} version repeat an earlier FormID (only the first is compared).");
            if (ix.Unpackable > 0) sb.AppendLine($"  note: {Plural(ix.Unpackable, "compressed record")} in the {which} version don't unpack (compared by their stored bytes).");
        }
        var own = d.Total(true); var ovr = d.Total(false);
        sb.AppendLine($"  own records: {Counts(own, true)}; {own.Same:N0} the same");
        sb.AppendLine($"  overrides:   {Counts(ovr, false)}; {ovr.Same:N0} the same");
        var rows = d.Types.Where(t => t.Value.Own.Any || t.Value.Overrides.Any || t.Value.Own.Noise + t.Value.Overrides.Noise > 0).ToList();
        if (rows.Count > 0)
        {
            sb.AppendLine("  by record type (changed = fields differ; noise = the bytes differ but every field is equal, which is no difference):");
            var w = Math.Max(4, rows.Max(r => r.Key.Length));
            foreach (var (sig, (o, v)) in rows)
                sb.AppendLine($"    {sig.PadRight(w)}  own: {Counts(o, true)}  |  overrides: {Counts(v, false)}");
        }
        if (d.FieldsNote != null) sb.AppendLine($"  note: fields not compared, so every record changed by bytes is listed as changed: {d.FieldsNote}");
        d.Details.Text(sb, pp.Name, all);
        if (d.Changed.Count > 0)
        {
            sb.AppendLine($"  changed records ({d.Changed.Count:N0}; the fields that differ):");
            foreach (var c in all ? d.Changed : d.Changed.Take(ChangedShown))
                sb.AppendLine($"    {c.Old.Sig} {c.Key.Show(pp.Name)} {c.New.EditorId ?? "(no EditorID)"}{(c.Key.Own ? "" : " (override)")}: " +
                              (c.NotCompared ?? string.Join(", ", c.Fields)));
            if (!all && d.Changed.Count > ChangedShown) sb.AppendLine($"    ... and {d.Changed.Count - ChangedShown:N0} more (--all or --json lists them all)");
        }
        if (d.Renumbered.Count > 0)
        {
            sb.AppendLine($"  renumbered records ({d.Renumbered.Count:N0}; same type and EditorID, new FormID):");
            foreach (var (ok, nk, orec, _) in all ? d.Renumbered : d.Renumbered.Take(ChangedShown))
                sb.AppendLine($"    {orec.Sig} {orec.EditorId}: {ok.Show(pp.Name)} -> {nk.Show(pp.Name)}");
            if (!all && d.Renumbered.Count > ChangedShown) sb.AppendLine($"    ... and {d.Renumbered.Count - ChangedShown:N0} more (--all or --json lists them all)");
        }
    }

    /// <summary>How many changed records the text report lists per plugin (--all and the JSON list all).</summary>
    public const int ChangedShown = 100;

    static string Kind(TriageInputKind k) => k switch
    {
        TriageInputKind.Plugin => "plugin",
        TriageInputKind.Folder => "folder",
        TriageInputKind.Zip => "zip",
        TriageInputKind.SevenZip => "7z",
        TriageInputKind.Rar => "rar",
        _ => k.ToString(),
    };

    public string Json()
    {
        JsonObject Side(DiffInput i) => new() { ["path"] = i.Path, ["kind"] = Kind(i.Kind), ["files"] = i.Files.Count, ["skipped"] = i.Skipped.Count };
        var plugins = new JsonArray();
        foreach (var (pp, status, d) in Plugins)
        {
            var po = new JsonObject
            {
                ["name"] = pp.Name, ["status"] = status, ["old"] = FilesDiff.Spell(Old, pp.OldRel!), ["new"] = FilesDiff.Spell(New, pp.NewRel!),
                ["header"] = HeaderJson(d), ["records"] = RecordsJson(d), ["types"] = TypesJson(d),
                ["fields_not_compared"] = d.FieldsNote,
                ["changed"] = new JsonArray(d.Changed.Select(c => (JsonNode)new JsonObject
                {
                    ["key"] = c.Key.Show(pp.Name), ["type"] = c.Old.Sig, ["editor_id"] = c.New.EditorId, ["override"] = !c.Key.Own,
                    ["fields"] = new JsonArray(c.Fields.Select(f => (JsonNode)f).ToArray()), ["not_compared"] = c.NotCompared,
                }).ToArray()),
                ["renumbered"] = new JsonArray(d.Renumbered.Select(r => (JsonNode)new JsonObject
                {
                    ["type"] = r.Old.Sig, ["editor_id"] = r.Old.EditorId, ["old_key"] = r.OldKey.Show(pp.Name), ["new_key"] = r.NewKey.Show(pp.Name),
                }).ToArray()),
            };
            d.Details.Json(po, pp.Name);
            plugins.Add(po);
        }
        foreach (var pp in Pairing.Removed) plugins.Add(new JsonObject { ["name"] = pp.Name, ["status"] = "removed", ["old"] = FilesDiff.Spell(Old, pp.OldRel!), ["new"] = null });
        foreach (var pp in Pairing.Added) plugins.Add(new JsonObject { ["name"] = pp.Name, ["status"] = "added", ["old"] = null, ["new"] = FilesDiff.Spell(New, pp.NewRel!) });
        var root = new JsonObject
        {
            ["schema"] = DiffCommand.JsonSchema,
            ["tool"] = Tool,
            ["old"] = Side(Old),
            ["new"] = Side(New),
            ["plugins"] = plugins,
            ["files"] = null, ["archives"] = null, ["scripts"] = null,
        };
        Files?.Json(root);
        root["differences"] = Differences;
        root["exit_code"] = Differences ? 1 : 0;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
    }

    static JsonObject CountsJson(DiffCounts c) => new()
    {
        ["added"] = c.Added, ["removed"] = c.Removed, ["changed"] = c.Changed, ["noise"] = c.Noise, ["renumbered"] = c.Renumbered, ["same"] = c.Same,
    };

    static JsonObject HeaderJson(PluginDiff d)
    {
        JsonObject Side(RecordIndex ix) => new()
        {
            ["type"] = ix.Header.Style.ToString().ToLowerInvariant(),
            ["flags"] = ix.Header.Flags.ToString("X8"),
            ["form_version"] = ix.Header.FormVersion,
            ["masters"] = new JsonArray(ix.Header.Masters.Select(m => (JsonNode)new JsonObject
                { ["name"] = m, ["type"] = ix.MasterStyles[m].ToString().ToLowerInvariant(), ["found"] = !ix.UnknownMasters.Contains(m) }).ToArray()),
            ["hedr_records"] = ix.Header.NumRecords,
        };
        return new JsonObject
        {
            ["old"] = Side(d.Old), ["new"] = Side(d.New),
            ["changes"] = new JsonArray(d.Header.Select(h => (JsonNode)new JsonObject { ["text"] = h.Text, ["real"] = h.Real }).ToArray()),
        };
    }

    static JsonObject RecordsJson(PluginDiff d)
    {
        JsonObject Side(RecordIndex ix) => new()
        {
            ["records"] = ix.Walked, ["own"] = ix.Records.Count(r => r.Key.Own), ["overrides"] = ix.Records.Count(r => !r.Key.Own),
            ["past_end"] = ix.PastEnd, ["duplicates"] = ix.Duplicates, ["unpackable"] = ix.Unpackable,
        };
        return new JsonObject { ["old"] = Side(d.Old), ["new"] = Side(d.New), ["own"] = CountsJson(d.Total(true)), ["overrides"] = CountsJson(d.Total(false)) };
    }

    static JsonArray TypesJson(PluginDiff d) =>
        new(d.Types.Select(t => (JsonNode)new JsonObject { ["type"] = t.Key, ["own"] = CountsJson(t.Value.Own), ["overrides"] = CountsJson(t.Value.Overrides) }).ToArray());

    public string ResultLine(string? outFile)
    {
        int changed = Plugins.Count(p => p.Status != "same");
        var what = $"{Plugins.Count} plugin{(Plugins.Count == 1 ? "" : "s")} compared, {changed} changed, {Pairing.Added.Count} added, {Pairing.Removed.Count} removed";
        if (Plugins.Count > 0)
        {
            var c = new DiffCounts();
            foreach (var (_, _, d) in Plugins)
                foreach (var own in new[] { true, false })
                {
                    var t = d.Total(own);
                    c.Added += t.Added; c.Removed += t.Removed; c.Changed += t.Changed; c.Noise += t.Noise; c.Renumbered += t.Renumbered;
                }
            what += $"; records: {c.Added} added, {c.Removed} removed, {c.Changed} changed, {c.Renumbered} renumbered, {c.Noise} noise";
        }
        if (Files != null)
        {
            var (fa, fr, fc) = Files.FileTotals();
            what += $"; files: {fa} added, {fr} removed, {fc} changed";
        }
        var where = outFile != null ? $" {outFile}" : "";
        return Differences ? $"RESULT: DIFFERENCES ({what}){where}" : $"RESULT: NO DIFFERENCES ({what}){where}";
    }
}
