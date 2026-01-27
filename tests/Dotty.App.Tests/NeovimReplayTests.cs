using Xunit;
using Dotty.Terminal.Adapter;
using System.Text;

namespace Dotty.App.Tests;

public class NeovimReplayTests
{
    // Helper: scan the buffer for orphaned bases or continuation mismatches
    private static void AssertNoOrphanedBases(TerminalBuffer tb)
    {
        int rows = tb.Rows;
        int cols = tb.Columns;

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                var cell = tb.GetCell(r, c);
                if (cell.IsContinuation)
                {
                    // continuation should not have Grapheme
                    Assert.True(string.IsNullOrEmpty(cell.Grapheme), $"Continuation cell at {r},{c} unexpectedly has Grapheme '{cell.Grapheme}'");
                }

                if (!cell.IsContinuation && !string.IsNullOrEmpty(cell.Grapheme))
                {
                    int width = Math.Max(1, (int)cell.Width);
                    // ensure trailing continuation cells exist and are marked
                    for (int i = 1; i < width; i++)
                    {
                        if (c + i >= cols) break;
                        var cont = tb.GetCell(r, c + i);
                        Assert.True(cont.IsContinuation, $"Base at {r},{c} width={width} expects continuation at {r},{c+i}");
                    }
                }
            }
        }
    }

    [Fact]
    public void Replay_EraseLine_LF_CUP_Sequence_DetectsNoOrphans()
    {
        // Create a buffer sized to match typical Neovim session from logs
        int rows = 80;
        int cols = 150;
        var tb = new TerminalBuffer(rows: rows, columns: cols);

        // Heuristic replay of the noisy sequence: repeated EraseLine then LF
        // which we saw in your logs, followed by a number of CUP/SGR writes
        // to populate a statusline area. This isn't a perfect reproduction
        // of Neovim, but it stresses the same codepaths.

        // Move cursor to an upper area and do a long run of EraseLine + LF
        tb.SetCursor(4, 0);
        for (int r = 4; r < 78; r++)
        {
            tb.EraseLine(0); // CSI K
            tb.LineFeed();   // LF
        }

        // Now simulate statusline writes: set SGR then CUP then write text
        // We'll write a mix of single and double-width glyphs across columns
        // similar to the many CUP commands in the log.
        var wide = "界"; // double-width
        var single = "x";

        // Write at various rows/cols that appear in the log excerpt
        int[] targetRows = new[] { 28, 29, 30, 31, 32, 33, 34, 35, 36, 37 };
        int[] targetCols = new[] { 52, 60, 70, 80, 90, 100, 110 };

        foreach (var r in targetRows)
        {
            foreach (var c in targetCols)
            {
                tb.SetCursor(r, c);
                // alternate wide/single to mimic variable graphemes in statusline
                var text = ((r + c) % 2 == 0) ? wide.AsSpan() : single.AsSpan();
                tb.WriteText(text, CellAttributes.Default);
            }
        }

        // Now do another pass of EraseLine on rows overlapping statusline
        for (int r = 26; r < 40; r++)
        {
            tb.SetCursor(r, 0);
            tb.EraseLine(0);
        }

        // Finally scan for anomalies and fail if any invariant is violated
        AssertNoOrphanedBases(tb);
    }
}
