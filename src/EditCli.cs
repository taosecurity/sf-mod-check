// `sf-mod-check edit` (0.25.0), plan step 5: the command line, the text report and --json around EditProof.Run
// (EditCommand.cs). Design: NOTES.md, "## 0.25.0 (SmcDialogue)", "Step 1 design".
//   sf-mod-check edit <plugin> --out <new.esm> [--drop-master <name>] [--set-light]
//                     [--set-text <record> --text "<text>" [--response N]]... [--data <dir>] [--masters-dir <dir>]...
//                     [--json] [--quiet] [--verbose|--log <file>]
// Exit 0 = kept, 1 = a proof failed (the new file deleted), 2 = refused or couldn't run.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SfModCheck;

/// <summary>The edit command's arguments, as parsed.</summary>
public sealed class EditArgs
{
    public string Input = "";
    public string? Out;
    public EditPlan Plan = new();
    public string DataDir = Cli.SteamData;
    public bool DataGiven;
    public List<string> MastersDirs = [];
    public bool Json, Quiet, Verbose;
    public string? Log;
}

public static class EditCommand
{
    public const string Usage =
        "usage: sf-mod-check edit <plugin> --out <new plugin> [--drop-master <master>] [--set-light]\n" +
        "                         [--set-text <INFO or MESG> --text \"<new text>\" [--response <n>]]...\n" +
        "                         [--data <Data folder>] [--masters-dir <folder>]... [--json] [--quiet] [--verbose] [--log <file>]\n" +
        "  Makes a few safe changes to a finished plugin and writes them to a NEW file. The plugin itself is never changed.\n" +
        "  The new file is kept only when three proofs pass on it: diff (only the intended changes), gap (no new loss) and\n" +
        "  check (no new ERROR). Otherwise it is deleted, and the proof that failed says why.\n" +
        "  --out          the new plugin file; it must not exist yet, and it can't be the input\n" +
        "  --drop-master  remove a master the plugin doesn't use (the FormIDs after it are renumbered); refused if anything uses it\n" +
        "  --set-light    set the light flag; refused unless the plugin is full and its own records are 000800 to 000FFF\n" +
        "  --set-text     replace one line's text: an INFO's response text (NAM1) or a MESG's description (DESC), named by\n" +
        "                 EditorID, FormKey (000801:MyMod.esm) or console FormID. Give the new text with --text after it,\n" +
        "                 and --response <n> for an INFO's other responses (numbered from 1, as `find --full` shows them).\n" +
        "                 Repeat --set-text for more records (one line per record in a run). A localized plugin is\n" +
        "                 refused (its text is in .STRINGS files).\n" +
        "  --data         the game's Data folder, for the masters (default: Steam's usual folder)\n" +
        "  --masters-dir  look for masters here first (default: the plugin's own folder), then in the Data folder\n" +
        "  --json         print one JSON object: the edits, each proof's result and whether the new file was kept\n" +
        "  --quiet        leave out the tool's name and version line\n" +
        "  --verbose      also print the log (each step and proof in detail) to stderr\n" +
        "  --log <file>   write that log to a file, for a bug report\n" +
        "Exit 0 = the new file was kept, 1 = a proof failed (nothing kept), 2 = refused or could not run.";

