using System.Runtime.InteropServices;

namespace Dotty.Rendering.Gpu;

/// <summary>
/// Per-instance data for one terminal cell in the GL glyph renderer.
/// One instance per visible non-empty cell; continuations are skipped.
/// Packed to 20 bytes for efficient GPU upload via instanced arrays.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct CellInstance
{
    /// <summary>Column index (0-based) in the visible grid.</summary>
    public ushort Col;

    /// <summary>Row index (0-based) in the visible grid.</summary>
    public ushort Row;

    /// <summary>Glyph top-left X in atlas pixels.</summary>
    public short GlyphX;

    /// <summary>Glyph top-left Y in atlas pixels.</summary>
    public short GlyphY;

    /// <summary>Glyph width in atlas pixels.</summary>
    public short GlyphW;

    /// <summary>Glyph height in atlas pixels.</summary>
    public short GlyphH;

    /// <summary>Glyph horizontal placement offset (left bearing, px).</summary>
    public short OffX;

    /// <summary>Glyph top offset from the cell top (baseline + top bearing, px).</summary>
    public short OffY;

    /// <summary>Foreground red channel (0–255).</summary>
    public byte FgR;

    /// <summary>Foreground green channel (0–255).</summary>
    public byte FgG;

    /// <summary>Foreground blue channel (0–255).</summary>
    public byte FgB;

    /// <summary>Flags: bit 0 = bold, bit 1 = wide cell, bit 2 = inverse video.</summary>
    public byte Flags;

    /// <summary>Background red channel (0–255).</summary>
    public byte BgR;

    /// <summary>Background green channel (0–255).</summary>
    public byte BgG;

    /// <summary>Background blue channel (0–255).</summary>
    public byte BgB;

    /// <summary>Background alpha (255 for opaque, 0 for default/transparent).</summary>
    public byte BgA;
}

/// <summary>Flag bits for <see cref="CellInstance.Flags"/>.</summary>
public static class CellFlags
{
    public const byte Bold = 0x01;
    public const byte WideCell = 0x02;
    public const byte InverseVideo = 0x04;
    public const byte Underline = 0x08;
    public const byte Strikethrough = 0x10;
    public const byte Overline = 0x20;
    /// <summary>Instance draws only the decoration bars (no glyph).</summary>
    public const byte DecorOnly = 0x80;
}

/// <summary>
/// Shared per-vertex corner offsets for the unit quad used by all instances.
/// Two triangles: [0,1,2] and [0,2,3]. Uploaded once to a VBO with divisor=0.
/// </summary>
public static class QuadVertices
{
    /// <summary>6 vertices × 2 floats = 12 floats. Corner position (0 or 1) + UV selector.</summary>
    public static readonly float[] Vertices =
    {
        // pos_x  pos_y  uv_x  uv_y
        0f, 0f,   0f, 0f,
        1f, 0f,   1f, 0f,
        1f, 1f,   1f, 1f,
        0f, 0f,   0f, 0f,
        1f, 1f,   1f, 1f,
        0f, 1f,   0f, 1f,
    };

    /// <summary>Index buffer: [0,1,2, 0,2,3] pattern repeated per quad. Static.</summary>
    public static readonly ushort[] Indices = { 0, 1, 2, 0, 2, 3 };
}
