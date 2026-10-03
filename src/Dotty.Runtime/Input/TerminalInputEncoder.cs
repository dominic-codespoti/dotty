using System;
using System.Text;
using Dotty.Terminal.Adapter;

namespace Dotty.Runtime.Input;

/// <summary>
/// Encodes terminal keyboard and mouse events into byte sequences following
/// standard xterm legacy sequences, Kitty keyboard protocol, and SGR/X10 mouse protocols.
/// </summary>
public class TerminalInputEncoder
{
    /// <summary>Encodes a negotiated Kitty keyboard key event, falling back to legacy encoding when unselected.</summary>
    public int EncodeKeyEvent(
        TerminalKey key,
        TerminalKeyModifiers modifiers,
        int primaryCodepoint,
        TerminalKeyEventType eventType,
        ReadOnlySpan<char> associatedText,
        Span<byte> destination,
        int kittyFlags,
        bool keypadApplicationMode = false,
        bool applicationCursorKeys = false,
        int shiftedCodepoint = 0,
        int baseCodepoint = 0)
    {
        int modifier = GetModifier(modifiers);
        bool keypadMapped = TryGetKeypadCode(key, out int codepoint);
        bool mapped = keypadMapped || TryGetKittyCodepoint(key, out codepoint);
        bool reportText = (kittyFlags & 16) != 0 && eventType != TerminalKeyEventType.Release &&
            ContainsReportableText(associatedText);
        bool unknownTextEvent = key == TerminalKey.Unknown && primaryCodepoint == 0 &&
            (kittyFlags & (8 | 16)) == (8 | 16) && eventType != TerminalKeyEventType.Release && reportText;
        if (primaryCodepoint > 0 && !keypadMapped)
            codepoint = primaryCodepoint;
        else if (!mapped && !unknownTextEvent)
            return Encode(key, modifiers, destination, keypadApplicationMode, applicationCursorKeys);

        bool selected = unknownTextEvent || (kittyFlags & 8) != 0 ||
            ((kittyFlags & 1) != 0 && (IsDisambiguationKey(key) || modifier != 1));
        if (!selected)
            return Encode(key, modifiers, destination, keypadApplicationMode, applicationCursorKeys);

        int offset = WriteLiteral(destination, "\x1b[");
        offset = WriteNumber(destination, offset, codepoint);
        if (!unknownTextEvent && (kittyFlags & 4) != 0 && (shiftedCodepoint > 0 || baseCodepoint > 0))
        {
            destination[offset++] = (byte)':';
            if (shiftedCodepoint > 0)
                offset = WriteNumber(destination, offset, shiftedCodepoint);
            if (baseCodepoint > 0)
            {
                destination[offset++] = (byte)':';
                offset = WriteNumber(destination, offset, baseCodepoint);
            }
        }
        destination[offset++] = (byte)';';
        if (unknownTextEvent)
        {
            destination[offset++] = (byte)';';
        }
        else
        {
            offset = WriteNumber(destination, offset, modifier);
            if ((kittyFlags & 2) != 0)
            {
                destination[offset++] = (byte)':';
                offset = WriteNumber(destination, offset, (int)eventType);
            }
            if (reportText)
                destination[offset++] = (byte)';';
        }
        if (reportText)
        {
            int index = 0;
            bool wroteScalar = false;
            while (index < associatedText.Length)
            {
                int scalar = ReadScalar(associatedText, ref index);
                if (IsControlScalar(scalar))
                    continue;
                if (wroteScalar)
                    destination[offset++] = (byte)':';
                offset = WriteNumber(destination, offset, scalar);
                wroteScalar = true;
            }
        }
        destination[offset++] = (byte)'u';
        return offset;
    }

    private static bool ContainsReportableText(ReadOnlySpan<char> text)
    {
        int index = 0;
        while (index < text.Length)
        {
            if (!IsControlScalar(ReadScalar(text, ref index)))
                return true;
        }
        return false;
    }

    private static bool IsControlScalar(int scalar) => scalar <= 0x1F || scalar is >= 0x7F and <= 0x9F;

    /// <summary>Encodes a mouse event into caller-owned storage.</summary>

