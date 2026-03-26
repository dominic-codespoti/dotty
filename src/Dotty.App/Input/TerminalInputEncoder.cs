using System;
using System.Collections.Generic;
using System.Text;
using Avalonia.Input;
using Dotty.Terminal.Adapter;

namespace Dotty.App.Input
{
    public class TerminalInputEncoder
    {
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
            // Handle Control + Char
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
    }
}