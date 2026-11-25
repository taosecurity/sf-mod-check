// sfmodcheck release: voice files for the mod's dialogue (0.18.0, GAPS G14). A voiced dialogue line plays from
//   sound\voice\<plugin file name, lower case>\<voice type EditorID, lower case>\<response number, 8 lower hex>.wem
// with its lip animation at the same path with .ffxanim. The response number is the low 24 bits of the 32-bit number
// each response of an INFO record carries (Mutagen IDialogResponseGetter.WEMFile), so the name always starts "00":
// the top byte is the plugin's load index when the line was made (01, 02, fd, fe in Bethesda's DLC and Creations,
// whose files all use the low 24 bits). Several INFOs may share one. Proven on files in 0.18.0 steps 2 and 5
// (StarfieldPluginCheck NOTES.md, "0.18.0 (GapFix)").
// Checked: dialogue lines (INFO) the plugin adds (not overrides of other plugins' lines), with at least one response,
// no SharedDialog link, a non-zero number, and not in a player topic (a scene's dialogue action for the player, alias
// -2, or a player-dialogue action's player choices, from the plugin's own scenes).
//   voice-missing  WARNING  rule 1: when the line's subject conditions say GetIsID (an NPC: its voice type) or
//                           GetIsVoiceType (a voice type, or a form list of them) == 1, each named voice type needs its
//                           file; rule 2: otherwise the line needs its file in at least one voice-type folder.
//                  NOTE     a plugin with dialogue lines and no voice files at all (silent, subtitles only); .wem files
//                           with no .ffxanim beside them (the line plays, the lips don't move); a clean result.
// The voice files are the plugin's own: its "<plugin> - Voices_<language>.ba2" archives in the run's inputs (each
// language is checked on its own), plus .wem files in its other archives and loose sound\voice\<plugin>\ files, which
// count for every language.
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using BuildKit;

namespace SfModCheck;

public static class ReleaseVoice
{
    /// <summary>Warnings per plugin and language; the rest are counted in one more line (and listed under --verbose).</summary>
    public static int MaxWarnings = 25;

    /// <summary>One voiced response the check expects a file for.</summary>
    /// Hex is the file name's number (low 24 bits), Full the whole number (for a hint when a file is named by it).
    internal sealed record Line(string Info, string Hex, string Full, List<string> VoiceTypes);

    public static IEnumerable<ReleaseFinding> Run(ReleaseContext c)
    {
        var o = new List<ReleaseFinding>();
        var plugins = c.Packages.SelectMany(p => p.Plugins.Select(x => (Package: p, Path: x))).ToList();
        if (plugins.Count == 0) return o;
        var data = Path.GetFullPath(c.DataDir);
        var dirs = c.Packages.SelectMany(p => p.Plugins).Select(p => Path.GetDirectoryName(Path.GetFullPath(p))!)
            .Distinct(StringComparer.OrdinalIgnoreCase).Concat(c.Options.MastersDirs).ToList();
        foreach (var (pkg, path) in plugins)
        {
            var name = Path.GetFileName(path);
            List<Line>? lines;
            try { lines = Lines(path, data, dirs); }
            catch (Exception e) when (Fatal.Recoverable(e))
            {
                Log.Write($"voice: {name}: dialogue not read: {e}");
                o.Add(new(Level.Note, "voice-missing", $"{name}: its dialogue couldn't be read ({SafeText.OneLine(e.Message)}), so its voice files weren't checked"));
                continue;
            }
            if (lines == null)
            {
                // a master that isn't installed: Mutagen can't tell the plugin's own lines from the master's without
                // the master's style (a light or medium master read as full shifts every FormID), so no check
                o.Add(new(Level.Note, "voice-missing", $"{name}: a master isn't installed, so its voice files weren't checked. Install the masters (or give their folder with --masters-dir) and run release again"));
                continue;
            }
            o.AddRange(Check(c, name, lines));
        }
        return o;
    }

