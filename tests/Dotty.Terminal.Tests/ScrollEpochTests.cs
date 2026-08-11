using System;
using Xunit;
using Dotty.Terminal.Adapter;

namespace Dotty.Terminal.Tests;

/// <summary>
/// Verifies the motion-epoch accounting added for incremental scroll rendering:
/// scroll operations rotate epochs with content (moved rows keep their epoch so
/// the renderer can skip them), bump only the exposed band, bump identity
/// generations for the whole region (classification/glyph caches), and record
/// a PendingScroll for the renderer to replay as a region memmove.
/// </summary>
public class ScrollEpochTests
{
    private static TerminalBuffer CreateBuffer(int rows = 24, int cols = 80)
    {
        return new TerminalBuffer(rows, cols);
    }

    /// <summary>Writes `count` distinct lines to `row` so its epoch is bumped `count` times.</summary>
    private static void WriteRows(TerminalBuffer buf, int row, int count, int cols = 80)
    {
        for (int i = 0; i < count; i++)
        {
            buf.SetCursor(row, 0);
            buf.WriteText($"row{row}-{i}".PadRight(cols), default);
        }
    }

    private static void WriteOne(TerminalBuffer buf, int row)
    {
        buf.SetCursor(row, 0);
        buf.WriteText($"r{row}", default);
    }

    /// <summary>
    /// Writes per-row counts in round-robin order. The writer coalesces
    /// consecutive same-row dirty calls, so adjacent writes to the same row
    /// would produce a single bump; alternating rows defeats that dedup and
    /// yields one epoch bump per write.
    /// </summary>
    private static void WriteDistinctEpochs(TerminalBuffer buf, params (int row, int count)[] spec)
    {
        int max = 0;
        foreach (var s in spec) max = Math.Max(max, s.count);
        for (int round = 0; round < max; round++)
        {
            foreach (var s in spec)
            {
                if (round < s.count) WriteOne(buf, s.row);
            }
        }
    }

    [Fact]
    public void ScrollUpLines_RotatesEpochsWithContent_AndBumpsExposedBand()
    {
        var buf = CreateBuffer();
        buf.SetScrollRegion(3, 9); // 0-based region [2..8]
        // Distinct pre-scroll epochs per row: 2→3, 3→2, 4→5, 5→1, 6→4, 7→6, 8→2
        WriteDistinctEpochs(buf, (2, 3), (3, 2), (4, 5), (5, 1), (6, 4), (7, 6), (8, 2));

        var preGen = new ulong[24];
        var preEpoch = new ulong[24];
        for (int r = 0; r < 24; r++) { preGen[r] = buf.GetRowGeneration(r); preEpoch[r] = buf.GetRowEpoch(r); }

        buf.ScrollUpLines(2);

        // Content moved up: row r now holds the content that was at r+2, so
        // its epoch equals the pre-scroll epoch of r+2.
        Assert.Equal(preEpoch[4], buf.GetRowEpoch(2));
        Assert.Equal(preEpoch[5], buf.GetRowEpoch(3));
        Assert.Equal(preEpoch[6], buf.GetRowEpoch(4));
        Assert.Equal(preEpoch[7], buf.GetRowEpoch(5));
        Assert.Equal(preEpoch[8], buf.GetRowEpoch(6));
        // Exposed bottom band bumped by exactly 1.
        Assert.Equal(preEpoch[7] + 1, buf.GetRowEpoch(7));
        Assert.Equal(preEpoch[8] + 1, buf.GetRowEpoch(8));
        // Rows outside the region untouched.
        Assert.Equal(preEpoch[1], buf.GetRowEpoch(1));
        Assert.Equal(preEpoch[0], buf.GetRowEpoch(0));

        // Identity generations: whole region bumped by exactly 1.
        for (int r = 2; r <= 8; r++)
            Assert.Equal(preGen[r] + 1, buf.GetRowGeneration(r));
        Assert.Equal(preGen[1], buf.GetRowGeneration(1));

        // Queue: one scroll, content moved UP (negative delta).
        Assert.Equal(1, buf.PendingScrollCount);
        Assert.True(buf.TryDequeuePendingScroll(out var s));
        Assert.Equal(new TerminalBuffer.PendingScroll(2, 8, -2), s);
        Assert.Equal(0, buf.PendingScrollCount);
    }

