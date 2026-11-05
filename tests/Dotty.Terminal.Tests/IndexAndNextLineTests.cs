using System;
using System.Linq;
using System.Text;
using Dotty.Abstractions.Graphics;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

/// <summary>
/// ESC D (IND) and ESC E (NEL) through a real parser and adapter. IND preserves the column; NEL indexes before
/// returning to column one. Both cancel a pending wrap, respect margins and origin mode, and exercise image hooks.
///
/// </summary>
[Collection(nameof(GraphicsMemoryMetricsCollection))]
public sealed class IndexAndNextLineTests
{
    private const string Esc = "\u001b";

    private sealed class Rig : IDisposable
    {
        public TerminalAdapter Adapter { get; }
        public BasicAnsiParser Parser { get; }

        public Rig(int rows = 5, int columns = 10, int capacity = 100)
        {
            Adapter = new TerminalAdapter(rows, columns, capacity);
            Adapter.SetCellPixelSize(10, 20);
            Adapter.ConfigureGraphics(new KittyGraphicsOptions { Enabled = true }, new FakeImageDecoder());
            Parser = new BasicAnsiParser { Handler = Adapter };
        }

        public TerminalBuffer Buffer => Adapter.Buffer;

        public void Feed(string text) => Parser.Feed(Encoding.ASCII.GetBytes(text));

        public void FeedByteByByte(string text)
        {
            foreach (byte b in Encoding.ASCII.GetBytes(text))
                Parser.Feed(new[] { b });
        }

        public string Row(int row) => Buffer.GetRowText(row).TrimEnd();

        public void Goto(int row0, int col0) => Feed(Esc + "[" + (row0 + 1) + ";" + (col0 + 1) + "H");

        public void Dispose() => Adapter.Dispose();
    }


    [Fact]
    public void NewLineModeAppliesCarriageReturnToLfVtAndFfButNotInd()
    {
        using var t = new Rig();
        t.Feed("abcd\r\u001b[?20h\n");
        Assert.Equal((1, 0), (t.Buffer.CursorRow, t.Buffer.CursorCol));
        t.Feed("xy\r\u000b");
        Assert.Equal((2, 0), (t.Buffer.CursorRow, t.Buffer.CursorCol));
        t.Feed("zz\r\u000c");
        Assert.Equal((3, 0), (t.Buffer.CursorRow, t.Buffer.CursorCol));

        t.Goto(0, 4);
        t.Feed(Esc + "D");
        Assert.Equal((1, 4), (t.Buffer.CursorRow, t.Buffer.CursorCol));
        t.Feed(Esc + "E");
        Assert.Equal((2, 0), (t.Buffer.CursorRow, t.Buffer.CursorCol));
    }
    [Fact]
    public void IndexAtTheBottomRowScrollsTheScreenAndKeepsTheColumn()
    {
        using var t = new Rig(rows: 3);
        t.Feed("a\r\nb\r\nc");
        Assert.Equal((2, 1), (t.Buffer.CursorRow, t.Buffer.CursorCol));

        t.Feed(Esc + "D");

        Assert.Equal((2, 1), (t.Buffer.CursorRow, t.Buffer.CursorCol));
        Assert.Equal("b", t.Row(0));
        Assert.Equal("c", t.Row(1));
        Assert.Equal("", t.Row(2));

        t.Feed("Z");
        Assert.Equal(" Z", t.Row(2));
    }

    [Fact]
    public void IndexAboveTheBottomMovesDownOnlyAndKeepsTheColumn()
    {
        using var t = new Rig();
        t.Feed("ab\r\ncd");
        t.Goto(0, 1);

        t.Feed(Esc + "D");

        Assert.Equal((1, 1), (t.Buffer.CursorRow, t.Buffer.CursorCol));
        Assert.Equal("ab", t.Row(0));
        Assert.Equal("cd", t.Row(1));
        Assert.Equal(0, t.Buffer.ScrollbackCount);
    }

