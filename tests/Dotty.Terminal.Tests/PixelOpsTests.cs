using Dotty.Terminal.Graphics;
using Xunit;

namespace Dotty.Terminal.Tests;

public sealed class PixelOpsTests
{
    private static int RefPremul(int c, int a) => (c * a + 127) / 255;

    [Fact]
    public void PremultiplyMatchesScalarReferenceForEveryColourAlphaPair()
    {
        // 65536 pixels (blocks of 4 pixels go through the vector path); each colour channel carries a different
        // value so lane mix-ups between R, G, B and alpha broadcast would show.
        var buf = new byte[65536 * 4];
        for (int a = 0; a < 256; a++)
            for (int c = 0; c < 256; c++)
            {
                int o = (a * 256 + c) * 4;
                buf[o] = (byte)c;
                buf[o + 1] = (byte)(c * 7 + 13);
                buf[o + 2] = (byte)(255 - c);
                buf[o + 3] = (byte)a;
            }
        var expected = new byte[buf.Length];
        for (int i = 0; i < buf.Length; i += 4)
        {
            int a = buf[i + 3];
            expected[i] = (byte)RefPremul(buf[i], a);
            expected[i + 1] = (byte)RefPremul(buf[i + 1], a);
            expected[i + 2] = (byte)RefPremul(buf[i + 2], a);
            expected[i + 3] = (byte)a;
        }
        PixelOps.PremultiplyInPlace(buf);
        for (int i = 0; i < buf.Length; i++)
            if (buf[i] != expected[i])
                Assert.Fail($"byte {i} (pixel {i / 4}, lane {i % 4}): got {buf[i]}, expected {expected[i]}");
    }

    [Fact]
    public void EveryLengthIncludingVectorTailsMatchesTheScalarPath()
    {
        var rng = new Random(11);
        for (int px = 0; px <= 70; px++)
        {
            var a = new byte[px * 4];
            rng.NextBytes(a);
            var b = (byte[])a.Clone();
            PixelOps.PremultiplyInPlace(a);
            PixelOps.PremultiplyScalar(b);
            Assert.Equal(b, a);
        }
    }

    [Theory]
    [InlineData(255, 0, 0)]
    [InlineData(255, 1, 1)]     // (255*1+127)/255 = 1
    [InlineData(128, 1, 1)]     // (128+127)/255 = 1  (round half up of 0.50196)
    [InlineData(127, 1, 0)]     // (127+127)/255 = 0
    [InlineData(255, 254, 254)] // (64770+127)/255 = 254
    [InlineData(1, 254, 1)]     // (254+127)/255 = 1
    [InlineData(0, 254, 0)]
    [InlineData(255, 255, 255)]
    [InlineData(1, 255, 1)]
    [InlineData(200, 128, 100)] // (25600+127)/255 = 100
    public void PremultiplyBoundaryValues(int c, int a, int expected)
    {
        // 5 pixels so the first four take the vector path and the last the scalar tail.
        var buf = new byte[20];
        for (int p = 0; p < 5; p++)
        {
            buf[p * 4] = buf[p * 4 + 1] = buf[p * 4 + 2] = (byte)c;
            buf[p * 4 + 3] = (byte)a;
        }
        PixelOps.PremultiplyInPlace(buf);
        for (int p = 0; p < 5; p++)
        {
            Assert.Equal(expected, buf[p * 4]);
            Assert.Equal(expected, buf[p * 4 + 1]);
            Assert.Equal(expected, buf[p * 4 + 2]);
            Assert.Equal(a, buf[p * 4 + 3]);
        }
    }

