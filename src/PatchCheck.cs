// sf-mod-check patch check <patch> (0.15.0): is a conflict patch still up to date for the load order? It reads the
// patch-sources.json that `patch` wrote beside the patch and compares it with the load order as it is now. Read only.
//   sf-mod-check patch check <patch.esm> [--manifest <patch-sources.json>] [--plugins <Plugins.txt>] [--data <Data folder>]
//                            [--extra-data <folder>] [--json <file>] [--quiet] [--verbose] [--log <file>]
// Findings (LEVEL [rule] text): PROBLEM [removed], REVIEW [moved], [updated], [new-editor], [duplicate], [patch-changed],
// NOTE [updated] and [patch-off]. Ends with RESULT: UP TO DATE, RESULT: REBUILD (n reasons) or RESULT: CANNOT CHECK (why).
// Exit 0 = up to date (NOTEs only), 1 = something to rebuild or review, 2 = can't check.
// Ports the private patch_sources.py check; the record hashes in the manifest replace its old-copy archive search.
using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using Mutagen.Bethesda.Plugins;
using SfModCheck.Conflict;

namespace SfModCheck;

public static class PatchCheck
{
    public const string Usage =
        "usage: sf-mod-check patch check <patch.esm> [--manifest <patch-sources.json>] [--plugins <Plugins.txt>] [--data <Data folder>]\n" +
        "                                [--extra-data <folder>] [--json <file>] [--quiet] [--verbose] [--log <file>]";

    /// <summary>How many FormKeys a finding's text shows; the JSON lists them all.</summary>
    public const int Shown = 5;

