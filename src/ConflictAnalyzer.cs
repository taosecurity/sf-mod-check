// Moved from StarfieldConflictCheck (Analyzer.cs, commit d336108) into sfmodcheck on 2026-11-16.
// For every record a mod overrides, works out what each plugin meant to change and which of those changes the winner loses.
//
// A plugin's intended changes = fields that differ from the version it was built on, which is the latest earlier
// version from one of its own (transitive) masters. A change is lost when the winning version has a different value
// for that field and the winner does not have the loser as a master (if it does, the winner chose its value knowingly).
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;

namespace SfModCheck.Conflict;


// Original: the base game value, described. LoserChanges/WinnerChanges: what each plugin did to it. Differences: lost vs in game.
public record LostField(string Field, bool WinnerChangedToo, string LoserValue, string WinnerValue, List<Difference> Differences,
    string Original, List<Difference> LoserChanges, List<Difference> WinnerChanges);

// Origin: the plugin that defines the record (usually Starfield.esm). RecordName: its in-game name, if any.
public record LostChange(
    FormKey FormKey, string RecordType, string? EditorID, string? RecordName, string Origin,
    string Loser, bool LoserOfficial, string Winner, bool WinnerOfficial,
    List<LostField> Fields);

public record ItmRecord(string Plugin, FormKey FormKey, string RecordType, string? EditorID);

public class Analysis
{
    public int RecordsOverriddenByMods;
    public List<LostChange> Lost { get; } = new();
    public List<ItmRecord> Itms { get; } = new();
    // (earlier, later) -> "Type EditorID: Field" entries the later mod changed on purpose that the earlier one would undo if moved after it.
    public Dictionary<(string Earlier, string Later), List<string>> ReorderCost { get; } = new();
    public Dictionary<string, int> UnreadableFields { get; } = new();
    public List<string> Errors { get; } = new();
}

public static class Analyzer
{
    // INavigationMeshGetter -> NavigationMesh, IIdleMarkerGetter -> IdleMarker (TrimStart('I') ate every leading I).
    public static string TypeShort(Type t) =>
        t.Name.StartsWith('I') && t.Name.EndsWith("Getter") && t.Name.Length > "IGetter".Length ? t.Name[1..^"Getter".Length] : t.Name;

    // --max-memory: stop with MemoryLimitException when managed memory passes this many bytes (0 = no limit).
    public static long MaxManagedBytes;
    // --verbose / --log: one line per record type (records, versions, memory).
    public static Action<string>? Progress;
    // Progress for the person waiting (stderr in sfmodcheck; nothing with --quiet).
    public static Action<string> Info = Console.Error.WriteLine;
    // What the run was doing (plugin, record), for the message when something stops it anyway (0.5.1). The record being
    // compared is kept as its parts and only written out when the message is needed (code review: not once per record).
    public static string Where
    {
        get => whereRecord is { } w ? $"comparing {w.Key} ({TypeShort(w.Type)}) in {string.Join(", ", w.Versions.Select(v => v.Plugin.FileName))}" : where;
        set { where = value; whereRecord = null; }
    }
    static string where = "";
    static (FormKey Key, Type Type, List<(PluginInfo Plugin, IMajorRecordGetter Rec)> Versions)? whereRecord;

    // A failure the run can carry on past (skip the record, field or group and list it). Not the --max-memory stop, and
    // not running out of memory or stack, which must stop the run with CANNOT CHECK rather than leave a report that
    // looks whole (code review 0.5.1). Every catch in the analyzer uses it; reflection wraps errors, so inner ones count.
    public static bool Recoverable(Exception e)
    {
        for (Exception? x = e; x is not null; x = x.InnerException)
            if (x is MemoryLimitException or OutOfMemoryException or InsufficientExecutionStackException) return false;
        return true;
    }