    public const string Example = "sf-mod-check edit MyMod.esm --out MyMod-fixed.esm --set-light";
    const string Tool = "sf-mod-check edit";
    const string TextExample = "--set-text MyMod_GreetingInfo --text \"The new line.\"";
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    /// <summary>edit's own option names: a --text followed by one of these is a missing text, not the text.</summary>
    static readonly HashSet<string> OptionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "--out", "--drop-master", "--set-light", "--set-text", "--response", "--text", "--data", "--masters-dir",
        "--log", "--json", "--quiet", "--verbose", "-h", "--help",
    };

    /// <summary>The arguments, or null with an error text (exit 2), or null with help set.</summary>
    public static EditArgs? Parse(string[] a, out string? error, out bool help)
    {
        error = null; help = false;
        var o = new EditArgs();
        string? input = null;
        TextEdit? open = null;      // the last --set-text, until its --text comes
        bool lastHasResponse = false;
        for (int i = 0; i < a.Length; i++)
        {
            string? Value() =>
                i + 1 < a.Length && !a[i + 1].StartsWith("--") && a[i + 1].Trim().Length > 0 ? a[++i] : null;
            string Needs(string what, string example) =>
                $"{a[i]} needs {what} after it{(i + 1 < a.Length && a[i + 1].Trim().Length == 0 ? " (it was given an empty value)" : "")}, for example: {a[i]} {example}";
            switch (a[i])
            {
                case "--out":
                    if (Value() is not { } of) { error = Needs("the new plugin's file name", "MyMod-fixed.esm"); return null; }
                    if (o.Out != null) { error = "give --out once: edit writes one new file."; return null; }
                    o.Out = of; break;
                case "--drop-master":
                    if (Value() is not { } dm) { error = Needs("the master's file name", "UnusedMaster.esm"); return null; }
                    if (o.Plan.DropMaster != null)
                    {
                        error = $"give --drop-master once per run (got {SafeText.OneLine(o.Plan.DropMaster)} and {SafeText.OneLine(dm)}). Run edit again on the new file for the next one.";
                        return null;
                    }
                    o.Plan.DropMaster = dm.Trim(); break;
                case "--set-light": o.Plan.SetLight = true; break;
                case "--set-text":
                    if (open != null) { error = NoText(open); return null; }
                    if (Value() is not { } rec) { error = Needs("the INFO or MESG to change (EditorID, FormKey or console FormID)", "MyMod_GreetingInfo --text \"The new line.\""); return null; }
                    open = new TextEdit(rec.Trim(), 1, "");
                    lastHasResponse = false;
                    break;
                case "--response":
                    if (lastHasResponse) { error = "give --response once for each --set-text."; return null; }
                    if (open == null && o.Plan.Texts.Count == 0)
                    {
                        error = $"--response belongs to a --set-text: give it after --set-text <record>, for example: --set-text MyMod_GreetingInfo --response 2 --text \"The new line.\"";
                        return null;
                    }
                    if (Value() is not { } rs) { error = Needs("a response number (from 1)", "2"); return null; }
                    if (!int.TryParse(rs, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
                    {
                        error = $"--response needs a whole number (responses are numbered from 1, as `find --full` shows them), not \"{SafeText.OneLine(rs)}\".";
                        return null;
                    }
                    if (open != null) open = open with { Response = n };
                    else o.Plan.Texts[^1] = o.Plan.Texts[^1] with { Response = n };
                    lastHasResponse = true;
                    break;
                case "--text":
                    if (open == null) { error = $"--text belongs to a --set-text: give the record first, for example: {TextExample}"; return null; }
                    // the next argument is the text as it is: it may start with dashes, and "" empties the line
                    if (i + 1 >= a.Length) { error = Needs("the new text in quotes", "\"The new line.\""); return null; }
                    if (OptionNames.Contains(a[i + 1].Trim()))
                    {
                        error = $"--text needs the new text after it, but the next argument is the option {SafeText.OneLine(a[i + 1].Trim())}. Give the text in quotes, for example: {TextExample} (a line that is exactly an option name can't be given).";
                        return null;
                    }
                    var tx = a[++i];
                    o.Plan.Texts.Add(open with { Text = tx });
                    open = null;
                    break;
                case "--data":
                    if (Value() is not { } d) { error = Needs("the game's Data folder", Cli.Example("--data")); return null; }
                    o.DataDir = d; o.DataGiven = true; break;
                case "--masters-dir":
                    if (Value() is not { } md) { error = Needs("a folder", Cli.Example("--masters-dir")); return null; }
                    o.MastersDirs.Add(md); break;
                case "--log":
                    if (Value() is not { } lf) { error = Needs("a file name", "edit-log.txt"); return null; }
                    o.Log = lf; break;
                case "--json": o.Json = true; break;
                case "--quiet": o.Quiet = true; break;
                case "--verbose": o.Verbose = true; break;
                case "-h": case "--help": help = true; return null;
                default:
                    if (a[i].StartsWith("--")) { error = $"unknown option {SafeText.OneLine(a[i])}"; return null; }
                    if (a[i].Trim().Length == 0) { error = $"the plugin name is empty: give the plugin to edit, for example: {Example}"; return null; }
                    if (input != null)
                    {
                        error = $"edit takes one plugin per run: got {SafeText.OneLine(input)} and {SafeText.OneLine(a[i])}. A path with spaces needs quotes around it, and new text goes after --text.";
                        return null;
                    }
                    input = a[i];
                    break;
            }
        }
        if (open != null) { error = NoText(open); return null; }
        if (input == null) { error = $"give the plugin to edit, for example: {Example}"; return null; }
        o.Input = input;
        return o;
    }

    static string NoText(TextEdit open) =>
        $"--set-text {SafeText.OneLine(open.Record)} has no --text after it: give the new text, for example: --set-text {SafeText.OneLine(open.Record)} --text \"The new line.\"";

    static bool ProtectedName(string p) =>
        Path.GetExtension(p).ToLowerInvariant() is ".esm" or ".esp" or ".esl" or ".ba2" or ".zip" or ".7z" or ".rar" or ".pex";

    /// <summary>Why --log can't be used, or null. Checked before the log is opened, so a bad --log never truncates
    /// anything: not a folder, not a plugin, archive or script name, not an NTFS stream, and not the input or --out
    /// (by full path and by real path, through links).</summary>
    public static string? LogProblem(string log, string input, string? outPath)
    {
        const string example = "for example --log edit-log.txt";
        string full;
        try { full = Path.GetFullPath(log); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"can't use --log \"{SafeText.OneLine(log)}\": {e.Message} Give --log a .txt file, {example}";
        }
        var shown = SafeText.OneLine(full);
        if (Directory.Exists(full)) return $"can't use --log {shown}: that is a folder. Give --log a file name, {example}";
        if (OperatingSystem.IsWindows() && DiffCommand.HasStreamColon(full))
            return $"won't write the log to {shown}: a ':' after the drive writes into another file or folder (an NTFS stream). Give --log a plain path, {example}";
        if (ProtectedName(full)) return $"won't write the log to {shown}: that name is a plugin, archive or script. Give --log a .txt file, {example}";
        if (DiffCommand.RealPath(full, out var why, out var kind) is not { } real)
            return $"can't use --log {shown}: {why}, so edit can't make sure it isn't the input or --out. {DiffCommand.PathHint(kind, "--log")}, {example}";
        if (ProtectedName(real))
            return $"won't write the log to {shown}: its real path is {SafeText.OneLine(real)}, a plugin, archive or script. Give --log a .txt file, {example}";
        foreach (var (other, name) in new[] { (input, "the input"), (outPath, "--out") })
        {
            if (string.IsNullOrWhiteSpace(other)) continue;
            string otherFull;
            try { otherFull = Path.GetFullPath(other); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { continue; }   // refused later, with its own message
            if (string.Equals(full, otherFull, StringComparison.OrdinalIgnoreCase)
                || DiffCommand.RealPath(otherFull) is { } otherReal && string.Equals(real, otherReal, StringComparison.OrdinalIgnoreCase))
                return $"won't write the log to {shown}: that is {name}. Give --log another file, {example}";
        }
        return null;
    }

    public static int Run(string[] a, string version, string byline) => Run(a, version, byline, Console.Out, Console.Error);

    /// <summary>The command, writing to the given streams (the tests run it in process, for the CorruptForTest hook).</summary>
    public static int Run(string[] a, string version, string byline, TextWriter stdout, TextWriter stderr)
    {
        bool json = a.Contains("--json");
        string input = "", outPath = "";
        try { return RunLogged(a, version, byline, stdout, stderr, ref json, ref input, ref outPath); }
        catch (Exception e)
        {
            // anything not caught on the way (a damaged plugin Mutagen can't read, a locked file): one RESULT line, exit 2.
            // EditProof.Run has already deleted its temporary file.
            stdout.WriteLine(Fatal.ResultLine(e, () =>
            {
                var why = $"{e.GetType().Name}: {SafeText.OneLine(e.Message.Split('\n')[0])}";
                return json ? StopJson(version, input, outPath, "cannot run", why, null) : $"RESULT: CANNOT EDIT ({why})";
            }));
            Fatal.AfterResult(() =>
            {
                stderr.WriteLine(Log.On ? "The log has the full error." : "For the full error, run again with --log edit-log.txt and look at that file.");
                Log.Write($"stopped: {e}");
            });
            return 2;
        }
        finally { Log.Close(); }
    }

    static string StopJson(string version, string input, string outPath, string result, string error, string? fix)
    {
        var o = new JsonObject
        {
            ["tool"] = Tool, ["version"] = version, ["input"] = input, ["out"] = outPath,
            ["edits"] = new JsonArray(), ["proof"] = new JsonObject(), ["kept"] = false,
            ["result"] = result, ["error"] = error,
        };
        if (fix != null) o["fix"] = fix;
        return o.ToJsonString(Indented);
    }

    static string SafeFullPath(string p)
    {
        try { return Path.GetFullPath(p); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return p; }
    }

    static int RunLogged(string[] a, string version, string byline, TextWriter stdout, TextWriter stderr, ref bool json, ref string input, ref string outPath)
    {
        var o = Parse(a, out var error, out var help);
        if (help) { stdout.WriteLine(Usage); return 0; }   // asked-for help: stdout, exit 0, no RESULT line
        if (o == null)
        {
            stderr.WriteLine(error);
            stderr.WriteLine(Usage);
            stdout.WriteLine(json ? StopJson(version, input, outPath, "cannot run", "bad arguments: " + error, null) : "RESULT: CANNOT EDIT (bad arguments; see the message above)");
            return 2;
        }
        // parsed: from here the format is the parsed --json (a "--json" given as --text's value is text, not the option)
        var asJson = json = o.Json;
        input = o.Input; outPath = o.Out ?? "";
        var inShown = input; var outShown = outPath;
        int Stop(string why, string detail, string? fix = null, string result = "cannot run")
        {
            stderr.WriteLine(detail);
            if (fix != null) stderr.WriteLine(fix);
            Log.Write($"stopped: {detail}{(fix != null ? " " + fix : "")}");
            stdout.WriteLine(asJson ? StopJson(version, inShown, outShown, result, why, fix)
                                  : $"RESULT: {(result == "refused" ? "REFUSED" : "CANNOT EDIT")} ({SafeText.OneLine(why)})");
            return 2;
        }
        if (!o.Quiet && !o.Json) stderr.WriteLine(byline);
        if (o.Verbose) Log.ToStderr();
        if (o.Log != null)
        {
            if (LogProblem(o.Log, o.Input, o.Out) is { } lp) return Stop("the log file can't be used", lp);
            if (Log.ToFile(o.Log) is { } le) return Stop("the log file can't be written", le);
        }
        Log.Write($"sf-mod-check {version} edit on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, .NET {Environment.Version}");
        Log.Write($"arguments: {string.Join(" ", a.Select(x => x.Contains(' ') ? $"\"{x}\"" : x))}");
        Log.Write($"working folder: {Environment.CurrentDirectory}");

        foreach (var md in o.MastersDirs)
            if (!Directory.Exists(md))
                return Stop($"the --masters-dir folder {SafeFullPath(md)} doesn't exist",
                    $"can't use --masters-dir {SafeFullPath(md)}: that folder doesn't exist. Check the path (a path with spaces needs quotes around it).");
        if (o.DataGiven && !Directory.Exists(o.DataDir))
            return Stop($"the Data folder {SafeFullPath(o.DataDir)} doesn't exist",
                $"can't use --data {SafeFullPath(o.DataDir)}: that folder doesn't exist. Give the game's Data folder, the one that holds Starfield.esm (a path with spaces needs quotes around it).");
        var dataDir = o.DataDir;
        var dataWhy = o.DataGiven ? "from --data" : "Steam's usual folder (no --data given)";
        if (!o.DataGiven && !TriageCommand.HasStarfield(dataDir))
        {
            // no game in Steam's usual place: the plugin's own folder stands in (masters are looked for there first anyway)
            try
            {
                if (Path.GetDirectoryName(Path.GetFullPath(o.Input)) is { } own && Directory.Exists(own))
                {
                    dataDir = own;
                    dataWhy = "the plugin's own folder (Steam's usual folder has no Starfield.esm; give --data for another one)";
                }
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { /* the input is refused below */ }
        }
        Log.Write($"Data folder: {SafeFullPath(dataDir)}: {dataWhy}");
        Log.Write($"edits asked for: {(o.Plan.DropMaster != null ? $"drop-master {o.Plan.DropMaster}; " : "")}{(o.Plan.SetLight ? "set-light; " : "")}{o.Plan.Texts.Count} set-text");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        EditOutcome outcome;
        try { outcome = EditProof.Run(o.Input, o.Out, o.Plan, dataDir, o.MastersDirs); }
        catch (EditRefusedException r)
        {
            inShown = SafeFullPath(o.Input); outShown = o.Out != null ? SafeFullPath(o.Out) : "";
            // a reason can carry names read from the plugin (masters, signatures): one line, no control characters
            var reason = SafeText.OneLine(r.Reason);
            return Stop(reason, $"edit refused: {reason}", SafeText.OneLine(r.Fix), "refused");
        }
        input = outcome.Input; outPath = outcome.Out;
        var secs = sw.Elapsed.TotalSeconds;
        Log.Write($"edit: {(outcome.Kept ? "kept" : "not kept")}, exit code {(outcome.Kept ? 0 : 1)}, {secs:F1} s");
        stdout.WriteLine(json ? Json(version, outcome) : Text(version, outcome, secs));
        return outcome.Kept ? 0 : 1;
    }

    /// <summary>The text report: each change, each proof, kept or not, then one RESULT line.</summary>
    public static string Text(string version, EditOutcome o, double seconds)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"sf-mod-check {version} edit: {SafeText.OneLine(Path.GetFileName(o.Input))} -> {SafeText.OneLine(Path.GetFileName(o.Out))}");
        foreach (var c in o.Edit.Changes)
            sb.AppendLine($"edit {c.Edit}{(c.Record != null ? " " + SafeText.OneLine(c.Record) : "")}: {SafeText.OneLine(c.What)}");
        foreach (var p in o.Proof)
            sb.AppendLine($"proof {p.Name}: {(p.Passed ? "passed" : "FAILED")}: {SafeText.OneLine(p.Reason)}");
        if (o.Kept)
        {
            sb.AppendLine($"kept {SafeText.OneLine(o.Out)}");
            sb.Append($"RESULT: KEPT ({o.Proof.Count} proofs passed; {SafeText.OneLine(Path.GetFileName(o.Out))}; {seconds:F1} s)");
        }
        else
        {
            var f = o.Failed;
            sb.AppendLine($"not kept: {FailedText(f)}. The new file was deleted; the input is unchanged.");
            sb.Append($"RESULT: NOT KEPT (the {f?.Name ?? "edit"} proof failed; {seconds:F1} s)");
        }
        return sb.ToString();
    }

    static string FailedText(ProofResult? f) => f == null ? "a proof didn't run" : $"the {f.Name} proof failed: {SafeText.OneLine(f.Reason)}";

    /// <summary>The --json report: tool, version, input, out, edits (each with what it changed), proof (diff, gap and
    /// check, each with passed and reason), kept, and result ("kept" or "not kept", with error naming the failed proof).</summary>
    public static string Json(string version, EditOutcome o)
    {
        var proof = new JsonObject();
        foreach (var p in o.Proof) proof[p.Name] = new JsonObject { ["passed"] = p.Passed, ["reason"] = p.Reason };
        var root = new JsonObject
        {
            ["tool"] = Tool,
            ["version"] = version,
            ["input"] = o.Input,
            ["out"] = o.Out,
            ["edits"] = new JsonArray(o.Edit.Changes.Select(c => (JsonNode)new JsonObject
            {
                ["edit"] = c.Edit, ["record"] = c.Record, ["changed"] = c.What,
            }).ToArray()),
            ["proof"] = proof,
            ["kept"] = o.Kept,
            ["result"] = o.Kept ? "kept" : "not kept",
        };
        if (!o.Kept) root["error"] = FailedText(o.Failed);
        return root.ToJsonString(Indented);
    }
}
