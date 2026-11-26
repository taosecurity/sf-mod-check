// sf-mod-check 0.24.0: the scene model behind `scenes <NPC or quest>`. Design: StarfieldPluginCheck NOTES.md,
// "0.24.0 (SmcDialogue)", "Step 1 design". This file holds the model (SceneNode, ScenePart, Step, LineNode), how the
// scenes are picked for an NPC or a quest, the talk-menu finder, the "Then" rules and SceneWords (conditions in plain
// words). It writes nothing to the console; ScenesCommand.cs prints it.
//
// Mutagen 0.54.4 names (checked 2026-11-26 by reflection on the pinned dll): an INFO's StartScene, StartScenePhase,
// SetParentQuestStage (OnBegin, OnEnd; -1 = none), SharedDialog, Perk and Flags.Flags (a raw Int16). Starfield's INFO
// has no flag called Goodbye: the bit that ends the conversation is 0x40, which Mutagen calls EndRunningScene (the same
// bit dialogue_tree.py reads); Random is 0x2. A scene's parts are its Phases (Name, StartConditions); an action's part is
// its StartPhase (0-based). A Start Scene action lists StartScenes, each with STSCScene and StartPhaseForScene.
using System.Globalization;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;

namespace SfModCheck;

/// <summary>One dialogue line (INFO) as the tree shows it.</summary>
internal sealed record LineNode(
    FormKey Info, string? EditorId, string Text, FormKey? SharedFrom, IReadOnlyList<IConditionGetter> Conditions,
    bool Goodbye, bool Random, FormKey? Perk, FormKey? StartScene, string? StartPart, int StageOnBegin, int StageOnEnd)
{
    public const short GoodbyeFlag = 0x40, RandomFlag = 0x2;
    public bool HasText => Text.Length > 0;
    /// <summary>The line starts a scene, names a part or ends the conversation: its "Then" is worth printing.</summary>
    public bool Jumps => Goodbye || StartScene != null || !string.IsNullOrEmpty(StartPart);
}

/// <summary>One player choice of a Player Dialogue action: the player's topic and the NPC's answer topic.</summary>
internal sealed record Choice(FormKey? PlayerTopic, FormKey? ResponseTopic, IReadOnlyList<LineNode> Player, IReadOnlyList<LineNode> Answers);

internal enum StepType { Says, Choices, Other }

/// <summary>One scene action in a part: an alias's lines (Says), the player's choices (Choices) or anything else (Other,
/// one short line; a Start Scene action lists the scenes it starts in Starts).</summary>
internal sealed record Step(
    StepType Type, int Alias, string Speaker, FormKey? Topic, IReadOnlyList<LineNode> Lines, bool Random,
    IReadOnlyList<Choice> Choices, string Text, IReadOnlyList<(FormKey Scene, string? Part)> Starts);

internal sealed record ScenePart(int Index, string Name, IReadOnlyList<IConditionGetter> Conditions, List<Step> Steps);

internal sealed record SceneNode(
    FormKey Key, string? EditorId, FormKey Quest, IReadOnlyList<IConditionGetter> Conditions, IReadOnlyList<ScenePart> Parts, bool Script)
{
    /// <summary>The part with exactly this name (the game's own match), or -1.</summary>
    public int PartIndex(string? name)
    {
        if (string.IsNullOrEmpty(name)) return -1;
        for (int i = 0; i < Parts.Count; i++)
            if (Parts[i].Name.Length > 0 && Parts[i].Name == name) return i;
        return -1;
    }
}

/// <summary>The scenes of one load order as trees, built on demand from a DialogueIndex.</summary>
internal sealed class SceneTree
{
    readonly DialogueIndex x;
    readonly Dictionary<FormKey, SceneNode> built = new();
    readonly Dictionary<FormKey, FormKey?> hubs = new();
    /// <summary>`--hub &lt;scene&gt;`: the talk menu, instead of the one found by counting jumps.</summary>
    public FormKey? HubOverride;
    /// <summary>Remarks for the tree (a part name with no scene to start); not rule findings.</summary>
    public readonly List<string> Remarks = new();
    readonly HashSet<FormKey> remarked = new();

    public SceneTree(DialogueIndex index) => x = index;

    public DialogueIndex Index => x;

    /// <summary>A record's EditorID, or its FormKey when it has none.</summary>
    public string Name(FormKey k, Type t) => SafeText.OneLine(x.EditorId(k, t)) is { Length: > 0 } e ? e : k.ToString();
    public string Link(IFormLinkGetter l) => l.IsNull ? "None" : Name(l.FormKey, l.Type);

