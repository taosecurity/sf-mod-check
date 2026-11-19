// The assets check (0.6.0): the archive name reader, the INI archive lists, and the check on a fixture plugin.
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Starfield;
using Mutagen.Bethesda.Starfield.Assets;
using Xunit;

namespace SfModCheck.Tests;

public class AssetsTests
{
    /// <summary>A minimal general (GNRL) BA2 holding these names: the 24-byte header, no file records the reader needs,
    /// then the name table.</summary>
    internal static byte[] Ba2(string type, params string[] names)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("BTDX"));
        w.Write(2u);                                   // version
        w.Write(Encoding.ASCII.GetBytes(type));
        w.Write((uint)names.Length);
        w.Write(0UL);                                  // name table offset, filled below
        w.Write(new byte[8]);                          // v2 header extra
        w.Write(new byte[36 * names.Length]);          // file records (not read)
        var nameOfs = (ulong)ms.Position;
        foreach (var n in names)
        {
            var b = Encoding.UTF8.GetBytes(n);
            w.Write((ushort)b.Length);
            w.Write(b);
        }
        ms.Position = 16;
        w.Write(nameOfs);
        return ms.ToArray();
    }

    [Fact]
    public void Archive_names_are_read_lower_case_with_backslashes()
    {
        using var s = new Sandbox();
        var p = Path.Combine(s.Dir, "x.ba2");
        File.WriteAllBytes(p, Ba2("GNRL", "Scripts/Foo/Bar.pex", "meshes\\A\\B.nif"));
        Assert.Equal(new[] { "scripts\\foo\\bar.pex", "meshes\\a\\b.nif" }, Ba2Names.Read(p));
        Assert.Equal("GNRL", Ba2Names.Type(p));
    }

    [Fact]
    public void A_damaged_archive_is_refused_with_its_name()
    {
        using var s = new Sandbox();
        var p = Path.Combine(s.Dir, "bad.ba2");
        var bytes = Ba2("GNRL", "scripts\\a.pex");
        BitConverter.GetBytes(1_000_000u).CopyTo(bytes, 12);   // more files than the name table can hold
        File.WriteAllBytes(p, bytes);
        var e = Assert.Throws<InvalidDataException>(() => Ba2Names.Read(p));
        Assert.Contains("bad.ba2", e.Message);
        File.WriteAllBytes(p, Encoding.ASCII.GetBytes("not an archive at all, just text"));
        Assert.Contains("not a BA2", Assert.Throws<InvalidDataException>(() => Ba2Names.Read(p)).Message);
    }

    [Fact]
    public void Ini_archive_lists_are_read_and_a_later_ini_wins()
    {
        using var s = new Sandbox();
        var a = Path.Combine(s.Dir, "Starfield.ini");
        var b = Path.Combine(s.Dir, "StarfieldCustom.ini");
        File.WriteAllText(a, "[General]\nsResourceArchiveList=Ignored.ba2\n[Archive]\nsResourceArchiveList=One.ba2, Two.ba2\nsResourceStartUpArchiveList=Start.ba2\n");
        File.WriteAllText(b, "[Archive]\nSRESOURCEARCHIVELIST=Three.ba2\n");
        var got = AssetIndex.IniArchives(new[] { a, b, Path.Combine(s.Dir, "missing.ini") }).Select(x => (x.Key, x.Name)).ToList();
        Assert.Contains(("sResourceStartUpArchiveList", "Start.ba2"), got);
        Assert.Contains(("sResourceArchiveList", "Three.ba2"), got);
        Assert.DoesNotContain(got, x => x.Name is "One.ba2" or "Two.ba2" or "Ignored.ba2");
    }

    [Fact]
    public void Script_names_come_from_attached_scripts_fragments_and_aliases()
    {
        var q = new Quest(Mutagen.Bethesda.Plugins.FormKey.Factory("000800:SfmTest.esm"), StarfieldRelease.Starfield)
        {
            VirtualMachineAdapter = new QuestAdapter
            {
                Scripts = { new ScriptEntry { Name = "SfmTest:QuestScript" } },
                Script = new ScriptEntry { Name = "Fragments:Quests:QF_SfmTest_000800" },
                Aliases = { new QuestFragmentAlias { Scripts = { new ScriptEntry { Name = "SfmTest:AliasScript" } } } },
            },
        };
        Assert.Equal(new[] { "Fragments:Quests:QF_SfmTest_000800", "SfmTest:AliasScript", "SfmTest:QuestScript" }, Assets.Scripts(q).Order());
        Assert.Equal("scripts\\fragments\\quests\\qf_sfmtest_000800.pex", Assets.PexPath("Fragments:Quests:QF_SfmTest_000800"));
    }

    static void AttachScript(StarfieldMod m, string script) =>
        m.MiscItems.First().VirtualMachineAdapter = new VirtualMachineAdapter { Scripts = { new ScriptEntry { Name = script } } };

    [Fact]
    public void A_missing_script_is_an_error_and_a_loose_pex_clears_it()
    {
        using var w = new World();
        var p = w.Build("noscript", m => AttachScript(m, "SfmTest:MissingScript"));
        Expect.Finding(w.Check(p), "ERROR", "assets", "script SfmTest:MissingScript (scripts\\sfmtest\\missingscript.pex) is in no loose file or loaded archive");
        Directory.CreateDirectory(Path.Combine(w.Dir, "Scripts", "SfmTest"));
        File.WriteAllBytes(Path.Combine(w.Dir, "Scripts", "SfmTest", "MissingScript.pex"), new byte[4]);
        Expect.No(w.Check(p), "assets");
    }

    [Fact]
    public void A_script_in_the_plugins_own_archive_or_a_masters_archive_is_found()
    {
        using var w = new World();
        var p = w.Build("ownba2", m => AttachScript(m, "SfmTest:Packed"));
        File.WriteAllBytes(Path.Combine(w.Dir, "ownba2 - Main.ba2"), Ba2("GNRL", "scripts\\sfmtest\\packed.pex"));
        Expect.No(w.Check(p), "assets");
        var q = w.Build("fromMaster", m => AttachScript(m, "SfmTest:FromMaster"));
        File.WriteAllBytes(Path.Combine(w.Dir, "TestFull - Main.ba2"), Ba2("GNRL", "Scripts/SfmTest/FromMaster.pex"));
        Expect.No(w.Check(q), "assets");
    }

    [Fact]
    public void An_archive_an_ini_lists_counts_even_without_a_plugin_of_its_name()
    {
        using var w = new World();
        var p = w.Build("iniba2", m => AttachScript(m, "SfmTest:FromIni"));
        File.WriteAllBytes(Path.Combine(w.Dir, "Shared Stuff.ba2"), Ba2("GNRL", "scripts\\sfmtest\\fromini.pex"));
        Expect.Finding(w.Check(p), "ERROR", "assets", "SfmTest:FromIni");   // not named after any plugin: not loaded
        var ini = Path.Combine(w.Dir, "Extra.ini");
        File.WriteAllText(ini, "[Archive]\nsResourceArchiveList2=Shared Stuff.ba2\n");
        Expect.No(w.Check(p, o => o.Inis.Add(ini)), "assets");
    }

    [Fact]
    public void Another_languages_voice_archive_and_unrelated_archives_do_not_count()
    {
        using var w = new World();
        var p = w.Build("voices", m => AttachScript(m, "SfmTest:InVoices"));
        File.WriteAllBytes(Path.Combine(w.Dir, "voices - Voices_de.ba2"), Ba2("GNRL", "scripts\\sfmtest\\invoices.pex"));
        File.WriteAllBytes(Path.Combine(w.Dir, "SomeOtherMod - Main.ba2"), Ba2("GNRL", "scripts\\sfmtest\\invoices.pex"));
        Expect.Finding(w.Check(p), "ERROR", "assets", "SfmTest:InVoices");
        Expect.No(w.Check(p, o => o.Language = "de"), "assets");
    }

    // check mode: the master version comes from the masters' link cache; a model and a script the master record
    // already names are inherited, the override's own new script is still reported
    [Fact]
    public void Missing_paths_the_master_version_names_are_inherited_and_the_overrides_own_are_reported()
    {
        var master = new StarfieldMod(Mutagen.Bethesda.Plugins.ModKey.FromFileName("SfmMaster.esm"), StarfieldRelease.Starfield);
        var orig = master.MiscItems.AddNew("M_Misc");
        orig.VirtualMachineAdapter = new VirtualMachineAdapter { Scripts = { new ScriptEntry { Name = "Vanilla:Gone" } } };
        orig.Model = new Model { File = new AssetLink<StarfieldModelAssetType>("Vanilla\\Gone.nif") };
        var mod = new StarfieldMod(Mutagen.Bethesda.Plugins.ModKey.FromFileName("SfmOver.esm"), StarfieldRelease.Starfield);
        var ov = mod.MiscItems.GetOrAddAsOverride(orig);
        ov.VirtualMachineAdapter!.Scripts.Add(new ScriptEntry { Name = "Mod:Own" });
        using var s = new Sandbox();
        var index = AssetIndex.Build(Array.Empty<string>(), new[] { s.Dir }, Array.Empty<string>(), "en");
        var cache = master.ToImmutableLinkCache();
        var (scripts, models, missing, inherited) = Assets.Find(mod, index, r => r.FormKey.ToString(),
            r => cache.TryResolve(r.FormKey, r.Registration.GetterType, out var o) ? o : null);
        Assert.Equal((2, 1, 2), (scripts, models, inherited));
        Assert.Equal("scripts\\mod\\own.pex", Assert.Single(missing).Path);
        // without the master version (or for the plugin's own new records) everything missing is reported
        Assert.Equal(3, Assets.Find(mod, index, r => r.FormKey.ToString()).Missing.Count);
    }

    [Fact]
    public void A_missing_model_is_a_warning_and_no_assets_turns_the_check_off()
    {
        using var w = new World();
        var p = w.Build("nomodel", m => m.MiscItems.First().Model = new Model { File = new AssetLink<StarfieldModelAssetType>("SfmTest\\Thing.nif") });
        Expect.Finding(w.Check(p), "WARNING", "assets", "model meshes\\sfmtest\\thing.nif is in no loose file or loaded archive");
        Expect.No(w.Check(p, o => o.NoAssets = true), "assets");
        File.WriteAllBytes(Path.Combine(w.Dir, "TestMedium - Main.ba2"), Ba2("GNRL", "meshes\\SfmTest\\Thing.nif"));
        Expect.No(w.Check(p), "assets");
    }

    // 0.6.0 part 2 item 2: the archives we count for the limit are the ones Mutagen says apply to each plugin
    // (Archive.GetApplicableArchivePaths per ModKey), less other languages' voice archives, which the game doesn't load.
    [Fact]
    public void The_archives_counted_match_mutagens_applicable_archives()
    {
        // Mutagen's GetApplicableArchivePaths reads the game's Plugins.txt folder under LocalAppData, which it can't find
        // on Linux or macOS (CI 2026-11-19), so the comparison runs on Windows only; our own count is tested elsewhere.
        if (!OperatingSystem.IsWindows()) return;
        using var s = new Sandbox();
        var data = s.Sub("Data");
        var gnrl = Ba2("GNRL", "meshes\\a.nif");
        var dx10 = Ba2("DX10", "textures\\a.dds");
        foreach (var n in new[] { "Alpha - Main.ba2", "Alpha - Voices_en.ba2", "Alpha - Voices_de.ba2", "Beta - Main.ba2",
                                  "Beta Two - Main.ba2", "Gamma - Main.ba2", "Alphabet - Main.ba2" })
            File.WriteAllBytes(Path.Combine(data, n), gnrl);
        foreach (var n in new[] { "Alpha - Textures.ba2", "Beta - Textures01.ba2" })
            File.WriteAllBytes(Path.Combine(data, n), dx10);
        var plugins = new[] { "Alpha.esm", "Beta.esm", "Beta Two.esp" };   // Gamma and Alphabet aren't active

        var index = AssetIndex.Build(plugins, new[] { data }, Array.Empty<string>(), "en", names: false);
        var ours = index.Gnrl.Concat(index.Dx10).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var mutagen = plugins.SelectMany(p => Mutagen.Bethesda.Archives.Archive.GetApplicableArchivePaths(
                Mutagen.Bethesda.GameRelease.Starfield, new Noggog.DirectoryPath(data), Mutagen.Bethesda.Plugins.ModKey.FromFileName(p)))
            .Select(f => Path.GetFileName(f.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

        Assert.Equal(mutagen.Where(n => !n.Contains("Voices_de")).ToList(), ours);
        Assert.Contains("Alpha - Voices_de.ba2", mutagen);   // Mutagen lists it; the game (English) doesn't load it
        Assert.Equal(new[] { "Alpha - Main.ba2", "Alpha - Voices_en.ba2", "Beta - Main.ba2", "Beta Two - Main.ba2" }, index.Gnrl.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(new[] { "Alpha - Textures.ba2", "Beta - Textures01.ba2" }, index.Dx10.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Empty(index.Loaded);   // names: false reads only the headers
    }

    [Fact]
    public void A_texture_archive_is_skipped_and_a_damaged_archive_is_a_warning()
    {
        using var w = new World();
        var p = w.Build("arch", m => AttachScript(m, "SfmTest:InTextures"));
        File.WriteAllBytes(Path.Combine(w.Dir, "arch - Textures.ba2"), Ba2("DX10", "scripts\\sfmtest\\intextures.pex"));
        File.WriteAllBytes(Path.Combine(w.Dir, "arch - Main.ba2"), Encoding.ASCII.GetBytes("damaged, not BTDX at all......"));
        var r = w.Check(p);
        Expect.Finding(r, "ERROR", "assets", "SfmTest:InTextures");
        Expect.Finding(r, "WARNING", "assets", "archive arch - Main.ba2 could not be read");
    }

    // code review 0.6.0: in check mode a failure in the assets step costs only that step (one WARNING), not the
    // whole check (CANNOT CHECK, exit 2)
    [Fact]
    public void A_failure_in_the_assets_step_is_one_warning_in_check_mode()
    {
        using var w = new World();
        var p = w.Build("assetsfail", m => AttachScript(m, "SfmTest:Whatever"));
        Result r;
        try
        {
            Assets.AfterIndex = () => throw new IOException("made-up disk error");
            r = w.Check(p);
        }
        finally { Assets.AfterIndex = null; }
        Expect.Finding(r, "WARNING", "assets", "the assets check stopped (IOException: made-up disk error)");
        Assert.False(r.CannotCheckLinks);
        Assert.DoesNotContain(r.Findings, f => f.Severity == "ERROR");
    }

    // code review 0.6.0: a path outside Data (rooted, or with "..") never counts as found, even if the file exists
    [Fact]
    public void A_path_outside_data_never_counts_as_found()
    {
        using var s = new Sandbox();
        var data = s.Sub("Data");
        File.WriteAllBytes(Path.Combine(s.Dir, "outside.nif"), new byte[4]);
        File.WriteAllBytes(Path.Combine(data, "inside.nif"), new byte[4]);
        var index = AssetIndex.Build(Array.Empty<string>(), new[] { data }, Array.Empty<string>(), "en");
        Assert.True(index.Has("inside.nif"));
        Assert.False(index.Has("..\\outside.nif"));
        Assert.False(index.Has("meshes/../../outside.nif"));
        Assert.False(index.Has(Path.Combine(s.Dir, "outside.nif")));
    }
}
