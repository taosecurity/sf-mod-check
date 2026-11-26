// sf-mod-check 0.24.0 `scenes` (SmcDialogue step 2): the scene model on a made-up plugin. Design tests 1 (the "Then"
// rules), 8 (conditions in words) and 9 (picking an NPC's and a quest's scenes). The rules and the command come in
// steps 3 and 4. Each fails on 0.23.0, which has no SceneTree.
using System.Text.Json;
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using SfModCheck.Conflict;
using Xunit;

namespace SfModCheck.Tests;

public class ScenesTests
{
    static readonly WriteOptions NoGap = new() { GapCheck = false, FixEffects = false };

    /// <summary>Data\ScBase.esm (full): NPCs ScNpc and ScOther, quest ScQuest with aliases 0 (ScNpc) and 1 (ScOther), and
    /// these scenes:
    ///   ScHub      part "MENU": the player's choices
    ///                Bye     -> an answer with the end-conversation flag                    The conversation ends.
    ///                Again   -> an answer that starts ScHub (no part)                       Back to these choices.
    ///                Tell    -> an answer that starts ScTopicA at "QUESTION PHASE"          Goes to ScTopicA (part ...).
    ///                Typo    -> an answer that starts ScTopicA at "QUESTIONPHASE"           Meant to jump ... doesn't exist.
    ///                Silent  -> no NPC answer; the player line starts ScTopicB              Goes to ScTopicB.
    ///   ScTopicA   parts "START", "QUESTION PHASE", "END"
    ///                1: ScNpc says a line (no jump; a leftover part name with no scene)      On to part 2.
    ///                2: choices: back to START, on to END, to ScHub                         Back to part 1 / On to part 3 / talk menu
    ///                3: ScNpc says a line; a Start Scene action starts ScJumpOnly            The scene ends.
    ///   ScTopicB   part 1: ScNpc says a line that starts ScHub                             Back to the talk menu.
    ///   ScJumpOnly part 1: a timer only (picked for ScNpc only through ScTopicA's jump)
    ///   ScOtherSc  part 1: ScOther says a line (not ScNpc's)
    /// ScTopicA jumps to ScHub's first part and ScTopicB to its second ("TOPLEVELNEST", as vanilla talks do), so
    /// ScHub is the talk menu.</summary>
    sealed class Game : Sandbox
    {
        public readonly string Data, PluginsTxt;
        public readonly FormKey Npc, Other, Quest, Quest2, Hub, TopicA, TopicB, JumpOnly, OtherScene, Loop, Unused, Menu2;

        public Game()
        {
            Data = Sub("Data");
            var b = Plugin.New("ScBase.esm", PluginStyle.Full, "test", "made-up base");
            var npc = b.Npcs.AddNew("ScNpc");
            var other = b.Npcs.AddNew("ScOther");
            Npc = npc.FormKey; Other = other.FormKey;
            var quest = b.Quests.AddNew("ScQuest");
            Quest = quest.FormKey;
            quest.Aliases = new()
            {
                new QuestReferenceAlias { ID = 0, Name = "Npc", UniqueActor = npc.ToNullableLink<INpcGetter>() },
                new QuestReferenceAlias { ID = 1, Name = "Other", UniqueActor = other.ToNullableLink<INpcGetter>() },
            };
            // step 3: stages 0 (start), 10 (set by "Here it is.", needed by ScTopicA part 2), 20 (needed by "Last line.",
            // set by nothing), 30 (set by ScTopicB, read by nothing)
            QuestStage Stage(ushort n, string? note = null)
            {
                var s = new QuestStage { Index = n };
                if (note != null) s.LogEntries.Add(new QuestLogEntry { Note = note });
                quest.Stages.Add(s);
                return s;
            }
            Stage(0, "Start").Flags = QuestStage.Flag.RunOnStart;
            Stage(10, "Heard the story");
            Stage(20, "Never set");
            Stage(30);
            var quest2 = b.Quests.AddNew("ScQuest2");
            Quest2 = quest2.FormKey;
            int topics = 0;
            DialogTopic Topic(DialogTopic.CategoryEnum c = DialogTopic.CategoryEnum.Scene, Quest? on = null)
            {
                on ??= quest;
                var t = new DialogTopic(b) { EditorID = $"ScTopic{++topics}", Quest = on.ToLink<IQuestGetter>(), Category = c, Subtype = DialogTopic.SubtypeEnum.Custom };
                on.DialogTopics.Add(t);
                return t;
            }
            DialogResponses Line(DialogTopic t, string text, Scene? start = null, string? part = null, short flags = 0)
            {
                var i = new DialogResponses(b) { EditorID = "SC_" + text.Replace(" ", "").Replace(".", "") };
                i.Responses.Add(new DialogResponse { ResponseText = text });
                if (start != null) i.StartScene.SetTo(start);
                if (part != null) i.StartScenePhase = part;
                if (flags != 0) i.Flags = new DialogResponseFlags { Flags = flags };
                t.Responses.Add(i);
                return i;
            }
            Scene NewScene(string edid, params string[] parts) => NewSceneOf(quest, edid, parts);
            Scene NewSceneOf(Quest on, string edid, params string[] parts)
            {
                var s = new Scene(b) { EditorID = edid, Quest = on.ToNullableLink<IQuestGetter>() };
                foreach (var p in parts) s.Phases.Add(new ScenePhase { Name = p });
                s.Actions = new();
                on.Scenes.Add(s);
                return s;
            }
            ConditionFloat StageDone(int stage, float value = 1)
            {
                var d = new GetStageDoneConditionData { SecondParameter = stage };
                d.FirstParameter.Link.SetTo(quest);
                return new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = value, Data = d };
            }
            uint index = 0;
            PlayerDialogueSceneAction Choices(Scene s, uint part, params (DialogTopic Player, DialogTopic? Answer)[] items)
            {
                var pd = new PlayerDialogueSceneAction { AliasID = 0, StartPhase = part, EndPhase = part, Index = ++index };
                foreach (var (p, a) in items)
                {
                    var it = new PlayerDialogueSceneActionItem { PlayerChoice = p.ToNullableLink<IDialogTopicGetter>() };
                    if (a != null) it.NpcResponse.SetTo(a);
                    pd.DialogueList.Add(it);
                }
                s.Actions!.Add(pd);
                return pd;
            }
            void Says(Scene s, uint part, int alias, DialogTopic t) =>
                s.Actions!.Add(new DialogueSceneAction { AliasID = alias, StartPhase = part, EndPhase = part, Index = ++index, Topic = t.ToNullableLink<IDialogTopicGetter>() });

