// sf-mod-check find 0.16.1 on made-up plugins outside Data: --plugin with --masters-dir, a light master's link in a
// condition, the whole-plugin listing with --max and --type, the refused option mixes, and MO2's My Games Data folder.
// Each fails on 0.16.0: --plugin, --masters-dir and --max are unknown options there, and a list's entries (conditions)
// print only as a count.
using System.Text.Json;
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Starfield;
using Xunit;

namespace SfModCheck.Tests;

public class FindPluginTests
{
    static readonly WriteOptions NoGap = new() { GapCheck = false, FixEffects = false };

    /// <summary>masters\ holds BaseQ.esm (full: magic effect BQ_Effect, global BQ_Global) and LightM.esm (light: actor
    /// value LM_Value). patch\ holds Patch.esm (full, masters BaseQ.esm and LightM.esm): it overrides BQ_Effect with two
    /// conditions, GetValue(LM_Value) == 0 OR'd with GetGlobalValue(BQ_Global) == 1, and adds keywords P_One, P_Two and
    /// P_Three. Data\ is an empty game folder, so nothing comes from a real game.</summary>
    sealed class Set : Sandbox
    {
        public readonly string Masters, PatchDir, Patch, Data;
        public readonly FormKey Effect, LightValue, Global;

        public Set()
        {
            Masters = Sub("masters");
            PatchDir = Sub("patch");
            Data = Sub("Data");
            var b = Plugin.New("BaseQ.esm", PluginStyle.Full, "test", "made-up base");
            Effect = b.MagicEffects.AddNew("BQ_Effect").FormKey;
            Global = b.Globals.AddNew("BQ_Global").FormKey;
            using (var g = new GameData(Masters)) PluginWriter.Write(b, Masters, g, NoGap);
            var l = Plugin.New("LightM.esm", PluginStyle.Light, "test", "made-up light master");
            LightValue = l.ActorValueInformation.AddNew("LM_Value").FormKey;
            using (var g = new GameData(Masters)) PluginWriter.Write(l, Masters, g, NoGap);

            var p = Plugin.New("Patch.esm", PluginStyle.Full, "test", "made-up patch");
            using (var g = new GameData(Masters))
            {
                var baseMod = g.Open("BaseQ.esm");
                g.Open("LightM.esm");
                var mgef = p.MagicEffects.GetOrAddAsOverride(baseMod.MagicEffects.First());
                var getValue = new GetValueConditionData();
                getValue.FirstParameter.SetTo(LightValue);
                mgef.Conditions.Add(new ConditionFloat { CompareOperator = CompareOperator.EqualTo, Flags = Condition.Flag.OR, Data = getValue, ComparisonValue = 0 });
                var getGlobal = new GetGlobalValueConditionData();
                getGlobal.FirstParameter.Link.SetTo(Global);
                mgef.Conditions.Add(new ConditionFloat { CompareOperator = CompareOperator.EqualTo, Data = getGlobal, ComparisonValue = 1 });
                p.Keywords.AddNew("P_One");
                p.Keywords.AddNew("P_Two");
                p.Keywords.AddNew("P_Three");
                PluginWriter.Write(p, PatchDir, g, NoGap);
            }
            Patch = Path.Combine(PatchDir, "Patch.esm");
        }

        public (int Code, string Stdout, string Stderr) Find(params string[] args) =>
            ExeTests.RunSplit(new[] { "find" }.Concat(args).Concat(["--data", Data]).ToArray());
    }

    [Fact]
    public void A_light_masters_link_in_a_condition_prints_with_its_EditorID_through_masters_dir()
    {
        using var s = new Set();
        // The CHANGELOG and README example is a real run on these made-up files: set SFMODCHECK_FIND_PLUGIN_EXAMPLE
        // to a folder to keep a copy of them (masters\, patch\ and an empty Data\).
        if (Environment.GetEnvironmentVariable("SFMODCHECK_FIND_PLUGIN_EXAMPLE") is { Length: > 0 } keep)
            foreach (var sub in new[] { "masters", "patch", "Data" })
            {
                Directory.CreateDirectory(Path.Combine(keep, sub));
                foreach (var f in Directory.EnumerateFiles(Path.Combine(s.Dir, sub))) File.Copy(f, Path.Combine(keep, sub, Path.GetFileName(f)), true);
            }
        var (code, stdout, stderr) = s.Find("BQ_Effect", "--plugin", s.Patch, "--masters-dir", s.Masters);
        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("in 3 plugins (Patch.esm and its 2 masters)", stdout);
        Assert.Contains("  winner: Patch.esm", stdout);
        Assert.Contains("    Conditions: (2 entries)", stdout);
        Assert.Contains($"      - OR GetValue(LM_Value [{s.LightValue}]) == 0 (run on Subject)", stdout);
        Assert.Contains($"      - GetGlobalValue(BQ_Global [{s.Global}]) == 1 (run on Subject)", stdout);
        Assert.Contains("LightM.esm", s.LightValue.ToString());   // the link names the light master itself

        // the same entries in JSON, beside the unchanged fields
        (code, stdout, _) = s.Find(s.Effect.ToString(), "--plugin", s.Patch, "--masters-dir", s.Masters, "--json");
        Assert.Equal(0, code);
        var m = Assert.Single(JsonDocument.Parse(stdout).RootElement.GetProperty("matches").EnumerateArray());
        Assert.Equal("(2 entries)", m.GetProperty("fields").GetProperty("Conditions").GetString());
        Assert.Equal($"GetGlobalValue(BQ_Global [{s.Global}]) == 1 (run on Subject)",
            m.GetProperty("entries").GetProperty("Conditions")[1].GetString());
    }

