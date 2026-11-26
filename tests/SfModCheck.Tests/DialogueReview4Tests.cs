// 0.21.0 code-review round 4 fixes, on a made-up plugin: voice-lines gives no rows for a voice type whose EditorID
// isn't one folder name (it would become a path in FULL PATH), refuses such a --voice name, and a SharedDialog line
// whose own GetIsID names an NPC with no voice type is settled (no rows) instead of taking its linkers' voice types.
using System.Text;
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Starfield;
using Noggog;
using Xunit;

namespace SfModCheck.Tests;

public class DialogueReview4Tests
{
    static readonly WriteOptions NoGap = new() { GapCheck = false, FixEffects = false };

    static ConditionFloat Cond(ConditionData d) => new() { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1, Data = d };

    /// <summary>Data\FrMod.esm: voice types FrGood and "..\FrUp"; NPCs FrNpcGood (FrGood), FrNpcBad ("..\FrUp"),
    /// FrNpcT (no voice type); quest FrQuest with topics FrShared and FrTalk:
    ///   FR_Good    FrTalk, Speaker FrNpcGood                    0x100  frgood
    ///   FR_Bad     FrTalk, Speaker FrNpcBad                     0x200  no rows (bad name)
    ///   FR_Shared  FrShared, GetIsID FrNpcT                     0x300  no rows (a named NPC settles it)
    ///   FR_Link    FrTalk, Speaker FrNpcGood, SharedDialog FR_Shared, no responses</summary>
    sealed class Game : Sandbox
    {
        public readonly string Data, Mod;

        public Game()
        {
            Data = Sub("Data");
            var m = Plugin.New("FrMod.esm", PluginStyle.Full, "test", "made-up plugin");
            var good = m.VoiceTypes.AddNew("FrGood");
            var bad = m.VoiceTypes.AddNew("..\\FrUp");
            var npcGood = m.Npcs.AddNew("FrNpcGood");
            npcGood.Voice.SetTo(good);
            var npcBad = m.Npcs.AddNew("FrNpcBad");
            npcBad.Voice.SetTo(bad);
            var npcT = m.Npcs.AddNew("FrNpcT");
            var quest = m.Quests.AddNew("FrQuest");
            var sharedTopic = new DialogTopic(m) { EditorID = "FrShared", Quest = quest.ToLink<IQuestGetter>(), Category = DialogTopic.CategoryEnum.Misc, Subtype = DialogTopic.SubtypeEnum.Custom };
            var talk = new DialogTopic(m) { EditorID = "FrTalk", Quest = quest.ToLink<IQuestGetter>(), Category = DialogTopic.CategoryEnum.Misc, Subtype = DialogTopic.SubtypeEnum.Custom };
            quest.DialogTopics.Add(sharedTopic);
            quest.DialogTopics.Add(talk);
            DialogResponses Info(DialogTopic t, string edid, string text, uint wem)
            {
                var i = new DialogResponses(m) { EditorID = edid };
                i.Responses.Add(new DialogResponse { ResponseText = text, WEMFile = wem });
                t.Responses.Add(i);
                return i;
            }
            Info(talk, "FR_Good", "Made-up good line.", 0x100).Speaker.SetTo(npcGood);
            Info(talk, "FR_Bad", "Made-up bad line.", 0x200).Speaker.SetTo(npcBad);
            var shared = Info(sharedTopic, "FR_Shared", "Made-up shared line.", 0x300);
            var isT = new GetIsIDConditionData();
            isT.FirstParameter.Link.SetTo(npcT.FormKey);
            shared.Conditions.Add(Cond(isT));
            var link = new DialogResponses(m) { EditorID = "FR_Link" };
            link.Speaker.SetTo(npcGood);
            link.SharedDialog.SetTo(shared);
            talk.Responses.Add(link);
            using (var g = new GameData(Data)) PluginWriter.Write(m, Data, g, NoGap);
            Mod = Path.Combine(Data, "FrMod.esm");
        }
    }

    [Fact]
    public void Voice_lines_skips_a_voice_type_that_is_not_a_folder_name_and_a_templated_shared_line()
    {
        using var s = new Game();
        var o = Path.Combine(s.Dir, "out.tsv");
        var (code, stdout, stderr) = ExeTests.RunSplit("voice-lines", s.Mod, "--out", o, "--data", s.Data);
        Assert.True(code == 0, stdout + stderr);
        var rows = Encoding.Latin1.GetString(File.ReadAllBytes(o)).Split("\r\n")[1..^1].Select(l => l.Split('\t')).ToList();
        Assert.Equal(new[] { "frgood 00000100" }, rows.Select(r => $"{r[0]} {r[1]}"));
        Assert.Contains("isn't one folder name", stderr);
        Assert.DoesNotContain("..\\frup", string.Join("\n", rows.Select(r => r[6])));
    }

    [Fact]
    public void Voice_lines_refuses_a_voice_option_that_is_not_a_folder_name()
    {
        using var s = new Game();
        var (code, _, stderr) = ExeTests.RunSplit("voice-lines", s.Mod, "--voice", "..\\up", "--data", s.Data);
        Assert.Equal(2, code);
        Assert.Contains("one folder name", stderr);
    }
}
