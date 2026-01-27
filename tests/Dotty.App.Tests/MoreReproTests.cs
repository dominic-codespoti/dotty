using Xunit;
using Dotty.Terminal.Adapter;

namespace Dotty.App.Tests;

public class MoreReproTests
{
    private static void AssertNoOrphanedBases(TerminalBuffer tb)
    {
        int rows = tb.Rows;
        int cols = tb.Columns;

        for (int r = 0; r < rows; r++)
        for (int c = 0; c < cols; c++)
        {
            var cell = tb.GetCell(r, c);
            if (cell.IsContinuation)
            {
                Assert.True(string.IsNullOrEmpty(cell.Grapheme), $"Continuation cell at {r},{c} unexpectedly has Grapheme '{cell.Grapheme}'");
            }

            if (!cell.IsContinuation && !string.IsNullOrEmpty(cell.Grapheme))
            {
                int width = Math.Max(1, (int)cell.Width);
                for (int i = 1; i < width; i++)
                {
                    if (c + i >= cols) break;
                    var cont = tb.GetCell(r, c + i);
                    Assert.True(cont.IsContinuation, $"Base at {r},{c} width={width} expects continuation at {r},{c+i}");
                }
            }
        }
    }

    [Fact]
    public void Chunked_Write_BaseThenCombining_Attaches_NoOrphan()
    {
        var tb = new TerminalBuffer(rows: 6, columns: 20);

        tb.SetCursor(0, 0);
        // write base 'e'
        tb.WriteText("e".AsSpan(), CellAttributes.Default);
        // then combining acute in separate chunk
        tb.WriteText("\u0301".AsSpan(), CellAttributes.Default);

        // place an erase over the continuation and ensure no orphan
        tb.SetCursor(0, 1);
        tb.EraseLine(0);

        AssertNoOrphanedBases(tb);
    }

    [Fact]
    public void Chunked_Write_CombiningThenBase_NoAttach_NoOrphan()
    {
        var tb = new TerminalBuffer(rows: 6, columns: 20);

        tb.SetCursor(1, 1);
        // combining comes first (malformed stream); should not attach
        tb.WriteText("\u0301".AsSpan(), CellAttributes.Default);
        tb.WriteText("e".AsSpan(), CellAttributes.Default);

        // Now overwrite continuation area and verify invariants
        tb.SetCursor(1, 2);
        tb.EraseLine(0);

        AssertNoOrphanedBases(tb);
    }

    [Fact]
    public void Rapid_Alternate_Erase_Write_Loop()
    {
        var tb = new TerminalBuffer(rows: 10, columns: 40);
        int r = 4;

        for (int iter = 0; iter < 200; iter++)
        {
            tb.SetCursor(r, 0);
            tb.EraseLine(0);
            tb.SetCursor(r, 2);
            tb.WriteText((iter % 2 == 0 ? "界" : "x").AsSpan(), CellAttributes.Default);
            if (iter % 5 == 0)
            {
                // toggling scroll to exercise ScrollUpRegion paths
                tb.SetScrollRegion(2, 8);
                tb.SetOriginMode(true);
                tb.LineFeed();
                tb.SetOriginMode(false);
            }
        }

        AssertNoOrphanedBases(tb);
    }

    [Fact]
    public void Manual_Corruption_Then_Writes()
    {
        var tb = new TerminalBuffer(rows: 6, columns: 20);
        var screen = tb.ActiveScreenForTests;

        // Write a wide grapheme normally
        tb.SetCursor(2, 2);
        tb.WriteText("界".AsSpan(), CellAttributes.Default);

        // Manually corrupt continuation flag (simulate a bug elsewhere)
        ref var cont = ref screen.GetCellRef(2, 3);
        cont.IsContinuation = false; // corrupt

        // Now perform typical statusline style erase/write sequences
        tb.SetCursor(2, 3);
        tb.EraseLine(0);
        tb.SetCursor(2, 2);
        tb.WriteText("x".AsSpan(), CellAttributes.Default);

        AssertNoOrphanedBases(tb);
    }

    [Fact]
    public void Rapid_CUP_Writes_Across_Statusline()
    {
        var tb = new TerminalBuffer(rows: 30, columns: 120);

        for (int r = 25; r < 29; r++)
        for (int c = 50; c < 100; c += 2)
        {
            tb.SetCursor(r, c);
            tb.WriteText((c % 4 == 0 ? "界" : "o").AsSpan(), CellAttributes.Default);
            if (c % 7 == 0)
            {
                tb.EraseLine(0);
            }
        }

        AssertNoOrphanedBases(tb);
    }
}
