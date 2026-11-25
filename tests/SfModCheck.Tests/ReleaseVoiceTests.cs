// The release command's voice-file check (0.18.0, GAPS G14), on made-up plugins and archives.
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Starfield;
using SfModCheck;
using Xunit;

namespace SfModCheck.Tests;

public class ReleaseVoiceTests
{
    static readonly WriteOptions NoGap = new() { GapCheck = false };

    static DialogResponses Info(StarfieldMod mod, DialogTopic topic, string edid, params uint[] numbers)
    {
        var i = new DialogResponses(mod) { EditorID = edid };
        foreach (var n in numbers) i.Responses.Add(new DialogResponse { WEMFile = n });
        topic.Responses.Add(i);
        return i;
    }

    static ConditionFloat IsId(INpcGetter npc)
    {
        var d = new GetIsIDConditionData();
        d.FirstParameter.Link.SetTo(npc.FormKey);
        return new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1, Data = d };
    }

    static ConditionFloat IsVoiceType(FormKey vtOrList)
    {
        var d = new GetIsVoiceTypeConditionData();
        d.VoiceTypeOrList.Link.SetTo(vtOrList);
        return new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1, Data = d };
    }

    /// <summary>A light plugin with two voice types, an NPC, a form list of both voice types and these lines:
    /// RV_Complete (GetIsID on the NPC, voice type A, 00000101), RV_MissingB (GetIsVoiceType on the list, 00000102),
    /// RV_NoFile (no condition, 00000103), RV_Player (a player line, 00000104), RV_NoFace (no condition, 00000105),
    /// RV_Header (no responses) and RV_Shared (a SharedDialog link, 00000106).</summary>
    static string WritePlugin(string dir, string name = "RvTest.esm", bool complete = false)
    {
        Directory.CreateDirectory(dir);
        var mod = Plugin.New(name, PluginStyle.Light, "test", "release voice test");
        var a = mod.VoiceTypes.AddNew("RvVoiceA");
        var b = mod.VoiceTypes.AddNew("RvVoiceB");
        var npc = mod.Npcs.AddNew("RvNpc");
        npc.Voice.SetTo(a);
        var list = mod.FormLists.AddNew("RvVoices");
        list.Items.Add(a.ToLink<IStarfieldMajorRecordGetter>());
        list.Items.Add(b.ToLink<IStarfieldMajorRecordGetter>());
        var quest = mod.Quests.AddNew("RvQuest");
        DialogTopic Topic(string edid)
        {
            var t = new DialogTopic(mod) { EditorID = edid, Quest = quest.ToLink<IQuestGetter>() };
            quest.DialogTopics.Add(t);
            return t;
        }
        var topic = Topic("RvTopic");
        Info(mod, topic, "RV_Complete", 0x101).Conditions.Add(IsId(npc));
        if (!complete)
        {
            Info(mod, topic, "RV_MissingB", 0x102).Conditions.Add(IsVoiceType(list.FormKey));
            Info(mod, topic, "RV_NoFile", 0x103);
            Info(mod, topic, "RV_NoFace", 0x105);
            var shared = Info(mod, topic, "RV_Shared", 0x106);
            shared.SharedDialog.SetTo(Info(mod, topic, "RV_SharedTarget").FormKey);
            Info(mod, topic, "RV_Header");
            var ptopic = Topic("RvPlayerTopic");
            Info(mod, ptopic, "RV_Player", 0x104);
            var scene = new Scene(mod) { EditorID = "RvScene", Quest = quest.ToNullableLink<IQuestGetter>() };
            quest.Scenes.Add(scene);
            scene.Actions = [new DialogueSceneAction { AliasID = -2, Topic = ptopic.ToNullableLink<IDialogTopicGetter>() }];
        }
        using (var g = new GameData(dir)) PluginWriter.Write(mod, dir, g, NoGap);
        return Path.Combine(dir, name);
    }

    static string Data(Sandbox s)
    {
        var data = s.Sub("Data");
        ReleaseNeedsTests.Write(data, "Starfield.esm", (_, _) => { });
        return data;
    }

    [Fact]
    public void A_missing_voice_type_and_a_line_with_no_file_warn_and_player_header_and_shared_lines_are_skipped()
    {
        using var s = new Sandbox();
        var data = Data(s);
        var pkg = s.Sub("RvTest");
        WritePlugin(pkg);
        File.WriteAllBytes(Path.Combine(pkg, "RvTest - Voices_en.ba2"), AssetsTests.Ba2("GNRL",
            "sound\\voice\\rvtest.esm\\rvvoicea\\00000101.wem", "sound\\voice\\rvtest.esm\\rvvoicea\\00000101.ffxanim",
            "sound\\voice\\rvtest.esm\\rvvoicea\\00000102.wem", "sound\\voice\\rvtest.esm\\rvvoicea\\00000102.ffxanim",
            "sound\\voice\\rvtest.esm\\rvvoiceb\\00000105.wem"));
        var f = ReleaseVoice.Run(ReleaseNeedsTests.Context(s, pkg, data)).ToList();
        var list = ReleaseNeedsTests.List(f);
        var warn = f.Where(x => x.Level == Level.Warning).ToList();
        Assert.True(warn.Count == 2, list);
        Assert.All(warn, x => Assert.Equal("voice-missing", x.Rule));
        Assert.Contains(warn, x => x.Text.Contains("RV_MissingB") && x.Text.Contains("voice type rvvoiceb") &&
                                   x.Text.Contains("sound\\voice\\rvtest.esm\\rvvoiceb\\00000102.wem") && x.Text.Contains("RvTest - Voices_en.ba2"));
        Assert.Contains(warn, x => x.Text.Contains("RV_NoFile") && x.Text.Contains("00000103.wem") && x.Text.Contains("silent in every voice"));
        Assert.DoesNotContain(f, x => x.Text.Contains("RV_Complete") || x.Text.Contains("RV_Player") || x.Text.Contains("RV_Header") || x.Text.Contains("RV_Shared"));
        Assert.Contains(f, x => x.Level == Level.Note && x.Text.Contains("no .ffxanim") && x.Text.Contains("rvvoiceb\\00000105.ffxanim"));
    }

    [Fact]
    public void A_complete_mod_gets_one_clean_note_and_loose_files_count()
    {
        using var s = new Sandbox();
        var data = Data(s);
        var pkg = s.Sub("RvDone");
        WritePlugin(pkg, "RvDone.esm", complete: true);
        var dir = Path.Combine(pkg, "Sound", "Voice", "RvDone.esm", "RvVoiceA");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "00000101.wem"), new byte[4]);
        File.WriteAllBytes(Path.Combine(dir, "00000101.ffxanim"), new byte[4]);
        var f = ReleaseVoice.Run(ReleaseNeedsTests.Context(s, pkg, data)).ToList();
        var n = Assert.Single(f);
        Assert.True(n.Level == Level.Note && n.Text.Contains("1 voiced dialogue response checked, every one has its voice file"), ReleaseNeedsTests.List(f));
    }

    [Fact]
    public void Dialogue_with_no_voice_files_at_all_is_one_note_not_a_warning_per_line()
    {
        using var s = new Sandbox();
        var data = Data(s);
        var pkg = s.Sub("RvSilent");
        WritePlugin(pkg, "RvSilent.esm");
        var f = ReleaseVoice.Run(ReleaseNeedsTests.Context(s, pkg, data)).ToList();
        var n = Assert.Single(f);
        Assert.True(n.Level == Level.Note && n.Rule == "voice-missing" && n.Text.Contains("no voice files") && n.Text.Contains("4 voiced dialogue responses"),
            ReleaseNeedsTests.List(f));
    }

    [Fact]
    public void Each_voice_language_is_checked_on_its_own_and_warnings_are_capped()
    {
        using var s = new Sandbox();
        var data = Data(s);
        var pkg = s.Sub("RvLang");
        WritePlugin(pkg, "RvLang.esm");
        string[] all = ["rvvoicea\\00000101", "rvvoicea\\00000102", "rvvoiceb\\00000102", "rvvoicea\\00000103", "rvvoicea\\00000105"];
        File.WriteAllBytes(Path.Combine(pkg, "RvLang - Voices_en.ba2"), AssetsTests.Ba2("GNRL",
            all.SelectMany(x => new[] { $"sound\\voice\\rvlang.esm\\{x}.wem", $"sound\\voice\\rvlang.esm\\{x}.ffxanim" }).ToArray()));
        File.WriteAllBytes(Path.Combine(pkg, "RvLang - Voices_de.ba2"), AssetsTests.Ba2("GNRL", "sound\\voice\\rvlang.esm\\rvvoicea\\00000101.wem"));
        var old = ReleaseVoice.MaxWarnings;
        ReleaseVoice.MaxWarnings = 2;
        try
        {
            var f = ReleaseVoice.Run(ReleaseNeedsTests.Context(s, pkg, data)).ToList();
            var list = ReleaseNeedsTests.List(f);
            Assert.True(f.Count(x => x.Level == Level.Warning && x.Text.Contains("Voices_en")) == 0, list);
            Assert.Contains(f, x => x.Level == Level.Note && x.Text.Contains("RvLang - Voices_en.ba2: 4 voiced dialogue responses checked"));
            var de = f.Where(x => x.Level == Level.Warning && x.Text.Contains("Voices_de")).ToList();
            Assert.True(de.Count == 3, list);   // 2 lines and "2 more voice files missing"
            Assert.Contains(de, x => x.Text.Contains("2 more voice files missing"));
        }
        finally { ReleaseVoice.MaxWarnings = old; }
    }
}
