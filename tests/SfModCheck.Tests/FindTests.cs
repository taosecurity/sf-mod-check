// sf-mod-check find (0.16.0) on a made-up load order: a full base plugin, a medium and a light mod that override its
// misc item, and an extra folder. Each test runs the built program. None of this exists in 0.15.2 (no find command).
using System.Text.Json;
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Starfield;
using Xunit;

namespace SfModCheck.Tests;

public class FindTests
{
    static readonly WriteOptions NoGap = new() { GapCheck = false, FixEffects = false };

    /// <summary>FindBase.esm (full): misc item FB_Misc "Made-up Widget" (value 5) and keyword FB_Keyword.
    /// FindMedium.esm (medium) overrides FB_Misc (value 7) and adds the keyword Dup; FindLight.esm (light) overrides FB_Misc
    /// (value 9) and adds its own keyword Dup, a second record with that EditorID. Plugins.txt lists all three.</summary>
    sealed class Game : Sandbox
    {
        public readonly string Data, PluginsTxt;
        public readonly FormKey BaseMisc, BaseKeyword, MediumDup, LightDup;

        public Game()
        {
            Data = Sub("Data");
            var b = Plugin.New("FindBase.esm", PluginStyle.Full, "test", "made-up base");
            var misc = b.MiscItems.AddNew("FB_Misc");
            misc.Name = "Made-up Widget";
            misc.Value = 5;
            BaseMisc = misc.FormKey;
            BaseKeyword = b.Keywords.AddNew("FB_Keyword").FormKey;
            using (var g = new GameData(Data)) PluginWriter.Write(b, Data, g, NoGap);
            MediumDup = Mod("FindMedium.esm", PluginStyle.Medium, 7);
            LightDup = Mod("FindLight.esm", PluginStyle.Light, 9);
            PluginsTxt = Path.Combine(Dir, "Plugins.txt");
            File.WriteAllLines(PluginsTxt, ["*FindBase.esm", "*FindMedium.esm", "*FindLight.esm"]);
        }

        /// <summary>A mod on FindBase.esm that sets FB_Misc's value and adds a keyword with the EditorID Dup.</summary>
        FormKey Mod(string name, PluginStyle style, int value, string? dir = null)
        {
            dir ??= Data;
            var mod = Plugin.New(name, style, "test", "made-up mod");
            using var g = new GameData(Data);
            var baseMod = g.Open("FindBase.esm");
            mod.MiscItems.GetOrAddAsOverride(baseMod.MiscItems.First()).Value = value;
            var dup = mod.Keywords.AddNew("Dup");
            PluginWriter.Write(mod, dir, g, NoGap);
            return dup.FormKey;
        }

        public FormKey ExtraMod(string dir) => Mod("FindExtra.esm", PluginStyle.Light, 11, dir);

        public (int Code, string Stdout, string Stderr) Find(params string[] args) =>
            ExeTests.RunSplit(new[] { "find" }.Concat(args).Concat(["--data", Data, "--plugins", PluginsTxt]).ToArray());
    }

    [Fact]
    public void An_EditorID_finds_the_record_its_definer_its_winner_and_the_other_copies()
    {
        using var game = new Game();
        // The CHANGELOG and README example is a real run on these made-up files: set SFMODCHECK_FIND_EXAMPLE to a
        // folder to keep a copy of them (Data\ and Plugins.txt).
        if (Environment.GetEnvironmentVariable("SFMODCHECK_FIND_EXAMPLE") is { Length: > 0 } keep)
        {
            Directory.CreateDirectory(Path.Combine(keep, "Data"));
            foreach (var f in Directory.EnumerateFiles(game.Data)) File.Copy(f, Path.Combine(keep, "Data", Path.GetFileName(f)), true);
            File.Copy(game.PluginsTxt, Path.Combine(keep, "Plugins.txt"), true);
        }
        var (code, stdout, _) = game.Find("fb_misc");   // letter case ignored
        Assert.Equal(0, code);
        Assert.Contains($"FB_Misc [{game.BaseMisc}] MISC \"Made-up Widget\"", stdout);
        Assert.Contains("  defined in: FindBase.esm", stdout);
        Assert.Contains("  winner: FindLight.esm", stdout);
        Assert.Contains("  also in: FindMedium.esm", stdout);
        Assert.Contains("    Value: 9", stdout);   // the winning copy's fields
        Assert.Contains("RESULT: FOUND 1 record (", stdout);
    }

