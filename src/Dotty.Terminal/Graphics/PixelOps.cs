using System;
using System.Runtime.Intrinsics;

namespace Dotty.Terminal.Graphics;

/// <summary>Pure pixel-buffer primitives: alpha premultiplication, box downscale and rectangle composition.</summary>
public static class PixelOps
{
    /// <summary>
    /// Exact division of a 16-bit product by 255 with round-to-nearest: <c>round(x / 255)</c> for
    /// <c>0 &lt;= x &lt;= 65025</c>, computed as <c>(y + (y &gt;&gt; 8)) &gt;&gt; 8</c> with <c>y = x + 128</c>
    /// (no division; identical to <c>(x + 127) / 255</c> over that range - x/255 is never a tie because 255 is odd).
    /// </summary>
    internal static int DivRound255(int x)
    {
        int y = x + 128;
        return (y + (y >> 8)) >> 8;
    }

    /// <summary>
    /// Converts straight-alpha RGBA8 to premultiplied in place: <c>c' = round(c * a / 255)</c>, alpha unchanged.
    /// a = 0 gives 0, a = 255 leaves colours untouched. The length must be a multiple of 4.
    /// </summary>
    public static void PremultiplyInPlace(Span<byte> rgba)
    {
        if ((rgba.Length & 3) != 0) throw new ArgumentException("Length must be a multiple of 4.", nameof(rgba));
        int i = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            // Per 4-pixel vector: broadcast each pixel's alpha over its four lanes, but use 255 for the alpha
            // lane itself so that a * 255 / 255 == a keeps alpha unchanged.
            var spread = Vector128.Create((byte)3, 3, 3, 3, 7, 7, 7, 7, 11, 11, 11, 11, 15, 15, 15, 15);
            var alphaLanes = Vector128.Create((byte)0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0, 255);
            var round = Vector128.Create((ushort)128);
            for (; i + 16 <= rgba.Length; i += 16)
            {
                var px = Vector128.Create<byte>(rgba.Slice(i, 16));
                var mul = Vector128.ConditionalSelect(alphaLanes, Vector128<byte>.AllBitsSet, Vector128.Shuffle(px, spread));
                var (pl, ph) = Vector128.Widen(px);
                var (ml, mh) = Vector128.Widen(mul);
                var yl = pl * ml + round;
                var yh = ph * mh + round;
                yl = (yl + Vector128.ShiftRightLogical(yl, 8));
                yh = (yh + Vector128.ShiftRightLogical(yh, 8));
                var res = Vector128.Narrow(Vector128.ShiftRightLogical(yl, 8), Vector128.ShiftRightLogical(yh, 8));
                res.CopyTo(rgba.Slice(i, 16));
            }
        }
        PremultiplyScalar(rgba.Slice(i));
    }

    internal static void PremultiplyScalar(Span<byte> rgba)
    {
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            int a = rgba[i + 3];
            if (a == 255) continue;
            if (a == 0)
            {
                rgba[i] = rgba[i + 1] = rgba[i + 2] = 0;
                continue;
            }
            rgba[i] = (byte)DivRound255(rgba[i] * a);
            rgba[i + 1] = (byte)DivRound255(rgba[i + 1] * a);
            rgba[i + 2] = (byte)DivRound255(rgba[i + 2] * a);
        }
    }

    /// <summary>
    /// Converts premultiplied RGBA8 back to straight alpha in place: <c>c = min(255, (c * 255 + a / 2) / a)</c>;
    /// pixels with a = 0 become fully zero. Lossy for small alpha (readback/diagnostics only).
    /// </summary>
    public static void UnpremultiplyInPlace(Span<byte> rgba)
    {
        if ((rgba.Length & 3) != 0) throw new ArgumentException("Length must be a multiple of 4.", nameof(rgba));
        for (int i = 0; i < rgba.Length; i += 4)
        {
            int a = rgba[i + 3];
            if (a == 255) continue;
            if (a == 0)
            {
                rgba[i] = rgba[i + 1] = rgba[i + 2] = 0;
                continue;
            }
            for (int k = 0; k < 3; k++)
                rgba[i + k] = (byte)Math.Min(255, (rgba[i + k] * 255 + a / 2) / a);
        }
    }

    /// <summary>
    /// Size a <paramref name="width"/> x <paramref name="height"/> image must be reduced to so that neither side exceeds
    /// <paramref name="maxTexture"/>. Images that already fit are returned unchanged; otherwise the longer side
    /// becomes exactly <paramref name="maxTexture"/> and the other keeps the aspect ratio (rounded to nearest, at least 1).
    /// </summary>
    public static (int Width, int Height) ComputeDownscaledSize(int width, int height, int maxTexture)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (maxTexture <= 0) throw new ArgumentOutOfRangeException(nameof(maxTexture));
        if (width <= maxTexture && height <= maxTexture) return (width, height);
        long longest = Math.Max(width, height);
        int Scale(int side) => side == longest ? maxTexture
            : (int)Math.Clamp(((long)side * maxTexture + longest / 2) / longest, 1, maxTexture);
        return (Scale(width), Scale(height));
    }

    /// <summary>
    /// Box-filter downscale into a new buffer of the same format. Each destination pixel averages the source pixels
    /// covering <c>[x*sw/dw, ceil((x+1)*sw/dw))</c> (integer partition, whole pixels, rounded to nearest); averaging
    /// premultiplied data is correct. Throws if the target is larger than the source in either dimension.
    /// The caller owns the returned reference.
    /// </summary>
    public static ImagePixels BoxDownscale(ImagePixels src, int newWidth, int newHeight)
    {
        ArgumentNullException.ThrowIfNull(src);
        if (newWidth <= 0 || newWidth > src.Width) throw new ArgumentOutOfRangeException(nameof(newWidth));
        if (newHeight <= 0 || newHeight > src.Height) throw new ArgumentOutOfRangeException(nameof(newHeight));

        int bpp = (int)src.Format;
        var dst = ImagePixels.Allocate(newWidth, newHeight, src.Format);
        var s = src.AsSpan();
        var d = dst.AsWritableSpan();
        long sw = src.Width, sh = src.Height;
        Span<long> sums = stackalloc long[4];
        for (int y = 0; y < newHeight; y++)
        {
            int y0 = (int)(y * sh / newHeight);
            int y1 = (int)Math.Max(y0 + 1, ((y + 1) * sh + newHeight - 1) / newHeight);
            for (int x = 0; x < newWidth; x++)
            {
                int x0 = (int)(x * sw / newWidth);
                int x1 = (int)Math.Max(x0 + 1, ((x + 1) * sw + newWidth - 1) / newWidth);
                sums.Clear();
                for (int sy = y0; sy < y1; sy++)
                {
                    int row = sy * src.Stride;
                    for (int sx = x0; sx < x1; sx++)
                    {
                        int o = row + sx * bpp;
                        for (int c = 0; c < bpp; c++) sums[c] += s[o + c];
                    }
                }
                long n = (long)(y1 - y0) * (x1 - x0);
                int outOff = y * dst.Stride + x * bpp;
                for (int c = 0; c < bpp; c++) d[outOff + c] = (byte)((sums[c] + n / 2) / n);
            }
        }
        return dst;
    }

    /// <summary>
    /// Copies (<paramref name="blend"/> false) or composites (true) a rectangle of premultiplied RGBA8 pixels from
    /// <paramref name="src"/> onto <paramref name="dst"/>. Blend is Porter-Duff source-over for premultiplied data:
    /// <c>out = src + round(dst * (255 - srcA) / 255)</c> per channel (alpha included), saturated at 255.
    /// Returns false, touching nothing, when either rectangle is not fully inside its buffer, a size is negative, or a
    /// buffer is too small for its declared stride/height. Zero-sized rectangles succeed. <paramref name="src"/> and
    /// <paramref name="dst"/> must not overlap.
    /// </summary>
    public static bool CompositeRect(
        Span<byte> dst, int dstStride, int dstWidth, int dstHeight, int dstX, int dstY,
        ReadOnlySpan<byte> src, int srcStride, int srcWidth, int srcHeight, int srcX, int srcY,
        int width, int height, bool blend)
    {
        if (width < 0 || height < 0 || dstX < 0 || dstY < 0 || srcX < 0 || srcY < 0) return false;
        if (dstWidth < 0 || dstHeight < 0 || srcWidth < 0 || srcHeight < 0) return false;
        if ((long)dstX + width > dstWidth || (long)dstY + height > dstHeight) return false;
        if ((long)srcX + width > srcWidth || (long)srcY + height > srcHeight) return false;
        if (dstStride < (long)dstWidth * 4 || srcStride < (long)srcWidth * 4) return false;
        if (dstHeight > 0 && (long)dstStride * (dstHeight - 1) + (long)dstWidth * 4 > dst.Length) return false;
        if (srcHeight > 0 && (long)srcStride * (srcHeight - 1) + (long)srcWidth * 4 > src.Length) return false;
        if (width == 0 || height == 0) return true;

        for (int row = 0; row < height; row++)
        {
            var s = src.Slice((srcY + row) * srcStride + srcX * 4, width * 4);
            var d = dst.Slice((dstY + row) * dstStride + dstX * 4, width * 4);
            if (!blend)
            {
                s.CopyTo(d);
                continue;
            }
            for (int i = 0; i < s.Length; i += 4)
            {
                int inv = 255 - s[i + 3];
                if (inv == 0)
                {
                    s.Slice(i, 4).CopyTo(d.Slice(i, 4));
                    continue;
                }
                if (inv == 255) continue;
                for (int c = 0; c < 4; c++)
                    d[i + c] = (byte)Math.Min(255, s[i + c] + DivRound255(d[i + c] * inv));
            }
        }
        return true;
    }
}
