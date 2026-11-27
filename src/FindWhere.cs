// sf-mod-check find --where and --editorid-match (0.27.0): list every record of a type whose winning copy matches simple
// field filters, and every record whose EditorID matches a regular expression. Design: NOTES.md "0.27.0", part B.
//   --where "<field> <op> [<value>]"   op: =, !=, ~ (contains), has (a flag, or any list entry), exists (not empty)
//   --editorid-match <regex>           .NET regex, letter case ignored, 2-second match timeout
// Several --where filters are ANDed. Field names are checked against Mutagen's class for the record type before any
// plugin is read, so a typo is refused at once with that type's field list. A dotted path (Factions.Faction) walks one
// level down per part; a list on the way matches when any entry matches. Only the winning copy is tested (a partial
// form's absent field falls back to the earlier copy, as find's fields block does).
// Exit 0 = at least one match, 1 = no record matched, 2 = could not run.
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Plugins.Records.Mapping;
using Mutagen.Bethesda.Starfield;
using Mutagen.Bethesda.Strings;
using SfModCheck.Conflict;

namespace SfModCheck;

internal static class FindWhere
{
    internal enum Op { Eq, Ne, Contains, Has, Exists }

    /// <summary>One --where filter. Path is the field and its dotted parts; Flag is the flag a "has" on a flags field
    /// tests (set by Check); ConsoleKey is a console FormID value turned into a FormKey once the load order is known.</summary>
    internal sealed class Filter
    {
        public required string Text;
        public required string[] Path;
        public required Op Op;
        public required string OpText;
        public string? Value;
        public Enum? Flag;
        public FormKey? ConsoleKey;
        public string Field => string.Join(".", Path);
    }

    internal const string Grammar = "--where needs \"<field> <op> <value>\", where <op> is =, !=, ~, has or exists, for example --where \"Flags has Unique\"";

    static readonly Regex FieldWord = new(@"^[A-Za-z0-9_]+(\.[A-Za-z0-9_]+)*\z", RegexOptions.CultureInvariant);

    /// <summary>Parses one --where value: the field (letters, digits, _ and dots), the operator, then the rest as the
    /// value (trimmed, one pair of surrounding double quotes removed). exists takes no value; the others need one.</summary>
    internal static Filter? Parse(string s, out string? why)
    {
        why = null;
        var t = s.Trim();
        var sp = t.IndexOfAny([' ', '\t']);
        var field = sp < 0 ? t : t[..sp];
        var rest = sp < 0 ? "" : t[sp..].TrimStart();
        var sp2 = rest.IndexOfAny([' ', '\t']);
        var opText = sp2 < 0 ? rest : rest[..sp2];
        var value = sp2 < 0 ? "" : rest[sp2..].Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
        Op? op = opText.ToLowerInvariant() switch
        {
            "=" => Op.Eq, "!=" => Op.Ne, "~" => Op.Contains, "has" => Op.Has, "exists" => Op.Exists, _ => null,
        };
        if (field.Length == 0 || !FieldWord.IsMatch(field) || op == null)
        {
            why = Grammar;
            return null;
        }
        if (op == Op.Exists && value.Length > 0) { why = $"{Grammar} (exists takes no value)"; return null; }
        if (op != Op.Exists && value.Length == 0) { why = $"{Grammar} ({opText} needs a value)"; return null; }
        return new Filter { Text = t, Path = field.Split('.'), Op = op.Value, OpText = opText.ToLowerInvariant(), Value = op == Op.Exists ? null : value };
    }

    /// <summary>Each record type's Mutagen class (with setters, the names the binary overlay shares) and getter
    /// interface, by signature: the getter interface that carries the signature itself, and the class of the same name.</summary>
    internal static readonly Lazy<Dictionary<string, (Type Class, Type Getter)>> Types = new(() =>
    {
        var map = new Dictionary<string, (Type, Type)>(StringComparer.Ordinal);
        var asm = typeof(IStarfieldModGetter).Assembly;
        foreach (var c in asm.GetTypes())
        {
            if (!c.IsClass || c.IsAbstract || c.Namespace != "Mutagen.Bethesda.Starfield" || !typeof(IStarfieldMajorRecord).IsAssignableFrom(c)) continue;
            var g = asm.GetType($"Mutagen.Bethesda.Starfield.I{c.Name}Getter");
            if (g == null || !g.IsInterface) continue;
            foreach (var a in g.GetCustomAttributes<AssociatedRecordTypesAttribute>(false))
                foreach (var rt in a.Types) map.TryAdd(rt.Type, (c, g));
        }
        return map;
    });

