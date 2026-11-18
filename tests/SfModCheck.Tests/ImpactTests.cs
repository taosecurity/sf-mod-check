// 0.5.1: how conflict words a difference (Impact), without a load order. Items 2 (unreadable land flags counted),
// 4 (byte blobs as one value) and 6 (a reordered list is paired by entry, not index by index) of the 0.5.1 to-do list.
using Loqui;
using Mutagen.Bethesda.Plugins.Binary.Streams;
using Mutagen.Bethesda.Plugins.Binary.Translations;
using Mutagen.Bethesda.Starfield;
using Noggog;
using Noggog.StructuredStrings;
using SfModCheck.Conflict;
using Xunit;
using Impact = SfModCheck.Conflict.Impact;

namespace SfModCheck.Tests;

public class ImpactTests
{
    static string NoName(Mutagen.Bethesda.Plugins.IFormLinkGetter l) => l.FormKey.ToString();

    static List<string> Diff(object a, object b, Action? unreadable = null) =>
        Impact.Differences("Items", a, b, NoName, unreadable).Select(d => d.Text).ToList();

    [Fact]
    public void An_entry_changed_in_place_is_described_where_it_is()
    {
        // a plain string entry is named by its old value
        var texts = Diff(new List<string> { "a", "b", "c" }, new List<string> { "a", "Z", "c" });
        Assert.Equal(new[] { "items (\"b\") is \"Z\" in game instead of \"b\"" }, texts);
    }

    [Fact]
    public void A_reordered_and_edited_list_lists_the_entry_that_changed_not_every_shifted_one()
    {
        // index by index (0.5.0) this read as four "changed" entries; only "a" went and "X" came
        var texts = Diff(new List<string> { "a", "b", "c", "d" }, new List<string> { "b", "c", "d", "X" });
        // (also: "has an extra an entry" before 0.5.1)
        Assert.Equal(new[] { "items is missing an entry (\"a\") in game", "items has an extra entry (\"X\") in game" }, texts);
    }

    [Fact]
    public void A_short_byte_blob_is_shown_as_hex_and_a_long_one_by_length()
    {
        Assert.Equal(new[] { "items is 02 00 00 00 in game instead of 01 00 00 00" }, Diff(new byte[] { 1, 0, 0, 0 }, new byte[] { 2, 0, 0, 0 }));
        Assert.Equal("100 bytes of data", Impact.Describe(new byte[100], NoName));   // inside a described entry
    }

    // Code review of 0.5.1: two long blobs of the same length read the same by length alone.
    [Fact]
    public void Long_byte_blobs_show_where_they_first_differ()
    {
        var (a, b) = (new byte[20], new byte[20]);
        b[12] = 0xAB;
        Assert.Equal(new[] { "items is 20 bytes of data (from byte 12: AB 00 00 00 00 00 00 00) in game instead of 20 bytes of data (from byte 12: 00 00 00 00 00 00 00 00)" }, Diff(a, b));
    }

    // Code review round 2: a short blob next to a long one is still shown in full.
    [Fact]
    public void A_short_blob_beside_a_long_one_is_shown_in_full() =>
        Assert.Equal(new[] { "items is 40 bytes of data (from byte 0: 00 00 00 00 00 00 00 00 …) in game instead of 01 00 00 00" },
            Diff(new byte[] { 1, 0, 0, 0 }, new byte[40]));

    // Code review of 0.5.1: land flags shown as "unreadable" in a described entry are counted too, not only in the walk.
    [Fact]
    public void Unreadable_land_flags_in_a_described_entry_are_counted()
    {
        int unreadable = 0;
        var grid = new BadFlagsGrid(new P2Int(3, 4));
        var texts = Diff(new List<object>(), new List<object> { grid }, () => unreadable++);
        Assert.True(unreadable >= 1);
        Assert.Contains(texts, t => t.Contains("land flags unreadable"));
    }

    [Fact]
    public void A_land_flag_read_that_fails_is_reported_not_dropped()
    {
        int unreadable = 0;
        var texts = Diff(new BadFlagsGrid(new P2Int(0, 0)), new BadFlagsGrid(new P2Int(1, 0)), () => unreadable++);
        Assert.Equal(1, unreadable);                            // the caller counts it under "Tool limits hit"
        Assert.Contains(texts, t => t.StartsWith("items point is"));   // the readable part is still described
        Assert.Equal("point 0, 0, land flags unreadable", Impact.Describe(new BadFlagsGrid(new P2Int(0, 0)), NoName));
    }

    /// <summary>A cell grid whose Flags can't be read, as a damaged record's would be.</summary>
    sealed class BadFlagsGrid(P2Int point) : ICellGridGetter
    {
        public P2Int Point { get; } = point;
        public CellGrid.Flag Flags => throw new InvalidOperationException("damaged XCLC");
        ILoquiRegistration ILoquiObject.Registration => throw new NotSupportedException();
        object ICellGridGetter.CommonInstance() => throw new NotSupportedException();
        object? ICellGridGetter.CommonSetterInstance() => throw new NotSupportedException();
        object ICellGridGetter.CommonSetterTranslationInstance() => throw new NotSupportedException();
        object IBinaryItem.BinaryWriteTranslator => throw new NotSupportedException();
        void IBinaryItem.WriteToBinary(MutagenWriter writer, TypedWriteParams translationParams) => throw new NotSupportedException();
        void IPrintable.Print(StructuredStringBuilder sb, string? name) => throw new NotSupportedException();
    }
}
