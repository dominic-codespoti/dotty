using System;
using System.Threading;
using System.Text;
using System.Collections.Generic;
using Dotty.Terminal.Adapter;
using Dotty.App.Rendering;
using SkiaSharp;

namespace Dotty.App.Rendering;

/// <summary>
/// Production-grade terminal frame compositor.
/// Region-first background synthesis with strict grid alignment.
/// No path unions, no tolerance heuristics.
/// </summary>
public sealed class TerminalFrameComposer : IDisposable
{
    private readonly SKPaint _backgroundFill = new() { IsAntialias = true };
    private readonly SKPaint _backgroundStroke = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 1f
    };
    private readonly SKPaint _glyphPaint = new()
    {
        IsAntialias = false,
    };

    private readonly SKFont _glyphFont = new();
    private readonly SKPaint _linePaint = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeCap = SKStrokeCap.Round
    };

    /// <summary>
    /// Current display scale factor (device pixels per DIP). Set by the host
    /// canvas before each render. Used to snap geometry and stroke widths to
    /// whole device pixels so fractional display scales stay crisp.
    /// </summary>
    public float DeviceScale { get; set; } = 1f;

    private float SnapDip(float dip)
    {
        float ds = Math.Max(0.1f, DeviceScale);
        return (float)(Math.Round(dip * ds) / ds);
    }

    private SKRect SnapRect(SKRect rect)
    {
        float left = SnapDip(rect.Left);
        float top = SnapDip(rect.Top);
        float right = SnapDip(rect.Right);
        float bottom = SnapDip(rect.Bottom);
        return SKRect.Create(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    // --- background synthesis state ---
    private readonly List<RowSpan> _rowSpans = new();
    private readonly Dictionary<RegionKey, ActiveRegion> _activeRegions = new();
    private readonly List<Region> _regions = new();
    private readonly List<RegionKey> _toRemove = new();
    private int _touchGen = 0;
    private readonly Stack<ActiveRegion> _activeRegionPool = new();
    private SynthCell[] _reusableSynthSpan = Array.Empty<SynthCell>();

    // --- HarfBuzz text shaping ---
    private TextShaper? _textShaper;
    private ShapedRunCache? _shapedRunCache;

    public TextShaper? TextShaper
    {
        get => _textShaper;
        set => _textShaper = value;
    }

    public ShapedRunCache? ShapedRunCache
    {
        get => _shapedRunCache;
        set => _shapedRunCache = value;
    }

    /// <summary>
    /// A8 coverage atlas + quad renderer (GPU plan Phase 2). When set along
    /// with <see cref="UseQuadGlyphs"/>, the glyph phase draws through
    /// <see cref="QuadGlyphRenderer"/> instead of per-cell DrawText. Owned and
    /// disposed by the canvas, not by the composer.
    /// </summary>
    public GlyphAtlas? GlyphAtlas { get; set; }
    public QuadGlyphRenderer? QuadRenderer { get; set; }
    public bool UseQuadGlyphs { get; set; }

    // --- Font fallback ---
    private List<SKTypeface>? _fallbackTypefaces;
    private SKTypeface? _primaryTypeface;

    /// <summary>
    /// Ordered list of typefaces for font fallback. Index 0 = primary.
    /// The primary typeface from the paint is used when this is null or empty.
    /// Emoji fonts should be placed at the end of the list.
    /// </summary>
    public List<SKTypeface>? FallbackTypefaces
    {
        get => _fallbackTypefaces;
        set
        {
            _fallbackTypefaces = value;
            _primaryTypeface = value != null && value.Count > 0 ? value[0] : null;
        }
    }

    // --- cached cell info ---
    // Legacy `_cellInfos` removed in favor of a single `CellClass` pass.
    private CellClass[] _cellClasses = Array.Empty<CellClass>();

    // Per-row classification cache, keyed by the buffer's identity generation
    // (which bumps on every content change but is never rotated, so a hit
    // guarantees the row's content is byte-identical to what was classified).
    // Lets full-range background synthesis skip unchanged rows.
    private CellClass[][]? _rowClassCache;
    private ulong[]? _rowClassGen;

    private readonly TerminalAppearanceSettings _appearance;

    public TerminalFrameComposer(TerminalAppearanceSettings? appearance = null)
    {
        _appearance = appearance ?? new TerminalAppearanceSettings();
    }

    public void Dispose()
    {
        _backgroundFill.Dispose();
        _backgroundStroke.Dispose();
        _glyphPaint.Dispose();
        _glyphFont.Dispose();
        _linePaint.Dispose();
        _activeRegionPool.Clear();
    }

    // ============================================================
    // PUBLIC API (unchanged)
    // ============================================================

    /// <summary>
    /// Serializes composer cache access across threads. The GPU-plan lease
    /// path executes <see cref="RenderTo"/> on the compositor's render thread
    /// while the UI thread may concurrently build the next frame's caches
    /// (classification rows, shaped-run blobs); this lock keeps those
    /// mutations exclusive. Hold times are bounded by one frame's raster.
    /// </summary>
    public object RenderLock { get; } = new object();

    /// <summary>Typeface of the composer's glyph font (for draw-op font setup).</summary>
    public SKTypeface PrimaryTypeface => _glyphFont.Typeface;

    /// <summary>Size of the composer's glyph font (for draw-op font setup).</summary>
    public float GlyphSize => _glyphFont.Size;

    public void RenderTo(
        SKCanvas target,
        IRenderSource buffer,
        SKPaint paint,
        SKFont font,
        float cellW,
        float cellH,
        int startRow = 0,
        int? endRow = null,
        bool? quadGlyphs = null)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));
        if (paint == null) throw new ArgumentNullException(nameof(paint));
        if (font == null) throw new ArgumentNullException(nameof(font));
        if (cellW <= 0 || cellH <= 0) return;

        lock (RenderLock)
        {
            int safeEndRow = endRow ?? (buffer.Rows - 1);

            EnsureCellClasses(buffer.Columns);

            CollectBackgroundRegions(buffer, startRow, safeEndRow);
            DrawBackgroundRegions(target, cellW, cellH, exactCellBackgrounds: buffer.IsAlternateScreenActive);

            SyncGlyphPaint(paint, font);
            // Per-call quad-mode override: the lease path forces quads on the
            // GPU canvas while the bitmap path keeps DrawText, sharing one
            // composer instance. The flip happens under RenderLock, so a
            // pending op from a previous frame can never observe a torn mode.
            bool prev = UseQuadGlyphs;
            if (quadGlyphs.HasValue) UseQuadGlyphs = quadGlyphs.Value;
            try
            {
                DrawGlyphs(target, buffer, paint, cellW, cellH, startRow, safeEndRow);
            }
            finally
            {
                UseQuadGlyphs = prev;
            }
        }
    }

    public void ResetCaches()
    {
        _regions.Clear();
        foreach (var region in _activeRegions.Values)
        {
            region.Color = default;
            _activeRegionPool.Push(region);
        }
        _activeRegions.Clear();
        _rowSpans.Clear();
        // Classification depends on the current font/typeface resolution;
        // force a re-classify on the next pass.
        if (_rowClassGen != null)
            Array.Clear(_rowClassGen, 0, _rowClassGen.Length);
    }

    /// <summary>
    /// Per-row dirty redraw for the non-scroll case (scroll offset and
    /// scrollback count unchanged, no alt-screen transition). The retained
    /// surface still holds the previous full render; only <paramref name="dirtyRows"/>
    /// (sorted ascending) are re-rasterized:
    /// 1. classify every visible row through the generation-keyed cache
    ///    (unchanged rows cost one Array.Copy);
    /// 2. synthesize background regions over the full visible range from the
    ///    cached classes so pills are never split and viewport-edge behavior
    ///    matches the full render;
    /// 3. re-apply the base color under dirty rows (non-AA hard rects) so
    ///    cells that lost their background do not keep stale pixels;
    /// 4. draw background regions intersecting dirty rows (opaque -> identity
    ///    elsewhere);
    /// 5. draw glyphs for dirty rows only.
    /// Cost model (73x136): a 1-row statusline update ~0.16 ms vs ~7 ms full.
    /// See docs/architecture/IncrementalScrollRendering.md §4.5.
    /// </summary>
    public void RenderDirty(
        SKCanvas target,
        IRenderSource buffer,
        SKPaint paint,
        SKFont font,
        float cellW,
        float cellH,
        SKColor bgColor,
        int startRow,
        int endRow,
        ReadOnlySpan<int> dirtyRows)
    {
        if (target == null || buffer == null || paint == null || font == null) return;
        if (cellW <= 0 || cellH <= 0 || dirtyRows.IsEmpty) return;
        int visibleRowCount = endRow - startRow + 1;
        if (dirtyRows.Length >= Math.Max(1, visibleRowCount))
        {
            // Degenerate: the dirty set covers the range; a full render is cheaper.
            RenderTo(target, buffer, paint, font, cellW, cellH, startRow, endRow);
            return;
        }

        EnsureCellClasses(buffer.Columns);

        // 1. Classify every visible row and synthesize background regions over
        //    the full range (CollectBackgroundRegions classifies each row via
        //    the generation-keyed cache; unchanged rows cost a reference swap).
        CollectBackgroundRegions(buffer, startRow, endRow);

        // 3. Base-color refill under dirty rows.
        bool prevAA = _backgroundFill.IsAntialias;
        _backgroundFill.IsAntialias = false;
        _backgroundFill.Style = SKPaintStyle.Fill;
        _backgroundFill.Color = bgColor;
        foreach (var r in dirtyRows)
        {
            if (r < startRow || r > endRow) continue;
            target.DrawRect(SKRect.Create(0, r * cellH, buffer.Columns * cellW, cellH), _backgroundFill);
        }
        _backgroundFill.IsAntialias = prevAA;

        // 4. Background regions intersecting dirty rows.
        DrawBackgroundRegions(target, cellW, cellH, buffer.IsAlternateScreenActive, dirtyRows);

        // 5. Glyphs for dirty rows only.
        SyncGlyphPaint(paint, font);
        DrawGlyphs(target, buffer, paint, cellW, cellH, startRow, endRow, dirtyRows);
    }

    /// <summary>
    /// True when <paramref name="dirtyRows"/> (sorted ascending) contains any
    /// row in the half-open range [<paramref name="top"/>, <paramref name="bottom"/>).
    /// </summary>
    private static bool SpanOverlapsDirtyRows(int top, int bottom, ReadOnlySpan<int> dirtyRows)
    {
        int lo = 0, hi = dirtyRows.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            int row = dirtyRows[mid];
            if (row < top) lo = mid + 1;
            else if (row >= bottom) hi = mid - 1;
            else return true;
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="rows"/> (sorted ascending) contains <paramref name="row"/>.
    /// </summary>
    private static bool ContainsDirtyRow(ReadOnlySpan<int> rows, int row)
    {
        int lo = 0, hi = rows.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            int v = rows[mid];
            if (v == row) return true;
            if (v < row) lo = mid + 1;
            else hi = mid - 1;
        }
        return false;
    }

    // ============================================================
    // BACKGROUND REGION PIPELINE
    // ============================================================

    private void CollectBackgroundRegions(IRenderSource buffer, int startRow, int endRow)
    {
        _regions.Clear();
        _activeRegions.Clear();

        for (int row = startRow; row <= endRow; row++)
        {
            // Classify the row once and let the span builder and glyph
            // renderer consume that single source of truth.
            EnsureRowClassified(buffer, row);
            BuildRowSpans(_cellClasses, row);
            MergeRowSpans(row);
        }

        FlushActiveRegions();

        
    }

    private void BuildRowSpans(CellClass[] rowCells, int row)
    {
        // Convert classification into synth cells; the pure builder appends
        // directly into the composer's persistent span list (no per-row List).
        if (_reusableSynthSpan.Length < rowCells.Length) { _reusableSynthSpan = new SynthCell[rowCells.Length]; }
        var synth = _reusableSynthSpan;
        for (int i = 0; i < rowCells.Length; i++)
        {
            var c = rowCells[i];
            synth[i] = new SynthCell
            {
                IsContinuation = c.IsContinuation,
                Width = c.Width,
                HasBg = c.HasBg,
                Bg = c.Bg,
                IsSeparatorGlyph = c.IsSeparatorGlyph
            };
        }

        BackgroundSynth.BuildRowSpans(synth.AsSpan(0, rowCells.Length), _rowSpans);
    }

    private void MergeRowSpans(int row)
    {
        _touchGen++;
        _toRemove.Clear();

        foreach (var span in _rowSpans)
        {
            var key = new RegionKey(span.X0, span.X1, span.Color);

            if (_activeRegions.TryGetValue(key, out var region))
            {
                region.BottomRow = row + 1;
                region.LastTouchedGen = _touchGen;
            }
            else
            {
                if (!_activeRegionPool.TryPop(out region)) { region = new ActiveRegion(); }
                region.X0 = span.X0;
                region.X1 = span.X1;
                region.TopRow = row;
                region.BottomRow = row + 1;
                region.Color = span.Color;
                region.LastTouchedGen = _touchGen;
                _activeRegions[key] = region;
            }
        }

        foreach (var kvp in _activeRegions)
        {
            if (kvp.Value.LastTouchedGen == _touchGen) continue;

            var r = kvp.Value;
            _regions.Add(new Region(r.X0, r.X1, r.TopRow, r.BottomRow, r.Color));
            _toRemove.Add(kvp.Key);
        }

        foreach (var k in _toRemove)
        {
            _activeRegionPool.Push(_activeRegions[k]);
            _activeRegions.Remove(k);
        }
    }

    private void FlushActiveRegions()
    {
        foreach (var r in _activeRegions.Values)
        {
            _regions.Add(new Region(r.X0, r.X1, r.TopRow, r.BottomRow, r.Color));
            r.Color = default;
            _activeRegionPool.Push(r);
        }

        _activeRegions.Clear();
    }

    private void DrawBackgroundRegions(
        SKCanvas canvas,
        float cellW,
        float cellH,
        bool exactCellBackgrounds,
        ReadOnlySpan<int> onlyRows = default)
    {
        float horizontalPadding = exactCellBackgrounds ? 0f : _appearance.HorizontalPadding;
        float verticalPadding = exactCellBackgrounds ? 0f : _appearance.GetVerticalPadding(cellH);
        float radius = exactCellBackgrounds ? 0f : _appearance.GetRadius(cellH, verticalPadding);

        foreach (var r in _regions)
        {
            if (!onlyRows.IsEmpty && !SpanOverlapsDirtyRows(r.TopRow, r.BottomRow, onlyRows))
                continue;

            float left = r.X0 * cellW - horizontalPadding;
            float right = r.X1 * cellW + horizontalPadding;
            float top = r.TopRow * cellH + verticalPadding;
            float bottom = r.BottomRow * cellH - verticalPadding;

            if (right <= left || bottom <= top) continue;

            var rect = SnapRect(SKRect.Create(left, top, right - left, bottom - top));
            if (exactCellBackgrounds)
            {
                _backgroundFill.Style = SKPaintStyle.Fill;
                _backgroundFill.Color = r.Color;
                canvas.DrawRect(rect, _backgroundFill);
                continue;
            }

            var rectRadius = SnapDip(Math.Min(radius, Math.Min(rect.Width, rect.Height) * 0.5f));
            bool canInset = rect.Width >= rect.Height + 2f;
            DrawPill(canvas, rect, r.Color, canInset, rectRadius);
        }
    }

    private void DrawPill(SKCanvas canvas, SKRect rect, SKColor color, bool drawInnerStroke, float radius)
    {
        float rad = Math.Clamp(radius, 0f, Math.Min(rect.Height, rect.Width) * 0.5f);

        _backgroundFill.Style = SKPaintStyle.Fill;
        _backgroundFill.Color = color;
        canvas.DrawRoundRect(rect, rad, rad, _backgroundFill);

        if (!drawInnerStroke) return;

        _backgroundStroke.Style = SKPaintStyle.Stroke;
        _backgroundStroke.StrokeJoin = SKStrokeJoin.Round;
        _backgroundStroke.StrokeCap = SKStrokeCap.Round;
        _backgroundStroke.StrokeWidth = Math.Max(1f / Math.Max(0.1f, DeviceScale), SnapDip(2f));
        _backgroundStroke.Color = DarkenColor(color);

        var rr = new SKRoundRect(rect, rad, rad);
        canvas.Save();
        canvas.ClipRoundRect(rr, SKClipOperation.Intersect, antialias: true);
        canvas.DrawRoundRect(rect, rad, rad, _backgroundStroke);
        canvas.Restore();
    }

    //     ============================================================
    // GLYPH RENDERING
    // ============================================================

    // Default hyperlink color (blue) - can be made configurable
    private static readonly SKColor HyperlinkColor = new SKColor(0xFF, 0x64, 0xB0); // Accent blue
    private static readonly SKColor HyperlinkUnderlineColor = new SKColor(0xFF, 0x64, 0xB0);

    private void DrawGlyphs(
        SKCanvas canvas,
        IRenderSource buffer,
        SKPaint paint,
        float cellW,
        float cellH,
        int startRow,
        int endRow,
        ReadOnlySpan<int> onlyRows = default,
        bool allowQuadPath = true)
    {
        if (allowQuadPath && UseQuadGlyphs && GlyphAtlas != null && QuadRenderer != null)
        {
            // The quad path handles its own fallback rows by re-entering here
            // with allowQuadPath: false - never recurse.
            DrawGlyphsQuad(canvas, buffer, paint, cellW, cellH, startRow, endRow, onlyRows);
            return;
        }

        var fm = _glyphFont.Metrics;
        float baselineOffset = -fm.Ascent;

        var defaultColor = paint.Color;
        long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool isBlinkVisible = (ms % 1000) < 500;
        bool baseAntialias = _glyphPaint.IsAntialias;
        bool baseSubpixel = _glyphFont.Subpixel;
        var baseEdging = _glyphFont.Edging;

        _linePaint.StrokeWidth = Math.Max(1f / Math.Max(0.1f, DeviceScale), SnapDip(cellH * 0.05f));
        Span<SKRect> geometryRects = stackalloc SKRect[8];
        var sb = new StringBuilder(64);

        for (int row = startRow; row <= endRow; row++)
        {
            if (!onlyRows.IsEmpty && !ContainsDirtyRow(onlyRows, row)) continue;

            EnsureRowClassified(buffer, row);

            float rowTop = row * cellH;
            float baseline = MathF.Round(rowTop + baselineOffset);

            canvas.Save();
            canvas.ClipRect(SKRect.Create(0, rowTop, buffer.Columns * cellW, cellH));

            int col = 0;
            while (col < buffer.Columns)
            {
                var cc = _cellClasses[col];
                if (!cc.ShouldDrawGlyph || cc.Invisible || (cc.SlowBlink && !isBlinkVisible))
                {
                    col += cc.Width;
                    continue;
                }

                bool hasHyperlink = cc.HyperlinkId != 0;
                var fgColor = cc.HasFg ? cc.Fg : defaultColor;
                if (hasHyperlink) fgColor = HyperlinkColor;
                if (cc.Faint) fgColor = fgColor.WithAlpha((byte)(fgColor.Alpha / 2));

                bool disableSmoothing = IsPixelGridRune(cc.FirstRune);

                float x = MathF.Round(col * cellW);
                bool renderedAsBlockGeometry = false;

                // Handle block elements / box drawing with geometry (not shaped text)
                if (IsGeometryRenderedRune(cc.FirstRune))
                {
                    float left = x;
                    float top = MathF.Round(row * cellH);
                    float right = MathF.Round((col + cc.Width) * cellW);
                    float bottom = MathF.Round((row + 1) * cellH);

                    int rectCount = BuildGeometryRects(cc.FirstRune, geometryRects, left, top, right, bottom);
                    if (rectCount > 0)
                    {
                        _glyphPaint.Color = fgColor;
                        _glyphPaint.IsAntialias = disableSmoothing ? false : baseAntialias;
                        _glyphFont.Subpixel = disableSmoothing ? false : baseSubpixel;
                        _glyphFont.Edging = disableSmoothing ? SKFontEdging.Alias : baseEdging;
                        _glyphPaint.Style = SKPaintStyle.Fill;
                        _glyphPaint.StrokeWidth = 0f;
                        for (int i = 0; i < rectCount; i++)
                        {
                            var rect = SnapRect(geometryRects[i]);
                            if (rect.Width <= 0f || rect.Height <= 0f) continue;
                            canvas.DrawRect(rect, _glyphPaint);
                        }
                        renderedAsBlockGeometry = true;
                    }
                }

                if (!renderedAsBlockGeometry)
                {
                    // Attempt to extend a shapeable run from this cell
                    int runEnd = col + cc.Width;
                    if (!disableSmoothing && _textShaper != null && !cc.IsContinuation)
                    {
                        var runFg = fgColor;
                        bool runBold = cc.Bold;
                        int runTypefaceIdx = cc.TypefaceIndex;

                        while (runEnd < buffer.Columns)
                        {
                            var next = _cellClasses[runEnd];
                            if (!next.ShouldDrawGlyph || next.Invisible) break;
                            if (IsPixelGridRune(next.FirstRune)) break;
                            if (next.HasFg ? next.Fg != runFg : runFg != defaultColor) break;
                            if (next.Bold != runBold) break;
                            if (next.TypefaceIndex != runTypefaceIdx) break;
                            if (next.IsContinuation) { runEnd++; continue; }
                            runEnd += next.Width;
                        }

                        // Count non-continuation glyph cells in the run
                        int glyphCount = 0;
                        for (int c = col; c < runEnd; )
                        {
                            if (!_cellClasses[c].IsContinuation) glyphCount++;
                            c += _cellClasses[c].Width;
                        }

                        if (glyphCount >= 2)
                        {
                            // Build combined text for the run
                            sb.Clear();
                            for (int c = col; c < runEnd; )
                            {
                                var cell = _cellClasses[c];
                                if (cell.ShouldDrawGlyph && !cell.IsContinuation)
                                    sb.Append(cell.Grapheme);
                                c += cell.Width;
                            }

                            string combined = sb.ToString();
                            var runTypeface = GetTypefaceForIndex(runTypefaceIdx);
                            float textSize = _glyphFont.Size;

                            _glyphPaint.Color = fgColor;
                            _glyphPaint.StrokeWidth = runBold ? 0.8f : 0f;
                            _glyphPaint.Style = SKPaintStyle.Fill;

                            // Use HarfBuzz-shaped positions. The built SKTextBlob is
                            // cached alongside the run so the per-frame combined-string
                            // + SKTextBlobBuilder + SKFont churn happens once per
                            // unique (text, typeface, size, bold) key.
                            if (TryGetRunBlob(combined, runTypeface, textSize, runBold, out var blob, out bool disposeBlob))
                            {
                                canvas.DrawText(blob, x, baseline, _glyphPaint);
                                if (disposeBlob) blob.Dispose();
                            }

                            // Draw per-cell decorations for the shaped run
                            for (int c = col; c < runEnd; )
                            {
                                var cell = _cellClasses[c];
                                if (!cell.IsContinuation && cell.ShouldDrawGlyph && !cell.Invisible)
                                {
                                    float cellX = MathF.Round(c * cellW);
                                    float cellLineW = cellW * cell.Width;
                                    DrawCellDecorations(canvas, fm, fgColor, hasHyperlink,
                                        cellX, baseline, cellLineW, cell);
                                }
                                c += cell.Width;
                            }

                            col = runEnd;
                            continue;
                        }
                    }

                    // Single cell (or shaping unavailable): direct DrawText
                    _glyphPaint.Color = fgColor;
                    _glyphPaint.IsAntialias = disableSmoothing ? false : baseAntialias;
                    _glyphFont.Subpixel = disableSmoothing ? false : baseSubpixel;
                    _glyphFont.Edging = disableSmoothing ? SKFontEdging.Alias : baseEdging;
                    _glyphPaint.StrokeWidth = cc.Bold ? 0.8f : 0f;
                    _glyphPaint.Style = SKPaintStyle.Fill;

                    // Use fallback typeface if this cell needs a different font
                    var savedTypeface = _glyphFont.Typeface;
                    if (cc.TypefaceIndex != 0)
                    {
                        var cellTf = GetTypefaceForIndex(cc.TypefaceIndex);
                        if (cellTf != null)
                            _glyphFont.Typeface = cellTf;
                    }

                    canvas.DrawText(cc.Grapheme, x, baseline, SKTextAlign.Left, _glyphFont, _glyphPaint);

                    // Restore primary typeface
                    if (cc.TypefaceIndex != 0)
                        _glyphFont.Typeface = savedTypeface;
                }

                // Draw per-cell decorations
                DrawCellDecorations(canvas, fm, fgColor, hasHyperlink, x, baseline, cellW * cc.Width, cc);

                col += cc.Width;
            }

            canvas.Restore();
        }
    }

    /// <summary>
    /// Returns the shaped SKTextBlob for a run, shaping and building/caching it
    /// when missing. <paramref name="disposeBlob"/> is true only when there is no
    /// shared cache and the caller owns the blob.
    /// </summary>
    private bool TryGetRunBlob(string combined, SKTypeface runTypeface, float textSize, bool runBold, out SKTextBlob blob, out bool disposeBlob)
    {
        blob = null!;
        disposeBlob = false;
        ShapedRun shaped = default;
        SKTextBlob? cached = null;
        if (_shapedRunCache != null)
            _shapedRunCache.TryGet(combined, runTypeface, textSize, runBold, out shaped, out cached);
        if (cached == null)
        {
            if (_textShaper == null) return false;
            shaped = _textShaper.Shape(combined, runTypeface, textSize);
            _shapedRunCache?.Add(combined, runTypeface, textSize, runBold, shaped);
        }
        if (cached != null) { blob = cached; return true; }

        var blobBuilder = new SKTextBlobBuilder();
        using var runFont = new SKFont(runTypeface, textSize);
        var runHandle = blobBuilder.AllocatePositionedRun(runFont, shaped.GlyphIndices.Length);
        runHandle.SetGlyphs(shaped.GlyphIndices.AsSpan());
        runHandle.SetPositions(shaped.Positions.AsSpan());
        var built = blobBuilder.Build() ?? throw new InvalidOperationException("SKTextBlobBuilder.Build() returned null");
        if (_shapedRunCache != null)
            _shapedRunCache.AddBlob(combined, runTypeface, textSize, runBold, built);
        else
            disposeBlob = true;
        blob = built;
        return true;
    }

    private readonly struct CellQuadPlan
    {
        public readonly GlyphInfo Info;
        public readonly float X;
        public readonly float Baseline;
        public readonly SKColor Color;

        public CellQuadPlan(GlyphInfo info, float x, float baseline, SKColor color)
        {
            Info = info;
            X = x;
            Baseline = baseline;
            Color = color;
        }
    }

    private CellQuadPlan[] _quadPlanScratch = Array.Empty<CellQuadPlan>();

    /// <summary>
    /// GPU-plan Phase 2 glyph path: per-frame CPU-built quad buffer consumed by
    /// one DrawVertices call per pass (textured glyphs + solid decorations/box
    /// geometry). Per row: ensure every glyph in the A8 atlas first — a miss
    /// falls the whole row back to the direct path so nothing double-draws.
    /// Rows with curl/dotted/dashed underline also fall back wholesale.
    /// Rasterization is grayscale AA (the atlas is A8); the direct path's
    /// subpixel AA is a documented v1 divergence (pixel-diff gate tolerance).
    /// </summary>
    private void DrawGlyphsQuad(
        SKCanvas canvas,
        IRenderSource buffer,
        SKPaint paint,
        float cellW,
        float cellH,
        int startRow,
        int endRow,
        ReadOnlySpan<int> onlyRows)
    {
        var atlas = GlyphAtlas!;
        var quad = QuadRenderer!;
        var batch = quad.Batch;
        var fm = _glyphFont.Metrics;
        float baselineOffset = -fm.Ascent;
        var defaultColor = paint.Color;
        long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool isBlinkVisible = (ms % 1000) < 500;

        _linePaint.StrokeWidth = Math.Max(1f / Math.Max(0.1f, DeviceScale), SnapDip(cellH * 0.05f));
        Span<SKRect> geometryRects = stackalloc SKRect[8];
        var sb = new StringBuilder(64);

        for (int row = startRow; row <= endRow; row++)
        {
            if (!onlyRows.IsEmpty && !ContainsDirtyRow(onlyRows, row)) continue;
            EnsureRowClassified(buffer, row);

            if (RowHasComplexDecorations())
            {
                DrawGlyphs(canvas, buffer, paint, cellW, cellH, row, row, default, allowQuadPath: false);
                continue;
            }

            float rowTop = row * cellH;
            float baseline = MathF.Round(rowTop + baselineOffset);
            int cols = buffer.Columns;

            if (_quadPlanScratch.Length < cols)
                _quadPlanScratch = new CellQuadPlan[cols];

            // Phase 1: ensure every glyph in the atlas; collect placement plans.
            int planCount = 0;
            bool fallback = false;
            int col = 0;
            while (col < cols)
            {
                var cc = _cellClasses[col];
                if (!cc.ShouldDrawGlyph || cc.Invisible || (cc.SlowBlink && !isBlinkVisible) || cc.IsContinuation)
                {
                    col += cc.Width;
                    continue;
                }
                if (IsGeometryRenderedRune(cc.FirstRune))
                {
                    col += cc.Width; // solids in phase 2; nothing to ensure
                    continue;
                }

                bool hasHyperlink = cc.HyperlinkId != 0;
                var fgColor = cc.HasFg ? cc.Fg : defaultColor;
                if (hasHyperlink) fgColor = HyperlinkColor;
                if (cc.Faint) fgColor = fgColor.WithAlpha((byte)(fgColor.Alpha / 2));

                bool disableSmoothing = IsPixelGridRune(cc.FirstRune);
                float x = MathF.Round(col * cellW);

                // Run detection (same rules as the direct path).
                int runEnd = col + cc.Width;
                if (!disableSmoothing && _textShaper != null)
                {
                    var runFg = fgColor;
                    bool runBold = cc.Bold;
                    int runTypefaceIdx = cc.TypefaceIndex;

                    while (runEnd < cols)
                    {
                        var next = _cellClasses[runEnd];
                        if (!next.ShouldDrawGlyph || next.Invisible) break;
                        if (IsPixelGridRune(next.FirstRune)) break;
                        if (next.HasFg ? next.Fg != runFg : runFg != defaultColor) break;
                        if (next.Bold != runBold) break;
                        if (next.TypefaceIndex != runTypefaceIdx) break;
                        if (next.IsContinuation) { runEnd++; continue; }
                        runEnd += next.Width;
                    }

                    int glyphCount = 0;
                    for (int c = col; c < runEnd;)
                    {
                        if (!_cellClasses[c].IsContinuation) glyphCount++;
                        c += _cellClasses[c].Width;
                    }

                    if (glyphCount >= 2)
                    {
                        sb.Clear();
                        for (int c = col; c < runEnd;)
                        {
                            var cell = _cellClasses[c];
                            if (cell.ShouldDrawGlyph && !cell.IsContinuation)
                                sb.Append(cell.Grapheme);
                            c += cell.Width;
                        }
                        string combined = sb.ToString();
                        var runTypeface = GetTypefaceForIndex(runTypefaceIdx);
                        float textSize = _glyphFont.Size;

                        var key = new GlyphKey(combined, runTypeface, textSize, runBold);
                        if (atlas.TryGetGlyph(key, out var info))
                        {
                            _quadPlanScratch[planCount++] = new CellQuadPlan(info, x, baseline, fgColor);
                            col = runEnd;
                            continue;
                        }
                        if (TryGetRunBlob(combined, runTypeface, textSize, runBold, out var blob, out bool disposeBlob))
                        {
                            bool ok = atlas.EnsureGlyphShaped(key, blob, out info);
                            if (disposeBlob) blob.Dispose();
                            if (ok)
                            {
                                _quadPlanScratch[planCount++] = new CellQuadPlan(info, x, baseline, fgColor);
                                col = runEnd;
                                continue;
                            }
                        }
                        fallback = true;
                        break;
                    }
                }

                // Single cell.
                var cellKey = new GlyphKey(cc.Grapheme, GetTypefaceForIndex(cc.TypefaceIndex), _glyphFont.Size, cc.Bold);
                if (atlas.EnsureGlyph(cellKey, out var cellInfo))
                {
                    _quadPlanScratch[planCount++] = new CellQuadPlan(cellInfo, x, baseline, fgColor);
                }
                else
                {
                    fallback = true;
                    break;
                }
                col += cc.Width;
            }

            if (fallback)
            {
                DrawGlyphs(canvas, buffer, paint, cellW, cellH, row, row, default, allowQuadPath: false);
                continue;
            }

            // Phase 2: emit glyph quads + solid quads from the plans.
            col = 0;
            int planIdx = 0;
            while (col < cols)
            {
                var cc = _cellClasses[col];
                bool hasHyperlink = cc.HyperlinkId != 0;
                var fgColor = cc.HasFg ? cc.Fg : defaultColor;
                if (hasHyperlink) fgColor = HyperlinkColor;
                if (cc.Faint) fgColor = fgColor.WithAlpha((byte)(fgColor.Alpha / 2));
                float x = MathF.Round(col * cellW);

                if (cc.ShouldDrawGlyph && !cc.Invisible && !(cc.SlowBlink && !isBlinkVisible) && !cc.IsContinuation)
                {
                    if (IsGeometryRenderedRune(cc.FirstRune))
                    {
                        float left = x;
                        float top = MathF.Round(row * cellH);
                        float right = MathF.Round((col + cc.Width) * cellW);
                        float bottom = MathF.Round((row + 1) * cellH);
                        int rectCount = BuildGeometryRects(cc.FirstRune, geometryRects, left, top, right, bottom);
                        for (int i = 0; i < rectCount; i++)
                        {
                            var rect = SnapRect(geometryRects[i]);
                            if (rect.Width <= 0f || rect.Height <= 0f) continue;
                            batch.AddSolidQuad(rect.Left, rect.Top, rect.Width, rect.Height, fgColor);
                        }
                    }
                    else if (planIdx < planCount)
                    {
                        var plan = _quadPlanScratch[planIdx++];
                        float destX = plan.X + plan.Info.LeftBearing;
                        float destY = plan.Baseline + plan.Info.TopBearing;
                        // Image shaders sample in the image's pixel coordinates.
                        batch.AddGlyphQuad(
                            destX, destY, plan.Info.Width, plan.Info.Height,
                            new SKRect(plan.Info.X, plan.Info.Y, plan.Info.X + plan.Info.Width, plan.Info.Y + plan.Info.Height),
                            plan.Color);
                    }
                }

                if (cc.ShouldDrawGlyph && !cc.Invisible)
                    AddDecorationQuads(batch, fm, fgColor, hasHyperlink, x, baseline, cellW * cc.Width, cc);

                col += cc.Width;
            }
        }

        quad.Flush(canvas);
    }

    /// <summary>
    /// True when the current row's classification contains any cell whose
    /// underline style needs the direct path (curl/dotted/dashed geometry is
    /// not expressible as quads in v1). The whole row falls back to keep
    /// per-cell alignment trivial.
    /// </summary>
    private bool RowHasComplexDecorations()
    {
        for (int i = 0; i < _cellClasses.Length; i++)
        {
            var style = _cellClasses[i].CellUnderlineStyle;
            if (style == UnderlineStyle.Curl || style == UnderlineStyle.Dotted || style == UnderlineStyle.Dashed)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Solid-quad equivalent of <see cref="DrawCellDecorations"/> for the
    /// quad path. Single/double underline, strikethrough, overline, and
    /// hyperlink underline; complex styles never reach here (row fallback).
    /// </summary>
    private void AddDecorationQuads(QuadGlyphBatch batch, SKFontMetrics fm, SKColor fgColor, bool hasHyperlink, float x, float baseline, float lineW, in CellClass cc)
    {
        var style = cc.CellUnderlineStyle;
        if (style == UnderlineStyle.None && !cc.Strikethrough && !cc.Overline && !hasHyperlink)
            return;

        SKColor lineColor = hasHyperlink ? HyperlinkUnderlineColor
            : (cc.UnderlineColorArgb != 0) ? new SKColor(cc.UnderlineColorArgb) : fgColor;
        float w = Math.Max(1f / Math.Max(0.1f, DeviceScale), _linePaint.StrokeWidth);

        if (style != UnderlineStyle.None || hasHyperlink)
        {
            float y = SnapDip(baseline + fm.Descent * 0.5f);
            switch (style)
            {
                case UnderlineStyle.Double:
                    batch.AddSolidQuad(x, y, lineW, w, lineColor);
                    batch.AddSolidQuad(x, SnapDip(baseline + fm.Descent * 0.8f), lineW, w, lineColor);
                    break;
                case UnderlineStyle.Single:
                case UnderlineStyle.None: // hyperlink fallback
                    batch.AddSolidQuad(x, y, lineW, w, lineColor);
                    break;
                default: // Curl/Dotted/Dashed never reach here (row fallback); draw single as a best effort
                    batch.AddSolidQuad(x, y, lineW, w, lineColor);
                    break;
            }
        }

        if (cc.Strikethrough)
        {
            float y = SnapDip(baseline - (fm.Ascent * -0.3f));
            batch.AddSolidQuad(x, y, lineW, w, lineColor);
        }
        if (cc.Overline)
        {
            float y = SnapDip(baseline + fm.Ascent * 1.05f);
            batch.AddSolidQuad(x, y, lineW, w, lineColor);
        }
    }

    private void DrawCellDecorations(
        SKCanvas canvas,
        SKFontMetrics fm,
        SKColor fgColor,
        bool hasHyperlink,
        float x,
        float baseline,
        float lineW,
        in CellClass cc)
    {
        var style = cc.CellUnderlineStyle;
        if (style == UnderlineStyle.None && !cc.Strikethrough && !cc.Overline && !hasHyperlink)
            return;

        SKColor lineColor = hasHyperlink ? HyperlinkUnderlineColor
            : (cc.UnderlineColorArgb != 0) ? new SKColor(cc.UnderlineColorArgb) : fgColor;
        _linePaint.Color = lineColor;

        // Underline variants.
        if (style != UnderlineStyle.None || hasHyperlink)
        {
            float y = SnapDip(baseline + fm.Descent * 0.5f);
            float w = Math.Max(1f / Math.Max(0.1f, DeviceScale), _linePaint.StrokeWidth);

            switch (style)
            {
                case UnderlineStyle.Curl:
                {
                    // Pre-existing curl path: SKPathBuilder would require
                    // restructuring this working code for no functional gain.
#pragma warning disable CS0618
                    using var path = new SKPath();
                    float amp = w * 2.5f;
                    float period = Math.Max(4f, w * 6f);
                    int steps = Math.Max(2, (int)(lineW / 2f));
                    path.MoveTo(x, y);
                    for (int i = 1; i <= steps; i++)
                    {
                        float t = i / (float)steps;
                        float px = x + lineW * t;
                        float py = SnapDip(y + amp * (float)Math.Sin(t * Math.PI * 2 * (lineW / period)));
                        path.LineTo(px, py);
                    }
                    canvas.DrawPath(path, _linePaint);
                    break;
                }
#pragma warning restore CS0618
                case UnderlineStyle.Dotted:
                {
                    float dotSpacing = Math.Max(3f, w * 4f);
                    float dotR = Math.Max(1f / Math.Max(0.1f, DeviceScale), SnapDip(w * 0.6f));
                    for (float px = x; px < x + lineW; px += dotSpacing)
                    {
                        canvas.DrawCircle(px, y, dotR, _linePaint);
                    }
                    break;
                }
                case UnderlineStyle.Dashed:
                {
                    float dashLen = Math.Max(4f, w * 6f);
                    float gapLen = Math.Max(2f, w * 3f);
                    for (float px = x; px < x + lineW; )
                    {
                        float end = Math.Min(px + dashLen, x + lineW);
                        canvas.DrawLine(px, y, end, y, _linePaint);
                        px = end + gapLen;
                    }
                    break;
                }
                default: // Single, Double, or hyperlink fallback
                {
                    canvas.DrawLine(x, y, x + lineW, y, _linePaint);
                    if (style == UnderlineStyle.Double)
                    {
                        float y2 = SnapDip(baseline + fm.Descent * 0.8f);
                        canvas.DrawLine(x, y2, x + lineW, y2, _linePaint);
                    }
                    break;
                }
            }
        }

        if (cc.Strikethrough)
        {
            float y = SnapDip(baseline - (fm.Ascent * -0.3f));
            canvas.DrawLine(x, y, x + lineW, y, _linePaint);
        }
        if (cc.Overline)
        {
            float y = SnapDip(baseline + fm.Ascent * 1.05f);
            canvas.DrawLine(x, y, x + lineW, y, _linePaint);
        }
    }

    private SKTypeface GetTypefaceForIndex(int index)
    {
        if (_fallbackTypefaces != null && index >= 0 && index < _fallbackTypefaces.Count)
            return _fallbackTypefaces[index];
        return _glyphFont.Typeface;
    }

    private static SKColor ToSkColor(uint argb)
    {
        // ARGB uint to SKColor - note SKColor takes RGBA in little-endian order
        return new SKColor((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24));
    }

    // ============================================================
    // SUPPORT
    // ============================================================

    // Legacy: EnsureCellInfos removed. Use EnsureCellClasses instead.
    private void EnsureCellClasses(int columns)
    {
        if (_cellClasses.Length < columns)
            _cellClasses = new CellClass[columns];
    }

    /// <summary>
    /// Points <see cref="_cellClasses"/> at the row's classification,
    /// classifying directly into the per-row cache array when the identity
    /// generation changed. Zero-copy on cache hits: no per-cell bounds checks
    /// and no Array.Copy — just a reference swap.
    /// </summary>
    private void EnsureRowClassified(IRenderSource buffer, int row)
    {
        EnsureCellClasses(buffer.Columns);
        EnsureRowClassCache(buffer);

        ulong gen = buffer.GetRowGeneration(row);
        var cached = _rowClassCache![row];
        if (cached != null && cached.Length >= buffer.Columns && _rowClassGen![row] == gen)
        {
            _cellClasses = cached;
            return;
        }

        if (cached == null || cached.Length < buffer.Columns)
            cached = _rowClassCache[row] = new CellClass[buffer.Columns];
        _cellClasses = cached;
        ClassifyRowCells(buffer, row);
        _rowClassGen![row] = gen;
    }

    private void EnsureRowClassCache(IRenderSource buffer)
    {
        if (_rowClassCache != null && _rowClassCache.Length >= buffer.Rows) return;
        int rows = Math.Max(buffer.Rows, 1);
        var cache = new CellClass[rows][];
        var gens = new ulong[rows];
        if (_rowClassCache != null)
            Array.Copy(_rowClassCache, cache, Math.Min(_rowClassCache.Length, rows));
        if (_rowClassGen != null)
            Array.Copy(_rowClassGen, gens, Math.Min(_rowClassGen.Length, rows));
        // Fresh slots keep null row arrays so they always miss (re-classify).
        _rowClassCache = cache;
        _rowClassGen = gens;
    }

    private static unsafe int GetFirstRune(string? s)
    {
        if (string.IsNullOrEmpty(s)) return -1;
        fixed (char* ptr = s)
        {
            char c0 = ptr[0];
            if (char.IsHighSurrogate(c0) && s.Length > 1)
            {
                char c1 = ptr[1];
                if (char.IsLowSurrogate(c1))
                {
                    return char.ConvertToUtf32(c0, c1);
                }
            }
            return c0;
        }
    }

    private struct CellClass
    {
        public bool IsContinuation;
        public int Width;
        public bool HasBg;
        public SKColor Bg;
        public bool HasFg;
        public SKColor Fg;

        public string Grapheme;
        public int FirstRune;
        public bool IsSeparatorGlyph;
        public bool ShouldDrawGlyph;
        public CellHot RawCell;
        public ushort HyperlinkId;

        // Resolved style fields
        public bool Bold;
        public bool Faint;
        public bool Underline;
        public bool DoubleUnderline;
        public UnderlineStyle CellUnderlineStyle;
        public bool Strikethrough;
        public bool Overline;
        public bool Invisible;
        public bool SlowBlink;
        public uint UnderlineColorArgb;

        // Font fallback: index into FallbackTypefaces (0 = primary)
        public int TypefaceIndex;
    }

    private void ClassifyRowCells(IRenderSource buffer, int row)
    {
        EnsureCellClasses(buffer.Columns);

        // Row-span reads: one bounds check per row, no per-cell GetCell
        // (which re-checks bounds and runs the mutating continuation-repair).
        var cells = buffer.GetRowCells(row);
        var colds = buffer.GetRowColdCells(row);
        int cols = Math.Min(buffer.Columns, cells.Length);

        for (int col = 0; col < cols; col++)
        {
            var cell = cells[col];
            var cold = colds[col];

            var cc = new CellClass();
            cc.RawCell = cell;
            cc.IsContinuation = cell.IsContinuation;
            cc.Width = Math.Max(1, (int)cell.Width);

            
            var style = buffer.GetStyle(cell.StyleId);
            cc.HasBg = style.Background.Argb != 0;
            cc.Bg = style.Background.Argb != 0 ? ToSkColor(style.Background.Argb) : default;
            if (cc.HasBg && cc.Bg.Alpha == 0) cc.Bg = cc.Bg.WithAlpha(255);

            
            cc.HasFg = style.Foreground.Argb != 0;
            cc.Fg = style.Foreground.Argb != 0 ? ToSkColor(style.Foreground.Argb) : default;

            cc.Bold = style.Bold;
            cc.Faint = style.Faint;
            cc.Underline = style.Underline;
            cc.DoubleUnderline = style.DoubleUnderline;
            cc.CellUnderlineStyle = style.UnderlineStyle;
            cc.Strikethrough = style.Strikethrough;
            cc.Overline = style.Overline;
            cc.Invisible = style.Invisible;
            cc.SlowBlink = style.SlowBlink;
            cc.UnderlineColorArgb = style.UnderlineColor.Argb;

            cc.Grapheme = GraphemeHelper.Resolve(cell.Rune, cold.GraphemeIndex) ?? string.Empty;
            cc.FirstRune = GetFirstRune(cc.Grapheme);
            cc.IsSeparatorGlyph = cc.FirstRune != -1 && IsLikelySeparatorRune(cc.FirstRune);

            cc.ShouldDrawGlyph = !cc.IsContinuation && !cell.IsEmpty && !(cc.IsSeparatorGlyph && !cc.HasBg);

            cc.HyperlinkId = cold.HyperlinkId;

            cc.TypefaceIndex = ResolveCellTypeface(cc);

            _cellClasses[col] = cc;
        }
    }

    private int ResolveCellTypeface(in CellClass cc)
    {
        if (!cc.ShouldDrawGlyph || cc.IsContinuation || cc.FirstRune <= 0)
            return 0;

        if (_fallbackTypefaces == null || _fallbackTypefaces.Count <= 1)
            return 0;

        // Check primary font (index 0)
        #pragma warning disable CS0618 // Pre-existing API; SKFont.ContainsGlyph requires allocation in hot path
        if (_fallbackTypefaces[0].ContainsGlyph(cc.FirstRune))
#pragma warning restore CS0618
            return 0;

        // Walk the fallback chain (monospace fonts first, then emoji)
        for (int i = 1; i < _fallbackTypefaces.Count; i++)
        {
            #pragma warning disable CS0618
            if (_fallbackTypefaces[i].ContainsGlyph(cc.FirstRune))
#pragma warning restore CS0618
                return i;
        }

        return 0;
    }

    // Whitelist of known separator runes (common Powerline / Nerd Font codepoints).
    // This list can be extended if you observe other separators in themes.
    private static readonly HashSet<int> SeparatorRuneWhitelist = new()
    {
        0xE0B0, 0xE0B1, 0xE0B2, 0xE0B3, 0xE0B4, 0xE0B5, 0xE0B6, 0xE0B7,
        0xE0B8, 0xE0B9, 0xE0BA, 0xE0BB, 0xE0BC, 0xE0BD, 0xE0BE, 0xE0BF
    };

    private static bool IsLikelySeparatorRune(int value)
    {
        // 1) Whitelist exact runes we've observed and want to treat as separators.
        if (SeparatorRuneWhitelist.Contains(value)) return true;

        // 2) Fallback: common Powerline PUA block (conservative range).
        // If you prefer a broader PUA detection, expand this range or
        // add specific codepoints to the whitelist above.
        return (value >= 0xE0A0 && value <= 0xE0FF);
    }

    // Optional helper to extend the whitelist at runtime (useful for tests).
    public static void AddSeparatorRune(int rune) => SeparatorRuneWhitelist.Add(rune);

    private static bool IsPixelGridRune(int value)
    {
        // Keep block/box glyphs locked to hard pixel edges to avoid visible
        // inter-row seams in dense ASCII art while preserving AA for normal text.
        return (value >= 0x2500 && value <= 0x259F)
            || (value >= 0x1FB00 && value <= 0x1FBFF);
    }

    private static bool IsBlockElementRune(int value)
        => value >= 0x2580 && value <= 0x259F;

    private static bool IsBoxDrawingRune(int value)
        => value >= 0x2500 && value <= 0x257F;

    private static bool IsGeometryRenderedRune(int value)
        => IsBlockElementRune(value) || IsBoxDrawingRune(value);

    private static int BuildGeometryRects(int rune, Span<SKRect> rects, float left, float top, float right, float bottom)
    {
        if (IsBlockElementRune(rune))
        {
            return BuildBlockElementRects(rune, rects, left, top, right, bottom);
        }

        if (IsBoxDrawingRune(rune))
        {
            return BuildBoxDrawingRects(rune, rects, left, top, right, bottom);
        }

        return 0;
    }

    private static int BuildBlockElementRects(int rune, Span<SKRect> rects, float left, float top, float right, float bottom)
    {
        float width = right - left;
        float height = bottom - top;
        if (width <= 0f || height <= 0f) return 0;

        static void AddRect(Span<SKRect> rects, ref int count, float l, float t, float r, float b)
        {
            if (r <= l || b <= t) return;
            rects[count++] = SKRect.Create(l, t, r - l, b - t);
        }

        static void AddHorizontalSlice(Span<SKRect> rects, ref int count, float left, float top, float right, float bottom, int eighths)
        {
            float h = (bottom - top) * (eighths / 8f);
            AddRect(rects, ref count, left, bottom - h, right, bottom);
        }

        static void AddVerticalSlice(Span<SKRect> rects, ref int count, float left, float top, float right, float bottom, int eighths)
        {
            float w = (right - left) * (eighths / 8f);
            AddRect(rects, ref count, left, top, left + w, bottom);
        }

        float midX = left + width * 0.5f;
        float midY = top + height * 0.5f;
        float x7_8 = left + width * 0.875f;
        float y1_8 = top + height * 0.125f;

        int count = 0;
        switch (rune)
        {
            case 0x2580: // UPPER HALF BLOCK
                AddRect(rects, ref count, left, top, right, midY);
                break;
            case 0x2581:
            case 0x2582:
            case 0x2583:
            case 0x2584:
            case 0x2585:
            case 0x2586:
            case 0x2587:
                AddHorizontalSlice(rects, ref count, left, top, right, bottom, rune - 0x2580);
                break;
            case 0x2588: // FULL BLOCK
                AddRect(rects, ref count, left, top, right, bottom);
                break;
            case 0x2589:
            case 0x258A:
            case 0x258B:
            case 0x258C:
            case 0x258D:
            case 0x258E:
            case 0x258F:
                AddVerticalSlice(rects, ref count, left, top, right, bottom, 0x2590 - rune);
                break;
            case 0x2590: // RIGHT HALF BLOCK
                AddRect(rects, ref count, midX, top, right, bottom);
                break;
            case 0x2594: // UPPER ONE EIGHTH
                AddRect(rects, ref count, left, top, right, y1_8);
                break;
            case 0x2595: // RIGHT ONE EIGHTH
                AddRect(rects, ref count, x7_8, top, right, bottom);
                break;
            case 0x2596: // QUADRANT LOWER LEFT
                AddRect(rects, ref count, left, midY, midX, bottom);
                break;
            case 0x2597: // QUADRANT LOWER RIGHT
                AddRect(rects, ref count, midX, midY, right, bottom);
                break;
            case 0x2598: // QUADRANT UPPER LEFT
                AddRect(rects, ref count, left, top, midX, midY);
                break;
            case 0x2599: // QUADRANT UPPER LEFT AND LOWER LEFT AND LOWER RIGHT
                AddRect(rects, ref count, left, top, midX, midY);
                AddRect(rects, ref count, left, midY, right, bottom);
                break;
            case 0x259A: // QUADRANT UPPER LEFT AND LOWER RIGHT
                AddRect(rects, ref count, left, top, midX, midY);
                AddRect(rects, ref count, midX, midY, right, bottom);
                break;
            case 0x259B: // QUADRANT UPPER LEFT AND UPPER RIGHT AND LOWER LEFT
                AddRect(rects, ref count, left, top, right, midY);
                AddRect(rects, ref count, left, midY, midX, bottom);
                break;
            case 0x259C: // QUADRANT UPPER LEFT AND UPPER RIGHT AND LOWER RIGHT
                AddRect(rects, ref count, left, top, right, midY);
                AddRect(rects, ref count, midX, midY, right, bottom);
                break;
            case 0x259D: // QUADRANT UPPER RIGHT
                AddRect(rects, ref count, midX, top, right, midY);
                break;
            case 0x259E: // QUADRANT UPPER RIGHT AND LOWER LEFT
                AddRect(rects, ref count, midX, top, right, midY);
                AddRect(rects, ref count, left, midY, midX, bottom);
                break;
            case 0x259F: // QUADRANT UPPER RIGHT AND LOWER LEFT AND LOWER RIGHT
                AddRect(rects, ref count, midX, top, right, midY);
                AddRect(rects, ref count, left, midY, right, bottom);
                break;
        }

        return count;
    }

    private static int BuildBoxDrawingRects(int rune, Span<SKRect> rects, float left, float top, float right, float bottom)
    {
        float width = right - left;
        float height = bottom - top;
        if (width <= 0f || height <= 0f) return 0;

        static void AddRect(Span<SKRect> rects, ref int count, float l, float t, float r, float b)
        {
            if (count >= rects.Length) return;
            if (r <= l || b <= t) return;
            rects[count++] = SKRect.Create(l, t, r - l, b - t);
        }

        static float ClampBand(float band, float limit)
            => MathF.Max(1f, MathF.Min(MathF.Round(band), MathF.Max(1f, limit)));

        float minDim = MathF.Min(width, height);
        float singleThickness = ClampBand(minDim / 8f, minDim);
        float heavyThickness = ClampBand(singleThickness * 1.75f, minDim);
        float doubleThickness = ClampBand(singleThickness * 0.6f, minDim);
        float centerX = left + width * 0.5f;
        float centerY = top + height * 0.5f;

        static void AddHorizontalBand(Span<SKRect> rects, ref int count, float left, float right, float centerY, float thickness)
        {
            float t = MathF.Max(1f, MathF.Round(thickness));
            float bandTop = MathF.Round(centerY - t * 0.5f);
            AddRect(rects, ref count, left, bandTop, right, bandTop + t);
        }

        static void AddVerticalBand(Span<SKRect> rects, ref int count, float top, float bottom, float centerX, float thickness)
        {
            float t = MathF.Max(1f, MathF.Round(thickness));
            float bandLeft = MathF.Round(centerX - t * 0.5f);
            AddRect(rects, ref count, bandLeft, top, bandLeft + t, bottom);
        }

        static void AddDoubleHorizontalBand(Span<SKRect> rects, ref int count, float left, float right, float centerY, float thickness)
        {
            float t = MathF.Max(1f, MathF.Round(thickness));
            float gap = MathF.Max(1f, t);
            AddHorizontalBand(rects, ref count, left, right, centerY - (gap + t) * 0.5f, t);
            AddHorizontalBand(rects, ref count, left, right, centerY + (gap + t) * 0.5f, t);
        }

        static void AddDoubleVerticalBand(Span<SKRect> rects, ref int count, float top, float bottom, float centerX, float thickness)
        {
            float t = MathF.Max(1f, MathF.Round(thickness));
            float gap = MathF.Max(1f, t);
            AddVerticalBand(rects, ref count, top, bottom, centerX - (gap + t) * 0.5f, t);
            AddVerticalBand(rects, ref count, top, bottom, centerX + (gap + t) * 0.5f, t);
        }

        static void AddHorizontalHalfBand(Span<SKRect> rects, ref int count, float left, float right, float centerY, float thickness, bool toRight)
        {
            float mid = MathF.Round((left + right) * 0.5f);
            if (toRight)
                AddHorizontalBand(rects, ref count, mid, right, centerY, thickness);
            else
                AddHorizontalBand(rects, ref count, left, mid, centerY, thickness);
        }

        static void AddVerticalHalfBand(Span<SKRect> rects, ref int count, float top, float bottom, float centerX, float thickness, bool toBottom)
        {
            float mid = MathF.Round((top + bottom) * 0.5f);
            if (toBottom)
                AddVerticalBand(rects, ref count, mid, bottom, centerX, thickness);
            else
                AddVerticalBand(rects, ref count, top, mid, centerX, thickness);
        }

        static void AddDoubleHorizontalHalfBand(Span<SKRect> rects, ref int count, float left, float right, float centerY, float thickness, bool toRight)
        {
            float mid = MathF.Round((left + right) * 0.5f);
            if (toRight)
                AddDoubleHorizontalBand(rects, ref count, mid, right, centerY, thickness);
            else
                AddDoubleHorizontalBand(rects, ref count, left, mid, centerY, thickness);
        }

        static void AddDoubleVerticalHalfBand(Span<SKRect> rects, ref int count, float top, float bottom, float centerX, float thickness, bool toBottom)
        {
            float mid = MathF.Round((top + bottom) * 0.5f);
            if (toBottom)
                AddDoubleVerticalBand(rects, ref count, mid, bottom, centerX, thickness);
            else
                AddDoubleVerticalBand(rects, ref count, top, mid, centerX, thickness);
        }

        int count = 0;

        switch (rune)
        {
            // Single-line horizontal runs.
            case 0x2500:
            case 0x2504:
            case 0x2508:
            case 0x254C:
            case 0x2574:
                AddHorizontalBand(rects, ref count, left, right, centerY, singleThickness);
                break;
            case 0x2501:
            case 0x2505:
            case 0x2509:
            case 0x254D:
                AddHorizontalBand(rects, ref count, left, right, centerY, heavyThickness);
                break;

            // Single-line vertical runs.
            case 0x2502:
            case 0x2506:
            case 0x250A:
            case 0x254E:
            case 0x2575:
                AddVerticalBand(rects, ref count, top, bottom, centerX, singleThickness);
                break;
            case 0x2503:
            case 0x2507:
            case 0x250B:
            case 0x254F:
                AddVerticalBand(rects, ref count, top, bottom, centerX, heavyThickness);
                break;

            // Single-line corners and tees.
            case 0x250C: // ┌
                AddHorizontalHalfBand(rects, ref count, left, right, centerY, singleThickness, toRight: true);
                AddVerticalHalfBand(rects, ref count, top, bottom, centerX, singleThickness, toBottom: true);
                break;
            case 0x2510: // ┐
                AddHorizontalHalfBand(rects, ref count, left, right, centerY, singleThickness, toRight: false);
                AddVerticalHalfBand(rects, ref count, top, bottom, centerX, singleThickness, toBottom: true);
                break;
            case 0x2514: // └
                AddHorizontalHalfBand(rects, ref count, left, right, centerY, singleThickness, toRight: true);
                AddVerticalHalfBand(rects, ref count, top, bottom, centerX, singleThickness, toBottom: false);
                break;
            case 0x2518: // ┘
                AddHorizontalHalfBand(rects, ref count, left, right, centerY, singleThickness, toRight: false);
                AddVerticalHalfBand(rects, ref count, top, bottom, centerX, singleThickness, toBottom: false);
                break;
            case 0x251C: // ├
                AddHorizontalHalfBand(rects, ref count, left, right, centerY, singleThickness, toRight: true);
                AddVerticalBand(rects, ref count, top, bottom, centerX, singleThickness);
                break;
            case 0x2524: // ┤
                AddHorizontalHalfBand(rects, ref count, left, right, centerY, singleThickness, toRight: false);
                AddVerticalBand(rects, ref count, top, bottom, centerX, singleThickness);
                break;
            case 0x252C: // ┬
                AddHorizontalBand(rects, ref count, left, right, centerY, singleThickness);
                AddVerticalHalfBand(rects, ref count, top, bottom, centerX, singleThickness, toBottom: true);
                break;
            case 0x2534: // ┴
                AddHorizontalBand(rects, ref count, left, right, centerY, singleThickness);
                AddVerticalHalfBand(rects, ref count, top, bottom, centerX, singleThickness, toBottom: false);
                break;
            case 0x253C: // ┼
                AddHorizontalBand(rects, ref count, left, right, centerY, singleThickness);
                AddVerticalBand(rects, ref count, top, bottom, centerX, singleThickness);
                break;

            // Double-line corners and tees (common in Neovim/UI banners).
            case 0x2554: // ╔
                AddDoubleHorizontalHalfBand(rects, ref count, left, right, centerY, doubleThickness, toRight: true);
                AddDoubleVerticalHalfBand(rects, ref count, top, bottom, centerX, doubleThickness, toBottom: true);
                break;
            case 0x2557: // ╗
                AddDoubleHorizontalHalfBand(rects, ref count, left, right, centerY, doubleThickness, toRight: false);
                AddDoubleVerticalHalfBand(rects, ref count, top, bottom, centerX, doubleThickness, toBottom: true);
                break;
            case 0x255A: // ╚
                AddDoubleHorizontalHalfBand(rects, ref count, left, right, centerY, doubleThickness, toRight: true);
                AddDoubleVerticalHalfBand(rects, ref count, top, bottom, centerX, doubleThickness, toBottom: false);
                break;
            case 0x255D: // ╝
                AddDoubleHorizontalHalfBand(rects, ref count, left, right, centerY, doubleThickness, toRight: false);
                AddDoubleVerticalHalfBand(rects, ref count, top, bottom, centerX, doubleThickness, toBottom: false);
                break;
            case 0x2560: // ╠
                AddDoubleHorizontalHalfBand(rects, ref count, left, right, centerY, doubleThickness, toRight: true);
                AddDoubleVerticalBand(rects, ref count, top, bottom, centerX, doubleThickness);
                break;
            case 0x2563: // ╣
                AddDoubleHorizontalHalfBand(rects, ref count, left, right, centerY, doubleThickness, toRight: false);
                AddDoubleVerticalBand(rects, ref count, top, bottom, centerX, doubleThickness);
                break;
            case 0x2566: // ╦
                AddDoubleHorizontalBand(rects, ref count, left, right, centerY, doubleThickness);
                AddDoubleVerticalHalfBand(rects, ref count, top, bottom, centerX, doubleThickness, toBottom: true);
                break;
            case 0x2569: // ╩
                AddDoubleHorizontalBand(rects, ref count, left, right, centerY, doubleThickness);
                AddDoubleVerticalHalfBand(rects, ref count, top, bottom, centerX, doubleThickness, toBottom: false);
                break;
            case 0x256C: // ╬
                AddDoubleHorizontalBand(rects, ref count, left, right, centerY, doubleThickness);
                AddDoubleVerticalBand(rects, ref count, top, bottom, centerX, doubleThickness);
                break;

            // Double-line horizontal and vertical segments.
            case 0x2550:
            case 0x2576:
                AddDoubleHorizontalBand(rects, ref count, left, right, centerY, doubleThickness);
                break;
            case 0x2551:
            case 0x2577:
                AddDoubleVerticalBand(rects, ref count, top, bottom, centerX, doubleThickness);
                break;
        }

        return count;
    }

    private void SyncGlyphPaint(SKPaint source, SKFont sourceFont)
    {
        _glyphFont.Typeface = sourceFont.Typeface;
        _glyphFont.Size = sourceFont.Size;
        _glyphFont.Subpixel = sourceFont.Subpixel;
        _glyphFont.Edging = sourceFont.Edging;
        _glyphFont.Hinting = sourceFont.Hinting;
        _glyphPaint.IsAntialias = source.IsAntialias;
    }

    private static unsafe bool TryParseHexColor(string? hex, out SKColor color)
    {
        color = default;
        if (string.IsNullOrEmpty(hex) || hex.Length < 7 || hex[0] != '#')
            return false;

        fixed (char* ptr = hex)
        {
            int r = ParseHexByte(ptr[1], ptr[2]);
            if (r < 0) return false;
            
            int g = ParseHexByte(ptr[3], ptr[4]);
            if (g < 0) return false;
            
            int b = ParseHexByte(ptr[5], ptr[6]);
            if (b < 0) return false;
            
            color = new SKColor((byte)r, (byte)g, (byte)b);
            return true;
        }
    }

    private static int ParseHexByte(char high, char low)
    {
        int h = ParseHexChar(high);
        if (h < 0) return -1;
        int l = ParseHexChar(low);
        if (l < 0) return -1;
        return (h << 4) | l;
    }

    private static int ParseHexChar(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    }

    private static SKColor DarkenColor(SKColor c)
        => new(
            (byte)Math.Max(0, c.Red - 32),
            (byte)Math.Max(0, c.Green - 32),
            (byte)Math.Max(0, c.Blue - 32),
            c.Alpha);

    // ============================================================
    // DATA TYPES
    // ============================================================

    private readonly record struct Region(int X0, int X1, int TopRow, int BottomRow, SKColor Color);
    private readonly record struct RegionKey(int X0, int X1, SKColor Color);

    private sealed class ActiveRegion
    {
        public int X0, X1;
        public int TopRow, BottomRow;
        public SKColor Color;
        public int LastTouchedGen;
    }

    // Legacy `CellRenderInfo` removed — `CellClass` is now the single
    // source of truth for per-cell decisions.
}
