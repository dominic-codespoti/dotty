using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Graphics;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

/// <summary>
/// Grapheme clusters split across parser writes are re-joined with the cell they started in, and the cell is
/// re-measured (a late VS16 widens it, a late VS15 narrows it). Splitting the same bytes anywhere must leave the
/// same screen as feeding them at once.
/// </summary>
public sealed class BufferTextWriterClusterTests
{
    private const int Rows = 3;
    private const int Cols = 4;
    private const string Blank = "[ ][ ][ ][ ]";
    private const string Heart = "\u2764\uFE0F";
    private const string Flag = "\U0001F1FA\U0001F1F8";
    private const string Family = "\U0001F468\u200D\U0001F469\u200D\U0001F467";
    private const string Keycap = "1\uFE0F\u20E3";
    private const string HangulLvt = "\u1100\u1161\u11A8";
    private const string ThumbsUpTone = "\U0001F44D\U0001F3FD";
    private const string WatchText = "\u231A\uFE0E";

    private static (TerminalAdapter Adapter, BasicAnsiParser Parser) Setup(int rows = Rows, int cols = Cols)
    {
        var adapter = new TerminalAdapter(rows, cols);
        return (adapter, new BasicAnsiParser { Handler = adapter });
    }

    private static void Feed(BasicAnsiParser parser, string text) => parser.Feed(Encoding.UTF8.GetBytes(text));

    /// <summary>Rows as "[text]" cells ("[~]" continuation, ":2" wide, leading "&gt;" soft-wrapped), then the cursor.</summary>
    private static string Dump(TerminalAdapter adapter)
    {
        var buffer = adapter.Buffer;
        var sb = new StringBuilder();
        for (int row = 0; row < buffer.Rows; row++)
        {
            if (row > 0) sb.Append('|');
            if (buffer.ActiveBuffer.GetRowContinuesPrevious(row)) sb.Append('>');
            for (int col = 0; col < buffer.Columns; col++)
            {
                CellHot cell = buffer.GetCell(row, col);
                if (cell.IsContinuation) { sb.Append("[~]"); continue; }
                if (cell.IsEmpty) { sb.Append("[ ]"); continue; }
                sb.Append('[').Append(GraphemeHelper.Resolve(cell.Rune, buffer.GetColdCell(row, col).GraphemeIndex));
                if (cell.Width == 2) sb.Append(":2");
                sb.Append(']');
            }
        }
        sb.Append('@').Append(buffer.CursorRow).Append(',').Append(buffer.CursorCol);
        return sb.ToString();
    }

    public static TheoryData<string, string> Inputs => new()
    {
        { "a" + Heart + "b", $"[a][{Heart}:2][~][b]|{Blank}|{Blank}@0,3" },
        { Flag + "x", $"[{Flag}:2][~][x][ ]|{Blank}|{Blank}@0,3" },
        { Family + "x", $"[{Family}:2][~][x][ ]|{Blank}|{Blank}@0,3" },
        { Keycap + "x", $"[{Keycap}:2][~][x][ ]|{Blank}|{Blank}@0,3" },
        { HangulLvt + "x", $"[{HangulLvt}:2][~][x][ ]|{Blank}|{Blank}@0,3" },
        { ThumbsUpTone + "x", $"[{ThumbsUpTone}:2][~][x][ ]|{Blank}|{Blank}@0,3" },
        { "e\u0301\u0302x", $"[e\u0301\u0302][x][ ][ ]|{Blank}|{Blank}@0,2" },
        { "\u06001x", $"[\u06001][x][ ][ ]|{Blank}|{Blank}@0,2" },
        { WatchText + "x", $"[{WatchText}][x][ ][ ]|{Blank}|{Blank}@0,2" },
        { "abc" + Heart, $"[a][b][c][ ]|[{Heart}:2][~][ ][ ]|{Blank}@1,2" },
        { "abc" + Flag, $"[a][b][c][ ]|[{Flag}:2][~][ ][ ]|{Blank}@1,2" },
        { "abc\u1100\u1161", $"[a][b][c][ ]|[\u1100\u1161:2][~][ ][ ]|{Blank}@1,2" },
        { "ab" + Heart + "c", $"[a][b][{Heart}:2][~]|>[c][ ][ ][ ]|{Blank}@1,1" },
    };

