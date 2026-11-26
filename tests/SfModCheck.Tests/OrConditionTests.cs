// The 0.21.1 OR-conditions fix (shipped in 0.24.0) in lines and voice-lines, on a made-up plugin, and the bundled nits:
// design test 11 (a GetIsID of another NPC OR an alias the NPC fills keeps the NPC's row), the AND control, a pure
// GetIsID OR chain, a trailing OR flag, the voice-lines --help order, the no-voice-type NOTE naming templated NPCs, and
// --out suggesting each command's own file name. The OR tests fail on 0.23.0, which reads each condition alone.
using System.Text.Json;
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Starfield;
using Noggog;
using Xunit;

namespace SfModCheck.Tests;

public class OrConditionTests
{
    static readonly WriteOptions NoGap = new() { GapCheck = false, FixEffects = false };

    static ConditionFloat Cond(ConditionData d, bool or = false) =>
        new() { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1, Data = d, Flags = or ? Condition.Flag.OR : 0 };

    static ConditionFloat IsId(FormKey npc, bool or = false)
    {
        var d = new GetIsIDConditionData();
        d.FirstParameter.Link.SetTo(npc);
        return Cond(d, or);
    }

    static ConditionFloat Alias(int id, bool or = false) => Cond(new GetIsAliasRefConditionData { FirstParameter = id }, or);

    /// <summary>Data\OrBase.esm: voice types OrVoice (female) and OrOtherVoice, NPCs OrNpc (OrVoice), OrOther and OrThird
    /// (both OrOtherVoice), quest OrQuest with alias 0 (UniqueActor OrNpc), and these lines in one Misc topic:
    ///   OR_Mixed     GetIsID(OrOther) OR GetIsAliasRef(0)      0x101   OrNpc's (quest alias) and OrOther's
    ///   OR_AndMixed  GetIsID(OrOther) AND GetIsAliasRef(0)     0x102   OrOther's only
    ///   OR_Pure      GetIsID(OrOther) OR GetIsID(OrThird)      0x103   not OrNpc's
    ///   OR_Trailing  GetIsID(OrOther) with an OR flag, alone   0x104   not OrNpc's (the OR joins nothing)
    ///   OR_Self      GetIsID(OrOther) OR GetIsID(OrNpc)        0x105   OrNpc's (condition) and OrOther's
    ///   OR_None      no condition                              0x106   no voice type (the NOTE)</summary>
    sealed class Game : Sandbox
    {
        public readonly string Data, Plugin_;

        public Game()
        {
            Data = Sub("Data");
            var b = Plugin.New("OrBase.esm", PluginStyle.Full, "test", "made-up OR conditions");
            var voice = b.VoiceTypes.AddNew("OrVoice");
            voice.Flags = VoiceType.Flag.Female;
            var otherVoice = b.VoiceTypes.AddNew("OrOtherVoice");
            var npc = b.Npcs.AddNew("OrNpc");
            npc.Voice.SetTo(voice);
            var other = b.Npcs.AddNew("OrOther");
            other.Voice.SetTo(otherVoice);
            var third = b.Npcs.AddNew("OrThird");
            third.Voice.SetTo(otherVoice);
            var quest = b.Quests.AddNew("OrQuest");
            quest.Aliases = new ExtendedList<AQuestAlias> { new QuestReferenceAlias { ID = 0, Name = "Npc", UniqueActor = npc.ToNullableLink<INpcGetter>() } };
            var misc = new DialogTopic(b) { EditorID = "OrMisc", Quest = quest.ToLink<IQuestGetter>(), Category = DialogTopic.CategoryEnum.Misc, Subtype = DialogTopic.SubtypeEnum.Custom };
            quest.DialogTopics.Add(misc);
            DialogResponses Info(string edid, string text, uint wem, params Condition[] conds)
            {
                var i = new DialogResponses(b) { EditorID = edid };
                i.Responses.Add(new DialogResponse { ResponseText = text, WEMFile = wem });
                i.Conditions.AddRange(conds);
                misc.Responses.Add(i);
                return i;
            }
            Info("OR_Mixed", "Made-up mixed.", 0x101, IsId(other.FormKey, or: true), Alias(0));
            Info("OR_AndMixed", "Made-up and.", 0x102, IsId(other.FormKey), Alias(0));
            Info("OR_Pure", "Made-up pure.", 0x103, IsId(other.FormKey, or: true), IsId(third.FormKey));
            Info("OR_Trailing", "Made-up trailing.", 0x104, IsId(other.FormKey, or: true));
            Info("OR_Self", "Made-up self.", 0x105, IsId(other.FormKey, or: true), IsId(npc.FormKey));
            Info("OR_None", "Made-up anyone.", 0x106);
            using (var g = new GameData(Data)) PluginWriter.Write(b, Data, g, NoGap);
            Plugin_ = Path.Combine(Data, "OrBase.esm");
        }

