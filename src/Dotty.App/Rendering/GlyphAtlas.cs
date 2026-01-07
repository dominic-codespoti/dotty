using System;
using System.Collections.Generic;
using SkiaSharp;

namespace Dotty.App.Rendering;

/// <summary>
/// Simple glyph atlas: maps a glyph key to a rectangle in an SKBitmap atlas.
/// Responsibilities:
/// - Accept a glyph key (string + style)
/// - Return glyph metadata (bounds in atlas)
/// - Lazily rasterise missing glyphs
/// Public surface:
/// - EnsureGlyph(GlyphKey key)
/// - TryGetGlyph(GlyphKey key, out GlyphInfo info)
/// - SKBitmap AtlasBitmap
/// This class intentionally has no references to TerminalBuffer, Avalonia or rows/cols.
/// It depends only on SkiaSharp so it can be tested without Avalonia.
/// </summary>
public class GlyphAtlas : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<GlyphKey, GlyphInfo> _map = new(new GlyphKeyComparer());
    private SKBitmap _bitmap;
    private SKCanvas _canvas;

    private int _nextX = 0;
    private int _nextY = 0;
    private int _rowHeight = 0;
    private readonly int _padding = 2;

    // The paint used to rasterise glyphs. Caller should pass a Typeface and size.
    private SKTypeface _typeface;
    private float _textSize;

    public SKBitmap AtlasBitmap => _bitmap;

    public GlyphAtlas(SKTypeface typeface, float textSize, int initialSize = 1024)
    {
        _typeface = typeface ?? SKTypeface.Default;
        _textSize = textSize <= 0 ? 12f : textSize;
        _bitmap = new SKBitmap(initialSize, initialSize, SKColorType.Rgba8888, SKAlphaType.Premul);
        _canvas = new SKCanvas(_bitmap);
        _canvas.Clear(SKColors.Transparent);
    }

    public void EnsureGlyph(GlyphKey key)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        lock (_lock)
        {
            if (_map.ContainsKey(key)) return;

            // Rasterise the glyph into a temporary surface to measure it
            using var paint = new SKPaint
            {
                Typeface = _typeface,
                TextSize = _textSize,
                IsAntialias = true,
                Color = SKColors.White,
            };

            var text = key.Grapheme ?? string.Empty;
            var bounds = new SKRect();
            paint.MeasureText(text, ref bounds);
            int w = Math.Max(1, (int)Math.Ceiling(bounds.Width)) + _padding * 2;
            int h = Math.Max(1, (int)Math.Ceiling(bounds.Height)) + _padding * 2;

            // If not enough space in current row, move to next row
            if (_nextX + w > _bitmap.Width)
            {
                _nextX = 0;
                _nextY += _rowHeight + _padding;
                _rowHeight = 0;
            }

            // If not enough space vertically, grow the bitmap
            if (_nextY + h > _bitmap.Height)
            {
                GrowBitmap(Math.Max(_bitmap.Width * 2, _nextX + w), Math.Max(_bitmap.Height * 2, _nextY + h));
            }

            // Rasterise text at (_nextX + padding, baseline)
            var destX = _nextX + _padding;
            // compute baseline assuming ascent is negative
            var fm = paint.FontMetrics;
            var baseline = _nextY + _padding - fm.Ascent;

            _canvas.DrawText(text, destX, baseline, paint);

            var info = new GlyphInfo
            {
                X = _nextX,
                Y = _nextY,
                Width = w,
                Height = h,
                Advance = (float)Math.Ceiling(bounds.Width)
            };

            // compute baseline offset relative to top of glyph image
            info.BaselineOffset = _padding - fm.Ascent;

            _map[key.Clone()] = info;

            _nextX += w + _padding;
            _rowHeight = Math.Max(_rowHeight, h);
        }
    }

    public bool TryGetGlyph(GlyphKey key, out GlyphInfo info)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        lock (_lock)
        {
            return _map.TryGetValue(key, out info);
        }
    }

    private void GrowBitmap(int minWidth, int minHeight)
    {
        var newW = Math.Max(_bitmap.Width * 2, minWidth);
        var newH = Math.Max(_bitmap.Height * 2, minHeight);

        var newBmp = new SKBitmap(newW, newH, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var newCanvas = new SKCanvas(newBmp);
        newCanvas.Clear(SKColors.Transparent);
        newCanvas.DrawBitmap(_bitmap, 0, 0);

        _canvas.Dispose();
        _bitmap.Dispose();
        _bitmap = newBmp;
        _canvas = new SKCanvas(_bitmap);
    }

    public void Dispose()
    {
        _canvas?.Dispose();
        _bitmap?.Dispose();
    }
}

public sealed class GlyphKey
{
    public string Grapheme { get; }
    public string? ForegroundHex { get; }
    public bool Bold { get; }

    public GlyphKey(string grapheme, string? foregroundHex = null, bool bold = false)
    {
        Grapheme = grapheme ?? string.Empty;
        ForegroundHex = foregroundHex;
        Bold = bold;
    }

    public GlyphKey Clone() => new(Grapheme, ForegroundHex, Bold);
}

public sealed class GlyphInfo
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public float Advance { get; set; }
    // Distance from the top of the glyph image to the baseline (in pixels)
    public float BaselineOffset { get; set; }
}

internal sealed class GlyphKeyComparer : IEqualityComparer<GlyphKey>
{
    public bool Equals(GlyphKey? x, GlyphKey? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null) return false;
        return x.Grapheme == y.Grapheme && x.ForegroundHex == y.ForegroundHex && x.Bold == y.Bold;
    }

    public int GetHashCode(GlyphKey obj)
    {
        unchecked
        {
            var hash = 17;
            hash = hash * 23 + (obj.Grapheme?.GetHashCode() ?? 0);
            hash = hash * 23 + (obj.ForegroundHex?.GetHashCode() ?? 0);
            hash = hash * 23 + (obj.Bold ? 1 : 0);
            return hash;
        }
    }
}
