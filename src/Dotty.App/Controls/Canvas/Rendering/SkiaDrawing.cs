using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Dotty.App.Controls;
using Dotty.App.Services;
using Dotty.Terminal.Adapter;
using SkiaSharp;

namespace Dotty.App.Controls.Canvas;

public sealed class SkiaDrawing : ICustomDrawOperation
{
    private readonly TerminalCanvas _owner;
    private readonly TerminalBuffer _buffer;
    private readonly float _cellW;
    private readonly float _cellH;
    private readonly Dotty.App.Rendering.TerminalFrameComposer? _composer;
    private readonly Thickness _padding;
    private readonly Rect _bounds;

    public SkiaDrawing(TerminalCanvas owner, TerminalBuffer buffer, float cellW, float cellH, Dotty.App.Rendering.TerminalFrameComposer? composer = null, Thickness padding = default)
    {
        _owner = owner;
        _buffer = buffer;
        _cellW = cellW;
        _cellH = cellH;
        _composer = composer;
        _padding = padding;
        _bounds = new Rect(0, 0, buffer.Columns * cellW + padding.Left + padding.Right, buffer.Rows * cellH + padding.Top + padding.Bottom);
    }

    public Rect Bounds => _bounds;

    public bool HitTest(Point point) => false;

    public void Render(ImmediateDrawingContext context)
    {
        var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>() ?? throw new InvalidOperationException("SkiaSharpApiLeaseFeature not available");
        using var lease = leaseFeature.Lease();
        var canvas = lease.SkCanvas;

        canvas.Save();

        var buffer = _buffer ?? throw new InvalidOperationException("Terminal buffer missing");
        try
        {
            // Mark the buffer for the start of this render so per-render
            // diagnostics (like DumpRowRange) can dedupe output and so
            // we record frame events for investigation.
            try { buffer.MarkRender(); } catch { }

            // Clear the entire Skia surface BEFORE applying any transform so
            // we cover the full control area (avoid relying on Bounds which
            // may not match the actual rendered surface size). Use the default
            // background color with alpha so opacity is respected.
            canvas.Save();
            canvas.ResetMatrix();
            var bgColor = SKColor.Parse(Defaults.DefaultBackground);
            canvas.Clear(bgColor);
            canvas.Restore();

            // Apply Avalonia transform to Skia canvas
            var m = ToSkiaMatrix(context.CurrentTransform);
            canvas.SetMatrix(m);
            if (_padding.Left != 0 || _padding.Top != 0)
            {
                canvas.Translate((float)_padding.Left, (float)_padding.Top);
            }

            var paint = _owner.SkPaint;
            if (paint == null) return;

            // Use the composer to render directly into the leased Skia canvas. The composer
            // maintains persistent per-row bitmaps and only repaints rows whose version changed.
            var composer = _composer;
            if (composer != null)
            {
                composer.RenderTo(canvas, buffer, paint, _cellW, _cellH);
            }
            else
            {
                // Fallback: draw each glyph directly as before
                for (int r = 0; r < buffer.Rows; r++)
                {
                    for (int c = 0; c < buffer.Columns; c++)
                    {
                        var cell = buffer.GetCell(r, c);
                        if (cell.IsContinuation || string.IsNullOrEmpty(cell.Grapheme)) continue;
                        var text = cell.Grapheme!;
                        float x = (float)(c * _cellW);
                        var fm = paint.FontMetrics;
                        float glyphHeight = Math.Abs(fm.Ascent) + Math.Abs(fm.Descent);
                        float baselineOffset = (float)(_cellH * 0.5f) + (glyphHeight * 0.5f) - Math.Abs(fm.Descent);
                        float y = (float)(r * _cellH + baselineOffset);
                        canvas.DrawText(text, x, y, paint);
                    }
                }
            }
            DrawSelectionOverlay(canvas, buffer);
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

    private void DrawSelectionOverlay(SKCanvas canvas, TerminalBuffer buffer)
    {
        var selection = _owner.SelectionRange;
        if (selection.IsEmpty) return;

        // Clamp selection to buffer bounds
        int startRow = Math.Clamp(selection.StartRow, 0, buffer.Rows - 1);
        int endRow = Math.Clamp(selection.EndRow, 0, buffer.Rows - 1);

        int cellPxW = Math.Max(1, (int)MathF.Round(_cellW));
        int cellPxH = Math.Max(1, (int)MathF.Round(_cellH));

        for (int row = startRow; row <= endRow; row++)
        {
            int startCol = row == selection.StartRow ? Math.Clamp(selection.StartColumn, 0, buffer.Columns - 1) : 0;
            int endCol = row == selection.EndRow ? Math.Clamp(selection.EndColumn, 0, buffer.Columns - 1) : buffer.Columns - 1;

            int x = startCol * cellPxW;
            int columns = Math.Max(1, endCol - startCol + 1);
            int width = columns * cellPxW;
            int y = row * cellPxH;

            // Create paint inside loop - this pattern was working
            using var selectionPaint = new SKPaint
            {
                Color = new SKColor(0, 255, 0, 200),
                Style = SKPaintStyle.Fill,
            };
            canvas.DrawRect(new SKRect(x, y, x + width, y + cellPxH), selectionPaint);
        }
    }

    private static SKColor ToSkiaColor(IBrush? brush)
    {
        if (brush is ISolidColorBrush solid)
        {
            var color = solid.Color;
            return new SKColor(color.R, color.G, color.B, color.A);
        }

        return new SKColor(0x33, 0x85, 0xDB, 0x80);
    }

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