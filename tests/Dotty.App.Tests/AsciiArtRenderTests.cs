using Xunit;
using Dotty.Terminal.Adapter;
using System.Text;

namespace Dotty.App.Tests;

public class AsciiArtRenderTests
{
    private static void AssertNoOrphanedBases(TerminalBuffer tb)
    {
        var violations = tb.ValidateInvariants();
        Assert.True(violations.Count == 0,
            "Buffer invariants violated:" + Environment.NewLine + string.Join(Environment.NewLine, violations));
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
