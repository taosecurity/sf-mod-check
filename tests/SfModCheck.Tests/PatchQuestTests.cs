// sf-mod-check patch (0.19.0): a quest's stages, objectives and aliases merge by key, and its condition lists stay one
// value (StarfieldPluginCheck NOTES.md, "0.19.0 (GapFix)", Step 4, tests 1-9 and 19). Each test builds a tiny game in a
// sandbox, runs the built program and reads the patch it writes. No game files are needed.
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Starfield;
using SfModCheck.Conflict;
using Xunit;

namespace SfModCheck.Tests;

public class PatchQuestTests
{
    static readonly WriteOptions NoGap = new() { GapCheck = false, FixEffects = false };
    const string QuestFk = "000800:Base.esm";

    /// <summary>Base.esm (full) with one quest: stages 10 and 20 (one log entry each), objective 10 (one target on
    /// alias 0), reference aliases 0 and 1, and two dialogue conditions chained with OR.</summary>
    sealed class Game : Sandbox
    {
        public readonly string Data;
        public Game(Action<Quest>? baseQuest = null) : this((_, q) => baseQuest?.Invoke(q)) { }

        public Game(Action<StarfieldMod, Quest> baseQuest)
        {
            Data = Sub("Data");
            var b = Plugin.New("Base.esm", PluginStyle.Full, "test", "made-up base game");
            var q = b.Quests.AddNew("B_Quest");
            q.Name = "Base quest";
            q.Stages.Add(Stage(10, "base 10"));
            q.Stages.Add(Stage(20, "base 20"));
            q.Objectives.Add(new QuestObjective { Index = 10, DisplayText = "Go there", Targets = [new QuestObjectiveTarget { AliasID = 0 }] });
            q.Aliases = [new QuestReferenceAlias { ID = 0, Name = "Hero" }, new QuestReferenceAlias { ID = 1, Name = "Friend" }];
            q.DialogConditions.Add(Cond(1, Condition.Flag.OR));
            q.DialogConditions.Add(Cond(2));
            baseQuest(b, q);
            using var g = new GameData(Data);
            PluginWriter.Write(b, Data, g, NoGap);
        }

        public void Mod(string name, Action<Quest> change) => Mod(name, (_, q) => change(q));

        public void Mod(string name, Action<StarfieldMod, Quest> change)
        {
            var mod = Plugin.New(name, PluginStyle.Light, "test", "made-up mod");
            using var g = new GameData(Data);
            change(mod, mod.Quests.GetOrAddAsOverride(g.Open("Base.esm").Quests.First()));
            PluginWriter.Write(mod, Data, g, NoGap);
            mods.Add(name);
        }

        readonly List<string> mods = new();
        public string Out => Path.Combine(Dir, "patch");
        public string Report => File.ReadAllText(Path.Combine(Out, "patch-report.md"));

        public (int Code, string Stdout, string Stderr) Patch(params string[] extra)
        {
            var txt = Path.Combine(Dir, "Plugins.txt");
            File.WriteAllLines(txt, new[] { "Base.esm" }.Concat(mods).Select(a => "*" + a));
            return ExeTests.RunSplit(new[] { "patch", "--data", Data, "--plugins", txt, "--out", Out, "--quiet" }.Concat(extra).ToArray());
        }

        readonly List<GameData> opened = new();

        /// <summary>The patched quest, read from the written patch.</summary>
        public IQuestGetter Quest()
        {
            var g = new GameData(Data, [Out]);
            opened.Add(g);
            return Assert.Single(g.Open("ConflictPatch.esm").Quests);
        }

        public void CloseAll() { foreach (var g in opened) g.Dispose(); opened.Clear(); }

        public override void Dispose()
        {
            foreach (var g in opened) g.Dispose();
            base.Dispose();
        }
    }

    static QuestStage Stage(ushort index, params string[] notes) =>
        new() { Index = index, LogEntries = [.. notes.Select(n => new QuestLogEntry { Note = n })] };

    static ConditionFloat Cond(float value, Condition.Flag flags = 0) =>
        new() { CompareOperator = CompareOperator.EqualTo, Flags = flags, Data = new GetValueConditionData(), ComparisonValue = value };

    static void Ok((int Code, string Stdout, string Stderr) run)
    {
        Assert.True(run.Code == 0, run.Stdout + run.Stderr);
        Assert.DoesNotContain("reads back differently", run.Stdout + run.Stderr);
        Assert.StartsWith("RESULT: OK, patch ", ExeTests.ResultLine(run.Stdout));
    }

    static List<string> Notes(IQuestStageGetter s) => s.LogEntries.Select(e => e.Note ?? "").ToList();

    [Fact]
    public void Two_mods_add_different_stages_both_are_kept_in_index_order()
    {
        using var game = new Game();
        game.Mod("ModA.esm", q => q.Stages.Add(Stage(15, "A 15")));
        game.Mod("ModB.esm", q => q.Stages.Add(Stage(30, "B 30")));
        Ok(game.Patch());
        Assert.Equal(new[] { 10, 15, 20, 30 }, game.Quest().Stages.Select(s => (int)s.Index));
        Assert.Contains("merged by stage Index", game.Report);
    }

