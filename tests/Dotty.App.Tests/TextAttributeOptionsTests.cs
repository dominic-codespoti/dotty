using Dotty.Rendering.Gpu;
using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

public sealed class TextAttributeOptionsTests
{
    [Theory]
    // foreground, background, opacity, expected
    [InlineData(0xFFFFFFFFu, 0xFF000000u, 0.5f, 0xFF808080u)]      // 127.5 rounds half away from zero
    [InlineData(0xFFD4D4D4u, 0xFF1E1E1Eu, 0.5f, 0xFF797979u)]      // 121 = 30 + 182 / 2
    [InlineData(0xFFFF0000u, 0xFF0000FFu, 0.25f, 0xFF4000BFu)]     // 63.75 -> 64, 191.25 -> 191
    [InlineData(0xFF102030u, 0xFFF0E0D0u, 1f, 0xFF102030u)]        // opacity 1 keeps the foreground
    [InlineData(0xFF102030u, 0xFFF0E0D0u, 0f, 0xFFF0E0D0u)]        // opacity 0 is the background
    [InlineData(0x80FFFFFFu, 0xFF000000u, 0.5f, 0x80808080u)]      // foreground alpha is kept
    [InlineData(0xFFFFFFFFu, 0xFF000000u, 2f, 0xFFFFFFFFu)]        // clamped to 1
    [InlineData(0xFFFFFFFFu, 0xFF000000u, -1f, 0xFF000000u)]       // clamped to 0
    [InlineData(0xFFFFFFFFu, 0xFF000000u, float.NaN, 0xFF808080u)] // non-finite uses the 0.5 default
    public void MixFaint_BlendsTowardTheBackground(uint foreground, uint background, float opacity, uint expected)
    {
        var mixed = TextAttributeOptions.MixFaint(new SgrColorArgb(foreground), new SgrColorArgb(background), opacity);

        Assert.Equal(new SgrColorArgb(expected), mixed);
    }

    [Theory]
    [InlineData(0.5, 0.5f)]
    [InlineData(0.3, 0.3f)]
    [InlineData(-0.2, 0f)]
    [InlineData(1.7, 1f)]
    [InlineData(double.NaN, 0.5f)]
    [InlineData(double.PositiveInfinity, 0.5f)]
    public void NormalizeFaintOpacity_ClampsAndDefaults(double configured, float expected) =>
        Assert.Equal(expected, TextAttributeOptions.NormalizeFaintOpacity(configured));

    [Fact]
    public void ConfigDefaultMatchesTheRendererDefault() =>
        Assert.Equal(TextAttributeOptions.DefaultFaintOpacity,
            TextAttributeOptions.NormalizeFaintOpacity(new Dotty.Runtime.Config.DottyUserConfig().Font.FaintOpacity));
}