    public sealed record Finding(string Level, string Rule, string? Plugin, string Text, List<string> Records);
    public sealed record Report(string Patch, string State, string Result, List<Finding> Findings, string? Why = null);

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // a local file, never put in a web page
    };

    public static int Run(string[] a, string version, string byline)
    {
        string? jsonPath = null, patchName = null;
        try { return RunLogged(a, version, byline, ref jsonPath, ref patchName); }
        catch (Exception e)
        {
            var why = $"{e.GetType().Name}: {SafeText.OneLine(e.Message.Split('\n')[0])}";
            Console.WriteLine(Fatal.ResultLine(e, () => $"RESULT: CANNOT CHECK ({e.GetType().Name}: {SafeText.OneLine(e.Message.Split('\n')[0])})"));
            Fatal.AfterResult(() =>
            {
                Console.Error.WriteLine(Log.On ? "The log has the full error." : "For the full error, run again with --log sf-mod-check-log.txt and look at that file.");
                Log.Write($"stopped: {e}");
                WriteJson(jsonPath, CantReport(patchName, why));
            });
            return 2;
        }
        finally { Log.Close(); }
    }

    static Report CantReport(string? patch, string why) => new(patch ?? "", "cannot check", $"RESULT: CANNOT CHECK ({why})", new(), why);

    static void WriteJson(string? path, Report r)
    {
        if (path is null) return;
        try { File.WriteAllText(path, JsonSerializer.Serialize(r, Json) + "\n"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"can't write --json {Path.GetFullPath(path)}: {e.Message} Check the folder exists and isn't read-only.");
        }
    }

    static int RunLogged(string[] a, string version, string byline, ref string? jsonPath, ref string? patchName)
    {
        string? patch = null, manifestPath = null, pluginsTxt = null, dataDir = null, extraDir = null, logFile = null;
        bool quiet = false, verbose = false;
        string? json = null;
        int BadArgs(string message)
        {
            Console.Error.WriteLine(message);
            Console.Error.WriteLine(Usage);
            Console.WriteLine("RESULT: CANNOT CHECK (bad arguments; see the message above)");
            return 2;
        }
        for (int i = 0; i < a.Length; i++)
        {
            string? Value(string what)
            {
                if (i + 1 < a.Length && !a[i + 1].StartsWith("--") && a[i + 1].Trim().Length > 0) return a[++i];
                return null;
            }
            string Needs(string what) => $"{a[i]} needs {what} after it, for example: {a[i]} {Cli.Example(a[i])}";
            switch (a[i])
            {
                case "--manifest":
                    if (Value("a patch-sources.json file") is not { } m) return BadArgs(Needs("a patch-sources.json file"));
                    manifestPath = m; break;
                case "--plugins":
                    if (Value("a Plugins.txt file") is not { } p) return BadArgs(Needs("a Plugins.txt file"));
                    pluginsTxt = p; break;
                case "--data":
                    if (Value("the game's Data folder") is not { } d) return BadArgs(Needs("the game's Data folder"));
                    dataDir = d; break;
                case "--extra-data":
                    if (Value("a folder") is not { } x) return BadArgs(Needs("a folder"));
                    extraDir = x; break;
                case "--json":
                    if (Value("a file name") is not { } j) return BadArgs(Needs("a file name"));
                    json = j; break;
                case "--log":
                    if (Value("a file name") is not { } l) return BadArgs(Needs("a file name"));
                    logFile = l; break;
                case "--quiet": quiet = true; break;
                case "--verbose": verbose = true; break;
                case "-h": case "--help": Console.WriteLine(Usage); return 0;   // asked-for help: stdout, exit 0, no RESULT line
                default:
                    if (a[i].StartsWith('-')) return BadArgs($"unknown option {a[i]}");
                    if (a[i].Trim().Length == 0) return BadArgs("the patch name is empty: give the patch, for example: sf-mod-check patch check sf-mod-check-patch\\ConflictPatch.esm");
                    if (patch != null) return BadArgs($"patch check reads one patch per run, not {patch} and {a[i]} (a path with spaces needs quotes around it)");
                    patch = a[i]; break;
            }
        }
        if (patch == null) return BadArgs("give the patch to check, for example: sf-mod-check patch check sf-mod-check-patch\\ConflictPatch.esm");
        jsonPath = json;
        patchName = Path.GetFileName(patch);
        if (!quiet) Console.Error.WriteLine(byline);
        if (verbose) Log.ToStderr();
        string? jsonOut = json;
        int Cant(string why, string detail)
        {
            Console.Error.WriteLine(detail);
            Log.Write($"stopped: {detail}");
            var r = CantReport(Path.GetFileName(patch), why);
            Console.WriteLine(r.Result);
            WriteJson(jsonOut, r);
            return 2;
        }
        if (logFile != null && Log.ToFile(logFile) is { } logError) return Cant("the log file can't be written", logError);
        Log.Write($"sf-mod-check {version} patch check on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, .NET {Environment.Version}");
        Log.Write($"arguments: {string.Join(" ", a.Select(x => x.Contains(' ') ? $"\"{x}\"" : x))}");
        Log.Write($"working folder: {Environment.CurrentDirectory}");

        if (!File.Exists(patch))
            return Cant("the patch doesn't exist", $"{Path.GetFullPath(patch)} doesn't exist. Give the patch file that sf-mod-check patch wrote (with quotes around a path with spaces).");
        patch = Path.GetFullPath(patch);
        manifestPath = Path.GetFullPath(manifestPath ?? Path.Combine(Path.GetDirectoryName(patch)!, PatchSources.FileName));
        if (!File.Exists(manifestPath))
            return Cant("no patch-sources.json",
                $"{manifestPath} doesn't exist, so there is nothing to check the patch against. sf-mod-check patch (0.15.0 or later) writes it beside the patch: " +
                "make the patch again with it, or give the manifest's path with --manifest.");
        Log.Write($"manifest: {manifestPath}");
        if (PatchSources.Read(manifestPath, out var badWhy) is not { } man)
            return Cant("the manifest can't be read", $"{manifestPath} isn't a patch-sources.json: {badWhy}. Make the patch again with sf-mod-check patch, which writes a new one.");
        if (man.Schema != PatchSources.Schema)
            return Cant($"manifest schema {man.Schema} is unknown",
                $"{manifestPath} has schema {man.Schema}; this version of sf-mod-check reads schema {PatchSources.Schema}. Use the sf-mod-check that made the patch, or make the patch again with this one.");
        if (man.Patch?.Name is null || man.LoadOrder?.Plugins is null || man.Masters is null || man.Records is null
            || man.Masters.Any(m => m?.Name is null) || man.Records.Any(r => r?.FormKey is null || r.Editors is null))
            return Cant("the manifest is incomplete", $"{manifestPath} is missing parts of a patch-sources.json (patch, loadOrder, masters or records). Make the patch again with sf-mod-check patch.");
        var keys = new HashSet<FormKey>();
        foreach (var r in man.Records)
        {
            if (!FormKey.TryFactory(r.FormKey, out var fk))
                return Cant("the manifest has a bad FormKey", $"{manifestPath}: \"{SafeText.OneLine(r.FormKey)}\" isn't a FormKey such as 000800:Base.esm. Make the patch again with sf-mod-check patch.");
            keys.Add(fk);
        }
        if (Squash(man.Patch.Name) != Squash(Path.GetFileName(patch)))
            return Cant("the manifest describes another patch",
                $"{manifestPath} describes {SafeText.OneLine(man.Patch.Name)}, not {Path.GetFileName(patch)}. Give the patch it describes, or that patch's manifest with --manifest.");

        var dataWhy = dataDir != null ? "from --data" : "Steam's usual folder (no --data given)";
        dataDir ??= Cli.SteamData;
        if (!Directory.Exists(dataDir))
            return Cant($"the Data folder {Path.GetFullPath(dataDir)} doesn't exist",
                $"can't find the game's Data folder: {Path.GetFullPath(dataDir)} doesn't exist ({dataWhy}). Run again with " +
                "--data \"<the game's Data folder>\" (Game Pass: ...\\Starfield\\Content\\Data; a path with spaces needs quotes).");
        dataDir = Path.GetFullPath(dataDir);
        Log.Write($"Data folder: {dataDir}: {dataWhy}");
        string pluginsWhy;
        if (pluginsTxt != null) pluginsWhy = "from --plugins";
        else if (File.Exists(PatchCommand.GamePluginsTxt())) { pluginsTxt = PatchCommand.GamePluginsTxt(); pluginsWhy = "the game's own Plugins.txt (no --plugins given)"; }
        else
            return Cant("no load order: give --plugins",
                $"can't find your load order: no --plugins given and no game Plugins.txt at {PatchCommand.GamePluginsTxt()}. Run again with " +
                "--plugins \"<the Plugins.txt your mod manager uses>\" (MO2 keeps one per profile, in the profile's folder).");
        if (!File.Exists(pluginsTxt))
            return Cant($"the Plugins.txt {Path.GetFullPath(pluginsTxt)} doesn't exist",
                $"can't read the load order: {Path.GetFullPath(pluginsTxt)} doesn't exist. Give --plugins the Plugins.txt your mod manager uses.");
        pluginsTxt = Path.GetFullPath(pluginsTxt);
        Log.Write($"load order: {pluginsTxt}: {pluginsWhy}");
        if (extraDir != null)
        {
            if (!Directory.Exists(extraDir))
                return Cant($"the --extra-data folder {Path.GetFullPath(extraDir)} doesn't exist",
                    $"can't use --extra-data {Path.GetFullPath(extraDir)}: that folder doesn't exist. Check the path (a path with spaces needs quotes).");
            extraDir = Path.GetFullPath(extraDir);
            Log.Write($"extra plugin folder (its files win over Data's): {extraDir}");
        }

        var sw = Stopwatch.StartNew();
        LoadOrderSetup lo;
        List<string> listed;
        try
        {
            listed = File.ReadAllLines(pluginsTxt).Select(l => l.Trim()).Where(l => l.StartsWith('*')).Select(l => l[1..].Trim()).ToList();
            lo = LoadOrderSetup.Build(dataDir, pluginsTxt, extraDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return Cant($"the load order can't be read: {e.GetType().Name}",
                $"can't read the load order: {e.Message} Close any program that has a plugin open (xEdit, the Creation Kit) and try again.");
        }
        try
        {
            if (!quiet) Console.Error.WriteLine($"Loaded {lo.Plugins.Count} plugins ({lo.Problems.Count} load-order problems) in {sw.Elapsed.TotalSeconds:F0} s");
            foreach (var p in lo.Problems) Log.Write($"load-order problem: {p}");
            var report = Check(man, keys, Path.GetFileName(patch), lo, listed, dataDir, extraDir);
            foreach (var f in report.Findings) Console.WriteLine($"{f.Level} [{f.Rule}] {f.Text}");
            Console.WriteLine(report.Result);
            WriteJson(json, report);
            Fatal.AfterResult(() => Log.Write($"result: {report.State}, {report.Findings.Count} findings, {sw.Elapsed.TotalSeconds:F1} s"));
            return report.State == "rebuild" ? 1 : 0;
        }
        finally { foreach (var p in lo.Plugins) p.Mod.Dispose(); }
    }

    /// <summary>A plugin name without spaces, in lower case: two names that differ only so are two builds of one patch.</summary>
    static string Squash(string name) => name.Replace(" ", "").ToLowerInvariant();

    static string Records(List<string> keys) =>
        string.Join(", ", keys.Take(Shown)) + (keys.Count > Shown ? $" and {keys.Count - Shown} more" : "");

    static string Count(int n, string one) => $"{n} {one}{(n == 1 ? "" : "s")}";

    /// <summary>The findings for one manifest against the load order as it is now. listed: the active lines of
    /// Plugins.txt, in order (the order the game is asked for, before any master is pulled forward).</summary>
    public static Report Check(PatchSources.Manifest man, ISet<FormKey> keys, string patchName, LoadOrderSetup lo, IReadOnlyList<string> listed,
        string dataDir, string? extraDir)
    {
        var findings = new List<Finding>();
        void Add(string level, string rule, string? plugin, string text, List<string>? recs = null)
        {
            findings.Add(new(level, rule, plugin, text, recs ?? new()));
            Log.Write($"patch check: {level} [{rule}] {text}");
        }
        var byName = lo.Plugins.ToDictionary(p => p.FileName, StringComparer.OrdinalIgnoreCase);
        var self = byName.GetValueOrDefault(patchName);
        var name = self?.FileName ?? patchName;
        var twins = lo.Plugins.Where(p => !p.FileName.Equals(name, StringComparison.OrdinalIgnoreCase) && Squash(p.FileName) == Squash(name))
            .Select(p => p.FileName).ToList();
        var at = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < listed.Count; i++) at.TryAdd(listed[i], i);
        // the file of that name the load order would use (--extra-data wins over Data), matched as the game matches names
        string? DiskPath(string n)
        {
            foreach (var dir in new[] { extraDir, dataDir })
            {
                if (dir is null) continue;
                var hit = Directory.EnumerateFiles(dir).FirstOrDefault(f => Path.GetFileName(f).Equals(n, StringComparison.OrdinalIgnoreCase));
                if (hit is not null) return hit;
            }
            return null;
        }
        bool OnDisk(string n) => DiskPath(n) is not null;

        // A listed patch that didn't load (a master is missing, for example) is still checked: its masters say why.
        var selfPath = self?.Path ?? (at.ContainsKey(patchName) ? DiskPath(patchName) : null);
        if (selfPath is null)
        {
            var why = at.ContainsKey(patchName) ? "it is listed in Plugins.txt, but it isn't in the Data folder or the --extra-data folder" : "it isn't active in Plugins.txt";
            var alt = twins.Count == 0 ? "" : $" {string.Join(", ", twins)} {(twins.Count == 1 ? "is" : "are")} active instead: if that is a newer build, check it with its own manifest.";
            Add("NOTE", "patch-off", patchName, $"{patchName} isn't in the load order ({why}), so nothing else was checked.{alt}");
            return new(patchName, "not active", "RESULT: UP TO DATE (the patch isn't in the load order; nothing was checked)", findings);
        }
        if (self is null) Log.Write($"patch check: {name} is listed but didn't load (see the load-order problems); checked from {selfPath}");
        int patchAt = at.TryGetValue(name, out var pa) ? pa : int.MaxValue;
        // Where a plugin is listed. An official file loads before every mod wherever Plugins.txt lists it (as
        // LoadOrderSetup orders them), and a BlueprintShips file after every plugin, so neither is ever "moved".
        int Pos(string n) => LoadOrderSetup.IsBlueprint(n) ? int.MaxValue : LoadOrderSetup.IsOfficial(n) ? -1 : at.TryGetValue(n, out var x) ? x : -1;

        // the patch file the game loads now against the build the manifest describes
        var size = new FileInfo(selfPath).Length;
        if (size != man.Patch.Size || PatchSources.Sha256Of(selfPath) != man.Patch.Sha256)
            Add("REVIEW", "patch-changed", name,
                $"{name} in the load order ({selfPath}) isn't the build this manifest describes " +
                (size != man.Patch.Size ? $"(size {size}, manifest {man.Patch.Size})" : "(same size, other SHA-256)") +
                ", so the checks below judge the manifest's build. Make the patch again with sf-mod-check patch, which writes a new manifest.");

        var (files, masterLists) = PatchSources.Styles(lo);
        Dictionary<FormKey, string>? HashesIn(PluginInfo p)
        {
            if (!files.TryGetValue(p.FileName, out var me) || !masterLists.TryGetValue(p.FileName, out var ms)) return null;
            try { return PatchSources.Hashes(p, files, ms, me.Style, keys); }
            catch (Exception e) when (Fatal.Recoverable(e))
            {
                Log.Write($"patch check: {p.FileName} can't be read for record hashes ({Impact.Summary(e)})");
                return null;
            }
        }
        var recByKey = new Dictionary<FormKey, PatchSources.Rec>();
        foreach (var r in man.Records) if (FormKey.TryFactory(r.FormKey, out var fk)) recByKey.TryAdd(fk, r);

        // the patch's masters
        var masterSet = new HashSet<string>(man.Masters.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var m in man.Masters)
        {
            var mn = SafeText.OneLine(m.Name);
            if (!byName.TryGetValue(m.Name, out var mp))
            {
                var why = !OnDisk(m.Name) ? "isn't in the Data folder (or the --extra-data folder)"
                    : at.ContainsKey(m.Name) || LoadOrderSetup.IsOfficial(m.Name) ? "can't be loaded (a missing master of its own or a damaged file; --verbose lists the load-order problems)"
                    : "isn't active in Plugins.txt";
                Add("PROBLEM", "removed", m.Name, $"master {mn} {why}: {name} won't load without it. Rebuild the patch with sf-mod-check patch.");
                continue;
            }
            // a BlueprintShips master loads after every plugin, the patch included, as it did at build time
            if (!LoadOrderSetup.IsBlueprint(mp.FileName) && Pos(mp.FileName) > patchAt)
                Add("REVIEW", "moved", mp.FileName, $"master {mn} now loads after {name}, and the game won't load a plugin before its master: move {name} last, or rebuild it.");
            var mSize = new FileInfo(mp.Path).Length;
            var changed = mSize != m.Size ? $"size {m.Size} -> {mSize}" : PatchSources.Sha256Of(mp.Path) != m.Sha256 ? "content changed (SHA-256)" : null;
            if (changed is null) { Log.Write($"patch check: master {mp.FileName} is the file the patch was built from"); continue; }
            var now = HashesIn(mp);
            if (now is null)
            {
                Add("REVIEW", "updated", mp.FileName, $"master {mn} changed since the build ({changed}) and its records can't be read to compare: rebuild {name} to be safe.");
                continue;
            }
            var hit = new List<string>();
            foreach (var (fk, r) in recByKey)
            {
                var then = r.Editors.FirstOrDefault(e => string.Equals(e?.Plugin, m.Name, StringComparison.OrdinalIgnoreCase));
                var has = now.TryGetValue(fk, out var h);
                // an editor whose copy is gone or hashes differently, an editor with no hash to compare, or a master
                // that now edits a patched record it didn't edit at build time
                if (then is not null ? !has || then.Hash is null || then.Hash != h : has) hit.Add(r.FormKey);
            }
            if (hit.Count > 0)
                Add("REVIEW", "updated", mp.FileName, $"master {mn} changed since the build ({changed}) and changed {Count(hit.Count, "record")} {name} holds ({Records(hit)}): rebuild {name}.", hit);
            else
                Add("NOTE", "updated", mp.FileName, $"master {mn} changed since the build ({changed}), but the records {name} holds are unchanged in it.");
        }

        // every other active plugin that edits a record the patch holds: new since the build, changed since the build,
        // or moved after the patch
        var built = new HashSet<string>(man.LoadOrder.Plugins.Where(p => p is not null), StringComparer.OrdinalIgnoreCase);
        var looked = 0;
        foreach (var p in lo.Plugins)
        {
            if (p.FileName.Equals(name, StringComparison.OrdinalIgnoreCase) || twins.Contains(p.FileName, StringComparer.OrdinalIgnoreCase) || masterSet.Contains(p.FileName)) continue;
            looked++;
            var now = HashesIn(p);
            if (now is null) { Log.Write($"patch check: {p.FileName} skipped: its records can't be read"); continue; }
            if (now.Count == 0) continue;
            var hit = now.Keys.Select(k => recByKey[k].FormKey).OrderBy(k => k, StringComparer.Ordinal).ToList();
            var shown = $"{Count(hit.Count, "record")} {name} holds ({Records(hit)})";
            var after = Pos(p.FileName) > patchAt && !LoadOrderSetup.IsBlueprint(p.FileName);
            if (built.Contains(p.FileName) && after)
                Add("REVIEW", "moved", p.FileName, $"plugin {p.FileName} now loads after {name} and changes {shown}, so its copies win over the patch: move {name} last, or rebuild it.", hit);
            else if (built.Contains(p.FileName))
                Add("REVIEW", "new-editor", p.FileName, $"plugin {p.FileName} now changes {shown}, which it didn't when the patch was built: rebuild {name} to include it.", hit);
            else
                Add("REVIEW", "new-editor", p.FileName, $"new plugin {p.FileName} changes {shown}{(after ? $" and loads after {name}" : "")}: rebuild {name} to include it.", hit);
        }
        Log.Write($"patch check: {looked} other plugins read for the {keys.Count} patched records");

        foreach (var t in twins)
            Add("REVIEW", "duplicate", t, $"{t} has the same name as {name} apart from spaces or case (another build of it), and both are active" +
                (Pos(t) > patchAt ? $"; it loads after {name}, so it overrides it" : "") + ": keep only one.");

        var order = new[] { "PROBLEM", "REVIEW", "NOTE" };
        var sorted = findings.OrderBy(f => Array.IndexOf(order, f.Level)).ToList();   // stable: found order within a level
        var reasons = sorted.Count(f => f.Level != "NOTE");
        return reasons == 0
            ? new(name, "up to date", "RESULT: UP TO DATE", sorted)
            : new(name, "rebuild", $"RESULT: REBUILD ({Count(reasons, "reason")})", sorted);
    }
}
