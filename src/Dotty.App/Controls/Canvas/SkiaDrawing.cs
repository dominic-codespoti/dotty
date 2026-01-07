using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Dotty.App.Controls;
using Dotty.Terminal.Adapter;
using SkiaSharp;

namespace Dotty.App.Controls.Canvas;

public sealed class SkiaDrawing : ICustomDrawOperation
{
    private readonly TerminalCanvas _owner;
    private readonly TerminalBuffer _buffer;
    private readonly float _cellW;
    private readonly float _cellH;
    private readonly Dotty.App.Rendering.GlyphAtlas? _atlas;
    private readonly Dotty.App.Rendering.TerminalFrameComposer? _composer;
    private readonly Rect _bounds;

    public SkiaDrawing(TerminalCanvas owner, TerminalBuffer buffer, float cellW, float cellH, Dotty.App.Rendering.GlyphAtlas? atlas = null, Dotty.App.Rendering.TerminalFrameComposer? composer = null)
    {
        _owner = owner;
        _buffer = buffer;
        _cellW = cellW;
        _cellH = cellH;
        _bounds = new Rect(0, 0, buffer.Columns * cellW, buffer.Rows * cellH);
        _atlas = atlas;
        _composer = composer;
    }

    public Rect Bounds => _bounds;

    public bool HitTest(Point point) => false;

    public void Render(ImmediateDrawingContext context)
    {
        var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>() ?? throw new InvalidOperationException("SkiaSharpApiLeaseFeature not available");
        using var lease = leaseFeature.Lease();
        var canvas = lease.SkCanvas;

        canvas.Save();

        try
        {
            // Apply Avalonia transform to Skia canvas
            var m = ToSkiaMatrix(context.CurrentTransform);
            canvas.SetMatrix(m);

            var paint = _owner.SkPaint;
            if (paint == null) return;

            // If a composer is available, use it to render the full frame offscreen,
            // then blit the resulting image once. This avoids scanline / partial-present issues.
            if (_composer != null && _atlas != null)
            {
                using var img = _composer.Compose(_buffer, _atlas, paint, _cellW, _cellH);
                var dest = new SKRect(0, 0, (float)_bounds.Width, (float)_bounds.Height);
                canvas.DrawImage(img, dest);
            }
            else
            {
                // Fallback: draw each glyph directly as before
                for (int r = 0; r < _buffer.Rows; r++)
                {
                    for (int c = 0; c < _buffer.Columns; c++)
                    {
                        var cell = _buffer.GetCell(r, c);
                        if (cell.IsContinuation || string.IsNullOrEmpty(cell.Grapheme)) continue;
                        var text = cell.Grapheme!;
                        float x = (float)(c * _cellW);
                        // compute baseline y: we want baseline at cell top + cell height - descent
                        var fm = paint.FontMetrics;
                        float baselineOffset = Math.Abs(fm.Descent);
                        float y = (float)(r * _cellH + _cellH - baselineOffset);
                        canvas.DrawText(text, x, y, paint);
                    }
                }
            }
        }
        finally
        {
            canvas.Restore();
        }
    }

    public bool Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);
    public override bool Equals(object? obj) => Equals(obj as ICustomDrawOperation);
    public override int GetHashCode() => _bounds.GetHashCode();
    public void Dispose() { }

    private static SKMatrix ToSkiaMatrix(Matrix matrix)
    {
        return new SKMatrix
        {
            ScaleX = (float)matrix.M11,
            SkewX = (float)matrix.M12,
            TransX = (float)matrix.M31,
            SkewY = (float)matrix.M21,
            ScaleY = (float)matrix.M22,
            TransY = (float)matrix.M32,
            Persp0 = 0,
            Persp1 = 0,
            Persp2 = 1,
        };
    }
}