    [Fact]
    public void FullyOpaqueBufferIsUntouchedAndTransparentColoursAreZeroed()
    {
        var opaque = new byte[64];
        for (int i = 0; i < 64; i++) opaque[i] = (byte)(i % 4 == 3 ? 255 : i * 3);
        var copy = (byte[])opaque.Clone();
        PixelOps.PremultiplyInPlace(opaque);
        Assert.Equal(copy, opaque);

        var clear = new byte[32];
        for (int i = 0; i < 32; i++) clear[i] = (byte)(i % 4 == 3 ? 0 : 200);
        PixelOps.PremultiplyInPlace(clear);
        Assert.True(clear.AsSpan().IndexOfAnyExcept((byte)0) < 0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(18)]
    public void PremultiplyRejectsPartialPixels(int length)
    {
        Assert.Throws<ArgumentException>(() => PixelOps.PremultiplyInPlace(new byte[length]));
        Assert.Throws<ArgumentException>(() => PixelOps.UnpremultiplyInPlace(new byte[length]));
    }

    [Fact]
    public void UnpremultiplyKnownValues()
    {
        byte[] px = [64, 32, 255, 128,   10, 20, 30, 255,   9, 9, 9, 0,   255, 255, 255, 100];
        PixelOps.UnpremultiplyInPlace(px);
        // a=128: (64*255+64)/128 = 128; (32*255+64)/128 = 64; (255*255+64)/128 = 508 -> 255
        Assert.Equal(new byte[] { 128, 64, 255, 128 }, px.AsSpan(0, 4).ToArray());
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, px.AsSpan(4, 4).ToArray());
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, px.AsSpan(8, 4).ToArray());
        Assert.Equal(new byte[] { 255, 255, 255, 100 }, px.AsSpan(12, 4).ToArray());
    }

    [Fact]
    public void PremultiplyThenUnpremultiplyIsWithinOneForAlphaAtLeast128()
    {
        for (int a = 128; a <= 255; a++)
            for (int c = 0; c < 256; c++)
            {
                byte[] px = [(byte)c, (byte)c, (byte)c, (byte)a];
                PixelOps.PremultiplyInPlace(px);
                PixelOps.UnpremultiplyInPlace(px);
                Assert.InRange(px[0] - c, -1, 1);
                Assert.Equal(a, px[3]);
            }
    }

    [Theory]
    [InlineData(100, 50, 200, 100, 50)]
    [InlineData(200, 100, 200, 200, 100)]     // exactly at the limit is untouched
    [InlineData(4000, 3000, 2048, 2048, 1536)]
    [InlineData(3000, 4000, 2048, 1536, 2048)]
    [InlineData(5000, 5000, 4096, 4096, 4096)]
    [InlineData(10000, 1, 2048, 2048, 1)]     // thin images keep at least one pixel
    [InlineData(1, 10000, 2048, 1, 2048)]
    [InlineData(2049, 1000, 2048, 2048, 1000)] // 1000*2048/2049 = 999.5 -> rounds to 1000
    [InlineData(3, 2, 2, 2, 1)]                // 2*2/3 = 1.33 -> 1
    [InlineData(5, 3, 4, 4, 2)]                // 3*4/5 = 2.4 -> 2
    [InlineData(7, 5, 4, 4, 3)]                // 5*4/7 = 2.86 -> 3
    public void ComputeDownscaledSize(int w, int h, int max, int expectedW, int expectedH)
    {
        Assert.Equal((expectedW, expectedH), PixelOps.ComputeDownscaledSize(w, h, max));
    }

    [Fact]
    public void ComputeDownscaledSizeNeverExceedsLimitOrOriginal()
    {
        var rng = new Random(3);
        for (int i = 0; i < 5000; i++)
        {
            int w = rng.Next(1, 20000), h = rng.Next(1, 20000), max = rng.Next(1, 8192);
            var (nw, nh) = PixelOps.ComputeDownscaledSize(w, h, max);
            Assert.InRange(nw, 1, max);
            Assert.InRange(nh, 1, max);
            Assert.True(nw <= w && nh <= h);
            if (w > max || h > max) Assert.Equal(max, Math.Max(nw, nh));
        }
        var (bw, bh) = PixelOps.ComputeDownscaledSize(int.MaxValue, int.MaxValue - 1, 16384);
        Assert.Equal(16384, bw);
        Assert.Equal(16384, bh); // (2^31-2)*16384 / (2^31-1) = 16383.99.. rounds to 16384
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1, 0)]
    public void ComputeDownscaledSizeRejectsNonPositiveArguments(int w, int h, int max)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelOps.ComputeDownscaledSize(w, h, max));
    }

    private static ImagePixels Make(int w, int h, ImagePixelFormat f, params byte[] data)
    {
        var px = ImagePixels.Allocate(w, h, f);
        data.CopyTo(px.AsWritableSpan());
        return px;
    }

    [Fact]
    public void BoxDownscaleAveragesAFourPixelBlockToOnePixel()
    {
        using var src = Make(2, 2, ImagePixelFormat.Rgba8Premultiplied,
            10, 20, 30, 40, 30, 40, 50, 60, 50, 60, 70, 80, 70, 80, 90, 100);
        using var dst = PixelOps.BoxDownscale(src, 1, 1);
        Assert.Equal(ImagePixelFormat.Rgba8Premultiplied, dst.Format);
        Assert.Equal(new byte[] { 40, 50, 60, 70 }, dst.AsSpan().ToArray());
    }

    [Fact]
    public void BoxDownscaleRoundsHalfUp()
    {
        // channel sums: R=1 -> 0.25 -> 0, G=2 -> 0.5 -> 1 (half up), B=3 -> 0.75 -> 1, A=4 -> 1
        using var src = Make(2, 2, ImagePixelFormat.Rgba8Premultiplied,
            0, 0, 0, 1, 0, 1, 1, 1, 0, 0, 1, 1, 1, 1, 1, 1);
        using var dst = PixelOps.BoxDownscale(src, 1, 1);
        Assert.Equal(new byte[] { 0, 1, 1, 1 }, dst.AsSpan().ToArray());
    }

    [Fact]
    public void BoxDownscaleWorksOnRgb8WithItsStride()
    {
        using var src = Make(2, 2, ImagePixelFormat.Rgb8, 0, 0, 0, 255, 255, 255, 255, 0, 0, 0, 255, 0);
        using var dst = PixelOps.BoxDownscale(src, 1, 1);
        Assert.Equal(ImagePixelFormat.Rgb8, dst.Format);
        Assert.Equal(3, dst.Stride);
        // R: 0+255+255+0=510/4=127.5 -> 128; G: 0+255+0+255=510 -> 128; B: 0+255+0+0=255/4=63.75 -> 64
        Assert.Equal(new byte[] { 128, 128, 64 }, dst.AsSpan().ToArray());
    }

    [Fact]
    public void BoxDownscaleOverlapsSourceColumnsWhenTheRatioIsFractional()
    {
        // 3 -> 2 columns: dst0 covers source [0,2), dst1 covers [1,3); the middle pixel contributes to both.
        using var src = Make(3, 1, ImagePixelFormat.Rgb8, 0, 0, 0, 90, 90, 90, 180, 180, 180);
        using var dst = PixelOps.BoxDownscale(src, 2, 1);
        Assert.Equal(new byte[] { 45, 45, 45, 135, 135, 135 }, dst.AsSpan().ToArray());
    }

    [Fact]
    public void BoxDownscaleAveragesAWholeRow()
    {
        var data = new byte[100 * 3];
        for (int i = 0; i < 100; i++) data[i * 3] = (byte)i;
        using var src = Make(100, 1, ImagePixelFormat.Rgb8, data);
        using var dst = PixelOps.BoxDownscale(src, 1, 1);
        Assert.Equal(50, dst.AsSpan()[0]); // (4950 + 50) / 100
        Assert.Equal(0, dst.AsSpan()[1]);
    }

    [Fact]
    public void BoxDownscaleKeepsUniformImagesUniformAndSameSizeIsACopy()
    {
        var uniform = new byte[7 * 5 * 4];
        for (int i = 0; i < uniform.Length; i += 4) { uniform[i] = 9; uniform[i + 1] = 8; uniform[i + 2] = 7; uniform[i + 3] = 255; }
        using var src = Make(7, 5, ImagePixelFormat.Rgba8Premultiplied, uniform);
        using var small = PixelOps.BoxDownscale(src, 3, 2);
        for (int i = 0; i < small.AsSpan().Length; i += 4)
            Assert.Equal(new byte[] { 9, 8, 7, 255 }, small.AsSpan().Slice(i, 4).ToArray());

        using var copy = PixelOps.BoxDownscale(src, 7, 5);
        Assert.NotSame(src, copy);
        Assert.NotEqual(src.Serial, copy.Serial);
        Assert.Equal(src.AsSpan().ToArray(), copy.AsSpan().ToArray());
    }

    [Fact]
    public void BoxDownscaleVerticalPartitionMatchesHandComputation()
    {
        // 1 column, 4 rows -> 3 rows. dst0: [0, ceil(4/3)=2), dst1: [4/3=1, ceil(8/3)=3), dst2: [8/3=2, 4)
        using var src = Make(1, 4, ImagePixelFormat.Rgb8, 10, 10, 10, 20, 20, 20, 40, 40, 40, 80, 80, 80);
        using var dst = PixelOps.BoxDownscale(src, 1, 3);
        // (10+20)/2=15 ; (20+40)/2=30 ; (40+80)/2=60
        Assert.Equal(new byte[] { 15, 15, 15, 30, 30, 30, 60, 60, 60 }, dst.AsSpan().ToArray());
    }

    [Fact]
    public void BoxDownscaleRejectsUpscalingAndBadSizes()
    {
        using var src = Make(4, 4, ImagePixelFormat.Rgb8);
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelOps.BoxDownscale(src, 5, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelOps.BoxDownscale(src, 4, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelOps.BoxDownscale(src, 0, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelOps.BoxDownscale(src, 4, 0));
        Assert.Throws<ArgumentNullException>(() => PixelOps.BoxDownscale(null!, 1, 1));
    }

    // ---- CompositeRect ----------------------------------------------------------------------------------------

    private static byte[] Solid(int w, int h, byte r, byte g, byte b, byte a, int stride = 0)
    {
        if (stride == 0) stride = w * 4;
        var buf = new byte[stride * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = y * stride + x * 4;
                buf[o] = r; buf[o + 1] = g; buf[o + 2] = b; buf[o + 3] = a;
            }
        return buf;
    }

    [Fact]
    public void CompositeRectCopyWritesOnlyTheRequestedRectangle()
    {
        var dst = Solid(4, 3, 1, 2, 3, 4);
        var src = Solid(2, 2, 200, 201, 202, 203);
        Assert.True(PixelOps.CompositeRect(dst, 16, 4, 3, 1, 1, src, 8, 2, 2, 0, 0, 2, 2, blend: false));
        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 4; x++)
            {
                bool inside = x is 1 or 2 && y is 1 or 2;
                var px = dst.AsSpan(y * 16 + x * 4, 4).ToArray();
                Assert.Equal(inside ? new byte[] { 200, 201, 202, 203 } : new byte[] { 1, 2, 3, 4 }, px);
            }
    }

    [Fact]
    public void CompositeRectHonoursSourceOffsetAndStridePadding()
    {
        // Source is 3x2 with 4 padding bytes per row; copy its right 2 columns of the bottom row only.
        var src = new byte[(3 * 4 + 4) * 2];
        for (int i = 0; i < src.Length; i++) src[i] = (byte)(i + 1);
        var dst = new byte[2 * 4];
        Assert.True(PixelOps.CompositeRect(dst, 8, 2, 1, 0, 0, src, 16, 3, 2, 1, 1, 2, 1, blend: false));
        Assert.Equal(src.AsSpan(16 + 4, 8).ToArray(), dst);

        // Padding bytes in dst are not written.
        var padded = new byte[12];
        Array.Fill(padded, (byte)0xEE);
        Assert.True(PixelOps.CompositeRect(padded, 12, 2, 1, 0, 0, src, 16, 3, 2, 0, 0, 2, 1, blend: false));
        Assert.Equal(new byte[] { 0xEE, 0xEE, 0xEE, 0xEE }, padded.AsSpan(8, 4).ToArray());
    }

    [Fact]
    public void BlendWithOpaqueSourceReplacesAndTransparentSourceKeepsDestination()
    {
        var dst = Solid(2, 1, 50, 60, 70, 255);
        byte[] src = [9, 8, 7, 255,   0, 0, 0, 0];
        Assert.True(PixelOps.CompositeRect(dst, 8, 2, 1, 0, 0, src, 8, 2, 1, 0, 0, 2, 1, blend: true));
        Assert.Equal(new byte[] { 9, 8, 7, 255, 50, 60, 70, 255 }, dst);
    }

    [Fact]
    public void BlendHalfTransparentPremultipliedSourceOverOpaqueDestination()
    {
        byte[] dst = [200, 100, 0, 255];
        byte[] src = [64, 0, 0, 128];
        Assert.True(PixelOps.CompositeRect(dst, 4, 1, 1, 0, 0, src, 4, 1, 1, 0, 0, 1, 1, blend: true));
        // inv = 127: R = 64 + (200*127+127)/255 = 64 + 100; G = (100*127+127)/255 = 50; A = 128 + 127 = 255
        Assert.Equal(new byte[] { 164, 50, 0, 255 }, dst);
    }

    [Fact]
    public void BlendOverTransparentDestinationYieldsTheSource()
    {
        byte[] dst = [0, 0, 0, 0];
        byte[] src = [30, 20, 10, 40];
        PixelOps.CompositeRect(dst, 4, 1, 1, 0, 0, src, 4, 1, 1, 0, 0, 1, 1, blend: true);
        Assert.Equal(src, dst);
    }

    [Fact]
    public void BlendSaturatesInsteadOfWrapping()
    {
        byte[] dst = [255, 255, 255, 255];
        byte[] src = [255, 255, 255, 10]; // not a valid premultiplied pixel; must still clamp
        PixelOps.CompositeRect(dst, 4, 1, 1, 0, 0, src, 4, 1, 1, 0, 0, 1, 1, blend: true);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, dst);
    }

    [Theory]
    [InlineData(3, 0, 2, 2)] // dst x + w > dst width (4)
    [InlineData(0, 2, 2, 2)] // dst y + h > dst height (3)
    [InlineData(-1, 0, 1, 1)]
    [InlineData(0, -1, 1, 1)]
    [InlineData(0, 0, -1, 1)]
    [InlineData(0, 0, 1, -1)]
    public void CompositeRectRejectsOutOfBoundsDestinationWithoutWriting(int dx, int dy, int w, int h)
    {
        var dst = Solid(4, 3, 1, 1, 1, 1);
        var before = (byte[])dst.Clone();
        var src = Solid(4, 4, 9, 9, 9, 9);
        Assert.False(PixelOps.CompositeRect(dst, 16, 4, 3, dx, dy, src, 16, 4, 4, 0, 0, w, h, blend: false));
        Assert.Equal(before, dst);
    }

    [Fact]
    public void CompositeRectRejectsOutOfBoundsSourceAndBadBuffers()
    {
        var dst = Solid(4, 4, 1, 1, 1, 1);
        var src = Solid(2, 2, 9, 9, 9, 9);
        Assert.False(PixelOps.CompositeRect(dst, 16, 4, 4, 0, 0, src, 8, 2, 2, 1, 0, 2, 1, blend: false)); // src x + w = 3 > 2
        Assert.False(PixelOps.CompositeRect(dst, 16, 4, 4, 0, 0, src, 8, 2, 2, 0, 1, 1, 2, blend: false)); // src y + h = 3 > 2
        Assert.False(PixelOps.CompositeRect(dst, 16, 4, 4, 0, 0, src, 8, 2, 2, -1, 0, 1, 1, blend: false));
        Assert.False(PixelOps.CompositeRect(dst, 12, 4, 4, 0, 0, src, 8, 2, 2, 0, 0, 1, 1, blend: false));  // dst stride < row
        Assert.False(PixelOps.CompositeRect(dst, 16, 4, 4, 0, 0, src, 4, 2, 2, 0, 0, 1, 1, blend: false));  // src stride < row
        Assert.False(PixelOps.CompositeRect(dst.AsSpan(0, 63), 16, 4, 4, 0, 0, src, 8, 2, 2, 0, 0, 1, 1, blend: false)); // dst too short
        Assert.False(PixelOps.CompositeRect(dst, 16, 4, 4, 0, 0, src.AsSpan(0, 15), 8, 2, 2, 0, 0, 1, 1, blend: false)); // src too short
        Assert.True(dst.All(b => b == 1));
    }

    [Fact]
    public void ZeroSizedRectangleSucceedsAsNoOp()
    {
        var dst = Solid(2, 2, 1, 1, 1, 1);
        var src = Solid(2, 2, 9, 9, 9, 9);
        Assert.True(PixelOps.CompositeRect(dst, 8, 2, 2, 2, 2, src, 8, 2, 2, 2, 2, 0, 0, blend: true));
        Assert.True(dst.All(b => b == 1));
    }

    [Fact]
    public void FullFrameCompositeUsesTheExactRectEdges()
    {
        // Rectangle that touches the last column/row of both buffers (off-by-one guard).
        var dst = Solid(3, 3, 0, 0, 0, 0);
        var src = Solid(3, 3, 5, 5, 5, 255);
        Assert.True(PixelOps.CompositeRect(dst, 12, 3, 3, 2, 2, src, 12, 3, 3, 2, 2, 1, 1, blend: true));
        for (int i = 0; i < 9; i++)
            Assert.Equal(i == 8 ? new byte[] { 5, 5, 5, 255 } : new byte[] { 0, 0, 0, 0 }, dst.AsSpan(i * 4, 4).ToArray());
    }
}

[Collection("Allocation-sensitive tests")]
public sealed class PixelOpsAllocationTests
{
    [Fact]
    public void PremultiplyAndCompositeDoNotAllocate()
    {
        var buf = new byte[4096];
        new Random(1).NextBytes(buf);
        var src = new byte[64 * 4];
        var dst = new byte[64 * 4];
        AllocationAssert.NoAllocations(() =>
        {
            PixelOps.PremultiplyInPlace(buf);
            PixelOps.CompositeRect(dst, 256, 64, 1, 0, 0, src, 256, 64, 1, 0, 0, 64, 1, blend: true);
        });
    }
}
