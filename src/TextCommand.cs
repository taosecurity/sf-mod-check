// sf-mod-check text (0.28.0): every string a player can see that one plugin adds or changes. Read only.
//   sf-mod-check text <plugin | archive> [--data <Data folder>] [--masters-dir <folder>]... [--type <SIG>,...]
//                     [--json] [--out <file>] [--quiet] [--verbose] [--log <file>]
// The input goes through --plugin's path (LoadOrderInput), as summary's does, so the masters are found the same way and
// an archive with several plugins gets one section per plugin, in load order. A player-facing string is any
// value Mutagen types as ITranslatedStringGetter, at any depth of a record (the type the game localizes); child records
// are listed as records of their own. A new record lists every string; an override lists only the strings whose text
// differs from the winning master version's (the master-closure link cache summary builds), with that text as "was";
// a master string the override lacks is listed with no text (0.28.0 step 5).
// NOTES.md "0.28.0 (Followups)", part (a), has the design.
// Exit 0 = listed (also when nothing is found), 2 = could not run. Text ends with one RESULT line; --json prints one object.
using System.Collections;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using Mutagen.Bethesda.Strings;
using SfModCheck.Conflict;

namespace SfModCheck;

public static class TextCommand
{
    public const string Usage =
        "usage: sf-mod-check text <plugin | archive> [--data <Data folder>] [--masters-dir <folder>]... [--type <SIG>,...]\n" +
        "                         [--json] [--out <file>] [--quiet] [--verbose] [--log <file>]\n" +
        "  Lists every string a player can see that a plugin adds or changes: item, book, message and terminal text, quest\n" +
        "  names and objectives, dialogue, and every other text the game translates. A new record lists all its strings;\n" +
        "  an override lists only the strings it changes, each with the master's text as \"was\" (a string it clears\n" +
        "  shows as (no text), except in a partial form or a localized plugin).\n" +
        "  <plugin | archive>  a plugin (.esm, .esp or .esl), a mod archive (.zip, .7z or .rar). An\n" +
        "               archive with several plugins gets one section per plugin, in load order. The masters are\n" +
        "               looked up in the plugin's own folder, then each --masters-dir, then the game's Data folder. A\n" +
        "               localized plugin's text is read from its Strings folder or its BA2 (English).\n" +
        "  --type       only these record types, comma-separated (--type BOOK,MESG)\n" +
        "  --json       print one JSON object instead of the text\n" +
        "  --out        write the text (or the JSON) to this file, replacing it, and print only the counts\n" +
        "               (a plugin, BA2, archive, string file, script or DLL name is refused)\n" +
        "Exit 0 = listed (also when nothing is found), 2 = could not run.";

    /// <summary>One string of a record: its path (Name, MenuButtons[2].Text), its text, and for a changed override the
    /// master's text (null when the master has no string there).</summary>
    internal sealed record Str(string Field, string Text, string? Was);

    internal const string KindNew = "new", KindChanged = "changed", KindNoMaster = "no master";
    /// <summary>How the text output shows a string an override cleared (its JSON text is "").</summary>
    internal const string NoText = "(no text)";

    /// <summary>One listed record: an own record (new), an override that changes strings (changed), or an override
    /// with no master version to compare (no master: every string listed).</summary>
    internal sealed record Rec(string Type, string FormKey, string? EditorId, string Kind, List<Str> Strings);

    internal sealed class PluginText
    {
        public required string Name;
        public bool Localized;
        public string? Warning;
        public string? MasterWarning;   // a localized master whose strings all read as empty (0.28.0 code review round 2)
        public string? FromArchive;
        public List<Rec> Records = new();
        public List<string> Unread = new();
        public int Strings => Records.Sum(r => r.Strings.Count);
        public int New => Records.Count(r => r.Kind == KindNew);
        public int Changed => Records.Count(r => r.Kind == KindChanged);
        public int NoMaster => Records.Count(r => r.Kind == KindNoMaster);
    }

