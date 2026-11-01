using System;
using Dotty.Silk.Rendering;
using Xunit;

namespace Dotty.App.Tests;

public sealed class CellMetricsCalculatorTests
{
    // A DejaVu-like face at 20 px: ascent 18.56, descent 4.72, advance 12.04.
    private static FontMetricsInput Face(
        float underlinePos = 1.75f, float underlineThick = 0.88f, float strikePos = -5.9f, float strikeThick = 0.98f, float xHeight = 10.9f) =>
        new(Ascent: -18.56f, Descent: 4.72f, Advance: 12.04f,
            UnderlinePosition: underlinePos, UnderlineThickness: underlineThick,
            StrikeoutPosition: strikePos, StrikeoutThickness: strikeThick, XHeight: xHeight);

    private static CellMetrics Compute(in FontMetricsInput face, float sizePx = 20f, double lineHeight = 1.0, float scale = 1f,
        CellAdjustment width = default, CellAdjustment height = default) =>
        CellMetricsCalculator.Compute(face, new CellMetricsOptions(sizePx, lineHeight, scale, width, height));

    [Theory]
    [InlineData(11.25, 1f, 15f)]
    [InlineData(10.5, 1f, 14f)]
    [InlineData(12, 1f, 16f)]
    [InlineData(11.25, 1.25f, 18.75f)]
    [InlineData(11.25, 1.5f, 22.5f)]
    [InlineData(11.25, 2f, 30f)]
    [InlineData(9, 3f, 36f)]
    [InlineData(12, 0f, 16f)]          // invalid scale treated as 1
    [InlineData(12, float.NaN, 16f)]
    public void PointsToPixels_UsesCssInchAndContentScale(double points, float scale, float expectedPx) =>
        Assert.Equal(expectedPx, CellMetricsCalculator.PointsToPixels(points, scale), 4);

    [Theory]
    // line height, expected cell height, expected baseline shift (floor of half the extra), baseline.
    [InlineData(1.0, 23, -1, 18)]  // font box 19 + 5 = 24 px but 23.28 rounds to 23: squeezed by one, shift floor(-1/2)
    [InlineData(1.25, 25, 0, 19)]  // 25 vs 24: one spare pixel goes below
    [InlineData(1.5, 30, 3, 22)]   // 6 spare pixels: 3 above, 3 below
    [InlineData(2.0, 40, 8, 27)]
    [InlineData(0.5, 23, -1, 18)]  // line height below the font box never shrinks the cell under the face height
    public void Compute_SplitsExtraLineHeightHalfAboveHalfBelow(double lineHeight, int expectedHeight, int expectedShift, int expectedBaseline)
    {
        var metrics = Compute(Face(), lineHeight: lineHeight);

        Assert.Equal(expectedHeight, metrics.CellHeight);
        Assert.Equal(expectedShift, metrics.BaselineShift);
        Assert.Equal(expectedBaseline, metrics.Baseline);
        Assert.Equal(metrics.Ascent + metrics.BaselineShift, metrics.Baseline);
    }

    [Fact]
    public void Compute_RoundsWidthAndFontBox()
    {
        var metrics = Compute(Face());
        Assert.Equal(12, metrics.CellWidth);
        Assert.Equal(19, metrics.Ascent);
        Assert.Equal(5, metrics.Descent);
    }

    [Fact]
    public void Compute_SqueezedFontBoxMovesTheBaselineUp()
    {
        // 23 px cell, 24 px box: shift -1 (floor), so the box overhangs by one pixel at the bottom only.
        var metrics = Compute(Face(), lineHeight: 1.0);
        Assert.Equal(-1, metrics.BaselineShift);
        Assert.Equal(18, metrics.Baseline);
    }

