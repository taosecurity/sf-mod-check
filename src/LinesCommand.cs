// sf-mod-check lines (0.21.0): the dialogue lines one NPC can speak, grouped by type (the topic's Category / Subtype),
// read only. The line rules are in DialogueLines.cs; the design is in NOTES.md, "0.21.0 (SmcLookup)", "Step 1 design".
//   sf-mod-check lines <EditorID | FormKey | FormID> [--plugins <Plugins.txt> | --mo2 <MO2 folder> [--profile <name>]]
//                      [--data <Data folder>] [--extra-data <folder>] [--compare <NPC>] [--type <name>] [--max <n>]
//                      [--no-files] [--json] [--quiet] [--verbose] [--log <file>]
//   sf-mod-check lines <EditorID | FormKey> --plugin <plugin file> [--masters-dir <folder>]... [...]
// --compare counts a second NPC's own lines (not its shared voice-type lines) per type and marks the types this NPC
// has no line of as missing. Each line's wem and lip say whether sound\voice\<the INFO's own plugin>\<the NPC's voice
// type>\<number>.wem and .ffxanim are there (VoiceFileIndex), ? for an NPC with no voice type of its own or --no-files.
// Exit 0 = the NPC was found (even with 0 lines), 1 = not found, 2 = could not run (a --compare NPC that isn't found
// is 2). Text ends with one RESULT line; --json prints one object.
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using SfModCheck.Conflict;

namespace SfModCheck;

public static class LinesCommand
{
    public const string Usage =
        "usage: sf-mod-check lines <EditorID | FormKey | FormID> [--plugins <Plugins.txt> | --mo2 <MO2 folder> [--profile <name>]]\n" +
        "                          [--data <Data folder>] [--extra-data <folder>] [--compare <NPC>] [--type <name>] [--max <n>]\n" +
        "                          [--no-files] [--json] [--quiet] [--verbose] [--log <file>]\n" +
        "       sf-mod-check lines <EditorID | FormKey> --plugin <plugin file> [--masters-dir <folder>]... [...]\n" +
        "  Lists the dialogue lines one NPC can speak, grouped by type (the topic's Category / Subtype, for example\n" +
        "  Combat / Hit), and counts them per type. A line counts when its speaker is the NPC, a condition names the NPC\n" +
        "  (GetIsID) or an alias it fills (GetIsAliasRef), or a scene gives the line to an alias the NPC fills. Lines for\n" +
        "  the NPC's voice type (GetIsVoiceType) are listed apart as shared lines. Each line says whether its voice file\n" +
        "  (wem) and lip file (lip, .ffxanim) are there, in the plugin's archives or loose.\n" +
        "  The NPC      an EditorID (letter case ignored), a FormKey (005788:Starfield.esm) or a console FormID (00005788)\n" +
        "               (an EditorID of exactly 8 hex digits: put eid: before it, as eid:00005788)\n" +
        "  --plugin     read only this plugin and its masters (found in its folder, then each --masters-dir, then --data)\n" +
        "  --compare    count another NPC's lines per type too (for example a vanilla companion) and mark the types this\n" +
        "               NPC has none of as missing; the same forms as the NPC\n" +
        "  --type       keep only lines of this Category (Combat) or Category / Subtype (\"Combat / Hit\"); letter case ignored\n" +
        "  --max        list at most this many lines (default 200; 0 lists them all); the counts are always complete\n" +
        "  --no-files   don't look for the voice and lip files (faster; wem and lip show ?)\n" +
        "  --json       print one JSON object with every line\n" +
        "Exit 0 = the NPC was found (even with no lines), 1 = not found, 2 = could not run.";

    /// <summary>The lines listed in text by default (--max changes it).</summary>
    public const int DefaultMax = 200;
    /// <summary>The memory guard while reading the dialogue (the same as find --refs).</summary>
    public const long MemoryLimit = 16L * 1000 * 1000 * 1000;
    /// <summary>A line's text is cut at this many characters in the text output.</summary>
    const int MaxText = 200;

