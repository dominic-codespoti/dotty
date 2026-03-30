using System;
using System.IO;
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
    double ViewportWidth,
    double ViewportHeight,
    int ScrollbackCount,
    bool ShowCursor,
    Dotty.App.Controls.TerminalCursorShape CursorShape);

/// <summary>
/// TerminalVisualHandler with aggressive surface clearing.
/// Every render pass starts with a complete clear to ensure no content stacking.
/// </summary>
public sealed class TerminalVisualHandler : CompositionCustomVisualHandler
{
    private TerminalRenderState? _state;
    private static bool _captureNextFrame = false;
    private static int _captureFrameCount = 0;
    
    // Track if this is the first render to ensure we always start clean
    private bool _isFirstRender = true;
    
    public static void CaptureScreenshot()
    {
        _captureNextFrame = true;
    }
    
    public static void EnableAutoCapture(int frameCount)
    {
        _captureFrameCount = frameCount;
    }
    
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
        try
        {
            var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature == null) return;

            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;
            
            // AGGRESSIVE CLEARING: Always clear the entire canvas first.
            // This is the key to preventing content stacking.
            if (_state == null)
            {
                // No state - clear to transparent
                canvas.Clear(SKColors.Transparent);
                _isFirstRender = false;
                return;
            }
            
            var s = _state.Value;
            
            // Always clear with background color before any rendering
            // This ensures no previous content bleeds through
            canvas.Clear(s.BgColor);
            
            // If this is the first render, do an extra clear to be safe
            if (_isFirstRender)
            {
                canvas.Clear(s.BgColor);
                _isFirstRender = false;
            }
            
            if (s.Buffer == null || s.Paint == null) 
            {
                // Nothing to render, but we've already cleared
                return;
            }

            var buffer = s.Buffer;
            try
            {
                try { buffer.MarkRender(); } catch { }

                
                var m = ToSkiaMatrix(context.CurrentTransform);
                canvas.Concat(ref m);


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
                        var paint = s.Paint;
                        var fm = paint.FontMetrics;
                        float glyphHeight = Math.Abs(fm.Ascent) + Math.Abs(fm.Descent);
                        float baselineOffset = (float)(s.CellHeight * 0.5f) + (glyphHeight * 0.5f) - Math.Abs(fm.Descent);
                        
                        for (int r = sbStart; r <= sbEnd; r++)
                        {
                            int idx = r + sbCount;
                            var line = buffer.GetScrollbackLine(idx);
                            if (line.Length <= 0) continue;

                            float y = (float)(r * s.CellHeight + baselineOffset);
                            float x = 0;
                            var text = line.Buffer == null ? string.Empty : new string(line.Buffer, 0, line.Length);
                            canvas.DrawText(text, x, y, paint);
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
                
                // Capture screenshot if requested
                if (_captureNextFrame || _captureFrameCount > 0)
                {
                    SaveCanvasToPng(lease, (int)s.ViewportWidth, (int)s.ViewportHeight, s.BgColor);
                    _captureNextFrame = false;
                    if (_captureFrameCount > 0)
                    {
                        _captureFrameCount--;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TerminalVisualHandler] Render crash caught: {ex}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TerminalVisualHandler] Render crash caught: {ex}");
        }
    }
    
    private void SaveCanvasToPng(ISkiaSharpApiLease lease, int width, int height, SKColor bgColor)
    {
        try
        {
            // Get the surface from the lease and read pixels
            var surface = lease.SkSurface;
            if (surface == null)
            {
                Console.WriteLine("[TerminalVisualHandler] Cannot capture screenshot: surface is null");
                return;
            }
            
            // Create a bitmap to capture the current frame
            using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            
            // Read pixels from the surface
            if (surface.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0))
            {
                // Encode and save
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                var filename = $"/tmp/dotty_canvas_{timestamp}.png";
                
                using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                if (data != null)
                {
                    File.WriteAllBytes(filename, data.ToArray());
                    Console.WriteLine($"[TerminalVisualHandler] Screenshot saved to: {filename}");
                }
            }
            else
            {
                Console.WriteLine("[TerminalVisualHandler] Failed to read pixels from surface");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TerminalVisualHandler] Screenshot save error: {ex.Message}");
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
