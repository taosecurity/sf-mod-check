// sf-mod-check lines (0.21.0) on made-up plugins: each way a line is found for an NPC (speaker, GetIsID, GetIsAliasRef
// on a UniqueActor alias, a scene Dialogue action whose alias is forced to a placed actor of the NPC, a Player Dialogue
// action's NPC response), the shared GetIsVoiceType block, a negated GetIsID, generic and player lines, the winning copy
// of an overridden line, --type, --max, --plugin, and the not-an-NPC and not-found exits. Each fails on 0.20.0, which has
// no lines command.
using System.Text.Json;
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Starfield;
using Noggog;
using Xunit;

namespace SfModCheck.Tests;

public class LinesTests
{
    static readonly WriteOptions NoGap = new() { GapCheck = false, FixEffects = false };

    static ConditionFloat Cond(ConditionData d, CompareOperator op = CompareOperator.EqualTo, float value = 1) =>
        new() { CompareOperator = op, ComparisonValue = value, Data = d };

    static ConditionFloat IsId(FormKey npc, CompareOperator op = CompareOperator.EqualTo)
    {
        var d = new GetIsIDConditionData();
        d.FirstParameter.Link.SetTo(npc);
        return Cond(d, op);
    }

    /// <summary>Data\LnBase.esm (full) holds voice types LnVoice (female) and LnOtherVoice, NPCs LnNpc (LnVoice) and LnOther,
    /// a keyword AnimFaceArchetypeHappy, a form list LnVoices (LnVoice), a cell with a placed LnNpc, and quest LnQuest with
    /// aliases 0 (UniqueActor LnNpc), 1 (ForcedReference: the placed LnNpc) and 2 (UniqueActor LnOther), and these lines:
    ///   LN_Speaker   Misc / Hello      Speaker LnNpc, emotion Happy                           speaker
    ///   LN_IsId      Combat / Hit      GetIsID(LnNpc) == 1                                     condition
    ///   LN_NotId     Combat / Hit      GetIsID(LnNpc) != 1                                     generic (not counted)
    ///   LN_Alias     Misc / Idle       GetIsAliasRef(0) == 1                                   quest alias
    ///   LN_Scene     Scene / Custom    scene LnScene's Dialogue action on alias 1, 2 responses scene alias
    ///   LN_Response  Scene / Custom    LnScene's Player Dialogue action (alias 0): NPC response scene alias
    ///   LN_Choice    Player / Custom   that action's player choice                             player line
    ///   LN_Shared    Misc / Idle       GetIsVoiceType(LnVoices) == 1                           shared
    ///   LN_Generic   Misc / Idle       no condition                                            generic
    ///   LN_Other     Combat / Hit      GetIsID(LnOther) == 1                                   not counted
    ///   LN_OtherSc   Scene / Custom    scene LnOtherScene on alias 2 (LnOther)                 not counted
    /// Data\LnAddon.esm (light, master LnBase.esm) overrides LN_IsId with a new text and adds LN_AddonHit (Combat / Hit,
    /// GetIsID(LnNpc)). Plugins.txt lists both.</summary>
    sealed class Game : Sandbox
    {
        public readonly string Data, PluginsTxt, Addon;
        public readonly FormKey Npc, Quest, IsIdInfo;

