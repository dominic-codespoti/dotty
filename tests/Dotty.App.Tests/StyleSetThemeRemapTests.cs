using Dotty.Terminal.Adapter;
using Dotty.Terminal.Adapter.Buffer;
using Xunit;

namespace Dotty.App.Tests;

public class StyleSetThemeRemapTests
{
    [Fact]
    public void RemapPalette_UpdatesExistingIndexedStylesOnly()
    {
        var styleSet = new StyleSet();
        var attributes = new CellAttributes { Bold = true };
        attributes.SetForegroundIndexed(0, new SgrColorArgb(0xFF010101));
        attributes.SetBackgroundIndexed(1, new SgrColorArgb(0xFF020202));
        attributes.SetUnderlineColorIndexed(2, new SgrColorArgb(0xFF030303));
        ushort styleId = styleSet.GetOrCreateId(attributes);

        var colors = new uint[256];
        colors[0] = 0xFF111111;
        colors[1] = 0xFF222222;
        colors[2] = 0xFF333333;

        Assert.True(styleSet.RemapPalette(colors));

        ref readonly var remapped = ref styleSet.GetStyle(styleId);
        Assert.Equal(0xFF111111u, remapped.Foreground.Argb);
        Assert.Equal(0xFF222222u, remapped.Background.Argb);
        Assert.Equal(0xFF333333u, remapped.UnderlineColor.Argb);
        Assert.True(remapped.Bold);
    }

    [Fact]
    public void RemapPalette_LeavesExplicitRgbColorsUntouchedEvenWhenTheyMatch()
    {
        var styleSet = new StyleSet();
        var attributes = new CellAttributes
        {
            Foreground = new SgrColorArgb(0xFFABCDEF),
            Background = new SgrColorArgb(0xFF123456),
        };

        ushort styleId = styleSet.GetOrCreateId(attributes);

        var colors = new uint[256];
        colors[0] = 0xFFABCDEF;
        colors[1] = 0xFF111111;

        Assert.False(styleSet.RemapPalette(colors));

        ref readonly var remapped = ref styleSet.GetStyle(styleId);
        Assert.Equal(attributes.Foreground.Argb, remapped.Foreground.Argb);
        Assert.Equal(attributes.Background.Argb, remapped.Background.Argb);
    }
    [Fact]
    public void CaptureStyles_ReturnsDefensiveCopy()
    {
        var styleSet = new StyleSet();
        var attributes = new CellAttributes { Bold = true };
        ushort styleId = styleSet.GetOrCreateId(attributes);

        var captured = styleSet.CaptureStyles();
        captured[styleId] = CellAttributes.Default;

        ref readonly var live = ref styleSet.GetStyle(styleId);
        Assert.True(live.Bold);
        Assert.NotSame(captured, styleSet.CaptureStyles());
    }

    [Fact]
    public void RemapPalette_PreservesPublishedIdTableConsistency()
    {
        var styleSet = new StyleSet();
        var attributes = new CellAttributes { Bold = true };
        attributes.SetForegroundIndexed(0, new SgrColorArgb(0xFF010101));
        ushort styleId = styleSet.GetOrCreateId(attributes);

        var colors = new uint[256];
        colors[0] = 0xFF111111;

        Assert.True(styleSet.RemapPalette(colors));

        var published = styleSet.CaptureStyles();
        var remapped = styleSet.GetStyle(styleId);
        Assert.Equal(published[styleId], remapped);
        Assert.Equal(styleId, styleSet.GetOrCreateId(published[styleId]));
    }
}

