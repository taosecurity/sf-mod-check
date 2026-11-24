// --mo2 and --profile (0.16.0) on a made-up MO2 instance: Base.esm in the game's Data folder, and every mod in its own
// folder under the instance's mods folder, as MO2 keeps them. Each test runs the built program (or reads the profile
// directly). None of this exists in 0.15.2: --mo2 and --profile are unknown options there (exit 2).
using System.Text.Json;
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Starfield;
using Mutagen.Bethesda.Starfield.Assets;
using Xunit;

namespace SfModCheck.Tests;

public class Mo2Tests
{
    static readonly WriteOptions NoGap = new() { GapCheck = false, FixEffects = false };

    /// <summary>The game folder Game\Data holds Base.esm (misc item B_Misc, value 5, weight 1). The MO2 instance
    /// Instance\ has ModOrganizer.ini (selected_profile Main, gamePath Game, mod_directory %BASE_DIR%/mymods) and the
    /// profile Main with modlist.txt (+ModB, a separator, -ModC, +ModA, top line first), plugins.txt and
    /// loadorder.txt. ModA.esm sets the value to 10 and uses a loose model in its own folder; ModB.esm sets the
    /// weight to 2, so ModA's value is lost to ModB; ModC.esm (in a disabled mod) sets the value to 99.</summary>
    sealed class Mo2 : Sandbox
    {
        public readonly string Data, Instance, Mods, Profile;

        public Mo2()
        {
            Data = Sub(Path.Combine("Game", "Data"));
            Instance = Sub("Instance");
            Mods = Sub(Path.Combine("Instance", "mymods"));
            Profile = Sub(Path.Combine("Instance", "profiles", "Main"));
            var b = Plugin.New("Base.esm", PluginStyle.Full, "test", "made-up base game");
            var misc = b.MiscItems.AddNew("B_Misc");
            misc.Value = 5;
            misc.Weight = 1;
            using (var g = new GameData(Data)) PluginWriter.Write(b, Data, g, NoGap);
            Mod("ModA", "ModA.esm", m =>
            {
                m.Value = 10;
                m.Model = new Model { File = new AssetLink<StarfieldModelAssetType>("Mo2Test\\Thing.nif") };
            });
            var loose = Path.Combine(Mods, "ModA", "meshes", "Mo2Test");
            Directory.CreateDirectory(loose);
            File.WriteAllBytes(Path.Combine(loose, "Thing.nif"), new byte[4]);
            Mod("ModB", "ModB.esm", m => m.Weight = 2);
            Mod("ModC", "ModC.esm", m => m.Value = 99);
            Ini("Main");
            File.WriteAllLines(Path.Combine(Profile, "modlist.txt"), ["# made-up", "+ModB", "-Stuff_separator", "-ModC", "+ModA", "*Unmanaged: Thing"]);
            File.WriteAllLines(Path.Combine(Profile, "plugins.txt"), ["# made-up", "*Base.esm", "*ModB.esm", "*ModA.esm", "*ModC.esm"]);
            File.WriteAllLines(Path.Combine(Profile, "loadorder.txt"), ["Base.esm", "ModA.esm", "ModB.esm", "ModC.esm"]);
        }

        /// <summary>Writes a light mod that overrides B_Misc into mymods\folder (or another folder).</summary>
        public void Mod(string folder, string name, Action<MiscItem> change, string? dir = null)
        {
            dir ??= Path.Combine(Mods, folder);
            Directory.CreateDirectory(dir);
            var mod = Plugin.New(name, PluginStyle.Light, "test", "made-up mod");
            using var g = new GameData(Data);
            change(mod.MiscItems.GetOrAddAsOverride(g.Open("Base.esm").MiscItems.First()));
            PluginWriter.Write(mod, dir, g, NoGap);
        }

        /// <summary>ModOrganizer.ini as MO2 writes it: @ByteArray values with doubled backslashes.</summary>
        public void Ini(string selected) => File.WriteAllLines(Path.Combine(Instance, "ModOrganizer.ini"),
        [
            "[General]",
            "gameName=Starfield",
            $"gamePath=@ByteArray({Path.Combine(Dir, "Game").Replace("\\", "\\\\")})",
            $"selected_profile=@ByteArray({selected})",
            "[Settings]",
            $"base_directory={Instance.Replace("\\", "/")}",
            "mod_directory=%BASE_DIR%/mymods",
        ]);