        public Game()
        {
            Data = Sub("Data");
            var b = Plugin.New("LnBase.esm", PluginStyle.Full, "test", "made-up base");
            var voice = b.VoiceTypes.AddNew("LnVoice");
            voice.Flags = VoiceType.Flag.Female;
            var otherVoice = b.VoiceTypes.AddNew("LnOtherVoice");
            var npc = b.Npcs.AddNew("LnNpc");
            npc.Voice.SetTo(voice);
            Npc = npc.FormKey;
            var other = b.Npcs.AddNew("LnOther");
            other.Voice.SetTo(otherVoice);
            var happy = b.Keywords.AddNew("AnimFaceArchetypeHappy");
            var list = b.FormLists.AddNew("LnVoices");
            list.Items.Add(voice.ToLink<IStarfieldMajorRecordGetter>());

            var cell = new Cell(b) { EditorID = "LnCell" };
            var placed = new PlacedNpc(b) { EditorID = "LnNpcRef" };
            placed.Base.SetTo(npc);
            cell.Persistent.Add(placed);
            b.Cells.Add(new CellBlock { SubBlocks = new ExtendedList<CellSubBlock> { new CellSubBlock { Cells = new ExtendedList<Cell> { cell } } } });

            var quest = b.Quests.AddNew("LnQuest");
            Quest = quest.FormKey;
            quest.Aliases = new ExtendedList<AQuestAlias>
            {
                new QuestReferenceAlias { ID = 0, Name = "Npc", UniqueActor = npc.ToNullableLink<INpcGetter>() },
                new QuestReferenceAlias { ID = 1, Name = "NpcRef", ForcedReference = new FormLinkNullable<IPlacedGetter>(placed.FormKey) },
                new QuestReferenceAlias { ID = 2, Name = "Other", UniqueActor = other.ToNullableLink<INpcGetter>() },
            };
            DialogTopic Topic(string edid, DialogTopic.CategoryEnum c, DialogTopic.SubtypeEnum s)
            {
                var t = new DialogTopic(b) { EditorID = edid, Quest = quest.ToLink<IQuestGetter>(), Category = c, Subtype = s };
                quest.DialogTopics.Add(t);
                return t;
            }
            DialogResponses Info(DialogTopic t, string edid, params string[] texts)
            {
                var i = new DialogResponses(b) { EditorID = edid };
                uint n = 0x100;
                foreach (var x in texts) i.Responses.Add(new DialogResponse { ResponseText = x, WEMFile = ++n });
                t.Responses.Add(i);
                return i;
            }
            var hello = Topic("LnHello", DialogTopic.CategoryEnum.Misc, DialogTopic.SubtypeEnum.Hello);
            var sp = Info(hello, "LN_Speaker", "Made-up hello.");
            sp.Speaker.SetTo(npc);
            sp.Responses[0].Emotion.SetTo(happy);
            var hit = Topic("LnHit", DialogTopic.CategoryEnum.Combat, DialogTopic.SubtypeEnum.Hit);
            var isId = Info(hit, "LN_IsId", "Made-up ouch.");
            isId.Conditions.Add(IsId(npc.FormKey));
            IsIdInfo = isId.FormKey;
            Info(hit, "LN_NotId", "Made-up not me.").Conditions.Add(IsId(npc.FormKey, CompareOperator.NotEqualTo));
            Info(hit, "LN_Other", "Made-up other.").Conditions.Add(IsId(other.FormKey));
            var idle = Topic("LnIdle", DialogTopic.CategoryEnum.Misc, DialogTopic.SubtypeEnum.Idle);
            Info(idle, "LN_Alias", "Made-up alias line.").Conditions.Add(Cond(new GetIsAliasRefConditionData { FirstParameter = 0 }));
            var vtc = new GetIsVoiceTypeConditionData();
            vtc.VoiceTypeOrList.Link.SetTo(list.FormKey);
            Info(idle, "LN_Shared", "Made-up shared line.").Conditions.Add(Cond(vtc));
            Info(idle, "LN_Generic", "Made-up anyone line.");
            var sceneTopic = Topic("LnSceneTopic", DialogTopic.CategoryEnum.Scene, DialogTopic.SubtypeEnum.Custom);
            Info(sceneTopic, "LN_Scene", "Made-up scene one.", "Made-up scene two.");
            var respTopic = Topic("LnRespTopic", DialogTopic.CategoryEnum.Scene, DialogTopic.SubtypeEnum.Custom);
            Info(respTopic, "LN_Response", "Made-up answer.");
            var choiceTopic = Topic("LnChoiceTopic", DialogTopic.CategoryEnum.Player, DialogTopic.SubtypeEnum.Custom);
            Info(choiceTopic, "LN_Choice", "Made-up question.");
            var otherSceneTopic = Topic("LnOtherSceneTopic", DialogTopic.CategoryEnum.Scene, DialogTopic.SubtypeEnum.Custom);
            Info(otherSceneTopic, "LN_OtherSc", "Made-up other scene.");

            var scene = new Scene(b) { EditorID = "LnScene", Quest = quest.ToNullableLink<IQuestGetter>() };
            var pd = new PlayerDialogueSceneAction { AliasID = 0 };
            var item = new PlayerDialogueSceneActionItem { PlayerChoice = choiceTopic.ToNullableLink<IDialogTopicGetter>() };
            item.NpcResponse.SetTo(respTopic);
            pd.DialogueList.Add(item);
            scene.Actions = [new DialogueSceneAction { AliasID = 1, Topic = sceneTopic.ToNullableLink<IDialogTopicGetter>() }, pd];
            quest.Scenes.Add(scene);
            var otherScene = new Scene(b) { EditorID = "LnOtherScene", Quest = quest.ToNullableLink<IQuestGetter>() };
            otherScene.Actions = [new DialogueSceneAction { AliasID = 2, Topic = otherSceneTopic.ToNullableLink<IDialogTopicGetter>() }];
            quest.Scenes.Add(otherScene);
            using (var g = new GameData(Data)) PluginWriter.Write(b, Data, g, NoGap);

            var a = Plugin.New("LnAddon.esm", PluginStyle.Light, "test", "made-up addon");
            using (var g = new GameData(Data))
            {
                var baseMod = g.Open("LnBase.esm");
                var bq = baseMod.Quests.First();
                var q = a.Quests.GetOrAddAsOverride(bq);
                q.DialogTopics.Clear(); q.Scenes.Clear();
                var bt = bq.DialogTopics.First(t => t.EditorID == "LnHit");
                var t = (DialogTopic)bt.DeepCopy();
                t.Responses.Clear();
                var over = (DialogResponses)bt.Responses.First(i => i.EditorID == "LN_IsId").DeepCopy();
                over.Responses[0].ResponseText = "Made-up ouch, overridden.";
                t.Responses.Add(over);
                var added = new DialogResponses(a) { EditorID = "LN_AddonHit" };
                added.Responses.Add(new DialogResponse { ResponseText = "Made-up addon ouch.", WEMFile = 0x200 });
                added.Conditions.Add(IsId(npc.FormKey));
                t.Responses.Add(added);
                q.DialogTopics.Add(t);
                PluginWriter.Write(a, Data, g, NoGap);
            }
            Addon = Path.Combine(Data, "LnAddon.esm");
            PluginsTxt = Path.Combine(Dir, "Plugins.txt");
            File.WriteAllLines(PluginsTxt, ["*LnBase.esm", "*LnAddon.esm"]);
        }

