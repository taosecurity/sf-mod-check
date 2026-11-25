// sf-mod-check find --versions (0.20.0): every copy of one record in load order, and what each copy changes against
// the copy it was built on. The base for each field is picked as conflict picks it (ConflictAnalyzer.cs, the base loop
// after "The version the record starts from"); that loop is copied here, not shared, so conflict's output can't change.
// A test (FindVersionsTests) checks both pick the same base. Text and JSON both print from VersionInfo.
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using SfModCheck.Conflict;

namespace SfModCheck;

/// <summary>One changed value of a copy. Base: the plugin whose copy the field was compared with ("" when none had it).</summary>
public sealed record VersionChange(string Field, string Path, string Kind, string Before, string After, string Base);

/// <summary>One copy of the record, in load order.</summary>
public sealed class VersionInfo
{
    public required string Plugin;
    public required int Index;
    public bool Winner, Partial;
    /// <summary>"defines" (the record's own plugin, or the BlueprintShips file that adds it), "first" (the first copy
    /// here, with no base), or null for an override.</summary>
    public string? Role;
    /// <summary>The latest earlier copy among this copy's masters (record level), or null.</summary>
    public string? BuiltOn;
    /// <summary>"official", "parent" or "identical" when the copy changes nothing; null otherwise.</summary>
    public string? NoChanges;
    public List<VersionChange> Changes = new();
    public int NotShown;
    public List<string> Unreadable = new();
    /// <summary>Earlier mod copies that changed something and whose plugin isn't among this copy's masters.</summary>
    public List<string> MissingMasters = new();
}

public static class RecordVersions
{
    /// <summary>Each value in a text change line is cut at this many characters (JSON keeps TriageDetail's 2000).</summary>
    public const int MaxTextValue = 300;

    static bool Localized(PluginInfo p) => (Convert.ToInt64(p.Mod.ModHeader.Flags) & 0x80) != 0;

    /// <summary>The copies' versions. plugins: the whole load order read (for the masters at any depth).
    /// typesDiffer: the copies have different record types, so nothing is compared.</summary>
    public static List<VersionInfo> Compare(FormKey formKey, List<(PluginInfo Plugin, IMajorRecordGetter Rec)> versions,
                                            IReadOnlyList<PluginInfo> plugins, out bool typesDiffer)
    {
        var byName = new Dictionary<string, PluginInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in plugins) byName.TryAdd(p.FileName, p);
        var allMasters = new Dictionary<ModKey, HashSet<ModKey>>();
        HashSet<ModKey> MastersOf(PluginInfo p)
        {
            if (allMasters.TryGetValue(p.Mod.ModKey, out var set)) return set;
            set = new HashSet<ModKey>();
            allMasters[p.Mod.ModKey] = set;   // set before the walk, so a master loop can't recurse forever
            foreach (var m in p.Masters)
                if (byName.TryGetValue(m, out var mp) && mp.Mod.ModKey != p.Mod.ModKey) { set.Add(mp.Mod.ModKey); set.UnionWith(MastersOf(mp)); }
            return set;
        }

        int n = versions.Count;
        var infos = versions.Select((v, i) => new VersionInfo
        {
            Plugin = v.Plugin.FileName, Index = v.Plugin.Index, Winner = i == n - 1, Partial = FieldCompare.IsPartial(v.Rec),
        }).ToList();
        typesDiffer = false;
        if (n == 0) return infos;
        var recType = versions[0].Rec.GetType();
        if (versions.Any(v => v.Rec.GetType() != recType)) { typesDiffer = true; return infos; }

        // the copy the record starts from: 0, or the BlueprintShips file that adds it when a mod's copy loads before it
        // (ConflictAnalyzer's `origin`, 0.12.0)
        int origin = 0;
        for (int j = 1; j < n && origin == 0; j++)
            if (versions[j].Plugin.Mod.ModKey == formKey.ModKey && versions[0].Plugin.Mod.ModKey != formKey.ModKey
                && LoadOrderSetup.IsBlueprint(versions[j].Plugin.FileName)) origin = j;
        bool firstHasNoBase = versions[origin].Plugin.Mod.ModKey != formKey.ModKey;
        infos[origin].Role = firstHasNoBase ? "first" : "defines";

