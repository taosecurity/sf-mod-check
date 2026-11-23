// The 0.14.0 texture and material checks: the .nif string table, the .mat links, the material database IDs, and the
// check on fixture plugins with made-up .nif, .mat and archive files built here.
using System.Text;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Starfield;
using Mutagen.Bethesda.Starfield.Assets;
using Xunit;

namespace SfModCheck.Tests;

public class AssetFilesTests
{
    /// <summary>A Starfield-style .nif with no blocks whose string table holds these strings.</summary>
    internal static byte[] Nif(params string[] strings)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("Gamebryo File Format, Version 20.2.0.7\n"));
        w.Write(0x14020007u); w.Write((byte)1); w.Write(12u);
        w.Write(0u);                                   // blocks
        w.Write(172u);                                 // Bethesda stream version
        w.Write((byte)1); w.Write((byte)0);            // author ""
        w.Write(0u);                                   // the value after the author (version > 130)
        w.Write((byte)1); w.Write((byte)0);            // export script
        w.Write((byte)1); w.Write((byte)0);            // max filepath
        w.Write((ushort)0);                            // block types
        w.Write((uint)strings.Length);
        w.Write((uint)strings.DefaultIfEmpty("").Max(s => s.Length));
        foreach (var s in strings) { var b = Encoding.UTF8.GetBytes(s); w.Write((uint)b.Length); w.Write(b); }
        w.Write(0u);                                   // groups
        return ms.ToArray();
    }

    /// <summary>A .mat in the shape the Creation Kit writes: its own name, an Import list, a Parent and texture files.</summary>
    internal static byte[] Mat(string self, string[] imports, params string[] textures)
    {
        static string J(string s) => System.Text.Json.JsonSerializer.Serialize(s);
        var tex = string.Join(",\n", textures.Select((t, i) => i % 2 == 0
            ? $"{{ \"Data\": {{ \"FileName\": {J(t)} }}, \"Type\": \"BSMaterial::TextureFile\" }}"
            : $"{{ \"Data\": {{ \"File\": {J(t)}, \"Name\": \"x\" }}, \"Type\": \"BSMaterial::MRTextureFile\" }}"));
        return Encoding.UTF8.GetBytes($$"""
            {
              "Filename" : {{J(self)}},
              "Import" : [ {{string.Join(", ", imports.Select(J))}} ],
              "Objects" : [ { "Components" : [ {{tex}} ], "ID" : "res:00000001:00000002:00000003", "Parent" : "res:778E006F:0005D695:A495A182" } ]
            }
            """);
    }

    /// <summary>A general (GNRL, version 2) BA2 whose files are stored as is, which Mutagen's archive reader can open.</summary>
    internal static byte[] Gnrl(params (string Name, byte[] Data)[] files)
    {
        long dataAt = 32 + 36L * files.Length;
        long nameAt = dataAt + files.Sum(f => (long)f.Data.Length);
        var o = new MemoryStream();
        var w = new BinaryWriter(o);
        w.Write(Encoding.ASCII.GetBytes("BTDX")); w.Write(2u); w.Write(Encoding.ASCII.GetBytes("GNRL")); w.Write((uint)files.Length); w.Write((ulong)nameAt);
        w.Write(1u); w.Write(0u);                      // version 2's 8 more bytes
        long at = dataAt;
        foreach (var f in files)
        {
            var ext = Path.GetExtension(f.Name).TrimStart('.');
            w.Write(0u); w.Write(Encoding.ASCII.GetBytes((ext + "\0\0\0\0")[..4])); w.Write(0u); w.Write(0x100100u);
            w.Write((ulong)at); w.Write(0u); w.Write((uint)f.Data.Length); w.Write(0xBAADF00Du);
            at += f.Data.Length;
        }
        foreach (var f in files) w.Write(f.Data);
        foreach (var f in files) { var b = Encoding.UTF8.GetBytes(f.Name); w.Write((ushort)b.Length); w.Write(b); }
        w.Flush();
        return o.ToArray();
    }

    /// <summary>A stand-in for the game's material database: the IDs of these material paths among other bytes.</summary>
    internal static byte[] Cdb(params string[] materials)
    {
        var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("BETH....made-up database...."));
        foreach (var m in materials)
        {
            var id = AssetFiles.MaterialId(m);
            ms.Write(BitConverter.GetBytes((uint)id));            // file CRC
            ms.Write("mat\0"u8);
            ms.Write(BitConverter.GetBytes((uint)(id >> 32)));    // folder CRC
            ms.Write(new byte[] { 1, 2, 3 });
        }
        return ms.ToArray();
    }

    static void Put(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    [Fact]
    public void A_nif_names_its_materials_in_its_string_table()
    {
        var got = AssetFiles.NifMaterials(Nif("Root", "Data/Materials/SfmTest/A.MAT", @"materials\sfmtest\b.mat", "SfmTest\\C.mat", "   ", "meshes\\x.mesh"));
        Assert.Equal(new[] { @"materials\sfmtest\a.mat", @"materials\sfmtest\b.mat", @"materials\sfmtest\c.mat" }, got);
        var cut = Nif("materials\\a.mat")[..60];
        Assert.Throws<InvalidDataException>(() => AssetFiles.NifMaterials(cut));
        Assert.Throws<InvalidDataException>(() => AssetFiles.NifMaterials(Encoding.ASCII.GetBytes("not a model at all")));
    }

    [Fact]
    public void A_mat_names_textures_and_parents_but_not_itself()
    {
        var (tex, parents) = AssetFiles.MatLinks(Mat("MATERIALS\\SfmTest\\Self.mat", ["Data\\MATERIALS\\SfmTest\\Parent.mat"],
            "Data\\Textures\\SfmTest\\A_color.dds", "Data/Textures/SfmTest/A_normal.DDS"));
        Assert.Equal(new[] { @"textures\sfmtest\a_color.dds", @"textures\sfmtest\a_normal.dds" }, tex);
        Assert.Equal(new[] { @"materials\sfmtest\parent.mat" }, parents);   // "res:" parents are internal IDs
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => AssetFiles.MatLinks(Encoding.UTF8.GetBytes("{ \"Import\": [ ")));
    }

    [Fact]
    public void Material_ids_follow_the_games_crc_and_are_found_in_a_database()
    {
        Assert.Equal(AssetFiles.Crc("materials\\sfmtest"), AssetFiles.Crc("Materials/SfmTest"));
        Assert.Equal(0u, AssetFiles.Crc(""));
        Assert.Equal(AssetFiles.MaterialId(@"materials\sfmtest\a.mat"), AssetFiles.MaterialId("Materials/SfmTest/A.mat"));
        var ids = AssetFiles.CdbIds(Cdb(@"materials\sfmtest\a.mat", @"materials\other\b.mat"));
        Assert.Contains(AssetFiles.MaterialId(@"materials\sfmtest\a.mat"), ids);
        Assert.Contains(AssetFiles.MaterialId(@"materials\other\b.mat"), ids);
        Assert.DoesNotContain(AssetFiles.MaterialId(@"materials\sfmtest\c.mat"), ids);
    }

    [Fact]
    public void A_test_built_archive_is_read_by_mutagens_reader()
    {
        using var s = new Sandbox();
        var p = Path.Combine(s.Dir, "x.ba2");
        File.WriteAllBytes(p, Gnrl(("Meshes/SfmTest/A.nif", Nif("materials\\a.mat")), ("materials\\sfmtest\\a.mat", Encoding.UTF8.GetBytes("{}"))));
        Assert.Equal(Encoding.UTF8.GetBytes("{}"), AssetFiles.FromArchive(p, @"materials\sfmtest\a.mat"));
        Assert.Null(AssetFiles.FromArchive(p, @"materials\sfmtest\none.mat"));
        Assert.Equal(new[] { @"meshes\sfmtest\a.nif", @"materials\sfmtest\a.mat" }, AssetFiles.ArchiveModelFiles(p).Select(x => x.Name));
    }

    static Model ModelOf(string path) => new() { File = new AssetLink<StarfieldModelAssetType>(path) };

    /// <summary>The game's material database in TestFull's own archive, holding these vanilla-style materials.</summary>
    static void GameMaterials(World w, params string[] materials) =>
        File.WriteAllBytes(Path.Combine(w.Dir, "TestFull - Materials.ba2"), Gnrl((AssetFiles.CdbPath, Cdb(materials))));

    [Fact]
    public void Textures_a_loose_material_names_are_found_loose_or_in_a_texture_archive_whatever_their_case()
    {
        using var w = new World();
        GameMaterials(w, @"materials\vanilla\base.mat");
        var p = w.Build("loosemat", m => m.MiscItems.First().Model = ModelOf("SfmTest\\Thing.nif"));
        Put(Path.Combine(w.Dir, "Meshes", "SfmTest", "Thing.nif"), Nif("Materials\\SfmTest\\Thing.mat"));
        Put(Path.Combine(w.Dir, "Materials", "SfmTest", "Thing.mat"), Mat("MATERIALS\\SfmTest\\Thing.mat", ["Data\\Materials\\Vanilla\\Base.mat"],
            "Data\\Textures\\SfmTest\\Thing_color.dds", "Data/Textures/SfmTest/THING_normal.DDS"));
        var r = w.Check(p);
        Expect.Finding(r, "WARNING", "assets", @"texture textures\sfmtest\thing_color.dds is in no loose file or loaded archive; used by materials\sfmtest\thing.mat");
        Expect.Finding(r, "WARNING", "assets", @"texture textures\sfmtest\thing_normal.dds");
        // one loose (other case on disk), one in a texture archive that loads with a master
        Put(Path.Combine(w.Dir, "textures", "sfmtest", "THING_COLOR.dds"), new byte[4]);
        File.WriteAllBytes(Path.Combine(w.Dir, "TestFull - Textures.ba2"), AssetsTests.Ba2("DX10", "Textures/SfmTest/Thing_Normal.dds"));
        Expect.No(w.Check(p), "assets");
    }

    [Fact]
    public void A_texture_in_an_archive_that_does_not_load_is_still_missing()
    {
        using var w = new World();
        GameMaterials(w);
        var p = w.Build("notloaded", m => m.MiscItems.First().Model = ModelOf("SfmTest\\Thing.nif"));
        Put(Path.Combine(w.Dir, "Meshes", "SfmTest", "Thing.nif"), Nif("materials\\sfmtest\\thing.mat"));
        Put(Path.Combine(w.Dir, "Materials", "SfmTest", "Thing.mat"), Mat("x", [], "Data\\Textures\\SfmTest\\Thing_color.dds"));
        File.WriteAllBytes(Path.Combine(w.Dir, "SomeOtherMod - Textures.ba2"), AssetsTests.Ba2("DX10", "textures\\sfmtest\\thing_color.dds"));
        Expect.Finding(w.Check(p), "WARNING", "assets", @"texture textures\sfmtest\thing_color.dds");
    }

    [Fact]
    public void A_model_and_material_in_the_plugins_own_archive_are_read_from_it()
    {
        using var w = new World();
        GameMaterials(w);
        var p = w.Build("packed", m => m.MiscItems.First().Model = ModelOf("SfmTest\\Packed.nif"));
        File.WriteAllBytes(Path.Combine(w.Dir, "packed - Main.ba2"), Gnrl(
            ("meshes\\sfmtest\\packed.nif", Nif("materials\\sfmtest\\packed.mat")),
            ("meshes\\sfmtest\\unused.nif", Nif("materials\\sfmtest\\nowhere.mat")),     // no record names it: still read
            ("materials\\sfmtest\\packed.mat", Mat("x", [], "Data\\Textures\\SfmTest\\Packed_color.dds"))));
        var r = w.Check(p);
        Expect.Finding(r, "WARNING", "assets", @"texture textures\sfmtest\packed_color.dds is in no loose file or loaded archive; used by materials\sfmtest\packed.mat");
        Expect.Finding(r, "WARNING", "assets", @"material materials\sfmtest\nowhere.mat is in no loose file or loaded archive; used by meshes\sfmtest\unused.nif");
        File.WriteAllBytes(Path.Combine(w.Dir, "packed - Textures.ba2"), AssetsTests.Ba2("DX10", "textures\\sfmtest\\packed_color.dds"));
        Expect.Finding(w.Check(p), "WARNING", "assets", "nowhere.mat");
        Assert.DoesNotContain(w.Check(p).Findings, f => f.Text.Contains("packed_color"));
    }

    [Fact]
    public void A_vanilla_style_material_counts_when_the_games_database_holds_it()
    {
        using var w = new World();
        var p = w.Build("vanillamat", m => m.MiscItems.First().Model = ModelOf("SfmTest\\Thing.nif"));
        Put(Path.Combine(w.Dir, "Meshes", "SfmTest", "Thing.nif"), Nif("Materials\\Vanilla\\Rock01.mat", "materials\\vanilla\\nosuch.mat"));
        // no database at all: one WARNING says the materials couldn't be checked, none is called missing
        var r = w.Check(p);
        Expect.Finding(r, "WARNING", "assets", "2 material path(s) are neither loose files nor in a loaded archive, and no material database (.cdb");
        Assert.DoesNotContain(r.Findings, f => f.Text.StartsWith("material "));
        GameMaterials(w, @"materials\vanilla\rock01.mat");
        r = w.Check(p);
        Expect.Finding(r, "WARNING", "assets", @"material materials\vanilla\nosuch.mat is in no loose file or loaded archive; used by meshes\sfmtest\thing.nif");
        Assert.DoesNotContain(r.Findings, f => f.Text.Contains("rock01"));
    }

    [Fact]
    public void Every_material_database_counts_the_games_a_plugins_own_and_a_loose_one()
    {
        using var w = new World();
        var p = w.Build("twodb", m => m.MiscItems.First().Model = ModelOf("SfmTest\\Thing.nif"));
        Put(Path.Combine(w.Dir, "Meshes", "SfmTest", "Thing.nif"),
            Nif("materials\\vanilla\\rock01.mat", "materials\\dlc\\dlconly.mat", "materials\\loose\\looseonly.mat", "materials\\vanilla\\nosuch.mat"));
        GameMaterials(w, @"materials\vanilla\rock01.mat");
        var r = w.Check(p);
        Expect.Finding(r, "WARNING", "assets", @"material materials\dlc\dlconly.mat is in no loose file or loaded archive");
        // a second database, in the plugin's own archive under another folder, holds a material only it knows
        File.WriteAllBytes(Path.Combine(w.Dir, "twodb - Main.ba2"), Gnrl((@"materials\creations\twodb.esm\materialsbeta.cdb", Cdb(@"materials\dlc\dlconly.mat"))));
        // and a loose one (other case on disk) a third
        Put(Path.Combine(w.Dir, "Materials", "KgcShip", "MaterialsBeta.CDB"), Cdb(@"materials\loose\looseonly.mat"));
        r = w.Check(p);
        Expect.Finding(r, "WARNING", "assets", @"material materials\vanilla\nosuch.mat is in no loose file or loaded archive");
        Assert.DoesNotContain(r.Findings, f => f.Text.Contains("dlconly") || f.Text.Contains("looseonly") || f.Text.Contains("rock01"));
        // a database in an archive that doesn't load doesn't count
        File.Delete(Path.Combine(w.Dir, "twodb - Main.ba2"));
        File.WriteAllBytes(Path.Combine(w.Dir, "SomeOtherMod - Main.ba2"), Gnrl((@"materials\other\materialsbeta.cdb", Cdb(@"materials\dlc\dlconly.mat"))));
        Expect.Finding(w.Check(p), "WARNING", "assets", @"material materials\dlc\dlconly.mat");
    }

    [Fact]
    public void A_lod_mesh_in_the_plugins_own_archive_is_read_only_when_a_record_names_it()
    {
        using var w = new World();
        GameMaterials(w);
        byte[] Archive() => Gnrl(
            ("meshes\\sfmtest\\thing.nif", Nif()),
            ("meshes\\lod\\generated\\world\\cell0.nif", Nif("materials\\lod\\generated\\world\\cell0.mat")),
            ("meshes\\sfmtest\\spare.nif", Nif("materials\\sfmtest\\spare.mat")));
        var p = w.Build("lodmod", m => m.MiscItems.First().Model = ModelOf("SfmTest\\Thing.nif"));
        File.WriteAllBytes(Path.Combine(w.Dir, "lodmod - Main.ba2"), Archive());
        var r = w.Check(p);
        Expect.Finding(r, "WARNING", "assets", @"material materials\sfmtest\spare.mat");
        Assert.DoesNotContain(r.Findings, f => f.Text.Contains("cell0"));
        // a record that names the LOD mesh gets it read
        p = w.Build("lodnamed", m => m.MiscItems.First().Model = ModelOf("lod\\generated\\world\\cell0.nif"));
        File.WriteAllBytes(Path.Combine(w.Dir, "lodnamed - Main.ba2"), Archive());
        Expect.Finding(w.Check(p), "WARNING", "assets", @"material materials\lod\generated\world\cell0.mat");
    }

    [Fact]
    public void Release_counts_a_material_database_in_the_zips_own_archive()
    {
        using var s = new Sandbox();
        var data = s.Sub("Data");
        ReleaseNeedsTests.Write(data, "Starfield.esm", (_, _) => { });
        File.WriteAllBytes(Path.Combine(data, "Starfield - Materials.ba2"), Gnrl((AssetFiles.CdbPath, Cdb(@"materials\vanilla\rock01.mat"))));
        var pkg = s.Sub("CdbTest");
        ReleaseNeedsTests.Write(pkg, "CdbTest.esm", (_, misc) => misc.Model = ModelOf("CdbTest\\Thing.nif"));
        Put(Path.Combine(pkg, "Meshes", "CdbTest", "Thing.nif"), Nif("materials\\vanilla\\rock01.mat", "materials\\cdbtest\\own.mat", "materials\\cdbtest\\gone.mat"));
        File.WriteAllBytes(Path.Combine(pkg, "CdbTest - Main.ba2"), Gnrl((@"materials\creations\cdbtest.esm\materialsbeta.cdb", Cdb(@"materials\cdbtest\own.mat"))));
        var f = ReleaseNeeds.Run(ReleaseNeedsTests.Context(s, pkg, data)).ToList();
        var mat = f.Where(x => x.Rule == "material-missing").ToList();
        Assert.True(mat.Count == 1, ReleaseNeedsTests.List(f));
        Assert.Contains(@"the material materials\cdbtest\gone.mat", mat[0].Text);
    }

    [Fact]
    public void A_damaged_nif_or_mat_is_a_warning_not_a_crash()
    {
        using var w = new World();
        GameMaterials(w);
        var p = w.Build("damaged", m => m.MiscItems.First().Model = ModelOf("SfmTest\\Broken.nif"));
        Put(Path.Combine(w.Dir, "Meshes", "SfmTest", "Broken.nif"), Nif("materials\\sfmtest\\broken.mat")[..50]);
        Expect.Finding(w.Check(p), "WARNING", "assets", @"couldn't read meshes\sfmtest\broken.nif (its header ends early");
        Put(Path.Combine(w.Dir, "Meshes", "SfmTest", "Broken.nif"), Nif("materials\\sfmtest\\broken.mat"));
        Put(Path.Combine(w.Dir, "Materials", "SfmTest", "Broken.mat"), Encoding.UTF8.GetBytes("{ \"Import\": [ "));
        Expect.Finding(w.Check(p), "WARNING", "assets", @"couldn't read materials\sfmtest\broken.mat");
    }

    [Fact]
    public void Release_reports_textures_and_materials_the_zip_and_the_game_do_not_provide()
    {
        using var s = new Sandbox();
        var data = s.Sub("Data");
        ReleaseNeedsTests.Write(data, "Starfield.esm", (_, _) => { });
        File.WriteAllBytes(Path.Combine(data, "Starfield - Materials.ba2"), Gnrl((AssetFiles.CdbPath, Cdb(@"materials\vanilla\rock01.mat"))));
        File.WriteAllBytes(Path.Combine(data, "Starfield - Textures01.ba2"), AssetsTests.Ba2("DX10", "textures\\vanilla\\rock01_color.dds"));
        // a loose copy in Data doesn't count for a release
        Put(Path.Combine(data, "Textures", "RnTest", "InData_color.dds"), new byte[4]);
        var pkg = s.Sub("RnTest");
        ReleaseNeedsTests.Write(pkg, "RnTest.esm", (_, misc) => misc.Model = ModelOf("RnTest\\Thing.nif"));
        Put(Path.Combine(pkg, "Meshes", "RnTest", "Thing.nif"), Nif("materials\\rntest\\thing.mat", "materials\\vanilla\\rock01.mat", "materials\\vanilla\\gone.mat"));
        File.WriteAllBytes(Path.Combine(pkg, "RnTest - Main.ba2"), Gnrl(("materials\\rntest\\thing.mat",
            Mat("x", [], "Data\\Textures\\Vanilla\\Rock01_color.dds", "Data\\Textures\\RnTest\\InData_color.dds", "Data\\Textures\\RnTest\\Packed_color.dds"))));
        File.WriteAllBytes(Path.Combine(pkg, "RnTest - Textures.ba2"), AssetsTests.Ba2("DX10", "textures\\rntest\\packed_color.dds"));

        var ctx = ReleaseNeedsTests.Context(s, pkg, data);
        var f = ReleaseNeeds.Run(ctx).ToList();
        var tex = f.Where(x => x.Rule == "texture-missing").ToList();
        var mat = f.Where(x => x.Rule == "material-missing").ToList();
        Assert.True(tex.Count == 1 && mat.Count == 1, ReleaseNeedsTests.List(f));
        Assert.Equal(Level.Warning, tex[0].Level);
        Assert.Contains(@"the texture textures\rntest\indata_color.dds", tex[0].Text);
        Assert.Contains(@"used by materials\rntest\thing.mat", tex[0].Text);
        Assert.Contains(@"the material materials\vanilla\gone.mat", mat[0].Text);
        Assert.Contains("isn't in any of their material databases", mat[0].Text);

        // with --dmmdeps given, DMMDeps reports these files, so the two rules stay quiet (the DMMDeps run itself fails
        // here, which its own rule reports)
        ctx.Options.DmmDeps = Path.Combine(s.Dir, "no-dmmdeps.exe");
        f = ReleaseNeeds.Run(ctx).ToList();
        Assert.DoesNotContain(f, x => x.Rule is "texture-missing" or "material-missing");
    }

    [Fact]
    public void A_mat_in_the_plugins_own_archive_that_nothing_names_is_still_read_for_its_textures()
    {
        using var w = new World();
        GameMaterials(w);
        var p = w.Build("matonly");
        File.WriteAllBytes(Path.Combine(w.Dir, "matonly - Main.ba2"), Gnrl(
            ("materials\\vanilla\\swap.mat", Mat("x", [], "Data\\Textures\\SfmTest\\Swap_color.dds"))));
        Expect.Finding(w.Check(p), "WARNING", "assets", @"texture textures\sfmtest\swap_color.dds is in no loose file or loaded archive; used by materials\vanilla\swap.mat");
    }

    [Fact]
    public void A_perk_icon_name_is_not_a_texture_file()
    {
        using var w = new World();
        var p = w.Build("perkicon", m =>
        {
            m.Perks.Add(new Perk(m) { EditorID = "SFM_Perk", PerkIcon = new AssetLink<StarfieldTextureAssetType>("patch_made_up") });
            m.Perks.Add(new Perk(m) { EditorID = "SFM_Perk2", PerkIcon = new AssetLink<StarfieldTextureAssetType>("SfmTest\\Icon.dds") });
        });
        var r = w.Check(p);
        Assert.DoesNotContain(r.Findings, f => f.Text.Contains("patch_made_up"));
        Expect.Finding(r, "WARNING", "assets", @"texture textures\sfmtest\icon.dds is in no loose file or loaded archive; used by SFM_Perk2");
    }

    /// <summary>A made-up Wwise sound bank: a BKHD chunk of this version, then a HIRC chunk with one other object (an
    /// action, type 3) and one Sound object (type 2) per source, as (plugin ID, stream type, source ID).</summary>
    internal static byte[] Bnk(uint version, params (uint Plugin, byte Stream, uint Source)[] sounds)
    {
        var hirc = new MemoryStream();
        var h = new BinaryWriter(hirc);
        h.Write((uint)sounds.Length + 1);
        h.Write((byte)3); h.Write(6u); h.Write(99u); h.Write((ushort)0);
        uint id = 1000;
        foreach (var (plugin, stream, source) in sounds)
        {
            h.Write((byte)2); h.Write(4u + 4 + 1 + 4 + 4 + 3);
            h.Write(id++); h.Write(plugin); h.Write(stream); h.Write(source); h.Write(1234u); h.Write(new byte[3]);
        }
        h.Flush();
        var o = new MemoryStream();
        var w = new BinaryWriter(o);
        w.Write(Encoding.ASCII.GetBytes("BKHD")); w.Write(8u); w.Write(version); w.Write(4242u);
        w.Write(Encoding.ASCII.GetBytes("HIRC")); w.Write((uint)hirc.Length); w.Write(hirc.ToArray());
        w.Flush();
        return o.ToArray();
    }

    const uint Codec = 0x00040001, Generated = 0x00650002;

    [Fact]
    public void A_bank_lists_its_streamed_media_sources_once()
    {
        var (v, ids) = AssetFiles.BankSources(Bnk(140, (Codec, 2, 11), (0x00010001, 1, 12), (Codec, 0, 13), (Generated, 2, 14), (Codec, 2, 11)));
        Assert.Equal(140u, v);
        Assert.Equal(new uint[] { 11, 12 }, ids);   // in the bank (13) and generated (14) need no file
        var (v2, ids2) = AssetFiles.BankSources(Bnk(150, (Codec, 2, 11)));
        Assert.Equal(150u, v2);
        Assert.Empty(ids2);                          // an unknown version isn't read past its header
        Assert.Throws<InvalidDataException>(() => AssetFiles.BankSources(Bnk(140, (Codec, 2, 11))[..30]));
        Assert.Throws<InvalidDataException>(() => AssetFiles.BankSources(Encoding.ASCII.GetBytes("RIFF not a bank at all")));
        Assert.Equal(@"sound\soundbanks\11.wem", AssetFiles.WemPath(11));
    }

    [Fact]
    public void A_banks_streamed_sounds_are_found_loose_or_in_a_loading_archive()
    {
        using var w = new World();
        var p = w.Build("snd");
        File.WriteAllBytes(Path.Combine(w.Dir, "snd - Main.ba2"), Gnrl(
            ("Sound/SoundBanks/777.bnk", Bnk(140, (Codec, 2, 101), (Codec, 2, 102), (Codec, 1, 103), (Codec, 2, 104), (Codec, 0, 105))),
            ("sound\\soundbanks\\102.wem", new byte[4])));
        Put(Path.Combine(w.Dir, "Sound", "SoundBanks", "101.WEM"), new byte[4]);                                   // loose, other case
        File.WriteAllBytes(Path.Combine(w.Dir, "TestFull - Sounds.ba2"), AssetsTests.Ba2("GNRL", "sound\\soundbanks\\103.wem"));   // a master's archive
        File.WriteAllBytes(Path.Combine(w.Dir, "SomeOtherMod - Main.ba2"), AssetsTests.Ba2("GNRL", "sound\\soundbanks\\104.wem")); // doesn't load
        var r = w.Check(p);
        Expect.Finding(r, "WARNING", "assets", @"sound sound\soundbanks\104.wem is in no loose file or loaded archive; used by sound\soundbanks\777.bnk");
        Assert.Single(r.Findings, f => f.Text.StartsWith("sound "));
        // a loose bank in Data isn't the plugin's own in check (every mod shares Data), so it isn't read
        Put(Path.Combine(w.Dir, "Sound", "SoundBanks", "888.bnk"), Bnk(140, (Codec, 2, 201)));
        Assert.DoesNotContain(w.Check(p).Findings, f => f.Text.Contains("201.wem"));
    }

    [Fact]
    public void A_damaged_bank_or_an_unknown_bank_version_is_a_warning_not_a_crash()
    {
        using var w = new World();
        var p = w.Build("badbank");
        File.WriteAllBytes(Path.Combine(w.Dir, "badbank - Main.ba2"), Gnrl(
            ("sound\\soundbanks\\1.bnk", Bnk(140, (Codec, 2, 101))[..30]),
            ("sound\\soundbanks\\2.bnk", Bnk(151, (Codec, 2, 102)))));
        var r = w.Check(p);
        Expect.Finding(r, "WARNING", "assets", @"couldn't read sound\soundbanks\1.bnk (its HIRC chunk runs past the end of the file), so the sounds it names weren't checked");
        Expect.Finding(r, "WARNING", "assets", @"sound\soundbanks\2.bnk is a Wwise sound bank of version 151, and only version 140");
        Assert.DoesNotContain(r.Findings, f => f.Text.Contains(".wem"));
    }

    [Fact]
    public void Release_reports_sounds_the_zip_and_the_game_do_not_provide_also_with_dmmdeps()
    {
        using var s = new Sandbox();
        var data = s.Sub("Data");
        ReleaseNeedsTests.Write(data, "Starfield.esm", (_, _) => { });
        File.WriteAllBytes(Path.Combine(data, "Starfield - WwiseSounds01.ba2"), AssetsTests.Ba2("GNRL", "sound\\soundbanks\\301.wem"));
        var pkg = s.Sub("RnSnd");
        ReleaseNeedsTests.Write(pkg, "RnSnd.esm", (_, _) => { });
        // a loose bank in the zip is the mod's own in release
        Put(Path.Combine(pkg, "Sound", "Soundbanks", "55.bnk"), Bnk(140, (Codec, 2, 301), (Codec, 2, 302), (Codec, 2, 303)));
        Put(Path.Combine(pkg, "sound", "soundbanks", "302.wem"), new byte[4]);
        Put(Path.Combine(pkg, "sound", "soundbanks", "56.bnk"), Bnk(160));
        var ctx = ReleaseNeedsTests.Context(s, pkg, data);
        foreach (var dmm in new[] { null, Path.Combine(s.Dir, "no-dmmdeps.exe") })
        {
            ctx.Options.DmmDeps = dmm;
            var f = ReleaseNeeds.Run(ctx).Where(x => x.Rule == "sound-missing").ToList();
            Assert.True(f.Count == 2, ReleaseNeedsTests.List(f));
            var miss = Assert.Single(f, x => x.Level == Level.Warning);
            Assert.Contains(@"the sound bank sound\soundbanks\55.bnk streams sound\soundbanks\303.wem", miss.Text);
            Assert.Contains(@"sound\soundbanks\56.bnk is a Wwise sound bank of version 160", Assert.Single(f, x => x.Level == Level.Note).Text);
        }
    }

    [Fact]
    public void Release_with_two_zips_reports_a_loose_bank_under_the_plugin_whose_zip_ships_it()
    {
        // 0.14.0 bug hunt: the first plugin took every zip's loose banks, so a main mod's bank was blamed on its add-on
        using var s = new Sandbox();
        var data = s.Sub("Data");
        ReleaseNeedsTests.Write(data, "Starfield.esm", (_, _) => { });
        var addon = s.Sub("RnAddon");
        ReleaseNeedsTests.Write(addon, "RnAddon.esm", (_, _) => { });
        var main = s.Sub("RnMain");
        ReleaseNeedsTests.Write(main, "RnMain.esm", (_, _) => { });
        Put(Path.Combine(main, "sound", "soundbanks", "77.bnk"), Bnk(140, (Codec, 2, 401)));
        var ctx = ReleaseNeedsTests.Context(s, addon, data);
        var zip = Path.Combine(s.Sub("zips"), "RnMain-0.1.0.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(main, zip);
        ctx.Options.Zips.Add(zip);
        ctx.Packages.Add(ReleaseCommand.Unpack(zip, Path.Combine(ctx.Work, "zip1")));
        var f = ReleaseNeeds.Run(ctx).Where(x => x.Rule == "sound-missing").ToList();
        var miss = Assert.Single(f);
        Assert.StartsWith(@"RnMain.esm: the sound bank sound\soundbanks\77.bnk streams sound\soundbanks\401.wem, which is in none of the zips' loose files", miss.Text);
    }

    [Fact]
    public void The_conflict_summary_counts_sounds_only_when_there_are_some()
    {
        var a = new Conflict.Analysis { AssetsChecked = true };
        a.MissingAssets.Add(new Conflict.AssetFinding("x.esm", new MissingAsset("WARNING", "model", @"meshes\a.nif", null, ["A"])));
        Assert.Equal("1 (0 scripts, 1 model)", Conflict.Report.AssetsCount(a));
        a.MissingAssets.Add(new Conflict.AssetFinding("x.esm", new MissingAsset("WARNING", "sound", @"sound\soundbanks\1.wem", null, [@"sound\soundbanks\2.bnk"])));
        a.MissingAssets.Add(new Conflict.AssetFinding("x.esm", new MissingAsset("WARNING", "sound", @"sound\soundbanks\3.wem", null, [@"sound\soundbanks\2.bnk"])));
        Assert.Equal("3 (0 scripts, 1 model, 2 sounds)", Conflict.Report.AssetsCount(a));
        // an unlisted kind still counts, so the parts add up to the total
        a.MissingAssets.Add(new Conflict.AssetFinding("x.esm", new MissingAsset("WARNING", "face", @"meshes\f.nif", null, ["A"])));
        Assert.Equal("4 (0 scripts, 1 model, 2 sounds, 1 other)", Conflict.Report.AssetsCount(a));
    }

    // ---- face data (0.14.0, D5) ----

    static readonly Mutagen.Bethesda.Plugins.FormKey HeadKey = Mutagen.Bethesda.Plugins.FormKey.Factory("000801:TestFull.esm");

    /// <summary>An NPC with a head part of the same plugin (so the links check stays quiet), or with none.</summary>
    static Npc FaceNpc(StarfieldMod m, string edid, bool face = true)
    {
        var n = new Npc(m) { EditorID = edid };
        if (face)
        {
            var head = m.HeadParts.FirstOrDefault();
            if (head == null) m.HeadParts.Add(head = new HeadPart(m) { EditorID = "SFM_Head" });
            n.HeadParts.Add(head.FormKey.ToLink<IHeadPartGetter>());
        }
        m.Npcs.Add(n);
        return n;
    }

    static void PutFace(string dir, string plugin, uint id, params string[] parts)
    {
        foreach (var part in parts)
            Put(Path.Combine(dir, (part == ".nif" ? Assets.FaceNif(plugin, id) : Assets.FaceTexture(plugin, id, part)).Replace('\\', Path.DirectorySeparatorChar)), new byte[4]);
    }

    static (StarfieldMod Mod, AssetIndex Index, Sandbox Box) FaceMod(string plugin, Action<StarfieldMod> add)
    {
        var mod = new StarfieldMod(Mutagen.Bethesda.Plugins.ModKey.FromFileName(plugin), StarfieldRelease.Starfield);
        add(mod);
        var s = new Sandbox();
        return (mod, AssetIndex.Build(Array.Empty<string>(), new[] { s.Dir }, Array.Empty<string>(), "en"), s);
    }

    static AssetIndex Index(Sandbox s) => AssetIndex.Build(Array.Empty<string>(), new[] { s.Dir }, Array.Empty<string>(), "en");

    [Fact]
    public void An_added_npc_with_no_face_file_at_all_is_a_note_unless_a_template_gives_its_traits()
    {
        uint id = 0;
        var (mod, index, s) = FaceMod("SfmAdd.esm", m =>
        {
            id = FaceNpc(m, "Add_Face").FormKey.ID;
            FaceNpc(m, "Add_NoFace", face: false);                                                  // nothing to bake
            FaceNpc(m, "Add_NullTints", face: false).Tints = null;                                  // no tint subrecords, as read from a file
            FaceNpc(m, "Add_Templated").UseTemplateActors = Npc.TemplateActorType.Traits;           // its template's face
        });
        using (s)
        {
            var nif = Assets.FaceNif("SfmAdd.esm", id);
            Assert.Equal($@"meshes\actors\character\facegendata\facegeom\sfmadd.esm\{id:x8}.nif", nif);
            // only release asks for faces
            Assert.Empty(Assets.Find(mod, index, r => r.EditorID ?? "").Missing);
            var f = Assert.Single(Assets.Find(mod, index, r => r.EditorID ?? "", faces: true).Missing);
            Assert.Equal(("NOTE", "face", nif), (f.Severity, f.Kind, f.Path));
            Assert.StartsWith($"NPC Add_Face has its own face but no baked face data: neither {nif} nor its four facecustomization textures", f.Text);
            // one of the five files (loose, other case) is enough: Bethesda's own NPCs have partial sets
            PutFace(s.Dir, "SFMADD.ESM", id, "_color");
            Assert.Empty(Assets.Find(mod, Index(s), r => r.EditorID ?? "", faces: true).Missing);
        }
    }

    [Fact]
    public void An_override_is_looked_up_for_face_data_only_when_it_changes_the_face()
    {
        var master = new StarfieldMod(Mutagen.Bethesda.Plugins.ModKey.FromFileName("SfmMaster.esm"), StarfieldRelease.Starfield);
        var orig = FaceNpc(master, "M_Npc");
        var mod = new StarfieldMod(Mutagen.Bethesda.Plugins.ModKey.FromFileName("SfmOver.esm"), StarfieldRelease.Starfield);
        orig.Tints = null;          // no tint subrecords in either version
        var ov = orig.DeepCopy();   // the override: same FormKey, a copy of the master's fields
        mod.Npcs.Add(ov);
        using var s = new Sandbox();
        var cache = master.ToImmutableLinkCache();
        Mutagen.Bethesda.Plugins.Records.IMajorRecordGetter? Orig(Mutagen.Bethesda.Plugins.Records.IMajorRecordGetter r) =>
            cache.TryResolve(r.FormKey, r.Registration.GetterType, out var o) ? o : null;
        // the same face as the master's: nothing to bake, even with no files at all
        Assert.Empty(Assets.Find(mod, Index(s), r => r.EditorID ?? "", Orig, faces: true).Missing);
        // tints added where the master has none is a changed face too
        ov.Tints = new Noggog.ExtendedList<NpcTint> { new NpcTint() };
        Assert.Equal("face", Assert.Single(Assets.Find(mod, Index(s), r => r.EditorID ?? "", Orig, faces: true).Missing).Kind);
        ov.Tints = null;
        // a changed face: its files go under the master's file name
        ov.HeadParts.Add(Mutagen.Bethesda.Plugins.FormKey.Factory("000802:TestFull.esm").ToLink<IHeadPartGetter>());
        var f = Assert.Single(Assets.Find(mod, Index(s), r => r.EditorID ?? "", Orig, faces: true).Missing);
        Assert.Equal(("NOTE", "face"), (f.Severity, f.Kind));
        Assert.Equal(Assets.FaceNif("sfmmaster.esm", orig.FormKey.ID), f.Path);
        PutFace(s.Dir, "sfmmaster.esm", orig.FormKey.ID, ".nif");
        Assert.Empty(Assets.Find(mod, Index(s), r => r.EditorID ?? "", Orig, faces: true).Missing);
    }

    [Fact]
    public void Face_files_of_a_medium_plugin_count_by_their_16_bit_or_24_bit_names()
    {
        // installed medium creations name their textures by the 16-bit local ID and their .nif by a 24-bit one with a
        // third byte Mutagen's FormKey doesn't carry (real data, 2026-11-23)
        foreach (var put in new[] { (Id: 0x1234u, Part: "_ao"), (Id: 0x011234u, Part: ".nif") })
        {
            var (mod, _, s) = FaceMod("SfmMed.esm", m =>
            {
                var npc = new Npc(Mutagen.Bethesda.Plugins.FormKey.Factory("001234:SfmMed.esm"), StarfieldRelease.Starfield) { EditorID = "Med_Npc" };
                npc.HeadParts.Add(HeadKey.ToLink<IHeadPartGetter>());
                m.Npcs.Add(npc);
            });
            using (s)
            {
                Assert.Single(Assets.Find(mod, Index(s), r => r.EditorID ?? "", faces: true).Missing);
                PutFace(s.Dir, "sfmmed.esm", put.Id, put.Part);
                Assert.Empty(Assets.Find(mod, Index(s), r => r.EditorID ?? "", faces: true).Missing);
            }
        }
    }

    [Fact]
    public void Check_does_not_look_for_face_data()
    {
        using var w = new World();
        var p = w.Build("face", m => FaceNpc(m, "SFM_Face"));
        var r = w.Check(p);
        Assert.DoesNotContain(r.Findings, f => f.Text.Contains("face"));
        Assert.DoesNotContain(r.Findings, f => f.Severity == "ERROR");
    }

    [Fact]
    public void Release_notes_npcs_with_no_face_data_also_with_dmmdeps()
    {
        using var s = new Sandbox();
        var data = s.Sub("Data");
        ReleaseNeedsTests.Write(data, "Starfield.esm", (_, _) => { });
        var pkg = s.Sub("RnFace");
        uint id = 0;
        ReleaseNeedsTests.Write(pkg, "RnFace.esm", (m, _) => id = FaceNpc(m, "RN_Face").FormKey.ID);
        var ctx = ReleaseNeedsTests.Context(s, pkg, data);
        foreach (var dmm in new[] { null, Path.Combine(s.Dir, "no-dmmdeps.exe") })
        {
            ctx.Options.DmmDeps = dmm;
            var f = Assert.Single(ReleaseNeeds.Run(ctx), x => x.Rule == "face-missing");
            Assert.Equal(Level.Note, f.Level);
            Assert.StartsWith("RnFace.esm: NPC RN_Face [", f.Text);
            // a light plugin: the 12-bit local ID
            Assert.Contains($@"no baked face data: neither meshes\actors\character\facegendata\facegeom\rnface.esm\{id & 0xFFF:x8}.nif", f.Text);
        }
        // the face files in the zip's loose files: quiet (a fresh sandbox, since the context zips the folder once)
        using var s2 = new Sandbox();
        var data2 = s2.Sub("Data");
        ReleaseNeedsTests.Write(data2, "Starfield.esm", (_, _) => { });
        var pkg2 = s2.Sub("RnFace");
        ReleaseNeedsTests.Write(pkg2, "RnFace.esm", (m, _) => FaceNpc(m, "RN_Face"));
        PutFace(pkg2, "rnface.esm", id & 0xFFF, ".nif", "_ao", "_color", "_normal", "_rough");
        Assert.DoesNotContain(ReleaseNeeds.Run(ReleaseNeedsTests.Context(s2, pkg2, data2)), x => x.Rule == "face-missing");
    }

    [Fact]
    public void A_texture_set_material_field_is_checked()
    {
        using var w = new World();
        GameMaterials(w, @"materials\vanilla\rock01.mat");
        var p = w.Build("txst", m =>
        {
            m.TextureSets.Add(new TextureSet(m) { EditorID = "SFM_Txst", Material = "SfmTest\\Missing.mat" });
            m.TextureSets.Add(new TextureSet(m) { EditorID = "SFM_Txst2", Material = "Materials\\Vanilla\\Rock01.mat" });
        });
        var r = w.Check(p);
        Expect.Finding(r, "WARNING", "assets", @"material materials\sfmtest\missing.mat is in no loose file or loaded archive; used by SFM_Txst");
        Assert.DoesNotContain(r.Findings, f => f.Text.Contains("rock01"));
    }

    [Fact]
    public void A_material_database_added_after_the_first_lookup_is_read_too()
    {
        // code review 0.14.0: release adds each plugin's Data masters' databases as it goes, after the first lookup
        using var s = new Sandbox();
        var index = Index(s);
        Assert.Null(index.InCdb(@"materials\late\one.mat"));
        Assert.NotNull(index.CdbProblem);
        var late = Path.Combine(s.Dir, "late.cdb");
        File.WriteAllBytes(late, Cdb(@"materials\late\one.mat"));
        index.AddCdb(late, null);
        Assert.True(index.InCdb(@"materials\late\one.mat"));
        Assert.Null(index.CdbProblem);
        Assert.False(index.InCdb(@"materials\late\nosuch.mat"));
    }

    [Fact]
    public void A_material_many_records_name_is_read_once()
    {
        // code review 0.14.0: it was opened, and counted as read, once per record
        var (mod, _, s) = FaceMod("MatOnce.esm", m =>
        {
            for (int i = 0; i < 3; i++) m.TextureSets.Add(new TextureSet(m) { EditorID = $"SFM_Txst{i}", Material = "SfmTest\\Shared.mat" });
        });
        using (s)
        {
            Put(Path.Combine(s.Dir, "Materials", "SfmTest", "Shared.mat"), Mat("Data\\Materials\\SfmTest\\Shared.mat", [], "Data\\Textures\\SfmTest\\gone.dds"));
            var uses = new AssetUses();
            var (_, _, missing, _) = Assets.Find(mod, Index(s), Checker.Name, _ => null, uses);
            Assert.Equal(1, uses.FilesRead);
            var gone = Assert.Single(missing);
            Assert.Equal("texture", gone.Kind);
            Assert.Equal(@"textures\sfmtest\gone.dds", gone.Path);
        }
    }

    [Fact]
    public void Conflict_actions_name_each_missing_kind_and_its_effect()
    {
        var a = new Conflict.Analysis { AssetsChecked = true };
        a.MissingAssets.Add(new Conflict.AssetFinding("x.esm", new MissingAsset("WARNING", "texture", @"textures\a.dds", null, ["A"])));
        a.MissingAssets.Add(new Conflict.AssetFinding("x.esm", new MissingAsset("WARNING", "material", @"materials\a.mat", null, ["A"])));
        a.MissingAssets.Add(new Conflict.AssetFinding("x.esm", new MissingAsset("WARNING", "sound", @"sound\soundbanks\1.wem", null, ["B"])));
        var act = Assert.Single(Conflict.Report.AssetActions(a));
        Assert.Equal(3, act.Priority);
        Assert.StartsWith("1 missing material, 1 missing texture and 1 missing sound; those surfaces look wrong and those sounds are silent.", act.Text);
        Assert.DoesNotContain("model", act.Text);
    }

    [Fact]
    public void Conflict_action_for_an_unlisted_kind_is_never_blank()
    {
        var a = new Conflict.Analysis { AssetsChecked = true };
        a.MissingAssets.Add(new Conflict.AssetFinding("x.esm", new MissingAsset("WARNING", "face", @"meshes\a.nif", null, ["A"])));
        var act = Assert.Single(Conflict.Report.AssetActions(a));
        Assert.StartsWith("1 other missing file; the game can't find those files.", act.Text);
    }
}
