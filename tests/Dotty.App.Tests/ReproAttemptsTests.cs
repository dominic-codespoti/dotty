using Xunit;
using Dotty.Terminal.Adapter;

namespace Dotty.App.Tests;

public class ReproAttemptsTests
{
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
    }

    [Fact]
    public void Statusline_CUP_Like_Stress()
    {
        var tb = new TerminalBuffer(rows: 80, columns: 120);

        // Simulate long run of EraseLine + LF similar to logs
        tb.SetCursor(4, 0);
        for (int i = 4; i < 78; i++)
        {
            tb.EraseLine(0);
            tb.LineFeed();
        }

        // Simulate many CUPs writing alternating wide/single graphemes
        var wide = "界";
        var single = "a";
        for (int r = 28; r <= 36; r++)
        {
            for (int c = 50; c < 90; c += 3)
            {
                tb.SetCursor(r, c);
                var text = ((r + c) % 2 == 0) ? wide.AsSpan() : single.AsSpan();
                tb.WriteText(text, CellAttributes.Default);
            }
        }

        // Erase overlapping rows to stress clear semantics
        for (int r = 26; r < 40; r++)
        {
            tb.SetCursor(r, 0);
            tb.EraseLine(0);
        }

        AssertNoOrphanedBases(tb);
    }

    [Fact]
    public void OriginMode_ScrollRegion_Interaction()
    {
        var tb = new TerminalBuffer(rows: 40, columns: 80);

        // set scroll region roughly in middle and enable origin mode
        tb.SetScrollRegion(10, 30);
        tb.SetOriginMode(true);

        // write at bottom of region and force LF/scroll
        tb.SetCursor(20 - 10, 0); // when origin mode, SetCursor translates
        for (int i = 0; i < 5; i++)
        {
            tb.WriteText("界".AsSpan(), CellAttributes.Default);
            tb.LineFeed();
        }

        // now write statusline like CUPs around region bottom
        for (int c = 40; c < 70; c += 4)
        {
            tb.SetCursor(30 - 10, c);
            tb.WriteText("x".AsSpan(), CellAttributes.Default);
        }

        // erase a few lines and verify invariant
        for (int r = 25; r <= 31; r++)
        {
            tb.SetCursor(r, 0);
            tb.EraseLine(0);
        }

        AssertNoOrphanedBases(tb);
    }

    [Fact]
    public void AlternateScreen_Toggle_Stress()
    {
        var tb = new TerminalBuffer(rows: 30, columns: 80);

        // write something on main
        tb.SetCursor(2, 2);
        tb.WriteText("main".AsSpan(), CellAttributes.Default);

        // switch to alternate and populate
        tb.SetAlternateScreen(true);
        for (int r = 0; r < 8; r++)
        {
            for (int c = 10; c < 60; c += 5)
            {
                tb.SetCursor(r, c);
                tb.WriteText(((r + c) % 2 == 0 ? "界" : "o").AsSpan(), CellAttributes.Default);
            }
        }

        // toggle back and forth
        tb.SetAlternateScreen(false);
        tb.SetAlternateScreen(true);
        tb.SetAlternateScreen(false);

        // perform some erases on main screen rows
        for (int r = 0; r < 6; r++)
        {
            tb.SetCursor(r, 0);
            tb.EraseLine(0);
        }

        AssertNoOrphanedBases(tb);
    }

    [Fact]
    public void Mixed_Wide_Combining_And_Erase()
    {
        var tb = new TerminalBuffer(rows: 10, columns: 40);

        // wide base with combining mark appended
        tb.SetCursor(1, 1);
        tb.WriteText("e\u0301".AsSpan(), CellAttributes.Default); // e + acute

        tb.SetCursor(1, 3);
        tb.WriteText("界".AsSpan(), CellAttributes.Default);

        // now move into continuation of the wide glyph and erase
        tb.SetCursor(1, 4);
        tb.EraseLine(0);

        AssertNoOrphanedBases(tb);
    }

    [Fact]
    public void Rapid_Overwrite_Continuation()
    {
        var tb = new TerminalBuffer(rows: 6, columns: 20);

        tb.SetCursor(2, 2);
        tb.WriteText("界".AsSpan(), CellAttributes.Default);

        // write directly into continuation column which should clear base
        tb.SetCursor(2, 3);
        tb.WriteText("x".AsSpan(), CellAttributes.Default);

        AssertNoOrphanedBases(tb);
    }
}