            var hub = NewScene("ScHub", "MENU", "TOPLEVELNEST");
            var a = NewScene("ScTopicA", "START", "QUESTION PHASE", "END");
            var bScene = NewScene("ScTopicB", "");
            var jump = NewScene("ScJumpOnly", "");
            var otherScene = NewScene("ScOtherSc", "");
            Hub = hub.FormKey; TopicA = a.FormKey; TopicB = bScene.FormKey; JumpOnly = jump.FormKey; OtherScene = otherScene.FormKey;

            (DialogTopic, DialogTopic?) Pair(string you, string answer, Scene? start = null, string? part = null, short flags = 0, Quest? on = null,
                DialogTopic.CategoryEnum playerCategory = DialogTopic.CategoryEnum.Player)
            {
                var p = Topic(playerCategory, on);
                Line(p, you);
                var r = Topic(on: on);
                Line(r, answer, start, part, flags);
                return (p, r);
            }
            var silentPlayer = Topic(DialogTopic.CategoryEnum.Player);
            Line(silentPlayer, "Silent.", bScene);
            var bye = Pair("Bye.", "Goodbye.", flags: LineNode.GoodbyeFlag);
            var again = Pair("Again.", "Once more.", hub);
            var tell = Pair("Tell.", "Here it is.", a, "QUESTION PHASE");
            tell.Item2!.Responses[0].SetParentQuestStage = new DialogSetParentQuestStage { OnBegin = -1, OnEnd = 10 };
            Choices(hub, 0, bye, again, tell, Pair("Typo.", "Wrong part.", a, "QUESTIONPHASE"), (silentPlayer, null));

            var a1 = Topic();
            Line(a1, "Part one line.", part: "LEFTOVER");
            Says(a, 0, 0, a1);
            a.Phases[1].StartConditions.Add(StageDone(10));
            Choices(a, 1, Pair("Start over.", "From the top.", a, "START"), Pair("Finish.", "To the end.", a, "END"), Pair("Menu.", "Back we go.", hub));
            var a3 = Topic();
            Line(a3, "Last line.").Conditions.Add(StageDone(20));
            Says(a, 2, 0, a3);
            var ss = new StartSceneAction { AliasID = -1, StartPhase = 2, EndPhase = 2, Index = ++index };
            var st = new StartScene();
            st.STSCScene.SetTo(jump);
            ss.StartScenes.Add(st);
            a.Actions!.Add(ss);

            var b1 = Topic();
            Line(b1, "To the menu.", hub, "TOPLEVELNEST").Conditions.Add(StageDone(10, 0));
            Says(bScene, 0, 0, b1);
            bScene.SetParentQuestStage = new SceneSetParentQuestStage { OnBegin = 30, OnEnd = -1 };

