// sf-mod-check 0.21.0: the dialogue lines one NPC can speak, for `lines <NPC>` (LinesCommand.cs) and, from plan step 3a,
// `voice-lines <plugin>`. Design: StarfieldPluginCheck NOTES.md, "0.21.0 (SmcLookup)", "Step 1 design".
// DialogueIndex reads the winning copy of every dialogue topic (DIAL), line (INFO), scene, quest and form list of a load
// order once; LinesOf then finds one NPC's lines in it. A line counts for the NPC when the first of these says so (its
// "found by"):
//   speaker      the INFO's Speaker is the NPC
//   condition    a Subject condition GetIsID == 1 names the NPC
//   quest alias  a Subject condition GetIsAliasRef == 1 names an alias of the topic's quest that the NPC fills
//   scene alias  a scene's Dialogue action (its alias) or Player Dialogue action (its NPC response, the action's alias)
//                uses the topic, and that alias of the scene's quest is filled by the NPC
//   voice type   a Subject condition GetIsVoiceType == 1 names the NPC's voice type or a form list holding it, and no
//                rule above matched: a shared line, which every NPC with that voice type may say
// An alias is filled by the NPC when its UniqueActor is the NPC, its ForcedReference is a placed actor of the NPC, or its
// CreateReferenceToObject object is the NPC. Other fill types aren't followed (logged under --verbose).
// Conditions that are negated, compared with anything but 1 or not run on the Subject are ignored (as voice-missing does).
// Player topics (a scene's Dialogue action for the player, alias -2, and a Player Dialogue action's player choices) are
// never the NPC's lines; those in the NPC's scenes are counted. A topic's Category Player alone doesn't make it one:
// Starfield's crew comment topics (a branch with an affinity event) are Category Player and spoken by the companion.
// Besides a line's own conditions, its dialogue group's (DialogGroup) and its quest's DialogConditions count.
// Lines with no speaker rule at all (generic lines) are counted, not listed.
using System.Diagnostics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using SfModCheck.Conflict;

namespace SfModCheck;

/// <summary>One response of one dialogue line the NPC can speak (a line with three responses gives three rows).</summary>
internal sealed record LineRow(
    FormKey Info, string? InfoEditorId, FormKey Topic, string? TopicEditorId,
    DialogTopic.CategoryEnum Category, DialogTopic.SubtypeEnum Subtype,
    int Response, string Text, string ScriptNotes, string Emotion, string FoundBy, bool Shared,
    FormKey Quest, FormKey? Scene, uint WemFile, FormKey VoiceInfo)
{
    // VoiceInfo: the INFO whose responses these are (the SharedDialog line for a line that links one); its plugin
    // names the voice-file folder (code-review round 1).
    public string Type => DialogueLines.TypeName(Category, Subtype);
}

/// <summary>What LinesOf found for one NPC.</summary>
internal sealed class NpcLines
{
    public List<LineRow> Lines = new();
    /// <summary>Response rows of player topics in the NPC's scenes (the player is silent; counted only).</summary>
    public int PlayerLines;
    /// <summary>Response rows of lines with no speaker rule at all (any NPC may say them; counted only).</summary>
    public int GenericLines;
}

internal static class DialogueLines
{
    /// <summary>"Category / Subtype", Mutagen's enum names (Combat / Hit).</summary>
    public static string TypeName(DialogTopic.CategoryEnum c, DialogTopic.SubtypeEnum s) => $"{c} / {s}";

    /// <summary>The emotion keyword's EditorID without "AnimFaceArchetype", "(none)" for no keyword.</summary>
    public static string EmotionName(string? editorId, FormKey key, bool isNull)
    {
        if (isNull) return "(none)";
        if (string.IsNullOrEmpty(editorId)) return $"[{key}]";
        const string prefix = "AnimFaceArchetype";
        return editorId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && editorId.Length > prefix.Length
            ? SafeText.OneLine(editorId[prefix.Length..]) : SafeText.OneLine(editorId);
    }

    /// <summary>A positive Subject condition (== 1, not negated), the only kind the speaker rules read.</summary>
    internal static bool Positive(IConditionGetter c) =>
        c.CompareOperator == CompareOperator.EqualTo && c is IConditionFloatGetter f && f.ComparisonValue == 1 &&
        c.Data.RunOnType == Condition.RunOnType.Subject;
}

