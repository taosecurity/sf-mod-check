// sf-mod-check lines (0.21.0) on made-up plugins: each way a line is found for an NPC (speaker, GetIsID, GetIsAliasRef
// on a UniqueActor alias, a scene Dialogue action whose alias is forced to a placed actor of the NPC, a Player Dialogue
// action's NPC response), the shared GetIsVoiceType block, a negated GetIsID, generic and player lines, the winning copy
// of an overridden line, --type, --max, --plugin, and the not-an-NPC and not-found exits; from step 3 --compare (counts
// per type, missing types, a reference that can't be used), the voice and lip files (loose, in the plugin's archive, in
// the INFO's own plugin's folder, another voice type's folder not counting), --no-files and a template-voice NPC. Each
// fails on 0.20.0, which has no lines command.
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
    ///   LN_OtherDeath Combat / Death   GetIsID(LnOther) == 1 (a type only LnOther has)         not counted
    ///   LN_Templated Misc / Hello      Speaker LnTemplated (an NPC with no voice type)         not counted
    /// The voice files the step 3 test reads are described at the end of the constructor.
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
            uint wem = 0x100;   // each response its own file number: LN_Speaker 0x101, LN_IsId 0x102, ...
            DialogResponses Info(DialogTopic t, string edid, params string[] texts)
            {
                var i = new DialogResponses(b) { EditorID = edid };
                foreach (var x in texts) i.Responses.Add(new DialogResponse { ResponseText = x, WEMFile = ++wem });
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
            // step 3: a type only LnOther speaks (Combat / Death), and an NPC with no voice type of its own
            var death = Topic("LnDeath", DialogTopic.CategoryEnum.Combat, DialogTopic.SubtypeEnum.Death);
            Info(death, "LN_OtherDeath", "Made-up other death.").Conditions.Add(IsId(other.FormKey));
            var templated = b.Npcs.AddNew("LnTemplated");
            Info(hello, "LN_Templated", "Made-up templated hello.").Speaker.SetTo(templated);

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

            // step 3: voice files. LN_Speaker (0x101) has its .wem and .ffxanim loose, LN_IsId (0x102; overridden by the
            // add-on, still LnBase.esm's line, so in its folder) only the .wem, the add-on's LN_AddonHit (0x200) both in
            // the add-on's archive. A .wem of LN_Alias (0x105) in the wrong voice-type folder doesn't count.
            var vdir = Path.Combine(Data, "Sound", "Voice", "LnBase.esm", "LnVoice");
            Directory.CreateDirectory(vdir);
            File.WriteAllBytes(Path.Combine(vdir, "00000101.wem"), [0]);
            File.WriteAllBytes(Path.Combine(vdir, "00000101.ffxanim"), [0]);
            File.WriteAllBytes(Path.Combine(vdir, "00000102.wem"), [0]);
            var odir = Path.Combine(Data, "Sound", "Voice", "LnBase.esm", "LnOtherVoice");
            Directory.CreateDirectory(odir);
            File.WriteAllBytes(Path.Combine(odir, "00000105.wem"), [0]);
            File.WriteAllBytes(Path.Combine(Data, "LnAddon - Main.ba2"),
                AssetsTests.Ba2("GNRL", @"sound\voice\lnaddon.esm\lnvoice\00000200.wem", @"sound\voice\lnaddon.esm\lnvoice\00000200.ffxanim"));
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
        Assert.False(j.TryGetProperty("reference", out _));   // no --compare
        Assert.DoesNotContain("reference", j.GetProperty("counts")[0].EnumerateObject().Select(p => p.Name));

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
        Assert.Contains($"  {s.IsIdInfo} LnHit r1 \"Made-up ouch, overridden.\" wem yes, lip no, (none), condition", stdout);
        Assert.Contains("shared lines (voice type LnVoice: any NPC with this voice type may say them):", stdout);
        Assert.Contains("types (this NPC | shared):", stdout);
        Assert.Matches(@"\n  Misc / Idle +1 +1\r?\n", stdout);
        Assert.Contains("1 player line in its scenes (the player is silent, not counted)", stdout);
        Assert.Contains("2 generic lines with no speaker, speaker condition or scene alias", stdout);
        Assert.Contains("RESULT: FOUND 8 lines of LnNpc (1 shared, 4 types) (", stdout);
        Assert.True(stdout.IndexOf(" Combat / Hit", StringComparison.Ordinal) < stdout.IndexOf(" Misc / Hello", StringComparison.Ordinal));   // Category enum order
    }

    [Fact]
    public void Compare_counts_the_reference_per_type_and_marks_the_missing_types()
    {
        using var s = new Game();
        var (code, stdout, stderr) = s.InOrder("LnNpc", "--compare", "LnOther");
        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("types (this NPC | shared | LnOther [", stdout);
        Assert.Matches(@"\n  Combat / Hit +2 +0 +1\r?\n", stdout);
        Assert.Matches(@"\n  Combat / Death +0 +0 +1   missing\r?\n", stdout);
        Assert.Matches(@"\n  Misc / Idle +1 +1 +0\r?\n", stdout);
        Assert.Matches(@"\n  Scene / Custom +3 +0 +1\r?\n", stdout);
        Assert.DoesNotContain("HINT: to see which types", stdout);
        Assert.Contains("RESULT: FOUND 8 lines of LnNpc (1 shared, 4 types; 1 missing next to LnOther) (", stdout);

        (code, stdout, stderr) = s.InOrder("LnNpc", "--compare", "LnOther", "--json");
        Assert.True(code == 0, stdout + stderr);
        var j = JsonDocument.Parse(stdout).RootElement;
        Assert.Equal("LnOther", j.GetProperty("reference").GetProperty("editorId").GetString());
        Assert.Equal(3, j.GetProperty("reference").GetProperty("lines").GetInt32());
        var counts = j.GetProperty("counts").EnumerateArray().ToDictionary(
            c => $"{c.GetProperty("category").GetString()} / {c.GetProperty("subtype").GetString()}",
            c => (c.GetProperty("count").GetInt32(), c.GetProperty("reference").GetInt32(), c.GetProperty("missing").GetBoolean()));
        Assert.Equal((0, 1, true), counts["Combat / Death"]);
        Assert.Equal((2, 1, false), counts["Combat / Hit"]);
        Assert.Equal((1, 0, false), counts["Misc / Hello"]);
        Assert.Equal(5, counts.Count);

        // --type narrows the reference too
        (code, stdout, _) = s.InOrder("LnNpc", "--compare", "LnOther", "--type", "Scene");
        Assert.Equal(0, code);
        Assert.Contains("(0 shared, 1 type; 0 missing next to LnOther) of type Scene (", stdout);
        Assert.DoesNotContain("Combat / Death", stdout);

        // a reference that isn't there, or isn't an NPC, can't run
        (code, stdout, stderr) = s.InOrder("LnNpc", "--compare", "LnNobody");
        Assert.Equal(2, code);
        Assert.Contains("--compare LnNobody: no record with EditorID LnNobody in 2 plugins", stderr);
        Assert.Contains("RESULT: CANNOT CHECK (the --compare NPC LnNobody isn't found)", stdout);
        (code, stdout, stderr) = s.InOrder("LnNpc", "--compare", "LnQuest");
        Assert.Equal(2, code);
        Assert.Contains("--compare LnQuest is a QUST", stderr);
        Assert.Contains("RESULT: CANNOT CHECK (--compare LnQuest is a QUST, not an NPC)", stdout);
        (code, _, stderr) = s.Lines("LnNpc", "--plugin", s.Addon, "--compare", "0000080A");
        Assert.Equal(2, code);
        Assert.Contains("--compare 0000080A is a console FormID", stderr);
    }

    [Fact]
    public void Voice_and_lip_files_are_found_loose_and_in_the_plugins_archives()
    {
        using var s = new Game();
        var (code, stdout, stderr) = s.InOrder("LnNpc", "--json");
        Assert.True(code == 0, stdout + stderr);
        var lines = JsonDocument.Parse(stdout).RootElement.GetProperty("lines").EnumerateArray().ToList();
        (bool?, bool?) Files(string text)
        {
            var l = lines.Single(x => x.GetProperty("text").GetString() == text);
            static bool? B(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetBoolean();
            return (B(l.GetProperty("wem")), B(l.GetProperty("lip")));
        }
        Assert.Equal((true, true), Files("Made-up hello."));              // loose, both
        Assert.Equal((true, false), Files("Made-up ouch, overridden."));  // loose .wem, in LnBase.esm's folder
        Assert.Equal((true, true), Files("Made-up addon ouch."));         // the add-on's archive
        Assert.Equal((false, false), Files("Made-up alias line."));       // only in another voice type's folder
        Assert.Equal((false, false), Files("Made-up scene two."));

        (code, stdout, _) = s.InOrder("LnNpc", "--no-files");
        Assert.Equal(0, code);
        Assert.Contains("\"Made-up hello.\" wem ?, lip ?, Happy, speaker", stdout);
        Assert.DoesNotContain("wem yes", stdout);
        Assert.Contains("HINT: to see which types of line it lacks next to another NPC", stdout);

        (code, stdout, _) = s.InOrder("LnNpc");
        Assert.Contains("\"Made-up hello.\" wem yes, lip yes, Happy, speaker", stdout);
        Assert.Contains("\"Made-up alias line.\" wem no, lip no, (none), quest alias", stdout);
    }

    [Fact]
    public void An_NPC_with_no_voice_type_of_its_own_gets_question_marks()
    {
        using var s = new Game();
        var (code, stdout, stderr) = s.InOrder("LnTemplated");
        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("voice type: none of its own", stdout);
        Assert.Contains("\"Made-up templated hello.\" wem ?, lip ?, (none), speaker", stdout);
        Assert.Contains("RESULT: FOUND 1 line of LnTemplated (0 shared, 1 type) (", stdout);
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