    [Theory]
    [MemberData(nameof(Inputs))]
    public void WholeFeed_ProducesExpectedCells(string input, string expected)
    {
        var (adapter, parser) = Setup();
        Feed(parser, input);
        Assert.Equal(expected, Dump(adapter));
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void SplitAtEveryByteBoundary_MatchesWholeFeed(string input, string expected)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(input);
        for (int split = 1; split < bytes.Length; split++)
        {
            var (adapter, parser) = Setup();
            parser.Feed(bytes.AsSpan(0, split));
            parser.Feed(bytes.AsSpan(split));
            Assert.True(expected == Dump(adapter), $"split at byte {split}: {Dump(adapter)}");
        }
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void OneByteAtATime_MatchesWholeFeed(string input, string expected)
    {
        var (adapter, parser) = Setup();
        byte[] bytes = Encoding.UTF8.GetBytes(input);
        for (int i = 0; i < bytes.Length; i++) parser.Feed(bytes.AsSpan(i, 1));
        Assert.Equal(expected, Dump(adapter));
    }

    [Fact]
    public void LateVs16_WidensInPlaceAndMovesTheCursor()
    {
        var (adapter, parser) = Setup(rows: 2, cols: 8);
        Feed(parser, "a\u2764");
        Assert.Equal(1, adapter.Buffer.GetCell(0, 1).Width);
        Assert.Equal(2, adapter.Buffer.CursorCol);

        Feed(parser, "\uFE0F");
        Assert.Equal(2, adapter.Buffer.GetCell(0, 1).Width);
        Assert.True(adapter.Buffer.GetCell(0, 2).IsContinuation);
        Assert.Equal(3, adapter.Buffer.CursorCol);
    }

    [Fact]
    public void LateVs16_AtTheRightEdge_WithAutowrapTurnedOff_KeepsOneCell()
    {
        var (adapter, parser) = Setup();
        Feed(parser, "abc\u2764");      // the heart fills the last column; a wrap is pending
        Feed(parser, "\u001b[?7l");     // DECAWM off: a wide cell can no longer move to the next line
        Feed(parser, "\uFE0F");

        CellHot cell = adapter.Buffer.GetCell(0, 3);
        Assert.Equal(1, cell.Width);
        Assert.Equal(Heart, GraphemeHelper.Resolve(cell.Rune, adapter.Buffer.GetColdCell(0, 3).GraphemeIndex));
        Assert.Equal('c', (char)adapter.Buffer.GetCell(0, 2).Rune);
        Assert.True(adapter.Buffer.GetCell(1, 0).IsEmpty);
        Assert.Equal(0, adapter.Buffer.CursorRow);
    }

    [Fact]
    public void LateVs16_OnAFullCell_IsDroppedAndDoesNotWiden()
    {
        var (adapter, parser) = Setup(rows: 2, cols: 8);
        string capped = "\u2764" + string.Concat(Enumerable.Repeat("\u0301", 64));
        Feed(parser, capped);
        Feed(parser, "\uFE0F");

        CellHot cell = adapter.Buffer.GetCell(0, 0);
        Assert.Equal(1, cell.Width);
        Assert.Equal(capped, GraphemeHelper.Resolve(cell.Rune, adapter.Buffer.GetColdCell(0, 0).GraphemeIndex));
        Assert.Equal(1, adapter.Buffer.CursorCol);
    }

    [Fact]
    public void LateVs16_AfterAPlaceholder_LeavesItOneCellWide()
    {
        var (adapter, parser) = Setup(rows: 2, cols: 8);
        Feed(parser, "\U0010EEEE\u0305\u0305");
        short code = adapter.Buffer.GetColdCell(0, 0).GraphemeIndex;
        Feed(parser, "\uFE0F");

        CellHot cell = adapter.Buffer.GetCell(0, 0);
        Assert.Equal((uint)PlaceholderTable.PlaceholderRune, cell.Rune);
        Assert.Equal(1, cell.Width);
        Assert.Equal(code, adapter.Buffer.GetColdCell(0, 0).GraphemeIndex);
        Assert.Equal(1, adapter.Buffer.CursorCol);
    }

    [Fact]
    public void LateVs16_AtTheStartOfARow_DoesNotWidenTheCellOnThePreviousRow()
    {
        var (adapter, parser) = Setup();
        Feed(parser, "abc\u2764\r\n");
        Feed(parser, "\uFE0F");

        // The variation selector is kept with the heart (as any zero-width text is) but nothing moves or widens.
        CellHot heart = adapter.Buffer.GetCell(0, 3);
        Assert.Equal(1, heart.Width);
        Assert.Equal(Heart, GraphemeHelper.Resolve(heart.Rune, adapter.Buffer.GetColdCell(0, 3).GraphemeIndex));
        Assert.True(adapter.Buffer.GetCell(1, 0).IsEmpty);
        Assert.Equal(1, adapter.Buffer.CursorRow);
        Assert.Equal(0, adapter.Buffer.CursorCol);
    }

    [Fact]
    public void NonAsciiWriteOnASoftWrappedRow_KeepsTheRowWrapped()
    {
        var (adapter, parser) = Setup();
        Feed(parser, "abcde\u00E9");

        Assert.True(adapter.Buffer.ActiveBuffer.GetRowContinuesPrevious(1));
        Assert.Equal('\u00E9', (char)adapter.Buffer.GetCell(1, 1).Rune);
    }

    [Fact]
    public void WideCharacterThatDoesNotFitOnTheBottomRow_Scrolls()
    {
        var (adapter, parser) = Setup(rows: 2, cols: 4);
        Feed(parser, "abcd\r\nefg\u754C");

        Assert.Equal('e', (char)adapter.Buffer.GetCell(0, 0).Rune);
        Assert.Equal('g', (char)adapter.Buffer.GetCell(0, 2).Rune);
        CellHot wide = adapter.Buffer.GetCell(1, 0);
        Assert.Equal(0x754Cu, wide.Rune);
        Assert.Equal(2, wide.Width);
        Assert.True(adapter.Buffer.GetCell(1, 1).IsContinuation);
        Assert.Equal(1, adapter.Buffer.CursorRow);
        Assert.Equal(2, adapter.Buffer.CursorCol);
    }
}
