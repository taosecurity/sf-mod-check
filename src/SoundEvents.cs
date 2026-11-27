// The Wwise sound-event check's record side (0.23.0, step 2; design: SmcLookup\wwise-research NOTES.md, "Proposed
// check design"). Every SoundReference (its Start and Stop GUIDs) in a record, and a WWED's own Start and End, are the
// sound GUIDs a record names. The walk follows Mutagen 0.54.4's getter types through a per-type plan: only the
// properties whose declared type can lead to a SoundReference are read, so a record without sounds costs one cached
// lookup and child records (a quest's scenes and topics, a cell's references) are left to their own turn. The bank
// side is SoundBanks.cs; the wiring is Assets.Find (kind "sound-event").
using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;

namespace SfModCheck;

public static class SoundRefs
{
    /// <summary>Every non-zero sound GUID a record names, with its field path ("MTSH.Start", "Sounds[2].Sound.Stop",
    /// "WWED.End"). A field the plan can't read is skipped and logged.</summary>
    public static List<(string Field, Guid Guid)> Guids(IMajorRecordGetter rec)
    {
        var list = new List<(string, Guid)>();
        if (rec is IWWiseEventDataGetter w)
        {
            if (w.Start is { } s && s != Guid.Empty) list.Add(("WWED.Start", s));
            if (w.End is { } e && e != Guid.Empty) list.Add(("WWED.End", e));
        }
        Walk(rec, "", list, 0);
        return list;
    }

    /// <summary>Whether this record type can hold a sound GUID at all (a WWED, or a SoundReference somewhere in its
    /// fields). Cached per type.</summary>
    public static bool MayHaveSounds(IMajorRecordGetter rec) => rec is IWWiseEventDataGetter || Plan(rec.GetType()).Length > 0;

    /// <summary>A single-track music record (MUST) that plays nothing: no start event and no event mapping.</summary>
    public static bool IsSilentTrack(IMajorRecordGetter rec) =>
        rec is IMusicTrackGetter t && t.Type == MusicTrack.TypeEnum.SingleTrack &&
        (t.MTSH is not { } s || s.Start == Guid.Empty && s.EventMapping.IsNull);

    static void Walk(object o, string path, List<(string, Guid)> list, int depth)
    {
        if (depth > 10) return;
        if (o is ISoundReferenceGetter sr)
        {
            if (sr.Start != Guid.Empty) list.Add((Join(path, "Start"), sr.Start));
            if (sr.Stop != Guid.Empty) list.Add((Join(path, "Stop"), sr.Stop));
            return;
        }
        foreach (var p in Plan(o.GetType()))
        {
            object? v;
            try { v = p.GetValue(o); }
            catch (Exception e) when (Fatal.Recoverable(e) || e is TargetInvocationException) { Log.Write($"sound events: field {Join(path, p.Name)} couldn't be read ({SafeText.OneLine((e.InnerException ?? e).Message)})"); continue; }
            if (v is null) continue;
            var here = Join(path, p.Name);
            if (v is ISoundReferenceGetter || v is not IEnumerable) { Walk(v, here, list, depth + 1); continue; }
            int i = 0;
            foreach (var item in (IEnumerable)v)
            {
                if (item is not null && item is not IMajorRecordGetter && item is not IFormLinkGetter) Walk(item, $"{here}[{i}]", list, depth + 1);
                i++;
            }
        }
    }

    static string Join(string path, string name) => path.Length == 0 ? name : path + "." + name;

    // ---------------------------------------------------------------- the per-type plan

    static readonly ConcurrentDictionary<Type, PropertyInfo[]> plans = new();
    static readonly ConcurrentDictionary<Type, bool> reach = new();
    static readonly Assembly starfield = typeof(StarfieldMod).Assembly;
    static readonly Lazy<Type[]> concrete = new(() =>
    {
        try { return starfield.GetTypes().Where(t => t.IsClass && !t.IsAbstract && t.Namespace == "Mutagen.Bethesda.Starfield").ToArray(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t is { IsClass: true, IsAbstract: false, Namespace: "Mutagen.Bethesda.Starfield" }).Cast<Type>().ToArray(); }
    });

