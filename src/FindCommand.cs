// sf-mod-check find (0.16.0): looks up one record in a load order, read only. It prints the plugin that defines the
// record, the plugin that wins it, the other plugins with a copy, its type and name, and the winning copy's main fields.
//   sf-mod-check find <EditorID | FormKey | FormID> [--type <SIG>] [--plugins <Plugins.txt>] [--data <Data folder>]
//                     [--extra-data <folder>] [--json] [--quiet] [--verbose] [--log <file>]
// Exit 0 = found, 1 = not found, 2 = could not run. The text output ends with one RESULT line; --json prints one object.
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using Mutagen.Bethesda.Strings;
using SfModCheck.Conflict;

namespace SfModCheck;

public static class FindCommand
{
    public const string Usage =
        "usage: sf-mod-check find <EditorID | FormKey | FormID> [--type <SIG>] [--plugins <Plugins.txt> | --mo2 <MO2 folder> [--profile <name>]]\n" +
        "                         [--data <Data folder>] [--extra-data <folder>] [--json] [--quiet] [--verbose] [--log <file>]\n" +
        "  EditorID  exact, letter case ignored: CityNewAtlantisLocation\n" +
        "  FormKey   <6 hex digits>:<plugin>: 01295A:Starfield.esm\n" +
        "  FormID    8 hex digits as the console shows them: 0001295A (the first digits pick the plugin by load order)\n" +
        "  --type    keep only records of this four-letter type, for example LCTN, MISC, NPC_\n" +
        "Exit 0 = found, 1 = not found, 2 = could not run.";

    /// <summary>The field lines printed per record at most (the rest are counted on one line).</summary>
    public const int MaxFieldLines = 40;

    enum Kind { EditorId, FormKey, FormId }

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
        ["tool"] = "sf-mod-check find", ["version"] = version, ["query"] = query, ["result"] = "cannot run", ["error"] = why,
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    static readonly Regex ConsoleId = new(@"^(0x)?[0-9A-Fa-f]{8}\z", RegexOptions.CultureInvariant);
    static readonly Regex Signature = new(@"^[A-Z0-9_]{4}\z", RegexOptions.CultureInvariant);