    [Fact]
    public void Two_mods_change_one_stage_log_entries_merge_and_one_stage_remains()
    {
        using var game = new Game();
        game.Mod("ModA.esm", q => q.Stages[0].LogEntries.Add(new QuestLogEntry { Note = "A log" }));
        game.Mod("ModB.esm", q => { q.Stages[0].LogEntries.Add(new QuestLogEntry { Note = "B log" }); q.Stages[0].Flags = QuestStage.Flag.KeepInstanceDataFromHereOn; });
        Ok(game.Patch());
        var quest = game.Quest();
        Assert.Equal(new[] { 10, 20 }, quest.Stages.Select(s => (int)s.Index));
        var s10 = quest.Stages[0];
        Assert.Equal(new[] { "base 10", "B log", "A log" }, Notes(s10));
        Assert.Equal(QuestStage.Flag.KeepInstanceDataFromHereOn, s10.Flags);   // only the winner changed the flags
    }

    [Fact]
    public void Both_edit_one_log_entry_the_winner_list_is_kept_with_a_choice_row()
    {
        // code review 0.19.0, round 4: two edits of one log entry must not both be kept
        using var game = new Game();
        game.Mod("ModA.esm", q => { q.Stages[0].LogEntries[0].Note = "A edit"; q.Stages[0].Flags = QuestStage.Flag.KeepInstanceDataFromHereOn; });
        game.Mod("ModB.esm", q => q.Stages[0].LogEntries[0].Note = "B edit");
        Ok(game.Patch());
        var s10 = game.Quest().Stages[0];
        Assert.Equal(new[] { "B edit" }, Notes(s10));
        Assert.Equal(QuestStage.Flag.KeepInstanceDataFromHereOn, s10.Flags);   // ModA's other change still merged
        Assert.Contains("LogEntries; ModB.esm's value is kept", game.Report);
    }

    [Fact]
    public void Both_make_the_same_stage_change_no_clash_row()
    {
        // code review 0.19.0, round 2: one change both made is not a clash
        using var game = new Game();
        game.Mod("ModA.esm", q => { q.Stages[0].LogEntries.Add(new QuestLogEntry { Note = "A log" }); q.Stages[0].Flags = QuestStage.Flag.KeepInstanceDataFromHereOn; });
        game.Mod("ModB.esm", q => { q.Stages[0].LogEntries.Add(new QuestLogEntry { Note = "B log" }); q.Stages[0].Flags = QuestStage.Flag.KeepInstanceDataFromHereOn; });
        Ok(game.Patch());
        var s10 = game.Quest().Stages[0];
        Assert.Equal(new[] { "base 10", "B log", "A log" }, Notes(s10));
        Assert.Equal(QuestStage.Flag.KeepInstanceDataFromHereOn, s10.Flags);
        Assert.DoesNotContain("Flags; ", game.Report);
    }

    [Fact]
    public void Winner_already_holding_the_loser_stage_change_writes_no_copy_of_the_quest()
    {
        // code review 0.19.0, round 2: a stage merged one level down that equals the winner's is no reason to write the
        // quest (an identical copy only adds risk)
        using var game = new Game();
        game.Mod("ModA.esm", q => q.Stages[0].LogEntries.Add(new QuestLogEntry { Note = "A log" }));
        game.Mod("ModB.esm", q => { q.Stages[0].LogEntries.Add(new QuestLogEntry { Note = "A log" }); q.Stages[0].LogEntries.Add(new QuestLogEntry { Note = "B log" }); q.Stages[0].Flags = QuestStage.Flag.KeepInstanceDataFromHereOn; });
        var run = game.Patch();
        Assert.True(run.Code == 0, run.Stdout + run.Stderr);
        Assert.StartsWith("RESULT: OK, nothing to patch", ExeTests.ResultLine(run.Stdout));
        Assert.Contains("the patched record would equal today's, so it is left out", game.Report);
    }

    [Fact]
    public void Both_add_the_same_new_stage_index_winner_kept_with_choice_row()
    {
        using var game = new Game();
        game.Mod("ModA.esm", q => { q.Stages.Add(Stage(50, "A 50")); q.Stages.Add(Stage(15, "A 15")); });
        game.Mod("ModB.esm", q => { q.Stages.Add(Stage(50, "B 50")); q.Stages.Add(Stage(60, "B 60")); });
        Ok(game.Patch());
        var quest = game.Quest();
        Assert.Equal(new[] { 10, 15, 20, 50, 60 }, quest.Stages.Select(s => (int)s.Index));
        Assert.Equal(new[] { "B 50" }, Notes(quest.Stages[3]));
        Assert.Contains("ModA.esm and ModB.esm both add stage 50 with different contents; ModB.esm's is kept", game.Report);
    }

    [Fact]
    public void Loser_only_alias_change_is_forwarded()
    {
        using var game = new Game();
        game.Mod("ModA.esm", q => ((QuestReferenceAlias)q.Aliases[1]).Name = "Best friend");
        game.Mod("ModB.esm", q => q.Name = "Renamed");
        Ok(game.Patch());
        var quest = game.Quest();
        Assert.Equal("Renamed", quest.Name?.String);
        Assert.Equal(new[] { "Hero", "Best friend" }, quest.Aliases.Cast<IQuestReferenceAliasGetter>().Select(a => a.Name));
    }

