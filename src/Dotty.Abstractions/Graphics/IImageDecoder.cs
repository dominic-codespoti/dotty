using System;

namespace Dotty.Abstractions.Graphics;

/// <summary>Outcome of an <see cref="IImageDecoder"/> call.</summary>
public enum ImageDecodeStatus
{
    /// <summary>The destination holds the decoded image.</summary>
    Ok,
    /// <summary>The data is corrupt, truncated, or does not match the declared dimensions.</summary>
    Invalid,
    /// <summary>The data is well formed but cannot be decoded by this decoder.</summary>
    Unsupported,
}

/// <summary>
/// Decodes compressed images into pixels. Implementations must be thread-safe and must not throw for bad input.
/// </summary>
public interface IImageDecoder
{
    /// <summary>
    /// Decodes a PNG whose dimensions were already read from its header. <paramref name="straightRgba"/> must hold at
    /// least <c>width * height * 4</c> bytes and receives straight-alpha (non-premultiplied) RGBA8, tightly packed.
    /// The decoder returns <see cref="ImageDecodeStatus.Invalid"/> when the stream's own dimensions differ from the
    /// ones passed.
    /// </summary>
    ImageDecodeStatus DecodePngRgba(ReadOnlySpan<byte> png, int width, int height, Span<byte> straightRgba);
}