    public int EncodeMouseEvent(
        TerminalAdapter.MouseMode mode,
        TerminalAdapter.MouseEncoding encoding,
        int button,
        int row,
        int column,
        bool isPress,
        bool isMove,
        TerminalKeyModifiers modifiers,
        Span<byte> destination)
    {
        if (mode == TerminalAdapter.MouseMode.None) return 0;
        if (isMove && mode != TerminalAdapter.MouseMode.ButtonEvent && mode != TerminalAdapter.MouseMode.AnyEvent) return 0;
        if (isMove && button == 3 && mode != TerminalAdapter.MouseMode.AnyEvent) return 0;

        int cb = button;
        if (!isPress && !isMove && encoding != TerminalAdapter.MouseEncoding.SGR) cb = 3;
        if (isMove) cb += 32;
        if ((modifiers & TerminalKeyModifiers.Shift) != 0) cb += 4;
        if ((modifiers & TerminalKeyModifiers.Alt) != 0) cb += 8;
        if ((modifiers & TerminalKeyModifiers.Control) != 0) cb += 16;
        int x = column + 1;
        int y = row + 1;

        int offset = 0;
        destination[offset++] = 0x1b;
        destination[offset++] = (byte)'[';
        if (encoding == TerminalAdapter.MouseEncoding.SGR)
        {
            destination[offset++] = (byte)'<';
            offset = WriteNumber(destination, offset, cb);
            destination[offset++] = (byte)';';
            offset = WriteNumber(destination, offset, x);
            destination[offset++] = (byte)';';
            offset = WriteNumber(destination, offset, y);
            destination[offset++] = (byte)((isPress || isMove) ? 'M' : 'm');
            return offset;
        }

        if (x > 223 || y > 223) return 0;
        destination[offset++] = (byte)'M';
        destination[offset++] = (byte)(cb + 32);
        destination[offset++] = (byte)(x + 32);
        destination[offset++] = (byte)(y + 32);
        return offset;
    }

