using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using SkiaSharp;

namespace Dotty.Rendering.Gpu;

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
        float advance, float baselineOffset, float leftBearing, float topBearing, bool isColor = false)
    {
        X = x; Y = y; Width = width; Height = height;
        Advance = advance; BaselineOffset = baselineOffset;
        LeftBearing = leftBearing; TopBearing = topBearing;
        IsColor = isColor;
    }
    public readonly bool IsColor;
}

/// <summary>
/// Atlas lookup key. Deliberately has NO foreground color: the atlas stores
/// either grayscale coverage or intrinsic premultiplied color, so one entry
/// serves every foreground color/alpha. Typeface identity is by instance — canvases share
/// resolved typefaces through a static cache, so same-family instances compare
/// equal in practice.
/// </summary>
public readonly struct GlyphKey : IEquatable<GlyphKey>
{
    public readonly string Grapheme;
    public readonly SKTypeface Typeface;
    public readonly float TextSize;
    public readonly bool Bold;

    public readonly UiIcon Icon;

    public GlyphKey(string grapheme, SKTypeface typeface, float textSize, bool bold)
    {
        Grapheme = grapheme ?? string.Empty;
        Typeface = typeface ?? SKTypeface.Default;
        TextSize = textSize;
        Bold = bold;
        Icon = UiIcon.None;
    }

    public GlyphKey(UiIcon icon, float pixelSize)
    {
        Grapheme = string.Empty;
        Typeface = SKTypeface.Default;
        TextSize = pixelSize;
        Bold = false;
        Icon = icon;
    }

    public bool Equals(GlyphKey other) =>
        Icon == other.Icon &&
        string.Equals(Grapheme, other.Grapheme, StringComparison.Ordinal) &&
        ReferenceEquals(Typeface, other.Typeface) &&
        TextSize.Equals(other.TextSize) &&
        Bold == other.Bold;

    public override bool Equals(object? obj) => obj is GlyphKey other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(Grapheme, RuntimeHelpers.GetHashCode(Typeface), TextSize, Bold, Icon);
}
/// <summary>One changed rectangle in an atlas page bitmap.</summary>
public readonly struct AtlasDirtyRegion
{
    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }
    public AtlasDirtyRegion(int x, int y, int width, int height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }
}


