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
// Player topics (Category Player, a scene's Dialogue action for the player, alias -2, and a Player Dialogue action's
// player choices) are never the NPC's lines; those in the NPC's scenes are counted. Lines with no speaker rule at all
// (generic lines) are counted, not listed.
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
    FormKey Quest, FormKey? Scene, uint WemFile)
{
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
    readonly ILinkCache ids;
    readonly Dictionary<FormKey, string?> edids = new();

    DialogueIndex(ILinkCache ids) => this.ids = ids;

    /// <summary>Reads the plugins in load order (the last copy wins). Throws MemoryLimitException past the limit the
    /// caller set in Analyzer.MaxManagedBytes.</summary>
    public static DialogueIndex Build(List<PluginInfo> plugins, Action<string>? progress)
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
            progress?.Invoke($"Reading the dialogue in {p.FileName}");
            int infos = 0;
            try
            {
                foreach (var t in p.Mod.EnumerateMajorRecords<IDialogTopicGetter>())
                {
                    Tick();
                    x.Topics[t.FormKey] = t;
                    foreach (var i in t.Responses) { Tick(); x.Infos[i.FormKey] = new(i, t.FormKey, p.FileName); infos++; }
                }
                foreach (var s in p.Mod.EnumerateMajorRecords<ISceneGetter>()) { Tick(); scenes[s.FormKey] = s; }
                foreach (var q in p.Mod.EnumerateMajorRecords<IQuestGetter>()) { Tick(); x.Quests[q.FormKey] = q; }
                foreach (var l in p.Mod.EnumerateMajorRecords<IFormListGetter>()) { Tick(); x.FormLists[l.FormKey] = l.Items.Select(i => i.FormKey).ToHashSet(); }
            }
            catch (Exception e) when (Fatal.Recoverable(e))
            {
                x.Unread.Add($"{p.FileName} couldn't be read in full ({e.GetType().Name}: {SafeText.OneLine(e.Message.Split('\n')[0])}); its later dialogue wasn't read");
            }
            Log.Write($"lines: read {p.FileName}: {infos} dialogue lines, {sw.Elapsed.TotalSeconds:F1} s, managed memory {GC.GetTotalMemory(false) / 1e9:F2} GB");
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
        foreach (var q in x.Quests.Values)
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
            if (topic.Category == DialogTopic.CategoryEnum.Player || PlayerTopics.Contains(topic.FormKey))
            {
                playerRows[topic.FormKey] = playerRows.GetValueOrDefault(topic.FormKey) + Responses(i).Count;
                continue;
            }
            string? foundBy = null;
            FormKey? scene = null;
            bool anyRule = !i.Speaker.IsNull, shared = false;
            if (i.Speaker.FormKeyNullable == npc) foundBy = "speaker";
            foreach (var c in i.Conditions)
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
                foreach (var c in i.Conditions)
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
            var responses = Responses(i);
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
                    resp.ResponseText?.String ?? "", resp.ScriptNotes ?? "", emo, foundBy, shared, topic.Quest.FormKey, scene, resp.WEMFile));
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

    /// <summary>A line's own responses, or, when it has none and links a SharedDialog line, that line's.</summary>
    IReadOnlyList<IDialogResponseGetter> Responses(IDialogResponsesGetter i)
    {
        if (i.Responses.Count > 0 || i.SharedDialog.IsNull) return i.Responses;
        return Infos.TryGetValue(i.SharedDialog.FormKey, out var s) ? s.Info.Responses : i.Responses;
    }
}