    /// <summary>Writes a key sequence into caller-owned storage. Returns zero for unsupported keys.</summary>
    public int Encode(
        TerminalKey key,
        TerminalKeyModifiers modifiers,
        Span<byte> destination,
        bool keypadApplicationMode = false,
        bool applicationCursorKeys = false)
    {
        if (TryEncodeKeypad(key, modifiers, keypadApplicationMode, destination, out int keypadLength))
            return keypadLength;

        bool ctrl = (modifiers & TerminalKeyModifiers.Control) != 0;
        bool alt = (modifiers & TerminalKeyModifiers.Alt) != 0;
        bool shift = (modifiers & TerminalKeyModifiers.Shift) != 0;
        int mod = GetModifier(modifiers & (TerminalKeyModifiers.Shift | TerminalKeyModifiers.Alt | TerminalKeyModifiers.Control | TerminalKeyModifiers.Meta));

        if (key == TerminalKey.Backspace)
        {
            if (ctrl && alt) return WriteLiteral(destination, "\x1b\x17");
            if (ctrl) return WriteLiteral(destination, "\x17");
            if (alt) return WriteLiteral(destination, "\x1b\x7f");
            return WriteLiteral(destination, "\x7f");
        }
        if (key == TerminalKey.Delete)
        {
            if (ctrl && alt) return WriteLiteral(destination, "\x1b[3;7~");
            if (ctrl) return WriteLiteral(destination, "\x1b[3;5~");
            if (alt) return WriteLiteral(destination, "\x1b[3;3~");
            if (shift) return WriteLiteral(destination, "\x1b[3;2~");
            return WriteLiteral(destination, "\x1b[3~");
        }
        if (key == TerminalKey.Tab)
        {
            if (shift) return WriteLiteral(destination, "\x1b[Z");
            if (alt) return WriteLiteral(destination, "\x1b\x09");
            return WriteLiteral(destination, "\x09");
        }
        if (key == TerminalKey.Enter) return alt ? WriteLiteral(destination, "\x1b\x0d") : WriteLiteral(destination, "\x0d");
        if (key == TerminalKey.Escape) return alt ? WriteLiteral(destination, "\x1b\x1b") : WriteLiteral(destination, "\x1b");

        if (key is TerminalKey.Up or TerminalKey.Down or TerminalKey.Right or TerminalKey.Left
            or TerminalKey.Home or TerminalKey.End or TerminalKey.PageUp or TerminalKey.PageDown or TerminalKey.Insert)
        {
            if (mod > 1)
            {
                byte final = key switch
                {
                    TerminalKey.Up => (byte)'A',
                    TerminalKey.Down => (byte)'B',
                    TerminalKey.Right => (byte)'C',
                    TerminalKey.Left => (byte)'D',
                    TerminalKey.Home => (byte)'H',
                    TerminalKey.End => (byte)'F',
                    _ => 0
                };
                int code = key switch
                {
                    TerminalKey.PageUp => 5,
                    TerminalKey.PageDown => 6,
                    TerminalKey.Insert => 2,
                    _ => 0
                };
                int offset = WriteLiteral(destination, "\x1b[");
                if (code == 0)
                {
                    destination[offset++] = (byte)'1';
                    destination[offset++] = (byte)';';
                    offset = WriteNumber(destination, offset, mod);
                    destination[offset++] = final;
                }
                else
                {
                    offset = WriteNumber(destination, offset, code);
                    destination[offset++] = (byte)';';
                    offset = WriteNumber(destination, offset, mod);
                    destination[offset++] = (byte)'~';
                }
                return offset;
            }

            return key switch
            {
                TerminalKey.Up => WriteLiteral(destination, applicationCursorKeys ? "\x1bOA" : "\x1b[A"),
                TerminalKey.Down => WriteLiteral(destination, applicationCursorKeys ? "\x1bOB" : "\x1b[B"),
                TerminalKey.Right => WriteLiteral(destination, applicationCursorKeys ? "\x1bOC" : "\x1b[C"),
                TerminalKey.Left => WriteLiteral(destination, applicationCursorKeys ? "\x1bOD" : "\x1b[D"),
                TerminalKey.Home => WriteLiteral(destination, "\x1b[H"),
                TerminalKey.End => WriteLiteral(destination, "\x1b[F"),
                TerminalKey.PageUp => WriteLiteral(destination, "\x1b[5~"),
                TerminalKey.PageDown => WriteLiteral(destination, "\x1b[6~"),
                TerminalKey.Insert => WriteLiteral(destination, "\x1b[2~"),
                _ => 0
            };
        }

        if ((key >= TerminalKey.F1 && key <= TerminalKey.F24) || key == TerminalKey.F25)
        {
            int fNum = key == TerminalKey.F25 ? 25 : (int)(key - TerminalKey.F1) + 1;
            if (fNum <= 4)
            {
                if (mod > 1)
                {
                    int offset = WriteLiteral(destination, "\x1b[1;");
                    offset = WriteNumber(destination, offset, mod);
                    destination[offset++] = (byte)('P' + fNum - 1);
                    return offset;
                }
                destination[0] = 0x1b; destination[1] = (byte)'O'; destination[2] = (byte)('P' + fNum - 1);
                return 3;
            }
            int code = FunctionCode(fNum);
            if (code > 0)
            {
                int offset = WriteLiteral(destination, "\x1b[");
                offset = WriteNumber(destination, offset, code);
                if (mod > 1)
                {
                    destination[offset++] = (byte)';';
                    offset = WriteNumber(destination, offset, mod);
                }
                destination[offset++] = (byte)'~';
                return offset;
            }
        }

        if (ctrl && !alt)
        {
            if (key >= TerminalKey.A && key <= TerminalKey.Z)
            {
                destination[0] = (byte)((key - TerminalKey.A) + 1);
                return 1;
            }
            byte control = key switch
            {
                TerminalKey.Space => 0x00,
                TerminalKey.LeftBracket => 0x1b,
                TerminalKey.BackSlash => 0x1c,
                TerminalKey.RightBracket => 0x1d,
                TerminalKey.GraveAccent => 0x1e,
                TerminalKey.Minus or TerminalKey.Slash => 0x1f,
                _ => 0
            };
            if (key == TerminalKey.Space || key is TerminalKey.LeftBracket or TerminalKey.BackSlash or TerminalKey.RightBracket
                or TerminalKey.GraveAccent or TerminalKey.Minus or TerminalKey.Slash)
            {
                destination[0] = control;
                return 1;
            }
            return 0;
        }

        if (alt && !ctrl)
        {
            if (key >= TerminalKey.A && key <= TerminalKey.Z)
            {
                destination[0] = 0x1b;
                destination[1] = (byte)(shift ? 'A' + (key - TerminalKey.A) : 'a' + (key - TerminalKey.A));
                return 2;
            }
            if (key >= TerminalKey.Number0 && key <= TerminalKey.Number9)
            {
                destination[0] = 0x1b;
                destination[1] = (byte)('0' + (key - TerminalKey.Number0));
                return 2;
            }
            char altChar = key switch
            {
                TerminalKey.Space => ' ',
                TerminalKey.Minus => shift ? '_' : '-',
                TerminalKey.Equal => shift ? '+' : '=',
                TerminalKey.LeftBracket => shift ? '{' : '[',
                TerminalKey.RightBracket => shift ? '}' : ']',
                TerminalKey.BackSlash => shift ? '|' : '\\',
                TerminalKey.Semicolon => shift ? ':' : ';',
                TerminalKey.Quote => shift ? '"' : '\'',
                TerminalKey.GraveAccent => shift ? '~' : '`',
                TerminalKey.Comma => shift ? '<' : ',',
                TerminalKey.Period => shift ? '>' : '.',
                TerminalKey.Slash => shift ? '?' : '/',
                _ => '\0'
            };
            if (altChar != '\0')
            {
                destination[0] = 0x1b;
                destination[1] = (byte)altChar;
                return 2;
            }
        }

        if (ctrl && alt)
        {
            if (key >= TerminalKey.A && key <= TerminalKey.Z)
            {
                destination[0] = 0x1b;
                destination[1] = (byte)((key - TerminalKey.A) + 1);
                return 2;
            }
            byte control = key switch
            {
                TerminalKey.Space => 0x00,
                TerminalKey.LeftBracket => 0x1b,
                TerminalKey.BackSlash => 0x1c,
                TerminalKey.RightBracket => 0x1d,
                _ => 0xff
            };
            if (control != 0xff)
            {
                destination[0] = 0x1b;
                destination[1] = control;
                return 2;
            }
        }
        return 0;
    }

