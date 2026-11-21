// Overrides.FindItms on cells (0.10.0 relay step 13): cells are looked up through the same ResolveAll call as their
// children, so Mutagen builds one cell index instead of two. These tests pin what that lookup must still give: a cell
// override is compared with the winning master version (not the first or the base one), and children are found
// through their parent cell in any master version. In-memory plugins only; no game files are read.
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Starfield;
using Noggog;
using Xunit;

namespace SfModCheck.Tests;

public class FindItmsCellTests
{
    static Cell AddCell(StarfieldMod m, string edid)
    {
        var c = new Cell(m) { EditorID = edid };
        m.Cells.Add(new CellBlock { SubBlocks = new ExtendedList<CellSubBlock> { new CellSubBlock { Cells = new ExtendedList<Cell> { c } } } });
        return c;
    }

    static void AddOverride(StarfieldMod mod, Cell c) =>
        mod.Cells.Add(new CellBlock { SubBlocks = new ExtendedList<CellSubBlock> { new CellSubBlock { Cells = new ExtendedList<Cell> { c } } } });

    /// <summary>Base game: cells Same, Changed, DlcWins and Holder (Holder has one temporary placed object). An
    /// official update overrides DlcWins with a new EditorID. Returns the mod, its overrides and the masters' cache.</summary>
    static (StarfieldMod Mod, List<IMajorRecordGetter> Overrides, Mutagen.Bethesda.Plugins.Cache.ILinkCache Cache) Build(bool childSame)
    {
        var b = new StarfieldMod(ModKey.FromFileName("SfmCellBase.esm"), StarfieldRelease.Starfield);
        var kw = b.Keywords.AddNew("SfmCell_Kw");
        var misc = b.MiscItems.AddNew("SfmCell_Misc");
        var same = AddCell(b, "SfmCell_Same");
        var changed = AddCell(b, "SfmCell_Changed");
        var dlcWins = AddCell(b, "SfmCell_DlcWins");
        var holder = AddCell(b, "SfmCell_Holder");
        var child = new PlacedObject(b) { Base = misc.ToLink<IPlaceableObjectGetter>() };
        holder.Temporary.Add(child);

        var d = new StarfieldMod(ModKey.FromFileName("SfmCellDlc.esm"), StarfieldRelease.Starfield);
        var dlcCell = dlcWins.DeepCopy();
        dlcCell.EditorID = "SfmCell_DlcWins_Updated";
        AddOverride(d, dlcCell);

        var mod = new StarfieldMod(ModKey.FromFileName("SfmCellMod.esm"), StarfieldRelease.Starfield);
        AddOverride(mod, same.DeepCopy());                  // identical to the only master version: ITM
        var c2 = changed.DeepCopy(); c2.EditorID = "SfmCell_Changed_ByMod";
        AddOverride(mod, c2);                               // changed: not an ITM
        AddOverride(mod, dlcWins.DeepCopy());               // identical to the base version, but the update's version wins: not an ITM
        var h = holder.DeepCopy();
        if (!childSame) ((PlacedObject)h.Temporary[0]).Base = new FormLink<IPlaceableObjectGetter>(kw.FormKey);
        AddOverride(mod, h);                                // a parent with a child: kept; the child is compared through its cell

        var overrides = new List<IMajorRecordGetter>();
        foreach (var r in mod.EnumerateMajorRecords()) overrides.Add(r);
        var cache = new IStarfieldModGetter[] { b, d }.ToImmutableLinkCache<IStarfieldMod, IStarfieldModGetter>();
        return (mod, overrides, cache);
    }

    static List<string> Names(List<IMajorRecordGetter> itms) => itms.Select(r => r.EditorID ?? r.FormKey.ToString()).OrderBy(s => s).ToList();

    [Fact]
    public void A_cell_override_is_compared_with_the_winning_master_version()
    {
        var (mod, overrides, cache) = Build(childSame: true);
        var (itms, failed) = Overrides.FindItms(mod, overrides, cache);
        Assert.Equal(0, failed);
        var names = Names(itms);
        Assert.Contains("SfmCell_Same", names);
        Assert.DoesNotContain("SfmCell_Changed_ByMod", names);
        Assert.DoesNotContain("SfmCell_DlcWins", names);   // matches the base game's version, but the update's version wins
        Assert.DoesNotContain("SfmCell_Holder", names);    // kept for its child
        Assert.Single(itms, r => r is IPlacedObjectGetter);
        Assert.Equal(2, itms.Count);
    }

    [Fact]
    public void A_changed_child_of_an_overridden_cell_is_not_an_ITM()
    {
        var (mod, overrides, cache) = Build(childSame: false);
        var (itms, failed) = Overrides.FindItms(mod, overrides, cache);
        Assert.Equal(0, failed);
        Assert.DoesNotContain(itms, r => r is IPlacedObjectGetter);
        Assert.Equal(["SfmCell_Same"], Names(itms));
    }

    [Fact]
    public void A_cell_the_update_also_overrides_is_an_ITM_when_it_matches_the_update()
    {
        var (mod, overrides, cache) = Build(childSame: true);
        var dlcCopy = (Cell)cache.Resolve<ICellGetter>(overrides.OfType<ICellGetter>().Single(c => c.EditorID == "SfmCell_DlcWins").FormKey).DeepCopy();
        // the mod's copy of DlcWins now matches the update's version
        var mine = mod.Cells.SelectMany(bl => bl.SubBlocks).SelectMany(sb => sb.Cells).Single(c => c.EditorID == "SfmCell_DlcWins");
        mine.EditorID = dlcCopy.EditorID;
        var (itms, _) = Overrides.FindItms(mod, overrides, cache);
        Assert.Contains("SfmCell_DlcWins_Updated", Names(itms));
    }
}
