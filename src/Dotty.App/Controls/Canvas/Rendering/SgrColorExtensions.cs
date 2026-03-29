using Dotty.Terminal.Adapter;
using SkiaSharp;

namespace Dotty.App.Controls.Canvas.Rendering;

/// <summary>
/// Extension methods for SgrColor to convert to SkiaSharp colors.
/// Eliminates duplication between TerminalFrameComposer and other color conversion logic.
/// </summary>
public static class SgrColorExtensions
{
    /// <summary>
    /// Converts an SgrColor to a SkiaSharp SKColor.
    /// </summary>
    public static SKColor ToSKColor(this SgrColor color)
    {
        return SKColor.Parse(color.Hex);
    }
}