    [Fact]
    public void IndexDiffersFromNextLineOnlyByTheCarriageReturn()
    {
        using var t = new Rig();
        t.Feed("abc");
        t.Feed(Esc + "D");
        t.Feed("X");
        Assert.Equal("abc", t.Row(0));
        Assert.Equal("   X", t.Row(1));

        t.Feed(Esc + "E");
        t.Feed("Y");
        Assert.Equal("Y", t.Row(2));
        Assert.Equal((2, 1), (t.Buffer.CursorRow, t.Buffer.CursorCol));
    }

    [Fact]
    public void NextLineReturnsToColumnOneMidScreen()
    {
        using var t = new Rig();
        t.Feed("ab" + Esc + "E" + "Z");

        Assert.Equal("ab", t.Row(0));
        Assert.Equal("Z", t.Row(1));
        Assert.Equal((1, 1), (t.Buffer.CursorRow, t.Buffer.CursorCol));
    }

    [Fact]
    public void NextLineAtTheBottomScrollsAndReturnsToColumnOne()
    {
        using var t = new Rig(rows: 3);
        t.Feed("a\r\nb\r\ncde");

        t.Feed(Esc + "E");

        Assert.Equal((2, 0), (t.Buffer.CursorRow, t.Buffer.CursorCol));
        Assert.Equal("b", t.Row(0));
        Assert.Equal("cde", t.Row(1));
        Assert.Equal("", t.Row(2));
        Assert.Equal(1, t.Buffer.ScrollbackCount);
    }

    [Fact]
    public void RepeatedIndexAtTheBottomGrowsScrollbackByOneEach()
    {
        using var t = new Rig(rows: 3);
        t.Goto(2, 0);

        for (int i = 1; i <= 7; i++)
        {
            t.Feed(Esc + "D");
            Assert.Equal(i, t.Buffer.ScrollbackCount);
        }
    }

    [Fact]
    public void IndexAndNextLineAtTheBottomPushTheTopLineIntoScrollback()
    {
        using var t = new Rig(rows: 3);
        t.Feed("one\r\ntwo\r\nthree");

        t.Feed(Esc + "D");
        t.Feed(Esc + "E");

        Assert.Equal(2, t.Buffer.ScrollbackCount);
        var lines = t.Buffer.GetScrollbackLines();
        Assert.Equal("one", lines[0].TrimEnd());
        Assert.Equal("two", lines[1].TrimEnd());
    }

    [Fact]
    public void IndexCancelsAPendingWrapMidScreen()
    {
        using var t = new Rig();
        t.Feed("0123456789");   // cursor parked on the last column with a wrap pending

        t.Feed(Esc + "D" + "X");

        Assert.Equal("0123456789", t.Row(0));
        Assert.Equal(new string(' ', 9) + "X", t.Row(1));
        Assert.Equal("", t.Row(2));
    }

    [Fact]
    public void IndexCancelsAPendingWrapWhenItScrolls()
    {
        using var t = new Rig(rows: 2);
        t.Feed("\r\n0123456789");

        t.Feed(Esc + "D" + "X");

        Assert.Equal("0123456789", t.Row(0));
        Assert.Equal(new string(' ', 9) + "X", t.Row(1));
    }

    [Fact]
    public void NextLineCancelsAPendingWrap()
    {
        using var t = new Rig();
        t.Feed("0123456789");

        t.Feed(Esc + "E" + "X");

        Assert.Equal("0123456789", t.Row(0));
        Assert.Equal("X", t.Row(1));
        Assert.Equal("", t.Row(2));
    }