    /// <summary>The scene as a tree, or null when the index doesn't hold it.</summary>
    public SceneNode? Scene(FormKey k)
    {
        if (built.TryGetValue(k, out var n)) return n;
        if (!x.Scenes.TryGetValue(k, out var s)) return null;
        var quest = s.Quest.FormKeyNullable ?? FormKey.Null;
        var parts = new List<ScenePart>();
        for (int i = 0; i < s.Phases.Count; i++)
            parts.Add(new ScenePart(i, s.Phases[i].Name ?? "", s.Phases[i].StartConditions, new()));
        var actions = (s.Actions ?? []).OrderBy(a => a.StartPhase).ThenBy(a => a.Index ?? uint.MaxValue).ToList();
        foreach (var a in actions)
        {
            var step = StepOf(a, quest);
            var p = (int)a.StartPhase;
            if (p < 0 || p >= parts.Count)
            {
                Log.Write($"scenes: {Name(k, typeof(ISceneGetter))}: action {a.Index} starts in part {p + 1}, which the scene doesn't have; listed in its last part");
                if (parts.Count == 0) parts.Add(new ScenePart(0, "", [], new()));
                p = parts.Count - 1;
            }
            parts[p].Steps.Add(step);
        }
        n = new SceneNode(k, s.EditorID, quest, s.Conditions, parts, s.VirtualMachineAdapter != null);
        return built[k] = n;
    }

    Step StepOf(IASceneActionGetter a, FormKey quest)
    {
        var alias = a.AliasID ?? -1;
        switch (a)
        {
            case IDialogueSceneActionGetter d when !d.Topic.IsNull:
            {
                var lines = Lines(d.Topic.FormKey);
                var spoken = lines.Where(l => l.HasText).ToList();
                return new Step(StepType.Says, alias, Speaker(quest, alias), d.Topic.FormKey, lines,
                    spoken.Count > 1 && spoken.All(l => l.Random), [], "", []);
            }
            case IPlayerDialogueSceneActionGetter pd:
            {
                var choices = pd.DialogueList.Select(c => new Choice(
                    c.PlayerChoice.IsNull ? null : c.PlayerChoice.FormKey, c.NpcResponse.IsNull ? null : c.NpcResponse.FormKey,
                    c.PlayerChoice.IsNull ? [] : Lines(c.PlayerChoice.FormKey), c.NpcResponse.IsNull ? [] : Lines(c.NpcResponse.FormKey))).ToList();
                return new Step(StepType.Choices, alias, Speaker(quest, alias), null, [], false, choices, "", []);
            }
            case IStartSceneActionGetter ss:
            {
                var starts = ss.StartScenes.Select(st => (Scene: st.STSCScene.FormKeyNullable ?? st.LCEPScene.FormKeyNullable ?? FormKey.Null,
                    Part: string.IsNullOrEmpty(st.StartPhaseForScene) ? null : st.StartPhaseForScene))
                    .Where(st => !st.Scene.IsNull).ToList();
                var text = starts.Count == 0 ? "starts no scene"
                    : "starts " + string.Join(", ", starts.Select(st => $"scene {Name(st.Scene, typeof(ISceneGetter))}" + (st.Part != null ? $" (part \"{SafeText.OneLine(st.Part)}\")" : "")));
                return new Step(StepType.Other, alias, Speaker(quest, alias), null, [], false, [], text, starts);
            }
            default:
            {
                var kind = a.GetType().Name;
                foreach (var cut in new[] { "BinaryOverlay", "SceneAction" })   // TimerSceneActionBinaryOverlay: timer
                    if (kind.EndsWith(cut, StringComparison.Ordinal)) kind = kind[..^cut.Length];
                if (kind.Length == 0) kind = "an action";
                var who = alias >= 0 ? $" ({Speaker(quest, alias)})" : "";
                return new Step(StepType.Other, alias, Speaker(quest, alias), null, [], false, [], $"{kind.ToLowerInvariant()} action{who}", []);
            }
        }
    }

    /// <summary>Who an alias is: "You" for the player (-2), the NPC filling it, else the alias's name, else "alias N".</summary>
    public string Speaker(FormKey quest, int alias)
    {
        if (alias == -2) return "You";
        if (alias < 0) return "(no alias)";
        var npcs = x.AliasNpcs(quest, alias);
        if (npcs.Count > 0) return string.Join(" or ", npcs.Select(n => Name(n, typeof(INpcGetter))));
        if (x.Quests.TryGetValue(quest, out var q))
            foreach (var a in q.Aliases ?? [])
                if (a is IQuestReferenceAliasGetter r && r.ID == alias && !string.IsNullOrEmpty(r.Name)) return SafeText.OneLine(r.Name)!;
        return $"alias {alias}";
    }

    /// <summary>A topic's lines as LineNodes, in their random-pick order.</summary>
    public List<LineNode> Lines(FormKey topic) => x.TopicLines(topic).Select(c => Line(c.Info)).ToList();

