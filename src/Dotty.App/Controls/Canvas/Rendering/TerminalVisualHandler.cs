using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using Dotty.Terminal.Adapter;
using SkiaSharp;
using Dotty.App.Services;
using Dotty.App.Rendering;

namespace Dotty.App.Controls.Canvas.Rendering;

public record struct TerminalRenderState(
    TerminalBuffer? Buffer,
    float CellWidth,
    float CellHeight,
    TerminalFrameComposer? Composer,
    Thickness Padding,
    TerminalSelectionRange Selection,
    SKPaint? Paint,
    SKColor BgColor,
    double ScrollY,
    double ViewportHeight,
    int ScrollbackCount,
    bool ShowCursor,
    Dotty.App.Controls.TerminalCursorShape CursorShape);

public sealed class TerminalVisualHandler : CompositionCustomVisualHandler
{
    private TerminalRenderState? _state;
    
    public override void OnMessage(object message)
    {
        if (message is TerminalRenderState state)
        {
            _state = state;
            RegisterForNextAnimationFrameUpdate();
            Invalidate();
        }
    }

    public override void OnRender(ImmediateDrawingContext context)
    {
        if (_state == null) return;
        var s = _state.Value;
        if (s.Buffer == null || s.Paint == null) return;

        var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (leaseFeature == null) return;

        using var lease = leaseFeature.Lease();
        var canvas = lease.SkCanvas;

        canvas.Save();

        var buffer = s.Buffer;
        try
        {
            try { buffer.MarkRender(); } catch { }

            canvas.Save();
            canvas.ResetMatrix();
            canvas.Clear(s.BgColor);
            canvas.Restore();

            var m = ToSkiaMatrix(context.CurrentTransform);
            canvas.SetMatrix(m);
            if (s.Padding.Left != 0 || s.Padding.Top != 0)
            {
                canvas.Translate((float)s.Padding.Left, (float)s.Padding.Top);
            }

            // GPU virtual scroll: Shift the canvas UP by the scroll offset so the composer
            // draws things exactly where the camera expects. Wait, the Composer iterates 0..Rows.
            // If we have scrollback, we need the Composer to draw rows from -Scrollback up to Rows.
            // Let's rely on the Composer drawing the visible range.
            int sbCount = s.ScrollbackCount;
            // The scroll offset is in pixels. Y = 0 means the top of the scrollback.
            // The bottom of the scrollback is Y = sbCount * CellHeight.
            // The active buffer is below the scrollback.
            double totalHeight = (sbCount + buffer.Rows) * s.CellHeight;
            // Top of active screen in pixel coords:
            double screenTop = sbCount * s.CellHeight;
            
            // Translate the canvas so that Y=0 corresponds to the start of the entire virtual document.
            // s.ScrollY is the pixel offset the user scrolled down.
            // And TerminalFrameComposer draws row 0 at Y=0. So row -sbCount is drawn at Y = -sbCount * s.CellHeight.
            // We must translate by +sbCount * CellHeight to correct this shift.
            canvas.Translate(0, (float)(sbCount * s.CellHeight - s.ScrollY));

            var composer = s.Composer;
            if (composer != null)
            {
                int startVisibleRow = (int)Math.Floor(s.ScrollY / s.CellHeight) - sbCount;
                int endVisibleRow = (int)Math.Ceiling((s.ScrollY + s.ViewportHeight) / s.CellHeight) - sbCount;
                startVisibleRow = Math.Max(-sbCount, Math.Min(buffer.Rows - 1, startVisibleRow));
                endVisibleRow = Math.Max(-sbCount, Math.Min(buffer.Rows - 1, endVisibleRow));

                int composerStart = Math.Max(0, startVisibleRow);
                int composerEnd = Math.Max(0, Math.Min(buffer.Rows - 1, endVisibleRow));
                
                if (composerStart <= composerEnd)
                {
                    composer.RenderTo(canvas, buffer, s.Paint, s.CellWidth, s.CellHeight, composerStart, composerEnd);
                }

                int sbStart = Math.Max(-sbCount, startVisibleRow);
                int sbEnd = Math.Min(-1, endVisibleRow);
                if (sbStart <= sbEnd)
                {
                    var lines = buffer.GetScrollbackLines();
                    var paint = s.Paint;
                    paint.LcdRenderText = true;
                    paint.SubpixelText = true;
                    paint.IsAntialias = true;
                    var fm = paint.FontMetrics;
                    float glyphHeight = Math.Abs(fm.Ascent) + Math.Abs(fm.Descent);
                    float baselineOffset = (float)(s.CellHeight * 0.5f) + (glyphHeight * 0.5f) - Math.Abs(fm.Descent);
                    
                    for (int r = sbStart; r <= sbEnd; r++)
                    {
                        int idx = r + sbCount;
                        if (idx >= 0 && idx < lines.Count)
                        {
                            var lineStr = lines[idx];
                            if (string.IsNullOrEmpty(lineStr)) continue;
                            float y = (float)(r * s.CellHeight + baselineOffset);
                            
                            int col = 0;
                            var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(lineStr);
                            while (enumerator.MoveNext())
                            {
                                string g = enumerator.GetTextElement();
                                float x = MathF.Round(col * s.CellWidth);
                                canvas.DrawText(g, x, y, paint);
                                col++;
                            }
                        }
                    }
                }
            }
            else
            {
                for (int r = -sbCount; r < buffer.Rows; r++)
                {
                    // Basic culling
                    float yTop = (r + sbCount) * s.CellHeight;
                    if (yTop + s.CellHeight < s.ScrollY || yTop > s.ScrollY + 2000) continue; // 2000 is safe fallback

                    for (int c = 0; c < buffer.Columns; c++)
                    {
                        var cell = buffer.GetCell(r, c);
                        if (cell.IsContinuation || string.IsNullOrEmpty(cell.Grapheme)) continue;
                        var text = cell.Grapheme!;
                        float x = (float)(c * s.CellWidth);
                        var fm = s.Paint.FontMetrics;
                        float glyphHeight = Math.Abs(fm.Ascent) + Math.Abs(fm.Descent);
                        float baselineOffset = (float)(s.CellHeight * 0.5f) + (glyphHeight * 0.5f) - Math.Abs(fm.Descent);
                        float y = (float)(r * s.CellHeight + baselineOffset);
                        canvas.DrawText(text, x, y, s.Paint);
                    }
                }
            }
            
            // Also bump selection overlay by +sbCount
            DrawSelectionOverlay(canvas, buffer, s.Selection, s.CellWidth, s.CellHeight, sbCount);
        }
        finally
        {
            canvas.Restore();
        }
    }