        public (int Code, string Stdout, string Stderr, JsonElement Json) Conflict(params string[] extra)
        {
            var outDir = Path.Combine(Dir, "report");
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            var (code, stdout, stderr) = ExeTests.RunSplit(new[] { "conflict", "--mo2", Instance, "--out", outDir, "--quiet" }.Concat(extra).ToArray());
            var json = File.Exists(Path.Combine(outDir, "conflicts.json"))
                ? JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "conflicts.json"))).RootElement.Clone()
                : default;
            return (code, stdout, stderr, json);
        }
    }

    static string S(JsonElement e, string p) => e.GetProperty(p).GetString() ?? "";

    [Fact]
    public void Conflict_on_an_MO2_profile_finds_the_conflicts_between_mods_in_the_mods_folder()
    {
        using var mo2 = new Mo2();
        var (code, stdout, _, json) = mo2.Conflict();
        Assert.Equal(0, code);
        // loadorder.txt's order: ModA, then ModB, so ModA's value is lost to ModB (plugins.txt alone has them the other way)
        var lost = Assert.Single(json.GetProperty("lost").EnumerateArray());
        Assert.Equal(("ModA.esm", "ModB.esm"), (S(lost, "Loser"), S(lost, "Winner")));
        Assert.StartsWith("RESULT: OK, 1 lost-change entries (0 official, 1 mod vs mod)", ExeTests.ResultLine(stdout));
        Assert.StartsWith("MO2 profile Main (", S(json, "loadOrderSource"));
        // the disabled mod's plugin isn't loaded, though plugins.txt marks it on; Data came from MO2's gamePath
        var loaded = json.GetProperty("loadOrder").EnumerateArray().Select(p => S(p, "FileName")).ToList();
        Assert.Equal(["Base.esm", "ModA.esm", "ModB.esm"], loaded);
        Assert.Contains(json.GetProperty("loadOrderProblems").EnumerateArray(), p => p.GetString()!.StartsWith("ModC.esm: listed but not found"));
        // the assets check found ModA's loose model in ModA's own folder
        Assert.Empty(json.GetProperty("missingAssets").EnumerateArray());
        File.Delete(Path.Combine(mo2.Mods, "ModA", "meshes", "Mo2Test", "Thing.nif"));
        (code, _, _, json) = mo2.Conflict();
        Assert.Equal(0, code);
        Assert.Single(json.GetProperty("missingAssets").EnumerateArray());   // the check does look for it
    }

    [Fact]
    public void Without_loadorder_txt_plugins_txt_gives_the_order()
    {
        using var mo2 = new Mo2();
        File.Delete(Path.Combine(mo2.Profile, "loadorder.txt"));
        var (code, _, _, json) = mo2.Conflict();
        Assert.Equal(0, code);
        var lost = Assert.Single(json.GetProperty("lost").EnumerateArray());
        Assert.Equal(("ModB.esm", "ModA.esm"), (S(lost, "Loser"), S(lost, "Winner")));
    }

    [Fact]
    public void Overwrite_wins_over_every_mod_and_the_top_mod_wins_over_lower_ones()
    {
        using var mo2 = new Mo2();
        // a second copy of ModB.esm in a lower mod (weight 4), and one in Overwrite (weight 3)
        mo2.Mod("ModBOld", "ModB.esm", m => m.Weight = 4);
        File.AppendAllLines(Path.Combine(mo2.Profile, "modlist.txt"), ["+ModBOld"]);
        var args = new[] { "find", "B_Misc", "--mo2", mo2.Instance };
        var (code, stdout, _) = ExeTests.RunSplit(args);
        Assert.Equal(0, code);
        Assert.Contains("    Weight: 2", stdout);   // ModB (top line) wins over ModBOld
        mo2.Mod("", "ModB.esm", m => m.Weight = 3, Path.Combine(mo2.Instance, "overwrite"));
        (code, stdout, _) = ExeTests.RunSplit(args);
        Assert.Equal(0, code);
        Assert.Contains("    Weight: 3", stdout);   // Overwrite wins
        Assert.Contains("RESULT: FOUND 1 record (", stdout);
    }

    [Fact]
    public void Profile_picks_another_profile_and_bad_profiles_stop_with_exit_2()
    {
        using var mo2 = new Mo2();
        var other = Path.Combine(mo2.Instance, "profiles", "Other");
        Directory.CreateDirectory(other);
        File.WriteAllLines(Path.Combine(other, "plugins.txt"), ["*Base.esm", "*ModA.esm"]);
        File.WriteAllLines(Path.Combine(other, "modlist.txt"), ["+ModA"]);
        var (code, stdout, _, json) = mo2.Conflict("--profile", "Other");
        Assert.Equal(0, code);
        Assert.Equal(["Base.esm", "ModA.esm"], json.GetProperty("loadOrder").EnumerateArray().Select(p => S(p, "FileName")));
        Assert.StartsWith("RESULT: OK, 0 lost-change entries", ExeTests.ResultLine(stdout));

        string err;
        (code, stdout, err, _) = mo2.Conflict("--profile", "Missing");
        Assert.Equal(2, code);
        Assert.Contains("there is no MO2 profile folder", err);
        Assert.StartsWith("RESULT: CANNOT CHECK (the MO2 profile can't be used)", ExeTests.ResultLine(stdout));
        (code, _, err, _) = mo2.Conflict("--profile", "..\\Main");
        Assert.Equal(2, code);
        Assert.Contains("isn't a single folder name", err);
        mo2.Ini("");   // nothing selected and no --profile
        (code, _, err, _) = mo2.Conflict();
        Assert.Equal(2, code);
        Assert.Contains("no --profile given and no profile selected", err);
    }

    [Fact]
    public void Mo2_with_plugins_and_profile_without_mo2_stop_with_exit_2()
    {
        using var mo2 = new Mo2();
        var txt = Path.Combine(mo2.Dir, "Plugins.txt");
        File.WriteAllLines(txt, ["*Base.esm"]);
        foreach (var cmd in new[] { "conflict", "patch", "find" })
        {
            var more = cmd == "find" ? new[] { "B_Misc" } : new[] { "--out", Path.Combine(mo2.Dir, "out-" + cmd) };
            var (code, _, err) = ExeTests.RunSplit(new[] { cmd, "--mo2", mo2.Instance, "--plugins", txt }.Concat(more).ToArray());
            Assert.Equal(2, code);
            Assert.Contains("--mo2 and --plugins both give a load order", err);
            (code, _, err) = ExeTests.RunSplit(new[] { cmd, "--profile", "Main", "--plugins", txt, "--data", mo2.Data }.Concat(more).ToArray());
            Assert.Equal(2, code);
            Assert.Contains("--profile names an MO2 profile, so it needs --mo2", err);
        }
    }

    [Fact]
    public void An_enabled_mod_with_no_folder_is_a_load_order_problem()
    {
        using var mo2 = new Mo2();
        File.AppendAllLines(Path.Combine(mo2.Profile, "modlist.txt"), ["+Gone"]);
        var (code, _, _, json) = mo2.Conflict();
        Assert.Equal(0, code);
        Assert.Contains(json.GetProperty("loadOrderProblems").EnumerateArray(), p => p.GetString()!.StartsWith("MO2 mod Gone is enabled but has no folder"));
    }

    [Fact]
    public void Patch_on_an_MO2_profile_restores_the_lost_change_and_says_how_to_add_it_in_MO2()
    {
        using var mo2 = new Mo2();
        var outDir = Path.Combine(mo2.Dir, "patch");
        var (code, stdout, _) = ExeTests.RunSplit("patch", "--mo2", mo2.Instance, "--out", outDir, "--quiet");
        Assert.Equal(0, code);
        Assert.StartsWith("RESULT: OK, patch ", ExeTests.ResultLine(stdout));
        using var g = new GameData(mo2.Data, [Path.Combine(mo2.Mods, "ModA"), Path.Combine(mo2.Mods, "ModB")]);
        var patch = StarfieldMod.CreateFromBinaryOverlay(Path.Combine(outDir, "ConflictPatch.esm"), StarfieldRelease.Starfield,
            new Mutagen.Bethesda.Plugins.Binary.Parameters.BinaryReadParameters
            {
                MasterFlagsLookup = new Mutagen.Bethesda.Plugins.Order.LoadOrder<Mutagen.Bethesda.Plugins.Records.IModMasterStyledGetter>(
                    new[] { "Base.esm", "ModA.esm", "ModB.esm" }.Select(n => (Mutagen.Bethesda.Plugins.Records.IModMasterStyledGetter)g.Open(n)))
            });
        using (patch)
        {
            var misc = Assert.Single(patch.MiscItems);
            Assert.Equal((10, 2f), (misc.Value, misc.Weight));   // ModA's value and ModB's weight
        }
        var report = File.ReadAllText(Path.Combine(outDir, "patch-report.md"));
        Assert.Contains("In MO2, use Create empty mod", report);
        // --out may not be a mod folder
        (code, _, var err) = ExeTests.RunSplit("patch", "--mo2", mo2.Instance, "--out", Path.Combine(mo2.Mods, "ModA"), "--quiet");
        Assert.Equal(2, code);
        Assert.Contains("an MO2 mod or Overwrite folder", err);
    }

    [Fact]
    public void ReadIni_unwraps_ByteArray_and_keeps_the_first_value_of_a_key()
    {
        using var s = new Sandbox();
        File.WriteAllLines(Path.Combine(s.Dir, "ModOrganizer.ini"),
            ["[General]", "selected_profile=@ByteArray(Main)", "gamePath=@ByteArray(C:\\\\Games\\\\Starfield)", "[Other]", "selected_profile=Later"]);
        var ini = Mo2Profile.ReadIni(s.Dir);
        Assert.Equal("Main", ini["selected_profile"]);
        Assert.Equal("C:\\Games\\Starfield", ini["GAMEPATH"]);
        Assert.False(Mo2Profile.IsFolderName(".."));
        Assert.False(Mo2Profile.IsFolderName("a\\b"));
        Assert.True(Mo2Profile.IsFolderName("My Mod 1.2"));
    }
}
