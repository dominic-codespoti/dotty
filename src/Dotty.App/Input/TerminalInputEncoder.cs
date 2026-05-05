using System;
using System.Collections.Generic;
using System.Text;
using Avalonia.Input;
using Dotty.Terminal.Adapter;

namespace Dotty.App.Input
{
    public class TerminalInputEncoder
    {
        /// <summary>
        /// Kitty keyboard protocol mode: 0=disabled, 1=full, 2=partial.
        /// </summary>
        public int KittyMode { get; set; }

        public byte[]? EncodeMouseEvent(
            TerminalAdapter.MouseMode mode, 
            TerminalAdapter.MouseEncoding encoding, 
            int button, // 0=Left, 1=Middle, 2=Right, 3=None, 64=ScrollUp, 65=ScrollDown
            int row, 
            int column, 
            bool isPress, 
            bool isMove, 
            KeyModifiers modifiers)
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

            if (modifiers.HasFlag(KeyModifiers.Shift)) cb += 4;
            if (modifiers.HasFlag(KeyModifiers.Alt)) cb += 8;
            if (modifiers.HasFlag(KeyModifiers.Control)) cb += 16;
            
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

        public byte[]? Encode(Key key, KeyModifiers modifiers, bool keypadApplicationMode = false)
        {
            // Kitty keyboard protocol: unambiguous encoding for all key combinations
            if (KittyMode > 0)
                return EncodeKitty(key, modifiers, keypadApplicationMode);

            // Legacy encoding below
            if (modifiers.HasFlag(KeyModifiers.Control) && !modifiers.HasFlag(KeyModifiers.Shift) && !modifiers.HasFlag(KeyModifiers.Alt))
            {
                if (key >= Key.A && key <= Key.Z)
                {
                    return new byte[] { (byte)((key - Key.A) + 1) };
                }
                
                return key switch
                {
                    Key.Space => new byte[] { 0x00 },
                    Key.OemOpenBrackets => new byte[] { 0x1B }, // ESC
                    Key.OemBackslash => new byte[] { 0x1C },
                    Key.OemCloseBrackets => new byte[] { 0x1D },
                    Key.OemTilde => new byte[] { 0x1E },
                    Key.OemMinus => new byte[] { 0x1F },
                    Key.PageUp => Encoding.UTF8.GetBytes("\x1b[5;5~"),
                    Key.PageDown => Encoding.UTF8.GetBytes("\x1b[6;5~"),
                    Key.Up => Encoding.UTF8.GetBytes("\x1b[1;5A"),
                    Key.Down => Encoding.UTF8.GetBytes("\x1b[1;5B"),
                    Key.Right => Encoding.UTF8.GetBytes("\x1b[1;5C"),
                    Key.Left => Encoding.UTF8.GetBytes("\x1b[1;5D"),
                    _ => null
                };
            }

            // Arrow keys and navigation
            if (modifiers == KeyModifiers.None || modifiers == KeyModifiers.Shift)
            {
                var modStr = modifiers.HasFlag(KeyModifiers.Shift) ? "2" : "";

                if (keypadApplicationMode)
                {
                    string? keypadSeq = key switch
                    {
                        Key.NumPad0 => "\x1bOp",
                        Key.NumPad1 => "\x1bOq",
                        Key.NumPad2 => "\x1bOr",
                        Key.NumPad3 => "\x1bOs",
                        Key.NumPad4 => "\x1bOt",
                        Key.NumPad5 => "\x1bOu",
                        Key.NumPad6 => "\x1bOv",
                        Key.NumPad7 => "\x1bOw",
                        Key.NumPad8 => "\x1bOx",
                        Key.NumPad9 => "\x1bOy",
                        Key.Decimal => "\x1bOn",
                        Key.Divide => "\x1bOl",
                        Key.Multiply => "\x1bOR",
                        Key.Subtract => "\x1bOS",
                        Key.Add => "\x1bOm",
                        _ => null
                    };

                    if (keypadSeq != null) return Encoding.UTF8.GetBytes(keypadSeq);
                }
                
                string? seq = key switch
                {
                    Key.Up => modifiers == KeyModifiers.None ? "\x1b[A" : "\x1b[1;2A",
                    Key.Down => modifiers == KeyModifiers.None ? "\x1b[B" : "\x1b[1;2B",
                    Key.Right => modifiers == KeyModifiers.None ? "\x1b[C" : "\x1b[1;2C",
                    Key.Left => modifiers == KeyModifiers.None ? "\x1b[D" : "\x1b[1;2D",
                    Key.Home => modifiers == KeyModifiers.None ? "\x1b[H" : "\x1b[1;2H",
                    Key.End => modifiers == KeyModifiers.None ? "\x1b[F" : "\x1b[1;2F",
                    Key.PageUp => modifiers == KeyModifiers.None ? "\x1b[5~" : "\x1b[5;2~",
                    Key.PageDown => modifiers == KeyModifiers.None ? "\x1b[6~" : "\x1b[6;2~",
                    Key.Insert => modifiers == KeyModifiers.None ? "\x1b[2~" : "\x1b[2;2~",
                    Key.Delete => modifiers == KeyModifiers.None ? "\x1b[3~" : "\x1b[3;2~",
                    Key.F1 => modifiers == KeyModifiers.None ? "\x1bOP" : "\x1b[1;2P",
                    Key.F2 => modifiers == KeyModifiers.None ? "\x1bOQ" : "\x1b[1;2Q",
                    Key.F3 => modifiers == KeyModifiers.None ? "\x1bOR" : "\x1b[1;2R",
                    Key.F4 => modifiers == KeyModifiers.None ? "\x1bOS" : "\x1b[1;2S",
                    Key.F5 => "\x1b[15~",
                    Key.F6 => "\x1b[17~",
                    Key.F7 => "\x1b[18~",
                    Key.F8 => "\x1b[19~",
                    Key.F9 => "\x1b[20~",
                    Key.F10 => "\x1b[21~",
                    Key.F11 => "\x1b[23~",
                    Key.F12 => "\x1b[24~",
                    _ => null
                };
                if (seq != null) return Encoding.UTF8.GetBytes(seq);

                // Other keys
                return key switch
                {
                    Key.Escape => new byte[] { 0x1b },
                    Key.Enter => new byte[] { 0x0d },
                    Key.Tab => new byte[] { 0x09 },
                    Key.Back => new byte[] { 0x7f }, // Delete maps to ^?
                    _ => null
                };
            }

            return null; // Let text input handle it if possible
        }

