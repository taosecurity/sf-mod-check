// 0.10.0 item 7 (plan step 8b, second part): xEdit field priorities in `conflict`, on by default, --no-priorities
// for 0.9.1's output. Made-up load orders, as in ConflictTests.
using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Starfield;
using SfModCheck.Conflict;
using Xunit;

namespace SfModCheck.Tests;

public partial class ConflictTests
{
    // ModA renames the misc item (EditorID: benign in xEdit) and changes its value; ModB, built without ModA, changes
    // its weight, so both of ModA's changes are lost to ModB.
    static void RenamedAndRevalued(Game game)
    {
        game.Mod("ModA.esm", (m, open) => { var x = Misc(m, open("Base.esm")); x.EditorID = "A_Misc"; x.Value = 10; });
        game.Mod("ModB.esm", (m, open) => Misc(m, open("Base.esm")).Weight = 2);
    }

    static string Page(Game game, string file) => File.ReadAllText(Path.Combine(game.Dir, "report", file));

    [Fact]
    public void A_lost_editor_id_is_shown_apart_as_benign()
    {
        using var game = new Game();
        RenamedAndRevalued(game);
        var (code, stdout, json) = game.Conflict(new[] { "Base.esm", "ModA.esm", "ModB.esm" });
        Assert.True(code == 0, stdout);
        var lost = Assert.Single(Lost(json));
        Assert.Equal(new[] { "Value" }, Fields(lost));
        Assert.Equal("normal", S(lost, "priority"));
        var benign = Assert.Single(json.GetProperty("benign").EnumerateArray());
        Assert.Equal(("ModA.esm", "ModB.esm"), (S(benign, "Loser"), S(benign, "Winner")));
        var bf = Assert.Single(benign.GetProperty("Fields").EnumerateArray());
        Assert.Equal(("EditorID", "benign"), (S(bf, "Field"), S(bf, "priority")));
        Assert.Equal(1, json.GetProperty("fieldPriorities").GetProperty("benignEntries").GetInt32());
        Assert.StartsWith("RESULT: OK, 1 lost-change entries (0 official, 1 mod vs mod), 1 benign entry shown apart, 0 ITMs", ExeTests.ResultLine(stdout));
        var md = Page(game, "report.md");
        Assert.Contains("## Benign differences (not counted as lost)", md);
        Assert.Contains("| Benign differences shown apart (xEdit field priorities) | 1 field in 1 record |", md);
        Assert.Contains("| EditorID | benign | A\\_Misc | B\\_Misc |", md);
        Assert.Contains("- Field priorities come from xEdit", md);
        var html = Page(game, "report.html");
        Assert.Contains("<h2 id=\"benign\">", html);
        Assert.Contains("href=\"#benign\"", html);
    }

    [Fact]
    public void No_priorities_counts_every_differing_field_as_in_0_9_1()
    {
        using var game = new Game();
        RenamedAndRevalued(game);
        var (code, stdout, json) = game.Conflict(new[] { "Base.esm", "ModA.esm", "ModB.esm" }, "--no-priorities");
        Assert.True(code == 0, stdout);
        var lost = Assert.Single(Lost(json));
        Assert.Equal(new[] { "EditorID", "Value" }, Fields(lost).Order());
        Assert.False(lost.TryGetProperty("priority", out _));
        Assert.False(json.TryGetProperty("benign", out _));
        Assert.False(json.TryGetProperty("fieldPriorities", out _));
        Assert.StartsWith("RESULT: OK, 1 lost-change entries (0 official, 1 mod vs mod), 0 ITMs", ExeTests.ResultLine(stdout));
        foreach (var page in new[] { Page(game, "report.md"), Page(game, "report.html") })
        {
            Assert.DoesNotContain("Benign differences", page);
            Assert.DoesNotContain("Field priorities come from xEdit", page);
            Assert.DoesNotContain("Critical", page);
        }
    }

