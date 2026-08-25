using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SkiaSharp;

namespace Dotty.App.Rendering;

/// <summary>
/// Placement metadata for one atlas entry, in atlas pixels (1 px = 1 unit).
/// The quad renderer (Phase 2) draws the glyph at:
///   destX = cellX + <see cref="LeftBearing"/>
///   destY = baselineY + <see cref="TopBearing"/>
/// where baselineY = cellTop + <see cref="BaselineOffset"/>, and advances the
/// pen by <see cref="Advance"/> (a width-2 cell places the next cell 2 cells
/// past the pen).
/// </summary>
public readonly struct GlyphInfo
{
    public readonly int X;
    public readonly int Y;
    public readonly int Width;
    public readonly int Height;
    public readonly float Advance;
    public readonly float BaselineOffset;
    public readonly float LeftBearing;
    public readonly float TopBearing;

    public GlyphInfo(
        int x, int y, int width, int height,
        float advance, float baselineOffset, float leftBearing, float topBearing)
    {
        X = x; Y = y; Width = width; Height = height;
        Advance = advance; BaselineOffset = baselineOffset;
        LeftBearing = leftBearing; TopBearing = topBearing;
    }
}

/// <summary>
/// Atlas lookup key. Deliberately has NO foreground color: the atlas stores
/// coverage only, so one entry serves every fg color (the deleted predecessor
/// baked RGB into the atlas and keyed on color, which both multiplied entries
/// and made lookups miss). Typeface identity is by instance — canvases share
/// resolved typefaces through a static cache, so same-family instances compare
/// equal in practice.
/// </summary>
public readonly struct GlyphKey : IEquatable<GlyphKey>
{
    public readonly string Grapheme;
    public readonly SKTypeface Typeface;
    public readonly float TextSize;
    public readonly bool Bold;

    public GlyphKey(string grapheme, SKTypeface typeface, float textSize, bool bold)
    {
        Grapheme = grapheme ?? string.Empty;
        Typeface = typeface ?? SKTypeface.Default;
        TextSize = textSize;
        Bold = bold;
    }

    public bool Equals(GlyphKey other) =>
        string.Equals(Grapheme, other.Grapheme, StringComparison.Ordinal) &&
        ReferenceEquals(Typeface, other.Typeface) &&
        TextSize.Equals(other.TextSize) &&
        Bold == other.Bold;

    public override bool Equals(object? obj) => obj is GlyphKey other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(Grapheme, RuntimeHelpers.GetHashCode(Typeface), TextSize, Bold);
}

/// <summary>
/// Single-channel (A8) coverage glyph atlas. Rasterizes graphemes once per
/// (grapheme, typeface, size, bold) key, stores tight-bounds placement
/// metadata (bearings + advance + baseline), and packs entries into shelves
/// with a hard size cap. All mutation and reads are lock-protected; the
/// bitmap is only exposed for texture upload under the lock (Phase 2).
/// Defects of the deleted predecessor that this design fixes:
///  - A8 coverage instead of baked RGBA (color applied at draw time);
///  - no color in the key (single key contract);
///  - Bold actually applies (synthetic stroke at rasterization);
///  - bearings recorded (placement verifiable, not centered by bounds width);
///  - width-2/wide graphemes rasterize at their natural advance;
///  - bounded growth with a hard cap + fallback signal instead of unbounded
///    doubling.
/// </summary>
public sealed class GlyphAtlas : IDisposable
{
    public const int MaxAtlasSize = 4096;   // 4096^2 A8 = 16 MB per atlas
    private const int DefaultInitialSize = 1024;
    private const int Padding = 2;          // gap between entries (sampling bleed)
    private const int MaxGlyphDimension = 512;

    private readonly object _lock = new();
    private readonly SKTypeface _typeface;
    private readonly float _textSize;
    private readonly Dictionary<GlyphKey, GlyphInfo> _map = new();
    private readonly List<Shelf> _shelves = new();
    private SKBitmap _bitmap;
    private SKCanvas _canvas;
    private int _nextShelfY;
    private bool _disposed;

