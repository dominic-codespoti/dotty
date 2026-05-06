using Dotty.Terminal.Adapter;
using Dotty.Terminal.Adapter.Buffer;
using Xunit;

namespace Dotty.App.Tests;

public class StyleSetThemeRemapTests
{
    [Fact]
    public void RemapAnsiPalette_UpdatesExistingAnsiBackedStyles()
    {
        var styleSet = new StyleSet();
        var attributes = new CellAttributes
        {
            Foreground = new SgrColorArgb(0xFF010101),
            Background = new SgrColorArgb(0xFF020202),
            UnderlineColor = new SgrColorArgb(0xFF030303),
            Bold = true,
        };

        ushort styleId = styleSet.GetOrCreateId(attributes);

        var previousPalette = new uint[16];
        var currentPalette = new uint[16];
        previousPalette[0] = 0xFF010101;
        previousPalette[1] = 0xFF020202;
        previousPalette[2] = 0xFF030303;
        currentPalette[0] = 0xFF111111;
        currentPalette[1] = 0xFF222222;
        currentPalette[2] = 0xFF333333;

        Assert.True(styleSet.RemapAnsiPalette(previousPalette, currentPalette));

        ref readonly var remapped = ref styleSet.GetStyle(styleId);
        Assert.Equal(0xFF111111u, remapped.Foreground.Argb);
        Assert.Equal(0xFF222222u, remapped.Background.Argb);
        Assert.Equal(0xFF333333u, remapped.UnderlineColor.Argb);
        Assert.True(remapped.Bold);
    }

    [Fact]
    public void RemapAnsiPalette_LeavesNonPaletteColorsUntouched()
    {
        var styleSet = new StyleSet();
        var attributes = new CellAttributes
        {
            Foreground = new SgrColorArgb(0xFFABCDEF),
            Background = new SgrColorArgb(0xFF123456),
        };

        ushort styleId = styleSet.GetOrCreateId(attributes);

        var previousPalette = new uint[16];
        var currentPalette = new uint[16];
        previousPalette[0] = 0xFF010101;
        currentPalette[0] = 0xFF111111;

        Assert.False(styleSet.RemapAnsiPalette(previousPalette, currentPalette));

        ref readonly var remapped = ref styleSet.GetStyle(styleId);
        Assert.Equal(attributes.Foreground.Argb, remapped.Foreground.Argb);
        Assert.Equal(attributes.Background.Argb, remapped.Background.Argb);
    }
}
