using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using Dotty.Abstractions.Adapter;
using Dotty.Abstractions.Parser;

namespace Dotty.Terminal.Parser
{
    public sealed class BasicAnsiParser : ITerminalParser
    {
        private const byte ESC = 0x1b;
        private readonly byte[] _leftover = new byte[32];
        private char[] _charScratch = new char[512];
        private int _leftoverLen = 0;
        private Charset _charset = Charset.Ascii;

        private static readonly SearchValues<byte> s_controlChars = SearchValues.Create(
            new byte[] { ESC, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x7F });

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
                    int nextCtrl = inputSpan.Slice(i).IndexOfAny(s_controlChars);

                    int runEnd = nextCtrl >= 0 ? i + nextCtrl : inputSpan.Length;

                    if (runEnd > i)
                    {
                        var run = inputSpan.Slice(i, runEnd - i);
                        DispatchPrintableRun(run);
                        i = runEnd;
                        if (i >= inputSpan.Length) break;
                    }

                    byte b = inputSpan[i];
                    if (b == ESC)
                    {
                        int seqStart = i;
                        i++;
                        if (i >= inputSpan.Length)
                        {
                            SaveLeftover(inputSpan.Slice(seqStart));
                            return;
                        }

                        byte next = inputSpan[i];
                        if (next == (byte)'[')
                        {
                            i++;
                            int paramsStart = i;
                            bool csiFinalFound = false;
                            while (i < inputSpan.Length)
                            {
                                byte cb = inputSpan[i];
                                if (cb >= 0x40 && cb <= 0x7e)
                                {
                                    csiFinalFound = true;
                                    var final = (char)cb;
                                    var paramSpan = inputSpan.Slice(paramsStart, i - paramsStart);

                                    if (final == 'M' && paramSpan.Length == 0)
                                    {
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

                            if (!csiFinalFound)
                            {
                                SaveLeftover(inputSpan.Slice(seqStart));
                                return;
                            }
                        }
                        else if (next == (byte)']')
                        {
                            i++;
                            int payloadStart = i;
                            bool finished = false;
                            while (i < inputSpan.Length)
                            {
                                byte cb = inputSpan[i];
                                if (cb == 0x07)
                                {
                                    HandleOscPayload(inputSpan.Slice(payloadStart, i - payloadStart));
                                    i++;
                                    finished = true;
                                    break;
                                }
                                if (cb == ESC && i + 1 < inputSpan.Length && inputSpan[i + 1] == (byte)'\\')
                                {
                                    HandleOscPayload(inputSpan.Slice(payloadStart, i - payloadStart));
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
                            Handler?.OnFullReset();
                            i++;
                        }
                        else if (next == (byte)'7')
                        {
                            Handler?.OnSaveCursor();
                            i++;
                        }
                        else if (next == (byte)'8')
                        {
                            Handler?.OnRestoreCursor();
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
                        else if (next == (byte)'M')
                        {
                            Handler?.OnReverseIndex();
                            i++;
                        }
                        else if (next == (byte)'H')
                        {
                            Handler?.OnSetTabStop();
                            i++;
                        }
                        else if (next == (byte)'=')
                        {
                            Handler?.OnSetKeypadApplicationMode(true);
                            i++;
                        }
                        else if (next == (byte)'>')
                        {
                            Handler?.OnSetKeypadApplicationMode(false);
                            i++;
                        }
                        else
                        {
                            i++;
                        }
                    }
                    else if (b == 0x07)
                    {
                        Handler?.OnBell();
                        i++;
                    }
                    else if (b == 0x08)
                    {
                        Handler?.OnCursorBack(1);
                        i++;
                    }
                    else if (b == 0x09)
                    {
                        Handler?.OnTab();
                        i++;
                    }
                    else if (b == 0x0A || b == 0x0B || b == 0x0C)
                    {
                        Handler?.OnLineFeed();
                        i++;
                    }
                    else if (b == 0x0D)
                    {
                        Handler?.OnCarriageReturn();
                        i++;
                    }
                    else
                    {
                        i++;
                    }
                }

                _leftoverLen = 0;
            }
            finally
            {
            }
        }

        private void DispatchPrintableRun(ReadOnlySpan<byte> run)
        {
            if (run.IsEmpty) return;

            bool hasNonAscii = false;
            for (int j = 0; j < run.Length; j++)
            {
                if (run[j] >= 0x80)
                {
                    hasNonAscii = true;
                    break;
                }
            }

            if (!hasNonAscii && _charset != Charset.DecSpecialGraphics)
            {
                // Fast path: avoid byte→char conversion for pure ASCII runs.
                // TerminalAdapter provides an internal byte-based path; fall back to
                // the char-based interface for any other ITerminalHandler implementor.
                if (Handler is Terminal.Adapter.TerminalAdapter adapter)
                {
                    adapter.OnPrintAscii(run);
                }
                else
                {
                    Span<char> asc = GetScratch(run.Length, out char[]? rented);
                    try
                    {
                        for (int j = 0; j < run.Length; j++)
                            asc[j] = (char)run[j];
                        Handler?.OnPrint(asc);
                    }
                    finally
                    {
                        ReturnScratch(rented);
                    }
                }
            }
            else
            {
                DecodePrintableRun(run);
            }
        }

        private void HandleOscPayload(ReadOnlySpan<byte> payloadBytes)
        {
            int semiIdx = payloadBytes.IndexOf((byte)';');
            ReadOnlySpan<byte> codeBytes = semiIdx >= 0 ? payloadBytes.Slice(0, semiIdx) : payloadBytes;
            ReadOnlySpan<byte> dataBytes = semiIdx >= 0 ? payloadBytes.Slice(semiIdx + 1) : ReadOnlySpan<byte>.Empty;

            if (!TryParseAsciiInt(codeBytes, out int oscCode))
            {
                return;
            }

            if (dataBytes.IsEmpty)
            {
                Handler?.OnOperatingSystemCommand(oscCode, ReadOnlySpan<char>.Empty);
                return;
            }

            int maxChars = Encoding.UTF8.GetMaxCharCount(dataBytes.Length);
            char[] pooled = ArrayPool<char>.Shared.Rent(maxChars);
            try
            {
                int charsDecoded = Encoding.UTF8.GetChars(dataBytes, pooled.AsSpan());
                Handler?.OnOperatingSystemCommand(oscCode, pooled.AsSpan(0, charsDecoded));
            }
            finally
            {
                ArrayPool<char>.Shared.Return(pooled);
            }
        }

        private void HandleCsi(char final, ReadOnlySpan<byte> paramBytes)
        {
            if (final == 'm' && (paramBytes.IsEmpty || paramBytes[0] != '<'))
            {
                int maxChars = Encoding.UTF8.GetMaxCharCount(paramBytes.Length);
                char[] pooled = ArrayPool<char>.Shared.Rent(maxChars);
                try
                {
                    int charsDecoded = Encoding.UTF8.GetChars(paramBytes, pooled.AsSpan());
                    Handler?.OnSetGraphicsRendition(pooled.AsSpan(0, charsDecoded));
                }
                finally
                {
                    ArrayPool<char>.Shared.Return(pooled);
                }
                return;
            }

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
                        else if (mode == 0 || mode == 1 || mode == 2)
                            Handler?.OnEraseDisplay(mode);
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
                    case 'E':
                        Handler?.OnCursorNextLine(paramCount > 0 ? parsedParams[0] : 1);
                        break;
                    case 'F':
                        Handler?.OnCursorPreviousLine(paramCount > 0 ? parsedParams[0] : 1);
                        break;
                    case 'G':
                        Handler?.OnCursorHorizontalAbsolute(paramCount > 0 ? parsedParams[0] : 1);
                        break;
                    case 'd':
                        Handler?.OnCursorVerticalAbsolute(paramCount > 0 ? parsedParams[0] : 1);
                        break;
                    case 'Z':
                        Handler?.OnBackTab(paramCount > 0 ? parsedParams[0] : 1);
                        break;
                    case 'b':
                        Handler?.OnRepeatCharacter(paramCount > 0 ? parsedParams[0] : 1);
                        break;
                    case 'g':
                    {
                        int mode = paramCount > 0 ? parsedParams[0] : 0;
                        if (mode == 3)
                            Handler?.OnClearAllTabStops();
                        else if (mode == 0)
                            Handler?.OnClearTabStop();
                        break;
                    }
                    case 'L':
                        Handler?.OnInsertLines(paramCount > 0 ? parsedParams[0] : 1);
                        break;
                    case '@':
                        Handler?.OnInsertChars(paramCount > 0 ? parsedParams[0] : 1);
                        break;
                    case 'X':
                        Handler?.OnEraseCharacters(paramCount > 0 ? parsedParams[0] : 1);
                        break;
                    case 'P':
                        Handler?.OnDeleteChars(paramCount > 0 ? parsedParams[0] : 1);
                        break;
                    case 'S':
                        Handler?.OnScrollUp(paramCount > 0 ? parsedParams[0] : 1);
                        break;
                    case 'T':
                        Handler?.OnScrollDown(paramCount > 0 ? parsedParams[0] : 1);
                        break;
                    case 'n':
                        if (paramCount > 0 && parsedParams[0] == 6)
                        {
                            if (isPrivate)
                                Handler?.OnCursorPositionReport();
                            else
                                Handler?.OnDeviceStatusReport(6);
                        }
                        else
                        {
                            Handler?.OnDeviceStatusReport(paramCount > 0 ? parsedParams[0] : 0);
                        }
                        break;
                    case 'c':
                        Handler?.OnSendDeviceAttributes(isPrivate ? 2 : 0);
                        break;
                    case 'r':
                        Handler?.OnSetScrollRegion(
                            paramCount > 0 ? parsedParams[0] : 1,
                            paramCount > 1 ? parsedParams[1] : 0);
                        break;
                    case 'q':
                        Handler?.OnSetCursorShape(paramCount > 0 ? parsedParams[0] : 0);
                        break;
                    case 's':
                        Handler?.OnSaveCursor();
                        break;
                    case 'u':
                        if (isPrivate && paramCount > 0)
                        {
                            int mode = parsedParams[0];
                            Handler?.OnSetKittyKeyboardMode(mode);
                        }
                        else if (isPrivate && paramCount == 0)
                        {
                            Handler?.OnQueryKittyKeyboard();
                        }
                        else
                        {
                            Handler?.OnRestoreCursor();
                        }
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
                                else if (code == 7) Handler?.OnSetAutoWrap(enable);
                                else if (code == 2004) Handler?.OnSetBracketedPasteMode(enable);
                                else if (code == 1000 || code == 1002 || code == 1003 || code == 1005 || code == 1006 || code == 1015) 
                                    Handler?.OnSetMouseMode(code, enable);
                                else if (code == 2026) 
                                    Handler?.OnSetSynchronizedUpdate(enable);
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
                        else if (final == 'M')
                        {
                            Handler?.OnDeleteLines(paramCount > 0 ? parsedParams[0] : 1);
                        }
                        break;
                    default:
                        break;
                }
            }
            else
            {
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
                    else if (mode == 0 || mode == 1 || mode == 2)
                        Handler?.OnEraseDisplay(mode);
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
                case 'E':
                    Handler?.OnCursorNextLine(GetParam(0, 1));
                    break;
                case 'F':
                    Handler?.OnCursorPreviousLine(GetParam(0, 1));
                    break;
                case 'G':
                    Handler?.OnCursorHorizontalAbsolute(GetParam(0, 1));
                    break;
                case 'd':
                    Handler?.OnCursorVerticalAbsolute(GetParam(0, 1));
                    break;
                case 'Z':
                    Handler?.OnBackTab(GetParam(0, 1));
                    break;
                case 'b':
                    Handler?.OnRepeatCharacter(GetParam(0, 1));
                    break;
                case 'g':
                    {
                        int tabClearMode = GetParam(0, 0);
                        if (tabClearMode == 3)
                            Handler?.OnClearAllTabStops();
                        else if (tabClearMode == 0)
                            Handler?.OnClearTabStop();
                    }
                    break;
                case 'L':
                    Handler?.OnInsertLines(GetParam(0, 1));
                    break;
                case '@':
                    Handler?.OnInsertChars(GetParam(0, 1));
                    break;
                case 'X':
                    Handler?.OnEraseCharacters(GetParam(0, 1));
                    break;
                case 'P':
                    Handler?.OnDeleteChars(GetParam(0, 1));
                    break;
                case 'S':
                    Handler?.OnScrollUp(GetParam(0, 1));
                    break;
                case 'T':
                    Handler?.OnScrollDown(GetParam(0, 1));
                    break;
                case 'n':
                    {
                        bool isPrivate = @params.StartsWith("?");
                        int code = isPrivate && @params.Length > 1
                            ? int.TryParse(@params.Substring(1), out var privateCode) ? privateCode : 0
                            : GetParam(0, 0);

                        if (code == 6)
                        {
                            if (isPrivate)
                                Handler?.OnCursorPositionReport();
                            else
                                Handler?.OnDeviceStatusReport(6);
                        }
                        else
                        {
                            Handler?.OnDeviceStatusReport(code);
                        }
                    }
                    break;
                case 'c':
                    Handler?.OnSendDeviceAttributes(@params.StartsWith(">") ? 2 : 0);
                    break;
                case 'r':
                    Handler?.OnSetScrollRegion(GetParam(0, 1), GetParam(1, 0));
                    break;
                case 'q':
                    Handler?.OnSetCursorShape(GetParam(0, 0));
                    break;
                case 's':
                    Handler?.OnSaveCursor();
                    break;
                case 'u':
                    Handler?.OnRestoreCursor();
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
                                    else if (code == 7) Handler?.OnSetAutoWrap(enable);
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
                    bool isSgrMouse = @params.StartsWith("<");
                    if (isSgrMouse)
                    {
                        var partsArray = @params.Substring(1).Split(';', StringSplitOptions.RemoveEmptyEntries);
                        if (partsArray.Length >= 3)
                        {
                            int.TryParse(partsArray[0], out int cb);
                            int.TryParse(partsArray[1], out int cx);
                            int.TryParse(partsArray[2], out int cy);
                            Handler?.OnMouseEvent(cb, cx, cy, final == 'M');
                        }
                    }
                    else if (final == 'M')
                    {
                        Handler?.OnDeleteLines(GetParam(0, 1));
                    }
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

        private static bool TryParseAsciiInt(ReadOnlySpan<byte> bytes, out int value)
        {
            value = 0;
            if (bytes.IsEmpty) return false;

            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                if (b < '0' || b > '9') return false;
                value = (value * 10) + (b - '0');
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

        private void DecodePrintableRun(ReadOnlySpan<byte> run)
        {
            if (run.IsEmpty)
            {
                return;
            }

            int maxChars = Encoding.UTF8.GetMaxCharCount(run.Length);
            Span<char> buffer = GetScratch(maxChars, out char[]? rented);
            try
            {
                int charsDecoded = Encoding.UTF8.GetChars(run, buffer);
                Span<char> charSpan = buffer.Slice(0, charsDecoded);

                if (_charset == Charset.DecSpecialGraphics)
                {
                    for (int i = 0; i < charSpan.Length; i++)
                    {
                        if (s_decSpecialGraphicsMap.TryGetValue(charSpan[i], out var mapped))
                        {
                            charSpan[i] = mapped;
                        }
                    }
                }

                Handler?.OnPrint(charSpan);
            }
            finally
            {
                ReturnScratch(rented);
            }
        }

        private Span<char> GetScratch(int neededLength, out char[]? rented)
        {
            if (neededLength <= _charScratch.Length)
            {
                rented = null;
                return _charScratch.AsSpan(0, neededLength);
            }

            rented = ArrayPool<char>.Shared.Rent(neededLength);
            return rented.AsSpan(0, neededLength);
        }

        private static void ReturnScratch(char[]? rented)
        {
            if (rented is not null)
            {
                ArrayPool<char>.Shared.Return(rented);
            }
        }
    }
}