    // "CrowdRegion_Cydonia01 [00ABCD:Mod.esm], subrecord BFCB" from Mutagen's record exceptions; "" when it doesn't say.
    public static string RecordOf(Exception e)
    {
        for (var x = e; x is not null; x = x.InnerException)
        {
            if (x is not Mutagen.Bethesda.Plugins.Exceptions.RecordException re || (re.FormKey is null && re.EditorID is null)) continue;
            var s = string.Join(" ", new[] { Impact.OneLine(re.EditorID), re.FormKey is { } fk ? $"[{fk}]" : null }.Where(t => !string.IsNullOrEmpty(t)));
            if (x is Mutagen.Bethesda.Plugins.Exceptions.SubrecordException se) s += $", subrecord {Impact.OneLine(se.Subrecord.ToString())}";
            return s;
        }
        return "";
    }

    // A top-level group property (Quests, ArmorAddons) as the record type it holds ("quest", "armor addon").
    static string GroupName(System.Reflection.PropertyInfo prop)
    {
        var t = prop.PropertyType.IsGenericType ? prop.PropertyType.GetGenericArguments()[0] : null;
        return t is not null && typeof(IMajorRecordGetter).IsAssignableFrom(t) ? Impact.TypeName(TypeShort(t)) : prop.Name;
    }

    // SFMODCHECK_DEBUG_RECORD=<FormKey> (for example 064F3C:Starfield.esm) prints the working for that one record.
    static readonly string? DebugRecord = Environment.GetEnvironmentVariable("SFMODCHECK_DEBUG_RECORD");

    // Every Starfield record getter interface that belongs to a real record class (INpcGetter for Npc, and so on).
    static readonly Lazy<Type[]> AllConcrete = new(() =>
    {
        var types = typeof(IStarfieldModGetter).Assembly.GetTypes();
        var classes = types.Where(t => t.IsClass && !t.IsAbstract && typeof(IMajorRecord).IsAssignableFrom(t))
            .Select(t => t.FullName).ToHashSet();
        return types.Where(t => t.IsInterface && t.Name.StartsWith('I') && t.Name.EndsWith("Getter")
                && typeof(IMajorRecordGetter).IsAssignableFrom(t)
                && classes.Contains($"{t.Namespace}.{t.Name[1..^"Getter".Length]}"))
            .ToArray();
    });

