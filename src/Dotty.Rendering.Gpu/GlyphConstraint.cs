using System;

namespace Dotty.Rendering.Gpu;

/// <summary>How <see cref="ConstraintSolver"/> scales a glyph's ink into its box.</summary>
public enum GlyphSizeMode : byte
{
    /// <summary>Keep the rasterised size.</summary>
    None,
    /// <summary>Shrink uniformly only when the ink overflows the box; never enlarge.</summary>
    Fit,
    /// <summary>Scale uniformly up or down to the largest size that still fits the box.</summary>
    Cover,
    /// <summary>Scale each axis independently so the ink fills the box exactly.</summary>
    Stretch,
}

/// <summary>Placement of the (scaled) ink inside its box on one axis.</summary>
public enum GlyphAlign : byte
{
    /// <summary>Keep the font's own position (pen origin, baseline), moved into the box only when it overflows.</summary>
    None,
    Start,
    Center,
    End,
}

/// <summary>
/// Sizing rule for one glyph class. The box is <c>min(span, MaxSpan)</c> cells wide and one cell high, inset by
/// <see cref="Padding"/> (a fraction of the box on each side, clamped to 0..0.45).
/// </summary>
public readonly record struct GlyphConstraint(
    GlyphSizeMode Size,
    GlyphAlign AlignX,
    GlyphAlign AlignY,
    float Padding = 0f,
    byte MaxSpan = 1)
{
    /// <summary>No constraint: the solver returns the identity placement.</summary>
    public static GlyphConstraint None => default;

    /// <summary>Nerd Font / private-use icons: shrink to fit and centre; may use a blank following cell.</summary>
    public static GlyphConstraint Icon => new(GlyphSizeMode.Fit, GlyphAlign.Center, GlyphAlign.Center, 0f, 2);

    /// <summary>Powerline-extra separators not drawn by the sprite rasteriser: fill the cell edge to edge.</summary>
    public static GlyphConstraint CellFill => new(GlyphSizeMode.Stretch, GlyphAlign.Start, GlyphAlign.Start, 0f, 1);

    /// <summary>Text from a fallback family: keep its natural size and position, clamp only when it overflows the cell span.</summary>
    public static GlyphConstraint TextFallback => new(GlyphSizeMode.Fit, GlyphAlign.None, GlyphAlign.None, 0f, 2);

    public bool IsNone => Size == GlyphSizeMode.None && AlignX == GlyphAlign.None && AlignY == GlyphAlign.None;
}

/// <summary>
/// Per-codepoint constraint table. Box drawing, blocks and the core Powerline separators (U+E0B0..U+E0BF) are drawn by
/// <see cref="SpriteRasterizer"/> before the font path is consulted, so their entries here only matter with
/// <c>font.builtinGlyphs</c> off.
/// <para>
/// Ranges follow the Nerd Fonts v3 glyph sets as listed on the Nerd Fonts cheat sheet / font-patcher source
/// (https://github.com/ryanoasis/nerd-fonts/wiki/Glyph-Sets-and-Code-Points). They were transcribed from memory of
/// v3.x and not machine-verified: range ends may be off by a few code points between Nerd Fonts releases. Because every
/// remaining Private Use Area code point (BMP U+E000..U+F8FF, planes 15 and 16) also maps to <see cref="GlyphConstraint.Icon"/>,
/// an inexact end only matters where it borders the Powerline-extra separator block.
/// </para>
/// </summary>
public static class GlyphConstraints
{
    private readonly record struct Range(int First, int Last, GlyphConstraint Constraint);

