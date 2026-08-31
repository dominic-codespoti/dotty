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

    /// <summary>
    /// Encodes a mouse event into escape sequences based on the active mouse tracking mode and encoding.
    /// </summary>
    public byte[]? EncodeMouseEvent(
        TerminalAdapter.MouseMode mode,
        TerminalAdapter.MouseEncoding encoding,
        int button, // 0=Left, 1=Middle, 2=Right, 3=None, 64=ScrollUp, 65=ScrollDown
        int row,
        int column,
        bool isPress,
        bool isMove,
        TerminalKeyModifiers modifiers)
    {
        if (mode == TerminalAdapter.MouseMode.None) return null;

        if (isMove)
        {
            if (mode != TerminalAdapter.MouseMode.ButtonEvent && mode != TerminalAdapter.MouseMode.AnyEvent)
                return null;
            // Move without any button pressed requires AnyEvent
            if (button == 3 && mode != TerminalAdapter.MouseMode.AnyEvent)
                return null;
        }

        int cb = button;
        if (!isPress && !isMove && encoding != TerminalAdapter.MouseEncoding.SGR)
        {
            // Uncoded release is always 3 (except SGR which knows the button)
            cb = 3;
        }
        if (isMove) cb += 32;

        if (modifiers.HasFlag(TerminalKeyModifiers.Shift)) cb += 4;
        if (modifiers.HasFlag(TerminalKeyModifiers.Alt)) cb += 8;
        if (modifiers.HasFlag(TerminalKeyModifiers.Control)) cb += 16;

        int x = column + 1;
        int y = row + 1;

        if (encoding == TerminalAdapter.MouseEncoding.SGR)
        {
            char endChar = (isPress || isMove) ? 'M' : 'm';
            return Encoding.UTF8.GetBytes($"\x1b[<{cb};{x};{y}{endChar}");
        }
        else
        {
            if (x > 223 || y > 223) return null; // Standard limits
            char bChar = (char)(cb + 32);
            char xChar = (char)(x + 32);
            char yChar = (char)(y + 32);
            return Encoding.UTF8.GetBytes($"\x1b[M{bChar}{xChar}{yChar}");
        }
    }

    /// <summary>
    /// Encodes a key event into terminal escape sequences or control characters.
    /// </summary>
    public byte[]? Encode(
        TerminalKey key,
        TerminalKeyModifiers modifiers,
        bool keypadApplicationMode = false,
        bool applicationCursorKeys = false)
    {
        // Kitty keyboard protocol: unambiguous encoding for all key combinations.
        // It takes precedence over legacy/application cursor-key modes.
        if (KittyMode > 0)
            return EncodeKitty(key, modifiers, keypadApplicationMode);

        bool ctrl = modifiers.HasFlag(TerminalKeyModifiers.Control);
        bool alt = modifiers.HasFlag(TerminalKeyModifiers.Alt);
        bool shift = modifiers.HasFlag(TerminalKeyModifiers.Shift);
        int mod = GetModifier(modifiers);

        // 1. Backspace shortcuts
        if (key == TerminalKey.Backspace)
        {
            if (ctrl && alt)
            {
                // Ctrl+Alt+Backspace -> \x1b\x17 (Escape + ^W)
                return new byte[] { 0x1b, 0x17 };
            }
            if (ctrl)
            {
                // Ctrl+Backspace -> 0x17 (^W / werase: delete whole word before cursor)
                return new byte[] { 0x17 };
            }
            if (alt)
            {
                // Alt+Backspace -> \x1b\x7f (Escape + DEL: backward-kill-word in readline/zsh)
                return new byte[] { 0x1b, 0x7f };
            }
            return new byte[] { 0x7f };
        }

        // 2. Delete shortcuts
        if (key == TerminalKey.Delete)
        {
            if (ctrl && alt)
            {
                return Encoding.UTF8.GetBytes("\x1b[3;7~");
            }
            if (ctrl)
            {
                // Ctrl+Delete -> \x1b[3;5~ (kill-word: delete whole word after cursor)
                return Encoding.UTF8.GetBytes("\x1b[3;5~");
            }
            if (alt)
            {
                // Alt+Delete -> \x1b[3;3~ (kill-word)
                return Encoding.UTF8.GetBytes("\x1b[3;3~");
            }
            if (shift)
            {
                return Encoding.UTF8.GetBytes("\x1b[3;2~");
            }
            return Encoding.UTF8.GetBytes("\x1b[3~");
        }

        // 3. Tab shortcuts
        if (key == TerminalKey.Tab)
        {
            if (shift)
            {
                // Shift+Tab -> \x1b[Z (BackTab)
                return Encoding.UTF8.GetBytes("\x1b[Z");
            }
            if (alt)
            {
                return new byte[] { 0x1b, 0x09 };
            }
            return new byte[] { 0x09 };
        }

        // 4. Enter shortcuts
        if (key == TerminalKey.Enter)
        {
            if (alt)
            {
                return new byte[] { 0x1b, 0x0d };
            }
            return new byte[] { 0x0d };
        }

        // 5. Escape
        if (key == TerminalKey.Escape)
        {
            if (alt)
            {
                return new byte[] { 0x1b, 0x1b };
            }
            return new byte[] { 0x1b };
        }

        // 6. Navigation keys with any modifiers
        if (key is TerminalKey.Up or TerminalKey.Down or TerminalKey.Right or TerminalKey.Left
            or TerminalKey.Home or TerminalKey.End or TerminalKey.PageUp or TerminalKey.PageDown
            or TerminalKey.Insert)
        {
            if (mod > 1)
            {
                string? modSeq = key switch
                {
                    TerminalKey.Up => $"\x1b[1;{mod}A",
                    TerminalKey.Down => $"\x1b[1;{mod}B",
                    TerminalKey.Right => $"\x1b[1;{mod}C",
                    TerminalKey.Left => $"\x1b[1;{mod}D",
                    TerminalKey.Home => $"\x1b[1;{mod}H",
                    TerminalKey.End => $"\x1b[1;{mod}F",
                    TerminalKey.PageUp => $"\x1b[5;{mod}~",
                    TerminalKey.PageDown => $"\x1b[6;{mod}~",
                    TerminalKey.Insert => $"\x1b[2;{mod}~",
                    _ => null
                };
                if (modSeq != null) return Encoding.UTF8.GetBytes(modSeq);
            }
            else
            {
                string? bareSeq = key switch
                {
                    TerminalKey.Up => applicationCursorKeys ? "\x1bOA" : "\x1b[A",
                    TerminalKey.Down => applicationCursorKeys ? "\x1bOB" : "\x1b[B",
                    TerminalKey.Right => applicationCursorKeys ? "\x1bOC" : "\x1b[C",
                    TerminalKey.Left => applicationCursorKeys ? "\x1bOD" : "\x1b[D",
                    TerminalKey.Home => "\x1b[H",
                    TerminalKey.End => "\x1b[F",
                    TerminalKey.PageUp => "\x1b[5~",
                    TerminalKey.PageDown => "\x1b[6~",
                    TerminalKey.Insert => "\x1b[2~",
                    _ => null
                };
                if (bareSeq != null) return Encoding.UTF8.GetBytes(bareSeq);
            }
        }

        // 7. Keypad in Application Mode
        if (keypadApplicationMode && (modifiers == TerminalKeyModifiers.None || modifiers == TerminalKeyModifiers.Shift))
        {
            string? keypadSeq = key switch
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
                _ => null
            };
            if (keypadSeq != null) return Encoding.UTF8.GetBytes(keypadSeq);
        }

        // 8. Function keys F1-F24 with modifiers
        if (key >= TerminalKey.F1 && key <= TerminalKey.F24)
        {
            int fNum = (int)(key - TerminalKey.F1) + 1;
            if (fNum <= 4)
            {
                char fChar = (char)('P' + (fNum - 1));
                if (mod > 1)
                    return Encoding.UTF8.GetBytes($"\x1b[1;{mod}{fChar}");
                return Encoding.UTF8.GetBytes($"\x1bO{fChar}");
            }
            else
            {
                int code = fNum switch
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
                    _ => 0
                };
                if (code > 0)
                {
                    if (mod > 1)
                        return Encoding.UTF8.GetBytes($"\x1b[{code};{mod}~");
                    return Encoding.UTF8.GetBytes($"\x1b[{code}~");
                }
            }
        }

        // 9. Ctrl combinations (Ctrl+A - Ctrl+Z, Ctrl+Space, Ctrl+[, etc.)
        if (ctrl && !alt)
        {
            if (key >= TerminalKey.A && key <= TerminalKey.Z)
            {
                return new byte[] { (byte)((key - TerminalKey.A) + 1) };
            }

            return key switch
            {
                TerminalKey.Space => new byte[] { 0x00 },
                TerminalKey.LeftBracket => new byte[] { 0x1B },
                TerminalKey.BackSlash => new byte[] { 0x1C },
                TerminalKey.RightBracket => new byte[] { 0x1D },
                TerminalKey.GraveAccent => new byte[] { 0x1E },
                TerminalKey.Minus or TerminalKey.Slash => new byte[] { 0x1F },
                _ => null
            };
        }

        // 10. Alt combinations (Alt+A - Alt+Z, Alt+0 - Alt+9, Alt+Punctuation)
        // Sends \x1b followed by the key character (Meta prefix in readline/shells)
        if (alt && !ctrl)
        {
            if (key >= TerminalKey.A && key <= TerminalKey.Z)
            {
                char ch = shift ? (char)('A' + (key - TerminalKey.A)) : (char)('a' + (key - TerminalKey.A));
                return new byte[] { 0x1b, (byte)ch };
            }

            if (key >= TerminalKey.Number0 && key <= TerminalKey.Number9)
            {
                char ch = (char)('0' + (key - TerminalKey.Number0));
                return new byte[] { 0x1b, (byte)ch };
            }

            char? altChar = key switch
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
                _ => null
            };

            if (altChar.HasValue)
            {
                return new byte[] { 0x1b, (byte)altChar.Value };
            }
        }

        // 11. Ctrl+Alt combinations
        if (ctrl && alt)
        {
            if (key >= TerminalKey.A && key <= TerminalKey.Z)
            {
                byte ctrlByte = (byte)((key - TerminalKey.A) + 1);
                return new byte[] { 0x1b, ctrlByte };
            }

            return key switch
            {
                TerminalKey.Space => new byte[] { 0x1b, 0x00 },
                TerminalKey.LeftBracket => new byte[] { 0x1b, 0x1b },
                TerminalKey.BackSlash => new byte[] { 0x1b, 0x1c },
                TerminalKey.RightBracket => new byte[] { 0x1b, 0x1d },
                _ => null
            };
        }

        return null;
    }

    private static int GetModifier(TerminalKeyModifiers modifiers)
    {
        int m = 1; // none
        if (modifiers.HasFlag(TerminalKeyModifiers.Shift)) m += 1; // 2
        if (modifiers.HasFlag(TerminalKeyModifiers.Alt)) m += 2;   // 3 (with shift: 4)
        if (modifiers.HasFlag(TerminalKeyModifiers.Control)) m += 4; // 5-8
        if (modifiers.HasFlag(TerminalKeyModifiers.Meta)) m += 8;   // 9-16
        return m;
    }

    private byte[]? EncodeKitty(TerminalKey key, TerminalKeyModifiers modifiers, bool keypadApplicationMode)
    {
        int modifier = GetModifier(modifiers);
        var sb = new StringBuilder();

        // Determine CSI code for the key
        int code = key switch
        {
            TerminalKey.Tab => 9,
            TerminalKey.Enter => 13,
            TerminalKey.Escape => 27,
            TerminalKey.Backspace => 127,
            TerminalKey.Up => 1,
            TerminalKey.Down => 2,
            TerminalKey.Right => 3,
            TerminalKey.Left => 4,
            TerminalKey.PageUp => 5,
            TerminalKey.PageDown => 6,
            TerminalKey.Home => 7,
            TerminalKey.End => 8,
            TerminalKey.Insert => 2,
            TerminalKey.Delete => 3,
            TerminalKey.F1 => 1,
            TerminalKey.F2 => 2,
            TerminalKey.F3 => 3,
            TerminalKey.F4 => 4,
            TerminalKey.F5 => 15,
            TerminalKey.F6 => 17,
            TerminalKey.F7 => 18,
            TerminalKey.F8 => 19,
            TerminalKey.F9 => 20,
            TerminalKey.F10 => 21,
            TerminalKey.F11 => 23,
            TerminalKey.F12 => 24,
            _ => -1
        };

        if (code > 0)
        {
            // Special keys: \e[code;modifieru (or : for CSI)
            // CSI keys (code >= 15 or arrows etc.) use : separator
            bool isCsi = code >= 15 || (code >= 1 && code <= 8);
            char sep = isCsi ? ':' : 'u';
            if (modifier > 1)
                sb.Append($"\x1b[{code};{modifier}{sep}");
            else
                sb.Append($"\x1b[{code}{sep}");
        }
        else
        {
            // Not a special key - return null to let TextInput handle it
            return null;
        }

        return sb.Length > 0 ? Encoding.UTF8.GetBytes(sb.ToString()) : null;
    }
}
