using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

public sealed class RangeClearRegressionTests
{
    [Fact]
    public void EraseCharacters_ClearsWideGlyphsAtBothRangeEdgesAndColdMetadata()
    {
        var tb = new TerminalBuffer(rows: 2, columns: 12);
        ushort linkId = tb.GetOrCreateHyperlinkId("https://example.test");
        var linked = new CellAttributes { HyperlinkId = linkId };

        tb.SetCursor(0, 0);
        tb.WriteText("A界B".AsSpan(), CellAttributes.Default); // A=0, 界=1..2, B=3
        tb.SetCursor(0, 4);
        tb.WriteText("e\u0301".AsSpan(), linked); // grapheme at 4
        tb.WriteText("界".AsSpan(), linked);       // wide glyph at 5..6
        tb.WriteText("Z".AsSpan(), CellAttributes.Default);

        Assert.True(tb.GetCell(0, 4).HasGrapheme);
        Assert.Equal(linkId, tb.GetColdCell(0, 4).HyperlinkId);
        Assert.Equal(linkId, tb.GetColdCell(0, 5).HyperlinkId);
        Assert.Equal(linkId, tb.GetColdCell(0, 6).HyperlinkId);

        tb.SetCursor(0, 2);
        tb.EraseCharacters(4); // [2,6): clips both wide glyphs and includes grapheme

        foreach (int col in new[] { 1, 2, 3, 4, 5, 6 })
        {
            Assert.True(tb.GetCell(0, col).IsEmpty, $"column {col} was not cleared");
            var cold = tb.GetColdCell(0, col);
            Assert.Equal((ushort)0, cold.HyperlinkId);
            Assert.Equal(-1, cold.GraphemeIndex);
        }
        Assert.Equal((uint)'A', tb.GetCell(0, 0).Rune);
        Assert.Equal((uint)'Z', tb.GetCell(0, 7).Rune);

        var screen = tb.ActiveScreenForTests;
        int physicalRow = screen.GetPhysicalRow(0);
        Assert.Equal(7, screen.RowMaxCol[physicalRow]);
        Assert.Equal(7, screen.RowEndCol[physicalRow]);

        tb.SetCursor(0, 7);
        tb.EraseCharacters(1);
        Assert.Equal(0, screen.RowMaxCol[physicalRow]);
        Assert.Equal(0, screen.RowEndCol[physicalRow]);
        Assert.Empty(tb.ValidateInvariants());
    }
}