    [Fact]
    public void Plugin_with_no_record_lists_every_record_up_to_max_with_a_complete_summary()
    {
        using var s = new Set();
        var (code, stdout, stderr) = s.Find("--plugin", s.Patch, "--masters-dir", s.Masters, "--max", "2");
        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("find: every record in Patch.esm (load order: Patch.esm and its 2 masters)", stdout);
        Assert.Contains("... and 2 more records (--max 0 lists them all, or --type narrows the list)", stdout);
        Assert.Contains("Patch.esm holds 4 records: 3 new, 1 override; by type: KYWD 3, MGEF 1", stdout);
        Assert.Contains("RESULT: FOUND 4 records in Patch.esm, 2 listed (", stdout);

        (code, stdout, _) = s.Find("--plugin", s.Patch, "--masters-dir", s.Masters, "--type", "MGEF");
        Assert.Equal(0, code);
        Assert.Contains($"BQ_Effect [{s.Effect}] MGEF", stdout);
        Assert.DoesNotContain("P_One", stdout);
        Assert.Contains("RESULT: FOUND 1 record of type MGEF in Patch.esm (", stdout);

        (code, stdout, _) = s.Find("--plugin", s.Patch, "--masters-dir", s.Masters, "--type", "NPC_");
        Assert.Equal(1, code);
        Assert.Contains("RESULT: NOT FOUND (no records of type NPC_ in Patch.esm)", stdout);

        (code, stdout, _) = s.Find("--plugin", s.Patch, "--masters-dir", s.Masters, "--max", "0", "--json");
        Assert.Equal(0, code);
        var j = JsonDocument.Parse(stdout).RootElement;
        Assert.Equal("Patch.esm", j.GetProperty("plugin").GetString());
        Assert.Equal(4, j.GetProperty("total").GetInt32());
        Assert.Equal(4, j.GetProperty("listed").GetInt32());
        Assert.Equal(3, j.GetProperty("byType").GetProperty("KYWD").GetInt32());
        Assert.Equal(4, j.GetProperty("matches").GetArrayLength());
    }

    [Fact]
    public void Option_mixes_that_would_ignore_an_option_and_a_missing_master_stop_with_exit_2()
    {
        using var s = new Set();
        var txt = Path.Combine(s.Dir, "Plugins.txt");
        File.WriteAllLines(txt, ["*BaseQ.esm"]);
        var (code, stdout, stderr) = s.Find("BQ_Effect", "--plugin", s.Patch, "--plugins", txt);
        Assert.Equal(2, code);
        Assert.Contains("RESULT: CANNOT CHECK (--plugin with --plugins or --mo2: give one load order)", stdout);
        (code, stdout, stderr) = s.Find("BQ_Effect", "--plugin", s.Patch, "--extra-data", s.Masters);
        Assert.Equal(2, code);
        Assert.Contains("use --masters-dir", stderr);
        (code, stdout, stderr) = s.Find("BQ_Effect", "--masters-dir", s.Masters, "--plugins", txt);
        Assert.Equal(2, code);
        Assert.Contains("RESULT: CANNOT CHECK (--masters-dir without --plugin)", stdout);
        (code, stdout, stderr) = s.Find("BQ_Effect", "--plugin", s.Patch, "--masters-dir", s.Masters, "--max", "5");
        Assert.Equal(2, code);
        Assert.Contains("--max limits the whole-plugin listing", stderr);
        (code, stdout, stderr) = s.Find("--plugin", s.Patch, "--masters-dir", s.Masters, "--max", "many");
        Assert.Equal(2, code);
        Assert.Contains("--max needs a whole number", stderr);
        // without --masters-dir the masters are looked for in the plugin's folder and Data only
        (code, stdout, stderr) = s.Find("--plugin", s.Patch);
        Assert.Equal(2, code);
        Assert.Contains("can't find Patch.esm's master BaseQ.esm", stderr);
        Assert.Contains("give its folder with --masters-dir", stderr);
        // a folder given as --plugin says so (bug hunt 0.16.1), not "that file doesn't exist"
        (code, stdout, stderr) = s.Find("--plugin", s.Masters);
        Assert.Equal(2, code);
        Assert.Contains("that is a folder. Give the plugin file itself", stderr);
        // conflict doesn't take --plugin
        (code, _, stderr) = ExeTests.RunSplit("conflict", "--plugin", s.Patch, "--data", s.Data);
        Assert.Equal(2, code);
        Assert.Contains("unknown option --plugin", stderr);
    }