    private void DrawSelectionOverlay(SKCanvas canvas, TerminalBuffer buffer, TerminalSelectionRange selection, float cellW, float cellH, int sbCount)
    {
        if (selection.IsEmpty) return;

        int startRow = Math.Clamp(selection.StartRow, -sbCount, buffer.Rows - 1);
        int endRow = Math.Clamp(selection.EndRow, -sbCount, buffer.Rows - 1);

        for (int row = startRow; row <= endRow; row++)
        {
            int startCol = row == selection.StartRow ? Math.Clamp(selection.StartColumn, 0, buffer.Columns - 1) : 0;
            int endCol = row == selection.EndRow ? Math.Clamp(selection.EndColumn, 0, buffer.Columns - 1) : buffer.Columns - 1;

            float x = startCol * cellW;
            float columns = Math.Max(1, endCol - startCol + 1);
            float width = columns * cellW;
            float y = (row + sbCount) * cellH;

            // Only draw if the rect is within reasonable visual range? 
            // Wait, coordinate space is already translated to match.
            using var selectionPaint = new SKPaint
            {
                Color = new SKColor(255, 255, 255, 75), // Light white overlay
                Style = SKPaintStyle.Fill,
                IsAntialias = false
            };

            canvas.DrawRect(new SKRect(x, y, x + width, y + cellH), selectionPaint);
        }
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