            // ScLoop (in ScQuest2, no aliases, so no NPC picks it): every choice comes back to "ASK"; the second
            // player topic is a Scene topic used only as a player choice
            var loop = NewSceneOf(quest2, "ScLoop", "ASK");
            Loop = loop.FormKey;
            Choices(loop, 0, Pair("Ask.", "Ask again.", loop, on: quest2), Pair("Ask more.", "And again.", loop, "ASK", on: quest2, playerCategory: DialogTopic.CategoryEnum.Scene));

            // ScQuest2's tied talk menu: ScMenu2 and ScSub2 each have two scenes jumping to them, but ScMenu2 enters
            // ScSub2 at its greeting part (2 of 3, before its choices) while ScSub2 comes back to ScMenu2's choice part
            // "TOPLEVELNEST", so ScMenu2 is the menu (Astrid's talk menu and methods submenu have this shape)
            var menu2 = NewSceneOf(quest2, "ScMenu2", "START", "TOPLEVELNEST");
            var sub2 = NewSceneOf(quest2, "ScSub2", "", "STARTPHASE", "SUBNEST");
            var t1 = NewSceneOf(quest2, "ScTalk1", "");
            var t2 = NewSceneOf(quest2, "ScTalk2", "");
            Menu2 = menu2.FormKey;
            Choices(menu2, 1, Pair("Methods?", "Sure.", sub2, "STARTPHASE", on: quest2), Pair("Talk one?", "Talk one.", t1, on: quest2));
            var greet2 = Topic(on: quest2);
            Line(greet2, "Ask away.");
            Says(sub2, 1, 0, greet2);
            Choices(sub2, 2, Pair("Back up.", "Fine.", menu2, "TOPLEVELNEST", on: quest2), Pair("Talk two?", "Talk two.", t2, on: quest2));
            var t1l = Topic(on: quest2);
            Line(t1l, "Done one.", menu2, "TOPLEVELNEST");
            Says(t1, 0, 0, t1l);
            var t2l = Topic(on: quest2);
            Line(t2l, "Done two.", sub2, "SUBNEST");
            Says(t2, 0, 0, t2l);

            var unused = Topic();
            unused.EditorID = "ScUnusedTopic";
            Unused = unused.FormKey;
            Line(unused, "Nobody hears this.");
            jump.Actions!.Add(new TimerSceneAction { AliasID = -1, StartPhase = 0, EndPhase = 0, Index = ++index });
            var o1 = Topic();
            Line(o1, "Not ScNpc.");
            Says(otherScene, 0, 1, o1);

            using (var g = new GameData(Data)) PluginWriter.Write(b, Data, g, NoGap);
            PluginsTxt = Path.Combine(Dir, "Plugins.txt");
            File.WriteAllLines(PluginsTxt, ["*ScBase.esm"]);
        }

        /// <summary>ScBase.esm as read by the last Open().</summary>
        public IStarfieldModGetter? BaseMod;

        public (SceneTree Tree, Action Close) Open()
        {
            var lo = LoadOrderSetup.Build(Data, PluginsTxt);
            var index = DialogueIndex.Build(lo.Plugins, null);
            BaseMod = (IStarfieldModGetter)lo.Plugins.Single(p => p.FileName == "ScBase.esm").Mod;
            return (new SceneTree(index), () => { foreach (var p in lo.Plugins) p.Mod.Dispose(); });
        }

