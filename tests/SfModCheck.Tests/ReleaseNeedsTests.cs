// The release command's needs step (0.7.0, chunk C2): attached scripts against the zips, --masters-dir archives and
// the game's archives; the DMMDeps pin and the mapping of its lists; the plugin check's lines.
using System.IO.Compression;
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Starfield;
using SfModCheck;
using Xunit;

namespace SfModCheck.Tests;

public class ReleaseNeedsTests
{
    static readonly WriteOptions NoGap = new() { GapCheck = false };

    /// <summary>Writes a light plugin with no masters and one misc item into dir.</summary>
    static string Write(string dir, string name, Action<StarfieldMod, MiscItem> change)
    {
        Directory.CreateDirectory(dir);
        var mod = Plugin.New(name, PluginStyle.Light, "test", "release needs test");
        var misc = mod.MiscItems.AddNew("RN_Misc");
        change(mod, misc);
        using (var g = new GameData(dir)) PluginWriter.Write(mod, dir, g, NoGap);
        return Path.Combine(dir, name);
    }

    static void Put(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>Zips the folder and unpacks it the way the release command does.</summary>
    static ReleaseContext Context(Sandbox s, string pkgDir, string data, Action<ReleaseOptions>? configure = null)
    {
        var zip = Path.Combine(s.Sub("zips"), Path.GetFileName(pkgDir) + "-0.1.0.zip");
        ZipFile.CreateFromDirectory(pkgDir, zip);
        var work = s.Sub("work");
        var o = new ReleaseOptions { DataDir = data, DataGiven = true };
        o.Zips.Add(zip);
        configure?.Invoke(o);
        return new ReleaseContext { Options = o, Packages = [ReleaseCommand.Unpack(zip, Path.Combine(work, "zip0"))], Work = work };
    }

    static string List(IEnumerable<ReleaseFinding> f) => string.Join("\n", f.Select(x => $"  {x.Level} [{x.Rule}] {x.Text}"));

    [Fact]
    public void Attached_scripts_are_found_in_the_zip_masters_dirs_and_game_archives_and_missing_ones_are_problems()
    {
        using var s = new Sandbox();
        var data = s.Sub("Data");
        Write(data, "Starfield.esm", (_, _) => { });
        File.WriteAllBytes(Path.Combine(data, "Starfield - Misc.ba2"), AssetsTests.Ba2("GNRL", "Scripts\\RnTest\\FromGame.pex"));
        // a loose copy in Data (an older install, say) doesn't count: the release must ship the script
        Put(Path.Combine(data, "Scripts", "RnTest", "Missing.pex"), new byte[4]);
        var masters = s.Sub("masters");
        File.WriteAllText(Path.Combine(masters, "Other.esm"), "not read");
        File.WriteAllBytes(Path.Combine(masters, "Other - Main.ba2"), AssetsTests.Ba2("GNRL", "scripts/rntest/frommaster.pex"));

        var pkg = s.Sub("RnTest");
        Write(pkg, "RnTest.esm", (_, misc) => misc.VirtualMachineAdapter = new VirtualMachineAdapter
        {
            Scripts =
            {
                new ScriptEntry { Name = "RnTest:Loose" }, new ScriptEntry { Name = "NoNsPacked" },
                new ScriptEntry { Name = "RnTest:FromGame" }, new ScriptEntry { Name = "RnTest:FromMaster" },
                new ScriptEntry { Name = "RnTest:Missing" }, new ScriptEntry { Name = "NoNsMissing" },
            },
        });
        Put(Path.Combine(pkg, "Scripts", "RnTest", "Loose.pex"), new byte[4]);
        File.WriteAllBytes(Path.Combine(pkg, "RnTest - Main.ba2"), AssetsTests.Ba2("GNRL", "Scripts\\NoNsPacked.pex"));

        var f = ReleaseNeeds.Run(Context(s, pkg, data, o => o.MastersDirs.Add(masters))).ToList();
        var missing = f.Where(x => x.Rule == "script-missing").ToList();
        Assert.True(missing.Count == 2, List(f));
        Assert.Contains(missing, x => x.Level == Level.Problem && x.Text.Contains("RnTest:Missing") && x.Text.Contains("scripts\\rntest\\missing.pex") && x.Text.StartsWith("RnTest.esm"));
        Assert.Contains(missing, x => x.Level == Level.Problem && x.Text.Contains("NoNsMissing"));
        Assert.All(missing, x => Assert.Equal(RuleKind.Engine, x.Kind));
        // the plugin check runs without its assets check, so no script is reported twice
        Assert.DoesNotContain(f, x => x.Rule == "plugin-check" && x.Text.Contains("missing.pex", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(f, x => x.Level == Level.Note && x.Rule == "plugin-check" && x.Text.StartsWith("RnTest.esm: OK"));
        Assert.Contains(f, x => x.Level == Level.Note && x.Rule == "dmmdeps" && x.Text.Contains("wasn't given"));
    }

    [Fact]
    public void Scripts_are_still_checked_when_a_master_is_missing()
    {
        using var s = new Sandbox();
        var data = s.Sub("Data");
        var elsewhere = s.Sub("elsewhere");
        var master = Plugin.New("RnMaster.esm", PluginStyle.Full, "test", "a master the check can't find");
        var kw = master.Keywords.AddNew("RNM_Keyword");
        using (var g = new GameData(elsewhere)) PluginWriter.Write(master, elsewhere, g, NoGap);
        var pkg = s.Sub("RnOrphan");
        var mod = Plugin.New("RnOrphan.esm", PluginStyle.Light, "test", "needs RnMaster.esm");
        var misc = mod.MiscItems.AddNew("RN_Misc");
        misc.Keywords = [kw.ToLink<IKeywordGetter>()];
        misc.VirtualMachineAdapter = new VirtualMachineAdapter { Scripts = { new ScriptEntry { Name = "RnOrphan:Gone" } } };
        using (var g = new GameData(elsewhere)) { g.Open("RnMaster.esm"); PluginWriter.Write(mod, pkg, g, NoGap); }
        var f = ReleaseNeeds.Run(Context(s, pkg, data)).ToList();
        Assert.Contains(f, x => x.Level == Level.Problem && x.Rule == "script-missing" && x.Text.Contains("RnOrphan:Gone"));
        Assert.Contains(f, x => x.Level == Level.Problem && x.Rule == "plugin-check" && x.Text.Contains("RnMaster.esm is not installed"));
    }

    [Fact]
    public void A_dmmdeps_exe_with_another_hash_is_refused_and_not_run()
    {
        using var s = new Sandbox();
        var data = s.Sub("Data");
        var pkg = s.Sub("RnHash");
        Write(pkg, "RnHash.esm", (_, _) => { });
        var fake = Path.Combine(s.Sub("tools"), "dmmdeps-fat.exe");
        File.WriteAllText(fake, "not DMMDeps");
        var f = ReleaseNeeds.Run(Context(s, pkg, data, o => o.DmmDeps = fake)).ToList();
        var d = f.Where(x => x.Rule == "dmmdeps").ToList();
        Assert.True(d.Count == 1, List(f));
        Assert.Equal(Level.Problem, d[0].Level);
        Assert.Contains("not the reviewed v0.4.1 build", d[0].Text);
        Assert.Contains(ReleaseNeeds.Sha256(fake), d[0].Text);
        Assert.False(Directory.Exists(Path.Combine(s.Dir, "work", "dmmdeps")), "the scratch folder was made, so DMMDeps was about to run");
    }

    [Fact]
    public void A_plugin_check_error_is_a_problem_naming_the_plugin()
    {
        using var s = new Sandbox();
        var data = s.Sub("Data");
        var pkg = s.Sub("RnBad");
        Write(pkg, "RnBad.esm", (mod, misc) => misc.Keywords = [new FormKey(mod.ModKey, 0xFF0).ToLink<IKeywordGetter>()]);
        var f = ReleaseNeeds.Run(Context(s, pkg, data)).ToList();
        Assert.Contains(f, x => x.Level == Level.Problem && x.Rule == "plugin-check" && x.Text.StartsWith("RnBad.esm: [links]") && x.Text.Contains("doesn't exist"));
        Assert.Contains(f, x => x.Level == Level.Note && x.Rule == "plugin-check" && x.Text.StartsWith("RnBad.esm: FAILED, 1 errors"));
    }

    [Fact]
    public void Dmmdeps_lists_are_mapped_as_reference_output_maps_them()
    {
        var staged = new HashSet<string>
        {
            "meshes\\mod\\a.nif", "meshes\\actors\\character\\facegendata\\facegeom\\mod.esm\\00000801.nif", "scripts\\nons.pex",
            "scripts\\mod\\base.pex", "sound\\voice\\mod.esm\\wwise.dat", "textures\\old.dds", "readme-mod.md",
        };
        var keep = new HashSet<string> { "meshes\\mod\\a.nif", "materials\\mod\\b.mat", "scripts\\mod\\missing.pex" };
        var lists = new[] { ("Mod.esm", keep, new List<string> { "meshes\\vanilla.nif" }, new List<string> { "textures\\gone.dds" }) };
        var reported = new Dictionary<string, HashSet<string>> { ["Mod.esm"] = ["scripts\\mod\\missing.pex"] };
        var f = ReleaseNeeds.MapLists("Mod-0.1.0.zip", staged, lists, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "NoNs" }, reported);
        string One(Level l, string path) => Assert.Single(f, x => x.Level == l && x.Text.Contains(path)).Text;
        Assert.Contains("needs materials\\mod\\b.mat", One(Level.Problem, "b.mat"));
        Assert.Contains("found nowhere", One(Level.Problem, "gone.dds"));
        Assert.Contains("also in the game's archives", One(Level.Review, "vanilla.nif"));
        Assert.Contains("FaceGen mesh (#176)", One(Level.Review, "00000801.nif"));
        Assert.Contains("base script via extends? (#175)", One(Level.Review, "base.pex"));
        Assert.Contains("unreferenced: stale leftover?", One(Level.Review, "old.dds"));
        Assert.Contains("by design (#176)", One(Level.Note, "wwise.dat"));
        Assert.Contains("DMMDeps misses names without a namespace", One(Level.Note, "nons.pex"));
        Assert.DoesNotContain(f, x => x.Text.Contains("missing.pex"));   // the scripts step reported it already
        Assert.DoesNotContain(f, x => x.Text.Contains("readme-mod.md"));  // root files aren't game data
        Assert.Equal(8, f.Count);   // a.nif is staged and needed: nothing to say
    }

    [Fact]
    public void Dmmdeps_paths_lose_data_and_slashes()
    {
        Assert.Equal("meshes\\a\\b.nif", ReleaseNeeds.Norm("Data/Meshes//A\\B.nif"));
        Assert.Equal("scripts\\x.pex", ReleaseNeeds.Norm(" \\Scripts\\X.pex "));
    }

    [Theory]
    [InlineData("Starfield - Meshes01.ba2", true)]
    [InlineData("ShatteredSpace - Main.ba2", true)]
    [InlineData("SFBGS003 - Main.ba2", true)]
    [InlineData("BlueprintShips-Starfield - Localization.ba2", true)]
    [InlineData("ContentResources.zip", true)]
    [InlineData("BLUEPRINTSHIPS-STARFIELD - Main.ba2", true)]
    [InlineData("CONTENTRESOURCES.ZIP", true)]
    [InlineData("sfta01 - Main.ba2", true)]
    [InlineData("sfta10 - Main.ba2", true)]
    [InlineData("BlueprintShips-SFBGS050 - Main.ba2", true)]
    [InlineData("SFBGSExtras - Main.ba2", false)]
    [InlineData("sfbgsfix.ba2", false)]
    [InlineData("somemod - main.ba2", false)]
    [InlineData("MyMod-ShatteredSpaceAddon - Main.ba2", false)]
    [InlineData("StarfieldExtended - Main.ba2", false)]
    [InlineData("SFBGS003Fixes - Main.ba2", false)]
    [InlineData("sfta01Patch - Main.ba2", false)]
    [InlineData("SFBGS003.Fixes - Main.ba2", false)]
    [InlineData("Starfield.Extras - Main.ba2", false)]
    [InlineData("sfta01.patch.ba2", false)]
    [InlineData("Starfield - Notes.txt", false)]
    [InlineData("SFBGS003.zip", false)]
    [InlineData("Data/Starfield - Textures01.ba2", true)]
    [InlineData("Data\\Starfield - Textures01.ba2", true)]
    [InlineData("C:\\Games\\Starfield\\Data\\ContentResources.zip", true)]
    [InlineData("Data\\Starfield - Textures01.ba2 \n", true)]
    [InlineData("Data\\SomeMod - Main.ba2", false)]
    public void Official_archives_are_told_from_mods(string archive, bool official) =>
        Assert.Equal(official, ReleaseNeeds.IsOfficialArchive(archive));

    /// <summary>DMMDeps matched every file of one mod in that mod's own BA2, left in its cache from
    /// an old deploy. Stale mod matches count as found; game matches stay REVIEW; a master mod's archive is named.</summary>
    [Fact]
    public void Parent_matches_in_cached_mod_archives_count_as_found()
    {
        using var s = new Sandbox();
        var dir = s.Sub("p");
        var plugin = Path.Combine(dir, "Patch.esm");
        File.WriteAllBytes(plugin, ReleaseRulesTests.Plugin("Starfield.esm", "Base.esm"));

        var deps = Path.Combine(dir, "Patch_deps.json");
        File.WriteAllText(deps, """
            {"Plugin":"Patch.esm","Files":[
              {"PcPath":"Data\\Meshes\\Mine.nif","Kind":"parentmatch","Source":"parent-archive:patch - main.ba2"},
              {"PcPath":"Data\\Meshes\\Vanilla.nif","Kind":"parentmatch","Source":"parent-archive:Starfield - Meshes01.ba2"},
              {"PcPath":"Data\\Meshes\\FromBase.nif","Kind":"parentmatch","Source":"parent-archive:Base - Main.ba2"},
              {"PcPath":"Data\\Meshes\\FromBase2.nif","Kind":"parentmatch","Source":"parent-archive:Data\\Base - Textures.ba2 \n"},
              {"PcPath":"Data\\Meshes\\Kept.nif","Kind":"keep","Source":"disk"}]}
            """);
        var sources = ReleaseNeeds.ReadParentSources(deps);
        Assert.Equal(4, sources.Count);
        Assert.Equal("Data\\Base - Textures.ba2", sources["meshes\\frombase2.nif"]);   // trimmed when read
        var keep = new HashSet<string>(StringComparer.Ordinal) { "meshes\\kept.nif" };
        var findings = new List<ReleaseFinding>();
        var left = ReleaseNeeds.SortParentMatches("Patch.esm", plugin, ["meshes\\mine.nif", "meshes\\vanilla.nif", "meshes\\frombase.nif", "meshes\\frombase2.nif", "meshes\\unknown.nif"],
            sources, keep, findings);

        Assert.Equal(["meshes\\vanilla.nif", "meshes\\unknown.nif"], left);          // game match and no source: usual mapping
        Assert.Contains("meshes\\mine.nif", keep);                                    // stale cache: found
        Assert.Contains(findings, f => f.Level == Level.Review && f.Text.Contains("master's archive Base - Main.ba2") && f.Text.Contains("frombase.nif"));
        Assert.Contains(findings, f => f.Level == Level.Note && f.Text.Contains("patch - main.ba2 (1)"));
        // A Windows folder in the archive name still finds the master on every OS (code review round 7).
        Assert.Contains(findings, f => f.Level == Level.Review && f.Text.Contains("Base - Textures.ba2") && f.Text.Contains("frombase2.nif"));
        Assert.DoesNotContain("meshes\\frombase2.nif", keep);
        Assert.Equal(3, findings.Count);
    }
}