    LineNode Line(IDialogResponsesGetter i)
    {
        FormKey? shared = null;
        var responses = i.Responses;
        if (responses.Count == 0 && !i.SharedDialog.IsNull)
        {
            shared = i.SharedDialog.FormKey;
            if (x.Infos.TryGetValue(i.SharedDialog.FormKey, out var s)) responses = s.Info.Responses;
        }
        var text = string.Join(" ", responses.Select(r => SafeText.OneLine(r.ResponseText?.String)?.Trim() ?? "").Where(t => t.Length > 0));
        var flags = i.Flags?.Flags ?? 0;
        var sp = i.SetParentQuestStage;
        return new LineNode(i.FormKey, i.EditorID, text, shared, i.Conditions,
            (flags & LineNode.GoodbyeFlag) != 0, (flags & LineNode.RandomFlag) != 0,
            i.Perk.IsNull ? null : i.Perk.FormKey,
            i.StartScene.IsNull ? null : i.StartScene.FormKey,
            string.IsNullOrEmpty(i.StartScenePhase) ? null : i.StartScenePhase,
            sp?.OnBegin ?? -1, sp?.OnEnd ?? -1);
    }

    /// <summary>Every jump a scene makes: its lines' StartScene (with the part named) and its Start Scene actions.</summary>
    public IEnumerable<(FormKey Scene, string? Part)> Jumps(SceneNode s)
    {
        foreach (var p in s.Parts)
            foreach (var st in p.Steps)
            {
                foreach (var l in st.Lines.Concat(st.Choices.SelectMany(c => c.Player.Concat(c.Answers))))
                    if (l.StartScene is { } t) yield return (t, l.StartPart);
                foreach (var j in st.Starts) yield return j;
            }
    }

    /// <summary>The talk menu of a quest: the scene that the most other scenes of the quest jump to (at any part),
    /// when at least two do; or HubOverride. A tie is broken by how the tied scenes jump to each other: the menu enters
    /// a submenu before its choices (at its greeting), while the submenu sends the talk back to the menu's choice part,
    /// so the one tied scene that no other tied scene enters before its choices wins. Null when none qualifies.</summary>
    public FormKey? Hub(FormKey quest)
    {
        if (HubOverride is { } h) return h;
        if (hubs.TryGetValue(quest, out var got)) return got;
        var from = new Dictionary<FormKey, HashSet<FormKey>>();
        var entered = new HashSet<(FormKey Target, FormKey From)>();   // jumps to a scene's first part
        foreach (var k in x.Scenes.Keys)
        {
            var s = Scene(k);
            if (s == null || s.Quest != quest) continue;
            foreach (var (t, part) in Jumps(s))
            {
                // any part counts: vanilla and mod talks jump back to the menu's choice part (TOPLEVELNEST), not its first
                if (t == k || Scene(t) is not { } ts || ts.Quest != quest) continue;
                if (!from.TryGetValue(t, out var set)) from[t] = set = new();
                set.Add(k);
                if (Enters(ts, part)) entered.Add((t, k));
            }
        }
        FormKey? hub = null;
        string why = "";
        var most = from.Count == 0 ? 0 : from.Values.Max(v => v.Count);
        var tied = from.Where(f => f.Value.Count == most).Select(f => f.Key).OrderBy(k => k.ID).ToList();
        if (most >= 2 && tied.Count == 1) hub = tied[0];
        else if (most >= 2)
        {
            var notEntered = tied.Where(c => !tied.Any(o => o != c && entered.Contains((c, o)))).ToList();
            if (notEntered.Count == 1)
            {
                hub = notEntered[0];
                why = $"; it ties with {string.Join(", ", tied.Where(c => c != hub).Select(c => Name(c, typeof(ISceneGetter))))}, but none of them jumps to it before its choices";
            }
        }
        Log.Write(hub is { } hk
            ? $"scenes: talk menu of {Name(quest, typeof(IQuestGetter))}: {Name(hk, typeof(ISceneGetter))} ({from[hk].Count} scenes jump to it{why})"
            : $"scenes: talk menu of {Name(quest, typeof(IQuestGetter))}: none found (no scene has two or more jumps to it, or a tie that the jumps between the tied scenes don't settle)");
        return hubs[quest] = hub;
    }

    /// <summary>Whether a jump to a scene at a part (none = the first) lands before the scene's first choice part (or,
    /// for a scene without choices, at its first part): an entry, not a return to its choices.</summary>
    static bool Enters(SceneNode s, string? part)
    {
        var at = string.IsNullOrEmpty(part) ? 0 : s.PartIndex(part);
        if (at < 0) return false;
        var choices = s.Parts.Select((p, i) => p.Steps.Any(st => st.Type == StepType.Choices) ? i : -1).FirstOrDefault(i => i >= 0, -1);
        return choices < 0 ? at == 0 : at < choices;
    }

