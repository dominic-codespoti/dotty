using System;
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
    public void EncodeKeyEvent_FlagsSelectKittyAndPreserveLegacyFallback()
    {
        var encoder = new TerminalInputEncoder();
        Span<byte> buffer = stackalloc byte[128];
        int kittyLength = encoder.EncodeKeyEvent(TerminalKey.A, TerminalKeyModifiers.None, 'a', TerminalKeyEventType.Press, "".AsSpan(), buffer, 8);
        Assert.Equal("\x1b[97;1u", Encoding.ASCII.GetString(buffer[..kittyLength]));
        int legacyLength = encoder.EncodeKeyEvent(TerminalKey.Up, TerminalKeyModifiers.None, 0, TerminalKeyEventType.Press, "".AsSpan(), buffer, 0);
        Assert.Equal("\x1b[A", Encoding.ASCII.GetString(buffer[..legacyLength]));
        int disambiguatedLength = encoder.EncodeKeyEvent(TerminalKey.Escape, TerminalKeyModifiers.None, 27, TerminalKeyEventType.Press, "".AsSpan(), buffer, 1);
        Assert.Equal("\x1b[27;1u", Encoding.ASCII.GetString(buffer[..disambiguatedLength]));
    }

    [Fact]
    public void EncodeKeyEvent_EventAlternateAndAssociatedTextFieldsHonorFlags()
    {
        var encoder = new TerminalInputEncoder();
        Span<byte> buffer = stackalloc byte[128];
        int length = encoder.EncodeKeyEvent(TerminalKey.A, TerminalKeyModifiers.Shift, 'a', TerminalKeyEventType.Repeat, "a😀".AsSpan(), buffer, 8 | 2 | 4 | 16, shiftedCodepoint: 'A', baseCodepoint: 'a');
        Assert.Equal("\x1b[97:65:97;2:2;97:128512u", Encoding.ASCII.GetString(buffer[..length]));

        length = encoder.EncodeKeyEvent(TerminalKey.A, TerminalKeyModifiers.None, 'a', TerminalKeyEventType.Release, "a".AsSpan(), buffer, 8 | 2 | 16);
        Assert.Equal("\x1b[97;1:3u", Encoding.ASCII.GetString(buffer[..length]));
    }

    [Fact]
    public void EncodeKeyEvent_Flag16ReportsTextForNativeRemappedNumericKey()
    {
        var encoder = new TerminalInputEncoder();
        Span<byte> buffer = stackalloc byte[64];
        int length = encoder.EncodeKeyEvent(TerminalKey.Number1, TerminalKeyModifiers.None,
            128578, TerminalKeyEventType.Press, "🙂".AsSpan(), buffer, 31);

        Assert.Equal("\x1b[128578;1:1;128578u", Encoding.ASCII.GetString(buffer[..length]));
    }

    [Fact]
    public void EncodeKeyEvent_ReportsUnknownCommittedTextAndOmitsC0C1Controls()
    {
        var encoder = new TerminalInputEncoder();
        Span<byte> buffer = stackalloc byte[64];

        int length = encoder.EncodeKeyEvent(TerminalKey.Unknown, TerminalKeyModifiers.None, 0,
            TerminalKeyEventType.Press, "å".AsSpan(), buffer, 31);
        Assert.Equal("\x1b[0;;229u", Encoding.ASCII.GetString(buffer[..length]));

        length = encoder.EncodeKeyEvent(TerminalKey.A, TerminalKeyModifiers.None, 'a',
            TerminalKeyEventType.Press, "\u0001\u007f\u0085A".AsSpan(), buffer, 31);
        Assert.Equal("\x1b[97;1:1;65u", Encoding.ASCII.GetString(buffer[..length]));

        length = encoder.EncodeKeyEvent(TerminalKey.Unknown, TerminalKeyModifiers.None, 0,
            TerminalKeyEventType.Press, "\u0000\u001f\u007f\u0080\u009f".AsSpan(), buffer, 31);
        Assert.Equal(0, length);
    }

    [Fact]
    public void EncodeKeyEvent_KeypadUsesKittyPrivateIdentity()
    {
        var encoder = new TerminalInputEncoder();
        Span<byte> buffer = stackalloc byte[32];
        int length = encoder.EncodeKeyEvent(TerminalKey.Keypad5, TerminalKeyModifiers.None, 0, TerminalKeyEventType.Press, "".AsSpan(), buffer, 8);
        Assert.Equal("\x1b[57404;1u", Encoding.ASCII.GetString(buffer[..length]));
        length = encoder.EncodeKeyEvent(TerminalKey.Keypad1, TerminalKeyModifiers.None, '1',
            TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty, buffer, 8);
        Assert.Equal("\x1b[57400;1u", Encoding.ASCII.GetString(buffer[..length]));
    }

    [Theory]
    [InlineData(TerminalKey.Menu, 57363)]
    [InlineData(TerminalKey.F13, 57376)]
    [InlineData(TerminalKey.F24, 57387)]
    [InlineData(TerminalKey.F25, 57388)]
    [InlineData(TerminalKey.NumLock, 57360)]
    [InlineData(TerminalKey.Keypad1, 57400)]
    [InlineData(TerminalKey.CapsLock, 57358)]
    [InlineData(TerminalKey.ScrollLock, 57359)]
    [InlineData(TerminalKey.PrintScreen, 57361)]
    [InlineData(TerminalKey.Pause, 57362)]
    [InlineData(TerminalKey.ShiftLeft, 57441)]
    [InlineData(TerminalKey.ControlLeft, 57442)]
    [InlineData(TerminalKey.AltLeft, 57443)]
    [InlineData(TerminalKey.SuperLeft, 57444)]
    [InlineData(TerminalKey.ShiftRight, 57447)]
    [InlineData(TerminalKey.ControlRight, 57448)]
    [InlineData(TerminalKey.AltRight, 57449)]
    [InlineData(TerminalKey.SuperRight, 57450)]
    public void EncodeKeyEvent_UsesOfficialKittyFunctionalKeyCodepoints(TerminalKey key, int codepoint)

    {
        var encoder = new TerminalInputEncoder();
        Span<byte> buffer = stackalloc byte[32];

        int length = encoder.EncodeKeyEvent(key, TerminalKeyModifiers.None, 0,
            TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty, buffer, 8);

        Assert.Equal($"\x1b[{codepoint};1u", Encoding.ASCII.GetString(buffer[..length]));
    }

    [Fact]
    public void EncodeKeyEvent_AllowsShiftedOnlyAndBaseOnlyAlternateFields()
    {
        var encoder = new TerminalInputEncoder();
        Span<byte> buffer = stackalloc byte[64];

        int length = encoder.EncodeKeyEvent(TerminalKey.A, TerminalKeyModifiers.None, 113,
            TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty, buffer, 8 | 4, baseCodepoint: 'a');
        Assert.Equal("\x1b[113::97;1u", Encoding.ASCII.GetString(buffer[..length]));

        length = encoder.EncodeKeyEvent(TerminalKey.A, TerminalKeyModifiers.Shift, 'a',
            TerminalKeyEventType.Press, "A".AsSpan(), buffer, 8 | 4, shiftedCodepoint: 'A');
        Assert.Equal("\x1b[97:65;2u", Encoding.ASCII.GetString(buffer[..length]));
    }


    [Fact]
    public void EncodeKeyEvent_IncludesNativeCapsAndNumLockBits()
    {
        var encoder = new TerminalInputEncoder();
        Span<byte> buffer = stackalloc byte[32];
        int length = encoder.EncodeKeyEvent(TerminalKey.A, TerminalKeyModifiers.CapsLock | TerminalKeyModifiers.NumLock,
            'a', TerminalKeyEventType.Press, ReadOnlySpan<char>.Empty, buffer, 8);
        Assert.Equal("\x1b[97;193u", Encoding.ASCII.GetString(buffer[..length]));
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
    [InlineData(TerminalKey.F25, TerminalKeyModifiers.None, "\x1b[46~")]
    [InlineData(TerminalKey.F21, TerminalKeyModifiers.Shift, "\x1b[42;2~")]
    [InlineData(TerminalKey.F22, TerminalKeyModifiers.Alt, "\x1b[43;3~")]
    [InlineData(TerminalKey.F23, TerminalKeyModifiers.Control, "\x1b[44;5~")]
    [InlineData(TerminalKey.F24, TerminalKeyModifiers.Meta, "\x1b[45;9~")]
    public void Encode_FunctionKeysF21ToF25_UseXtermCodes(
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

    [Fact]
    public void Encode_ModifyOtherKeysFallback_EmitsXtermSequence()
    {
        var encoder = new TerminalInputEncoder();
        // Ctrl+, has no legacy control encoding; without a level it stays unsupported.
        Assert.Null(encoder.Encode(TerminalKey.Comma, TerminalKeyModifiers.Control));
        Assert.Equal("\x1b[27;5;44~", Encoding.ASCII.GetString(encoder.Encode(
            TerminalKey.Comma, TerminalKeyModifiers.Control, modifyOtherKeysLevel: 1)!));
        // Legacy Ctrl+letter keeps its control byte even when a level is negotiated.
        Assert.Equal("\x17", Encoding.ASCII.GetString(encoder.Encode(
            TerminalKey.W, TerminalKeyModifiers.Control, modifyOtherKeysLevel: 2)!));
    }
}

internal static class TerminalInputEncoderTestExtensions
{
    internal static byte[]? Encode(
        this TerminalInputEncoder encoder,
        TerminalKey key,
        TerminalKeyModifiers modifiers,
        bool keypadApplicationMode = false,
        bool applicationCursorKeys = false,
        int modifyOtherKeysLevel = 0)
    {
        Span<byte> buffer = stackalloc byte[64];
        int length = encoder.Encode(key, modifiers, buffer, keypadApplicationMode, applicationCursorKeys, modifyOtherKeysLevel);
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
