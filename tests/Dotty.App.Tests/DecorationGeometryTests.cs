using System;
using System.Collections.Generic;
using System.Linq;
using Dotty.Rendering.Gpu;
using Dotty.Silk.Rendering;
using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

public sealed class DecorationGeometryTests
{
    private static readonly double[] LineHeights = { 1.0, 1.1, 1.25, 1.5 };

    // DejaVu-like proportions per em: ascent 0.928, descent 0.236, advance 0.602, underline 0.0875/0.044.
    private static CellSpriteMetrics Measure(int cellWidth, double lineHeight)
    {
        float size = cellWidth / 0.602f;
        float em = size / 20f;
        var face = new FontMetricsInput(Ascent: -18.56f * em, Descent: 4.72f * em, Advance: 12.04f * em,
            UnderlinePosition: 1.75f * em, UnderlineThickness: 0.88f * em,
            StrikeoutPosition: -5.9f * em, StrikeoutThickness: 0.98f * em, XHeight: 10.9f * em);
        var m = CellMetricsCalculator.Compute(face, new CellMetricsOptions(size, lineHeight, 1f, default, default));
        return CellSpriteMetrics.Create(m.CellWidth, m.CellHeight, m.Baseline, m.UnderlinePosition, m.UnderlineThickness,
            m.StrikethroughPosition, m.StrikethroughThickness, m.OverlinePosition, m.OverlineThickness);
    }

    public static IEnumerable<object[]> Cells()
    {
        for (int width = 7; width <= 40; width++)
            foreach (double lineHeight in LineHeights)
                yield return new object[] { width, lineHeight };
    }

    private static CellSpriteMetrics Metrics(int width = 10, int height = 20, int underline = 17, int thickness = 1) =>
        CellSpriteMetrics.Create(width, height, 15, underline, thickness, 10, 1, 0, 1);

