using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

[Collection(nameof(GraphicsMemoryMetricsCollection))]
public sealed class WriterMarginConformanceTests
{
    private const string Esc = "\u001b";

    private sealed class Rig : IDisposable
    {
        public TerminalAdapter Adapter { get; } = new(rows: 3, columns: 8, scrollbackCapacity: 8);
        public BasicAnsiParser Parser { get; }
        public TerminalBuffer Buffer => Adapter.Buffer;

        public Rig() => Parser = new BasicAnsiParser { Handler = Adapter };
        public void Feed(string text) => Parser.Feed(Encoding.UTF8.GetBytes(text));
        public void SetMargins() => Feed(Esc + "[?69h" + Esc + "[3;6s");
        public void Dispose() => Adapter.Dispose();
    }

    [Fact]
    public void ByteAndCharBulkWritersWrapAtTheActiveRightMargin()
    {
        using var bytes = new Rig();
        bytes.SetMargins();
        bytes.Feed(Esc + "[1;3H" + "abcd" + "e");
        Assert.Equal("  abcd  ", bytes.Buffer.GetRowText(0));
        Assert.Equal("  e     ", bytes.Buffer.GetRowText(1));
        Assert.Equal((1, 3), (bytes.Buffer.CursorRow, bytes.Buffer.CursorCol));

        using var chars = new Rig();
        chars.SetMargins();
        chars.Buffer.SetCursor(0, 2);
        chars.Buffer.WriteText("abcd".AsSpan(), CellAttributes.Default);
        chars.Buffer.WriteText("e".AsSpan(), CellAttributes.Default);
        Assert.Equal("  abcd  ", chars.Buffer.GetRowText(0));
        Assert.Equal("  e     ", chars.Buffer.GetRowText(1));
        Assert.Equal((1, 3), (chars.Buffer.CursorRow, chars.Buffer.CursorCol));
    }

    [Fact]
    public void SingleAsciiAndWideGraphemesUseMarginEdgeForWrapPending()
    {
        using var t = new Rig();
        t.SetMargins();
        t.Buffer.SetCursor(0, 5);
        t.Buffer.WriteText("\t".AsSpan(), CellAttributes.Default);
        Assert.Equal(' ', (char)t.Buffer.GetCell(0, 5).Rune);
        Assert.Equal(' ', (char)t.Buffer.GetCell(1, 2).Rune);

        t.Feed(Esc + "[2;5H" + "界" + "X");
        Assert.Equal('界', (char)t.Buffer.GetCell(1, 4).Rune);
        Assert.Equal('X', (char)t.Buffer.GetCell(2, 2).Rune);
        Assert.Empty(t.Buffer.ValidateInvariants());
    }

    [Fact]
    public void WritesOutsideBothSidesKeepTheDefinedMarginPolicyAndNoWrapStopsAtTheEdge()
    {
        using var t = new Rig();
        t.SetMargins();
        t.Feed(Esc + "[1;1H" + "abcdefg");
        Assert.Equal("abcdef  ", t.Buffer.GetRowText(0));
        Assert.Equal('g', (char)t.Buffer.GetCell(1, 2).Rune);

        t.Feed(Esc + "[1;7H" + "xy");
        Assert.Equal('x', (char)t.Buffer.GetCell(0, 6).Rune);
        Assert.Equal('y', (char)t.Buffer.GetCell(0, 7).Rune);

        t.Feed(Esc + "[?7l" + Esc + "[2;3H" + "abcdef");
        Assert.Equal('f', (char)t.Buffer.GetCell(1, 5).Rune);
        Assert.Equal((1, 5), (t.Buffer.CursorRow, t.Buffer.CursorCol));
        Assert.Empty(t.Buffer.ValidateInvariants());
    }

    [Fact]
    public void RepSmallAndSaturatedCountsMatchNaiveReferenceWithRestrictedWrapWidthAndHistory()
    {
        AssertRepParity(3);
        AssertRepParity(200);
    }

    private static void AssertRepParity(int count)
    {
        using var reduced = new Rig();
        using var naive = new Rig();
        string start = Esc + "[?69h" + Esc + "[3;6s" + Esc + "[1;3H" + "x";
        reduced.Feed(start + Esc + "[" + count + "b");
        naive.Feed(start + new string('x', count));
        Assert.Equal(Dump(naive.Buffer), Dump(reduced.Buffer));
    }

    private static string Dump(TerminalBuffer buffer)
    {
        var text = new StringBuilder();
        foreach (var historyLine in buffer.GetScrollbackLines())
            text.Append("H:").Append(historyLine).Append('|');
        for (int row = 0; row < buffer.Rows; row++)
        {
            for (int col = 0; col < buffer.Columns; col++)
            {
                var cell = buffer.GetCell(row, col);
                var cold = buffer.GetColdCell(row, col);
                text.Append(cell.Rune).Append(':').Append(cell.StyleId).Append(':').Append(cell.PackedFlags)
                    .Append(':').Append(cold.GraphemeIndex).Append(':').Append(cold.HyperlinkId).Append(',');
            }
            text.Append('|');
        }
        return text.Append("C:").Append(buffer.CursorRow).Append(',').Append(buffer.CursorCol).ToString();
    }
}
