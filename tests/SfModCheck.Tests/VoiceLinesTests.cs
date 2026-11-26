// sf-mod-check voice-lines (0.21.0) on a made-up plugin: the exact header, CRLF and no BOM, 8 upper-case hex file
// numbers with the top byte dropped, a line with two voice types (two rows), the speaker and alias rules, a line with no
// voice type (no row and a NOTE; a row with --voice), a quote and a tab in the text, SEX from the Female flag, FACE from
// the emotion keyword, SCRIPT NOTES, FULL PATH, one file number with two texts (one row and a WARNING), a SharedDialog
// line and a player line left out, Windows-1252 bytes (a ? for a character it can't hold) and --utf8, and the exit 2
// cases. Each fails on 0.20.0, which has no voice-lines command.
using System.Text;
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Starfield;
using Noggog;
using Xunit;

namespace SfModCheck.Tests;

public class VoiceLinesTests
{
    static readonly WriteOptions NoGap = new() { GapCheck = false, FixEffects = false };

    static ConditionFloat Cond(ConditionData d) => new() { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1, Data = d };

    static ConditionFloat IsId(FormKey npc)
    {
        var d = new GetIsIDConditionData();
        d.FirstParameter.Link.SetTo(npc);
        return Cond(d);
    }

    /// <summary>Data\VlBase.esm: voice types VlFemale (Female flag) and VlMale, NPCs VlNpc (VlFemale) and VlMan (VlMale),
    /// keyword AnimFaceArchetypeHappy, quest VlQuest with alias 0 (UniqueActor VlMan), and these lines:
    ///   VL_Two        GetIsID(VlNpc), GetIsVoiceType(VlMale)   0x01000ABC  text with a quote and a tab, Happy, notes
    ///   VL_Speaker    Speaker VlNpc                            0x101
    ///   VL_Alias      GetIsAliasRef(0)                         0x200
    ///   VL_Shared1    GetIsID(VlNpc)                           0x300       "Made-up first."
    ///   VL_Shared2    GetIsID(VlNpc)                           0x300       "Made-up second." (same file, another text)
    ///   VL_Shared     GetIsID(VlNpc), SharedDialog VL_Speaker  0x400       left out
    ///   VL_Player     GetIsID(VlNpc), the player's choice in scene VlScene   0x500   left out
    ///   VL_Branch     GetIsID(VlNpc) in a Category Player topic no scene uses 0xA00 vlfemale (a crew comment)
    ///   VL_Accent     GetIsID(VlNpc)                           0x600       "Café — naïve."
    ///   VL_None       no condition, no speaker                 0x700       no voice type
    ///   VL_Silent     GetIsID(VlNpc)                           0           not voiced
    ///   VL_Group      a dialogue group INFO, GetIsID(VlNpc), no responses
    ///   VL_InGroup    in VL_Group, no condition of its own     0x900       vlfemale (the group's condition)
    ///   VL_QuestCond  in quest VlCrew, whose DialogConditions say GetIsID(VlMan)   0x800   vlmale</summary>
    sealed class Game : Sandbox
    {
        public readonly string Data, Plugin_;
        public readonly Dictionary<string, FormKey> Infos = new();

