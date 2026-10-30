using Dotty.Abstractions.Graphics;
using Dotty.Rendering.Gpu;
using Dotty.Silk.Rendering;
using Xunit;

namespace Dotty.App.Tests;

public sealed class FramebufferScreenshotTests
{
    // 2x2 readback, bottom row first as glReadPixels returns it.
    private static readonly byte[] BottomUp =
    {
        // bottom row: opaque blue, premultiplied 50% white (128,128,128,128)
        0, 0, 255, 255,   128, 128, 128, 128,
        // top row: opaque red, transparent (0,0,0,0)
        255, 0, 0, 255,   0, 0, 0, 0,
    };

    private static byte[] Decode(byte[] png)
    {
        var straight = new byte[2 * 2 * 4];
        Assert.Equal(ImageDecodeStatus.Ok, new SkiaImageDecoder().DecodePngRgba(png, 2, 2, straight));
        return straight;
    }

    [Fact]
    public void FlipsRowsAndUnpremultipliesTranslucentPixels()
    {
        byte[] rgba = Decode(FramebufferScreenshot.EncodePng(BottomUp, 2, 2, opaque: false));

        // Top-down: first row is the readback's last row.
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, rgba[0..4]);
        Assert.Equal((byte)0, rgba[7]);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, rgba[8..12]);
        // 128/128 premultiplied is straight white at alpha 128.
        Assert.Equal(new byte[] { 255, 255, 255, 128 }, rgba[12..16]);
    }

    [Fact]
    public void OpaqueWindowIgnoresFramebufferAlpha()
    {
        byte[] rgba = Decode(FramebufferScreenshot.EncodePng(BottomUp, 2, 2, opaque: true));

        Assert.Equal(new byte[] { 0, 0, 0, 255 }, rgba[4..8]);
        Assert.Equal(new byte[] { 128, 128, 128, 255 }, rgba[12..16]);
    }

    [Fact]
    public void RejectsShortBuffers() =>
        Assert.Throws<ArgumentException>(() => FramebufferScreenshot.EncodePng(new byte[15], 2, 2, opaque: true));
}
