using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

[Collection(nameof(GraphicsMemoryMetricsCollection))]
public sealed class MoreFixConformanceTests
{
    private const string Esc = "\x1b";

    private sealed class Rig : IDisposable
    {
        public TerminalAdapter Adapter { get; } = new(rows: 3, columns: 16, scrollbackCapacity: 8);
        public BasicAnsiParser Parser { get; }
        public TerminalBuffer Buffer => Adapter.Buffer;
        public Rig() => Parser = new BasicAnsiParser { Handler = Adapter };
        public void Feed(string text) => Parser.Feed(Encoding.UTF8.GetBytes(text));
        public void Dispose() => Adapter.Dispose();
    }

    [Theory]
    [InlineData(false, "\t")]
    [InlineData(true, "\t")]
    [InlineData(false, Esc + "[I")]
    [InlineData(true, Esc + "[I")]
    public void RightEdgeTabEitherConsumesOrPreservesPrintableWrap(bool moreFix, string tab)
    {
        using var t = new Rig();
        t.Feed(Esc + (moreFix ? "[?41h" : "[?41l") + new string('x', 16) + tab);
        Assert.Equal(moreFix ? (1, 8) : (0, 15), (t.Buffer.CursorRow, t.Buffer.CursorCol));
        t.Feed("A");
        Assert.Equal(new string('x', 16), t.Buffer.GetRowText(0));
        Assert.Equal('A', t.Buffer.GetRowText(1)[moreFix ? 8 : 0]);
        Assert.Equal((1, moreFix ? 9 : 1), (t.Buffer.CursorRow, t.Buffer.CursorCol));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompatibilityTabWrapRestartsAtLeftMargin(bool moreFix)
    {
        using var t = new Rig();
        t.Feed(Esc + "[?69h" + Esc + "[4;12s" + Esc + "[1;4H" + Esc + (moreFix ? "[?41h" : "[?41l") + "abcdefghi\tA");
        Assert.Equal("   abcdefghi", t.Buffer.GetRowText(0)[..12]);
        Assert.Equal('A', t.Buffer.GetRowText(1)[moreFix ? 8 : 3]);
        Assert.Equal((1, moreFix ? 9 : 4), (t.Buffer.CursorRow, t.Buffer.CursorCol));
    }

    [Theory]
    [InlineData(Esc + "[?7l" + "abcdefghijklmnop")]
    [InlineData(Esc + "[1;16H")]
    public void MoreFixNeverInventsAPendingWrap(string setup)
    {
        using var t = new Rig();
        t.Feed(Esc + "[?41h" + setup + "\tA");
        Assert.Equal((0, 15), (t.Buffer.CursorRow, t.Buffer.CursorCol));
        Assert.Equal('A', t.Buffer.GetRowText(0)[15]);
        Assert.Equal(new string(' ', 16), t.Buffer.GetRowText(1));
    }

    [Fact]
    public void TabWrappingAtTheBottomScrollsExactlyOnce()
    {
        using var t = new Rig();
        t.Feed(Esc + "[?41h" + Esc + "[3;1H" + new string('x', 16) + "\tA");
        Assert.Equal(1, t.Buffer.ScrollbackCount);
        Assert.Equal(new string('x', 16), t.Buffer.GetRowText(1));
        Assert.Equal('A', t.Buffer.GetRowText(2)[8]);
        Assert.Equal((2, 9), (t.Buffer.CursorRow, t.Buffer.CursorCol));
    }
}