        public Game()
        {
            Data = Sub("Data");
            var b = Plugin.New("VlBase.esm", PluginStyle.Full, "test", "made-up voice lines");
            var female = b.VoiceTypes.AddNew("VlFemale");
            female.Flags = VoiceType.Flag.Female;
            var male = b.VoiceTypes.AddNew("VlMale");
            var npc = b.Npcs.AddNew("VlNpc");
            npc.Voice.SetTo(female);
            var man = b.Npcs.AddNew("VlMan");
            man.Voice.SetTo(male);
            var happy = b.Keywords.AddNew("AnimFaceArchetypeHappy");
            var quest = b.Quests.AddNew("VlQuest");
            quest.Aliases = new ExtendedList<AQuestAlias> { new QuestReferenceAlias { ID = 0, Name = "Man", UniqueActor = man.ToNullableLink<INpcGetter>() } };
            DialogTopic Topic(string edid, DialogTopic.CategoryEnum c)
            {
                var t = new DialogTopic(b) { EditorID = edid, Quest = quest.ToLink<IQuestGetter>(), Category = c, Subtype = DialogTopic.SubtypeEnum.Custom };
                quest.DialogTopics.Add(t);
                return t;
            }
            var misc = Topic("VlMisc", DialogTopic.CategoryEnum.Misc);
            var player = Topic("VlPlayerTopic", DialogTopic.CategoryEnum.Player);
            DialogResponses Info(DialogTopic t, string edid, string text, uint wem)
            {
                var i = new DialogResponses(b) { EditorID = edid };
                i.Responses.Add(new DialogResponse { ResponseText = text, WEMFile = wem });
                t.Responses.Add(i);
                Infos[edid] = i.FormKey;
                return i;
            }
            var two = Info(misc, "VL_Two", "She said \"hi\"\tthere.", 0x01000ABC);
            two.Conditions.Add(IsId(npc.FormKey));
            var vtc = new GetIsVoiceTypeConditionData();
            vtc.VoiceTypeOrList.Link.SetTo(male.FormKey);
            two.Conditions.Add(Cond(vtc));
            two.Responses[0].Emotion.SetTo(happy);
            two.Responses[0].ScriptNotes = "Made-up whisper.";
            var speaker = Info(misc, "VL_Speaker", "Made-up hello.", 0x101);
            speaker.Speaker.SetTo(npc);
            Info(misc, "VL_Alias", "Made-up alias line.", 0x200).Conditions.Add(Cond(new GetIsAliasRefConditionData { FirstParameter = 0 }));
            Info(misc, "VL_Shared1", "Made-up first.", 0x300).Conditions.Add(IsId(npc.FormKey));
            Info(misc, "VL_Shared2", "Made-up second.", 0x300).Conditions.Add(IsId(npc.FormKey));
            var sh = Info(misc, "VL_Shared", "Made-up shared.", 0x400);
            sh.Conditions.Add(IsId(npc.FormKey));
            sh.SharedDialog.SetTo(speaker);
            Info(player, "VL_Player", "Made-up player line.", 0x500).Conditions.Add(IsId(npc.FormKey));
            Info(misc, "VL_Accent", "Café — naïve.", 0x600).Conditions.Add(IsId(npc.FormKey));
            Info(misc, "VL_None", "Made-up anyone line.", 0x700);
            Info(misc, "VL_Silent", "Made-up silent line.", 0).Conditions.Add(IsId(npc.FormKey));
            // a dialogue group: the group INFO (no responses) holds the condition, its line has none of its own
            var group = new DialogResponses(b) { EditorID = "VL_Group" };
            group.Conditions.Add(IsId(npc.FormKey));
            misc.Responses.Add(group);
            Info(misc, "VL_InGroup", "Made-up group line.", 0x900).DialogGroup.SetTo(group);
            // a quest whose DialogConditions name the speaker for all its lines
            var crew = b.Quests.AddNew("VlCrew");
            crew.DialogConditions.Add(IsId(man.FormKey));
            var crewTopic = new DialogTopic(b) { EditorID = "VlCrewTopic", Quest = crew.ToLink<IQuestGetter>(), Category = DialogTopic.CategoryEnum.Combat, Subtype = DialogTopic.SubtypeEnum.Hit };
            crew.DialogTopics.Add(crewTopic);
            Info(crewTopic, "VL_QuestCond", "Made-up crew line.", 0x800);
            // the player's choice in a scene is a player line; a Category Player topic no scene gives the player is the NPC's
            var scene = new Scene(b) { EditorID = "VlScene", Quest = quest.ToNullableLink<IQuestGetter>() };
            var pd = new PlayerDialogueSceneAction { AliasID = 0 };
            pd.DialogueList.Add(new PlayerDialogueSceneActionItem { PlayerChoice = player.ToNullableLink<IDialogTopicGetter>() });
            scene.Actions = [pd];
            quest.Scenes.Add(scene);
            var branch = Topic("VlBranchTopic", DialogTopic.CategoryEnum.Player);
            Info(branch, "VL_Branch", "Made-up crew comment.", 0xA00).Conditions.Add(IsId(npc.FormKey));
            using (var g = new GameData(Data)) PluginWriter.Write(b, Data, g, NoGap);
            Plugin_ = Path.Combine(Data, "VlBase.esm");
        }

        public (int Code, string Stdout, string Stderr) Run(params string[] args) =>
            ExeTests.RunSplit(new[] { "voice-lines", Plugin_ }.Concat(args).Concat(["--data", Data]).ToArray());

        public (int Code, byte[] Bytes, string Stderr) ToFile(params string[] args)
        {
            var o = Path.Combine(Dir, $"out-{Guid.NewGuid():N}.tsv");
            var (code, stdout, stderr) = Run([.. args, "--out", o]);
            Assert.True(code == 0, stdout + stderr);
            Assert.Equal("", stdout);   // the list goes to the file, the messages to stderr
            return (code, File.ReadAllBytes(o), stderr);
        }
    }

