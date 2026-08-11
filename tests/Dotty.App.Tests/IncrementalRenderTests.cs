using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Xunit;
using Xunit.Sdk;
using SkiaSharp;
using Dotty.App.Controls;
using Dotty.App.Rendering;
using Dotty.Terminal.Adapter;

namespace Dotty.App.Tests;

/// <summary>
/// Pixel-identity verification for the incremental render path: after a buffer
/// scroll (or viewport shift), the replay+RenderDirty path must produce pixels
/// byte-identical to a full re-render of the same buffer state. Uses the real
/// primitives — MemmoveRegionRows / MemmoveWholeFrame / ApplyScrollToMirror /
/// ComputeDirtyRows from TerminalCanvas and RenderDirty from the composer — in
/// the same orchestration the canvas performs.
/// </summary>
public class IncrementalRenderTests
{
    private const int Rows = 30, Cols = 60;
    private const int CellW = 8, CellH = 16;
    private const int W = Cols * CellW, H = Rows * CellH;

    // Non-black base background: exercises the incremental path's re-fill of
    // the base color under dirty rows (a black background would mask bugs).
    // NOTE: SKColor ctor is (red, green, blue, alpha).
    private static readonly SKColor Bg = new(0x24, 0x24, 0x24, 0xFF);

    private static TerminalBuffer CreateBuffer(bool alt)
    {
        var buffer = new TerminalBuffer(Rows, Cols);
        if (alt) buffer.SetAlternateScreen(true);
        return buffer;
    }

    /// <summary>Fills every row with a distinct background+foreground band.</summary>
    private static void Fill(TerminalBuffer buffer)
    {
        for (int r = 0; r < Rows; r++)
        {
            buffer.SetCursor(r, 0);
            var attrs = new CellAttributes
            {
                Background = SgrColorArgb.FromRgb((byte)(24 + r * 4), 40, 50),
                Foreground = SgrColorArgb.FromRgb(224, 226, 234),
            };
            buffer.WriteText($"line {r:D2} content ".PadRight(Cols), attrs);
        }
    }

    private static SKBitmap NewBitmap() => new(W, H, SKColorType.Rgba8888, SKAlphaType.Premul);

    /// <summary>Replicates the canvas full path: Clear + content translate + RenderTo + scrollback text.</summary>
    private static void FullRender(SKBitmap bmp, TerminalBuffer buffer, TerminalFrameComposer composer, SKPaint paint, SKFont font, float translate, int startVisibleRow, int endVisibleRow)
    {
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(Bg);
        canvas.SetMatrix(SKMatrix.CreateTranslation(0, translate));

        int visStart = Math.Max(0, startVisibleRow);
        int visEnd = Math.Min(buffer.Rows - 1, endVisibleRow);
        if (visStart <= visEnd)
            composer.RenderTo(canvas, buffer, paint, font, CellW, CellH, visStart, visEnd);

        int sbCount = buffer.ScrollbackCount;
        int sbStart = Math.Max(-sbCount, startVisibleRow);
        int sbEnd = Math.Min(-1, endVisibleRow);
        if (sbStart <= sbEnd)
        {
            var fm = font.Metrics;
            float glyphHeight = Math.Abs(fm.Ascent) + Math.Abs(fm.Descent);
            float baselineOffset = (float)(CellH * 0.5f) + (glyphHeight * 0.5f) - Math.Abs(fm.Descent);
            for (int r = sbStart; r <= sbEnd; r++)
            {
                int idx = r + sbCount;
                idx = Math.Max(0, Math.Min(sbCount - 1, idx));
                var line = buffer.GetScrollbackLine(idx);
                if (line.Length <= 0) continue;
                float y = (float)(r * CellH + baselineOffset);
                canvas.DrawText(SKTextBlob.Create(line.Text ?? string.Empty, font), 0, y, paint);
            }
        }

        canvas.Flush();
    }

