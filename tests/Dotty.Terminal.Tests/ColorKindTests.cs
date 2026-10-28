using System.Runtime.CompilerServices;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Adapter.Buffer;
using Xunit;

namespace Dotty.Terminal.Tests;

/// <summary>
/// Foreground / underline colour-kind tags (Default | Indexed(n) | Rgb): needed so Unicode-placeholder cells can tell a
/// palette image id from a truecolor one after the SGR parser has reduced both to an ARGB value.
/// </summary>
public sealed class ColorKindTests
{
    private static readonly TerminalPalette Palette = new();

    private static CellAttributes Sgr(string parameters, CellAttributes? start = null) =>
        SgrParserArgb.Apply(parameters.AsSpan(), start ?? CellAttributes.Default, Palette);

    [Fact]
    public void DefaultAttributesHaveDefaultKinds()
    {
        var a = CellAttributes.Default;

        Assert.Equal(ColorKind.Default, a.ForegroundKind);
        Assert.Equal(ColorKind.Default, a.UnderlineColorKind);
        Assert.Equal(-1, a.ForegroundIndex);
        Assert.Equal(-1, a.UnderlineColorIndex);
    }

    [Theory]
    [InlineData("30", 0)]
    [InlineData("31", 1)]
    [InlineData("37", 7)]
    [InlineData("90", 8)]
    [InlineData("91", 9)]
    [InlineData("97", 15)]
    [InlineData("38;5;0", 0)]
    [InlineData("38;5;1", 1)]
    [InlineData("38;5;15", 15)]
    [InlineData("38;5;16", 16)]
    [InlineData("38;5;196", 196)]
    [InlineData("38;5;255", 255)]
    public void PaletteForegroundsAreIndexedWithTheirIndexAndPaletteArgb(string sgr, int index)
    {
        var a = Sgr(sgr);

        Assert.Equal(ColorKind.Indexed, a.ForegroundKind);
        Assert.Equal(index, a.ForegroundIndex);
        Assert.Equal(Palette[index], a.Foreground.Argb);
        Assert.Equal(ColorKind.Default, a.UnderlineColorKind);
    }

    [Theory]
    [InlineData("38;2;1;2;3")]
    [InlineData("38;2;0;0;0")]
    [InlineData("38;2;255;255;255")]
    public void TruecolorForegroundIsRgbWithNoIndex(string sgr)
    {
        var a = Sgr(sgr);

        Assert.Equal(ColorKind.Rgb, a.ForegroundKind);
        Assert.Equal(-1, a.ForegroundIndex);
    }

    [Fact]
    public void TruecolorEqualToAPaletteEntryStaysRgb()
    {
        uint argb = Palette[1];
        string sgr = $"38;2;{(argb >> 16) & 0xFF};{(argb >> 8) & 0xFF};{argb & 0xFF}";

        var rgb = Sgr(sgr);
        var indexed = Sgr("31");

        Assert.Equal(indexed.Foreground, rgb.Foreground);
        Assert.Equal(ColorKind.Rgb, rgb.ForegroundKind);
        Assert.Equal(ColorKind.Indexed, indexed.ForegroundKind);
        Assert.NotEqual(indexed, rgb);
    }

    [Fact]
    public void Sgr39ClearsIndexedAndRgbForeground()
    {
        foreach (string set in new[] { "31", "38;5;200", "38;2;9;8;7" })
        {
            var cleared = Sgr("39", Sgr(set));

            Assert.Equal(ColorKind.Default, cleared.ForegroundKind);
            Assert.Equal(-1, cleared.ForegroundIndex);
            Assert.True(cleared.Foreground.IsEmpty);
            Assert.Equal(CellAttributes.Default, cleared);
        }
    }

    [Fact]
    public void Sgr0AndEmptyParametersResetBothKinds()
    {
        var both = Sgr("31;58;5;4");
        Assert.Equal(ColorKind.Indexed, both.ForegroundKind);
        Assert.Equal(ColorKind.Indexed, both.UnderlineColorKind);

        Assert.Equal(CellAttributes.Default, Sgr("0", both));
        Assert.Equal(CellAttributes.Default, Sgr("", both));
        var viaZeroInList = Sgr("1;0", both);
        Assert.Equal(ColorKind.Default, viaZeroInList.ForegroundKind);
        Assert.Equal(ColorKind.Default, viaZeroInList.UnderlineColorKind);
    }

    [Fact]
    public void LaterColourSettingWinsAndReplacesTheKind()
    {
        Assert.Equal(ColorKind.Rgb, Sgr("31;38;2;1;2;3").ForegroundKind);
        Assert.Equal(ColorKind.Indexed, Sgr("38;2;1;2;3;31").ForegroundKind);
        Assert.Equal(1, Sgr("38;2;1;2;3;31").ForegroundIndex);
        Assert.Equal(200, Sgr("31;38;5;200").ForegroundIndex);
        Assert.Equal(3, Sgr("38;5;200;33;1").ForegroundIndex);
        Assert.Equal(ColorKind.Indexed, Sgr("38;5;200", Sgr("38;2;1;2;3")).ForegroundKind);
        Assert.Equal(-1, Sgr("38;2;1;2;3", Sgr("38;5;200")).ForegroundIndex);
    }

