using System.Text;

namespace Dotty.Terminal
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
                        // Not CSI - handle few single byte sequences like ESC c
                        if (next == (byte)'c')
                        {
                            Handler?.OnClearScreen();
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
            switch (final)
            {
                case 'J':
                    // erase display - common params: 2
                    if (@params == "2" || string.IsNullOrEmpty(@params))
                    {
                        Handler?.OnClearScreen();
                    }
                    else if (@params == "3")
                    {
                        Handler?.OnClearScrollback();
                    }
                    break;
                case 'H':
                    // cursor home - we'll treat like clear in minimal model
                    Handler?.OnClearScreen();
                    break;
                case 'm':
                    Handler?.OnSetGraphicsRendition(@params.AsSpan());
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
