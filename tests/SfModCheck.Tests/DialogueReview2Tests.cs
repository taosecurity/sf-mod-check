// 0.21.0 code-review round 2 fixes, on a made-up base, a made-up DLC that overrides the base's scene, and an add-on:
// - voice-lines: a line's speaker wins over its quest's GetIsVoiceType list;
// - voice-lines: a line whose own GetIsID names an NPC with no voice type of its own gets no rows (a NOTE), not the
//   quest list's voice types;
// - voice-lines and lines: a GetIsVoiceType list that holds another list is read through;
// - voice-lines: a DLC master's copy of a base scene is read, so a topic it makes a player choice is a player line;
// - lines: a line whose speaker is another NPC isn't one of this NPC's shared lines.
using System.Text;
using System.Text.Json;
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Starfield;
using Noggog;
using Xunit;

namespace SfModCheck.Tests;

public class DialogueReview2Tests
{
    static readonly WriteOptions NoGap = new() { GapCheck = false, FixEffects = false };

    static ConditionFloat Cond(ConditionData d) => new() { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1, Data = d };

    static ConditionFloat IsId(FormKey npc)
    {
        var d = new GetIsIDConditionData();
        d.FirstParameter.Link.SetTo(npc);
        return Cond(d);
    }

    /// <summary>Data\RvBase.esm: voice types RvA (female), RvB; list RvInner (both) inside list RvOuter; NPCs RvNpcA
    /// (RvA), RvNpcB (RvB), RvTempl (no voice type); quest RvQuest (DialogConditions GetIsVoiceType RvOuter); quest
    /// RvScenes with topic RvChoice and scene RvScene (no actions). Data\RvDlc.esm overrides RvScene so that RvChoice is
    /// a player choice. Data\RvMod.esm (masters RvBase, RvDlc) adds:
    ///   RV_Speaker  RvQuest topic, Speaker RvNpcB                 0x100  rvb only
    ///   RV_Templ    RvQuest topic, GetIsID(RvTempl)               0x200  no rows (NOTE)
    ///   RV_List     RvQuest topic, no conditions                  0x300  rva and rvb (nested list)
    ///   RV_Choice   in RvChoice, GetIsID(RvNpcA)                  0x400  left out (the DLC's player choice)</summary>
    sealed class Game : Sandbox
    {
        public readonly string Data, Mod;
        public readonly Dictionary<string, FormKey> Infos = new();

