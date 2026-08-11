using Dotty.App.Controls;
using Xunit;

namespace Dotty.App.Tests;

/// <summary>
/// Regression tests for the pure-scroll exposed-band computation in
/// <see cref="TerminalCanvas.ComputeExposedRows"/>. The memmove clears the band at
/// the edge the content moved away from (top when scrolling back, bottom when
/// scrolling forward); the returned rows are exactly the rows that must be
/// re-rendered, in buffer coordinates (negative = scrollback).
/// </summary>
public class ScrollExposedRowsTests
{
    private const double CellH = 16.0;
    private const double ViewportH = 30 * CellH; // 30 visible rows
    private const int SbCount = 2362;
    private const double MaxOffset = (30 + SbCount) * CellH - ViewportH;

    [Fact]
    public void WheelUp_RowAligned_ExposesOnlyTheTopThreeRows()
    {
        // Scroll back 3 rows from the bottom: content shifts down, cleared band at top.
        double oldTop = MaxOffset;
        double newTop = MaxOffset - 3 * CellH;

        TerminalCanvas.ComputeExposedRows(oldTop, newTop, ViewportH, CellH, SbCount,
            out int exposeStartRow, out int exposeEndRow);

        // Rows -3..-1 = scrollback indices 0..2, i.e. exactly the three revealed rows.
        Assert.Equal(-3, exposeStartRow);
        Assert.Equal(-1, exposeEndRow);
    }

    [Fact]
    public void WheelUp_RowAligned_DoesNotExtendPastTheClearedBand()
    {
        // The old code extended the band by |delta| past the old offset, re-rendering
        // rows that were already correct after the pixel shift (visible as doubled
        // antialiased edges). The exposed band must stop at the old top edge.
        double oldTop = MaxOffset;
        double newTop = MaxOffset - 3 * CellH;

        TerminalCanvas.ComputeExposedRows(oldTop, newTop, ViewportH, CellH, SbCount,
            out int exposeStartRow, out int exposeEndRow);

        Assert.Equal((int)Math.Ceiling(oldTop / CellH) - 1 - SbCount, exposeEndRow);
        Assert.True(exposeEndRow < 0, "band must be entirely scrollback when scrolling back from the bottom");
    }

    [Fact]
    public void WheelDown_RowAligned_ExposesOnlyTheBottomThreeRows()
    {
        // Scroll forward 3 rows from a mid position: content shifts up, cleared band
        // at the bottom of the viewport — not the top as the old code computed.
        double oldTop = MaxOffset - 10 * CellH;
        double newTop = oldTop + 3 * CellH;

        TerminalCanvas.ComputeExposedRows(oldTop, newTop, ViewportH, CellH, SbCount,
            out int exposeStartRow, out int exposeEndRow);

        // Bottom 3 visible rows = grid rows 20..22 for this offset.
        int expectedStart = (int)Math.Floor((newTop + ViewportH - 3 * CellH) / CellH) - SbCount;
        int expectedEnd = (int)Math.Ceiling((newTop + ViewportH) / CellH) - 1 - SbCount;
        Assert.Equal(expectedStart, exposeStartRow);
        Assert.Equal(expectedEnd, exposeEndRow);
        Assert.Equal(20, exposeStartRow);
        Assert.Equal(22, exposeEndRow);
        Assert.True(exposeStartRow >= 0, "band must be in the visible grid, not scrollback");
    }

    [Fact]
    public void WheelDown_FromTop_ExposesNewerScrollbackRowsAtTheBottom()
    {
        // Scrolling forward from the very top reveals newer scrollback rows (idx 30..32)
        // at the bottom of the viewport; the previously broken code left those rows black.
        double oldTop = 0;
        double newTop = 3 * CellH;

        TerminalCanvas.ComputeExposedRows(oldTop, newTop, ViewportH, CellH, SbCount,
            out int exposeStartRow, out int exposeEndRow);

        Assert.Equal((int)Math.Floor((newTop + ViewportH - 3 * CellH) / CellH) - SbCount, exposeStartRow);
        Assert.Equal((int)Math.Ceiling((newTop + ViewportH) / CellH) - 1 - SbCount, exposeEndRow);
        // At the top, the bottom band maps to scrollback indices 30..32.
        Assert.Equal(-2332, exposeStartRow);
        Assert.Equal(-2330, exposeEndRow);
        Assert.True(exposeStartRow >= -SbCount, "exposed rows must be within the scrollback range");
    }

    [Fact]
    public void WheelUp_FractionalDelta_IncludesPartiallyClearedBoundaryRows()
    {
        // A 53px scroll back (3 rows + 5px) partially clears the top boundary row.
        double oldTop = MaxOffset;
        double newTop = MaxOffset - 53;

        TerminalCanvas.ComputeExposedRows(oldTop, newTop, ViewportH, CellH, SbCount,
            out int exposeStartRow, out int exposeEndRow);

        Assert.Equal((int)Math.Floor(newTop / CellH) - SbCount, exposeStartRow);
        Assert.Equal((int)Math.Ceiling(oldTop / CellH) - 1 - SbCount, exposeEndRow);
        // The partially cleared row is included (one row above the exact 3-row band).
        Assert.Equal(-4, exposeStartRow);
        Assert.Equal(-1, exposeEndRow);
    }

    [Fact]
    public void WheelDown_FractionalDelta_IncludesPartiallyClearedBoundaryRows()
    {
        double oldTop = MaxOffset - 10 * CellH;
        double newTop = oldTop + 53;

        TerminalCanvas.ComputeExposedRows(oldTop, newTop, ViewportH, CellH, SbCount,
            out int exposeStartRow, out int exposeEndRow);

        double clearTop = newTop + ViewportH - 53;
        Assert.Equal((int)Math.Floor(clearTop / CellH) - SbCount, exposeStartRow);
        Assert.Equal((int)Math.Ceiling((newTop + ViewportH) / CellH) - 1 - SbCount, exposeEndRow);
    }

    [Fact]
    public void NoScroll_ZeroDelta_ComputesEmptyOrZeroWidthBand()
    {
        double oldTop = MaxOffset - 3 * CellH;
        double newTop = oldTop;

        TerminalCanvas.ComputeExposedRows(oldTop, newTop, ViewportH, CellH, SbCount,
            out int exposeStartRow, out int exposeEndRow);

        // Zero-width band: start row == end row + 1 (no rows to render).
        Assert.Equal(exposeEndRow + 1, exposeStartRow);
    }
}
