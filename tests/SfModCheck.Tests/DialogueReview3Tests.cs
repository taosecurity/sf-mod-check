// 0.21.0 code-review round 3 fix, on a made-up plugin: voice-lines gives a SharedDialog line the voice types of the
// lines that link it before its quest's GetIsVoiceType list, so a quest-wide list doesn't add rows for voice types
// that never speak it. A shared line nobody links still falls back to the quest's list.
using System.Text;
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Starfield;
using Noggog;
using Xunit;

namespace SfModCheck.Tests;

public class DialogueReview3Tests
{
    static readonly WriteOptions NoGap = new() { GapCheck = false, FixEffects = false };

    static ConditionFloat Cond(ConditionData d) => new() { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1, Data = d };

    /// <summary>Data\SrMod.esm: voice types SrA, SrB, SrC; list SrAll (all three); NPC SrNpcA (SrA); quest SrQuest
    /// (DialogConditions GetIsVoiceType SrAll) with topics SrShared and SrTalk:
    ///   SR_Shared   SrShared, no speaker                       0x500  linked by SR_Link: sra only
    ///   SR_Lone     SrShared, no speaker, nobody links it      0x600  sra, srb, src (the quest's list)
    ///   SR_Link     SrTalk, Speaker SrNpcA, SharedDialog SR_Shared, no responses</summary>
    sealed class Game : Sandbox
    {
        public readonly string Data, Mod;

        public Game()
        {
            Data = Sub("Data");
            var m = Plugin.New("SrMod.esm", PluginStyle.Full, "test", "made-up plugin");
            var va = m.VoiceTypes.AddNew("SrA");
            var vb = m.VoiceTypes.AddNew("SrB");
            var vc = m.VoiceTypes.AddNew("SrC");
            var all = m.FormLists.AddNew("SrAll");
            foreach (var v in new[] { va, vb, vc }) all.Items.Add(v.ToLink<IStarfieldMajorRecordGetter>());
            var npcA = m.Npcs.AddNew("SrNpcA");
            npcA.Voice.SetTo(va);
            var quest = m.Quests.AddNew("SrQuest");
            var vtc = new GetIsVoiceTypeConditionData();
            vtc.VoiceTypeOrList.Link.SetTo(all.FormKey);
            quest.DialogConditions.Add(Cond(vtc));
            var sharedTopic = new DialogTopic(m) { EditorID = "SrShared", Quest = quest.ToLink<IQuestGetter>(), Category = DialogTopic.CategoryEnum.Misc, Subtype = DialogTopic.SubtypeEnum.Custom };
            var talk = new DialogTopic(m) { EditorID = "SrTalk", Quest = quest.ToLink<IQuestGetter>(), Category = DialogTopic.CategoryEnum.Misc, Subtype = DialogTopic.SubtypeEnum.Custom };
            quest.DialogTopics.Add(sharedTopic);
            quest.DialogTopics.Add(talk);
            DialogResponses Info(DialogTopic t, string edid, string text, uint wem)
            {
                var i = new DialogResponses(m) { EditorID = edid };
                i.Responses.Add(new DialogResponse { ResponseText = text, WEMFile = wem });
                t.Responses.Add(i);
                return i;
            }
            var shared = Info(sharedTopic, "SR_Shared", "Made-up shared line.", 0x500);
            Info(sharedTopic, "SR_Lone", "Made-up lone line.", 0x600);
            var link = new DialogResponses(m) { EditorID = "SR_Link" };
            link.Speaker.SetTo(npcA);
            link.SharedDialog.SetTo(shared);
            talk.Responses.Add(link);
            using (var g = new GameData(Data)) PluginWriter.Write(m, Data, g, NoGap);
            Mod = Path.Combine(Data, "SrMod.esm");
        }
    }

    [Fact]
    public void Voice_lines_reads_a_shared_lines_linkers_before_the_quest_list()
    {
        using var s = new Game();
        var o = Path.Combine(s.Dir, "out.tsv");
        var (code, stdout, stderr) = ExeTests.RunSplit("voice-lines", s.Mod, "--out", o, "--data", s.Data);
        Assert.True(code == 0, stdout + stderr);
        var rows = Encoding.Latin1.GetString(File.ReadAllBytes(o)).Split("\r\n")[1..^1].Select(l => l.Split('\t')).ToList();
        Assert.Equal(new[] { "sra 00000500", "sra 00000600", "srb 00000600", "src 00000600" }, rows.Select(r => $"{r[0]} {r[1]}").Order(StringComparer.Ordinal));
    }
}