    /// <summary>Recency stamp + refcount, maintained by <see cref="GlyphAtlasService"/> under its lock.</summary>
    internal long LastUsedStamp { get; set; }
    internal int RefCount { get; set; }

    /// <summary>
    /// Bumped whenever the backing bitmap is replaced (grow). Renderers hold
    /// an <see cref="SKImage"/> derived from the bitmap; they must recreate it
    /// when this changes (the old bitmap is disposed on grow).
    /// </summary>
    internal int Generation { get; private set; }

    public SKTypeface Typeface => _typeface;
    public float TextSize => _textSize;
    public int Width => _bitmap.Width;
    public int Height => _bitmap.Height;
    public long SizeBytes => (long)_bitmap.Width * _bitmap.Height; // A8: 1 byte/px
    public int EntryCount { get { lock (_lock) return _map.Count; } }

    /// <summary>
    /// The A8 atlas bitmap. Callers must hold the atlas reference (service
    /// Acquire) and take the <see cref="TryGetGlyph"/>-style lock discipline
    /// around any read or texture upload.
    /// </summary>
    internal SKBitmap AtlasBitmap { get { lock (_lock) return _bitmap; } }

    private struct Shelf
    {
        public int Y;
        public int Height;
        public int X;
    }

    public GlyphAtlas(SKTypeface typeface, float textSize, int initialSize = DefaultInitialSize)
    {
        _typeface = typeface ?? SKTypeface.Default;
        _textSize = textSize > 0 ? textSize : 12f;
        _bitmap = CreateAtlasBitmap(Math.Clamp(initialSize, 64, MaxAtlasSize));
        _canvas = new SKCanvas(_bitmap);
    }