    /// <summary>
    /// Replicates the canvas incremental path: replay queued scrolls as region
    /// memmoves (+ mirror rotation) or fallback sentinels, then RenderDirty for
    /// the rows whose pixels were not moved into place.
    /// </summary>
    private static void IncrementalRender(SKBitmap bmp, TerminalBuffer buffer, TerminalFrameComposer composer, SKPaint paint, SKFont font, ulong[] mirror, float translate, int startVisibleRow, int endVisibleRow)
    {
        int visStart = Math.Max(0, startVisibleRow);
        int visEnd = Math.Min(buffer.Rows - 1, endVisibleRow);
        if (visStart > visEnd) return;

        using var canvas = new SKCanvas(bmp);
        canvas.SetMatrix(SKMatrix.CreateTranslation(0, translate));

        unsafe
        {
            byte* pixels = (byte*)bmp.GetPixels();
            int stride = bmp.RowBytes;
            while (buffer.TryDequeuePendingScroll(out var s))
            {
                TerminalCanvas.ApplyScrollToMirror(mirror, s.Top, s.Bottom, s.Delta, visStart, visEnd, out bool memmoved);
                if (memmoved)
                    TerminalCanvas.MemmoveRegionRows(pixels, stride, H, translate, s.Top, s.Bottom, s.Delta, CellH);
            }
        }

        var dirty = new List<int>();
        TerminalCanvas.ComputeDirtyRows(buffer.RowScrollEpochs, mirror, visStart, visEnd, dirty);
        if (dirty.Count > 0)
            composer.RenderDirty(canvas, buffer, paint, font, CellW, CellH, Bg, visStart, visEnd, CollectionsMarshal.AsSpan(dirty));

        canvas.Flush();
    }

    /// <summary>
    /// Simulates a viewport pureScroll exactly like the canvas: whole-frame
    /// memmove, then exposed grid-band rows are sentineled for RenderDirty and
    /// exposed scrollback rows are filled with the base background + text.
    /// </summary>
    private static void PureScrollRender(SKBitmap bmp, TerminalBuffer buffer, TerminalFrameComposer composer, SKPaint paint, SKFont font, ulong[] mirror, float oldOff, float newOff, int startVisibleRow, int endVisibleRow)
    {
        using var canvas = new SKCanvas(bmp);
        int sbCount = buffer.ScrollbackCount;
        float newTranslate = sbCount * CellH - newOff;
        int pixelDelta = (int)Math.Round(oldOff - newOff);
        unsafe
        {
            byte* pixels = (byte*)bmp.GetPixels();
            TerminalCanvas.MemmoveWholeFrame(pixels, bmp.RowBytes, H, pixelDelta);
        }

        canvas.SetMatrix(SKMatrix.CreateTranslation(0, newTranslate));

        int visStart = Math.Max(0, startVisibleRow);
        int visEnd = Math.Min(buffer.Rows - 1, endVisibleRow);

        // Exposed band, computed with the real canvas logic from offsets.
        TerminalCanvas.ComputeExposedRows(
            oldTop: oldOff, newTop: newOff,
            viewportHeight: H, cellHeight: CellH, scrollbackCount: sbCount,
            out int exposeStartRow, out int exposeEndRow);
        exposeStartRow = Math.Max(startVisibleRow, exposeStartRow);
        exposeEndRow = Math.Min(endVisibleRow, exposeEndRow);

        // Grid rows in the band: sentinel so the dirty pass re-renders them.
        for (int r = Math.Max(exposeStartRow, visStart); r <= Math.Min(exposeEndRow, visEnd); r++)
            mirror[r] = ulong.MaxValue;

        // Scrollback rows in the band: base background + text (canvas replicate).
        int sbStart = Math.Max(-sbCount, exposeStartRow);
        int sbEnd = Math.Min(-1, exposeEndRow);
        if (sbStart <= sbEnd)
        {
            using var bgPaint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = false, Color = Bg };
            canvas.DrawRect(SKRect.Create(0, sbStart * CellH, Cols * CellW, (sbEnd - sbStart + 1) * CellH), bgPaint);
            var fm = font.Metrics;
            float glyphHeight = Math.Abs(fm.Ascent) + Math.Abs(fm.Descent);
            float baselineOffset = (float)(CellH * 0.5f) + (glyphHeight * 0.5f) - Math.Abs(fm.Descent);
            for (int r = sbStart; r <= sbEnd; r++)
            {
                int idx = r + sbCount;
                idx = Math.Max(0, Math.Min(sbCount - 1, idx));
                var line = buffer.GetScrollbackLine(idx);
                if (line.Length <= 0) continue;
                float y = (float)(r * CellH + baselineOffset);
                canvas.DrawText(SKTextBlob.Create(line.Text ?? string.Empty, font), 0, y, paint);
            }
        }