    [Theory]
    // underline position below baseline, thickness, expected top row, expected thickness.
    [InlineData(1.75f, 0.88f, 21, 1)]
    [InlineData(2.4f, 2.6f, 21, 3)]
    [InlineData(0.2f, 0.3f, 19, 1)]   // thin strokes are at least one pixel
    [InlineData(40f, 1f, 24, 1)]      // absurd position is clamped inside the cell (height 25)
    [InlineData(-30f, 1f, 0, 1)]
    public void Compute_UnderlineFromFontMetrics(float position, float thickness, int expectedTop, int expectedThickness)
    {
        var metrics = Compute(Face(underlinePos: position, underlineThick: thickness), lineHeight: 1.25);

        Assert.Equal(expectedTop, metrics.UnderlinePosition);
        Assert.Equal(expectedThickness, metrics.UnderlineThickness);
        Assert.True(metrics.UnderlinePosition + metrics.UnderlineThickness <= metrics.CellHeight);
        Assert.Equal(0, metrics.OverlinePosition);
        Assert.Equal(metrics.UnderlineThickness, metrics.OverlineThickness);
    }

    [Theory]
    [InlineData(float.NaN, float.NaN)]
    [InlineData(float.NaN, 0f)]
    [InlineData(float.NaN, -2f)]
    public void Compute_MissingUnderlineUsesFallbacks(float position, float thickness)
    {
        var metrics = Compute(Face(underlinePos: position, underlineThick: thickness), lineHeight: 1.25);

        // 20 px / 14 = 1.43 -> 1 px; top = baseline + round(max(1, 4.72/2 - 0.5)) = 19 + 2.
        Assert.Equal(1, metrics.UnderlineThickness);
        Assert.Equal(21, metrics.UnderlinePosition);
    }

    [Fact]
    public void Compute_StrikethroughFromFontMetrics()
    {
        // Skia: bottom of the stroke 5.9 px above the baseline; top = 19 - 5.9 - 1 = 12.1 -> 12.
        var metrics = Compute(Face(), lineHeight: 1.25);
        Assert.Equal(1, metrics.StrikethroughThickness);
        Assert.Equal(12, metrics.StrikethroughPosition);
    }

    [Theory]
    [InlineData(10.9f, 13)]       // centred on half the x-height: 19 - 5.45 - 0.5 = 13.05
    [InlineData(float.NaN, 14)]   // no x-height: half the ascent, 19 - 4.64 - 0.5 = 13.86
    public void Compute_MissingStrikethroughCentresOnTheXHeight(float xHeight, int expectedTop)
    {
        var metrics = Compute(Face(strikePos: float.NaN, strikeThick: float.NaN, xHeight: xHeight), lineHeight: 1.25);
        Assert.Equal(expectedTop, metrics.StrikethroughPosition);
        Assert.Equal(1, metrics.StrikethroughThickness);
    }

    [Theory]
    [InlineData(1f, 1)]
    [InlineData(1.25f, 2)]   // 25 px font: 25/14 = 1.79 -> 2
    [InlineData(2f, 3)]      // 40 px: 2.86 -> 3
    [InlineData(3f, 4)]      // 60 px: 4.29 -> 4
    public void Compute_FallbackThicknessScalesWithFontSize(float scale, int expected)
    {
        var face = Face(underlineThick: float.NaN, strikeThick: float.NaN);
        var scaled = face with { Ascent = face.Ascent * scale, Descent = face.Descent * scale, Advance = face.Advance * scale };
        var metrics = Compute(scaled, sizePx: 20f * scale, scale: scale);
        Assert.Equal(expected, metrics.UnderlineThickness);
        Assert.Equal(expected, metrics.StrikethroughThickness);
    }

