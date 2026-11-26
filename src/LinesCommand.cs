// sf-mod-check lines (0.21.0): the dialogue lines one NPC can speak, grouped by type (the topic's Category / Subtype),
// read only. The line rules are in DialogueLines.cs; the design is in NOTES.md, "0.21.0 (SmcLookup)", "Step 1 design".
//   sf-mod-check lines <EditorID | FormKey | FormID> [--plugins <Plugins.txt> | --mo2 <MO2 folder> [--profile <name>]]
//                      [--data <Data folder>] [--extra-data <folder>] [--type <name>] [--max <n>] [--json] [--quiet]
//                      [--verbose] [--log <file>]
//   sf-mod-check lines <EditorID | FormKey> --plugin <plugin file> [--masters-dir <folder>]... [...]
// Exit 0 = the NPC was found (even with 0 lines), 1 = not found, 2 = could not run. Text ends with one RESULT line;
// --json prints one object.
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
        "                          [--data <Data folder>] [--extra-data <folder>] [--type <name>] [--max <n>] [--json] [--quiet]\n" +
        "                          [--verbose] [--log <file>]\n" +
        "       sf-mod-check lines <EditorID | FormKey> --plugin <plugin file> [--masters-dir <folder>]... [...]\n" +
        "  Lists the dialogue lines one NPC can speak, grouped by type (the topic's Category / Subtype, for example\n" +
        "  Combat / Hit), and counts them per type. A line counts when its speaker is the NPC, a condition names the NPC\n" +
        "  (GetIsID) or an alias it fills (GetIsAliasRef), or a scene gives the line to an alias the NPC fills. Lines for\n" +
        "  the NPC's voice type (GetIsVoiceType) are listed apart as shared lines.\n" +
        "  The NPC   an EditorID (letter case ignored), a FormKey (005788:Starfield.esm) or a console FormID (00005788)\n" +
        "  --plugin  read only this plugin and its masters (found in its folder, then each --masters-dir, then --data)\n" +
        "  --type    keep only lines of this Category (Combat) or Category / Subtype (\"Combat / Hit\"); letter case ignored\n" +
        "  --max     list at most this many lines (default 200; 0 lists them all); the counts are always complete\n" +
        "  --json    print one JSON object with every line\n" +
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
        string? q = null, type = null, logFile = null;
        bool quiet = false, verbose = false, json = false;
        int max = DefaultMax;
        var input = new LoadOrderInput(allowPlugin: true);
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

        bool isFormId = ConsoleId.IsMatch(q), isFormKey = !isFormId && q.Contains(':');
        FormKey key = default;
        if (isFormKey && !FormKey.TryFactory(q, out key))
        {
            Console.Error.WriteLine($"{SafeText.OneLine(q)} isn't a FormKey: give six hex digits, a colon and the plugin's file name, for example 005788:Starfield.esm.");
            cantWhy = "not a FormKey";
            return 2;
        }
        if (isFormId && input.Plugin != null)
        {
            Console.Error.WriteLine($"{SafeText.OneLine(q)} is a console FormID, which depends on the game's whole load order, and --plugin loads only the plugin and its masters. " +
                                    "Give the NPC's FormKey (six hex digits, a colon and the plugin's file name, for example 000800:MyMod.esm) or its EditorID, or leave out --plugin.");
            cantWhy = "a console FormID with --plugin";
            return 2;
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
            var unread = new List<string>();
            string? notFoundWhy = null;
            List<FindCommand.Match> matches;
            if (isFormId)
            {
                var fid = uint.Parse(q.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? q[2..] : q, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (FindCommand.FromConsoleId(fid, lo.Plugins, out notFoundWhy) is { } fk) { key = fk; Log.Write($"console FormID {fid:X8} is {fk}"); }
            }
            if (!isFormId && !isFormKey) matches = FindCommand.ByEditorId(q, null, lo.Plugins, unread);
            else if (notFoundWhy != null) matches = new();
            else
            {
                if (!lo.Plugins.Any(p => p.FileName.Equals(key.ModKey.FileName, StringComparison.OrdinalIgnoreCase)))
                    notFoundWhy = $"{key.ModKey.FileName} isn't in this load order";
                matches = FindCommand.Copies(new HashSet<FormKey> { key }, new(), lo.Plugins, unread);
            }
            foreach (var u in unread) Console.Error.WriteLine($"note: {u}");
            var source = lo.OrderSource;
            var npcs = matches.Where(m => m.Winner.Rec is INpcGetter).ToList();
            if (matches.Count == 0)
            {
                var what = (isFormKey ? $"no record {SafeText.OneLine(q)}" : isFormId ? $"no record with FormID {SafeText.OneLine(q)}" : $"no record with EditorID {SafeText.OneLine(q)}") +
                           $" in {lo.Plugins.Count} plugins" + (notFoundWhy != null ? $": {notFoundWhy}" : "");
                var hint = (!isFormId && !isFormKey ? "EditorIDs must match exactly (letter case doesn't matter). " : "") +
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
            if (npcs.Count == 0)
            {
                var m = matches[0];
                Console.Error.WriteLine($"{SafeText.OneLine(q)} is a {m.Type} ({m.Key}), not an NPC: lines looks at an NPC_ record. " +
                                        "find shows what the record is: sf-mod-check find " + (q.Contains(' ') ? $"\"{SafeText.OneLine(q)}\"" : SafeText.OneLine(q)));
                cantWhy = $"{SafeText.OneLine(q)} is a {m.Type}, not an NPC";
                return 2;
            }
            if (npcs.Count > 1)
            {
                Console.Error.WriteLine($"{npcs.Count} NPCs have the EditorID {SafeText.OneLine(q)}: {string.Join(", ", npcs.Select(n => n.Key.ToString()))}. Give the one you mean as a FormKey.");
                cantWhy = $"{npcs.Count} NPCs have the EditorID {SafeText.OneLine(q)}";
                return 2;
            }
            var npc = (INpcGetter)npcs[0].Winner.Rec;
            var npcKey = npcs[0].Key;

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
            FormKey? vt = npc.Voice.IsNull ? null : npc.Voice.FormKey;
            string? vtName = vt is { } v ? index.EditorId(v, typeof(IVoiceTypeGetter)) : null;
            bool? female = null;
            if (vt is { } v2)
            {
                var vtCopies = FindCommand.Copies(new HashSet<FormKey> { v2 }, new(), lo.Plugins, new());
                if (vtCopies.Count > 0 && vtCopies[0].Winner.Rec is IVoiceTypeGetter vtr) female = vtr.Flags.HasFlag(VoiceType.Flag.Female);
            }
            var found = index.LinesOf(npcKey, vt);
            var lines = found.Lines.Where(l => type == null || TypeMatches(type, l.Category, l.Subtype))
                .OrderBy(l => l.Shared).ThenBy(l => (int)l.Category).ThenBy(l => (int)l.Subtype)
                .ThenBy(l => l.TopicEditorId ?? l.Topic.ToString(), StringComparer.Ordinal).ThenBy(l => l.Info.ToString(), StringComparer.Ordinal)
                .ThenBy(l => l.Response).ToList();
            var counts = lines.GroupBy(l => (l.Category, l.Subtype)).OrderBy(g => (int)g.Key.Category).ThenBy(g => (int)g.Key.Subtype)
                .Select(g => (g.Key.Category, g.Key.Subtype, Own: g.Count(l => !l.Shared), Shared: g.Count(l => l.Shared))).ToList();
            int shared = lines.Count(l => l.Shared);
            var npcName = string.IsNullOrEmpty(npc.EditorID) ? npcKey.ToString() : SafeText.OneLine(npc.EditorID);
            var result = $"FOUND {lines.Count} line{(lines.Count == 1 ? "" : "s")} of {npcName} ({shared} shared, {counts.Count} type{(counts.Count == 1 ? "" : "s")})" +
                         (type != null ? $" of type {SafeText.OneLine(type)}" : "");
            resultPrinted = true;
            if (json) Console.WriteLine(Json(version, q, source, npcKey, npc, vtName, female, lines, counts, found, result));
            else Text(version, source, lo.Plugins.Count, npcKey, npc, vt, vtName, female, lines, counts, found, max, quiet, result, sw);
            Log.Write($"result: {result}, exit code 0, {sw.Elapsed.TotalSeconds:F1} s");
            return 0;
        }
        finally
        {
            Analyzer.MaxManagedBytes = oldLimit;
            foreach (var p in lo.Plugins) p.Mod.Dispose();
        }
    }

    static string Row(LineRow l)
    {
        var text = SafeText.OneLine(l.Text);
        if (text.Length > MaxText) text = text[..MaxText] + "...";
        var topic = string.IsNullOrEmpty(l.TopicEditorId) ? l.Topic.ToString() : SafeText.OneLine(l.TopicEditorId);
        return $"  {l.Info} {topic} r{l.Response} \"{text}\" wem ?, lip ?, {l.Emotion}, {l.FoundBy}";
    }

    static void Text(string version, string source, int pluginCount, FormKey npcKey, INpcGetter npc, FormKey? vt, string? vtName, bool? female,
                     List<LineRow> lines, List<(DialogTopic.CategoryEnum Category, DialogTopic.SubtypeEnum Subtype, int Own, int Shared)> counts,
                     NpcLines found, int max, bool quiet, string result, Stopwatch sw)
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
            Console.WriteLine(Row(l));
            listed++;
        }
        if (lines.Count > listed) Console.WriteLine($"... and {lines.Count - listed} more line{(lines.Count - listed == 1 ? "" : "s")} (--max 0 lists them all)");
        Console.WriteLine();
        if (counts.Count > 0)
        {
            Console.WriteLine("types (this NPC | shared):");
            var w = counts.Max(c => TypeWidth(c.Category, c.Subtype));
            foreach (var c in counts)
                Console.WriteLine($"  {DialogueLines.TypeName(c.Category, c.Subtype).PadRight(w)}  {c.Own,5}  {c.Shared,5}");
        }
        Console.WriteLine($"{found.PlayerLines} player line{(found.PlayerLines == 1 ? "" : "s")} in its scenes (the player is silent, not counted)");
        Console.WriteLine($"{found.GenericLines} generic line{(found.GenericLines == 1 ? "" : "s")} with no speaker, speaker condition or scene alias (any NPC may say them), not listed");
        Console.WriteLine($"RESULT: {result} ({sw.Elapsed.TotalSeconds:F0} s)");
    }

    static int TypeWidth(DialogTopic.CategoryEnum c, DialogTopic.SubtypeEnum s) => DialogueLines.TypeName(c, s).Length;

    static string Json(string version, string q, string source, FormKey npcKey, INpcGetter npc, string? vtName, bool? female, List<LineRow> lines,
                       List<(DialogTopic.CategoryEnum Category, DialogTopic.SubtypeEnum Subtype, int Own, int Shared)> counts, NpcLines found, string result)
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
                ["wem"] = null, ["lip"] = null, ["quest"] = l.Quest.IsNull ? null : l.Quest.ToString(), ["scene"] = l.Scene?.ToString(),
            }).ToArray()),
            ["counts"] = new JsonArray(counts.Select(c => (JsonNode)new JsonObject
            {
                ["category"] = c.Category.ToString(), ["subtype"] = c.Subtype.ToString(), ["count"] = c.Own, ["shared"] = c.Shared,
            }).ToArray()),
            ["playerLines"] = found.PlayerLines,
            ["genericLines"] = found.GenericLines,
            ["result"] = result,
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
