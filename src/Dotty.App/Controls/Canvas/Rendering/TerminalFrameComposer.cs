using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using Dotty.Terminal.Adapter;
using SkiaSharp;
using System.Text;
using System.Linq;

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
    // Front buffer (persistent single backing bitmap)
    private SKBitmap? _frontBuffer;
    private SKCanvas? _frontCanvas;
    private int _frontW = 0;
    private int _frontH = 0;
    // (per-row caches allocated by EnsureRowCache)

    public TerminalFrameComposer()
    {
    }

    /// <summary>
    /// Reset and free all internal caches (per-row bitmaps and the front buffer).
    /// Call this when the backing buffer or terminal mode changes so stale
    /// bitmaps are not reused. This is intentionally cheap to call; it disposes
    /// existing SK resources and resets internal state so subsequent calls to
    /// EnsureRowCache / EnsureFrontBuffer will recreate appropriately.
    /// </summary>
    public void ResetCaches()
    {
        for (int i = 0; i < _rowBitmaps.Length; i++)
        {
            _rowCanvases[i]?.Dispose();
            _rowBitmaps[i]?.Dispose();
            _rowCanvases[i] = null;
            _rowBitmaps[i] = null;
        }

        _rowBitmaps = Array.Empty<SKBitmap?>();
        _rowCanvases = Array.Empty<SKCanvas?>();
        _rowVersions = Array.Empty<int>();
        _rowPixelW = 0;
        _rowPixelH = 0;

        _frontCanvas?.Dispose();
        _frontBuffer?.Dispose();
        _frontCanvas = null;
        _frontBuffer = null;
        _frontW = 0;
        _frontH = 0;
    }

    /// <summary>
    /// Render cached rows into the target canvas. Repaint per-row caches only when
    /// the buffer's row version changes. This method performs no allocations except
    /// when resizing the row caches.
    /// </summary>
    public void RenderTo(SKCanvas target, TerminalBuffer buffer, GlyphAtlas atlas, SKPaint paint, float cellW, float cellH)
    {
        try
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (atlas == null) throw new ArgumentNullException(nameof(atlas));
            if (paint == null) throw new ArgumentNullException(nameof(paint));

            // Target is the UI-provided canvas; we render per-row cached bitmaps onto it.
            int rowPixelH = Math.Max(1, (int)Math.Ceiling(cellH));
            int rowPixelW = Math.Max(1, (int)Math.Ceiling(buffer.Columns * cellW));

            // Defer EnsureRowCache until after we process scroll-generation invalidation
            // because ResetCaches() below can clear the arrays that EnsureRowCache creates.

            // Sanity-check: our per-row cache arrays should match the buffer row count.
            // In release or when caches were reset concurrently, recreate caches
            // rather than asserting/crashing.
            if (_rowVersions.Length != buffer.Rows)
            {
                try { EnsureRowCache(buffer.Rows, rowPixelW, rowPixelH); } catch { }
            }

            // Ensure a single persistent front buffer that we'll paint rows into,
            // then present with ONE DrawBitmap call at the end.
            int totalPixelW = rowPixelW;
            int totalPixelH = rowPixelH * buffer.Rows;

            // If the buffer reported scroll/erase work since our last frame,
            // perform a targeted invalidation: reset per-row versions and
            // clear the front canvas when sizes match. Only fall back to the
            // heavier ResetCaches when dimensions differ (to avoid leaks).
            try
            {
                int gen = buffer.ScrollGeneration;
                if (gen != _lastScrollGeneration)
                {
                    // If our caches are already sized correctly, just invalidate
                    // the per-row versions and clear the front canvas to avoid
                    // reallocating SKBitmaps. Otherwise, reset caches so they
                    // will be recreated with correct sizes.
                    if (_rowBitmaps.Length == buffer.Rows && _rowPixelW == rowPixelW && _rowPixelH == rowPixelH
                        && _frontW == totalPixelW && _frontH == totalPixelH && _frontCanvas != null)
                    {
                        for (int i = 0; i < _rowVersions.Length; i++)
                        {
                            _rowVersions[i] = -1;
                            _rowTextCache[i] = string.Empty;
                        }
                        try { _frontCanvas.Clear(SKColors.Transparent); } catch { }
                    }
                    else
                    {
                        // Sizes changed or buffers missing — reset everything.
                        ResetCaches();
                    }

                    _lastScrollGeneration = gen;
                }
            }
            catch { }

            // Ensure row caches now that any scroll-generation invalidation has been
            // applied. This avoids a race where we create per-row arrays and then
            // ResetCaches() clears them, leading to IndexOutOfRange when accessed.
            EnsureRowCache(buffer.Rows, rowPixelW, rowPixelH);

            EnsureFrontBuffer(totalPixelW, totalPixelH);

            if (_frontCanvas == null || _frontBuffer == null) return;

            // Snapshot dirty flags for the entire frame so mutations during
            // rendering don't cause missed invalidations or spurious cache hits.
            var dirtySnapshot = buffer.DirtyRows != null ? (bool[])buffer.DirtyRows.Clone() : null;

            // Paint rows into the front buffer. Use a cheap blit paint for atlas copies.
            using var blitPaint = new SKPaint { FilterQuality = SKFilterQuality.None, IsAntialias = false };
            // Use a clear paint with BlendMode.Clear to clear only a destination rect
            // (SKCanvas.Clear ignores clip regions on some backends).
            using var clearPaint = new SKPaint { BlendMode = SKBlendMode.Clear };

            // collect current per-row src hashes for sidecar output. Use a local
            // stable rows count so concurrent buffer.Rows changes don't cause OOB.
            int rows = buffer.Rows;
            var perRowSrcHash = new int[rows];
            for (int r = 0; r < rows; r++)
            {
                perRowSrcHash[r] = (_rowBitmaps.Length > r && _rowBitmaps[r] != null) ? _rowBitmaps[r].GetHashCode() : 0;
            }

            for (int r = 0; r < rows; r++)
            {
                int ver = buffer.GetRowVersion(r);
                var dirtyArr = dirtySnapshot;
                bool rowDirty = dirtyArr != null && r < dirtyArr.Length && dirtyArr[r];

                // Consider a row changed if it's marked dirty OR its version differs
                // from the cached version. When dirty, always repaint the entire row.
                // Allow an env var to force full repaint for debugging renderer cache issues.
                bool forceFull = false;
                try { forceFull = Environment.GetEnvironmentVariable("DOTTY_FORCE_FULL_REPAINT") == "1"; } catch { }
                bool rowChanged = forceFull || rowDirty || _rowVersions[r] != ver;

                // Decision: rowChanged computed above; no instrumentation logged.

                if (rowChanged)
                {
                    // Guard against cases where caches were reset concurrently
                    // — create a local canvas reference and skip this row if missing.
                    SKCanvas? rc = (r < _rowCanvases.Length) ? _rowCanvases[r] : null;
                    if (rc == null)
                    {
                        // Missing per-row canvas; skip repaint for this row to avoid exceptions.
                        continue;
                    }

                    rc.Clear(SKColors.Transparent);
                    DrawRow(rc, buffer, atlas, paint, r, cellW, rowPixelH, blitPaint);
                    // Debug: optionally paint a large, full-row identifier into the per-row bitmap
                    try
                    {
                        if (Environment.GetEnvironmentVariable("DOTTY_DEBUG_PAINT_ROW_INDEX") == "1")
                        {
                            var label = r.ToString();
                            using var idBg = new SKPaint { Color = new SKColor(0xFF, 0xFF, 0x00, 0x80), IsAntialias = false };
                            using var idText = new SKPaint { Color = SKColors.Black, IsAntialias = true };
                            // choose a large text size that fills most of the row
                            idText.TextSize = Math.Max(8, rowPixelH * 0.8f);
                            // draw a translucent background across the whole row so it's obvious when repeated
                            rc.DrawRect(new SKRect(0, 0, (float)rowPixelW, rowPixelH), idBg);
                            // center the label horizontally
                            float textW = idText.MeasureText(label);
                            float textX = Math.Max(2f, ((float)rowPixelW - textW) / 2f);
                            float textY = rowPixelH - 2f; // baseline near bottom
                            rc.DrawText(label, textX, textY, idText);
                        }
                    }
                    catch { }
                    _rowVersions[r] = ver;
                    try
                    {
                        // Optionally track the textual content we painted for debug.
                        if (Environment.GetEnvironmentVariable("DOTTY_DEBUG_BUFFER_MISMATCH") == "1")
                        {
                            _rowTextCache[r] = buffer.GetRowText(r);
                        }
                    }
                    catch { }

                    // Only clear & blit the destination row when we actually repainted it.
                    var dst = new SKRect(0, r * rowPixelH, rowPixelW, r * rowPixelH + rowPixelH);
                    _frontCanvas.Save();
                    _frontCanvas.ClipRect(dst);
                    _frontCanvas.DrawRect(dst, clearPaint);
                    _frontCanvas.Restore();

                    if (r < _rowBitmaps.Length && _rowBitmaps[r] != null)
                    {
                        // No instrumentation here.

                        _frontCanvas.DrawBitmap(_rowBitmaps[r], dst, blitPaint);

                        // Optionally log the textual content of the logical row for targeted debugging
                        try
                        {
                            if (Environment.GetEnvironmentVariable("DOTTY_DEBUG_LOG_ROW_TEXT") == "1")
                            {
                                try
                                {
                                    var txt = buffer.GetRowText(r);
                                    Console.WriteLine($"[RowText] r={r} text='{txt}'");
                                }
                                catch { }
                            }
                        }
                        catch { }
                    }
                }
            }

            // Present the front buffer in a single draw call using pixel-exact sizes
            // to avoid float→int→float scaling drift. Use the cached row pixel
            // dimensions so rows line up 1:1 with the front buffer.
            var finalDest = new SKRect(0, 0, rowPixelW, rowPixelH * buffer.Rows);
            var presentPaint = new SKPaint { FilterQuality = SKFilterQuality.None, IsAntialias = false };
            try
            {
                if (_frontBuffer != null)
                    target.DrawBitmap(_frontBuffer, finalDest, presentPaint);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RenderDrawEx] {ex}");
                try { ResetCaches(); } catch { }
            }

            // Clear dirty flags after successful present so future changes are tracked
            try { buffer.ClearDirtyRows(); } catch { }

            // Optionally dump the front buffer to PNG for offline inspection.
            try
            {
                    if (Environment.GetEnvironmentVariable("DOTTY_DUMP_FRONT_BUFFER") == "1" && _frontBuffer != null)
                {
                    try
                    {
                        using var img = SKImage.FromBitmap(_frontBuffer);
                        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                        if (data != null)
                        {
                            var dir = Path.Combine(Directory.GetCurrentDirectory(), "artifacts");
                            Directory.CreateDirectory(dir);
                            var file = Path.Combine(dir, $"frontbuf_{DateTime.Now:yyyyMMdd_HHmmss}_{_presentCount++}.png");
                            using var fs = File.Open(file, FileMode.Create, FileAccess.Write);
                            data.SaveTo(fs);
                            Console.WriteLine($"[FrontDump] wrote {file}");

                            // Write a simple JSON sidecar mapping row -> srcHash for easier offline correlation.
                            // Use manual string building to avoid System.Text.Json reflection issues in trimmed/AOT builds.
                            try
                            {
                                var sideFile = Path.Combine(dir, Path.GetFileNameWithoutExtension(file) + ".json");
                                var sb = new System.Text.StringBuilder();
                                sb.Append('{');
                                sb.Append("\"timestamp\":\"").Append(DateTime.Now.ToString("o")).Append('\"');
                                sb.Append(",\"presentCount\":").Append(_presentCount);
                                sb.Append(",\"rows\":[");
                                for (int i = 0; i < perRowSrcHash.Length; i++)
                                {
                                    if (i > 0) sb.Append(',');
                                    sb.Append('{').Append("\"row\":").Append(i).Append(',').Append("\"srcHash\":").Append(perRowSrcHash[i]).Append('}');
                                }
                                sb.Append(']');
                                sb.Append('}');
                                File.WriteAllText(sideFile, sb.ToString());
                                Console.WriteLine($"[FrontDump] wrote sidecar {sideFile}");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[FrontDumpSidecarEx] {ex}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[FrontDumpEx] {ex}");
                    }
                }
            }
            catch { }
        }
        catch (Exception ex)
        {
            // Catch managed exceptions to avoid bringing down the process; log and reset.
            try { Console.WriteLine($"[RenderEx] {ex}"); } catch { }
            try { ResetCaches(); } catch { }
        }

        // end RenderTo
    }

    private void DrawRow(SKCanvas rowCanvas, TerminalBuffer buffer, GlyphAtlas atlas, SKPaint paint, int r, float cellW, int rowPixelH, SKPaint blitPaint)
    {
        // Draw each cell in the row into the provided rowCanvas. We paint the
        // background for every cell (when specified) and then draw glyphs so
        // attribute-only changes correctly replace visuals.
        // Ensure nothing drawn for this row escapes its vertical bounds.
        float rowPixelW = (float)(buffer.Columns * cellW);
        rowCanvas.Save();
        rowCanvas.ClipRect(new SKRect(0, 0, rowPixelW, rowPixelH));

        for (int c = 0; c < buffer.Columns; c++)
        {
            var cell = buffer.GetCell(r, c);

            // Paint background if specified
            if (cell.Background != null && !cell.Background.Value.IsEmpty)
            {
                var hex = cell.Background.Value.Hex;
                if (!string.IsNullOrEmpty(hex) && hex.Length >= 7 && hex[0] == '#')
                {
                    try
                    {
                        byte rbg = Convert.ToByte(hex.Substring(1, 2), 16);
                        byte gbg = Convert.ToByte(hex.Substring(3, 2), 16);
                        byte bbg = Convert.ToByte(hex.Substring(5, 2), 16);
                        using var bgPaint = new SKPaint { Color = new SKColor(rbg, gbg, bbg), IsAntialias = false };
                        var rect = new SKRect((float)(c * cellW), 0, (float)((c + 1) * cellW), rowPixelH);
                        rowCanvas.DrawRect(rect, bgPaint);
                    }
                    catch { }
                }
            }

            if (cell.IsContinuation || cell.IsEmpty) continue;

            var fg = cell.Foreground?.ToString();
            var key = new GlyphKey(cell.Grapheme ?? string.Empty, fg, cell.Bold);

            if (atlas.TryGetGlyph(key, out var info))
            {
                var src = new SKRectI(info.X, info.Y, info.X + info.Width, info.Y + info.Height);
                var fm = paint.FontMetrics;
                float expectedGlyphH = Math.Abs(fm.Ascent) + Math.Abs(fm.Descent);
                float extraPad = (info.Height - expectedGlyphH) / 2f;
                float baselineDest = rowPixelH - Math.Abs(fm.Descent);
                float destY = baselineDest + extraPad - info.BaselineOffset;
                float destX = (float)(c * cellW);
                var dst = new SKRect(destX, destY, destX + info.Width, destY + info.Height);
                rowCanvas.DrawBitmap(atlas.AtlasBitmap, src, dst, blitPaint);
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
        rowCanvas.Restore();
    }

    private void EnsureFrontBuffer(int width, int height)
    {
        if (_frontBuffer != null && _frontW == width && _frontH == height) return;

        _frontCanvas?.Dispose();
        _frontBuffer?.Dispose();

        _frontW = width;
        _frontH = height;

        var w = Math.Max(width, 64);
        var h = Math.Max(height, 64);
        // Use BGRA for typical GPU-friendly ordering on many backends
        _frontBuffer = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        _frontCanvas = new SKCanvas(_frontBuffer);
        _frontCanvas.Clear(SKColors.Transparent);
    }

    private SKBitmap?[] _rowBitmaps = Array.Empty<SKBitmap?>();
    private SKCanvas?[] _rowCanvases = Array.Empty<SKCanvas?>();
    private int[] _rowVersions = Array.Empty<int>();
    private int _rowPixelW = 0;
    private int _rowPixelH = 0;
    private string[] _rowTextCache = Array.Empty<string>();
    private long _presentCount = 0;
    private int _lastScrollGeneration = 0;

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
        _rowTextCache = new string[rows];

        _rowPixelW = rowPixelW;
        _rowPixelH = rowPixelH;

        for (int i = 0; i < rows; i++)
        {
            _rowBitmaps[i] = new SKBitmap(rowPixelW, rowPixelH, SKColorType.Rgba8888, SKAlphaType.Premul);
            _rowCanvases[i] = new SKCanvas(_rowBitmaps[i]);
            _rowVersions[i] = -1;
            _rowTextCache[i] = string.Empty;
        }
    }

    public void Dispose()
    {
        for (int i = 0; i < _rowBitmaps.Length; i++)
        {
            _rowCanvases[i]?.Dispose();
            _rowBitmaps[i]?.Dispose();
        }

        _frontCanvas?.Dispose();
        _frontBuffer?.Dispose();
    }
}
