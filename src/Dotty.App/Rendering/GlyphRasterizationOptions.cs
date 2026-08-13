using System;

namespace Dotty.App.Rendering;

/// <summary>
/// Rasterization flags that change glyph rendering. Value-equatable so the
/// atlas service can key atlases by these flags: two instances with identical
/// flags must produce the same atlas identity.
/// </summary>
public sealed class GlyphRasterizationOptions : IEquatable<GlyphRasterizationOptions>
{
    public bool IsAntialias { get; init; } = false;
    public bool IsLinearText { get; init; } = true;
    public bool SubpixelText { get; init; } = true;
    public bool IsAutohinted { get; init; } = true;
    public bool LcdRenderText { get; init; } = true;

    public bool Equals(GlyphRasterizationOptions? other)
    {
        if (other is null) return false;
        return IsAntialias == other.IsAntialias &&
               IsLinearText == other.IsLinearText &&
               SubpixelText == other.SubpixelText &&
               IsAutohinted == other.IsAutohinted &&
               LcdRenderText == other.LcdRenderText;
    }

    public override bool Equals(object? obj) => Equals(obj as GlyphRasterizationOptions);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = 17;
            hash = hash * 31 + IsAntialias.GetHashCode();
            hash = hash * 31 + IsLinearText.GetHashCode();
            hash = hash * 31 + SubpixelText.GetHashCode();
            hash = hash * 31 + IsAutohinted.GetHashCode();
            hash = hash * 31 + LcdRenderText.GetHashCode();
            return hash;
        }
    }
}