        public (int Code, string Stdout, string Stderr) Lines(params string[] args) =>
            ExeTests.RunSplit(new[] { "lines" }.Concat(args).Concat(["--data", Data]).ToArray());
        public (int Code, string Stdout, string Stderr) InOrder(params string[] args) => Lines([.. args, "--plugins", PluginsTxt]);
    }

    [Fact]
    public void Every_way_a_line_is_found_gives_its_found_by_and_the_rest_are_counted()
    {
        using var s = new Game();
        var (code, stdout, stderr) = s.InOrder("LnNpc", "--json");
        Assert.True(code == 0, stdout + stderr);
        var j = JsonDocument.Parse(stdout).RootElement;
        Assert.Equal(Npc(s), j.GetProperty("npc").GetProperty("formKey").GetString());
        Assert.Equal("LnVoice", j.GetProperty("npc").GetProperty("voiceType").GetString());
        Assert.True(j.GetProperty("npc").GetProperty("female").GetBoolean());
        var lines = j.GetProperty("lines").EnumerateArray().ToList();
        string FoundBy(string edidText) => lines.Single(l => l.GetProperty("text").GetString() == edidText).GetProperty("foundBy").GetString()!;
        Assert.Equal("speaker", FoundBy("Made-up hello."));
        Assert.Equal("condition", FoundBy("Made-up ouch, overridden."));   // the add-on's winning copy
        Assert.Equal("condition", FoundBy("Made-up addon ouch."));
        Assert.Equal("quest alias", FoundBy("Made-up alias line."));
        Assert.Equal("scene alias", FoundBy("Made-up scene one."));   // alias 1: forced to the placed LnNpc
        Assert.Equal("scene alias", FoundBy("Made-up scene two."));
        Assert.Equal("scene alias", FoundBy("Made-up answer."));      // a Player Dialogue action's NPC response
        Assert.Equal("voice type", FoundBy("Made-up shared line."));
        Assert.True(lines.Single(l => l.GetProperty("text").GetString() == "Made-up shared line.").GetProperty("shared").GetBoolean());
        Assert.Equal(8, lines.Count);
        foreach (var gone in new[] { "Made-up ouch.", "Made-up not me.", "Made-up other.", "Made-up anyone line.", "Made-up question.", "Made-up other scene." })
            Assert.DoesNotContain(lines, l => l.GetProperty("text").GetString() == gone);
        Assert.Equal("Happy", lines.Single(l => l.GetProperty("foundBy").GetString() == "speaker").GetProperty("emotion").GetString());
        Assert.Equal("(none)", lines.Single(l => l.GetProperty("text").GetString() == "Made-up answer.").GetProperty("emotion").GetString());
        Assert.Equal(1, j.GetProperty("playerLines").GetInt32());
        Assert.Equal(2, j.GetProperty("genericLines").GetInt32());   // LN_Generic and the negated LN_NotId
        Assert.Equal(JsonValueKind.Null, lines[0].GetProperty("wem").ValueKind);

        var counts = j.GetProperty("counts").EnumerateArray()
            .ToDictionary(c => $"{c.GetProperty("category").GetString()} / {c.GetProperty("subtype").GetString()}", c => (c.GetProperty("count").GetInt32(), c.GetProperty("shared").GetInt32()));
        Assert.Equal((2, 0), counts["Combat / Hit"]);
        Assert.Equal((1, 0), counts["Misc / Hello"]);
        Assert.Equal((1, 1), counts["Misc / Idle"]);
        Assert.Equal((3, 0), counts["Scene / Custom"]);
        Assert.Equal(4, counts.Count);
        Assert.Equal("FOUND 8 lines of LnNpc (1 shared, 4 types)", j.GetProperty("result").GetString());
    }