    [Fact]
    public void IndexAtTheBottomMarginScrollsOnlyTheRegion()
    {
        using var t = new Rig(rows: 5);
        t.Feed(Esc + "[2;3r");   // rows 1..2 (0-based)
        t.Goto(0, 0); t.Feed("A");
        t.Goto(1, 0); t.Feed("B");
        t.Goto(2, 0); t.Feed("C");
        t.Goto(3, 0); t.Feed("D");
        t.Goto(2, 0);

        t.Feed(Esc + "D");

        Assert.Equal("A", t.Row(0));
        Assert.Equal("C", t.Row(1));
        Assert.Equal("", t.Row(2));
        Assert.Equal("D", t.Row(3));
        Assert.Equal(2, t.Buffer.CursorRow);
        Assert.Equal(0, t.Buffer.ScrollbackCount);
    }

    [Fact]
    public void IndexInsideTheRegionAboveItsBottomMarginOnlyMoves()
    {
        using var t = new Rig(rows: 5);
        t.Feed(Esc + "[2;4r");
        t.Goto(1, 0); t.Feed("B");
        t.Goto(1, 0);

        t.Feed(Esc + "D");

        Assert.Equal(2, t.Buffer.CursorRow);
        Assert.Equal("B", t.Row(1));
    }

    [Fact]
    public void IndexBelowTheBottomMarginMovesDownToTheLastRowAndNeverScrolls()
    {
        using var t = new Rig(rows: 5);
        t.Feed(Esc + "[2;3r");   // region rows 1..2; rows 3 and 4 lie below it
        t.Goto(1, 0); t.Feed("B");
        t.Goto(2, 0); t.Feed("C");
        t.Goto(3, 2);

        t.Feed(Esc + "D");
        Assert.Equal((4, 2), (t.Buffer.CursorRow, t.Buffer.CursorCol));

        t.Feed(Esc + "D");       // already on the last row: nothing moves and nothing scrolls
        Assert.Equal((4, 2), (t.Buffer.CursorRow, t.Buffer.CursorCol));
        Assert.Equal("B", t.Row(1));
        Assert.Equal("C", t.Row(2));
        Assert.Equal(0, t.Buffer.ScrollbackCount);
    }

    [Fact]
    public void NextLineBelowTheBottomMarginMovesDownAndReturnsToColumnOneWithoutScrolling()
    {
        using var t = new Rig(rows: 5);
        t.Feed(Esc + "[2;3r");
        t.Goto(3, 4);

        t.Feed(Esc + "E");
        Assert.Equal((4, 0), (t.Buffer.CursorRow, t.Buffer.CursorCol));

        t.Goto(4, 3);
        t.Feed(Esc + "E");       // last row: the carriage return still happens, nothing scrolls
        Assert.Equal((4, 0), (t.Buffer.CursorRow, t.Buffer.CursorCol));
        Assert.Equal(0, t.Buffer.ScrollbackCount);
    }

    [Fact]
    public void IndexBelowTheBottomMarginCancelsAPendingWrap()
    {
        using var t = new Rig(rows: 5);
        t.Feed(Esc + "[2;3r");
        t.Goto(3, 0);
        t.Feed("0123456789");    // wrap pending on the last column

        t.Feed(Esc + "D" + "X");

        Assert.Equal("0123456789", t.Row(3));
        Assert.Equal(new string(' ', 9) + "X", t.Row(4));
    }

    [Fact]
    public void IndexOnTheLastRowBelowTheMarginStillCancelsAPendingWrap()
    {
        using var t = new Rig(rows: 5);
        t.Feed(Esc + "[2;3r");
        t.Goto(4, 0);
        t.Feed("0123456789");    // wrap pending on the last column of the last row

        t.Feed(Esc + "D" + "X");

        Assert.Equal("012345678X", t.Row(4));
        Assert.Equal(0, t.Buffer.ScrollbackCount);
    }

    [Fact]
    public void NextLineAtTheBottomMarginScrollsTheRegionAndReturnsToColumnOne()
    {
        using var t = new Rig(rows: 5);
        t.Feed(Esc + "[2;3r");
        t.Goto(1, 0); t.Feed("B");
        t.Goto(2, 0); t.Feed("Cxy");

        t.Feed(Esc + "E");

        Assert.Equal("Cxy", t.Row(1));
        Assert.Equal("", t.Row(2));
        Assert.Equal((2, 0), (t.Buffer.CursorRow, t.Buffer.CursorCol));
        Assert.Equal(0, t.Buffer.ScrollbackCount);
    }

