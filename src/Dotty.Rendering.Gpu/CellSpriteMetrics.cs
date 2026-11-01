using System;

namespace Dotty.Rendering.Gpu;

/// <summary>
/// Whole-device-pixel cell geometry that decoration and procedural sprites are rasterised against (the GPU-project
/// copy of the host's <c>CellMetrics</c>). Vertical positions are measured down from the cell top; every stroke lies
/// inside the cell. <c>default</c> (zero cell size) means "not measured": the frame builder then emits no sprites.
/// </summary>
public readonly record struct CellSpriteMetrics(
    int CellWidth,
    int CellHeight,
    int Baseline,
    int UnderlinePosition,
    int UnderlineThickness,
    int StrikethroughPosition,
    int StrikethroughThickness,
    int OverlinePosition,
    int OverlineThickness)
{
    /// <summary>Largest cell edge a sprite is built for; matches the atlas' largest glyph.</summary>
    public const int MaxCellDimension = 256;

    /// <summary>True when the metrics describe a real cell (both edges 1..<see cref="MaxCellDimension"/>).</summary>
    public bool IsValid =>
        CellWidth is > 0 and <= MaxCellDimension && CellHeight is > 0 and <= MaxCellDimension;

    /// <summary>
    /// Builds metrics from raw values, clamping every stroke to at least one pixel and inside the cell, so the
    /// sprite rasterisers never see an out-of-cell rectangle.
    /// </summary>
    public static CellSpriteMetrics Create(
        int cellWidth, int cellHeight, int baseline,
        int underlinePosition, int underlineThickness,
        int strikethroughPosition, int strikethroughThickness,
        int overlinePosition, int overlineThickness)
    {
        cellWidth = Math.Clamp(cellWidth, 1, MaxCellDimension);
        cellHeight = Math.Clamp(cellHeight, 1, MaxCellDimension);
        baseline = Math.Clamp(baseline, 0, cellHeight);
        underlineThickness = Math.Clamp(underlineThickness, 1, cellHeight);
        strikethroughThickness = Math.Clamp(strikethroughThickness, 1, cellHeight);
        overlineThickness = Math.Clamp(overlineThickness, 1, cellHeight);
        return new CellSpriteMetrics(
            cellWidth, cellHeight, baseline,
            Math.Clamp(underlinePosition, 0, cellHeight - underlineThickness), underlineThickness,
            Math.Clamp(strikethroughPosition, 0, cellHeight - strikethroughThickness), strikethroughThickness,
            Math.Clamp(overlinePosition, 0, cellHeight - overlineThickness), overlineThickness);
    }
}

/// <summary>
/// One A8 coverage sprite (row-major, <see cref="Width"/> bytes per row) placed <see cref="Left"/>/<see cref="Top"/>
/// pixels from the cell's top-left corner.
/// </summary>
public sealed class SpriteBitmap
{
    public SpriteBitmap(int width, int height, int left = 0, int top = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        Width = width;
        Height = height;
        Left = left;
        Top = top;
        Pixels = new byte[checked(width * height)];
    }

    public int Width { get; }
    public int Height { get; }
    public int Left { get; }
    public int Top { get; }
    public byte[] Pixels { get; }

    public byte this[int x, int y]
    {
        get => Pixels[y * Width + x];
        set => Pixels[y * Width + x] = value;
    }

    /// <summary>Fills the rectangle (clipped to the bitmap) with full coverage.</summary>
    public void FillRect(int x, int y, int width, int height, byte value = 255)
    {
        int x0 = Math.Max(0, x), y0 = Math.Max(0, y);
        int x1 = Math.Min(Width, x + width), y1 = Math.Min(Height, y + height);
        for (int row = y0; row < y1; row++)
            Pixels.AsSpan(row * Width + x0, Math.Max(0, x1 - x0)).Fill(value);
    }

    /// <summary>Raises the pixel's coverage to at least <paramref name="value"/> (union of shapes).</summary>
    public void Max(int x, int y, byte value)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            return;
        ref byte pixel = ref Pixels[y * Width + x];
        if (value > pixel)
            pixel = value;
    }
}

/// <summary>
/// Atlas key of a sprite. <see cref="Code"/> is the code point of a procedural glyph or a negative
/// <see cref="DecorationGeometry"/> decoration code; <see cref="Width"/> is the sprite's cell span in pixels.
/// The metrics are part of the key, so a size or font change creates new entries instead of reusing stale ones.
/// </summary>
public readonly record struct SpriteKey(int Code, int Width, CellSpriteMetrics Metrics);