    static string Cp1252(byte[] b)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252).GetString(b);
    }

    [Fact]
    public void The_list_has_the_contract_columns_one_row_per_voice_type_and_file()
    {
        using var s = new Game();
        var (_, bytes, stderr) = s.ToFile();
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "no BOM");
        var text = Cp1252(bytes);
        Assert.EndsWith("\r\n", text);
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));   // CRLF only
        var lines = text.Split("\r\n")[..^1];
        Assert.Equal("VOICE TYPE\tFILENAME\tRESPONSE TEXT\tFACE\tSCRIPT NOTES\tSEX\tFULL PATH\tINFO\tRESPONSE", lines[0]);
        var rows = lines[1..].Select(l => l.Split('\t')).ToList();
        Assert.All(rows, r => Assert.Equal(9, r.Length));
        Assert.Equal(new[]
        {
            "vlfemale 00000101", "vlfemale 00000300", "vlfemale 00000600", "vlfemale 00000900", "vlfemale 00000A00", "vlfemale 00000ABC",
            "vlmale 00000200", "vlmale 00000800", "vlmale 00000ABC",
        }, rows.Select(r => $"{r[0]} {r[1]}"));

        var two = rows.Single(r => r[0] == "vlfemale" && r[1] == "00000ABC");
        Assert.Equal("\"She said \"\"hi\"\" there.\"", two[2]);   // the tab became a space, the quotes are CSV-quoted
        Assert.Equal("Happy", two[3]);
        Assert.Equal("Made-up whisper.", two[4]);
        Assert.Equal("female", two[5]);
        Assert.Equal(@"Data\Sound\Voice\VlBase.esm\vlfemale\00000ABC.wem", two[6]);
        Assert.Equal(s.Infos["VL_Two"].ToString(), two[7]);
        Assert.Equal("1", two[8]);
        var twoMale = rows.Single(r => r[0] == "vlmale" && r[1] == "00000ABC");
        Assert.Equal("male", twoMale[5]);
        Assert.Equal(@"Data\Sound\Voice\VlBase.esm\vlmale\00000ABC.wem", twoMale[6]);

        var speaker = rows.Single(r => r[1] == "00000101");   // found by the INFO's Speaker
        Assert.Equal(("", "", "female"), (speaker[3], speaker[4], speaker[5]));
        Assert.Equal(s.Infos["VL_Alias"].ToString(), rows.Single(r => r[1] == "00000200")[7]);   // the alias VlMan fills

        // one file number with two texts: the lower INFO's text, and a WARNING naming both
        var shared = rows.Single(r => r[1] == "00000300");
        Assert.Equal("Made-up first.", shared[2]);
        Assert.Contains($"WARNING: voice type vlfemale, file 00000300: {s.Infos["VL_Shared1"]} r1 and {s.Infos["VL_Shared2"]} r1 have different texts", stderr);

        // left out: SharedDialog, player, not voiced; no voice type: a NOTE
        foreach (var gone in new[] { "Made-up shared.", "Made-up player line.", "Made-up silent line.", "Made-up anyone line." })
            Assert.DoesNotContain(gone, text);
        Assert.Contains($"NOTE: 1 voiced line has no voice type (first: VL_None ({s.Infos["VL_None"]}))", stderr);
        Assert.Contains("RESULT: 9 rows, 10 voiced lines, 2 voice types; 1 line with no voice type (", stderr);

        // Windows-1252: é, — and ï as their bytes
        var accent = rows.Single(r => r[1] == "00000600");
        Assert.Equal("Café — naïve.", accent[2]);
        var at = IndexOf(bytes, Encoding.ASCII.GetBytes("Caf"));
        Assert.Equal(new byte[] { 0xE9, 0x20, 0x97, 0x20, (byte)'n', (byte)'a', 0xEF }, bytes[(at + 3)..(at + 10)]);
        Assert.DoesNotContain("can't be written in Windows-1252", stderr);
    }

    [Fact]
    public void A_character_windows_1252_cant_hold_becomes_a_question_mark_and_is_counted()
    {
        // a plugin's own strings are Windows-1252 already, so this is the writer on its own (a UTF-8 text from a
        // localized plugin would reach it)
        var (b, n) = VoiceLinesCommand.Encode("Snow ☃ and 😀, café", false);
        Assert.Equal(2, n);   // the emoji is one character, not two halves
        Assert.Equal("Snow ? and ?, café", Cp1252(b));
        var (u, m) = VoiceLinesCommand.Encode("Snow ☃", true);
        Assert.Equal(0, m);
        Assert.Equal("Snow ☃", Encoding.UTF8.GetString(u));
        Assert.Equal("\"a \"\"b\"\" c\"", VoiceLinesCommand.Field("a \"b\"\tc"));
        Assert.Equal("a b", VoiceLinesCommand.Field("a\r\nb".Replace("\r\n", "\n")));
    }

    static int IndexOf(byte[] hay, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= hay.Length; i++)
            if (hay.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        throw new Xunit.Sdk.XunitException("not found");
    }

    [Fact]
    public void Voice_gives_lines_with_no_voice_type_their_rows_and_utf8_keeps_every_character()
    {
        using var s = new Game();
        var (_, bytes, stderr) = s.ToFile("--voice", "VlMale", "--utf8");
        var text = new UTF8Encoding(false, true).GetString(bytes);
        Assert.Contains("Café — naïve.", text);
        Assert.DoesNotContain("Windows-1252", stderr);
        var none = text.Split("\r\n").Single(l => l.Contains("Made-up anyone line.")).Split('\t');
        Assert.Equal(("vlmale", "00000700", "male"), (none[0], none[1], none[5]));
        Assert.DoesNotContain("no voice type (first", stderr);
        Assert.Contains("RESULT: 10 rows, 10 voiced lines, 2 voice types; 0 lines with no voice type (", stderr);
        // --voice only fills lines that name none: VL_Speaker stays vlfemale only
        Assert.DoesNotContain("vlmale\t00000101", text);

        // a --voice name that isn't a voice type still names the folder, with SEX empty
        var (_, b2, e2) = s.ToFile("--voice", "MadeUpVoice");
        var row = Cp1252(b2).Split("\r\n").Single(l => l.Contains("Made-up anyone line.")).Split('\t');
        Assert.Equal(("madeupvoice", ""), (row[0], row[5]));
        Assert.Contains("NOTE: --voice MadeUpVoice isn't a voice type in the plugin or its masters", e2);
    }

    [Fact]
    public void Lines_reads_the_dialogue_group_and_quest_conditions_too()
    {
        using var s = new Game();
        var (code, stdout, stderr) = ExeTests.RunSplit("lines", "VlNpc", "--plugin", s.Plugin_, "--data", s.Data, "--json", "--no-files");
        Assert.True(code == 0, stdout + stderr);
        var lines = System.Text.Json.JsonDocument.Parse(stdout).RootElement.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal("condition", lines.Single(l => l.GetProperty("text").GetString() == "Made-up group line.").GetProperty("foundBy").GetString());
        Assert.DoesNotContain(lines, l => l.GetProperty("text").GetString() == "Made-up crew line.");
        var (c2, o2, e2) = ExeTests.RunSplit("lines", "VlMan", "--plugin", s.Plugin_, "--data", s.Data, "--json", "--no-files");
        Assert.True(c2 == 0, o2 + e2);
        var man = System.Text.Json.JsonDocument.Parse(o2).RootElement.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal("condition", man.Single(l => l.GetProperty("text").GetString() == "Made-up crew line.").GetProperty("foundBy").GetString());
    }

    [Fact]
    public void Stdout_without_out_and_the_could_not_run_exits()
    {
        using var s = new Game();
        var (code, stdout, stderr) = s.Run("--quiet");
        Assert.True(code == 0, stdout + stderr);
        Assert.StartsWith("VOICE TYPE\tFILENAME\t", stdout);
        Assert.Contains("RESULT: 9 rows", stderr);
        Assert.DoesNotContain("RESULT", stdout);

        var (c1, _, e1) = ExeTests.RunSplit("voice-lines", Path.Combine(s.Dir, "NoSuch.esm"), "--data", s.Data);
        Assert.Equal(2, c1);
        Assert.Contains("RESULT: CANNOT CHECK (no plugin file NoSuch.esm)", e1);
        var (c2, _, e2) = s.Run("--plugins", "x.txt");
        Assert.Equal(2, c2);
        Assert.Contains("doesn't take --plugins", e2);
        var (c3, _, e3) = ExeTests.RunSplit("voice-lines");
        Assert.Equal(2, c3);
        Assert.Contains("RESULT: CANNOT CHECK (no plugin given)", e3);
        var (c4, o4, _) = ExeTests.RunSplit("voice-lines", "--help");
        Assert.Equal(0, c4);
        Assert.Contains("VOICE TYPE  FILENAME  RESPONSE TEXT", o4);

        // a master that isn't there: exit 2
        var a = Plugin.New("VlAddon.esm", PluginStyle.Light, "test", "made-up add-on");
        using (var g = new GameData(s.Data))
        {
            var bq = g.Open("VlBase.esm").Quests.First();
            a.Quests.GetOrAddAsOverride(bq);
            PluginWriter.Write(a, s.Sub("addon"), g, NoGap);
        }
        var (c5, _, e5) = ExeTests.RunSplit("voice-lines", Path.Combine(s.Sub("addon"), "VlAddon.esm"), "--data", s.Sub("empty"));
        Assert.Equal(2, c5);
        Assert.Contains("RESULT: CANNOT CHECK (", e5);
    }
}
