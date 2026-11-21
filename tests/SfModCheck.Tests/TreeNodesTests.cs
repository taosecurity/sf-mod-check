// 0.10.0 item 1: overrides of official Story Manager and Planet Content Manager nodes (check "trees"), their impact
// text in conflict, and condition functions the game doesn't have (check "conditions").
using BuildKit;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Starfield;
using Mutagen.Bethesda;
using Impact = SfModCheck.Conflict.Impact;
using LostChange = SfModCheck.Conflict.LostChange;
using LostField = SfModCheck.Conflict.LostField;
using Xunit;

namespace SfModCheck.Tests;

public class TreeNodesTests
{
    static readonly WriteOptions NoGap = new() { GapCheck = false, FixEffects = false };

    /// <summary>A master named like an official one (SFBGSFFF.esm matches Bethesda's SFBGS### names) with one
    /// node of each kind, and a made-up master (TestTrees.esm) with the same.</summary>
    sealed class Trees : Sandbox
    {
        public const string Official = "SFBGSFFF.esm", Made = "TestTrees.esm";
        public readonly Dictionary<string, FormKey> Nodes = new();

        public Trees()
        {
            foreach (var name in new[] { Official, Made })
            {
                var m = Plugin.New(name, PluginStyle.Full, "test", "made-up master with tree nodes");
                var p = name == Official ? "O_" : "M_";
                Nodes[p + "SMBN"] = m.StoryManagerBranchNodes.AddNew(p + "Branch").FormKey;
                Nodes[p + "SMQN"] = m.StoryManagerQuestNodes.AddNew(p + "QuestNode").FormKey;
                Nodes[p + "SMEN"] = m.StoryManagerEventNodes.AddNew(p + "EventNode").FormKey;
                Nodes[p + "PCMT"] = m.PlanetContentManagerTrees.AddNew(p + "Tree").FormKey;
                Nodes[p + "PCBN"] = m.PlanetContentManagerBranchNodes.AddNew(p + "PcBranch").FormKey;
                Nodes[p + "PCCN"] = m.PlanetContentManagerContentNodes.AddNew(p + "PcContent").FormKey;
                using var g = new GameData(Dir);
                PluginWriter.Write(m, Dir, g, NoGap);
            }
        }

        /// <summary>A plugin on both masters that overrides the given nodes (keys as in Nodes).</summary>
        public string Mod(string file, params string[] keys)
        {
            var mod = Plugin.New(file, PluginStyle.Light, "test", "overrides tree nodes");
            mod.Keywords.AddNew("TT_Keyword");
            using var g = new GameData(Dir);
            g.Open(Official);
            g.Open(Made);
            foreach (var k in keys)
            {
                var fk = Nodes[k];
                switch (k[2..])
                {
                    case "SMBN": mod.StoryManagerBranchNodes.Add(new StoryManagerBranchNode(fk, StarfieldRelease.Starfield) { EditorID = k, MaxConcurrentQuests = 3 }); break;
                    case "SMQN": mod.StoryManagerQuestNodes.Add(new StoryManagerQuestNode(fk, StarfieldRelease.Starfield) { EditorID = k, MaxConcurrentQuests = 3 }); break;
                    case "SMEN": mod.StoryManagerEventNodes.Add(new StoryManagerEventNode(fk, StarfieldRelease.Starfield) { EditorID = k, MaxConcurrentQuests = 3 }); break;
                    case "PCMT": mod.PlanetContentManagerTrees.Add(new PlanetContentManagerTree(fk, StarfieldRelease.Starfield) { EditorID = k }); break;
                    case "PCBN": mod.PlanetContentManagerBranchNodes.Add(new PlanetContentManagerBranchNode(fk, StarfieldRelease.Starfield) { EditorID = k }); break;
                    case "PCCN": mod.PlanetContentManagerContentNodes.Add(new PlanetContentManagerContentNode(fk, StarfieldRelease.Starfield) { EditorID = k }); break;
                }
            }
            var dir = Sub("build-" + file);
            PluginWriter.Write(mod, dir, g, NoGap);
            var final = Path.Combine(Dir, file);
            File.Move(Path.Combine(dir, file), final);
            return final;
        }

        public Result Check(string path) => Checker.Run(new Options { Plugin = path, DataDir = Dir, MastersDirs = [Dir], NoAssets = true });
    }

    static readonly string[] Kinds = ["SMBN", "SMQN", "SMEN", "PCMT", "PCBN", "PCCN"];