/// <summary>The voice and lip files of the plugins a load order holds (0.21.0, `lines`): for a plugin, every name under
/// sound\voice\&lt;plugin&gt;\ in its "&lt;plugin base&gt; - *.ba2" archives (which covers "Voices_en" and Starfield's
/// "Voices01") and loose under each folder, read once per plugin, names only. Any language counts.</summary>
internal sealed class VoiceFileIndex
{
    readonly IReadOnlyList<string> dirs;
    readonly Dictionary<string, HashSet<string>> byPlugin = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="dirs">the folders searched for archives and loose files (LoadOrderInput.AssetDirs)</param>
    public VoiceFileIndex(IReadOnlyList<string> dirs) => this.dirs = dirs;

    /// <summary>The file a voiced response plays from: sound\voice\&lt;plugin&gt;\&lt;voice type&gt;\&lt;low 24 bits, 8 lower hex&gt;&lt;ext&gt;.</summary>
    public static string PathOf(string plugin, string voiceType, uint wemFile, string ext) =>
        $"sound\\voice\\{plugin.ToLowerInvariant()}\\{voiceType.ToLowerInvariant()}\\{(wemFile & 0xFFFFFF):x8}{ext}";

    public bool Has(string plugin, string voiceType, uint wemFile, string ext) =>
        Files(plugin).Contains(PathOf(plugin, voiceType, wemFile, ext));

