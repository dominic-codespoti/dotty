using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.App.Tests;

public sealed class DecColumnProtocolTests
{
    [Fact]
    public void Vt3SuppressesAllFourColumnControlsAndVt4ExecutesThem()
    {
        using var adapter = new TerminalAdapter(rows: 4, columns: 8);
        var parser = new BasicAnsiParser { Handler = adapter };
        Fill(adapter.Buffer);
        adapter.OnSetConformanceLevel(63, 0);

        adapter.Buffer.SetCursor(0, 1);
        parser.Feed("\u001b[2'}X"u8);
        adapter.Buffer.SetCursor(0, 2);
        parser.Feed("\u001b[1'~Y"u8);
        adapter.Buffer.SetCursor(0, 3);
        parser.Feed("\u001b6Z"u8);
        adapter.Buffer.SetCursor(0, 4);
        parser.Feed("\u001b9W"u8);
        Assert.Equal("aXYZWfgh", adapter.Buffer.GetRowText(0));
        for (int row = 1; row < adapter.Buffer.Rows; row++)
            Assert.Equal("abcdefgh", adapter.Buffer.GetRowText(row));

        adapter.OnSetConformanceLevel(64, 0);
        adapter.Buffer.SetCursor(1, 3);
        parser.Feed("\u001b[?2'}\u001b[2 '}"u8);
        Assert.Equal("aXYZWfgh", adapter.Buffer.GetRowText(0));
        for (int row = 1; row < adapter.Buffer.Rows; row++)
            Assert.Equal("abcdefgh", adapter.Buffer.GetRowText(row));

        Fill(adapter.Buffer);
        adapter.Buffer.SetCursor(1, 3);
        parser.Feed("\u001b['}"u8);
        Assert.Equal("abc defg", adapter.Buffer.GetRowText(0));
        Assert.Equal("abc defg", adapter.Buffer.GetRowText(3));

        parser.Feed("\u001b[0'}"u8);
        Assert.Equal("abc  def", adapter.Buffer.GetRowText(1));
        parser.Feed("\u001b[999999999999'}"u8);
        Assert.Equal("abc     ", adapter.Buffer.GetRowText(2));

        Fill(adapter.Buffer);
        adapter.Buffer.SetScrollRegion(2, 3);
        adapter.Buffer.SetLeftRightMarginMode(true);
        adapter.Buffer.SetHorizontalMargins(3, 6);
        adapter.Buffer.SetCursor(2, 2);
        parser.Feed("\u001b6"u8);
        Assert.Equal("ab cdegh", adapter.Buffer.GetRowText(1));
        Assert.Equal("ab cdegh", adapter.Buffer.GetRowText(2));
        adapter.Buffer.SetCursor(1, 5);
        parser.Feed("\u001b9"u8);
        Assert.Equal("abcde gh", adapter.Buffer.GetRowText(1));
    }


    [Fact]
    public void ParserDeleteColumnsTreatsOmittedAndZeroCountsAsOneAndSaturatesLargeCounts()
    {
        using var adapter = new TerminalAdapter(rows: 3, columns: 8);
        var parser = new BasicAnsiParser { Handler = adapter };
        Fill(adapter.Buffer);
        adapter.OnSetConformanceLevel(64, 0);
        adapter.Buffer.SetCursor(1, 3);

        parser.Feed("\u001b['~"u8);

        Assert.Equal("abcefgh ", adapter.Buffer.GetRowText(0));
        Assert.Equal("abcefgh ", adapter.Buffer.GetRowText(1));
        Assert.Equal("abcefgh ", adapter.Buffer.GetRowText(2));

        Fill(adapter.Buffer);
        adapter.Buffer.SetCursor(1, 3);
        parser.Feed("\u001b[0'~"u8);
        Assert.Equal("abcefgh ", adapter.Buffer.GetRowText(1));

        Fill(adapter.Buffer);
        adapter.Buffer.SetCursor(1, 3);
        parser.Feed("\u001b[999999999999'~"u8);
        Assert.Equal("abc     ", adapter.Buffer.GetRowText(1));
    }

    [Fact]
    public void EligibleColumnEditsPreservePendingWrapAndBackIndexConsumesItWithReverseWrap()
    {
        using var deleteAdapter = new TerminalAdapter(rows: 3, columns: 8);
        var deleteParser = new BasicAnsiParser { Handler = deleteAdapter };
        deleteAdapter.Buffer.SetCursor(0, 7);
        deleteParser.Feed("X\u001b[1'~Y"u8);
        Assert.Equal(0u, deleteAdapter.Buffer.GetCell(0, 7).Rune);
        Assert.Equal((uint)'Y', deleteAdapter.Buffer.GetCell(1, 0).Rune);

        using var indexAdapter = new TerminalAdapter(rows: 3, columns: 8);
        var indexParser = new BasicAnsiParser { Handler = indexAdapter };
        indexAdapter.Buffer.SetCursor(0, 7);
        indexParser.Feed("X\u001b9Y"u8);
        Assert.Equal(0u, indexAdapter.Buffer.GetCell(0, 7).Rune);
        Assert.Equal((uint)'Y', indexAdapter.Buffer.GetCell(1, 0).Rune);

        using var reverseAdapter = new TerminalAdapter(rows: 3, columns: 8);
        var reverseParser = new BasicAnsiParser { Handler = reverseAdapter };
        reverseAdapter.Buffer.SetReverseWrapMode(45, true);
        reverseAdapter.Buffer.SetCursor(0, 7);
        reverseParser.Feed("X\u001b6Y"u8);
        Assert.Equal((uint)'Y', reverseAdapter.Buffer.GetCell(0, 7).Rune);
        Assert.Equal(0u, reverseAdapter.Buffer.GetCell(1, 0).Rune);
    }

    [Fact]
    public void VerticalIneligibleForwardIndexPreservesPendingWrapForNextPrint()
    {
        using var adapter = new TerminalAdapter(rows: 4, columns: 8);
        var parser = new BasicAnsiParser { Handler = adapter };
        adapter.Buffer.SetScrollRegion(2, 3);
        adapter.Buffer.SetLeftRightMarginMode(true);
        adapter.Buffer.SetHorizontalMargins(3, 6);
        adapter.Buffer.SetCursor(0, 5);

        parser.Feed("X\u001b9Y"u8);

        Assert.Equal((uint)'X', adapter.Buffer.GetCell(0, 5).Rune);
        Assert.Equal((uint)'Y', adapter.Buffer.GetCell(1, 2).Rune);
    }

    private static void Fill(TerminalBuffer buffer)
    {
        for (int row = 0; row < buffer.Rows; row++)
        {
            buffer.SetCursor(row, 0);
            buffer.WriteText("abcdefgh".AsSpan(), CellAttributes.Default);
        }
    }
}