    // Sorted, non-overlapping. Named sets are listed for documentation; all icon sets share one rule.
    private static readonly Range[] Ranges =
    [
        new(0xE000, 0xE00A, GlyphConstraint.Icon),     // Pomicons
        new(0xE0A0, 0xE0A3, GlyphConstraint.Icon),     // Powerline branch/LN/lock + extra column-number icon
        new(0xE0B0, 0xE0C8, GlyphConstraint.CellFill), // Powerline + Powerline Extra separators (flames, waveforms, ...)
        new(0xE0CA, 0xE0CA, GlyphConstraint.CellFill), // Powerline Extra
        new(0xE0CC, 0xE0D7, GlyphConstraint.CellFill), // Powerline Extra (pixelated, lego, trapezoid separators)
        new(0xE200, 0xE2A9, GlyphConstraint.Icon),     // Font Awesome Extension
        new(0xE300, 0xE3E3, GlyphConstraint.Icon),     // Weather Icons
        new(0xE5FA, 0xE6B7, GlyphConstraint.Icon),     // Seti-UI + Custom
        new(0xE700, 0xE8EF, GlyphConstraint.Icon),     // Devicons
        new(0xEA60, 0xEC1E, GlyphConstraint.Icon),     // Codicons
        new(0xED00, 0xF2FF, GlyphConstraint.Icon),     // Font Awesome (v3 location)
        new(0xF300, 0xF381, GlyphConstraint.Icon),     // Font Logos (formerly Font Linux)
        new(0xF400, 0xF533, GlyphConstraint.Icon),     // Octicons
        new(0xF0001, 0xF1AF0, GlyphConstraint.Icon),   // Material Design Icons (plane 15)
    ];

    /// <summary>True for a Private Use Area code point (BMP, plane 15 or plane 16).</summary>
    public static bool IsPrivateUse(int codepoint) =>
        codepoint is >= 0xE000 and <= 0xF8FF or >= 0xF0000 and <= 0x10FFFD;

    /// <summary>Constraint for one code point; <see cref="GlyphConstraint.None"/> outside the table and the PUA.</summary>
    public static GlyphConstraint ForCodepoint(int codepoint)
    {
        int lo = 0, hi = Ranges.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            ref readonly var range = ref Ranges[mid];
            if (codepoint < range.First) hi = mid - 1;
            else if (codepoint > range.Last) lo = mid + 1;
            else return range.Constraint;
        }
        return IsPrivateUse(codepoint) ? GlyphConstraint.Icon : GlyphConstraint.None;
    }

    /// <summary>
    /// Constraint for a grapheme: only a single scalar (optionally followed by a variation selector) is looked up;
    /// any other cluster has no table entry.
    /// </summary>
    public static GlyphConstraint ForGrapheme(string grapheme) =>
        TryGetSingleScalar(grapheme, out int codepoint) ? ForCodepoint(codepoint) : GlyphConstraint.None;

    /// <summary>True when a narrow glyph of this code point may spread into a blank following cell.</summary>
    public static bool IsSpanEligible(int codepoint) => ForCodepoint(codepoint).MaxSpan >= 2 && IsPrivateUse(codepoint);

    private static bool TryGetSingleScalar(string grapheme, out int codepoint)
    {
        codepoint = 0;
        if (string.IsNullOrEmpty(grapheme))
            return false;
        int length;
        if (char.IsHighSurrogate(grapheme[0]) && grapheme.Length > 1 && char.IsLowSurrogate(grapheme[1]))
        {
            codepoint = char.ConvertToUtf32(grapheme[0], grapheme[1]);
            length = 2;
        }
        else
        {
            codepoint = grapheme[0];
            length = 1;
        }
        if (grapheme.Length == length)
            return true;
        return grapheme.Length == length + 1 && grapheme[length] is '\uFE0E' or '\uFE0F';
    }
}

/// <summary>
/// Decides how many cells a glyph may occupy (<see cref="GlyphKey.CellSpan"/>): a wide cell is two; a narrow
/// single-scalar Private Use icon is two only when the following cell on the row exists and is blank; everything else is one.
/// </summary>
public static class GlyphCellSpan
{
    public static byte Decide(int codepoint, int cellWidth, bool singleScalar, bool nextCellBlank)
    {
        if (cellWidth >= 2)
            return 2;
        return singleScalar && nextCellBlank && GlyphConstraints.IsSpanEligible(codepoint) ? (byte)2 : (byte)1;
    }

    /// <summary>A following cell is blank when it is inside the row, not a continuation, and holds nothing or a plain space.</summary>
    public static bool IsBlank(ReadOnlySpan<Dotty.Terminal.Adapter.CellHot> row, int column, int rowLength)
    {
        if ((uint)column >= (uint)Math.Min(rowLength, row.Length))
            return false;
        ref readonly var cell = ref row[column];
        return !cell.IsContinuation && !cell.HasGrapheme && (cell.Rune == 0 || cell.Rune == ' ');
    }
}