    [Fact]
    public void Alias_both_changed_winner_kept_and_pick_takes_loser_list()
    {
        using var game = new Game();
        game.Mod("ModA.esm", q => { ((QuestReferenceAlias)q.Aliases[1]).Name = "A friend"; q.Aliases.Add(new QuestReferenceAlias { ID = 2, Name = "A extra" }); });
        game.Mod("ModB.esm", q => ((QuestReferenceAlias)q.Aliases[1]).Name = "B friend");
        Ok(game.Patch());
        Assert.Equal(new[] { "Hero", "B friend", "A extra" }, game.Quest().Aliases.Cast<IQuestReferenceAliasGetter>().Select(a => a.Name));
        Assert.Contains("both changed alias 1; an alias is kept whole", game.Report);
        game.CloseAll();
        Ok(game.Patch("--pick", $"{QuestFk}:Aliases=ModA.esm"));
        Assert.Equal(new[] { "Hero", "A friend", "A extra" }, game.Quest().Aliases.Cast<IQuestReferenceAliasGetter>().Select(a => a.Name));
    }

    [Fact]
    public void Alias_kind_differs_winner_kept_with_message()
    {
        using var game = new Game();
        game.Mod("ModA.esm", q => { q.Aliases.Add(new QuestReferenceAlias { ID = 5, Name = "A ref" }); q.Aliases.Add(new QuestReferenceAlias { ID = 6, Name = "A six" }); });
        game.Mod("ModB.esm", q => q.Aliases.Add(new QuestLocationAlias { ID = 5, Name = "B place" }));
        Ok(game.Patch());
        var quest = game.Quest();
        Assert.Equal(4, quest.Aliases.Count);
        Assert.IsAssignableFrom<IQuestLocationAliasGetter>(quest.Aliases[2]);
        Assert.Contains("ModA.esm makes alias 5 a reference alias and ModB.esm a location alias; one alias can't be both, so ModB.esm's is kept", game.Report);
    }

    [Fact]
    public void New_aliases_append_after_winner_aliases_in_order()
    {
        using var game = new Game();
        game.Mod("ModA.esm", q => { q.Aliases.Add(new QuestReferenceAlias { ID = 4, Name = "A4" }); q.Aliases.Add(new QuestReferenceAlias { ID = 2, Name = "A2" }); });
        game.Mod("ModB.esm", q => q.Aliases.Add(new QuestReferenceAlias { ID = 3, Name = "B3" }));
        Ok(game.Patch());
        Assert.Equal(new uint[] { 0, 1, 3, 4, 2 }, game.Quest().Aliases.Cast<IQuestReferenceAliasGetter>().Select(a => a.ID));
    }

    [Fact]
    public void Objective_targets_merge_and_conditions_stay_one_value()
    {
        using var game = new Game();
        game.Mod("ModA.esm", q => q.Objectives[0].Targets.Add(new QuestObjectiveTarget { AliasID = 1, Conditions = [Cond(7)] }));
        game.Mod("ModB.esm", q => { q.Objectives[0].Targets.Add(new QuestObjectiveTarget { AliasID = 2 }); q.Objectives[0].DisplayText = "Go here"; });
        Ok(game.Patch());
        var o = Assert.Single(game.Quest().Objectives);
        Assert.Equal("Go here", o.DisplayText?.String);
        Assert.Equal(new[] { 0, 2, 1 }, o.Targets.Select(t => t.AliasID));
        // the target's conditions came whole with it
        Assert.Equal(7f, Assert.IsAssignableFrom<IConditionFloatGetter>(Assert.Single(o.Targets[2].Conditions)).ComparisonValue);
    }

    [Fact]
    public void Objective_target_on_an_alias_the_merge_removed_gets_a_note()
    {
        // ModA points objective 10 at alias 1; ModB removes alias 1. The merge keeps ModA's target and ModB's removal,
        // so the target names an alias the patched quest lacks: a Notes line, not a refusal (Step 4, checks after the build).
        // Objective_targets_merge_and_conditions_stay_one_value adds a target on an alias no version has: no note there.
        using var game = new Game();
        game.Mod("ModA.esm", q => q.Objectives[0].Targets.Add(new QuestObjectiveTarget { AliasID = 1 }));
        game.Mod("ModB.esm", q => q.Aliases.RemoveAt(1));
        Ok(game.Patch());
        var quest = game.Quest();
        Assert.Equal(new uint[] { 0 }, quest.Aliases.Cast<IQuestReferenceAliasGetter>().Select(a => a.ID));
        Assert.Equal(new[] { 0, 1 }, Assert.Single(quest.Objectives).Targets.Select(t => t.AliasID));
        var report = game.Report;
        Assert.Contains("objective 10's target names alias 1, which the merged quest doesn't have", report);
        Assert.DoesNotContain("names alias 0", report);
        game.CloseAll();
        using var quiet = new Game();
        quiet.Mod("ModA.esm", q => q.Objectives[0].Targets.Add(new QuestObjectiveTarget { AliasID = 2 }));
        quiet.Mod("ModB.esm", q => q.Name = "Renamed");
        Ok(quiet.Patch());
        Assert.DoesNotContain("which the merged quest doesn't have", quiet.Report);
    }