    static string Npc(Game s) => s.Npc.ToString();

    [Fact]
    public void Text_groups_by_type_and_ends_with_the_table_counts_and_result()
    {
        using var s = new Game();
        var (code, stdout, stderr) = s.InOrder("LnNpc");
        Assert.True(code == 0, stdout + stderr);
        if (Environment.GetEnvironmentVariable("SFMODCHECK_LINES_EXAMPLE") is { Length: > 0 } keep) File.WriteAllText(keep, stdout);
        Assert.Contains($"lines: LnNpc [{s.Npc}] in 2 plugins (", stdout);
        Assert.Contains("  voice type: LnVoice [", stdout);
        Assert.Contains("(female)", stdout);
        Assert.Contains(" Combat / Hit (2 lines)", stdout);
        Assert.Contains($"  {s.IsIdInfo} LnHit r1 \"Made-up ouch, overridden.\" wem ?, lip ?, (none), condition", stdout);
        Assert.Contains("shared lines (voice type LnVoice: any NPC with this voice type may say them):", stdout);
        Assert.Contains("types (this NPC | shared):", stdout);
        Assert.Matches(@"\n  Misc / Idle +1 +1\r?\n", stdout);
        Assert.Contains("1 player line in its scenes (the player is silent, not counted)", stdout);
        Assert.Contains("2 generic lines with no speaker, speaker condition or scene alias", stdout);
        Assert.Contains("RESULT: FOUND 8 lines of LnNpc (1 shared, 4 types) (", stdout);
        Assert.True(stdout.IndexOf(" Combat / Hit", StringComparison.Ordinal) < stdout.IndexOf(" Misc / Hello", StringComparison.Ordinal));   // Category enum order
    }

    [Fact]
    public void Type_and_max_narrow_the_listing()
    {
        using var s = new Game();
        var (code, stdout, stderr) = s.InOrder("LnNpc", "--type", "combat");
        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("RESULT: FOUND 2 lines of LnNpc (0 shared, 1 type) of type combat (", stdout);
        Assert.DoesNotContain("Misc /", stdout);

        (code, stdout, _) = s.InOrder("LnNpc", "--type", "Scene/Custom");
        Assert.Equal(0, code);
        Assert.Contains("RESULT: FOUND 3 lines of LnNpc (0 shared, 1 type)", stdout);

        (code, stdout, stderr) = s.InOrder("LnNpc", "--type", "Gossip");
        Assert.Equal(2, code);
        Assert.Contains("--type needs a dialogue Category (Player, Command, Scene, Combat, Favor, Detection, Service, Misc)", stderr);
        Assert.Contains("RESULT: CANNOT CHECK (unknown line type Gossip)", stdout);

        (code, stdout, _) = s.InOrder("LnNpc", "--max", "3");
        Assert.Equal(0, code);
        Assert.Contains("... and 5 more lines (--max 0 lists them all)", stdout);
        Assert.Contains("  Scene / Custom", stdout);   // the table stays complete
        Assert.Contains("RESULT: FOUND 8 lines", stdout);
    }

    [Fact]
    public void Plugin_mode_reads_the_plugin_and_its_masters()
    {
        using var s = new Game();
        var (code, stdout, stderr) = s.Lines(s.Npc.ToString(), "--plugin", s.Addon);
        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("in 2 plugins (LnAddon.esm and its 1 master)", stdout);
        Assert.Contains("\"Made-up addon ouch.\"", stdout);
        Assert.Contains("RESULT: FOUND 8 lines of LnNpc", stdout);

        (code, stdout, stderr) = s.Lines("0000080A", "--plugin", s.Addon);
        Assert.Equal(2, code);
        Assert.Contains("is a console FormID", stderr);
    }

    [Fact]
    public void Another_record_type_and_an_unknown_NPC_are_exit_2_and_1()
    {
        using var s = new Game();
        var (code, stdout, stderr) = s.InOrder("LnQuest");
        Assert.Equal(2, code);
        Assert.Contains($"LnQuest is a QUST ({s.Quest}), not an NPC", stderr);
        Assert.Contains("RESULT: CANNOT CHECK (LnQuest is a QUST, not an NPC)", stdout);

        (code, stdout, _) = s.InOrder("LnNobody");
        Assert.Equal(1, code);
        Assert.Contains("RESULT: NOT FOUND (no record with EditorID LnNobody in 2 plugins)", stdout);

        (code, stdout, _) = s.InOrder("LnNobody", "--json");
        Assert.Equal(1, code);
        Assert.Equal("not found", JsonDocument.Parse(stdout).RootElement.GetProperty("result").GetString());

        (code, stdout, _) = s.InOrder("--help");
        Assert.Equal(0, code);
        Assert.Contains("usage: sf-mod-check lines", stdout);
    }
}