    public static int Run(string[] a, string version, string byline)
    {
        bool json = a.Contains("--json");
        try
        {
            var code = RunLogged(a, version, byline, out var resultPrinted, out var cantWhy);
            if (code == 2 && !resultPrinted) CannotRun(json, version, cantWhy);
            return code;
        }
        catch (Exception e)
        {
            Console.WriteLine(Fatal.ResultLine(e, () => json
                ? CannotRunJson(version, $"{e.GetType().Name}: {SafeText.OneLine(e.Message.Split('\n')[0])}")
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

    static void CannotRun(bool json, string version, string why) =>
        Console.WriteLine(json ? CannotRunJson(version, why) : $"RESULT: CANNOT CHECK ({why})");

    static string CannotRunJson(string version, string why) => new JsonObject
    {
        ["tool"] = "sf-mod-check text", ["version"] = version, ["result"] = "cannot run", ["error"] = why,
    }.ToJsonString(Indented);

    /// <summary>File kinds --out never writes over: a plugin, an archive, a BA2, a string file, a script or a DLL (a typo
    /// there would destroy a mod; the string files and scripts since 0.28.0 code review round 1).</summary>
    static readonly HashSet<string> ModFileExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".esm", ".esp", ".esl", ".ba2", ".zip", ".7z", ".rar", ".strings", ".dlstrings", ".ilstrings", ".pex", ".psc", ".dll" };

    static int RunLogged(string[] a, string version, string byline, out bool resultPrinted, out string cantWhy)
    {
        resultPrinted = false;
        cantWhy = "bad arguments; see the message above";
        string? target = null, logFile = null, typeArg = null, outFile = null;
        bool quiet = false, verbose = false, json = false;
        using var input = new LoadOrderInput(allowPlugin: true, ArchiveInput.Use.PluginsOnly, "text", positionalPlugin: true);
        for (int i = 0; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "--data": case "--masters-dir":
                    if (input.TryOption(a, ref i, Usage) != true) return 2;
                    continue;
                case "--plugin": case "--plugins": case "--mo2": case "--profile": case "--extra-data":
                    Console.Error.WriteLine($"text doesn't take {a[i]}: give the plugin or archive itself as the first argument " +
                                            "(sf-mod-check text MyMod.esm), and say where its masters are with --masters-dir or --data.");
                    cantWhy = $"text doesn't take {a[i]}";
                    return 2;
                case "--type":
                    if (Cli.Value(a, ref i, "record types, comma-separated (BOOK,MESG)", Usage) is not { } t) return 2;
                    typeArg = t; break;
                case "--out":
                    if (Cli.Value(a, ref i, "a file name", Usage) is not { } of) return 2;
                    outFile = of; break;
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
                    if (target != null)
                    {
                        Console.Error.WriteLine($"text takes one plugin or archive: got {SafeText.OneLine(target)} and {SafeText.OneLine(a[i])}. Run it once for each.");
                        cantWhy = "more than one plugin or archive given";
                        return 2;
                    }
                    target = a[i];
                    break;
            }
        }
        if (target == null)
        {
            Console.Error.WriteLine("text needs a plugin or a mod archive: sf-mod-check text MyMod.esm");
            Console.Error.WriteLine(Usage);
            cantWhy = "no plugin or archive given";
            return 2;
        }
        HashSet<string>? types = null;
        if (typeArg != null)
        {
            types = new(StringComparer.Ordinal);
            foreach (var part in typeArg.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var t = part.ToUpperInvariant();
                if (!FindCommand.Signature.IsMatch(t) || !FindCommand.KnownSigs.Value.Contains(t))
                {
                    Console.Error.WriteLine($"--type: \"{SafeText.OneLine(part)}\" isn't a Starfield record type. Known types: {string.Join(", ", FindCommand.KnownSigs.Value.Order(StringComparer.Ordinal))}.");
                    cantWhy = $"unknown record type {SafeText.OneLine(t)}";
                    return 2;
                }
                types.Add(t);
            }
            if (types.Count == 0)
            {
                Console.Error.WriteLine("--type needs at least one record type, for example --type BOOK,MESG.");
                cantWhy = "--type with no record type";
                return 2;
            }
        }
        if (outFile != null)
        {
            if (ModFileExtensions.Contains(Path.GetExtension(outFile)))
            {
                Console.Error.WriteLine($"--out writes a text or JSON file, not a {Path.GetExtension(outFile)} file: give it a name such as text.txt or text.json.");
                cantWhy = "--out names a mod file";
                return 2;
            }
            if (Directory.Exists(outFile))
            {
                Console.Error.WriteLine($"--out {SafeText.OneLine(outFile)} is a folder: give a file name, such as {SafeText.OneLine(Path.Combine(outFile, "text.txt"))}.");
                cantWhy = "--out is a folder";
                return 2;
            }
        }
        var pluginArg = new[] { "--plugin", target };
        int at = 0;
        if (input.TryOption(pluginArg, ref at, Usage) != true) return 2;

        if (!quiet && !json) Console.Error.WriteLine(byline);
        if (verbose) Log.ToStderr();
        if (logFile != null && Log.ToFile(logFile) is { } logError) { Console.Error.WriteLine(logError); cantWhy = "the log file can't be written"; return 2; }
        Log.Write($"sf-mod-check {version} text on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, .NET {Environment.Version}");
        Log.Write($"arguments: {string.Join(" ", a.Select(x => x.Contains(' ') ? $"\"{x}\"" : x))}");
        Log.Write($"working folder: {Environment.CurrentDirectory}");
        if (!input.Resolve(out var why)) { cantWhy = why; return 2; }

        var sw = Stopwatch.StartNew();
        LoadOrderSetup lo;
        try { lo = input.Build(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            cantWhy = $"the plugin and its masters can't be read: {e.GetType().Name}";
            Console.Error.WriteLine($"can't read the plugin and its masters: {e.Message} Check that this account can read them, close any program that has a plugin open (xEdit, the Creation Kit), and try again.");
            Log.Write($"stopped: reading the load order: {e}");
            return 2;
        }
        try
        {
            Log.Write($"loaded {lo.Plugins.Count} plugins, {lo.Problems.Count} load-order problems");
            foreach (var p in lo.Problems) Log.Write($"load-order problem: {p}");
            var wanted = input.ArchivePlugins.Count > 0
                ? new HashSet<string>(input.ArchivePlugins, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>([Path.GetFileName(input.Plugin!)], StringComparer.OrdinalIgnoreCase);
            var targets = lo.Plugins.Where(p => wanted.Contains(p.FileName)).ToList();
            if (targets.Count == 0)
            {
                cantWhy = "the plugin can't be opened";
                Console.Error.WriteLine($"can't open {string.Join(", ", wanted.Select(SafeText.OneLine))}: {string.Join("; ", lo.Problems.Select(SafeText.OneLine))}");
                Log.Write("stopped: no wanted plugin opened");
                return 2;
            }
            var archiveName = input.PluginArchive is { } arc ? Path.GetFileName(arc) : null;
            var all = new List<PluginText>();
            foreach (var t in targets)
            {
                Analyzer.Where = $"listing the text of {t.FileName}";
                var psw = Stopwatch.StartNew();
                var pt = Collect(t, lo, types);
                if (archiveName != null) pt.FromArchive = archiveName;
                all.Add(pt);
                Log.Write($"text: {t.FileName}: {pt.Strings} strings in {pt.Records.Count} records ({pt.New} new, {pt.Changed} changed, {pt.NoMaster} with no master version), {psw.Elapsed.TotalSeconds:F1} s");
            }
            foreach (var p in lo.Problems) if (!quiet && !json) Console.Error.WriteLine($"note: load-order problem: {SafeText.OneLine(p)}");
            var result = ResultText(all);
            string body = json ? Json(version, all, lo.Problems) : TextBody(version, lo.OrderSource, all, quiet && outFile == null);
            if (outFile != null)
            {
                try { File.WriteAllText(outFile, body, new UTF8Encoding(false)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    Console.Error.WriteLine($"can't write {SafeText.OneLine(outFile)}: {e.Message}");
                    cantWhy = "the --out file can't be written";
                    return 2;
                }
                resultPrinted = true;
                Console.WriteLine($"Wrote {SafeText.OneLine(Path.GetFullPath(outFile))}: {Plural(all.Sum(p => p.Strings), "string", "strings")} in {Plural(all.Sum(p => p.Records.Count), "record", "records")}.");
                Console.WriteLine("RESULT: " + result);
            }
            else
            {
                resultPrinted = true;
                if (json) Console.WriteLine(body);
                else
                {
                    Console.Write(body);
                    Console.WriteLine("RESULT: " + result);
                }
            }
            Log.Write($"result: {result}, exit code 0, {sw.Elapsed.TotalSeconds:F1} s");
            return 0;
        }
        finally { foreach (var p in lo.Plugins) p.Mod.Dispose(); }
    }

    static string Plural(int n, string one, string many) => n == 1 ? $"{n} {one}" : $"{n} {many}";

    internal static string ResultText(List<PluginText> all)
    {
        int strings = all.Sum(p => p.Strings), records = all.Sum(p => p.Records.Count);
        int added = all.Sum(p => p.New), changed = all.Sum(p => p.Changed), noMaster = all.Sum(p => p.NoMaster);
        var r = $"text: {Plural(strings, "string", "strings")} in {Plural(records, "record", "records")} ({added} new, {changed} changed" +
                (noMaster > 0 ? $", {noMaster} with no master version" : "") + $") in {string.Join(", ", all.Select(p => SafeText.OneLine(p.Name)))}";
        foreach (var p in all.Where(p => p.Warning != null)) r += $"; WARNING: {SafeText.OneLine(p.Name)}'s string files weren't found";
        foreach (var p in all.Where(p => p.MasterWarning != null)) r += $"; WARNING: the string files of {SafeText.OneLine(p.Name)}'s masters weren't found";
        return r;
    }

    static bool IsLocalized(PluginInfo p) => (Convert.ToInt64(p.Mod.ModHeader.Flags) & Flags.Localized) != 0;

    static string SigOf(IMajorRecordGetter r) =>
        Checker.SigsOf(r.GetType())?.Order(StringComparer.Ordinal).FirstOrDefault() ?? Analyzer.TypeShort(r.GetType());

    static string? EditorIdOf(IMajorRecordGetter r)
    {
        try { return r.EditorID; }
        catch (Exception e) when (Fatal.Recoverable(e)) { return null; }
    }

    /// <summary>One plugin's strings: its new records' and the changes of its overrides, against the winning version
    /// among its masters (the master closure's link cache, as summary's ITM check).</summary>
    internal static PluginText Collect(PluginInfo t, LoadOrderSetup lo, HashSet<string>? types)
    {
        var pt = new PluginText { Name = t.FileName, Localized = IsLocalized(t) };
        var self = t.Mod.ModKey;
        var seen = new HashSet<FormKey>();
        var news = new List<IMajorRecordGetter>();
        var overrides = new List<IMajorRecordGetter>();
        var reported = new HashSet<string>(StringComparer.Ordinal);
        int walked = 0;
        RecordWalk.Each(t, rec =>
        {
            walked++;
            if (!seen.Add(rec.FormKey)) return;
            if (types != null && !types.Contains(SigOf(rec))) return;
            if (rec.IsDeleted) return;   // a deleted record shows no text
            (rec.FormKey.ModKey == self ? news : overrides).Add(rec);
        }, (what, err) =>
        {
            while (err is System.Reflection.TargetInvocationException { InnerException: { } inner }) err = inner;
            if (reported.Add(what)) pt.Unread.Add($"its {what} records could not be read, so their text is not listed ({SfModCheck.Conflict.Impact.Summary(err)})");
        });

        // the strings seen, empty ones too: a localized plugin whose every string reads as empty lacks its string files
        int seenStrings = 0, nonEmpty = 0;
        var listed = new Dictionary<FormKey, Rec>();
        foreach (var r in news)
        {
            var strs = Strings(r, ref seenStrings, ref nonEmpty);
            if (strs.Count > 0) listed[r.FormKey] = new Rec(SigOf(r), r.FormKey.ToString(), EditorIdOf(r), KindNew, strs);
        }

        var closure = MasterClosure(t, lo);
        var cache = lo.Plugins.Where(p => closure.Contains(p.FileName)).Select(p => (IStarfieldModGetter)p.Mod).ToImmutableLinkCache();
        var paired = new HashSet<FormKey>();
        int pairs = 0;
        // the strings of master versions from a localized master: if every one reads as empty, its string files are missing
        var localizedMasters = lo.Plugins.Where(p => closure.Contains(p.FileName) && IsLocalized(p)).Select(p => p.Mod.ModKey).ToHashSet();
        int masterSeen = 0, masterNonEmpty = 0;
        // A partial-form master version (ShatteredSpace.esm's EDID-only quests) holds only some fields, and the game reads
        // the rest from the version under it (as Rules.FullQuest for check): each top-level field's strings come from the
        // newest version that holds that field, down to the newest full one (0.28.0 code review round 3).
        List<Str> MasterStrings(IMajorRecordGetter master, ref int s, ref int n)
        {
            var result = Strings(master, ref s, ref n);
            if (!IsPartialForm(master)) return result;
            var versions = master is ICellGetter
                ? cache.ResolveAll<ICellGetter>(master.FormKey).Cast<IMajorRecordGetter>()
                : cache.ResolveAll(master.FormKey, master.Registration.GetterType);
            var held = result.Select(x => TopField(x.Field)).ToHashSet(StringComparer.Ordinal);
            foreach (var v in versions.Skip(1))   // the first is the winning version, master itself
            {
                var add = Strings(v, ref s, ref n).Where(x => !held.Contains(TopField(x.Field))).ToList();
                result.AddRange(add);
                held.UnionWith(add.Select(x => TopField(x.Field)));
                if (!IsPartialForm(v)) break;
            }
            Log.Write($"text: {master.FormKey}: the winning master version is a partial form, so its left-out strings are read from the versions under it");
            return result;
        }
        void Compared(IMajorRecordGetter ov, IMajorRecordGetter master)
        {
            if (!paired.Add(ov.FormKey)) return;
            pairs++;
            int mSeen = 0, mNonEmpty = 0;
            var was = MasterStrings(master, ref mSeen, ref mNonEmpty);
            if (localizedMasters.Contains(master.FormKey.ModKey)) { masterSeen += mSeen; masterNonEmpty += mNonEmpty; }
            // a string the master has and the override doesn't (cleared, or its field or list entry left out): the player
            // loses that text, so it is listed with no text and the master's as "was" (0.28.0 step 5). Not for a partial
            // form, whose left-out fields come from the master in the game, and not for a localized plugin, whose string
            // that can't be looked up reads as empty too (0.28.0 code review round 1).
            bool partial = IsPartialForm(ov);
            if (partial) Log.Write($"text: {ov.FormKey}: a partial form, so the master strings it leaves out are not listed as cleared");
            var changed = Changes(was, Strings(ov, ref seenStrings, ref nonEmpty), cleared: !partial && !pt.Localized);
            if (changed.Count > 0) listed[ov.FormKey] = new Rec(SigOf(ov), ov.FormKey.ToString(), EditorIdOf(ov) ?? EditorIdOf(master), KindChanged, changed);
        }
        try
        {
            var (_, notCompared) = Overrides.FindItms(t.Mod, overrides, cache, Compared);
            if (notCompared > 0)
                pt.Unread.Add($"{Plural(notCompared, "override", "overrides")} couldn't be compared with the master version, so their strings are listed in full as \"{KindNoMaster}\"");
        }
        catch (Exception e) when (Fatal.Recoverable(e))
        {
            Log.Write($"text: {t.FileName}: the compare with the masters stopped: {e}");
            pt.Unread.Add($"the overrides could not all be compared with their masters ({e.GetType().Name}: {SafeText.OneLine(e.Message.Split('\n')[0])})");
        }
        // an override with no master version of the same type (or one that couldn't be compared): every string listed
        foreach (var ov in overrides)
        {
            if (paired.Contains(ov.FormKey)) continue;
            var strs = Strings(ov, ref seenStrings, ref nonEmpty);
            if (strs.Count > 0) listed[ov.FormKey] = new Rec(SigOf(ov), ov.FormKey.ToString(), EditorIdOf(ov), KindNoMaster, strs);
        }
        // in signature order, each type's records in the plugin's own order
        var order = news.Concat(overrides).Select((r, i) => (r.FormKey, i)).ToDictionary(x => x.FormKey, x => x.i);
        pt.Records = listed.OrderBy(kv => kv.Value.Type, StringComparer.Ordinal).ThenBy(kv => order[kv.Key]).Select(kv => kv.Value).ToList();
        if (pt.Localized && seenStrings > 0 && nonEmpty == 0)
            pt.Warning = $"{t.FileName} is localized but every one of its {Plural(seenStrings, "string", "strings")} reads as empty: its string files weren't found. " +
                         $"They belong in Strings\\{Path.GetFileNameWithoutExtension(t.FileName)}_en.STRINGS (and .DLSTRINGS, .ILSTRINGS) next to the plugin, or in its Main BA2.";
        if (masterSeen > 0 && masterNonEmpty == 0)
        {
            var names = lo.Plugins.Where(p => localizedMasters.Contains(p.Mod.ModKey)).Select(p => p.FileName).ToList();
            pt.MasterWarning = $"every one of the {Plural(masterSeen, "string", "strings")} of the master versions it overrides reads as empty, so the masters' text is not shown as \"was\": " +
                               $"the string files of {string.Join(", ", names)} weren't found. They belong in a Strings folder next to each master, or in its Localization BA2.";
        }
        Log.Write($"text: {t.FileName}: {walked} records walked, {news.Count} new and {overrides.Count} overrides kept, {pairs} compared with a master version, " +
                  $"{seenStrings} strings seen ({nonEmpty} not empty)");
        return pt;
    }

    static readonly System.Text.RegularExpressions.Regex ListIndex = new(@"\[\d+\]", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>A string path's record field: Stages[0].LogEntries[1].Entry is Stages.</summary>
    static string TopField(string field) => field.IndexOfAny(['.', '[']) is var i and >= 0 ? field[..i] : field;

    /// <summary>The strings an override changes against its master's (0.28.0 code review round 1). Strings are grouped
    /// by their path with the list indexes taken out (MenuButtons[].Text), and in a group a text the master has too is
    /// unchanged wherever it sits, so an entry added or left out moves no other. The rest are paired, the same path
    /// first: an override string paired with a master one is changed (the master's text as was), one left over has no
    /// text in the master, and a master string left over is cleared (listed with no text) when cleared is true.</summary>
    internal static List<Str> Changes(List<Str> master, List<Str> ov, bool cleared)
    {
        static string Key(string field) => ListIndex.Replace(field, "[]");
        var left = master.GroupBy(s => Key(s.Field), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        // the same path and text first for every override string, then the same text anywhere in its group, so a string
        // that sits at its master entry's place is never paired elsewhere by an earlier one (0.28.0 code review round 2)
        var rest = new List<Str>();
        foreach (var s in ov)
        {
            if (left.TryGetValue(Key(s.Field), out var l) && l.FindIndex(m => m.Field == s.Field && m.Text == s.Text) is var i and >= 0) l.RemoveAt(i);
            else rest.Add(s);
        }
        var unmatched = new List<Str>();
        foreach (var s in rest)
        {
            if (left.TryGetValue(Key(s.Field), out var l) && l.FindIndex(m => m.Text == s.Text) is var i and >= 0) l.RemoveAt(i);
            else unmatched.Add(s);
        }
        var changes = new List<Str>();
        foreach (var s in unmatched)
        {
            string? old = null;
            if (left.TryGetValue(Key(s.Field), out var l) && l.Count > 0)
            {
                int i = Math.Max(0, l.FindIndex(m => m.Field == s.Field));
                old = l[i].Text;
                l.RemoveAt(i);
            }
            changes.Add(s with { Was = old });
        }
        if (cleared)
            foreach (var m in left.Values.SelectMany(l => l)) changes.Add(new Str(m.Field, "", m.Text));
        return changes;
    }

    /// <summary>The Starfield record types Mutagen 0.54.4 reads as partial forms (`IsPartialFormable => true` in its
    /// Quest, Cell and DialogTopic registrations); the flag is FieldCompare.PartialFormFlag.</summary>
    static readonly HashSet<string> PartialFormable = new(StringComparer.Ordinal) { "QUST", "CELL", "DIAL" };

    /// <summary>Whether an override is a partial form: the fields it leaves out are the master's in the game.</summary>
    internal static bool IsPartialForm(IMajorRecordGetter r) => FieldCompare.IsPartial(r) && PartialFormable.Contains(SigOf(r));

    /// <summary>The master names of p and of their masters, from the load order (as summary's).</summary>
    static HashSet<string> MasterClosure(PluginInfo p, LoadOrderSetup lo)
    {
        var byName = lo.Plugins.ToDictionary(x => x.FileName, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(string n)
        {
            if (!seen.Add(n)) return;
            if (byName.TryGetValue(n, out var m)) foreach (var mm in m.Masters) Visit(mm);
        }
        foreach (var m in p.Masters) Visit(m);
        return seen;
    }

    /// <summary>A record's non-empty translated strings by path, in field order. Child records are left out (each is
    /// a record of its own), as are padding members; the walk stops at FindTree's depth cap. seen counts every string
    /// value met (empty ones too), nonEmpty the ones with text.</summary>
    internal static List<Str> Strings(IMajorRecordGetter rec, ref int seen, ref int nonEmpty)
    {
        var l = new List<Str>();
        var t = rec.GetType();
        int s = 0, n = 0;
        foreach (var p in FieldCompare.Fields(t))
        {
            if (DeepCompare.IsPadding(t, p.Name)) continue;
            object? v;
            try { v = FieldCompare.Get(p, rec); }
            catch (Exception e) when (Fatal.Recoverable(e)) { Log.Write($"text: {rec.FormKey}: {p.Name} can't be read ({e.GetType().Name})"); continue; }
            Walk(p.Name, v, 0, l, ref s, ref n, rec.FormKey);
        }
        seen += s;
        nonEmpty += n;
        return l;
    }

    static void Walk(string path, object? v, int depth, List<Str> into, ref int seen, ref int nonEmpty, FormKey owner)
    {
        switch (v)
        {
            case null: return;
            case ITranslatedStringGetter ts:
            {
                seen++;
                string? text;
                try { text = ts.String; }
                catch (Exception x) when (Fatal.Recoverable(x)) { Log.Write($"text: {owner}: {path} can't be read ({x.GetType().Name})"); return; }
                if (string.IsNullOrEmpty(text)) return;
                nonEmpty++;
                into.Add(new Str(path, text, null));
                return;
            }
            case string or IFormLinkGetter or IAssetLinkGetter or IMajorRecordGetter: return;
        }
        var t = v.GetType();
        if (t.IsValueType || t.Namespace?.StartsWith("Mutagen") != true && v is not IEnumerable) return;
        if (depth >= FindTree.MaxDepth) return;
        if (v is IEnumerable e)
        {
            if (v is IEnumerable<byte>) return;
            List<object?> items;
            try { items = e.Cast<object?>().ToList(); }
            catch (Exception x) when (Fatal.Recoverable(x)) { Log.Write($"text: {owner}: {path} can't be read ({x.GetType().Name})"); return; }
            for (int i = 0; i < items.Count; i++) Walk($"{path}[{i}]", items[i], depth + 1, into, ref seen, ref nonEmpty, owner);
            return;
        }
        foreach (var p in FieldCompare.NestedProperties(t))
        {
            if (DeepCompare.IsPadding(t, p.Name)) continue;
            object? pv;
            try { pv = p.GetValue(v); }
            catch (Exception x) when (Fatal.Recoverable(x)) { Log.Write($"text: {owner}: {path}.{p.Name} can't be read ({x.GetType().Name})"); continue; }
            Walk($"{path}.{p.Name}", pv, depth + 1, into, ref seen, ref nonEmpty, owner);
        }
    }

    /// <summary>A string's lines, each made safe to print (control characters made visible).</summary>
    static string[] SafeLines(string s) =>
        s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(x => SafeText.OneLine(x)).ToArray();

    static void PutString(StringBuilder sb, string indent, string label, string text)
    {
        var lines = SafeLines(text);
        sb.Append(indent).Append(label).Append(": ").Append(lines[0]).Append('\n');
        foreach (var more in lines.Skip(1)) sb.Append(indent).Append("  ").Append(more).Append('\n');
    }

    internal static string TextBody(string version, string orderSource, List<PluginText> all, bool quiet)
    {
        var sb = new StringBuilder();
        if (!quiet) sb.Append($"sf-mod-check {version} text of {SafeText.OneLine(orderSource)}\n");
        foreach (var p in all)
        {
            sb.Append('\n');
            sb.Append($"{SafeText.OneLine(p.Name)} ({(p.Localized ? "localized, " : "")}{Plural(p.Strings, "string", "strings")} in {Plural(p.Records.Count, "record", "records")}" +
                      $"{(p.FromArchive != null ? $", from {SafeText.OneLine(p.FromArchive)}" : "")})\n");
            if (p.Warning != null) sb.Append($"  WARNING: {SafeText.OneLine(p.Warning)}\n");
            if (p.MasterWarning != null) sb.Append($"  WARNING: {SafeText.OneLine(p.MasterWarning)}\n");
            foreach (var g in p.Records.GroupBy(r => r.Type))
            {
                sb.Append($"  {g.Key} ({Plural(g.Count(), "record", "records")}, {Plural(g.Sum(r => r.Strings.Count), "string", "strings")})\n");
                foreach (var r in g)
                {
                    sb.Append($"    {r.Type}{(r.EditorId is { Length: > 0 } e ? " " + SafeText.OneLine(e) : "")} [{SafeText.OneLine(r.FormKey)}] {r.Kind}\n");
                    foreach (var s in r.Strings)
                    {
                        PutString(sb, "      ", SafeText.OneLine(s.Field), s.Text.Length == 0 ? NoText : s.Text);
                        if (r.Kind == KindChanged) PutString(sb, "        ", "was", s.Was ?? "(no text in the master)");
                    }
                }
            }
            foreach (var u in p.Unread) sb.Append($"  not read: {SafeText.OneLine(u)}\n");
        }
        return sb.ToString();
    }

    internal static string Json(string version, List<PluginText> all, IReadOnlyList<string> problems)
    {
        JsonNode Plugin(PluginText p)
        {
            var o = new JsonObject
            {
                ["name"] = p.Name,
                ["localized"] = p.Localized,
            };
            if (p.Warning != null) o["warning"] = p.Warning;
            if (p.MasterWarning != null) o["masterWarning"] = p.MasterWarning;
            if (p.FromArchive != null) o["fromArchive"] = p.FromArchive;
            o["records"] = new JsonArray(p.Records.Select(r => (JsonNode)new JsonObject
            {
                ["type"] = r.Type, ["formKey"] = r.FormKey, ["editorId"] = r.EditorId, ["kind"] = r.Kind,
                ["strings"] = new JsonArray(r.Strings.Select(s =>
                {
                    var so = new JsonObject { ["field"] = s.Field, ["text"] = s.Text };
                    if (r.Kind == KindChanged) so["was"] = s.Was;
                    return (JsonNode)so;
                }).ToArray()),
            }).ToArray());
            o["unread"] = new JsonArray(p.Unread.Select(u => (JsonNode)u).ToArray());
            return o;
        }
        var root = new JsonObject
        {
            ["tool"] = "sf-mod-check text",
            ["version"] = version,
            ["result"] = "done",
            ["plugins"] = new JsonArray(all.Select(Plugin).ToArray()),
            ["loadOrderProblems"] = new JsonArray(problems.Select(p => (JsonNode)p).ToArray()),
        };
        return root.ToJsonString(Indented);
    }
}
