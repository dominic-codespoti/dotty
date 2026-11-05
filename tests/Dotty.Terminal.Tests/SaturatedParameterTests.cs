using System.Text;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

/// <summary>
/// The parser saturates oversized numeric parameters at int.MaxValue; the buffer
/// operations that add them to a cursor position must clamp, not overflow.
/// </summary>
public sealed class SaturatedParameterTests
{
    private const string Huge = "99999999999";

    private static (TerminalAdapter Adapter, BasicAnsiParser Parser) Create()
    {
        var adapter = new TerminalAdapter(rows: 3, columns: 10);
        return (adapter, new BasicAnsiParser { Handler = adapter });
    }

    private static void Feed(BasicAnsiParser parser, string text) => parser.Feed(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void CursorForward_MovesToLastColumn()
    {
        var (adapter, parser) = Create();
        Feed(parser, $"ab\u001b[{Huge}C");
        Assert.Equal(9, adapter.Buffer.CursorCol);
    }

    [Fact]
    public void CursorNextLine_MovesToLastRowFirstColumn()
    {
        var (adapter, parser) = Create();
        Feed(parser, $"\u001b[2;3H\u001b[{Huge}E");
        Assert.Equal(2, adapter.Buffer.CursorRow);
        Assert.Equal(0, adapter.Buffer.CursorCol);
    }

    [Fact]
    public void InsertChars_ClearsToEndOfLine()
    {
        var (adapter, parser) = Create();
        Feed(parser, $"abcdef\u001b[4G\u001b[{Huge}@");
        Assert.Equal("abc", adapter.Buffer.GetRowText(0).TrimEnd());
        Assert.Empty(adapter.Buffer.ValidateInvariants());
    }

    [Fact]
    public void DeleteChars_DoesNotTouchTextLeftOfCursor()
    {
        var (adapter, parser) = Create();
        Feed(parser, $"abcdef\u001b[4G\u001b[{Huge}P");
        Assert.Equal("abc", adapter.Buffer.GetRowText(0).TrimEnd());
        Assert.Empty(adapter.Buffer.ValidateInvariants());
    }

    [Fact]
    public void EraseChars_ClearsToEndOfLine()
    {
        var (adapter, parser) = Create();
        Feed(parser, $"abcdef\u001b[4G\u001b[{Huge}X");
        Assert.Equal("abc", adapter.Buffer.GetRowText(0).TrimEnd());
    }

    [Fact]
    public void DeleteChars_LargerThanRemainingOnlyClearsFromCursor()
    {
        var (adapter, parser) = Create();
        // 10 columns, cursor at column 4 (6 cells remain), count 8.
        Feed(parser, "abcdefghij\u001b[5G\u001b[8P");
        Assert.Equal("abcd", adapter.Buffer.GetRowText(0).TrimEnd());
    }

    [Fact]
    public void LongOscTitle_IsTruncatedToMaxTitleChars()
    {
        var (adapter, parser) = Create();
        Feed(parser, "\u001b]2;" + new string('t', 100_000) + "\u0007");
        Assert.Equal(TerminalAdapter.MaxTitleChars, adapter.WindowTitle!.Length);
    }
}
