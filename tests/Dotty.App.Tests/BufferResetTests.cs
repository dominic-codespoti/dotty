using Dotty.Terminal.Adapter;
using Xunit;

namespace Dotty.App.Tests;

public class BufferResetTests
{
    [Fact]
    public void SoftReset_KeepsPositionAlternateScreenAndTabStops_ResetsModes()
    {
        var tb = new TerminalBuffer(6, 10);
        tb.SetAlternateScreen(true);
        tb.SetScrollRegion(3, 5);
        tb.SetOriginMode(true);
        tb.SetAutoWrap(false);
        tb.SetCursorVisible(false);
        tb.SetBracketedPasteMode(true);
        tb.SetTabStopAt(3);
        tb.SetCursor(4, 2);

        tb.SoftReset();

        Assert.True(tb.IsAlternateScreenActive);
        Assert.True(tb.CursorVisible);
        Assert.True(tb.AutoWrap);
        Assert.False(tb.OriginMode);
        Assert.True(tb.BracketedPasteMode);
        Assert.Equal(3, tb.GetNextTabStopFrom(0));
        Assert.Equal(4, tb.CursorRow);
        Assert.Equal(2, tb.CursorCol);

        tb.RestoreCursor();
        Assert.Equal(0, tb.CursorRow);
        Assert.Equal(0, tb.CursorCol);
    }

    [Fact]
    public void FullReset_LeavesAlternateScreenAndClearsPromptMarks()
    {
        var tb = new TerminalBuffer(6, 10);
        tb.AddPromptMark(PromptKind.Prompt);
        tb.SetAlternateScreen(true);
        tb.SetCursorVisible(false);
        tb.SetOriginMode(true);

        tb.FullReset();

        Assert.False(tb.IsAlternateScreenActive);
        Assert.True(tb.CursorVisible);
        Assert.False(tb.OriginMode);
        Assert.Empty(tb.GetPromptMarks());
        Assert.Equal(0, tb.CursorRow);
    }
}