        public Dictionary<string, string> FoundBy(string npc)
        {
            var (code, stdout, stderr) = ExeTests.RunSplit("lines", npc, "--plugin", Plugin_, "--data", Data, "--json", "--no-files");
            Assert.True(code == 0, stdout + stderr);
            return JsonDocument.Parse(stdout).RootElement.GetProperty("lines").EnumerateArray()
                .ToDictionary(l => l.GetProperty("text").GetString()!, l => l.GetProperty("foundBy").GetString()!);
        }
    }

    [Fact]
    public void Lines_reads_an_OR_chain_as_alternatives()
    {
        using var s = new Game();
        var mine = s.FoundBy("OrNpc");
        Assert.Equal("quest alias", mine["Made-up mixed."]);   // design test 11
        Assert.Equal("condition", mine["Made-up self."]);
        foreach (var gone in new[] { "Made-up and.", "Made-up pure.", "Made-up trailing.", "Made-up anyone." })
            Assert.DoesNotContain(gone, mine.Keys);
        Assert.Equal(2, mine.Count);

        var theirs = s.FoundBy("OrOther");
        foreach (var t in new[] { "Made-up mixed.", "Made-up and.", "Made-up pure.", "Made-up trailing.", "Made-up self." })
            Assert.Equal("condition", theirs[t]);
    }

    [Fact]
    public void Voice_lines_gives_an_OR_chain_every_alternative_speakers_voice()
    {
        using var s = new Game();
        var (code, stdout, stderr) = ExeTests.RunSplit("voice-lines", s.Plugin_, "--data", s.Data);
        Assert.True(code == 0, stdout + stderr);
        var rows = stdout.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(l => l.Split('\t'))
            .GroupBy(f => f[1]).ToDictionary(g => g.Key, g => g.Select(f => f[0].ToLowerInvariant()).OrderBy(x => x).ToList());
        Assert.Equal(["orothervoice", "orvoice"], rows["00000101"]);   // design test 11: the alias's NPC too
        Assert.Equal(["orothervoice"], rows["00000102"]);              // AND: GetIsID settles it
        Assert.Equal(["orothervoice"], rows["00000103"]);
        Assert.Equal(["orothervoice"], rows["00000104"]);
        Assert.Equal(["orothervoice", "orvoice"], rows["00000105"]);
        Assert.DoesNotContain("00000106", rows.Keys);
        // the NOTE names the templated-NPC reason too (0.21.0 review nit)
        Assert.Contains("NOTE: 1 voiced line has no voice type (first: OR_None", stderr);
        Assert.Contains("takes its voice from a template", stderr);
    }

    [Fact]
    public void Help_gives_the_full_order_and_out_suggests_each_commands_own_file()
    {
        var (c1, o1, _) = ExeTests.RunSplit("voice-lines", "--help");
        Assert.Equal(0, c1);
        Assert.Contains("the first of these that names an NPC or a voice type: its own conditions", o1);
        Assert.Contains("its dialogue group's conditions, its quest's.", o1);

        using var s = new Game();
        var (c2, _, e2) = ExeTests.RunSplit("voice-lines", s.Plugin_, "--data", s.Data, "--out");
        Assert.Equal(2, c2);
        Assert.Contains("for example: --out voice-lines.txt", e2);
        var (c3, _, e3) = ExeTests.RunSplit("scenes", "OrNpc", "--data", s.Data, "--out");
        Assert.Equal(2, c3);
        Assert.Contains("for example: --out scenes.md", e3);
        var (c4, _, e4) = ExeTests.RunSplit("ba2", "extract", Path.Combine(s.Dir, "x.ba2"), "*.wem", "--out");
        Assert.Equal(2, c4);
        Assert.Contains("for example: --out extracted", e4);
        var (c5, _, e5) = ExeTests.RunSplit("conflict", "--out");
        Assert.Equal(2, c5);
        Assert.Contains("for example: --out conflict-report", e5);
    }
}
