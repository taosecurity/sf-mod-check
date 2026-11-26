// 0.21.0 code-review round 1 fixes, on a made-up master and add-on:
// - voice-lines: a line the add-on adds to a master's topic that a master scene gives the player is left out, and one a
//   master scene gives to an alias gets that alias's voice type (the masters' scenes are read for overridden topics);
// - voice-lines: a line's own GetIsID wins over its quest's GetIsVoiceType list (one row, not one per list entry);
// - voice-lines: a SharedDialog line with no speaker of its own gets the voice types of the lines that link it;
// - lines: a line that links a master's SharedDialog line looks for its voice file in the master's folder.
using System.Text;
using System.Text.Json;
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Starfield;
using Noggog;
using Xunit;

namespace SfModCheck.Tests;

public class DialogueReviewTests
{
    static readonly WriteOptions NoGap = new() { GapCheck = false, FixEffects = false };

    static ConditionFloat Cond(ConditionData d) => new() { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1, Data = d };

    static ConditionFloat IsId(FormKey npc)
    {
        var d = new GetIsIDConditionData();
        d.FirstParameter.Link.SetTo(npc);
        return Cond(d);
    }

    /// <summary>Data\DrBase.esm: voice types DrA (female), DrB; list DrList (both); NPCs DrNpc (DrA), DrMan (DrB);
    /// quest DrQuest (DialogConditions GetIsVoiceType DrList), quest DrScenes (alias 0 = DrNpc) with topics DrChoice and
    /// DrSpoken used by scene DrScene (DrChoice the player's choice, DrSpoken alias 0's), and topic DrSharedTopic with
    /// DR_BaseShared (0x500, no conditions). Data\DrMod.esm adds:
    ///   DR_Own        DrQuest topic, GetIsID(DrNpc)               0x100  dra only
    ///   DR_AddChoice  in DrChoice, GetIsID(DrNpc)                  0x200  left out (a player line)
    ///   DR_AddSpoken  in DrSpoken, no conditions                   0x300  dra (scene alias 0)
    ///   DR_Shared     DrModShared topic, no conditions             0x400  drb (DR_Link shares it)
    ///   DR_Link       GetIsID(DrMan), SharedDialog DR_Shared, no responses
    ///   DR_LinkBase   GetIsID(DrNpc), SharedDialog DR_BaseShared, no responses
    /// A loose Sound\Voice\DrBase.esm\DrA\00000500.wem.</summary>
    sealed class Game : Sandbox
    {
        public readonly string Data, Mod;
        public readonly Dictionary<string, FormKey> Infos = new();