    [Fact]
    public void A_change_with_only_benign_fields_is_not_a_lost_change()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, open) => Misc(m, open("Base.esm")).EditorID = "A_Misc");
        game.Mod("ModB.esm", (m, open) => Misc(m, open("Base.esm")).Weight = 2);
        var (code, stdout, json) = game.Conflict(new[] { "Base.esm", "ModA.esm", "ModB.esm" });
        Assert.True(code == 0, stdout);
        Assert.Empty(Lost(json));
        Assert.Single(json.GetProperty("benign").EnumerateArray());
        Assert.StartsWith("RESULT: OK, 0 lost-change entries (0 official, 0 mod vs mod), 1 benign entry shown apart, 0 ITMs", ExeTests.ResultLine(stdout));
        Assert.Contains("Nothing needs doing.", Page(game, "report.md"));   // nothing to patch: the only difference is benign
    }

    [Fact]
    public void A_lost_game_setting_name_is_critical_and_listed_first()
    {
        using var game = new Game();
        // ModG adds a setting; ModR (on ModG) renames it; ModV (on ModG, not ModR) changes its value, so the new name is lost.
        game.Mod("ModG.esm", (m, _) => m.GameSettings.Add(new GameSettingFloat(m) { EditorID = "G_Setting", Data = 1f }));
        game.Mod("ModR.esm", (m, open) => m.GameSettings.GetOrAddAsOverride(open("ModG.esm").GameSettings.First()).EditorID = "R_Setting");
        game.Mod("ModV.esm", (m, open) => ((GameSettingFloat)m.GameSettings.GetOrAddAsOverride(open("ModG.esm").GameSettings.First())).Data = 2f);
        // ModA/ModB: an ordinary lost change in the same pair order, so "first" means something
        game.Mod("ModA.esm", (m, open) => Misc(m, open("Base.esm")).Value = 10);
        game.Mod("ModB.esm", (m, open) => Misc(m, open("Base.esm")).Weight = 2);
        var log = Path.Combine(game.Dir, "debug.log");
        var (code, stdout, json) = game.Conflict(new[] { "Base.esm", "ModG.esm", "ModR.esm", "ModV.esm", "ModA.esm", "ModB.esm" }, "--log", log);
        Assert.True(code == 0, stdout);
        var lost = Lost(json).ToList();
        Assert.Equal(2, lost.Count);
        Assert.Equal(("ModR.esm", "ModV.esm", "critical"), (S(lost[0], "Loser"), S(lost[0], "Winner"), S(lost[0], "priority")));
        Assert.Equal("critical", S(lost[0].GetProperty("Fields").EnumerateArray().Single(), "priority"));
        Assert.Equal("normal", S(lost[1], "priority"));
        Assert.Equal(1, json.GetProperty("fieldPriorities").GetProperty("criticalEntries").GetInt32());
        Assert.Contains("(1 critical lost-change entry), ", ExeTests.ResultLine(stdout));
        Assert.Contains("| EditorID | Forwardable | " + SfModCheck.Conflict.Report.CriticalNote, Page(game, "report.md"));
        Assert.Contains("<span class=\"badge p1\">Critical</span>", Page(game, "report.html"));
        Assert.Contains("field priorities: critical: GameSettingFloat ", File.ReadAllText(log));
        Assert.Contains("ModV.esm over ModR.esm: EditorID", File.ReadAllText(log));
    }

    [Fact]
    public void Patch_is_not_changed_by_field_priorities()
    {
        // patch never applies the priorities: the benign EditorID is still restored with the value
        using var game = new Game();
        RenamedAndRevalued(game);
        var txt = Path.Combine(game.Dir, "Plugins.txt");
        File.WriteAllLines(txt, new[] { "*Base.esm", "*ModA.esm", "*ModB.esm" });
        var outDir = Path.Combine(game.Dir, "patch");
        var (code, stdout, _) = ExeTests.RunSplit(new[] { "patch", "--data", game.Data, "--plugins", txt, "--out", outDir, "--quiet" });
        Assert.True(code == 0, stdout);
        using var g = new BuildKit.GameData(game.Data, [outDir]);
        var misc = g.Open("ConflictPatch.esm").MiscItems.Single();
        Assert.Equal(("A_Misc", 10, 2f), (misc.EditorID, misc.Value, misc.Weight));
    }

    [Fact]
    public void The_benign_and_critical_counts_are_empty_without_priorities()
    {
        var a = new Analysis();
        Assert.Equal("", ConflictCommand.BenignPart(a));
        a.PrioritiesOn = true;
        Assert.Equal("0 benign entries shown apart, ", ConflictCommand.BenignPart(a));
    }
}
