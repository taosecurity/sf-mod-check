// 0.21.0 code-review round 5 fixes, on a made-up plugin: lines doesn't credit an NPC, through a quest alias it fills,
// with a line whose own GetIsID names another NPC; voice-lines doesn't give a shared line its quest's voice-type list
// when a linker names an NPC with no voice type (the linker settles it); --out can't overwrite the plugin.
using System.Text;
using System.Text.Json;
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Starfield;
using Noggog;
using Xunit;

namespace SfModCheck.Tests;

public class DialogueReview5Tests
{
    static readonly WriteOptions NoGap = new() { GapCheck = false, FixEffects = false };

    static ConditionFloat Cond(ConditionData d) => new() { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1, Data = d };

    /// <summary>Data\FvMod.esm: voice types FvA and FvAll; NPCs FvNpcA (FvA), FvNpcB (FvA), FvTempl (no voice type);
    /// form list FvList (FvAll); quest FvQuest (alias 0 = UniqueActor FvNpcA; DialogConditions GetIsVoiceType FvList)
    /// with topics FvShared and FvTalk:
    ///   FV_AliasA   FvTalk, GetIsAliasRef(0)                     0x100  lines FvNpcA: quest alias
    ///   FV_AliasB   FvTalk, GetIsAliasRef(0) and GetIsID FvNpcB  0x200  not FvNpcA's line
    ///   FV_Shared   FvShared, no conditions of its own           0x300  no rows (its linker names FvTempl)
    ///   FV_Link     FvTalk, GetIsID FvTempl, SharedDialog FV_Shared, no responses</summary>
    sealed class Game : Sandbox
    {
        public readonly string Data, Mod;

        public Game()
        {
            Data = Sub("Data");
            var m = Plugin.New("FvMod.esm", PluginStyle.Full, "test", "made-up plugin");
            var va = m.VoiceTypes.AddNew("FvA");
            var vall = m.VoiceTypes.AddNew("FvAll");
            var npcA = m.Npcs.AddNew("FvNpcA");
            npcA.Voice.SetTo(va);
            var npcB = m.Npcs.AddNew("FvNpcB");
            npcB.Voice.SetTo(va);
            var templ = m.Npcs.AddNew("FvTempl");
            var list = m.FormLists.AddNew("FvList");
            list.Items.Add(vall.ToLink<IStarfieldMajorRecordGetter>());
            var quest = m.Quests.AddNew("FvQuest");
            quest.Aliases = new ExtendedList<AQuestAlias> { new QuestReferenceAlias { ID = 0, Name = "Npc", UniqueActor = npcA.ToNullableLink<INpcGetter>() } };
            var vtc = new GetIsVoiceTypeConditionData();
            vtc.VoiceTypeOrList.Link.SetTo(list.FormKey);
            quest.DialogConditions.Add(Cond(vtc));
            var sharedTopic = new DialogTopic(m) { EditorID = "FvShared", Quest = quest.ToLink<IQuestGetter>(), Category = DialogTopic.CategoryEnum.Misc, Subtype = DialogTopic.SubtypeEnum.Custom };
            var talk = new DialogTopic(m) { EditorID = "FvTalk", Quest = quest.ToLink<IQuestGetter>(), Category = DialogTopic.CategoryEnum.Misc, Subtype = DialogTopic.SubtypeEnum.Custom };
            quest.DialogTopics.Add(sharedTopic);
            quest.DialogTopics.Add(talk);
            DialogResponses Info(DialogTopic t, string edid, string text, uint wem)
            {
                var i = new DialogResponses(m) { EditorID = edid };
                i.Responses.Add(new DialogResponse { ResponseText = text, WEMFile = wem });
                t.Responses.Add(i);
                return i;
            }
            Info(talk, "FV_AliasA", "Made-up alias line A.", 0x100).Conditions.Add(Cond(new GetIsAliasRefConditionData { FirstParameter = 0 }));
            var lb = Info(talk, "FV_AliasB", "Made-up alias line B.", 0x200);
            lb.Conditions.Add(Cond(new GetIsAliasRefConditionData { FirstParameter = 0 }));
            var isB = new GetIsIDConditionData();
            isB.FirstParameter.Link.SetTo(npcB.FormKey);
            lb.Conditions.Add(Cond(isB));
            var shared = Info(sharedTopic, "FV_Shared", "Made-up shared line.", 0x300);
            var link = new DialogResponses(m) { EditorID = "FV_Link" };
            var isT = new GetIsIDConditionData();
            isT.FirstParameter.Link.SetTo(templ.FormKey);
            link.Conditions.Add(Cond(isT));
            link.SharedDialog.SetTo(shared);
            talk.Responses.Add(link);
            using (var g = new GameData(Data)) PluginWriter.Write(m, Data, g, NoGap);
            Mod = Path.Combine(Data, "FvMod.esm");
        }
    }

    [Fact]
    public void Lines_leaves_out_an_alias_line_whose_own_condition_names_another_npc()
    {
        using var s = new Game();
        var (code, stdout, stderr) = ExeTests.RunSplit("lines", "FvNpcA", "--plugin", s.Mod, "--json", "--data", s.Data);
        Assert.True(code == 0, stdout + stderr);
        var texts = JsonDocument.Parse(stdout).RootElement.GetProperty("lines").EnumerateArray()
            .Select(x => x.GetProperty("text").GetString()).ToList();
        Assert.Contains("Made-up alias line A.", texts);
        Assert.DoesNotContain("Made-up alias line B.", texts);
    }

    [Fact]
    public void Voice_lines_lets_a_linker_naming_a_templated_npc_settle_a_shared_line()
    {
        using var s = new Game();
        var o = Path.Combine(s.Dir, "out.tsv");
        var (code, stdout, stderr) = ExeTests.RunSplit("voice-lines", s.Mod, "--out", o, "--data", s.Data);
        Assert.True(code == 0, stdout + stderr);
        var rows = Encoding.Latin1.GetString(File.ReadAllBytes(o)).Split("\r\n")[1..^1].Select(l => l.Split('\t')).ToList();
        Assert.DoesNotContain(rows, r => r[1] == "00000300");
    }

    [Fact]
    public void Voice_lines_refuses_to_write_over_the_plugin()
    {
        using var s = new Game();
        var before = File.ReadAllBytes(s.Mod);
        var (code, _, stderr) = ExeTests.RunSplit("voice-lines", s.Mod, "--out", s.Mod, "--data", s.Data);
        Assert.Equal(2, code);
        Assert.Contains("not a .esm file", stderr);   // 0.28.1: the shared --out guard refuses a mod-file name before the plugin check
        Assert.Equal(before, File.ReadAllBytes(s.Mod));
    }
}