    /// <summary>The properties of a runtime type that can lead to a SoundReference.</summary>
    internal static PropertyInfo[] Plan(Type t) => plans.GetOrAdd(t, t =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && p.Name is not ("Registration" or "StaticRegistration"))
            .Where(p => Reaches(p.PropertyType)).ToArray());

    /// <summary>Whether a declared type can hold a SoundReference: it is one, a list of a type that can, or a Mutagen
    /// Starfield type (or a class that implements it) with a property that can. Links and child records can't.</summary>
    internal static bool Reaches(Type t)
    {
        if (typeof(ISoundReferenceGetter).IsAssignableFrom(t)) return true;
        if (reach.TryGetValue(t, out var known)) return known;
        lock (reachLock)
        {
            if (reach.TryGetValue(t, out known)) return known;
            if (!inProgress.Add(t)) { cycleHit = true; return false; }   // a cycle counts as no for now
            var outer = cycleHit;
            cycleHit = false;
            var result = Compute(t);
            inProgress.Remove(t);
            // a "no" that leaned on a type still being worked out may be wrong, so it is only kept at the top
            if (result || !cycleHit || inProgress.Count == 0) reach[t] = result;
            cycleHit |= outer;
            return result;
        }
    }

    static readonly object reachLock = new();
    static readonly HashSet<Type> inProgress = new();
    static bool cycleHit;

    static bool Compute(Type t)
    {
        bool result;
        if (t.IsValueType || t == typeof(string) || typeof(IFormLinkGetter).IsAssignableFrom(t) || typeof(IMajorRecordGetter).IsAssignableFrom(t)) result = false;
        else if (Element(t) is { } el) result = Reaches(el);
        else if (t.Namespace != "Mutagen.Bethesda.Starfield") result = false;
        else
        {
            var own = t.IsInterface ? t.GetInterfaces().Prepend(t).SelectMany(i => i.GetProperties()) : t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            result = own.Where(p => p.GetIndexParameters().Length == 0 && p.Name is not ("Registration" or "StaticRegistration")).Any(p => Reaches(p.PropertyType));
            // an abstract or interface type (a scene's actions, a perk's effects): the classes that implement it count
            if (!result && (t.IsInterface || t.IsAbstract))
                result = concrete.Value.Where(c => c != t && t.IsAssignableFrom(c))
                    .Any(c => c.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .Any(p => p.GetIndexParameters().Length == 0 && p.Name is not ("Registration" or "StaticRegistration") && Reaches(p.PropertyType)));
        }
        return result;
    }

    static Type? Element(Type t)
    {
        if (!typeof(IEnumerable).IsAssignableFrom(t) || t == typeof(string)) return null;
        var e = (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>) ? t : null)
                ?? t.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return e?.GetGenericArguments()[0] ?? typeof(object);
    }

    // ---------------------------------------------------------------- the masters' GUIDs

    static readonly ConditionalWeakTable<IModGetter, HashSet<UInt128>> masterSets = new();

    /// <summary>How many times each plugin's GUIDs were really read (a test hook: release reads each master once per run).</summary>
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> Reads = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every sound GUID (in the manifest's form) in a plugin's records, read once per plugin object per run.
    /// The master-GUID rule: a GUID a master's own record names isn't the mod's fault, even when no bank holds it
    /// (the 17 dangling GUIDs in Bethesda's data, and add-on events Starfield.esm names).</summary>
    public static HashSet<UInt128> AllGuids(IModGetter mod)
    {
        lock (masterSets)
        {
            if (masterSets.TryGetValue(mod, out var known)) return known;
            Reads.AddOrUpdate(mod.ModKey.FileName.String, 1, (_, n) => n + 1);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var set = new HashSet<UInt128>();
            int records = 0;
            try
            {
                foreach (var rec in mod.EnumerateMajorRecords())
                    {
                        if (!MayHaveSounds(rec)) continue;
                        records++;
                        try { foreach (var (_, g) in Guids(rec)) set.Add(AssetFiles.SoundGuid(g)); }
                        catch (Exception e) when (Fatal.Recoverable(e)) { Log.Write($"sound events: {rec.FormKey} in {mod.ModKey}: sounds not read ({e.GetType().Name}: {SafeText.OneLine(e.Message)})"); }
                    }
            }
            catch (Exception e) when (Fatal.Recoverable(e)) { Log.Write($"sound events: {mod.ModKey}: stopped at a record Mutagen can't read ({e.GetType().Name}: {SafeText.OneLine(e.Message)}); {set.Count} GUIDs taken so far"); }
            Log.Write($"sound events: {mod.ModKey}: {set.Count:N0} sound GUIDs in {records:N0} records with sound fields ({sw.Elapsed.TotalSeconds:F1} s)");
            masterSets.Add(mod, set);
            return set;
        }
    }

    /// <summary>The getter interfaces of the Starfield record types that can hold a sound GUID, for a typed walk of a
    /// whole plugin (no cells or placed references unless they can hold one).</summary>
    internal static readonly Lazy<List<Type>> RecordTypes = new(() =>
        concrete.Value.Where(c => typeof(IStarfieldMajorRecordGetter).IsAssignableFrom(c) && !c.Name.EndsWith("BinaryOverlay", StringComparison.Ordinal))
            .Where(c => typeof(IWWiseEventDataGetter).IsAssignableFrom(c) || Plan(c).Length > 0)
            .Select(c => c.GetInterface("I" + c.Name + "Getter")).OfType<Type>().Distinct().ToList());
}
