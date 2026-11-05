using System.Text;
using Dotty.Abstractions.Config;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;
using Xunit;

namespace Dotty.Terminal.Tests;

/// <summary>DECSTR (<c>CSI ! p</c>): exact xterm soft-reset set, and everything it must leave alone.</summary>
public sealed class SoftResetTests
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

    private static string State(TerminalAdapter a) => string.Join('|',
        a.Buffer.CursorRow, a.Buffer.CursorCol, a.Buffer.CursorVisible, a.Buffer.AutoWrap, a.Buffer.OriginMode,
        a.Buffer.CursorShape, a.Buffer.CursorBlinking, a.ApplicationCursorKeysEnabled, a.KeypadApplicationMode,
        a.ModifyOtherKeysLevel, a.CursorShape, a.Buffer.BracketedPasteMode, a.CurrentMouseMode, a.CurrentMouseEncoding,
        a.FocusReportingEnabled, a.KittyKeyboardFlags, a.WindowTitle, a.Buffer.IsAlternateScreenActive,
        a.Buffer.GetNextTabStopFrom(0), a.Buffer.GetRowText(0), a.Buffer.GetRowText(1));

    [Fact]
    public void SoftReset_ResetsCursorVisibilityAndShape()
    {
        var (adapter, parser, _) = Create();
        Feed(parser, Esc + "[?25l" + Esc + "[4 q");
        Assert.False(adapter.Buffer.CursorVisible);
        Assert.Equal(TerminalCursorShape.Underline, adapter.Buffer.CursorShape);
        Assert.False(adapter.Buffer.CursorBlinking);

        Feed(parser, Esc + "[!p");

        Assert.True(adapter.Buffer.CursorVisible);
        Assert.Equal(TerminalCursorShape.Block, adapter.Buffer.CursorShape);
        Assert.True(adapter.Buffer.CursorBlinking);
        Assert.Equal(0, adapter.CursorShape);
    }

    [Fact]
    public void SoftReset_ResetsScrollRegionWithoutMovingCursor()
    {
        var (adapter, parser, _) = Create();
        Feed(parser, "L0\r\nL1\r\nL2\r\nL3\r\nL4\r\nL5");
        Feed(parser, Esc + "[3;5r" + Esc + "[!p" + Esc + "[6;1H\n");

        // Whole-screen scroll: row 0 now holds what was row 1. With the margins still set the LF below the
        // region would not scroll at all.
        Assert.Equal("L1", adapter.Buffer.GetRowText(0).TrimEnd());
        Assert.Equal("L5", adapter.Buffer.GetRowText(4).TrimEnd());
    }

    [Fact]
    public void SoftReset_DoesNotHomeCursorOrTouchScreen()
    {
        var (adapter, parser, _) = Create();
        Feed(parser, "abc" + Esc + "[2;4H" + Esc + "[3;5r" + Esc + "[2;4H");
        Feed(parser, Esc + "[!p");

        Assert.Equal(1, adapter.Buffer.CursorRow);
        Assert.Equal(3, adapter.Buffer.CursorCol);
        Assert.Equal("abc", adapter.Buffer.GetRowText(0).TrimEnd());
    }

    [Fact]
    public void SoftReset_ResetsOriginMode()
    {
        var (adapter, parser, _) = Create();
        Feed(parser, Esc + "[?6h");
        Assert.True(adapter.Buffer.OriginMode);
        Feed(parser, Esc + "[!p");
        Assert.False(adapter.Buffer.OriginMode);
    }

    [Fact]
    public void SoftReset_ResetsAutoWrap()
    {
        var (adapter, parser, _) = Create();
        Feed(parser, Esc + "[?7l");
        Assert.False(adapter.Buffer.AutoWrap);
        Feed(parser, Esc + "[!p");
        Assert.True(adapter.Buffer.AutoWrap);
    }

    [Fact]
    public void SoftReset_ResetsKeyboardModes()
    {
        var (adapter, parser, _) = Create();
        Feed(parser, Esc + "[?1h" + Esc + "=" + Esc + "[>4;2m");
        Assert.True(adapter.ApplicationCursorKeysEnabled);
        Assert.True(adapter.KeypadApplicationMode);
        Assert.Equal(2, adapter.ModifyOtherKeysLevel);

        Feed(parser, Esc + "[!p");

        Assert.False(adapter.ApplicationCursorKeysEnabled);
        Assert.False(adapter.KeypadApplicationMode);
        Assert.Equal(0, adapter.ModifyOtherKeysLevel);
    }

    [Fact]
    public void SoftReset_ResetsSgrAttributes()
    {
        var (adapter, parser, _) = Create();
        Feed(parser, Esc + "[1;4;31m" + Esc + "[!pz");

        Assert.Equal('z', adapter.Buffer.GetRowText(0)[0]);
        Assert.Equal(0, adapter.Buffer.GetCell(0, 0).StyleId);
    }

    [Fact]
    public void SoftReset_ResetsCharsets()
    {
        var (adapter, parser, _) = Create();
        Feed(parser, Esc + "(0" + Esc + "[!pq");

        Assert.Equal('q', adapter.Buffer.GetRowText(0)[0]);
    }

    [Fact]
    public void SoftReset_ResetsSavedCursorToHomeWithDefaultAttributes()
    {
        var (adapter, parser, _) = Create();
        Feed(parser, Esc + "[1;31m" + Esc + "[3;3H" + Esc + "7");
        Feed(parser, Esc + "[!p");
        Feed(parser, Esc + "[5;5H" + Esc + "8" + "w");

        Assert.Equal('w', adapter.Buffer.GetRowText(0)[0]);
        Assert.Equal(0, adapter.Buffer.GetCell(0, 0).StyleId);
        Assert.Equal(0, adapter.Buffer.CursorRow);
        Assert.Equal(1, adapter.Buffer.CursorCol);
    }

    [Fact]
    public void SoftReset_RestoreWithoutPriorSave_LandsAtHome()
    {
        var (adapter, parser, _) = Create();
        Feed(parser, Esc + "[4;4H" + Esc + "[!p" + Esc + "8");

        Assert.Equal(0, adapter.Buffer.CursorRow);
        Assert.Equal(0, adapter.Buffer.CursorCol);
    }

    [Fact]
    public void SoftReset_ResetsOsc4PaletteButNotDynamicColors()
    {
        var (adapter, parser, replies) = Create();
        Feed(parser, "\x1b]4;1;?\x07\x1b]10;?\x07");
        string baseline4 = replies[0], baselineFg = replies[1];
        replies.Clear();

        Feed(parser, "\x1b]4;1;rgb:00/ff/00\x07\x1b]10;rgb:11/22/33\x07");
        Feed(parser, Esc + "[!p");
        Feed(parser, "\x1b]4;1;?\x07\x1b]10;?\x07");

        Assert.Equal(2, replies.Count);
        Assert.Equal(baseline4, replies[0]);
        Assert.NotEqual(baselineFg, replies[1]);
        Assert.Equal("\x1b]10;rgb:1111/2222/3333\x1b\\", replies[1]);
    }

    [Fact]
    public void SoftReset_PreservesModesTabStopsTitleAndAlternateScreen()
    {
        var (adapter, parser, replies) = Create();
        Feed(parser, Esc + "[?1049h" + Esc + "[?2004h" + Esc + "[?1000h" + Esc + "[?1006h" + Esc + "[?1004h" + Esc + "[=1;1u");
        Feed(parser, Esc + "[1;4H" + Esc + "H" + "\x1b]2;hello\x07");
        Feed(parser, Esc + "[?2026h");

        Feed(parser, Esc + "[!p");

        Assert.True(adapter.Buffer.IsAlternateScreenActive);
        Assert.True(adapter.Buffer.BracketedPasteMode);
        Assert.Equal(TerminalAdapter.MouseMode.Normal, adapter.CurrentMouseMode);
        Assert.Equal(TerminalAdapter.MouseEncoding.SGR, adapter.CurrentMouseEncoding);
        Assert.True(adapter.FocusReportingEnabled);
        Assert.True(adapter.SynchronizedUpdateActive);
        Assert.Equal(1, adapter.KittyKeyboardFlags);
        Assert.Equal(3, adapter.Buffer.GetNextTabStopFrom(0));
        Assert.Equal("hello", adapter.WindowTitle);
        Assert.Empty(replies);
    }

    [Fact]
    public void SoftReset_AcceptsNumericParameterAndSplitInput()
    {
        var (adapter, parser, _) = Create();
        Feed(parser, Esc + "[?25l" + Esc + "[?7l");
        foreach (byte b in Encoding.ASCII.GetBytes(Esc + "[5!p"))
            parser.Feed(new[] { b });

        Assert.True(adapter.Buffer.CursorVisible);
        Assert.True(adapter.Buffer.AutoWrap);

        Feed(parser, Esc + "[?25l");
        foreach (byte b in Encoding.ASCII.GetBytes(Esc + "[!p"))
            parser.Feed(new[] { b });
        Assert.True(adapter.Buffer.CursorVisible);
    }

    [Fact]
    public void SoftReset_IsIdempotent()
    {
        var (adapter, parser, _) = Create();
        Feed(parser, "abc" + Esc + "[?25l" + Esc + "[3;5r" + Esc + "[?6h" + Esc + "[?7l" + Esc + "[?1h" + Esc + "=" + Esc + "[1;31m");
        Feed(parser, Esc + "[2;4H" + Esc + "[?2004h" + Esc + "[?1000h");

        Feed(parser, Esc + "[!p");
        string once = State(adapter);
        Feed(parser, Esc + "[!p");


        Assert.Equal(once, State(adapter));
    }
    [Fact]
    public void ResetBoundariesKeepAndClearInputAndBehavioralModesAsXtermDoes()
    {
        var (adapter, parser, replies) = Create();
        Feed(parser, $"{Esc}[2h{Esc}[12l{Esc}[?4h{Esc}[?18h{Esc}[?19h{Esc}[?35l{Esc}[?42h{Esc}[2*x{Esc}(0{Esc}[!p");

        Assert.False(adapter.KeyboardActionModeLocked);
        Assert.True(adapter.LocalEchoEnabled);
        Assert.True(adapter.SmoothScrollEnabled);
        Assert.True(adapter.PrintFormFeedEnabled);
        Assert.True(adapter.PrintExtentFullPage);
        Assert.False(adapter.FontShiftModeEnabled);
        Assert.True(adapter.NationalReplacementEnabled);
        adapter.OnRequestStatusString("*x"u8);
        Assert.Equal("\x1bP1$r2*x\x1b\\", Assert.Single(replies));
        replies.Clear();
        Feed(parser, "q");
        Assert.Equal("q", adapter.Buffer.GetRowText(0)[0].ToString());

        Feed(parser, $"{Esc}[2h{Esc}[12h{Esc}[?18h{Esc}[?19h{Esc}[?4h{Esc}[?42h{Esc}[2*x{Esc}(0{Esc}c");
        Assert.False(adapter.KeyboardActionModeLocked);
        Assert.False(adapter.LocalEchoEnabled);
        Assert.False(adapter.SmoothScrollEnabled);
        Assert.True(adapter.PrintFormFeedEnabled);
        Assert.True(adapter.PrintExtentFullPage);
        Assert.False(adapter.FontShiftModeEnabled);
        Assert.False(adapter.NationalReplacementEnabled);
        adapter.OnRequestStatusString("*x"u8);
        Assert.Equal("\x1bP1$r0*x\x1b\\", Assert.Single(replies));
        Feed(parser, "q");
        Assert.Equal("q", adapter.Buffer.GetRowText(0)[0].ToString());
    }

    [Fact]
    public void SoftReset_DiffersFromFullResetOnPreservedState()
    {
        var (adapter, parser, _) = Create();
        Feed(parser, "abc" + "\x1b]2;hello\x07" + Esc + "[?2004h" + Esc + "[?1000h" + Esc + "[?1004h" + Esc + "[2;4H");

        Feed(parser, Esc + "[!p");
        Assert.Equal("abc", adapter.Buffer.GetRowText(0).TrimEnd());
        Assert.True(adapter.Buffer.BracketedPasteMode);

        Feed(parser, Esc + "c");
        Assert.Equal("", adapter.Buffer.GetRowText(0).TrimEnd());
        Assert.False(adapter.Buffer.BracketedPasteMode);
        Assert.Equal(TerminalAdapter.MouseMode.None, adapter.CurrentMouseMode);
        Assert.False(adapter.FocusReportingEnabled);
    }

    [Fact]
    public void SoftReset_KeepsBackarrowKeyModeAndVisibilityReports()
    {
        var (adapter, parser, _) = Create();
        Feed(parser, Esc + "[?67h" + Esc + "[?2033h");

        Feed(parser, Esc + "[!p");

        Assert.True(adapter.BackarrowSendsBackspace);
        Assert.True(adapter.VisibilityReportsEnabled);
    }
}