    [Theory]
    // adjust text, scale, natural 12 x 25 -> expected
    [InlineData("", 1f, 12, 25)]
    [InlineData("0", 1f, 12, 25)]
    [InlineData("1", 1f, 13, 26)]
    [InlineData("-1", 1f, 11, 24)]
    [InlineData("2", 1.5f, 15, 28)]     // logical px times the content scale
    [InlineData("0.5", 1f, 13, 26)]     // 12.5 rounds away from zero
    [InlineData("10%", 2f, 13, 28)]     // percent ignores the scale: 13.2, 27.5
    [InlineData("-50%", 1f, 6, 13)]
    [InlineData("-100%", 1f, 1, 1)]     // never below one pixel
    [InlineData("-100", 1f, 1, 1)]
    public void Compute_AppliesCellAdjustments(string adjust, float scale, int expectedWidth, int expectedHeight)
    {
        var a = CellAdjustment.Parse(adjust);
        var metrics = Compute(Face(), lineHeight: 1.25, scale: scale, width: a, height: a);

        Assert.Equal(expectedWidth, metrics.CellWidth);
        Assert.Equal(expectedHeight, metrics.CellHeight);
        Assert.InRange(metrics.UnderlinePosition, 0, metrics.CellHeight - metrics.UnderlineThickness);
        Assert.InRange(metrics.StrikethroughPosition, 0, metrics.CellHeight - metrics.StrikethroughThickness);
    }

    [Theory]
    [InlineData("", true, 0d, false)]
    [InlineData("  ", true, 0d, false)]
    [InlineData("3", true, 3d, false)]
    [InlineData(" -2.5 ", true, -2.5d, false)]
    [InlineData("+4", true, 4d, false)]
    [InlineData("15%", true, 15d, true)]
    [InlineData("-7 %", true, -7d, true)]
    [InlineData("100", true, 100d, false)]
    [InlineData("101", false, 0d, false)]
    [InlineData("-101%", false, 0d, false)]
    [InlineData("abc", false, 0d, false)]
    [InlineData("%", false, 0d, false)]
    [InlineData("1e3", false, 0d, false)]
    [InlineData("NaN", false, 0d, false)]
    [InlineData("1,5", false, 0d, false)]
    public void CellAdjustment_TryParse(string text, bool ok, double value, bool percent)
    {
        Assert.Equal(ok, CellAdjustment.TryParse(text, out var adjustment));
        Assert.Equal(new CellAdjustment(value, percent), adjustment);
    }

    [Fact]
    public void Compute_DegenerateFontFallsBackToTheFontSize()
    {
        var metrics = Compute(new FontMetricsInput(float.NaN, float.NaN, float.NaN), sizePx: 20f);

        Assert.Equal(12, metrics.CellWidth);   // 0.6 em
        Assert.Equal(20, metrics.CellHeight);
        Assert.Equal(20, metrics.Ascent);
        Assert.Equal(0, metrics.Descent);
        Assert.InRange(metrics.UnderlinePosition, 0, metrics.CellHeight - 1);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-5f)]
    [InlineData(float.NaN)]
    public void Compute_InvalidFontSizeStillYieldsAPixel(float size)
    {
        var metrics = Compute(new FontMetricsInput(0f, 0f, 0f), sizePx: size, lineHeight: double.NaN);
        Assert.True(metrics.CellWidth >= 1);
        Assert.True(metrics.CellHeight >= 1);
        Assert.True(metrics.UnderlineThickness >= 1);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.25f)]
    [InlineData(1.5f)]
    [InlineData(1.75f)]
    [InlineData(2f)]
    [InlineData(3f)]
    public void Compute_CellsAreIntegersThatTrackTheScale(float scale)
    {
        var face = Face();
        var scaled = face with { Ascent = face.Ascent * scale, Descent = face.Descent * scale, Advance = face.Advance * scale };
        var metrics = Compute(scaled, sizePx: 20f * scale, lineHeight: 1.25, scale: scale);

        Assert.Equal((int)MathF.Round(12.04f * scale, MidpointRounding.AwayFromZero), metrics.CellWidth);
        Assert.Equal((int)MathF.Round(25f * scale, MidpointRounding.AwayFromZero), metrics.CellHeight);
        Assert.InRange(metrics.Baseline, metrics.Ascent - 1, metrics.CellHeight);
    }
}
