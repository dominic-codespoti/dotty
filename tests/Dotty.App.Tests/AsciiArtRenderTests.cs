using Xunit;
using Dotty.Terminal.Adapter;
using System.Text;

namespace Dotty.App.Tests;

public class AsciiArtRenderTests
{
    // Duplicate helper to assert no orphaned bases / continuation mismatches
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

        // Simple ASCII-art box (single-width characters)
        var art = new[]
        {
            "##########",
            "#  HELLO #",
            "#  WORLD #",
            "##########"
        };

        int startRow = 2;
        int startCol = 5;

        // Render the art
        for (int i = 0; i < art.Length; i++)
        {
            tb.SetCursor(startRow + i, startCol);
            tb.WriteText(art[i].AsSpan(), CellAttributes.Default);
        }

        // Snapshot the rendered rows
        string[] before = new string[art.Length];
        for (int i = 0; i < art.Length; i++)
        {
            var sb = new StringBuilder();
            for (int c = 0; c < art[i].Length; c++)
            {
                var cell = tb.GetCell(startRow + i, startCol + c);
                sb.Append(cell.Grapheme ?? "");
            }
            before[i] = sb.ToString();
        }

        // Force a scroll by moving cursor to bottom and issuing a few line feeds
        tb.SetCursor(rows - 1, 0);
        for (int i = 0; i < 3; i++) tb.LineFeed();

        // Re-render the same art at the same position
        for (int i = 0; i < art.Length; i++)
        {
            tb.SetCursor(startRow + i, startCol);
            tb.WriteText(art[i].AsSpan(), CellAttributes.Default);
        }

        // Verify that the re-rendered rows match the original art text
        for (int i = 0; i < art.Length; i++)
        {
            var sb = new StringBuilder();
            for (int c = 0; c < art[i].Length; c++)
            {
                var cell = tb.GetCell(startRow + i, startCol + c);
                sb.Append(cell.Grapheme ?? "");
            }
            var after = sb.ToString();
            Assert.Equal(art[i], after);
        }

        // Ensure no orphaned bases / continuation mismatches exist anywhere
        AssertNoOrphanedBases(tb);
    }
}