        var fields = FieldCompare.Fields(recType);
        int nf = fields.Length;
        var vals = new object?[n, nf];
        var prov = new bool[n, nf];
        var unreadable = new HashSet<int>[n];
        for (int i = 0; i < n; i++)
        {
            unreadable[i] = new();
            for (int f = 0; f < nf; f++)
            {
                try { prov[i, f] = FieldCompare.Provides(versions[i].Rec, fields[f], out vals[i, f]); }
                catch (Exception e) when (Fatal.Recoverable(e)) { unreadable[i].Add(f); infos[i].Unreadable.Add($"{fields[f].Name} (couldn't be compared: {e.GetType().Name})"); }
            }
        }

        var changed = new bool[n];   // the copy changed some field on purpose (not only translations)
        for (int i = 0; i < n; i++)
        {
            if (i == origin) continue;
            var masters = MastersOf(versions[i].Plugin);
            // record level "built on": a later BlueprintShips master, else the origin for a copy before it, else the
            // latest earlier copy among its masters
            int rb = -1;
            for (int j = n - 1; j > i && rb < 0; j--)
                if (LoadOrderSetup.IsBlueprint(versions[j].Plugin.FileName) && masters.Contains(versions[j].Plugin.Mod.ModKey)) rb = j;
            if (rb < 0 && i < origin) rb = origin;
            for (int j = i - 1; j >= 0 && rb < 0; j--)
                if (masters.Contains(versions[j].Plugin.Mod.ModKey)) rb = j;
            infos[i].BuiltOn = rb >= 0 ? versions[rb].Plugin.FileName : null;

            var detail = new TriageDetail();
            for (int f = 0; f < nf; f++)
            {
                if (!prov[i, f] || unreadable[i].Contains(f)) continue;
                // the base loop as conflict has it
                int b = -1;
                for (int j = n - 1; j > i && b < 0; j--)
                    if (prov[j, f] && LoadOrderSetup.IsBlueprint(versions[j].Plugin.FileName) && masters.Contains(versions[j].Plugin.Mod.ModKey)) b = j;
                if (b < 0 && i < origin) { if (prov[origin, f]) b = origin; }
                else
                    for (int j = i - 1; j >= 0 && b < 0; j--)
                        if (prov[j, f] && masters.Contains(versions[j].Plugin.Mod.ModKey)) b = j;
                var baseVal = b >= 0 ? vals[b, f] : null;
                bool same;
                try { same = FieldCompare.DeepEq(vals[i, f], baseVal, 0); }
                catch (Exception e) when (Fatal.Recoverable(e)) { infos[i].Unreadable.Add($"{fields[f].Name} (couldn't be compared: {e.GetType().Name})"); continue; }
                if (!same) changed[i] = true;
                var translations = b >= 0 ? TriageDetail.TranslationKind(Localized(versions[b].Plugin), Localized(versions[i].Plugin)) : null;
                if (same && translations is null) continue;
                List<DetailLine> lines;
                try { (lines, _) = detail.Field(fields[f].Name, baseVal, vals[i, f], translations, knownEqual: same); }
                catch (Exception e) when (Fatal.Recoverable(e))
                {
                    if (!same) infos[i].Unreadable.Add($"{fields[f].Name} (couldn't be compared: {e.GetType().Name})");
                    continue;
                }
                var basePlugin = b >= 0 ? versions[b].Plugin.FileName : "";
                foreach (var l in lines) infos[i].Changes.Add(new(fields[f].Name, l.Path, l.Kind, l.Before, l.After, basePlugin));
            }
            infos[i].NotShown = detail.NotShown;
            if (infos[i].Changes.Count + infos[i].NotShown == 0 && infos[i].Unreadable.Count == 0)
            {
                var r = versions[i].Rec;
                // conflict's ITM rule: official copies, and parents that hold child records, aren't called identical copies
                infos[i].NoChanges = versions[i].Plugin.Official ? "official"
                    : r is ICellGetter or IWorldspaceGetter or IDialogTopicGetter
                      || (r is IQuestGetter && r is IMajorRecordGetterEnumerable kids && kids.EnumerateMajorRecords().Any()) ? "parent"
                    : "identical";
            }
        }
        for (int i = 0; i < n; i++)
        {
            if (i == origin) continue;
            var masters = MastersOf(versions[i].Plugin);
            for (int j = 0; j < i; j++)
                if (changed[j] && !versions[j].Plugin.Official && versions[j].Plugin.Mod.ModKey != versions[i].Plugin.Mod.ModKey
                    && !masters.Contains(versions[j].Plugin.Mod.ModKey))
                    infos[i].MissingMasters.Add(versions[j].Plugin.FileName);
        }
        return infos;
    }

    static string Cut(string s) => s.Length > MaxTextValue ? s[..MaxTextValue] + "..." : s;

    /// <summary>One change as a text line: "Path: kind: Before -> After" (added shows After only, removed Before only).</summary>
    public static string ChangeText(VersionChange c, string? builtOn)
    {
        var text = c.Kind switch
        {
            TriageDetail.Added => $"{c.Path}: added: {Cut(c.After)}",
            TriageDetail.Removed => $"{c.Path}: removed: {Cut(c.Before)}",
            TriageDetail.Reordered => $"{c.Path}: {c.Kind}",
            _ => $"{c.Path}: {c.Kind}: {Cut(c.Before)} -> {Cut(c.After)}",
        };
        if (c.Base != (builtOn ?? "")) text += c.Base.Length > 0 ? $" (against {c.Base})" : " (no earlier copy among its masters has it)";
        return SafeText.OneLine(text);
    }

    /// <summary>The text block under a match: "  versions: ..." and one numbered line per copy.</summary>
    public static List<string> Lines(List<VersionInfo> vs, bool typesDiffer, string definer, bool definerLoaded, string? pluginMode)
    {
        var o = new List<string>();
        var where = pluginMode != null ? $" (in {pluginMode} and its masters)" : "";
        o.Add(vs.Count == 1 ? $"  versions: 1 copy{where}" : $"  versions: {vs.Count} copies, the last one wins{where}");
        if (typesDiffer) o.Add("    the copies have different record types, so they aren't compared");
        for (int i = 0; i < vs.Count; i++)
        {
            var v = vs[i];
            var tags = new List<string>();
            if (v.Winner) tags.Add("winner");
            if (v.Partial) tags.Add("partial form");
            var head = $"    {i + 1}. {v.Plugin}{(tags.Count > 0 ? $" ({string.Join(", ", tags)})" : "")}: ";
            if (typesDiffer) { o.Add(head.TrimEnd(' ', ':')); continue; }
            string what;
            if (v.Role == "defines") what = "defines it";
            else if (v.Role == "first")
                what = definerLoaded ? $"first copy here; {definer}, which defines it, doesn't hold it"
                                     : $"first copy here; {definer}, which defines it, isn't loaded";
            else
            {
                var built = v.BuiltOn != null ? $"built on {v.BuiltOn}" : "built on no earlier copy (none of its masters has one)";
                int count = v.Changes.Count + v.NotShown;
                what = built + ", " + v.NoChanges switch
                {
                    "official" => "no changes",
                    "parent" => "no changes to its own fields (it holds child records)",
                    "identical" => "no changes (an identical copy)",
                    _ => count == 0 ? "no changes it could compare" : $"{count} change{(count == 1 ? "" : "s")}",
                };
            }
            o.Add(head + what);
            foreach (var c in v.Changes) o.Add("         " + ChangeText(c, v.BuiltOn));
            if (v.NotShown > 0) o.Add($"         ... and {v.NotShown} more changes not shown");
            foreach (var u in v.Unreadable) o.Add($"         {SafeText.OneLine(u)}");
            foreach (var m in v.MissingMasters)
                o.Add($"         note: {v.Plugin} doesn't have {m} as a master, so it may undo {m}'s changes (sf-mod-check conflict says which)");
        }
        return o;
    }

    public static JsonArray Json(List<VersionInfo> vs, bool typesDiffer) => new(vs.Select(v =>
    {
        var o = new JsonObject
        {
            ["plugin"] = v.Plugin,
            ["index"] = v.Index,
            ["winner"] = v.Winner,
            ["partial"] = v.Partial,
            ["role"] = v.Role,
            ["builtOn"] = v.BuiltOn,
            ["noChanges"] = v.NoChanges,
            ["changes"] = new JsonArray(v.Changes.Select(c => (JsonNode)new JsonObject
            {
                ["field"] = c.Field, ["path"] = c.Path, ["kind"] = c.Kind, ["before"] = c.Before, ["after"] = c.After,
                ["against"] = c.Base != (v.BuiltOn ?? "") ? c.Base : null,
            }).ToArray()),
            ["notShown"] = v.NotShown,
            ["unreadable"] = new JsonArray(v.Unreadable.Select(u => (JsonNode)u).ToArray()),
            ["missingMasters"] = new JsonArray(v.MissingMasters.Select(m => (JsonNode)m).ToArray()),
        };
        if (typesDiffer) o["typesDiffer"] = true;
        return (JsonNode)o;
    }).ToArray());
}
