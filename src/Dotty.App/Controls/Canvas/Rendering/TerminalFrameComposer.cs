using System;
using Dotty.Terminal.Adapter;
using SkiaSharp;

namespace Dotty.App.Rendering;

/// <summary>
/// Composes a full frame into a single offscreen surface/image.
/// Responsibilities:
/// - Owns a reusable backing SKBitmap/Canvas
/// - Given a buffer and glyph atlas, draws all glyphs into the backing
/// - Returns an SKImage that can be blitted in a single DrawImage call
/// This class does not reference Avalonia.
/// </summary>
public class TerminalFrameComposer : IDisposable
{
    private SKBitmap? _backing;
    private SKCanvas? _canvas;
    // (per-row caches allocated by EnsureRowCache)

    public TerminalFrameComposer()
    {
    }

    /// <summary>
    /// Render cached rows into the target canvas. Repaint per-row caches only when
    /// the buffer's row version changes. This method performs no allocations except
    /// when resizing the row caches.
    /// </summary>
    public void RenderTo(SKCanvas target, TerminalBuffer buffer, GlyphAtlas atlas, SKPaint paint, float cellW, float cellH)
    {
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));
        if (atlas == null) throw new ArgumentNullException(nameof(atlas));
        if (paint == null) throw new ArgumentNullException(nameof(paint));
        // Target is the UI-provided canvas; we render per-row cached bitmaps onto it.
        int rowPixelH = Math.Max(1, (int)Math.Ceiling(cellH));
        int rowPixelW = Math.Max(1, (int)Math.Ceiling(buffer.Columns * cellW));

        EnsureRowCache(buffer.Rows, rowPixelW, rowPixelH);

        for (int r = 0; r < buffer.Rows; r++)
        {
            int ver = buffer.GetRowVersion(r);
            if (_rowVersions[r] != ver)
            {
                // Repaint row into its persistent row canvas
                var rc = _rowCanvases[r]!;
                rc.Clear(SKColors.Transparent);
                DrawRow(rc, buffer, atlas, paint, r, cellW, rowPixelH);
                _rowVersions[r] = ver;
            }

            // Blit cached row bitmap into target canvas
            var bmp = _rowBitmaps[r];
            if (bmp != null)
            {
                var dst = new SKRect(0, r * cellH, buffer.Columns * cellW, r * cellH + rowPixelH);
                target.DrawBitmap(bmp, dst);
            }
        }
    }

    private void DrawRow(SKCanvas rowCanvas, TerminalBuffer buffer, GlyphAtlas atlas, SKPaint paint, int r, float cellW, int rowPixelH)
    {
        // Draw each cell in the row into the provided rowCanvas
        for (int c = 0; c < buffer.Columns; c++)
        {
            var cell = buffer.GetCell(r, c);
            if (cell.IsContinuation || cell.IsEmpty) continue;
            var fg = cell.Foreground?.ToString();
            var key = new GlyphKey(cell.Grapheme ?? string.Empty, fg, cell.Bold);

            if (atlas.TryGetGlyph(key, out var info))
            {
                var src = new SKRectI(info.X, info.Y, info.X + info.Width, info.Y + info.Height);
                var fm = paint.FontMetrics;
                float baselineDest = rowPixelH - Math.Abs(fm.Descent);
                float destY = baselineDest - info.BaselineOffset;
                float destX = (float)(c * cellW);
                var dst = new SKRect(destX, destY, destX + info.Width, destY + info.Height);
                rowCanvas.DrawBitmap(atlas.AtlasBitmap, src, dst);
            }
            else
            {
                var text = cell.Grapheme ?? string.Empty;
                var fm = paint.FontMetrics;
                float baselineOffset = Math.Abs(fm.Descent);
                float x = (float)(c * cellW);
                float y = rowPixelH - baselineOffset;
                rowCanvas.DrawText(text, x, y, paint);
            }
        }
    }

    private void EnsureBacking(int width, int height)
    {
        if (_backing != null && _backing.Width >= width && _backing.Height >= height) return;

        _canvas?.Dispose();
        _backing?.Dispose();

        // create backing with at least the requested size
        var w = Math.Max(width, 64);
        var h = Math.Max(height, 64);
        _backing = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        _canvas = new SKCanvas(_backing);
        _canvas.Clear(SKColors.Transparent);
    }

    private SKBitmap?[] _rowBitmaps = Array.Empty<SKBitmap?>();
    private SKCanvas?[] _rowCanvases = Array.Empty<SKCanvas?>();
    private int _rowPixelW = 0;
    private int _rowPixelH = 0;

    private void EnsureRowCache(int rows, int rowPixelW, int rowPixelH)
    {
        if (_rowBitmaps.Length == rows && _rowPixelW == rowPixelW && _rowPixelH == rowPixelH) return;

        // Dispose old
        for (int i = 0; i < _rowBitmaps.Length; i++)
        {
            _rowCanvases[i]?.Dispose();
            _rowBitmaps[i]?.Dispose();
        }

        _rowBitmaps = new SKBitmap?[rows];
        _rowCanvases = new SKCanvas?[rows];
        _rowVersions = new int[rows];

        _rowPixelW = rowPixelW;
        _rowPixelH = rowPixelH;

        for (int i = 0; i < rows; i++)
        {
            _rowBitmaps[i] = new SKBitmap(rowPixelW, rowPixelH, SKColorType.Rgba8888, SKAlphaType.Premul);
            _rowCanvases[i] = new SKCanvas(_rowBitmaps[i]);
            _rowVersions[i] = -1;
        }
    }

    public void Dispose()
    {
        _canvas?.Dispose();
        _backing?.Dispose();
    }
}
