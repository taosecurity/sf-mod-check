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
        "  A line's voice types come from its conditions (GetIsID, GetIsVoiceType), else from its speaker (the line's\n" +
        "  Speaker, or the NPC that fills its alias or scene alias). Player lines and SharedDialog lines are left out.\n" +
        "  <plugin>        the plugin file; its masters are found in its folder, then each --masters-dir, then --data\n" +
        "  --out           write the list to this file (default: the screen, stdout)\n" +
        "  --voice         a voice type (EditorID) for the lines that name none; may be given more than once\n" +
        "  --utf8          write UTF-8 (default: Windows-1252, what the Creation Kit writes and Voice Cloner reads)\n" +
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
        var input = new LoadOrderInput(allowPlugin: true);
        for (int i = 0; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "--data": case "--masters-dir":
                    if (input.TryOption(a, ref i, Usage) == false) return 2;
                    continue;
                case "--out":
                    if (Cli.Value(a, ref i, "a file to write the list to", Usage) is not { } o) return 2;
                    outFile = o; break;
                case "--voice":
                    if (Cli.Value(a, ref i, "a voice type's EditorID", Usage) is not { } v) return 2;
                    if (v.Trim().Length == 0) { Console.Error.WriteLine("--voice needs a voice type's EditorID, not an empty name."); return 2; }
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
        input.TryOption(["--plugin", plugin], ref zero, Usage);
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
                Console.Error.WriteLine($"NOTE: {noVoice.Count} voiced line{(noVoice.Count == 1 ? " has" : "s have")} no voice type (first: {noVoice[0]}): no condition names " +
                                        "an NPC or voice type and no speaker or alias gives one, so they have no rows. Give their voice type with --voice <EditorID>.");
            if (replaced > 0)
                Console.Error.WriteLine($"NOTE: {replaced} character{(replaced == 1 ? "" : "s")} can't be written in Windows-1252 and became ?. Use --utf8 if the voice tool reads UTF-8.");

            if (outFile != null)
            {
                try
                {
                    var full = Path.GetFullPath(outFile);
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
        (string? Name, string Sex) Vt(FormKey k)
        {
            if (vtInfo.TryGetValue(k, out var x)) return x;
            IVoiceTypeGetter? v = null;
            try { cache.TryResolve(k, out v); } catch (Exception e) when (Fatal.Recoverable(e)) { v = null; }
            x = v == null ? (index.EditorId(k, typeof(IVoiceTypeGetter))?.ToLowerInvariant(), "")
                          : (string.IsNullOrEmpty(v.EditorID) ? null : v.EditorID.ToLowerInvariant(), v.Flags.HasFlag(VoiceType.Flag.Female) ? "female" : "male");
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
        void Resolve(IDialogResponsesGetter i, IDialogTopicGetter topic, SortedDictionary<string, string> found, List<string> why)
        {
            void AddVt(FormKey k, string how)
            {
                var (n, sex) = Vt(k);
                if (n == null) { why.Add($"{how}: voice type {k} has no EditorID, no folder"); return; }
                found[n] = sex;
                why.Add($"{how}: {n}");
            }
            void AddNpc(FormKey npc, string how)
            {
                if (Npc(npc) is not { } n) return;
                if (n.Voice.IsNull) { why.Add($"{how}: NPC {npc} has no voice type of its own"); return; }
                AddVt(n.Voice.FormKey, $"{how} {npc}");
            }
            // voice-missing's condition rules
            foreach (var tier in index.ConditionTiers(i, topic))
            {
                foreach (var c in tier)
                {
                    if (!DialogueLines.Positive(c)) continue;
                    if (c.Data is IGetIsIDConditionDataGetter g) AddNpc(g.FirstParameter.Link.FormKey, "GetIsID");
                    if (c.Data is IGetIsVoiceTypeConditionDataGetter gv)
                    {
                        var k = gv.VoiceTypeOrList.Link.FormKey;
                        if (IsVoiceType(k)) AddVt(k, "GetIsVoiceType");
                        else if (index.FormLists.TryGetValue(k, out var items))
                            foreach (var x in items) if (IsVoiceType(x)) AddVt(x, $"GetIsVoiceType list {k}");
                    }
                }
                if (found.Count > 0) return;
            }
            // the lines rules for a speaker
            if (!i.Speaker.IsNull) AddNpc(i.Speaker.FormKey, "speaker");
            foreach (var c in index.ConditionsOf(i, topic))
                if (DialogueLines.Positive(c) && c.Data is IGetIsAliasRefConditionDataGetter ar)
                    foreach (var npc in index.AliasNpcs(topic.Quest.FormKey, ar.FirstParameter)) AddNpc(npc, $"GetIsAliasRef {ar.FirstParameter}");
            if (index.SceneTopics.TryGetValue(topic.FormKey, out var uses))
                foreach (var u in uses)
                    foreach (var npc in index.AliasNpcs(u.Quest, u.Alias)) AddNpc(npc, $"scene {u.Scene} alias {u.Alias}");
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
            Resolve(i, topic, found, why);
            if (found.Count == 0 && linkers.TryGetValue(key, out var links))
                foreach (var lk in links)
                    if (index.Topics.TryGetValue(lk.Topic, out var lt) && !index.PlayerTopics.Contains(lt.FormKey))
                    {
                        var before = why.Count;
                        Resolve(lk.Info, lt, found, why);
                        for (int w = before; w < why.Count; w++) why[w] = $"shared by {lk.Info.FormKey}, {why[w]}";
                    }
            if (found.Count == 0 && extra.Count > 0)
                foreach (var (n, sex) in extra) { found[n] = sex; why.Add($"--voice: {n}"); }
            Log.Write($"voice-lines: {label}: {(found.Count == 0 ? "no voice type" : string.Join(", ", found.Keys))}" +
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
                            warnings.Add($"voice type {vt}, file {row.FileName}: {first.Info} r{first.Response} and {key} r{r + 1} have different texts, " +
                                         $"but one file can say only one of them; the list has {first.Info}'s. Give one of the lines its own file number.");
                        continue;
                    }
                    rows[k] = row;
                }
            }
        }
        var sorted = rows.Values.OrderBy(r => r.VoiceType, StringComparer.Ordinal).ThenBy(r => r.FileName, StringComparer.Ordinal).ToList();
        return (sorted, voiced, noVoice, warnings);
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
