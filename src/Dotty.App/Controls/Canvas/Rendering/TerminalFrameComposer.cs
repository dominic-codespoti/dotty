using System;
using System.Text;
using System.Collections.Generic;
using Dotty.Terminal.Adapter;
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
        FilterQuality = SKFilterQuality.None,
        IsLinearText = true,
        IsAutohinted = true,
        SubpixelText = true,
        LcdRenderText = true
    };

    // --- background synthesis state ---
    private readonly List<Span> _rowSpans = new();
    private readonly Dictionary<RegionKey, ActiveRegion> _activeRegions = new();
    private readonly List<Region> _regions = new();
    private readonly List<RegionKey> _toRemove = new();
    private int _touchGen = 0;

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
    }

    // ============================================================
    // PUBLIC API (unchanged)
    // ============================================================

    public unsafe void RenderTo(
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
        BlitBackgroundRegions(target, buffer, cellW, cellH, startRow, safeEndRow);

        // ---- glyphs ----
        SyncGlyphPaint(paint);

        // We are rendering directly to the target canvas, which typically allows LCD text if opaque, 
        // but default to standard antialiasing constraints if unknown. Let's use target properties if possible,
        // or just enable it aggressively for bleeding edge performance/quality.
        _glyphPaint.LcdRenderText = true;
        _glyphPaint.SubpixelText = true;
        _glyphPaint.IsAntialias = true;

        DrawGlyphs(target, buffer, paint, cellW, cellH, startRow, safeEndRow);
    }

    
    private SKBitmap? _bgBitmap;

    private unsafe void BlitBackgroundRegions(
        SKCanvas target,
        TerminalBuffer buffer,
        float cellW,
        float cellH,
        int startRow,
        int endRow)
    {
        int vRows = buffer.Rows;
        int vCols = buffer.Columns;
        int pixelWidth = (int)Math.Ceiling(vCols * cellW);
        int pixelHeight = (int)Math.Ceiling(vRows * cellH);
        
        if (pixelWidth <= 0 || pixelHeight <= 0) return;

        if (_bgBitmap == null || _bgBitmap.Width < pixelWidth || _bgBitmap.Height < pixelHeight)
        {
            _bgBitmap?.Dispose();
            _bgBitmap = new SKBitmap(new SKImageInfo(pixelWidth, pixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
        }

        // Fast zero-fill
        long bytesLength = _bgBitmap.RowBytes * _bgBitmap.Height;
        System.Runtime.CompilerServices.Unsafe.InitBlock((void*)_bgBitmap.GetPixels(), 0, (uint)bytesLength);

        IntPtr pixels = _bgBitmap.GetPixels();
        uint* ptr = (uint*)pixels;
        int stride = _bgBitmap.RowBytes / 4;
        
        var activeBuffer = buffer.ActiveBuffer; // Screen
        var cells = activeBuffer.Cells;
        var rowMap = activeBuffer.RowMap;

        // Blit active buffer
        fixed (Cell* cellPtr = cells)
        fixed (int* rowPtr = rowMap)
        {
            for (int r = startRow; r <= endRow; r++)
            {
                int actualRow = rowPtr[r];
                int startScreenY = (int)Math.Round(r * cellH);
                int endScreenY = (int)Math.Round((r + 1) * cellH);
                int pyHeight = endScreenY - startScreenY;
                
                for (int c = 0; c < vCols; c++)
                {
                    Cell* cell = cellPtr + (actualRow * vCols + c);
                    uint bg = cell->Background;
                    if (bg == 0) continue;
                    
                    int startScreenX = (int)Math.Round(c * cellW);
                    int endScreenX = (int)Math.Round((c + 1) * cellW);
                    int pxWidth = endScreenX - startScreenX;
                    
                    uint renderColor = (uint)new SKColor(bg).WithAlpha(255);
                    
                    for (int py = 0; py < pyHeight; py++)
                    {
                        uint* dstRow = ptr + ((startScreenY + py) * stride) + startScreenX;
                        for (int px = 0; px < pxWidth; px++)
                        {
                            dstRow[px] = renderColor;
                        }
                    }
                }
            }
        }
        
        target.DrawBitmap(_bgBitmap, 0, 0);
    }

    public void ResetCaches()
    {
        _regions.Clear();
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
        var synth = new SynthCell[rowCells.Length];
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

        var spans = BackgroundSynth.BuildRowSpans(synth);
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
                region = new ActiveRegion
                {
                    X0 = span.X0,
                    X1 = span.X1,
                    TopRow = row,
                    BottomRow = row + 1,
                    Color = span.Color,
                    LastTouchedGen = _touchGen
                };
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
            _activeRegions.Remove(k);
    }

    private void FlushActiveRegions()
    {
        foreach (var r in _activeRegions.Values)
        {
            _regions.Add(new Region(r.X0, r.X1, r.TopRow, r.BottomRow, r.Color));
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

    // ============================================================
    // GLYPH RENDERING
    // ============================================================

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
        float glyphHeight = Math.Abs(fm.Ascent) + Math.Abs(fm.Descent);

        // Center the glyph box vertically inside the cell box (capsule). We
        // compute an offset relative to the top of the row and then add the
        // row origin so we can snap the final baseline to the pixel grid.
        float baselineOffset = (cellH * 0.5f) + (glyphHeight * 0.5f) - Math.Abs(fm.Descent);

        var defaultColor = paint.Color;
        long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool isBlinkVisible = (ms % 1000) < 500;

        using var linePaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            IsAntialias = true,
            StrokeWidth = Math.Max(1f, cellH * 0.05f)
        };

        for (int row = startRow; row <= endRow; row++)
        {
            ClassifyRowCells(buffer, row);

            float baseline = MathF.Round(row * cellH + baselineOffset);

            for (int col = 0; col < buffer.Columns; col++)
            {
                var cc = _cellClasses[col];
                if (!cc.ShouldDrawGlyph) continue;

                var raw = cc.RawCell;
                if (raw.Invisible) continue;
                if (raw.SlowBlink && !isBlinkVisible) continue;

                var fgColor = cc.HasFg ? cc.Fg : defaultColor;
                _glyphPaint.Color = fgColor;

                // Use a stroke-based embolden instead of Skia's FakeBoldText.
                _glyphPaint.StrokeWidth = raw.Bold ? 0.8f : 0f;
                _glyphPaint.Style = SKPaintStyle.Fill;

                // Snap X positions to integer pixels to align glyphs to the grid.
                float x = MathF.Round(col * cellW);
                canvas.DrawText(cc.Grapheme, x, baseline, _glyphPaint);

                bool hasLine = raw.Underline || raw.DoubleUnderline || raw.Strikethrough || raw.Overline;
                if (hasLine)
                {
                    linePaint.Color = ((raw.UnderlineColor != 0)) ? new SKColor(raw.UnderlineColor) : fgColor;
                    float lineW = cellW * cc.Width;
                    
                    if (raw.Underline)
                    {
                        float y = baseline + fm.Descent * 0.5f;
                        canvas.DrawLine(x, y, x + lineW, y, linePaint);
                    }
                    if (raw.DoubleUnderline)
                    {
                        float y1 = baseline + fm.Descent * 0.3f;
                        float y2 = baseline + fm.Descent * 0.8f;
                        canvas.DrawLine(x, y1, x + lineW, y1, linePaint);
                        canvas.DrawLine(x, y2, x + lineW, y2, linePaint);
                    }
                    if (raw.Strikethrough)
                    {
                        float y = baseline - (fm.Ascent * -0.3f);
                        canvas.DrawLine(x, y, x + lineW, y, linePaint);
                    }
                    if (raw.Overline)
                    {
                        float y = baseline + fm.Ascent * 1.05f;
                        canvas.DrawLine(x, y, x + lineW, y, linePaint);
                    }
                }
            }
        }
    }

    private static SKColor ToSkColor(SgrColor sgrColor)
    {
        return SKColor.Parse(sgrColor.Hex);
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
        public Cell RawCell;
    }

    private void ClassifyRowCells(TerminalBuffer buffer, int row)
    {
        EnsureCellClasses(buffer.Columns);

        for (int col = 0; col < buffer.Columns; col++)
        {
            var cell = buffer.GetCell(row, col);

            var cc = new CellClass();
            cc.RawCell = cell;
            cc.IsContinuation = cell.IsContinuation;
            cc.Width = Math.Max(1, (int)cell.Width);

            SKColor bg = default;
            cc.HasBg = cell.Background != 0;
            cc.Bg = cell.Background != 0 ? new SKColor(cell.Background) : default;

            SKColor fg = default;
            cc.HasFg = cell.Foreground != 0;
            cc.Fg = cell.Foreground != 0 ? new SKColor(cell.Foreground) : default;

            cc.Grapheme = char.ConvertFromUtf32((int)cell.Rune);
            cc.FirstRune = GetFirstRune(cc.Grapheme);
            cc.IsSeparatorGlyph = cc.FirstRune != -1 && IsLikelySeparatorRune(cc.FirstRune);

            cc.ShouldDrawGlyph = !cc.IsContinuation && !cell.IsEmpty && !(cc.IsSeparatorGlyph && !cc.HasBg);

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

    private void SyncGlyphPaint(SKPaint source)
    {
        _glyphPaint.Typeface = source.Typeface;
        _glyphPaint.TextSize = source.TextSize;
        _glyphPaint.TextEncoding = source.TextEncoding;
        _glyphPaint.TextScaleX = source.TextScaleX;
        _glyphPaint.TextSkewX = source.TextSkewX;
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
