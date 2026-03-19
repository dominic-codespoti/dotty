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

            try
            {
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
                                    
                                    if (final == 'M' && paramSpan.Length == 0)
                                    {
                                        // X11 mouse format: ESC [ M Cb Cx Cy
                                        if (i + 3 < inputSpan.Length)
                                        {
                                            int cbByte = inputSpan[i + 1] - 32;
                                            int cxByte = inputSpan[i + 2] - 32;
                                            int cyByte = inputSpan[i + 3] - 32;
                                            bool isPress = (cbByte & 3) != 3;
                                            Handler?.OnMouseEvent(cbByte, cxByte, cyByte, isPress);
                                            i += 4;
                                            break;
                                        }
                                        else
                                        {
                                            SaveLeftover(inputSpan.Slice(seqStart));
                                            return;
                                        }
                                    }
                                    
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
                    else if (b == 0x08) // BS
                    {
                        Handler?.OnCursorBack(1);
                        i++;
                    }
                    else if (b == 0x09) // TAB
                    {
                        Handler?.OnTab();
                        i++;
                    }
                    else if (b == 0x0A || b == 0x0B || b == 0x0C) // LF, VT, FF
                    {
                        Handler?.OnLineFeed();
                        i++;
                    }
                    else if (b == 0x0D) // CR
                    {
                        Handler?.OnCarriageReturn();
                        i++;
                    }
                    else if (b < 0x20 || b == 0x7F)
                    {
                        // ignore other control characters
                        i++;
                    }
                    else
                    {
                        // collect a run of printable bytes until next C0 character or DEL
                        int start = i;
                        bool hasNonAscii = false;
                        while (i < inputSpan.Length && inputSpan[i] >= 0x20 && inputSpan[i] != 0x7F)
                        {
                            if (inputSpan[i] >= 0x80) hasNonAscii = true;
                            i++;
                        }
                        var run = inputSpan.Slice(start, i - start);
                        // Decode UTF-8 run, translate DEC graphics if active, and send to handler
                        if (run.Length > 0)
                        {
                            if (!hasNonAscii && _charset != Charset.DecSpecialGraphics) 
                            {
                                // Direct fast path for ascii text runs
                                char[] ascArray = System.Buffers.ArrayPool<char>.Shared.Rent(run.Length);
                                Span<char> asc = ascArray.AsSpan(0, run.Length);
                                for (int j = 0; j < run.Length; j++) { asc[j] = (char)run[j]; }
                                Handler?.OnPrint(asc);
                                System.Buffers.ArrayPool<char>.Shared.Return(ascArray);
                            }
                            else
                            {
                                var decoded = DecodePrintableRun(run);
                                if (decoded.Length > 0)
                                {
                                    Handler?.OnPrint(decoded.AsSpan());
                                }
                            }
                        }
                    }
                }

                // finished without leftover -> clear leftover
                _leftoverLen = 0;
            }
            finally
            {
                // Defer flush to caller to allow batching
                // Handler?.FlushRender();
            }
        }

        private void HandleCsi(char final, ReadOnlySpan<byte> paramBytes)
        {
            // SGR needs the full string for SgrParser
            if (final == 'm' && (paramBytes.IsEmpty || paramBytes[0] != '<'))
            {
                string @params = Encoding.UTF8.GetString(paramBytes);
                Handler?.OnSetGraphicsRendition(@params.AsSpan());
                return;
            }

            // Fast path: parse numeric params directly from bytes without string allocation
            Span<int> parsedParams = stackalloc int[8];
            if (TryParseParams(paramBytes, parsedParams, out int paramCount, out bool isPrivate))
            {
                switch (final)
                {
                    case 'J':
                    {
                        int mode = paramCount > 0 ? parsedParams[0] : 0;
                        if (mode == 3)
                            Handler?.OnClearScrollback();
                        else if (mode == 2)
                            Handler?.OnEraseDisplay(2);
                        break;
                    }
                    case 'K':
                        Handler?.OnEraseLine(paramCount > 0 ? parsedParams[0] : 0);
                        break;
                    case 'H':
                    case 'f':
                        Handler?.OnMoveCursor(
                            paramCount > 0 ? parsedParams[0] : 1,
                            paramCount > 1 ? parsedParams[1] : 1);
                        break;
                    case 'A':
                        Handler?.OnCursorUp(paramCount > 0 ? parsedParams[0] : 1);
                        break;
                    case 'B':
                        Handler?.OnCursorDown(paramCount > 0 ? parsedParams[0] : 1);
                        break;
                    case 'C':
                        Handler?.OnCursorForward(paramCount > 0 ? parsedParams[0] : 1);
                        break;
                    case 'D':
                        Handler?.OnCursorBack(paramCount > 0 ? parsedParams[0] : 1);
                        break;
                    case 'r':
                        Handler?.OnSetScrollRegion(
                            paramCount > 0 ? parsedParams[0] : 1,
                            paramCount > 1 ? parsedParams[1] : 0);
                        break;
                    case 'h':
                    case 'l':
                        if (isPrivate && paramCount > 0)
                        {
                            bool enable = final == 'h';
                            for (int pIdx = 0; pIdx < paramCount; pIdx++)
                            {
                                int code = parsedParams[pIdx];
                                if (code == 1049) Handler?.OnSetAlternateScreen(enable);
                                else if (code == 25) Handler?.OnSetCursorVisibility(enable);
                                else if (code == 6) Handler?.OnSetOriginMode(enable);
                                else if (code == 1) Handler?.OnSetApplicationCursorKeys(enable);
                                else if (code == 2004) Handler?.OnSetBracketedPasteMode(enable);
                                else if (code == 1000 || code == 1002 || code == 1003 || code == 1005 || code == 1006 || code == 1015) 
                                    Handler?.OnSetMouseMode(code, enable);
                            }
                        }
                        break;
                    case 'M':
                    case 'm':
                        if (paramCount >= 3)
                        {
                            int cb = parsedParams[0];
                            int cx = parsedParams[1];
                            int cy = parsedParams[2];
                            bool isPress = (cb & 0x03) != 0x03;
                            Handler?.OnMouseEvent(cb, cx, cy, isPress);
                        }
                        break;
                    default:
                        break;
                }
            }
            else
            {
                // Fallback to string-based parsing for unusual param formats
                HandleCsiFallback(final, paramBytes);
            }
        }

        private void HandleCsiFallback(char final, ReadOnlySpan<byte> paramBytes)
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
                        Handler?.OnClearScrollback();
                    else if (mode == 2)
                        Handler?.OnEraseDisplay(2);
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
                case 'r':
                    Handler?.OnSetScrollRegion(GetParam(0, 1), GetParam(1, 0));
                    break;
                case 'h':
                case 'l':
                    try
                    {
                        var p = @params;
                        bool isPrivate = false;
                        if (p.StartsWith("?") || p.StartsWith(">"))
                        {
                            isPrivate = true;
                            p = p.Substring(1);
                        }
                        
                        if (isPrivate)
                        {
                            bool enable = final == 'h';
                            string[] modeParts = p.Split(';', StringSplitOptions.RemoveEmptyEntries);
                            foreach (var mp in modeParts)
                            {
                                if (int.TryParse(mp, out var code))
                                {
                                    if (code == 1049) Handler?.OnSetAlternateScreen(enable);
                                    else if (code == 25) Handler?.OnSetCursorVisibility(enable);
                                    else if (code == 6) Handler?.OnSetOriginMode(enable);
                                    else if (code == 1) Handler?.OnSetApplicationCursorKeys(enable);
                                    else if (code == 2004) Handler?.OnSetBracketedPasteMode(enable);
                                    else if (code == 1000 || code == 1002 || code == 1003 || code == 1005 || code == 1006 || code == 1015) 
                                        Handler?.OnSetMouseMode(code, enable);
                                }
                            }
                        }
                    }
                    catch { }
                    break;
                case 'M':
                case 'm':
                    try
                    {
                        var p = @params;
                        if (p.StartsWith("<"))
                        {
                            p = p.Substring(1);
                            string[] mParts = p.Split(';', StringSplitOptions.RemoveEmptyEntries);
                            if (mParts.Length >= 3 && int.TryParse(mParts[0], out int cb) && int.TryParse(mParts[1], out int cx) && int.TryParse(mParts[2], out int cy))
                            {
                                bool isPress = final == 'M';
                                Handler?.OnMouseEvent(cb, cx, cy, isPress);
                            }
                        }
                    }
                    catch { }
                    break;
                default:
                    break;
            }
        }

        private static bool TryParseParams(ReadOnlySpan<byte> paramBytes, Span<int> outParams, out int count, out bool isPrivate)
        {
            count = 0;
            isPrivate = false;

            if (paramBytes.IsEmpty)
                return true;

            int start = 0;
            if (paramBytes[0] == '?')
            {
                isPrivate = true;
                start = 1;
            }
            else if (paramBytes[0] == '>')
            {
                isPrivate = true;
                start = 1;
            }
            else if (paramBytes[0] == '<')
            {
                return false;
            }

            int current = 0;
            bool hasDigit = false;

            for (int i = start; i < paramBytes.Length; i++)
            {
                byte b = paramBytes[i];
                if (b >= '0' && b <= '9')
                {
                    current = current * 10 + (b - '0');
                    hasDigit = true;
                }
                else if (b == ';')
                {
                    if (count >= outParams.Length) return false;
                    outParams[count++] = hasDigit ? current : 0;
                    current = 0;
                    hasDigit = false;
                }
                else if (b == ' ')
                {
                    continue;
                }
                else
                {
                    return false;
                }
            }

            if (hasDigit || start < paramBytes.Length)
            {
                if (count >= outParams.Length) return false;
                outParams[count++] = current;
            }

            return true;
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
