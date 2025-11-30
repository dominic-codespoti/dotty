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

        public ITerminalHandler? Handler { get; set; }

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
                        // Not CSI - handle OSC (] ... BEL) and a few single byte sequences like ESC c
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
                                    // payload is from payloadStart to i-1
                                    var payload = Encoding.UTF8.GetString(inputSpan.Slice(payloadStart, i - payloadStart));
                                    Handler?.OnOperatingSystemCommand(payload.AsSpan());
                                    i++;
                                    finished = true;
                                    break;
                                }
                                // also support ST sequence: ESC '\\' (0x1b 0x5c) as terminator
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
                                // incomplete OSC sequence, save leftover
                                SaveLeftover(inputSpan.Slice(seqStart));
                                return;
                            }
                        }
                        else if (next == (byte)'c')
                        {
                            // RIS (Reset) - treat as full clear
                            Handler?.OnEraseDisplay(2);
                            i++;
                        }
                        else
                        {
                            // Unknown escape, skip it
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
                    // Decode UTF-8 run and send to handler
                    string s = Encoding.UTF8.GetString(run);
                    Handler?.OnPrint(s.AsSpan());
                }
            }

            // finished without leftover -> clear leftover
            _leftoverLen = 0;
        }

        private void HandleCsi(char final, ReadOnlySpan<byte> paramBytes)
        {
            // decode param bytes to chars
            string @params = Encoding.UTF8.GetString(paramBytes);
            // Split numeric parameters (e.g., "12;34")
            string[] parts = @params.Split(';', StringSplitOptions.RemoveEmptyEntries);
            int GetParam(int idx, int def)
            {
                if (idx < parts.Length && int.TryParse(parts[idx], out var v)) return v;
                return def;
            }

                switch (final)
            {
                case 'J':
                    // erase display - common params: 2 (entire screen)
                    try
                    {
                        var dbg = Environment.GetEnvironmentVariable("DOTTY_DEBUG_PARSER");
                        if (!string.IsNullOrEmpty(dbg) && dbg != "0")
                        {
                            Console.Error.WriteLine($"[PARSER] CSI J params='{@params}'");
                        }
                    }
                    catch { }
                        // Interpret parameter per ANSI: default is 0 (erase from cursor to end of screen)
                        int mode = 0;
                        if (!string.IsNullOrEmpty(@params) && int.TryParse(@params, out var m)) mode = m;
                        if (mode == 3)
                        {
                            Handler?.OnClearScrollback();
                        }
                        else if (mode == 2)
                        {
                            // full clear
                            Handler?.OnEraseDisplay(2);
                        }
                        else
                        {
                            // mode 0 (erase to end) and mode 1 are currently ignored to avoid
                            // surprising clears triggered by prompt updates; implement more
                            // precise erase semantics later.
                        }
                        break;
                case 'K':
                    // Erase in line: 0=to end,1=to start,2=entire line
                    int modeK = 0;
                    if (!string.IsNullOrEmpty(@params) && int.TryParse(@params, out var mk)) modeK = mk;
                    Handler?.OnEraseLine(modeK);
                    break;
                case 'H':
                case 'f':
                    // Cursor position: [row;col]
                    int row = GetParam(0, 1);
                    int col = GetParam(1, 1);
                    Handler?.OnMoveCursor(row, col);
                    break;
                case 'A':
                    // CUU - cursor up by n
                    Handler?.OnCursorUp(GetParam(0, 1));
                    break;
                case 'B':
                    // CUD - cursor down by n
                    Handler?.OnCursorDown(GetParam(0, 1));
                    break;
                case 'C':
                    // CUF - cursor forward
                    Handler?.OnCursorForward(GetParam(0, 1));
                    break;
                case 'D':
                    // CUB - cursor back
                    Handler?.OnCursorBack(GetParam(0, 1));
                    break;
                case 'm':
                    Handler?.OnSetGraphicsRendition(@params.AsSpan());
                    break;
                case 'h':
                case 'l':
                    // Mode set/reset. Support DEC-private ?1049 (alternate screen) and ?25 (cursor visibility) minimally.
                    // paramBytes may contain a leading '?' when it's a DEC private mode. Handle both forms.
                    try
                    {
                        var p = @params;
                        bool isPrivate = false;
                        if (p.StartsWith("?")) { isPrivate = true; p = p.Substring(1); }
                        if (isPrivate && int.TryParse(p, out var code))
                        {
                            if (code == 1049)
                            {
                                bool enable = final == 'h';
                                Handler?.OnSetAlternateScreen(enable);
                            }
                            else if (code == 25)
                            {
                                // DEC Private Mode 25: cursor visibility
                                bool enable = final == 'h';
                                Handler?.OnSetCursorVisibility(enable);
                            }
                        }
                    }
                    catch { }
                    break;
                default:
                    // ignore other sequences for now
                    break;
            }
        }

        private void SaveLeftover(ReadOnlySpan<byte> bytes)
        {
            int len = Math.Min(bytes.Length, _leftover.Length);
            bytes.Slice(0, len).CopyTo(_leftover.AsSpan());
            _leftoverLen = len;
        }
    }
}