    public static Analysis Run(List<PluginInfo> plugins)
    {
        var result = new Analysis();
        var byName = plugins.ToDictionary(p => p.FileName, StringComparer.OrdinalIgnoreCase);

        // Transitive masters per plugin.
        var allMasters = new Dictionary<ModKey, HashSet<ModKey>>();
        HashSet<ModKey> MastersOf(PluginInfo p)
        {
            if (allMasters.TryGetValue(p.Mod.ModKey, out var set)) return set;
            set = new HashSet<ModKey>();
            foreach (var m in p.Masters)
                if (byName.TryGetValue(m, out var mp)) { set.Add(mp.Mod.ModKey); set.UnionWith(MastersOf(mp)); }
            return allMasters[p.Mod.ModKey] = set;
        }

        // Every record that at least one mod overrides.
        var targets = new Dictionary<FormKey, Type>();
        void AddTargets(PluginInfo p, IEnumerable<IMajorRecordGetter> recs)
        {
            foreach (var rec in recs)
                if (rec.FormKey.ModKey != p.Mod.ModKey) targets.TryAdd(rec.FormKey, rec.Registration.GetterType);
        }
        // One line per unreadable spot (0.5.1): both passes below meet the same bad record (a 157-plugin load order: 14 lines
        // for 7 groups), so a read problem is listed once per plugin, record and error.
        // Seen means the same plugin and record type (the usual repeat), or the same plugin, named record and error (a
        // quest group that fails in the first pass fails again as its scenes in the second). Without a record name the
        // error text alone can't tell two groups apart, so it isn't used (code review).
        var reported = new HashSet<(string Plugin, string Record, string Why)>();
        var reportedTypes = new HashSet<(string Plugin, string What)>();
        void ReadProblem(PluginInfo p, string what, Exception e)
        {
            while (e is System.Reflection.TargetInvocationException { InnerException: { } inner }) e = inner;   // a group property's getter
            var why = $"{e.GetType().Name}: {Impact.OneLine(e.Message.Split('\n')[0])}";
            var at = RecordOf(e);
            var plugin = p.FileName.ToLowerInvariant();
            if (!reportedTypes.Add((plugin, what)) | (at.Length > 0 && !reported.Add((plugin, at, why)))) return;
            result.Errors.Add($"{p.FileName}: could not read its {what} records{(at.Length > 0 ? $" (at {at})" : "")}, so its changes to them were not checked ({why})");
        }
        foreach (var p in plugins.Where(p => !p.Official))
        {
            // One unreadable record used to stop the whole run (2026-11-17, a 157-plugin load order: a generic base form
            // with a component type Mutagen 0.54.4 can't parse). Plugins that read cleanly take the single
            // pass as before; otherwise each top-level group is read on its own and only the unreadable group is skipped,
            // listed under "Tool limits hit". Records read before the failure stay (TryAdd keeps them once).
            Where = $"reading {p.FileName}";
            try { AddTargets(p, p.Mod.EnumerateMajorRecords()); }
            catch (Exception err) when (Recoverable(err))
            {
                foreach (var prop in p.Mod.GetType().GetProperties())
                {
                    if (prop.GetIndexParameters().Length > 0) continue;
                    var what = GroupName(prop);
                    object? val;
                    try { val = prop.GetValue(p.Mod); }
                    catch (Exception e) when (Recoverable(e))
                    {
                        if (typeof(IMajorRecordGetterEnumerable).IsAssignableFrom(prop.PropertyType)) ReadProblem(p, what, e);
                        continue;
                    }
                    if (val is not IMajorRecordGetterEnumerable group || val is IMajorRecordGetter || val is IModGetter) continue;
                    try { AddTargets(p, group.EnumerateMajorRecords()); }
                    catch (Exception e) when (Recoverable(e)) { ReadProblem(p, what, e); }
                }
            }
        }
        result.RecordsOverriddenByMods = targets.Count;
        Info($"{targets.Count} records overridden by mods");
        Progress?.Invoke($"{targets.Count} records overridden by mods, in {targets.Values.Distinct().Count()} record types");

        // Record references in impact text read as "EditorID (type)" rather than raw FormKeys. The cache keeps EditorIDs
        // only and is asked for the link's own record type: a whole-record, any-type lookup made Mutagen index and keep
        // every record of every type it searched (CHANGELOG.md, 0.5.0).
        var ids = plugins.Select(p => (IStarfieldModGetter)p.Mod).ToImmutableLinkCache<IStarfieldMod, IStarfieldModGetter>(LinkCachePreferences.OnlyIdentifiers());
        // Keyed on the FormKey's text: FormKeys compare without letter case, but the text keeps each plugin's own spelling.
        var names = new Dictionary<(string, Type), string>();
        var concreteFor = new Dictionary<Type, Type[]>();
        string Name(IFormLinkGetter link)
        {
            var key = (link.FormKey.ToString(), link.Type);
            if (names.TryGetValue(key, out var known)) return known;
            string text;
            try
            {
                // A link to a kind of record (IPlacedGetter) is looked up as each concrete record type it covers, so the
                // text names the real type ("placed NPC"), as the whole-record lookup did.
                text = ids.TryResolveIdentifier(link.FormKey, Concrete(link.Type), out var editorId, out var found)
                    ? $"{editorId ?? link.FormKey.ToString()} ({Impact.TypeName(RecordName(found ?? link.Type))})"
                    : link.FormKey.ToString();
            }
            catch (Exception err) when (Recoverable(err)) { text = link.FormKey.ToString(); }
            return names[key] = text;
        }
        // INavigationMeshGetter -> NavigationMesh, the record class name the type table uses.
        static string RecordName(Type t) => TypeShort(t);

        // The concrete record getter types (INpcGetter, IPlacedNpcGetter, ...) a link type covers.
        Type[] Concrete(Type linkType)
        {
            if (concreteFor.TryGetValue(linkType, out var known)) return known;
            var result = AllConcrete.Value.Where(linkType.IsAssignableFrom).ToArray();
            return concreteFor[linkType] = result.Length > 0 ? result : new[] { linkType };
        }

        // Versions of each target are found by reading every plugin once per target record type and keeping only the
        // target records, one record type at a time. Mutagen's link cache (used before 2026-11-16) indexes and keeps every
        // record of a type in every plugin: over 10 GB for placed objects, and it still returned only the mod's copy of most
        // placed objects, so they were never compared (CHANGELOG.md, 0.5.0).
        int done = 0;
        var order = new Dictionary<FormKey, int>();
        foreach (var fk in targets.Keys) order[fk] = order.Count;
        var costAt = new Dictionary<(string Earlier, string Later), List<int>>();
        foreach (var group in targets.GroupBy(t => t.Value).OrderBy(g => g.Key.Name, StringComparer.Ordinal))
        {
            var type = group.Key;
            var found = new Dictionary<FormKey, List<(PluginInfo Plugin, IMajorRecordGetter Rec)>>();
            var partly = new List<PluginInfo>();   // plugins whose records of this type could not all be read
            foreach (var p in plugins)  // load order: origin first, winner last
            {
                Where = $"reading {TypeShort(type)} records from {p.FileName}";
                try
                {
                    foreach (var rec in p.Mod.EnumerateMajorRecords(type))
                    {
                        if (!targets.TryGetValue(rec.FormKey, out var t) || t != type) continue;
                        if (!found.TryGetValue(rec.FormKey, out var list)) found[rec.FormKey] = list = new();
                        if (list.Count == 0 || list[^1].Plugin != p) list.Add((p, rec));  // one version per plugin
                    }
                }
                catch (Exception e) when (Recoverable(e)) { ReadProblem(p, Impact.TypeName(TypeShort(type)), e); partly.Add(p); }
                // also between plugins while a record type is read, so --max-memory holds during the read itself (0.5.0)
                if (MaxManagedBytes > 0 && GC.GetTotalMemory(false) > MaxManagedBytes)
                    throw new MemoryLimitException($"memory passed {MaxManagedBytes / 1e9:F1} GB while reading {TypeShort(type)} records from {p.FileName} ({done} of {targets.Count} done)");
            }
            Progress?.Invoke($"  {TypeShort(type)}: {group.Count()} records, {found.Values.Sum(l => l.Count)} versions, managed memory {GC.GetTotalMemory(false) / 1e9:F1} GB");
            // A plugin read only partly may hold an unread version of a record (code review). A plugin can only override
            // records of its own masters, so it matters only for those; and only where its version would win or be the
            // base of a later version (a master of it). Those records are skipped and counted, not reported wrongly.
            var notCompared = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // by file name: hashing a PluginInfo record hashes its whole plugin through Mutagen
            // direct masters only: a record's FormID can only name the plugin itself or one of its own masters
            var partlyMasters = partly.Select(p => p.Masters.Select(m => ModKey.TryFromFileName(m, out var mk) ? mk : (ModKey?)null)
                .OfType<ModKey>().Append(p.Mod.ModKey).ToHashSet()).ToList();
            PluginInfo? MayHoldUnread(FormKey fk, List<(PluginInfo Plugin, IMajorRecordGetter Rec)>? vs)
            {
                for (int u = 0; u < partly.Count; u++)
                {
                    var p = partly[u];
                    if (!partlyMasters[u].Contains(fk.ModKey)) continue;   // it can't override this record
                    if (vs?.Any(v => ReferenceEquals(v.Plugin, p)) == true) continue;   // its version was read before the failure
                    if (vs is null || vs.Count == 0 || p.Index > vs[^1].Plugin.Index
                        || vs.Any(v => v.Plugin.Index > p.Index && MastersOf(v.Plugin).Contains(p.Mod.ModKey))) return p;
                }
                return null;
            }
            Where = $"comparing {TypeShort(type)} records";
        foreach (var (formKey, _) in group)
        {
            whereRecord = null;   // a skipped record must not leave the previous one named (code review)
            if (++done % 5000 == 0) Info($"  {done}/{targets.Count}");
            if (MaxManagedBytes > 0 && GC.GetTotalMemory(false) > MaxManagedBytes)
                throw new MemoryLimitException($"memory passed {MaxManagedBytes / 1e9:F1} GB while comparing {TypeShort(type)} records ({done} of {targets.Count} done)");
            found.TryGetValue(formKey, out var versions);
            if (partly.Count > 0 && MayHoldUnread(formKey, versions) is { } unread)
            {
                notCompared[unread.FileName] = notCompared.GetValueOrDefault(unread.FileName) + 1;
                continue;
            }
            if (versions is null || versions.Count < 2) continue;
            whereRecord = (formKey, type, versions);
            // This record's results are kept only once all of it is worked out (code review): a failure part way used to
            // leave some entries behind, and a reorder-cost key without its position, which stopped the final sort.
            var recLost = new List<LostChange>();
            var recItms = new List<ItmRecord>();
            var recCost = new List<((string Earlier, string Later) Pair, string Text)>();
            var recUnreadable = new List<string>();
            // A record whose versions can't be compared is skipped and listed, not the whole run (0.5.1; reads are lazy,
            // so a damaged field can throw anywhere below).
            try
            {

            var recType = versions[0].Rec.GetType();
            if (versions.Any(v => v.Rec.GetType() != recType))
            {
                result.Errors.Add($"{formKey}: record type differs between plugins, skipped");
                continue;
            }
            var fields = FieldCompare.Fields(recType);
            int n = versions.Count, nf = fields.Length;

            // Each version's value per field, and whether it supplies one (partial forms skip fields they leave alone).
            var vals = new object?[n, nf];
            var prov = new bool[n, nf];
            var unsure = new bool[n];   // a field couldn't be read or compared: never call this version an ITM (0.5.0)
            void Unreadable(IMajorRecordGetter r, string field)
            {
                recUnreadable.Add($"{r.Registration.ClassType.Name}.{field}");
            }
            for (int i = 0; i < n; i++)
                for (int f = 0; f < nf; f++)
                {
                    try { prov[i, f] = FieldCompare.Provides(versions[i].Rec, fields[f], out vals[i, f]); }
                    catch (Exception err) when (Recoverable(err))
                    {
                        unsure[i] = true;
                        Unreadable(versions[i].Rec, fields[f].Name);
                    }
                }

            // What each version meant to change: fields that differ from the latest earlier value among its own masters.
            var intended = new HashSet<int>[n];
            intended[0] = new();
            for (int i = 1; i < n; i++)
            {
                intended[i] = new();
                var masters = MastersOf(versions[i].Plugin);
                for (int f = 0; f < nf; f++)
                {
                    if (!prov[i, f]) continue;
                    int b = -1;
                    for (int j = i - 1; j >= 0 && b < 0; j--)
                        if (prov[j, f] && masters.Contains(versions[j].Plugin.Mod.ModKey)) b = j;
                    var baseVal = b >= 0 ? vals[b, f] : null;
                    bool same;
                    try { same = FieldCompare.DeepEq(vals[i, f], baseVal, 0); }
                    catch (Exception err) when (Recoverable(err)) { same = true; unsure[i] = true; Unreadable(versions[i].Rec, fields[f].Name); }
                    if (!same) intended[i].Add(f);
                }
                // Parents (cells, worldspaces, topics) must be copied unchanged to hold child records, so they are not ITMs.
                // A quest is such a parent only when this plugin's copy holds child records (its topics and dialogue lines);
                // a quest copied with nothing under it is still an ITM (2026-11-16; matches sfmodcheck check's itm rule).
                if (intended[i].Count == 0 && !unsure[i] && !versions[i].Plugin.Official && versions[i].Rec is not (ICellGetter or IWorldspaceGetter or IDialogTopicGetter)
                    && !(versions[i].Rec is IQuestGetter && versions[i].Rec is IMajorRecordGetterEnumerable kids && kids.EnumerateMajorRecords().Any()))
                {
                    var r = versions[i].Rec;
                    recItms.Add(new(versions[i].Plugin.FileName, formKey, r.Registration.ClassType.Name, Impact.OneLine(r.EditorID)));
                }
            }

            if (DebugRecord is not null && DebugRecord.Equals(formKey.ToString(), StringComparison.OrdinalIgnoreCase))
                for (int i = 0; i < n; i++)
                    Console.Error.WriteLine($"DEBUG {versions[i].Plugin.FileName}: intended=[{string.Join(",", intended[i].Select(f => fields[f].Name))}] " +
                        string.Join(" ", Enumerable.Range(0, nf).Select(f => $"{fields[f].Name}{(prov[i, f] ? "" : "(none)")}={FieldCompare.Show(versions[i].Rec, fields[f].Name, 40)}")));

            // Changes thrown away: the game uses the last version that supplies each field.
            var lostByPair = new Dictionary<(int Loser, int Winner), List<LostField>>();
            for (int f = 0; f < nf; f++)
            {
                int k = -1;
                for (int j = n - 1; j >= 0 && k < 0; j--) if (prov[j, f]) k = j;
                if (k < 0) continue;
                var winner = versions[k].Plugin;
                var winnerMasters = MastersOf(winner);
                for (int i = 0; i < k; i++)
                {
                    var loser = versions[i].Plugin;
                    if (!intended[i].Contains(f)) continue;
                    if (loser.Official && winner.Official) continue;          // Bethesda vs Bethesda is not our business
                    if (winnerMasters.Contains(loser.Mod.ModKey)) continue;   // winner saw this plugin and decided
                    bool same;
                    try { same = FieldCompare.DeepEq(vals[i, f], vals[k, f], 0); }
                    catch (Exception err) when (Recoverable(err)) { same = true; Unreadable(versions[i].Rec, fields[f].Name); }   // counted in Tool limits (0.5.0)
                    if (same) continue;
                    lostByPair.TryAdd((i, k), new());
                    // The base game value: the first version that supplies this field (normally the defining plugin).
                    int o = 0;
                    while (o < n - 1 && !prov[o, f]) o++;
                    var field = fields[f].Name;
                    // A part of the value that can't be read while it is described is counted once in Tool limits
                    // (0.5.1), not dropped silently.
                    bool partUnreadable = false;
                    void Mark() => partUnreadable = true;
                    List<Difference> Safe(Func<List<Difference>> make) { try { return make(); } catch (Exception err) when (Recoverable(err)) { Mark(); return new(); } }
                    var outerMark = Overrides.OnUnreadable;
                    Overrides.OnUnreadable = Mark;   // also land flags shown as "unreadable" in the values (code review)
                    try
                    {
                        string original;
                        try { original = Impact.Describe(vals[o, f], Name); } catch (Exception err) when (Recoverable(err)) { Mark(); original = "(could not describe)"; }
                        lostByPair[(i, k)].Add(new(field, intended[k].Contains(f),
                            FieldCompare.Show(versions[i].Rec, field), FieldCompare.Show(versions[k].Rec, field),
                            Safe(() => Impact.Differences(field, vals[i, f], vals[k, f], Name, Mark)),
                            original,
                            Safe(() => Impact.Changes(field, vals[o, f], vals[i, f], Name, Mark)),
                            Safe(() => Impact.Changes(field, vals[o, f], vals[k, f], Name, Mark))));
                    }
                    finally { Overrides.OnUnreadable = outerMark; }
                    if (partUnreadable) Unreadable(versions[i].Rec, field);
                }
            }
            foreach (var ((i, k), lost) in lostByPair)
            {
                var (loseRec, winRec) = (versions[i].Rec, versions[k].Rec);
                string? recName = null;
                try { recName = versions.Select(v => (v.Rec as ITranslatedNamedGetter)?.Name?.String).LastOrDefault(s => !string.IsNullOrEmpty(s)); recName = recName is null ? null : Impact.OneLine(recName); } catch (Exception err) when (Recoverable(err)) { }
                recLost.Add(new(formKey, loseRec.Registration.ClassType.Name, Impact.OneLine(winRec.EditorID ?? loseRec.EditorID), recName,
                    versions[0].Plugin.FileName, versions[i].Plugin.FileName, versions[i].Plugin.Official,
                    versions[k].Plugin.FileName, versions[k].Plugin.Official, lost));
            }

            // Cost of reordering: for every pair of mods on this record (earlier e, later l, neither a master of the other),
            // which changes l made on purpose would e undo if e loaded after l instead.
            for (int e = 0; e < n; e++)
                for (int lt = e + 1; lt < n; lt++)
                {
                    var (pe, pl) = (versions[e].Plugin, versions[lt].Plugin);
                    if (pe.Official || pl.Official || MastersOf(pl).Contains(pe.Mod.ModKey)) continue;
                    for (int f = 0; f < nf; f++)
                    {
                        if (!intended[lt].Contains(f) || !prov[e, f]) continue;
                        List<Difference> d;
                        try { d = Impact.Differences(fields[f].Name, vals[lt, f], vals[e, f], Name); } catch (Exception err) when (Recoverable(err)) { d = new() { new("?", false) }; }
                        if (d.Count == 0 || d.All(x => x.Minor)) continue;
                        recCost.Add(((pe.FileName, pl.FileName), $"{versions[lt].Rec.Registration.ClassType.Name} {Impact.OneLine(versions[lt].Rec.EditorID ?? formKey.ToString())}: {fields[f].Name}"));
                    }
                }

            result.Lost.AddRange(recLost);
            result.Itms.AddRange(recItms);
            foreach (var (pair, text) in recCost)
            {
                result.ReorderCost.TryAdd(pair, new());
                result.ReorderCost[pair].Add(text);
                costAt.TryAdd(pair, new());
                costAt[pair].Add(order[formKey]);
            }
            }
            // --max-memory and running out of memory still stop the run (Recoverable), as they did before this catch existed
            catch (Exception e) when (Recoverable(e))
            {
                result.Errors.Add($"{formKey} ({Impact.TypeName(TypeShort(type))}): could not compare its versions in {string.Join(", ", versions.Select(v => v.Plugin.FileName))}, " +
                                  $"so it was skipped ({e.GetType().Name}: {Impact.OneLine(e.Message.Split('\n')[0])})");
            }
            finally
            {
                // The fields it couldn't read are counted even when the record was skipped: they are the best clue to
                // why (code review round 3). Its findings are kept only on success (above).
                foreach (var key in recUnreadable) result.UnreadableFields[key] = result.UnreadableFields.GetValueOrDefault(key) + 1;
                // whereRecord stays set here, so a run stopped by an error from this record still names it; the next
                // record clears it at the top of the loop
            }
        }
            foreach (var (p, count) in notCompared)
                result.Errors.Add($"{p}: {count} other {Impact.TypeName(TypeShort(type))} record{(count == 1 ? " was" : "s were")} not compared, " +
                                  $"because {p} may change {(count == 1 ? "it" : "them")} too but its {Impact.TypeName(TypeShort(type))} records could not all be read");
        }
        Where = "sorting the results";   // the command sets the next step

        // Records were worked through one type at a time; put the results back in the order the records were found,
        // so reports list them (and their first examples) as before.
        var lostInOrder = result.Lost.OrderBy(l => order[l.FormKey]).ToList();
        result.Lost.Clear(); result.Lost.AddRange(lostInOrder);
        var itms = result.Itms.OrderBy(t => order[t.FormKey]).ToList();
        result.Itms.Clear(); result.Itms.AddRange(itms);
        var costs = result.ReorderCost.Select(kv => (kv.Key, Entries: kv.Value.Zip(costAt[kv.Key]).OrderBy(z => z.Second).Select(z => z.First).ToList(), First: costAt[kv.Key].Min()))
            .OrderBy(c => c.First).ToList();
        result.ReorderCost.Clear();
        foreach (var c in costs) result.ReorderCost[c.Key] = c.Entries;
        return result;
    }
}

public class MemoryLimitException(string message) : Exception(message);
