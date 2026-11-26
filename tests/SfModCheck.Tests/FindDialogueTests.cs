// sf-mod-check find --full on dialogue (0.22.0, item A): a topic (DIAL) lists its lines (INFO children) with the plugin
// that wins each one and each response's number, text, emotion and face-animation fields; an INFO's own tree shows
// those fields under Mutagen's names. Made-up plugins only. The DIAL list fails on 0.21.0 (no "lines:" block, no
// "infos" JSON key); the INFO tree test passes there too (the fields already printed: it only needed a test).
using System.Text.Json;
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Starfield;
using Xunit;

namespace SfModCheck.Tests;

public class FindDialogueTests
{
    static readonly WriteOptions NoGap = new() { GapCheck = false, FixEffects = false };

    /// <summary>FdBase.esm (full): keywords AnimFaceArchetypeHappy and FD_Face, idle FD_Idle, quest FD_Quest with topic
    /// FD_Topic holding FD_InfoA (two responses: "Made-up hello." with emotion Happy, emotion out 50, face archetype
    /// FD_Face and NPC animation FD_Idle; "Made-up second line.") and FD_InfoB (one response "Made-up bye.").
    /// FdMod.esm (light, master FdBase.esm) overrides FD_InfoB with the text "Made-up bye, changed.". Plugins.txt lists
    /// both. Mods\ holds the same FdMod.esm for --plugin runs (its master found through --masters-dir Data).</summary>
    sealed class Game : Sandbox
    {
        public readonly string Data, PluginsTxt;
        public readonly FormKey Topic, InfoA, InfoB, Happy, Face;

        public Game()
        {
            Data = Sub("Data");
            var b = Plugin.New("FdBase.esm", PluginStyle.Full, "test", "made-up base");
            var happy = b.Keywords.AddNew("AnimFaceArchetypeHappy");
            var face = b.Keywords.AddNew("FD_Face");
            var idle = b.IdleAnimations.AddNew("FD_Idle");
            Happy = happy.FormKey; Face = face.FormKey;
            var quest = b.Quests.AddNew("FD_Quest");
            var topic = new DialogTopic(b) { EditorID = "FD_Topic", Quest = quest.ToLink<IQuestGetter>(), Category = DialogTopic.CategoryEnum.Misc, Subtype = DialogTopic.SubtypeEnum.Custom };
            quest.DialogTopics.Add(topic);
            Topic = topic.FormKey;
            var a = new DialogResponses(b) { EditorID = "FD_InfoA" };
            var first = new DialogResponse { ResponseText = "Made-up hello.", WEMFile = 0x101, EmotionOut = 50 };
            first.Emotion.SetTo(happy);
            first.Animation = new Animation();
            first.Animation.FaceArchetype.SetTo(face);
            first.Animation.NpcAnimation.SetTo(idle);
            a.Responses.Add(first);
            a.Responses.Add(new DialogResponse { ResponseText = "Made-up second line.", WEMFile = 0x102 });
            topic.Responses.Add(a);
            InfoA = a.FormKey;
            var bi = new DialogResponses(b) { EditorID = "FD_InfoB" };
            bi.Responses.Add(new DialogResponse { ResponseText = "Made-up bye.", WEMFile = 0x103 });
            topic.Responses.Add(bi);
            InfoB = bi.FormKey;
            using (var g = new GameData(Data)) PluginWriter.Write(b, Data, g, NoGap);

            var m = Plugin.New("FdMod.esm", PluginStyle.Light, "test", "made-up mod");
            using (var g = new GameData(Data))
            {
                var baseMod = g.Open("FdBase.esm");
                var bq = baseMod.Quests.First();
                var q = m.Quests.GetOrAddAsOverride(bq);
                q.DialogTopics.Clear(); q.Scenes.Clear();
                var t = (DialogTopic)bq.DialogTopics.First().DeepCopy();
                t.Responses.Clear();
                var over = (DialogResponses)bq.DialogTopics.First().Responses.First(i => i.EditorID == "FD_InfoB").DeepCopy();
                over.Responses[0].ResponseText = "Made-up bye, changed.";
                t.Responses.Add(over);
                q.DialogTopics.Add(t);
                PluginWriter.Write(m, Data, g, NoGap);
            }
            PluginsTxt = Path.Combine(Dir, "Plugins.txt");
            File.WriteAllLines(PluginsTxt, ["*FdBase.esm", "*FdMod.esm"]);
            if (Environment.GetEnvironmentVariable("SFMODCHECK_FIND_DIALOGUE_KEEP") is { Length: > 0 } keep)
            {
                Directory.CreateDirectory(Path.Combine(keep, "Data"));
                foreach (var f in Directory.EnumerateFiles(Data)) File.Copy(f, Path.Combine(keep, "Data", Path.GetFileName(f)), true);
                File.Copy(PluginsTxt, Path.Combine(keep, "Plugins.txt"), true);
            }
        }

