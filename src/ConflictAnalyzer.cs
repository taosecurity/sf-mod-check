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
    List<LostField> Fields)
{
    /// <summary>Set by the analyzer (0.10.0 code review): true when the record's own plugin was read in full and holds
    /// no version of it, or isn't loaded at all; false when it holds one. Null when it was read only partly (and in
    /// tests and older callers): judged from Origin, so a possible injection counts as one.</summary>
    public bool? Injected { get; init; }
}

public record ItmRecord(string Plugin, FormKey FormKey, string RecordType, string? EditorID);

// triage (0.8.0): a field one of the `only` plugins changed on purpose, against the version it was built on (Base: the
// latest earlier version from one of its own masters, "" when none supplies the field). Values are short texts.
public record FieldChange(string Plugin, FormKey FormKey, string RecordType, string? EditorID, string Origin, string Base,
    string Field, string BaseValue, string NewValue);

public class Analysis
{
    public int RecordsOverriddenByMods;
    public List<LostChange> Lost { get; } = new();
    public List<ItmRecord> Itms { get; } = new();
    // (earlier, later) -> "Type EditorID: Field" entries the later mod changed on purpose that the earlier one would undo if moved after it.
    public Dictionary<(string Earlier, string Later), List<string>> ReorderCost { get; } = new();
    public Dictionary<string, int> UnreadableFields { get; } = new();
    public List<string> Errors { get; } = new();
    // triage (0.8.0): with Run's `only` set, the fields those plugins changed on purpose (at most MaxChanges; the rest
    // are counted in ChangesNotListed). Empty without it.
    public List<FieldChange> Changes { get; } = new();
    public int ChangesNotListed;
    public const int MaxChanges = 20000;
    // Overrides whose record type differs from the first record with that FormID (0.5.2), in load order.
    public List<TypeChange> TypeChanges { get; } = new();
    // The assets check (0.6.0 part 2): each mod plugin's missing scripts and models, in load order. AssetsChecked is
    // false with --no-assets.
    public bool AssetsChecked;
    public int ArchivesRead, AssetScriptUses, AssetModelUses;
    // Missing path uses not reported because the version the override replaces (in the last of its masters that has
    // the record) names the same path: inherited, often from Bethesda's own records (SWG hand-check 2026-11-18).
    public int AssetsInherited;
    // Why the assets step (or the archive count) stopped part way, else null (code review 2026-11-18: a folder that
    // can't be listed no longer costs the whole report). Also in Errors.
    public string? AssetsFailed;
    // How many of Errors are the assets step's own notes (it stopped, or passed --max-memory in its lookups): the
    // RESULT line counts them apart from read problems, since every record was still compared (code review 0.6.0).
    public int AssetNotes;
    public List<AssetFinding> MissingAssets { get; } = new();
    public List<(string Archive, string Why)> UnreadableArchives { get; } = new();
    // The archive counts per registry (0.6.0 part 2): made with or without --no-assets; ArchivesCounted is false only
    // when the run stopped before them.
    public bool ArchivesCounted;
    public int GnrlArchives, Dx10Archives;
    // Field priorities (0.10.0, conflict only; FieldPriorities.cs): PrioritiesOn is false with --no-priorities and in
    // patch and triage. When on, Lost keeps the normal and critical fields (entries with a critical field first),
    // Benign holds the ignore and benign fields as entries of their own, and CriticalFields marks the critical ones.
    public bool PrioritiesOn;
    public List<LostChange> Benign { get; } = new();
    public HashSet<LostField> CriticalFields { get; } = new(ReferenceEqualityComparer.Instance);
}

/// <summary>A missing script or model, and the plugin whose records use it.</summary>
public record AssetFinding(string Plugin, MissingAsset Asset);

public static class Analyzer
{
    /// <summary>A lost change's Injected answer: false when the owning plugin holds a version, unknown (null) when it
    /// doesn't but the owner (or its version) wasn't read in full, true otherwise (0.10.0 code review rounds 2 and 3).</summary>
    internal static bool? Injection(bool ownerHoldsAVersion, bool ownerNotReadInFull) =>
        ownerHoldsAVersion ? false : ownerNotReadInFull ? null : true;

    // INavigationMeshGetter -> NavigationMesh, IIdleMarkerGetter -> IdleMarker (TrimStart('I') ate every leading I).
    public static string TypeShort(Type t) =>
        t.Name.StartsWith('I') && t.Name.EndsWith("Getter") && t.Name.Length > "IGetter".Length ? t.Name[1..^"Getter".Length] : t.Name;

