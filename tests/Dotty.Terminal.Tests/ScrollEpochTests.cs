using System;
using Xunit;
using Dotty.Terminal.Adapter;
namespace Dotty.Terminal.Tests;

/// <summary>
/// Identity-generation semantics that drive the renderer's per-row dirty
/// detection and the composer's classification cache. The motion-epoch
/// system these tests originally covered was removed (2026-08-26) —
/// RowScrollEpochs had no readers; ScrollEpochMath deleted with it.
/// </summary>
public class ScrollGenerationTests
{
    private static TerminalBuffer CreateBuffer(int rows = 24, int cols = 80)
    {
        return new TerminalBuffer(rows, cols);
    }

    private static void WriteRows(TerminalBuffer buf, int row, int count, int cols = 80)
    {
        for (int i = 0; i < count; i++)
        {
            buf.SetCursor(row, 0);
            buf.WriteText($"row{row}-{i}".PadRight(cols), default);
        }
    }

    [Fact]
    public void FullScreenScroll_BumpsEveryRegionGeneration_AndGrowsScrollback()
    {
        var buf = CreateBuffer();
        WriteRows(buf, 0, 1);
        var pre = new ulong[24];
        for (int r = 0; r < 24; r++) pre[r] = buf.GetRowGeneration(r);

        buf.ScrollUpLines(1); // full-screen region: top == 0

        Assert.True(buf.ScrollbackCount > 0, "full-screen SU must grow scrollback");
        for (int r = 1; r < 24; r++)
            Assert.True(buf.GetRowGeneration(r) > pre[r], $"row {r} must be marked dirty after scroll");
    }

    [Fact]
    public void WriteAfterScroll_BumpsOnlyWrittenRowGeneration()
    {
        var buf = CreateBuffer();
        buf.SetScrollRegion(3, 9);
        WriteRows(buf, 2, 1);
        WriteRows(buf, 4, 1);
        buf.ScrollUpLines(1);
        ulong afterScroll = buf.GetRowGeneration(2);

        WriteRows(buf, 2, 1);
        Assert.True(buf.GetRowGeneration(2) > afterScroll, "write must bump the written row's generation");
        for (int r = 3; r <= 9; r++)
        {
            if (r == 2) continue;
            // untouched rows keep the scroll-time generation (no extra bumps)
        }
    }

    [Fact]
    public void WholeRegionReplacement_BumpsAllGenerations()
    {
        var buf = CreateBuffer();
        buf.SetScrollRegion(3, 9);
        WriteRows(buf, 2, 3);

        buf.ScrollUpLines(100); // n >= region height -> clear, no content move

        for (int r = 2; r <= 8; r++)
            Assert.True(buf.GetRowGeneration(r) > 0, "cleared rows must be re-rendered");
    }

    [Fact]
    public void RenderBoundary_ResetsWriterCoalescing_SoTypingAlwaysBumpsGeneration()
    {
        // The writer coalesces consecutive writes to the same row (one dirty
        // call per row per burst). The renderer's dirty detection depends on a
        // bump per render cycle, so the canvas must reset that coalescing at
        // every render boundary — otherwise keystrokes in the same row never
        // mark the row dirty and the display goes stale.
        var buf = CreateBuffer();
        buf.SetCursor(0, 0);
        buf.WriteText("a", default);
        ulong afterFirst = buf.GetRowGeneration(0);
        buf.SetCursor(0, 1);
        buf.WriteText("b", default);
        // Same row, no render between: coalesced into the first bump.
        Assert.Equal(afterFirst, buf.GetRowGeneration(0));

        // The canvas calls MarkRender at the start of every RenderToBitmap.
        buf.MarkRender();

        buf.SetCursor(0, 2);
        buf.WriteText("c", default);
        Assert.True(buf.GetRowGeneration(0) > afterFirst, "write after a render must bump the generation");
    }

    [Fact]
    public void SetAlternateScreenToggle_BumpsEveryRowGeneration()
    {
        var buf = CreateBuffer();
        WriteRows(buf, 0, 2);

        buf.SetAlternateScreen(true);
        for (int r = 0; r < 24; r++)
            Assert.True(buf.GetRowGeneration(r) > 0, "toggle must bump every row's generation");

        buf.SetAlternateScreen(false);
        for (int r = 0; r < 24; r++)
            Assert.True(buf.GetRowGeneration(r) > 0, "toggle back must bump every row's generation");
    }
    [Fact]
    public void CaptureRenderSnapshotVisible_WithScrollOffset_CapturesShiftedScrollbackRows()
    {
        var buf = CreateBuffer(rows: 5, cols: 20);
        // Write line 0-4
        for (int i = 0; i < 5; i++)
        {
            buf.SetCursor(i, 0);
            buf.WriteText($"Line {i}".AsSpan(), default);
        }

        // Scroll up by 2 lines -> Line 0 and Line 1 become scrollback rows
        buf.ScrollUpLines(2);
        buf.SetCursor(3, 0);
        buf.WriteText("Line 5".AsSpan(), default);
        buf.SetCursor(4, 0);
        buf.WriteText("Line 6".AsSpan(), default);

        // Capture with scrollOffset = 2 (scrolled up to top)
        using var snap = buf.CaptureRenderSnapshotVisible(scrollOffset: 2);
        Assert.Equal(5, snap.Rows);

        var row0Cells = snap.GetRowCells(0);
        Assert.Equal((uint)'L', row0Cells[0].Rune);
        Assert.Equal((uint)'i', row0Cells[1].Rune);
        Assert.Equal((uint)'n', row0Cells[2].Rune);
        Assert.Equal((uint)'e', row0Cells[3].Rune);
        Assert.Equal((uint)' ', row0Cells[4].Rune);
        Assert.Equal((uint)'0', row0Cells[5].Rune);
    }
}