    [Fact]
    public void Alias_reference_check_takes_a_version_with_no_alias_list()
    {
        // a real merged-patch rebuild had a quest version whose alias list is absent
        var mod = new StarfieldMod(ModKey.FromNameAndExtension("X.esm"), StarfieldRelease.Starfield);
        var bare = mod.Quests.AddNew("Bare");
        bare.Aliases = null!;
        var full = bare.DeepCopy();
        full.Aliases = [new QuestReferenceAlias { ID = 1 }];
        full.Objectives.Add(new QuestObjective { Index = 10, Targets = [new QuestObjectiveTarget { AliasID = 1 }] });
        var patched = full.DeepCopy();
        patched.Aliases = null!;
        Assert.Empty(QuestMerge.MissingAliasRefs(bare, [bare]));
        Assert.Equal(new[] { "objective 10's target names alias 1, which the merged quest doesn't have" },
            QuestMerge.MissingAliasRefs(patched, [bare, full]));
    }

    [Fact]
    public void Quest_dialog_conditions_are_one_value_not_merged()
    {
        using var game = new Game();
        // ModA changes the second condition; ModB, the winner, adds a third. Entry by entry this would mix the two.
        game.Mod("ModA.esm", q => { q.DialogConditions[1] = Cond(9); q.Summary = "A summary"; });
        game.Mod("ModB.esm", q => q.DialogConditions.Add(Cond(3)));
        Ok(game.Patch());
        var quest = game.Quest();
        Assert.Equal("A summary", quest.Summary);
        Assert.Equal(new[] { 1f, 2f, 3f }, quest.DialogConditions.Cast<IConditionFloatGetter>().Select(c => c.ComparisonValue));
        Assert.Contains("DialogConditions: both changed it; the winner ModB.esm's value is kept", game.Report);
    }

    // Not in the design's list: two losers change one stage and the winner leaves the stages alone (the forward path).
    // 0.18.0 wrote stage 10 twice here.
    [Fact]
    public void Two_losers_change_one_stage_the_winner_left_alone_one_stage_10_with_both_logs()
    {
        using var game = new Game();
        game.Mod("ModA.esm", q => { q.Stages[0].LogEntries.Add(new QuestLogEntry { Note = "A log" }); q.Stages[0].Flags = QuestStage.Flag.KeepInstanceDataFromHereOn; });
        game.Mod("ModB.esm", q => q.Stages[0].LogEntries.Add(new QuestLogEntry { Note = "B log" }));
        game.Mod("ModC.esm", q => q.Name = "Renamed");
        Ok(game.Patch());
        var quest = game.Quest();
        Assert.Equal("Renamed", quest.Name?.String);
        Assert.Equal(new[] { 10, 20 }, quest.Stages.Select(s => (int)s.Index));
        Assert.Equal(new[] { "base 10", "A log", "B log" }, Notes(quest.Stages[0]));
        Assert.Equal(QuestStage.Flag.KeepInstanceDataFromHereOn, quest.Stages[0].Flags);
    }

    // ---------- the quest's script data (step 5b, tests 10-12) ----------

    const string FragScript = "MyMod:QF_Quest";

    static ScriptIntProperty Int(string name, int value) => new() { Name = name, Data = value };

    static QuestScriptFragment Frag(ushort stage) => new() { Stage = stage, StageIndex = 0, FragmentName = $"Fragment_Stage_{stage:D4}_Item_00" };

    /// <summary>The base quest's script data: script MyQuestScript (Count 1, Level 1) and the fragment script with
    /// stage 10's fragment.</summary>
    static void BaseVmad(Quest q)
    {
        var vm = new QuestAdapter { Script = new ScriptEntry { Name = FragScript } };
        vm.Scripts.Add(new ScriptEntry { Name = "MyQuestScript", Properties = [Int("Count", 1), Int("Level", 1)] });
        vm.Fragments.Add(Frag(10));
        q.VirtualMachineAdapter = vm;
    }

    static ScriptIntProperty Prop(Quest q, string script, string name) =>
        (ScriptIntProperty)q.VirtualMachineAdapter!.Scripts.First(s => s.Name == script).Properties.First(p => p.Name == name);

    static int Value(IQuestGetter q, string script, string name) =>
        ((IScriptIntPropertyGetter)q.VirtualMachineAdapter!.Scripts.First(s => s.Name == script).Properties.First(p => p.Name == name)).Data;

