// sf-mod-check scenes (0.24.0): an NPC's or a quest's conversations as a tree, and the scene rules on them, read only.
// The model, the "Then" rules and the rules are in SceneTree.cs; the design is in NOTES.md, "0.24.0 (SmcDialogue)",
// "Step 1 design".
//   sf-mod-check scenes <NPC or quest: EditorID | FormKey | FormID> [load-order options as in lines]
//                       [--plugin <file>] [--hub <scene>] [--out <file.md>] [--json] [--max N] [--quiet] [--verbose|--log <file>]
// Exit 0 = the NPC or quest was found (even with problems or no scenes), 1 = not found, 2 = could not run. Text is
// Markdown and ends with one RESULT line; --out writes the Markdown to a file and prints only the counts; --json prints
// one object.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using SfModCheck.Conflict;

namespace SfModCheck;

public static class ScenesCommand
{
    public const string Usage =
        "usage: sf-mod-check scenes <NPC or quest: EditorID | FormKey | FormID> [--plugins <Plugins.txt> | --mo2 <MO2 folder> [--profile <name>]]\n" +
        "                           [--data <Data folder>] [--extra-data <folder>] [--hub <scene>] [--out <file.md>] [--json] [--max <n>]\n" +
        "                           [--quiet] [--verbose] [--log <file>]\n" +
        "       sf-mod-check scenes <EditorID | FormKey> --plugin <plugin file> [--masters-dir <folder>]... [...]\n" +
        "  Prints an NPC's or a quest's conversations (scenes) as a tree: each part's lines, the player's choices and the\n" +
        "  NPC's answers, and where the talk goes next. Then it checks them: a line that jumps to a part a scene doesn't\n" +
        "  have, choices with no way out, stages that are needed but never set (or set but never checked), and Scene topics\n" +
        "  no scene uses. Last comes a table of each quest's stages: the author's note, what sets it and what needs it.\n" +
        "  The NPC or quest  an EditorID (letter case ignored), a FormKey (000803:MyMod.esm) or a console FormID\n" +
        "                    (an EditorID of exactly 8 hex digits: put eid: before it, as eid:00005788)\n" +
        "  --plugin     read only this plugin and its masters, and show only the scenes the plugin adds or changes\n" +
        "  --hub        the talk menu scene (EditorID or FormKey), when the one found by counting jumps is wrong\n" +
        "  --out        write the tree to this Markdown file and print only the counts\n" +
        "  --max        show at most this many scenes (default 50; 0 shows them all); the problems and stages are always complete\n" +
        "  --json       print one JSON object instead\n" +
        "Exit 0 = found (even with problems), 1 = not found, 2 = could not run.";

    public const int DefaultMax = 50;
    const string Tool = "sf-mod-check scenes";

