// XCLC (cell grid) comparison, 0.5.0: junk in the 3 unused bytes after the land flags is not a difference.
// Bethesda's Starfield.esm cell 0151D8 holds A9CE0000 there; a Creation Kit override writes zeros.
using Mutagen.Bethesda.Starfield;
using Noggog;
using SfModCheck;
using SfModCheck.Conflict;
using Xunit;

namespace SfModCheck.Tests;

public class CellGridTests
{
    static CellGrid Grid(int x, int y, uint raw) => new() { Point = new P2Int(x, y), Flags = (CellGrid.Flag)unchecked((int)raw) };

    [Fact]
    public void Junk_in_the_unused_bytes_is_ignored()
    {
        Assert.True(Overrides.CellGridEq(Grid(3, -4, 0xA9CE0001), Grid(3, -4, 0x00000001)));
        Assert.True(FieldCompare.DeepEq(Grid(3, -4, 0xA9CE0001), Grid(3, -4, 0x00000001), 0));
    }

    [Fact]
    public void A_different_land_flag_byte_is_a_difference()
    {
        Assert.False(Overrides.CellGridEq(Grid(3, -4, 0xA9CE0001), Grid(3, -4, 0xA9CE0002)));
        Assert.False(FieldCompare.DeepEq(Grid(3, -4, 0xA9CE0001), Grid(3, -4, 0xA9CE0002), 0));
    }

    [Fact]
    public void A_different_point_is_a_difference()
    {
        Assert.False(Overrides.CellGridEq(Grid(3, -4, 1), Grid(3, 4, 1)));
        Assert.False(FieldCompare.DeepEq(Grid(3, -4, 1), Grid(2, -4, 1), 0));
    }
}