    private static int WriteLiteral(Span<byte> destination, ReadOnlySpan<char> text) =>
        Encoding.ASCII.GetBytes(text, destination);


    private static bool IsDisambiguationKey(TerminalKey key) => key is
        TerminalKey.Escape or TerminalKey.Enter or TerminalKey.Tab or TerminalKey.Backspace or
        TerminalKey.Delete or TerminalKey.Insert or TerminalKey.Home or TerminalKey.End or
        TerminalKey.PageUp or TerminalKey.PageDown or TerminalKey.Up or TerminalKey.Down or
        TerminalKey.Left or TerminalKey.Right or TerminalKey.F1 or TerminalKey.F2 or TerminalKey.F3 or TerminalKey.F4 or
        TerminalKey.F5 or TerminalKey.F6 or TerminalKey.F7 or TerminalKey.F8 or TerminalKey.F9 or TerminalKey.F10 or
        TerminalKey.F11 or TerminalKey.F12 or TerminalKey.F13 or TerminalKey.F14 or TerminalKey.F15 or TerminalKey.F16 or
        TerminalKey.F17 or TerminalKey.F18 or TerminalKey.F19 or TerminalKey.F20 or TerminalKey.F21 or TerminalKey.F22 or
        TerminalKey.F23 or TerminalKey.F24 or TerminalKey.Keypad0 or TerminalKey.Keypad1 or TerminalKey.Keypad2 or
        TerminalKey.F25 or
        TerminalKey.Keypad3 or TerminalKey.Keypad4 or TerminalKey.Keypad5 or TerminalKey.Keypad6 or TerminalKey.Keypad7 or
        TerminalKey.Keypad8 or TerminalKey.Keypad9 or TerminalKey.KeypadDecimal or TerminalKey.KeypadDivide or
        TerminalKey.KeypadMultiply or TerminalKey.KeypadSubtract or TerminalKey.KeypadAdd or TerminalKey.KeypadEnter or
        TerminalKey.KeypadEqual or TerminalKey.ShiftLeft or TerminalKey.ShiftRight or TerminalKey.ControlLeft or
        TerminalKey.ControlRight or TerminalKey.AltLeft or TerminalKey.AltRight or TerminalKey.SuperLeft or
        TerminalKey.SuperRight or TerminalKey.CapsLock or TerminalKey.ScrollLock or TerminalKey.NumLock or
        TerminalKey.PrintScreen or TerminalKey.Pause or TerminalKey.Menu;

