using Dotty.App.Controls;
using Dotty.App.Input;
using Xunit;

namespace Dotty.App.Tests;

public sealed class TerminalTextInputMethodClientTests
{
    [Fact]
    public void SetPreeditText_UpdatesStateAndCanvas()
    {
        var canvas = new TerminalCanvas();
        var client = new TerminalTextInputMethodClient(canvas);

        Assert.True(client.SupportsPreedit);
        Assert.False(client.SupportsSurroundingText);

        client.SetPreeditText("abc", 2);
        Assert.Equal("abc", client.PreeditText);
        Assert.Equal(2, client.PreeditCursor);
        Assert.Equal("abc", GetCanvasPreedit(canvas));

        // A null preedit clears composition.
        client.SetPreeditText(null);
        Assert.Null(client.PreeditText);
        Assert.Null(GetCanvasPreedit(canvas));
    }

    [Fact]
    public void ResetComposition_ClearsOnlyWhenComposing()
    {
        var canvas = new TerminalCanvas();
        var client = new TerminalTextInputMethodClient(canvas);

        client.ResetComposition(); // no-op when not composing
        Assert.Null(client.PreeditText);

        client.SetPreeditText("ä", null);
        Assert.Equal("ä", client.PreeditText);

        client.ResetComposition();
        Assert.Null(client.PreeditText);
        Assert.Null(GetCanvasPreedit(canvas));
    }

    [Fact]
    public void CursorRectangle_FallsBackSafelyWithoutBuffer()
    {
        var canvas = new TerminalCanvas();
        var client = new TerminalTextInputMethodClient(canvas);

        // No buffer attached: the rect must still be a finite, non-empty cell.
        var rect = client.CursorRectangle;
        Assert.True(rect.Width > 0 && rect.Height > 0);
        Assert.True(double.IsFinite(rect.X) && double.IsFinite(rect.Y));
    }

    private static string? GetCanvasPreedit(TerminalCanvas canvas)
    {
        var field = typeof(TerminalCanvas).GetField(
            "_preeditText",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return field?.GetValue(canvas) as string;
    }
}