    /// <summary>The findings for one plugin's lines against the voice files in the run's inputs.</summary>
    internal static List<ReleaseFinding> Check(ReleaseContext c, string pluginName, List<Line> lines)
    {
        var o = new List<ReleaseFinding>();
        var plugin = pluginName.ToLowerInvariant();
        Log.Write($"voice: {pluginName}: {lines.Count} voiced responses to check");
        if (lines.Count == 0) return o;
        var (sets, common) = VoiceFiles(c, plugin);
        if (sets.Count == 0 && common.Count == 0)
        {
            o.Add(new(Level.Note, "voice-missing",
                $"{pluginName}: {Num(lines.Count, "voiced dialogue response")} and no voice files (no \"{Path.GetFileNameWithoutExtension(pluginName)} - Voices_en.ba2\" " +
                $"and no loose sound\\voice\\{plugin}\\ files), so the dialogue is silent, subtitles only. If it should be voiced, add the .wem and .ffxanim files"));
            return o;
        }
        if (sets.Count == 0) sets["its loose files and archives"] = [];
        foreach (var (label, own) in sets)
        {
            bool Has(string n) => own.Contains(n) || common.Contains(n);
            // the voice-type folders that hold each response number, for rule 2
            var folders = own.Union(common).Where(n => n.EndsWith(".wem", StringComparison.Ordinal))
                .Select(n => n.Split('\\')).Where(s => s.Length == 5)
                .ToLookup(s => Path.GetFileNameWithoutExtension(s[4]), s => s[3]);
            var missing = new List<(string Text, string Subject)>();
            int noFace = 0; string? firstNoFace = null; int ok = 0;
            // a file named by the whole number (top byte kept) is never found by the game: say so in the warning
            string Renamed(Line l, bool there) => l.Full != l.Hex && there
                ? $" (a file named {l.Full}.wem is there, but the game uses only the last 6 hex digits of the number, so rename it and its .ffxanim to {l.Hex})"
                : "";
            foreach (var l in lines)
            {
                var found = new List<string>();
                if (l.VoiceTypes.Count > 0)
                {
                    foreach (var vt in l.VoiceTypes)
                    {
                        var want = $"sound\\voice\\{plugin}\\{vt}\\{l.Hex}.wem";
                        if (Has(want)) found.Add(vt);
                        else missing.Add(($"{pluginName}: dialogue line {l.Info}: no voice file for voice type {vt} in {label} " +
                                          $"(expected {want}), so the line plays silent for that speaker. Add the file, or remove that voice type from the line's conditions" +
                                          Renamed(l, Has($"sound\\voice\\{plugin}\\{vt}\\{l.Full}.wem")), want));
                    }
                }
                else
                {
                    found.AddRange(folders[l.Hex]);
                    if (found.Count == 0)
                        missing.Add(($"{pluginName}: dialogue line {l.Info}: no voice file {l.Hex}.wem in any voice-type folder of sound\\voice\\{plugin}\\ in {label}, " +
                                     "so the line is silent in every voice. Add the file, or give the line a GetIsID or GetIsVoiceType condition if another plugin voices it" +
                                     Renamed(l, folders[l.Full].Any()),
                                     $"sound\\voice\\{plugin}\\{l.Hex}.wem"));
                }
                foreach (var vt in found)
                {
                    var face = $"sound\\voice\\{plugin}\\{vt}\\{l.Hex}.ffxanim";
                    if (!Has(face)) { noFace++; firstNoFace ??= face; }
                }
                if (found.Count > 0) ok++;
                Log.Write($"voice: {pluginName}: {label}: {l.Info} {l.Hex}: looked for {(l.VoiceTypes.Count > 0 ? string.Join(", ", l.VoiceTypes) : "any voice type")}; " +
                          $"found {(found.Count > 0 ? string.Join(", ", found) : "none")}");
            }
            foreach (var (text, subject) in missing.Take(MaxWarnings))
                o.Add(new(Level.Warning, "voice-missing", text) { Plugin = pluginName, Subject = subject });
            if (missing.Count > MaxWarnings)
                o.Add(new(Level.Warning, "voice-missing",
                    $"{pluginName}: {Num(missing.Count - MaxWarnings, "more voice file")} missing in {label} (run with --verbose to list every line)") { Plugin = pluginName });
            if (noFace > 0)
                o.Add(new(Level.Note, "voice-missing",
                    $"{pluginName}: {Num(noFace, "voice file")} in {label} with no .ffxanim beside it (first: {firstNoFace}): the line plays but the lips don't move"));
            if (missing.Count == 0)
                o.Add(new(Level.Note, "voice-missing", $"{pluginName}: {label}: {Num(lines.Count, "voiced dialogue response")} checked, every one has its voice file"));
            Log.Write($"voice: {pluginName}: {label}: {ok} of {lines.Count} responses found, {missing.Count} voice files missing, {noFace} without .ffxanim");
        }
        return o;
    }

    static string Num(int n, string one) => $"{n} {one}{(n == 1 ? "" : "s")}";

