using System.Diagnostics;
using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

/// <summary>
/// REP (CSI Ps b) is bounded to what the retained window can observe; the result must be
/// identical to a naive repeat (the same character printed n times).
/// ScrollbackCount is capped at capacity, so dropping whole rows beyond the window only
/// changes the internal scrolled-row counter, which is unobservable here.
/// </summary>
public sealed class RepeatCharacterTests
{
    private const int Rows = 3;
    private const int Cols = 4;
    private const int Scrollback = 5;
    private const int Window = (Rows + Scrollback + 1) * Cols; // 36

    private static (TerminalAdapter Adapter, BasicAnsiParser Parser) Create(int rows = Rows, int cols = Cols, int scrollback = Scrollback)
    {
        var adapter = new TerminalAdapter(rows, cols, scrollback);
        return (adapter, new BasicAnsiParser { Handler = adapter });
    }

    private static void Feed(BasicAnsiParser parser, string text) => parser.Feed(Encoding.UTF8.GetBytes(text));

    private static string Dump(TerminalAdapter adapter)
    {
        var b = adapter.Buffer;
        var sb = new StringBuilder();
        for (int r = 0; r < b.Rows; r++)
        {
            for (int c = 0; c < b.Columns; c++)
            {
                var cell = b.GetCell(r, c);
                sb.Append(cell.Rune).Append(':').Append(cell.StyleId).Append(' ');
            }
            sb.Append('|');
        }
        sb.Append("cur=").Append(b.CursorRow).Append(',').Append(b.CursorCol);
        sb.Append("|sb=").Append(b.ScrollbackCount).Append('|');
        foreach (var line in b.GetScrollbackLines()) sb.Append(line).Append('/');
        return sb.ToString();
    }

    private static void AssertEqualsNaive(string prefix, int n, bool autowrap, char ch = 'x')
    {
        string setup = (autowrap ? "" : "\u001b[?7l") + prefix + ch;
        var (rep, repParser) = Create();
        var (naive, naiveParser) = Create();

        Feed(repParser, setup + $"\u001b[{n}b" + "Z");
        Feed(naiveParser, setup + new string(ch, n) + "Z");

        Assert.Equal(Dump(naive), Dump(rep));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("ab", true)]
    [InlineData("abc", true)]   // next write lands in the last column
    [InlineData("abcd", true)]  // wrap pending at the start
    [InlineData("", false)]
    [InlineData("abc", false)]
    [InlineData("abcd", false)]
    public void SmallAndBoundaryCounts_MatchNaiveRepeat(string prefix, bool autowrap)
    {
        for (int n = 1; n <= 3 * Window + Cols; n++)
            AssertEqualsNaive(prefix, n, autowrap);
    }

    [Fact]
    public void NonPositiveCount_IsNoOp()
    {
        var (adapter, _) = Create();
        adapter.OnPrint("x");
        adapter.OnRepeatCharacter(0);
        adapter.OnRepeatCharacter(-5);
        Assert.Equal(1, adapter.Buffer.CursorCol);
    }

    [Fact]
    public void NoPreviousCharacter_IsNoOp()
    {
        var (adapter, parser) = Create();
        Feed(parser, "\u001b[10b");
        Assert.Equal(0, adapter.Buffer.CursorCol);
        Assert.Equal(0, adapter.Buffer.CursorRow);
    }

    [Fact]
    public void NonAsciiCharacter_SmallCountsMatchNaiveRepeat()
    {
        for (int n = 1; n <= 20; n++)
            AssertEqualsNaive("a", n, autowrap: true, ch: 'é');
    }

    [Fact]
    public void HugeCount_CompletesPromptly()
    {
        var (adapter, parser) = Create(rows: 24, cols: 80, scrollback: 10000);
        var sw = Stopwatch.StartNew();
        Feed(parser, "x\u001b[2147483647b");
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"REP took {sw.Elapsed}");
        Assert.Equal('x', (char)adapter.Buffer.GetCell(0, 0).Rune);
        Assert.Equal('x', (char)adapter.Buffer.GetCell(23, 79).Rune);
    }

    [Fact]
    public void HugeCount_NonAscii_CompletesPromptly()
    {
        var (adapter, parser) = Create(rows: 24, cols: 80, scrollback: 10000);
        var sw = Stopwatch.StartNew();
        Feed(parser, "é\u001b[2147483647b");
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"REP took {sw.Elapsed}");
    }

    [Fact]
    public void HugeCount_AutowrapOff_LeavesCursorInLastColumn()
    {
        var (adapter, parser) = Create();
        Feed(parser, "\u001b[?7lx\u001b[2147483647b");
        Assert.Equal(0, adapter.Buffer.CursorRow);
        Assert.Equal(Cols - 1, adapter.Buffer.CursorCol);
        for (int c = 0; c < Cols; c++)
            Assert.Equal('x', (char)adapter.Buffer.GetCell(0, c).Rune);
    }
}
