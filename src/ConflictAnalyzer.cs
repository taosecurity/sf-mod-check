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
    // --max-memory: stop with MemoryLimitException when managed memory passes this many bytes (0 = no limit).
    public static long MaxManagedBytes;
    // --verbose / --log: one line per record type (records, versions, memory).
    public static Action<string>? Progress;
    // Progress for the person waiting (stderr in sfmodcheck; nothing with --quiet).
    public static Action<string> Info = Console.Error.WriteLine;
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
        foreach (var p in plugins.Where(p => !p.Official))
            foreach (var rec in p.Mod.EnumerateMajorRecords())
                if (rec.FormKey.ModKey != p.Mod.ModKey) targets.TryAdd(rec.FormKey, rec.Registration.GetterType);
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
            catch (Exception) { text = link.FormKey.ToString(); }
            return names[key] = text;
        }
        // INavigationMeshGetter -> NavigationMesh, the record class name the type table uses.
        static string RecordName(Type t) => t.Name.StartsWith('I') && t.Name.EndsWith("Getter") ? t.Name[1..^"Getter".Length] : t.Name;

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
            foreach (var p in plugins)  // load order: origin first, winner last
            {
                try
                {
                    foreach (var rec in p.Mod.EnumerateMajorRecords(type))
                    {
                        if (!targets.TryGetValue(rec.FormKey, out var t) || t != type) continue;
                        if (!found.TryGetValue(rec.FormKey, out var list)) found[rec.FormKey] = list = new();
                        if (list.Count == 0 || list[^1].Plugin != p) list.Add((p, rec));  // one version per plugin
                    }
                }
                catch (Exception e)
                {
                    result.Errors.Add($"{p.FileName}: could not read its {Impact.TypeName(type.Name.TrimStart('I').Replace("Getter", ""))} records ({e.GetType().Name}: {e.Message.Split('\n')[0]})");
                }
            }
            Progress?.Invoke($"  {type.Name.TrimStart('I').Replace("Getter", "")}: {group.Count()} records, {found.Values.Sum(l => l.Count)} versions, managed memory {GC.GetTotalMemory(false) / 1e9:F1} GB");
        foreach (var (formKey, _) in group)
        {
            if (++done % 5000 == 0) Info($"  {done}/{targets.Count}");
            if (MaxManagedBytes > 0 && GC.GetTotalMemory(false) > MaxManagedBytes)
                throw new MemoryLimitException($"memory passed {MaxManagedBytes / 1e9:F1} GB while comparing {type.Name.TrimStart('I').Replace("Getter", "")} records ({done} of {targets.Count} done)");
            if (!found.TryGetValue(formKey, out var versions) || versions.Count < 2) continue;

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
            for (int i = 0; i < n; i++)
                for (int f = 0; f < nf; f++)
                {
                    try { prov[i, f] = FieldCompare.Provides(versions[i].Rec, fields[f], out vals[i, f]); }
                    catch (Exception)
                    {
                        var key = $"{versions[i].Rec.Registration.ClassType.Name}.{fields[f].Name}";
                        result.UnreadableFields[key] = result.UnreadableFields.GetValueOrDefault(key) + 1;
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
                    try { same = FieldCompare.DeepEq(vals[i, f], baseVal, 0); } catch (Exception) { same = true; }
                    if (!same) intended[i].Add(f);
                }
                // Parents (cells, worldspaces, topics) must be copied unchanged to hold child records, so they are not ITMs.
                // A quest is such a parent only when this plugin's copy holds child records (its topics and dialogue lines);
                // a quest copied with nothing under it is still an ITM (2026-11-16; matches sfmodcheck check's itm rule).
                if (intended[i].Count == 0 && !versions[i].Plugin.Official && versions[i].Rec is not (ICellGetter or IWorldspaceGetter or IDialogTopicGetter)
                    && !(versions[i].Rec is IQuestGetter && versions[i].Rec is IMajorRecordGetterEnumerable kids && kids.EnumerateMajorRecords().Any()))
                {
                    var r = versions[i].Rec;
                    result.Itms.Add(new(versions[i].Plugin.FileName, formKey, r.Registration.ClassType.Name, Impact.OneLine(r.EditorID)));
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
                    try { same = FieldCompare.DeepEq(vals[i, f], vals[k, f], 0); } catch (Exception) { same = true; }
                    if (same) continue;
                    lostByPair.TryAdd((i, k), new());
                    // The base game value: the first version that supplies this field (normally the defining plugin).
                    int o = 0;
                    while (o < n - 1 && !prov[o, f]) o++;
                    var field = fields[f].Name;
                    List<Difference> Safe(Func<List<Difference>> make) { try { return make(); } catch (Exception) { return new(); } }
                    string original;
                    try { original = Impact.Describe(vals[o, f], Name); } catch (Exception) { original = "(could not describe)"; }
                    lostByPair[(i, k)].Add(new(field, intended[k].Contains(f),
                        FieldCompare.Show(versions[i].Rec, field), FieldCompare.Show(versions[k].Rec, field),
                        Safe(() => Impact.Differences(field, vals[i, f], vals[k, f], Name)),
                        original,
                        Safe(() => Impact.Changes(field, vals[o, f], vals[i, f], Name)),
                        Safe(() => Impact.Changes(field, vals[o, f], vals[k, f], Name))));
                }
            }
            foreach (var ((i, k), lost) in lostByPair)
            {
                var (loseRec, winRec) = (versions[i].Rec, versions[k].Rec);
                string? recName = null;
                try { recName = versions.Select(v => (v.Rec as ITranslatedNamedGetter)?.Name?.String).LastOrDefault(s => !string.IsNullOrEmpty(s)); recName = recName is null ? null : Impact.OneLine(recName); } catch (Exception) { }
                result.Lost.Add(new(formKey, loseRec.Registration.ClassType.Name, Impact.OneLine(winRec.EditorID ?? loseRec.EditorID), recName,
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
                        try { d = Impact.Differences(fields[f].Name, vals[lt, f], vals[e, f], Name); } catch (Exception) { d = new() { new("?", false) }; }
                        if (d.Count == 0 || d.All(x => x.Minor)) continue;
                        result.ReorderCost.TryAdd((pe.FileName, pl.FileName), new());
                        result.ReorderCost[(pe.FileName, pl.FileName)].Add($"{versions[lt].Rec.Registration.ClassType.Name} {Impact.OneLine(versions[lt].Rec.EditorID ?? formKey.ToString())}: {fields[f].Name}");
                        costAt.TryAdd((pe.FileName, pl.FileName), new());
                        costAt[(pe.FileName, pl.FileName)].Add(order[formKey]);
                    }
                }
        }
        }

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
