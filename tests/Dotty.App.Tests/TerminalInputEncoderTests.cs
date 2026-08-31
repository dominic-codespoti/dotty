using Dotty.Runtime.Input;
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
            modifiers: TerminalKeyModifiers.Control);

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
            modifiers: TerminalKeyModifiers.None);

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
            modifiers: TerminalKeyModifiers.None);

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
            modifiers: TerminalKeyModifiers.None);

        Assert.NotNull(bytes);
        Assert.Equal("\u001b[<64;4;3M", Encoding.UTF8.GetString(bytes!));
    }
    [Fact]
    public void Encode_CtrlBackspace_EncodesWordErase()
    {
        var encoder = new TerminalInputEncoder();
        var bytes = encoder.Encode(TerminalKey.Backspace, TerminalKeyModifiers.Control);

        Assert.NotNull(bytes);
        Assert.Equal(new byte[] { 0x17 }, bytes); // ^W (Unix werase / word delete)
    }

    [Fact]
    public void Encode_AltBackspace_EncodesEscapeDel()
    {
        var encoder = new TerminalInputEncoder();
        var bytes = encoder.Encode(TerminalKey.Backspace, TerminalKeyModifiers.Alt);

        Assert.NotNull(bytes);
        Assert.Equal(new byte[] { 0x1b, 0x7f }, bytes); // \e\x7f (backward-kill-word)
    }

    [Fact]
    public void Encode_CtrlDelete_EncodesKillWord()
    {
        var encoder = new TerminalInputEncoder();
        var bytes = encoder.Encode(TerminalKey.Delete, TerminalKeyModifiers.Control);

        Assert.NotNull(bytes);
        Assert.Equal("\u001b[3;5~", Encoding.UTF8.GetString(bytes!));
    }

    [Fact]
    public void Encode_AltDelete_EncodesKillWord()
    {
        var encoder = new TerminalInputEncoder();
        var bytes = encoder.Encode(TerminalKey.Delete, TerminalKeyModifiers.Alt);

        Assert.NotNull(bytes);
        Assert.Equal("\u001b[3;3~", Encoding.UTF8.GetString(bytes!));
    }

    [Fact]
    public void Encode_CtrlArrow_EncodesWordMovement()
    {
        var encoder = new TerminalInputEncoder();
        var left = encoder.Encode(TerminalKey.Left, TerminalKeyModifiers.Control);
        var right = encoder.Encode(TerminalKey.Right, TerminalKeyModifiers.Control);

        Assert.NotNull(left);
        Assert.NotNull(right);
        Assert.Equal("\u001b[1;5D", Encoding.UTF8.GetString(left!));
        Assert.Equal("\u001b[1;5C", Encoding.UTF8.GetString(right!));
    }

    [Fact]
    public void Encode_AltArrow_EncodesWordMovement()
    {
        var encoder = new TerminalInputEncoder();
        var left = encoder.Encode(TerminalKey.Left, TerminalKeyModifiers.Alt);
        var right = encoder.Encode(TerminalKey.Right, TerminalKeyModifiers.Alt);

        Assert.NotNull(left);
        Assert.NotNull(right);
        Assert.Equal("\u001b[1;3D", Encoding.UTF8.GetString(left!));
        Assert.Equal("\u001b[1;3C", Encoding.UTF8.GetString(right!));
    }

    [Fact]
    public void Encode_AltLetter_EncodesMetaPrefix()
    {
        var encoder = new TerminalInputEncoder();
        var altB = encoder.Encode(TerminalKey.B, TerminalKeyModifiers.Alt);
        var altF = encoder.Encode(TerminalKey.F, TerminalKeyModifiers.Alt);
        var altD = encoder.Encode(TerminalKey.D, TerminalKeyModifiers.Alt);

        Assert.NotNull(altB);
        Assert.NotNull(altF);
        Assert.NotNull(altD);
        Assert.Equal(new byte[] { 0x1b, (byte)'b' }, altB);
        Assert.Equal(new byte[] { 0x1b, (byte)'f' }, altF);
        Assert.Equal(new byte[] { 0x1b, (byte)'d' }, altD);
    }

    [Fact]
    public void Encode_ShiftTab_EncodesBackTab()
    {
        var encoder = new TerminalInputEncoder();
        var bytes = encoder.Encode(TerminalKey.Tab, TerminalKeyModifiers.Shift);

        Assert.NotNull(bytes);
        Assert.Equal("\u001b[Z", Encoding.UTF8.GetString(bytes!));
    }

    [Fact]
    public void Encode_CtrlShortcuts_EncodesControlAscii()
    {
        var encoder = new TerminalInputEncoder();
        var ctrlW = encoder.Encode(TerminalKey.W, TerminalKeyModifiers.Control);
        var ctrlU = encoder.Encode(TerminalKey.U, TerminalKeyModifiers.Control);
        var ctrlK = encoder.Encode(TerminalKey.K, TerminalKeyModifiers.Control);
        var ctrlA = encoder.Encode(TerminalKey.A, TerminalKeyModifiers.Control);
        var ctrlE = encoder.Encode(TerminalKey.E, TerminalKeyModifiers.Control);

        Assert.NotNull(ctrlW);
        Assert.NotNull(ctrlU);
        Assert.NotNull(ctrlK);
        Assert.NotNull(ctrlA);
        Assert.NotNull(ctrlE);

        Assert.Equal(new byte[] { 0x17 }, ctrlW); // 23
        Assert.Equal(new byte[] { 0x15 }, ctrlU); // 21
        Assert.Equal(new byte[] { 0x0B }, ctrlK); // 11
        Assert.Equal(new byte[] { 0x01 }, ctrlA); // 1
        Assert.Equal(new byte[] { 0x05 }, ctrlE); // 5
    }
    [Theory]
    [InlineData(TerminalKey.Up, "\x1bOA")]
    [InlineData(TerminalKey.Down, "\x1bOB")]
    [InlineData(TerminalKey.Right, "\x1bOC")]
    [InlineData(TerminalKey.Left, "\x1bOD")]
    public void Encode_ApplicationCursorKeys_UseApplicationArrows(TerminalKey key, string expected)
    {
        var encoder = new TerminalInputEncoder();

        var bytes = encoder.Encode(key, TerminalKeyModifiers.None, applicationCursorKeys: true);

        Assert.Equal(expected, Encoding.ASCII.GetString(bytes!));
    }

    [Fact]
    public void Encode_ApplicationCursorKeys_ModifiedArrowsRemainCsi()
    {
        var encoder = new TerminalInputEncoder();

        var bytes = encoder.Encode(
            TerminalKey.Up,
            TerminalKeyModifiers.Control,
            applicationCursorKeys: true);

        Assert.Equal("\x1b[1;5A", Encoding.ASCII.GetString(bytes!));
    }

    [Fact]
    public void Encode_ApplicationCursorKeys_DisabledUsesLegacyArrow()
    {
        var encoder = new TerminalInputEncoder();

        var bytes = encoder.Encode(
            TerminalKey.Left,
            TerminalKeyModifiers.None,
            applicationCursorKeys: false);

        Assert.Equal("\x1b[D", Encoding.ASCII.GetString(bytes!));
    }

    [Fact]
    public void Encode_KittyAndSuperModesRemainSelected()
    {
        var encoder = new TerminalInputEncoder { KittyMode = 1 };

        var bytes = encoder.Encode(
            TerminalKey.Up,
            TerminalKeyModifiers.Meta,
            applicationCursorKeys: true);

        Assert.Equal("\x1b[1;9:", Encoding.ASCII.GetString(bytes!));
    }

    [Fact]
    public void Encode_UnsupportedKey_ReturnsNull()
    {
        var encoder = new TerminalInputEncoder();

        Assert.Null(encoder.Encode(TerminalKey.Unknown, TerminalKeyModifiers.None));
    }
}
 