    [Fact]
    public void OutOfRangePaletteIndexIsIgnoredAndLeavesTheTagAlone()
    {
        var before = Sgr("31");
        var after = Sgr("38;5;256", before);

        Assert.Equal(before, after);
        Assert.Equal(1, after.ForegroundIndex);
        Assert.Equal(ColorKind.Default, Sgr("38;5;256").ForegroundKind);
    }

    [Fact]
    public void BackgroundColoursNeverTouchTheForegroundOrUnderlineTags()
    {
        foreach (string sgr in new[] { "41", "101", "48;5;9", "48;2;1;2;3" })
        {
            var a = Sgr(sgr, Sgr("32;58;5;3"));

            Assert.Equal(ColorKind.Indexed, a.ForegroundKind);
            Assert.Equal(2, a.ForegroundIndex);
            Assert.Equal(ColorKind.Indexed, a.UnderlineColorKind);
            Assert.Equal(3, a.UnderlineColorIndex);
        }
        Assert.Equal(ColorKind.Default, Sgr("41").ForegroundKind);
    }

    [Theory]
    [InlineData("58;5;0", 0)]
    [InlineData("58;5;9", 9)]
    [InlineData("58;5;255", 255)]
    public void PaletteUnderlineColourIsIndexed(string sgr, int index)
    {
        var a = Sgr(sgr);

        Assert.Equal(ColorKind.Indexed, a.UnderlineColorKind);
        Assert.Equal(index, a.UnderlineColorIndex);
        Assert.Equal(Palette[index], a.UnderlineColor.Argb);
        Assert.Equal(ColorKind.Default, a.ForegroundKind);
    }

    [Fact]
    public void TruecolorUnderlineIsRgbAndSgr59Clears()
    {
        var rgb = Sgr("58;2;1;2;3");
        Assert.Equal(ColorKind.Rgb, rgb.UnderlineColorKind);
        Assert.Equal(-1, rgb.UnderlineColorIndex);

        Assert.Equal(CellAttributes.Default, Sgr("59", rgb));
        Assert.Equal(CellAttributes.Default, Sgr("59", Sgr("58;5;7")));
    }

    [Fact]
    public void ForegroundAndUnderlineTagsAreIndependent()
    {
        var a = Sgr("38;5;12;58;2;4;5;6");

        Assert.Equal(ColorKind.Indexed, a.ForegroundKind);
        Assert.Equal(12, a.ForegroundIndex);
        Assert.Equal(ColorKind.Rgb, a.UnderlineColorKind);

        var b = Sgr("59", a);
        Assert.Equal(12, b.ForegroundIndex);
        Assert.Equal(ColorKind.Default, b.UnderlineColorKind);
        var c = Sgr("39", a);
        Assert.Equal(ColorKind.Default, c.ForegroundKind);
        Assert.Equal(ColorKind.Rgb, c.UnderlineColorKind);
    }

    [Fact]
    public void NonColourSgrKeepsTags()
    {
        var a = Sgr("1;3;4;7;9;22;23;24;27;29", Sgr("38;5;77;58;5;33"));

        Assert.Equal(77, a.ForegroundIndex);
        Assert.Equal(33, a.UnderlineColorIndex);
    }

    // ---- equality / hashing / interning --------------------------------------------------------------------------

