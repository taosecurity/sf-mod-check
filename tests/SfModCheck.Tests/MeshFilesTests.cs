// 0.15.0 (plan step 12): the .mesh geometry files a mod's own .nif names, read from its BSGeometry blocks, with
// made-up .nif files built here in the layout AssetFiles.NifLinks documents.
using System.Text;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Starfield;
using Mutagen.Bethesda.Starfield.Assets;
using Xunit;

namespace SfModCheck.Tests;

public class MeshFilesTests
{
    /// <summary>One BSGeometry block: each name fills one of the four slots (null leaves it empty). flags 0x200 means
    /// the geometry is inside the .nif. extra adds bytes the layout doesn't expect.</summary>
    internal static byte[] Geometry(uint flags, int extra, params string?[] names)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(0u);                                   // name index
        w.Write(1u); w.Write(7u);                      // one extra data ref
        w.Write(0xFFFFFFFFu);                          // controller
        w.Write(flags);
        w.Write(new byte[12 + 36 + 4 + 4]);            // translation, rotation, scale, collision
        w.Write(new byte[16 + 24 + 12]);               // bounding sphere, bound min and max, skin, shader, alpha
        for (int slot = 0; slot < 4; slot++)
        {
            var n = slot < names.Length ? names[slot] : null;
            if (n == null) { w.Write((byte)0); continue; }
            w.Write((byte)1);
            w.Write(36u); w.Write(12u); w.Write(64u);  // triangle size, vertex count, flags
            var b = Encoding.UTF8.GetBytes(n);
            w.Write((uint)b.Length); w.Write(b);
            if ((flags & 0x200) != 0) w.Write(new byte[20]);   // stand-in for the geometry data inside the .nif
        }
        w.Write(new byte[extra]);
        return ms.ToArray();
    }

    /// <summary>A Starfield-style .nif with these blocks (type name, bytes) and these header strings.</summary>
    internal static byte[] Nif(IReadOnlyList<(string Type, byte[] Data)> blocks, params string[] strings)
    {
        var types = blocks.Select(b => b.Type).Distinct().ToList();
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("Gamebryo File Format, Version 20.2.0.7\n"));
        w.Write(0x14020007u); w.Write((byte)1); w.Write(12u);
        w.Write((uint)blocks.Count);
        w.Write(172u);                                 // Bethesda stream version
        w.Write((byte)1); w.Write((byte)0);            // author ""
        w.Write(0u);
        w.Write((byte)1); w.Write((byte)0);            // export script
        w.Write((byte)1); w.Write((byte)0);            // max filepath
        w.Write((ushort)types.Count);
        foreach (var t in types) { w.Write((uint)t.Length); w.Write(Encoding.ASCII.GetBytes(t)); }
        foreach (var b in blocks) w.Write((ushort)types.IndexOf(b.Type));
        foreach (var b in blocks) w.Write((uint)b.Data.Length);
        w.Write((uint)strings.Length);
        w.Write((uint)strings.DefaultIfEmpty("").Max(s => s.Length));
        foreach (var s in strings) { var b = Encoding.UTF8.GetBytes(s); w.Write((uint)b.Length); w.Write(b); }
        w.Write(0u);                                   // groups
        foreach (var b in blocks) w.Write(b.Data);
        return ms.ToArray();
    }

    static byte[] OneMesh(string name, params string[] strings) => Nif([("BSGeometry", Geometry(0, 0, name))], strings);

    static Model ModelOf(string path) => new() { File = new AssetLink<StarfieldModelAssetType>(path) };

    static void Put(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    [Fact]
    public void A_nif_names_its_geometry_files_in_both_spellings()
    {
        var nif = Nif([
            ("NiNode", new byte[30]),
            ("BSGeometry", Geometry(0, 0, @"ab12cd\EF34", null, @"AB12CD\ef34", "Data\\Geometries\\SfmTest\\Hilt.MESH")),
            ("BSGeometry", Geometry(0, 0, "", "sfmtest/blade")),
        ], "Root", @"Materials\SfmTest\Hilt.mat");
        var (mats, meshes, problem) = AssetFiles.NifLinks(nif);
        Assert.Null(problem);
        Assert.Equal(new[] { @"materials\sfmtest\hilt.mat" }, mats);
        Assert.Equal(new[] { @"geometries\ab12cd\ef34.mesh", @"geometries\sfmtest\hilt.mesh", @"geometries\sfmtest\blade.mesh" }, meshes);
        // the 0.14.0 reader still gives the same materials
        Assert.Equal(mats, AssetFiles.NifMaterials(nif));
    }

    [Fact]
    public void Geometry_inside_the_nif_names_no_file()
    {
        var (_, meshes, problem) = AssetFiles.NifLinks(Nif([("BSGeometry", Geometry(0x200, 0, @"ab\cd"))]));
        Assert.Null(problem);
        Assert.Empty(meshes);
    }

    [Fact]
    public void A_geometry_block_of_another_layout_is_a_problem_but_the_materials_still_count()
    {
        var (mats, meshes, problem) = AssetFiles.NifLinks(Nif([("BSGeometry", Geometry(0, 3, @"ab\cd"))], @"materials\a.mat"));
        Assert.Equal(new[] { @"materials\a.mat" }, mats);
        Assert.Empty(meshes);
        Assert.Equal("its BSGeometry block 0 doesn't have the layout sf-mod-check knows", problem);
        // a block that runs past the end of the file
        var cut = Nif([("BSGeometry", Geometry(0, 0, @"ab\cd"))]);
        (_, _, problem) = AssetFiles.NifLinks(cut[..^5]);
        Assert.NotNull(problem);
    }

    [Fact]
    public void Check_warns_about_a_mesh_its_own_nif_names_that_is_nowhere()
    {
        using var w = new World();
        File.WriteAllBytes(Path.Combine(w.Dir, "TestFull - Materials.ba2"), AssetFilesTests.Gnrl((AssetFiles.CdbPath, AssetFilesTests.Cdb())));
        var p = w.Build("meshy", m => m.MiscItems.First().Model = ModelOf("SfmTest\\Thing.nif"));
        Put(Path.Combine(w.Dir, "Meshes", "SfmTest", "Thing.nif"), OneMesh(@"SfmTest\Thing_Body"));
        var r = w.Check(p);
        Expect.Finding(r, "WARNING", "assets", @"mesh geometries\sfmtest\thing_body.mesh is in no loose file or loaded archive; used by meshes\sfmtest\thing.nif");
        // loose (another case on disk) counts
        Put(Path.Combine(w.Dir, "geometries", "SFMTEST", "thing_body.mesh"), new byte[4]);
        Expect.No(w.Check(p), "assets");
    }

    [Fact]
    public void A_mesh_in_the_plugins_own_archive_counts()
    {
        using var w = new World();
        File.WriteAllBytes(Path.Combine(w.Dir, "TestFull - Materials.ba2"), AssetFilesTests.Gnrl((AssetFiles.CdbPath, AssetFilesTests.Cdb())));
        var p = w.Build("packedmesh", m => m.MiscItems.First().Model = ModelOf("SfmTest\\Packed.nif"));
        File.WriteAllBytes(Path.Combine(w.Dir, "packedmesh - Main.ba2"), AssetFilesTests.Gnrl(
            ("meshes\\sfmtest\\packed.nif", Nif([("BSGeometry", Geometry(0, 0, @"aa11\bb22", @"aa11\gone"))])),
            ("geometries\\aa11\\bb22.mesh", new byte[8])));
        var r = w.Check(p);
        Expect.Finding(r, "WARNING", "assets", @"mesh geometries\aa11\gone.mesh is in no loose file or loaded archive; used by meshes\sfmtest\packed.nif");
        Assert.DoesNotContain(r.Findings, f => f.Text.Contains("bb22"));
    }

    [Fact]
    public void Release_reports_mesh_missing_also_with_dmmdeps()
    {
        using var s = new Sandbox();
        var data = s.Sub("Data");
        ReleaseNeedsTests.Write(data, "Starfield.esm", (_, _) => { });
        File.WriteAllBytes(Path.Combine(data, "Starfield - Materials.ba2"), AssetFilesTests.Gnrl((AssetFiles.CdbPath, AssetFilesTests.Cdb())));
        File.WriteAllBytes(Path.Combine(data, "Starfield - Meshes01.ba2"), AssetFilesTests.Gnrl(("geometries\\vanilla\\rock.mesh", new byte[8])));
        var pkg = s.Sub("RnMesh");
        ReleaseNeedsTests.Write(pkg, "RnMesh.esm", (_, misc) => misc.Model = ModelOf("RnMesh\\Thing.nif"));
        Put(Path.Combine(pkg, "Meshes", "RnMesh", "Thing.nif"), Nif([("BSGeometry", Geometry(0, 0, @"vanilla\rock", @"rnmesh\gone"))]));

        var ctx = ReleaseNeedsTests.Context(s, pkg, data);
        var f = ReleaseNeeds.Run(ctx).ToList();
        var m = Assert.Single(f, x => x.Rule == "mesh-missing");
        Assert.Equal(Level.Warning, m.Level);
        Assert.Contains(@"the model meshes\rnmesh\thing.nif names the geometry file geometries\rnmesh\gone.mesh", m.Text);
        Assert.DoesNotContain(f, x => x.Text.Contains("rock.mesh"));
        Assert.Contains(f, x => x.Rule == "dmmdeps" && x.Text.Contains(".mesh geometry of the mod's own models"));
        // DMMDeps can't see geometry, so the rule still runs with --dmmdeps
        ctx.Options.DmmDeps = Path.Combine(s.Dir, "no-dmmdeps.exe");
        f = ReleaseNeeds.Run(ctx).ToList();
        Assert.Single(f, x => x.Rule == "mesh-missing");
    }

    [Fact]
    public void Conflict_counts_meshes_and_names_their_effect()
    {
        var a = new Conflict.Analysis { AssetsChecked = true };
        a.MissingAssets.Add(new Conflict.AssetFinding("x.esm", new MissingAsset("WARNING", "mesh", @"geometries\a\b.mesh", null, [@"meshes\a.nif"])));
        a.MissingAssets.Add(new Conflict.AssetFinding("x.esm", new MissingAsset("WARNING", "mesh", @"geometries\a\c.mesh", null, [@"meshes\a.nif"])));
        Assert.Equal("0 scripts, 0 models, 2 meshes", Conflict.Report.Kinds(a));
        var act = Assert.Single(Conflict.Report.AssetActions(a));
        Assert.StartsWith("2 missing meshes; parts of those models don't show.", act.Text);
        Assert.DoesNotContain(".mesh geometry, voice", Conflict.Report.AssetsNotChecked(a));
    }

    [Fact]
    public void The_mesh_check_is_in_the_readme_agents_changelog_and_the_hint()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "AssetFiles.cs"))) dir = dir.Parent;
        Assert.True(dir != null, "src\\AssetFiles.cs not found above " + AppContext.BaseDirectory);
        string Doc(string n) => File.ReadAllText(Path.Combine(dir!.FullName, n));
        var readme = Doc("README.md");
        Assert.Contains("| `mesh-missing` |", readme);
        Assert.Contains("**Geometry** (0.15.0, WARNING)", readme);
        Assert.DoesNotContain("music, `.mesh` geometry; see the", readme);
        Assert.Contains("`mesh-missing`", Doc("CHANGELOG.md").Split("\n## 0.14.0")[0]);
        Assert.Contains("`mesh-missing`", Doc("AGENTS.md"));
        // the README's hint list is the HINT text
        Assert.Contains("A missing mesh means that part of the model doesn't show", readme);
        Assert.Contains("A missing mesh means that part of the model doesn't show", Hints.ByCheck["assets"]);
    }
}
