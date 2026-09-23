using SilkKey = Silk.NET.Input.Key;
using System;
using Dotty.Runtime.Input;
using Dotty.Silk;
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

        Assert.Equal("\x1b[1;9A", Encoding.ASCII.GetString(bytes!));
    }

    [Theory]
    [InlineData(TerminalKey.Up, "\x1b[A")]
    [InlineData(TerminalKey.Down, "\x1b[B")]
    [InlineData(TerminalKey.Right, "\x1b[C")]
    [InlineData(TerminalKey.Left, "\x1b[D")]
    [InlineData(TerminalKey.Home, "\x1b[H")]
    [InlineData(TerminalKey.End, "\x1b[F")]
    [InlineData(TerminalKey.PageUp, "\x1b[5~")]
    [InlineData(TerminalKey.PageDown, "\x1b[6~")]
    [InlineData(TerminalKey.Insert, "\x1b[2~")]
    [InlineData(TerminalKey.Delete, "\x1b[3~")]
    public void Encode_KittyNavigation_UsesLegacyFunctionalSequences(TerminalKey key, string expected)
    {
        var encoder = new TerminalInputEncoder { KittyMode = 2 };

        Assert.Equal(expected, Encoding.ASCII.GetString(encoder.Encode(key, TerminalKeyModifiers.None)!));
    }

    [Theory]
    [InlineData(TerminalKey.F1, "\x1bOP")]
    [InlineData(TerminalKey.F2, "\x1bOQ")]
    [InlineData(TerminalKey.F3, "\x1bOR")]
    [InlineData(TerminalKey.F4, "\x1bOS")]
    [InlineData(TerminalKey.F5, "\x1b[15~")]
    [InlineData(TerminalKey.F6, "\x1b[17~")]
    [InlineData(TerminalKey.F7, "\x1b[18~")]
    [InlineData(TerminalKey.F8, "\x1b[19~")]
    [InlineData(TerminalKey.F9, "\x1b[20~")]
    [InlineData(TerminalKey.F10, "\x1b[21~")]
    [InlineData(TerminalKey.F11, "\x1b[23~")]
    [InlineData(TerminalKey.F12, "\x1b[24~")]
    public void Encode_KittyFunctionKeysF1ToF12_UseLegacyFunctionalSequences(TerminalKey key, string expected)
    {
        var encoder = new TerminalInputEncoder { KittyMode = 1 };

        Assert.Equal(expected, Encoding.ASCII.GetString(encoder.Encode(key, TerminalKeyModifiers.None)!));
    }

    [Theory]
    [InlineData(TerminalKey.Up, TerminalKeyModifiers.Shift, "\x1b[1;2A")]
    [InlineData(TerminalKey.Left, TerminalKeyModifiers.Alt, "\x1b[1;3D")]
    [InlineData(TerminalKey.Delete, TerminalKeyModifiers.Control, "\x1b[3;5~")]
    public void Encode_KittyModifiedSpecialKeys_UsesModifierParameter(
        TerminalKey key,
        TerminalKeyModifiers modifiers,
        string expected)
    {
        var encoder = new TerminalInputEncoder { KittyMode = 1 };

        Assert.Equal(expected, Encoding.ASCII.GetString(encoder.Encode(key, modifiers)!));
    }

    [Theory]
    [InlineData(TerminalKey.F13, "\x1b[57376u")]
    [InlineData(TerminalKey.F14, "\x1b[57377u")]
    [InlineData(TerminalKey.F15, "\x1b[57378u")]
    [InlineData(TerminalKey.F16, "\x1b[57379u")]
    [InlineData(TerminalKey.F17, "\x1b[57380u")]
    [InlineData(TerminalKey.F18, "\x1b[57381u")]
    [InlineData(TerminalKey.F19, "\x1b[57382u")]
    [InlineData(TerminalKey.F20, "\x1b[57383u")]
    [InlineData(TerminalKey.F21, "\x1b[57384u")]
    [InlineData(TerminalKey.F22, "\x1b[57385u")]
    [InlineData(TerminalKey.F23, "\x1b[57386u")]
    [InlineData(TerminalKey.F24, "\x1b[57387u")]
    public void Encode_KittyFunctionKeysF13ToF24_UsePrivateUseCodes(TerminalKey key, string expected)
    {
        var encoder = new TerminalInputEncoder { KittyMode = 1 };

        Assert.Equal(expected, Encoding.ASCII.GetString(encoder.Encode(key, TerminalKeyModifiers.None)!));
    }

    [Theory]
    [InlineData(TerminalKey.F1, TerminalKeyModifiers.Shift, "\x1b[1;2P")]
    [InlineData(TerminalKey.F5, TerminalKeyModifiers.Alt, "\x1b[15;3~")]
    [InlineData(TerminalKey.F12, TerminalKeyModifiers.Control, "\x1b[24;5~")]
    [InlineData(TerminalKey.F21, TerminalKeyModifiers.Meta, "\x1b[57384;9u")]
    public void Encode_KittyFunctionKeys_PreserveModifiers(
        TerminalKey key,
        TerminalKeyModifiers modifiers,
        string expected)
    {
        var encoder = new TerminalInputEncoder { KittyMode = 1 };

        Assert.Equal(expected, Encoding.ASCII.GetString(encoder.Encode(key, modifiers)!));
    }

    [Theory]
    [InlineData(TerminalKey.Keypad0, "0")]
    [InlineData(TerminalKey.Keypad5, "5")]
    [InlineData(TerminalKey.Keypad9, "9")]
    [InlineData(TerminalKey.KeypadDecimal, ".")]
    [InlineData(TerminalKey.KeypadDivide, "/")]
    [InlineData(TerminalKey.KeypadMultiply, "*")]
    [InlineData(TerminalKey.KeypadSubtract, "-")]
    [InlineData(TerminalKey.KeypadAdd, "+")]
    [InlineData(TerminalKey.KeypadEnter, "\r")]
    [InlineData(TerminalKey.KeypadEqual, "=")]
    public void Encode_NumericKeypad_UsesTextBytesOutsideApplicationMode(TerminalKey key, string expected)
    {
        var encoder = new TerminalInputEncoder();

        Assert.Equal(expected, Encoding.ASCII.GetString(encoder.Encode(key, TerminalKeyModifiers.None)!));
    }

    [Theory]
    [InlineData(TerminalKey.Keypad0, "\x1bOp")]
    [InlineData(TerminalKey.Keypad5, "\x1bOu")]
    [InlineData(TerminalKey.Keypad9, "\x1bOy")]
    [InlineData(TerminalKey.KeypadDecimal, "\x1bOn")]
    [InlineData(TerminalKey.KeypadDivide, "\x1bOl")]
    [InlineData(TerminalKey.KeypadMultiply, "\x1bOR")]
    [InlineData(TerminalKey.KeypadSubtract, "\x1bOS")]
    [InlineData(TerminalKey.KeypadAdd, "\x1bOm")]
    [InlineData(TerminalKey.KeypadEnter, "\x1bOM")]
    [InlineData(TerminalKey.KeypadEqual, "=")]
    public void Encode_ApplicationKeypad_UsesCompleteSs3Sequences(TerminalKey key, string expected)
    {
        var encoder = new TerminalInputEncoder();

        Assert.Equal(expected, Encoding.ASCII.GetString(encoder.Encode(
            key,
            TerminalKeyModifiers.None,
            keypadApplicationMode: true)!));
    }

    [Theory]
    [InlineData(TerminalKey.F21, TerminalKeyModifiers.None, "\x1b[42~")]
    [InlineData(TerminalKey.F22, TerminalKeyModifiers.None, "\x1b[43~")]
    [InlineData(TerminalKey.F23, TerminalKeyModifiers.None, "\x1b[44~")]
    [InlineData(TerminalKey.F24, TerminalKeyModifiers.None, "\x1b[45~")]
    [InlineData(TerminalKey.F21, TerminalKeyModifiers.Shift, "\x1b[42;2~")]
    [InlineData(TerminalKey.F22, TerminalKeyModifiers.Alt, "\x1b[43;3~")]
    [InlineData(TerminalKey.F23, TerminalKeyModifiers.Control, "\x1b[44;5~")]
    [InlineData(TerminalKey.F24, TerminalKeyModifiers.Meta, "\x1b[45;9~")]
    public void Encode_FunctionKeysF21ToF24_UseXtermCodes(
        TerminalKey key,
        TerminalKeyModifiers modifiers,
        string expected)
    {
        var encoder = new TerminalInputEncoder();

        Assert.Equal(expected, Encoding.ASCII.GetString(encoder.Encode(key, modifiers)!));
    }

    [Fact]
    public void Encode_UnsupportedKey_ReturnsNull()
    {
        var encoder = new TerminalInputEncoder();
        Assert.Null(encoder.Encode(TerminalKey.Unknown, TerminalKeyModifiers.None));
    }
}