        /// <summary>ScBase.esm's setter table (no scripts, so none to read), changed by change if given.</summary>
        public Func<ModKey, StagePlugin?> Plugins(Action<QuestStages.State>? change = null)
        {
            var st = QuestStages.Setters(BaseMod!, _ => (null, "not found"), r => r.EditorID ?? r.FormKey.ToString());
            change?.Invoke(st);
            var pl = new StagePlugin(BaseMod!, st);
            return m => m == BaseMod!.ModKey ? pl : null;
        }
    }

    static LineNode Answer(SceneNode s, int part, string text) =>
        s.Parts[part].Steps.SelectMany(st => st.Lines.Concat(st.Choices.SelectMany(c => c.Answers))).Single(l => l.Text == text);

    [Fact]
    public void Then_covers_each_way_a_line_can_go()
    {
        using var g = new Game();
        var (tree, close) = g.Open();
        try
        {
            Assert.Equal(g.Hub, tree.Hub(g.Quest));
            Assert.Equal(g.Menu2, tree.Hub(g.Quest2));   // a tie, settled by which one is entered at its first part
            var hub = tree.Scene(g.Hub)!;
            var a = tree.Scene(g.TopicA)!;
            var b = tree.Scene(g.TopicB)!;
            Assert.Equal("The conversation ends.", tree.Then(hub, 0, Answer(hub, 0, "Goodbye.")));
            Assert.Equal("Back to these choices.", tree.Then(hub, 0, Answer(hub, 0, "Once more.")));
            Assert.Equal("Goes to ScTopicA (part \"QUESTION PHASE\").", tree.Then(hub, 0, Answer(hub, 0, "Here it is.")));
            Assert.Equal("Meant to jump to \"QUESTIONPHASE\", a part that doesn't exist.", tree.Then(hub, 0, Answer(hub, 0, "Wrong part.")));
            var silent = hub.Parts[0].Steps.Single().Choices.Single(c => c.ResponseTopic == null);
            Assert.Equal("Goes to ScTopicB.", tree.ChoiceThen(hub, 0, silent));

            Assert.Equal("On to part 2.", tree.Then(a, 0, Answer(a, 0, "Part one line.")));
            Assert.Contains(tree.Remarks, r => r.Contains("names the part \"LEFTOVER\" but no scene to start"));
            Assert.Equal("Back to part 1.", tree.Then(a, 1, Answer(a, 1, "From the top.")));
            Assert.Equal("On to part 3.", tree.Then(a, 1, Answer(a, 1, "To the end.")));
            Assert.Equal("Back to the talk menu.", tree.Then(a, 1, Answer(a, 1, "Back we go.")));
            Assert.Equal("The scene ends.", tree.Then(a, 2, Answer(a, 2, "Last line.")));
            Assert.Equal("Back to the talk menu.", tree.Then(b, 0, Answer(b, 0, "To the menu.")));

            // the model itself: parts, speakers, the Start Scene action and the timer
            Assert.Equal(["START", "QUESTION PHASE", "END"], a.Parts.Select(p => p.Name));
            Assert.Equal("ScNpc", a.Parts[0].Steps.Single().Speaker);
            var start = a.Parts[2].Steps.Single(s => s.Type == StepType.Other);
            Assert.Equal("starts scene ScJumpOnly", start.Text);
            Assert.Equal(g.JumpOnly, start.Starts.Single().Scene);
            Assert.Equal("timer action", tree.Scene(g.JumpOnly)!.Parts[0].Steps.Single().Text);
            Assert.True(Answer(hub, 0, "Goodbye.").Goodbye);

            // --hub: another scene is the talk menu; a jump to ScHub is then an ordinary jump
            tree.HubOverride = g.TopicB;
            Assert.Equal("Goes to ScHub (part \"TOPLEVELNEST\").", tree.Then(b, 0, Answer(b, 0, "To the menu.")));
            Assert.Equal("Back to the talk menu.", tree.ChoiceThen(hub, 0, silent));
        }
        finally { close(); }
    }

    [Fact]
    public void An_npc_gets_the_scenes_it_speaks_in_and_those_they_jump_to_and_a_quest_gets_all_of_its()
    {
        using var g = new Game();
        var (tree, close) = g.Open();
        try
        {
            var why = new Dictionary<FormKey, string>();
            var npc = tree.ForNpc(g.Npc, why).Select(s => s.Key).ToList();
            Assert.Equal(g.Hub, npc[0]);   // the talk menu first
            Assert.Equal(new[] { g.Hub, g.TopicA, g.TopicB, g.JumpOnly }.ToHashSet(), npc.ToHashSet());
            Assert.Equal(4, npc.Count);
            Assert.StartsWith("jumped to from ScTopicA", why[g.JumpOnly]);
            Assert.StartsWith("spoken by alias 0", why[g.TopicB]);

            Assert.Equal([g.OtherScene], tree.ForNpc(g.Other).Select(s => s.Key));
            var quest = tree.ForQuest(g.Quest).Select(s => s.Key).ToList();
            Assert.Equal([g.Hub, g.TopicA, g.TopicB, g.JumpOnly, g.OtherScene], quest);   // FormID order
        }
        finally { close(); }
    }

    static ConditionFloat Cond(ConditionData d, CompareOperator op = CompareOperator.EqualTo, float value = 1, bool or = false) =>
        new() { CompareOperator = op, ComparisonValue = value, Data = d, Flags = or ? Condition.Flag.OR : 0 };

    [Fact]
    public void Conditions_read_in_plain_words_and_or_chains_join()
    {
        var mod = new StarfieldMod(ModKey.FromFileName("ScWords.esm"), StarfieldRelease.Starfield);
        var q = mod.Quests.AddNew("WQuest");
        var perk = mod.Perks.AddNew("WPerk");
        var npc = mod.Npcs.AddNew("WNpc");
        var who = mod.Npcs.AddNew("WOther");
        var fac = mod.Factions.AddNew("WFaction");
        var names = new Dictionary<FormKey, string> { [q.FormKey] = "WQuest", [perk.FormKey] = "WPerk", [npc.FormKey] = "WNpc", [who.FormKey] = "WOther", [fac.FormKey] = "WFaction" };
        string Link(IFormLinkGetter l) => names[l.FormKey];

        ConditionFloat Stage(float v, CompareOperator op = CompareOperator.EqualTo) { var d = new GetStageDoneConditionData { SecondParameter = 20 }; d.FirstParameter.Link.SetTo(q); return Cond(d, op, v); }
        ConditionFloat HasPerk(float v) { var d = new HasPerkConditionData(); d.FirstParameter.Link.SetTo(perk); return Cond(d, value: v); }
        ConditionFloat Rank(CompareOperator op, float v) { var d = new GetPerkRankConditionData(); d.FirstParameter.Link.SetTo(perk); return Cond(d, op, v); }
        ConditionFloat IsId(INpcGetter n, bool or = false) { var d = new GetIsIDConditionData(); d.FirstParameter.Link.SetTo(n.FormKey); return Cond(d, or: or); }
        ConditionFloat Faction(float v, bool or = false) { var d = new GetInFactionConditionData(); d.FirstParameter.Link.SetTo(fac); return Cond(d, value: v, or: or); }

        string One(IConditionGetter c) => SceneWords.One(c, Link);
        Assert.Equal("after WQuest stage 20", One(Stage(1)));
        Assert.Equal("before WQuest stage 20", One(Stage(0)));
        Assert.Equal("after WQuest stage 20", One(Stage(0, CompareOperator.NotEqualTo)));
        Assert.Equal("with the perk WPerk", One(HasPerk(1)));
        Assert.Equal("without the perk WPerk", One(HasPerk(0)));
        Assert.Equal("at WPerk rank 2", One(Rank(CompareOperator.EqualTo, 2)));
        Assert.Equal("at WPerk rank 2 or more", One(Rank(CompareOperator.GreaterThanOrEqualTo, 2)));
        Assert.Equal("at WPerk rank 2 or less", One(Rank(CompareOperator.LessThan, 3)));
        Assert.Equal("with WOther", One(IsId(who)));
        Assert.Equal("30% of the time", One(Cond(new GetRandomPercentConditionData(), CompareOperator.LessThan, 30)));
        Assert.Equal("for members of WFaction", One(Faction(1)));
        Assert.Equal("for non-members of WFaction", One(Faction(0)));
        // anything else as find prints it
        Assert.StartsWith("GetLevel() >= 5", One(Cond(new GetLevelConditionData(), CompareOperator.GreaterThanOrEqualTo, 5)));

        // an OR flag joins a condition to the next one; the NPC asked for is the speaker check and is left out
        var words = SceneWords.Text([IsId(npc), Faction(1, or: true), Stage(1), HasPerk(1)], Link, npc.FormKey);
        Assert.Equal(["for members of WFaction or after WQuest stage 20", "with the perk WPerk"], words);
        Assert.Equal(["with WNpc or with WOther"], SceneWords.Text([IsId(npc, or: true), IsId(who)], Link));
        Assert.Equal(["with WOther"], SceneWords.Text([IsId(npc, or: true), IsId(who)], Link, npc.FormKey));
    }

    // ---- step 3: the five rules (design tests 2 to 6) and the stage table (design test 7) ----

    static List<SceneNode> All(SceneTree tree, Game g) => tree.ForQuest(g.Quest).Concat(tree.ForQuest(g.Quest2)).ToList();

    [Fact]
    public void Scene_rules_find_each_problem_once_and_pass_what_is_fine()
    {
        using var g = new Game();
        var (tree, close) = g.Open();
        try
        {
            var r = tree.Check(All(tree, g), g.Plugins());
            SceneProblem One(string rule) => Assert.Single(r.Problems, p => p.Rule == rule);

            // scene-phase-missing: "QUESTIONPHASE" is found with the spaces-and-case suggestion; "QUESTION PHASE"
            // (the Tell answer) matches and is not reported
            var phase = One("scene-phase-missing");
            Assert.Equal("ERROR", phase.Severity);
            Assert.Contains("ScHub: line ", phase.Message);
            Assert.Contains("starts ScTopicA [SCEN:", phase.Message);
            Assert.Contains("at the part \"QUESTIONPHASE\", which it doesn't have", phase.Message);
            Assert.EndsWith("Did you mean \"QUESTION PHASE\"?", phase.Message);

            // scene-no-exit: ScLoop's two choices both come back; ScHub (Goodbye) and ScTopicA's choices have a way out
            var loop = One("scene-no-exit");
            Assert.Equal("WARNING", loop.Severity);
            Assert.StartsWith("ScLoop, part 1: every one of the 2 choice(s) leads back", loop.Message);
            Assert.Equal(g.Loop.ToString(), loop.Record);

            // stage-gate-unset: stage 20 (nothing sets it); stage 10 is set by the Tell answer, so ScTopicA part 2 is fine
            var gate = One("stage-gate-unset");
            Assert.Equal("WARNING", gate.Severity);
            Assert.StartsWith("ScTopicA, part 3: needs ScQuest stage 20 done, but nothing in ScBase.esm sets that stage", gate.Message);

            // stage-set-unchecked: stage 30 (ScTopicB sets it, no condition reads it); 10 is read, 0 is only the start flag
            var set = One("stage-set-unchecked");
            Assert.Equal("NOTE", set.Severity);
            Assert.StartsWith("ScQuest stage 30 is set (ScTopicB (on begin)) but no condition in ScBase.esm checks it", set.Message);

            // scene-topic-unused: ScUnusedTopic; the Scene topic used only as ScLoop's player choice is fine
            var unused = One("scene-topic-unused");
            Assert.Equal("WARNING", unused.Severity);
            Assert.StartsWith("ScUnusedTopic [DIAL:", unused.Message);
            Assert.Equal(g.Unused.ToString(), unused.Record);
            Assert.Equal(5, r.Problems.Count);
        }
        finally { close(); }
    }

    [Fact]
    public void Stage_and_topic_rules_skip_a_plugin_out_of_scope_and_a_gate_turns_into_a_note_when_setters_are_unsure()
    {
        using var g = new Game();
        var (tree, close) = g.Open();
        try
        {
            // a master's (or an official) plugin: no stage rules, no topic rule, no stage table; the scene rules still run
            var none = tree.Check(All(tree, g), _ => null);
            Assert.Equal(["scene-no-exit", "scene-phase-missing"], none.Problems.Select(p => p.Rule).Order());
            Assert.Empty(none.Stages);

            // a script that couldn't be read: the gate is a NOTE that says so
            var unread = tree.Check(All(tree, g), g.Plugins(st => st.Unreadable["SC:Missing"] = "not found"));
            var gate = Assert.Single(unread.Problems, p => p.Rule == "stage-gate-unset");
            Assert.Equal("NOTE", gate.Severity);
            Assert.Contains("1 attached script(s) couldn't be found or read (e.g. SC:Missing: not found), so it wasn't checked", gate.Message);

            // a SetStage call with a number worked out at run time: also a NOTE
            var dyn = tree.Check(All(tree, g), g.Plugins(st => st.Dynamic[g.Quest] = 2));
            gate = Assert.Single(dyn.Problems, p => p.Rule == "stage-gate-unset");
            Assert.Equal("NOTE", gate.Severity);
            Assert.Contains("2 SetStage call(s) take the stage number from a value worked out while the game runs", gate.Message);

            // a call whose quest can't be told that uses the number counts as set
            var unknown = tree.Check(All(tree, g), g.Plugins(st => st.UnknownTargetStages.Add(20)));
            Assert.DoesNotContain(unknown.Problems, p => p.Rule == "stage-gate-unset");
        }
        finally { close(); }
    }

    [Fact]
    public void Stage_table_lists_each_stage_with_its_note_setters_and_needs()
    {
        using var g = new Game();
        var (tree, close) = g.Open();
        try
        {
            var rows = tree.Check(All(tree, g), g.Plugins()).Stages;
            Assert.All(rows, row => Assert.Equal(g.Quest, row.Quest));   // ScQuest2 has no stages and no gates
            Assert.Equal([0, 10, 20, 30], rows.Select(row => row.Stage));
            var s0 = rows[0];
            Assert.Equal("Start", s0.Note);
            Assert.Equal(["the start stage flag"], s0.SetBy);
            Assert.Equal(["nothing"], s0.NeededBy);
            var s10 = rows[1];
            Assert.Equal("Heard the story", s10.Note);
            Assert.Equal(["SC_Hereitis (on end)"], s10.SetBy);
            Assert.Equal(["after: ScTopicA, part 2", "before: ScTopicB, part 1"], s10.NeededBy);
            var s20 = rows[2];
            Assert.Equal("Never set", s20.Note);
            Assert.Equal(["nothing found"], s20.SetBy);
            Assert.Equal(["after: ScTopicA, part 3"], s20.NeededBy);
            var s30 = rows[3];
            Assert.Equal("(none)", s30.Note);
            Assert.Equal(["ScTopicB (on begin)"], s30.SetBy);
            Assert.Equal("ScQuest", s30.QuestName);

            // a stage something sets that the quest doesn't have still gets a row
            var extra = tree.Check(All(tree, g), g.Plugins(st => st.How[(g.Quest, 45)] = ["a made-up setter"])).Stages;
            Assert.Equal([0, 10, 20, 30, 45], extra.Select(row => row.Stage));
            Assert.Equal("(no such stage)", extra[4].Note);
            Assert.Equal(["a made-up setter"], extra[4].SetBy);
            Assert.Equal(["nothing"], extra[4].NeededBy);
        }
        finally { close(); }
    }

    [Fact]
    public void Scene_phase_missing_runs_in_check_and_lists_the_parts_when_nothing_is_close()
    {
        using var w = new World();
        var p = w.Build("phase", m =>
        {
            var q = m.Quests.AddNew("SFM_PhaseQuest");
            var s = new Scene(m) { EditorID = "SFM_PhaseScene", Quest = q.ToNullableLink<IQuestGetter>() };
            s.Phases.Add(new ScenePhase { Name = "QUESTION PHASE" });
            s.Phases.Add(new ScenePhase { Name = "END" });
            q.Scenes.Add(s);
            var t = new DialogTopic(m) { EditorID = "SFM_PhaseTopic", Quest = q.ToLink<IQuestGetter>(), Category = DialogTopic.CategoryEnum.Scene, Subtype = DialogTopic.SubtypeEnum.Custom };
            q.DialogTopics.Add(t);
            DialogResponses Info(string edid, string part)
            {
                var i = new DialogResponses(m) { EditorID = edid, StartScenePhase = part };
                i.StartScene.SetTo(s);
                i.Responses.Add(new DialogResponse { ResponseText = "Made-up line." });
                t.Responses.Add(i);
                return i;
            }
            Info("SFM_PhaseTypo", "QUESTIONPHASE");
            Info("SFM_PhaseRight", "QUESTION PHASE");
        });
        var r = w.Check(p);
        Expect.Finding(r, "ERROR", "scene-phase-missing", "SFM_PhaseTypo");
        var f = Assert.Single(r.Findings, x => x.Check == "scene-phase-missing");
        Assert.Contains("starts SFM_PhaseScene [SCEN:", f.Text);
        Assert.EndsWith("Did you mean \"QUESTION PHASE\"?", f.Text);

        var far = Rules.ScenePhaseText("L", "S", "SOMEWHERE", ["QUESTION PHASE", "", "END"]);
        Assert.EndsWith("Its parts are \"QUESTION PHASE\", \"END\".", far);
        Assert.EndsWith("Its parts have no names.", Rules.ScenePhaseText("L", "S", "X", [""]));
    }

    // ---- step 4: the command (design test 10) ----

    static (int Code, string Stdout, string Stderr) Scenes(Game g, params string[] args) =>
        ExeTests.RunSplit(new[] { "scenes" }.Concat(args).Concat(["--plugins", g.PluginsTxt, "--data", g.Data]).ToArray());

    [Fact]
    public void Scenes_command_prints_the_tree_and_its_json_matches_the_text_and_max_caps_only_scenes()
    {
        using var g = new Game();
        var (code, text, err) = Scenes(g, "ScNpc");
        Assert.True(code == 0, text + err);
        Assert.StartsWith("# Conversations of ScNpc", text);
        Assert.Contains("## ScHub", text);
        Assert.Contains("| You say | ScNpc answers | Then |", text);
        Assert.Contains("| \"Bye.\" | \"Goodbye.\" | The conversation ends. |", text);
        Assert.Contains("Back to the talk menu.", text);
        Assert.Contains("[sets stage 10]", text);
        Assert.Contains("Part 2 \"QUESTION PHASE\" (runs only after ScQuest stage 10; otherwise skipped).", text);
        Assert.Contains("- \"Last line.\" (only after ScQuest stage 20)", text);
        Assert.Contains("- ERROR scene-phase-missing: ScHub: line ", text);
        Assert.Contains("- HINT (scene-phase-missing): ", text);
        Assert.Contains("## Stages of ScQuest", text);
        Assert.Contains("| 10 | Heard the story | SC_Hereitis (on end) | after: ScTopicA, part 2; before: ScTopicB, part 1 |", text);
        Assert.Contains("names the part \"LEFTOVER\" but no scene to start", text);
        var result = text.TrimEnd().Split('\n')[^1];
        Assert.StartsWith("RESULT: 4 scenes, 1 error, 2 warnings, 1 note", result);   // ScLoop isn't ScNpc's, so no scene-no-exit

        // JSON: the same scenes and counts
        (code, var js, err) = Scenes(g, "ScNpc", "--json");
        Assert.True(code == 0, js + err);
        var j = JsonDocument.Parse(js).RootElement;
        Assert.Equal("npc", j.GetProperty("kind").GetString());
        Assert.Equal(g.Hub.ToString(), j.GetProperty("hub")[0].GetString());
        var scenes = j.GetProperty("scenes").EnumerateArray().ToList();
        Assert.Equal(4, scenes.Count);
        Assert.Equal(g.Hub.ToString(), scenes[0].GetProperty("formKey").GetString());
        var r = j.GetProperty("result");
        Assert.Equal(4, r.GetProperty("scenes").GetInt32());
        Assert.Equal(4, r.GetProperty("shown").GetInt32());
        var problems = j.GetProperty("problems").EnumerateArray().ToList();
        Assert.Equal(1, r.GetProperty("errors").GetInt32());
        Assert.Equal(2, r.GetProperty("warnings").GetInt32());
        Assert.Equal(1, r.GetProperty("notes").GetInt32());
        Assert.Equal(4, problems.Count);
        Assert.Equal(problems.Count, problems.Count(p => text.Contains($"- {p.GetProperty("severity").GetString()} {p.GetProperty("rule").GetString()}: {p.GetProperty("message").GetString()}")));
        Assert.Equal(4, j.GetProperty("stages").GetArrayLength());
        var bye = scenes[0].GetProperty("parts")[0].GetProperty("steps")[0].GetProperty("choices")[0];
        Assert.Equal("The conversation ends.", bye.GetProperty("then").GetString());
        Assert.True(bye.GetProperty("answers")[0].GetProperty("goodbye").GetBoolean());
        foreach (var key in new[] { "tool", "version", "query", "source", "problems", "stages", "remarks", "unread" })
            Assert.True(j.TryGetProperty(key, out _), key);

        // --max caps the scenes only
        (code, js, err) = Scenes(g, "ScNpc", "--json", "--max", "1");
        Assert.True(code == 0, js + err);
        var capped = JsonDocument.Parse(js).RootElement;
        Assert.Equal(1, capped.GetProperty("scenes").GetArrayLength());
        Assert.Equal(4, capped.GetProperty("result").GetProperty("scenes").GetInt32());
        Assert.Equal(1, capped.GetProperty("result").GetProperty("shown").GetInt32());
        Assert.Equal(4, capped.GetProperty("problems").GetArrayLength());
        Assert.Equal(4, capped.GetProperty("stages").GetArrayLength());
        (code, text, err) = Scenes(g, "ScNpc", "--max", "1");
        Assert.Contains("... and 3 more scenes (--max 0 shows them all)", text);
        Assert.DoesNotContain("## ScTopicA", text);
        Assert.Contains("## Stages of ScQuest", text);

        // a quest: all of its scenes; ScLoop's quest finds the loop
        (code, js, err) = Scenes(g, "ScQuest2", "--json");
        Assert.True(code == 0, js + err);
        j = JsonDocument.Parse(js).RootElement;
        Assert.Equal("quest", j.GetProperty("kind").GetString());
        Assert.Contains(j.GetProperty("problems").EnumerateArray(), p => p.GetProperty("rule").GetString() == "scene-no-exit");
    }

    [Fact]
    public void Scenes_command_writes_out_and_exits_1_for_an_unknown_npc_and_2_for_a_wrong_record()
    {
        using var g = new Game();
        var md = Path.Combine(g.Dir, "tree.md");
        var (code, stdout, err) = Scenes(g, "ScNpc", "--out", md);
        Assert.True(code == 0, stdout + err);
        Assert.DoesNotContain("## ScHub", stdout);
        Assert.Contains("Wrote ", stdout);
        Assert.StartsWith("RESULT: 4 scenes", stdout.TrimEnd().Split('\n')[^1]);
        var file = File.ReadAllText(md);
        Assert.StartsWith("# Conversations of ScNpc", file);
        Assert.Contains("## Problems", file);

        (code, stdout, err) = Scenes(g, "NoSuchNpc");
        Assert.Equal(1, code);
        Assert.Contains("RESULT: NOT FOUND (no record with EditorID NoSuchNpc", stdout);
        (code, stdout, err) = Scenes(g, "NoSuchNpc", "--json");
        Assert.Equal(1, code);
        Assert.Equal("not found", JsonDocument.Parse(stdout).RootElement.GetProperty("result").GetString());

        // a scene is neither an NPC nor a quest
        (code, stdout, err) = Scenes(g, "ScHub");
        Assert.Equal(2, code);
        Assert.Contains("not an NPC or a quest", err);

        // --verbose logs why each scene was picked and the talk menu
        (code, stdout, err) = Scenes(g, "ScNpc", "--verbose", "--out", md);
        Assert.Equal(0, code);
        Assert.Contains("picked ScJumpOnly: jumped to from ScTopicA", err);
        Assert.Contains("talk menu of ScQuest: ScHub", err);
    }
}
