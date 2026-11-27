// sf-mod-check voice-lines (0.21.0): a tab-separated list of a plugin's voiced dialogue lines in the Creation Kit
// dialogue-export columns that voice tools read (sf-mod-build's voice step, Starfield Voice Cloner), one row per voice
// type and file number. Design: StarfieldPluginCheck NOTES.md, "0.21.0 (SmcLookup)", "Step 1 design" (`voice-lines`).
//   sf-mod-check voice-lines <plugin> [--out <file>] [--voice <voice type EditorID>]... [--utf8] [--data <Data folder>]
//                            [--masters-dir <folder>]... [--quiet] [--verbose] [--log <file>]
// The columns are a contract (the README has them word for word):
//   VOICE TYPE  FILENAME  RESPONSE TEXT  FACE  SCRIPT NOTES  SEX  FULL PATH  INFO  RESPONSE
// Lines: the INFOs the plugin adds (its own FormKeys) with responses of their own, no SharedDialog link, not in a player
// topic (a scene's Dialogue action for the player, a Player Dialogue action's player choices), and each
// response with a non-zero file number. Voice types per line: first voice-missing's condition rules (a Subject
// GetIsID == 1 names an NPC: its voice type; GetIsVoiceType == 1: the voice type or each one in the form list); when
// those name none, the lines rules for a speaker (the INFO's Speaker, a GetIsAliasRef == 1 alias or a scene alias of
// the topic's quest, with the NPCs that fill it); still none: the --voice voice types, else no row (NOTE).
// The TSV goes to --out or stdout; every message and the RESULT line go to stderr. Exit 0 = written (even 0 rows),
// 2 = could not run.
using System.Diagnostics;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using SfModCheck.Conflict;

namespace SfModCheck;

public static class VoiceLinesCommand
{
    public const string Usage =
        "usage: sf-mod-check voice-lines <plugin> [--out <file>] [--voice <voice type EditorID>]... [--utf8]\n" +
        "                                [--data <Data folder>] [--masters-dir <folder>]... [--quiet] [--verbose] [--log <file>]\n" +
        "  Writes the plugin's voiced dialogue lines as a tab-separated file in the Creation Kit dialogue-export columns\n" +
        "  that voice tools read, one row per voice type and file number:\n" +
        "    VOICE TYPE  FILENAME  RESPONSE TEXT  FACE  SCRIPT NOTES  SEX  FULL PATH  INFO  RESPONSE\n" +
        "  A line's voice types come from the first of these that names an NPC or a voice type: its own conditions\n" +
        "  (GetIsID, GetIsVoiceType; each alternative of an OR counts), its speaker (the line's Speaker, or the NPC that\n" +
        "  fills its alias or scene alias), its dialogue group's conditions, its quest's. --voice covers lines with none.\n" +
        "  Player lines and lines that link a SharedDialog line are left out.\n" +
        "  <plugin>        the plugin file; its masters are found in its folder, then each --masters-dir, then --data\n" +
        "  --out           write the list to this file (default: the screen, stdout)\n" +
        "  --voice         a voice type (EditorID) for the lines that name none; may be given more than once\n" +
        "  --utf8          write UTF-8 (default: Windows-1252, what the Creation Kit writes and Voice Cloner reads;\n" +
        "                  a UTF-8 file needs SVC_EXPORT_ENCODING=utf-8 set on the Voice Cloner side)\n" +
        "Exit 0 = written (even with no rows), 2 = could not run. Messages and the RESULT line go to stderr.";

    public const string Header = "VOICE TYPE\tFILENAME\tRESPONSE TEXT\tFACE\tSCRIPT NOTES\tSEX\tFULL PATH\tINFO\tRESPONSE";

    /// <summary>One row of the list.</summary>
    internal sealed record Row(string VoiceType, uint File, string Text, string Face, string Notes, string Sex, FormKey Info, int Response)
    {
        public string FileName => (File & 0xFFFFFF).ToString("X8");
    }