    [Fact]
    public void Every_override_of_an_official_node_is_named_with_the_conflict_free_alternative()
    {
        using var t = new Trees();
        var r = t.Check(t.Mod("treemod.esm", Kinds.Select(k => "O_" + k).ToArray()));
        var trees = r.Findings.Where(f => f.Check == "trees").ToList();
        Assert.Equal(6, trees.Count);
        Assert.All(trees, f => Assert.Equal("WARNING", f.Severity));
        Assert.All(trees, f => Assert.Contains("add your own node to the tree rather than edit an official one", f.Text));
        Assert.Contains(trees, f => f.Text.StartsWith("O_SMQN [SMQN:") && f.Text.Contains("overrides an official Story Manager quest node from SFBGSFFF.esm (the Story Manager decides which quests and events start, and when)"));
        Assert.Contains(trees, f => f.Text.Contains("overrides an official Planet Content Manager content node from SFBGSFFF.esm (the Planet Content Manager decides"));
        Assert.Contains(trees, f => f.Text.Contains("Planet Content Manager tree from"));
        Assert.Equal(0, r.Errors);
        Assert.NotNull(Hints.For("trees"));
    }

    [Fact]
    public void Nodes_of_a_mod_master_and_official_plugins_editing_official_nodes_are_not_flagged()
    {
        using var t = new Trees();
        // a mod editing another mod's nodes: not an official tree
        Assert.DoesNotContain(t.Check(t.Mod("othermod.esm", Kinds.Select(k => "M_" + k).ToArray())).Findings, f => f.Check == "trees");
        // an official plugin editing an official master's nodes (the DLCs extend Starfield.esm's trees): log only
        Assert.DoesNotContain(t.Check(t.Mod("SFBGSFFE.esm", Kinds.Select(k => "O_" + k).ToArray())).Findings, f => f.Check == "trees");
    }

    [Fact]
    public void A_condition_function_the_game_does_not_have_is_a_warning()
    {
        using var w = new World();
        var p = w.Build("unknownfn", m =>
        {
            var c = m.Quests.First().Scenes.First().Conditions;
            for (int i = 0; i < 2; i++) c.Add(new ConditionFloat { Data = new UnknownConditionData { Function = (Condition.Function)9999 }, ComparisonValue = 1 });
        });
        var r = w.Check(p);
        Expect.Finding(r, "WARNING", "conditions", $"condition function 9999 isn't one sf-mod-check knows (the {Rules.KnownFunctionCount} in Mutagen's list and the ones the official masters use)");
        Expect.Finding(r, "WARNING", "conditions", "(2 conditions)");
        Assert.DoesNotContain(r.Findings, f => f.Check == "read");   // not reported as a record Mutagen can't read
        Assert.Equal(607, Rules.KnownFunctionCount);   // Mutagen 0.54.4's list
    }

    [Fact]
    public void Known_condition_functions_are_not_flagged()
    {
        using var w = new World();
        Assert.DoesNotContain(w.Check(w.Build("clean")).Findings, f => f.Text.Contains("isn't one sf-mod-check knows"));
    }

    [Fact]
    public void A_function_number_the_official_masters_use_is_not_flagged()
    {
        // 966 is in Starfield.esm's dialogue but not in Mutagen 0.54.4's list (found 2026-11-20); the learned list covers it
        Assert.True(Rules.FunctionsSeenCount > 0, "data\\condition-functions-seen.txt isn't built in");
        using var w = new World();
        var p = w.Build("fn966", m => m.Quests.First().Scenes.First().Conditions.Add(
            new ConditionFloat { Data = new UnknownConditionData { Function = (Condition.Function)966 }, ComparisonValue = 1 }));
        Assert.DoesNotContain(w.Check(p).Findings, f => f.Check == "conditions");
    }

    [Fact]
    public void Conflict_impact_text_names_the_node_and_the_alternative()
    {
        var field = new LostField("MaxConcurrentQuests", false, "3", "1", new() { new("max concurrent quests is 1 in game instead of 3", false) }, "1",
            new() { new("changes max concurrent quests from 1 to 3", false) }, new());
        var lost = new LostChange(FormKey.Factory("000801:Starfield.esm"), "StoryManagerQuestNode", "SM_Node", null, "Starfield.esm",
            "ModA.esm", false, "ModB.esm", false, new() { field });
        var t = Impact.ForField(lost, field);
        Assert.StartsWith("Story Manager quest node SM_Node: a Story Manager quest node (the Story Manager decides which quests and events start, and when).", t.What);
        Assert.Contains("add your own node to the tree rather than edit an official one", t.What);
        Assert.Equal("Planet Content Manager branch nodes", Impact.TypeName("PlanetContentManagerBranchNode", 2));
        // other types keep their text
        var misc = lost with { RecordType = "MiscItem" };
        Assert.DoesNotContain("own node", Impact.ForField(misc, field).What);
    }
}
