using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.App.Tests;

public sealed class DecRectangleProtocolTests
{
    [Fact]
    public void Vt3RejectsEachRectangleCommandAndVt4AcceptsEach()
    {
        using var adapter = new TerminalAdapter(rows: 4, columns: 8);
        var parser = new BasicAnsiParser { Handler = adapter };
        FillDistinct(adapter.Buffer);
        adapter.OnSetConformanceLevel(63, 0);

        parser.Feed("\u001b[1;1;1;2;1;2;3;1$v"u8);
        AssertRows(adapter.Buffer, "abcdefgh", "ijklmnop", "qrstuvwx", "yzABCDEF");
        parser.Feed("\u001b[88;1;1;1;1$x"u8);
        AssertRows(adapter.Buffer, "abcdefgh", "ijklmnop", "qrstuvwx", "yzABCDEF");
        parser.Feed("\u001b[1;1;1;1$z"u8);
        AssertRows(adapter.Buffer, "abcdefgh", "ijklmnop", "qrstuvwx", "yzABCDEF");
        parser.Feed("\u001b[1;1;1;1${"u8);
        AssertRows(adapter.Buffer, "abcdefgh", "ijklmnop", "qrstuvwx", "yzABCDEF");

        adapter.OnSetConformanceLevel(64, 0);
        parser.Feed("\u001b[1;1;1;2;1;2;3;1$v"u8);
        Assert.Equal("ijabmnop", adapter.Buffer.GetRowText(1));
        parser.Feed("\u001b[88;1;1;1;1$x"u8);
        Assert.Equal("Xbcdefgh", adapter.Buffer.GetRowText(0));
        parser.Feed("\u001b[1;1;1;1$z"u8);
        Assert.Equal(" bcdefgh", adapter.Buffer.GetRowText(0));
        FillDistinct(adapter.Buffer);
        parser.Feed("\u001b[1;1;1;1${"u8);
        Assert.Equal(" bcdefgh", adapter.Buffer.GetRowText(0));
    }

    [Fact]
    public void CopyIsOverlapSafeAndClipsDestinationAndDefaults()
    {
        using var adapter = new TerminalAdapter(rows: 4, columns: 8);
        var parser = new BasicAnsiParser { Handler = adapter };
        FillDistinct(adapter.Buffer);

        parser.Feed("\u001b[1;1;1;2;2;2;1;2$v"u8);
        Assert.Equal("abklmnop", adapter.Buffer.GetRowText(1));
        FillDistinct(adapter.Buffer);

        parser.Feed("\u001b[1;1;1;5;1;1;3;1$v"u8);
        Assert.Equal("ababcdeh", adapter.Buffer.GetRowText(0));

        FillDistinct(adapter.Buffer);
        parser.Feed("\u001b[0;0;3;3;1;3;7;1$v"u8);
        Assert.Equal("qrstuvab", adapter.Buffer.GetRowText(2));
        Assert.Equal("yzABCDij", adapter.Buffer.GetRowText(3));

        FillDistinct(adapter.Buffer);
        parser.Feed("\u001b[1;1;2;2;1;3;1;1$v"u8);
        Assert.Equal("abcdefgh", adapter.Buffer.GetRowText(0));
        Assert.Equal("ijklmnop", adapter.Buffer.GetRowText(1));
        Assert.Equal("abstuvwx", adapter.Buffer.GetRowText(2));
        Assert.Equal("ijABCDEF", adapter.Buffer.GetRowText(3));
    }

    [Fact]
    public void OriginModeMapsDefaultsOnceAndIgnoresMarginsWhenDisabled()
    {
        using var adapter = new TerminalAdapter(rows: 4, columns: 8);
        var parser = new BasicAnsiParser { Handler = adapter };
        FillDistinct(adapter.Buffer);
        adapter.Buffer.SetScrollRegion(2, 3);
        adapter.Buffer.SetLeftRightMarginMode(true);
        adapter.Buffer.SetHorizontalMargins(3, 6);
        parser.Feed("\u001b[1;1;1;1$z"u8);
        Assert.Equal(" bcdefgh", adapter.Buffer.GetRowText(0));

        adapter.Buffer.SetOriginMode(true);
        parser.Feed("\u001b[1;1;0;0$z"u8);
        Assert.Equal(" bcdefgh", adapter.Buffer.GetRowText(0));
        Assert.Equal("ij    op", adapter.Buffer.GetRowText(1));
        Assert.Equal("qr    wx", adapter.Buffer.GetRowText(2));
        Assert.Equal("yzABCDEF", adapter.Buffer.GetRowText(3));
    }

    [Fact]
    public void SelectiveEraseRetainsStyleWhileDeceraResetsIt()
    {
        using var adapter = new TerminalAdapter(rows: 2, columns: 8);
        var parser = new BasicAnsiParser { Handler = adapter };
        parser.Feed("\u001b[1;38;2;230;35;65;48;2;17;39;201m\u001b[88;1;1;1;3$x"u8);
        var filled = adapter.Buffer.GetCell(0, 0);
        var attrs = adapter.Buffer.GetStyle(filled.StyleId);
        Assert.Equal((uint)'X', filled.Rune);
        Assert.True(attrs.Bold);
        Assert.Equal(0xFFE62341u, attrs.Foreground.Argb);
        Assert.Equal(0xFF1127C9u, attrs.Background.Argb);

        parser.Feed("\u001b[1;1;1;3${"u8);
        for (int column = 0; column < 3; column++)
        {
            var erased = adapter.Buffer.GetCell(0, column);
            Assert.Equal(32u, erased.Rune);
            Assert.Equal(filled.StyleId, erased.StyleId);
        }

        parser.Feed("\u001b[88;1;1;1;3$x"u8);
        parser.Feed("\u001b[1;1;1;3$z"u8);
        for (int column = 0; column < 3; column++)
        {
            var erased = adapter.Buffer.GetCell(0, column);
            Assert.Equal(32u, erased.Rune);
            Assert.Equal(0, erased.StyleId);
            Assert.False(erased.IsProtected);
        }
    }