        public (int Code, string Stdout, string Stderr) Find(params string[] args) =>
            ExeTests.RunSplit(new[] { "find" }.Concat(args).Concat(["--data", Data, "--plugins", PluginsTxt]).ToArray());

        public (int Code, string Stdout, string Stderr) FindPlugin(params string[] args) =>
            ExeTests.RunSplit(new[] { "find" }.Concat(args).Concat(["--plugin", Path.Combine(Data, "FdMod.esm"), "--data", Data]).ToArray());
    }

    static string Norm(string s) => s.Replace("\r", "");

    [Fact]
    public void Full_on_a_topic_lists_its_lines_with_winners_responses_emotions_and_faces()
    {
        using var game = new Game();
        var (code, stdout, stderr) = game.Find("FD_Topic", "--full");
        Assert.True(code == 0, stdout + stderr);
        var text = Norm(stdout);
        Assert.Contains($"FD_Topic [{game.Topic}] DIAL", text);
        Assert.Contains("  lines: 2 INFOs (winning copies)\n", text);
        Assert.Contains($"    FD_InfoA [{game.InfoA}], winner FdBase.esm\n", text);
        Assert.Contains("      1: \"Made-up hello.\"\n", text);
        Assert.Contains($"         Emotion: AnimFaceArchetypeHappy [{game.Happy}], EmotionOut: 50, Animation.FaceArchetype: FD_Face [{game.Face}], Animation.NpcAnimation: FD_Idle [", text);
        Assert.Contains("      2: \"Made-up second line.\"\n", text);
        Assert.Contains($"    FD_InfoB [{game.InfoB}], winner FdMod.esm\n", text);
        Assert.Contains("      1: \"Made-up bye, changed.\"\n", text);
        Assert.DoesNotContain("Made-up bye.\"", text);   // the losing copy's text
        // the lines come after the topic's tree and before the RESULT line
        Assert.True(text.IndexOf("  tree:", StringComparison.Ordinal) < text.IndexOf("  lines:", StringComparison.Ordinal));
        Assert.Contains("RESULT: FOUND 1 record (", text);

        (code, stdout, stderr) = game.Find("FD_Topic", "--full", "--json");
        Assert.True(code == 0, stdout + stderr);
        using var doc = JsonDocument.Parse(stdout);
        var match = doc.RootElement.GetProperty("matches")[0];
        Assert.True(match.TryGetProperty("tree", out _));
        Assert.True(match.TryGetProperty("fields", out _));   // today's keys stay
        var infos = match.GetProperty("infos");
        Assert.Equal(2, infos.GetProperty("total").GetInt32());
        var recs = infos.GetProperty("records");
        Assert.Equal(2, recs.GetArrayLength());
        Assert.Equal(game.InfoA.ToString(), recs[0].GetProperty("formKey").GetString());
        Assert.Equal("FdBase.esm", recs[0].GetProperty("winner").GetString());
        var r0 = recs[0].GetProperty("responses");
        Assert.Equal(2, r0.GetArrayLength());
        Assert.Equal(1, r0[0].GetProperty("number").GetInt32());
        Assert.Equal("Made-up hello.", r0[0].GetProperty("text").GetString());
        Assert.Equal($"AnimFaceArchetypeHappy [{game.Happy}]", r0[0].GetProperty("Emotion").GetString());
        Assert.Equal("50", r0[0].GetProperty("EmotionOut").GetString());
        Assert.Equal($"FD_Face [{game.Face}]", r0[0].GetProperty("Animation.FaceArchetype").GetString());
        Assert.Equal(2, r0[1].GetProperty("number").GetInt32());
        Assert.Equal("FdMod.esm", recs[1].GetProperty("winner").GetString());
        Assert.Equal("Made-up bye, changed.", recs[1].GetProperty("responses")[0].GetProperty("text").GetString());

        // without --full: no lines block and no infos key
        (code, stdout, _) = game.Find("FD_Topic");
        Assert.Equal(0, code);
        Assert.DoesNotContain("  lines:", stdout);
        (code, stdout, _) = game.Find("FD_Topic", "--json");
        using var plain = JsonDocument.Parse(stdout);
        Assert.False(plain.RootElement.GetProperty("matches")[0].TryGetProperty("infos", out _));
    }