/// <summary>
/// Two-page glyph atlas: A8 coverage for monochrome glyphs and lazily allocated
/// premultiplied RGBA for intrinsic-color glyphs. Rasterizes graphemes once per
/// (grapheme, typeface, size, bold) key and stores tight-bounds placement.
/// Mutation and atlas packing are lock-protected. Glyph lookup reads an immutable
/// copy-on-write map; bitmap access remains lock-protected for texture upload.
/// </summary>
public sealed class GlyphAtlas : IDisposable
{
    public const int MaxAtlasSize = 4096;   // Each page capped independently; A8 1 B/px, RGBA 4 B/px.
    private const int DefaultInitialSize = 1024;
    private const int Padding = 2;          // gap between entries (sampling bleed)
    private const int MaxGlyphDimension = 512;
    private const float MinFallbackScale = 0.5f;
    private const float MaxFallbackScale = 1.5f;
    private const string FallbackGrapheme = "\uFFFD";
    private readonly object _lock = new();
    private readonly SKTypeface _typeface;
    private readonly float _textSize;
    private readonly float _primaryBaseline;
    private readonly float _primaryCellHeight;
    private FontFallbackChain? _fallbackChain;
    private GlyphMapSnapshot _publishedMap = GlyphMapSnapshot.Empty;
    private readonly Dictionary<GlyphKey, GlyphInfo> _map = new();
    private readonly List<Shelf> _shelves = new();
    private readonly List<AtlasDirtyRegion> _dirtyRegions = new();
    private readonly List<Shelf> _colorShelves = new();
    private readonly List<AtlasDirtyRegion> _colorDirtyRegions = new();
    private SKBitmap? _colorBitmap;
    private SKCanvas? _colorCanvas;
    private int _colorNextShelfY;
    private int _colorContentVersion;
    private bool _colorFullUploadRequired;
    private int _colorWidth;
    private int _colorHeight;
    private static readonly object ColorProbeLock = new();
    private static SKBitmap? _colorProbeBitmap;
    private static SKCanvas? _colorProbeCanvas;
    private SKBitmap _bitmap;
    private SKCanvas _canvas;
    private int _width;
    private int _height;
    private int _nextShelfY;
    private bool _disposed;
    private GlyphInfo _fallbackGlyph;
    private bool _hasFallbackGlyph;
    /// <summary>Native pixel storage retained by the shared color-probe scratch bitmap, excluding its canvas wrapper.</summary>
    public static long IntrinsicColorProbeScratchBytes
    {
        get
        {
            lock (ColorProbeLock)
                return _colorProbeBitmap == null ? 0 : (long)_colorProbeBitmap.RowBytes * _colorProbeBitmap.Height;
        }
    }
    private bool _fullUploadRequired;
    private bool _glyphMapDirty;
    private int _publishedMapCopyCount;
    private int _contentVersion;
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
    public FontFallbackChain? FallbackChain
    {
        get => Volatile.Read(ref _fallbackChain);
        set => Volatile.Write(ref _fallbackChain, value);
    }
    public int Width => Volatile.Read(ref _width);
    public int Height => Volatile.Read(ref _height);
    public int ColorContentVersion => Volatile.Read(ref _colorContentVersion);
    public int ColorWidth => Volatile.Read(ref _colorWidth);
    public int ColorHeight => Volatile.Read(ref _colorHeight);
    public long SizeBytes => (long)Width * Height + (long)ColorWidth * ColorHeight * 4;
    public int EntryCount
    {
        get
        {
            lock (_lock)
                return _map.Count - (_hasFallbackGlyph ? 1 : 0);
        }
    }
    /// <summary>
    /// The A8 atlas bitmap. Callers must hold the atlas reference (service
    /// Acquire) and use <see cref="WithAtlasBitmap"/> or
    /// <see cref="WithAtlasUpdates"/> when reading pixels, so growth cannot
    /// dispose the bitmap during the read.
    /// </summary>
    public SKBitmap AtlasBitmap { get { lock (_lock) return _bitmap; } }

