// sf-mod-check patch 0.13.0: cells and worldspaces are built (the record itself only). Each test builds a tiny game in a
// sandbox: Base.esm with an interior cell and a worldspace holding a persistent cell and one exterior cell, each cell
// with a placed object, then mods that change them. No game files are needed.
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using Noggog;
using Xunit;

namespace SfModCheck.Tests;

public class PatchCellsTests
{
    static readonly WriteOptions NoGap = new() { GapCheck = false, FixEffects = false };

    sealed class Game : Sandbox
    {
        public readonly string Data;
        public readonly FormKey Interior, Exterior, World, Persistent;
        // the placed object in each cell
        public readonly FormKey RefInterior, RefExterior, RefPersistent;
        // the navmesh in the interior cell (step 8c)
        public readonly FormKey Navmesh;
        public Game()
        {
            Data = Sub("Data");
            var b = Plugin.New("Base.esm", PluginStyle.Full, "test", "made-up base game");
            var misc = b.MiscItems.AddNew("B_Misc");
            var cell = new Cell(b) { EditorID = "B_Cell", Flags = Cell.Flag.IsInteriorCell };
            var refIn = new PlacedObject(b) { Base = misc.ToLink<IPlaceableObjectGetter>() };
            cell.Temporary.Add(refIn);
            cell.Components.Add(CityMap("maps\\city.dat"));
            var navm = new NavigationMesh(b) { EditorID = "B_Navm", NavmeshGeometry = Geometry(cell.FormKey, 0) };
            cell.NavigationMeshes.Add(navm);
            Navmesh = navm.FormKey;
            b.Cells.Add(new CellBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellBlock, SubBlocks = new ExtendedList<CellSubBlock> { new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock, Cells = new ExtendedList<Cell> { cell } } } });
            var world = b.Worldspaces.AddNew("B_World");
            world.EnvironmentMap = "base.dds";
            var top = new Cell(b) { EditorID = "B_WorldPersistent" };
            var refTop = new PlacedObject(b) { Base = misc.ToLink<IPlaceableObjectGetter>() };
            top.Persistent.Add(refTop);
            world.TopCell = top;
            var ext = new Cell(b) { EditorID = "B_Ext", Grid = new CellGrid { Point = new P2Int(1, 2) } };
            var refExt = new PlacedObject(b) { Base = misc.ToLink<IPlaceableObjectGetter>() };
            ext.Temporary.Add(refExt);
            world.SubCells.Add(new WorldspaceBlock
            {
                BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellBlock,
                Items = new ExtendedList<WorldspaceSubBlock> { new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellSubBlock, Items = new ExtendedList<Cell> { ext } } },
            });
            (Interior, Exterior, World, Persistent) = (cell.FormKey, ext.FormKey, world.FormKey, top.FormKey);
            (RefInterior, RefExterior, RefPersistent) = (refIn.FormKey, refExt.FormKey, refTop.FormKey);
            using var g = new GameData(Data);
            PluginWriter.Write(b, Data, g, NoGap);
        }

        public void Mod(string name, Action<StarfieldMod, IStarfieldModGetter, ILinkCache<IStarfieldMod, IStarfieldModGetter>> build)
        {
            var mod = Plugin.New(name, PluginStyle.Light, "test", "made-up mod");
            using var g = new GameData(Data);
            var b = g.Open("Base.esm");
            build(mod, b, b.ToImmutableLinkCache());
            PluginWriter.Write(mod, Data, g, NoGap);
        }

        /// <summary>The cell as an override in mod (an exterior cell brings its worldspace along, as Mutagen copies it).</summary>
        public static Cell CellIn(StarfieldMod mod, ILinkCache<IStarfieldMod, IStarfieldModGetter> cache, FormKey fk) =>
            (Cell)cache.ResolveAllContexts<ICell, ICellGetter>(fk).First().GetOrAddAsOverride(mod);

        /// <summary>The placed object as an override in mod (Mutagen brings its cell, and an exterior cell's worldspace, along).</summary>
        public static PlacedObject RefIn(StarfieldMod mod, ILinkCache<IStarfieldMod, IStarfieldModGetter> cache, FormKey fk) =>
            (PlacedObject)cache.ResolveAllContexts<IPlacedObject, IPlacedObjectGetter>(fk).First().GetOrAddAsOverride(mod);

        /// <summary>The navmesh as an override in mod (Mutagen brings its cell along).</summary>
        public static NavigationMesh NavIn(StarfieldMod mod, ILinkCache<IStarfieldMod, IStarfieldModGetter> cache, FormKey fk) =>
            (NavigationMesh)cache.ResolveAllContexts<INavigationMesh, INavigationMeshGetter>(fk).First().GetOrAddAsOverride(mod);

        /// <summary>A one-vertex navmesh geometry in an interior cell; x tells two meshes apart.</summary>
        public static NavmeshGeometry Geometry(FormKey cell, float x) => new()
        {
            Parent = new CellNavmeshParent { Parent = new FormLink<ICellGetter>(cell) },
            Vertices = new ExtendedList<NavmeshVertex> { new NavmeshVertex { Point = new P3Float(x, 0, 0) } },
        };

        /// <summary>A city-map component naming one file (a cell holds one component of this kind).</summary>
        public static CityMapsUsageComponent CityMap(string file) => new() { MOD2 = System.Text.Encoding.ASCII.GetBytes(file + "\0") };

        /// <summary>The worldspace as an override in mod, without its cells (a group override copies them too).</summary>
        public static Worldspace WorldIn(StarfieldMod mod, IStarfieldModGetter b)
        {
            var w = mod.Worldspaces.GetOrAddAsOverride(b.Worldspaces.First());
            w.SubCells.Clear();
            w.TopCell = null;
            return w;
        }

        public string Out => Path.Combine(Dir, "patch");

        public (int Code, string Stdout, string Stderr) Patch(params string[] active)
        {
            var txt = Path.Combine(Dir, "Plugins.txt");
            File.WriteAllLines(txt, active.Select(a => "*" + a));
            return ExeTests.RunSplit("patch", "--data", Data, "--plugins", txt, "--out", Out, "--quiet");
        }

        /// <summary>The patch run with a log file (what --verbose shows), for the log-line tests.</summary>
        public (int Code, string Stdout, string Stderr, string Log) PatchLogged(params string[] active)
        {
            var txt = Path.Combine(Dir, "Plugins.txt");
            File.WriteAllLines(txt, active.Select(a => "*" + a));
            var log = Path.Combine(Dir, "patch.log");
            var (code, stdout, stderr) = ExeTests.RunSplit("patch", "--data", Data, "--plugins", txt, "--out", Out, "--quiet", "--log", log);
            return (code, stdout, stderr, File.Exists(log) ? File.ReadAllText(log) : "");
        }

        readonly List<GameData> opened = new();
        public IStarfieldModGetter OpenPatch()
        {
            var g = new GameData(Data, [Out]);
            opened.Add(g);
            return g.Open("ConflictPatch.esm");
        }

        public void ConflictIsClean()
        {
            var (code, stdout, stderr) = ExeTests.RunSplit("conflict", "--data", Data, "--plugins", Path.Combine(Out, "Plugins-with-patch.txt"),
                "--extra-data", Out, "--out", Path.Combine(Dir, "report"), "--quiet");
            Assert.True(code == 0, stdout + stderr);
            Assert.StartsWith("RESULT: OK, 0 lost-change entries", ExeTests.ResultLine(stdout));
        }

        public override void Dispose()
        {
            foreach (var g in opened) g.Dispose();
            base.Dispose();
        }
    }

    [Fact]
    public void Two_mods_changing_different_fields_of_an_interior_cell_both_keep_them()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b, c) => Game.CellIn(m, c, game.Interior).WaterHeight = 5);
        game.Mod("ModB.esm", (m, b, c) => Game.CellIn(m, c, game.Interior).WaterEnvironmentMap = "modb.dds");
        var (code, stdout, stderr) = game.Patch("Base.esm", "ModA.esm", "ModB.esm");
        Assert.True(code == 0, stdout + stderr);
        Assert.StartsWith("RESULT: OK, patch ", ExeTests.ResultLine(stdout));
        Assert.Contains("1 records, 1 fields forwarded", ExeTests.ResultLine(stdout));
        var patch = game.OpenPatch();
        var cell = Assert.Single(patch.EnumerateMajorRecords<ICellGetter>());
        Assert.Equal(game.Interior, cell.FormKey);
        Assert.Equal((5f, "modb.dds"), (cell.WaterHeight, cell.WaterEnvironmentMap));
        // only the cell: its placed object stays with the plugins that have it
        Assert.Empty(cell.Temporary);
        Assert.Empty(cell.Persistent);
        Assert.Single(Assert.Single(patch.Cells).SubBlocks);
        Assert.Empty(patch.Worldspaces);
        Assert.Single(patch.EnumerateMajorRecords());
        Assert.DoesNotContain("Not built", File.ReadAllText(Path.Combine(game.Out, "patch-report.md")));
        game.ConflictIsClean();
        var (checkCode, checkOut, checkErr) = ExeTests.RunSplit("check", Path.Combine(game.Out, "ConflictPatch.esm"), "--data", game.Data, "--quiet", "--no-assets");
        Assert.True(checkCode == 0, checkOut + checkErr);
    }

    [Fact]
    public void An_exterior_cell_brings_its_worldspace_as_the_game_uses_it_today()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b, c) => Game.CellIn(m, c, game.Exterior).WaterHeight = 7);
        game.Mod("ModB.esm", (m, b, c) => Game.CellIn(m, c, game.Exterior).WaterEnvironmentMap = "modb.dds");
        // ModC changes only the worldspace, and wins it: the patch's copy must keep ModC's change
        game.Mod("ModC.esm", (m, b, c) => Game.WorldIn(m, b).EnvironmentMap = "modc.dds");
        var (code, stdout, stderr) = game.Patch("Base.esm", "ModA.esm", "ModB.esm", "ModC.esm");
        // The CHANGELOG example is a real run on these made-up files: set SFMODCHECK_PATCH_CELLS_EXAMPLE to a folder
        // to keep a copy of them (Data\ and Plugins.txt).
        if (Environment.GetEnvironmentVariable("SFMODCHECK_PATCH_CELLS_EXAMPLE") is { Length: > 0 } keep)
        {
            Directory.CreateDirectory(Path.Combine(keep, "Data"));
            foreach (var f in Directory.EnumerateFiles(game.Data))
            {
                var dest = Path.Combine(keep, "Data", Path.GetFileName(f));
                File.Copy(f, dest, true);
                File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(f));
            }
            File.Copy(Path.Combine(game.Dir, "Plugins.txt"), Path.Combine(keep, "Plugins.txt"), true);
        }
        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("2 records, 1 fields forwarded", ExeTests.ResultLine(stdout));
        var patch = game.OpenPatch();
        var world = Assert.Single(patch.Worldspaces);
        Assert.Equal(game.World, world.FormKey);
        Assert.Equal("modc.dds", world.EnvironmentMap);
        Assert.Null(world.TopCell);           // the persistent cell (and its object) isn't patched, so it isn't copied
        Assert.Empty(world.LargeReferences);
        Assert.Null(world.OffsetData);
        var cell = Assert.Single(world.SubCells.SelectMany(x => x.Items).SelectMany(x => x.Items));
        Assert.Equal(game.Exterior, cell.FormKey);
        Assert.Equal((7f, "modb.dds"), (cell.WaterHeight, cell.WaterEnvironmentMap));
        Assert.Equal(new P2Int(1, 2), cell.Grid!.Point);
        Assert.Empty(cell.Temporary);
        Assert.Empty(patch.Cells);
        Assert.Equal(2, patch.EnumerateMajorRecords().Count());
        var report = File.ReadAllText(Path.Combine(game.Out, "patch-report.md"));
        Assert.Contains("the parent of patched cells", report);
        Assert.Contains("large-reference table (RNAM)", report);
        game.ConflictIsClean();
        // and the patch passes check
        var (checkCode, checkOut, checkErr) = ExeTests.RunSplit("check", Path.Combine(game.Out, "ConflictPatch.esm"), "--data", game.Data, "--quiet", "--no-assets");
        Assert.True(checkCode == 0, checkOut + checkErr);
    }

    // 2026-11-22 (relay step 13, plan step 10's log read on real data): the log had no line for a record built as a patch
    // record, for one left out because it would equal today's, or for a record type that isn't built (NAVI's reason
    // was only in the report). Every record the patch decides on now has a log line saying what and why. The
    // not-built and left-out lines are checked on real data (NAVI and a quest), since a made-up game has neither.
    [Fact]
    public void The_log_has_a_line_for_every_record_built_or_left_out()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b, c) => Game.CellIn(m, c, game.Exterior).WaterHeight = 7);
        game.Mod("ModB.esm", (m, b, c) => Game.CellIn(m, c, game.Exterior).WaterEnvironmentMap = "modb.dds");
        var (code, stdout, stderr, log) = game.PatchLogged("Base.esm", "ModA.esm", "ModB.esm");
        Assert.True(code == 0, stdout + stderr);
        Assert.Contains($"patch: Cell B_Ext [{game.Exterior}] built: 1 field(s) forwarded (WaterHeight from ModA.esm)", log);
        Assert.Contains($"patch: cell B_Ext [{game.Exterior}] goes in worldspace {game.World}", log);
        Assert.Contains($"patch: worldspace B_World [{game.World}] added as the parent of 1 cell(s)", log);
    }

    [Fact]
    public void A_worldspace_field_two_mods_change_is_forwarded()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b, c) => Game.WorldIn(m, b).EnvironmentMap = "moda.dds");
        game.Mod("ModB.esm", (m, b, c) => Game.WorldIn(m, b).WaterEnvironmentMap = "modb.dds");
        var (code, stdout, stderr) = game.Patch("Base.esm", "ModA.esm", "ModB.esm");
        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("1 records, 1 fields forwarded", ExeTests.ResultLine(stdout));
        var world = Assert.Single(game.OpenPatch().Worldspaces);
        Assert.Equal(("moda.dds", "modb.dds"), (world.EnvironmentMap, world.WaterEnvironmentMap));
        Assert.Empty(world.SubCells);
        Assert.Null(world.TopCell);
        game.ConflictIsClean();
    }

    // 2026-11-22 (relay step 8, real data): two city cells' only "lost" change was their one city-map component with the
    // file name in other letter case, and the entry-by-entry merge put both copies in. A component's kind is its identity.
    // (Step 8d made a case-only file name "No effect", so the two files here differ by a letter as well.)
    [Fact]
    public void Two_versions_of_one_component_never_both_go_in()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b, c) => { var x = Game.CellIn(m, c, game.Interior); x.Components.Clear(); x.Components.Add(Game.CityMap("Maps\\Town.dat")); });
        game.Mod("ModB.esm", (m, b, c) => { var x = Game.CellIn(m, c, game.Interior); x.Components.Clear(); x.Components.Add(Game.CityMap("MAPS\\CITY.DAT")); });
        var (code, stdout, stderr) = game.Patch("Base.esm", "ModA.esm", "ModB.esm");
        Assert.True(code == 0, stdout + stderr);
        // the winner's component stays the only one, so the cell would equal today's and is left out
        Assert.StartsWith("RESULT: OK, nothing to patch", ExeTests.ResultLine(stdout));
        Assert.Contains("the patched record would equal today's", File.ReadAllText(Path.Combine(game.Out, "patch-report.md")));
    }

    // code review 0.13.0, round 1: a mod that adds a second component of a kind the list already has keeps it; the kind is
    // an identity only while every list holds one of it (before the fix, the winner's one city map swallowed ModA's second)
    [Fact]
    public void A_second_component_of_one_kind_is_added_not_swapped_in()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b, c) => Game.CellIn(m, c, game.Interior).Components.Add(Game.CityMap("maps\\town.dat")));
        game.Mod("ModB.esm", (m, b, c) => { var x = Game.CellIn(m, c, game.Interior); x.Components.Clear(); x.Components.Add(Game.CityMap("maps\\other.dat")); });
        var (code, stdout, stderr) = game.Patch("Base.esm", "ModA.esm", "ModB.esm");
        Assert.True(code == 0, stdout + stderr);
        var cell = Assert.Single(game.OpenPatch().EnumerateMajorRecords<ICellGetter>(), x => x.FormKey == game.Interior);
        var files = cell.Components.OfType<ICityMapsUsageComponentGetter>()
            .Select(x => System.Text.Encoding.ASCII.GetString(x.MOD2!.Value.ToArray()).TrimEnd('\0')).ToList();
        Assert.Equal(new[] { "maps\\other.dat", "maps\\town.dat" }, files);
    }

    [Fact]
    public void Cells_worldspaces_placed_objects_and_navmeshes_are_built()
    {
        Assert.Null(PatchCommand.WhyNotBuilt(typeof(ICellGetter)));
        Assert.Null(PatchCommand.WhyNotBuilt(typeof(IWorldspaceGetter)));
        Assert.Null(PatchCommand.WhyNotBuilt(typeof(IPlacedObjectGetter)));
        Assert.Null(PatchCommand.WhyNotBuilt(typeof(IPlacedNpcGetter)));
        Assert.Null(PatchCommand.WhyNotBuilt(typeof(INavigationMeshGetter)));
        Assert.Contains("navmesh info map", PatchCommand.WhyNotBuilt(typeof(INavigationMeshInfoMapGetter)));
        Assert.Contains("dialogue", PatchCommand.WhyNotBuilt(typeof(IDialogResponsesGetter)));
    }

    static MemorySlice<byte> Nnam(byte b) => new byte[] { b, 0, 0, 0 };

    // step 8c: a navmesh one mod changes and another (the winner) leaves alone goes in whole, inside its cell
    [Fact]
    public void A_navmesh_a_later_mod_left_alone_is_forwarded_whole_into_its_cell()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b, c) => { var n = Game.NavIn(m, c, game.Navmesh); n.NavmeshGeometry = Game.Geometry(game.Interior, 5); n.NNAM = Nnam(7); });
        game.Mod("ModB.esm", (m, b, c) => Game.NavIn(m, c, game.Navmesh).EditorID = "B_NavmRenamed");
        var (code, stdout, stderr) = game.Patch("Base.esm", "ModA.esm", "ModB.esm");
        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("2 records, ", ExeTests.ResultLine(stdout));
        var patch = game.OpenPatch();
        var cell = Assert.Single(Assert.Single(Assert.Single(patch.Cells).SubBlocks).Cells);
        Assert.Equal(game.Interior, cell.FormKey);
        Assert.Empty(cell.Temporary);
        var n = Assert.Single(cell.NavigationMeshes);
        Assert.Equal(game.Navmesh, n.FormKey);
        Assert.Equal("B_NavmRenamed", n.EditorID);
        Assert.Equal(5f, Assert.Single(n.NavmeshGeometry!.Vertices).Point.X);
        Assert.Equal(new byte[] { 7, 0, 0, 0 }, n.NNAM!.Value.ToArray());
        Assert.Equal(2, patch.EnumerateMajorRecords().Count());
        var report = File.ReadAllText(Path.Combine(game.Out, "patch-report.md"));
        Assert.Contains("the parent of patched navmeshes", report);
        Assert.DoesNotContain("Not built", report);
        game.ConflictIsClean();
        CheckIsClean(game);
        if (Environment.GetEnvironmentVariable("SFMODCHECK_PATCH_NAVMESH_KEEP") is { Length: > 0 } keep)
            foreach (var (from, to) in new[] { (game.Data, "Data"), (game.Out, "patch") })
            {
                Directory.CreateDirectory(Path.Combine(keep, to));
                foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(keep, to, Path.GetFileName(f)), true);
            }
    }

    // step 8c: one mod changes the geometry, the winner the NNAM data: a mix would be a mesh no one made, so today's stays
    [Fact]
    public void A_navmesh_mesh_is_never_mixed_from_two_mods()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b, c) => Game.NavIn(m, c, game.Navmesh).NavmeshGeometry = Game.Geometry(game.Interior, 5));
        game.Mod("ModB.esm", (m, b, c) => Game.NavIn(m, c, game.Navmesh).NNAM = Nnam(9));
        var (code, stdout, stderr) = game.Patch("Base.esm", "ModA.esm", "ModB.esm");
        Assert.True(code == 0, stdout + stderr);
        var report = File.ReadAllText(Path.Combine(game.Out, "patch-report.md"));
        Assert.Contains("are one mesh", report);
        Assert.Contains("the patched record would equal today's", report);
        Assert.DoesNotContain("navmeshes", ExeTests.ResultLine(stdout));
        Assert.False(File.Exists(Path.Combine(game.Out, "ConflictPatch.esm")) && game.OpenPatch().EnumerateMajorRecords<INavigationMeshGetter>().Any());
    }

    // code review 0.13.0, round 1: the mixed-mesh row's --pick line works when run (the geometry is a plain forwarded
    // field there, which the choice loops never picked, so the pick went unused before the fix)
    [Fact]
    public void The_pick_a_mixed_mesh_row_suggests_takes_that_mesh()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b, c) => Game.NavIn(m, c, game.Navmesh).NavmeshGeometry = Game.Geometry(game.Interior, 5));
        game.Mod("ModB.esm", (m, b, c) => Game.NavIn(m, c, game.Navmesh).NNAM = Nnam(9));
        var txt = Path.Combine(game.Dir, "Plugins.txt");
        File.WriteAllLines(txt, new[] { "*Base.esm", "*ModA.esm", "*ModB.esm" });
        var (code0, stdout0, stderr0) = ExeTests.RunSplit("patch", "--data", game.Data, "--plugins", txt, "--out", game.Out, "--quiet");
        Assert.True(code0 == 0, stdout0 + stderr0);
        Assert.Contains($"--pick {game.Navmesh}:NavmeshGeometry=ModA.esm", File.ReadAllText(Path.Combine(game.Out, "patch-report.md")));
        Directory.Delete(game.Out, true);
        var (code, stdout, stderr) = ExeTests.RunSplit("patch", "--data", game.Data, "--plugins", txt, "--out", game.Out, "--quiet",
            "--pick", $"{game.Navmesh}:NavmeshGeometry=ModA.esm");
        Assert.True(code == 0, stdout + stderr);
        Assert.DoesNotContain("was not used", stdout + stderr);
        var n = Assert.Single(game.OpenPatch().EnumerateMajorRecords<INavigationMeshGetter>());
        Assert.Equal(5f, Assert.Single(n.NavmeshGeometry!.Vertices).Point.X);
        Assert.Null(n.NNAM);   // ModA's whole mesh: its NNAM is Base's (none), not ModB's
        Assert.Contains("whole mesh is used", File.ReadAllText(Path.Combine(game.Out, "patch-report.md")));
        CheckIsClean(game);
    }

    [Fact]
    public void A_cell_that_failed_to_build_but_is_a_parent_says_so()
    {
        var plan = new PatchCommand.Plan();
        var fk = FormKey.Factory("000801:Base.esm");
        plan.NotBuilt.Add(new(fk, "Cell", "B_Cell", "it could not be built (X: y)", new()));
        PatchCommand.NoteNotBuiltParent(plan, fk, "1 patched placed object");
        var row = Assert.Single(plan.NotBuilt);
        Assert.StartsWith("it could not be built (X: y); it is in the patch only as the parent of 1 patched placed object", row.Why);
        Assert.EndsWith("so its own lost changes are still not patched", row.Why);
    }

    // step 8c: both change the geometry: the winner's mesh stays unless --pick takes another plugin's whole mesh
    [Fact]
    public void A_pick_on_a_navmesh_takes_that_plugins_whole_mesh()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b, c) => { var n = Game.NavIn(m, c, game.Navmesh); n.NavmeshGeometry = Game.Geometry(game.Interior, 5); n.NNAM = Nnam(7); });
        game.Mod("ModB.esm", (m, b, c) => Game.NavIn(m, c, game.Navmesh).NavmeshGeometry = Game.Geometry(game.Interior, 6));
        var txt = Path.Combine(game.Dir, "Plugins.txt");
        File.WriteAllLines(txt, new[] { "*Base.esm", "*ModA.esm", "*ModB.esm" });
        var (code, stdout, stderr) = ExeTests.RunSplit("patch", "--data", game.Data, "--plugins", txt, "--out", game.Out, "--quiet",
            "--pick", $"{game.Navmesh}:NavmeshGeometry=ModA.esm");
        Assert.True(code == 0, stdout + stderr);
        var n = Assert.Single(game.OpenPatch().EnumerateMajorRecords<INavigationMeshGetter>());
        Assert.Equal(5f, Assert.Single(n.NavmeshGeometry!.Vertices).Point.X);
        Assert.Equal(new byte[] { 7, 0, 0, 0 }, n.NNAM!.Value.ToArray());   // ModA's NNAM came with its geometry
        CheckIsClean(game);
    }

    [Fact]
    public void Readme_and_agents_say_placed_objects_and_navmeshes_are_built()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "Program.cs"))) dir = dir.Parent;
        Assert.True(dir != null, "src\\Program.cs not found above " + AppContext.BaseDirectory);
        string Text(string f) => File.ReadAllText(Path.Combine(dir!.FullName, f)).ReplaceLineEndings(" ").Replace("  ", " ");
        Assert.Contains("**What it doesn't build yet:** dialogue topics", Text("README.md"));
        Assert.Contains("**Placed objects** (since 0.13.0", Text("README.md"));
        Assert.Contains("**Navmeshes** (since 0.13.0)", Text("README.md"));
        Assert.Contains("cells, worldspaces, placed objects and navmeshes are built since 0.13.0", Text("AGENTS.md"));
        Assert.Contains("and placed objects and navmeshes, each inside its cell", Text("AGENTS.md"));
        Assert.Contains("`patch` builds placed objects", Text("CHANGELOG.md"));
        Assert.Contains("`patch` builds navmeshes", Text("CHANGELOG.md"));
    }

    static void Move(PlacedObject r) => r.Position = new P3Float(10, 20, 30);

    static void CheckIsClean(Game game)
    {
        var (code, stdout, stderr) = ExeTests.RunSplit("check", Path.Combine(game.Out, "ConflictPatch.esm"), "--data", game.Data, "--quiet", "--no-assets");
        Assert.True(code == 0, stdout + stderr);
    }

    // plan step 8b: a placed object moved by one mod and scaled by another keeps both, inside its cell as the game uses it today
    [Fact]
    public void A_placed_object_moved_by_one_mod_and_scaled_by_another_keeps_both()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b, c) => Move(Game.RefIn(m, c, game.RefInterior)));
        game.Mod("ModB.esm", (m, b, c) => Game.RefIn(m, c, game.RefInterior).Scale = 2);
        var (code, stdout, stderr) = game.Patch("Base.esm", "ModA.esm", "ModB.esm");
        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("2 records, 1 fields forwarded", ExeTests.ResultLine(stdout));
        var patch = game.OpenPatch();
        var cell = Assert.Single(Assert.Single(Assert.Single(patch.Cells).SubBlocks).Cells);
        Assert.Equal(game.Interior, cell.FormKey);
        Assert.Equal("B_Cell", cell.EditorID);   // the parent cell as the game uses it today
        var r = Assert.IsAssignableFrom<IPlacedObjectGetter>(Assert.Single(cell.Temporary));
        Assert.Equal(game.RefInterior, r.FormKey);
        Assert.Equal(2f, r.Scale);
        Assert.Equal(new P3Float(10, 20, 30), r.Position);
        Assert.Empty(cell.Persistent);
        Assert.Empty(patch.Worldspaces);
        Assert.Equal(2, patch.EnumerateMajorRecords().Count());
        var report = File.ReadAllText(Path.Combine(game.Out, "patch-report.md"));
        Assert.Contains("the parent of patched placed objects", report);
        Assert.DoesNotContain("Not built", report);
        game.ConflictIsClean();
        CheckIsClean(game);
        // SFMODCHECK_PATCH_CELLS_KEEP=<folder> keeps the made-up Data folder and the patch, for the gap check by hand
        if (Environment.GetEnvironmentVariable("SFMODCHECK_PATCH_CELLS_KEEP") is { Length: > 0 } keep)
            foreach (var (from, to) in new[] { (game.Data, "Data"), (game.Out, "patch") })
            {
                Directory.CreateDirectory(Path.Combine(keep, to));
                foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(keep, to, Path.GetFileName(f)), true);
            }
    }

    // an object in an exterior cell brings its cell, and the cell brings its worldspace, both as the game uses them today
    [Fact]
    public void A_placed_object_in_an_exterior_cell_brings_its_cell_and_worldspace()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b, c) => Move(Game.RefIn(m, c, game.RefExterior)));
        game.Mod("ModB.esm", (m, b, c) => Game.RefIn(m, c, game.RefExterior).Scale = 3);
        var (code, stdout, stderr) = game.Patch("Base.esm", "ModA.esm", "ModB.esm");
        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("3 records, 1 fields forwarded", ExeTests.ResultLine(stdout));
        var patch = game.OpenPatch();
        var world = Assert.Single(patch.Worldspaces);
        Assert.Null(world.TopCell);
        var cell = Assert.Single(world.SubCells.SelectMany(x => x.Items).SelectMany(x => x.Items));
        Assert.Equal(game.Exterior, cell.FormKey);
        var r = Assert.IsAssignableFrom<IPlacedObjectGetter>(Assert.Single(cell.Temporary));
        Assert.Equal((3f, new P3Float(10, 20, 30)), (r.Scale, r.Position));
        Assert.Empty(patch.Cells);
        Assert.Equal(3, patch.EnumerateMajorRecords().Count());
        game.ConflictIsClean();
        CheckIsClean(game);
    }

    // a persistent object of a worldspace goes in the worldspace's persistent cell, in its persistent list
    [Fact]
    public void A_persistent_placed_object_goes_in_the_persistent_cell()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b, c) => Move(Game.RefIn(m, c, game.RefPersistent)));
        game.Mod("ModB.esm", (m, b, c) => Game.RefIn(m, c, game.RefPersistent).Scale = 4);
        var (code, stdout, stderr) = game.Patch("Base.esm", "ModA.esm", "ModB.esm");
        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("3 records, 1 fields forwarded", ExeTests.ResultLine(stdout));
        var world = Assert.Single(game.OpenPatch().Worldspaces);
        Assert.Empty(world.SubCells);
        var top = world.TopCell!;
        Assert.Equal(game.Persistent, top.FormKey);
        var r = Assert.Single(top.Persistent);
        Assert.Equal(game.RefPersistent, r.FormKey);
        Assert.Empty(top.Temporary);
        game.ConflictIsClean();
        CheckIsClean(game);
    }

    // a cell patched for its own sake holds the patched object too, and isn't marked as a parent
    [Fact]
    public void A_patched_cell_holds_its_patched_placed_object()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b, c) => { Game.CellIn(m, c, game.Interior).WaterHeight = 5; Move(Game.RefIn(m, c, game.RefInterior)); });
        game.Mod("ModB.esm", (m, b, c) => { Game.CellIn(m, c, game.Interior).WaterEnvironmentMap = "modb.dds"; Game.RefIn(m, c, game.RefInterior).Scale = 2; });
        var (code, stdout, stderr) = game.Patch("Base.esm", "ModA.esm", "ModB.esm");
        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("2 records, 2 fields forwarded", ExeTests.ResultLine(stdout));
        var cell = Assert.Single(game.OpenPatch().EnumerateMajorRecords<ICellGetter>());
        Assert.Equal((5f, "modb.dds"), (cell.WaterHeight, cell.WaterEnvironmentMap));
        Assert.Equal(game.RefInterior, Assert.Single(cell.Temporary).FormKey);
        Assert.DoesNotContain("the parent of patched placed objects", File.ReadAllText(Path.Combine(game.Out, "patch-report.md")));
        game.ConflictIsClean();
        CheckIsClean(game);
    }

    // step 8d: a city-map component's file name is a fixed path buffer (260 bytes in the game's files), raw bytes in Mutagen
    static CityMapsUsageComponent PaddedCityMap(string file)
    {
        var bytes = new byte[260];
        System.Text.Encoding.ASCII.GetBytes(file).CopyTo(bytes, 0);
        return new() { MOD2 = bytes };
    }

    static IEnumerable<System.Text.Json.JsonElement> FindAll(System.Text.Json.JsonElement e, string name)
    {
        if (e.ValueKind == System.Text.Json.JsonValueKind.Object)
            foreach (var p in e.EnumerateObject())
            {
                if (p.Name == name) yield return p.Value;
                foreach (var x in FindAll(p.Value, name)) yield return x;
            }
        else if (e.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var v in e.EnumerateArray())
                foreach (var x in FindAll(v, name)) yield return x;
    }

    static (int Code, string Stdout, string Stderr) Conflict(Game game, params string[] active)
    {
        var txt = Path.Combine(game.Dir, "Plugins-conflict.txt");
        File.WriteAllLines(txt, active.Select(a => "*" + a));
        return ExeTests.RunSplit("conflict", "--data", game.Data, "--plugins", txt, "--out", Path.Combine(game.Dir, "report"), "--quiet");
    }

    // step 8d (real data: two city cells): a raw file name that differs only in letter case is letter case, as a string is
    [Fact]
    public void A_raw_file_name_that_differs_only_in_letter_case_is_no_effect()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b, c) => { var x = Game.CellIn(m, c, game.Interior); x.Components.Clear(); x.Components.Add(PaddedCityMap("CityMaps\\SFBGS008\\sfCity.dat")); });
        game.Mod("ModB.esm", (m, b, c) => { var x = Game.CellIn(m, c, game.Interior); x.Components.Clear(); x.Components.Add(PaddedCityMap("CityMaps\\SFBGS008\\SFCity.dat")); });
        var (code, stdout, stderr) = Conflict(game, "Base.esm", "ModA.esm", "ModB.esm");
        Assert.True(code == 0, stdout + stderr);
        // conflict still lists the entry, rated "No effect" (letter case), as it does a case-only string
        Assert.StartsWith("RESULT: OK, 1 lost-change entries", ExeTests.ResultLine(stdout));
        using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(game.Dir, "report", "conflicts.json"))))
        {
            var diffs = FindAll(doc.RootElement, "Differences").SelectMany(d => d.EnumerateArray()).ToList();
            Assert.NotEmpty(diffs);
            Assert.All(diffs, d => Assert.True(d.GetProperty("Minor").GetBoolean(), d.ToString()));
            Assert.Contains(diffs, d => d.GetProperty("Text").GetString()!.Contains("differs only in letter case"));
        }
        Assert.Contains("No effect", File.ReadAllText(Path.Combine(game.Dir, "report", "report.md")));
        (code, stdout, stderr) = game.Patch("Base.esm", "ModA.esm", "ModB.esm");
        Assert.True(code == 0, stdout + stderr);
        Assert.StartsWith("RESULT: OK, nothing to patch", ExeTests.ResultLine(stdout));
    }

    // ... but a raw file name with another letter, not only another case, is still a real change
    [Fact]
    public void A_raw_file_name_with_another_letter_is_still_a_lost_change()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b, c) => { var x = Game.CellIn(m, c, game.Interior); x.Components.Clear(); x.Components.Add(PaddedCityMap("CityMaps\\SFBGS008\\sfCityA.dat")); });
        game.Mod("ModB.esm", (m, b, c) => { var x = Game.CellIn(m, c, game.Interior); x.Components.Clear(); x.Components.Add(PaddedCityMap("CityMaps\\SFBGS008\\SFCityB.dat")); });
        var (code, stdout, stderr) = Conflict(game, "Base.esm", "ModA.esm", "ModB.esm");
        Assert.True(code == 0, stdout + stderr);
        Assert.StartsWith("RESULT: OK, 1 lost-change entries", ExeTests.ResultLine(stdout));
    }

    [Theory]
    [InlineData("maps\\a.dat", "MAPS\\A.DAT", true)]
    [InlineData("maps\\a.dat", "maps\\a.dat", false)]      // equal: nothing to call letter case
    [InlineData("maps\\a.dat", "maps\\b.dat", false)]      // another letter
    [InlineData("maps\\a.dat", "maps|a.dat", false)]       // \ and | differ by 0x20 too, but aren't letters
    [InlineData("maps\\a.dat", "MAPS\\A.DA", false)]       // another length of text
    public void Text_blob_case_only_is_a_narrow_rule(string a, string b, bool expected)
    {
        byte[] Pad(string s) { var x = new byte[32]; System.Text.Encoding.ASCII.GetBytes(s).CopyTo(x, 0); return x; }
        Assert.Equal(expected, SfModCheck.Conflict.Impact.TextBlobCaseOnly(Pad(a), Pad(b)));
        Assert.Equal(expected, SfModCheck.Conflict.Impact.TextBlobCaseOnly(new MemorySlice<byte>(Pad(a)), new MemorySlice<byte>(Pad(b))));
    }

    [Fact]
    public void Text_blob_case_only_leaves_binary_data_and_bytes_after_the_text_alone()
    {
        // 'A' and 'a' as bytes of binary data (a byte below 0x20 before them): not text
        Assert.False(SfModCheck.Conflict.Impact.TextBlobCaseOnly(new byte[] { 1, 0x41, 2 }, new byte[] { 1, 0x61, 2 }));
        // a difference after the text's zero byte is junk in the buffer, not letter case
        Assert.False(SfModCheck.Conflict.Impact.TextBlobCaseOnly(new byte[] { 0x61, 0, 0x41 }, new byte[] { 0x61, 0, 0x61 }));
        // blobs of other lengths, and a value that isn't a blob
        Assert.False(SfModCheck.Conflict.Impact.TextBlobCaseOnly(new byte[] { 0x61 }, new byte[] { 0x41, 0 }));
        Assert.False(SfModCheck.Conflict.Impact.TextBlobCaseOnly("a", "A"));
        // code review 0.13.0, round 1: a number or flags kept as bytes is no file name (65 against 97, "AB" against "ab")
        Assert.False(SfModCheck.Conflict.Impact.TextBlobCaseOnly(new byte[] { 0x41, 0, 0, 0 }, new byte[] { 0x61, 0, 0, 0 }));
        Assert.False(SfModCheck.Conflict.Impact.TextBlobCaseOnly(new byte[] { 0x41, 0x42 }, new byte[] { 0x61, 0x62 }));
        Assert.False(SfModCheck.Conflict.Impact.TextBlobCaseOnly(new byte[] { 0x41, 0x42, 0x43, 0x44 }, new byte[] { 0x61, 0x62, 0x63, 0x64 }));
        Assert.True(SfModCheck.Conflict.Impact.TextBlobCaseOnly(new byte[] { 0x41, 0x2E, 0x42, 0 }, new byte[] { 0x61, 0x2E, 0x62, 0 }));
    }

    // step 8d: official files load before every mod (BlueprintShips files aside), even when Plugins.txt lists one after
    // a mod; a cell change the official file makes and a later mod's copy drops is restored like any mod's
    [Fact]
    public void A_cell_change_from_an_official_file_listed_after_a_mod_is_restored()
    {
        using var game = new Game();
        game.Mod("SFBGS00A.esm", (m, b, c) => Game.CellIn(m, c, game.Interior).WaterHeight = 7);
        game.Mod("ModA.esm", (m, b, c) => Game.CellIn(m, c, game.Interior).WaterEnvironmentMap = "moda.dds");
        var (code, stdout, stderr) = Conflict(game, "Base.esm", "ModA.esm", "SFBGS00A.esm");
        Assert.True(code == 0, stdout + stderr);
        Assert.StartsWith("RESULT: OK, 1 lost-change entries (1 official, 0 mod vs mod)", ExeTests.ResultLine(stdout));
        (code, stdout, stderr) = game.Patch("Base.esm", "ModA.esm", "SFBGS00A.esm");
        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("1 records, 1 fields forwarded", ExeTests.ResultLine(stdout));
        var cell = Assert.Single(game.OpenPatch().EnumerateMajorRecords<ICellGetter>());
        Assert.Equal((7f, "moda.dds"), (cell.WaterHeight, cell.WaterEnvironmentMap));
        game.ConflictIsClean();
        CheckIsClean(game);
    }

    // step 8d: a placed object and a navmesh lost to a BlueprintShips file keep the BlueprintShips reason (0.12.0)
    [Fact]
    public void A_placed_object_and_a_navmesh_lost_to_a_BlueprintShips_file_are_not_built()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b, c) =>
        {
            Move(Game.RefIn(m, c, game.RefInterior));
            Game.NavIn(m, c, game.Navmesh).NavmeshGeometry = Game.Geometry(game.Interior, 5);
        });
        game.Mod("BlueprintShips-SFBGS00A.esm", (m, b, c) =>
        {
            Game.RefIn(m, c, game.RefInterior).Scale = 2;
            Game.NavIn(m, c, game.Navmesh).EditorID = "BP_Navm";
        });
        // listing it first doesn't move it: the game loads BlueprintShips files after every plugin
        var (code, stdout, stderr) = game.Patch("Base.esm", "BlueprintShips-SFBGS00A.esm", "ModA.esm");
        Assert.True(code == 0, stdout + stderr);
        var result = ExeTests.ResultLine(stdout);
        Assert.StartsWith("RESULT: OK, nothing to patch", result);
        Assert.Contains("2 records not built", result);
        Assert.False(File.Exists(Path.Combine(game.Out, "ConflictPatch.esm")));
        var report = File.ReadAllText(Path.Combine(game.Out, "patch-report.md"));
        Assert.Contains("## Not built (2): no patch can restore these", report);
        Assert.Contains("loads after every mod, this patch too, so no patch or load order brings this change back", report);
        Assert.DoesNotContain("are one mesh", report);
    }
}
