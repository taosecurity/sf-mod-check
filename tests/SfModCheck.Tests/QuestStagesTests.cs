// The quest-stage check (0.19.0): made-up quests on the test plugin, with made-up scripts built byte by byte
// (PexBytes) and written as loose files beside the plugin. No game data.
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Starfield;
using Xunit;

namespace SfModCheck.Tests;

public class QuestStagesTests
{
    const string Unset = "quest-stage-unset", Missing = "quest-stage-missing";

    static Quest StageQuest(StarfieldMod m, string editorId, params int[] stages)
    {
        var q = m.Quests.AddNew(editorId);
        foreach (var s in stages) q.Stages.Add(new QuestStage { Index = (ushort)s });
        return q;
    }

    static ScriptEntry Script(string name, params ScriptProperty[] props)
    {
        var e = new ScriptEntry { Name = name };
        foreach (var p in props) e.Properties.Add(p);
        return e;
    }

    /// <summary>A loose script beside the plugin: one object with an OnInit function holding the code given.</summary>
    static void WriteScript(World w, string name, string parent, PIns[] code, (string, string, PV)[]? vars = null,
        (string, string)[]? locals = null, PFn[]? more = null)
    {
        var fns = new List<PFn> { new("OnInit", Locals: locals ?? [("::temp0", "Bool")], Code: code) };
        if (more != null) fns.AddRange(more);
        var bytes = PexBytes.Build([new PObj(name, parent, Vars: vars, States: [("", fns.ToArray())])]);
        var path = Path.Combine([w.Dir, "Scripts", .. name.Split(':')]) + ".pex";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    static PIns SetStage(string on, PV stage) => PIns.CallMethod("SetStage", on, "::temp0", stage);

    [Fact]
    public void Unset_stage_and_set_but_missing_stage_are_warned()
    {
        using var w = new World();
        WriteScript(w, "SFM:StageScript", "Quest", [SetStage("self", PV.Int(20)), SetStage("self", PV.Int(40))]);
        var p = w.Build("unset", m =>
        {
            var q = StageQuest(m, "SFM_StageQuest", 10, 20, 30);
            q.Stages[0].Flags = QuestStage.Flag.RunOnStart;
            q.VirtualMachineAdapter = new QuestAdapter();
            q.VirtualMachineAdapter.Scripts.Add(Script("SFM:StageScript"));
        });
        var r = w.Check(p);
        Expect.Finding(r, "WARNING", Unset, "SFM_StageQuest");
        var unset = Assert.Single(r.Findings, f => f.Check == Unset);
        Assert.Contains("sets stage 30 (1 of 3)", unset.Text);
        var missing = Assert.Single(r.Findings, f => f.Check == Missing);
        Assert.Equal("WARNING", missing.Severity);
        Assert.Contains("script SFM:StageScript sets stage 40 on SFM_StageQuest", missing.Text);
        Assert.Contains("has no stage 40", missing.Text);
    }

    [Fact]
    public void Correct_quest_stays_quiet()
    {
        using var w = new World();
        // stage 10: start stage; 20: a response's end; 30: a scene's begin; 40: a response fragment through GetOwningQuest
        // and a cast; 50: an int property the record sets; 60: a helper function called with a constant
        WriteScript(w, "SFM:InfoFragment", "TopicInfo",
        [
            PIns.CallMethod("GetOwningQuest", "self", "::temp1"),
            new PIns(0x0E, [PV.Id("kmyQuest"), PV.Id("::temp1")]),
            SetStage("kmyQuest", PV.Int(40)),
        ], locals: [("::temp0", "Bool"), ("::temp1", "Quest"), ("kmyQuest", "Quest")]);
        WriteScript(w, "SFM:GoodQuestScript", "Quest",
        [
            SetStage("self", PV.Id("::Stage_var")),
            PIns.CallMethod("Advance", "self", "::NoneVar", PV.Int(60)),
        ], vars: [("::Stage_var", "Int", PV.Int(0))], more:
        [
            new PFn("Advance", Params: [("aiStage", "Int")], Locals: [("::temp0", "Bool")], Code: [SetStage("self", PV.Id("aiStage"))]),
        ]);
        var p = w.Build("good", m =>
        {
            var q = StageQuest(m, "SFM_GoodQuest", 10, 20, 30, 40, 50, 60);
            q.Stages[0].Flags = QuestStage.Flag.RunOnStart;
            q.VirtualMachineAdapter = new QuestAdapter();
            q.VirtualMachineAdapter.Scripts.Add(Script("SFM:GoodQuestScript", new ScriptIntProperty { Name = "Stage", Data = 50 }));
            var topic = new DialogTopic(m) { EditorID = "SFM_GoodTopic", Quest = q.ToLink<IQuestGetter>() };
            q.DialogTopics.Add(topic);
            var info = new DialogResponses(m) { EditorID = "SFM_GoodInfo", SetParentQuestStage = new DialogSetParentQuestStage { OnBegin = -1, OnEnd = 20 } };
            info.VirtualMachineAdapter = new DialogResponsesAdapter { ScriptFragments = new ScriptFragments { Script = Script("SFM:InfoFragment") } };
            topic.Responses.Add(info);
            var scene = new Scene(m) { EditorID = "SFM_GoodScene", Quest = q.ToNullableLink<IQuestGetter>(), SetParentQuestStage = new SceneSetParentQuestStage { OnBegin = 30, OnEnd = -1 } };
            q.Scenes.Add(scene);
        });
        var r = w.Check(p);
        Expect.No(r, Unset);
        Expect.No(r, Missing);
    }

    [Fact]
    public void A_property_default_counts_as_set_but_never_as_missing()
    {
        using var w = new World();
        WriteScript(w, "SFM:DefaultScript", "Quest", [SetStage("self", PV.Id("::Stage_var"))], vars: [("::Stage_var", "Int", PV.Int(-1))]);
        var p = w.Build("default", m =>
        {
            var q = StageQuest(m, "SFM_DefaultQuest", 10);
            q.Stages[0].Flags = QuestStage.Flag.RunOnStart;
            q.VirtualMachineAdapter = new QuestAdapter();
            q.VirtualMachineAdapter.Scripts.Add(Script("SFM:DefaultScript"));
        });
        var r = w.Check(p);
        Expect.No(r, Missing);
        Expect.No(r, Unset);
    }

    [Fact]
    public void A_stage_number_worked_out_at_run_time_gives_one_note()
    {
        using var w = new World();
        WriteScript(w, "SFM:DynamicScript", "Quest",
        [
            PIns.CallMethod("GetCurrentStageID", "self", "::temp1"),
            new PIns(0x01, [PV.Id("::temp2"), PV.Id("::temp1"), PV.Int(10)]),   // iadd: the current stage + 10
            SetStage("self", PV.Id("::temp2")),
        ], locals: [("::temp0", "Bool"), ("::temp1", "Int"), ("::temp2", "Int")]);
        var p = w.Build("dynamic", m =>
        {
            var q = StageQuest(m, "SFM_DynamicQuest", 10, 20, 30);
            q.VirtualMachineAdapter = new QuestAdapter();
            q.VirtualMachineAdapter.Scripts.Add(Script("SFM:DynamicScript"));
        });
        var r = w.Check(p);
        var f = Assert.Single(r.Findings, f => f.Check == Unset);
        Assert.Equal("NOTE", f.Severity);
        Assert.Contains("SFM_DynamicQuest", f.Text);
        Assert.Contains("worked out while the game runs", f.Text);
        Expect.No(r, Missing);
    }

    [Fact]
    public void An_unreadable_script_turns_the_unset_rule_into_one_note()
    {
        using var w = new World();
        // a script that isn't a compiled script at all
        var bad = Path.Combine(w.Dir, "Scripts", "SFM", "BrokenScript.pex");
        Directory.CreateDirectory(Path.GetDirectoryName(bad)!);
        File.WriteAllBytes(bad, [1, 2, 3, 4, 5, 6, 7, 8]);
        WriteScript(w, "SFM:SetsMissing", "Quest", [SetStage("self", PV.Int(99))]);
        var p = w.Build("unreadable", m =>
        {
            var q = StageQuest(m, "SFM_BrokenQuest", 10, 20);
            q.VirtualMachineAdapter = new QuestAdapter();
            q.VirtualMachineAdapter.Scripts.Add(Script("SFM:BrokenScript"));
            q.VirtualMachineAdapter.Scripts.Add(Script("SFM:SetsMissing"));
        });
        var r = w.Check(p);
        var f = Assert.Single(r.Findings, f => f.Check == Unset);
        Assert.Equal("NOTE", f.Severity);
        Assert.Contains("1 attached script(s) couldn't be found or read (e.g. SFM:BrokenScript", f.Text);
        // the missing rule still runs
        Expect.Finding(r, "WARNING", Missing, "sets stage 99 on SFM_BrokenQuest");
    }

    [Fact]
    public void A_mod_parent_script_is_read_but_a_native_base_type_is_not()
    {
        using var w = new World();
        // a made-up base type with a native function whose own code sets stage 0 (as the game's base types can)
        var baseBytes = PexBytes.Build([new PObj("SFMBaseType", "", States: [("",
        [
            new PFn("NativeThing", Flags: 2),
            new PFn("ResetAll", Locals: [("::temp0", "Bool")], Code: [SetStage("self", PV.Int(0))]),
        ])])]);
        File.WriteAllBytes(Path.Combine(w.Sub("Scripts"), "SFMBaseType.pex"), baseBytes);
        WriteScript(w, "SFM:ParentScript", "SFMBaseType", [SetStage("self", PV.Int(20))]);
        WriteScript(w, "SFM:ChildScript", "SFM:ParentScript", [SetStage("self", PV.Int(10))]);
        var p = w.Build("parent", m =>
        {
            var q = StageQuest(m, "SFM_ParentQuest", 10, 20);
            q.VirtualMachineAdapter = new QuestAdapter();
            q.VirtualMachineAdapter.Scripts.Add(Script("SFM:ChildScript"));
        });
        var r = w.Check(p);
        Expect.No(r, Unset);     // 20 comes from the mod's parent script
        Expect.No(r, Missing);   // the base type's stage 0 isn't the plugin's call
    }

    [Fact]
    public void Skipped_with_no_assets()
    {
        using var w = new World();
        var p = w.Build("noassets", m => StageQuest(m, "SFM_NoAssetsQuest", 10, 20));
        Expect.Finding(w.Check(p), "WARNING", Unset, "SFM_NoAssetsQuest");
        Expect.No(w.Check(p, o => o.NoAssets = true), Unset);
    }
}
