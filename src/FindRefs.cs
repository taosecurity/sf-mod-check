// sf-mod-check find --refs (0.20.0): the records whose winning copy points at the record looked up. One scan serves
// every match. Only plugins that are a target's own plugin or have it as a master can hold a link to it, so only those
// get their links read; the other plugins are read for FormKeys only, because a later copy there of a referring record
// overrides it and drops the link. A container (cell, worldspace, quest, topic) lists its child records' links too, so
// each link is counted on the innermost record: a container's own count is its count minus its direct children's
// (the container rule of Checker.LinkPass, 0.4.1). Text and JSON both print from RefsInfo.
using System.Diagnostics;
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using SfModCheck.Conflict;

namespace SfModCheck;

/// <summary>One record whose winning copy points at the target. In: the innermost container that holds it, or null.</summary>
public sealed record RefEntry(FormKey FormKey, string Type, string? EditorId, string Winner, FormKey? In, string? InText);

/// <summary>What --refs found for one target.</summary>
public sealed class RefsInfo
{
    public List<RefEntry> Records = new();
    /// <summary>Records that point at it only in a copy a later plugin overrides (the later copy drops the link).</summary>
    public int OnlyInOverridden;
    /// <summary>Plugins that couldn't be read in full (the same for every target of one scan).</summary>
    public int NotReadInFull;
    /// <summary>Records whose links couldn't be read (the same for every target of one scan).</summary>
    public int Unreadable;
}

public static class RecordRefs
{
    /// <summary>The managed memory the scan may use (16 GB, under the 20 GB the big runs are capped at).</summary>
    public const long MemoryLimit = 16L * 1000 * 1000 * 1000;
    /// <summary>Records between memory checks, and between --verbose progress lines, inside one plugin.</summary>
    internal static int CheckEvery = 10_000, LogEvery = 50_000;

    /// <summary>A referring record as known so far: the copy in the latest plugin read, the targets that copy points at,
    /// and for each target the plugin of the latest copy that pointed at it.</summary>
    sealed class Referrer
    {
        public required string Plugin;
        public required string Type;
        public string? EditorId;
        public FormKey? In;
        public string? InText;
        public HashSet<FormKey> Targets = new();
        public Dictionary<FormKey, string> HeldIn = new();
    }

    static string Sig(IMajorRecordGetter r) => Checker.SigsOf(r.GetType())?.FirstOrDefault() ?? Analyzer.TypeShort(r.GetType());

    static string Name(IMajorRecordGetter r) =>
        string.IsNullOrEmpty(r.EditorID) ? $"[{r.FormKey}]" : $"{SafeText.OneLine(r.EditorID)} [{r.FormKey}]";