    [Fact]
    public void Full_on_an_info_shows_each_responses_emotion_and_face_fields_by_name()
    {
        using var game = new Game();
        var (code, stdout, stderr) = game.Find("FD_InfoA", "--full");
        Assert.True(code == 0, stdout + stderr);
        var text = Norm(stdout);
        Assert.Contains($"FD_InfoA [{game.InfoA}] INFO", text);
        Assert.Contains("    Responses: (2 entries)\n      [0]:\n", text);
        Assert.Contains($"        Emotion: AnimFaceArchetypeHappy [{game.Happy}]\n", text);
        Assert.Contains("        EmotionOut: 50\n", text);
        Assert.Contains("        ResponseText: \"Made-up hello.\"\n", text);
        Assert.Contains("        Animation:\n", text);
        Assert.Contains($"          FaceArchetype: FD_Face [{game.Face}]\n", text);
        Assert.Contains("          NpcAnimation: FD_Idle [", text);
        Assert.Contains("          HasAnimationFaceArchetype: False\n", text);
        Assert.DoesNotContain("  lines:", text);   // an INFO has no lines block of its own

        (code, stdout, stderr) = game.Find("FD_InfoA", "--full", "--json");
        Assert.True(code == 0, stdout + stderr);
        using var doc = JsonDocument.Parse(stdout);
        var match = doc.RootElement.GetProperty("matches")[0];
        var resp = match.GetProperty("tree").GetProperty("Responses")[0];
        Assert.Equal($"AnimFaceArchetypeHappy [{game.Happy}]", resp.GetProperty("Emotion").GetString());
        Assert.Equal("50", resp.GetProperty("EmotionOut").GetString());
        Assert.Equal($"FD_Face [{game.Face}]", resp.GetProperty("Animation").GetProperty("FaceArchetype").GetString());
        Assert.False(match.TryGetProperty("infos", out _));
    }

