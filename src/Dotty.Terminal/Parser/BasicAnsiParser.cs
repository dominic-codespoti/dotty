using System.Collections.Generic;
using System.Text;
using Dotty.Abstractions.Adapter;
using Dotty.Abstractions.Parser;

namespace Dotty.Terminal.Parser
{
    /// <summary>
    /// Minimal stateful ANSI/VT parser. Handles printable text and a very small set of control sequences:
    /// - CSI 2 J (erase display)
    /// - CSI 3 J (erase saved lines / scrollback)
    /// - CSI H   (cursor home)
    /// - CSI ... m (SGR) - forwarded to handler as raw params
    /// - BEL (0x07) -> OnBell
    /// </summary>
    public sealed class BasicAnsiParser : ITerminalParser
    {
        private const byte ESC = 0x1b;
        private readonly byte[] _leftover = new byte[32];
        private int _leftoverLen = 0;
        private Charset _charset = Charset.Ascii;

        public ITerminalHandler? Handler { get; set; }

        private enum Charset
        {
            Ascii,
            DecSpecialGraphics,
        }

        private static readonly Dictionary<char, char> s_decSpecialGraphicsMap = new()
        {
            ['j'] = '┘',
            ['k'] = '┐',
            ['l'] = '┌',
            ['m'] = '└',
            ['t'] = '├',
            ['u'] = '┤',
            ['v'] = '┴',
            ['w'] = '┬',
            ['n'] = '┼',
            ['q'] = '─',
            ['x'] = '│',
            ['o'] = '⎺',
            ['s'] = '⎽',
            ['p'] = '⎻',
            ['r'] = '⎼',
            ['`'] = '◆',
            ['a'] = '▒',
            ['f'] = '°',
            ['g'] = '±',
            ['~'] = '•',
            ['h'] = '▦',
            ['i'] = '✦',
            ['0'] = '█',
            [','] = '←',
            ['+'] = '→',
            ['.'] = '↓',
            ['-'] = '↑',
            ['y'] = '≤',
            ['z'] = '≥',
            ['{'] = 'π',
            ['|'] = '≠',
            ['}'] = '£',
        };

        public void Feed(ReadOnlySpan<byte> bytes)
        {
            // Prepend leftover
            Span<byte> working = bytes.Length + _leftoverLen <= 0 ? Span<byte>.Empty : (stackalloc byte[0]);

            // If we have leftover, allocate a small buffer to concatenate
            byte[]? concat = null;
            ReadOnlySpan<byte> inputSpan;
            if (_leftoverLen > 0)
            {
                concat = new byte[_leftoverLen + bytes.Length];
                Buffer.BlockCopy(_leftover, 0, concat, 0, _leftoverLen);
                bytes.CopyTo(concat.AsSpan(_leftoverLen));
                inputSpan = concat;
            }
            else
            {
                inputSpan = bytes;
            }

            int i = 0;
            while (i < inputSpan.Length)
            {
                byte b = inputSpan[i];
                if (b == ESC)
                {
                    // Try to parse an escape sequence starting at i
                    int seqStart = i;
                    i++;
                    if (i >= inputSpan.Length)
                    {
                        SaveLeftover(inputSpan.Slice(seqStart));
                        return;
                    }

                    byte next = inputSpan[i];
                    if (next == (byte)'[') // CSI
                    {
                        i++; // move into params
                        int paramsStart = i;
                        // scan until a letter between @ and ~ (final byte)
                        while (i < inputSpan.Length)
                        {
                            byte cb = inputSpan[i];
                            if (cb >= 0x40 && cb <= 0x7e)
                            {
                                // final
                                var final = (char)cb;
                                var paramSpan = inputSpan.Slice(paramsStart, i - paramsStart);
                                HandleCsi(final, paramSpan);
                                i++;
                                break;
                            }
                            i++;
                        }

                        if (i > inputSpan.Length)
                        {
                            // incomplete
                            SaveLeftover(inputSpan.Slice(seqStart));
                            return;
                        }
                    }
                    else
                    {
                        // Not CSI - handle OSC (] ... BEL), charset selects, and a few single byte sequences like ESC c
                        if (next == (byte)']') // OSC - Operating System Command
                        {
                            i++; // move into payload
                            int payloadStart = i;
                            bool finished = false;
                            while (i < inputSpan.Length)
                            {
                                byte cb = inputSpan[i];
                                if (cb == 0x07) // BEL terminator
                                {
                                    var payload = Encoding.UTF8.GetString(inputSpan.Slice(payloadStart, i - payloadStart));
                                    Handler?.OnOperatingSystemCommand(payload.AsSpan());
                                    i++;
                                    finished = true;
                                    break;
                                }
                                if (cb == ESC && i + 1 < inputSpan.Length && inputSpan[i + 1] == (byte)'\\')
                                {
                                    var payload = Encoding.UTF8.GetString(inputSpan.Slice(payloadStart, i - payloadStart));
                                    Handler?.OnOperatingSystemCommand(payload.AsSpan());
                                    i += 2;
                                    finished = true;
                                    break;
                                }
                                i++;
                            }

                            if (!finished)
                            {
                                SaveLeftover(inputSpan.Slice(seqStart));
                                return;
                            }
                        }
                        else if (next == (byte)'c')
                        {
                            Handler?.OnEraseDisplay(2);
                            i++;
                        }
                        else if (next == (byte)'(' || next == (byte)')')
                        {
                            i++;
                            if (i >= inputSpan.Length)
                            {
                                SaveLeftover(inputSpan.Slice(seqStart));
                                return;
                            }

                            var selection = (char)inputSpan[i];
                            ApplyCharsetSelection(selection);
                            i++;
                        }
                        else
                        {
                            i++;
                        }
                    }
                }
                else if (b == 0x07) // BEL
                {
                    Handler?.OnBell();
                    i++;
                }
                else
                {
                    // collect a run of printable/non-control bytes until next ESC or BEL
                    int start = i;
                    while (i < inputSpan.Length && inputSpan[i] != ESC && inputSpan[i] != 0x07)
                        i++;
                    var run = inputSpan.Slice(start, i - start);
                    // Decode UTF-8 run, translate DEC graphics if active, and send to handler
                    var decoded = DecodePrintableRun(run);
                    Handler?.OnPrint(decoded.AsSpan());
                }
            }

            // finished without leftover -> clear leftover
            _leftoverLen = 0;
        }