    /// <summary>The members a path part may name on a declared type: its nested properties without padding, and for an
    /// abstract type those of every class in the same assembly built on it (a level list's entries, an NPC's Level).</summary>
    static List<PropertyInfo> Members(Type t)
    {
        var list = FieldCompare.NestedProperties(t).Where(p => !DeepCompare.IsPadding(t, p.Name)).ToList();
        if (t.IsAbstract || t.IsInterface)
            foreach (var sub in t.Assembly.GetTypes().Where(x => x.IsClass && !x.IsAbstract && t.IsAssignableFrom(x) && !x.Name.Contains("BinaryOverlay")))
                foreach (var p in FieldCompare.NestedProperties(sub))
                    if (!DeepCompare.IsPadding(sub, p.Name) && !list.Any(q => q.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase))) list.Add(p);
        return list;
    }

    static Type Unwrap(Type t) => Nullable.GetUnderlyingType(t) ?? t;

    /// <summary>A declared type that is a list (not text, a link, an asset path or bytes), and its entry type.</summary>
    static bool ListType(Type t, out Type entry)
    {
        entry = t;
        if (t == typeof(string) || typeof(ITranslatedStringGetter).IsAssignableFrom(t) || typeof(IFormLinkGetter).IsAssignableFrom(t)
            || typeof(IAssetLinkGetter).IsAssignableFrom(t) || !typeof(IEnumerable).IsAssignableFrom(t)) return false;
        var e = t.IsArray ? t.GetElementType() : t.IsGenericType ? t.GetGenericArguments().LastOrDefault() : null;
        if (e == null) return false;
        if (e == typeof(byte)) return false;   // bytes print as one value
        entry = e;
        return true;
    }

    /// <summary>A declared type with no parts below it, as FindTree prints a leaf.</summary>
    static bool LeafType(Type t) =>
        t == typeof(string) || t.IsPrimitive || t.IsEnum || t == typeof(decimal) || t == typeof(Guid)
        || typeof(ITranslatedStringGetter).IsAssignableFrom(t) || typeof(IFormLinkGetter).IsAssignableFrom(t)
        || typeof(IAssetLinkGetter).IsAssignableFrom(t) || typeof(IConditionGetter).IsAssignableFrom(t)
        || t.Namespace?.StartsWith("Mutagen") != true;

    static bool IsFlags(Type t) => t.IsEnum && t.IsDefined(typeof(FlagsAttribute), false);

    /// <summary>What a declared type is, with its article: "a link", "text", "a number".</summary>
    static string Kind(Type t)
    {
        t = Unwrap(t);
        if (ListType(t, out _)) return "a list";
        if (typeof(IFormLinkGetter).IsAssignableFrom(t)) return "a link";
        if (t == typeof(string) || typeof(ITranslatedStringGetter).IsAssignableFrom(t) || typeof(IAssetLinkGetter).IsAssignableFrom(t)) return "text";
        if (t == typeof(bool)) return "a true/false value";
        if (IsFlags(t)) return "a set of flags";
        if (t.IsEnum) return "a named value";
        if (t.IsPrimitive || t == typeof(decimal)) return "a number";
        return LeafType(t) ? "a single value" : "a group of fields";
    }

    /// <summary>A record type's top-level fields as --where names them: find's fields without padding.</summary>
    static List<PropertyInfo> TopFields(Type t) => FieldCompare.Fields(t).Where(p => !DeepCompare.IsPadding(t, p.Name)).ToList();

    /// <summary>Checks every filter against the record type's class before any plugin is read: each path part names a
    /// member, "has" goes on flags or a list, and a flag name is one of its enum's names. Returns the refusal, or null.</summary>
    internal static string? Check(string sig, List<Filter> filters)
    {
        if (!Types.Value.TryGetValue(sig, out var tt)) return $"--where can't read the fields of {sig} records (Mutagen has no record class for it); --editorid-match still works on them";
        foreach (var f in filters)
        {
            var props = TopFields(tt.Class);
            Type declared = typeof(object);
            bool viaList = false;
            for (int i = 0; i < f.Path.Length; i++)
            {
                var part = f.Path[i];
                var p = props.FirstOrDefault(x => x.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
                if (p == null)
                {
                    var names = string.Join(", ", props.Select(x => x.Name).Distinct().Order(StringComparer.OrdinalIgnoreCase));
                    return i == 0
                        ? $"{sig} has no field \"{SafeText.OneLine(part)}\". Its fields: {names}"
                        : $"{string.Join(".", f.Path[..i])} has no part \"{SafeText.OneLine(part)}\". Its parts: {names}";
                }
                f.Path[i] = p.Name;
                declared = Unwrap(p.PropertyType);
                if (i == f.Path.Length - 1) break;
                var below = declared;
                if (ListType(declared, out var entry)) { viaList = true; below = Unwrap(entry); }
                if (LeafType(below))
                    return $"{string.Join(".", f.Path[..(i + 1)])} is {Kind(below)} and has no parts: leave out \".{string.Join(".", f.Path[(i + 1)..])}\"";
                props = Members(below);
            }
            if (f.Op != Op.Has) continue;
            if (ListType(declared, out _)) continue;
            if (IsFlags(declared))
            {
                var name = Enum.GetNames(declared).FirstOrDefault(n => n.Equals(f.Value, StringComparison.OrdinalIgnoreCase));
                if (name == null) return $"{f.Field} has no flag \"{SafeText.OneLine(f.Value ?? "")}\". Its flags: {string.Join(", ", Enum.GetNames(declared))}";
                f.Flag = (Enum)Enum.Parse(declared, name);
                continue;
            }
            return $"has works on flags and lists; {f.Field} is {Kind(declared)}{(viaList ? " (in a list, where any entry may match)" : "")}, use = or ~";
        }
        return null;
    }

    /// <summary>What to search for: the filters, the EditorID regex, the type (null with --editorid-match alone).</summary>
    internal sealed record Query(string? Type, List<Filter> Filters, Regex? EditorId, string? EditorIdText, int Max, bool Json, bool Quiet, bool PluginMode);

    sealed class Hit
    {
        public required FormKey Key;
        public required List<(PluginInfo Plugin, IMajorRecordGetter Rec)> Copies;
        public (PluginInfo Plugin, IMajorRecordGetter Rec) Winner => Copies[^1];
        public List<(string Field, string Text)> Shown = new();
    }

    /// <summary>The scan and the output. cantWhy is set when the run stops with exit 2.</summary>
    internal static int Run(LoadOrderSetup lo, Query q, string version, Stopwatch sw, out bool resultPrinted, out string cantWhy)
    {
        resultPrinted = false;
        cantWhy = "";
        foreach (var f in q.Filters) Log.Write($"where: {f.Field} {f.OpText}{(f.Value != null ? $" \"{SafeText.OneLine(f.Value)}\"" : "")}");
        if (q.EditorIdText != null) Log.Write($"editorid-match: {SafeText.OneLine(q.EditorIdText)}");
        // a console FormID value names a plugin by its place in this load order (not with --plugin, which loads only a few)
        foreach (var f in q.Filters)
            if (f.Value != null && FindCommand.ConsoleId.IsMatch(f.Value))
            {
                if (q.PluginMode) { Log.Write($"where: {f.Value} is compared as text, not as a console FormID (--plugin loads only the plugin and its masters)"); continue; }
                var fid = uint.Parse(f.Value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? f.Value[2..] : f.Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                f.ConsoleKey = FindCommand.FromConsoleId(fid, lo.Plugins, out _);
                if (f.ConsoleKey != null) Log.Write($"where: console FormID {f.Value} is {f.ConsoleKey}");
            }

        var idCache = lo.Plugins.Select(p => (IStarfieldModGetter)p.Mod).ToImmutableLinkCache<IStarfieldMod, IStarfieldModGetter>(LinkCachePreferences.OnlyIdentifiers());
        ILinkCache? fullCache = null;
        var unread = new List<string>();
        var eids = new Dictionary<(FormKey, bool), string?>();
        Dictionary<FormKey, string>? anyEids = null;
        static bool AnyType(IFormLinkGetter l) => typeof(IStarfieldMajorRecordGetter).IsAssignableTo(l.Type);
        string? LinkEid(IFormLinkGetter l)
        {
            bool any = AnyType(l);
            if (eids.TryGetValue((l.FormKey, any), out var e)) return e;
            e = null;
            if (!any)
                try
                {
                    if (idCache.TryResolveIdentifier(l.FormKey, l.Type, out var id))
                    {
                        if (!string.IsNullOrEmpty(id)) e = id;
                        else
                        {
                            // the winning copy has no EditorID: a partial form keeps the earlier copy's, as below
                            fullCache ??= lo.Plugins.Select(p => (IStarfieldModGetter)p.Mod).ToImmutableLinkCache<IStarfieldMod, IStarfieldModGetter>();
                            foreach (var c in fullCache.ResolveAllSimpleContexts(l.FormKey, l.Type))
                            {
                                if (!string.IsNullOrEmpty(c.Record.EditorID)) { e = c.Record.EditorID; break; }
                                if (!FieldCompare.IsPartial(c.Record)) break;
                            }
                        }
                    }
                }
                catch (Exception x) when (Fatal.Recoverable(x)) { }
            else
            {
                // a link to any record type (a form list's entries): the link cache would index every type, so one
                // pass over the load order reads each record's winning EditorID instead, once, on the first such link
                anyEids ??= AllEditorIds(lo.Plugins, unread);
                anyEids.TryGetValue(l.FormKey, out e);
            }
            return eids[(l.FormKey, any)] = e;
        }
        // as find prints a link; a link to any record type gets its EditorID only once a filter has read them all
        string Link(IFormLinkGetter l) =>
            (AnyType(l) && anyEids == null ? null : LinkEid(l)) is { } e ? $"{SafeText.OneLine(e)} [{l.FormKey}]" : $"[{l.FormKey}]";

        var hits = new List<Hit>();
        int tested = 0;
        try
        {
            if (q.Type != null)
            {
                var getter = Types.Value.TryGetValue(q.Type, out var tt) ? tt.Getter : null;
                var copies = new Dictionary<FormKey, List<(PluginInfo, IMajorRecordGetter)>>();
                foreach (var p in lo.Plugins)
                {
                    Analyzer.Where = $"reading the {q.Type} records in {p.FileName}";
                    int n = 0;
                    try
                    {
                        var recs = getter != null ? p.Mod.EnumerateMajorRecords(getter) : p.Mod.EnumerateMajorRecords().Where(r => Checker.SigsOf(r.GetType())?.Contains(q.Type) == true);
                        foreach (var r in recs)
                        {
                            n++;
                            if (!copies.TryGetValue(r.FormKey, out var l)) copies[r.FormKey] = l = new();
                            l.Add((p, r));
                        }
                    }
                    catch (Exception e) when (Fatal.Recoverable(e)) { unread.Add($"{p.FileName} couldn't be read in full ({e.GetType().Name}: {SafeText.OneLine(e.Message.Split('\n')[0])}); its later records weren't searched"); }
                    if (n > 0) Log.Write($"{p.FileName}: {n} {q.Type} record(s)");
                }
                int deleted = 0;
                foreach (var (key, list) in copies)
                {
                    var hit = new Hit { Key = key, Copies = list };
                    if (hit.Winner.Rec.IsDeleted) { deleted++; continue; }   // not in the game: never a match
                    tested++;
                    Analyzer.Where = $"testing {key} from {hit.Winner.Plugin.FileName}";
                    if (q.EditorId != null && !EidMatches(q.EditorId, WinningEditorId(list))) continue;
                    if (q.Filters.All(f => Matches(f, Values(f, list), LinkEid))) hits.Add(hit);
                }
                if (deleted > 0) Log.Write($"where: {deleted} record(s) whose winning copy is deleted were left out");
            }
            else
            {
                // --editorid-match alone: every record of every type; only the winning copy is kept, and only while it
                // matches. A partial form keeps the earlier EditorID (and name), so it joins the earlier copies as the winner.
                var winners = new Dictionary<FormKey, List<(PluginInfo, IMajorRecordGetter)>>();
                foreach (var p in lo.Plugins)
                {
                    Analyzer.Where = $"reading the EditorIDs in {p.FileName}";
                    int n = 0;
                    try
                    {
                        foreach (var r in p.Mod.EnumerateMajorRecords())
                        {
                            n++;
                            if (r.IsDeleted) { winners.Remove(r.FormKey); continue; }   // not in the game
                            var eid = r.EditorID;
                            bool partial = FieldCompare.IsPartial(r);
                            if (string.IsNullOrEmpty(eid) && partial)
                            {
                                if (winners.TryGetValue(r.FormKey, out var held)) held.Add((p, r));
                                continue;
                            }
                            if (!EidMatches(q.EditorId!, eid)) winners.Remove(r.FormKey);
                            else if (partial && winners.TryGetValue(r.FormKey, out var held)) held.Add((p, r));
                            else winners[r.FormKey] = [(p, r)];
                        }
                    }
                    catch (Exception e) when (e is not RegexMatchTimeoutException && Fatal.Recoverable(e)) { unread.Add($"{p.FileName} couldn't be read in full ({e.GetType().Name}: {SafeText.OneLine(e.Message.Split('\n')[0])}); its later records weren't searched"); }
                    tested += n;
                    Log.Write($"{p.FileName}: {n} record(s) read");
                }
                // a partial copy that brought its own matching EditorID over earlier copies that didn't match is held
                // alone; a second pass, for those records only, puts the earlier copies (its name) back in front of it
                var before = winners.Where(w => FieldCompare.IsPartial(w.Value[0].Item2)).ToDictionary(w => w.Key, w => w.Value[0].Item1.Index);
                if (before.Count > 0)
                {
                    var earlier = new Dictionary<FormKey, List<(PluginInfo, IMajorRecordGetter)>>();
                    int last = before.Values.Max();
                    foreach (var p in lo.Plugins.Where(p => p.Index < last))
                    {
                        Analyzer.Where = $"reading the earlier copies of partial forms in {p.FileName}";
                        try
                        {
                            foreach (var r in p.Mod.EnumerateMajorRecords())
                            {
                                if (!before.TryGetValue(r.FormKey, out var at) || p.Index >= at) continue;
                                if (r.IsDeleted) earlier.Remove(r.FormKey);
                                else if (FieldCompare.IsPartial(r) && earlier.TryGetValue(r.FormKey, out var l)) l.Add((p, r));
                                else earlier[r.FormKey] = [(p, r)];
                            }
                        }
                        catch (Exception e) when (Fatal.Recoverable(e)) { unread.Add($"{p.FileName} couldn't be read in full ({e.GetType().Name}: {SafeText.OneLine(e.Message.Split('\n')[0])}); a partial form's earlier copies may be missing"); }
                    }
                    foreach (var (k, l) in earlier) winners[k].InsertRange(0, l);
                    Log.Write($"editorid-match: {before.Count} partial winner(s) without a matching earlier copy, {earlier.Count} of them with earlier copies");
                }
                hits.AddRange(winners.Select(w => new Hit { Key = w.Key, Copies = w.Value }));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            foreach (var u in unread) Console.Error.WriteLine($"note: {u}");
            Console.Error.WriteLine($"--editorid-match {SafeText.OneLine(q.EditorIdText ?? "")} took longer than 2 seconds on one EditorID; give a simpler regular expression.");
            cantWhy = "the --editorid-match regular expression took too long";
            Log.Write("stopped: the --editorid-match regex timed out");
            return 2;
        }
        foreach (var u in unread) Console.Error.WriteLine($"note: {u}");
        hits = hits.OrderBy(h => h.Winner.Plugin.Index).ThenBy(h => h.Key.ToString(), StringComparer.Ordinal).ToList();
        var listed = q.Max == 0 ? hits : hits.Take(q.Max).ToList();
        foreach (var h in listed)
            foreach (var f in q.Filters) h.Shown.Add((f.Field, Show(Values(f, h.Copies), Link)));
        Log.Write($"where: {tested} record(s) tested, {hits.Count} match, {listed.Count} listed, {sw.Elapsed.TotalSeconds:F1} s");

        var what = Describe(q);
        resultPrinted = true;
        if (q.Json)
        {
            Console.WriteLine(Json(version, q, lo, listed, hits.Count, unread));
        }
        else
        {
            if (!q.Quiet) Console.WriteLine($"sf-mod-check {version} find: every {q.Type ?? "record"} {what} in {lo.Plugins.Count} plugins ({lo.OrderSource})");
            foreach (var h in listed)
            {
                var (wp, rec) = h.Winner;
                var name = NameOfHit(h);
                var eid = WinningEditorId(h.Copies);
                Console.WriteLine($"{(string.IsNullOrEmpty(eid) ? "(no EditorID)" : SafeText.OneLine(eid))} [{h.Key}]{(name != null ? $" \"{name}\"" : "")} ({TypeOf(rec)}, winner {wp.FileName})");
                foreach (var (f, t) in h.Shown) Console.WriteLine($"  {f}: {t}");
            }
            if (hits.Count > listed.Count)
                Console.WriteLine($"... and {hits.Count - listed.Count} more (--max 0 lists them all)");
            Console.WriteLine($"{hits.Count} record{(hits.Count == 1 ? "" : "s")} match{(hits.Count == 1 ? "es" : "")}");
            Console.WriteLine(hits.Count == 0
                ? $"RESULT: NOT FOUND (no {q.Type ?? "record"} {what} in {lo.Plugins.Count} plugins)"
                : $"RESULT: FOUND {hits.Count} record{(hits.Count == 1 ? "" : "s")}{(listed.Count < hits.Count ? $", {listed.Count} listed" : "")} ({sw.Elapsed.TotalSeconds:F0} s)");
        }
        Log.Write($"result: {(hits.Count == 0 ? "not found, exit code 1" : $"found {hits.Count}, exit code 0")}, {sw.Elapsed.TotalSeconds:F1} s");
        return hits.Count == 0 ? 1 : 0;
    }

    /// <summary>The winning copy's name; a partial form without one keeps the earlier copy's, as --where tests it.</summary>
    static string? NameOfHit(Hit h)
    {
        for (int i = h.Copies.Count - 1; i >= 0; i--)
        {
            var r = h.Copies[i].Rec;
            if (FindCommand.NameOf(r) is { } n) return n;
            if (!FieldCompare.IsPartial(r)) return null;
        }
        return null;
    }

    static string TypeOf(IMajorRecordGetter r) => Checker.SigsOf(r.GetType())?.Order(StringComparer.Ordinal).FirstOrDefault() ?? Analyzer.TypeShort(r.GetType());

    /// <summary>"where Flags has Unique and Name exists, EditorID matching ^Boss" for the heading and the RESULT line.</summary>
    static string Describe(Query q)
    {
        var parts = new List<string>();
        if (q.Filters.Count > 0) parts.Add("where " + string.Join(" and ", q.Filters.Select(f => $"{f.Field} {f.OpText}{(f.Value != null ? $" {SafeText.OneLine(f.Value)}" : "")}")));
        if (q.EditorIdText != null) parts.Add($"with an EditorID matching {SafeText.OneLine(q.EditorIdText)}");
        return string.Join(", ", parts);
    }

    static bool EidMatches(Regex re, string? eid) => !string.IsNullOrEmpty(eid) && re.IsMatch(eid);

    /// <summary>The winning copy's EditorID; a partial form without one keeps the earlier copy's.</summary>
    static string? WinningEditorId(List<(PluginInfo Plugin, IMajorRecordGetter Rec)> copies)
    {
        for (int i = copies.Count - 1; i >= 0; i--)
        {
            var r = copies[i].Rec;
            if (!string.IsNullOrEmpty(r.EditorID) || !FieldCompare.IsPartial(r)) return r.EditorID;
        }
        return null;
    }

    /// <summary>Every record's winning EditorID in the load order (a partial form without one keeps the earlier copy's;
    /// a deleted or nameless full copy has none), for links typed to any record type.</summary>
    static Dictionary<FormKey, string> AllEditorIds(List<PluginInfo> plugins, List<string> unread)
    {
        var sw = Stopwatch.StartNew();
        var map = new Dictionary<FormKey, string>();
        foreach (var p in plugins)
        {
            try
            {
                foreach (var r in p.Mod.EnumerateMajorRecords())
                {
                    var eid = r.EditorID;
                    if (r.IsDeleted) map.Remove(r.FormKey);
                    else if (!string.IsNullOrEmpty(eid)) map[r.FormKey] = eid;
                    else if (!FieldCompare.IsPartial(r)) map.Remove(r.FormKey);
                }
            }
            catch (Exception e) when (Fatal.Recoverable(e))
            {
                Log.Write($"where: the EditorIDs in {p.FileName} couldn't be read in full ({e.GetType().Name})");
                unread.Add($"{p.FileName} couldn't be read in full ({e.GetType().Name}: {SafeText.OneLine(e.Message.Split('\n')[0])}); its later EditorIDs weren't matched against form-list entries");
            }
        }
        Log.Write($"where: a link to any record type: read {map.Count} EditorIDs from {plugins.Count} plugins ({sw.Elapsed.TotalSeconds:F1} s)");
        return map;
    }

    static readonly Dictionary<(Type, string), PropertyInfo?> PropCache = new();

    static PropertyInfo? Prop(Type t, string name, bool top)
    {
        lock (PropCache)
        {
            if (PropCache.TryGetValue((t, name), out var p)) return p;
            var props = top ? FieldCompare.Fields(t) : FieldCompare.NestedProperties(t);
            return PropCache[(t, name)] = props.FirstOrDefault(x => x.Name.Equals(name, StringComparison.Ordinal));
        }
    }

    /// <summary>A value that is a list of entries (not text, a link, an asset path or bytes).</summary>
    static bool IsList(object? v) => v is IEnumerable and not (string or ITranslatedStringGetter or IAssetLinkGetter or IFormLinkGetter or IEnumerable<byte>);

    /// <summary>The values the filter's path reaches on the winning copy: the top-level field (a partial form's absent
    /// field from the earlier copy), then one member per part, fanning out over every list on the way.</summary>
    static List<object?> Values(Filter f, List<(PluginInfo Plugin, IMajorRecordGetter Rec)> copies)
    {
        object? top = null;
        for (int i = copies.Count - 1; i >= 0; i--)
        {
            var r = copies[i].Rec;
            var p = Prop(r.GetType(), f.Path[0], true);
            if (p == null) break;
            try { if (FieldCompare.Provides(r, p, out top)) break; }
            catch (Exception e) when (Fatal.Recoverable(e)) { Log.Write($"where: {r.FormKey}'s {p.Name} in {copies[i].Plugin.FileName} can't be read ({e.GetType().Name})"); top = null; break; }
            top = null;
        }
        var now = new List<object?> { top };
        foreach (var part in f.Path.Skip(1))
        {
            var next = new List<object?>();
            foreach (var v in now)
            {
                if (v == null) continue;
                IEnumerable<object?> items;
                try { items = IsList(v) ? ((IEnumerable)v).Cast<object?>().ToList() : [v]; }
                catch (Exception e) when (Fatal.Recoverable(e)) { continue; }
                foreach (var x in items)
                {
                    if (x == null || Prop(x.GetType(), part, false) is not { } p) continue;
                    try { next.Add(p.GetValue(x)); }
                    catch (Exception e) when (Fatal.Recoverable(e)) { }
                }
            }
            now = next;
        }
        return now;
    }

    /// <summary>The reached values with each list opened into its entries.</summary>
    static IEnumerable<object?> Flat(List<object?> values)
    {
        foreach (var v in values)
        {
            if (!IsList(v)) { yield return v; continue; }
            List<object?> items;
            try { items = ((IEnumerable)v!).Cast<object?>().ToList(); }
            catch (Exception e) when (Fatal.Recoverable(e)) { continue; }
            foreach (var x in items) yield return x;
        }
    }

    static bool Empty(object? v) => v switch
    {
        null => true,
        ITranslatedStringGetter ts => string.IsNullOrEmpty(ts.String),
        IAssetLinkGetter al => string.IsNullOrEmpty(al.GivenPath),
        _ => FieldCompare.IsEmpty(v),
    };

    internal static bool Matches(Filter f, List<object?> values, Func<IFormLinkGetter, string?> eid) => f.Op switch
    {
        Op.Exists => values.Any(v => !Empty(v)),
        Op.Eq => Flat(values).Any(v => Equal(f, v, eid)),
        Op.Ne => !Flat(values).Any(v => Equal(f, v, eid)),
        Op.Contains => Flat(values).Any(v => Contains(v, f.Value!, eid)),
        Op.Has => f.Flag != null
            ? values.Any(v => v is Enum e && e.GetType() == f.Flag.GetType() && HasFlag(e, f.Flag))
            : Flat(values).Any(v => Equal(f, v, eid)),
        _ => false,
    };

    static bool HasFlag(Enum value, Enum flag)
    {
        var want = Bits(flag);
        var have = Bits(value);
        return want == 0 ? have == 0 : (have & want) == want;
    }

    /// <summary>An enum's bits (a negative signed value keeps its bit pattern).</summary>
    static ulong Bits(Enum e) => Type.GetTypeCode(Enum.GetUnderlyingType(e.GetType())) switch
    {
        TypeCode.SByte or TypeCode.Int16 or TypeCode.Int32 or TypeCode.Int64 => unchecked((ulong)Convert.ToInt64(e, CultureInfo.InvariantCulture)),
        _ => Convert.ToUInt64(e, CultureInfo.InvariantCulture),
    };

    /// <summary>An enum as text: .NET's names, or for a flags value with bits that have no name (which .NET prints as
    /// a bare number), the named flags that are set and the rest in hex: "Female, Unique, 0x2000010".</summary>
    internal static string EnumText(Enum e)
    {
        var t = e.GetType();
        var s = e.ToString();
        if (!IsFlags(t) || s.Length == 0 || !(char.IsDigit(s[0]) || s[0] == '-')) return s;
        var have = Bits(e);
        var names = new List<string>();
        ulong named = 0;
        foreach (var v in Enum.GetValues(t).Cast<Enum>())
        {
            var b = Bits(v);
            if (b != 0 && (b & (b - 1)) == 0 && (have & b) == b) { names.Add(v.ToString()); named |= b; }
        }
        var rest = have & ~named;
        if (rest != 0) names.Add($"0x{rest:X}");
        return names.Count == 0 ? s : string.Join(", ", names);
    }

    static readonly Regex Bracketed = new(@"\[([0-9A-Fa-f]{6}:[^\]]+)\]\s*\z", RegexOptions.CultureInvariant);

    static bool Equal(Filter f, object? v, Func<IFormLinkGetter, string?> eid)
    {
        var want = f.Value!;
        switch (v)
        {
            case null: return false;
            case IFormLinkGetter fl:
                if (fl.IsNull) return false;
                if (f.ConsoleKey is { } ck && ck == fl.FormKey) return true;
                // a FormKey, or a link as find prints it ("EditorID [FormKey]")
                if (FormKey.TryFactory(want, out var fk) && fk == fl.FormKey) return true;
                if (Bracketed.Match(want) is { Success: true } m && FormKey.TryFactory(m.Groups[1].Value, out var bk) && bk == fl.FormKey) return true;
                return string.Equals(eid(fl), want, StringComparison.OrdinalIgnoreCase);
            case ITranslatedStringGetter ts: return string.Equals(ts.String ?? "", want, StringComparison.OrdinalIgnoreCase);
            case string s: return string.Equals(s, want, StringComparison.OrdinalIgnoreCase);
            case IAssetLinkGetter al: return string.Equals((al.GivenPath ?? "").Replace('/', '\\'), want.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
            case bool b: return string.Equals(b ? "true" : "false", want, StringComparison.OrdinalIgnoreCase);
            case Enum e:
                if (string.Equals(EnumText(e), want, StringComparison.OrdinalIgnoreCase) || string.Equals(e.ToString(), want, StringComparison.OrdinalIgnoreCase)) return true;
                return ulong.TryParse(want, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ev) && Bits(e) == ev;
        }
        if (IsNumber(v) && double.TryParse(want, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return v switch
            {
                float x => x == (float)d,
                double x => x == d,
                decimal x => (double)x == d,
                _ => Convert.ToDouble(v, CultureInfo.InvariantCulture) == d,
            };
        return string.Equals(PlainText(v), want, StringComparison.OrdinalIgnoreCase);
    }

    static bool Contains(object? v, string want, Func<IFormLinkGetter, string?> eid)
    {
        switch (v)
        {
            case null: return false;
            case IFormLinkGetter fl:
                if (fl.IsNull) return false;
                return fl.FormKey.ToString().Contains(want, StringComparison.OrdinalIgnoreCase)
                       || (eid(fl)?.Contains(want, StringComparison.OrdinalIgnoreCase) ?? false);
        }
        return PlainText(v).Contains(want, StringComparison.OrdinalIgnoreCase);
    }

    static bool IsNumber(object v) => v is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal;

    /// <summary>A value's text without quotes, as compared by = and ~ (links are compared by EditorID and FormKey).</summary>
    static string PlainText(object v) => v switch
    {
        ITranslatedStringGetter ts => ts.String ?? "",
        string s => s,
        IAssetLinkGetter al => al.GivenPath ?? "",
        Enum e => EnumText(e),
        bool => v.ToString() ?? "",
        _ when IsNumber(v) => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
        _ => FindTree.LeafText(v, null, out _) is { Length: > 0 } t ? t : FieldCompare.Dump(v),
    };

    /// <summary>How a --where field prints beside a match: each value reached (lists opened), as find's --full prints a
    /// leaf, at most 10, joined with "; ".</summary>
    static string Show(List<object?> values, Func<IFormLinkGetter, string> link)
    {
        var texts = new List<string>();
        foreach (var v in Flat(values))
        {
            if (Empty(v) && v is not Enum && !(v != null && IsNumber(v)) && v is not bool) continue;
            string t;
            try { t = v is Enum en ? EnumText(en) : FindTree.LeafText(v, link, out _) ?? FieldCompare.Dump(v); }
            catch (Exception e) when (Fatal.Recoverable(e)) { t = $"(unreadable: {e.GetType().Name})"; }
            texts.Add(t.Length > 300 ? t[..300] + "..." : t);
        }
        if (texts.Count == 0) return "(empty)";
        var shown = string.Join("; ", texts.Take(10));
        return texts.Count > 10 ? $"{shown}; ... ({texts.Count} in all)" : shown;
    }

    static string Json(string version, Query q, LoadOrderSetup lo, List<Hit> listed, int total, List<string> unread)
    {
        var root = new JsonObject
        {
            ["tool"] = "sf-mod-check find",
            ["version"] = version,
            ["query"] = null,
            ["type"] = q.Type,
            ["where"] = new JsonArray(q.Filters.Select(f => (JsonNode)new JsonObject { ["field"] = f.Field, ["op"] = f.OpText, ["value"] = f.Value }).ToArray()),
            ["editorIdMatch"] = q.EditorIdText,
            ["loadOrder"] = new JsonObject { ["source"] = lo.OrderSource, ["plugins"] = lo.Plugins.Count },
            ["matches"] = new JsonArray(listed.Select(h =>
            {
                var fields = new JsonObject();
                foreach (var (f, t) in h.Shown) if (!fields.ContainsKey(f)) fields[f] = t;
                return (JsonNode)new JsonObject
                {
                    ["formKey"] = h.Key.ToString(),
                    ["editorId"] = WinningEditorId(h.Copies),
                    ["name"] = NameOfHit(h),
                    ["type"] = TypeOf(h.Winner.Rec),
                    ["winner"] = h.Winner.Plugin.FileName,
                    ["fields"] = fields,
                };
            }).ToArray()),
            ["listed"] = listed.Count,
            ["total"] = total,
            ["unread"] = new JsonArray(unread.Select(u => (JsonNode)u).ToArray()),
            ["result"] = total == 0 ? "not found" : "found",
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
