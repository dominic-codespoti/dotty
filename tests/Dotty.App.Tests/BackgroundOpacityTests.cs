using System;
using Dotty.Silk.Rendering;
using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

public sealed class BackgroundOpacityTests
{
    // config opacity (0-1), theme opacity (0-100), expected alpha.
    public static TheoryData<double, byte, byte> ResolveCases => new()
    {
        { 1.0, 100, 255 },
        { 0.0, 100, 0 },
        { 0.5, 100, 128 },
        { 0.8, 100, 204 },
        { 1.0, 50, 128 },
        { 0.5, 50, 64 },
        { 0.9, 0, 0 },
        // Clamping: out-of-range config, theme above 100.
        { 1.5, 100, 255 },
        { -0.25, 100, 0 },
        { 1.0, 200, 255 },
        { 0.5, 255, 128 },
        // Non-finite config values mean opaque.
        { double.NaN, 100, 255 },
        { double.PositiveInfinity, 100, 255 },
        { double.NegativeInfinity, 80, 204 },
    };

    [Theory]
    [MemberData(nameof(ResolveCases))]
    public void Resolve_MultipliesConfigAndThemeOpacity(double config, byte theme, byte expected)
    {
        Assert.Equal(expected, BackgroundOpacity.Resolve(config, theme));
        Assert.Equal(expected < 255, BackgroundOpacity.RequiresAlphaFramebuffer(config, theme));
    }

    [Theory]
    [InlineData(0xFF336699u, 255, 0x33 / 255f, 0x66 / 255f, 0x99 / 255f, 1f)]
    [InlineData(0xFF336699u, 0, 0f, 0f, 0f, 0f)]
    [InlineData(0xFFFFFFFFu, 128, 128 / 255f, 128 / 255f, 128 / 255f, 128 / 255f)]
    // The colour's own alpha byte is ignored: the opacity argument alone decides.
    [InlineData(0x00336699u, 255, 0x33 / 255f, 0x66 / 255f, 0x99 / 255f, 1f)]
    public void PremultipliedClear_ScalesChannelsByAlpha(uint argb, byte alpha, float r, float g, float b, float a)
    {
        var clear = BackgroundOpacity.PremultipliedClear(new SgrColorArgb(argb), alpha);

        Assert.Equal(r, clear.R, 5);
        Assert.Equal(g, clear.G, 5);
        Assert.Equal(b, clear.B, 5);
        Assert.Equal(a, clear.A, 5);
        Assert.True(clear.R <= clear.A && clear.G <= clear.A && clear.B <= clear.A, "premultiplied channels never exceed alpha");
    }
}