    private static bool TryGetKeypadCode(TerminalKey key, out int codepoint)
    {
        codepoint = key switch
        {
            TerminalKey.Keypad0 => 57399, TerminalKey.Keypad1 => 57400, TerminalKey.Keypad2 => 57401,
            TerminalKey.Keypad3 => 57402, TerminalKey.Keypad4 => 57403, TerminalKey.Keypad5 => 57404,
            TerminalKey.Keypad6 => 57405, TerminalKey.Keypad7 => 57406, TerminalKey.Keypad8 => 57407,
            TerminalKey.Keypad9 => 57408, TerminalKey.KeypadDecimal => 57409, TerminalKey.KeypadDivide => 57410,
            TerminalKey.KeypadMultiply => 57411, TerminalKey.KeypadSubtract => 57412, TerminalKey.KeypadAdd => 57413,
            TerminalKey.KeypadEnter => 57414, TerminalKey.KeypadEqual => 57415, _ => 0
        };
        return codepoint != 0;
    }

    private static bool TryGetKittyCodepoint(TerminalKey key, out int codepoint)
    {
        codepoint = key switch
        {
            >= TerminalKey.A and <= TerminalKey.Z => 'a' + (key - TerminalKey.A),
            >= TerminalKey.Number0 and <= TerminalKey.Number9 => '0' + (key - TerminalKey.Number0),
            >= TerminalKey.F1 and <= TerminalKey.F24 => 57364 + (key - TerminalKey.F1),
            TerminalKey.F25 => 57388,
            TerminalKey.Space => 32,
            TerminalKey.Minus => '-', TerminalKey.Equal => '=', TerminalKey.LeftBracket => '[',
            TerminalKey.RightBracket => ']', TerminalKey.BackSlash => '\\', TerminalKey.Semicolon => ';',
            TerminalKey.Quote => '\'', TerminalKey.GraveAccent => 96, TerminalKey.Comma => ',',
            TerminalKey.Period => '.', TerminalKey.Slash => '/',
            TerminalKey.Escape => 27, TerminalKey.Enter => 13, TerminalKey.Tab => 9, TerminalKey.Backspace => 127,
            TerminalKey.Insert => 57348, TerminalKey.Delete => 57349, TerminalKey.Left => 57350,
            TerminalKey.Down => 57351, TerminalKey.Up => 57352, TerminalKey.Right => 57353,
            TerminalKey.PageUp => 57354, TerminalKey.PageDown => 57355, TerminalKey.Home => 57356, TerminalKey.End => 57357,
            TerminalKey.CapsLock => 57358, TerminalKey.ScrollLock => 57359, TerminalKey.NumLock => 57360,
            TerminalKey.PrintScreen => 57361, TerminalKey.Pause => 57362, TerminalKey.Menu => 57363,
            TerminalKey.ShiftLeft => 57441, TerminalKey.ControlLeft => 57442, TerminalKey.AltLeft => 57443,
            TerminalKey.SuperLeft => 57444, TerminalKey.ShiftRight => 57447, TerminalKey.ControlRight => 57448,
            TerminalKey.AltRight => 57449, TerminalKey.SuperRight => 57450, _ => 0
        };
        return codepoint != 0;
    }

