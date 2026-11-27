// sf-mod-check 0.27.0, code review round 5: a partial winning copy of a cell (official plugins ship them, such as a
// city cell in an update plugin) leaves out the fields it doesn't change, and the cell's lines take those from the
// earlier copy. All plugins are made up: Base.esm holds a named interior cell with a location, and Late.esm changes
// it as a partial form that carries only its EditorID. No game files are needed.
using BuildKit;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using Xunit;

namespace SfModCheck.Tests;

public class CellPartialTests
{
    [Fact]
    public void A_partial_winning_cell_keeps_the_earlier_copy_s_name_and_location()
    {
        using var s = new Sandbox();
        var data = s.Sub("Data");
        var opts = new WriteOptions { GapCheck = false, FixEffects = false };
        var b = Plugin.New("Base.esm", PluginStyle.Full, "test", "made-up base game");
        var loc = b.Locations.AddNew("P_Loc");
        var misc = b.MiscItems.AddNew("P_Misc");
        var hall = new Cell(b) { EditorID = "P_Hall", Flags = Cell.Flag.IsInteriorCell, Name = "The Hall", Location = loc.ToNullableLink<ILocationGetter>() };
        hall.Temporary.Add(new PlacedObject(b) { Base = misc.ToLink<IPlaceableObjectGetter>() });
        b.Cells.Add(new CellBlock
        {
            BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellBlock,
            SubBlocks = new Noggog.ExtendedList<CellSubBlock> { new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock, Cells = new Noggog.ExtendedList<Cell> { hall } } },
        });
        using (var g = new GameData(data)) PluginWriter.Write(b, data, g, opts);
        using (var g = new GameData(data))
        {
            var cache = g.Open("Base.esm").ToImmutableLinkCache();
            var late = Plugin.New("Late.esm", PluginStyle.Light, "test", "made-up update");
            var c = (Cell)cache.ResolveAllContexts<ICell, ICellGetter>(hall.FormKey).First().GetOrAddAsOverride(late);
            c.Name = null;
            c.Location.Clear();
            c.MajorRecordFlagsRaw |= 0x4000;
            PluginWriter.Write(late, data, g, opts);
        }
        var txt = Path.Combine(s.Dir, "Plugins.txt");
        File.WriteAllLines(txt, ["*Base.esm", "*Late.esm"]);
        var (code, o, e) = ExeTests.RunSplit("cell", "P_Hall", "--data", data, "--plugins", txt, "--quiet");
        Assert.True(code == 0, o + e);
        Assert.Contains($"Cell P_Hall [{hall.FormKey}] \"The Hall\" (interior)", o);
        Assert.Contains("location: P_Loc [", o);
        Assert.Contains("added by Base.esm; changed by Late.esm", o);
    }

    // Base.esm: worldspace P_World with a location, and its exterior cell P_Ext at grid 1,2 with no location of its own.
    // Late.esm changes both as partial forms: the worldspace without its location, the cell without its grid.
    [Fact]
    public void A_partial_winning_worldspace_and_exterior_cell_keep_the_earlier_location_and_grid()
    {
        using var s = new Sandbox();
        var data = s.Sub("Data");
        var opts = new WriteOptions { GapCheck = false, FixEffects = false };
        var b = Plugin.New("Base.esm", PluginStyle.Full, "test", "made-up base game");
        var loc = b.Locations.AddNew("P_WorldLoc");
        var misc = b.MiscItems.AddNew("P_Misc");
        var world = b.Worldspaces.AddNew("P_World");
        world.Location = loc.ToNullableLink<ILocationGetter>();
        var ext = new Cell(b) { EditorID = "P_Ext", Grid = new CellGrid { Point = new Noggog.P2Int(1, 2) } };
        ext.Temporary.Add(new PlacedObject(b) { Base = misc.ToLink<IPlaceableObjectGetter>() });
        world.SubCells.Add(new WorldspaceBlock
        {
            BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellBlock,
            Items = new Noggog.ExtendedList<WorldspaceSubBlock> { new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellSubBlock, Items = new Noggog.ExtendedList<Cell> { ext } } },
        });
        using (var g = new GameData(data)) PluginWriter.Write(b, data, g, opts);
        using (var g = new GameData(data))
        {
            var cache = g.Open("Base.esm").ToImmutableLinkCache();
            var late = Plugin.New("Late.esm", PluginStyle.Light, "test", "made-up update");
            var c = (Cell)cache.ResolveAllContexts<ICell, ICellGetter>(ext.FormKey).First().GetOrAddAsOverride(late);
            c.Grid = null;
            c.MajorRecordFlagsRaw |= 0x4000;
            var w = late.Worldspaces.Single();
            w.Location.Clear();
            w.MajorRecordFlagsRaw |= 0x4000;
            PluginWriter.Write(late, data, g, opts);
        }
        var txt = Path.Combine(s.Dir, "Plugins.txt");
        File.WriteAllLines(txt, ["*Base.esm", "*Late.esm"]);
        var (code, o, e) = ExeTests.RunSplit("cell", "P_Ext", "--data", data, "--plugins", txt, "--quiet");
        Assert.True(code == 0, o + e);
        Assert.Contains($"Cell P_Ext [{ext.FormKey}] (worldspace P_World [{world.FormKey}], grid 1,2)", o);
        Assert.Contains("location: P_WorldLoc [", o);
        Assert.Contains("(from the worldspace)", o);
        var (code2, j, e2) = ExeTests.RunSplit("cell", "P_Ext", "--data", data, "--plugins", txt, "--json");
        Assert.True(code2 == 0, j + e2);
        using var doc = System.Text.Json.JsonDocument.Parse(j);
        var cellJson = doc.RootElement.GetProperty("cell");
        Assert.True(cellJson.GetProperty("locationFromWorldspace").GetBoolean());
        Assert.Equal(1, cellJson.GetProperty("grid")[0].GetInt32());
        Assert.Equal(2, cellJson.GetProperty("grid")[1].GetInt32());
    }
}