    [Fact]
    public void ScrollDownLines_RotatesEpochs_AndBumpsTopBand()
    {
        var buf = CreateBuffer();
        buf.SetScrollRegion(3, 9);
        WriteDistinctEpochs(buf, (2, 3), (3, 2), (4, 5), (5, 1), (6, 4), (7, 6), (8, 2));

        var preEpoch = new ulong[24];
        for (int r = 0; r < 24; r++) preEpoch[r] = buf.GetRowEpoch(r);

        buf.ScrollDownLines(2);

        // Content moved down: row r holds the content that was at r-2.
        Assert.Equal(preEpoch[2], buf.GetRowEpoch(4));
        Assert.Equal(preEpoch[3], buf.GetRowEpoch(5));
        Assert.Equal(preEpoch[4], buf.GetRowEpoch(6));
        Assert.Equal(preEpoch[5], buf.GetRowEpoch(7));
        Assert.Equal(preEpoch[6], buf.GetRowEpoch(8));
        // Exposed top band bumped by exactly 1.
        Assert.Equal(preEpoch[2] + 1, buf.GetRowEpoch(2));
        Assert.Equal(preEpoch[3] + 1, buf.GetRowEpoch(3));

        Assert.Equal(1, buf.PendingScrollCount);
        Assert.True(buf.TryDequeuePendingScroll(out var s));
        Assert.Equal(new TerminalBuffer.PendingScroll(2, 8, 2), s);
    }

    [Fact]
    public void LineFeed_AtRegionBottom_QueuesSingleRowScroll()
    {
        var buf = CreateBuffer();
        buf.SetScrollRegion(3, 9);
        WriteRows(buf, 2, 3);
        WriteRows(buf, 8, 5);

        buf.SetCursor(8, 0); // region bottom
        buf.LineFeed();

        Assert.Equal(1, buf.PendingScrollCount);
        Assert.True(buf.TryDequeuePendingScroll(out var s));
        Assert.Equal(new TerminalBuffer.PendingScroll(2, 8, -1), s);
        // Exposed bottom row bumped.
        Assert.True(buf.GetRowEpoch(8) > buf.GetRowEpoch(7));
    }

    [Fact]
    public void InsertLines_ShiftsEpochsDown_AndRecordsScroll()
    {
        var buf = CreateBuffer();
        buf.SetScrollRegion(3, 9);
        WriteDistinctEpochs(buf, (2, 3), (3, 2), (4, 5), (5, 1), (6, 4));

        var preEpoch = new ulong[24];
        for (int r = 0; r < 24; r++) preEpoch[r] = buf.GetRowEpoch(r);

        buf.SetCursor(3, 0); // insert at row 3
        buf.InsertLines(2);

        // Region [row..bottom] = [3..8]; content shifted down by 2.
        Assert.Equal(preEpoch[3], buf.GetRowEpoch(5));
        Assert.Equal(preEpoch[4], buf.GetRowEpoch(6));
        Assert.Equal(preEpoch[5], buf.GetRowEpoch(7));
        Assert.Equal(preEpoch[6], buf.GetRowEpoch(8));
        // Inserted (exposed) rows bumped by exactly 1.
        Assert.Equal(preEpoch[3] + 1, buf.GetRowEpoch(3));
        Assert.Equal(preEpoch[4] + 1, buf.GetRowEpoch(4));

        Assert.Equal(1, buf.PendingScrollCount);
        Assert.True(buf.TryDequeuePendingScroll(out var s));
        Assert.Equal(new TerminalBuffer.PendingScroll(3, 8, 2), s);
    }

    [Fact]
    public void DeleteLines_ShiftsEpochsUp_AndRecordsScroll()
    {
        var buf = CreateBuffer();
        buf.SetScrollRegion(3, 9);
        WriteDistinctEpochs(buf, (2, 3), (3, 2), (4, 5), (5, 1), (6, 4));

        var preEpoch = new ulong[24];
        for (int r = 0; r < 24; r++) preEpoch[r] = buf.GetRowEpoch(r);

        buf.SetCursor(3, 0);
        buf.DeleteLines(2);

        // Content shifted up by 2.
        Assert.Equal(preEpoch[5], buf.GetRowEpoch(3));
        Assert.Equal(preEpoch[6], buf.GetRowEpoch(4));
        Assert.Equal(preEpoch[7], buf.GetRowEpoch(5));
        Assert.Equal(preEpoch[8], buf.GetRowEpoch(6));
        // Trailing (exposed) rows bumped by exactly 1.
        Assert.Equal(preEpoch[7] + 1, buf.GetRowEpoch(7));
        Assert.Equal(preEpoch[8] + 1, buf.GetRowEpoch(8));

        Assert.Equal(1, buf.PendingScrollCount);
        Assert.True(buf.TryDequeuePendingScroll(out var s));
        Assert.Equal(new TerminalBuffer.PendingScroll(3, 8, -2), s);
    }

    [Fact]
    public void PendingScrolls_DequeueInFifoOrder()
    {
        var buf = CreateBuffer();
        buf.SetScrollRegion(3, 9);
        buf.ScrollUpLines(1);
        buf.ScrollDownLines(2);
        buf.ScrollUpLines(3);

        Assert.Equal(3, buf.PendingScrollCount);
        Assert.True(buf.TryDequeuePendingScroll(out var s1));
        Assert.Equal(-1, s1.Delta);
        Assert.True(buf.TryDequeuePendingScroll(out var s2));
        Assert.Equal(2, s2.Delta);
        Assert.True(buf.TryDequeuePendingScroll(out var s3));
        Assert.Equal(-3, s3.Delta);
        Assert.Equal(0, buf.PendingScrollCount);
        Assert.False(buf.TryDequeuePendingScroll(out _));
    }