    // --max-memory: stop with MemoryLimitException when managed memory passes this many bytes (0 = no limit).
    public static long MaxManagedBytes;
    // The managed memory the --max-memory checks compare with it; tests replace it to stop the run at one chosen spot.
    public static Func<long> ManagedBytes = () => GC.GetTotalMemory(false);
    // One guard and one message for every --max-memory check (review 0.5.4: four hand-kept copies).
    static bool OverMemory() => MaxManagedBytes > 0 && ManagedBytes() > MaxManagedBytes;
    // The same test for steps after the analysis (the assets check's lookups, code review 2026-11-18).
    public static bool OverMemoryLimit() => OverMemory();
    static MemoryLimitException MemoryStop(string doing) => new($"memory passed {MaxManagedBytes / 1e9:F1} GB while {doing}");
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

    // Which errors a catch may carry on past is one rule for the whole program since 0.5.2: Fatal.Recoverable (Fatal.cs).

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

    /// <param name="only">triage (0.8.0): when given (plugin file names), only the records these plugins override are
    /// compared, and the fields they changed on purpose are listed in Analysis.Changes. Null: every mod's overrides, as
    /// before, and no Changes.</param>
    /// <param name="onlyRecords">triage: when given, of those overrides only these records are compared (triage reads
    /// records that live in cells and worldspaces raw instead: their Mutagen reads kept gigabytes, NAPGR 0.1.6).</param>
    /// <param name="officialTypes">patch --include-official (0.10.0): when given, a change one official plugin loses to
    /// another is kept too (normally "Bethesda vs Bethesda is not our business"), and records that only official plugins
    /// override are compared as well, for the record types this accepts (the ones patch can build). Ignored with `only`.
    /// Null (conflict, triage, a default patch): exactly the 0.9.1 analysis.</param>
    public static Analysis Run(List<PluginInfo> plugins, ISet<string>? only = null, ISet<FormKey>? onlyRecords = null,
        Func<Type, bool>? officialTypes = null)
    {
        bool withOfficial = officialTypes is not null && only is null;
        var result = new Analysis();
        var onlySet = only is null ? null : new HashSet<string>(only, StringComparer.OrdinalIgnoreCase);
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
        // A plugin's direct masters and itself: a record's FormID can only name one of these.
        static HashSet<ModKey> DirectAndSelf(PluginInfo p) => p.Masters.Select(m => ModKey.TryFromFileName(m, out var mk) ? mk : (ModKey?)null)
            .OfType<ModKey>().Append(p.Mod.ModKey).ToHashSet();

        // Every record that at least one mod overrides.
        var targets = new Dictionary<FormKey, Type>();
        // The other record types later mods give the same FormID (0.5.2; rare). Until 0.5.1 only the first override's type
        // was kept, so an ordinary override after a type-changing one was never compared either.
        var otherTypes = new Dictionary<FormKey, HashSet<Type>>();
        // The mods whose records Mutagen could not all read (0.5.3: tells the record-type pass's unread plugins that
        // can't hold a record apart from those that may).
        var collectFailed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddTargets(PluginInfo p, IEnumerable<IMajorRecordGetter> recs)
        {
            foreach (var rec in recs)
            {
                if (rec.FormKey.ModKey == p.Mod.ModKey) continue;
                if (onlyRecords is not null && !onlyRecords.Contains(rec.FormKey)) continue;
                var type = rec.Registration.GetterType;
                if (!targets.TryAdd(rec.FormKey, type) && targets[rec.FormKey] != type)
                {
                    if (!otherTypes.TryGetValue(rec.FormKey, out var more)) otherTypes[rec.FormKey] = more = new();
                    more.Add(type);
                }
            }
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
            var why = Impact.Summary(e);
            var at = RecordOf(e);
            var plugin = p.FileName.ToLowerInvariant();
            if (!reportedTypes.Add((plugin, what)) | (at.Length > 0 && !reported.Add((plugin, at, why)))) return;
            result.Errors.Add($"{p.FileName}: could not read its {what} records{(at.Length > 0 ? $" (at {at})" : "")}, so its changes to them were not checked ({why})");
        }
        foreach (var p in plugins.Where(p => !p.Official && (onlySet is null || onlySet.Contains(p.FileName))))
        {
            // One unreadable record used to stop the whole run (2026-11-17, a 157-plugin load order: a generic base form
            // with a component type Mutagen 0.54.4 can't parse). Plugins that read cleanly take the single
            // pass as before; otherwise each top-level group is read on its own and only the unreadable group is skipped,
            // listed under "Tool limits hit". Records read before the failure stay (TryAdd keeps them once).
            Where = $"reading {p.FileName}";
            try { AddTargets(p, p.Mod.EnumerateMajorRecords()); }
            catch (Exception err) when (Fatal.Recoverable(err))
            {
                collectFailed.Add(p.FileName);
                foreach (var prop in p.Mod.GetType().GetProperties())
                {
                    if (prop.GetIndexParameters().Length > 0) continue;
                    var what = GroupName(prop);
                    object? val;
                    try { val = prop.GetValue(p.Mod); }
                    catch (Exception e) when (Fatal.Recoverable(e))
                    {
                        if (typeof(IMajorRecordGetterEnumerable).IsAssignableFrom(prop.PropertyType)) ReadProblem(p, what, e);
                        continue;
                    }
                    if (val is not IMajorRecordGetterEnumerable group || val is IMajorRecordGetter || val is IModGetter) continue;
                    try { AddTargets(p, group.EnumerateMajorRecords()); }
                    catch (Exception e) when (Fatal.Recoverable(e)) { ReadProblem(p, what, e); }
                }
            }
        }
        result.RecordsOverriddenByMods = targets.Count;
        Info($"{targets.Count} records overridden by mods");
        Progress?.Invoke($"{targets.Count} records overridden by mods, in {targets.Values.Concat(otherTypes.Values.SelectMany(t => t)).Distinct().Count()} record types");

        // Overrides that give a record another type (0.5.2): the passes below read one record type at a time, so they
        // can't see them; read the record headers raw instead.
        Where = "comparing record types";
        var typeWatch = System.Diagnostics.Stopwatch.StartNew();
        // A plugin's record-type problem is kept aside and placed after both passes: merged into that plugin's own
        // "could not read" line when there is one (one line per plugin, as 0.5.1 lists read problems), else on its own
        // (code review 0.5.2, rounds 1-2).
        var typeProblems = new List<(PluginInfo Plugin, string Text, string Short)>();
        result.TypeChanges.AddRange(TypeChanges.Find(plugins, targets, (p, text, shortText) => typeProblems.Add((p, text, shortText))));
        // the first record with each type-changed FormID: its plugin and its record type, as the raw pass read them
        var firstHolder = new Dictionary<FormKey, (PluginInfo Plugin, string Sig)>();
        foreach (var tc in result.TypeChanges) if (byName.TryGetValue(tc.First, out var fp)) firstHolder.TryAdd(tc.FormKey, (fp, tc.FirstType));
        // Plugins the record-type pass read only partly or not at all (0.5.3): one of them may hold, with another type, the
        // first record of a FormID, which the game keeps, so later versions would be compared against the wrong base.
        var typeUnread = typeProblems.Select(t => t.Plugin).DistinctBy(p => p.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(p => (Plugin: p, Holds: DirectAndSelf(p))).ToList();
        // Whether an unread plugin may hold this record: Mutagen saw it hold it, or Mutagen couldn't read it all either,
        // or it's an official file or the record's own plugin (code review 0.5.3). The overrides it holds are read
        // through Mutagen once, the first time it's asked, so only these few plugins cost anything (review round 2).
        var heldBy = new Dictionary<string, HashSet<FormKey>?>(StringComparer.OrdinalIgnoreCase);
        bool MayHold(PluginInfo p, FormKey fk)
        {
            if (p.Official || collectFailed.Contains(p.FileName) || p.Mod.ModKey == fk.ModKey) return true;
            if (!heldBy.TryGetValue(p.FileName, out var held))
            {
                // A whole-plugin read: named for a run stopped here, and held to --max-memory like the other reads,
                // during the read too (0.5.4 item 6; review: a check only after it came too late for a big plugin). The
                // caller's label comes back afterwards; a stop leaves this one in place.
                var (wasWhere, wasRecord) = (where, whereRecord);
                Where = $"reading {p.FileName} to see which records it overrides";
                void CheckMemory()
                {
                    if (OverMemory()) throw MemoryStop($"reading {p.FileName} to see which records it overrides");
                }
                try
                {
                    held = new HashSet<FormKey>();
                    int n = 0;
                    foreach (var r in p.Mod.EnumerateMajorRecords())
                    {
                        if (r.FormKey.ModKey != p.Mod.ModKey) held.Add(r.FormKey);
                        if (++n % 4096 == 0) CheckMemory();   // MemoryLimitException isn't recoverable: it passes the catch
                    }
                }
                catch (Exception e) when (Fatal.Recoverable(e)) { held = null; }   // can't tell: it may hold it
                heldBy[p.FileName] = held;
                CheckMemory();
                (where, whereRecord) = (wasWhere, wasRecord);
            }
            return held is null || held.Contains(fk);
        }
        Progress?.Invoke($"record types compared in {typeWatch.Elapsed.TotalSeconds:F1} s: {result.TypeChanges.Count} overrides change a record's type");

        // patch --include-official: records that only official plugins override, where a later official version doesn't
        // have an earlier overriding one among its masters (so it may drop that one's change: the 0.9.0 example is
        // SFBGS003TrackersAllianceHQLocation, sfta06 vs sfta02). Added after the record-type pass, which is about mods.
        if (withOfficial)
        {
            Where = "finding records official plugins override";
            var officialOverrides = new Dictionary<FormKey, (Type Type, List<PluginInfo> By)>();
            foreach (var p in plugins.Where(p => p.Official && p.Masters.Count > 0))
            {
                try
                {
                    foreach (var rec in p.Mod.EnumerateMajorRecords())
                    {
                        if (rec.FormKey.ModKey == p.Mod.ModKey || targets.ContainsKey(rec.FormKey)) continue;
                        var type = rec.Registration.GetterType;
                        if (!officialTypes!(type)) continue;
                        if (!officialOverrides.TryGetValue(rec.FormKey, out var e)) officialOverrides[rec.FormKey] = e = (type, new());
                        else if (e.Type != type) continue;   // a type change between official files: left alone
                        if (e.By.Count == 0 || e.By[^1] != p) e.By.Add(p);
                    }
                }
                catch (Exception e) when (Fatal.Recoverable(e)) { ReadProblem(p, "official override", e); }
                if (OverMemory()) throw MemoryStop($"finding the records {p.FileName} overrides");
            }
            int added = 0;
            foreach (var (fk, (type, by)) in officialOverrides)
            {
                bool mayLose = false;
                for (int b = 1; b < by.Count && !mayLose; b++)
                    for (int a = 0; a < b && !mayLose; a++)
                        mayLose = !MastersOf(by[b]).Contains(by[a].Mod.ModKey);
                if (mayLose && targets.TryAdd(fk, type)) added++;
            }
            Info($"--include-official: {added} records only official plugins override, where one may drop another's change");
            Progress?.Invoke($"--include-official: {officialOverrides.Count} records only official plugins override, {added} compared");
        }

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
            catch (Exception err) when (Fatal.Recoverable(err)) { text = link.FormKey.ToString(); }
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
        // (record, type) pairs: a record that mods give two types is compared once as each
        var pairs = targets.Select(t => (t.Key, t.Value)).Concat(otherTypes.SelectMany(o => o.Value.Select(t => (o.Key, t)))).ToList();
        bool IsTarget(FormKey fk, Type type) => targets.TryGetValue(fk, out var t) && (t == type || (otherTypes.TryGetValue(fk, out var more) && more.Contains(type)));
        foreach (var group in pairs.GroupBy(t => t.Item2).OrderBy(g => g.Key.Name, StringComparer.Ordinal))
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
                        if (!IsTarget(rec.FormKey, type)) continue;
                        if (!found.TryGetValue(rec.FormKey, out var list)) found[rec.FormKey] = list = new();
                        if (list.Count == 0 || list[^1].Plugin != p) list.Add((p, rec));  // one version per plugin
                    }
                }
                catch (Exception e) when (Fatal.Recoverable(e)) { ReadProblem(p, Impact.TypeName(TypeShort(type)), e); partly.Add(p); }
                // also between plugins while a record type is read, so --max-memory holds during the read itself (0.5.0)
                if (OverMemory()) throw MemoryStop($"reading {TypeShort(type)} records from {p.FileName} ({done} of {pairs.Count} done)");
            }
            Progress?.Invoke($"  {TypeShort(type)}: {group.Count()} records, {found.Values.Sum(l => l.Count)} versions, managed memory {GC.GetTotalMemory(false) / 1e9:F1} GB");
            // A plugin read only partly may hold an unread version of a record (code review). A plugin can only override
            // records of its own masters, so it matters only for those. Those records are skipped and counted, or compared
            // with the plugin named (MayHoldUnread below), never reported wrongly.
            var notCompared = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // by file name: hashing a PluginInfo record hashes its whole plugin through Mutagen
            var notComparedType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // the same, for the record-type pass
            var comparedWithout = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // compared, but a partly-read plugin's own change can't be checked (0.5.4 item 2)
            // direct masters only: a record's FormID can only name the plugin itself or one of its own masters
            var partlyMasters = partly.Select(DirectAndSelf).ToList();
            // This pass's record type(s), e.g. KYWD. Mutagen gives every record type a signature (0.5.4 item 5:
            // ConflictUnreadTests checks every Starfield record type), so the no-signature branch of 0.5.3 was dead code
            // and is gone; a Mutagen update that breaks this stops the run with a clear message instead of guessing.
            var groupSigs = Checker.SigsOf(type) ?? throw new InvalidOperationException(
                $"sf-mod-check does not know the record signature of {TypeShort(type)} (a Mutagen change?), so it can't tell which of its versions the game drops");
            // A partly-read plugin that may override this record (the record's FormID names it or one of its direct
            // masters) and whose version wasn't read. Where its version would win, or be the base of a later version (a
            // master of it), the record's results would be wrong: skip and count it (Skip). The same when only partial
            // forms load after it: a partial form supplies only some fields, so the unread version may still win the
            // others (review round 2). Elsewhere, with a full version after it and nothing later depending on it, the
            // read versions' results stand (a later full version beats it on every field), but
            // its own entries (a change lost to a later plugin, an ITM, a reorder cost) can't be seen. Before 0.5.4 that
            // went unsaid (item 2); now the record is compared and each such plugin is named (Middle). Skipping these too
            // hid other plugins' real findings (review round 1).
            // Round 3: the test is whether a full version loads after it (that one beats it on every field, whatever
            // partial forms sit between), and every plugin that forces a skip is named, not only the first. Lists are
            // made only when needed: nearly every record matches no partly-read plugin.
            (List<PluginInfo>? Skip, List<PluginInfo>? Middle) MayHoldUnread(FormKey fk, List<(PluginInfo Plugin, IMajorRecordGetter Rec)>? vs)
            {
                List<PluginInfo>? skip = null, middle = null;
                for (int u = 0; u < partly.Count; u++)
                {
                    var p = partly[u];
                    if (!partlyMasters[u].Contains(fk.ModKey)) continue;   // it can't override this record
                    // one pass over the read versions: p's own read version, a full version after it, a later one built on it
                    bool read = false, beaten = false, builtOn = false;
                    if (vs is not null)
                        foreach (var v in vs)
                        {
                            if (ReferenceEquals(v.Plugin, p)) { read = true; break; }
                            if (v.Plugin.Index <= p.Index) continue;
                            if (!beaten) beaten = !FieldCompare.IsPartial(v.Rec);
                            if (!builtOn) builtOn = MastersOf(v.Plugin).Contains(p.Mod.ModKey);
                        }
                    if (read) continue;   // its version was read before the failure
                    if (!beaten || builtOn)
                        (skip ??= new()).Add(p);
                    else
                        (middle ??= new()).Add(p);
                }
                return (skip, middle);
            }
            Where = $"comparing {TypeShort(type)} records";
        foreach (var (formKey, _) in group)
        {
            whereRecord = null;   // a skipped record must not leave the previous one named (code review)
            if (++done % 5000 == 0) Info($"  {done}/{pairs.Count}");
            if (OverMemory()) throw MemoryStop($"comparing {TypeShort(type)} records ({done} of {pairs.Count} done)");
            found.TryGetValue(formKey, out var versions);
            if (versions is { Count: > 0 })
            {
                // The first record with this FormID is the earlier of the record-type pass's first holder and this
                // type's first version (Mutagen may see a plugin the raw pass could not read).
                var hasKeeper = firstHolder.TryGetValue(formKey, out var keeper);
                var first = hasKeeper && keeper.Plugin.Index < versions[0].Plugin.Index ? keeper.Plugin : versions[0].Plugin;
                // Unless that is the record's own plugin, an earlier plugin the record-type pass couldn't read may hold
                // the first record (of this type or another), so the base is unknown: counted, not compared (0.5.3).
                // Only records with two or more versions are compared, so only those are counted (review round 2: a
                // record's single version was counted too).
                if (versions.Count > 1 && typeUnread.Count > 0 && first.Mod.ModKey != formKey.ModKey
                    && typeUnread.FirstOrDefault(t => t.Plugin.Index < first.Index && t.Holds.Contains(formKey.ModKey) && MayHold(t.Plugin, formKey)).Plugin is { } maybeFirst)
                {
                    notComparedType[maybeFirst.FileName] = notComparedType.GetValueOrDefault(maybeFirst.FileName) + 1;
                    continue;
                }
                // A record given another type: the game keeps the first record and drops the others, so versions of
                // another type are not compared (code review 0.5.2: the first type-changing mod was taken as the base,
                // giving ITMs and lost changes against the wrong record). Dropped when the first record's type, as the
                // raw pass read it, isn't this pass's type (review rounds 1-2: judged from the type, not from which
                // plugins the raw pass happened to read). The game drops every version of this type, unread ones too,
                // so nothing is counted either. Armor and armor addon may replace each other: not dropped.
                if (hasKeeper && ReferenceEquals(first, keeper.Plugin) && !ReferenceEquals(first, versions[0].Plugin))
                {
                    if (!groupSigs.Contains(keeper.Sig) && !groupSigs.Any(g => TypeChanges.ArmorPair(g, keeper.Sig))) continue;
                }
            }
            // A version that may be unread is counted, never dropped silently (code review 0.5.2 round 3).
            List<PluginInfo>? middles = null;   // counted once the record's results are kept (review round 2)
            if (partly.Count > 0)
            {
                var (skip, middle) = MayHoldUnread(formKey, versions);
                // Counted even with fewer than two read versions (0.5.3's rule; review round 4): the unread versions may
                // themselves be the ones that clash, so the record can't be called conflict-free.
                // Nothing to compare: not compared. Rare (a middle needs a read full version after it, so this takes a base
                // that is itself partly read), but then nothing else would name the plugin.
                if (skip is null && middle is not null && versions is not { Count: > 1 }) skip = middle;
                if (skip is not null)
                {
                    foreach (var s in skip) notCompared[s.FileName] = notCompared.GetValueOrDefault(s.FileName) + 1;
                    continue;
                }
                middles = middle;
            }
            if (versions is null || versions.Count < 2) continue;
            whereRecord = (formKey, type, versions);
            // This record's results are kept only once all of it is worked out (code review): a failure part way used to
            // leave some entries behind, and a reorder-cost key without its position, which stopped the final sort.
            var recLost = new List<LostChange>();
            var recItms = new List<ItmRecord>();
            var recCost = new List<((string Earlier, string Later) Pair, string Text)>();
            var recUnreadable = new List<string>();
            var recChanges = new List<FieldChange>();   // only with `only` (triage)
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
                    catch (Exception err) when (Fatal.Recoverable(err))
                    {
                        unsure[i] = true;
                        Unreadable(versions[i].Rec, fields[f].Name);
                    }
                }
            // A middle plugin is safe to leave out only if a later full version beats it on every field; one whose field
            // couldn't be read may not (the field's winner falls back to an earlier version). Then the record is not
            // compared after all, and counted as such (review round 4).
            if (middles is not null)
            {
                int lastSure = -1;   // the load index of the last full version whose fields all read
                for (int i = n - 1; i >= 0 && lastSure < 0; i--)
                    if (!FieldCompare.IsPartial(versions[i].Rec) && !unsure[i]) lastSure = versions[i].Plugin.Index;
                if (middles.Any(p => p.Index > lastSure))
                {
                    // every middle plugin is named: the record isn't compared, and each of them may change it (round 5)
                    foreach (var s in middles) notCompared[s.FileName] = notCompared.GetValueOrDefault(s.FileName) + 1;
                    continue;   // nothing of this record is kept (its results are added only at the end)
                }
            }

            // The version the record starts from: its own plugin's, normally versions[0]. A record that a BlueprintShips
            // file adds loads after the mods that edit it (they have that file as a master, but the game loads it after
            // every mod; 0.12.0), so the earliest copy is a mod's. Then the BlueprintShips file's version is the base,
            // and the mods before it are judged against it (0.12.0 step 3). Elsewhere a master always loads before its
            // plugin, so `origin` is 0 and nothing changes.
            int origin = 0;
            for (int j = 1; j < n && origin == 0; j++)
                if (versions[j].Plugin.Mod.ModKey == formKey.ModKey && versions[0].Plugin.Mod.ModKey != formKey.ModKey
                    && LoadOrderSetup.IsBlueprint(versions[j].Plugin.FileName)) origin = j;
            // What each version meant to change: fields that differ from the latest earlier value among its own masters
            // (for a version before `origin`, the origin's value; for a version with a BlueprintShips master that loads
            // after it, that master's value).
            var intended = new HashSet<int>[n];
            for (int i = 0; i < n; i++)
            {
                intended[i] = new();
                if (i == origin) continue;
                var masters = MastersOf(versions[i].Plugin);
                for (int f = 0; f < nf; f++)
                {
                    if (!prov[i, f]) continue;
                    int b = -1;
                    // A version whose master is a BlueprintShips file that loads after it (it overrides a record that file
                    // also overrides, or one that file adds): its author saw that file's value, so it is the base (0.12.0
                    // code review). Of several such masters, the one that loads last.
                    for (int j = n - 1; j > i && b < 0; j--)
                        if (prov[j, f] && LoadOrderSetup.IsBlueprint(versions[j].Plugin.FileName) && masters.Contains(versions[j].Plugin.Mod.ModKey)) b = j;
                    if (b < 0 && i < origin) { if (prov[origin, f]) b = origin; }
                    else
                        for (int j = i - 1; j >= 0 && b < 0; j--)
                            if (prov[j, f] && masters.Contains(versions[j].Plugin.Mod.ModKey)) b = j;
                    var baseVal = b >= 0 ? vals[b, f] : null;
                    bool same;
                    try { same = FieldCompare.DeepEq(vals[i, f], baseVal, 0); }
                    catch (Exception err) when (Fatal.Recoverable(err)) { same = true; unsure[i] = true; Unreadable(versions[i].Rec, fields[f].Name); }
                    if (!same) intended[i].Add(f);
                    if (!same && onlySet is not null && onlySet.Contains(versions[i].Plugin.FileName))
                    {
                        var r = versions[i].Rec;
                        recChanges.Add(new(versions[i].Plugin.FileName, formKey, r.Registration.ClassType.Name, Impact.OneLine(r.EditorID),
                            versions[origin].Plugin.FileName, b >= 0 ? versions[b].Plugin.FileName : "", fields[f].Name,
                            b >= 0 ? FieldCompare.Show(versions[b].Rec, fields[f].Name, 160) : "(not set)", FieldCompare.Show(r, fields[f].Name, 160)));
                    }
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
                    if (loser.Official && winner.Official && !withOfficial) continue;   // Bethesda vs Bethesda is not our business (unless patch --include-official)
                    if (winnerMasters.Contains(loser.Mod.ModKey)) continue;   // winner saw this plugin and decided
                    bool same;
                    try { same = FieldCompare.DeepEq(vals[i, f], vals[k, f], 0); }
                    catch (Exception err) when (Fatal.Recoverable(err)) { same = true; Unreadable(versions[i].Rec, fields[f].Name); }   // counted in Tool limits (0.5.0)
                    if (same) continue;
                    lostByPair.TryAdd((i, k), new());
                    // The base game value: the first version that supplies this field (normally the defining plugin).
                    // A record a BlueprintShips file adds: that file's own value (0.12.0).
                    int o = 0;
                    if (origin > 0 && prov[origin, f]) o = origin;
                    else while (o < n - 1 && !prov[o, f]) o++;
                    var field = fields[f].Name;
                    // A part of the value that can't be read while it is described is counted once in Tool limits
                    // (0.5.1), not dropped silently.
                    bool partUnreadable = false;
                    void Mark() => partUnreadable = true;
                    List<Difference> Safe(Func<List<Difference>> make) { try { return make(); } catch (Exception err) when (Fatal.Recoverable(err)) { Mark(); return new(); } }
                    var outerMark = Overrides.OnUnreadable;
                    Overrides.OnUnreadable = Mark;   // also land flags shown as "unreadable" in the values (code review)
                    try
                    {
                        string original;
                        try { original = Impact.Describe(vals[o, f], Name); } catch (Exception err) when (Fatal.Recoverable(err)) { Mark(); original = "(could not describe)"; }
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
            // Injected (field priorities make every field critical): the owning plugin holds no version. False when the
            // owner holds a version (of this type or, as the first holder, another one; 0.10.0 code review round 1).
            // Unknown (null) when the owner was read only partly or the raw pass couldn't read it: then the first
            // version decides, so a possible injection stays critical and is never shown apart as benign (round 2).
            bool? injected = null;
            if (lostByPair.Count > 0)
            {
                var owner = formKey.ModKey;
                injected = Injection(
                    origin > 0 || versions[0].Plugin.Mod.ModKey == owner || (firstHolder.TryGetValue(formKey, out var holder) && holder.Plugin.Mod.ModKey == owner),
                    partly.Any(p => p.Mod.ModKey == owner) || typeUnread.Any(t => t.Plugin.Mod.ModKey == owner));
            }
            foreach (var ((i, k), lost) in lostByPair)
            {
                var (loseRec, winRec) = (versions[i].Rec, versions[k].Rec);
                string? recName = null;
                try { recName = versions.Select(v => (v.Rec as ITranslatedNamedGetter)?.Name?.String).LastOrDefault(s => !string.IsNullOrEmpty(s)); recName = recName is null ? null : Impact.OneLine(recName); } catch (Exception err) when (Fatal.Recoverable(err)) { }
                recLost.Add(new(formKey, loseRec.Registration.ClassType.Name, Impact.OneLine(winRec.EditorID ?? loseRec.EditorID), recName,
                    versions[origin].Plugin.FileName, versions[i].Plugin.FileName, versions[i].Plugin.Official,
                    versions[k].Plugin.FileName, versions[k].Plugin.Official, lost) { Injected = injected });
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
                        try { d = Impact.Differences(fields[f].Name, vals[lt, f], vals[e, f], Name); } catch (Exception err) when (Fatal.Recoverable(err)) { d = new() { new("?", false) }; }
                        if (d.Count == 0 || d.All(x => x.Minor)) continue;
                        recCost.Add(((pe.FileName, pl.FileName), $"{versions[lt].Rec.Registration.ClassType.Name} {Impact.OneLine(versions[lt].Rec.EditorID ?? formKey.ToString())}: {fields[f].Name}"));
                    }
                }

            result.Lost.AddRange(recLost);
            result.Itms.AddRange(recItms);
            foreach (var ch in recChanges)
                if (result.Changes.Count < Analysis.MaxChanges) result.Changes.Add(ch); else result.ChangesNotListed++;
            if (middles is not null)
                foreach (var m in middles) comparedWithout[m.FileName] = comparedWithout.GetValueOrDefault(m.FileName) + 1;
            foreach (var (pair, text) in recCost)
            {
                result.ReorderCost.TryAdd(pair, new());
                result.ReorderCost[pair].Add(text);
                costAt.TryAdd(pair, new());
                costAt[pair].Add(order[formKey]);
            }
            }
            // --max-memory and running out of memory still stop the run (Fatal.Recoverable), as they did before this catch existed
            catch (Exception e) when (Fatal.Recoverable(e))
            {
                result.Errors.Add($"{formKey} ({Impact.TypeName(TypeShort(type))}): could not compare its versions in {string.Join(", ", versions.Select(v => v.Plugin.FileName))}, " +
                                  $"so it was skipped ({Impact.Summary(e)})");
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
            foreach (var (p, count) in comparedWithout)
                result.Errors.Add($"{p}: {count} other {Impact.TypeName(TypeShort(type))} record{(count == 1 ? " was" : "s were")} compared without its version: " +
                                  $"{p} may change {(count == 1 ? "it" : "them")} too, but its {Impact.TypeName(TypeShort(type))} records could not all be read, " +
                                  $"so its own entries (a change a later plugin overrides, an ITM, a reorder cost) are not listed");
            foreach (var (p, count) in notComparedType)
                result.Errors.Add($"{p}: {count} other {Impact.TypeName(TypeShort(type))} record{(count == 1 ? " was" : "s were")} not compared, " +
                                  $"because {p} may hold the first version of {(count == 1 ? "it" : "them")} as another record type, but its records' types could not all be read");
        }
        foreach (var (p, text, shortText) in typeProblems)
        {
            var own = result.Errors.FindIndex(e => e.StartsWith($"{p.FileName}: could not read its ", StringComparison.Ordinal));
            if (own >= 0) result.Errors[own] += $"; {shortText}";
            else result.Errors.Add(text);
        }
        Where = "sorting the results";   // the command sets the next step

        // Records were worked through one type at a time; put the results back in the order the records were found,
        // so reports list them (and their first examples) as before.
        var lostInOrder = result.Lost.OrderBy(l => order[l.FormKey]).ToList();
        result.Lost.Clear(); result.Lost.AddRange(lostInOrder);
        var itms = result.Itms.OrderBy(t => order[t.FormKey]).ToList();
        result.Itms.Clear(); result.Itms.AddRange(itms);
        if (result.Changes.Count > 0)
        {
            var changes = result.Changes.OrderBy(t => order[t.FormKey]).ToList();   // stable: a record's fields keep their order
            result.Changes.Clear(); result.Changes.AddRange(changes);
        }
        var costs = result.ReorderCost.Select(kv => (kv.Key, Entries: kv.Value.Zip(costAt[kv.Key]).OrderBy(z => z.Second).Select(z => z.First).ToList(), First: costAt[kv.Key].Min()))
            .OrderBy(c => c.First).ToList();
        result.ReorderCost.Clear();
        foreach (var c in costs) result.ReorderCost[c.Key] = c.Entries;
        return result;
    }
}

public class MemoryLimitException(string message) : Exception(message);