    /// <summary>Where the talk goes after a line spoken in a part of a scene, in words (the design's rules 1 to 7; the
    /// first match wins).</summary>
    public string Then(SceneNode scene, int part, LineNode line)
    {
        if (line.Goodbye) return "The conversation ends.";
        if (line.StartScene is { } target)
        {
            var ts = Scene(target);
            if (ts != null && line.StartPart != null && ts.PartIndex(line.StartPart) < 0)
                return $"Meant to jump to \"{SafeText.OneLine(line.StartPart)}\", a part that doesn't exist.";
            if (Hub(scene.Quest) is { } hub && hub == target && hub != scene.Key) return "Back to the talk menu.";
            if (target == scene.Key)
            {
                var i = line.StartPart != null ? scene.PartIndex(line.StartPart) : 0;
                if (i == part) return "Back to these choices.";
                return i < part ? $"Back to part {i + 1}." : $"On to part {i + 1}.";
            }
            return $"Goes to {Name(target, typeof(ISceneGetter))}" + (line.StartPart != null ? $" (part \"{SafeText.OneLine(line.StartPart)}\")" : "") + ".";
        }
        if (line.StartPart != null && remarked.Add(line.Info))
            Remarks.Add($"{Name(scene.Key, typeof(ISceneGetter))}: line {line.Info} names the part \"{SafeText.OneLine(line.StartPart)}\" but no scene to start, so the game ignores it.");
        for (int p = part + 1; p < scene.Parts.Count; p++)
            if (scene.Parts[p].Steps.Count > 0) return $"On to part {p + 1}.";
        return "The scene ends.";
    }

    /// <summary>A choice's "Then" when its NPC answer topic is empty: the player line that jumps, else the first player
    /// line; "The scene ends." style text from Then, or "" when the choice has no player line either.</summary>
    public string ChoiceThen(SceneNode scene, int part, Choice c)
    {
        var spoken = c.Player.Where(l => l.HasText || l.SharedFrom != null).ToList();
        var line = spoken.FirstOrDefault(l => l.StartScene != null) ?? spoken.FirstOrDefault();
        return line == null ? "" : Then(scene, part, line);
    }