    public static int Run(string[] a, string version, string byline)
    {
        bool json = a.Contains("--json");
        string query = "";
        try
        {
            var code = RunLogged(a, version, byline, ref query, out var resultPrinted, out var cantWhy);
            if (code == 2 && !resultPrinted) CannotRun(json, version, query, cantWhy);
            return code;
        }
        catch (Exception e)
        {
            Console.WriteLine(Fatal.ResultLine(e, () => json
                ? CannotRunJson(version, query, $"{e.GetType().Name}: {SafeText.OneLine(e.Message.Split('\n')[0])}")
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

    static void CannotRun(bool json, string version, string query, string why) =>
        Console.WriteLine(json ? CannotRunJson(version, query, why) : $"RESULT: CANNOT CHECK ({why})");

    static string CannotRunJson(string version, string query, string why) => new JsonObject
    {
        ["tool"] = Tool, ["version"] = version, ["query"] = query, ["result"] = "cannot run", ["error"] = why,
    }.ToJsonString(Indented);

    static int RunLogged(string[] a, string version, string byline, ref string query, out bool resultPrinted, out string cantWhy)
    {
        resultPrinted = false;
        cantWhy = "bad arguments; see the message above";
        string? q = null, logFile = null, outFile = null, hubQ = null;
        bool quiet = false, verbose = false, json = false;
        int max = DefaultMax;
        using var input = new LoadOrderInput(allowPlugin: true, ArchiveInput.Use.PluginsOnly, "scenes");
        for (int i = 0; i < a.Length; i++)
        {
            if (input.TryOption(a, ref i, Usage) is { } taken) { if (!taken) return 2; continue; }
            switch (a[i])
            {
                case "--max":
                    if (Cli.Value(a, ref i, "a number of scenes (0 = all)", Usage) is not { } mx) return 2;
                    if (!int.TryParse(mx, NumberStyles.None, CultureInfo.InvariantCulture, out max))
                    {
                        Console.Error.WriteLine($"--max needs a whole number of scenes, 0 or more (0 shows them all), not \"{SafeText.OneLine(mx)}\".");
                        cantWhy = "--max needs a whole number";
                        return 2;
                    }
                    break;
                case "--log":
                    if (Cli.Value(a, ref i, "a file name", Usage) is not { } lf) return 2;
                    logFile = lf; break;
                case "--out":
                    if (Cli.Value(a, ref i, "a Markdown file name", Usage, "scenes.md") is not { } of) return 2;
                    outFile = of; break;
                case "--hub":
                    if (Cli.Value(a, ref i, "the talk menu scene: an EditorID or a FormKey", Usage) is not { } hq) return 2;
                    hubQ = hq.Trim(); break;
                case "--json": json = true; break;
                case "--verbose": verbose = true; break;
                case "--quiet": quiet = true; break;
                case "-h": case "--help": Console.WriteLine(Usage); return 0;
                default:
                    if (a[i].StartsWith("--"))
                    {
                        Console.Error.WriteLine($"unknown option {a[i]}");
                        Console.Error.WriteLine(Usage);
                        return 2;
                    }
                    if (q != null)
                    {
                        Console.Error.WriteLine($"scenes looks at one NPC or quest per run: got {q} and {a[i]}. Run it once for each.");
                        return 2;
                    }
                    q = a[i].Trim();
                    break;
            }
        }
        if (string.IsNullOrEmpty(q))
        {
            Console.Error.WriteLine("scenes needs the NPC or quest to look at: an EditorID, a FormKey (000803:MyMod.esm) or a console FormID (00005788).");
            Console.Error.WriteLine(Usage);
            cantWhy = "no NPC or quest given";
            return 2;
        }
        query = q;
        if (outFile != null && json)
        {
            Console.Error.WriteLine("--out writes the Markdown tree and --json prints JSON: use one of them.");
            cantWhy = "--out and --json together";
            return 2;
        }
        if (!quiet && !json) Console.Error.WriteLine(byline);
        if (verbose) Log.ToStderr();
        if (logFile != null && Log.ToFile(logFile) is { } logError) { Console.Error.WriteLine(logError); cantWhy = "the log file can't be written"; return 2; }
        Log.Write($"sf-mod-check {version} scenes on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, .NET {Environment.Version}");
        Log.Write($"arguments: {string.Join(" ", a.Select(x => x.Contains(' ') ? $"\"{x}\"" : x))}");
        Log.Write($"working folder: {Environment.CurrentDirectory}");

        if (LinesCommand.Parse(q, input.Plugin, "", out cantWhy) is not { } mainQ) return 2;
        if (!input.Resolve(out var why)) { cantWhy = why; return 2; }

        var sw = Stopwatch.StartNew();
        LoadOrderSetup lo;
        try { lo = input.Build(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            cantWhy = $"the load order can't be read: {e.GetType().Name}";
            Console.Error.WriteLine($"can't read the load order: {e.Message} Check that this account can read that file or folder, close any program that has a plugin open (xEdit, the Creation Kit), and try again.");
            Log.Write($"stopped: reading the load order: {e}");
            return 2;
        }
        var oldLimit = Analyzer.MaxManagedBytes;
        try
        {
            Log.Write($"loaded {lo.Plugins.Count} plugins, {lo.Problems.Count} load-order problems ({sw.Elapsed.TotalSeconds:F1} s)");
            foreach (var p in lo.Problems) Log.Write($"load-order problem: {p}");
            if (!quiet && !json) Console.Error.WriteLine($"Loaded {lo.Plugins.Count} plugins ({lo.Problems.Count} load-order problems) in {sw.Elapsed.TotalSeconds:F0} s");
            if (input.Plugin != null && FindCommand.Unloaded(lo, input.Plugin) is { } unloadedWhy) { cantWhy = unloadedWhy; return 2; }

            // the NPC or quest
            var matches = LinesCommand.Lookup(mainQ, lo, out var notFoundWhy);
            var source = lo.OrderSource;
            if (matches.Count == 0)
            {
                var what = LinesCommand.NotFoundWhat(mainQ, lo, notFoundWhy);
                var hint = (!mainQ.IsFormId && !mainQ.IsFormKey ? "EditorIDs must match exactly (letter case doesn't matter). " : "") +
                           $"The load order searched was {source}. " +
                           (input.Plugin != null
                               ? "--plugin searches only that plugin and its masters; to search your whole load order, leave out --plugin."
                               : "If the mod isn't in that load order, give its Plugins.txt with --plugins (with Mod Organizer 2: --mo2 <MO2 folder>), or read the plugin alone with --plugin.");
                resultPrinted = true;
                if (json) Console.WriteLine(new JsonObject
                {
                    ["tool"] = Tool, ["version"] = version, ["query"] = q, ["source"] = source, ["result"] = "not found", ["hint"] = hint,
                }.ToJsonString(Indented));
                else
                {
                    Console.WriteLine($"HINT: {hint}");
                    Console.WriteLine($"RESULT: NOT FOUND ({what})");
                }
                Log.Write($"result: not found, exit code 1, {sw.Elapsed.TotalSeconds:F1} s");
                return 1;
            }
            var wanted = matches.Where(m => m.Winner.Rec is INpcGetter or IQuestGetter).ToList();
            if (wanted.Count == 0)
            {
                var m = matches[0];
                Console.Error.WriteLine($"{SafeText.OneLine(q)} is a {m.Type} ({m.Key}), not an NPC or a quest: scenes looks at an NPC_ or a QUST record. " +
                                        "find shows what the record is: sf-mod-check find " + LinesCommand.Quoted(q));
                cantWhy = $"{SafeText.OneLine(q)} is a {m.Type}, not an NPC or a quest";
                return 2;
            }
            if (wanted.Count > 1)
            {
                Console.Error.WriteLine($"{wanted.Count} NPCs or quests have the EditorID {SafeText.OneLine(q)}: {string.Join(", ", wanted.Select(n => n.Key.ToString()))}. Give the one you mean as a FormKey.");
                cantWhy = $"{wanted.Count} records have the EditorID {SafeText.OneLine(q)}";
                return 2;
            }
            var target = wanted[0];
            bool isNpc = target.Winner.Rec is INpcGetter;
            var targetKey = target.Key;
            var targetName = string.IsNullOrEmpty(target.Winner.Rec.EditorID) ? targetKey.ToString() : SafeText.OneLine(target.Winner.Rec.EditorID)!;
            Log.Write($"scenes: {(isNpc ? "NPC" : "quest")} {targetName} [{targetKey}]");

            // the dialogue
            Analyzer.MaxManagedBytes = LinesCommand.MemoryLimit;
            DialogueIndex index;
            var readSw = Stopwatch.StartNew();
            try { index = DialogueIndex.Build(lo.Plugins, quiet || json ? null : Console.Error.WriteLine); }
            catch (MemoryLimitException e)
            {
                Log.Write($"stopped: {e.Message}");
                cantWhy = $"memory passed {LinesCommand.MemoryLimit / 1e9:F1} GB while reading the dialogue; --plugin reads only one plugin and its masters";
                return 2;
            }
            Log.Write($"scenes: dialogue read in {readSw.Elapsed.TotalSeconds:F1} s: {index.Scenes.Count} scenes, {index.Topics.Count} topics, {index.Infos.Count} lines");
            foreach (var u in index.Unread) Console.Error.WriteLine($"note: {u}");
            string? targetFile = input.Plugin != null ? Path.GetFileName(input.Plugin) : null;
            if (targetFile != null && (index.UnreadPlugins.Contains(targetFile) || index.UnreadScenePlugins.Contains(targetFile)))
            {
                Console.Error.WriteLine($"can't read all of {SafeText.OneLine(targetFile)}'s dialogue or scenes, so the tree would miss parts. Check the plugin in xEdit or the Creation Kit, save it again, and retry.");
                cantWhy = $"{SafeText.OneLine(targetFile)}'s dialogue can't be read in full";
                return 2;
            }

            var tree = new SceneTree(index);
            if (hubQ != null)
            {
                if (FindScene(index, hubQ) is not { } hk)
                {
                    Console.Error.WriteLine($"--hub {SafeText.OneLine(hubQ)}: no scene with that EditorID or FormKey in this load order. Give the talk menu scene's EditorID or FormKey (find looks it up).");
                    cantWhy = $"the --hub scene {SafeText.OneLine(hubQ)} isn't found";
                    return 2;
                }
                tree.HubOverride = hk;
                Log.Write($"scenes: --hub: the talk menu is {tree.Name(hk, typeof(ISceneGetter))}");
            }

            // the scenes
            var pickSw = Stopwatch.StartNew();
            List<SceneNode> picked;
            if (isNpc) picked = tree.ForNpc(targetKey);
            else
            {
                picked = tree.ForQuest(targetKey);
                foreach (var s in picked) Log.Write($"scenes: picked {tree.Name(s.Key, typeof(ISceneGetter))}: a scene of the quest");
            }
            if (targetFile != null)
            {
                var own = lo.Plugins.First(p => p.FileName.Equals(targetFile, StringComparison.OrdinalIgnoreCase)).Mod;
                var ownScenes = own.EnumerateMajorRecords<ISceneGetter>().Select(s => s.FormKey).ToHashSet();
                int before = picked.Count;
                picked = picked.Where(s => ownScenes.Contains(s.Key)).ToList();
                Log.Write($"scenes: --plugin: {picked.Count} of {before} scenes are {targetFile}'s own or changed by it");
            }
            Log.Write($"scenes: {picked.Count} scenes picked in {pickSw.Elapsed.TotalSeconds:F1} s");

            // the rules and the stage table: the plugins in scope get their setter table (scripts read on demand)
            var checkSw = Stopwatch.StartNew();
            var check = tree.Check(picked, PluginsOf(lo, input, targetFile));
            Log.Write($"scenes: rules and stage table in {checkSw.Elapsed.TotalSeconds:F1} s");

            int limit = max == 0 ? int.MaxValue : max;
            var shown = picked.Take(limit).ToList();
            FormKey? skipNpc = isNpc ? targetKey : null;
            var counts = new Counts(picked.Count, shown.Count, check.Problems.Count(p => p.Severity == "ERROR"),
                check.Problems.Count(p => p.Severity == "WARNING"), check.Problems.Count(p => p.Severity == "NOTE"));
            var result = $"{counts.Scenes} scene{(counts.Scenes == 1 ? "" : "s")}, {counts.Errors} error{(counts.Errors == 1 ? "" : "s")}, " +
                         $"{counts.Warnings} warning{(counts.Warnings == 1 ? "" : "s")}, {counts.Notes} note{(counts.Notes == 1 ? "" : "s")}";
            resultPrinted = true;
            if (json)
                Console.WriteLine(Json(version, q, source, isNpc, targetKey, targetName, tree, shown, skipNpc, check, index, counts));
            else
            {
                var md = Markdown(tree, isNpc, targetName, shown, picked.Count, skipNpc, check);
                if (outFile != null)
                {
                    try { File.WriteAllText(outFile, md, new UTF8Encoding(false)); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                    {
                        resultPrinted = false;
                        Console.Error.WriteLine($"can't write {SafeText.OneLine(outFile)}: {e.Message}");
                        cantWhy = "the --out file can't be written";
                        return 2;
                    }
                    Console.WriteLine($"Wrote {SafeText.OneLine(Path.GetFullPath(outFile))}: {counts.Shown} of {counts.Scenes} scene(s), {check.Problems.Count} problem(s), {check.Stages.Count} stage row(s).");
                }
                else Console.Write(md);
                Console.WriteLine($"RESULT: {result} ({sw.Elapsed.TotalSeconds:F0} s)");
            }
            Log.Write($"result: {result}, exit code 0, {sw.Elapsed.TotalSeconds:F1} s");
            return 0;
        }
        finally
        {
            Analyzer.MaxManagedBytes = oldLimit;
            foreach (var p in lo.Plugins) p.Mod.Dispose();
        }
    }

    sealed record Counts(int Scenes, int Shown, int Errors, int Warnings, int Notes);

    /// <summary>A scene by EditorID (letter case ignored) or FormKey, or null.</summary>
    static FormKey? FindScene(DialogueIndex index, string q)
    {
        if (q.Contains(':') && FormKey.TryFactory(q, out var k)) return index.Scenes.ContainsKey(k) ? k : null;
        var hits = index.Scenes.Where(s => string.Equals(s.Value.EditorID, q, StringComparison.OrdinalIgnoreCase)).Select(s => s.Key).ToList();
        return hits.Count == 1 ? hits[0] : null;
    }

    /// <summary>pluginOf for SceneTree.Check: a plugin's setter table when its quests and topics are checked (not an
    /// official plugin; with --plugin, only that one), else null. Each table is built once; the script index is built
    /// on the first one.</summary>
    static Func<ModKey, StagePlugin?> PluginsOf(LoadOrderSetup lo, LoadOrderInput input, string? targetFile)
    {
        var cache = new Dictionary<ModKey, StagePlugin?>();
        AssetIndex? assets = null;
        string? assetsFailed = null;
        return m =>
        {
            if (cache.TryGetValue(m, out var got)) return got;
            StagePlugin? r = null;
            var p = lo.Plugins.FirstOrDefault(x => x.FileName.Equals(m.FileName.String, StringComparison.OrdinalIgnoreCase));
            if (p != null && !p.Official && !Checker.IsOfficial(p.FileName) &&
                (targetFile == null || p.FileName.Equals(targetFile, StringComparison.OrdinalIgnoreCase)))
            {
                if (assets == null && assetsFailed == null)
                {
                    try
                    {
                        assets = AssetIndex.Build(lo.Plugins.Select(x => x.FileName), input.AssetDirs, AssetIndex.DefaultInis(input.DataDir, []), "en");
                        Log.Write($"scenes: script index built ({assets.Loaded.Count} archives)");
                    }
                    catch (Exception e) when (Fatal.Recoverable(e))
                    {
                        assetsFailed = $"the script index couldn't be built ({e.GetType().Name})";
                        Log.Write($"scenes: {assetsFailed}: {SafeText.OneLine(e.Message)}");
                    }
                }
                Func<string, (PexFile? Pex, string? Problem)> scripts = assets != null
                    ? new QuestStages.ScriptSource(assets, QuestStages.ArchivesFor(assets, p.FileName, p.Masters, input.DataDir)).Get
                    : _ => (null, assetsFailed);
                var mod = (IStarfieldModGetter)p.Mod;
                var st = QuestStages.Setters(mod, scripts, rec => string.IsNullOrEmpty(rec.EditorID) ? rec.FormKey.ToString() : rec.EditorID);
                Log.Write($"scenes: {p.FileName}: setter table for {st.Set.Count} quest(s), {st.Unreadable.Count} script(s) unreadable");
                r = new StagePlugin(mod, st);
            }
            return cache[m] = r;
        };
    }

    // ---- text (Markdown) ----

    static string Cell(string s) => s.Replace("|", "\\|");

    static string Quote(LineNode l) => l.HasText ? $"\"{l.Text}\"" : l.SharedFrom != null ? "(a shared line with no text)" : "(no text)";

    /// <summary>A line's own words: its conditions, the INFO perk, the stages it sets and its shared source.</summary>
    static string Extras(SceneTree tree, LineNode l, FormKey? skipNpc)
    {
        var parts = new List<string>();
        var words = SceneWords.Text(l.Conditions, tree.Link, skipNpc);
        if (l.Perk is { } pk) words.Add($"with the perk {tree.Name(pk, typeof(IPerkGetter))}");
        var o = words.Count > 0 ? $" (only {string.Join(", and ", words)})" : "";
        foreach (var n in SetsStage(l)) o += $" [sets stage {n}]";
        if (l.SharedFrom is { } sh) o += $" (shared line {sh})";
        return o;
    }

    static IEnumerable<int> SetsStage(LineNode l)
    {
        if (l.StageOnBegin >= 0) yield return l.StageOnBegin;
        if (l.StageOnEnd >= 0 && l.StageOnEnd != l.StageOnBegin) yield return l.StageOnEnd;
    }

    static bool Spoken(LineNode l) => l.Shown;

    static string Markdown(SceneTree tree, bool isNpc, string targetName, List<SceneNode> shown, int total, FormKey? skipNpc, SceneCheck check)
    {
        var sb = new StringBuilder();
        void L(string s = "") => sb.Append(s).Append('\n');
        L($"# Conversations of {(isNpc ? "" : "the quest ")}{targetName}");
        L();
        if (shown.Count == 0) L(isNpc ? "No scene gives this NPC a line." : "This quest has no scenes.");
        foreach (var s in shown)
        {
            L($"## {tree.Name(s.Key, typeof(ISceneGetter))}");
            L();
            var start = SceneWords.Text(s.Conditions, tree.Link, skipNpc);
            var quest = s.Quest.IsNull ? "no quest" : $"quest {tree.Name(s.Quest, typeof(IQuestGetter))}";
            L($"Scene {s.Key} ({quest}). " + (start.Count > 0 ? $"It can start only {string.Join(", and ", start)}." : "It has no start conditions of its own.") +
              (s.Script ? " A script is attached." : ""));
            L();
            foreach (var p in s.Parts)
            {
                if (p.Steps.Count == 0) continue;
                var pw = SceneWords.Text(p.Conditions, tree.Link, skipNpc);
                L($"Part {p.Index + 1}" + (p.Name.Length > 0 ? $" \"{SafeText.OneLine(p.Name)}\"" : "") +
                  (pw.Count > 0 ? $" (runs only {string.Join(", and ", pw)}; otherwise skipped)" : "") + ".");
                L();
                foreach (var st in p.Steps)
                {
                    switch (st.Type)
                    {
                        case StepType.Says:
                            L($"{st.Speaker} {(st.Alias == -2 ? "say" : "says")}{(st.Random ? " one of these at random" : "")}:");
                            L();
                            foreach (var l in st.Lines.Where(Spoken))
                                L($"- {Quote(l)}{Extras(tree, l, skipNpc)}" + (l.Jumps ? $" {tree.Then(s, p.Index, l)}" : ""));
                            if (!st.Lines.Any(Spoken)) L("- (no spoken line)");
                            L();
                            break;
                        case StepType.Choices:
                            L("Your choices:");
                            L();
                            L($"| You say | {Cell(st.Speaker)} answers | Then |");
                            L("| --- | --- | --- |");
                            foreach (var c in st.Choices)
                                foreach (var (you, answers, then) in ChoiceRows(tree, s, p.Index, c, skipNpc))
                                    L($"| {Cell(you)} | {Cell(answers)} | {Cell(then)} |");
                            L();
                            break;
                        default:
                            L($"- {st.Text}.");
                            L();
                            break;
                    }
                }
            }
        }
        if (total > shown.Count)
        {
            L($"... and {total - shown.Count} more scene{(total - shown.Count == 1 ? "" : "s")} (--max 0 shows them all). The problems and stages below cover every scene.");
            L();
        }
        L("## Problems");
        L();
        if (check.Problems.Count == 0) L("None found.");
        foreach (var pr in check.Problems) L($"- {pr.Severity} {pr.Rule}: {pr.Message}");
        foreach (var rule in check.Problems.Select(p => p.Rule).Distinct())
            if (Hints.For(rule) is { } h) L($"- HINT ({rule}): {h}");
        L();
        if (tree.Remarks.Count > 0)
        {
            L("## Remarks");
            L();
            foreach (var r in tree.Remarks) L($"- {r}");
            L();
        }
        foreach (var grp in check.Stages.GroupBy(r => r.Quest))
        {
            L($"## Stages of {grp.First().QuestName}");
            L();
            L("| Stage | Author's note | Set by | Needed by |");
            L("| --- | --- | --- | --- |");
            foreach (var r in grp)
                L($"| {r.Stage} | {Cell(r.Note)} | {Cell(string.Join("; ", r.SetBy))} | {Cell(string.Join("; ", r.NeededBy))} |");
            L();
        }
        return sb.ToString();
    }

    /// <summary>A choice's table rows: one per distinct "Then" of its spoken answers (random answers that share one
    /// become "One of these at random: ..."), or one row with "(the topic's own scene answers)" when it has no answer
    /// topic.</summary>
    static IEnumerable<(string You, string Answers, string Then)> ChoiceRows(SceneTree tree, SceneNode s, int part, Choice c, FormKey? skipNpc)
    {
        var player = c.Player.Where(Spoken).ToList();
        var you = player.Count == 0 ? "(no line)" : string.Join(" / ", player.Select(l => Quote(l) + Extras(tree, l, skipNpc)));
        var answers = c.Answers.Where(Spoken).ToList();
        if (c.ResponseTopic == null || answers.Count == 0)
        {
            yield return (you, c.ResponseTopic == null ? "(the topic's own scene answers)" : "(no spoken answer)", ChoiceThen(tree, s, part, c));
            yield break;
        }
        foreach (var g in answers.GroupBy(l => tree.Then(s, part, l)))
        {
            var ls = g.ToList();
            var text = ls.Count > 1 && ls.All(l => l.Random)
                ? "One of these at random: " + string.Join(" / ", ls.Select(l => Quote(l) + Extras(tree, l, skipNpc)))
                : string.Join(" / ", ls.Select(l => Quote(l) + Extras(tree, l, skipNpc)));
            yield return (you, text, g.Key);
        }
    }

    static string ChoiceThen(SceneTree tree, SceneNode s, int part, Choice c) =>
        tree.ChoiceThen(s, part, c) is { Length: > 0 } t ? t : "(nothing to say)";

    // ---- JSON ----

    static JsonArray Strings(IEnumerable<string> s) => new(s.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());

    static JsonObject LineJson(SceneTree tree, SceneNode s, int part, LineNode l, FormKey? skipNpc) => new()
    {
        ["formKey"] = l.Info.ToString(),
        ["text"] = l.Text,
        ["conditions"] = Strings(l.Conditions.Select(c => FindCommand.ConditionText(c, tree.Link))),
        ["words"] = Strings(SceneWords.Text(l.Conditions, tree.Link, skipNpc)),
        ["then"] = tree.Then(s, part, l),
        ["setsStage"] = new JsonArray(SetsStage(l).Select(n => (JsonNode)JsonValue.Create(n)).ToArray()),
        ["goodbye"] = l.Goodbye,
        ["random"] = l.Random,
        ["startScene"] = l.StartScene?.ToString(),
        ["startPart"] = l.StartPart,
    };

    static string Json(string version, string q, string source, bool isNpc, FormKey key, string name, SceneTree tree, List<SceneNode> shown,
                       FormKey? skipNpc, SceneCheck check, DialogueIndex index, Counts counts)
    {
        var hubs = shown.Select(s => s.Quest).Distinct().Select(tree.Hub).OfType<FormKey>().Distinct().Select(h => h.ToString()).ToList();
        var scenes = new JsonArray();
        foreach (var s in shown)
        {
            var parts = new JsonArray();
            foreach (var p in s.Parts)
            {
                var steps = new JsonArray();
                foreach (var st in p.Steps)
                    steps.Add(new JsonObject
                    {
                        ["type"] = st.Type switch { StepType.Says => "says", StepType.Choices => "choices", _ => "other" },
                        ["alias"] = st.Alias,
                        ["speaker"] = st.Speaker,
                        ["random"] = st.Random,
                        ["lines"] = new JsonArray(st.Lines.Select(l => (JsonNode)LineJson(tree, s, p.Index, l, skipNpc)).ToArray()),
                        ["choices"] = new JsonArray(st.Choices.Select(c => (JsonNode)new JsonObject
                        {
                            ["player"] = new JsonArray(c.Player.Select(l => (JsonNode)LineJson(tree, s, p.Index, l, skipNpc)).ToArray()),
                            ["answers"] = new JsonArray(c.Answers.Select(l => (JsonNode)LineJson(tree, s, p.Index, l, skipNpc)).ToArray()),
                            ["then"] = c.ResponseTopic == null || !c.Answers.Any(Spoken)
                                ? ChoiceThen(tree, s, p.Index, c)
                                : string.Join(" / ", c.Answers.Where(Spoken).Select(l => tree.Then(s, p.Index, l)).Distinct()),
                        }).ToArray()),
                        ["text"] = st.Text,
                    });
                parts.Add(new JsonObject
                {
                    ["index"] = p.Index + 1,
                    ["name"] = p.Name,
                    ["conditions"] = Strings(p.Conditions.Select(c => FindCommand.ConditionText(c, tree.Link))),
                    ["words"] = Strings(SceneWords.Text(p.Conditions, tree.Link, skipNpc)),
                    ["steps"] = steps,
                });
            }
            scenes.Add(new JsonObject
            {
                ["formKey"] = s.Key.ToString(),
                ["editorId"] = s.EditorId,
                ["quest"] = s.Quest.IsNull ? null : s.Quest.ToString(),
                ["conditions"] = Strings(s.Conditions.Select(c => FindCommand.ConditionText(c, tree.Link))),
                ["words"] = Strings(SceneWords.Text(s.Conditions, tree.Link, skipNpc)),
                ["parts"] = parts,
            });
        }
        var root = new JsonObject
        {
            ["tool"] = Tool,
            ["version"] = version,
            ["query"] = q,
            ["kind"] = isNpc ? "npc" : "quest",
            ["formKey"] = key.ToString(),
            ["editorId"] = name,
            ["source"] = source,
            ["hub"] = Strings(hubs),
            ["scenes"] = scenes,
            ["problems"] = new JsonArray(check.Problems.Select(p => (JsonNode)new JsonObject
            {
                ["severity"] = p.Severity, ["rule"] = p.Rule, ["message"] = p.Message, ["hint"] = Hints.For(p.Rule), ["record"] = p.Record,
            }).ToArray()),
            ["stages"] = new JsonArray(check.Stages.Select(r => (JsonNode)new JsonObject
            {
                ["quest"] = r.Quest.ToString(), ["questEditorId"] = r.QuestName, ["stage"] = r.Stage, ["note"] = r.Note,
                ["setBy"] = Strings(r.SetBy), ["neededBy"] = Strings(r.NeededBy),
            }).ToArray()),
            ["remarks"] = Strings(tree.Remarks),
            ["unread"] = Strings(index.Unread),
            ["result"] = new JsonObject
            {
                ["scenes"] = counts.Scenes, ["shown"] = counts.Shown, ["errors"] = counts.Errors, ["warnings"] = counts.Warnings, ["notes"] = counts.Notes,
            },
        };
        return root.ToJsonString(Indented);
    }
}