    /// <summary>A loose compiled fragment script in Data with one function per stage given.</summary>
    static void WriteFragmentScript(Game game, params ushort[] stages)
    {
        var fns = stages.Select(st => new PFn($"Fragment_Stage_{st:D4}_Item_00")).ToArray();
        var path = Path.Combine([game.Data, "Scripts", .. FragScript.Split(':')]) + ".pex";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, PexBytes.Build([new PObj(FragScript, "Quest", States: [("", fns)])]));
    }

    [Fact]
    public void Vmad_properties_merge_by_name()
    {
        using var game = new Game(BaseVmad);
        game.Mod("ModA.esm", q => { Prop(q, "MyQuestScript", "Count").Data = 5; Prop(q, "MyQuestScript", "Level").Data = 3; });
        game.Mod("ModB.esm", q =>
        {
            Prop(q, "MyQuestScript", "Level").Data = 7;
            q.VirtualMachineAdapter!.Scripts.Add(new ScriptEntry { Name = "OtherScript", Properties = [Int("Mode", 2)] });
        });
        Ok(game.Patch());
        var quest = game.Quest();
        Assert.Equal(new[] { "MyQuestScript", "OtherScript" }, quest.VirtualMachineAdapter!.Scripts.Select(s => s.Name));
        Assert.Equal(5, Value(quest, "MyQuestScript", "Count"));   // only ModA changed it
        Assert.Equal(7, Value(quest, "MyQuestScript", "Level"));   // both changed it: the winner's
        Assert.Equal(2, Value(quest, "OtherScript", "Mode"));
        Assert.Contains("merged by script and property name", game.Report);
        Assert.Contains("both changed script MyQuestScript property Level; ModB.esm's value is kept", game.Report);
    }

    // Not in the design's list: the forward path (two losers, the winner left the script data alone).
    [Fact]
    public void Two_losers_change_different_script_properties_both_are_kept()
    {
        using var game = new Game(BaseVmad);
        game.Mod("ModA.esm", q => Prop(q, "MyQuestScript", "Count").Data = 5);
        game.Mod("ModB.esm", q => Prop(q, "MyQuestScript", "Level").Data = 7);
        game.Mod("ModC.esm", q => q.Name = "Renamed");
        Ok(game.Patch());
        var quest = game.Quest();
        Assert.Equal("Renamed", quest.Name?.String);
        Assert.Equal((5, 7), (Value(quest, "MyQuestScript", "Count"), Value(quest, "MyQuestScript", "Level")));
    }

    [Fact]
    public void Different_fragment_scripts_keep_winner_vmad_with_message()
    {
        using var game = new Game(BaseVmad);
        game.Mod("ModA.esm", q => { q.VirtualMachineAdapter!.Script.Name = "MyMod:QF_A"; q.VirtualMachineAdapter.Fragments.Add(Frag(20)); q.Summary = "A summary"; });
        game.Mod("ModB.esm", q => { q.VirtualMachineAdapter!.Script.Name = "MyMod:QF_B"; q.VirtualMachineAdapter.Fragments.Add(Frag(30)); });
        Ok(game.Patch());
        var quest = game.Quest();
        Assert.Equal("A summary", quest.Summary);
        Assert.Equal("MyMod:QF_B", quest.VirtualMachineAdapter!.Script.Name);
        Assert.Equal(new[] { 10, 30 }, quest.VirtualMachineAdapter.Fragments.Select(f => (int)f.Stage));
        Assert.Contains("ModA.esm and ModB.esm attach different stage fragment scripts (MyMod:QF\\_A, MyMod:QF\\_B)", game.Report);
    }

    [Fact]
    public void Same_fragment_script_fragments_merge_by_stage()
    {
        using var game = new Game(BaseVmad);
        WriteFragmentScript(game, 10, 20, 30);
        game.Mod("ModA.esm", q => q.VirtualMachineAdapter!.Fragments.Add(Frag(20)));
        game.Mod("ModB.esm", q => q.VirtualMachineAdapter!.Fragments.Insert(0, Frag(30)));
        Ok(game.Patch("--verbose"));
        var vm = game.Quest().VirtualMachineAdapter!;
        Assert.Equal(FragScript, vm.Script.Name);
        Assert.Equal(new[] { 10, 20, 30 }, vm.Fragments.Select(f => (int)f.Stage));
    }

    // Not in the design's list: the fragment script as the game loads it lacks a kept fragment's function.
    [Fact]
    public void Fragment_missing_from_the_script_keeps_winner_vmad_with_message()
    {
        using var game = new Game(BaseVmad);
        WriteFragmentScript(game, 10, 30);   // no stage 20 function
        game.Mod("ModA.esm", q => { q.VirtualMachineAdapter!.Fragments.Add(Frag(20)); q.Summary = "A summary"; });
        game.Mod("ModB.esm", q => q.VirtualMachineAdapter!.Fragments.Add(Frag(30)));
        Ok(game.Patch());
        Assert.Equal(new[] { 10, 30 }, game.Quest().VirtualMachineAdapter!.Fragments.Select(f => (int)f.Stage));
        Assert.Contains("has no function Fragment\\_Stage\\_0020\\_Item\\_00", game.Report);
    }

    [Fact]
    public void Quest_with_only_scalar_changes_writes_as_0_18_did()
    {
        using var game = new Game();
        game.Mod("ModA.esm", q => q.Summary = "A summary");
        game.Mod("ModB.esm", q => q.Name = "Renamed");
        Ok(game.Patch());
        var quest = game.Quest();
        Assert.Equal(("Renamed", "A summary"), (quest.Name?.String, quest.Summary));
        Assert.Equal(new[] { 10, 20 }, quest.Stages.Select(s => (int)s.Index));
        Assert.Equal(new[] { "base 10" }, Notes(quest.Stages[0]));
        Assert.Equal(2, quest.Aliases.Count);
        Assert.Equal(2, quest.DialogConditions.Count);
        Assert.DoesNotContain("merged by", game.Report);
    }

    // ---------- dialogue topics and lines (step 5c, tests 13-18) ----------

    /// <summary>The base quest's dialogue: topic B_Topic with lines B_Info1 (one response, one condition) and B_Info2,
    /// listed in that order in its TopicInfoList.</summary>
    static void BaseDialog(StarfieldMod b, Quest q)
    {
        var topic = new DialogTopic(b) { EditorID = "B_Topic", Quest = q.ToLink<IQuestGetter>(), Priority = 50, TopicInfoList = [] };
        q.DialogTopics.Add(topic);
        foreach (var (edid, text) in new[] { ("B_Info1", "Hello"), ("B_Info2", "Goodbye") })
        {
            var info = new DialogResponses(b) { EditorID = edid, Prompt = "base prompt" };
            info.Responses.Add(new DialogResponse { ResponseText = text, WEMFile = 1 });
            info.Conditions.Add(Cond(1));
            topic.Responses.Add(info);
            topic.TopicInfoList.Add(info.ToLink<IDialogResponsesGetter>());
        }
    }

    static DialogTopic Topic(Quest q) => q.DialogTopics.Single(t => t.EditorID == "B_Topic");
    static DialogResponses Info(Quest q, string edid) => Topic(q).Responses.Single(i => i.EditorID == edid);

    [Fact]
    public void Info_change_lost_by_two_mods_is_patched_with_parent_topic_and_quest_as_today()
    {
        using var game = new Game(BaseDialog);
        game.Mod("ModA.esm", q => Info(q, "B_Info1").Prompt = "A prompt");
        game.Mod("ModB.esm", q => Info(q, "B_Info1").StartScenePhase = "B phase");
        Ok(game.Patch("--verbose"));
        var quest = game.Quest();
        Assert.Equal("Base quest", quest.Name?.String);
        var topic = Assert.Single(quest.DialogTopics);
        Assert.Equal("B_Topic", topic.EditorID);
        Assert.Equal(50f, topic.Priority);
        var info = Assert.Single(topic.Responses);   // only the patched line, not B_Info2
        Assert.Equal("B_Info1", info.EditorID);
        Assert.Equal(("A prompt", "B phase"), (info.Prompt?.String, info.StartScenePhase));
        Assert.Equal("Hello", Assert.Single(info.Responses).ResponseText?.String);
        Assert.Contains("is in the patch as the parent of 1 patched dialogue line", game.Report);
        Assert.Contains("is in the patch as the parent of 1 patched topic", game.Report);
    }

    [Fact]
    public void Info_responses_are_one_value()
    {
        using var game = new Game(BaseDialog);
        game.Mod("ModA.esm", q => { var i = Info(q, "B_Info1"); i.Responses[0].ResponseText = "A hello"; i.Prompt = "A prompt"; i.Conditions[0] = Cond(5); });
        game.Mod("ModB.esm", q => { var i = Info(q, "B_Info1"); i.Responses.Add(new DialogResponse { ResponseText = "B more", WEMFile = 2 }); i.Conditions.Add(Cond(6)); });
        Ok(game.Patch());
        var info = Assert.Single(Assert.Single(game.Quest().DialogTopics).Responses);
        Assert.Equal("A prompt", info.Prompt?.String);
        // the winner's responses and conditions whole, never a mix of the two
        Assert.Equal(new[] { "Hello", "B more" }, info.Responses.Select(r => r.ResponseText?.String));
        Assert.Equal(new[] { 1f, 6f }, info.Conditions.Cast<IConditionFloatGetter>().Select(c => c.ComparisonValue));
        Assert.Contains("Responses: both changed it; the winner ModB.esm's value is kept", game.Report);
    }

    [Fact]
    public void Info_in_two_topics_is_not_built_with_message()
    {
        using var game = new Game(BaseDialog);
        game.Mod("ModA.esm", (m, q) =>
        {
            // ModA moves B_Info1 into a topic of its own and changes it
            var info = Info(q, "B_Info1");
            Topic(q).Responses.Remove(info);
            var mine = new DialogTopic(m) { EditorID = "A_Topic", Quest = q.ToLink<IQuestGetter>() };
            mine.Responses.Add(info);
            q.DialogTopics.Add(mine);
            info.Prompt = "A prompt";
            q.Summary = "A summary";
        });
        game.Mod("ModB.esm", q => { Info(q, "B_Info1").StartScenePhase = "B phase"; q.Name = "Renamed"; });
        Ok(game.Patch());
        Assert.Contains("this dialogue line sits under topic B\\_Topic in Base.esm and topic A\\_Topic in ModA.esm; a line can't be in two topics, so it is not patched: pick one in xEdit", game.Report);
        var quest = game.Quest();
        Assert.Equal(("Renamed", "A summary"), (quest.Name?.String, quest.Summary));
        Assert.Empty(quest.DialogTopics);
    }

    // Not in the design's list: the topic form of the refusal (a topic under two quests), and its lines go with it.
    [Fact]
    public void Topic_in_two_quests_is_not_built_and_its_lines_go_with_it()
    {
        using var game = new Game(BaseDialog);
        game.Mod("ModA.esm", (m, q) =>
        {
            var t = Topic(q);
            q.DialogTopics.Remove(t);
            var other = m.Quests.AddNew("A_Quest");
            t.Quest = other.ToLink<IQuestGetter>();
            t.Priority = 70;
            other.DialogTopics.Add(t);
            t.Responses.Single(i => i.EditorID == "B_Info1").Prompt = "A prompt";
            q.Summary = "A summary";
        });
        game.Mod("ModB.esm", q => { Topic(q).Priority = 80; Info(q, "B_Info1").StartScenePhase = "B phase"; q.Name = "Renamed"; });
        Ok(game.Patch());
        Assert.Contains("this topic belongs to quest B\\_Quest in Base.esm and quest A\\_Quest in ModA.esm; a topic can't be in two quests, so it is not patched", game.Report);
        Assert.Contains("its topic 000801:Base.esm can't be written", game.Report);
        Assert.Empty(game.Quest().DialogTopics);
    }

    [Fact]
    public void Topic_info_list_insert_keeps_loser_position()
    {
        using var game = new Game(BaseDialog);
        FormKey a3 = default, b4 = default;
        game.Mod("ModA.esm", (m, q) =>
        {
            var t = Topic(q);
            var i3 = new DialogResponses(m) { EditorID = "A_Info3" };
            t.Responses.Add(i3);
            t.TopicInfoList.Insert(1, i3.ToLink<IDialogResponsesGetter>());   // between B_Info1 and B_Info2
            a3 = i3.FormKey;
        });
        game.Mod("ModB.esm", (m, q) =>
        {
            var t = Topic(q);
            var i4 = new DialogResponses(m) { EditorID = "B_Info4" };
            t.Responses.Add(i4);
            t.TopicInfoList.Add(i4.ToLink<IDialogResponsesGetter>());
            b4 = i4.FormKey;
        });
        Ok(game.Patch());
        var topic = Assert.Single(game.Quest().DialogTopics);
        var base1 = FormKey.Factory("000802:Base.esm");
        Assert.Equal(new[] { "B_Info1", "A_Info3", "B_Info2", "B_Info4" },
            topic.TopicInfoList.Select(l => l.FormKey == a3 ? "A_Info3" : l.FormKey == b4 ? "B_Info4" : l.FormKey == base1 ? "B_Info1" : "B_Info2"));
        Assert.Empty(topic.Responses);   // the new lines stay in their own plugins
    }

    // ---------- code-review round 1 (2026-11-25) ----------

    [Fact]
    public void Stage_the_winner_removed_stays_removed_when_a_loser_changed_it()
    {
        using var game = new Game();
        game.Mod("ModA.esm", q => { q.Stages[1].LogEntries.Add(new QuestLogEntry { Note = "A log" }); q.Stages.Add(Stage(15, "A 15")); });
        game.Mod("ModB.esm", q => q.Stages.RemoveAt(1));
        Ok(game.Patch());
        Assert.Equal(new[] { 10, 15 }, game.Quest().Stages.Select(s => (int)s.Index));
        Assert.Contains("ModB.esm removes stage 20 and ModA.esm changes it; ModB.esm's is kept", game.Report);
    }

    [Fact]
    public void Two_losers_remove_and_change_one_stage_the_later_decides()
    {
        using (var game = new Game())
        {
            game.Mod("ModA.esm", q => q.Stages[1].LogEntries.Add(new QuestLogEntry { Note = "A log" }));
            game.Mod("ModB.esm", q => q.Stages.RemoveAt(1));
            game.Mod("ModC.esm", q => q.Name = "Renamed");
            Ok(game.Patch());
            Assert.Equal(new[] { 10 }, game.Quest().Stages.Select(s => (int)s.Index));
        }
        using (var game = new Game())
        {
            game.Mod("ModA.esm", q => q.Stages.RemoveAt(1));
            game.Mod("ModB.esm", q => q.Stages[1].LogEntries.Add(new QuestLogEntry { Note = "B log" }));
            game.Mod("ModC.esm", q => q.Name = "Renamed");
            Ok(game.Patch());
            var quest = game.Quest();
            Assert.Equal(new[] { 10, 20 }, quest.Stages.Select(s => (int)s.Index));
            Assert.Equal(new[] { "base 20", "B log" }, Notes(quest.Stages[1]));
        }
    }

    [Fact]
    public void Loser_removes_the_fragment_script_and_the_winner_changes_a_property()
    {
        using var game = new Game(BaseVmad);
        game.Mod("ModA.esm", q => { q.VirtualMachineAdapter!.Script.Name = ""; q.VirtualMachineAdapter.Fragments.Clear(); q.Summary = "A summary"; });
        game.Mod("ModB.esm", q => Prop(q, "MyQuestScript", "Level").Data = 7);
        Ok(game.Patch());
        var quest = game.Quest();
        Assert.Equal("A summary", quest.Summary);
        Assert.Empty(quest.VirtualMachineAdapter!.Fragments);
        Assert.Equal(7, Value(quest, "MyQuestScript", "Level"));
    }

    [Fact]
    public void Two_losers_change_and_remove_one_script_property_the_later_decides()
    {
        using var game = new Game(BaseVmad);
        game.Mod("ModA.esm", q => Prop(q, "MyQuestScript", "Count").Data = 5);
        game.Mod("ModB.esm", q => { var s = q.VirtualMachineAdapter!.Scripts.First(x => x.Name == "MyQuestScript"); s.Properties.RemoveAt(0); Prop(q, "MyQuestScript", "Level").Data = 7; });
        game.Mod("ModC.esm", q => q.Name = "Renamed");
        Ok(game.Patch());
        var props = game.Quest().VirtualMachineAdapter!.Scripts.First(s => s.Name == "MyQuestScript").Properties.Select(p => p.Name);
        Assert.Equal(new[] { "Level" }, props);
    }

    // code review 0.19.0, round 3: one fragment change both made needs no fragment script read
    [Fact]
    public void Same_fragment_change_by_both_merges_without_reading_the_script()
    {
        using var game = new Game(BaseVmad);   // no fragment script file: it can't be read
        game.Mod("ModA.esm", q => { q.VirtualMachineAdapter!.Fragments.Add(Frag(20)); Prop(q, "MyQuestScript", "Count").Data = 5; });
        game.Mod("ModB.esm", q => { q.VirtualMachineAdapter!.Fragments.Add(Frag(20)); Prop(q, "MyQuestScript", "Level").Data = 7; });
        Ok(game.Patch());
        var quest = game.Quest();
        Assert.Equal(new[] { 10, 20 }, quest.VirtualMachineAdapter!.Fragments.Select(f => (int)f.Stage));
        Assert.Equal((5, 7), (Value(quest, "MyQuestScript", "Count"), Value(quest, "MyQuestScript", "Level")));
        Assert.DoesNotContain("could not be read", game.Report);
    }

    // code review 0.19.0, round 3: a topic added as a parent in the line pass and dropped in the quest pass leaves no
    // "is in the patch" note behind
    [Fact]
    public void Parent_topic_dropped_with_its_quest_leaves_no_in_the_patch_note()
    {
        using var game = new Game(BaseDialog);
        game.Mod("ModA.esm", q => Info(q, "B_Info1").Prompt = "A prompt");
        game.Mod("ModB.esm", q => Info(q, "B_Info1").StartScenePhase = "B phase");
        var txt = Path.Combine(game.Dir, "Plugins.txt");
        File.WriteAllLines(txt, new[] { "*Base.esm", "*ModA.esm", "*ModB.esm" });
        var lo = LoadOrderSetup.Build(game.Data, txt);
        var questKey = FormKey.Factory(QuestFk);
        PatchCommand.Plan plan;
        try
        {
            PatchCommand.ComposeFailsForTest.Value = k => k == questKey;
            plan = PatchCommand.Build(lo, Analyzer.Run(lo.Plugins), new HashSet<string>(), new List<PatchCommand.Pick>());
        }
        finally
        {
            PatchCommand.ComposeFailsForTest.Value = null;
            foreach (var p in lo.Plugins) p.Mod.Dispose();
        }
        Assert.Empty(plan.Records);
        Assert.DoesNotContain(plan.Notes, n => n.Contains("is in the patch as the parent of"));
        Assert.Contains(plan.Notes, n => n.Contains("[000801:Base.esm] was needed only as the parent of patched dialogue"));
        Assert.Contains(plan.NotBuilt, r => r.EditorID == "B_Info1");
    }

    [Fact]
    public void Scene_and_branch_still_not_built_with_new_reason()
    {
        Assert.Null(PatchCommand.WhyNotBuilt(typeof(IDialogTopicGetter)));
        Assert.Null(PatchCommand.WhyNotBuilt(typeof(IDialogResponsesGetter)));
        Assert.Equal("scenes and dialogue branches are not built by this version; copy the fields in xEdit", PatchCommand.WhyNotBuilt(typeof(ISceneGetter)));
        Assert.Equal("scenes and dialogue branches are not built by this version; copy the fields in xEdit", PatchCommand.WhyNotBuilt(typeof(IDialogBranchGetter)));
    }

    [Fact]
    public void Quest_with_a_nonzero_unknown_and_a_patched_topic_reads_back()
    {
        // code review 0.19.0, round 2: with a topic in the patch the quest's dialogue group (and its Unknown) is written
        using var game = new Game((b, q) => { BaseDialog(b, q); q.Unknown = 7; });
        game.Mod("ModA.esm", q => Info(q, "B_Info1").Prompt = "A prompt");
        game.Mod("ModB.esm", q => Info(q, "B_Info1").StartScenePhase = "B phase");
        Ok(game.Patch());
    }

    [Fact]
    public void Topic_patched_with_none_of_its_lines_reads_back()
    {
        using var game = new Game(BaseDialog);
        game.Mod("ModA.esm", q => Topic(q).Priority = 60);
        game.Mod("ModB.esm", q => { var t = Topic(q); t.TopicInfoList.Reverse(); });
        Ok(game.Patch());
    }

    [Fact]
    public void Patch_with_quest_and_dialogue_reads_back_and_passes_gap_and_check()
    {
        using var game = new Game(BaseDialog);
        game.Mod("ModA.esm", q => { q.Stages.Add(Stage(15, "A 15")); Info(q, "B_Info1").Prompt = "A prompt"; Topic(q).Priority = 60; });
        game.Mod("ModB.esm", q => { q.Name = "Renamed"; Info(q, "B_Info1").StartScenePhase = "B phase"; Info(q, "B_Info2").Prompt = "B prompt"; });
        Ok(game.Patch());
        var quest = game.Quest();
        Assert.Equal(new[] { 10, 15, 20 }, quest.Stages.Select(s => (int)s.Index));
        var topic = Assert.Single(quest.DialogTopics);
        Assert.Equal(60f, topic.Priority);
        Assert.Equal(new[] { "B_Info1" }, topic.Responses.Select(i => i.EditorID));
        game.CloseAll();
        var patch = Path.Combine(game.Out, "ConflictPatch.esm");
        var gap = ExeTests.RunSplit("gap", patch, "--data", game.Data);
        Assert.True(gap.Code == 0, gap.Stdout + gap.Stderr);
        var check = ExeTests.RunSplit("check", patch, "--data", game.Data);
        Assert.True(check.Code == 0, check.Stdout + check.Stderr);
        Assert.DoesNotContain("ERROR", check.Stdout);
    }
}