    [Fact]
    public void FullScreenScroll_StillRecords_AndTouchesScrollback()
    {
        var buf = CreateBuffer();
        WriteRows(buf, 0, 1);

        buf.ScrollUpLines(1); // full-screen region: top == 0

        Assert.True(buf.ScrollbackCount > 0, "full-screen SU must grow scrollback");
        Assert.Equal(1, buf.PendingScrollCount);
        Assert.True(buf.TryDequeuePendingScroll(out var s));
        Assert.Equal(new TerminalBuffer.PendingScroll(0, 23, -1), s);
    }

    [Fact]
    public void WholeRegionReplacement_DoesNotQueueScroll_ButBumpsAllEpochs()
    {
        var buf = CreateBuffer();
        buf.SetScrollRegion(3, 9);
        WriteRows(buf, 2, 3);

        buf.ScrollUpLines(100); // n >= region height -> clear, no content move

        Assert.Equal(0, buf.PendingScrollCount);
        for (int r = 2; r <= 8; r++)
            Assert.True(buf.GetRowEpoch(r) > 0, "cleared rows must be re-rendered");
    }

    [Fact]
    public void WriteAfterScroll_BumpsOnlyWrittenRowEpoch()
    {
        var buf = CreateBuffer();
        buf.SetScrollRegion(3, 9);
        WriteRows(buf, 2, 1);
        WriteRows(buf, 4, 1);

        ulong preExposed = buf.GetRowEpoch(8);
        buf.ScrollUpLines(1);

        ulong afterScroll = buf.GetRowEpoch(2);
        // Write to row 2 (a moved row): epoch must bump.
        WriteRows(buf, 2, 1);
        Assert.True(buf.GetRowEpoch(2) > afterScroll, "write must bump the written row's epoch");
        // Exposed row 8 was bumped by the scroll itself.
        Assert.True(buf.GetRowEpoch(8) > preExposed, "exposed row must differ from pre-scroll");
    }

    [Fact]
    public void SetAlternateScreenToggle_ClearsPendingScrolls()
    {
        var buf = CreateBuffer();
        buf.SetScrollRegion(3, 9);
        buf.ScrollUpLines(1);
        Assert.Equal(1, buf.PendingScrollCount);

        buf.SetAlternateScreen(true);
        Assert.Equal(0, buf.PendingScrollCount);
        // Alt screen content is brand new: every row must be re-rendered.
        for (int r = 0; r < 24; r++)
            Assert.True(buf.GetRowEpoch(r) > 0, "toggle must bump every row's epoch");

        buf.ScrollUpLines(1);
        Assert.Equal(1, buf.PendingScrollCount);
        buf.SetAlternateScreen(false);
        Assert.Equal(0, buf.PendingScrollCount);
    }
    [Fact]
    public void Resize_KeepsEpochArrayInSync()
    {
        var buf = CreateBuffer(rows: 10);
        buf.ScrollUpLines(1);

        buf.Resize(30, 120);

        Assert.Equal(0, buf.PendingScrollCount);
        Assert.Equal(30UL, (ulong)buf.RowScrollEpochs.Length);
        // New rows have epoch 0; existing rows keep their values.
        Assert.Equal(0UL, buf.GetRowEpoch(29));
        Assert.Equal(0UL, buf.GetRowEpoch(0)); // row 0 received row 1's content (epoch 0)
        Assert.Equal(1UL, buf.GetRowEpoch(9)); // exposed bottom row was bumped
    }

    [Fact]
    public void ScrollEpochMath_RotateRange_MatchesArrayCopySemantics()
    {
        // Sanity: rotation with a positive delta (content down) and negative
        // (content up) over an overlapping range, matching Array.Copy overlap rules.
        var arr = new ulong[] { 10, 11, 12, 13, 14, 15, 16, 17 };
        ScrollEpochMath.RotateRange(arr, 2, 6, 2); // content down by 2
        // Row r receives old r-2: src [2..4] (12,13,14) -> dst [4..6].
        Assert.Equal(new ulong[] { 10, 11, 12, 13, 12, 13, 14, 17 }, arr);

        var arr2 = new ulong[] { 10, 11, 12, 13, 14, 15, 16, 17 };
        ScrollEpochMath.RotateRange(arr2, 1, 7, -3); // content up by 3
        // Row r receives old r+3: 14,15,16,17 -> 11,12,13,14 and 15,16,17 shifted in
        Assert.Equal(new ulong[] { 10, 14, 15, 16, 17, 15, 16, 17 }, arr2);
    }
}