    /// <summary>Every four-letter record type Mutagen knows for Starfield (for checking --type).</summary>
    static readonly Lazy<HashSet<string>> KnownSigs = new(() =>
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in typeof(IStarfieldModGetter).Assembly.GetTypes())
            if (t.IsInterface && typeof(IMajorRecordGetter).IsAssignableFrom(t) && Checker.SigsOf(t) is { } s) set.UnionWith(s);
        return set;
    });

    static int RunLogged(string[] a, string version, string byline, ref string query, out bool resultPrinted, out string cantWhy)
    {
        resultPrinted = false;
        cantWhy = "bad arguments; see the message above";
        string? q = null, type = null, logFile = null;
        bool quiet = false, verbose = false, json = false;
        var input = new LoadOrderInput();
        for (int i = 0; i < a.Length; i++)
        {
            if (input.TryOption(a, ref i, Usage) is { } taken) { if (!taken) return 2; continue; }
            switch (a[i])
            {
                case "--type":
                    if (Cli.Value(a, ref i, "a four-letter record type", Usage) is not { } t) return 2;
                    type = t.ToUpperInvariant(); break;
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
                        Console.Error.WriteLine($"find looks up one record per run: got {q} and {a[i]}. Run it once for each record.");
                        return 2;
                    }
                    q = a[i].Trim();
                    break;
            }
        }
        if (string.IsNullOrEmpty(q))
        {
            Console.Error.WriteLine("find needs the record to look up: an EditorID, a FormKey (01295A:Starfield.esm) or a console FormID (0001295A).");
            Console.Error.WriteLine(Usage);
            cantWhy = "no record given";
            return 2;
        }
        query = q;
        if (type != null && (!Signature.IsMatch(type) || !KnownSigs.Value.Contains(type)))
        {
            Console.Error.WriteLine($"--type needs a Starfield record type of four letters, digits or underscores, such as LCTN, MISC, NPC_ or QUST, not \"{type}\".");
            cantWhy = $"unknown record type {type}";
            return 2;
        }
        if (!quiet && !json) Console.Error.WriteLine(byline);
        if (verbose) Log.ToStderr();
        if (logFile != null && Log.ToFile(logFile) is { } logError) { Console.Error.WriteLine(logError); cantWhy = "the log file can't be written"; return 2; }
        Log.Write($"sf-mod-check {version} find on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, .NET {Environment.Version}");
        Log.Write($"arguments: {string.Join(" ", a.Select(x => x.Contains(' ') ? $"\"{x}\"" : x))}");
        Log.Write($"working folder: {Environment.CurrentDirectory}");

        var kind = ConsoleId.IsMatch(q) ? Kind.FormId : q.Contains(':') ? Kind.FormKey : Kind.EditorId;
        FormKey key = default;
        if (kind == Kind.FormKey && !FormKey.TryFactory(q, out key))
        {
            Console.Error.WriteLine($"{SafeText.OneLine(q)} isn't a FormKey: give six hex digits, a colon and the plugin's file name, for example 01295A:Starfield.esm.");
            cantWhy = "not a FormKey";
            return 2;
        }
        if (!input.Resolve(out var why)) { cantWhy = why; return 2; }

        var sw = Stopwatch.StartNew();
        LoadOrderSetup lo;
        try { lo = input.Build(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            cantWhy = $"the load order can't be read: {e.GetType().Name}";
            Console.Error.WriteLine($"can't read the load order: {e.Message} Close any program that has a plugin open (xEdit, the Creation Kit) and try again.");
            Log.Write($"stopped: reading the load order: {e}");
            return 2;
        }
        try
        {
            Log.Write($"loaded {lo.Plugins.Count} plugins, {lo.Problems.Count} load-order problems");
            foreach (var p in lo.Problems) Log.Write($"load-order problem: {p}");
            if (!quiet && !json) Console.Error.WriteLine($"Loaded {lo.Plugins.Count} plugins ({lo.Problems.Count} load-order problems) in {sw.Elapsed.TotalSeconds:F0} s");

            var unread = new List<string>();
            string? notFoundWhy = null;
            List<Match> matches;
            if (kind == Kind.FormId)
            {
                var fid = uint.Parse(q.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? q[2..] : q, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (FromConsoleId(fid, lo.Plugins, out notFoundWhy) is { } fk) { key = fk; Log.Write($"console FormID {fid:X8} is {fk}"); }
            }
            if (kind == Kind.EditorId) matches = ByEditorId(q, type, lo.Plugins, unread);
            else if (notFoundWhy != null) matches = new();
            else
            {
                if (!lo.Plugins.Any(p => p.FileName.Equals(key.ModKey.FileName, StringComparison.OrdinalIgnoreCase)))
                    notFoundWhy = $"{key.ModKey.FileName} isn't in this load order";
                matches = Copies(new HashSet<FormKey> { key }, new(), lo.Plugins, unread)
                    .Where(m => type == null || m.Type == type).ToList();
            }
            foreach (var u in unread) Console.Error.WriteLine($"note: {u}");

            var source = lo.OrderSource;
            if (matches.Count == 0)
            {
                var what = kind switch
                {
                    Kind.EditorId => $"no record with EditorID {SafeText.OneLine(q)}",
                    Kind.FormKey => $"no record {SafeText.OneLine(q)}",
                    _ => $"no record with FormID {SafeText.OneLine(q)}",
                } + (type != null ? $" of type {type}" : "") + $" in {lo.Plugins.Count} plugins" + (notFoundWhy != null ? $": {notFoundWhy}" : "");
                var hint = (kind == Kind.EditorId ? "EditorIDs must match exactly (letter case doesn't matter). " : "") +
                           $"The load order searched was {source}" +
                           (lo.Problems.Count > 0 ? $", with {lo.Problems.Count} load-order problems (plugins that didn't load are not searched; --verbose lists them)" : "") + ". " +
                           "If the mod isn't in that load order, give its Plugins.txt with --plugins (with Mod Organizer 2: --mo2 <MO2 folder>), or name the folder that holds the plugin with --extra-data." +
                           (kind == Kind.FormId ? " A console FormID depends on the load order; the FormKey form (01295A:Starfield.esm) doesn't." : "");
                resultPrinted = true;
                if (json) Console.WriteLine(Json(version, q, type, source, lo.Plugins.Count, matches, "not found", hint));
                else
                {
                    Console.WriteLine($"HINT: {hint}");
                    Console.WriteLine($"RESULT: NOT FOUND ({what})");
                }
                Log.Write($"result: not found, exit code 1, {sw.Elapsed.TotalSeconds:F1} s");
                return 1;
            }

            var idCache = lo.Plugins.Select(p => (IStarfieldModGetter)p.Mod).ToImmutableLinkCache<IStarfieldMod, IStarfieldModGetter>(LinkCachePreferences.OnlyIdentifiers());
            string Link(IFormLinkGetter link) => LinkText(link, idCache);
            foreach (var m in matches) m.Fields = FieldsOf(m.Winner.Rec, Link);
            resultPrinted = true;
            if (json) Console.WriteLine(Json(version, q, type, source, lo.Plugins.Count, matches, "found", null));
            else
            {
                if (!quiet) Console.WriteLine($"sf-mod-check {version} find: {SafeText.OneLine(q)} in {lo.Plugins.Count} plugins ({source})");
                foreach (var m in matches) Print(m);
                Console.WriteLine($"RESULT: FOUND {matches.Count} record{(matches.Count == 1 ? "" : "s")} ({sw.Elapsed.TotalSeconds:F0} s)");
            }
            Log.Write($"result: found {matches.Count}, exit code 0, {sw.Elapsed.TotalSeconds:F1} s");
            return 0;
        }
        finally { foreach (var p in lo.Plugins) p.Mod.Dispose(); }
    }

    /// <summary>One record and every copy of it in the load order (in load order; the last one wins).</summary>
    sealed class Match
    {
        public required FormKey Key;
        public required List<(PluginInfo Plugin, IMajorRecordGetter Rec)> Copies;
        public (PluginInfo Plugin, IMajorRecordGetter Rec) Winner => Copies[^1];
        public string Type => Checker.SigsOf(Winner.Rec.GetType())?.FirstOrDefault() ?? Analyzer.TypeShort(Winner.Rec.GetType());
        public List<(string Name, string Value)> Fields = new();
    }

    /// <summary>The plugin a console FormID points at, by the load order's runtime index: full plugins 00 to FC, medium
    /// plugins FD and one more byte, light plugins FE and three more hex digits (from each plugin's header flags).</summary>
    static FormKey? FromConsoleId(uint fid, List<PluginInfo> plugins, out string? why)
    {
        why = null;
        var full = new List<PluginInfo>(); var medium = new List<PluginInfo>(); var light = new List<PluginInfo>();
        foreach (var p in plugins)
        {
            var style = Style.Full;
            try { style = RawHeader.Read(p.Path)?.Style ?? Style.Full; }
            catch (Exception e) when (Fatal.Recoverable(e)) { Log.Write($"console FormID: {p.FileName}'s header can't be read ({e.GetType().Name}); counted as a full plugin"); }
            (style == Style.Light ? light : style == Style.Medium ? medium : full).Add(p);
        }
        Log.Write($"console FormID: {full.Count} full, {medium.Count} medium and {light.Count} light plugins");
        var top = fid >> 24;
        (List<PluginInfo> List, int Index, uint Local, string Kind) at = top switch
        {
            0xFE => (light, (int)((fid >> 12) & 0xFFF), fid & 0xFFF, "light"),
            0xFD => (medium, (int)((fid >> 16) & 0xFF), fid & 0xFFFF, "medium"),
            0xFF => (full, -1, 0, "full"),
            _ => (full, (int)top, fid & 0xFFFFFF, "full"),
        };
        if (top == 0xFF) { why = "FormIDs that start with FF belong to the save game, not a plugin"; return null; }
        if (at.Index >= at.List.Count)
        {
            why = $"this load order has {at.List.Count} {at.Kind} plugins, so none has index {at.Index:X}";
            return null;
        }
        return new FormKey(ModKey.FromFileName(at.List[at.Index].FileName), at.Local);
    }

    /// <summary>EditorID lookup: every plugin's records are read once for the EditorID; then the plugins that may hold
    /// other copies of the records found (their own plugin and the plugins with it as a master) are read for them.</summary>
    static List<Match> ByEditorId(string q, string? type, List<PluginInfo> plugins, List<string> unread)
    {
        var hits = new Dictionary<FormKey, List<(PluginInfo, IMajorRecordGetter)>>();
        foreach (var p in plugins)
        {
            Analyzer.Where = $"reading the EditorIDs in {p.FileName}";
            try
            {
                foreach (var r in p.Mod.EnumerateMajorRecords())
                    if (string.Equals(r.EditorID, q, StringComparison.OrdinalIgnoreCase)
                        && (type == null || Checker.SigsOf(r.GetType())?.Contains(type) == true))
                    {
                        if (!hits.TryGetValue(r.FormKey, out var list)) hits[r.FormKey] = list = new();
                        list.Add((p, r));
                    }
            }
            catch (Exception e) when (Fatal.Recoverable(e)) { unread.Add($"{p.FileName} couldn't be read in full ({e.GetType().Name}: {SafeText.OneLine(e.Message.Split('\n')[0])}); its later records weren't searched"); }
        }
        Log.Write($"EditorID {q}: {hits.Count} record(s) found by EditorID");
        if (hits.Count == 0) return new();
        return Copies(hits.Keys.ToHashSet(), hits, plugins, unread);
    }

    /// <summary>Every copy of the wanted records: read from each record's own plugin and the plugins that have it as a
    /// master (an override always lists the record's plugin as a master), skipping copies already known.</summary>
    static List<Match> Copies(HashSet<FormKey> wanted, Dictionary<FormKey, List<(PluginInfo, IMajorRecordGetter)>> known,
                              List<PluginInfo> plugins, List<string> unread)
    {
        bool MayHold(PluginInfo p, FormKey k) =>
            p.FileName.Equals(k.ModKey.FileName, StringComparison.OrdinalIgnoreCase)
            || p.Masters.Contains(k.ModKey.FileName.String, StringComparer.OrdinalIgnoreCase);
        bool Known(PluginInfo p, FormKey k) => known.TryGetValue(k, out var l) && l.Any(x => x.Item1.Index == p.Index);
        var found = wanted.ToDictionary(k => k, k => known.TryGetValue(k, out var l) ? new List<(PluginInfo, IMajorRecordGetter)>(l) : new());
        foreach (var p in plugins)
        {
            var look = wanted.Where(k => MayHold(p, k) && !Known(p, k)).ToHashSet();
            if (look.Count == 0) continue;
            Analyzer.Where = $"looking for {string.Join(", ", look)} in {p.FileName}";
            Log.Write($"reading {p.FileName} for {look.Count} record(s)");
            try
            {
                foreach (var r in p.Mod.EnumerateMajorRecords())
                    if (look.Contains(r.FormKey)) found[r.FormKey].Add((p, r));
            }
            catch (Exception e) when (Fatal.Recoverable(e)) { unread.Add($"{p.FileName} couldn't be read in full ({e.GetType().Name}: {SafeText.OneLine(e.Message.Split('\n')[0])}); its later records weren't searched"); }
        }
        return found.Where(f => f.Value.Count > 0)
            .Select(f => new Match { Key = f.Key, Copies = f.Value.OrderBy(c => c.Item1.Index).ToList() })
            .OrderBy(m => m.Copies[0].Plugin.Index).ThenBy(m => m.Key.ToString(), StringComparer.Ordinal).ToList();
    }

    static string? NameOf(IMajorRecordGetter r)
    {
        try { return (r as ITranslatedNamedGetter)?.Name?.String is { Length: > 0 } n ? SafeText.OneLine(n) : null; }
        catch (Exception e) when (Fatal.Recoverable(e)) { return null; }
    }

    static string Quote(string s) => "\"" + SafeText.OneLine(s) + "\"";

    /// <summary>A link as "EditorID [FormKey]" (the EditorID from the load order, when the link names a record type).</summary>
    static string LinkText(IFormLinkGetter link, ILinkCache ids)
    {
        // A link to any record type would make Mutagen index every record of every type: the FormKey alone then.
        if (!typeof(IStarfieldMajorRecordGetter).IsAssignableTo(link.Type))
            try
            {
                if (ids.TryResolveIdentifier(link.FormKey, link.Type, out var editorId) && !string.IsNullOrEmpty(editorId))
                    return $"{SafeText.OneLine(editorId)} [{link.FormKey}]";
            }
            catch (Exception e) when (Fatal.Recoverable(e)) { }
        return $"[{link.FormKey}]";
    }

    /// <summary>The winning copy's top-level fields as text: strings, numbers, enums and flags as they are, links as
    /// "EditorID [FormKey]", lists as their count; empty values are left out. Child records (a cell's references,
    /// a quest's topics) are records of their own and aren't listed.</summary>
    static List<(string, string)> FieldsOf(IMajorRecordGetter rec, Func<IFormLinkGetter, string> link)
    {
        var lines = new List<(string, string)>();
        foreach (var p in FieldCompare.Fields(rec.GetType()))
        {
            if (p.Name is "EditorID" or "Name") continue;   // in the heading
            string? text;
            try { text = Value(FieldCompare.Get(p, rec), link, 0); }
            catch (Exception e) when (Fatal.Recoverable(e)) { text = $"(unreadable: {e.GetType().Name})"; }
            if (text != null) lines.Add((p.Name, text));
        }
        return lines;
    }

    /// <summary>One value as text, or null when it is empty. A nested value (bounds, a model, a sound) shows its own
    /// non-empty parts one level down, cut at 200 characters.</summary>
    static string? Value(object? v, Func<IFormLinkGetter, string> link, int depth)
    {
        switch (v)
        {
            case ITranslatedStringGetter ts: return string.IsNullOrEmpty(ts.String) ? null : Quote(ts.String);
            case IAssetLinkGetter al: return string.IsNullOrEmpty(al.GivenPath) ? null : Quote(al.GivenPath);
        }
        if (FieldCompare.IsEmpty(v)) return null;
        switch (v)
        {
            case IFormLinkGetter fl: return link(fl);
            case string s: return Quote(s);
            case IEnumerable<byte> b: return $"({b.Count()} bytes)";
            case IEnumerable e: return Count(e);
        }
        var t = v!.GetType();
        if (t.IsPrimitive || t.IsEnum || v is decimal) return SafeText.OneLine(Convert.ToString(v, CultureInfo.InvariantCulture));
        if (depth >= 2 || t.Namespace?.StartsWith("Mutagen") != true) return SafeText.OneLine(v.ToString());
        var parts = new List<string>();
        foreach (var p in FieldCompare.NestedProperties(t))
        {
            string? part;
            try { part = Value(p.GetValue(v), link, depth + 1); }
            catch (Exception e) when (Fatal.Recoverable(e)) { part = $"(unreadable: {e.GetType().Name})"; }
            if (part != null) parts.Add($"{p.Name}={part}");
        }
        if (parts.Count == 0) return null;
        var text = $"{{{string.Join(", ", parts)}}}";
        return text.Length > 200 ? text[..200] + "..." : text;
    }

    static string Count(IEnumerable e)
    {
        var n = e.Cast<object?>().Count();
        return $"({n} {(n == 1 ? "entry" : "entries")})";
    }

    static void Print(Match m)
    {
        var (winner, rec) = m.Winner;
        var name = NameOf(rec);
        Console.WriteLine();
        Console.WriteLine($"{SafeText.OneLine(rec.EditorID) ?? "(no EditorID)"} [{m.Key}] {m.Type}{(name != null ? $" \"{name}\"" : "")}");
        Console.WriteLine($"  defined in: {LoadOrderSetup.Display(m.Key.ModKey.FileName)}");
        Console.WriteLine($"  winner: {LoadOrderSetup.Display(winner.FileName)}");
        var others = m.Copies.Take(m.Copies.Count - 1).Select(c => c.Plugin.FileName)
            .Where(f => !f.Equals(m.Key.ModKey.FileName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (others.Count > 0) Console.WriteLine($"  also in: {string.Join(", ", others)}");
        if (m.Fields.Count == 0) return;
        Console.WriteLine("  fields:");
        foreach (var (f, v) in m.Fields.Take(MaxFieldLines)) Console.WriteLine($"    {f}: {v}");
        if (m.Fields.Count > MaxFieldLines) Console.WriteLine($"    ... and {m.Fields.Count - MaxFieldLines} more");
    }

    static string Json(string version, string q, string? type, string source, int pluginCount, List<Match> matches, string result, string? hint)
    {
        var root = new JsonObject
        {
            ["tool"] = "sf-mod-check find",
            ["version"] = version,
            ["query"] = q,
            ["type"] = type,
            ["loadOrder"] = new JsonObject { ["source"] = source, ["plugins"] = pluginCount },
            ["matches"] = new JsonArray(matches.Select(m =>
            {
                var fields = new JsonObject();
                foreach (var (f, v) in m.Fields) fields[f] = v;
                return (JsonNode)new JsonObject
                {
                    ["formKey"] = m.Key.ToString(),
                    ["editorId"] = m.Winner.Rec.EditorID,
                    ["type"] = m.Type,
                    ["name"] = NameOf(m.Winner.Rec),
                    ["definedIn"] = m.Key.ModKey.FileName.String,
                    ["winner"] = m.Winner.Plugin.FileName,
                    ["plugins"] = new JsonArray(m.Copies.Select(c => (JsonNode)c.Plugin.FileName).ToArray()),
                    ["fields"] = fields,
                };
            }).ToArray()),
            ["result"] = result,
        };
        if (hint != null) root["hint"] = hint;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