    [Fact]
    public void OriginModeIndexMovesWithinTheRegionAndScrollsAtItsBottom()
    {
        using var t = new Rig(rows: 6);
        t.Feed(Esc + "[3;4r" + Esc + "[?6h");   // region rows 2..3; DECOM homes to the region top
        Assert.Equal(2, t.Buffer.CursorRow);
        t.Feed("p");

        t.Feed(Esc + "D");
        Assert.Equal(3, t.Buffer.CursorRow);
        Assert.Equal(1, t.Buffer.CursorCol);

        t.Feed("q");
        t.Feed(Esc + "D");   // bottom margin: scroll, cursor stays
        Assert.Equal(3, t.Buffer.CursorRow);
        Assert.Equal("q", t.Row(2).TrimStart());
        Assert.Equal("", t.Row(3));
        Assert.Equal(1, t.Buffer.CursorRowForReport);
        Assert.Equal(0, t.Buffer.ScrollbackCount);
    }

    [Fact]
    public void OriginModeNextLineReturnsToTheFirstColumnOfTheNextRegionRow()
    {
        using var t = new Rig(rows: 6);
        t.Feed(Esc + "[3;4r" + Esc + "[?6h");
        t.Feed("abc" + Esc + "E" + "Z");

        Assert.Equal("abc", t.Row(2));
        Assert.Equal("Z", t.Row(3));
        Assert.Equal(1, t.Buffer.CursorRowForReport);
    }

    [Fact]
    public void SequencesSplitAcrossFeedsBehaveLikeUnsplitOnes()
    {
        using var whole = new Rig(rows: 3);
        using var split = new Rig(rows: 3);
        string stream = "a\r\nb" + Esc + "Dc" + Esc + "E" + "d" + Esc + "Dz";

        whole.Feed(stream);
        split.FeedByteByByte(stream);

        for (int r = 0; r < 3; r++)
            Assert.Equal(whole.Row(r), split.Row(r));
        Assert.Equal((whole.Buffer.CursorRow, whole.Buffer.CursorCol), (split.Buffer.CursorRow, split.Buffer.CursorCol));
        Assert.Equal(whole.Buffer.ScrollbackCount, split.Buffer.ScrollbackCount);
        Assert.True(split.Buffer.ScrollbackCount >= 1);
    }

    [Fact]
    public void AnEscapeThatSupersedesAnUnfinishedCsiStillDispatchesIndex()
    {
        using var t = new Rig();
        t.Feed("ab" + Esc + "[1;" + Esc + "D" + "X");

        Assert.Equal("ab", t.Row(0));
        Assert.Equal("  X", t.Row(1));
    }

    [Fact]
    public void IndexDoesNotDisturbReverseIndexOrLineFeed()
    {
        using var t = new Rig(rows: 4);
        t.Goto(1, 2);
        t.Feed(Esc + "M");
        Assert.Equal((0, 2), (t.Buffer.CursorRow, t.Buffer.CursorCol));
        t.Feed("\n");
        Assert.Equal((1, 2), (t.Buffer.CursorRow, t.Buffer.CursorCol));
        t.Feed(Esc + "D");
        Assert.Equal((2, 2), (t.Buffer.CursorRow, t.Buffer.CursorCol));
    }

