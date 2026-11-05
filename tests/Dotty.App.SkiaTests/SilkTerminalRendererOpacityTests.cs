using System;
using Dotty.Rendering.Gpu;
using Dotty.Silk;
using Dotty.Terminal.Adapter;
using SkiaSharp;
using Xunit;

namespace Dotty.App.SkiaTests;

/// <summary>
/// Readback proof of background-only opacity (W4-C12): with <see cref="SilkTerminalRenderer.BackgroundAlpha"/> below
/// 255 the clear colour is premultiplied while explicit cell backgrounds stay opaque. Skipped without a GL 3.3 context.
/// </summary>
[Collection(nameof(NativeGlCollection))]
public sealed class SilkTerminalRendererOpacityTests
{
    [Theory]
    [InlineData(255)]
    [InlineData(128)]
    [InlineData(0)]
    public void ClearIsPremultipliedAndCellBackgroundsStayOpaque(int alpha)
    {
        using ImageGlHost? host = ImageGlHost.TryCreate();
        if (host is null) return;
        using var atlas = new GlyphAtlas(SKTypeface.Default, 16f, initialSize: 128);
        using var renderer = new SilkTerminalRenderer(host.Gl, atlas) { BackgroundAlpha = (byte)alpha };
        var cell = new CellInstance
        {
            Col = 0,
            Row = 0,
            FgR = 255,
            FgG = 255,
            FgB = 255,
            FgA = 255,
            BgR = 255,
            BgG = 0,
            BgB = 0,
            BgA = 255,
        };

        renderer.Render(new[] { cell }, Array.Empty<ChromeQuadInstance>(), atlas.Width, atlas.Height, host.Width, host.Height,
            32f, 32f, clearColor: SgrColorArgb.FromRgb(200, 100, 50), frameCaptured: true);

        var cleared = host.ReadTopLeft(48, 48);
        Assert.InRange(cleared.R, 200 * alpha / 255 - 1, 200 * alpha / 255 + 1);
        Assert.InRange(cleared.G, 100 * alpha / 255 - 1, 100 * alpha / 255 + 1);
        Assert.InRange(cleared.B, 50 * alpha / 255 - 1, 50 * alpha / 255 + 1);
        // Hidden test windows may lack alpha bits, in which case alpha reads back as 255.
        Assert.True(cleared.A == 255 || Math.Abs(cleared.A - alpha) <= 1, $"alpha {cleared.A}");
        Assert.Equal((255, 0, 0), (host.ReadTopLeft(8, 8).R, host.ReadTopLeft(8, 8).G, host.ReadTopLeft(8, 8).B));
    }
}
