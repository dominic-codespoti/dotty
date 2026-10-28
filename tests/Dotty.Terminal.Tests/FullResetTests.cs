using System.Text;
using Dotty.Abstractions.Config;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

/// <summary>RIS (<c>ESC c</c>): every gap row of the full-reset table.</summary>
public sealed class FullResetTests
{
    private const string Esc = "\x1b";

    private static (TerminalAdapter Adapter, BasicAnsiParser Parser, List<string> Replies) Create(int rows = 6, int cols = 10)
    {
        var adapter = new TerminalAdapter(rows, cols);
        var parser = new BasicAnsiParser { Handler = adapter };
        var replies = new List<string>();
        adapter.ReplyRequested += reply => replies.Add(reply.ToString());
        return (adapter, parser, replies);
    }

    private static void Feed(BasicAnsiParser parser, string text) => parser.Feed(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void FullReset_LeavesAlternateScreenAndClearsMainScreen()
    {
        var (adapter, parser, _) = Create();
        Feed(parser, "main" + Esc + "[?1049h" + "alt");
        Assert.True(adapter.Buffer.IsAlternateScreenActive);

        Feed(parser, Esc + "c");

        Assert.False(adapter.Buffer.IsAlternateScreenActive);
        Assert.Equal("", adapter.Buffer.GetRowText(0).TrimEnd());
        Assert.Equal(0, adapter.Buffer.CursorRow);
        Assert.Equal(0, adapter.Buffer.CursorCol);
        Assert.Equal(0, adapter.KittyKeyboardFlags);
    }

    [Fact]
    public void FullReset_RestoresCursorVisibility()
    {
        var (adapter, parser, _) = Create();
        Feed(parser, Esc + "[?25l" + Esc + "c");
        Assert.True(adapter.Buffer.CursorVisible);
    }

    [Fact]
    public void FullReset_ResetsScrollRegion()
    {
        var (adapter, parser, _) = Create();
        Feed(parser, "L0\r\nL1\r\nL2\r\nL3\r\nL4\r\nL5");
        Feed(parser, Esc + "[3;5r");
        Feed(parser, Esc + "c");
        Feed(parser, "L0\r\nL1\r\nL2\r\nL3\r\nL4\r\nL5\n");

        Assert.Equal("L1", adapter.Buffer.GetRowText(0).TrimEnd());
    }

    [Fact]
    public void FullReset_ResetsOriginAndAutoWrap()
    {
        var (adapter, parser, _) = Create();
        Feed(parser, Esc + "[?6h" + Esc + "[?7l" + Esc + "c");
        Assert.False(adapter.Buffer.OriginMode);
        Assert.True(adapter.Buffer.AutoWrap);
    }

    [Fact]
    public void FullReset_ResetsMouseFocusSyncAndPasteModes()
    {
        var (adapter, parser, _) = Create();
        Feed(parser, Esc + "[?1000h" + Esc + "[?1006h" + Esc + "[?1004h" + Esc + "[?2026h" + Esc + "[?2004h");
        Assert.True(adapter.SynchronizedUpdateActive);

        Feed(parser, Esc + "c");

        Assert.Equal(TerminalAdapter.MouseMode.None, adapter.CurrentMouseMode);
        Assert.Equal(TerminalAdapter.MouseEncoding.Default, adapter.CurrentMouseEncoding);
        Assert.False(adapter.FocusReportingEnabled);
        Assert.False(adapter.SynchronizedUpdateActive);
        Assert.False(adapter.Buffer.BracketedPasteMode);
    }

    [Fact]
    public void FullReset_ResetsCursorStyle()
    {
        var (adapter, parser, _) = Create();
        Feed(parser, Esc + "[5 q" + Esc + "c");
        Assert.Equal(TerminalCursorShape.Block, adapter.Buffer.CursorShape);
        Assert.True(adapter.Buffer.CursorBlinking);
        Assert.Equal(0, adapter.CursorShape);
    }

    [Fact]
    public void FullReset_ResetsCharsetAndAttributes()
    {
        var (adapter, parser, _) = Create();
        Feed(parser, Esc + "(0" + Esc + "[1;31m" + Esc + "c" + "q");
        Assert.Equal('q', adapter.Buffer.GetRowText(0)[0]);
        Assert.Equal(0, adapter.Buffer.GetCell(0, 0).StyleId);
    }

    [Fact]
    public void FullReset_ClearsRepeatCharacter()
    {
        var (adapter, parser, _) = Create();
        Feed(parser, "x" + Esc + "c" + Esc + "[3b");
        Assert.Equal("", adapter.Buffer.GetRowText(0).TrimEnd());
    }

    [Fact]
    public void FullReset_ClearsPromptMarks()
    {
        var (adapter, parser, _) = Create();
        Feed(parser, "\x1b]133;A\x07");
        Assert.NotEmpty(adapter.Buffer.GetPromptMarks());
        Feed(parser, Esc + "c");
        Assert.Empty(adapter.Buffer.GetPromptMarks());
    }

    [Fact]
    public void FullReset_KeepsTitleButResetsTabStopsAndDynamicColors()
    {
        var (adapter, parser, replies) = Create();
        Feed(parser, "\x1b]10;?\x07");
        string baselineFg = replies[0];
        replies.Clear();

        Feed(parser, "\x1b]2;hello\x07" + "\x1b]10;rgb:11/22/33\x07" + Esc + "[1;4H" + Esc + "H");
        Assert.Equal(3, adapter.Buffer.GetNextTabStopFrom(0));
        Feed(parser, Esc + "c");
        Feed(parser, "\x1b]10;?\x07");

        Assert.Equal("hello", adapter.WindowTitle);
        Assert.Equal(8, adapter.Buffer.GetNextTabStopFrom(0));
        Assert.Equal(baselineFg, Assert.Single(replies));
    }

    [Fact]
    public void FullReset_SavedCursorIsHome()
    {
        var (adapter, parser, _) = Create();
        Feed(parser, Esc + "[1m" + Esc + "[3;3H" + Esc + "7" + Esc + "c");
        Feed(parser, Esc + "[5;5H" + Esc + "8" + "w");
        Assert.Equal('w', adapter.Buffer.GetRowText(0)[0]);
        Assert.Equal(0, adapter.Buffer.GetCell(0, 0).StyleId);
    }

    [Fact]
    public void FullReset_IsIdempotentAndSplitInputSafe()
    {
        var (adapter, parser, _) = Create();
        Feed(parser, Esc + "[?1049h" + Esc + "[?25l" + Esc + "[?1000h");
        foreach (byte b in Encoding.ASCII.GetBytes(Esc + "c"))
            parser.Feed(new[] { b });
        Feed(parser, Esc + "c");

        Assert.False(adapter.Buffer.IsAlternateScreenActive);
        Assert.True(adapter.Buffer.CursorVisible);
        Assert.Equal(TerminalAdapter.MouseMode.None, adapter.CurrentMouseMode);
    }
}