    /// <summary>The plugin's voice file names (Ba2Names.Normalize form) in the run's inputs: per voice archive
    /// ("PluginName - Voices_en.ba2"), and those that count for every language (loose files, other archives).</summary>
    static (SortedDictionary<string, HashSet<string>> Sets, HashSet<string> Common) VoiceFiles(ReleaseContext c, string plugin)
    {
        var sets = new SortedDictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var common = new HashSet<string>(StringComparer.Ordinal);
        var prefix = $"sound\\voice\\{plugin}\\";
        var stem = Path.GetFileNameWithoutExtension(plugin) + " - ";
        foreach (var p in c.Packages)
        {
            foreach (var f in p.Files)
            {
                var g = f.StartsWith("data\\", StringComparison.Ordinal) ? f[5..] : f;   // a zip with a Data folder on top
                if (g.StartsWith(prefix, StringComparison.Ordinal)) common.Add(g);
            }
            foreach (var a in p.Archives)
            {
                var file = Path.GetFileName(a);
                if (!file.StartsWith(stem, StringComparison.OrdinalIgnoreCase)) continue;
                List<string> names;
                try { names = Ba2Names.Read(a); }
                catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    Log.Write($"voice: {file} not read ({e.Message}); the release rules report it");
                    continue;
                }
                var mine = names.Where(n => n.StartsWith(prefix, StringComparison.Ordinal));
                var part = Path.GetFileNameWithoutExtension(file)[stem.Length..];
                if (part.StartsWith("voices_", StringComparison.OrdinalIgnoreCase))
                {
                    if (!sets.TryGetValue(file, out var set)) sets[file] = set = new(StringComparer.Ordinal);
                    set.UnionWith(mine);
                }
                else common.UnionWith(mine);
            }
        }
        return (sets, common);
    }

    /// <summary>The voiced responses of the dialogue lines the plugin adds, with the voice types (EditorIDs, lower case)
    /// their subject conditions name; null when a master isn't installed (the plugin's own FormIDs can't be placed then).</summary>
    internal static List<Line>? Lines(string path, string data, IEnumerable<string> dirs)
    {
        using var game = new GameData(data, dirs);
        var name = Path.GetFileName(path);
        foreach (var m in GameData.HeaderMasters(path))
            if (game.TryOpen(m) == null) { Log.Write($"voice: {name}: master {m} isn't installed, so the voice check is skipped"); return null; }
        using var mod = game.OpenPlugin(path);
        var own = new Dictionary<FormKey, IStarfieldMajorRecordGetter>();
        foreach (var r in mod.EnumerateMajorRecords<IVoiceTypeGetter>()) own[r.FormKey] = r;
        foreach (var r in mod.EnumerateMajorRecords<INpcGetter>()) own[r.FormKey] = r;
        foreach (var r in mod.EnumerateMajorRecords<IFormListGetter>()) own[r.FormKey] = r;
        T? Get<T>(FormKey k) where T : class, IStarfieldMajorRecordGetter =>
            own.TryGetValue(k, out var r) ? r as T : game.LinkCache.TryResolve<T>(k, out var x) ? x : null;
        string? VoiceName(FormKey k) => Get<IVoiceTypeGetter>(k)?.EditorID is { Length: > 0 } e ? e.ToLowerInvariant() : null;

        var playerTopics = new HashSet<FormKey>();
        foreach (var s in mod.EnumerateMajorRecords<ISceneGetter>())
            foreach (var a in s.Actions ?? [])
            {
                if (a is IDialogueSceneActionGetter da && a.AliasID == -2) playerTopics.Add(da.Topic.FormKey);
                if (a is IPlayerDialogueSceneActionGetter pd) foreach (var d in pd.DialogueList) playerTopics.Add(d.PlayerChoice.FormKey);
            }

        var lines = new List<Line>();
        foreach (var t in mod.EnumerateMajorRecords<IDialogTopicGetter>())
        {
            var player = playerTopics.Contains(t.FormKey);
            foreach (var i in t.Responses)
            {
                if (i.FormKey.ModKey != mod.ModKey || i.Responses.Count == 0 || !i.SharedDialog.IsNull) continue;
                if (player) { Log.Write($"voice: {name}: {i.FormKey} is a player line (unvoiced), skipped"); continue; }
                var vts = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var cnd in i.Conditions)
                {
                    if (cnd.CompareOperator != CompareOperator.EqualTo || cnd is not IConditionFloatGetter f || f.ComparisonValue != 1 ||
                        cnd.Data.RunOnType != Condition.RunOnType.Subject) continue;
                    if (cnd.Data is IGetIsIDConditionDataGetter g && Get<INpcGetter>(g.FirstParameter.Link.FormKey) is { } npc && VoiceName(npc.Voice.FormKey) is { } v)
                        vts.Add(v);
                    if (cnd.Data is IGetIsVoiceTypeConditionDataGetter gv)
                    {
                        var k = gv.VoiceTypeOrList.Link.FormKey;
                        if (VoiceName(k) is { } v2) vts.Add(v2);
                        else if (Get<IFormListGetter>(k) is { } list)
                            foreach (var x in list.Items) if (VoiceName(x.FormKey) is { } v3) vts.Add(v3);
                    }
                }
                var label = i.EditorID is { Length: > 0 } ed ? $"{SafeText.OneLine(ed)} ({i.FormKey})" : i.FormKey.ToString();
                foreach (var r in i.Responses)
                    if (r.WEMFile != 0) lines.Add(new Line(label, (r.WEMFile & 0xFFFFFF).ToString("x8"), r.WEMFile.ToString("x8"), [.. vts]));
            }
        }
        return lines;
    }
}