    [Fact]
    public void IndexAboveTheTopMarginMovesDownAndNeverScrolls()
    {
        using var t = new Rig(rows: 6);
        t.Feed(Esc + "[3;5r");   // region rows 2..4 (0-based)
        t.Goto(0, 0); t.Feed("A");
        t.Goto(1, 0); t.Feed("B");
        t.Goto(0, 1);

        t.Feed(Esc + "D");
        Assert.Equal((1, 1), (t.Buffer.CursorRow, t.Buffer.CursorCol));
        t.Feed(Esc + "D");
        Assert.Equal((2, 1), (t.Buffer.CursorRow, t.Buffer.CursorCol));   // entered the region at its top margin: moved, not scrolled

        Assert.Equal("A", t.Row(0));
        Assert.Equal("B", t.Row(1));
        Assert.Equal(0, t.Buffer.ScrollbackCount);
    }

    [Fact]
    public void IndexAtTheBottomMarginCancelsAPendingWrapToo()
    {
        using var t = new Rig(rows: 5);
        t.Feed(Esc + "[2;3r");   // region rows 1..2 (0-based)
        t.Goto(2, 0);
        t.Feed("0123456789");    // parked on the last column of the bottom margin row with a wrap pending

        t.Feed(Esc + "D" + "X");

        Assert.Equal("0123456789", t.Row(1));
        Assert.Equal(new string(' ', 9) + "X", t.Row(2));
        Assert.Equal(0, t.Buffer.ScrollbackCount);
    }

    [Fact]
    public void IndexAndNextLineLeaveTheTabStopsAlone()
    {
        using var t = new Rig(columns: 20);
        t.Feed("abc" + Esc + "D" + "\t" + "X");   // column 3 is kept, so the next default stop is column 8
        t.Feed(Esc + "E" + "\t" + "Y");           // column 0, the same next stop

        Assert.Equal(new string(' ', 8) + "X", t.Row(1));
        Assert.Equal(new string(' ', 8) + "Y", t.Row(2));
    }

    [Fact]
    public void CharsetDesignationsEndingInDOrEAreNotIndexOrNextLine()
    {
        using var t = new Rig();
        t.Feed("ab" + Esc + "(D" + "c" + Esc + ")E" + "d");

        Assert.Equal("abcd", t.Row(0));
        Assert.Equal("", t.Row(1));
        Assert.Equal((0, 4), (t.Buffer.CursorRow, t.Buffer.CursorCol));
    }

    [Fact]
    public void ControlCharactersInsideTheEscapeSequenceExecuteBeforeTheFinal()
    {
        using var t = new Rig();
        t.Feed("ab" + Esc + "\r" + "D" + "X");   // the CR executes and the sequence keeps waiting for its final byte

        Assert.Equal("ab", t.Row(0));
        Assert.Equal("X", t.Row(1));
        Assert.Equal((1, 1), (t.Buffer.CursorRow, t.Buffer.CursorCol));
    }

    [Theory]
    [InlineData("", 'D')]                                  // ESC ESC D: the second ESC supersedes the first
    [InlineData("", 'E')]
    [InlineData("(", 'D')]                                 // an unfinished charset designation
    [InlineData(")", 'E')]
    [InlineData("]0;title", 'D')]                          // OSC: abandoned without dispatch
    [InlineData("]0;title", 'E')]
    [InlineData("P+q", 'D')]                               // DCS
    [InlineData("P+q", 'E')]
    [InlineData("^pm", 'D')]                               // PM, SOS and a foreign APC are discarded strings
    [InlineData("Xsos", 'E')]
    [InlineData("_Zapc", 'D')]
    [InlineData("_Ga=t,i=1,s=1,v=1,f=24", 'D')]           // Kitty graphics APC with control data only
    [InlineData("_Ga=t,i=1,s=1,v=1,f=24;AAAA", 'D')]      // ... and with an open payload
    [InlineData("_Ga=t,i=1,s=1,v=1,f=24;AAAA", 'E')]
    public void AnEscapeThatAbandonsAnOpenSequenceStillDispatchesIndexOrNextLine(string opener, char finalChar)
    {
        using var t = new Rig();
        var replies = new List<string>();
        string? title = null;
        t.Adapter.ReplyRequested += reply => replies.Add(reply.ToString());
        t.Adapter.TitleChanged += value => title = value;

        t.Feed("ab" + Esc + opener + Esc + finalChar + "X");

        bool index = finalChar == 'D';
        Assert.Equal("ab", t.Row(0));
        Assert.Equal(index ? "  X" : "X", t.Row(1));
        Assert.Equal((1, index ? 3 : 1), (t.Buffer.CursorRow, t.Buffer.CursorCol));
        Assert.Empty(replies);
        Assert.Null(title);
        Assert.False(t.Adapter.KittyGraphics!.HasOpenTransfer);
        Assert.Equal(0, t.Buffer.ActiveImageStore.Count);
    }

