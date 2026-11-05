using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

[Collection(nameof(GraphicsMemoryMetricsCollection))]
public sealed class EraseAndConformanceLevelTests
{
    private const string Esc = "\x1b";

    private sealed class Rig : IDisposable
    {
        public TerminalAdapter Adapter { get; } = new(rows: 3, columns: 8, scrollbackCapacity: 8);
        public BasicAnsiParser Parser { get; }
        public TerminalBuffer Buffer => Adapter.Buffer;
        public List<string> Replies { get; } = new();

        public Rig()
        {
            Parser = new BasicAnsiParser { Handler = Adapter };
            Adapter.ReplyRequested += reply => Replies.Add(reply.ToString());
        }

        public void Feed(string text) => Parser.Feed(Encoding.UTF8.GetBytes(text));
        public void Dispose() => Adapter.Dispose();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void SelectiveErasePreservesDecProtectedCellsButOrdinaryEraseDoesNot(int mode)
    {
        using var selective = new Rig();
        using var ordinary = new Rig();
        const string markedRow = "A" + Esc + "[1\"q" + "P" + Esc + "[0\"q" + "BCDE" + Esc + "[1\"q" + "Q" + Esc + "[0\"q" + "G";
        selective.Feed(markedRow + Esc + "[1;4H" + Esc + "[?" + mode + "K");
        ordinary.Feed(markedRow + Esc + "[1;4H" + Esc + "[" + mode + "K");

        string selectiveRow = selective.Buffer.GetRowText(0);
        string ordinaryRow = ordinary.Buffer.GetRowText(0);
        if (mode is 1 or 2)
            Assert.Equal('P', selectiveRow[1]);
        if (mode is 0 or 2)
            Assert.Equal('Q', selectiveRow[6]);
        if (mode == 2)
        {
            Assert.Equal(' ', selectiveRow[3]);
            Assert.Equal(' ', selectiveRow[5]);
        }
        if (mode is 1 or 2)
            Assert.Equal(' ', ordinaryRow[1]);
        if (mode is 0 or 2)
            Assert.Equal(' ', ordinaryRow[6]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void SelectiveDisplayErasePreservesDecProtectedCellsButOrdinaryDisplayEraseDoesNot(int mode)
    {
        using var selective = new Rig();
        using var ordinary = new Rig();
        const string markedRow = Esc + "[2;1H" + "A" + Esc + "[1\"q" + "P" + Esc + "[0\"q" + "BCDE" + Esc + "[1\"q" + "Q" + Esc + "[0\"q" + "G";
        selective.Feed(markedRow + Esc + "[2;4H" + Esc + "[?" + mode + "J");
        ordinary.Feed(markedRow + Esc + "[2;4H" + Esc + "[" + mode + "J");

        string selectiveRow = selective.Buffer.GetRowText(1);
        string ordinaryRow = ordinary.Buffer.GetRowText(1);
        if (mode is 1 or 2)
            Assert.Equal('P', selectiveRow[1]);
        if (mode is 0 or 2)
            Assert.Equal('Q', selectiveRow[6]);
        if (mode is 1 or 2)
            Assert.Equal(' ', ordinaryRow[1]);
        if (mode is 0 or 2)
            Assert.Equal(' ', ordinaryRow[6]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void SelectiveEraseIgnoresIsoProtectionWhileOrdinaryEraseHonorsIt(int mode)
    {
        using var selective = new Rig();
        using var ordinary = new Rig();
        string row = "A" + Esc + "V" + "P" + Esc + "W" + "BCDEFG";
        selective.Feed(row + Esc + "[1;4H" + Esc + "[?" + mode + "K");
        ordinary.Feed(row + Esc + "[1;4H" + Esc + "[" + mode + "K");

        if (mode is 1 or 2)
        {
            Assert.Equal('P', ordinary.Buffer.GetRowText(0)[1]);
            Assert.Equal(' ', selective.Buffer.GetRowText(0)[1]);
        }
    }

    [Fact]
    public void SelectiveDisplayMode2RetainsCursorAndScrollback()
    {
        using var t = new Rig();
        t.Feed("one\r\ntwo\r\nthree\r\nfour" + Esc + "[2;3H" + Esc + "V" + "P" + Esc + "W");
        int history = t.Buffer.ScrollbackCount;
        Assert.True(history > 0);
        Assert.Equal((1, 3), (t.Buffer.CursorRow, t.Buffer.CursorCol));

        t.Feed(Esc + "[?2J");

        Assert.Equal(history, t.Buffer.ScrollbackCount);
        Assert.Equal((1, 3), (t.Buffer.CursorRow, t.Buffer.CursorCol));
    }

    [Fact]
    public void SelectiveEraseRequiresQuestionIntroducer()
    {
        using var t = new Rig();
        t.Feed("keep" + Esc + "[1;1H" + Esc + "[>2J" + Esc + "[>2K");
        Assert.Equal("keep    ", t.Buffer.GetRowText(0));
        Assert.Empty(t.Replies);
    }

    [Fact]
    public void ConformanceLevelGatesMarginsAndModeReportsAndRisRestoresLevelFive()
    {
        using var t = new Rig();
        t.Feed(Esc + "[63;1\"p" + Esc + "[?69h" + Esc + "[3;6s");
        t.Feed(Esc + "[1;3H" + "abcde");
        Assert.Equal('e', (char)t.Buffer.GetCell(0, 6).Rune);
        Assert.Equal(0u, t.Buffer.GetCell(1, 2).Rune);

        Assert.False(t.Buffer.LeftRightMarginMode);
        t.Replies.Clear();
        t.Feed(Esc + "[?69$p");
        Assert.Contains(Esc + "[?69;2$y", t.Replies);

        t.Feed(Esc + "[64;1\"p" + Esc + "[?69h" + Esc + "[3;6s");
        t.Feed(Esc + "[2J");
        Assert.True(t.Buffer.LeftRightMarginMode);
        t.Feed(Esc + "[1;3H" + "abcde");
        Assert.Equal("  abcd  ", t.Buffer.GetRowText(0));
        Assert.Equal('e', (char)t.Buffer.GetCell(1, 2).Rune);
        t.Replies.Clear();
        t.Feed(Esc + "[?69$p");
        Assert.Contains(Esc + "[?69;1$y", t.Replies);
    }

    [Fact]
    public void ConformanceLevelThreeEnablesModeReportsAfterLevelTwoSilence()
    {
        using var t = new Rig();
        t.Feed(Esc + "[62;1\"p");
        t.Feed(Esc + "[?69$p");
        Assert.Empty(t.Replies);

        t.Feed(Esc + "[63;1\"p");
        t.Feed(Esc + "[?69$p");
        Assert.Contains(Esc + "[?69;2$y", t.Replies);
    }

    [Theory]
    [InlineData("66;1")]
    [InlineData("63;3")]
    [InlineData("63;1;1")]
    public void InvalidConformanceRequestsDoNotChangeTheLevel(string parameters)
    {
        using var t = new Rig();
        t.Feed(Esc + "[63;1\"p" + Esc + "[" + parameters + "\"p");
        t.Feed(Esc + "[?69h" + Esc + "[3;6s");
        Assert.False(t.Buffer.LeftRightMarginMode);
        t.Replies.Clear();
        t.Feed(Esc + "[?69$p");
        Assert.Contains(Esc + "[?69;2$y", t.Replies);
    }

    [Fact]
    public void RisRestoresDefaultConformanceAvailability()
    {
        using var t = new Rig();
        t.Feed(Esc + "[62;1\"p");
        t.Feed(Esc + "[?69$p");
        Assert.Empty(t.Replies);

        t.Feed(Esc + "c");
        t.Feed(Esc + "[?69$p");
        Assert.Contains(Esc + "[?69;2$y", t.Replies);
        t.Feed(Esc + "[?69h" + Esc + "[3;6s");
        Assert.True(t.Buffer.LeftRightMarginMode);
    }
}
