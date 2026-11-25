// sf-mod-check patch (0.19.0): a quest's stages, objectives and aliases merge by key, and its condition lists stay one
// value (StarfieldPluginCheck NOTES.md, "0.19.0 (GapFix)", Step 4, tests 1-9 and 19). Each test builds a tiny game in a
// sandbox, runs the built program and reads the patch it writes. No game files are needed.
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Starfield;
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
        public Game()
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
            using var g = new GameData(Data);
            PluginWriter.Write(b, Data, g, NoGap);
        }

        public void Mod(string name, Action<Quest> change)
        {
            var mod = Plugin.New(name, PluginStyle.Light, "test", "made-up mod");
            using var g = new GameData(Data);
            change(mod.Quests.GetOrAddAsOverride(g.Open("Base.esm").Quests.First()));
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
}
