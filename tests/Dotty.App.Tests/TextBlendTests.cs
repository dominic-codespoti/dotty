using System;
using Dotty.Rendering.Gpu;
using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

public sealed class TextBlendTests
{
    private static readonly SgrColorArgb White = SgrColorArgb.FromRgb(255, 255, 255);
    private static readonly SgrColorArgb Black = SgrColorArgb.FromRgb(0, 0, 0);

    private static void AssertGrey(int expected, SgrColorArgb actual, int tolerance = 1)
    {
        Assert.InRange(actual.R, expected - tolerance, expected + tolerance);
        Assert.Equal(actual.R, actual.G);
        Assert.Equal(actual.R, actual.B);
    }

    [Fact]
    public void Native_IsTheGammaSpaceBlendOfToday()
    {
        // 128/255 coverage of white over black lands on 128; of black over white on 127.
        AssertGrey(128, TextBlend.Blend(TextBlendMode.Native, White, Black, 128, 0.5f), tolerance: 0);
        AssertGrey(127, TextBlend.Blend(TextBlendMode.Native, Black, White, 128, 0.5f), tolerance: 0);
        Assert.Equal(0.3f, CoverageCorrection.Correct(TextBlendMode.Native, 0.3f, 1f, 0f, 1f));
    }

    [Fact]
    public void Linear_MatchesLinearLightLuminance()
    {
        // Hand-computed: encode(0.502) = 0.737 -> 188; black on white: encode(0.498) = 0.734 -> 187.
        AssertGrey(188, TextBlend.Blend(TextBlendMode.Linear, White, Black, 128, 0.5f));
        AssertGrey(187, TextBlend.Blend(TextBlendMode.Linear, Black, White, 128, 0.5f));
    }

    [Fact]
    public void LinearCorrected_BoostsDarkTextOnLightBackgrounds()
    {
        // a = 0.266 + 1 * 0.266 * 0.734 = 0.461 -> 255 * 0.539 = 137.
        AssertGrey(137, TextBlend.Blend(TextBlendMode.LinearCorrected, Black, White, 128, 1f));
        // Light on dark gets no boost (weight 0.5 + 0.5 * (0 - 1) = 0): same as linear.
        AssertGrey(188, TextBlend.Blend(TextBlendMode.LinearCorrected, White, Black, 128, 1f));
        // Contrast 0 is plain linear.
        Assert.Equal(
            TextBlend.Blend(TextBlendMode.Linear, Black, White, 77, 0.5f),
            TextBlend.Blend(TextBlendMode.LinearCorrected, Black, White, 77, 0f));
    }

    [Theory]
    [InlineData(TextBlendMode.Native)]
    [InlineData(TextBlendMode.Linear)]
    [InlineData(TextBlendMode.LinearCorrected)]
    public void EmptyAndFullCoverageAndEqualColoursAreFixedPoints(TextBlendMode mode)
    {
        var fg = SgrColorArgb.FromRgb(230, 200, 40);
        var bg = SgrColorArgb.FromRgb(20, 30, 60);
        Assert.Equal(bg, TextBlend.Blend(mode, fg, bg, 0, 1f));
        Assert.Equal(fg, TextBlend.Blend(mode, fg, bg, 255, 1f));
        Assert.Equal(bg, TextBlend.Blend(mode, bg, bg, 140, 1f));
    }

    [Theory]
    [InlineData(TextBlendMode.Linear, 0.5f)]
    [InlineData(TextBlendMode.LinearCorrected, 1f)]
    public void CorrectionIsMonotonicInCoverage(TextBlendMode mode, float contrast)
    {
        foreach (var (fgLuma, bgLuma) in new[] { (1f, 0f), (0f, 1f), (0.4f, 0.05f), (0.02f, 0.6f) })
        {
            float previous = 0f;
            for (int c = 0; c <= 255; c++)
            {
                float a = CoverageCorrection.Correct(mode, c / 255f, fgLuma, bgLuma, contrast);
                Assert.InRange(a, previous - 1e-6f, 1f);
                previous = a;
            }
            Assert.Equal(1f, previous, 5);
        }
    }

    [Theory]
    [InlineData("native", TextBlendMode.Native)]
    [InlineData("LINEAR", TextBlendMode.Linear)]
    [InlineData(" linear-corrected ", TextBlendMode.LinearCorrected)]
    [InlineData("gamma", TextBlendMode.Native)]
    [InlineData(null, TextBlendMode.Native)]
    public void ParsesTheBlendingKey(string? value, TextBlendMode expected) =>
        Assert.Equal(expected, CoverageCorrection.ParseMode(value));

    [Theory]
    [InlineData(0.25, 0.25f)]
    [InlineData(-1.0, 0f)]
    [InlineData(3.0, 1f)]
    [InlineData(double.NaN, CoverageCorrection.DefaultContrast)]
    public void NormalizesContrast(double configured, float expected) =>
        Assert.Equal(expected, CoverageCorrection.NormalizeContrast(configured));

    [Fact]
    public void ConfigDefaultsAreNative()
    {
        var text = new Dotty.Runtime.Config.DottyUserConfig().Text;
        Assert.Equal(TextBlendMode.Native, CoverageCorrection.ParseMode(text.Blending));
        Assert.Equal(CoverageCorrection.DefaultContrast, CoverageCorrection.NormalizeContrast(text.Contrast));
    }

    [Fact]
    public void SrgbTransferRoundTrips()
    {
        for (int i = 0; i <= 255; i++)
        {
            float c = i / 255f;
            Assert.Equal(c, CoverageCorrection.LinearToSrgb(CoverageCorrection.SrgbToLinear(c)), 4);
        }
    }
}
