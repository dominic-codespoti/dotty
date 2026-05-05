using System;
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
    private readonly SKPaint _linePaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _glyphPaint = new()
    {
        IsAntialias = false,
        FilterQuality = SKFilterQuality.None,
        IsLinearText = false,
        IsAutohinted = false,
        SubpixelText = false,
        LcdRenderText = false
    };

    // --- background synthesis state ---
    private readonly List<Span> _rowSpans = new();
    private readonly Dictionary<RegionKey, ActiveRegion> _activeRegions = new();
    private readonly List<Region> _regions = new();
    private readonly List<RegionKey> _toRemove = new();
    private int _touchGen = 0;
    private readonly Stack<ActiveRegion> _activeRegionPool = new();
    private SynthCell[] _reusableSynthSpan = Array.Empty<SynthCell>();

    // --- cached cell info ---
    // Legacy `_cellInfos` removed in favor of a single `CellClass` pass.
    private CellClass[] _cellClasses = Array.Empty<CellClass>();

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
        _linePaint.Dispose();
        _activeRegionPool.Clear();
    }

    // ============================================================
    // PUBLIC API (unchanged)
    // ============================================================

    public void RenderTo(
        SKCanvas target,
        TerminalBuffer buffer,
        SKPaint paint,
        float cellW,
        float cellH,
        int startRow = 0,
        int? endRow = null)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));
        if (paint == null) throw new ArgumentNullException(nameof(paint));
        if (cellW <= 0 || cellH <= 0) return;

        int safeEndRow = endRow ?? (buffer.Rows - 1);

        // Themeable metrics derived from appearance settings.
        float horizontalPadding = _appearance.HorizontalPadding;
        float verticalPadding = _appearance.GetVerticalPadding(cellH);
        float radius = _appearance.GetRadius(cellH, verticalPadding);

        int computedRows = safeEndRow - startRow + 1;
        float surfaceW = buffer.Columns * cellW;
        float surfaceH = computedRows * cellH;

        // Cell classification will handle per-row sizing/flags.
        EnsureCellClasses(buffer.Columns);

        // ---- background regions ----
        CollectBackgroundRegions(buffer, startRow, safeEndRow);
        DrawBackgroundRegions(target, cellW, cellH, surfaceW, surfaceH, horizontalPadding, verticalPadding, radius);

        // ---- glyphs ----
        SyncGlyphPaint(paint);

        if (GlyphAtlas != null)
            DrawGlyphsWithShader(target, buffer, paint, cellW, cellH, startRow, safeEndRow);
        else
            DrawGlyphs(target, buffer, paint, cellW, cellH, startRow, safeEndRow);
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
    }

    // ============================================================
    // BACKGROUND REGION PIPELINE
    // ============================================================

    private void CollectBackgroundRegions(TerminalBuffer buffer, int startRow, int endRow)
    {
        _regions.Clear();
        _activeRegions.Clear();

        for (int row = startRow; row <= endRow; row++)
        {
            // Classify the row once and let the span builder and glyph
            // renderer consume that single source of truth.
            ClassifyRowCells(buffer, row);
            BuildRowSpans(_cellClasses, row);
            MergeRowSpans(row);
        }

        FlushActiveRegions();

        
    }

    private void BuildRowSpans(CellClass[] rowCells, int row)
    {
        _rowSpans.Clear();

        // Convert classification into synth cells and call the pure builder.
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

        var spans = BackgroundSynth.BuildRowSpans(synth.AsSpan(0, rowCells.Length));
        foreach (var s in spans)
            _rowSpans.Add(new Span(s.X0, s.X1, s.Color));
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
        float surfaceW,
        float surfaceH,
        float horizontalPadding,
        float verticalPadding,
        float baseRadius)
    {
        foreach (var r in _regions)
        {
            float left = r.X0 * cellW - horizontalPadding;
            float right = r.X1 * cellW + horizontalPadding;
            float top = r.TopRow * cellH + verticalPadding;
            float bottom = r.BottomRow * cellH - verticalPadding;

            if (right <= left || bottom <= top) continue;

            var rect = SKRect.Create(left, top, right - left, bottom - top);

            // BuildCapsuleSafe already clamps the capsule radius; keep the
            // requested radius but clamp it to the available rect size.
            var rectRadius = Math.Min(baseRadius, Math.Min(rect.Width, rect.Height) * 0.5f);

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
        _backgroundStroke.StrokeWidth = 2f;
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

    private static readonly string s_glyphSkSL = @"
        uniform shader atlas;
        uniform shader cellData;
        uniform float2 u_cellSize;
        uniform float2 u_gridSize;
        uniform float2 u_atlasSize;

        half4 main(float2 coord) {
            float2 cellCoord = floor(coord / u_cellSize);
            if (cellCoord.x >= u_gridSize.x || cellCoord.y >= u_gridSize.y)
                return half4(0);

            float2 cellUV = (cellCoord + 0.5) / u_gridSize;
            half4 cell = cellData.eval(cellUV);

            float2 inCell = coord - cellCoord * u_cellSize;
            float2 glyphPos = cell.xy * 255.0;
            float2 glyphSize = half2(cell.z * 4.0, cell.w * 4.0);

            half4 fgColor = half4(0.88, 0.88, 0.88, 1.0);
            float flags = 0;
            half4 bgColor = half4(0, 0, 0, 1);

            if (glyphSize.x > 0 && glyphSize.y > 0) {
                float2 offset = (u_cellSize - glyphSize) * 0.5;
                float2 samplePos = inCell - offset;
                if (samplePos.x >= 0 && samplePos.x < glyphSize.x &&
                    samplePos.y >= 0 && samplePos.y < glyphSize.y) {
                    float2 atlasUV = (glyphPos + samplePos) / u_atlasSize;
                    half4 texColor = atlas.eval(atlasUV);
                    half4 blended = half4(mix(bgColor.rgb, fgColor.rgb, texColor.a), 1.0);
                    return blended;
                }
            }
            return bgColor;
        }";

    private void DrawGlyphsWithShader(
        SKCanvas canvas,
        TerminalBuffer buffer,
        SKPaint paint,
        float cellW,
        float cellH,
        int startRow,
        int endRow)
    {
        if (GlyphAtlas == null) { DrawGlyphs(canvas, buffer, paint, cellW, cellH, startRow, endRow); return; }

        int cols = buffer.Columns;
        int rows = endRow - startRow + 1;

        // Get atlas snapshot
        using var atlasImage = GlyphAtlas.CreateSnapshot();
        if (atlasImage == null) { DrawGlyphs(canvas, buffer, paint, cellW, cellH, startRow, endRow); return; }

        // Build cell data texture (RGBA8888, rows×cols pixels)
        // R: atlas U / 255, G: atlas V / 255, B: glyph width / 4, A: glyph height / 4
        byte[] pixels = new byte[cols * rows * 4];

        for (int r = startRow; r <= endRow; r++)
        {
            ClassifyRowCells(buffer, r);
            for (int c = 0; c < cols; c++)
            {
                int pi = (r - startRow) * cols + c;
                var cc = _cellClasses[c];
                if (!cc.ShouldDrawGlyph)
                {
                    pixels[pi * 4 + 2] = 0;
                    pixels[pi * 4 + 3] = 0;
                    continue;
                }

                var key = new GlyphKey(cc.Grapheme, null, cc.Bold);
                if (GlyphAtlas.TryGetGlyph(key, out var info))
                {
                    float uNorm = (float)info.X / atlasImage.Width;
                    float vNorm = (float)info.Y / atlasImage.Height;
                    pixels[pi * 4 + 0] = (byte)Math.Clamp(uNorm * 255, 0, 255);
                    pixels[pi * 4 + 1] = (byte)Math.Clamp(vNorm * 255, 0, 255);
                    pixels[pi * 4 + 2] = (byte)Math.Clamp(info.Width / 4f, 0, 255);
                    pixels[pi * 4 + 3] = (byte)Math.Clamp(info.Height / 4f, 0, 255);
                }
                else
                {
                    pixels[pi * 4 + 2] = 0;
                    pixels[pi * 4 + 3] = 0;
                }
            }
        }

        using var cellBitmap = new SKBitmap(cols, rows, SKColorType.Rgba8888, SKAlphaType.Premul);
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, cellBitmap.GetPixels(), pixels.Length);
        cellBitmap.NotifyPixelsChanged();
        using var cellImage = SKImage.FromBitmap(cellBitmap);

        // Create or reuse the runtime effect
        if (_glyphShaderEffect == null)
        {
            _glyphShaderEffect = SKRuntimeEffect.Create(s_glyphSkSL, out var errors);
            if (_glyphShaderEffect == null)
            {
                DrawGlyphs(canvas, buffer, paint, cellW, cellH, startRow, endRow);
                return;
            }
        }

        // Create uniforms
        var uniforms = new SKRuntimeEffectUniforms(_glyphShaderEffect);
        uniforms["u_cellSize"] = new float[] { cellW, cellH };
        uniforms["u_gridSize"] = new float[] { cols, rows };
        uniforms["u_atlasSize"] = new float[] { atlasImage.Width, atlasImage.Height };

        // Create child shaders: atlas texture + cell data
        var children = new SKRuntimeEffectChildren(_glyphShaderEffect);
        children["atlas"] = atlasImage.ToShader();
        children["cellData"] = cellImage.ToShader();

        using var shader = _glyphShaderEffect.ToShader(false, uniforms, children);
        if (shader == null) { DrawGlyphs(canvas, buffer, paint, cellW, cellH, startRow, endRow); return; }

        using var shaderPaint = new SKPaint { Shader = shader };
        float totalW = cols * cellW;
        float totalH = rows * cellH;
        canvas.DrawRect(0, startRow * cellH, totalW, totalH, shaderPaint);
    }

    // Default hyperlink color (blue) - can be made configurable
    private static readonly SKColor HyperlinkColor = new SKColor(0xFF, 0x64, 0xB0); // Accent blue
    private static readonly SKColor HyperlinkUnderlineColor = new SKColor(0xFF, 0x64, 0xB0);

    public GlyphAtlas? GlyphAtlas { get; set; }
    private SKRuntimeEffect? _glyphShaderEffect;
    private static readonly SKColor s_shaderBgDefault = new SKColor(0x00, 0x00, 0x00);

    private void DrawGlyphs(
        SKCanvas canvas,
        TerminalBuffer buffer,
        SKPaint paint,
        float cellW,
        float cellH,
        int startRow,
        int endRow)
    {
        var fm = _glyphPaint.FontMetrics;
        float baselineOffset = -fm.Ascent;

        var defaultColor = paint.Color;
        long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool isBlinkVisible = (ms % 1000) < 500;
        bool baseAntialias = _glyphPaint.IsAntialias;
        bool baseSubpixel = _glyphPaint.SubpixelText;
        bool baseLcd = _glyphPaint.LcdRenderText;

        _linePaint.StrokeWidth = Math.Max(1f, cellH * 0.05f);
        Span<SKRect> geometryRects = stackalloc SKRect[8];

        for (int row = startRow; row <= endRow; row++)
        {
            ClassifyRowCells(buffer, row);

            float rowTop = row * cellH;
            float baseline = MathF.Round(rowTop + baselineOffset);

            for (int col = 0; col < buffer.Columns; col++)
            {
                var cc = _cellClasses[col];
                if (!cc.ShouldDrawGlyph) continue;

                if (cc.Invisible) continue;
                if (cc.SlowBlink && !isBlinkVisible) continue;

                bool hasHyperlink = cc.HyperlinkId != 0;
                var fgColor = cc.HasFg ? cc.Fg : defaultColor;
                if (hasHyperlink) fgColor = HyperlinkColor;

                bool disableSmoothing = IsPixelGridRune(cc.FirstRune);

                float x = MathF.Round(col * cellW);
                bool renderedAsBlockGeometry = false;

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
                        _glyphPaint.SubpixelText = disableSmoothing ? false : baseSubpixel;
                        _glyphPaint.LcdRenderText = disableSmoothing ? false : baseLcd;
                        _glyphPaint.Style = SKPaintStyle.Fill;
                        _glyphPaint.StrokeWidth = 0f;
                        for (int i = 0; i < rectCount; i++)
                        {
                            var rect = geometryRects[i];
                            if (rect.Width <= 0f || rect.Height <= 0f) continue;
                            canvas.DrawRect(rect, _glyphPaint);
                        }
                        renderedAsBlockGeometry = true;
                    }
                }

                if (!renderedAsBlockGeometry)
                {
                    _glyphPaint.Color = fgColor;
                    bool disableSmoothing2 = IsPixelGridRune(cc.FirstRune);
                    _glyphPaint.IsAntialias = disableSmoothing2 ? false : baseAntialias;
                    _glyphPaint.SubpixelText = disableSmoothing2 ? false : baseSubpixel;
                    _glyphPaint.LcdRenderText = disableSmoothing2 ? false : baseLcd;
                    _glyphPaint.StrokeWidth = cc.Bold ? 0.8f : 0f;
                    _glyphPaint.Style = SKPaintStyle.Fill;
                    canvas.DrawText(cc.Grapheme, x, baseline, _glyphPaint);
                }

                bool hasLine = !renderedAsBlockGeometry
                    && (cc.Underline || cc.DoubleUnderline || cc.Strikethrough || cc.Overline || hasHyperlink);
                if (hasLine)
                {
                    if (hasHyperlink) _linePaint.Color = HyperlinkUnderlineColor;
                    else _linePaint.Color = (cc.UnderlineColorArgb != 0) ? new SKColor(cc.UnderlineColorArgb) : fgColor;

                    float lineW = cellW * cc.Width;
                    if (cc.Underline || hasHyperlink)
                    {
                        float y = baseline + fm.Descent * 0.5f;
                        canvas.DrawLine(x, y, x + lineW, y, _linePaint);
                    }
                    if (cc.DoubleUnderline)
                    {
                        float y1 = baseline + fm.Descent * 0.3f;
                        float y2 = baseline + fm.Descent * 0.8f;
                        canvas.DrawLine(x, y1, x + lineW, y1, _linePaint);
                        canvas.DrawLine(x, y2, x + lineW, y2, _linePaint);
                    }
                    if (cc.Strikethrough)
                    {
                        float y = baseline - (fm.Ascent * -0.3f);
                        canvas.DrawLine(x, y, x + lineW, y, _linePaint);
                    }
                    if (cc.Overline)
                    {
                        float y = baseline + fm.Ascent * 1.05f;
                        canvas.DrawLine(x, y, x + lineW, y, _linePaint);
                    }
                }
            }
        }
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
        public bool Underline;
        public bool DoubleUnderline;
        public bool Strikethrough;
        public bool Overline;
        public bool Invisible;
        public bool SlowBlink;
        public uint UnderlineColorArgb;
    }

    private void ClassifyRowCells(TerminalBuffer buffer, int row)
    {
        EnsureCellClasses(buffer.Columns);

        for (int col = 0; col < buffer.Columns; col++)
        {
            var cell = buffer.GetCell(row, col);
            var cold = buffer.GetColdCell(row, col);

            var cc = new CellClass();
            cc.RawCell = cell;
            cc.IsContinuation = cell.IsContinuation;
            cc.Width = Math.Max(1, (int)cell.Width);

            
            var style = buffer.StyleSet.GetStyle(cell.StyleId);
            cc.HasBg = style.Background.Argb != 0;
            cc.Bg = style.Background.Argb != 0 ? ToSkColor(style.Background.Argb) : default;
            if (cc.HasBg && cc.Bg.Alpha == 0) cc.Bg = cc.Bg.WithAlpha(255);

            
            cc.HasFg = style.Foreground.Argb != 0;
            cc.Fg = style.Foreground.Argb != 0 ? ToSkColor(style.Foreground.Argb) : default;

            cc.Bold = style.Bold;
            cc.Underline = style.Underline;
            cc.DoubleUnderline = style.DoubleUnderline;
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

            _cellClasses[col] = cc;
        }
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

    private void SyncGlyphPaint(SKPaint source)
    {
        _glyphPaint.Typeface = source.Typeface;
        _glyphPaint.TextSize = source.TextSize;
        _glyphPaint.TextEncoding = source.TextEncoding;
        _glyphPaint.TextScaleX = source.TextScaleX;
        _glyphPaint.TextSkewX = source.TextSkewX;
        _glyphPaint.IsAntialias = source.IsAntialias;
        _glyphPaint.IsLinearText = source.IsLinearText;
        _glyphPaint.SubpixelText = source.SubpixelText;
        _glyphPaint.LcdRenderText = source.LcdRenderText;
        _glyphPaint.IsAutohinted = source.IsAutohinted;
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

    // Capsules are drawn with DrawRoundRect and ClipRoundRect now; the
    // SKPath-based helper was removed to avoid SKPath allocations.

    // ============================================================
    // DATA TYPES
    // ============================================================

    private readonly record struct Span(int X0, int X1, SKColor Color);
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