        var dirty = new List<int>();
        TerminalCanvas.ComputeDirtyRows(buffer.RowScrollEpochs, mirror, visStart, visEnd, dirty);
        if (dirty.Count > 0)
            composer.RenderDirty(canvas, buffer, paint, font, CellW, CellH, Bg, visStart, visEnd, CollectionsMarshal.AsSpan(dirty));

        canvas.Flush();
    }

    private static void RunPureScroll(string name, TerminalBuffer buffer, TerminalFrameComposer composer, SKPaint paint, SKFont font, float oldOff, float newOff)
    {
        int sbCount = buffer.ScrollbackCount;
        int oldStart = (int)Math.Floor(oldOff / CellH) - sbCount;
        int oldEnd = (int)Math.Ceiling((oldOff + H) / CellH) - sbCount - 1;
        int newStart = (int)Math.Floor(newOff / CellH) - sbCount;
        int newEnd = (int)Math.Ceiling((newOff + H) / CellH) - sbCount - 1;

        var reference = NewBitmap();
        FullRender(reference, buffer, composer, paint, font, sbCount * CellH - newOff, newStart, newEnd);

        var prev = NewBitmap();
        FullRender(prev, buffer, composer, paint, font, sbCount * CellH - oldOff, oldStart, oldEnd);

        var mirror = buffer.RowScrollEpochs.ToArray();
        var inc = prev.Copy();
        PureScrollRender(inc, buffer, composer, paint, font, mirror, oldOff, newOff, newStart, newEnd);

        AssertPixelIdentical(reference, inc, name);
    }

    private static void AssertPixelIdentical(SKBitmap expected, SKBitmap actual, string scenario)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        unsafe
        {
            byte* e = (byte*)expected.GetPixels();
            byte* a = (byte*)actual.GetPixels();
            int bytes = expected.RowBytes * expected.Height;
            for (int i = 0; i < bytes; i++)
            {
                if (e[i] != a[i])
                {
                    int row = i / expected.RowBytes;
                    int byteInRow = i % expected.RowBytes;
                    throw new XunitException(
                        $"[{scenario}] pixel mismatch at byte {i} (row {row}, byte-in-row {byteInRow}): 0x{e[i]:X2} vs 0x{a[i]:X2}");
                }
            }
        }
    }

    private static void RunScenario(string name, TerminalBuffer buffer, Action<TerminalBuffer> mutate, float translate, int startVisibleRow, int endVisibleRow)
    {
        using var composer = new TerminalFrameComposer();
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var font = new SKFont(SKTypeface.Default, 13f);

        var pre = NewBitmap();
        FullRender(pre, buffer, composer, paint, font, translate, startVisibleRow, endVisibleRow);

        // Mirror of the state the pre frame was rendered from.
        var mirror = buffer.RowScrollEpochs.ToArray();

        mutate(buffer);

        var reference = NewBitmap();
        FullRender(reference, buffer, composer, paint, font, translate, startVisibleRow, endVisibleRow);

        var inc = pre.Copy();
        IncrementalRender(inc, buffer, composer, paint, font, mirror, translate, startVisibleRow, endVisibleRow);

        AssertPixelIdentical(reference, inc, name);
    }

    // ------------------------------------------------------------------
    // Alt screen (nvim regime, exact cell backgrounds)
    // ------------------------------------------------------------------

    [Fact]
    public void AltScreen_DLPageDown_MatchesFullRender()
    {
        var buffer = CreateBuffer(alt: true);
        Fill(buffer);
        RunScenario("alt-DL", buffer, b =>
        {
            b.SetScrollRegion(2, Rows);
            b.SetCursor(0, 0);
            b.DeleteLines(3); // nvim's \e[3M page-down under DECSTBM
            // Repaint the exposed band (nvim's CUP + new content writes).
            for (int r = Rows - 3; r < Rows; r++)
            {
                b.SetCursor(r, 0);
                b.WriteText($"repaint {r:D2} ".PadRight(Cols),
                    new CellAttributes { Background = SgrColorArgb.FromRgb(60, 80, 60), Foreground = SgrColorArgb.FromRgb(255, 255, 255) });
            }
        }, translate: 0, startVisibleRow: 0, endVisibleRow: Rows - 1);
    }

    [Fact]
    public void AltScreen_ScrollUpRegion_MatchesFullRender()
    {
        var buffer = CreateBuffer(alt: true);
        Fill(buffer);
        RunScenario("alt-SU", buffer, b =>
        {
            b.SetScrollRegion(2, Rows);
            b.ScrollUpLines(3);
        }, translate: 0, startVisibleRow: 0, endVisibleRow: Rows - 1);
    }

    [Fact]
    public void AltScreen_ScrollDownRegion_MatchesFullRender()
    {
        var buffer = CreateBuffer(alt: true);
        Fill(buffer);
        RunScenario("alt-SD", buffer, b =>
        {
            b.SetScrollRegion(2, Rows);
            b.ScrollDownLines(3);
        }, translate: 0, startVisibleRow: 0, endVisibleRow: Rows - 1);
    }

    [Fact]
    public void AltScreen_LineFeedAtRegionBottom_MatchesFullRender()
    {
        var buffer = CreateBuffer(alt: true);
        Fill(buffer);
        RunScenario("alt-LF", buffer, b =>
        {
            b.SetScrollRegion(2, Rows);
            b.SetCursor(Rows - 1, 0);
            b.LineFeed();
        }, translate: 0, startVisibleRow: 0, endVisibleRow: Rows - 1);
    }

    [Fact]
    public void AltScreen_InsertAndDeleteLines_MatchesFullRender()
    {
        var buffer = CreateBuffer(alt: true);
        Fill(buffer);
        RunScenario("alt-IL", buffer, b =>
        {
            b.SetScrollRegion(2, Rows);
            b.SetCursor(5, 0);
            b.InsertLines(2);
        }, translate: 0, startVisibleRow: 0, endVisibleRow: Rows - 1);

        var buffer2 = CreateBuffer(alt: true);
        Fill(buffer2);
        RunScenario("alt-DL2", buffer2, b =>
        {
            b.SetScrollRegion(2, Rows);
            b.SetCursor(5, 0);
            b.DeleteLines(2);
        }, translate: 0, startVisibleRow: 0, endVisibleRow: Rows - 1);
    }

    [Fact]
    public void AltScreen_ScrollThenWriteInterleaved_MatchesFullRender()
    {
        var buffer = CreateBuffer(alt: true);
        Fill(buffer);
        RunScenario("alt-scroll+write", buffer, b =>
        {
            b.SetScrollRegion(2, Rows);
            b.ScrollUpLines(2);
            // Write into a moved row: its content changed in place after the scroll.
            b.SetCursor(4, 0);
            b.WriteText("overwritten moved row ".PadRight(Cols),
                new CellAttributes { Background = SgrColorArgb.FromRgb(200, 60, 60), Foreground = SgrColorArgb.FromRgb(255, 255, 255) });
            b.SetCursor(Rows - 1, 0);
            b.WriteText("bottom write ".PadRight(Cols),
                new CellAttributes { Background = SgrColorArgb.FromRgb(200, 60, 60), Foreground = SgrColorArgb.FromRgb(255, 255, 255) });
        }, translate: 0, startVisibleRow: 0, endVisibleRow: Rows - 1);
    }

    [Fact]
    public void AltScreen_MultiScrollBurst_MatchesFullRender()
    {
        var buffer = CreateBuffer(alt: true);
        Fill(buffer);
        RunScenario("alt-multi-scroll", buffer, b =>
        {
            b.SetScrollRegion(2, Rows);
            b.ScrollUpLines(2);
            b.ScrollDownLines(1);
            b.ScrollUpLines(3);
        }, translate: 0, startVisibleRow: 0, endVisibleRow: Rows - 1);
    }

    // ------------------------------------------------------------------
    // Main screen (pill background synthesis)
    // ------------------------------------------------------------------

    [Fact]
    public void MainScreen_ScrollUpRegion_MatchesFullRender_WithPills()
    {
        var buffer = CreateBuffer(alt: false);
        Fill(buffer);
        // Region scrolls that cross pill boundaries (adjacent rows have
        // different background colors) must not split or misdraw pills.
        RunScenario("main-SU-pills", buffer, b =>
        {
            b.SetScrollRegion(2, Rows);
            b.ScrollUpLines(1);
        }, translate: 0, startVisibleRow: 0, endVisibleRow: Rows - 1);
    }

    [Fact]
    public void MainScreen_ScrollDownRegion_MatchesFullRender_WithPills()
    {
        var buffer = CreateBuffer(alt: false);
        Fill(buffer);
        RunScenario("main-SD-pills", buffer, b =>
        {
            b.SetScrollRegion(2, Rows);
            b.ScrollDownLines(1);
        }, translate: 0, startVisibleRow: 0, endVisibleRow: Rows - 1);
    }

    [Fact]
    public void MainScreen_WriteOnly_MatchesFullRender()
    {
        var buffer = CreateBuffer(alt: false);
        Fill(buffer);
        RunScenario("main-write-only", buffer, b =>
        {
            b.SetCursor(10, 5);
            b.WriteText("statusline changed ".PadRight(Cols),
                new CellAttributes { Background = SgrColorArgb.FromRgb(30, 31, 41), Foreground = SgrColorArgb.FromRgb(255, 255, 255) });
            b.SetCursor(2, 0);
            b.WriteText("another write ".PadRight(Cols), CellAttributes.Default);
        }, translate: 0, startVisibleRow: 0, endVisibleRow: Rows - 1);
    }

    // ------------------------------------------------------------------
    // Main screen with scrollback + viewport offset (fallback path)
    // ------------------------------------------------------------------

    [Fact]
    public void MainScreen_ScrolledUpViewport_RegionScrollFallsBack_MatchesFullRender()
    {
        var buffer = CreateBuffer(alt: false);
        // Grow scrollback by scrolling the full screen.
        for (int i = 0; i < 60; i++)
        {
            buffer.SetCursor(Rows - 1, 0);
            buffer.WriteText($"history line {i:D2} ".PadRight(Cols), CellAttributes.Default);
            buffer.LineFeed();
        }
        Fill(buffer);

        int sbCount = buffer.ScrollbackCount;
        Assert.True(sbCount > 0, "test requires scrollback");
        // Viewport scrolled up: 3 scrollback rows visible at the top.
        float offset = (sbCount - 3) * CellH;
        float translate = sbCount * CellH - offset; // == 3 * CellH
        int startVisibleRow = (int)Math.Floor(offset / CellH) - sbCount; // == -3
        int endVisibleRow = (int)Math.Ceiling((offset + H) / CellH) - sbCount - 1; // == Rows-4

        // Region [10..29] extends below the viewport (grid rows 27..29 are not
        // in the bitmap) -> the replay falls back to re-rendering visible rows
        // of the region instead of memmoving content in from off-screen.
        RunScenario("main-fallback", buffer, b =>
        {
            b.SetScrollRegion(11, Rows); // 0-based [10..29]
            b.ScrollUpLines(2);
        }, translate, startVisibleRow, endVisibleRow);
    }

    // ------------------------------------------------------------------
    // PureScroll (viewport shift) via the new band path
    // ------------------------------------------------------------------

    [Fact]
    public void PureScroll_MatchesFullRender()
    {
        var buffer = CreateBuffer(alt: false);
        for (int i = 0; i < 40; i++)
        {
            buffer.SetCursor(Rows - 1, 0);
            buffer.WriteText($"history {i:D2} ".PadRight(Cols), CellAttributes.Default);
            buffer.LineFeed();
        }
        Fill(buffer);

        int sbCount = buffer.ScrollbackCount;
        Assert.True(sbCount > 0, "test requires scrollback");
        float maxOff = (Rows + sbCount) * CellH - H; // == sbCount * CellH

        using var composer = new TerminalFrameComposer();
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var font = new SKFont(SKTypeface.Default, 13f);

        // Wheel-down near the bottom: content moves up, exposed band is the
        // bottom grid rows (grid-band path through RenderDirty).
        RunPureScroll("pureScroll-wheel-down", buffer, composer, paint, font,
            oldOff: maxOff - 3 * CellH, newOff: maxOff);

        // Wheel-up at the bottom: content moves down, exposed band is the top
        // scrollback rows (scrollback-band path).
        RunPureScroll("pureScroll-wheel-up", buffer, composer, paint, font,
            oldOff: maxOff, newOff: maxOff - 3 * CellH);
    }
}