    [Fact]
    public void A_FormKey_finds_the_same_record()
    {
        using var game = new Game();
        var (code, stdout, _) = game.Find(game.BaseMisc.ToString());
        Assert.Equal(0, code);
        Assert.Contains($"FB_Misc [{game.BaseMisc}] MISC", stdout);
        Assert.Contains("  winner: FindLight.esm", stdout);
    }

    [Fact]
    public void Console_FormIDs_pick_full_medium_and_light_plugins_by_load_order_index()
    {
        using var game = new Game();
        // FindBase.esm is full plugin 00; FindMedium.esm is medium plugin FD 00; FindLight.esm is light plugin FE 000.
        var (code, stdout, _) = game.Find($"00{game.BaseMisc.ID:X6}");
        Assert.Equal(0, code);
        Assert.Contains($"FB_Misc [{game.BaseMisc}]", stdout);
        (code, stdout, _) = game.Find($"0xFD00{game.MediumDup.ID:X4}");
        Assert.Equal(0, code);
        Assert.Contains($"Dup [{game.MediumDup}] KYWD", stdout);
        (code, stdout, _) = game.Find($"FE000{game.LightDup.ID:X3}");
        Assert.Equal(0, code);
        Assert.Contains($"Dup [{game.LightDup}] KYWD", stdout);
        (code, stdout, _) = game.Find($"FE001{game.LightDup.ID:X3}");   // there is no second light plugin
        Assert.Equal(1, code);
        Assert.Contains("this load order has 1 light plugins, so none has index 1", stdout);
    }

    // code review round 2 (0.16.0): the game loads a .esl file as light whatever its header flags say
    [Fact]
    public void A_esl_file_without_the_light_flag_counts_as_a_light_plugin_for_console_FormIDs()
    {
        using var game = new Game();
        var esl = Plugin.New("FindEsl.esm", PluginStyle.Full, "test", "made-up mod");
        esl.Keywords.AddNew("EslKeyword");
        using (var g = new GameData(game.Data)) PluginWriter.Write(esl, game.Data, g, NoGap);
        File.Move(Path.Combine(game.Data, "FindEsl.esm"), Path.Combine(game.Data, "FindEsl.esl"));
        File.WriteAllLines(game.PluginsTxt, ["*FindBase.esm", "*FindMedium.esm", "*FindEsl.esl", "*FindLight.esm"]);
        // FindEsl.esl is light plugin FE 000, so FindLight.esm is FE 001
        var (code, stdout, _) = game.Find($"FE001{game.LightDup.ID:X3}");
        Assert.Equal(0, code);
        Assert.Contains($"Dup [{game.LightDup}] KYWD", stdout);
    }

    [Fact]
    public void Type_keeps_only_records_of_that_type()
    {
        using var game = new Game();
        Assert.Equal(0, game.Find("FB_Misc", "--type", "misc").Code);
        var (code, stdout, _) = game.Find("FB_Misc", "--type", "KYWD");
        Assert.Equal(1, code);
        Assert.Contains("RESULT: NOT FOUND (no record with EditorID FB_Misc of type KYWD in 3 plugins)", stdout);
        var (bad, _, err) = game.Find("FB_Misc", "--type", "ABCD");
        Assert.Equal(2, bad);
        Assert.Contains("--type needs a Starfield record type", err);
    }

    [Fact]
    public void Not_found_is_exit_1_with_a_hint()
    {
        using var game = new Game();
        var (code, stdout, _) = game.Find("FB_Misk");
        Assert.Equal(1, code);
        Assert.Contains("HINT: EditorIDs must match exactly", stdout);
        Assert.Contains("give its Plugins.txt with --plugins", stdout);
        Assert.Contains("RESULT: NOT FOUND (no record with EditorID FB_Misk in 3 plugins)", stdout);
        (code, stdout, _) = game.Find("000800:NotLoaded.esm");
        Assert.Equal(1, code);
        Assert.Contains("NotLoaded.esm isn't in this load order", stdout);
    }

