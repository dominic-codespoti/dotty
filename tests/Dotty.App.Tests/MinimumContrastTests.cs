using System;
using Dotty.Rendering.Gpu;
using Dotty.Runtime.Selection;
using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

public sealed class MinimumContrastTests
{
    private static SgrColorArgb Rgb(int r, int g, int b) => SgrColorArgb.FromRgb((byte)r, (byte)g, (byte)b);

    [Theory]
    // foreground, background, floor -> expected (golden: the smallest mix toward white/black that meets the floor)
    [InlineData(128, 128, 128, 0, 0, 0, 4.5f, 128, 128, 128)]       // already 5.3:1, unchanged
    [InlineData(100, 100, 100, 0, 0, 0, 4.5f, 117, 117, 117)]       // light on dark lightens
    [InlineData(50, 50, 200, 0, 0, 0, 4.5f, 104, 104, 214)]         // hue kept as far as possible
    [InlineData(200, 200, 200, 255, 255, 255, 4.5f, 118, 118, 118)] // light on light darkens
    [InlineData(255, 0, 0, 255, 255, 255, 7f, 182, 0, 0)]
    [InlineData(90, 90, 90, 128, 128, 128, 4.5f, 23, 23, 23)]       // dark on mid-grey goes darker
    [InlineData(30, 30, 30, 40, 40, 40, 3f, 113, 113, 113)]         // black cannot reach 3:1 on #282828: flips to white
    [InlineData(250, 250, 250, 255, 255, 255, 21f, 0, 0, 0)]        // unreachable floor: the best extreme
    [InlineData(255, 255, 255, 0, 0, 0, 7f, 255, 255, 255)]
    [InlineData(120, 60, 200, 120, 60, 200, 4.5f, 224, 211, 242)]   // invisible text becomes readable
    public void Apply_GoldenPairs(int fr, int fg, int fb, int br, int bg, int bb, float floor, int er, int eg, int eb)
    {
        SgrColorArgb result = MinimumContrast.Apply(Rgb(fr, fg, fb), Rgb(br, bg, bb), floor);

        Assert.Equal((er, eg, eb), ((int)result.R, (int)result.G, (int)result.B));
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(0.5f)]
    [InlineData(float.NaN)]
    public void Apply_OffLeavesTheForeground(float floor)
    {
        var fg = Rgb(30, 30, 30);
        Assert.Equal(fg, MinimumContrast.Apply(fg, Rgb(40, 40, 40), floor));
    }

    [Fact]
    public void Apply_MeetsReachableFloorsAndKeepsAlpha()
    {
        var random = new Random(4021);
        for (int i = 0; i < 2000; i++)
        {
            var fg = new SgrColorArgb((uint)random.Next(0x10, 0x100) << 24 | (uint)random.Next(0, 0x1000000));
            var bg = Rgb(random.Next(256), random.Next(256), random.Next(256));
            float floor = 1f + (float)random.NextDouble() * 6f;

            SgrColorArgb result = MinimumContrast.Apply(fg, bg, floor);

            Assert.Equal(fg.A, result.A);
            double best = Math.Max(MinimumContrast.Ratio(Rgb(255, 255, 255), bg), MinimumContrast.Ratio(Rgb(0, 0, 0), bg));
            if (best >= floor)
                Assert.True(MinimumContrast.Ratio(result, bg) >= floor, $"{fg.Argb:X8} on {bg.Argb:X8} at {floor}");
            if (MinimumContrast.Ratio(fg, bg) >= floor)
                Assert.Equal(fg, result);
        }
    }

    [Fact]
    public void Ratio_MatchesWcagExtremes()
    {
        Assert.Equal(21.0, MinimumContrast.Ratio(Rgb(255, 255, 255), Rgb(0, 0, 0)), 6);
        Assert.Equal(1.0, MinimumContrast.Ratio(Rgb(10, 20, 30), Rgb(10, 20, 30)), 6);
        Assert.Equal(
            Dotty.Abstractions.Themes.ColorSchemeBase.CalculateContrastRatio(0xFF3385DB, 0xFF1E1E1E),
            MinimumContrast.Ratio(new SgrColorArgb(0xFF3385DB), new SgrColorArgb(0xFF1E1E1E)), 3);
    }

    [Theory]
    [InlineData(1.0, 1f)]
    [InlineData(4.5, 4.5f)]
    [InlineData(0.2, 1f)]
    [InlineData(40.0, 21f)]
    [InlineData(double.NaN, 1f)]
    public void Normalize_ClampsToTheWcagRange(double configured, float expected) =>
        Assert.Equal(expected, MinimumContrast.Normalize(configured));

    [Fact]
    public void DefaultConfigIsOff()
    {
        Assert.Equal(MinimumContrast.Off, MinimumContrast.Normalize(new Dotty.Runtime.Config.DottyUserConfig().Text.MinimumContrast));
        Assert.Equal(MinimumContrast.Off, TextAttributeOptions.Default.MinimumContrast);
    }

    [Fact]
    public void Selection_FloorsSelectedTextAgainstTheVisibleSelectionColour()
    {
        // #3385DB at alpha 128 over black shows as (26, 67, 110); grey 100 reaches 4.5:1 there at 173.
        var instances = new CellInstance[4];
        instances[0] = new CellInstance { Col = 0, Row = 0, FgR = 100, FgG = 100, FgB = 100, FgA = 255, BgA = 0 };
        instances[1] = new CellInstance { Col = 1, Row = 0, FgR = 208, FgG = 208, FgB = 208, FgA = 255, BgA = 0 };
        var range = TerminalSelectionRange.From(0, 0, 0, 1);
        var coverage = new bool[SelectionQuadBuilder.CoverageLength(range, 1, 2, 0)];

        SelectionQuadBuilder.Apply(instances, 2, range, block: false, paneRows: 1, paneColumns: 2, scrollOffset: 0,
            columnOffset: 0, rowOffset: 0, new SgrColorArgb(0xFF3385DB), 128, Rgb(255, 255, 255), coverage,
            minimumContrast: 4.5f, background: Rgb(0, 0, 0));

        Assert.Equal((173, 173, 173), ((int)instances[0].FgR, (int)instances[0].FgG, (int)instances[0].FgB));
        Assert.Equal((208, 208, 208), ((int)instances[1].FgR, (int)instances[1].FgG, (int)instances[1].FgB));
    }

    [Fact]
    public void Selection_LeavesForegroundsAloneWhenOff()
    {
        var instances = new CellInstance[2];
        instances[0] = new CellInstance { Col = 0, Row = 0, FgR = 100, FgG = 100, FgB = 100, FgA = 255 };
        var range = TerminalSelectionRange.From(0, 0, 0, 0);
        var coverage = new bool[SelectionQuadBuilder.CoverageLength(range, 1, 1, 0)];

        SelectionQuadBuilder.Apply(instances, 1, range, false, 1, 1, 0, 0, 0,
            new SgrColorArgb(0xFF3385DB), 128, Rgb(255, 255, 255), coverage);

        Assert.Equal((100, 100, 100), ((int)instances[0].FgR, (int)instances[0].FgG, (int)instances[0].FgB));
    }
}