    [Fact]
    public void EqualAttributesBuiltTwiceAreEqualWithEqualHashes()
    {
        var a = Sgr("31;58;5;9;1");
        var b = Sgr("31;58;5;9;1");

        Assert.Equal(a, b);
        Assert.True(a.Equals((object)b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void DifferentIndexWithDifferentArgbDiffers()
    {
        Assert.NotEqual(Sgr("31"), Sgr("32"));
        Assert.NotEqual(Sgr("38;5;1"), Sgr("38;5;2"));
        Assert.NotEqual(Sgr("58;5;1"), Sgr("58;5;2"));
    }

    [Fact]
    public void SameArgbReachedByTwoPaletteIndicesDiffersByIndex()
    {
        // 38;5;n for n in the 16..231 cube and the stock palette never collide, so force a collision by
        // building the attributes directly: same ARGB, different palette entries.
        var a = new CellAttributes();
        a.SetForegroundIndexed(3, new SgrColorArgb(0xFF123456));
        var b = new CellAttributes();
        b.SetForegroundIndexed(4, new SgrColorArgb(0xFF123456));

        Assert.NotEqual(a, b);
        Assert.NotEqual(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void IndexedAndRgbWithTheSameArgbInternToDifferentStyleIds()
    {
        var styles = new StyleSet();
        var indexed = new CellAttributes();
        indexed.SetForegroundIndexed(1, new SgrColorArgb(0xFFAA0000));
        var rgb = new CellAttributes();
        rgb.SetForegroundRgb(new SgrColorArgb(0xFFAA0000));

        ushort indexedId = styles.GetOrCreateId(indexed);
        ushort rgbId = styles.GetOrCreateId(rgb);

        Assert.NotEqual(indexedId, rgbId);
        Assert.Equal(indexedId, styles.GetOrCreateId(indexed));
        Assert.Equal(rgbId, styles.GetOrCreateId(rgb));
        Assert.Equal(ColorKind.Indexed, styles.GetStyle(indexedId).ForegroundKind);
        Assert.Equal(ColorKind.Rgb, styles.GetStyle(rgbId).ForegroundKind);
    }

    [Fact]
    public void UnderlineIndexedAndRgbInternToDifferentStyleIds()
    {
        var styles = new StyleSet();
        var indexed = new CellAttributes();
        indexed.SetUnderlineColorIndexed(5, new SgrColorArgb(0xFF00AA00));
        var rgb = new CellAttributes();
        rgb.SetUnderlineColorRgb(new SgrColorArgb(0xFF00AA00));

        Assert.NotEqual(styles.GetOrCreateId(indexed), styles.GetOrCreateId(rgb));
    }

    [Fact]
    public void ForegroundKindIsDefaultWheneverTheColourIsEmpty()
    {
        var a = new CellAttributes();
        a.SetForegroundIndexed(5, default);

        Assert.Equal(ColorKind.Default, a.ForegroundKind);
        Assert.Equal(-1, a.ForegroundIndex);
    }

    // ---- theme remap ---------------------------------------------------------------------------------------------

    private static (uint[] Previous, uint[] Current) Palettes()
    {
        var previous = new uint[16];
        var current = new uint[16];
        for (int i = 0; i < 16; i++)
        {
            previous[i] = 0xFF000000u | (uint)(0x10 + i);
            current[i] = 0xFF000000u | (uint)(0x80 + i);
        }
        return (previous, current);
    }

    [Fact]
    public void ThemeRemapChangesTheArgbButKeepsTheIndexedTag()
    {
        var (previous, current) = Palettes();
        var styles = new StyleSet();
        var a = new CellAttributes();
        a.SetForegroundIndexed(3, new SgrColorArgb(previous[3]));
        a.SetUnderlineColorIndexed(12, new SgrColorArgb(previous[12]));
        ushort id = styles.GetOrCreateId(a);

        Assert.True(styles.RemapAnsiPalette(previous, current));

        ref readonly var remapped = ref styles.GetStyle(id);
        Assert.Equal(current[3], remapped.Foreground.Argb);
        Assert.Equal(ColorKind.Indexed, remapped.ForegroundKind);
        Assert.Equal(3, remapped.ForegroundIndex);
        Assert.Equal(current[12], remapped.UnderlineColor.Argb);
        Assert.Equal(12, remapped.UnderlineColorIndex);
    }

    [Fact]
    public void ThemeRemapNeverTurnsAnRgbColourIntoAnIndexedOne()
    {
        var (previous, current) = Palettes();
        var styles = new StyleSet();
        var a = new CellAttributes();
        a.SetForegroundRgb(new SgrColorArgb(0xFF445566)); // not a palette value
        ushort id = styles.GetOrCreateId(a);

        Assert.False(styles.RemapAnsiPalette(previous, current));

        Assert.Equal(ColorKind.Rgb, styles.GetStyle(id).ForegroundKind);
        Assert.Equal(0xFF445566u, styles.GetStyle(id).Foreground.Argb);
    }

    [Fact]
    public void ExtendedPaletteIndicesAbove15KeepTheirTagThroughRemap()
    {
        var (previous, current) = Palettes();
        var styles = new StyleSet();
        var a = Sgr("38;5;200");
        ushort id = styles.GetOrCreateId(a);

        styles.RemapAnsiPalette(previous, current);

        Assert.Equal(200, styles.GetStyle(id).ForegroundIndex);
        Assert.Equal(a.Foreground, styles.GetStyle(id).Foreground);
    }

    // ---- layout --------------------------------------------------------------------------------------------------

    [Fact]
    public void CellAttributesGrowthIsBoundedToTheTagBytes()
    {
        // 3 x uint colours + 9 one-byte attributes + ushort hyperlink id packed to 24 bytes before the tags; the four
        // tag bytes (two flags, two indices) raise that to 28. StyleSet stores one entry per distinct style, not per
        // cell, so this is not part of the per-cell arena arithmetic (CellHot 8 B + ColdCell 4 B, pinned in ColdCellTests).
        Assert.True(Unsafe.SizeOf<CellAttributes>() <= 28, $"CellAttributes is {Unsafe.SizeOf<CellAttributes>()} bytes");
    }
}
