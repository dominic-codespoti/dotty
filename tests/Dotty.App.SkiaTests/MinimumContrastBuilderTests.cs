using System;
using Dotty.Rendering.Gpu;
using Dotty.Terminal.Adapter;
using SkiaSharp;
using Xunit;

namespace Dotty.App.SkiaTests;

/// <summary><c>text.minimumContrast</c> in the <see cref="QuadFrameBuilder"/> colour path: after inverse and faint.</summary>
public sealed class MinimumContrastBuilderTests
{
    private const float TextSize = 16f;

    private static CellInstance BuildOne(CellAttributes attrs, float minimumContrast)
    {
        var buffer = new TerminalBuffer(rows: 1, columns: 5);
        buffer.SetCursor(0, 0);
        buffer.WriteText("X".AsSpan(), attrs);
        using var atlas = new GlyphAtlas(SKTypeface.Default, TextSize);
        var options = new TextAttributeOptions(0.5f, BlinkVisible: true) { MinimumContrast = minimumContrast };
        var result = QuadFrameBuilder.Build(buffer, atlas, SKTypeface.Default, TextSize, new FrameGeometry(10f, 20f, 1, 5),
            textOptions: options);
        return Assert.Single(result.Instances.AsSpan(0, result.InstanceCount).ToArray());
    }

    [Theory]
    // inverse, faint, floor -> expected fg, expected bg (golden)
    [InlineData(false, false, 4.5f, 23, 128)]   // #5A5A5A on #808080 darkens to #171717
    [InlineData(true, false, 4.5f, 23, 128)]    // inverse: the visible pair is the same
    [InlineData(false, false, 1f, 90, 128)]     // off
    public void FloorsTheVisibleForegroundAfterInverse(bool inverse, bool faint, float floor, int fg, int bg)
    {
        var attrs = new CellAttributes
        {
            Foreground = inverse ? SgrColorArgb.FromRgb(128, 128, 128) : SgrColorArgb.FromRgb(90, 90, 90),
            Background = inverse ? SgrColorArgb.FromRgb(90, 90, 90) : SgrColorArgb.FromRgb(128, 128, 128),
            Inverse = inverse,
            Faint = faint,
        };

        CellInstance inst = BuildOne(attrs, floor);

        Assert.Equal((fg, fg, fg), ((int)inst.FgR, (int)inst.FgG, (int)inst.FgB));
        Assert.Equal((bg, bg, bg), ((int)inst.BgR, (int)inst.BgG, (int)inst.BgB));
    }

    [Theory]
    [InlineData(4.5f, 117)] // faint #646464 on black is floored to #757575
    [InlineData(1f, 100)]   // off: faint keeps half of #C8C8C8
    public void FloorsFaintTextAfterTheFaintMix(float floor, int expected)
    {
        var attrs = new CellAttributes
        {
            Foreground = SgrColorArgb.FromRgb(200, 200, 200),
            Background = SgrColorArgb.FromRgb(0, 0, 0),
            Faint = true,
        };

        CellInstance inst = BuildOne(attrs, floor);

        Assert.Equal((expected, expected, expected), ((int)inst.FgR, (int)inst.FgG, (int)inst.FgB));
    }
}
