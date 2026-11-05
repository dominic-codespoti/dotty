using System;

namespace Dotty.Rendering.Gpu;

/// <summary>
/// Face style of an atlas glyph. Until real bold/italic faces are selected (W4-C6) both are synthetic:
/// bold strokes the outline, italic skews it (<see cref="GlyphAtlas.SyntheticItalicSkewX"/>).
/// </summary>
[Flags]
public enum GlyphStyle : byte
{
    Regular = 0,
    Bold = 1,
    Italic = 2,
    BoldItalic = Bold | Italic,
}

public static class GlyphStyles
{
    /// <summary>The glyph style for SGR 1 (bold) and SGR 3 (italic).</summary>
    public static GlyphStyle From(bool bold, bool italic) =>
        (bold ? GlyphStyle.Bold : GlyphStyle.Regular) | (italic ? GlyphStyle.Italic : GlyphStyle.Regular);
}