    [Fact]
    public void Two_records_with_one_EditorID_are_both_printed()
    {
        using var game = new Game();
        var (code, stdout, _) = game.Find("Dup");
        Assert.Equal(0, code);
        Assert.Contains($"Dup [{game.MediumDup}] KYWD", stdout);
        Assert.Contains($"Dup [{game.LightDup}] KYWD", stdout);
        Assert.Contains("RESULT: FOUND 2 records", stdout);
    }

    [Fact]
    public void Json_prints_one_object_with_every_copy()
    {
        using var game = new Game();
        var (code, stdout, _) = game.Find("FB_Misc", "--json");
        Assert.Equal(0, code);
        var j = JsonDocument.Parse(stdout).RootElement;   // nothing else on stdout
        Assert.Equal("found", j.GetProperty("result").GetString());
        Assert.Equal(3, j.GetProperty("loadOrder").GetProperty("plugins").GetInt32());
        var m = Assert.Single(j.GetProperty("matches").EnumerateArray());
        Assert.Equal(game.BaseMisc.ToString(), m.GetProperty("formKey").GetString());
        Assert.Equal("MISC", m.GetProperty("type").GetString());
        Assert.Equal("Made-up Widget", m.GetProperty("name").GetString());
        Assert.Equal("FindBase.esm", m.GetProperty("definedIn").GetString());
        Assert.Equal("FindLight.esm", m.GetProperty("winner").GetString());
        Assert.Equal(["FindBase.esm", "FindMedium.esm", "FindLight.esm"], m.GetProperty("plugins").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal("9", m.GetProperty("fields").GetProperty("Value").GetString());

        (code, stdout, _) = game.Find("Nothing_Here", "--json");
        Assert.Equal(1, code);
        j = JsonDocument.Parse(stdout).RootElement;
        Assert.Equal("not found", j.GetProperty("result").GetString());
        Assert.Empty(j.GetProperty("matches").EnumerateArray());
        Assert.Contains("EditorIDs must match exactly", j.GetProperty("hint").GetString());
    }

    [Fact]
    public void A_plugin_in_the_extra_data_folder_wins_over_Data()
    {
        using var game = new Game();
        var extra = game.Sub("extra");
        game.ExtraMod(extra);
        File.AppendAllLines(game.PluginsTxt, ["*FindExtra.esm"]);
        var (code, stdout, _) = game.Find("FB_Misc", "--extra-data", extra);
        Assert.Equal(0, code);
        Assert.Contains("  winner: FindExtra.esm", stdout);
        Assert.Contains("  also in: FindMedium.esm, FindLight.esm", stdout);
        Assert.Contains("    Value: 11", stdout);
    }

    [Fact]
    public void Bad_input_is_exit_2_and_help_is_exit_0()
    {
        using var game = new Game();
        var (code, stdout, _) = ExeTests.RunSplit("find", "--help");
        Assert.Equal(0, code);
        Assert.StartsWith("usage: sf-mod-check find", stdout);
        (code, stdout, _) = ExeTests.RunSplit("find", "FB_Misc", "--data", game.Data, "--plugins", Path.Combine(game.Dir, "missing.txt"));
        Assert.Equal(2, code);
        Assert.Contains("RESULT: CANNOT CHECK (the Plugins.txt", stdout);
        (code, stdout, _) = ExeTests.RunSplit("find", "--data", game.Data, "--plugins", game.PluginsTxt);
        Assert.Equal(2, code);
        Assert.Contains("RESULT: CANNOT CHECK (no record given)", stdout);
        (code, stdout, _) = ExeTests.RunSplit("find", "FB_Misc", "--json", "--data", Path.Combine(game.Dir, "nope"), "--plugins", game.PluginsTxt);
        Assert.Equal(2, code);
        Assert.Equal("cannot run", JsonDocument.Parse(stdout).RootElement.GetProperty("result").GetString());
    }
}