    /// <summary>
    /// Runs an atlas upload operation while the backing bitmap remains
    /// immutable and alive. Growth replaces and disposes the bitmap under the
    /// same lock, so texture uploaders must use this method instead of keeping
    /// <see cref="AtlasBitmap"/> beyond its getter call.
    /// </summary>
    public int WithAtlasBitmap(Action<SKBitmap> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            action(_bitmap);
            return ContentVersion;
        }
    }

    /// <summary>
    /// Gives the texture uploader a stable bitmap and all changed regions since
    /// its previous call. The regions are retired only after the callback
    /// succeeds, so an upload failure can be retried without losing pixels.
    /// </summary>
    public int WithAtlasUpdates(Action<SKBitmap, IReadOnlyList<AtlasDirtyRegion>, bool> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            bool completed = false;
            try
            {
                action(_bitmap, _dirtyRegions, _fullUploadRequired);
                completed = true;
                return ContentVersion;
            }
            finally
            {
                if (completed)
                {
                    _dirtyRegions.Clear();
                    _fullUploadRequired = false;
                }
            }
        }
    }
    public int WithColorAtlasUpdates(Action<SKBitmap, IReadOnlyList<AtlasDirtyRegion>, bool> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_colorBitmap == null) return ColorContentVersion;
            bool completed = false;
            try
            {
                action(_colorBitmap, _colorDirtyRegions, _colorFullUploadRequired);
                completed = true;
                return ColorContentVersion;
            }
            finally
            {
                if (completed)
                {
                    _colorDirtyRegions.Clear();
                    _colorFullUploadRequired = false;
                }
            }
        }
    }
    public SKBitmap? ColorAtlasBitmap { get { lock (_lock) return _colorBitmap; } }

    /// <summary>Returns the pre-reserved tofu glyph used when the atlas is full.</summary>
    public bool TryGetFallbackGlyph(out GlyphInfo info)
    {
        if (Volatile.Read(ref _hasFallbackGlyph))
        {
            info = _fallbackGlyph;
            return true;
        }

        info = default;
        return false;
    }

    private sealed class GlyphMapSnapshot
    {
        public static readonly GlyphMapSnapshot Empty = new(new Dictionary<GlyphKey, GlyphInfo>());
        private readonly Dictionary<GlyphKey, GlyphInfo> _entries;

        public GlyphMapSnapshot(Dictionary<GlyphKey, GlyphInfo> entries) => _entries = entries;
        public bool TryGetValue(GlyphKey key, out GlyphInfo info) => _entries.TryGetValue(key, out info);
    }

    private struct Shelf
    {
        public int Y;
        public int Height;
        public int X;
    }
    public GlyphAtlas(SKTypeface typeface, float textSize, int initialSize = DefaultInitialSize, FontFallbackChain? fallbackChain = null)
    {
        _typeface = typeface ?? SKTypeface.Default;
        _textSize = textSize > 0 ? textSize : 12f;
        using (var primaryFont = new SKFont(_typeface, _textSize))
        {
            var metrics = primaryFont.Metrics;
            _primaryBaseline = -metrics.Ascent;
            _primaryCellHeight = MathF.Max(1f,
                MathF.Ceiling(-metrics.Ascent) + 1f +
                MathF.Ceiling(metrics.Descent) + 1f);
        }
        _fallbackChain = fallbackChain;
        int size = Math.Clamp(initialSize, 64, MaxAtlasSize);
        _bitmap = CreateAtlasBitmap(size);
        _width = size;
        _height = size;
        _canvas = new SKCanvas(_bitmap);

        // Reserve a replacement glyph before normal content can fill the
        // atlas. This keeps the frame builder's failure path visible instead
        // of silently dropping a glyph when growth reaches MaxAtlasSize.
        var fallbackKey = new GlyphKey(FallbackGrapheme, _typeface, _textSize, bold: false);
        if (EnsureGlyph(fallbackKey, out var fallbackGlyph))
        {
            _fallbackGlyph = fallbackGlyph;
            Volatile.Write(ref _hasFallbackGlyph, true);
        }
        PublishPendingGlyphs();
    }

    private static SKBitmap CreateAtlasBitmap(int size)
    {
        var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Alpha8, SKAlphaType.Premul));
        bitmap.Erase(SKColors.Transparent);
        return bitmap;
    }

    public bool TryGetGlyph(GlyphKey key, out GlyphInfo info)
    {
        // Published hits stay lock-free. On a miss, probe the working map so
        // readers can observe entries committed in the current frame before
        // its batched snapshot is published.
        if (Volatile.Read(ref _publishedMap).TryGetValue(key, out info))
            return true;

        lock (_lock)
            return _map.TryGetValue(key, out info);
    }

    /// <summary>
    /// Ensures the glyph is rasterized and packed. Returns false (with the
    /// atlas left valid) when the requested glyph cannot be represented.
    /// Call <see cref="TryGetFallbackGlyph"/> to render the reserved tofu
    /// replacement in that case.
    /// </summary>
    public bool EnsureGlyph(GlyphKey key, out GlyphInfo info)
        => EnsureGlyph(key, out info, out _);

    /// <summary>
    /// Ensures a glyph and reports whether this call inserted a new atlas
    /// entry. Existing entries use the lock-free published-map path.
    /// </summary>
    public bool EnsureGlyph(GlyphKey key, out GlyphInfo info, out bool added)
    {
        added = false;
        if (Volatile.Read(ref _publishedMap).TryGetValue(key, out info))
            return true;

        info = default;
        if (string.IsNullOrEmpty(key.Grapheme))
            return false;

        // A prior miss in this frame may already have placed the glyph in the
        // writer map. Avoid rerasterizing it before the next snapshot publish.
        lock (_lock)
        {
            if (_map.TryGetValue(key, out info))
                return true;
        }

        // Rasterization intentionally happens before taking the atlas lock.
        // A concurrent miss may rasterize the same glyph, but the commit
        // recheck below ensures only one copy is packed.
        var raster = RasterizeEffective(key);
        using (raster.Image)
        {
            return CommitRasterizedGlyph(key, raster, out info, out added);
        }
    }

    /// <summary>
    /// Ensures a font-independent vector icon is packed into the monochrome
    /// atlas. The entry is a square of ceil(pixelSize) pixels with zero bearings
    /// and baseline offset; its advance is the requested pixel size.
    /// </summary>
    public bool EnsureIcon(UiIcon icon, float pixelSize, out GlyphInfo info)
    {
        info = default;
        if (icon == UiIcon.None || !float.IsFinite(pixelSize)
            || pixelSize <= 0f || pixelSize > MaxGlyphDimension)
        {
            return false;
        }

        var key = new GlyphKey(icon, pixelSize);
        if (Volatile.Read(ref _publishedMap).TryGetValue(key, out info))
            return true;

        lock (_lock)
        {
            if (_map.TryGetValue(key, out info))
                return true;
        }

        int size = (int)MathF.Ceiling(pixelSize);
        // Borrow the shared immutable path: only the canvas transform is scaled,
        // and the atlas never mutates or disposes the asset.
        var path = UiIcons.GetPath(icon);
        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Alpha8, SKAlphaType.Premul));
        if (surface == null)
            return false;

        using var paint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
        };
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(pixelSize / 24f);
        canvas.DrawPath(path, paint);
        canvas.Flush();

        using var image = surface.Snapshot();
        var raster = new GlyphRaster(image, 0, 0, size, size, pixelSize, 0f, 0f, 0f);
        return CommitRasterizedGlyph(key, raster, out info, out _);
    }

    /// <summary>
    /// Bumped on every successful glyph placement (not only growth). Derived
    /// copies of the atlas pixels key their freshness on this.
    /// </summary>
    public int ContentVersion => Volatile.Read(ref _contentVersion);

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
        if (Volatile.Read(ref _publishedMap).TryGetValue(key, out info))
            return true;

        info = default;
        if (string.IsNullOrEmpty(key.Grapheme) || blob == null)
            return false;

        lock (_lock)
        {
            if (_map.TryGetValue(key, out info))
                return true;
        }

        var raster = RasterizeTightShaped(key, blob);
        using (raster.Image)
        {
            return CommitRasterizedGlyph(key, raster, out info, out _);
        }
    }

    /// <summary>
    /// Publishes all glyphs committed since the previous publication in one
    /// copy-on-write snapshot. Call after a frame/batch has ensured its glyphs.
    /// Published dictionaries are never mutated, preserving lock-free hits.
    /// </summary>
    public void PublishPendingGlyphs()
    {
        lock (_lock)
        {
            if (!_glyphMapDirty)
                return;

            Volatile.Write(ref _publishedMap,
                new GlyphMapSnapshot(new Dictionary<GlyphKey, GlyphInfo>(_map)));
            _glyphMapDirty = false;
            _publishedMapCopyCount++;
        }
    }

    internal int PublishedMapCopyCount => Volatile.Read(ref _publishedMapCopyCount);

    private bool CommitRasterizedGlyph(
        GlyphKey key,
        in GlyphRaster raster,
        out GlyphInfo info,
        out bool added)
    {
        added = false;
        info = default;
        if (raster.Width <= 0 || raster.Height <= 0
            || raster.Width > MaxGlyphDimension || raster.Height > MaxGlyphDimension)
        {
            return false;
        }

        lock (_lock)
        {
            if (_map.TryGetValue(key, out info))
                return true;
            if (raster.IsColor) EnsureColorPage();

            if (!TryPlace(raster.Width, raster.Height, raster.IsColor, out int x, out int y))
                return false;

            var targetCanvas = raster.IsColor ? _colorCanvas! : _canvas;
            targetCanvas.DrawImage(raster.Image,
                new SKRect(raster.Left, raster.Top, raster.Left + raster.Width, raster.Top + raster.Height),
                new SKRect(x, y, x + raster.Width, y + raster.Height),
                new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None), _rasterBlitPaint);
            targetCanvas.Flush();

            info = new GlyphInfo(x, y, raster.Width, raster.Height, raster.Advance,
                raster.BaselineOffset, raster.LeftBearing, raster.TopBearing, raster.IsColor);
            _map[key] = info;
            if (raster.IsColor)
            {
                Volatile.Write(ref _colorContentVersion, unchecked(_colorContentVersion + 1));
                _colorDirtyRegions.Add(new AtlasDirtyRegion(x, y, raster.Width, raster.Height));
            }
            else
            {
                Volatile.Write(ref _contentVersion, unchecked(_contentVersion + 1));
                _dirtyRegions.Add(new AtlasDirtyRegion(x, y, raster.Width, raster.Height));
            }
            _glyphMapDirty = true;
            added = true;
            return true;
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
        public readonly bool IsColor;

        public GlyphRaster(SKImage image, int left, int top, int width, int height,
            float advance, float baselineOffset, float leftBearing, float topBearing, bool isColor = false)
        {
            Image = image; Left = left; Top = top; Width = width; Height = height;
            Advance = advance; BaselineOffset = baselineOffset;
            LeftBearing = leftBearing; TopBearing = topBearing; IsColor = isColor;
        }
    }

    /// <summary>
    /// Rasterizes the grapheme into a temporary A8 surface with the baseline
    /// at y = ascent, then tight-scans the coverage to derive bearings.
    /// Placement contract: draw at (cellX + LeftBearing, baselineY + TopBearing)
    /// where baselineY = cellTop + BaselineOffset.
    /// </summary>
    private GlyphRaster RasterizeEffective(GlyphKey key)
    {
        var fallbackChain = Volatile.Read(ref _fallbackChain);
        if (fallbackChain == null || string.IsNullOrEmpty(key.Grapheme))
        {
            return RasterizeTight(key);
        }

        var resolvedTypeface = fallbackChain.ResolveTypefaceForGrapheme(
            key.Grapheme, key.Bold, out bool isFallback);
        // The primary font, including its Nerd Font PUA glyphs, keeps the
        // exact existing raster path. Normalization is only for a resolved
        // fallback typeface.
        if (!isFallback || resolvedTypeface == null ||
            ReferenceEquals(resolvedTypeface, key.Typeface) ||
            ReferenceEquals(resolvedTypeface, _typeface))
        {
            return RasterizeTight(key);
        }

        using var fallbackFont = new SKFont(resolvedTypeface, key.TextSize)
        {
            Edging = SKFontEdging.Antialias,
            Subpixel = false,
            Hinting = SKFontHinting.Full,
        };
        var fallbackMetrics = fallbackFont.Metrics;
        float fallbackHeight = MathF.Max(1f,
            MathF.Ceiling(-fallbackMetrics.Ascent) + 1f +
            MathF.Ceiling(fallbackMetrics.Descent) + 1f);
        float scale = _primaryCellHeight / fallbackHeight;
        scale = Math.Clamp(scale, MinFallbackScale, MaxFallbackScale);
        // A fallback's ink can overhang its advance. Reserve the complete
        // one-cell or wide-cell allocation before rasterizing.
        float cellWidth = GetCellWidth(key.Grapheme, key.TextSize);
        float measuredAdvance = MathF.Max(1f, fallbackFont.MeasureText(key.Grapheme) + 1f);
        scale = MathF.Min(scale, cellWidth / measuredAdvance);
        scale = MathF.Max(0.05f, scale);

        // Metrics are normally sufficient to fit the cell. Re-rasterize at a
        // smaller scale when a particular glyph's ink or synthetic bold
        // stroke still exceeds its hard allocation.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var effectiveKey = new GlyphKey(
                key.Grapheme, resolvedTypeface, key.TextSize * scale, key.Bold);
            fallbackFont.Size = key.TextSize * scale;
            var raster = RasterizeTightCore(
                effectiveKey, blob: null, baselineOverride: _primaryBaseline,
                maxAdvance: cellWidth, fontOverride: fallbackFont);
            if (raster.Width <= MathF.Ceiling(cellWidth) &&
                raster.Height <= MathF.Ceiling(_primaryCellHeight))
            {
                return raster;
            }

            float widthScale = raster.Width > 0 ? cellWidth / raster.Width : 1f;
            float heightScale = raster.Height > 0 ? _primaryCellHeight / raster.Height : 1f;
            float correction = MathF.Min(widthScale, heightScale) * 0.98f;
            raster.Image.Dispose();
            if (!(correction > 0f) || correction >= 1f)
            {
                scale *= 0.5f;
            }
            else
            {
                scale *= correction;
            }
            scale = MathF.Max(0.05f, scale);
        }

        // The final attempt is still a valid A8 raster. CommitRasterizedGlyph
        var finalKey = new GlyphKey(key.Grapheme, resolvedTypeface, key.TextSize * scale, key.Bold);
        fallbackFont.Size = key.TextSize * scale;
        var finalRaster = RasterizeTightCore(finalKey, blob: null,
            baselineOverride: _primaryBaseline, maxAdvance: cellWidth,
            fontOverride: fallbackFont);
        if (finalRaster.Width > MathF.Ceiling(cellWidth) ||
            finalRaster.Height > MathF.Ceiling(_primaryCellHeight))
        {
            // Do not commit an entry whose tight bounds cannot fit its cell;
            // the caller will use the reserved tofu glyph instead.
            return new GlyphRaster(
                finalRaster.Image, 0, 0, 0, 0,
                MathF.Min(finalRaster.Advance, cellWidth),
                _primaryBaseline, 0f, 0f);
        }
        return finalRaster;
    }

    private static float GetCellWidth(string grapheme, float textSize)
        => textSize * (IsWideGrapheme(grapheme) ? 2f : 1f);

    private static bool IsWideGrapheme(string grapheme)
    {
        if (string.IsNullOrEmpty(grapheme) ||
            !System.Text.Rune.TryGetRuneAt(grapheme, 0, out var rune))
        {
            return false;
        }

        int cp = rune.Value;
        return (cp >= 0x1100 && cp <= 0x115F) ||
               cp == 0x2329 || cp == 0x232A ||
               (cp >= 0x2E80 && cp <= 0xA4CF) ||
               (cp >= 0xAC00 && cp <= 0xD7A3) ||
               (cp >= 0xF900 && cp <= 0xFAFF) ||
               (cp >= 0xFE10 && cp <= 0xFE6F) ||
               (cp >= 0xFF00 && cp <= 0xFF60) ||
               (cp >= 0xFFE0 && cp <= 0xFFE6) ||
               (cp >= 0x1F300 && cp <= 0x1FAFF) ||
               (cp >= 0x20000 && cp <= 0x3FFFD);
    }

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

    private bool TryRasterizeIntrinsicColor(
        GlyphKey key, SKTextBlob? blob, SKFont font, SKPaint paint,
        int width, int height, float baseline, float advance, float reportedBaseline,
        out GlyphRaster raster)
    {
        raster = default;
        if (width > MaxGlyphDimension || height > MaxGlyphDimension) return false;
        lock (ColorProbeLock)
        {
            if (_colorProbeBitmap == null || _colorProbeBitmap.Width < width || _colorProbeBitmap.Height < height)
            {
                _colorProbeCanvas?.Dispose();
                _colorProbeBitmap?.Dispose();
                _colorProbeBitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
                _colorProbeCanvas = new SKCanvas(_colorProbeBitmap);
            }
            var bitmap = _colorProbeBitmap;
            var canvas = _colorProbeCanvas!;
            bitmap.Erase(SKColors.Transparent);
            paint.Color = SKColors.White;
            if (blob != null) canvas.DrawText(blob, 0f, baseline, paint);
            else canvas.DrawText(key.Grapheme, 0f, baseline, SKTextAlign.Left, font, paint);
            canvas.Flush();
            bool intrinsic = false;
            int left = width, top = height, right = -1, bottom = -1;
            unsafe
            {
                byte* pixels = (byte*)bitmap.GetPixels();
                int rowBytes = bitmap.RowBytes;
                for (int y = 0; y < height; y++)
                {
                    byte* row = pixels + (nint)y * rowBytes;
                    for (int x = 0; x < width; x++)
                    {
                        byte* p = row + x * 4;
                        if (p[3] == 0) continue;
                        if (p[0] != p[3] || p[1] != p[3] || p[2] != p[3]) intrinsic = true;
                        if (x < left) left = x; if (x > right) right = x;
                        if (y < top) top = y; if (y > bottom) bottom = y;
                    }
                }
                if (!intrinsic && right >= left)
                {
                    bitmap.Erase(SKColors.Transparent);
                    paint.Color = SKColors.Black;
                    if (blob != null) canvas.DrawText(blob, 0f, baseline, paint);
                    else canvas.DrawText(key.Grapheme, 0f, baseline, SKTextAlign.Left, font, paint);
                    canvas.Flush();
                    pixels = (byte*)bitmap.GetPixels();
                    for (int y = top; y <= bottom && !intrinsic; y++)
                    {
                        byte* row = pixels + (nint)y * rowBytes;
                        for (int x = left; x <= right; x++)
                        {
                            byte* p = row + x * 4;
                            if (p[0] != 0 || p[1] != 0 || p[2] != 0) { intrinsic = true; break; }
                        }
                    }
                }
            }
            paint.Color = SKColors.White;
            if (!intrinsic || right < left || bottom < top) return false;
            int glyphWidth = right - left + 1, glyphHeight = bottom - top + 1;
            using var cropped = SKSurface.Create(new SKImageInfo(glyphWidth, glyphHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
            cropped.Canvas.Clear(SKColors.Transparent);
            cropped.Canvas.DrawBitmap(bitmap,
                new SKRect(left, top, right + 1, bottom + 1),
                new SKRect(0, 0, glyphWidth, glyphHeight),
                new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None), _rasterBlitPaint);
            cropped.Canvas.Flush();
            raster = new GlyphRaster(cropped.Snapshot(), 0, 0, glyphWidth, glyphHeight, advance,
                reportedBaseline, left, top - baseline, isColor: true);
            return true;
        }
    }

    private GlyphRaster RasterizeTightCore(
        GlyphKey key, SKTextBlob? blob,
        float baselineOverride = float.NaN,
        float maxAdvance = float.PositiveInfinity,
        SKFont? fontOverride = null)
    {
        using var ownedFont = fontOverride == null
            ? new SKFont(key.Typeface, key.TextSize)
            {
                Edging = SKFontEdging.Antialias,
                Subpixel = false,
                Hinting = SKFontHinting.Full,
            }
            : null;
        var font = fontOverride ?? ownedFont!;
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
        float drawBaseline = -fm.Ascent;
        float reportedBaseline = float.IsNaN(baselineOverride) ? drawBaseline : baselineOverride;
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
        float boundedAdvance = MathF.Min(advance, maxAdvance);
        int width = Math.Max(1, (int)advance);
        int height = Math.Max(1, (int)(ascent + descent));

        if (TryRasterizeIntrinsicColor(key, blob, font, paint, width, height, drawBaseline, boundedAdvance, reportedBaseline, out var colorRaster))
            return colorRaster;

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Alpha8, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        if (blob != null)
            canvas.DrawText(blob, 0f, drawBaseline, paint);
        else
            canvas.DrawText(key.Grapheme, 0f, drawBaseline, SKTextAlign.Left, font, paint);
        canvas.Flush();

        using var pixmap = new SKPixmap();
        if (!surface.PeekPixels(pixmap))
        {
            return new GlyphRaster(surface.Snapshot(), 0, 0, 0, 0,
                boundedAdvance, reportedBaseline, 0f, 0f);
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
            return new GlyphRaster(surface.Snapshot(), 0, 0, 0, 0,
                boundedAdvance, reportedBaseline, 0f, 0f);
        }

        return new GlyphRaster(
            surface.Snapshot(), left, top, w, h,
            boundedAdvance, reportedBaseline, left, top - drawBaseline);
    }

    private bool TryPlace(int width, int height, bool color, out int x, out int y)
    {
        var shelves = color ? _colorShelves : _shelves;
        var bitmap = color ? _colorBitmap! : _bitmap;
        int nextShelfY = color ? _colorNextShelfY : _nextShelfY;
        for (int i = 0; i < shelves.Count; i++)
        {
            var shelf = shelves[i];
            if (shelf.Height >= height && bitmap.Width - shelf.X >= width + Padding)
            {
                x = shelf.X; y = shelf.Y;
                shelves[i] = new Shelf { Y = shelf.Y, Height = shelf.Height, X = shelf.X + width + Padding };
                return true;
            }
        }
        if (width + Padding > bitmap.Width || nextShelfY + height + Padding > bitmap.Height)
        {
            while (width + Padding > bitmap.Width || nextShelfY + height + Padding > bitmap.Height)
            {
                if (!(color ? GrowColor() : Grow()))
                {
                    x = 0; y = 0; return false;
                }
                bitmap = color ? _colorBitmap! : _bitmap;
            }
        }
        x = 0; y = nextShelfY;
        shelves.Add(new Shelf { Y = y, Height = height, X = width + Padding });
        if (color) _colorNextShelfY += height + Padding; else _nextShelfY += height + Padding;
        return true;
    }

    private void EnsureColorPage()
    {
        if (_colorBitmap != null) return;
        int size = _bitmap.Width;
        _colorBitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        _colorBitmap.Erase(SKColors.Transparent);
        _colorCanvas = new SKCanvas(_colorBitmap);
        Volatile.Write(ref _colorWidth, size); Volatile.Write(ref _colorHeight, size);
        _colorFullUploadRequired = true;
    }

    private bool GrowColor()
    {
        int newSize = _colorBitmap!.Width * 2;
        if (newSize > MaxAtlasSize) return false;
        var bigger = new SKBitmap(new SKImageInfo(newSize, newSize, SKColorType.Rgba8888, SKAlphaType.Premul));
        bigger.Erase(SKColors.Transparent);
        using (var canvas = new SKCanvas(bigger))
        { canvas.DrawBitmap(_colorBitmap, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None)); canvas.Flush(); }
        _colorCanvas!.Dispose(); _colorBitmap.Dispose();
        _colorBitmap = bigger; _colorCanvas = new SKCanvas(bigger);
        Volatile.Write(ref _colorWidth, newSize); Volatile.Write(ref _colorHeight, newSize);
        _colorDirtyRegions.Clear(); _colorFullUploadRequired = true;
        return true;
    }


    /// <summary>
    /// Doubles the atlas (capped at <see cref="MaxAtlasSize"/>), preserving
    /// existing entries. Returns false when the cap is reached.
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
        _width = newSize;
        _height = newSize;
        Generation++;
        // Existing content is now on a replacement bitmap. Earlier dirty
        // rectangles are superseded by one required full allocation upload.
        _dirtyRegions.Clear();
        _fullUploadRequired = true;
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
            _colorCanvas?.Dispose();
            _colorBitmap?.Dispose();
            _rasterBlitPaint.Dispose();
        }
    }
}