    /// <summary>The scenes of a quest, in FormID order.</summary>
    public List<SceneNode> ForQuest(FormKey quest) =>
        x.Scenes.Keys.Select(Scene).OfType<SceneNode>().Where(s => s.Quest == quest)
            .OrderBy(s => s.Key.ID).ThenBy(s => s.Key.ModKey.FileName.String, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The scenes an NPC speaks in (an action spoken by an alias the NPC fills), plus every scene of the same
    /// quests they jump to, followed until nothing new is added. Ordered by quest, then each quest's talk menu first,
    /// then by FormID. why gets one reason per scene (for --verbose).</summary>
    public List<SceneNode> ForNpc(FormKey npc, Dictionary<FormKey, string>? why = null)
    {
        why ??= new();
        var picked = new Dictionary<FormKey, SceneNode>();
        var todo = new Queue<SceneNode>();
        foreach (var k in x.Scenes.Keys)
        {
            if (Scene(k) is not { } s) continue;
            var by = s.Parts.SelectMany(p => p.Steps)
                .FirstOrDefault(st => st.Type != StepType.Other && st.Alias >= 0 && x.AliasNpcs(s.Quest, st.Alias).Contains(npc));
            if (by == null) continue;
            picked[k] = s;
            why[k] = $"spoken by alias {by.Alias}, which {Name(npc, typeof(INpcGetter))} fills";
            todo.Enqueue(s);
        }
        var quests = picked.Values.Select(s => s.Quest).ToHashSet();
        while (todo.Count > 0)
        {
            var s = todo.Dequeue();
            foreach (var (t, _) in Jumps(s))
            {
                if (picked.ContainsKey(t) || Scene(t) is not { } ts || !quests.Contains(ts.Quest)) continue;
                picked[t] = ts;
                why[t] = $"jumped to from {Name(s.Key, typeof(ISceneGetter))}";
                todo.Enqueue(ts);
            }
        }
        foreach (var (k, w) in why) if (picked.ContainsKey(k)) Log.Write($"scenes: picked {Name(k, typeof(ISceneGetter))}: {w}");
        return picked.Values
            .OrderBy(s => s.Quest.ModKey.FileName.String, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Quest.ID)
            .ThenBy(s => Hub(s.Quest) == s.Key ? 0 : 1)
            .ThenBy(s => s.Key.ID).ThenBy(s => s.Key.ModKey.FileName.String, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---- 0.24.0 step 3: the five rules and the stage table (SceneTree.Check) ----

    public const string Back = "Back to these choices.";

    /// <summary>A GetStageDone condition read while walking the shown scenes: done (after) or not done (before), and
    /// where it sits ("ScTopicA" for a scene's own condition, "ScTopicA, part 2" for a part's or a line's).</summary>
    readonly record struct Gate(FormKey Quest, int Stage, bool After, string Where, FormKey Record);

    /// <summary>A GetStageDone condition as (quest, stage, after), or null for any other condition or a comparison
    /// that isn't a plain 0 or 1.</summary>
    internal static (FormKey Quest, int Stage, bool After)? StageDone(IConditionGetter c)
    {
        if (c.Data is not IGetStageDoneConditionDataGetter g || c is not IConditionFloatGetter f) return null;
        if (!g.FirstParameter.UsesLink() || g.FirstParameter.Link.IsNull) return null;
        var v = f.ComparisonValue;
        bool? after = c.CompareOperator switch
        {
            CompareOperator.EqualTo when v == 1 => true,
            CompareOperator.NotEqualTo when v == 0 => true,
            CompareOperator.EqualTo when v == 0 => false,
            CompareOperator.NotEqualTo when v == 1 => false,
            _ => null,
        };
        return after is { } a ? (g.FirstParameter.Link.FormKey, g.SecondParameter, a) : null;
    }

    /// <summary>The stage numbers a condition reads: GetStageDone, or GetStage compared with a number.</summary>
    static (FormKey Quest, int Stage)? StageRead(IConditionGetter c)
    {
        if (c.Data is IGetStageDoneConditionDataGetter g && g.FirstParameter.UsesLink() && !g.FirstParameter.Link.IsNull)
            return (g.FirstParameter.Link.FormKey, g.SecondParameter);
        if (c.Data is IGetStageConditionDataGetter s && c is IConditionFloatGetter f && s.FirstParameter.UsesLink() && !s.FirstParameter.Link.IsNull)
            return (s.FirstParameter.Link.FormKey, (int)f.ComparisonValue);
        return null;
    }

    /// <summary>Every line a step holds: an alias's lines, and each choice's player and answer lines.</summary>
    static IEnumerable<LineNode> LinesOf(Step st) => st.Lines.Concat(st.Choices.SelectMany(c => c.Player.Concat(c.Answers)));

    readonly Dictionary<ModKey, HashSet<(FormKey, int)>> readsCache = new();

    /// <summary>The stages any condition in a plugin reads (each record's conditions, child records included).</summary>
    HashSet<(FormKey, int)> Reads(IStarfieldModGetter mod)
    {
        if (readsCache.TryGetValue(mod.ModKey, out var got)) return got;
        var o = new HashSet<(FormKey, int)>();
        foreach (var r in mod.EnumerateMajorRecords())
            ConditionWalker.ForEach(r, c => { if (StageRead(c) is { } s) o.Add(s); });
        Log.Write($"scenes: {mod.ModKey.FileName}: {o.Count} quest stage(s) read by conditions");
        return readsCache[mod.ModKey] = o;
    }

    /// <summary>The rules on the scenes shown, and the stage table. pluginOf gives the plugin that adds a quest or a
    /// topic (by its ModKey) with its setter table, or null when its quests and topics aren't checked (an official
    /// plugin, or not the --plugin one): the stage rules, the stage table and scene-topic-unused cover only those.</summary>
    public SceneCheck Check(IReadOnlyList<SceneNode> shown, Func<ModKey, StagePlugin?> pluginOf)
    {
        var o = new SceneCheck();
        var said = new HashSet<string>();
        void Add(string sev, string rule, string text, FormKey record)
        {
            if (said.Add(rule + "\n" + text)) o.Problems.Add(new SceneProblem(sev, rule, text, record.ToString()));
        }
        string SceneName(FormKey k) => Name(k, typeof(ISceneGetter));
        string QuestName(FormKey k) => Name(k, typeof(IQuestGetter));
        StagePlugin? PluginOfQuest(FormKey q) => x.Quests.ContainsKey(q) ? pluginOf(q.ModKey) : null;

        var gates = new List<Gate>();
        var touched = new List<FormKey>();
        void Touch(FormKey q) { if (!touched.Contains(q) && PluginOfQuest(q) != null) touched.Add(q); }
        void Conds(IReadOnlyList<IConditionGetter> cs, string where, FormKey rec)
        {
            foreach (var c in cs)
                if (StageDone(c) is { } g) { gates.Add(new Gate(g.Quest, g.Stage, g.After, where, rec)); Touch(g.Quest); }
        }

        foreach (var s in shown)
        {
            var sn = SceneName(s.Key);
            if (!s.Quest.IsNull) Touch(s.Quest);
            Conds(s.Conditions, sn, s.Key);
            foreach (var p in s.Parts)
            {
                var where = $"{sn}, part {p.Index + 1}";
                Conds(p.Conditions, where, s.Key);
                foreach (var st in p.Steps)
                {
                    foreach (var l in LinesOf(st))
                    {
                        Conds(l.Conditions, where, l.Info);
                        // scene-phase-missing (ERROR)
                        if (l.StartScene is { } t && l.StartPart != null && Scene(t) is { } ts && ts.PartIndex(l.StartPart) < 0)
                            Add("ERROR", "scene-phase-missing",
                                Rules.ScenePhaseText($"{sn}: line {l.Info}", $"{SceneName(t)} [SCEN:{t}]", l.StartPart, ts.Parts.Select(q => q.Name).ToList()), l.Info);
                    }
                    // scene-no-exit (WARNING): every choice leads back to these same choices
                    if (st.Type == StepType.Choices && st.Choices.Count > 0 && st.Choices.All(c => Loops(s, p.Index, c)))
                        Add("WARNING", "scene-no-exit", $"{where}: every one of the {st.Choices.Count} choice(s) leads back to these same choices, so the player can't leave the conversation here", s.Key);
                }
            }
        }

        // stage-gate-unset (WARNING, or a NOTE when the plugin's setters couldn't all be read)
        foreach (var grp in gates.Where(g => g.After).GroupBy(g => (g.Quest, g.Stage)))
        {
            var (q, n) = grp.Key;
            if (PluginOfQuest(q) is not { } pl || pl.Setters.IsSet(q, n)) continue;
            var where = Where(grp.Select(g => g.Where));
            var plugin = q.ModKey.FileName.String;
            if (pl.Setters.Dynamic.TryGetValue(q, out var dyn))
            {
                var sev = "NOTE";
                Add(sev, "stage-gate-unset", $"{where}: needs {QuestName(q)} stage {n} done, and nothing in {plugin} sets that stage by number, but {dyn} SetStage call(s) take the stage number from a value worked out while the game runs, so it wasn't checked", grp.First().Record);
            }
            else if (pl.Setters.Unreadable.Count > 0)
            {
                var sev = "NOTE";
                var (first, why) = pl.Setters.Unreadable.First();
                Add(sev, "stage-gate-unset", $"{where}: needs {QuestName(q)} stage {n} done, and nothing in {plugin} that could be read sets it, but {pl.Setters.Unreadable.Count} attached script(s) couldn't be found or read (e.g. {SafeText.OneLine(first)}: {SafeText.OneLine(why)}), so it wasn't checked", grp.First().Record);
            }
            else
                Add("WARNING", "stage-gate-unset", $"{where}: needs {QuestName(q)} stage {n} done, but nothing in {plugin} sets that stage (no start stage, dialogue, scene or script SetStage call), so this never shows", grp.First().Record);
        }

        // stage-set-unchecked (NOTE): set by something other than the start stage flag, read by no condition
        foreach (var q in touched)
        {
            var pl = PluginOfQuest(q)!;
            if (!pl.Setters.Set.TryGetValue(q, out var set)) continue;
            var reads = Reads(pl.Mod);
            foreach (var n in set.Order())
            {
                var how = pl.Setters.How.GetValueOrDefault((q, n)) ?? [];
                if (how.All(h => h == QuestStages.State.StartFlag) || reads.Contains((q, n))) continue;
                var sev = "NOTE";
                Add(sev, "stage-set-unchecked", $"{QuestName(q)} stage {n} is set ({SafeText.OneLine(how[0])}{(how.Count > 1 ? $" and {how.Count - 1} more" : "")}) but no condition in {q.ModKey.FileName} checks it, so setting it changes no dialogue or quest condition (a script may still read it)", q);
            }
        }

        // scene-topic-unused (WARNING): a Scene topic with spoken lines that no scene of the load order uses
        var plugins = touched.Select(q => q.ModKey).Concat(shown.Select(s => s.Key.ModKey)).Distinct().Where(m => pluginOf(m) != null).ToHashSet();
        var used = new HashSet<FormKey>();
        foreach (var sc in x.Scenes.Values)
            foreach (var a in sc.Actions ?? [])
            {
                if (a is IDialogueSceneActionGetter d && !d.Topic.IsNull) used.Add(d.Topic.FormKey);
                if (a is IPlayerDialogueSceneActionGetter pd)
                    foreach (var it in pd.DialogueList)
                    {
                        if (!it.PlayerChoice.IsNull) used.Add(it.PlayerChoice.FormKey);
                        if (!it.NpcResponse.IsNull) used.Add(it.NpcResponse.FormKey);
                    }
            }
        int unusedCount = 0;
        foreach (var (k, t) in x.Topics.OrderBy(t => t.Key.ID))
        {
            if (!plugins.Contains(k.ModKey) || t.Category != DialogTopic.CategoryEnum.Scene || used.Contains(k)) continue;
            if (!x.TopicLines(k).Any(c => c.Info.Responses.Any(r => !string.IsNullOrWhiteSpace(r.ResponseText?.String)))) continue;
            unusedCount++;
            Add("WARNING", "scene-topic-unused", $"{Name(k, typeof(IDialogTopicGetter))} [DIAL:{k}] is a Scene topic with spoken lines, but no scene action or player choice uses it, so its lines never play", k);
        }

        // the stage table: every stage of each touched quest, plus any stage number a condition needs or something sets that it lacks
        foreach (var q in touched)
        {
            var pl = PluginOfQuest(q)!;
            var quest = x.Quests[q];
            var stages = quest.Stages.Select(s => (int)s.Index).Concat(gates.Where(g => g.Quest == q).Select(g => g.Stage))
                .Concat(pl.Setters.How.Keys.Where(k => k.Quest == q).Select(k => k.Stage)).Distinct().Order();
            foreach (var n in stages)
            {
                var st = quest.Stages.FirstOrDefault(s => s.Index == n);
                var note = st == null ? "(no such stage)"
                    : string.Join(" / ", st.LogEntries.Select(e => SafeText.OneLine(e.Note)?.Trim() ?? "").Where(t => t.Length > 0)) is { Length: > 0 } t ? t : "(none)";
                var setBy = pl.Setters.How.GetValueOrDefault((q, n))?.Select(h => SafeText.OneLine(h) ?? "").ToList() ?? [];
                if (setBy.Count == 0 && pl.Setters.UnknownTargetStages.Contains(n)) setBy.Add("maybe a script call whose quest can't be told");
                if (setBy.Count == 0) setBy.Add("nothing found");
                var needed = gates.Where(g => g.Quest == q && g.Stage == n).Select(g => (g.After ? "after: " : "before: ") + g.Where).Distinct().ToList();
                if (needed.Count == 0) needed.Add("nothing");
                o.Stages.Add(new StageRow(q, QuestName(q), n, note, setBy, needed));
            }
        }
        Log.Write($"scenes: rules: {o.Problems.Count(p => p.Rule == "scene-phase-missing")} scene-phase-missing, {o.Problems.Count(p => p.Rule == "scene-no-exit")} scene-no-exit, " +
                  $"{o.Problems.Count(p => p.Rule == "stage-gate-unset")} stage-gate-unset, {o.Problems.Count(p => p.Rule == "stage-set-unchecked")} stage-set-unchecked, " +
                  $"{unusedCount} scene-topic-unused; {touched.Count} quest(s) in the stage table, {o.Stages.Count} row(s)");
        return o;
    }

    /// <summary>A choice leads back to its own choices: every way it can go (its answers, else the player line, plus
    /// any player line that jumps) is "Back to these choices." A choice with no lines at all doesn't count.</summary>
    bool Loops(SceneNode s, int part, Choice c)
    {
        var thens = c.Answers.Where(l => l.HasText || l.SharedFrom != null).Select(l => Then(s, part, l))
            .Concat(c.Player.Where(l => l.Jumps).Select(l => Then(s, part, l))).ToList();
        if (thens.Count == 0 && ChoiceThen(s, part, c) is { Length: > 0 } t) thens.Add(t);
        return thens.Count > 0 && thens.All(t => t == Back);
    }

    static string Where(IEnumerable<string> places)
    {
        var l = places.Distinct().ToList();
        return l.Count <= 3 ? string.Join("; ", l) : string.Join("; ", l.Take(3)) + $" and {l.Count - 3} more";
    }
}

/// <summary>One finding of `scenes` (record: the FormKey it is about).</summary>
internal sealed record SceneProblem(string Severity, string Rule, string Message, string Record);

/// <summary>One row of the stage table: Stage | Author's note | Set by | Needed by.</summary>
internal sealed record StageRow(FormKey Quest, string QuestName, int Stage, string Note, IReadOnlyList<string> SetBy, IReadOnlyList<string> NeededBy);

/// <summary>What SceneTree.Check found: the Problems list and the stage table.</summary>
internal sealed class SceneCheck
{
    public readonly List<SceneProblem> Problems = new();
    public readonly List<StageRow> Stages = new();
}

/// <summary>The plugin that adds a quest or topic, as the stage rules need it: the plugin and its setter table
/// (QuestStages.Setters).</summary>
internal sealed record StagePlugin(IStarfieldModGetter Mod, QuestStages.State Setters);

/// <summary>Conditions in plain words (0.24.0, `scenes`): GetStageDone, HasPerk, GetPerkRank, GetIsID, GetRandomPercent
/// and GetInFaction get words of their own; anything else prints as FindCommand.ConditionText gives it. An OR flag joins
/// a condition to the next one, so a run of ORs prints as one item joined with "or".</summary>
internal static class SceneWords
{
    /// <summary>The conditions as a list of items (each an AND part). A GetIsID of skipNpc is left out (it is the
    /// speaker check of the NPC asked for).</summary>
    public static List<string> Text(IReadOnlyList<IConditionGetter> conds, Func<IFormLinkGetter, string> link, FormKey? skipNpc = null)
    {
        var o = new List<string>();
        var run = new List<string>();
        for (int i = 0; i < conds.Count; i++)
        {
            var c = conds[i];
            if (!(skipNpc is { } n && IsIdOf(c, n))) run.Add(One(c, link));
            if (c.Flags.HasFlag(Condition.Flag.OR) && i + 1 < conds.Count) continue;   // joined to the next one
            if (run.Count > 0) o.Add(string.Join(" or ", run));
            run.Clear();
        }
        return o;
    }

    static bool IsIdOf(IConditionGetter c, FormKey npc) =>
        c.Data is IGetIsIDConditionDataGetter g && g.FirstParameter.Link.FormKey == npc && c.CompareOperator == CompareOperator.EqualTo &&
        c is IConditionFloatGetter f && f.ComparisonValue == 1;

    /// <summary>One condition in words.</summary>
    public static string One(IConditionGetter c, Func<IFormLinkGetter, string> link)
    {
        if (c is IConditionFloatGetter f && Words(c, f.ComparisonValue, link) is { } w) return w;
        var t = FindCommand.ConditionText(c, link);
        return t.StartsWith("OR ", StringComparison.Ordinal) ? t[3..] : t;   // the OR is shown by the "or" join
    }

    static string? Words(IConditionGetter c, float v, Func<IFormLinkGetter, string> link)
    {
        var op = c.CompareOperator;
        bool Eq(float n) => op == CompareOperator.EqualTo && v == n;
        bool Ne(float n) => op == CompareOperator.NotEqualTo && v == n;
        var d = c.Data;
        if (d.RunOnType != Condition.RunOnType.Subject && d.Function.ToString() is not ("GetStageDone" or "GetRandomPercent")) return null;
        string Num(float n) => n.ToString(CultureInfo.InvariantCulture);
        switch (d.Function.ToString())
        {
            case "GetStageDone":
            {
                var q = LinkParam(d, "FirstParameter");
                var stage = d.GetType().GetProperty("SecondParameter")?.GetValue(d);
                if (q == null || stage == null) return null;
                var s = Convert.ToString(stage, CultureInfo.InvariantCulture);
                if (Eq(1) || Ne(0)) return $"after {link(q)} stage {s}";
                if (Eq(0) || Ne(1)) return $"before {link(q)} stage {s}";
                return null;
            }
            case "HasPerk":
            {
                var p = LinkParam(d, "FirstParameter");
                if (p == null) return null;
                if (Eq(1) || Ne(0)) return $"with the perk {link(p)}";
                if (Eq(0) || Ne(1)) return $"without the perk {link(p)}";
                return null;
            }
            case "GetPerkRank":
            {
                var p = LinkParam(d, "FirstParameter");
                if (p == null) return null;
                return op switch
                {
                    CompareOperator.EqualTo => $"at {link(p)} rank {Num(v)}",
                    CompareOperator.GreaterThanOrEqualTo => $"at {link(p)} rank {Num(v)} or more",
                    CompareOperator.GreaterThan => $"at {link(p)} rank {Num(v + 1)} or more",
                    CompareOperator.LessThanOrEqualTo => $"at {link(p)} rank {Num(v)} or less",
                    CompareOperator.LessThan => $"at {link(p)} rank {Num(v - 1)} or less",
                    _ => null,
                };
            }
            case "GetIsID":
            {
                var p = LinkParam(d, "FirstParameter");
                if (p == null) return null;
                if (Eq(1) || Ne(0)) return $"with {link(p)}";
                if (Eq(0) || Ne(1)) return $"not with {link(p)}";
                return null;
            }
            case "GetRandomPercent":
                return op switch
                {
                    CompareOperator.LessThan => $"{Num(v)}% of the time",
                    CompareOperator.LessThanOrEqualTo => $"{Num(v + 1)}% of the time",
                    CompareOperator.GreaterThanOrEqualTo => $"{Num(100 - v)}% of the time",
                    CompareOperator.GreaterThan => $"{Num(99 - v)}% of the time",
                    _ => null,
                };
            case "GetInFaction":
            {
                var p = LinkParam(d, "FirstParameter");
                if (p == null) return null;
                if (Eq(1) || Ne(0)) return $"for members of {link(p)}";
                if (Eq(0) || Ne(1)) return $"for non-members of {link(p)}";
                return null;
            }
        }
        return null;
    }

    /// <summary>A condition parameter's form link (a plain link, or a FormLinkOrIndex that holds a link), or null.</summary>
    static IFormLinkGetter? LinkParam(object data, string name)
    {
        var v = data.GetType().GetProperty(name)?.GetValue(data);
        if (v is IFormLinkGetter fl) return fl.IsNull ? null : fl;
        var gi = v?.GetType().GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition().Name == "IFormLinkOrIndexGetter`1");
        if (gi == null || !(bool)gi.GetMethod("UsesLink")!.Invoke(v, null)!) return null;
        var l = (IFormLinkGetter)gi.GetProperty("Link")!.GetValue(v)!;
        return l.IsNull ? null : l;
    }
}