        public Game()
        {
            Data = Sub("Data");
            var b = Plugin.New("RvBase.esm", PluginStyle.Full, "test", "made-up base");
            var va = b.VoiceTypes.AddNew("RvA");
            va.Flags = VoiceType.Flag.Female;
            var vb = b.VoiceTypes.AddNew("RvB");
            var inner = b.FormLists.AddNew("RvInner");
            inner.Items.Add(va.ToLink<IStarfieldMajorRecordGetter>());
            inner.Items.Add(vb.ToLink<IStarfieldMajorRecordGetter>());
            var outer = b.FormLists.AddNew("RvOuter");
            outer.Items.Add(inner.ToLink<IStarfieldMajorRecordGetter>());
            var npcA = b.Npcs.AddNew("RvNpcA");
            npcA.Voice.SetTo(va);
            var npcB = b.Npcs.AddNew("RvNpcB");
            npcB.Voice.SetTo(vb);
            var templ = b.Npcs.AddNew("RvTempl");
            var quest = b.Quests.AddNew("RvQuest");
            var vtc = new GetIsVoiceTypeConditionData();
            vtc.VoiceTypeOrList.Link.SetTo(outer.FormKey);
            quest.DialogConditions.Add(Cond(vtc));
            var sq = b.Quests.AddNew("RvScenes");
            var choice = new DialogTopic(b) { EditorID = "RvChoice", Quest = sq.ToLink<IQuestGetter>(), Category = DialogTopic.CategoryEnum.Scene, Subtype = DialogTopic.SubtypeEnum.Custom };
            sq.DialogTopics.Add(choice);
            sq.Scenes.Add(new Scene(b) { EditorID = "RvScene", Quest = sq.ToNullableLink<IQuestGetter>() });
            using (var g = new GameData(Data)) PluginWriter.Write(b, Data, g, NoGap);

            using (var g = new GameData(Data))
            {
                var bm = g.Open("RvBase.esm");
                var d = Plugin.New("RvDlc.esm", PluginStyle.Full, "test", "made-up DLC");
                var bsq = bm.Quests.First(q => q.EditorID == "RvScenes");
                var dq = d.Quests.GetOrAddAsOverride(bsq);
                dq.DialogTopics.Clear(); dq.Scenes.Clear();
                var sc = (Scene)bsq.Scenes.First().DeepCopy();
                var pd = new PlayerDialogueSceneAction { AliasID = 0 };
                pd.DialogueList.Add(new PlayerDialogueSceneActionItem { PlayerChoice = new FormLinkNullable<IDialogTopicGetter>(bsq.DialogTopics.First().FormKey) });
                sc.Actions = [pd];
                dq.Scenes.Add(sc);
                d.MiscItems.AddNew("RvDlcMisc");
                PluginWriter.Write(d, Data, g, NoGap);
            }

            var a = Plugin.New("RvMod.esm", PluginStyle.Full, "test", "made-up add-on");
            using (var g = new GameData(Data))
            {
                var bm = g.Open("RvBase.esm");
                var dm = g.Open("RvDlc.esm");
                a.MiscItems.GetOrAddAsOverride(dm.MiscItems.First());   // keeps RvDlc.esm a master
                DialogResponses Info(DialogTopic t, string edid, string text, uint wem)
                {
                    var i = new DialogResponses(a) { EditorID = edid };
                    i.Responses.Add(new DialogResponse { ResponseText = text, WEMFile = wem });
                    t.Responses.Add(i);
                    Infos[edid] = i.FormKey;
                    return i;
                }
                var bq = bm.Quests.First(q => q.EditorID == "RvQuest");
                var q1 = a.Quests.GetOrAddAsOverride(bq);
                q1.DialogTopics.Clear(); q1.Scenes.Clear();
                var own = new DialogTopic(a) { EditorID = "RvModTopic", Quest = bq.ToLink<IQuestGetter>(), Category = DialogTopic.CategoryEnum.Misc, Subtype = DialogTopic.SubtypeEnum.Custom };
                q1.DialogTopics.Add(own);
                Info(own, "RV_Speaker", "Made-up speaker line.", 0x100).Speaker.SetTo(npcB);
                Info(own, "RV_Templ", "Made-up templated line.", 0x200).Conditions.Add(IsId(templ.FormKey));
                Info(own, "RV_List", "Made-up list line.", 0x300);

                var bsq = bm.Quests.First(q => q.EditorID == "RvScenes");
                var q2 = a.Quests.GetOrAddAsOverride(bsq);
                q2.DialogTopics.Clear(); q2.Scenes.Clear();
                var t = (DialogTopic)bsq.DialogTopics.First().DeepCopy();
                t.Responses.Clear();
                q2.DialogTopics.Add(t);
                Info(t, "RV_Choice", "Made-up choice.", 0x400).Conditions.Add(IsId(npcA.FormKey));
                PluginWriter.Write(a, Data, g, NoGap);
            }
            Mod = Path.Combine(Data, "RvMod.esm");
        }
    }

    [Fact]
    public void Voice_lines_prefers_the_speaker_settles_on_a_named_npc_reads_nested_lists_and_dlc_scenes()
    {
        using var s = new Game();
        var o = Path.Combine(s.Dir, "out.tsv");
        var (code, stdout, stderr) = ExeTests.RunSplit("voice-lines", s.Mod, "--out", o, "--data", s.Data);
        Assert.True(code == 0, stdout + stderr);
        var rows = Encoding.Latin1.GetString(File.ReadAllBytes(o)).Split("\r\n")[1..^1].Select(l => l.Split('\t')).ToList();
        Assert.Equal(new[] { "rva 00000300", "rvb 00000100", "rvb 00000300" }, rows.Select(r => $"{r[0]} {r[1]}").Order(StringComparer.Ordinal));
        Assert.Contains($"RV_Templ ({s.Infos["RV_Templ"]})", stderr);
    }

    [Fact]
    public void Lines_leaves_out_a_line_another_npc_speaks_and_reads_nested_lists()
    {
        using var s = new Game();
        var (code, stdout, stderr) = ExeTests.RunSplit("lines", "RvNpcA", "--plugin", s.Mod, "--json", "--data", s.Data);
        Assert.True(code == 0, stdout + stderr);
        var texts = JsonDocument.Parse(stdout).RootElement.GetProperty("lines").EnumerateArray()
            .Select(x => x.GetProperty("text").GetString()).ToList();
        Assert.Contains("Made-up list line.", texts);
        Assert.DoesNotContain("Made-up speaker line.", texts);
    }
}