        private void HandleCsi(char final, ReadOnlySpan<byte> paramBytes)
        {
            string @params = Encoding.UTF8.GetString(paramBytes);
            string[] parts = @params.Split(';', StringSplitOptions.RemoveEmptyEntries);
            int GetParam(int idx, int def)
            {
                if (idx < parts.Length && int.TryParse(parts[idx], out var v)) return v;
                return def;
            }

            switch (final)
            {
                case 'J':
                    int mode = GetParam(0, 0);
                    if (mode == 3)
                    {
                        Handler?.OnClearScrollback();
                    }
                    else if (mode == 2)
                    {
                        Handler?.OnEraseDisplay(2);
                    }
                    break;
                case 'K':
                    Handler?.OnEraseLine(GetParam(0, 0));
                    break;
                case 'H':
                case 'f':
                    Handler?.OnMoveCursor(GetParam(0, 1), GetParam(1, 1));
                    break;
                case 'A':
                    Handler?.OnCursorUp(GetParam(0, 1));
                    break;
                case 'B':
                    Handler?.OnCursorDown(GetParam(0, 1));
                    break;
                case 'C':
                    Handler?.OnCursorForward(GetParam(0, 1));
                    break;
                case 'D':
                    Handler?.OnCursorBack(GetParam(0, 1));
                    break;
                case 'm':
                    Handler?.OnSetGraphicsRendition(@params.AsSpan());
                    break;
                case 'r':
                    Handler?.OnSetScrollRegion(GetParam(0, 1), GetParam(1, 0));
                    break;
                case 'h':
                case 'l':
                    try
                    {
                        var p = @params;
                        bool isPrivate = false;
                        if (p.StartsWith("?"))
                        {
                            isPrivate = true;
                            p = p.Substring(1);
                        }
                        if (isPrivate && int.TryParse(p, out var code))
                        {
                            bool enable = final == 'h';
                            if (code == 1049)
                            {
                                Handler?.OnSetAlternateScreen(enable);
                            }
                            else if (code == 25)
                            {
                                Handler?.OnSetCursorVisibility(enable);
                            }
                            else if (code == 6)
                            {
                                Handler?.OnSetOriginMode(enable);
                            }
                        }
                    }
                    catch { }
                    break;
                default:
                    break;
            }
        }

        private void SaveLeftover(ReadOnlySpan<byte> bytes)
        {
            int len = Math.Min(bytes.Length, _leftover.Length);
            bytes.Slice(0, len).CopyTo(_leftover.AsSpan());
            _leftoverLen = len;
        }

        private void ApplyCharsetSelection(char selector)
        {
            switch (selector)
            {
                case '0':
                    _charset = Charset.DecSpecialGraphics;
                    return;
                case 'B':
                    _charset = Charset.Ascii;
                    return;
                default:
                    _charset = Charset.Ascii;
                    return;
            }
        }

        private string DecodePrintableRun(ReadOnlySpan<byte> run)
        {
            if (run.IsEmpty)
            {
                return string.Empty;
            }

            var text = Encoding.UTF8.GetString(run);
            if (_charset != Charset.DecSpecialGraphics)
            {
                return text;
            }

            StringBuilder? builder = null;
            for (int idx = 0; idx < text.Length; idx++)
            {
                var ch = text[idx];
                if (s_decSpecialGraphicsMap.TryGetValue(ch, out var mapped))
                {
                    if (builder == null)
                    {
                        builder = new StringBuilder(text.Length);
                        builder.Append(text, 0, idx);
                    }

                    builder.Append(mapped);
                }
                else if (builder != null)
                {
                    builder.Append(ch);
                }
            }

            return builder?.ToString() ?? text;
        }
    }
}
