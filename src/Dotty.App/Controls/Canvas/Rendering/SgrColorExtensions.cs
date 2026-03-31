using Dotty.Terminal.Adapter;
using SkiaSharp;

namespace Dotty.App.Controls.Canvas.Rendering;

/// <summary>
/// Extension methods for SgrColorArgb to convert to SkiaSharp colors.
/// </summary>
public static class SgrColorExtensions
{
    /// <summary>
    /// Converts an SgrColorArgb to a SkiaSharp SKColor.
    /// </summary>
    public static SKColor ToSKColor(this SgrColorArgb color)
    {
        // ARGB uint to SKColor (R, G, B, A)
        return new SKColor(
            (byte)(color.Argb >> 16),  // R
            (byte)(color.Argb >> 8),   // G
            (byte)color.Argb,          // B
            (byte)(color.Argb >> 24)   // A
        );
    }
}