    [Theory]
    [MemberData(nameof(Cells))]
    public void EveryStyle_StaysInsideTheCellAndItsBand(int cellWidth, double lineHeight)
    {
        var metrics = Measure(cellWidth, lineHeight);
        Assert.Equal(cellWidth, metrics.CellWidth);
        foreach (var style in new[] { UnderlineStyle.Single, UnderlineStyle.Double, UnderlineStyle.Curl, UnderlineStyle.Dotted, UnderlineStyle.Dashed })
        {
            var (top, height) = DecorationGeometry.UnderlineBand(style, metrics);
            Assert.True(top >= 0 && height >= 1 && top + height <= metrics.CellHeight, $"{style} band {top}+{height} in {metrics.CellHeight}");
            if (style == UnderlineStyle.Curl)
                continue;
            foreach (var rect in DecorationGeometry.UnderlineRects(style, metrics.CellWidth, metrics))
            {
                Assert.InRange(rect.X, 0, metrics.CellWidth - 1);
                Assert.InRange(rect.Right, rect.X + 1, metrics.CellWidth);
                Assert.InRange(rect.Y, top, top + height - 1);
                Assert.InRange(rect.Bottom, rect.Y + 1, top + height);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void Single_IsOneFullWidthStrokeAtTheMetricPosition(int cellWidth, double lineHeight)
    {
        var metrics = Measure(cellWidth, lineHeight);
        var rect = Assert.Single(DecorationGeometry.UnderlineRects(UnderlineStyle.Single, cellWidth, metrics));
        Assert.Equal(new PixelRect(0, metrics.UnderlinePosition, cellWidth, metrics.UnderlineThickness), rect);
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void Double_HasTwoEqualStrokesWithAtLeastOnePixelGap(int cellWidth, double lineHeight)
    {
        var metrics = Measure(cellWidth, lineHeight);
        var rects = DecorationGeometry.UnderlineRects(UnderlineStyle.Double, cellWidth, metrics);
        Assert.Equal(2, rects.Count);
        Assert.Equal(rects[0].Height, rects[1].Height);
        Assert.True(rects[1].Y - rects[0].Bottom >= 1);
        Assert.True(rects[1].Bottom <= metrics.CellHeight);
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void DottedAndDashed_RepeatPerCellWithGapsAtTheBoundary(int cellWidth, double lineHeight)
    {
        var metrics = Measure(cellWidth, lineHeight);
        foreach (var style in new[] { UnderlineStyle.Dotted, UnderlineStyle.Dashed })
        {
            var one = DecorationGeometry.UnderlineRects(style, cellWidth, metrics);
            var two = DecorationGeometry.UnderlineRects(style, 2 * cellWidth, metrics);
            Assert.NotEmpty(one);
            // Two cells are the one-cell pattern twice: the period divides the cell width.
            Assert.Equal(one.Concat(one.Select(r => r with { X = r.X + cellWidth })), two);
            for (int i = 1; i < two.Count; i++)
                Assert.True(two[i].X - two[i - 1].Right >= 1, $"{style} marks {i - 1}/{i} touch at width {cellWidth}");
        }
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public void Curly_PhaseIsContinuousAcrossCells(int cellWidth, double lineHeight)
    {
        var metrics = Measure(cellWidth, lineHeight);
        Assert.InRange(DecorationGeometry.CurlyCentre(cellWidth, metrics) - DecorationGeometry.CurlyCentre(0, metrics), -1f / 256, 1f / 256);

        var single = DecorationGeometry.RasterizeUnderline(UnderlineStyle.Curl, cellWidth, metrics);
        var span = DecorationGeometry.RasterizeUnderline(UnderlineStyle.Curl, 3 * cellWidth, metrics);
        for (int x = 0; x < span.Width; x++)
        {
            int column = 0;
            for (int y = 0; y < span.Height; y++)
            {
                Assert.Equal(single[x % cellWidth, y], span[x, y]);
                column = Math.Max(column, span[x, y]);
            }
            // The stroke never breaks: every column carries ink.
            Assert.True(column >= 100, $"column {x} max coverage {column}");
        }
    }

    [Fact]
    public void Dotted_GoldenAtTenPixels()
    {
        var rects = DecorationGeometry.UnderlineRects(UnderlineStyle.Dotted, 10, Metrics());
        Assert.Equal(new[] { 0, 2, 4, 6, 8 }, rects.Select(r => r.X));
        Assert.All(rects, r => Assert.Equal((1, 17, 1), (r.Width, r.Y, r.Height)));
    }

    [Theory]
    [InlineData(10, 1, new[] { 2 }, 6)]
    [InlineData(16, 1, new[] { 1, 9 }, 5)]
    [InlineData(16, 2, new[] { 1, 9 }, 5)]
    [InlineData(7, 1, new[] { 1 }, 4)]
    public void Dashed_GoldenPositions(int width, int thickness, int[] starts, int length)
    {
        var rects = DecorationGeometry.UnderlineRects(UnderlineStyle.Dashed, width, Metrics(width, thickness: thickness));
        Assert.Equal(starts, rects.Select(r => r.X));
        Assert.All(rects, r => Assert.Equal((length, thickness), (r.Width, r.Height)));
    }

    [Theory]
    [InlineData(20, 17, 1, 17)]      // fits below the metric position
    [InlineData(20, 19, 1, 17)]      // pushed up to stay inside the cell
    [InlineData(20, 18, 2, 14)]      // 2 + 2 + 2 px from row 14
    public void Double_IsPushedUpIntoTheCell(int height, int underline, int thickness, int expectedTop)
    {
        var rects = DecorationGeometry.UnderlineRects(UnderlineStyle.Double, 10, Metrics(10, height, underline, thickness));
        Assert.Equal(expectedTop, rects[0].Y);
        Assert.Equal(height, Math.Max(height, rects[1].Bottom));
        Assert.Equal(thickness, rects[1].Y - rects[0].Bottom);
    }

    [Fact]
    public void Double_InATwoPixelCellDegradesToOneStroke()
    {
        var rects = DecorationGeometry.UnderlineRects(UnderlineStyle.Double, 4, Metrics(4, 2, 1, 1));
        Assert.Equal(new PixelRect(0, 1, 4, 1), Assert.Single(rects));
    }

    [Theory]
    [InlineData(true, false, 10, 1)]
    [InlineData(false, true, 0, 1)]
    [InlineData(true, true, 0, 11)]
    public void Lines_CoverStrikethroughAndOverlineRows(bool strike, bool overline, int top, int height)
    {
        var metrics = Metrics();
        var sprite = DecorationGeometry.RasterizeLines(strike, overline, 10, metrics);
        Assert.Equal((top, height), (sprite.Top, sprite.Height));
        for (int y = 0; y < sprite.Height; y++)
        {
            int row = sprite.Top + y;
            bool inked = (strike && row == 10) || (overline && row == 0);
            for (int x = 0; x < sprite.Width; x++)
                Assert.Equal(inked ? 255 : 0, sprite[x, y]);
        }
    }

    [Fact]
    public void Codes_RoundTripAndStayDistinct()
    {
        var codes = new List<int>();
        foreach (UnderlineStyle style in new[] { UnderlineStyle.Single, UnderlineStyle.Double, UnderlineStyle.Curl, UnderlineStyle.Dotted, UnderlineStyle.Dashed })
            codes.Add(DecorationGeometry.UnderlineCode(style));
        codes.Add(DecorationGeometry.LinesCode(true, false));
        codes.Add(DecorationGeometry.LinesCode(false, true));
        codes.Add(DecorationGeometry.LinesCode(true, true));
        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.All(codes, code => Assert.True(DecorationGeometry.IsDecorationCode(code)));
        Assert.False(DecorationGeometry.IsDecorationCode(0));
        Assert.False(DecorationGeometry.IsDecorationCode(DecorationGeometry.LinesCode(false, false)));
        Assert.False(DecorationGeometry.IsDecorationCode(0x2500));
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(10, 0)]
    [InlineData(257, 20)]
    public void InvalidMetrics_AreNotValid(int width, int height) =>
        Assert.False(new CellSpriteMetrics(width, height, 0, 0, 1, 0, 1, 0, 1).IsValid);

    [Fact]
    public void Create_ClampsStrokesIntoTheCell()
    {
        var m = CellSpriteMetrics.Create(8, 10, 30, 40, 0, -5, 99, 3, 2);
        Assert.Equal((10, 9, 1), (m.Baseline, m.UnderlinePosition, m.UnderlineThickness));
        Assert.Equal((0, 10), (m.StrikethroughPosition, m.StrikethroughThickness));
        Assert.Equal((3, 2), (m.OverlinePosition, m.OverlineThickness));
    }
}