        public Game()
        {
            Data = Sub("Data");
            var b = Plugin.New("DrBase.esm", PluginStyle.Full, "test", "made-up base");
            var va = b.VoiceTypes.AddNew("DrA");
            va.Flags = VoiceType.Flag.Female;
            var vb = b.VoiceTypes.AddNew("DrB");
            var list = b.FormLists.AddNew("DrList");
            list.Items.Add(va.ToLink<IStarfieldMajorRecordGetter>());
            list.Items.Add(vb.ToLink<IStarfieldMajorRecordGetter>());
            var npc = b.Npcs.AddNew("DrNpc");
            npc.Voice.SetTo(va);
            var man = b.Npcs.AddNew("DrMan");
            man.Voice.SetTo(vb);
            var quest = b.Quests.AddNew("DrQuest");
            var vtc = new GetIsVoiceTypeConditionData();
            vtc.VoiceTypeOrList.Link.SetTo(list.FormKey);
            quest.DialogConditions.Add(Cond(vtc));
            var sq = b.Quests.AddNew("DrScenes");
            sq.Aliases = new ExtendedList<AQuestAlias> { new QuestReferenceAlias { ID = 0, Name = "Npc", UniqueActor = npc.ToNullableLink<INpcGetter>() } };
            DialogTopic Topic(Quest q, string edid, DialogTopic.CategoryEnum c)
            {
                var t = new DialogTopic(b) { EditorID = edid, Quest = q.ToLink<IQuestGetter>(), Category = c, Subtype = DialogTopic.SubtypeEnum.Custom };
                q.DialogTopics.Add(t);
                return t;
            }
            var choice = Topic(sq, "DrChoice", DialogTopic.CategoryEnum.Scene);
            var spoken = Topic(sq, "DrSpoken", DialogTopic.CategoryEnum.Scene);
            var sharedTopic = Topic(sq, "DrSharedTopic", DialogTopic.CategoryEnum.Misc);
            var baseShared = new DialogResponses(b) { EditorID = "DR_BaseShared" };
            baseShared.Responses.Add(new DialogResponse { ResponseText = "Made-up base shared line.", WEMFile = 0x500 });
            sharedTopic.Responses.Add(baseShared);
            var scene = new Scene(b) { EditorID = "DrScene", Quest = sq.ToNullableLink<IQuestGetter>() };
            var pd = new PlayerDialogueSceneAction { AliasID = 0 };
            pd.DialogueList.Add(new PlayerDialogueSceneActionItem { PlayerChoice = choice.ToNullableLink<IDialogTopicGetter>() });
            scene.Actions = [pd, new DialogueSceneAction { AliasID = 0, Topic = spoken.ToNullableLink<IDialogTopicGetter>() }];
            sq.Scenes.Add(scene);
            using (var g = new GameData(Data)) PluginWriter.Write(b, Data, g, NoGap);

            var a = Plugin.New("DrMod.esm", PluginStyle.Full, "test", "made-up add-on");
            using (var g = new GameData(Data))
            {
                var bm = g.Open("DrBase.esm");
                DialogResponses Info(DialogTopic t, string edid, string? text, uint wem)
                {
                    var i = new DialogResponses(a) { EditorID = edid };
                    if (text != null) i.Responses.Add(new DialogResponse { ResponseText = text, WEMFile = wem });
                    t.Responses.Add(i);
                    Infos[edid] = i.FormKey;
                    return i;
                }
                var bq = bm.Quests.First(q => q.EditorID == "DrQuest");
                var q1 = a.Quests.GetOrAddAsOverride(bq);
                q1.DialogTopics.Clear(); q1.Scenes.Clear();
                var own = new DialogTopic(a) { EditorID = "DrModTopic", Quest = bq.ToLink<IQuestGetter>(), Category = DialogTopic.CategoryEnum.Misc, Subtype = DialogTopic.SubtypeEnum.Custom };
                q1.DialogTopics.Add(own);
                Info(own, "DR_Own", "Made-up own line.", 0x100).Conditions.Add(IsId(npc.FormKey));

                var bsq = bm.Quests.First(q => q.EditorID == "DrScenes");
                var q2 = a.Quests.GetOrAddAsOverride(bsq);
                q2.DialogTopics.Clear(); q2.Scenes.Clear();
                DialogTopic Over(string edid)
                {
                    var t = (DialogTopic)bsq.DialogTopics.First(x => x.EditorID == edid).DeepCopy();
                    t.Responses.Clear();
                    q2.DialogTopics.Add(t);
                    return t;
                }
                Info(Over("DrChoice"), "DR_AddChoice", "Made-up added choice.", 0x200).Conditions.Add(IsId(npc.FormKey));
                Info(Over("DrSpoken"), "DR_AddSpoken", "Made-up added scene line.", 0x300);
                var modShared = new DialogTopic(a) { EditorID = "DrModShared", Quest = bsq.ToLink<IQuestGetter>(), Category = DialogTopic.CategoryEnum.Misc, Subtype = DialogTopic.SubtypeEnum.Custom };
                q2.DialogTopics.Add(modShared);
                var shared = Info(modShared, "DR_Shared", "Made-up shared line.", 0x400);
                var linkTopic = new DialogTopic(a) { EditorID = "DrLinkTopic", Quest = bsq.ToLink<IQuestGetter>(), Category = DialogTopic.CategoryEnum.Misc, Subtype = DialogTopic.SubtypeEnum.Custom };
                q2.DialogTopics.Add(linkTopic);
                var link = Info(linkTopic, "DR_Link", null, 0);
                link.Conditions.Add(IsId(man.FormKey));
                link.SharedDialog.SetTo(shared);
                var linkBase = Info(linkTopic, "DR_LinkBase", null, 0);
                linkBase.Conditions.Add(IsId(npc.FormKey));
                linkBase.SharedDialog.SetTo(baseShared.FormKey);
                PluginWriter.Write(a, Data, g, NoGap);
            }
            Mod = Path.Combine(Data, "DrMod.esm");
            var vdir = Path.Combine(Data, "Sound", "Voice", "DrBase.esm", "DrA");
            Directory.CreateDirectory(vdir);
            File.WriteAllBytes(Path.Combine(vdir, "00000500.wem"), [0]);
        }
    }

    [Fact]
    public void Voice_lines_reads_master_scenes_prefers_own_conditions_and_gives_shared_lines_rows()
    {
        using var s = new Game();
        var o = Path.Combine(s.Dir, "out.tsv");
        var (code, stdout, stderr) = ExeTests.RunSplit("voice-lines", s.Mod, "--out", o, "--data", s.Data);
        Assert.True(code == 0, stdout + stderr);
        var rows = Encoding.Latin1.GetString(File.ReadAllBytes(o)).Split("\r\n")[1..^1].Select(l => l.Split('\t')).ToList();
        Assert.Equal(new[] { "dra 00000100", "dra 00000300", "drb 00000400" }, rows.Select(r => $"{r[0]} {r[1]}"));
        Assert.Equal(s.Infos["DR_Shared"].ToString(), rows.Single(r => r[1] == "00000400")[7]);
        Assert.DoesNotContain("00000200", string.Join("\n", rows.Select(r => r[1])));
        Assert.DoesNotContain("no voice type (first", stderr);
    }

    [Fact]
    public void Lines_looks_for_a_shared_lines_voice_file_in_the_shared_lines_plugin()
    {
        using var s = new Game();
        var (code, stdout, stderr) = ExeTests.RunSplit("lines", "DrNpc", "--plugin", s.Mod, "--json", "--data", s.Data);
        Assert.True(code == 0, stdout + stderr);
        var lines = JsonDocument.Parse(stdout).RootElement.GetProperty("lines").EnumerateArray().ToList();
        var l = lines.Single(x => x.GetProperty("text").GetString() == "Made-up base shared line.");
        Assert.True(l.GetProperty("wem").GetBoolean());
    }
}