        private static int GetModifier(KeyModifiers modifiers)
        {
            int m = 1; // none
            if (modifiers.HasFlag(KeyModifiers.Shift)) m += 1; // 2
            if (modifiers.HasFlag(KeyModifiers.Alt)) m += 2;   // 3 (with shift: 4)
            if (modifiers.HasFlag(KeyModifiers.Control)) m += 4; // 5-8
            if (modifiers.HasFlag(KeyModifiers.Meta)) m += 8;   // 9-16
            return m;
        }

        private byte[]? EncodeKitty(Key key, KeyModifiers modifiers, bool keypadApplicationMode)
        {
            int modifier = GetModifier(modifiers);
            var sb = new StringBuilder();

            // Determine CSI code for the key
            int code = key switch
            {
                Key.Tab => 9,
                Key.Enter => 13,
                Key.Escape => 27,
                Key.Back => 127,
                Key.Up => 1, Key.Down => 2, Key.Right => 3, Key.Left => 4,
                Key.PageUp => 5, Key.PageDown => 6, Key.Home => 7, Key.End => 8,
                Key.Insert => 2, Key.Delete => 3,
                Key.F1 => 1, Key.F2 => 2, Key.F3 => 3, Key.F4 => 4,
                Key.F5 => 15, Key.F6 => 17, Key.F7 => 18, Key.F8 => 19,
                Key.F9 => 20, Key.F10 => 21, Key.F11 => 23, Key.F12 => 24,
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
}