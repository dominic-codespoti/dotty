using System;
using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

[Collection(nameof(GraphicsMemoryMetricsCollection))]
public sealed class MarginEditingConformanceTests
{
    private const string Esc = "\u001b";

    private sealed class Rig : IDisposable
    {
        public TerminalAdapter Adapter { get; } = new(rows: 5, columns: 7, scrollbackCapacity: 10);
        public BasicAnsiParser Parser { get; }
        public TerminalBuffer Buffer => Adapter.Buffer;
        public Rig() => Parser = new BasicAnsiParser { Handler = Adapter };
        public void Feed(string text) => Parser.Feed(Encoding.ASCII.GetBytes(text));
        public void Fill() => Feed("abcdefg\r\nhijklmn\r\nopqrstu\r\nvwxyz12\r\n3456789");
        public void Dispose() => Adapter.Dispose();
    }

    [Fact]
    public void IchAndDchRespectHorizontalMarginsAndOutsideCursorNoOp()
    {
        using var t = new Rig();
        t.Fill();
        t.Feed(Esc + "[?69h" + Esc + "[2;5s" + Esc + "[1;1H" + Esc + "[10@");
        Assert.Equal("abcdefg", t.Buffer.GetRowText(0));
        t.Feed(Esc + "[1;3H" + Esc + "[1@");
        Assert.Equal("ab cdfg", t.Buffer.GetRowText(0));
        t.Feed(Esc + "[1;3H" + Esc + "[99@");
        Assert.Equal("ab   fg", t.Buffer.GetRowText(0));

        t.Feed(Esc + "[2;1H" + Esc + "[1P");
        Assert.Equal("hijklmn", t.Buffer.GetRowText(1));
        t.Feed(Esc + "[2;3H" + Esc + "[1P");
        Assert.Equal("hikl mn", t.Buffer.GetRowText(1));
        t.Feed(Esc + "[2;3H" + Esc + "[99P");
        Assert.Equal("hi   mn", t.Buffer.GetRowText(1));
        Assert.Empty(t.Buffer.ValidateInvariants());
    }

    [Fact]
    public void PartialInsertAndDeleteLinesRespectBothMarginsAndPreserveHistoryAndCursor()
    {
        using var t = new Rig();
        t.Fill();
        t.Feed(Esc + "[?69h" + Esc + "[2;4s" + Esc + "[2;4r");
        int history = t.Buffer.ScrollbackCount;
        t.Feed(Esc + "[2;3H" + Esc + "[2L");
        Assert.Equal("abcdefg", t.Buffer.GetRowText(0));
        Assert.Equal("h   lmn", t.Buffer.GetRowText(1));
        Assert.Equal("o   stu", t.Buffer.GetRowText(2));
        Assert.Equal("vijkz12", t.Buffer.GetRowText(3));
        Assert.Equal("3456789", t.Buffer.GetRowText(4));
        Assert.Equal((1, 2), (t.Buffer.CursorRow, t.Buffer.CursorCol));
        t.Feed(Esc + "[3;3H" + Esc + "[1M");
        Assert.Equal("h   lmn", t.Buffer.GetRowText(1));
        Assert.Equal("oijkstu", t.Buffer.GetRowText(2));
        Assert.Equal("v   z12", t.Buffer.GetRowText(3));
        Assert.Equal((2, 2), (t.Buffer.CursorRow, t.Buffer.CursorCol));
        Assert.Equal(history, t.Buffer.ScrollbackCount);

        t.Feed(Esc + "[2;3H" + Esc + "[99M");
        Assert.Equal("h   lmn", t.Buffer.GetRowText(1));
        Assert.Equal("o   stu", t.Buffer.GetRowText(2));
        Assert.Equal("v   z12", t.Buffer.GetRowText(3));
        Assert.Equal("3456789", t.Buffer.GetRowText(4));
        Assert.Equal(history, t.Buffer.ScrollbackCount);
    }

    [Fact]
    public void OversizedPartialInsertLinesClearsOnlyTheBoundedRegion()
    {
        using var t = new Rig();
        t.Fill();
        t.Feed(Esc + "[?69h" + Esc + "[2;4s" + Esc + "[2;4r" + Esc + "[2;3H" + Esc + "[99L");
        Assert.Equal("abcdefg", t.Buffer.GetRowText(0));
        Assert.Equal("h   lmn", t.Buffer.GetRowText(1));
        Assert.Equal("o   stu", t.Buffer.GetRowText(2));
        Assert.Equal("v   z12", t.Buffer.GetRowText(3));
        Assert.Equal("3456789", t.Buffer.GetRowText(4));
        Assert.Equal(0, t.Buffer.ScrollbackCount);
    }

    [Fact]
    public void LineEditsOutsideEitherAxisAreNoOpsAndFullWidthEditsDoNotEnterHistory()
    {
        using var t = new Rig();
        t.Fill();
        t.Feed(Esc + "[?69h" + Esc + "[2;4s" + Esc + "[2;4r");
        var before = Rows(t);
        t.Feed(Esc + "[1;3H" + Esc + "[1L" + Esc + "[1M");
        t.Feed(Esc + "[3;1H" + Esc + "[1L" + Esc + "[1M");
        Assert.Equal(before, Rows(t));
        Assert.Equal(0, t.Buffer.ScrollbackCount);

        t.Feed(Esc + "[?69l" + Esc + "[3;1H" + Esc + "[1M");
        Assert.Equal(0, t.Buffer.ScrollbackCount);
        Assert.Equal("hijklmn", t.Buffer.GetRowText(1));
        Assert.Equal("vwxyz12", t.Buffer.GetRowText(2));
        Assert.Equal("", t.Buffer.GetRowText(3).TrimEnd());
    }

    private static string[] Rows(Rig t)
    {
        var result = new string[t.Buffer.Rows];
        for (int i = 0; i < result.Length; i++) result[i] = t.Buffer.GetRowText(i);
        return result;
    }
}