    static readonly Regex ConsoleId = new(@"^(0x)?[0-9A-Fa-f]{8}\z", RegexOptions.CultureInvariant);

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

    static void CannotRun(bool json, string version, string query, string why) =>
        Console.WriteLine(json ? CannotRunJson(version, query, why) : $"RESULT: CANNOT CHECK ({why})");

    static string CannotRunJson(string version, string query, string why) => new JsonObject
    {
        ["tool"] = "sf-mod-check lines", ["version"] = version, ["query"] = query, ["result"] = "cannot run", ["error"] = why,
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    /// <summary>The --type names: every Category, and every Category / Subtype pair (spaces around the slash optional).</summary>
    internal static bool TypeMatches(string filter, DialogTopic.CategoryEnum c, DialogTopic.SubtypeEnum s)
    {
        var f = Squash(filter);
        return f.Equals(c.ToString(), StringComparison.OrdinalIgnoreCase) || f.Equals($"{c}/{s}", StringComparison.OrdinalIgnoreCase);
    }

    static string Squash(string s) => string.Concat(s.Where(ch => !char.IsWhiteSpace(ch)));

    static bool KnownType(string filter)
    {
        var f = Squash(filter);
        foreach (var c in Enum.GetValues<DialogTopic.CategoryEnum>())
        {
            if (f.Equals(c.ToString(), StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var s in Enum.GetValues<DialogTopic.SubtypeEnum>())
                if (f.Equals($"{c}/{s}", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    static int RunLogged(string[] a, string version, string byline, ref string query, out bool resultPrinted, out string cantWhy)
    {
        resultPrinted = false;
        cantWhy = "bad arguments; see the message above";
        string? q = null, type = null, logFile = null, refQ = null;
        bool quiet = false, verbose = false, json = false, noFiles = false;
        int max = DefaultMax;
        using var input = new LoadOrderInput(allowPlugin: true, ArchiveInput.Use.PluginsOnly, "lines");
        for (int i = 0; i < a.Length; i++)
        {
            if (input.TryOption(a, ref i, Usage) is { } taken) { if (!taken) return 2; continue; }
            switch (a[i])
            {
                case "--max":
                    if (Cli.Value(a, ref i, "a number of lines (0 = all)", Usage) is not { } mx) return 2;
                    if (!int.TryParse(mx, NumberStyles.None, CultureInfo.InvariantCulture, out max))
                    {
                        Console.Error.WriteLine($"--max needs a whole number of lines, 0 or more (0 lists them all), not \"{SafeText.OneLine(mx)}\".");
                        cantWhy = "--max needs a whole number";
                        return 2;
                    }
                    break;
                case "--type":
                    if (Cli.Value(a, ref i, "a line type, such as Combat or \"Combat / Hit\"", Usage) is not { } t) return 2;
                    type = t.Trim(); break;
                case "--log":
                    if (Cli.Value(a, ref i, "a file name", Usage) is not { } lf) return 2;
                    logFile = lf; break;
                case "--compare":
                    if (Cli.Value(a, ref i, "the NPC to compare with: an EditorID, a FormKey or a console FormID", Usage) is not { } rq) return 2;
                    refQ = rq.Trim(); break;
                case "--no-files": noFiles = true; break;
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
                        Console.Error.WriteLine($"lines looks at one NPC per run: got {q} and {a[i]}. Run it once for each NPC.");
                        return 2;
                    }
                    q = a[i].Trim();
                    break;
            }
        }
        if (string.IsNullOrEmpty(q))
        {
            Console.Error.WriteLine("lines needs the NPC to look at: an EditorID, a FormKey (005788:Starfield.esm) or a console FormID (00005788).");
            Console.Error.WriteLine(Usage);
            cantWhy = "no NPC given";
            return 2;
        }
        query = q;
        if (type != null && !KnownType(type))
        {
            Console.Error.WriteLine($"--type needs a dialogue Category ({string.Join(", ", Enum.GetNames<DialogTopic.CategoryEnum>())}) " +
                                    $"or a Category / Subtype such as \"Combat / Hit\" or \"Misc / Idle\", not \"{SafeText.OneLine(type)}\". " +
                                    $"The subtypes are: {string.Join(", ", Enum.GetNames<DialogTopic.SubtypeEnum>())}.");
            cantWhy = $"unknown line type {SafeText.OneLine(type)}";
            return 2;
        }
        if (!quiet && !json) Console.Error.WriteLine(byline);
        if (verbose) Log.ToStderr();
        if (logFile != null && Log.ToFile(logFile) is { } logError) { Console.Error.WriteLine(logError); cantWhy = "the log file can't be written"; return 2; }
        Log.Write($"sf-mod-check {version} lines on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, .NET {Environment.Version}");
        Log.Write($"arguments: {string.Join(" ", a.Select(x => x.Contains(' ') ? $"\"{x}\"" : x))}");
        Log.Write($"working folder: {Environment.CurrentDirectory}");

        if (Parse(q, input.Plugin, "", out cantWhy) is not { } mainQ) return 2;
        Query? refQuery = null;
        if (refQ != null)
        {
            if (refQ.Length == 0)
            {
                Console.Error.WriteLine("--compare needs the NPC to compare with: an EditorID, a FormKey (005788:Starfield.esm) or a console FormID (00005788).");
                cantWhy = "--compare needs an NPC";
                return 2;
            }
            if (Parse(refQ, input.Plugin, "--compare ", out cantWhy) is not { } rq) return 2;
            refQuery = rq;
        }
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
            Log.Write($"loaded {lo.Plugins.Count} plugins, {lo.Problems.Count} load-order problems");
            foreach (var p in lo.Problems) Log.Write($"load-order problem: {p}");
            if (!quiet && !json) Console.Error.WriteLine($"Loaded {lo.Plugins.Count} plugins ({lo.Problems.Count} load-order problems) in {sw.Elapsed.TotalSeconds:F0} s");
            if (input.Plugin != null && FindCommand.Unloaded(lo, input.Plugin) is { } unloadedWhy) { cantWhy = unloadedWhy; return 2; }

            // the NPC
            var matches = Lookup(mainQ, lo, out var notFoundWhy);
            var source = lo.OrderSource;
            if (matches.Count == 0)
            {
                var what = NotFoundWhat(mainQ, lo, notFoundWhy);
                var hint = (!mainQ.IsFormId && !mainQ.IsFormKey ? "EditorIDs must match exactly (letter case doesn't matter). " : "") +
                           $"The load order searched was {source}. " +
                           (input.Plugin != null
                               ? "--plugin searches only that plugin and its masters; to search your whole load order, leave out --plugin."
                               : "If the mod isn't in that load order, give its Plugins.txt with --plugins (with Mod Organizer 2: --mo2 <MO2 folder>), or read the plugin alone with --plugin.");
                resultPrinted = true;
                if (json) Console.WriteLine(new JsonObject
                {
                    ["tool"] = "sf-mod-check lines", ["version"] = version, ["query"] = q, ["source"] = source, ["result"] = "not found", ["hint"] = hint,
                }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                else
                {
                    Console.WriteLine($"HINT: {hint}");
                    Console.WriteLine($"RESULT: NOT FOUND ({what})");
                }
                Log.Write($"result: not found, exit code 1, {sw.Elapsed.TotalSeconds:F1} s");
                return 1;
            }
            if (OneNpc(mainQ, matches, "", out cantWhy) is not { } one) return 2;
            var (npc, npcKey) = one;
            (INpcGetter Npc, FormKey Key)? reference = null;
            if (refQuery != null)
            {
                var refMatches = Lookup(refQuery, lo, out var refWhy);
                if (refMatches.Count == 0)
                {
                    Console.Error.WriteLine($"--compare {SafeText.OneLine(refQuery.Text)}: {NotFoundWhat(refQuery, lo, refWhy)}. Give the NPC to compare with as it is in this load order " +
                                            "(an EditorID, a FormKey or a console FormID); find looks it up: sf-mod-check find " + Quoted(refQuery.Text));
                    cantWhy = $"the --compare NPC {SafeText.OneLine(refQuery.Text)} isn't found";
                    return 2;
                }
                if (OneNpc(refQuery, refMatches, "--compare ", out cantWhy) is not { } r) return 2;
                reference = r;
                Log.Write($"compare with {r.Key}");
            }

            // its dialogue
            Analyzer.MaxManagedBytes = MemoryLimit;
            DialogueIndex index;
            try { index = DialogueIndex.Build(lo.Plugins, quiet || json ? null : Console.Error.WriteLine); }
            catch (MemoryLimitException e)
            {
                Log.Write($"stopped: {e.Message}");
                cantWhy = $"memory passed {MemoryLimit / 1e9:F1} GB while reading the dialogue; --plugin reads only one plugin and its masters";
                return 2;
            }
            foreach (var u in index.Unread) Console.Error.WriteLine($"note: {u}");
            if (input.Plugin != null && Path.GetFileName(input.Plugin) is { } targetName && index.UnreadPlugins.Contains(targetName))
            {
                // a part list of the plugin being checked would look complete (code-review round 4), as in voice-lines
                Console.Error.WriteLine($"can't read all of {SafeText.OneLine(targetName)}'s dialogue, so the list would miss lines. Check the plugin in xEdit or the Creation Kit, save it again, and retry.");
                cantWhy = $"{SafeText.OneLine(targetName)}'s dialogue can't be read in full";
                return 2;
            }
            var (vt, vtName, female) = VoiceOf(npc, index, lo);
            var found = index.LinesOf(npcKey, vt);
            bool Keep(LineRow l) => type == null || TypeMatches(type, l.Category, l.Subtype);
            var lines = found.Lines.Where(Keep)
                .OrderBy(l => l.Shared).ThenBy(l => (int)l.Category).ThenBy(l => (int)l.Subtype)
                .ThenBy(l => l.TopicEditorId ?? l.Topic.ToString(), StringComparer.Ordinal).ThenBy(l => l.Info.ToString(), StringComparer.Ordinal)
                .ThenBy(l => l.Response).ToList();

            // the reference's own lines per type (its shared voice-type lines are mostly generic, so they aren't counted)
            Dictionary<(DialogTopic.CategoryEnum, DialogTopic.SubtypeEnum), int>? refCounts = null;
            int refLines = 0;
            if (reference is { } rf)
            {
                var (rvt, _, _) = VoiceOf(rf.Npc, index, lo);
                var rl = index.LinesOf(rf.Key, rvt).Lines.Where(l => !l.Shared && Keep(l)).ToList();
                refLines = rl.Count;
                refCounts = rl.GroupBy(l => (l.Category, l.Subtype)).ToDictionary(g => g.Key, g => g.Count());
            }
            var types = lines.Select(l => (l.Category, l.Subtype)).Concat(refCounts?.Keys ?? Enumerable.Empty<(DialogTopic.CategoryEnum, DialogTopic.SubtypeEnum)>())
                .Distinct().OrderBy(t => (int)t.Item1).ThenBy(t => (int)t.Item2).ToList();
            var counts = types.Select(t => new TypeCount(t.Item1, t.Item2,
                lines.Count(l => !l.Shared && l.Category == t.Item1 && l.Subtype == t.Item2),
                lines.Count(l => l.Shared && l.Category == t.Item1 && l.Subtype == t.Item2),
                refCounts == null ? null : refCounts.GetValueOrDefault(t))).ToList();

            // the voice and lip files: the INFO's own plugin's folder, the NPC's voice type
            var files = new Dictionary<LineRow, (bool? Wem, bool? Lip)>(ReferenceEqualityComparer.Instance);
            var vf = noFiles || vtName == null ? null : new VoiceFileIndex(input.AssetDirs);
            if (noFiles) Log.Write("lines: --no-files: voice and lip files not looked for");
            else if (vtName == null) Log.Write("lines: the NPC has no voice type of its own (or it has no EditorID), so its voice and lip files can't be named");
            foreach (var l in lines)
                files[l] = vf == null ? (null, null)
                    : (vf.Has(l.VoiceInfo.ModKey.FileName, vtName!, l.WemFile, ".wem"), vf.Has(l.VoiceInfo.ModKey.FileName, vtName!, l.WemFile, ".ffxanim"));
            if (vf != null)
                Log.Write($"lines: voice files: {files.Values.Count(f => f.Wem == true)} of {lines.Count} lines have a .wem, {files.Values.Count(f => f.Lip == true)} a .ffxanim");

            int shared = lines.Count(l => l.Shared);
            int typesHere = counts.Count(c => c.Own + c.Shared > 0);
            int missing = counts.Count(c => c.Missing);
            var npcName = Name(npc, npcKey);
            var result = $"FOUND {lines.Count} line{(lines.Count == 1 ? "" : "s")} of {npcName} ({shared} shared, {typesHere} type{(typesHere == 1 ? "" : "s")}" +
                         (reference is { } rr ? $"; {missing} missing next to {Name(rr.Npc, rr.Key)}" : "") + ")" +
                         (type != null ? $" of type {SafeText.OneLine(type)}" : "");
            resultPrinted = true;
            if (json) Console.WriteLine(Json(version, q, source, npcKey, npc, vtName, female, lines, files, counts, reference, refLines, found, result));
            else Text(version, source, lo.Plugins.Count, npcKey, npc, vt, vtName, female, lines, files, counts, reference, found, max, quiet, result, sw);
            Log.Write($"result: {result}, exit code 0, {sw.Elapsed.TotalSeconds:F1} s");
            return 0;
        }
        finally
        {
            Analyzer.MaxManagedBytes = oldLimit;
            foreach (var p in lo.Plugins) p.Mod.Dispose();
        }
    }

    /// <summary>An NPC query as given: an EditorID, a FormKey or a console FormID.</summary>
    internal sealed record Query(string Text, bool IsFormId, bool IsFormKey, FormKey Key);

    /// <summary>One type's counts: this NPC's own and shared lines, and the --compare NPC's (null without --compare).</summary>
    sealed record TypeCount(DialogTopic.CategoryEnum Category, DialogTopic.SubtypeEnum Subtype, int Own, int Shared, int? Ref)
    {
        /// <summary>The reference speaks this type and this NPC has no line of it (its own or shared).</summary>
        public bool Missing => Ref > 0 && Own == 0 && Shared == 0;
    }

    /// <summary>Checks an NPC query's form before the load order is read; label is "" for the NPC, "--compare " for the reference.</summary>
    internal static Query? Parse(string q, string? plugin, string label, out string cantWhy)
    {
        cantWhy = "";
        if (FindCommand.ForcedEditorId(q) is { } forced) return new Query(forced, false, false, default);
        if (FindCommand.EmptyForcedEditorId(q))
        {
            Console.Error.WriteLine($"{label}{SafeText.OneLine(q)} {FindCommand.EmptyEditorIdText}");
            cantWhy = label.Length > 0 ? "--compare: an empty EditorID" : "an empty EditorID";
            return null;
        }
        bool isFormId = ConsoleId.IsMatch(q), isFormKey = !isFormId && q.Contains(':');
        FormKey key = default;
        if (isFormKey && !FormKey.TryFactory(q, out key))
        {
            Console.Error.WriteLine($"{label}{SafeText.OneLine(q)} isn't a FormKey: give six hex digits, a colon and the plugin's file name, for example 005788:Starfield.esm.");
            cantWhy = label.Length > 0 ? "--compare: not a FormKey" : "not a FormKey";
            return null;
        }
        if (isFormId && plugin != null)
        {
            Console.Error.WriteLine($"{label}{SafeText.OneLine(q)} is a console FormID, which depends on the game's whole load order, and --plugin loads only the plugin and its masters. " +
                                    "Give the NPC's FormKey (six hex digits, a colon and the plugin's file name, for example 000800:MyMod.esm) or its EditorID, or leave out --plugin.");
            cantWhy = label.Length > 0 ? "--compare: a console FormID with --plugin" : "a console FormID with --plugin";
            return null;
        }
        return new Query(q, isFormId, isFormKey, key);
    }

    /// <summary>Every record the query names in the load order (unread plugins are noted on stderr).</summary>
    internal static List<FindCommand.Match> Lookup(Query query, LoadOrderSetup lo, out string? notFoundWhy)
    {
        var unread = new List<string>();
        notFoundWhy = null;
        var q = query.Text;
        var key = query.Key;
        List<FindCommand.Match> matches;
        if (query.IsFormId)
        {
            var fid = uint.Parse(q.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? q[2..] : q, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (FindCommand.FromConsoleId(fid, lo.Plugins, out notFoundWhy) is { } fk) { key = fk; Log.Write($"console FormID {fid:X8} is {fk}"); }
        }
        if (!query.IsFormId && !query.IsFormKey) matches = FindCommand.ByEditorId(q, null, lo.Plugins, unread);
        else if (notFoundWhy != null) matches = new();
        else
        {
            if (!lo.Plugins.Any(p => p.FileName.Equals(key.ModKey.FileName, StringComparison.OrdinalIgnoreCase)))
                notFoundWhy = $"{key.ModKey.FileName} isn't in this load order";
            matches = FindCommand.Copies(new HashSet<FormKey> { key }, new(), lo.Plugins, unread);
        }
        foreach (var u in unread) Console.Error.WriteLine($"note: {u}");
        return matches;
    }

    internal static string NotFoundWhat(Query query, LoadOrderSetup lo, string? notFoundWhy)
    {
        var q = SafeText.OneLine(query.Text);
        return (query.IsFormKey ? $"no record {q}" : query.IsFormId ? $"no record with FormID {q}" : $"no record with EditorID {q}") +
               $" in {lo.Plugins.Count} plugins" + (notFoundWhy != null ? $": {notFoundWhy}" : "");
    }

    internal static string Quoted(string q) => q.Contains(' ') ? $"\"{SafeText.OneLine(q)}\"" : SafeText.OneLine(q);

    /// <summary>The one NPC among the matches, or null (with the message printed) when it is another record type or
    /// several NPCs share the EditorID.</summary>
    static (INpcGetter Npc, FormKey Key)? OneNpc(Query query, List<FindCommand.Match> matches, string label, out string cantWhy)
    {
        cantWhy = "";
        var q = SafeText.OneLine(query.Text);
        var npcs = matches.Where(m => m.Winner.Rec is INpcGetter).ToList();
        if (npcs.Count == 0)
        {
            var m = matches[0];
            Console.Error.WriteLine($"{label}{q} is a {m.Type} ({m.Key}), not an NPC: lines looks at an NPC_ record. " +
                                    "find shows what the record is: sf-mod-check find " + Quoted(query.Text));
            cantWhy = $"{label}{q} is a {m.Type}, not an NPC";
            return null;
        }
        if (npcs.Count > 1)
        {
            Console.Error.WriteLine($"{label}{npcs.Count} NPCs have the EditorID {q}: {string.Join(", ", npcs.Select(n => n.Key.ToString()))}. Give the one you mean as a FormKey.");
            cantWhy = $"{label}{npcs.Count} NPCs have the EditorID {q}";
            return null;
        }
        return ((INpcGetter)npcs[0].Winner.Rec, npcs[0].Key);
    }

    /// <summary>The NPC's own voice type (null for none), its EditorID and whether it is female (null when unreadable).</summary>
    static (FormKey? Vt, string? Name, bool? Female) VoiceOf(INpcGetter npc, DialogueIndex index, LoadOrderSetup lo)
    {
        if (npc.Voice.IsNull) return (null, null, null);
        var v = npc.Voice.FormKey;
        var name = index.EditorId(v, typeof(IVoiceTypeGetter));
        bool? female = null;
        // one record: a link cache, not a scan of every plugin (code-review round 2)
        try
        {
            var cache = lo.Plugins.Select(p => (IStarfieldModGetter)p.Mod).ToImmutableLinkCache();
            if (cache.TryResolve<IVoiceTypeGetter>(v, out var vtr)) female = vtr.Flags.HasFlag(VoiceType.Flag.Female);
        }
        catch (Exception e) when (Fatal.Recoverable(e)) { Console.Error.WriteLine($"note: voice type {v} couldn't be read ({e.GetType().Name})"); }
        return (v, name, female);
    }

    static string Name(INpcGetter npc, FormKey key) => string.IsNullOrEmpty(npc.EditorID) ? key.ToString() : SafeText.OneLine(npc.EditorID);

    static string YesNo(bool? b) => b switch { true => "yes", false => "no", null => "?" };

    static string Row(LineRow l, (bool? Wem, bool? Lip) f)
    {
        var text = SafeText.OneLine(l.Text);
        if (text.Length > MaxText) text = text[..(char.IsHighSurrogate(text[MaxText - 1]) ? MaxText - 1 : MaxText)] + "...";
        var topic = string.IsNullOrEmpty(l.TopicEditorId) ? l.Topic.ToString() : SafeText.OneLine(l.TopicEditorId);
        return $"  {l.Info} {topic} r{l.Response} \"{text}\" wem {YesNo(f.Wem)}, lip {YesNo(f.Lip)}, {l.Emotion}, {l.FoundBy}";
    }

    static void Text(string version, string source, int pluginCount, FormKey npcKey, INpcGetter npc, FormKey? vt, string? vtName, bool? female,
                     List<LineRow> lines, Dictionary<LineRow, (bool? Wem, bool? Lip)> files, List<TypeCount> counts,
                     (INpcGetter Npc, FormKey Key)? reference, NpcLines found, int max, bool quiet, string result, Stopwatch sw)
    {
        var name = npc.Name?.String is { Length: > 0 } n ? $" \"{SafeText.OneLine(n)}\"" : "";
        if (!quiet) Console.WriteLine($"sf-mod-check {version} lines: {(string.IsNullOrEmpty(npc.EditorID) ? "(no EditorID)" : SafeText.OneLine(npc.EditorID))} [{npcKey}]{name} in {pluginCount} plugins ({source})");
        Console.WriteLine(vt is { } v
            ? $"  voice type: {(vtName != null ? SafeText.OneLine(vtName) + " " : "")}[{v}]{(female == true ? " (female)" : female == false ? " (male)" : "")}"
            : "  voice type: none of its own (it may take its voice from a template), so no shared lines are looked for");
        int listed = 0, limit = max == 0 ? int.MaxValue : max;
        bool? lastShared = null; string? lastType = null;
        foreach (var l in lines)
        {
            if (listed >= limit) break;
            if (l.Shared != lastShared)
            {
                Console.WriteLine();
                Console.WriteLine(l.Shared
                    ? $"shared lines (voice type {(vtName != null ? SafeText.OneLine(vtName) : vt.ToString())}: any NPC with this voice type may say them):"
                    : "its lines:");
                lastShared = l.Shared; lastType = null;
            }
            if (l.Type != lastType)
            {
                var c = counts.First(x => x.Category == l.Category && x.Subtype == l.Subtype);
                var k = l.Shared ? c.Shared : c.Own;
                Console.WriteLine($" {l.Type} ({k} line{(k == 1 ? "" : "s")})");
                lastType = l.Type;
            }
            Console.WriteLine(Row(l, files[l]));
            listed++;
        }
        if (lines.Count > listed) Console.WriteLine($"... and {lines.Count - listed} more line{(lines.Count - listed == 1 ? "" : "s")} (--max 0 lists them all)");
        Console.WriteLine();
        if (counts.Count > 0)
        {
            Console.WriteLine(reference is { } r ? $"types (this NPC | shared | {Name(r.Npc, r.Key)} [{r.Key}]):" : "types (this NPC | shared):");
            var w = counts.Max(c => TypeWidth(c.Category, c.Subtype));
            foreach (var c in counts)
                Console.WriteLine($"  {DialogueLines.TypeName(c.Category, c.Subtype).PadRight(w)}  {c.Own,5}  {c.Shared,5}" +
                                  (c.Ref is { } rc ? $"  {rc,5}{(c.Missing ? "   missing" : "")}" : ""));
        }
        Console.WriteLine($"{found.PlayerLines} player line{(found.PlayerLines == 1 ? "" : "s")} in its scenes (the player is silent, not counted)");
        Console.WriteLine($"{found.GenericLines} generic line{(found.GenericLines == 1 ? "" : "s")} with no speaker, speaker condition or scene alias (any NPC may say them), not listed");
        if (reference == null)
            Console.WriteLine("HINT: to see which types of line it lacks next to another NPC (for example a vanilla companion), add --compare <that NPC's EditorID or FormKey>.");
        Console.WriteLine($"RESULT: {result} ({sw.Elapsed.TotalSeconds:F0} s)");
    }

    static int TypeWidth(DialogTopic.CategoryEnum c, DialogTopic.SubtypeEnum s) => DialogueLines.TypeName(c, s).Length;

    static string Json(string version, string q, string source, FormKey npcKey, INpcGetter npc, string? vtName, bool? female, List<LineRow> lines,
                       Dictionary<LineRow, (bool? Wem, bool? Lip)> files, List<TypeCount> counts, (INpcGetter Npc, FormKey Key)? reference, int refLines,
                       NpcLines found, string result)
    {
        var root = new JsonObject
        {
            ["tool"] = "sf-mod-check lines",
            ["version"] = version,
            ["query"] = q,
            ["npc"] = new JsonObject
            {
                ["formKey"] = npcKey.ToString(), ["editorId"] = npc.EditorID, ["voiceType"] = vtName, ["female"] = female,
            },
            ["source"] = source,
            ["lines"] = new JsonArray(lines.Select(l => (JsonNode)new JsonObject
            {
                ["info"] = l.Info.ToString(), ["topic"] = l.Topic.ToString(), ["topicEditorId"] = l.TopicEditorId,
                ["category"] = l.Category.ToString(), ["subtype"] = l.Subtype.ToString(), ["response"] = l.Response,
                ["text"] = l.Text, ["emotion"] = l.Emotion, ["foundBy"] = l.FoundBy, ["shared"] = l.Shared,
                ["wem"] = files[l].Wem, ["lip"] = files[l].Lip, ["quest"] = l.Quest.IsNull ? null : l.Quest.ToString(), ["scene"] = l.Scene?.ToString(),
            }).ToArray()),
            ["counts"] = new JsonArray(counts.Select(c =>
            {
                var o = new JsonObject { ["category"] = c.Category.ToString(), ["subtype"] = c.Subtype.ToString(), ["count"] = c.Own, ["shared"] = c.Shared };
                if (c.Ref is { } n) { o["reference"] = n; o["missing"] = c.Missing; }
                return (JsonNode)o;
            }).ToArray()),
            ["playerLines"] = found.PlayerLines,
            ["genericLines"] = found.GenericLines,
            ["result"] = result,
        };
        if (reference is { } r)
            root.Insert(root.IndexOf("playerLines"), "reference", new JsonObject { ["formKey"] = r.Key.ToString(), ["editorId"] = r.Npc.EditorID, ["lines"] = refLines });
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
