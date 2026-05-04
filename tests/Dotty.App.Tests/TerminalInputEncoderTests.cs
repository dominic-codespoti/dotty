using Avalonia.Input;
using Dotty.App.Input;
using Dotty.Terminal.Adapter;
using System.Text;
using Xunit;

namespace Dotty.App.Tests;

public class TerminalInputEncoderTests
{
    [Fact]
    public void EncodeMouseEvent_SgrPress_EncodesExpectedSequence()
    {
        var encoder = new TerminalInputEncoder();

        var bytes = encoder.EncodeMouseEvent(
            TerminalAdapter.MouseMode.Normal,
            TerminalAdapter.MouseEncoding.SGR,
            button: 0,
            row: 4,
            column: 9,
            isPress: true,
            isMove: false,
            modifiers: KeyModifiers.Control);

        Assert.NotNull(bytes);
        Assert.Equal("\u001b[<16;10;5M", Encoding.UTF8.GetString(bytes!));
    }

    [Fact]
    public void EncodeMouseEvent_SgrRelease_EncodesExpectedSequence()
    {
        var encoder = new TerminalInputEncoder();

        var bytes = encoder.EncodeMouseEvent(
            TerminalAdapter.MouseMode.Normal,
            TerminalAdapter.MouseEncoding.SGR,
            button: 0,
            row: 1,
            column: 2,
            isPress: false,
            isMove: false,
            modifiers: KeyModifiers.None);

        Assert.NotNull(bytes);
        Assert.Equal("\u001b[<0;3;2m", Encoding.UTF8.GetString(bytes!));
    }

    [Fact]
    public void EncodeMouseEvent_ButtonEventMoveWithoutButton_ReturnsNull()
    {
        var encoder = new TerminalInputEncoder();

        var bytes = encoder.EncodeMouseEvent(
            TerminalAdapter.MouseMode.ButtonEvent,
            TerminalAdapter.MouseEncoding.SGR,
            button: 3,
            row: 0,
            column: 0,
            isPress: true,
            isMove: true,
            modifiers: KeyModifiers.None);

        Assert.Null(bytes);
    }

    [Fact]
    public void EncodeMouseEvent_WheelUp_EncodesExpectedSequence()
    {
        var encoder = new TerminalInputEncoder();

        var bytes = encoder.EncodeMouseEvent(
            TerminalAdapter.MouseMode.Normal,
            TerminalAdapter.MouseEncoding.SGR,
            button: 64,
            row: 2,
            column: 3,
            isPress: true,
            isMove: false,
            modifiers: KeyModifiers.None);

        Assert.NotNull(bytes);
        Assert.Equal("\u001b[<64;4;3M", Encoding.UTF8.GetString(bytes!));
    }
}