    /// <summary>Scan the load order for the records that point at each target. unread gets a note per plugin that
    /// couldn't be read in full. Throws MemoryLimitException past MemoryLimit (the old limit is restored).</summary>
    public static Dictionary<FormKey, RefsInfo> Scan(IReadOnlyCollection<FormKey> targets, IReadOnlyList<PluginInfo> plugins,
                                                     List<string> unread, Action<string>? progress = null)
    {
        var sw = Stopwatch.StartNew();
        var wanted = targets.ToHashSet();
        var targetPlugins = wanted.Select(t => t.ModKey.FileName.String).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Has(PluginInfo p, string file) =>
            p.FileName.Equals(file, StringComparison.OrdinalIgnoreCase) || p.Masters.Contains(file, StringComparer.OrdinalIgnoreCase);
        bool Walk(PluginInfo p) => targetPlugins.Any(t => Has(p, t));
        var walkCount = plugins.Count(Walk);
        progress?.Invoke($"Reading the links in {plugins.Count} plugins ({walkCount} of them can point at it); this takes a while in a whole load order");
        Log.Write($"refs: {wanted.Count} target(s), {plugins.Count} plugins, {walkCount} can point at them");

        var referrers = new Dictionary<FormKey, Referrer>();
        var referrerPlugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int notReadInFull = 0, unreadable = 0;
        long linksFound = 0;
        var oldLimit = Analyzer.MaxManagedBytes;
        Analyzer.MaxManagedBytes = MemoryLimit;
        try
        {
            int i = 0;
            foreach (var p in plugins)
            {
                i++;
                CheckMemory(p.FileName);
                bool walk = Walk(p);
                // a copy of a known referrer can only be in its own plugin or a plugin that has it as a master
                if (!walk && !referrerPlugins.Any(r => Has(p, r)))
                {
                    Log.Write($"refs: {p.FileName} ({i}/{plugins.Count}): skipped, it can't hold a link to it or a copy of a record that has one");
                    continue;
                }
                Analyzer.Where = $"reading the {(walk ? "links" : "FormKeys")} in {p.FileName} for --refs";
                var hits = new Dictionary<FormKey, (IMajorRecordGetter Rec, Dictionary<(FormKey, Type), int> Counts)>();
                var present = new List<FormKey>();
                int records = 0;
                try
                {
                    foreach (var rec in p.Mod.EnumerateMajorRecords())
                    {
                        if (++records % CheckEvery == 0) CheckMemory(p.FileName);
                        if (records % LogEvery == 0) Log.Write($"refs: {p.FileName}: {records} records read, {sw.Elapsed.TotalSeconds:F0} s");
                        var fk = rec.FormKey;
                        if (referrers.ContainsKey(fk)) present.Add(fk);
                        if (!walk || wanted.Contains(fk)) continue;   // the target's own copies don't count
                        Dictionary<(FormKey, Type), int>? counts = null;
                        try
                        {
                            foreach (var l in rec.EnumerateFormLinks())
                            {
                                if (l.IsNull || !wanted.Contains(l.FormKey)) continue;
                                counts ??= new();
                                counts[(l.FormKey, l.Type)] = counts.GetValueOrDefault((l.FormKey, l.Type)) + 1;
                            }
                            // Mutagen's enumerator skips links held in struct members of script properties
                            foreach (var l in ScriptStructLinks.Of(rec))
                            {
                                if (!wanted.Contains(l.FormKey)) continue;
                                counts ??= new();
                                counts[(l.FormKey, l.Type)] = counts.GetValueOrDefault((l.FormKey, l.Type)) + 1;
                            }
                        }
                        catch (Exception e) when (Fatal.Recoverable(e))
                        {
                            unreadable++;
                            Log.Write($"refs: {Name(rec)} in {p.FileName}: its links can't be read ({e.GetType().Name}: {SafeText.OneLine(e.Message.Split('\n')[0])})");
                        }
                        if (counts != null) hits.TryAdd(fk, (rec, counts));
                    }
                }
                catch (Exception e) when (Fatal.Recoverable(e))
                {
                    notReadInFull++;
                    unread.Add($"{p.FileName} couldn't be read in full ({e.GetType().Name}: {SafeText.OneLine(e.Message.Split('\n')[0])}); its later records weren't searched for links");
                }

                // the container rule: each hit's innermost hit container is its parent (a record that holds a link has
                // every container above it holding it too), and a container keeps only what its direct children don't hold
                var parent = new Dictionary<FormKey, (FormKey Key, int Size)>();
                foreach (var (fk, (rec, _)) in hits)
                {
                    if (rec is not IMajorRecordGetterEnumerable container) continue;
                    var inside = new List<FormKey>();
                    int size = 0;
                    try
                    {
                        foreach (var c in container.EnumerateMajorRecords())
                        {
                            if (c.FormKey == fk) continue;
                            size++;
                            if (hits.ContainsKey(c.FormKey)) inside.Add(c.FormKey);
                        }
                    }
                    catch (Exception e) when (Fatal.Recoverable(e)) { Log.Write($"refs: {Name(rec)} in {p.FileName}: its child records can't be read ({e.GetType().Name})"); }
                    foreach (var d in inside)
                        if (!parent.TryGetValue(d, out var was) || was.Size > size) parent[d] = (fk, size);
                }
                var own = hits.ToDictionary(h => h.Key, h => new Dictionary<(FormKey, Type), int>(h.Value.Counts));
                foreach (var (child, (container, _)) in parent)
                    foreach (var (k, n) in hits[child].Counts)
                        if (own[container].TryGetValue(k, out var have)) own[container][k] = Math.Max(0, have - n);

                // winners: this plugin's copy replaces what earlier plugins said about each record it holds
                var holding = new HashSet<FormKey>();
                foreach (var (fk, counts) in own)
                {
                    var points = counts.Where(c => c.Value > 0).Select(c => c.Key.Item1).ToHashSet();
                    if (points.Count == 0) continue;
                    holding.Add(fk);
                    var rec = hits[fk].Rec;
                    var r = referrers.TryGetValue(fk, out var known) ? known : referrers[fk] = new Referrer { Plugin = p.FileName, Type = Sig(rec) };
                    r.Plugin = p.FileName;
                    r.Type = Sig(rec);
                    r.EditorId = string.IsNullOrEmpty(rec.EditorID) ? null : SafeText.OneLine(rec.EditorID);
                    (r.In, r.InText) = parent.TryGetValue(fk, out var pc) ? (pc.Key, Name(hits[pc.Key].Rec)) : ((FormKey?)null, (string?)null);
                    r.Targets = points;
                    foreach (var t in points) r.HeldIn[t] = p.FileName;
                    referrerPlugins.Add(fk.ModKey.FileName.String);
                    linksFound += counts.Values.Sum();
                }
                foreach (var fk in present)
                {
                    if (holding.Contains(fk)) continue;
                    var r = referrers[fk];
                    if (r.Targets.Count > 0) Log.Write($"refs: {fk} pointed at it in {r.Plugin}; {p.FileName}'s copy overrides it without the link");
                    r.Plugin = p.FileName;
                    r.Targets = new();
                }
                Log.Write($"refs: {p.FileName} ({i}/{plugins.Count}): {records} records{(walk ? "" : " (FormKeys only)")}, {linksFound} links to it so far, " +
                          $"managed memory {Analyzer.ManagedBytes() / 1e9:F1} GB");
            }
        }
        finally { Analyzer.MaxManagedBytes = oldLimit; }

        var result = new Dictionary<FormKey, RefsInfo>();
        foreach (var t in wanted)
        {
            var info = new RefsInfo { NotReadInFull = notReadInFull, Unreadable = unreadable };
            foreach (var (fk, r) in referrers)
            {
                if (r.Targets.Contains(t)) info.Records.Add(new RefEntry(fk, r.Type, r.EditorId, r.Plugin, r.In, r.InText));
                else if (r.HeldIn.TryGetValue(t, out var held))
                {
                    info.OnlyInOverridden++;
                    Log.Write($"refs: {fk} points at {t} only in {held}'s copy; {r.Plugin}'s copy wins without the link");
                }
            }
            info.Records = info.Records.OrderBy(e => e.Type, StringComparer.Ordinal).ThenBy(e => e.FormKey.ToString(), StringComparer.Ordinal).ToList();
            result[t] = info;
            Log.Write($"refs: {t}: {info.Records.Count} records point at it, {info.OnlyInOverridden} more only in an overridden copy");
        }
        Log.Write($"refs: scan done in {sw.Elapsed.TotalSeconds:F1} s");
        return result;
    }

