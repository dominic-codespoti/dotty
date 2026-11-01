using System;
using Dotty.Abstractions.Graphics;
using SkiaSharp;

namespace Dotty.Rendering.Gpu;

/// <summary>
/// <see cref="IImageDecoder"/> backed by SkiaSharp's PNG codec. Each call creates its own codec, so the decoder is
/// stateless and thread-safe. Needs the native Skia library; never throws for bad input.
/// </summary>
public sealed class SkiaImageDecoder : IImageDecoder
{
    public unsafe ImageDecodeStatus DecodePngRgba(ReadOnlySpan<byte> png, int width, int height, Span<byte> straightRgba)
    {
        if (width <= 0 || height <= 0 || png.IsEmpty) return ImageDecodeStatus.Invalid;
        long pixels = (long)width * height; // < 2^62, cannot overflow
        if (pixels > int.MaxValue / 4 || straightRgba.Length < pixels * 4) return ImageDecodeStatus.Invalid;

        try
        {
            using var data = SKData.CreateCopy(png);
            // The codec takes ownership of the stream (SkiaSharp revokes the managed owner), so the stream is not disposed here.
            using var codec = SKCodec.Create(new SKMemoryStream(data), out SKCodecResult created);
            if (codec == null) return Map(created);
            if (codec.EncodedFormat != SKEncodedImageFormat.Png) return ImageDecodeStatus.Invalid;
            if (codec.Info.Width != width || codec.Info.Height != height) return ImageDecodeStatus.Invalid;

            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            fixed (byte* p = straightRgba)
            {
                var result = codec.GetPixels(info, (IntPtr)p);
                return result == SKCodecResult.Success ? ImageDecodeStatus.Ok : Map(result);
            }
        }
        catch (Exception)
        {
            return ImageDecodeStatus.Invalid;
        }
    }

    private static ImageDecodeStatus Map(SKCodecResult result) => result switch
    {
        SKCodecResult.Success => ImageDecodeStatus.Ok,
        SKCodecResult.Unimplemented or SKCodecResult.InvalidConversion => ImageDecodeStatus.Unsupported,
        _ => ImageDecodeStatus.Invalid,
    };
}