    public static int Run(string[] a, string version, string byline)
    {
        try
        {
            var code = RunLogged(a, version, byline, out var resultPrinted, out var cantWhy);
            if (code == 2 && !resultPrinted) Console.Error.WriteLine($"RESULT: CANNOT CHECK ({cantWhy})");
            return code;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(Fatal.ResultLine(e, () => $"RESULT: CANNOT CHECK ({e.GetType().Name}: {SafeText.OneLine(e.Message.Split('\n')[0])})"));
            Fatal.AfterResult(() =>
            {
                Console.Error.WriteLine(Log.On ? "The log has the full error." : "For the full error, run again with --log sf-mod-check-log.txt and look at that file.");
                Log.Write($"stopped: {e}");
            });
            return 2;
        }
        finally { Log.Close(); }
    }

    static int RunLogged(string[] a, string version, string byline, out bool resultPrinted, out string cantWhy)
    {
        resultPrinted = false;
        cantWhy = "bad arguments; see the message above";
        string? plugin = null, outFile = null, logFile = null;
        bool quiet = false, verbose = false, utf8 = false;
        var voices = new List<string>();
        using var input = new LoadOrderInput(allowPlugin: true, command: "voice-lines");
        for (int i = 0; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "--data": case "--masters-dir":
                    if (input.TryOption(a, ref i, Usage) == false) return 2;
                    continue;
                case "--out":
                    if (Cli.Value(a, ref i, "a file to write the list to", Usage, "voice-lines.txt") is not { } o) return 2;
                    outFile = o; break;
                case "--voice":
                    if (Cli.Value(a, ref i, "a voice type's EditorID", Usage) is not { } v) return 2;
                    if (v.Trim().Length == 0) { Console.Error.WriteLine("--voice needs a voice type's EditorID, not an empty name."); return 2; }
                    if (!Mo2Profile.IsFolderName(v.Trim())) { Console.Error.WriteLine($"--voice {SafeText.OneLine(v.Trim())}: a voice type's EditorID is one folder name, with no \\ / : * ? \" < > | characters and not . or .."); return 2; }
                    voices.Add(v.Trim()); break;
                case "--log":
                    if (Cli.Value(a, ref i, "a file name", Usage) is not { } lf) return 2;
                    logFile = lf; break;
                case "--utf8": utf8 = true; break;
                case "--verbose": verbose = true; break;
                case "--quiet": quiet = true; break;
                case "-h": case "--help": Console.WriteLine(Usage); return 0;
                default:
                    if (a[i].StartsWith("--"))
                    {
                        Console.Error.WriteLine(a[i] is "--plugin" or "--plugins" or "--mo2" or "--profile" or "--extra-data"
                            ? $"voice-lines reads one plugin and its masters, so it doesn't take {a[i]}: give the plugin file as the first argument, and its masters' folder with --masters-dir or --data."
                            : $"unknown option {a[i]}");
                        Console.Error.WriteLine(Usage);
                        return 2;
                    }
                    if (plugin != null)
                    {
                        Console.Error.WriteLine($"voice-lines reads one plugin per run: got {plugin} and {a[i]}. Run it once for each plugin.");
                        return 2;
                    }
                    plugin = a[i];
                    break;
            }
        }
        if (string.IsNullOrWhiteSpace(plugin))
        {
            Console.Error.WriteLine("voice-lines needs the plugin file to read, for example: sf-mod-check voice-lines MyMod.esm --out MyMod-lines.tsv");
            Console.Error.WriteLine(Usage);
            cantWhy = "no plugin given";
            return 2;
        }
        if (!File.Exists(plugin))
        {
            Console.Error.WriteLine($"voice-lines: there is no plugin file {SafeText.OneLine(plugin)}. Give the path of the .esm, .esp or .esl file.");
            cantWhy = $"no plugin file {SafeText.OneLine(Path.GetFileName(plugin))}";
            return 2;
        }
        int zero = 0;
        if (input.TryOption(["--plugin", plugin], ref zero, Usage) != true) { cantWhy = "the plugin can't be taken as the input"; return 2; }
        if (!quiet) Console.Error.WriteLine(byline);
        if (verbose) Log.ToStderr();
        if (logFile != null && Log.ToFile(logFile) is { } logError) { Console.Error.WriteLine(logError); cantWhy = "the log file can't be written"; return 2; }
        Log.Write($"sf-mod-check {version} voice-lines on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, .NET {Environment.Version}");
        Log.Write($"arguments: {string.Join(" ", a.Select(x => x.Contains(' ') ? $"\"{x}\"" : x))}");
        Log.Write($"working folder: {Environment.CurrentDirectory}");
        if (!input.Resolve(out var why)) { cantWhy = why; return 2; }

        var sw = Stopwatch.StartNew();
        LoadOrderSetup lo;
        try { lo = input.Build(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            cantWhy = $"the plugin or a master can't be read: {e.GetType().Name}";
            Console.Error.WriteLine($"can't read the plugin or its masters: {e.Message} Check that this account can read them, close any program that has the plugin open (xEdit, the Creation Kit), and try again.");
            Log.Write($"stopped: reading the load order: {e}");
            return 2;
        }
        var oldLimit = Analyzer.MaxManagedBytes;
        try
        {
            foreach (var p in lo.Problems) Log.Write($"load-order problem: {p}");
            if (FindCommand.Unloaded(lo, input.Plugin!) is { } unloadedWhy) { cantWhy = unloadedWhy; return 2; }
            var target = lo.Plugins.First(p => p.FileName.Equals(Path.GetFileName(input.Plugin!), StringComparison.OrdinalIgnoreCase));
            Analyzer.MaxManagedBytes = LinesCommand.MemoryLimit;
            DialogueIndex index;
            try { index = DialogueIndex.Build(lo.Plugins, quiet ? null : Console.Error.WriteLine, target.FileName); }
            catch (MemoryLimitException e)
            {
                Log.Write($"stopped: {e.Message}");
                cantWhy = $"memory passed {LinesCommand.MemoryLimit / 1e9:F1} GB while reading the dialogue";
                return 2;
            }
            foreach (var u in index.Unread) Console.Error.WriteLine($"NOTE: {u}");
            if (index.UnreadPlugins.Contains(target.FileName))
            {
                // a part list would make the voice tool skip lines without saying so (code-review round 1)
                Console.Error.WriteLine($"can't read all of {target.FileName}'s dialogue, so the list would miss lines. Check the plugin in xEdit or the Creation Kit, save it again, and retry.");
                cantWhy = $"{target.FileName}'s dialogue can't be read in full";
                return 2;
            }
            if (index.UnreadPlugins.Count > 0 || index.UnreadActorPlugins.Count > 0)
            {
                // a master's quests, aliases, form lists or placed actors cut short take voice types from lines
                // without saying so, so this is an error too (code-review round 3)
                var which = string.Join(", ", index.UnreadPlugins.Concat(index.UnreadActorPlugins).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase));
                Console.Error.WriteLine($"can't read all the quests, form lists or placed actors of {which}, which {target.FileName}'s lines may use for their voice types, so the list could miss rows. Check the plugin in xEdit and retry.");
                cantWhy = $"{which} can't be read in full";
                return 2;
            }
            if (index.UnreadScenePlugins.Count > 0)
            {
                // the unread scenes may make some of the plugin's lines player lines (code-review round 2)
                var which = string.Join(", ", index.UnreadScenePlugins.Order(StringComparer.OrdinalIgnoreCase));
                Console.Error.WriteLine($"can't read all the scenes of {which}, which may use {target.FileName}'s lines, so the list could be wrong. Check the plugin in xEdit and retry.");
                cantWhy = $"the scenes of {which} can't be read in full";
                return 2;
            }
            var cache = lo.Plugins.Select(p => (IStarfieldModGetter)p.Mod).ToImmutableLinkCache();
            var (rows, voiced, noVoice, warnings) = Collect(index, cache, target, voices);

            var text = new StringBuilder();
            text.Append(Header).Append("\r\n");
            var fileName = target.FileName;
            foreach (var r in rows)
                text.Append(string.Join('\t', Field(r.VoiceType), r.FileName, Field(r.Text), Field(r.Face), Field(r.Notes), r.Sex,
                    Field($"Data\\Sound\\Voice\\{fileName}\\{r.VoiceType}\\{r.FileName}.wem"), r.Info.ToString(), r.Response.ToString())).Append("\r\n");
            var (bytes, replaced) = Encode(text.ToString(), utf8);

            foreach (var w in warnings) Console.Error.WriteLine($"WARNING: {w}");
            if (noVoice.Count > 0)
                Console.Error.WriteLine($"NOTE: {noVoice.Count} voiced line{(noVoice.Count == 1 ? " has" : "s have")} no voice type (first: {noVoice[0]}): no condition, speaker or " +
                                        "alias names one, or the NPC they name takes its voice from a template (it has no voice type of its own), so they have no rows. " +
                                        "Give their voice type with --voice <EditorID>.");
            if (replaced > 0)
                Console.Error.WriteLine($"NOTE: {replaced} character{(replaced == 1 ? "" : "s")} can't be written in Windows-1252 and became ?. Use --utf8 if the voice tool reads UTF-8.");

            if (outFile != null)
            {
                try
                {
                    var full = Path.GetFullPath(outFile);
                    // never write the TSV over the plugin or one of its masters (code-review round 5)
                    if (OutIsPlugin(full, lo, input.Plugin!))
                    {
                        Console.Error.WriteLine($"--out {SafeText.OneLine(outFile)} is the plugin or one of its masters; give a .tsv file name instead, for example --out {SafeText.OneLine(Path.GetFileNameWithoutExtension(full))}-lines.tsv");
                        cantWhy = "the --out file is the plugin or a master";
                        return 2;
                    }
                    if (Path.GetDirectoryName(full) is { } d) Directory.CreateDirectory(d);
                    File.WriteAllBytes(full, bytes);
                    Log.Write($"wrote {full}: {bytes.Length} bytes");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    Console.Error.WriteLine($"can't write {SafeText.OneLine(outFile)}: {SafeText.OneLine(e.Message)} Check the folder exists and this account may write there, and that no program has the file open.");
                    cantWhy = "the --out file can't be written";
                    return 2;
                }
            }
            else
            {
                using var stdout = Console.OpenStandardOutput();
                stdout.Write(bytes);
                stdout.Flush();
            }
            var vts = rows.Select(r => r.VoiceType).Distinct().Count();
            resultPrinted = true;
            Console.Error.WriteLine($"RESULT: {rows.Count} row{(rows.Count == 1 ? "" : "s")}, {voiced} voiced line{(voiced == 1 ? "" : "s")}, " +
                                    $"{vts} voice type{(vts == 1 ? "" : "s")}; {noVoice.Count} line{(noVoice.Count == 1 ? "" : "s")} with no voice type ({sw.Elapsed.TotalSeconds:F1} s)");
            Log.Write($"result: {rows.Count} rows, exit code 0, {sw.Elapsed.TotalSeconds:F1} s");
            return 0;
        }
        finally
        {
            Analyzer.MaxManagedBytes = oldLimit;
            foreach (var p in lo.Plugins) p.Mod.Dispose();
        }
    }

    /// <summary>The rows (sorted: voice type, then file name), the number of voiced lines read, the lines with no voice
    /// type (labels), and the warnings for one file number with two texts.</summary>
    internal static (List<Row> Rows, int Voiced, List<string> NoVoice, List<string> Warnings) Collect(
        DialogueIndex index, ILinkCache cache, PluginInfo target, List<string> extraVoices)
    {
        var modKey = target.Mod.ModKey;
        var vtInfo = new Dictionary<FormKey, (string? Name, string Sex)>();
        var badNames = new List<string>();
        (string? Name, string Sex) Vt(FormKey k)
        {
            if (vtInfo.TryGetValue(k, out var x)) return x;
            IVoiceTypeGetter? v = null;
            try { cache.TryResolve(k, out v); } catch (Exception e) when (Fatal.Recoverable(e)) { v = null; }
            x = v == null ? (index.EditorId(k, typeof(IVoiceTypeGetter))?.ToLowerInvariant(), "")
                          : (string.IsNullOrEmpty(v.EditorID) ? null : v.EditorID.ToLowerInvariant(), v.Flags.HasFlag(VoiceType.Flag.Female) ? "female" : "male");
            // the name becomes a folder in FULL PATH, which the voice tools write to: one that isn't a plain folder
            // name (..\ or a drive) gets no rows (code-review round 4)
            if (x.Name != null && !Mo2Profile.IsFolderName(x.Name))
            {
                badNames.Add($"voice type {k} has the EditorID {SafeText.OneLine(x.Name)}, which isn't one folder name, so its lines get no rows for it");
                x = (null, x.Sex);
            }
            return vtInfo[k] = x;
        }
        INpcGetter? Npc(FormKey k)
        {
            try { return cache.TryResolve<INpcGetter>(k, out var n) ? n : null; }
            catch (Exception e) when (Fatal.Recoverable(e)) { return null; }
        }
        bool IsVoiceType(FormKey k)
        {
            try { return cache.TryResolve<IVoiceTypeGetter>(k, out _); }
            catch (Exception e) when (Fatal.Recoverable(e)) { return false; }
        }

        // --voice: by EditorID; one that isn't in the plugin or its masters still names a folder, with SEX empty
        var extra = new List<(string Name, string Sex)>();
        foreach (var name in extraVoices)
        {
            IVoiceTypeGetter? v = null;
            try { cache.TryResolve(name, out v); } catch (Exception e) when (Fatal.Recoverable(e)) { v = null; }
            if (v == null) Console.Error.WriteLine($"NOTE: --voice {SafeText.OneLine(name)} isn't a voice type in the plugin or its masters, so its rows have no SEX.");
            extra.Add((name.ToLowerInvariant(), v == null ? "" : v.Flags.HasFlag(VoiceType.Flag.Female) ? "female" : "male"));
        }

        // The voice types that may speak a line. Conditions are read in tiers (code-review round 1): the line's own,
        // then its dialogue group's, then its quest's DialogConditions, and the first tier that names a voice type is
        // used, so a quest-wide GetIsVoiceType list doesn't add rows to a line whose own GetIsID names one NPC.
        // deep: false stops before the dialogue group's and quest's lists (a SharedDialog line's linkers come first there,
        // code-review round 3).
        // deepOnly: only the dialogue group's and quest's lists (the last pass of a linked line, whose own tiers were read).
        // Returns whether the line is settled: a voice type found, or an NPC named (code-review round 4).
        bool Resolve(IDialogResponsesGetter i, IDialogTopicGetter topic, SortedDictionary<string, string> found, List<string> why, bool deep = true, bool deepOnly = false)
        {
            void AddVt(FormKey k, string how)
            {
                var (n, sex) = Vt(k);
                if (n == null) { why.Add($"{how}: voice type {k} has no EditorID, no folder"); return; }
                found[n] = sex;
                why.Add($"{how}: {SafeText.OneLine(n)}");
            }
            // named: an NPC was named, even one whose voice type can't be read (a templated NPC); that settles the line
            // too, so a quest-wide list doesn't fill in for it (code-review round 2)
            bool named = false;
            void AddNpc(FormKey npc, string how)
            {
                if (Npc(npc) is not { } n) return;
                named = true;
                if (n.Voice.IsNull) { why.Add($"{how}: NPC {npc} has no voice type of its own"); return; }
                AddVt(n.Voice.FormKey, $"{how} {npc}");
            }
            // voice-missing's condition rules, on one tier of conditions
            // (0.24.0, the 0.21.1 OR fix) In an OR run that names an NPC or a voice type, a GetIsAliasRef alternative is
            // a speaker too: GetIsID Other OR GetIsAliasRef X gives Other's voice type and the voice types of X's NPCs.
            void Tier(IReadOnlyList<IConditionGetter> tier)
            {
                foreach (var part in DialogueLines.AndParts(tier))
                {
                    bool names = false;
                    foreach (var c in part)
                    {
                        if (!DialogueLines.Positive(c)) continue;
                        if (c.Data is IGetIsIDConditionDataGetter g) { names = true; AddNpc(g.FirstParameter.Link.FormKey, "GetIsID"); }
                        if (c.Data is IGetIsVoiceTypeConditionDataGetter gv)
                        {
                            names = true;
                            var k = gv.VoiceTypeOrList.Link.FormKey;
                            if (IsVoiceType(k)) AddVt(k, "GetIsVoiceType");
                            else foreach (var x in index.ListItems(k)) if (IsVoiceType(x)) AddVt(x, $"GetIsVoiceType list {k}");
                        }
                    }
                    if (!names || part.Count < 2) continue;
                    foreach (var c in part)
                        if (DialogueLines.Positive(c) && c.Data is IGetIsAliasRefConditionDataGetter ar)
                            foreach (var npc in index.AliasNpcs(topic.Quest.FormKey, ar.FirstParameter)) AddNpc(npc, $"GetIsAliasRef {ar.FirstParameter} (OR)");
                }
            }
            bool Settled() => found.Count > 0 || named;
            var tiers = index.ConditionTiers(i, topic);
            if (!deepOnly)
            {
                // 1. the line's own conditions
                Tier(tiers[0]);
                if (Settled()) return true;
                // 2. the lines rules for a speaker: the line's speaker, a GetIsAliasRef alias, a scene alias. They come before
                // the dialogue group's and the quest's lists, since the game ANDs those with the line's own (code-review round 2)
                if (!i.Speaker.IsNull) AddNpc(i.Speaker.FormKey, "speaker");
                foreach (var c in index.ConditionsOf(i, topic))
                    if (DialogueLines.Positive(c) && c.Data is IGetIsAliasRefConditionDataGetter ar)
                        foreach (var npc in index.AliasNpcs(topic.Quest.FormKey, ar.FirstParameter)) AddNpc(npc, $"GetIsAliasRef {ar.FirstParameter}");
                if (index.SceneTopics.TryGetValue(topic.FormKey, out var uses))
                    foreach (var u in uses)
                        foreach (var npc in index.AliasNpcs(u.Quest, u.Alias)) AddNpc(npc, $"scene {u.Scene} alias {u.Alias}");
                if (Settled() || !deep) return Settled();
            }
            // 3. the dialogue group's conditions, then the quest's DialogConditions
            foreach (var tier in tiers.Skip(1))
            {
                Tier(tier);
                if (Settled()) return true;
            }
            return false;
        }

        // the plugin's lines that link a SharedDialog line: that line is spoken by their voice types
        var linkers = new Dictionary<FormKey, List<DialogueIndex.InfoCopy>>();
        foreach (var (_, copy) in index.Infos)
            if (copy.Info.Responses.Count == 0 && !copy.Info.SharedDialog.IsNull)
            {
                if (!linkers.TryGetValue(copy.Info.SharedDialog.FormKey, out var l)) linkers[copy.Info.SharedDialog.FormKey] = l = new();
                l.Add(copy);
            }

        var rows = new Dictionary<(string, uint), Row>();
        var warnings = new List<string>();
        var noVoice = new List<string>();
        int voiced = 0;
        foreach (var (key, copy) in index.Infos.Where(kv => kv.Key.ModKey == modKey).OrderBy(kv => kv.Key.ID))
        {
            var i = copy.Info;
            var label = i.EditorID is { Length: > 0 } ed ? $"{SafeText.OneLine(ed)} ({key})" : key.ToString();
            if (!index.Topics.TryGetValue(copy.Topic, out var topic)) continue;
            if (i.Responses.Count == 0 || !i.SharedDialog.IsNull) { Log.Write($"voice-lines: {label}: no responses of its own or a SharedDialog line, left out"); continue; }
            if (index.PlayerTopics.Contains(topic.FormKey)) { Log.Write($"voice-lines: {label}: a player line, left out"); continue; }
            if (!i.Responses.Any(r => r.WEMFile != 0)) { Log.Write($"voice-lines: {label}: no response has a file number, left out"); continue; }
            voiced++;

            var found = new SortedDictionary<string, string>(StringComparer.Ordinal);   // voice type -> SEX
            var why = new List<string>();
            // a line other lines link: its own conditions and speaker first, then the linkers, and only then its dialogue
            // group's and quest's lists, so a quest-wide list on the shared topic doesn't beat the linkers' speakers
            var linked = linkers.TryGetValue(key, out var links);
            // a named NPC with no voice type settles a linked line too (no rows, the NOTE), as for any line
            var settled = Resolve(i, topic, found, why, deep: !linked);
            if (!settled && linked)
            {
                // a linker whose speaker or GetIsID names an NPC with no voice type settles the line too, so the group's
                // and quest's lists don't add rows for it (code-review round 5)
                var linkerSettled = false;
                foreach (var lk in links!)
                    if (index.Topics.TryGetValue(lk.Topic, out var lt) && !index.PlayerTopics.Contains(lt.FormKey))
                    {
                        var before = why.Count;
                        if (Resolve(lk.Info, lt, found, why)) linkerSettled = true;
                        for (int w = before; w < why.Count; w++) why[w] = $"shared by {lk.Info.FormKey}, {why[w]}";
                    }
                if (found.Count == 0 && !linkerSettled) Resolve(i, topic, found, why, deepOnly: true);
            }
            if (found.Count == 0 && extra.Count > 0)
                foreach (var (n, sex) in extra) { found[n] = sex; why.Add($"--voice: {n}"); }
            Log.Write($"voice-lines: {label}: {(found.Count == 0 ? "no voice type" : SafeText.OneLine(string.Join(", ", found.Keys)))}" +
                      (why.Count > 0 ? $" ({string.Join("; ", why)})" : ""));
            if (found.Count == 0) { noVoice.Add(label); continue; }

            for (int r = 0; r < i.Responses.Count; r++)
            {
                var resp = i.Responses[r];
                if (resp.WEMFile == 0) continue;
                var face = resp.Emotion.IsNull ? "" : DialogueLines.EmotionName(index.EditorId(resp.Emotion.FormKey, typeof(IKeywordGetter)), resp.Emotion.FormKey, false);
                if (face.StartsWith('[')) face = "";   // a keyword with no EditorID
                var text = resp.ResponseText?.String ?? "";
                foreach (var (vt, sex) in found)
                {
                    var row = new Row(vt, resp.WEMFile, text, face, resp.ScriptNotes ?? "", sex, key, r + 1);
                    var k = (vt, resp.WEMFile & 0xFFFFFF);
                    if (rows.TryGetValue(k, out var first))
                    {
                        if (Clean(first.Text) != Clean(text))
                            warnings.Add($"voice type {SafeText.OneLine(vt)}, file {row.FileName}: {first.Info} r{first.Response} and {key} r{r + 1} have different texts, " +
                                         $"but one file can say only one of them; the list has {first.Info}'s. Give one of the lines its own file number.");
                        continue;
                    }
                    rows[k] = row;
                }
            }
        }
        var sorted = rows.Values.OrderBy(r => r.VoiceType, StringComparer.Ordinal).ThenBy(r => r.FileName, StringComparer.Ordinal).ToList();
        warnings.InsertRange(0, badNames);
        return (sorted, voiced, noVoice, warnings);
    }

    /// <summary>True when the --out path is the input plugin or a loaded plugin's file (code-review round 5).</summary>
    internal static bool OutIsPlugin(string fullOut, LoadOrderSetup lo, string plugin)
    {
        static string Full(string p) { try { return Path.GetFullPath(p); } catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return ""; } }
        return lo.Plugins.Select(p => p.Path).Append(plugin)
            .Where(p => !string.IsNullOrEmpty(p))
            .Any(p => string.Equals(Full(p), fullOut, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Tabs, line breaks and other control characters become spaces (one row a line, one field a column).</summary>
    internal static string Clean(string s)
    {
        if (!s.Any(char.IsControl)) return s;
        return string.Concat(s.Select(ch => char.IsControl(ch) ? ' ' : ch));
    }

    /// <summary>A field as Python's csv module reads it: cleaned, and in quotes (with "" inside) when it holds a quote.</summary>
    internal static string Field(string s)
    {
        s = Clean(s);
        return s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    }

    /// <summary>The text in Windows-1252 (each character it can't hold becomes ?, counted) or UTF-8 with no BOM.</summary>
    internal static (byte[] Bytes, int Replaced) Encode(string text, bool utf8)
    {
        if (utf8) return (new UTF8Encoding(false).GetBytes(text), 0);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var enc = Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        var o = new List<byte>(text.Length);
        int replaced = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.IsAscii) { o.Add((byte)rune.Value); continue; }
            try { o.AddRange(enc.GetBytes(rune.ToString())); }
            catch (EncoderFallbackException) { o.Add((byte)'?'); replaced++; }
        }
        return (o.ToArray(), replaced);
    }
}