    HashSet<string> Files(string plugin)
    {
        if (byPlugin.TryGetValue(plugin, out var set)) return set;
        set = new HashSet<string>(StringComparer.Ordinal);
        var lower = plugin.ToLowerInvariant();
        var prefix = $"sound\\voice\\{lower}\\";
        var stem = Path.GetFileNameWithoutExtension(plugin) + " - ";
        var sw = Stopwatch.StartNew();
        int archives = 0, loose = 0;
        foreach (var d in dirs)
        {
            if (!Directory.Exists(d)) continue;
            IEnumerable<string> ba2s;
            try { ba2s = Directory.EnumerateFiles(d, "*.ba2").Where(f => Path.GetFileName(f).StartsWith(stem, StringComparison.OrdinalIgnoreCase)).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Write($"lines: voice files: {d} not listed ({e.Message})"); ba2s = []; }
            foreach (var a in ba2s)
            {
                try
                {
                    foreach (var n in Ba2Names.Read(a)) if (n.StartsWith(prefix, StringComparison.Ordinal)) set.Add(n);
                    archives++;
                }
                catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    Log.Write($"lines: voice files: {a} not read ({e.Message})");
                }
            }
            if (CiDir(d, "sound", "voice", plugin) is not { } folder) continue;
            try
            {
                foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    set.Add(Ba2Names.Normalize(Path.GetRelativePath(d, f)));
                    loose++;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Write($"lines: voice files: {folder} not listed in full ({e.Message})"); }
        }
        Log.Write($"lines: voice files of {plugin}: {set.Count} names from {archives} archives and {loose} loose files, {sw.Elapsed.TotalSeconds:F1} s");
        return byPlugin[plugin] = set;
    }

    /// <summary>A folder under root whose names match these parts in any letter case (Linux and macOS folders are
    /// case-sensitive, the game's aren't); null when there is none.</summary>
    static string? CiDir(string root, params string[] parts)
    {
        var cur = root;
        foreach (var part in parts)
        {
            var exact = Path.Combine(cur, part);
            if (Directory.Exists(exact)) { cur = exact; continue; }
            string? hit = null;
            try { hit = Directory.EnumerateDirectories(cur).FirstOrDefault(x => Path.GetFileName(x).Equals(part, StringComparison.OrdinalIgnoreCase)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            if (hit == null) return null;
            cur = hit;
        }
        return cur;
    }
}

/// <summary>The winning dialogue records of a load order (or one plugin and its masters), read once.</summary>
internal sealed class DialogueIndex
{
    public sealed record InfoCopy(IDialogResponsesGetter Info, FormKey Topic, string Plugin);
    /// <summary>A scene action's use of a topic: the scene, its quest and the alias that speaks it.</summary>
    public sealed record SceneUse(FormKey Scene, FormKey Quest, int Alias);

    public readonly Dictionary<FormKey, IDialogTopicGetter> Topics = new();
    public readonly Dictionary<FormKey, InfoCopy> Infos = new();
    public readonly Dictionary<FormKey, IQuestGetter> Quests = new();
    /// <summary>Topics spoken by an alias in a scene (Dialogue actions with alias >= 0, Player Dialogue NPC responses).</summary>
    public readonly Dictionary<FormKey, List<SceneUse>> SceneTopics = new();
    /// <summary>Player topics per scene (Dialogue actions with alias -2, Player Dialogue player choices).</summary>
    public readonly Dictionary<FormKey, HashSet<FormKey>> ScenePlayerTopics = new();
    public readonly HashSet<FormKey> PlayerTopics = new();
    /// <summary>Each form list's items (winning copy), for GetIsVoiceType on a list.</summary>
    public readonly Dictionary<FormKey, HashSet<FormKey>> FormLists = new();
    /// <summary>Placed actors that a quest alias forces, and their base NPC.</summary>
    public readonly Dictionary<FormKey, FormKey> ForcedBases = new();
    public readonly List<string> Unread = new();
    /// <summary>The plugins whose dialogue (topics, lines, scenes, quests) couldn't be read in full.</summary>
    public readonly HashSet<string> UnreadPlugins = new(StringComparer.OrdinalIgnoreCase);
    readonly ILinkCache ids;
    readonly Dictionary<FormKey, string?> edids = new();

    DialogueIndex(ILinkCache ids) => this.ids = ids;

    /// <summary>Reads the plugins in load order (the last copy wins). Throws MemoryLimitException past the limit the
    /// caller set in Analyzer.MaxManagedBytes. onlyDialogueOf (voice-lines, 0.21.0 step 3a): read topics, lines and
    /// scenes from that plugin only (its masters can't use its topics) and follow forced aliases only in the quests its
    /// topics and scenes name; quests and form lists are still read from every plugin.</summary>
    public static DialogueIndex Build(List<PluginInfo> plugins, Action<string>? progress, string? onlyDialogueOf = null)
    {
        var idCache = plugins.Select(p => (IStarfieldModGetter)p.Mod).ToImmutableLinkCache<IStarfieldMod, IStarfieldModGetter>(LinkCachePreferences.OnlyIdentifiers());
        var x = new DialogueIndex(idCache);
        var scenes = new Dictionary<FormKey, ISceneGetter>();
        int n = 0;
        void Tick()
        {
            if (++n % 4096 == 0 && Analyzer.OverMemoryLimit())
                throw new MemoryLimitException($"memory passed {Analyzer.MaxManagedBytes / 1e9:F1} GB while reading dialogue in {Analyzer.Where}");
        }
        foreach (var p in plugins)
        {
            var sw = Stopwatch.StartNew();
            Analyzer.Where = p.FileName;
            int infos = 0;
            var dialogue = onlyDialogueOf == null || p.FileName.Equals(onlyDialogueOf, StringComparison.OrdinalIgnoreCase);
            progress?.Invoke(dialogue ? $"Reading the dialogue in {p.FileName}" : $"Reading the quests in {p.FileName}");
            try
            {
                if (dialogue)
                {
                    foreach (var t in p.Mod.EnumerateMajorRecords<IDialogTopicGetter>())
                    {
                        Tick();
                        x.Topics[t.FormKey] = t;
                        foreach (var i in t.Responses) { Tick(); x.Infos[i.FormKey] = new(i, t.FormKey, p.FileName); infos++; }
                    }
                    foreach (var s in p.Mod.EnumerateMajorRecords<ISceneGetter>()) { Tick(); scenes[s.FormKey] = s; }
                }
                foreach (var q in p.Mod.EnumerateMajorRecords<IQuestGetter>()) { Tick(); x.Quests[q.FormKey] = q; }
                foreach (var l in p.Mod.EnumerateMajorRecords<IFormListGetter>()) { Tick(); x.FormLists[l.FormKey] = l.Items.Select(i => i.FormKey).ToHashSet(); }
            }
            catch (Exception e) when (Fatal.Recoverable(e))
            {
                x.Unread.Add($"{p.FileName} couldn't be read in full ({e.GetType().Name}: {SafeText.OneLine(e.Message.Split('\n')[0])}); its later dialogue wasn't read");
                x.UnreadPlugins.Add(p.FileName);
            }
            Log.Write($"lines: read {p.FileName}: {infos} dialogue lines, {sw.Elapsed.TotalSeconds:F1} s, managed memory {GC.GetTotalMemory(false) / 1e9:F2} GB");
        }

        // onlyDialogueOf: a topic the plugin overrides (to add lines to a master's topic) may be used by the master's
        // scenes, as a player choice or spoken by an alias, so read those plugins' scenes too and keep the ones that use
        // the plugin's topics (code-review round 1). The plugin's own copy of a scene wins.
        if (onlyDialogueOf != null)
        {
            var owners = x.Topics.Keys.Where(k => !k.ModKey.FileName.String.Equals(onlyDialogueOf, StringComparison.OrdinalIgnoreCase))
                .Select(k => k.ModKey.FileName.String).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var p in plugins)
            {
                if (!owners.Contains(p.FileName)) continue;
                var sw = Stopwatch.StartNew();
                Analyzer.Where = $"the scenes in {p.FileName}";
                progress?.Invoke($"Reading the scenes in {p.FileName}");
                int kept = 0;
                try
                {
                    foreach (var s in p.Mod.EnumerateMajorRecords<ISceneGetter>())
                    {
                        Tick();
                        if (scenes.ContainsKey(s.FormKey) || !UsesTopic(s, x.Topics)) continue;
                        scenes[s.FormKey] = s;
                        kept++;
                    }
                }
                catch (Exception e) when (Fatal.Recoverable(e))
                {
                    x.Unread.Add($"{p.FileName}'s scenes couldn't be read in full ({e.GetType().Name}: {SafeText.OneLine(e.Message.Split('\n')[0])}); its later scenes weren't read");
                }
                Log.Write($"lines: scenes in {p.FileName}: {kept} use the topics of {onlyDialogueOf}, {sw.Elapsed.TotalSeconds:F1} s");
            }
        }

        // scene actions: who speaks which topic (winning scene copies only)
        foreach (var s in scenes.Values)
        {
            var quest = s.Quest.FormKeyNullable ?? FormKey.Null;
            foreach (var a in s.Actions ?? [])
            {
                if (a is IDialogueSceneActionGetter da && !da.Topic.IsNull)
                {
                    if (a.AliasID == -2) x.AddPlayer(s.FormKey, da.Topic.FormKey);
                    else if (a.AliasID is { } al and >= 0) x.AddUse(da.Topic.FormKey, new(s.FormKey, quest, al));
                }
                if (a is IPlayerDialogueSceneActionGetter pd)
                    foreach (var d in pd.DialogueList)
                    {
                        if (!d.PlayerChoice.IsNull) x.AddPlayer(s.FormKey, d.PlayerChoice.FormKey);
                        if (!d.NpcResponse.IsNull && a.AliasID is { } al and >= 0) x.AddUse(d.NpcResponse.FormKey, new(s.FormKey, quest, al));
                    }
            }
        }

        // the placed actors that quest aliases force: their base NPCs, read from the plugins that may hold them
        var forced = new HashSet<FormKey>();
        var wanted = onlyDialogueOf == null ? null
            : x.Topics.Values.Select(t => t.Quest.FormKey).Concat(scenes.Values.Select(s => s.Quest.FormKeyNullable ?? FormKey.Null)).ToHashSet();
        foreach (var q in x.Quests.Values)
            if (wanted == null || wanted.Contains(q.FormKey))
            foreach (var a in q.Aliases ?? [])
                if (a is IQuestReferenceAliasGetter r && r.ForcedReference.FormKeyNullable is { } fk && !fk.IsNull) forced.Add(fk);
        if (forced.Count > 0)
        {
            var mods = forced.Select(k => k.ModKey.FileName.String).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var p in plugins)
            {
                if (!mods.Contains(p.FileName) && !p.Masters.Any(mods.Contains)) continue;
                var sw = Stopwatch.StartNew();
                Analyzer.Where = $"the placed actors in {p.FileName}";
                progress?.Invoke($"Reading the placed actors in {p.FileName}");
                int found = 0;
                try
                {
                    foreach (var r in p.Mod.EnumerateMajorRecords<IPlacedNpcGetter>())
                    {
                        Tick();
                        if (forced.Contains(r.FormKey)) { x.ForcedBases[r.FormKey] = r.Base.FormKey; found++; }
                    }
                }
                catch (Exception e) when (Fatal.Recoverable(e))
                {
                    x.Unread.Add($"{p.FileName}'s placed actors couldn't be read in full ({e.GetType().Name}: {SafeText.OneLine(e.Message.Split('\n')[0])}); aliases forced to its later actors aren't followed");
                }
                Log.Write($"lines: placed actors in {p.FileName}: {found} of the {forced.Count} forced by quest aliases, {sw.Elapsed.TotalSeconds:F1} s");
            }
        }
        Log.Write($"lines: index: {x.Topics.Count} topics, {x.Infos.Count} lines, {scenes.Count} scenes, {x.Quests.Count} quests, " +
                  $"{x.SceneTopics.Count} topics spoken in scenes, {x.PlayerTopics.Count} player topics");
        return x;
    }

    static bool UsesTopic(ISceneGetter s, Dictionary<FormKey, IDialogTopicGetter> topics)
    {
        foreach (var a in s.Actions ?? [])
        {
            if (a is IDialogueSceneActionGetter da && !da.Topic.IsNull && topics.ContainsKey(da.Topic.FormKey)) return true;
            if (a is IPlayerDialogueSceneActionGetter pd)
                foreach (var d in pd.DialogueList)
                    if (topics.ContainsKey(d.PlayerChoice.FormKey) || topics.ContainsKey(d.NpcResponse.FormKey)) return true;
        }
        return false;
    }

    void AddUse(FormKey topic, SceneUse use)
    {
        if (!SceneTopics.TryGetValue(topic, out var l)) SceneTopics[topic] = l = new();
        l.Add(use);
    }

    void AddPlayer(FormKey scene, FormKey topic)
    {
        PlayerTopics.Add(topic);
        if (!ScenePlayerTopics.TryGetValue(scene, out var s)) ScenePlayerTopics[scene] = s = new();
        s.Add(topic);
    }

    public string? EditorId(FormKey k, Type type)
    {
        if (edids.TryGetValue(k, out var e)) return e;
        try { e = ids.TryResolveIdentifier(k, type, out var ed) ? ed : null; }
        catch (Exception ex) when (Fatal.Recoverable(ex)) { e = null; }
        return edids[k] = e;
    }

    /// <summary>The aliases of each quest that the NPC fills; skipped (not followed) aliases are logged once each.</summary>
    Dictionary<FormKey, HashSet<int>> FilledBy(FormKey npc, HashSet<(FormKey, int)> skipped)
    {
        var o = new Dictionary<FormKey, HashSet<int>>();
        foreach (var q in Quests.Values)
            foreach (var a in q.Aliases ?? [])
            {
                if (a is not IQuestReferenceAliasGetter r) continue;
                bool fills = r.UniqueActor.FormKeyNullable == npc
                             || (r.ForcedReference.FormKeyNullable is { } fr && ForcedBases.TryGetValue(fr, out var b) && b == npc)
                             || r.CreateReferenceToObject?.Object.FormKey == npc;
                if (fills)
                {
                    if (!o.TryGetValue(q.FormKey, out var set)) o[q.FormKey] = set = new();
                    set.Add((int)r.ID);
                }
                else if (r.UniqueActor.IsNull && r.ForcedReference.IsNull && r.CreateReferenceToObject == null &&
                         (r.FindMatchingRefFromEvent != null || r.FindMatchingRefNearAlias != null || r.External != null || r.Location != null ||
                          !r.UniqueBaseForm.IsNull || r.CreateObjectTemplate != null))
                    skipped.Add((q.FormKey, (int)r.ID));
            }
        return o;
    }

    /// <summary>One NPC's lines (its voice type, or null for none of its own, gives the shared lines).</summary>
    public NpcLines LinesOf(FormKey npc, FormKey? voiceType)
    {
        var res = new NpcLines();
        var skipped = new HashSet<(FormKey, int)>();
        var filled = FilledBy(npc, skipped);
        bool Fills(FormKey quest, int alias) => filled.TryGetValue(quest, out var s) && s.Contains(alias);
        var loggedSkip = new HashSet<(FormKey, int)>();
        void NoteSkip(FormKey quest, int alias)
        {
            if (skipped.Contains((quest, alias)) && loggedSkip.Add((quest, alias)))
                Log.Write($"lines: alias {alias} of quest {quest} is filled another way (find matching, external, location or base form), not followed");
        }
        Log.Write($"lines: {npc} fills {filled.Sum(f => f.Value.Count)} aliases in {filled.Count} quests");

        var myScenes = new HashSet<FormKey>();
        var playerRows = new Dictionary<FormKey, int>();   // response rows per player topic
        foreach (var (info, copy) in Infos)
        {
            if (!Topics.TryGetValue(copy.Topic, out var topic)) continue;
            var i = copy.Info;
            if (PlayerTopics.Contains(topic.FormKey))
            {
                playerRows[topic.FormKey] = playerRows.GetValueOrDefault(topic.FormKey) + Responses(i).List.Count;
                continue;
            }
            string? foundBy = null;
            FormKey? scene = null;
            bool anyRule = !i.Speaker.IsNull, shared = false;
            if (i.Speaker.FormKeyNullable == npc) foundBy = "speaker";
            var conds = ConditionsOf(i, topic);
            foreach (var c in conds)
            {
                if (!DialogueLines.Positive(c)) continue;
                switch (c.Data)
                {
                    case IGetIsIDConditionDataGetter g:
                        anyRule = true;
                        if (foundBy == null && g.FirstParameter.Link.FormKey == npc) foundBy = "condition";
                        break;
                    case IGetIsAliasRefConditionDataGetter:
                        anyRule = true;
                        break;
                    case IGetIsVoiceTypeConditionDataGetter gv:
                        anyRule = true;
                        var k = gv.VoiceTypeOrList.Link.FormKey;
                        if (voiceType is { } vt && (k == vt || (FormLists.TryGetValue(k, out var items) && items.Contains(vt)))) shared = true;
                        break;
                }
            }
            if (foundBy == null)
                foreach (var c in conds)
                    if (DialogueLines.Positive(c) && c.Data is IGetIsAliasRefConditionDataGetter ar)
                    {
                        if (Fills(topic.Quest.FormKey, ar.FirstParameter)) { foundBy = "quest alias"; break; }
                        NoteSkip(topic.Quest.FormKey, ar.FirstParameter);
                    }
            if (SceneTopics.TryGetValue(topic.FormKey, out var uses))
            {
                anyRule = true;
                foreach (var u in uses)
                {
                    if (!Fills(u.Quest, u.Alias)) { NoteSkip(u.Quest, u.Alias); continue; }
                    myScenes.Add(u.Scene);
                    if (foundBy == null) { foundBy = "scene alias"; scene = u.Scene; }
                }
            }
            if (foundBy == null && shared) foundBy = "voice type";
            else shared = false;
            var (responses, voiceInfo) = Responses(i);
            if (foundBy == null)
            {
                if (!anyRule) res.GenericLines += responses.Count;
                continue;
            }
            for (int r = 0; r < responses.Count; r++)
            {
                var resp = responses[r];
                var emotion = resp.Emotion;
                var emo = DialogueLines.EmotionName(emotion.IsNull ? null : EditorId(emotion.FormKey, typeof(IKeywordGetter)), emotion.FormKey, emotion.IsNull);
                res.Lines.Add(new LineRow(info, i.EditorID, topic.FormKey, topic.EditorID, topic.Category, topic.Subtype, r + 1,
                    resp.ResponseText?.String ?? "", resp.ScriptNotes ?? "", emo, foundBy, shared, topic.Quest.FormKey, scene, resp.WEMFile, voiceInfo));
            }
        }

        // the player's lines in the NPC's scenes
        var counted = new HashSet<FormKey>();   // a player topic in two of the NPC's scenes counts once
        foreach (var s in myScenes)
            if (ScenePlayerTopics.TryGetValue(s, out var pts))
                foreach (var pt in pts)
                    if (counted.Add(pt)) res.PlayerLines += playerRows.GetValueOrDefault(pt);
        Log.Write($"lines: {npc}: {res.Lines.Count} response rows ({res.Lines.Count(l => l.Shared)} shared), {res.PlayerLines} player rows in {myScenes.Count} scenes, " +
                  $"{res.GenericLines} generic rows, {skipped.Count} aliases filled another way");
        return res;
    }

    /// <summary>The conditions that say who may speak a line (0.21.0 step 3a): its own, its dialogue group's (the
    /// DialogGroup INFO, which holds the conditions for the lines in it) and the topic's quest's DialogConditions (which
    /// apply to every line of the quest; Starfield's crew quests name their companion there).</summary>
    public List<IConditionGetter> ConditionsOf(IDialogResponsesGetter i, IDialogTopicGetter topic)
    {
        var o = new List<IConditionGetter>(i.Conditions);
        if (i.DialogGroup.FormKeyNullable is { } g && !g.IsNull && g != i.FormKey && Infos.TryGetValue(g, out var group)) o.AddRange(group.Info.Conditions);
        if (Quests.TryGetValue(topic.Quest.FormKey, out var q)) o.AddRange(q.DialogConditions);
        return o;
    }

    /// <summary>ConditionsOf in its three parts, most specific first: the line's own conditions, its dialogue group's,
    /// its quest's DialogConditions (voice-lines uses the first part that names a voice type).</summary>
    public List<IReadOnlyList<IConditionGetter>> ConditionTiers(IDialogResponsesGetter i, IDialogTopicGetter topic)
    {
        var o = new List<IReadOnlyList<IConditionGetter>> { i.Conditions };
        if (i.DialogGroup.FormKeyNullable is { } g && !g.IsNull && g != i.FormKey && Infos.TryGetValue(g, out var group)) o.Add(group.Info.Conditions);
        if (Quests.TryGetValue(topic.Quest.FormKey, out var q)) o.Add(q.DialogConditions);
        return o;
    }

    Dictionary<(FormKey, int), List<FormKey>>? aliasNpcs;

    /// <summary>The NPCs that fill an alias of a quest (UniqueActor, a forced placed actor's base, CreateReferenceToObject's
    /// object; the same fill rules as LinesOf), for voice-lines (0.21.0 step 3a). Empty when none can be told.</summary>
    public IReadOnlyList<FormKey> AliasNpcs(FormKey quest, int alias)
    {
        if (aliasNpcs == null)
        {
            aliasNpcs = new();
            foreach (var q in Quests.Values)
                foreach (var a in q.Aliases ?? [])
                {
                    if (a is not IQuestReferenceAliasGetter r) continue;
                    var l = new List<FormKey>();
                    if (r.UniqueActor.FormKeyNullable is { } u && !u.IsNull) l.Add(u);
                    if (r.ForcedReference.FormKeyNullable is { } fr && ForcedBases.TryGetValue(fr, out var b) && !b.IsNull) l.Add(b);
                    if (r.CreateReferenceToObject?.Object.FormKey is { } c && !c.IsNull) l.Add(c);
                    if (l.Count > 0) aliasNpcs[(q.FormKey, (int)r.ID)] = l;
                }
        }
        return aliasNpcs.TryGetValue((quest, alias), out var o) ? o : [];
    }

    /// <summary>A line's own responses, or, when it has none and links a SharedDialog line, that line's; with the INFO
    /// they come from.</summary>
    (IReadOnlyList<IDialogResponseGetter> List, FormKey From) Responses(IDialogResponsesGetter i)
    {
        if (i.Responses.Count > 0 || i.SharedDialog.IsNull) return (i.Responses, i.FormKey);
        return Infos.TryGetValue(i.SharedDialog.FormKey, out var s) ? (s.Info.Responses, s.Info.FormKey) : (i.Responses, i.FormKey);
    }
}