    [Fact]
    public void AnOverlongCsiThatIsEndedByAnEscapeStillDispatchesIndex()
    {
        using var t = new Rig();
        t.Feed("ab" + Esc + "[" + new string('1', 300) + Esc + "D" + "X");   // past the parameter cap the CSI is only being discarded

        Assert.Equal("ab", t.Row(0));
        Assert.Equal("  X", t.Row(1));
    }

    // ------------------------------------------------------------------------------------------- kitty graphics

    private static void PutImage(Rig t, int row, int col)
    {
        t.Goto(row, col);
        t.Parser.Feed(KittyTestData.ApcBytes("a=T,i=1,s=10,v=20,f=24,c=1,r=1,C=1", Convert.ToBase64String(new byte[10 * 20 * 3])));
    }

    private static long? PlacementRow(Rig t)
    {
        var store = t.Buffer.ActiveImageStore;
        return store.Placements.SingleOrDefault() is { } p ? p.AnchorRow - store.ViewTop : null;
    }

    [Fact]
    public void IndexAtTheBottomRowScrollsAPlacedImageUpWithItsText()
    {
        using var t = new Rig(rows: 6);
        PutImage(t, 3, 0);
        Assert.Equal(3L, PlacementRow(t));

        t.Goto(5, 0);
        t.Feed(Esc + "D");
        Assert.Equal(2L, PlacementRow(t));

        t.Feed(Esc + "D");
        Assert.Equal(1L, PlacementRow(t));
    }

    [Fact]
    public void NextLineAtTheBottomRowScrollsAPlacedImageUp()
    {
        using var t = new Rig(rows: 6);
        PutImage(t, 3, 0);

        t.Goto(5, 4);
        t.Feed(Esc + "E");

        Assert.Equal(2L, PlacementRow(t));
    }

    [Fact]
    public void IndexAboveTheBottomRowLeavesAPlacedImageAlone()
    {
        using var t = new Rig(rows: 6);
        PutImage(t, 3, 0);

        t.Goto(1, 0);
        t.Feed(Esc + "D" + Esc + "D" + Esc + "E");

        Assert.Equal(3L, PlacementRow(t));
        Assert.Equal(0, t.Buffer.ScrollbackCount);
    }

    [Fact]
    public void IndexAtTheBottomMarginOfARegionDoesNotMoveAnImageOutsideIt()
    {
        using var t = new Rig(rows: 6);
        PutImage(t, 5, 0);                   // below the region
        t.Feed(Esc + "[1;3r");
        t.Goto(2, 0);

        t.Feed(Esc + "D");

        Assert.Equal(5L, PlacementRow(t));
    }

    [Fact]
    public void IndexAtTheBottomMarginScrollsAnImageInsideTheRegionAndDropsItAtTheTopMargin()
    {
        using var t = new Rig(rows: 6);
        PutImage(t, 2, 0);
        t.Feed(Esc + "[2;4r");   // region rows 1..3 (0-based): the image sits inside it
        t.Goto(3, 0);

        t.Feed(Esc + "D");
        Assert.Equal(1L, PlacementRow(t));

        t.Feed(Esc + "D");       // the one-row image would leave the region through its top margin
        Assert.Null(PlacementRow(t));
        Assert.Equal(0, t.Buffer.ActiveImageStore.PlacementCount);
    }
}