    private static SKBitmap CreateAtlasBitmap(int size)
    {
        var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Alpha8, SKAlphaType.Premul));
        bitmap.Erase(SKColors.Transparent);
        return bitmap;
    }

    public bool TryGetGlyph(GlyphKey key, out GlyphInfo info)
    {
        lock (_lock)
        {
            return _map.TryGetValue(key, out info);
        }
    }

    /// <summary>
    /// Ensures the glyph is rasterized and packed. Returns false (with the
    /// atlas left valid) when the atlas is full — the caller falls back to
    /// the direct Skia path for that glyph.
    /// </summary>
    public bool EnsureGlyph(GlyphKey key, out GlyphInfo info)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out info)) return true;

            info = default;
            if (string.IsNullOrEmpty(key.Grapheme)) return false;

            var raster = RasterizeTight(key);
            using (raster.Image)
            {
                if (raster.Width <= 0 || raster.Height <= 0) return false;
                if (raster.Width > MaxGlyphDimension || raster.Height > MaxGlyphDimension) return false;

                if (!TryPlace(raster.Width, raster.Height, out int x, out int y)) return false;

                _canvas.DrawImage(
                    raster.Image,
                    new SKRect(raster.Left, raster.Top, raster.Left + raster.Width, raster.Top + raster.Height),
                    new SKRect(x, y, x + raster.Width, y + raster.Height),
                    new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None),
                    _rasterBlitPaint);
                _canvas.Flush();

                info = new GlyphInfo(
                    x, y, raster.Width, raster.Height,
                    raster.Advance, raster.BaselineOffset, raster.LeftBearing, raster.TopBearing);
                _map[key] = info;
                return true;
            }
        }
    }

    // Per-atlas blit paint: SkiaSharp paints are not thread-safe and atlases
    // are used concurrently by tests (and eventually by multiple views).
    private readonly SKPaint _rasterBlitPaint = new() { Color = SKColors.White, IsAntialias = false };

    /// <summary>
    /// Ensures a pre-shaped run (ligature) is rasterized and packed. The blob
    /// is only read during the call; the atlas retains nothing from it.
    /// Placement metadata follows the same contract as <see cref="EnsureGlyph"/>.
    /// </summary>
    public bool EnsureGlyphShaped(GlyphKey key, SKTextBlob blob, out GlyphInfo info)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out info)) return true;

            info = default;
            if (string.IsNullOrEmpty(key.Grapheme) || blob == null) return false;

            var raster = RasterizeTightShaped(key, blob);
            using (raster.Image)
            {
                if (raster.Width <= 0 || raster.Height <= 0) return false;
                if (raster.Width > MaxGlyphDimension || raster.Height > MaxGlyphDimension) return false;

                if (!TryPlace(raster.Width, raster.Height, out int x, out int y)) return false;

                _canvas.DrawImage(
                    raster.Image,
                    new SKRect(raster.Left, raster.Top, raster.Left + raster.Width, raster.Top + raster.Height),
                    new SKRect(x, y, x + raster.Width, y + raster.Height),
                    new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None),
                    _rasterBlitPaint);
                _canvas.Flush();

                info = new GlyphInfo(
                    x, y, raster.Width, raster.Height,
                    raster.Advance, raster.BaselineOffset, raster.LeftBearing, raster.TopBearing);
                _map[key] = info;
                return true;
            }
        }
    }

    private readonly struct GlyphRaster
    {
        public readonly SKImage Image;
        public readonly int Left;
        public readonly int Top;
        public readonly int Width;
        public readonly int Height;
        public readonly float Advance;
        public readonly float BaselineOffset;
        public readonly float LeftBearing;
        public readonly float TopBearing;

        public GlyphRaster(SKImage image, int left, int top, int width, int height,
            float advance, float baselineOffset, float leftBearing, float topBearing)
        {
            Image = image; Left = left; Top = top; Width = width; Height = height;
            Advance = advance; BaselineOffset = baselineOffset;
            LeftBearing = leftBearing; TopBearing = topBearing;
        }
    }

    /// <summary>
    /// Rasterizes the grapheme into a temporary A8 surface with the baseline
    /// at y = ascent, then tight-scans the coverage to derive bearings.
    /// Placement contract: draw at (cellX + LeftBearing, baselineY + TopBearing)
    /// where baselineY = cellTop + BaselineOffset.
    /// </summary>
    private GlyphRaster RasterizeTight(GlyphKey key)
    {
        return RasterizeTightCore(key, blob: null);
    }

    /// <summary>
    /// Same as <see cref="RasterizeTight"/> but rasterizes a pre-shaped
    /// <see cref="SKTextBlob"/> (ligature runs) instead of the raw string.
    /// The blob's glyph positions are relative to the baseline origin, matching
    /// the direct path's <c>DrawText(blob, x, baseline)</c> placement.
    /// </summary>
    private GlyphRaster RasterizeTightShaped(GlyphKey key, SKTextBlob blob)
    {
        return RasterizeTightCore(key, blob);
    }

    private GlyphRaster RasterizeTightCore(GlyphKey key, SKTextBlob? blob)
    {
        using var font = new SKFont(key.Typeface, key.TextSize)
        {
            Edging = SKFontEdging.Antialias,   // grayscale AA; subpixel needs RGB, not A8
            Subpixel = false,
            Hinting = SKFontHinting.Full,      // match the direct path's hinting
        };
        using var paint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
            // Synthetic bold applies to raw-string glyphs. Pre-shaped blobs
            // carry their own weight via the run font; no extra stroke.
            Style = blob == null && key.Bold ? SKPaintStyle.StrokeAndFill : SKPaintStyle.Fill,
            StrokeWidth = blob == null && key.Bold ? Math.Max(0.5f, key.TextSize * 0.04f) : 0f,
        };

        var fm = font.Metrics;
        float ascent = MathF.Ceiling(-fm.Ascent) + 1f;
        float descent = MathF.Ceiling(fm.Descent) + 1f;
        float advance;
        if (blob != null)
        {
            var bounds = blob.Bounds; // relative to the blob origin (baseline at 0,0)
            advance = MathF.Ceiling(bounds.Right) + 1f;
            ascent = MathF.Max(ascent, MathF.Ceiling(-bounds.Top) + 1f);
            descent = MathF.Max(descent, MathF.Ceiling(bounds.Bottom) + 1f);
        }
        else
        {
            advance = MathF.Ceiling(font.MeasureText(key.Grapheme)) + 1f;
        }
        int width = Math.Max(1, (int)advance);
        int height = Math.Max(1, (int)(ascent + descent));

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Alpha8, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        if (blob != null)
            canvas.DrawText(blob, 0f, -fm.Ascent, paint);
        else
            canvas.DrawText(key.Grapheme, 0f, -fm.Ascent, SKTextAlign.Left, font, paint);
        canvas.Flush();

        var pixmap = new SKPixmap();
        if (!surface.PeekPixels(pixmap))
        {
            return new GlyphRaster(surface.Snapshot(), 0, 0, 0, 0, advance, -fm.Ascent, 0f, 0f);
        }

        // Tight bounds scan over the A8 coverage.
        int left = width, top = height, right = -1, bottom = -1;
        unsafe
        {
            var p = (byte*)pixmap.GetPixels();
            int rowBytes = pixmap.RowBytes;
            for (int y = 0; y < height; y++)
            {
                var row = p + (nint)y * rowBytes;
                for (int x = 0; x < width; x++)
                {
                    if (row[x] == 0) continue;
                    if (x < left) left = x;
                    if (x > right) right = x;
                    if (y < top) top = y;
                    if (y > bottom) bottom = y;
                }
            }
        }

        int w = right - left + 1;
        int h = bottom - top + 1;
        if (w <= 0 || h <= 0)
        {
            return new GlyphRaster(surface.Snapshot(), 0, 0, 0, 0, advance, -fm.Ascent, 0f, 0f);
        }

        return new GlyphRaster(
            surface.Snapshot(), left, top, w, h,
            advance, -fm.Ascent, left, top - (-fm.Ascent));
    }

    private bool TryPlace(int width, int height, out int x, out int y)
    {
        for (int i = 0; i < _shelves.Count; i++)
        {
            var shelf = _shelves[i];
            if (shelf.Height >= height && _bitmap.Width - shelf.X >= width + Padding)
            {
                x = shelf.X;
                y = shelf.Y;
                _shelves[i] = new Shelf { Y = shelf.Y, Height = shelf.Height, X = shelf.X + width + Padding };
                return true;
            }
        }

        if (_nextShelfY + height + Padding > _bitmap.Height)
        {
            if (!Grow())
            {
                x = 0; y = 0;
                return false;
            }
        }

        x = 0;
        y = _nextShelfY;
        _shelves.Add(new Shelf { Y = y, Height = height, X = width + Padding });
        _nextShelfY += height + Padding;
        return true;
    }

    /// <summary>
    /// Doubles the atlas (capped at <see cref="MaxAtlasSize"/>), preserving
    /// existing entries. Returns false when the cap is reached.
    /// </summary>
    private bool Grow()
    {
        int newSize = _bitmap.Width * 2;
        if (newSize > MaxAtlasSize) return false;

        var bigger = CreateAtlasBitmap(newSize);
        using (var canvas = new SKCanvas(bigger))
        {
            canvas.DrawBitmap(_bitmap, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
            canvas.Flush();
        }
        _canvas.Dispose();
        _bitmap.Dispose();
        _bitmap = bigger;
        _canvas = new SKCanvas(_bitmap);
        Generation++;
        return true;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _canvas.Dispose();
            _bitmap.Dispose();
            _rasterBlitPaint.Dispose();
        }
    }
}