internal static class TerminalInputEncoderTestExtensions
{
    internal static byte[]? Encode(
        this TerminalInputEncoder encoder,
        TerminalKey key,
        TerminalKeyModifiers modifiers,
        bool keypadApplicationMode = false,
        bool applicationCursorKeys = false)
    {
        Span<byte> buffer = stackalloc byte[64];
        int length = encoder.Encode(key, modifiers, buffer, keypadApplicationMode, applicationCursorKeys);
        return length == 0 ? null : buffer[..length].ToArray();
    }

    internal static byte[]? EncodeMouseEvent(
        this TerminalInputEncoder encoder,
        TerminalAdapter.MouseMode mode,
        TerminalAdapter.MouseEncoding encoding,
        int button,
        int row,
        int column,
        bool isPress,
        bool isMove,
        TerminalKeyModifiers modifiers)
    {
        Span<byte> buffer = stackalloc byte[64];
        int length = encoder.EncodeMouseEvent(mode, encoding, button, row, column, isPress, isMove, modifiers, buffer);
        return length == 0 ? null : buffer[..length].ToArray();
    }
}

internal static class SilkKeyMapperTestEncoding
{
    internal static byte[]? Encode(
        SilkKey key,
        bool ctrl,
        bool shift,
        bool alt,
        bool keypadAppMode,
        int kittyMode = 0,
        bool super = false,
        bool applicationCursorKeys = false)
    {
        Span<byte> buffer = stackalloc byte[64];
        int length = SilkKeyMapper.Encode(
            key, ctrl, shift, alt, keypadAppMode, buffer, kittyMode, super, applicationCursorKeys);
        return length == 0 ? null : buffer[..length].ToArray();
    }
}


