using System;
using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

[Collection(nameof(GraphicsMemoryMetricsCollection))]
public sealed class BoundedScrollingTests
{
    private const string Esc = "\u001b";

    private sealed class Rig : IDisposable
    {
        public TerminalAdapter Adapter { get; } = new(rows: 5, columns: 5, scrollbackCapacity: 10);
        public BasicAnsiParser Parser { get; }
        public TerminalBuffer Buffer => Adapter.Buffer;

        public Rig() => Parser = new BasicAnsiParser { Handler = Adapter };
        public void Feed(string text) => Parser.Feed(Encoding.ASCII.GetBytes(text));
        public void Dispose() => Adapter.Dispose();
        public string Row(int row) => Buffer.GetRowText(row);
        public void FillRows() => Feed("abcde\r\nfghij\r\nklmno\r\npqrst\r\nuvwxy");
        public void EnableRectangle() => Feed(Esc + "[?69h" + Esc + "[2;4s");
    }

    [Fact]
    public void PartialWidthScrollUpMovesOnlySelectedColumnsAndDoesNotAddHistory()
    {
        using var t = new Rig();
        t.FillRows();
        t.EnableRectangle();

        t.Feed(Esc + "[2S");

        Assert.Equal("almne", t.Row(0));
        Assert.Equal("fqrsj", t.Row(1));
        Assert.Equal("kvwxo", t.Row(2));
        Assert.Equal("p   t", t.Row(3));
        Assert.Equal("u   y", t.Row(4));
        Assert.Equal(0, t.Buffer.ScrollbackCount);
    }

    [Fact]
    public void PartialWidthScrollDownMovesOnlySelectedColumnsAndClampsOversizedCount()
    {
        using var t = new Rig();
        t.FillRows();
        t.EnableRectangle();
        t.Feed(Esc + "[2;4r" + Esc + "[3;1H" + Esc + "[99T");

        Assert.Equal("abcde", t.Row(0));
        Assert.Equal("f   j", t.Row(1));
        Assert.Equal("k   o", t.Row(2));
        Assert.Equal("p   t", t.Row(3));
        Assert.Equal("uvwxy", t.Row(4));
        Assert.Equal(0, t.Buffer.ScrollbackCount);
    }

    [Fact]
    public void LineIndexAtBottomOutsideHorizontalMarginsHoldsCursorWithoutScrolling()
    {
        using var t = new Rig();
        t.FillRows();
        t.Feed(Esc + "[?69h" + Esc + "[2;4s" + Esc + "[2;3r" + Esc + "[3;5H\n");

        Assert.Equal((2, 4), (t.Buffer.CursorRow, t.Buffer.CursorCol));
        Assert.Equal("fghij", t.Row(1));
        Assert.Equal("klmno", t.Row(2));
        Assert.Equal(0, t.Buffer.ScrollbackCount);
    }

    [Fact]
    public void NextLineIndexesBeforeCarriageReturnAtBottomOutsideHorizontalMargins()
    {
        using var t = new Rig();
        t.FillRows();
        t.Feed(Esc + "[?69h" + Esc + "[2;4s" + Esc + "[2;3r" + Esc + "[3;5H" + Esc + "E");

        Assert.Equal((2, 1), (t.Buffer.CursorRow, t.Buffer.CursorCol));
        Assert.Equal("fghij", t.Row(1));
        Assert.Equal("klmno", t.Row(2));
        Assert.Equal(0, t.Buffer.ScrollbackCount);
    }

    [Fact]
    public void ReverseIndexAtTopOutsideHorizontalMarginsHoldsCursorAndDoesNotScroll()
    {
        using var t = new Rig();
        t.FillRows();
        t.Feed(Esc + "[?69h" + Esc + "[2;4s" + Esc + "[2;3r" + Esc + "[2;5H" + Esc + "M");

        Assert.Equal((1, 4), (t.Buffer.CursorRow, t.Buffer.CursorCol));
        Assert.Equal("fghij", t.Row(1));
        Assert.Equal(0, t.Buffer.ScrollbackCount);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\v")]
    [InlineData("\f")]
    public void ExplicitIndexControlsCancelPendingWrapAndPreserveColumn(string control)
    {
        using var t = new Rig();
        t.Feed("abcde" + control + "X");

        Assert.Equal("abcde", t.Row(0));
        Assert.Equal("    X", t.Row(1));
    }

    [Fact]
    public void ReverseIndexStartingBelowRegionMovesUpInsteadOfFreezing()
    {
        using var t = new Rig();
        t.Feed(Esc + "[2;3r" + Esc + "[5;5H" + Esc + "M");
        Assert.Equal((3, 4), (t.Buffer.CursorRow, t.Buffer.CursorCol));
        t.Feed(Esc + "M");
        Assert.Equal((2, 4), (t.Buffer.CursorRow, t.Buffer.CursorCol));
        Assert.Equal(0, t.Buffer.ScrollbackCount);
    }

    [Fact]
    public void CharSpanPendingWrapRestartsAtLeftMarginAtPhysicalRightEdge()
    {
        using var t = new Rig();
        t.Feed(Esc + "[?69h" + Esc + "[2;5s" + Esc + "[1;1Habcde");
        t.Feed(Esc + "[2;1HX");
        t.Feed(Esc + "[1;5He");
        t.Adapter.OnPrint("Z".AsSpan());

        Assert.Equal('X', t.Row(1)[0]);
        Assert.Equal('Z', t.Row(1)[1]);
        Assert.Equal(2, t.Buffer.CursorCol);
    }

    [Fact]
    public void ByteSpanPendingWrapRestartsAtLeftMarginAtPhysicalRightEdge()
    {
        using var t = new Rig();
        t.Feed(Esc + "[?69h" + Esc + "[2;5s" + Esc + "[1;1Habcde");
        t.Feed(Esc + "[2;1HX");
        t.Feed(Esc + "[1;5He");
        t.Feed("Z");

        Assert.Equal('X', t.Row(1)[0]);
        Assert.Equal('Z', t.Row(1)[1]);
        Assert.Equal(2, t.Buffer.CursorCol);
    }

    [Fact]
    public void ByteSpanPendingWrapAtBottomScrollsAndRestartsAtLeftMargin()
    {
        using var t = new Rig();
        t.Feed(Esc + "[?69h" + Esc + "[2;5s");
        t.Feed(Esc + "[1;1Habcde");
        t.Feed(Esc + "[2;1Hfghij");
        t.Feed(Esc + "[3;1Hklmno");
        t.Feed(Esc + "[4;1Hpqrst");
        t.Feed(Esc + "[5;1Huvwxy");
        t.Feed(Esc + "[5;1HX");
        t.Feed(Esc + "[5;5He");
        t.Feed("Z");

        Assert.Equal('X', t.Row(4)[0]);
        Assert.Equal('Z', t.Row(4)[1]);
        Assert.Equal(2, t.Buffer.CursorCol);
        Assert.Equal(0, t.Buffer.ScrollbackCount);
    }


}
