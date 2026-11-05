using Dotty.Rendering.Gpu;
using Dotty.Runtime.Config;
using Dotty.Silk.Rendering;
using SkiaSharp;
using Xunit;

namespace Dotty.App.Tests;

public sealed class RasterPolicyTests
{
    [Fact]
    public void DefaultIsTheHistoricalRaster()
    {
        var font = new FontUserConfig();
        RasterPolicy configured = RasterPolicy.FromConfig(font.Hinting, font.Antialias);

        Assert.Equal(RasterPolicy.Default, configured);
        Assert.Equal(default, configured);
        Assert.Equal(SKFontHinting.Full, configured.SkiaHinting);
        Assert.Equal(SKFontEdging.Antialias, configured.SkiaEdging);
        Assert.True(configured.IsAntialias);
    }

    [Theory]
    [InlineData("none", FontHinting.None, SKFontHinting.None)]
    [InlineData("Slight", FontHinting.Slight, SKFontHinting.Slight)]
    [InlineData(" normal ", FontHinting.Normal, SKFontHinting.Normal)]
    [InlineData("FULL", FontHinting.Full, SKFontHinting.Full)]
    [InlineData("bogus", FontHinting.Full, SKFontHinting.Full)]
    [InlineData("", FontHinting.Full, SKFontHinting.Full)]
    [InlineData(null, FontHinting.Full, SKFontHinting.Full)]
    public void ParsesHinting(string? value, FontHinting expected, SKFontHinting skia)
    {
        Assert.Equal(expected, RasterPolicy.ParseHinting(value));
        Assert.Equal(skia, new RasterPolicy(expected, FontAntialias.Gray).SkiaHinting);
    }

    [Theory]
    [InlineData("none", FontAntialias.None)]
    [InlineData("NONE", FontAntialias.None)]
    [InlineData("gray", FontAntialias.Gray)]
    [InlineData("subpixel", FontAntialias.Gray)]
    [InlineData(null, FontAntialias.Gray)]
    public void ParsesAntialias(string? value, FontAntialias expected) =>
        Assert.Equal(expected, RasterPolicy.ParseAntialias(value));

    [Fact]
    public void ApplyToConfiguresTheFontWithoutSubpixel()
    {
        using var font = new SKFont(SKTypeface.Default, 14f) { Subpixel = true };
        new RasterPolicy(FontHinting.Slight, FontAntialias.None).ApplyTo(font);

        Assert.False(font.Subpixel);
        Assert.Equal(SKFontHinting.Slight, font.Hinting);
        Assert.Equal(SKFontEdging.Alias, font.Edging);
    }

    [Fact]
    public void MetricsCacheKeysOnThePolicy()
    {
        var cache = new CellMetricsCache(4);
        var aliased = new RasterPolicy(FontHinting.None, FontAntialias.None);
        _ = FontMetricsService.MeasureCell(cache, SKTypeface.Default, new CellMetricsOptions(14f, 1.2, 1f));
        _ = FontMetricsService.MeasureCell(cache, SKTypeface.Default, new CellMetricsOptions(14f, 1.2, 1f, Raster: RasterPolicy.Default));
        Assert.Equal(1, cache.Count);

        CellMetrics measured = FontMetricsService.MeasureCell(cache, SKTypeface.Default, new CellMetricsOptions(14f, 1.2, 1f, Raster: aliased));
        Assert.Equal(2, cache.Count);
        Assert.True(measured.CellWidth >= 1 && measured.CellHeight >= 1);
    }
}