    private static int ReadScalar(ReadOnlySpan<char> text, ref int index)
    {
        char first = text[index++];
        if (char.IsHighSurrogate(first) && index < text.Length && char.IsLowSurrogate(text[index]))
            return char.ConvertToUtf32(first, text[index++]);
        return char.IsSurrogate(first) ? 0xfffd : first;
    }
    private static int FunctionCode(int function) => function switch
    {
        5 => 15,
        6 => 17,
        7 => 18,
        8 => 19,
        9 => 20,
        10 => 21,
        11 => 23,
        12 => 24,
        13 => 25,
        14 => 26,
        15 => 28,
        16 => 29,
        17 => 31,
        18 => 32,
        19 => 33,
        20 => 34,
        21 => 42,
        22 => 43,
        23 => 44,
        24 => 45,
        25 => 46,
        _ => 0
    };

    private static bool TryEncodeKeypad(
        TerminalKey key,
        TerminalKeyModifiers modifiers,
        bool applicationMode,
        Span<byte> destination,
        out int length)
    {
        length = 0;
        if (modifiers != TerminalKeyModifiers.None && modifiers != TerminalKeyModifiers.Shift) return false;
        string? sequence = applicationMode
            ? key switch
            {
                TerminalKey.Keypad0 => "\x1bOp",
                TerminalKey.Keypad1 => "\x1bOq",
                TerminalKey.Keypad2 => "\x1bOr",
                TerminalKey.Keypad3 => "\x1bOs",
                TerminalKey.Keypad4 => "\x1bOt",
                TerminalKey.Keypad5 => "\x1bOu",
                TerminalKey.Keypad6 => "\x1bOv",
                TerminalKey.Keypad7 => "\x1bOw",
                TerminalKey.Keypad8 => "\x1bOx",
                TerminalKey.Keypad9 => "\x1bOy",
                TerminalKey.KeypadDecimal => "\x1bOn",
                TerminalKey.KeypadDivide => "\x1bOl",
                TerminalKey.KeypadMultiply => "\x1bOR",
                TerminalKey.KeypadSubtract => "\x1bOS",
                TerminalKey.KeypadAdd => "\x1bOm",
                TerminalKey.KeypadEnter => "\x1bOM",
                TerminalKey.KeypadEqual => "=",
                _ => null
            }
            : key switch
            {
                TerminalKey.Keypad0 => "0",
                TerminalKey.Keypad1 => "1",
                TerminalKey.Keypad2 => "2",
                TerminalKey.Keypad3 => "3",
                TerminalKey.Keypad4 => "4",
                TerminalKey.Keypad5 => "5",
                TerminalKey.Keypad6 => "6",
                TerminalKey.Keypad7 => "7",
                TerminalKey.Keypad8 => "8",
                TerminalKey.Keypad9 => "9",
                TerminalKey.KeypadDecimal => ".",
                TerminalKey.KeypadDivide => "/",
                TerminalKey.KeypadMultiply => "*",
                TerminalKey.KeypadSubtract => "-",
                TerminalKey.KeypadAdd => "+",
                TerminalKey.KeypadEnter => "\r",
                TerminalKey.KeypadEqual => "=",
                _ => null
            };
        if (sequence is null) return false;
        length = Encoding.ASCII.GetBytes(sequence.AsSpan(), destination);
        return true;
    }

    private static int WriteNumber(Span<byte> destination, int offset, int value)
    {
        uint magnitude;
        if (value < 0)
        {
            destination[offset++] = (byte)'-';
            magnitude = (uint)(-(long)value);
        }
        else
        {
            magnitude = (uint)value;
        }

        int start = offset;
        do
        {
            destination[offset++] = (byte)('0' + magnitude % 10);
            magnitude /= 10;
        } while (magnitude != 0);
        for (int left = start, right = offset - 1; left < right; left++, right--)
            (destination[left], destination[right]) = (destination[right], destination[left]);
        return offset;
    }


    private static int GetModifier(TerminalKeyModifiers modifiers)
    {
        int value = 1;
        if ((modifiers & TerminalKeyModifiers.Shift) != 0) value += 1;
        if ((modifiers & TerminalKeyModifiers.Alt) != 0) value += 2;
        if ((modifiers & TerminalKeyModifiers.Control) != 0) value += 4;
        if ((modifiers & TerminalKeyModifiers.Meta) != 0) value += 8;
        if ((modifiers & TerminalKeyModifiers.CapsLock) != 0) value += 64;
        if ((modifiers & TerminalKeyModifiers.NumLock) != 0) value += 128;
        return value;
    }

}
