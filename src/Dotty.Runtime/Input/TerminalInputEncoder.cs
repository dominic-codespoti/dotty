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
    /// <summary>
    /// Kitty keyboard protocol mode: 0=disabled, 1=full, 2=partial.
    /// </summary>
    public int KittyMode { get; set; }

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
        if (KittyMode > 0)
            return EncodeKitty(key, modifiers, destination);

        bool ctrl = (modifiers & TerminalKeyModifiers.Control) != 0;
        bool alt = (modifiers & TerminalKeyModifiers.Alt) != 0;
        bool shift = (modifiers & TerminalKeyModifiers.Shift) != 0;
        int mod = GetModifier(modifiers);

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

        if (key >= TerminalKey.F1 && key <= TerminalKey.F24)
        {
            int fNum = (int)(key - TerminalKey.F1) + 1;
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

    private int EncodeKitty(TerminalKey key, TerminalKeyModifiers modifiers, Span<byte> destination)
    {
        int modifier = GetModifier(modifiers);
        byte final = key switch
        {
            TerminalKey.Up => (byte)'A',
            TerminalKey.Down => (byte)'B',
            TerminalKey.Right => (byte)'C',
            TerminalKey.Left => (byte)'D',
            TerminalKey.Home => (byte)'H',
            TerminalKey.End => (byte)'F',
            TerminalKey.F1 => (byte)'P',
            TerminalKey.F2 => (byte)'Q',
            TerminalKey.F3 => (byte)'R',
            TerminalKey.F4 => (byte)'S',
            _ => 0
        };
        if (modifier == 1)
        {
            string? bare = key switch
            {
                TerminalKey.Up => "\x1b[A",
                TerminalKey.Down => "\x1b[B",
                TerminalKey.Right => "\x1b[C",
                TerminalKey.Left => "\x1b[D",
                TerminalKey.Home => "\x1b[H",
                TerminalKey.End => "\x1b[F",
                _ => null
            };
            if (bare is not null) return WriteLiteral(destination, bare);
        }
        if (final != 0)
        {
            if (modifier == 1 && key is >= TerminalKey.F1 and <= TerminalKey.F4)
            {
                destination[0] = 0x1b; destination[1] = (byte)'O'; destination[2] = final;
                return 3;
            }
            int offset = WriteLiteral(destination, "\x1b[");
            destination[offset++] = (byte)'1';
            if (modifier > 1)
            {
                destination[offset++] = (byte)';';
                offset = WriteNumber(destination, offset, modifier);
            }
            destination[offset++] = final;
            return offset;
        }

        int tildeCode = key switch
        {
            TerminalKey.PageUp => 5,
            TerminalKey.PageDown => 6,
            TerminalKey.Insert => 2,
            TerminalKey.Delete => 3,
            TerminalKey.F5 => 15,
            TerminalKey.F6 => 17,
            TerminalKey.F7 => 18,
            TerminalKey.F8 => 19,
            TerminalKey.F9 => 20,
            TerminalKey.F10 => 21,
            TerminalKey.F11 => 23,
            TerminalKey.F12 => 24,
            _ => 0
        };
        if (tildeCode != 0)
        {
            int offset = WriteLiteral(destination, "\x1b[");
            offset = WriteNumber(destination, offset, tildeCode);
            if (modifier > 1)
            {
                destination[offset++] = (byte)';';
                offset = WriteNumber(destination, offset, modifier);
            }
            destination[offset++] = (byte)'~';
            return offset;
        }

        int privateCode = key switch
        {
            TerminalKey.F13 => 57376,
            TerminalKey.F14 => 57377,
            TerminalKey.F15 => 57378,
            TerminalKey.F16 => 57379,
            TerminalKey.F17 => 57380,
            TerminalKey.F18 => 57381,
            TerminalKey.F19 => 57382,
            TerminalKey.F20 => 57383,
            TerminalKey.F21 => 57384,
            TerminalKey.F22 => 57385,
            TerminalKey.F23 => 57386,
            TerminalKey.F24 => 57387,
            TerminalKey.Tab => 9,
            TerminalKey.Enter => 13,
            TerminalKey.Escape => 27,
            TerminalKey.Backspace => 127,
            _ => 0
        };
        if (privateCode == 0) return 0;
        int result = WriteLiteral(destination, "\x1b[");
        result = WriteNumber(destination, result, privateCode);
        if (modifier > 1)
        {
            destination[result++] = (byte)';';
            result = WriteNumber(destination, result, modifier);
        }
        destination[result++] = (byte)'u';
        return result;
    }

    private static int GetModifier(TerminalKeyModifiers modifiers)
    {
        int value = 1;
        if ((modifiers & TerminalKeyModifiers.Shift) != 0) value += 1;
        if ((modifiers & TerminalKeyModifiers.Alt) != 0) value += 2;
        if ((modifiers & TerminalKeyModifiers.Control) != 0) value += 4;
        if ((modifiers & TerminalKeyModifiers.Meta) != 0) value += 8;
        return value;
    }

}