    // code review round 1 (0.16.1): a plugin whose records can't be read at all is a failed run (exit 2), not
    // "NOT FOUND (no records)" with exit 1 as if it were empty
    [Fact]
    public void A_plugin_whose_records_cannot_be_read_stops_with_exit_2_not_not_found()
    {
        using var s = new Set();
        var bytes = File.ReadAllBytes(s.Patch);
        // TES4 header: 24 bytes plus its data size (offset 4). The first GRUP starts right after it; its first
        // record's signature sits 24 bytes into the GRUP. Overwriting that record's size makes it run past the file.
        int first = 24 + BitConverter.ToInt32(bytes, 4) + 24;
        Assert.Equal("GRUP", System.Text.Encoding.ASCII.GetString(bytes, first - 24, 4));
        BitConverter.GetBytes(0x7FFFFFF0).CopyTo(bytes, first + 4);
        var bad = Path.Combine(s.Sub("bad"), "Patch.esm");
        File.WriteAllBytes(bad, bytes);
        var (code, stdout, stderr) = s.Find("--plugin", bad, "--masters-dir", s.Masters);
        Assert.True(code == 2, stdout + stderr);
        Assert.DoesNotContain("NOT FOUND", stdout);
        Assert.Contains("CANNOT CHECK", stdout);
    }

    // code review round 2 (0.16.1): a record lookup in a --plugin that didn't load stops with exit 2, not an answer
    // from a master's copy; a console FormID with --plugin is refused, since only the plugin and its masters are loaded
    [Fact]
    public void A_lookup_in_a_plugin_that_did_not_load_and_a_console_FormID_with_plugin_stop_with_exit_2()
    {
        using var s = new Set();
        var bytes = File.ReadAllBytes(s.Patch);
        // the header stays readable; the first top-level GRUP's size (4 bytes into it) now runs far past the file
        int grup = 24 + BitConverter.ToInt32(bytes, 4);
        Assert.Equal("GRUP", System.Text.Encoding.ASCII.GetString(bytes, grup, 4));
        BitConverter.GetBytes(0x7FFFFFF0).CopyTo(bytes, grup + 4);
        var bad = Path.Combine(s.Sub("bad"), "Patch.esm");
        File.WriteAllBytes(bad, bytes);
        var (code, stdout, stderr) = s.Find("BQ_Effect", "--plugin", bad, "--masters-dir", s.Masters);
        Assert.True(code == 2, stdout + stderr);
        Assert.DoesNotContain("winner: BaseQ.esm", stdout);
        Assert.Contains("RESULT: CANNOT CHECK (Patch.esm can't be read: ", stdout);

        (code, stdout, stderr) = s.Find("01000800", "--plugin", s.Patch, "--masters-dir", s.Masters);
        Assert.Equal(2, code);
        Assert.Contains("is a console FormID, which depends on the game's whole load order", stderr);
        Assert.Contains("RESULT: CANNOT CHECK (a console FormID with --plugin)", stdout);
    }

    [Fact]
    public void An_MO2_profile_also_searches_the_My_Games_Data_folder_after_the_mods()
    {
        using var s = new Sandbox();
        var inst = s.Sub("Instance");
        var profile = s.Sub(Path.Combine("Instance", "profiles", "Main"));
        var modA = s.Sub(Path.Combine("Instance", "mods", "ModA"));
        var myGames = s.Sub(Path.Combine("Documents", "My Games", "Starfield", "Data"));
        File.WriteAllLines(Path.Combine(inst, "ModOrganizer.ini"), ["[General]", "selected_profile=@ByteArray(Main)"]);
        File.WriteAllLines(Path.Combine(profile, "plugins.txt"), ["*ModA.esm"]);
        File.WriteAllLines(Path.Combine(profile, "modlist.txt"), ["+ModA"]);
        var mo = Mo2Profile.Read(inst, null, out var error, myGames);
        Assert.True(mo != null, error);
        Assert.Equal([modA, myGames], mo!.ModDirs);
        mo = Mo2Profile.Read(inst, null, out error, Path.Combine(s.Dir, "no such folder"));
        Assert.Equal([modA], mo!.ModDirs);
    }
}
