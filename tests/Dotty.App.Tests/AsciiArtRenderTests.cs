using Xunit;
using Dotty.Terminal.Adapter;
using System.Text;

namespace Dotty.App.Tests;

public class AsciiArtRenderTests
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
                    Assert.True(cell.Rune == 0, $"Continuation cell at {r},{c} unexpectedly has Rune '{cell.Rune}'");
                }

                if (!cell.IsContinuation && cell.Rune != 0)
                {
                    int width = Math.Max(1, (int)cell.Width);
                    for (int i = 1; i < width; i++)
                    {
                        if (c + i >= cols) break;
                        var cont = tb.GetCell(r, c + i);
                        Assert.True(cont.IsContinuation, $"Base at {r},{c} width={width} expects continuation at {r},{c + i}");
                    }
                }
            }
        }
    }

    [Fact]
    public void AsciiArt_Scroll_And_Rerender_MaintainsBufferIntegrity()
    {
        int rows = 20;
        int cols = 80;
        var tb = new TerminalBuffer(rows: rows, columns: cols);

        var art = new[]
        {
            "##########",
            "#  HELLO #",
            "#  WORLD #",
            "##########"
        };

        int startRow = 2;
        int startCol = 5;

        for (int i = 0; i < art.Length; i++)
        {
            tb.SetCursor(startRow + i, startCol);
            tb.WriteText(art[i].AsSpan(), CellAttributes.Default);
        }

        string[] before = new string[art.Length];
        for (int i = 0; i < art.Length; i++)
        {
            var sb = new StringBuilder();
            for (int c = 0; c < art[i].Length; c++)
            {
                var cell = tb.GetCell(startRow + i, startCol + c);
                var cold = tb.GetColdCell(startRow + i, startCol + c);
                sb.Append(GraphemeHelper.Resolve(cell.Rune, cold.GraphemeIndex) ?? "");
            }
            before[i] = sb.ToString();
        }

        tb.SetCursor(rows - 1, 0);
        for (int i = 0; i < 3; i++) tb.LineFeed();

        for (int i = 0; i < art.Length; i++)
        {
            tb.SetCursor(startRow + i, startCol);
            tb.WriteText(art[i].AsSpan(), CellAttributes.Default);
        }

        for (int i = 0; i < art.Length; i++)
        {
            var sb = new StringBuilder();
            for (int c = 0; c < art[i].Length; c++)
            {
                var cell = tb.GetCell(startRow + i, startCol + c);
                var cold = tb.GetColdCell(startRow + i, startCol + c);
                sb.Append(GraphemeHelper.Resolve(cell.Rune, cold.GraphemeIndex) ?? "");
            }
            var after = sb.ToString();
            Assert.Equal(art[i], after);
        }

        AssertNoOrphanedBases(tb);
    }
}