    [Fact]
    public void SelectiveEraseProtectionDependsOnDecVersusIsoSelection()
    {
        using var adapter = new TerminalAdapter(rows: 2, columns: 8);
        var parser = new BasicAnsiParser { Handler = adapter };
        adapter.Buffer.SetDecProtection(1);
        parser.Feed("\u001b[88;1;1;1;3$x"u8);
        adapter.Buffer.SetDecProtection(0);
        parser.Feed("\u001b[1;1;1;3${"u8);
        Assert.Equal("XXX     ", adapter.Buffer.GetRowText(0));

        using var isoAdapter = new TerminalAdapter(rows: 2, columns: 8);
        var isoParser = new BasicAnsiParser { Handler = isoAdapter };
        isoAdapter.Buffer.SetIsoProtection(true);
        isoParser.Feed("\u001b[88;1;1;1;3$x"u8);
        isoParser.Feed("\u001b[1;1;1;3${"u8);
        Assert.Equal("   ", isoAdapter.Buffer.GetRowText(0)[..3]);
    }

    [Fact]
    public void InvalidFillCharactersAndReversedRangesAreIgnored()
    {
        using var adapter = new TerminalAdapter(rows: 3, columns: 8);
        var parser = new BasicAnsiParser { Handler = adapter };
        FillDistinct(adapter.Buffer);

        parser.Feed("\u001b[31;1;1;1;1$x\u001b[127;1;1;1;1$x\u001b[128;1;1;1;1$x\u001b[256;1;1;1;1$x"u8);
        parser.Feed("\u001b[3;1;1;8$z"u8);
        parser.Feed("\u001b[999999999999;1;1;8$z"u8);
        Assert.Equal("abcdefgh", adapter.Buffer.GetRowText(0));
        Assert.Equal("qrstuvwx", adapter.Buffer.GetRowText(2));

        parser.Feed("\u001b(0\u001b[113;1;1;1;1$x"u8);
        Assert.Equal(0x2500u, adapter.Buffer.GetCell(0, 0).Rune);
    }

    [Fact]
    public void RectangleMutationPreservesCursorAndOnlyDirtiesDestinationRows()
    {
        using var adapter = new TerminalAdapter(rows: 4, columns: 8);
        var parser = new BasicAnsiParser { Handler = adapter };
        parser.Feed("history\r\none\r\ntwo\r\nthree\r\n"u8);
        FillDistinct(adapter.Buffer);
        adapter.Buffer.SetCursor(2, 4);
        var generations = new ulong[4];

        for (int row = 0; row < 4; row++) generations[row] = adapter.Buffer.GetRowGeneration(row);
        int head = adapter.Buffer.ActiveBuffer.Head;
        int historyCount = adapter.Buffer.ScrollbackCount;
        string historyLine = adapter.Buffer.GetScrollbackLineText(0);

        parser.Feed("\u001b[1;1;1;2;1;2;2;1$v"u8);

        Assert.Equal(head, adapter.Buffer.ActiveBuffer.Head);
        Assert.Equal(historyCount, adapter.Buffer.ScrollbackCount);
        Assert.Equal(historyLine, adapter.Buffer.GetScrollbackLineText(0));

        Assert.Equal((2, 4), (adapter.Buffer.CursorRow, adapter.Buffer.CursorCol));
        Assert.Equal(generations[0], adapter.Buffer.GetRowGeneration(0));
        Assert.NotEqual(generations[1], adapter.Buffer.GetRowGeneration(1));
        Assert.Equal(generations[2], adapter.Buffer.GetRowGeneration(2));
        Assert.Equal(generations[3], adapter.Buffer.GetRowGeneration(3));
    }

    [Theory]
    [InlineData("\u001b[90;3;2;3;2$x", 90)]
    [InlineData("\u001b[3;2;3;2$z", 32)]
    [InlineData("\u001b[3;2;3;2${", 32)]
    [InlineData("\u001b[1;8;1;8;1;3;2;1$v", 88)]
    public void RectangleOperationsPreservePendingWrapForNextPrint(string sequence, int expectedRune)
    {
        using var adapter = new TerminalAdapter(rows: 3, columns: 8);
        var parser = new BasicAnsiParser { Handler = adapter };
        parser.Feed("\u001b[3;2HA\u001b[1;8HX"u8);
        parser.Feed(System.Text.Encoding.ASCII.GetBytes(sequence));
        parser.Feed("Y"u8);
        Assert.Equal((uint)'X', adapter.Buffer.GetCell(0, 7).Rune);
        Assert.Equal((uint)'Y', adapter.Buffer.GetCell(1, 0).Rune);
        Assert.Equal((uint)expectedRune, adapter.Buffer.GetCell(2, 1).Rune);
    }

    private static void FillDistinct(TerminalBuffer buffer)
    {
        string[] rows = ["abcdefgh", "ijklmnop", "qrstuvwx", "yzABCDEF"];
        for (int row = 0; row < buffer.Rows; row++)
        {
            buffer.SetCursor(row, 0);
            buffer.WriteText(rows[row % rows.Length].AsSpan(), CellAttributes.Default);
        }
    }

    private static void AssertRows(TerminalBuffer buffer, params string[] expected)
    {
        for (int row = 0; row < expected.Length; row++)
            Assert.Equal(expected[row], buffer.GetRowText(row));
    }
}
