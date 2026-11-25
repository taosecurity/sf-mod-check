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

    /// <summary>A My Games Data folder that doesn't exist, so Mo2Profile.Read never adds the real one on the test PC
    /// (code review round 1, 0.16.1).</summary>
    static string NoMyGames(Sandbox s) => Path.Combine(s.Dir, "no My Games Data");

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

    // code review round 1 (0.16.0): the AI prompt named Data and a Plugins.txt copy, the report said to run check,
    // and Plugins-with-patch.txt dropped turned-off plugins
    [Fact]
    public void Under_MO2_the_reports_name_MO2_steps_and_Plugins_with_patch_keeps_turned_off_plugins()
    {
        using var mo2 = new Mo2();
        File.WriteAllLines(Path.Combine(mo2.Profile, "plugins.txt"), ["# made-up", "*Base.esm", "*ModB.esm", "*ModA.esm", "ModC.esm"]);
        var (code, _, _, _) = mo2.Conflict();
        Assert.Equal(0, code);
        var text = string.Join("\n", Directory.EnumerateFiles(Path.Combine(mo2.Dir, "report"), "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".log", StringComparison.OrdinalIgnoreCase)).Select(File.ReadAllText));
        Assert.Contains("Put the patch in a new empty MO2 mod", text);
        Assert.Contains("run sf-mod-check conflict --mo2 ", text);
        Assert.Contains("where my MO2 instance", text);
        Assert.DoesNotContain("copy of my load order", text);

        var outDir = Path.Combine(mo2.Dir, "patch");
        (code, _, _) = ExeTests.RunSplit("patch", "--mo2", mo2.Instance, "--out", outDir, "--quiet");
        Assert.Equal(0, code);
        var report = File.ReadAllText(Path.Combine(outDir, "patch-report.md"));
        Assert.DoesNotContain("`sf-mod-check check ConflictPatch.esm`", report);
        Assert.Contains("doesn't read MO2 profiles yet", report);
        var withPatch = File.ReadAllLines(Path.Combine(outDir, "Plugins-with-patch.txt"));
        Assert.Contains("ModC.esm", withPatch);   // unstarred: turned off
        Assert.DoesNotContain("*ModC.esm", withPatch);
    }

    // 0.18.0: the rerun command in the reports repeats the --data and --extra-data the user gave (0.17.0 left them out)
    [Fact]
    public void The_MO2_rerun_command_repeats_the_data_and_extra_data_options()
    {
        using var mo2 = new Mo2();
        var extra = Path.Combine(mo2.Dir, "extraloose");
        Directory.CreateDirectory(extra);
        File.WriteAllLines(Path.Combine(mo2.Profile, "plugins.txt"), ["# made-up", "*Base.esm", "*ModB.esm", "*ModA.esm", "ModC.esm"]);
        var (code, _, _, _) = mo2.Conflict("--data", mo2.Data, "--extra-data", extra);
        Assert.Equal(0, code);
        var text = string.Join("\n", Directory.EnumerateFiles(Path.Combine(mo2.Dir, "report"), "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".log", StringComparison.OrdinalIgnoreCase)).Select(File.ReadAllText));
        Assert.Contains($"--data \"{Path.GetFullPath(mo2.Data)}\" --extra-data \"{Path.GetFullPath(extra)}\" again", text);

        var outDir = Path.Combine(mo2.Dir, "patch");
        (code, _, _) = ExeTests.RunSplit("patch", "--mo2", mo2.Instance, "--data", mo2.Data, "--extra-data", extra, "--out", outDir, "--quiet");
        Assert.Equal(0, code);
        var report = File.ReadAllText(Path.Combine(outDir, "patch-report.md"));
        Assert.Contains($"--data \"{Path.GetFullPath(mo2.Data)}\" --extra-data \"{Path.GetFullPath(extra)}\"` again", report);
    }

    // 0.18.0 code review round 1: a folder given with a trailing backslash must not end in \" in the rerun command
    // (the backslash would escape the closing quote when the line is pasted), and a drive root keeps its backslash
    [Fact]
    public void The_MO2_rerun_command_drops_a_trailing_backslash_from_the_folders()
    {
        Assert.Equal("\"D:\\Games\\Data\"", LoadOrderInput.QuotedPath("D:\\Games\\Data\\"));
        Assert.Equal("\"D:\\\\\"", LoadOrderInput.QuotedPath("D:\\"));
        using var mo2 = new Mo2();
        var extra = Path.Combine(mo2.Dir, "extraloose");
        Directory.CreateDirectory(extra);
        File.WriteAllLines(Path.Combine(mo2.Profile, "plugins.txt"), ["# made-up", "*Base.esm", "*ModB.esm", "*ModA.esm", "ModC.esm"]);
        var (code, _, _, _) = mo2.Conflict("--data", Path.GetFullPath(mo2.Data) + "\\", "--extra-data", extra + "\\");
        Assert.Equal(0, code);
        var text = string.Join("\n", Directory.EnumerateFiles(Path.Combine(mo2.Dir, "report"), "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".log", StringComparison.OrdinalIgnoreCase)).Select(File.ReadAllText));
        Assert.Contains($"--data \"{Path.GetFullPath(mo2.Data)}\" --extra-data \"{Path.GetFullPath(extra)}\" again", text);
        Assert.DoesNotContain("\\\" again", text);

        // round 2: the --mo2 instance folder on the same line too, in conflict's AI prompt and patch's report
        var inst = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mo2.Instance));
        var outDir = Path.Combine(mo2.Dir, "report2");
        (code, _, _) = ExeTests.RunSplit("conflict", "--mo2", inst + "\\", "--out", outDir, "--quiet");
        Assert.Equal(0, code);
        text = string.Join("\n", Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".log", StringComparison.OrdinalIgnoreCase)).Select(File.ReadAllText));
        Assert.Contains($"--mo2 \"{inst}\" --profile", text);
        Assert.DoesNotContain("\\\" --profile", text);
        var patchDir = Path.Combine(mo2.Dir, "patch2");
        (code, _, _) = ExeTests.RunSplit("patch", "--mo2", inst + "\\", "--out", patchDir, "--quiet");
        Assert.Equal(0, code);
        var report = File.ReadAllText(Path.Combine(patchDir, "patch-report.md"));
        Assert.Contains($"--mo2 \"{inst}\" --profile", report);
    }

    // code review round 1 (0.16.0): MO2's default for a missing mod_directory or profiles_directory is
    // %BASE_DIR%/mods (or profiles), and Overwrite doesn't need modlist.txt
    [Fact]
    public void A_moved_base_directory_moves_the_default_folders_and_Overwrite_is_read_without_modlist()
    {
        using var s = new Sandbox();
        var inst = s.Sub("Instance");
        var moved = s.Sub("Moved");
        var profile = s.Sub(Path.Combine("Moved", "profiles", "Main"));
        s.Sub(Path.Combine("Moved", "mods", "ModA"));
        var overwrite = s.Sub(Path.Combine("Moved", "overwrite"));
        File.WriteAllLines(Path.Combine(inst, "ModOrganizer.ini"),
            ["[General]", "selected_profile=@ByteArray(Main)", "[Settings]", $"base_directory={moved.Replace("\\", "/")}"]);
        File.WriteAllLines(Path.Combine(profile, "plugins.txt"), ["*ModA.esm"]);
        File.WriteAllLines(Path.Combine(profile, "modlist.txt"), ["+ModA"]);
        var mo = Mo2Profile.Read(inst, null, out var error, NoMyGames(s));
        Assert.True(mo != null, error);
        Assert.Equal([overwrite, Path.Combine(moved, "mods", "ModA")], mo!.ModDirs);
        File.Delete(Path.Combine(profile, "modlist.txt"));
        mo = Mo2Profile.Read(inst, null, out error, NoMyGames(s));
        Assert.True(mo != null, error);
        Assert.Equal([overwrite], mo!.ModDirs);
        Assert.Contains(mo.Problems, p => p.StartsWith("no modlist.txt"));
    }

    // code review round 4 (0.16.0): official files MO2 leaves out of plugins.txt lost their loadorder.txt place
    [Fact]
    public void Official_files_only_in_loadorder_txt_keep_their_place_unless_plugins_txt_turns_them_off()
    {
        using var s = new Sandbox();
        var inst = s.Sub("Instance");
        var profile = s.Sub(Path.Combine("Instance", "profiles", "Main"));
        File.WriteAllLines(Path.Combine(inst, "ModOrganizer.ini"), ["[General]", "selected_profile=@ByteArray(Main)"]);
        File.WriteAllLines(Path.Combine(profile, "plugins.txt"), ["*ModA.esm", "SFBGS004.esm", "ModB.esm"]);
        File.WriteAllLines(Path.Combine(profile, "modlist.txt"), ["+ModA"]);
        File.WriteAllLines(Path.Combine(profile, "loadorder.txt"),
            ["Starfield.esm", "SFBGS00D.esm", "SFBGS004.esm", "SFBGS003.esm", "ModA.esm", "ModB.esm"]);
        var mo = Mo2Profile.Read(inst, null, out var error, NoMyGames(s));
        Assert.True(mo != null, error);
        Assert.Equal(["Starfield.esm", "SFBGS00D.esm", "SFBGS003.esm", "ModA.esm"], mo!.Order.Listed);
    }

    // code review round 2 (0.16.0): the patch report's check line named no profile, find's hint said to give --mo2
    // to a run that had it, and a missing plugin was "not found in Data" though the MO2 mod folders were searched too
    [Fact]
    public void Under_MO2_the_patch_check_line_find_hint_and_missing_plugin_text_name_the_MO2_profile_and_folders()
    {
        using var mo2 = new Mo2();
        var outDir = Path.Combine(mo2.Dir, "patch");
        var (code, _, _) = ExeTests.RunSplit("patch", "--mo2", mo2.Instance, "--out", outDir, "--quiet");
        Assert.Equal(0, code);
        Assert.Contains($"conflict --mo2 \"{mo2.Instance}\" --profile \"Main\"", File.ReadAllText(Path.Combine(outDir, "patch-report.md")));
        // round 4: --only names the profile, not its plugins.txt (ModC.esm is starred there, but its mod is off)
        string stderr;
        (code, _, stderr) = ExeTests.RunSplit("patch", "--mo2", mo2.Instance, "--out", outDir, "--quiet", "--only", "ModC.esm");
        Assert.Equal(2, code);
        Assert.Contains("isn't active in the MO2 profile Main", stderr);

        string stdout;
        (code, stdout, _) = ExeTests.RunSplit("find", "NoSuchRecord", "--mo2", mo2.Instance);
        Assert.Equal(1, code);
        Assert.Contains("check that it is enabled in this MO2 profile", stdout);
        Assert.DoesNotContain("with Mod Organizer 2: --mo2", stdout);

        var (_, _, _, json) = mo2.Conflict();
        Assert.Contains(json.GetProperty("loadOrderProblems").EnumerateArray(),
            p => p.GetString() == "ModC.esm: listed but not found in Data or the extra and MO2 mod folders");
    }

    // code review round 3 (0.16.0): the top usage, the unexpected-argument errors and a missing --mo2 value didn't
    // name --mo2
    [Fact]
    public void The_usage_and_argument_errors_name_mo2_for_conflict_and_patch()
    {
        var (_, stdout, stderr) = ExeTests.RunSplit();
        var usage = stdout + stderr;
        Assert.Contains("sf-mod-check conflict [--plugins <Plugins.txt> | --mo2 <MO2 folder> [--profile <name>]]", usage);
        Assert.Contains("sf-mod-check patch [--plugins <Plugins.txt> | --mo2 <MO2 folder> [--profile <name>]]", usage);
        foreach (var cmd in new[] { "conflict", "patch" })
        {
            var (code, _, err) = ExeTests.RunSplit(cmd, "C:\\Modding\\MO2");
            Assert.Equal(2, code);
            Assert.Contains("or with MO2 the instance folder with --mo2", err);
        }
        var (c2, o2, e2) = ExeTests.RunSplit("find", "B_Misc", "--mo2");
        Assert.Equal(2, c2);
        Assert.Contains("--mo2 \"C:\\Modding\\MO2\"", o2 + e2);
    }

    [Fact]
    public void ReadIni_unwraps_ByteArray_and_keeps_the_first_value_of_a_key()
    {
        using var s = new Sandbox();
        File.WriteAllLines(Path.Combine(s.Dir, "ModOrganizer.ini"),
            ["[General]", "selected_profile=@ByteArray(Main)", "gamePath=@ByteArray(C:\\\\Games\\\\Starfield)", "[Other]", "selected_profile=Later",
             "[Settings]", "mod_directory=\"D:/Games, MO2/mods\""]);
        var ini = Mo2Profile.ReadIni(s.Dir);
        Assert.Equal("Main", ini["selected_profile"]);
        Assert.Equal("C:\\Games\\Starfield", ini["GAMEPATH"]);
        Assert.Equal("D:/Games, MO2/mods", ini["mod_directory"]);   // QSettings quotes a value with a comma (round 2)
        Assert.False(Mo2Profile.IsFolderName(".."));
    }

    [Fact]
    public void ReadIni_decodes_the_escapes_Qt_writes_for_non_ASCII_folder_names()
    {
        // Round 3: Qt 5's QSettings writes ö as \xf6 in text and its UTF-8 bytes as \xc3\xb6 inside @ByteArray(...).
        using var s = new Sandbox();
        File.WriteAllLines(Path.Combine(s.Dir, "ModOrganizer.ini"),
            ["[General]", "gamePath=@ByteArray(C:\\\\Users\\\\J\\xc3\\xb6rg\\\\Game)", "[Settings]", "base_directory=C:/Users/J\\xf6rg/MO2",
             "mod_directory=D:/\\x41\\x42/mods", "overwrite_directory=C:/Users/Jörg/ow"]);
        var ini = Mo2Profile.ReadIni(s.Dir);
        Assert.Equal("C:\\Users\\Jörg\\Game", ini["gamePath"]);
        Assert.Equal("C:/Users/Jörg/MO2", ini["base_directory"]);
        Assert.Equal("D:/AB/mods", ini["mod_directory"]);
        Assert.Equal("C:/Users/Jörg/ow", ini["overwrite_directory"]);   // a UTF-8 ini is read as it is
        Assert.False(Mo2Profile.IsFolderName("a\\b"));
        Assert.True(Mo2Profile.IsFolderName("My Mod 1.2"));
    }

    [NeedsStarfieldFact]
    public void Conflict_on_an_MO2_instance_whose_gamePath_is_the_real_game_finds_two_mods_changing_the_same_official_record()
    {
        // Two made-up mods each change the Iron misc item of the real Starfield.esm; MO2's gamePath is the real game
        // folder, so Data comes from it. Set SFMODCHECK_MO2_KEEP to a folder to keep the instance and the report there.
        var data = NeedsStarfieldFactAttribute.Data;
        using var s = new Sandbox();
        var inst = s.Sub("Instance");
        var profile = s.Sub(Path.Combine("Instance", "profiles", "Main"));
        using (var g = new GameData(data))
        {
            var iron = g.Open("Starfield.esm").MiscItems.First(m => m.EditorID == "InorgCommonIron");
            foreach (var (folder, value) in new[] { ("IronMod1", 11), ("IronMod2", 22) })
            {
                var dir = s.Sub(Path.Combine("Instance", "mods", folder));
                var mod = Plugin.New(folder + ".esm", PluginStyle.Light, "test", "made-up mod");
                mod.MiscItems.GetOrAddAsOverride(iron).Value = value;
                PluginWriter.Write(mod, dir, g, NoGap);
            }
        }
        File.WriteAllLines(Path.Combine(inst, "ModOrganizer.ini"),
            ["[General]", "gameName=Starfield", $"gamePath=@ByteArray({Path.GetDirectoryName(data)!.Replace("\\", "\\\\")})", "selected_profile=@ByteArray(Main)"]);
        File.WriteAllLines(Path.Combine(profile, "modlist.txt"), ["# made-up", "+IronMod2", "+IronMod1"]);
        File.WriteAllLines(Path.Combine(profile, "plugins.txt"), ["# made-up", "*IronMod1.esm", "*IronMod2.esm"]);
        File.WriteAllLines(Path.Combine(profile, "loadorder.txt"), ["IronMod1.esm", "IronMod2.esm"]);
        var outDir = Path.Combine(s.Dir, "report");
        var (code, stdout, _) = ExeTests.RunSplit(["conflict", "--mo2", inst, "--out", outDir, "--quiet"]);
        Assert.Equal(0, code);
        Assert.Contains("1 mod vs mod)", ExeTests.ResultLine(stdout));
        var lost = Assert.Single(JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "conflicts.json"))).RootElement.GetProperty("lost").EnumerateArray());
        Assert.Equal(("IronMod1.esm", "IronMod2.esm"), (S(lost, "Loser"), S(lost, "Winner")));
        if (Environment.GetEnvironmentVariable("SFMODCHECK_MO2_KEEP") is { Length: > 0 } keep)
        {
            Directory.CreateDirectory(keep);
            foreach (var d in new[] { "Instance", "report" })
                foreach (var f in Directory.EnumerateFiles(Path.Combine(s.Dir, d), "*", SearchOption.AllDirectories))
                {
                    var to = Path.Combine(keep, Path.GetRelativePath(s.Dir, f));
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Copy(f, to, true);
                }
            File.WriteAllText(Path.Combine(keep, "stdout.txt"), stdout);
        }
    }
}