    [Fact]
    public void Full_with_plugin_lists_the_topics_lines_in_the_plugin_and_its_masters()
    {
        using var game = new Game();
        var (code, stdout, stderr) = game.FindPlugin($"{game.Topic}", "--full");
        Assert.True(code == 0, stdout + stderr);
        var text = Norm(stdout);
        Assert.Contains("  lines: 2 INFOs (winning copies, in FdMod.esm and its masters)\n", text);
        Assert.Contains($"    FD_InfoB [{game.InfoB}], winner FdMod.esm\n", text);
        Assert.Contains("      1: \"Made-up bye, changed.\"\n", text);

        (code, stdout, stderr) = game.FindPlugin("FD_InfoB", "--full", "--json");
        Assert.True(code == 0, stdout + stderr);
        using (var doc = JsonDocument.Parse(stdout))
        {
            var resp = doc.RootElement.GetProperty("matches")[0].GetProperty("tree").GetProperty("Responses")[0];
            Assert.Equal("\"Made-up bye, changed.\"", resp.GetProperty("ResponseText").GetString());
        }

        // the listing with --full: the topic gets its lines block too
        (code, stdout, stderr) = ExeTests.RunSplit(["find", "--plugin", Path.Combine(game.Data, "FdMod.esm"), "--data", game.Data, "--full", "--type", "DIAL", "--json"]);
        Assert.True(code == 0, stdout + stderr);
        using (var doc = JsonDocument.Parse(stdout))
        {
            var infos = doc.RootElement.GetProperty("matches")[0].GetProperty("infos");
            Assert.Equal(2, infos.GetProperty("total").GetInt32());
        }
    }

    [Fact]
    public void Lines_block_cap_counts_the_infos_it_leaves_out()
    {
        var mod = Plugin.New("FdCap.esm", PluginStyle.Full, "test", "made-up");
        var quest = mod.Quests.AddNew("FC_Quest");
        var topic = new DialogTopic(mod) { EditorID = "FC_Topic", Quest = quest.ToLink<IQuestGetter>() };
        for (int i = 0; i < 5; i++)
        {
            var info = new DialogResponses(mod) { EditorID = $"FC_Info{i}" };
            info.Responses.Add(new DialogResponse { ResponseText = $"Made-up line {i}." });
            topic.Responses.Add(info);
        }
        var old = TopicInfos.MaxListed;
        TopicInfos.MaxListed = 3;
        try
        {
            var plugin = new SfModCheck.Conflict.PluginInfo(0, "FdCap.esm", false, [], null!);
            var infos = TopicInfos.Collect([(plugin, topic)], l => $"[{l.FormKey}]", null);
            Assert.Equal(5, infos.Total);
            var lines = TopicInfos.Lines(infos, 100);
            Assert.Contains("  lines: 5 INFOs (winning copies)", lines);
            Assert.Contains("    ... and 2 more INFOs not shown (--json --full lists up to 100)", lines);
            Assert.Equal(3, lines.Count(l => l.StartsWith("    FC_Info", StringComparison.Ordinal)));
            var json = TopicInfos.Json(infos, 4);
            Assert.Equal(4, json["records"]!.AsArray().Count);
            Assert.Equal(1, (int)json["notShown"]!);
        }
        finally { TopicInfos.MaxListed = old; }
    }

    [Fact]
    public void A_line_its_winner_deletes_is_marked_deleted_in_text_and_json()
    {
        // Code review round 1: a deleted INFO was listed like a live line.
        var mk = ModKey.FromFileName("FdBase.esm");
        var t = new TopicLines();
        t.Records.Add(new TopicLine(new FormKey(mk, 0x801), "FD_Live", "FdBase.esm", [new TopicResponse(1, "Made-up hello.", new())], null));
        t.Records.Add(new TopicLine(new FormKey(mk, 0x802), null, "FdMod.esm", [], null, Deleted: true));
        var text = string.Join("\n", TopicInfos.Lines(t));
        Assert.Contains("FD_Live [000801:FdBase.esm], winner FdBase.esm\n", text + "\n");
        Assert.Contains("(no EditorID) [000802:FdBase.esm], winner FdMod.esm (DELETED by its winner: the game doesn't use this line)", text);
        var recs = JsonDocument.Parse(TopicInfos.Json(t).ToJsonString()).RootElement.GetProperty("records");
        Assert.False(recs[0].TryGetProperty("deleted", out _));
        Assert.True(recs[1].GetProperty("deleted").GetBoolean());
    }
}
