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
using SfModCheck.Conflict;
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

    static int Count(string text, string part)
    {
        int n = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    (int Code, string Stdout, string Stderr) PickRun(Game game, string[] active, params string[] picks)
    {
        var txt = Path.Combine(game.Dir, "Plugins.txt");
        File.WriteAllLines(txt, active.Select(a => "*" + a));
        var args = new List<string> { "patch", "--data", game.Data, "--plugins", txt, "--out", game.Out, "--quiet" };
        foreach (var p in picks) { args.Add("--pick"); args.Add(p); }
        return ExeTests.RunSplit(args.ToArray());
    }

    // code review 0.13.0, round 2: a pick of the winner on a mesh field both changed is taken once, by the choice loop,
    // and gives one row (before the fix the navmesh step took the same pick again and added a second row)
    [Fact]
    public void A_pick_of_the_winner_on_a_mesh_choice_gives_one_row()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b, c) => Game.NavIn(m, c, game.Navmesh).NavmeshGeometry = Game.Geometry(game.Interior, 5));
        game.Mod("ModB.esm", (m, b, c) => Game.NavIn(m, c, game.Navmesh).NavmeshGeometry = Game.Geometry(game.Interior, 6));
        var (code, stdout, stderr) = PickRun(game, ["Base.esm", "ModA.esm", "ModB.esm"], $"{game.Navmesh}:NavmeshGeometry=ModB.esm");
        Assert.True(code == 0, stdout + stderr);
        Assert.DoesNotContain("was not used", stdout + stderr);
        var report = File.ReadAllText(Path.Combine(game.Out, "patch-report.md"));
        Assert.Equal(1, Count(report, "--pick: ModB.esm's"));
        Assert.Contains("--pick: ModB.esm's whole mesh is used", report);   // round 7: the one mesh row
    }

    // code review 0.13.0, round 2: a whole-record pick made for another field leaves a forwarded mesh change alone (before
    // the fix it took the picked plugin's mesh and dropped ModA's geometry)
    [Fact]
    public void A_whole_record_pick_keeps_a_forwarded_mesh_change()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b, c) => Game.NavIn(m, c, game.Navmesh).NavmeshGeometry = Game.Geometry(game.Interior, 5));
        game.Mod("ModC.esm", (m, b, c) => Game.NavIn(m, c, game.Navmesh).EditorID = "C_Navm");
        game.Mod("ModB.esm", (m, b, c) => Game.NavIn(m, c, game.Navmesh).EditorID = "B_Navm2");
        var (code, stdout, stderr) = PickRun(game, ["Base.esm", "ModA.esm", "ModC.esm", "ModB.esm"], $"{game.Navmesh}=ModC.esm");
        Assert.True(code == 0, stdout + stderr);
        Assert.DoesNotContain("was not used", stdout + stderr);
        var n = Assert.Single(game.OpenPatch().EnumerateMajorRecords<INavigationMeshGetter>());
        Assert.Equal("C_Navm", n.EditorID);
        Assert.Equal(5f, Assert.Single(n.NavmeshGeometry!.Vertices).Point.X);
        Assert.DoesNotContain("whole mesh is used", File.ReadAllText(Path.Combine(game.Out, "patch-report.md")));
        CheckIsClean(game);
    }

    // code review 0.13.0, round 7 (the simple rule): a mesh pick that changes the mesh leaves its one row, and the mesh
    // parts it brings are listed as picked (round 8: a pick that names today's mesh and drops no change adds none)
    [Fact]
    public void A_mesh_pick_always_leaves_one_row()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b, c) => { var n = Game.NavIn(m, c, game.Navmesh); n.NavmeshGeometry = Game.Geometry(game.Interior, 5); n.NNAM = Nnam(7); });
        game.Mod("ModB.esm", (m, b, c) => Game.NavIn(m, c, game.Navmesh).EditorID = "B_NavmRenamed");
        var (code, stdout, stderr) = PickRun(game, ["Base.esm", "ModA.esm", "ModB.esm"], $"{game.Navmesh}:NavmeshGeometry=ModA.esm");
        Assert.True(code == 0, stdout + stderr);
        Assert.DoesNotContain("was not used", stdout + stderr);
        var n = Assert.Single(game.OpenPatch().EnumerateMajorRecords<INavigationMeshGetter>());
        Assert.Equal(5f, Assert.Single(n.NavmeshGeometry!.Vertices).Point.X);
        Assert.Equal(new byte[] { 7, 0, 0, 0 }, n.NNAM!.Value.ToArray());
        var report = File.ReadAllText(Path.Combine(game.Out, "patch-report.md"));
        Assert.Equal(1, Count(report, "--pick: ModA.esm's whole mesh is used"));
        Assert.Contains("- NavmeshGeometry from ModA.esm", report);
        Assert.Contains("- NNAM from ModA.esm", report);
        Assert.Contains("picked with --pick (part of the navmesh's mesh)", report);
        CheckIsClean(game);
    }

    // code review 0.13.0, round 3: a pick the choice loop took on the geometry decides the whole mesh, so a mesh part
    // forwarded from another plugin goes, and the pick's one row says so (before the fix the report still listed ModA's
    // NNAM as forwarded while the patch held ModB's)
    [Fact]
    public void A_pick_on_a_mesh_choice_drops_another_part_and_says_so()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b, c) => { var n = Game.NavIn(m, c, game.Navmesh); n.NavmeshGeometry = Game.Geometry(game.Interior, 5); n.NNAM = Nnam(7); });
        game.Mod("ModB.esm", (m, b, c) => Game.NavIn(m, c, game.Navmesh).NavmeshGeometry = Game.Geometry(game.Interior, 6));
        var (code, stdout, stderr) = PickRun(game, ["Base.esm", "ModA.esm", "ModB.esm"], $"{game.Navmesh}:NavmeshGeometry=ModB.esm");
        Assert.True(code == 0, stdout + stderr);
        Assert.DoesNotContain("was not used", stdout + stderr);
        // ModB's whole mesh is ModB's record as it is, so no patch record is left to write
        Assert.False(File.Exists(Path.Combine(game.Out, "ConflictPatch.esm")) || File.Exists(Path.Combine(game.Data, "ConflictPatch.esm")), stdout);
        var report = File.ReadAllText(Path.Combine(game.Out, "patch-report.md"));
        Assert.DoesNotContain("- NNAM from ModA.esm", report);
        Assert.Equal(1, Count(report, "--pick: ModB.esm's"));
        Assert.Contains("--pick: ModB.esm's whole mesh is used (a navmesh's geometry, NNAM and pre-cut entries come from one plugin, " +
                        "so other plugins' changes to them are dropped)", report);
    }

    // code review 0.13.0, round 4, end to end: ModA's NNAM is today's (the winner ModB has none), ModB's geometry is
    // today's, so the mesh is mixed with no forwarded mesh line. A pick of the winner's mesh changes the patch, and
    // before the fix its row was left out (no plugin but the winner was named)
    [Fact]
    public void A_pick_of_the_winners_mesh_over_a_mixed_mesh_leaves_a_row()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b, c) => { var n = Game.NavIn(m, c, game.Navmesh); n.EditorID = "A_Navm"; n.NNAM = Nnam(7); });
        game.Mod("ModB.esm", (m, b, c) => { var n = Game.NavIn(m, c, game.Navmesh); n.NavmeshGeometry = Game.Geometry(game.Interior, 6); n.NNAM = null; });
        var (code, stdout, stderr) = PickRun(game, ["Base.esm", "ModA.esm", "ModB.esm"], $"{game.Navmesh}:NavmeshGeometry=ModB.esm");
        Assert.True(code == 0, stdout + stderr);
        Assert.DoesNotContain("was not used", stdout + stderr);
        var n = Assert.Single(game.OpenPatch().EnumerateMajorRecords<INavigationMeshGetter>());
        Assert.Equal(6f, Assert.Single(n.NavmeshGeometry!.Vertices).Point.X);
        Assert.Null(n.NNAM);   // the winner's whole mesh: ModA's NNAM goes
        var report = File.ReadAllText(Path.Combine(game.Out, "patch-report.md"));
        Assert.Contains("--pick: ModB.esm's whole mesh is used", report);
    }

    // code review 0.13.0, round 4: a pick on the geometry takes the whole mesh, the winner's own NNAM change included
    // (round 7: one row says other plugins' changes go; the parts that come from ModA are listed)
    [Fact]
    public void A_pick_on_a_mesh_choice_takes_the_winners_part_too()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b, c) => { var n = Game.NavIn(m, c, game.Navmesh); n.NavmeshGeometry = Game.Geometry(game.Interior, 5); n.NNAM = Nnam(7); });
        game.Mod("ModB.esm", (m, b, c) => { var n = Game.NavIn(m, c, game.Navmesh); n.NavmeshGeometry = Game.Geometry(game.Interior, 6); n.NNAM = Nnam(9); });
        var (code, stdout, stderr) = PickRun(game, ["Base.esm", "ModA.esm", "ModB.esm"], $"{game.Navmesh}:NavmeshGeometry=ModA.esm");
        Assert.True(code == 0, stdout + stderr);
        Assert.DoesNotContain("was not used", stdout + stderr);
        var n = Assert.Single(game.OpenPatch().EnumerateMajorRecords<INavigationMeshGetter>());
        Assert.Equal(5f, Assert.Single(n.NavmeshGeometry!.Vertices).Point.X);
        Assert.Equal(new byte[] { 7, 0, 0, 0 }, n.NNAM!.Value.ToArray());
        var report = File.ReadAllText(Path.Combine(game.Out, "patch-report.md"));
        Assert.Contains("--pick: ModA.esm's whole mesh is used", report);
        Assert.Contains("- NNAM from ModA.esm", report);   // the part that now comes from ModA is listed
        Assert.DoesNotContain("the winner ModB.esm's value is kept", report);   // no leftover row on NNAM
        CheckIsClean(game);
    }

    // code review 0.13.0, round 7 (the simple rule): field picks on one navmesh's mesh that name two plugins stop the
    // run with exit 2 and one error naming both, whether the loops would have taken them or not. Round 7 found the old
    // ranking let a pick on a Choice field (NNAM) beat a pick on a forwarded later field (the pre-cut entries); here
    // the geometry is the forwarded field (only ModC changed it) and NNAM the Choice field
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Two_mesh_picks_naming_two_plugins_stop_the_run(bool choiceFirst)
    {
        using var game = new Game();
        game.Mod("ModC.esm", (m, b, c) => Game.NavIn(m, c, game.Navmesh).NavmeshGeometry = Game.Geometry(game.Interior, 4));
        game.Mod("ModA.esm", (m, b, c) => Game.NavIn(m, c, game.Navmesh).NNAM = Nnam(7));
        game.Mod("ModB.esm", (m, b, c) => Game.NavIn(m, c, game.Navmesh).NNAM = Nnam(9));
        string nnam = $"{game.Navmesh}:NNAM=ModA.esm", geo = $"{game.Navmesh}:NavmeshGeometry=ModC.esm";
        var (code, stdout, stderr) = PickRun(game, ["Base.esm", "ModC.esm", "ModA.esm", "ModB.esm"], choiceFirst ? [nnam, geo] : [geo, nnam]);
        Assert.True(code == 2, stdout + stderr);
        Assert.Equal("RESULT: CANNOT BUILD (two --pick options name different plugins for one navmesh's mesh)", ExeTests.ResultLine(stdout));
        Assert.Contains($"--pick {(choiceFirst ? nnam : geo)} and --pick {(choiceFirst ? geo : nnam)} name two plugins for the mesh of navmesh B_Navm [{game.Navmesh}]", stderr);
        Assert.Contains("keep only the --pick that names the plugin whose mesh you want", stderr);
        Assert.False(File.Exists(Path.Combine(game.Out, "ConflictPatch.esm")));
    }

    // round 7: two mesh field picks naming one plugin (spelled two ways) are one choice, not a conflict
    [Fact]
    public void Two_mesh_picks_naming_one_plugin_take_its_mesh()
    {
        using var game = new Game();
        game.Mod("ModC.esm", (m, b, c) => Game.NavIn(m, c, game.Navmesh).NavmeshGeometry = Game.Geometry(game.Interior, 4));
        game.Mod("ModA.esm", (m, b, c) => Game.NavIn(m, c, game.Navmesh).NNAM = Nnam(7));
        game.Mod("ModB.esm", (m, b, c) => Game.NavIn(m, c, game.Navmesh).NNAM = Nnam(9));
        var (code, stdout, stderr) = PickRun(game, ["Base.esm", "ModC.esm", "ModA.esm", "ModB.esm"],
            $"{game.Navmesh}:NNAM=ModA.esm", $"{game.Navmesh}:PreCutMapEntries=moda.ESM");
        Assert.True(code == 0, stdout + stderr);
        Assert.DoesNotContain("was not used", stderr);
        var n = Assert.Single(game.OpenPatch().EnumerateMajorRecords<INavigationMeshGetter>());
        Assert.Equal(0f, Assert.Single(n.NavmeshGeometry!.Vertices).Point.X);   // ModA's whole mesh: Base's geometry
        Assert.Equal(new byte[] { 7, 0, 0, 0 }, n.NNAM!.Value.ToArray());
        Assert.Equal(1, Count(File.ReadAllText(Path.Combine(game.Out, "patch-report.md")), "--pick: ModA.esm's whole mesh is used"));
        CheckIsClean(game);
    }

    // code review 0.13.0, round 2: a parent cell left out after all (its worldspace couldn't be read again) has one
    // corrected "Not built" row, and its parent note goes
    [Fact]
    public void A_parent_cell_whose_worldspace_fails_keeps_one_corrected_row()
    {
        var plan = new PatchCommand.Plan();
        var fk = FormKey.Factory("000801:Base.esm");
        var other = FormKey.Factory("000802:Base.esm");
        var w = FormKey.Factory("000900:Base.esm");
        plan.NotBuilt.Add(new(fk, "Cell", "B_Cell", "it could not be built (X: y)", new()));
        PatchCommand.NoteNotBuiltParent(plan, fk, "1 patched placed object");
        plan.Notes.Add($"Cell B_Cell [{fk}] is in the patch as the parent of 1 patched placed object: the record as the game uses it today, with no field changed");
        plan.Notes.Add($"Cell B_Other [{other}] is in the patch as the parent of 1 patched navmesh: the record as the game uses it today, with no field changed");
        Assert.True(PatchCommand.NoteParentDropped(plan, fk, w));
        var row = Assert.Single(plan.NotBuilt);
        Assert.StartsWith("it could not be built (X: y); it was needed as the parent", row.Why);
        Assert.DoesNotContain("it is in the patch only", row.Why);
        Assert.Contains($"its worldspace {w} could not be read again", row.Why);
        Assert.Contains($"[{other}]", Assert.Single(plan.Notes));
        // a parent cell with no row of its own: its note goes, and the caller adds the row
        Assert.False(PatchCommand.NoteParentDropped(plan, other, w));
        Assert.Empty(plan.Notes);
    }

    // code review 0.13.0, round 3: the worldspace-failure branch itself. A cell there only as a parent gets a note (it has
    // no changes to redo), a cell with changes of its own gets one row, a cell whose own build failed keeps its one
    // corrected row, and every object in them gets its own row
    [Fact]
    public void Cells_of_a_worldspace_that_fails_leave_with_one_explanation_each()
    {
        var plan = new PatchCommand.Plan();
        var w = FormKey.Factory("000900:Base.esm");
        PatchCommand.PatchRecord Rec(IMajorRecord r, string type, bool parent) =>
            new(r.FormKey, type, r.EditorID, r, new(), new() { "Base.esm" }, new(), new()) { Parent = parent };
        var pure = new Cell(FormKey.Factory("000801:Base.esm"), StarfieldRelease.Starfield) { EditorID = "B_Pure" };
        var own = new Cell(FormKey.Factory("000802:Base.esm"), StarfieldRelease.Starfield) { EditorID = "B_Own" };
        var failed = new Cell(FormKey.Factory("000803:Base.esm"), StarfieldRelease.Starfield) { EditorID = "B_Failed" };
        var obj = new PlacedObject(FormKey.Factory("000811:Base.esm"), StarfieldRelease.Starfield);
        var obj2 = new PlacedObject(FormKey.Factory("000813:Base.esm"), StarfieldRelease.Starfield);
        var cells = new List<PatchCommand.PatchRecord> { Rec(pure, "Cell", true), Rec(own, "Cell", false), Rec(failed, "Cell", true) };
        plan.Records.AddRange(cells);
        plan.Records.Add(Rec(obj, "PlacedObject", false));
        plan.Records.Add(Rec(obj2, "PlacedObject", false));
        plan.RefPlaces[obj.FormKey] = new(pure.FormKey, false);
        plan.RefPlaces[obj2.FormKey] = new(failed.FormKey, false);
        plan.NotBuilt.Add(new(failed.FormKey, "Cell", "B_Failed", "it could not be built (X: y)", new()));
        PatchCommand.NoteNotBuiltParent(plan, failed.FormKey, "1 patched placed object");
        foreach (var c in new[] { pure, failed })
            plan.Notes.Add($"Cell {c.EditorID} [{c.FormKey}] is in the patch as the parent of 1 patched placed object: the record as the game uses it today, with no field changed");

        PatchCommand.DropCellsOfLostWorld(plan, w, cells);

        Assert.Empty(plan.Records);
        Assert.DoesNotContain(plan.NotBuilt, r => r.FormKey == pure.FormKey);
        var note = Assert.Single(plan.Notes);
        Assert.StartsWith($"Cell B_Pure [{pure.FormKey}] was needed only as the parent of 1 patched placed object", note);
        Assert.Contains("it has no changes of its own", note);
        Assert.Equal($"its worldspace {w} could not be read again", Assert.Single(plan.NotBuilt, r => r.FormKey == own.FormKey).Why);
        var f = Assert.Single(plan.NotBuilt, r => r.FormKey == failed.FormKey);
        Assert.StartsWith("it could not be built (X: y); it was needed as the parent", f.Why);
        foreach (var o in new[] { obj, obj2 })
            Assert.Equal($"the worldspace {w} of its cell could not be read again", Assert.Single(plan.NotBuilt, r => r.FormKey == o.FormKey).Why);
        Assert.Equal(4, plan.NotBuilt.Count);   // the two cells with changes and the two objects; the pure parent has only its note
    }

    // code review 0.13.0, round 4: each object that leaves with its cell gets a log line, a pure parent with no children
    // left reads "patched objects" (not "the parent of , but"), and a child with no place fails loudly
    [Fact]
    public void Lost_worldspace_children_are_logged_and_never_left_without_a_place()
    {
        Assert.Equal("patched objects", PatchCommand.ChildrenText([]));
        var w = FormKey.Factory("000900:Base.esm");
        PatchCommand.PatchRecord Rec(IMajorRecord r, string type, bool parent) =>
            new(r.FormKey, type, r.EditorID, r, new(), new() { "Base.esm" }, new(), new()) { Parent = parent };
        var cell = new Cell(FormKey.Factory("000801:Base.esm"), StarfieldRelease.Starfield) { EditorID = "B_Pure" };
        var obj = new PlacedObject(FormKey.Factory("000811:Base.esm"), StarfieldRelease.Starfield);

        var plan = new PatchCommand.Plan();
        var cells = new List<PatchCommand.PatchRecord> { Rec(cell, "Cell", true) };
        plan.Records.AddRange(cells);
        plan.Records.Add(Rec(obj, "PlacedObject", false));
        plan.RefPlaces[obj.FormKey] = new(cell.FormKey, false);
        using var s = new Sandbox();
        var log = Path.Combine(s.Dir, "log.txt");
        Assert.Null(Log.ToFile(log));
        try { PatchCommand.DropCellsOfLostWorld(plan, w, cells); }
        finally { Log.Close(); }
        Assert.Contains($"patch: PlacedObject {obj.FormKey} not built: the worldspace {w} of its cell could not be read again", File.ReadAllText(log));

        // the cell's only child already left the plan: the note still reads well
        var plan2 = new PatchCommand.Plan();
        var cells2 = new List<PatchCommand.PatchRecord> { Rec(cell, "Cell", true) };
        plan2.Records.AddRange(cells2);
        PatchCommand.DropCellsOfLostWorld(plan2, w, cells2);
        Assert.Contains("was needed only as the parent of patched objects, but", Assert.Single(plan2.Notes));

        // a placed object with no place is a bug: it fails instead of staying without its cell
        var plan3 = new PatchCommand.Plan();
        var cells3 = new List<PatchCommand.PatchRecord> { Rec(cell, "Cell", true) };
        plan3.Records.AddRange(cells3);
        plan3.Records.Add(Rec(obj, "PlacedObject", false));
        // round 5: with a message that names the record and says it is a bug to report (not a bare KeyNotFoundException)
        var e = Assert.Throws<InvalidOperationException>(() => PatchCommand.DropCellsOfLostWorld(plan3, w, cells3));
        Assert.Contains($"PlacedObject [{obj.FormKey}] has no planned place in a cell", e.Message);
        Assert.Contains("a bug in sf-mod-check: please report it with the --log file", e.Message);
    }

    // code review 0.13.0, round 5: a field pick on a mesh field ranks above a whole-record pick that took a later mesh
    // field, as it does for every other field. Before the fix the whole-record pick (on NNAM) decided ModC's mesh and the
    // explicit geometry pick was warned as unused; the whole-record pick's warning now names the mesh field it lost
    [Fact]
    public void A_field_mesh_pick_beats_a_whole_record_pick_on_a_later_mesh_field()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b, c) => { var n = Game.NavIn(m, c, game.Navmesh); n.NavmeshGeometry = Game.Geometry(game.Interior, 5); n.NNAM = Nnam(7); });
        game.Mod("ModC.esm", (m, b, c) => { var n = Game.NavIn(m, c, game.Navmesh); n.NavmeshGeometry = Game.Geometry(game.Interior, 4); n.NNAM = Nnam(8); });
        game.Mod("ModB.esm", (m, b, c) => { var n = Game.NavIn(m, c, game.Navmesh); n.NavmeshGeometry = Game.Geometry(game.Interior, 6); n.NNAM = Nnam(9); });
        var (code, stdout, stderr) = PickRun(game, ["Base.esm", "ModA.esm", "ModC.esm", "ModB.esm"],
            $"{game.Navmesh}=ModC.esm", $"{game.Navmesh}:NavmeshGeometry=ModA.esm");
        Assert.True(code == 0, stdout + stderr);
        Assert.DoesNotContain("NavmeshGeometry=ModA.esm was not used", stderr);
        // the mesh was the only thing the whole-record pick could take, so it gets "was not used" with the reason
        Assert.Contains($"WARNING: --pick {game.Navmesh}=ModC.esm was not used: the --pick on NavmeshGeometry (ModA.esm) decides that navmesh's whole mesh, " +
                        "and a field --pick ranks above a whole-record --pick", stderr);
        var n = Assert.Single(game.OpenPatch().EnumerateMajorRecords<INavigationMeshGetter>());
        Assert.Equal(5f, Assert.Single(n.NavmeshGeometry!.Vertices).Point.X);   // ModA's whole mesh
        Assert.Equal(new byte[] { 7, 0, 0, 0 }, n.NNAM!.Value.ToArray());
        var report = File.ReadAllText(Path.Combine(game.Out, "patch-report.md"));
        Assert.Equal(1, Count(report, "--pick: ModA.esm's whole mesh is used"));
        Assert.DoesNotContain("- NNAM from ModC.esm", report);
        CheckIsClean(game);
    }

    // code review 0.13.0, round 5: the winner ModB only carried ModA's NNAM forward; a geometry pick of ModC takes ModC's
    // whole mesh (Base's NNAM), and the one row names no one's part (round 7 dropped the per-part crediting)
    [Fact]
    public void A_geometry_pick_takes_the_whole_mesh_over_a_carried_forward_nnam()
    {
        using var game = new Game();
        game.Mod("ModC.esm", (m, b, c) => Game.NavIn(m, c, game.Navmesh).NavmeshGeometry = Game.Geometry(game.Interior, 4));
        game.Mod("ModA.esm", (m, b, c) => Game.NavIn(m, c, game.Navmesh).NNAM = Nnam(7));
        game.Mod("ModB.esm", (m, b, c) => { var n = Game.NavIn(m, c, game.Navmesh); n.NavmeshGeometry = Game.Geometry(game.Interior, 6); n.NNAM = Nnam(7); });
        var (code, stdout, stderr) = PickRun(game, ["Base.esm", "ModC.esm", "ModA.esm", "ModB.esm"], $"{game.Navmesh}:NavmeshGeometry=ModC.esm");
        Assert.True(code == 0, stdout + stderr);
        Assert.DoesNotContain("was not used", stdout + stderr);
        var n = Assert.Single(game.OpenPatch().EnumerateMajorRecords<INavigationMeshGetter>());
        Assert.Equal(4f, Assert.Single(n.NavmeshGeometry!.Vertices).Point.X);
        Assert.Null(n.NNAM);   // ModC's whole mesh: Base's NNAM (none)
        var report = File.ReadAllText(Path.Combine(game.Out, "patch-report.md"));
        Assert.Equal(1, Count(report, "--pick: ModC.esm's whole mesh is used"));
        Assert.DoesNotContain("- NNAM from ModA.esm", report);
        CheckIsClean(game);
    }

    // code review 0.13.0, round 5: a field pick's row credits a dropped change to the version that brought the value in
    // (0 for the first version's value), so a plugin that only carried it forward isn't named
    [Fact]
    public void IntroducedAt_credits_the_version_that_brought_the_value_in()
    {
        object?[] values = ["base", "a", "a", "c", "c"];
        Assert.Equal(1, PatchCommand.IntroducedAt(i => values[i], 2));
        Assert.Equal(3, PatchCommand.IntroducedAt(i => values[i], 4));
        Assert.Equal(0, PatchCommand.IntroducedAt(i => new object?[] { "x", "x", "x" }[i], 2));
        Assert.Equal(0, PatchCommand.IntroducedAt(i => values[i], 0));
        // round 6: the first version's value held again after another plugin changed it is nobody's change
        Assert.Equal(0, PatchCommand.IntroducedAt(i => new object?[] { "x", "y", "x" }[i], 2));
        Assert.Equal(0, PatchCommand.IntroducedAt(i => new object?[] { null, "y", null, null }[i], 3));
    }

    // code review 0.13.0, rounds 6 and 7: a field pick on a forwarded mesh field (the geometry, which only ModA changed)
    // beats a whole-record pick that the mesh choice (NNAM) would give the mesh to; the whole-record pick, left with
    // nothing to take, says why, and the report has the one mesh row (round 7 found a case with no row at all)
    [Fact]
    public void A_field_pick_on_a_forwarded_mesh_field_beats_a_whole_record_pick()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b, c) => Game.NavIn(m, c, game.Navmesh).NavmeshGeometry = Game.Geometry(game.Interior, 5));
        game.Mod("ModC.esm", (m, b, c) => Game.NavIn(m, c, game.Navmesh).NNAM = Nnam(8));
        game.Mod("ModB.esm", (m, b, c) => Game.NavIn(m, c, game.Navmesh).NNAM = Nnam(9));
        var (code, stdout, stderr) = PickRun(game, ["Base.esm", "ModA.esm", "ModC.esm", "ModB.esm"],
            $"{game.Navmesh}=ModC.esm", $"{game.Navmesh}:NavmeshGeometry=ModA.esm");
        Assert.True(code == 0, stdout + stderr);
        Assert.DoesNotContain("NavmeshGeometry=ModA.esm was not used", stderr);
        Assert.Contains($"WARNING: --pick {game.Navmesh}=ModC.esm was not used: the --pick on NavmeshGeometry (ModA.esm) decides that navmesh's whole mesh", stderr);
        var n = Assert.Single(game.OpenPatch().EnumerateMajorRecords<INavigationMeshGetter>());
        Assert.Equal(5f, Assert.Single(n.NavmeshGeometry!.Vertices).Point.X);   // ModA's whole mesh
        Assert.Null(n.NNAM);                                                     // ModA's NNAM is Base's (none)
        var report = File.ReadAllText(Path.Combine(game.Out, "patch-report.md"));
        Assert.Equal(1, Count(report, "--pick: ModA.esm's whole mesh is used"));
        Assert.DoesNotContain("ModC.esm's value is used", report);
        CheckIsClean(game);
    }

    // code review 0.13.0, round 6: two losers changed NNAM (ModX's value forwarded, ModP's in the "both changed" row) and
    // a geometry pick decides the mesh: both their lines and rows go, and the one mesh row is left (round 7)
    [Fact]
    public void A_mesh_pick_drops_every_other_change_to_the_mesh()
    {
        using var game = new Game();
        game.Mod("ModP.esm", (m, b, c) => Game.NavIn(m, c, game.Navmesh).NNAM = Nnam(7));
        game.Mod("ModX.esm", (m, b, c) => Game.NavIn(m, c, game.Navmesh).NNAM = Nnam(8));
        game.Mod("ModC.esm", (m, b, c) => Game.NavIn(m, c, game.Navmesh).NavmeshGeometry = Game.Geometry(game.Interior, 4));
        game.Mod("ModB.esm", (m, b, c) => Game.NavIn(m, c, game.Navmesh).NavmeshGeometry = Game.Geometry(game.Interior, 6));
        var (code, stdout, stderr) = PickRun(game, ["Base.esm", "ModP.esm", "ModX.esm", "ModC.esm", "ModB.esm"], $"{game.Navmesh}:NavmeshGeometry=ModC.esm");
        Assert.True(code == 0, stdout + stderr);
        Assert.DoesNotContain("was not used", stdout + stderr);
        var n = Assert.Single(game.OpenPatch().EnumerateMajorRecords<INavigationMeshGetter>());
        Assert.Equal(4f, Assert.Single(n.NavmeshGeometry!.Vertices).Point.X);
        Assert.Null(n.NNAM);
        var report = File.ReadAllText(Path.Combine(game.Out, "patch-report.md"));
        Assert.Equal(1, Count(report, "--pick: ModC.esm's whole mesh is used"));
        Assert.DoesNotContain("- NNAM from ModX.esm", report);
        Assert.DoesNotContain("ModP.esm's value is kept", report);
        CheckIsClean(game);
    }

    // code review 0.13.0, round 7: a parent cell whose record can't be composed takes its placed object out with a row
    // that says so, and the rest of the patch is still written (before, the exception stopped the whole run)
    [Fact]
    public void A_parent_cell_that_cant_be_built_leaves_with_its_objects()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b, c) => Move(Game.RefIn(m, c, game.RefInterior)));
        game.Mod("ModB.esm", (m, b, c) => Game.RefIn(m, c, game.RefInterior).Scale = 2);
        game.Mod("ModW.esm", (m, b, c) => Game.WorldIn(m, b).EnvironmentMap = "modw.dds");
        game.Mod("ModV.esm", (m, b, c) => Game.WorldIn(m, b).WaterEnvironmentMap = "modv.dds");
        var plan = InProcessPlan(game, ["Base.esm", "ModA.esm", "ModB.esm", "ModW.esm", "ModV.esm"], fk => fk == game.Interior);
        Assert.DoesNotContain(plan.Records, r => r.FormKey == game.Interior || r.FormKey == game.RefInterior);
        var row = Assert.Single(plan.NotBuilt, r => r.FormKey == game.RefInterior);
        Assert.Equal($"its cell {game.Interior} could not be built (InvalidOperationException: made to fail by a test)", row.Why);
        Assert.Contains(plan.Records, r => r.FormKey == game.World);   // the rest of the patch is still there
    }

    // round 7: a worldspace whose own build failed but is needed as a patched cell's parent goes in as the game uses it
    // today, and its "Not built" row says so; one whose parent copy can't be built either takes its cells out with it
    [Theory]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void A_worldspace_that_failed_to_build_but_is_a_parent_says_so(int failures)
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b, c) => Game.CellIn(m, c, game.Exterior).WaterHeight = 7);
        game.Mod("ModB.esm", (m, b, c) => Game.CellIn(m, c, game.Exterior).WaterEnvironmentMap = "modb.dds");
        game.Mod("ModW.esm", (m, b, c) => Game.WorldIn(m, b).EnvironmentMap = "modw.dds");
        game.Mod("ModV.esm", (m, b, c) => Game.WorldIn(m, b).WaterEnvironmentMap = "modv.dds");
        int left = failures;
        var plan = InProcessPlan(game, ["Base.esm", "ModA.esm", "ModB.esm", "ModW.esm", "ModV.esm"], fk => fk == game.World && left-- > 0);
        var row = Assert.Single(plan.NotBuilt, r => r.FormKey == game.World);
        Assert.StartsWith("it could not be built (InvalidOperationException: made to fail by a test)", row.Why);
        if (failures == 1)
        {
            Assert.Contains("; it is in the patch only as the parent of 1 patched cell, as the game uses it today, so its own lost changes are still not patched", row.Why);
            Assert.True(Assert.Single(plan.Records, r => r.FormKey == game.World).Parent);
            Assert.Contains(plan.Records, r => r.FormKey == game.Exterior);
        }
        else
        {
            Assert.DoesNotContain(plan.Records, r => r.FormKey == game.World || r.FormKey == game.Exterior);
            Assert.Equal($"its worldspace {game.World} could not be built (InvalidOperationException: made to fail by a test)",
                Assert.Single(plan.NotBuilt, r => r.FormKey == game.Exterior).Why);
        }
    }

    // code review 0.13.0, round 8: a navmesh left out with its parent cell takes its pick's row with it, and the pick
    // counts as not used, with the "Not built" reason (before, the report said the picked mesh was used)
    [Fact]
    public void A_pick_on_a_navmesh_left_out_with_its_cell_is_not_used()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b, c) => Game.NavIn(m, c, game.Navmesh).NavmeshGeometry = Game.Geometry(game.Interior, 5));
        game.Mod("ModB.esm", (m, b, c) => Game.NavIn(m, c, game.Navmesh).NavmeshGeometry = Game.Geometry(game.Interior, 6));
        var pick = new PatchCommand.Pick(game.Navmesh, "NavmeshGeometry", "ModA.esm");
        var plan = InProcessPlan(game, ["Base.esm", "ModA.esm", "ModB.esm"], fk => fk == game.Interior, [pick]);
        Assert.Contains(plan.NotBuilt, r => r.FormKey == game.Navmesh);
        Assert.DoesNotContain(plan.Choices, c => c.FormKey == game.Navmesh);
        Assert.False(pick.Used);
        Assert.StartsWith("that record isn't built: its cell ", pick.Why);
    }

    // round 8: a mesh pick that names today's mesh and drops nobody's change counts as taken but adds no row, as on
    // every other field (before, it gave a row naming the winner as the other plugin)
    [Fact]
    public void A_mesh_pick_of_todays_mesh_adds_no_row()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b, c) => Game.NavIn(m, c, game.Navmesh).EditorID = "A_Navm");
        game.Mod("ModB.esm", (m, b, c) => Game.NavIn(m, c, game.Navmesh).NavmeshGeometry = Game.Geometry(game.Interior, 6));
        var (code, stdout, stderr) = PickRun(game, ["Base.esm", "ModA.esm", "ModB.esm"], $"{game.Navmesh}:NavmeshGeometry=ModB.esm");
        Assert.True(code == 0, stdout + stderr);
        Assert.DoesNotContain("was not used", stdout + stderr);
        var n = Assert.Single(game.OpenPatch().EnumerateMajorRecords<INavigationMeshGetter>());
        Assert.Equal("A_Navm", n.EditorID);
        Assert.Equal(6f, Assert.Single(n.NavmeshGeometry!.Vertices).Point.X);
        Assert.DoesNotContain("whole mesh is used", File.ReadAllText(Path.Combine(game.Out, "patch-report.md")));
    }

    // round 8: a whole-record pick and a field pick that name the same plugin's mesh give no warning
    [Fact]
    public void A_whole_and_a_field_mesh_pick_naming_one_plugin_give_no_warning()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b, c) => Game.NavIn(m, c, game.Navmesh).NavmeshGeometry = Game.Geometry(game.Interior, 5));
        game.Mod("ModB.esm", (m, b, c) => Game.NavIn(m, c, game.Navmesh).NavmeshGeometry = Game.Geometry(game.Interior, 6));
        var (code, stdout, stderr) = PickRun(game, ["Base.esm", "ModA.esm", "ModB.esm"], $"{game.Navmesh}=ModA.esm", $"{game.Navmesh}:NNAM=ModA.esm");
        Assert.True(code == 0, stdout + stderr);
        Assert.DoesNotContain("was not used", stdout + stderr);
        Assert.Equal(5f, Assert.Single(Assert.Single(game.OpenPatch().EnumerateMajorRecords<INavigationMeshGetter>()).NavmeshGeometry!.Vertices).Point.X);
    }

    // round 8: a whole-record pick on a navmesh whose mesh isn't a Choice field says why it leaves the mesh alone and
    // how to take it, not the general "or the record isn't patched" text
    [Fact]
    public void A_whole_record_pick_on_a_mesh_that_isnt_a_choice_says_why()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b, c) => Game.NavIn(m, c, game.Navmesh).NavmeshGeometry = Game.Geometry(game.Interior, 5));
        game.Mod("ModB.esm", (m, b, c) => Game.NavIn(m, c, game.Navmesh).EditorID = "B_Navm");
        var (code, stdout, stderr) = PickRun(game, ["Base.esm", "ModA.esm", "ModB.esm"], $"{game.Navmesh}=ModA.esm");
        Assert.True(code == 0, stdout + stderr);
        Assert.Contains($"WARNING: --pick {game.Navmesh}=ModA.esm was not used: that navmesh's mesh isn't a field the winner and another plugin both changed", stderr);
        Assert.Contains($"use --pick {game.Navmesh}:NavmeshGeometry=ModA.esm", stderr);
        Assert.DoesNotContain("or the record isn't patched", stderr);
    }

    // round 8 (found earlier in the code): a pick naming a plugin whose navmesh copy is partial and lacks part of the
    // mesh is refused with a reason, instead of writing a mesh with that part empty
    [Fact]
    public void A_mesh_pick_on_a_partial_copy_without_the_geometry_is_refused()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b, c) => { var n = Game.NavIn(m, c, game.Navmesh); n.NavmeshGeometry = null; n.NNAM = Nnam(7); n.MajorRecordFlagsRaw |= 0x4000; });
        game.Mod("ModB.esm", (m, b, c) => Game.NavIn(m, c, game.Navmesh).NavmeshGeometry = Game.Geometry(game.Interior, 6));
        var (code, stdout, stderr) = PickRun(game, ["Base.esm", "ModA.esm", "ModB.esm"], $"{game.Navmesh}:NNAM=ModA.esm");
        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("ModA.esm's copy of that navmesh is partial and doesn't hold its whole mesh", stderr);
        if (File.Exists(Path.Combine(game.Out, "ConflictPatch.esm")))
            foreach (var n in game.OpenPatch().EnumerateMajorRecords<INavigationMeshGetter>())
                Assert.NotNull(n.NavmeshGeometry);
    }

    // round 7: a pick on a record that isn't built says so, with the "Not built" reason, not the general text
    [Fact]
    public void A_pick_on_a_record_that_isnt_built_says_why()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b, c) => Move(Game.RefIn(m, c, game.RefInterior)));
        game.Mod("BlueprintShips-SFBGS00A.esm", (m, b, c) => Game.RefIn(m, c, game.RefInterior).Scale = 2);
        var (code, stdout, stderr) = PickRun(game, ["Base.esm", "BlueprintShips-SFBGS00A.esm", "ModA.esm"], $"{game.RefInterior}:Position=ModA.esm");
        Assert.True(code == 0, stdout + stderr);
        Assert.Contains($"WARNING: --pick {game.RefInterior}:Position=ModA.esm was not used: that record isn't built: ", stderr);
        Assert.Contains("BlueprintShips-SFBGS00A.esm loads after every mod, this patch too", stderr);
        Assert.DoesNotContain("or the record isn't patched", stderr);
    }

    /// <summary>The patch plan worked out in this process (as `patch` does), with Compose made to fail for the records
    /// <paramref name="fails"/> picks.</summary>
    static PatchCommand.Plan InProcessPlan(Game game, string[] active, Func<FormKey, bool> fails, List<PatchCommand.Pick>? picks = null)
    {
        var txt = Path.Combine(game.Dir, "Plugins.txt");
        File.WriteAllLines(txt, active.Select(a => "*" + a));
        var lo = LoadOrderSetup.Build(game.Data, txt);
        try
        {
            PatchCommand.ComposeFailsForTest.Value = fails;
            return PatchCommand.Build(lo, Analyzer.Run(lo.Plugins), new HashSet<string>(), picks ?? new());
        }
        finally
        {
            PatchCommand.ComposeFailsForTest.Value = null;
            foreach (var p in lo.Plugins) p.Mod.Dispose();
        }
    }

    // code review 0.13.0, round 2: whether a component kind is an identity is worked out once per field over every
    // version, so the same plugins give the same list in either order
    [Fact]
    public void Component_lists_merge_the_same_in_either_load_order()
    {
        List<string> Files(string[] order)
        {
            using var game = new Game();
            game.Mod("ModA.esm", (m, b, c) => { var x = Game.CellIn(m, c, game.Interior); x.Components.Clear(); x.Components.Add(Game.CityMap("maps\\a.dat")); });
            game.Mod("ModC.esm", (m, b, c) => Game.CellIn(m, c, game.Interior).Components.Add(Game.CityMap("maps\\c.dat")));
            game.Mod("ModB.esm", (m, b, c) => Game.CellIn(m, c, game.Interior).WaterHeight = 3);
            var (code, stdout, stderr) = game.Patch(order);
            Assert.True(code == 0, stdout + stderr);
            var cell = Assert.Single(game.OpenPatch().EnumerateMajorRecords<ICellGetter>(), x => x.FormKey == game.Interior);
            return cell.Components.OfType<ICityMapsUsageComponentGetter>()
                .Select(x => System.Text.Encoding.ASCII.GetString(x.MOD2!.Value.ToArray()).TrimEnd('\0')).OrderBy(s => s).ToList();
        }
        var one = Files(["Base.esm", "ModA.esm", "ModC.esm", "ModB.esm"]);
        var two = Files(["Base.esm", "ModC.esm", "ModA.esm", "ModB.esm"]);
        Assert.Equal(new[] { "maps\\a.dat", "maps\\c.dat" }, one);
        Assert.Equal(one, two);
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
        // relay step 22: a field pick works on any field and can add a record with no lost change
        Assert.Contains("**A field pick works on any field** (since 0.13.0)", Text("README.md"));
        Assert.Contains("Since 0.13.0 a field pick works on any field", Text("AGENTS.md"));
        Assert.Contains("A field `--pick` (`<FormKey>:<Field>=<plugin>`) now works on any field", Text("CHANGELOG.md"));
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

    // ---------- relay step 22: a field --pick keeps a chosen plugin's value on any field (the city-water case) ----------

    /// <summary>ModO fills the worldspace's empty water field with its own water record (and, with more, also changes
    /// its water environment map).</summary>
    static void FillWater(StarfieldMod m, IStarfieldModGetter b, bool more = false)
    {
        var water = m.Waters.AddNew("O_WaterClear");
        var w = Game.WorldIn(m, b);
        w.Water.SetTo(water);
        if (more) w.WaterEnvironmentMap = "modo.dds";
    }

    // The forwarded field (Paradiso): ModO's water is a lost change the winner ModW left alone, so patch forwards it; a
    // pick of the winner keeps the winner's (empty) value instead, and the report names the change it drops.
    [Fact]
    public void A_field_pick_keeps_the_winners_value_on_a_forwarded_field()
    {
        using var game = new Game();
        game.Mod("ModO.esm", (m, b, c) => FillWater(m, b, more: true));
        game.Mod("ModW.esm", (m, b, c) => Game.WorldIn(m, b).EnvironmentMap = "modw.dds");
        var (code0, stdout0, stderr0, log0) = PickLogged(game, ["Base.esm", "ModO.esm", "ModW.esm"]);
        Assert.True(code0 == 0, stdout0 + stderr0);
        Assert.Contains("(Water from ModO.esm, WaterEnvironmentMap from ModO.esm)", log0);   // without the pick: ModO's water is forwarded
        Directory.Delete(game.Out, true);
        var (code, stdout, stderr) = PickRun(game, ["Base.esm", "ModO.esm", "ModW.esm"], $"{game.World}:Water=ModW.esm");
        Assert.True(code == 0, stdout + stderr);
        Assert.DoesNotContain("was not used", stdout + stderr);
        Assert.Contains("1 records, 1 fields forwarded, 1 picks used", ExeTests.ResultLine(stdout));
        var world = Assert.Single(game.OpenPatch().Worldspaces);
        Assert.True(world.Water.IsNull);
        Assert.Equal(("modw.dds", "modo.dds"), (world.EnvironmentMap, world.WaterEnvironmentMap));   // the other forwarded field stays
        Assert.Contains("--pick: ModW.esm's value is used; ModO.esm's change to Water is dropped", File.ReadAllText(Path.Combine(game.Out, "patch-report.md")));
    }

    // When the pick leaves the record equal to today's, it is left out, and the pick still counts as used.
    [Fact]
    public void A_field_pick_that_undoes_the_only_forwarded_change_leaves_the_record_out()
    {
        using var game = new Game();
        game.Mod("ModO.esm", (m, b, c) => FillWater(m, b));
        game.Mod("ModW.esm", (m, b, c) => Game.WorldIn(m, b).EnvironmentMap = "modw.dds");
        var (code, stdout, stderr) = PickRun(game, ["Base.esm", "ModO.esm", "ModW.esm"], $"{game.World}:Water=Base.esm");
        Assert.True(code == 0, stdout + stderr);
        Assert.DoesNotContain("was not used", stdout + stderr);
        Assert.StartsWith("RESULT: OK, nothing to patch", ExeTests.ResultLine(stdout));
        var report = File.ReadAllText(Path.Combine(game.Out, "patch-report.md"));
        Assert.Contains("--pick: Base.esm's value is used; ModO.esm's change to Water is dropped", report);
        Assert.Contains("the patched record would equal today's, so it is left out", report);
    }

    // A record with no lost change (Hopetown): ModO wins the worldspace, so nothing is lost; a field pick adds the record
    // as the game uses it today with that one field from the plugin picked.
    [Fact]
    public void A_field_pick_adds_a_record_that_has_no_lost_change()
    {
        using var game = new Game();
        game.Mod("ModO.esm", (m, b, c) => FillWater(m, b, more: true));
        var (code0, stdout0, stderr0) = PickRun(game, ["Base.esm", "ModO.esm"]);
        Assert.True(code0 == 0, stdout0 + stderr0);
        Assert.StartsWith("RESULT: OK, nothing to patch", ExeTests.ResultLine(stdout0));
        var (code, stdout, stderr, log) = PickLogged(game, ["Base.esm", "ModO.esm"], $"{game.World}:Water=Base.esm");
        Assert.True(code == 0, stdout + stderr);
        Assert.DoesNotContain("was not used", stdout + stderr);
        Assert.Contains("1 records, 0 fields forwarded, 1 picks used", ExeTests.ResultLine(stdout));
        var patch = game.OpenPatch();
        var world = Assert.Single(patch.Worldspaces);
        Assert.True(world.Water.IsNull);
        Assert.Equal("modo.dds", world.WaterEnvironmentMap);   // everything else as the game uses it today
        Assert.Empty(world.SubCells);
        Assert.Single(patch.EnumerateMajorRecords());
        Assert.Contains("--pick: Base.esm's value is used; ModO.esm's change to Water is dropped", File.ReadAllText(Path.Combine(game.Out, "patch-report.md")));
        Assert.Contains($"patch: Worldspace B_World [{game.World}]: --pick keeps Base.esm's Water", log);
        var (checkCode, checkOut, checkErr) = ExeTests.RunSplit("check", Path.Combine(game.Out, "ConflictPatch.esm"), "--data", game.Data, "--quiet", "--no-assets");
        Assert.True(checkCode == 0, checkOut + checkErr);
    }

    // A worldspace that would be a parent (Neon): the pick makes it a patch record of its own, with the picked field,
    // and its patched cell still sits in it.
    [Fact]
    public void A_field_pick_on_a_parent_worldspace_makes_it_a_patch_record()
    {
        using var game = new Game();
        game.Mod("ModA.esm", (m, b, c) => Game.CellIn(m, c, game.Exterior).WaterHeight = 7);
        game.Mod("ModB.esm", (m, b, c) => Game.CellIn(m, c, game.Exterior).WaterEnvironmentMap = "modb.dds");
        game.Mod("ModO.esm", (m, b, c) => FillWater(m, b));
        var (code, stdout, stderr) = PickRun(game, ["Base.esm", "ModA.esm", "ModB.esm", "ModO.esm"], $"{game.World}:Water=Base.esm");
        Assert.True(code == 0, stdout + stderr);
        Assert.DoesNotContain("was not used", stdout + stderr);
        Assert.Contains("2 records, 1 fields forwarded, 1 picks used", ExeTests.ResultLine(stdout));
        var world = Assert.Single(game.OpenPatch().Worldspaces);
        Assert.True(world.Water.IsNull);
        var cell = Assert.Single(world.SubCells.SelectMany(x => x.Items).SelectMany(x => x.Items));
        Assert.Equal((7f, "modb.dds"), (cell.WaterHeight, cell.WaterEnvironmentMap));
        Assert.DoesNotContain("the parent of patched cells", File.ReadAllText(Path.Combine(game.Out, "patch-report.md")));
    }

    // Picks that can't be used say why, each in its own warning, and the last usable pick on a field decides.
    [Fact]
    public void A_field_pick_that_cant_be_used_says_why()
    {
        using var game = new Game();
        game.Mod("ModO.esm", (m, b, c) => FillWater(m, b, more: true));
        game.Mod("ModX.esm", (m, b, c) => m.MiscItems.AddNew("X_Misc"));
        FormKey misc;
        using (var gd = new GameData(game.Data)) misc = Assert.Single(gd.Open("Base.esm").MiscItems).FormKey;
        var (code, stdout, stderr) = PickRun(game, ["Base.esm", "ModO.esm", "ModX.esm"],
            $"{game.World}:Nope=Base.esm", $"{game.World}:Water=ModX.esm", $"{misc}:Name=Base.esm",
            $"{game.World}:WaterEnvironmentMap=ModO.esm", $"{game.World}:WaterEnvironmentMap=Base.esm");
        Assert.True(code == 0, stdout + stderr);
        Assert.Contains($"WARNING: --pick {game.World}:Nope=Base.esm was not used: Worldspace records have no field Nope", stderr);
        Assert.Contains($"WARNING: --pick {game.World}:Water=ModX.esm was not used: ModX.esm doesn't hold that record", stderr);
        Assert.Contains($"WARNING: --pick {misc}:Name=Base.esm was not used: no mod in the load order overrides that record", stderr);
        Assert.Contains($"WARNING: --pick {game.World}:WaterEnvironmentMap=ModO.esm was not used: a later --pick on the same field names Base.esm", stderr);
        Assert.DoesNotContain("WaterEnvironmentMap=Base.esm was not used", stderr);
        var world = Assert.Single(game.OpenPatch().Worldspaces);
        Assert.Null(world.WaterEnvironmentMap);   // Base.esm's (none)
        Assert.False(world.Water.IsNull);         // no usable pick on Water: today's (ModO's)
    }

    // The general warning text for a pick nothing took, with and without a field.
    [Fact]
    public void The_not_used_warning_names_the_field_or_the_reason()
    {
        var fk = FormKey.Factory("000801:Base.esm");
        Assert.Equal($"WARNING: --pick {fk}=ModA.esm was not used: that record has no Choice field that ModA.esm supplies, or the record isn't patched",
            PatchCommand.NotUsedWarning(new PatchCommand.Pick(fk, null, "ModA.esm")));
        // round 7: "or the record isn't patched" for a field pick too (a record with lost changes can still not be built)
        Assert.Equal($"WARNING: --pick {fk}:Value=ModA.esm was not used: that record has no field Value that ModA.esm supplies, or the record isn't patched",
            PatchCommand.NotUsedWarning(new PatchCommand.Pick(fk, "Value", "ModA.esm")));
        Assert.Equal($"WARNING: --pick {fk}:Value=ModA.esm was not used: why",
            PatchCommand.NotUsedWarning(new PatchCommand.Pick(fk, "Value", "ModA.esm") { Why = "why" }));
    }

    (int Code, string Stdout, string Stderr, string Log) PickLogged(Game game, string[] active, params string[] picks)
    {
        var txt = Path.Combine(game.Dir, "Plugins.txt");
        File.WriteAllLines(txt, active.Select(a => "*" + a));
        var log = Path.Combine(game.Dir, "patch.log");
        var args = new List<string> { "patch", "--data", game.Data, "--plugins", txt, "--out", game.Out, "--quiet", "--log", log };
        foreach (var p in picks) { args.Add("--pick"); args.Add(p); }
        var (code, stdout, stderr) = ExeTests.RunSplit(args.ToArray());
        return (code, stdout, stderr, File.Exists(log) ? File.ReadAllText(log) : "");
    }
}