    static void CheckMemory(string plugin)
    {
        if (Analyzer.OverMemoryLimit())
            throw new MemoryLimitException($"memory passed {Analyzer.MaxManagedBytes / 1e9:F1} GB while reading links in {plugin}");
    }

    /// <summary>The text block. max: the records listed at most (0 = all). pluginMode: the --plugin file, or null.</summary>
    public static List<string> Lines(RefsInfo info, int max, string? pluginMode)
    {
        var lines = new List<string>();
        var n = info.Records.Count;
        var where = pluginMode != null ? $" (in {pluginMode} and its masters)" : "";
        var notRead = info.NotReadInFull > 0 ? $" ({info.NotReadInFull} plugin{(info.NotReadInFull == 1 ? "" : "s")} not read in full)" : "";
        lines.Add($"  referenced by: {(n == 0 ? "no records" : $"{n} record{(n == 1 ? "" : "s")}")} (winning copies){where}{notRead}");
        var shown = max == 0 ? info.Records : info.Records.Take(max).ToList();
        foreach (var e in shown)
            lines.Add($"    {e.EditorId ?? "(no EditorID)"} [{e.FormKey}] {e.Type}{(e.InText != null ? $" in {e.InText}" : "")}, winner {e.Winner}");
        if (shown.Count < n) lines.Add($"    ... and {n - shown.Count} more (--max 0 lists them all)");
        if (info.OnlyInOverridden > 0)
            lines.Add($"    note: {info.OnlyInOverridden} more record{(info.OnlyInOverridden == 1 ? "" : "s")} point{(info.OnlyInOverridden == 1 ? "s" : "")} at it only in a copy a later plugin overrides (--verbose names them)");
        if (info.Unreadable > 0)
            lines.Add($"    note: the links of {info.Unreadable} record{(info.Unreadable == 1 ? "" : "s")} couldn't be read (--verbose names them)");
        return lines;
    }

    /// <summary>The JSON object: total, listed, onlyInOverridden and the records listed (at most max; 0 = all).</summary>
    public static JsonObject Json(RefsInfo info, int max)
    {
        var shown = max == 0 ? info.Records : info.Records.Take(max).ToList();
        var o = new JsonObject
        {
            ["total"] = info.Records.Count,
            ["listed"] = shown.Count,
            ["onlyInOverridden"] = info.OnlyInOverridden,
            ["records"] = new JsonArray(shown.Select(e => (JsonNode)new JsonObject
            {
                ["formKey"] = e.FormKey.ToString(), ["type"] = e.Type, ["editorId"] = e.EditorId, ["winner"] = e.Winner,
                ["in"] = e.In?.ToString(),
            }).ToArray()),
        };
        if (info.NotReadInFull > 0) o["notReadInFull"] = info.NotReadInFull;
        if (info.Unreadable > 0) o["unreadable"] = info.Unreadable;
        return o;
    }
}
