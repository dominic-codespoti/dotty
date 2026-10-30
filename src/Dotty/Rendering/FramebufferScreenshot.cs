using System;
using SkiaSharp;

namespace Dotty.Silk.Rendering;

/// <summary>
/// Turns a <c>glReadPixels</c> RGBA readback (bottom row first, premultiplied colour as the renderer blends with
/// <c>ONE, ONE_MINUS_SRC_ALPHA</c>) into PNG bytes: rows are flipped to top-down and Skia's PNG encoder
/// un-premultiplies. An opaque window's framebuffer alpha is not what the compositor shows, so
/// <paramref name="opaque"/> forces alpha to 255 instead of trusting it. Thread-agnostic (no GL calls).
/// </summary>
internal static class FramebufferScreenshot
{
    internal static unsafe byte[] EncodePng(ReadOnlySpan<byte> bottomUpRgba, int width, int height, bool opaque)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        int stride = checked(width * 4);
        int length = checked(stride * height);
        if (bottomUpRgba.Length < length)
            throw new ArgumentException($"Expected {length} bytes of RGBA pixels, got {bottomUpRgba.Length}.", nameof(bottomUpRgba));

        byte[] topDown = new byte[length];
        for (int row = 0; row < height; row++)
            bottomUpRgba.Slice((height - 1 - row) * stride, stride).CopyTo(topDown.AsSpan(row * stride, stride));
        if (opaque)
        {
            for (int i = 3; i < length; i += 4)
                topDown[i] = 255;
        }

        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, opaque ? SKAlphaType.Opaque : SKAlphaType.Premul);
        fixed (byte* pixels = topDown)
        {
            using var pixmap = new SKPixmap(info, (IntPtr)pixels, stride);
            using SKData? png = pixmap.Encode(SKEncodedImageFormat.Png, 100);
            if (png == null)
                throw new InvalidOperationException("Skia could not encode the screenshot as PNG.");
            return png.ToArray();
        }
    }
}
