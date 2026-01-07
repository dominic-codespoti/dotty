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

    public TerminalFrameComposer()
    {
    }

    public SKImage Compose(TerminalBuffer buffer, GlyphAtlas atlas, SKPaint paint, float cellW, float cellH)
    {
        if (buffer == null) throw new ArgumentNullException(nameof(buffer));
        if (atlas == null) throw new ArgumentNullException(nameof(atlas));
        if (paint == null) throw new ArgumentNullException(nameof(paint));

        int w = Math.Max(1, (int)Math.Ceiling(buffer.Columns * cellW));
        int h = Math.Max(1, (int)Math.Ceiling(buffer.Rows * cellH));

        EnsureBacking(w, h);

        // Clear backing
        _canvas!.Clear(SKColors.Transparent);

        // Draw each cell using atlas entries
        for (int r = 0; r < buffer.Rows; r++)
        {
            for (int c = 0; c < buffer.Columns; c++)
            {
                var cell = buffer.GetCell(r, c);
                if (cell.IsContinuation || cell.IsEmpty) continue;

                var fg = cell.Foreground?.ToString();
                var key = new GlyphKey(cell.Grapheme ?? string.Empty, fg, cell.Bold);
                if (atlas.TryGetGlyph(key, out var info))
                {
                    var src = new SKRectI(info.X, info.Y, info.X + info.Width, info.Y + info.Height);

                    // Align by baseline so glyphs aren't vertically clipped.
                    var fm = paint.FontMetrics;
                    float baselineDest = r * cellH + cellH - Math.Abs(fm.Descent);
                    float destY = baselineDest - info.BaselineOffset;
                    float destX = c * cellW; // left-align inside the cell

                    var dst = new SKRect(destX, destY, destX + info.Width, destY + info.Height);
                    _canvas.DrawBitmap(atlas.AtlasBitmap, src, dst);
                }
                else
                {
                    // Fallback: draw directly with paint if atlas entry missing
                    var text = cell.Grapheme ?? string.Empty;
                    var fm = paint.FontMetrics;
                    float baselineOffset = Math.Abs(fm.Descent);
                    float x = c * cellW;
                    float y = r * cellH + cellH - baselineOffset;
                    _canvas.DrawText(text, x, y, paint);
                }
            }
        }

        // Snapshot an image to return
        return SKImage.FromBitmap(_backing!);
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

    public void Dispose()
    {
        _canvas?.Dispose();
        _backing?.Dispose();
    }
}
