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

        // Sequence payloads are kept in the reusable buffer only while a
        // sequence is split across Feed calls.  CSI parameters are deliberately
        // small; OSC strings need room for long hyperlinks and titles.
        private const int MaxCsiParameterBytes = 256;
        private const int MaxOscPayloadBytes = 64 * 1024;

        private enum SequenceState : byte
        {
            None,
            Escape,
            Charset,
            Csi,
            Osc,
            OscEscape,
            DiscardCsi,
            DiscardOsc,
        }

        private byte[] _leftover = new byte[32];
        private char[] _charScratch = new char[512];
        private readonly byte[] _utf8Leftover = new byte[4];
        private int _leftoverLen;
        private int _utf8LeftoverLen;
        private SequenceState _sequenceState;
        private Charset _charset = Charset.Ascii;

        // This set lets the ASCII fast path find both controls and non-ASCII
        // bytes in one pass.  A high-byte run is dispatched with its known
        // classification, so DispatchPrintableRun never scans it a second time.
        private static readonly SearchValues<byte> s_controlRunBreaks = CreateControlRunBreaks();

        private static SearchValues<byte> CreateControlRunBreaks()
        {
            Span<byte> values = stackalloc byte[9];
            values[0] = ESC;
            values[1] = 0x07;
            values[2] = 0x08;
            values[3] = 0x09;
            values[4] = 0x0A;
            values[5] = 0x0B;
            values[6] = 0x0C;
            values[7] = 0x0D;
            values[8] = 0x7F;
            return SearchValues.Create(values);
        }

        private static readonly SearchValues<byte> s_printableRunBreaks = CreatePrintableRunBreaks();

        public ITerminalHandler? Handler { get; set; }

        private static SearchValues<byte> CreatePrintableRunBreaks()
        {
            Span<byte> values = stackalloc byte[9 + 128];
            values[0] = ESC;
            values[1] = 0x07;
            values[2] = 0x08;
            values[3] = 0x09;
            values[4] = 0x0A;
            values[5] = 0x0B;
            values[6] = 0x0C;
            values[7] = 0x0D;
            values[8] = 0x7F;
            for (int i = 0; i < 128; i++)
            {
                values[9 + i] = (byte)(0x80 + i);
            }

            return SearchValues.Create(values);
        }

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
            int i = 0;
            while (i < bytes.Length)
            {
                if (_sequenceState != SequenceState.None)
                {
                    if (_utf8LeftoverLen > 0)
                        FlushUtf8Leftover();
                    ProcessSequenceByte(bytes[i++]);
                    continue;
                }

                int breakOffset = bytes.Slice(i).IndexOfAny(s_printableRunBreaks);
                int runEnd = breakOffset >= 0 ? i + breakOffset : bytes.Length;
                if (runEnd > i)
                {
                    DispatchPrintableRun(bytes.Slice(i, runEnd - i), hasNonAscii: false);
                    i = runEnd;
                    if (i >= bytes.Length)
                        break;
                }

                byte b = bytes[i];
                if (b >= 0x80)
                {
                    // The first classification pass stopped at this high byte.
                    // Find the end once, then dispatch without another
                    // hasNonAscii scan.
                    int highRunOffset = bytes.Slice(i + 1).IndexOfAny(s_controlRunBreaks);
                    int highRunEnd = highRunOffset >= 0
                        ? i + 1 + highRunOffset
                        : bytes.Length;
                    DispatchPrintableRun(bytes.Slice(i, highRunEnd - i), hasNonAscii: true);
                    i = highRunEnd;
                    continue;
                }

                if (_utf8LeftoverLen > 0)
                    FlushUtf8Leftover();

                if (b == ESC)
                {
                    _sequenceState = SequenceState.Escape;
                    i++;
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
        }

        private void ProcessSequenceByte(byte b)
        {
            switch (_sequenceState)
            {
                case SequenceState.Escape:
                    ProcessEscapeByte(b);
                    break;
                case SequenceState.Charset:
                    if (b == ESC)
                    {
                        _sequenceState = SequenceState.Escape;
                    }
                    else if (b == 0x18 || b == 0x1A)
                    {
                        ResetSequence();
                    }
                    else
                    {
                        ApplyCharsetSelection((char)b);
                        ResetSequence();
                    }
                    break;
                case SequenceState.Csi:
                    ProcessCsiByte(b);
                    break;
                case SequenceState.Osc:
                    ProcessOscByte(b);
                    break;
                case SequenceState.OscEscape:
                    ProcessOscEscapeByte(b);
                    break;
                case SequenceState.DiscardCsi:
                    if (b >= 0x40 && b <= 0x7E)
                    {
                        // The final is consumed but never dispatched.
                        ResetSequence();
                    }
                    else if (b == ESC)
                    {
                        ResetSequence();
                        _sequenceState = SequenceState.Escape;
                    }
                    else if (b == 0x18 || b == 0x1A)
                    {
                        ResetSequence();
                    }
                    break;
                case SequenceState.DiscardOsc:
                    if (b == 0x07 || b == 0x18 || b == 0x1A)
                    {
                        // BEL is OSC's terminator; CAN/SUB cancel it.
                        ResetSequence();
                    }
                    else if (b == ESC)
                    {
                        // Treat ESC as the beginning of a fresh sequence.  A
                        // following '\' therefore consumes an abandoned ST,
                        // while '[' and ']' start valid new sequences.
                        ResetSequence();
                        _sequenceState = SequenceState.Escape;
                    }
                    break;
            }
        }

        private void ProcessEscapeByte(byte b)
        {
            switch (b)
            {
                case (byte)'[':
                    _leftoverLen = 0;
                    _sequenceState = SequenceState.Csi;
                    break;
                case (byte)']':
                    _leftoverLen = 0;
                    _sequenceState = SequenceState.Osc;
                    break;
                case (byte)'c':
                    Handler?.OnFullReset();
                    ResetSequence();
                    break;
                case (byte)'7':
                    Handler?.OnSaveCursor();
                    ResetSequence();
                    break;
                case (byte)'8':
                    Handler?.OnRestoreCursor();
                    ResetSequence();
                    break;
                case (byte)'(':
                case (byte)')':
                    _sequenceState = SequenceState.Charset;
                    break;
                case (byte)'M':
                    Handler?.OnReverseIndex();
                    ResetSequence();
                    break;
                case (byte)'H':
                    Handler?.OnSetTabStop();
                    ResetSequence();
                    break;
                case (byte)'=':
                    Handler?.OnSetKeypadApplicationMode(true);
                    ResetSequence();
                    break;
                case (byte)'>':
                    Handler?.OnSetKeypadApplicationMode(false);
                    ResetSequence();
                    break;
                case ESC:
                    // A second ESC supersedes the incomplete one.
                    _sequenceState = SequenceState.Escape;
                    break;
                default:
                    ResetSequence();
                    break;
            }
        }

        private void ProcessCsiByte(byte b)
        {
            if (b >= 0x40 && b <= 0x7E)
            {
                HandleCsi((char)b, _leftover.AsSpan(0, _leftoverLen));
                ResetSequence();
            }
            else if (b == ESC)
            {
                ResetSequence();
                _sequenceState = SequenceState.Escape;
            }
            else if (b == 0x18 || b == 0x1A)
            {
                ResetSequence();
            }
            else if (!AppendSequenceByte(b, MaxCsiParameterBytes))
            {
                // Once the cap is crossed, discard the accumulated
                // parameters and consume bytes until a CSI final, ESC, CAN,
                // or SUB.
                BeginDiscard(SequenceState.DiscardCsi);
            }
        }

        private void ProcessOscByte(byte b)
        {
            if (b == 0x07)
            {
                HandleOscPayload(_leftover.AsSpan(0, _leftoverLen));
                ResetSequence();
            }
            else if (b == ESC)
            {
                _sequenceState = SequenceState.OscEscape;
            }
            else if (b == 0x18 || b == 0x1A)
            {
                ResetSequence();
            }
            else if (!AppendSequenceByte(b, MaxOscPayloadBytes))
            {
                BeginDiscard(SequenceState.DiscardOsc);
            }
        }

        private void ProcessOscEscapeByte(byte b)
        {
            if (b == (byte)'\\')
            {
                HandleOscPayload(_leftover.AsSpan(0, _leftoverLen));
                ResetSequence();
            }
            else if (b == ESC)
            {
                // The first ESC was payload; the second may begin ST.
                if (!AppendSequenceByte(ESC, MaxOscPayloadBytes))
                {
                    BeginDiscard(SequenceState.DiscardOsc);
                }
                else
                {
                    _sequenceState = SequenceState.OscEscape;
                }
            }
            else if (b == 0x18 || b == 0x1A)
            {
                ResetSequence();
            }
            else if (!AppendSequenceByte(ESC, MaxOscPayloadBytes))
            {
                BeginDiscard(SequenceState.DiscardOsc);
                ProcessSequenceByte(b);
            }
            else
            {
                _sequenceState = SequenceState.Osc;
                ProcessOscByte(b);
            }
        }

        private bool AppendSequenceByte(byte b, int cap)
        {
            if (_leftoverLen >= cap)
                return false;

            EnsureSequenceCapacity(_leftoverLen + 1, cap);
            _leftover[_leftoverLen++] = b;
            return true;
        }

        private void EnsureSequenceCapacity(int needed, int cap)
        {
            if (needed <= _leftover.Length)
                return;

            int newSize = Math.Min(cap, Math.Max(needed, _leftover.Length * 2));
            Array.Resize(ref _leftover, newSize);
        }

        private void ResetSequence()
        {
            _sequenceState = SequenceState.None;
            _leftoverLen = 0;
        }

        private void BeginDiscard(SequenceState discardState)
        {
            _leftoverLen = 0;
            _sequenceState = discardState;
        }

        private void DispatchPrintableRun(ReadOnlySpan<byte> run, bool hasNonAscii)
        {
            if (run.IsEmpty) return;

            if (!hasNonAscii && _utf8LeftoverLen == 0 && _charset != Charset.DecSpecialGraphics)
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
            if (paramBytes.Length > MaxCsiParameterBytes)
            {
                // Defensive guard for callers inside this class; Feed already
                // discards an over-cap CSI before it reaches dispatch.
                return;
            }

            if (final == 'm' && (paramBytes.IsEmpty || paramBytes[0] != '<'))
            {
                // CSI SGR is a byte-preserving parameter string (it may use
                // ':' subparameters), not a numeric CSI form.  The cap keeps
                // this on the parser-owned scratch buffer and allocation-free.
                int maxChars = Encoding.UTF8.GetMaxCharCount(paramBytes.Length);
                Span<char> sgr = GetScratch(maxChars, out char[]? rented);
                try
                {
                    int charsDecoded = Encoding.UTF8.GetChars(paramBytes, sgr);
                    Handler?.OnSetGraphicsRendition(sgr.Slice(0, charsDecoded));
                }
                finally
                {
                    ReturnScratch(rented);
                }
                return;
            }

            // Scroll commands consume only their first parameter. Keep the
            // complete validation/default handling in TryParseParams, but
            // reserve a single stack slot instead of eight for this hot path.
            if (final == 'S' || final == 'T')
            {
                Span<int> scrollParams = stackalloc int[1];
                if (!TryParseParams(
                    paramBytes,
                    scrollParams,
                    out int scrollParamCount,
                    out _,
                    out _))
                {
                    return;
                }

                int scrollLines = scrollParamCount > 0 ? scrollParams[0] : 1;
                if (final == 'S')
                    Handler?.OnScrollUp(scrollLines);
                else
                    Handler?.OnScrollDown(scrollLines);
                return;
            }

            Span<int> parsedParams = stackalloc int[8];
            bool parsed = TryParseParams(
                paramBytes,
                parsedParams,
                out int paramCount,
                out bool isPrivate,
                out bool isMouse);
            if (!parsed)
            {
                // A malformed field still terminates at the CSI final byte.
                // Preserve valid private modes before that field, matching
                // terminal fallback behaviour without allocating strings.
                if ((final == 'h' || final == 'l') && isPrivate && paramCount > 0)
                {
                    bool enable = final == 'h';
                    for (int pIdx = 0; pIdx < paramCount; pIdx++)
                        HandlePrivateMode(parsedParams[pIdx], enable);
                }
                return;
            }
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
                case 't':
                    // Window manipulation: CSI Ps t
                    Handler?.OnWindowReport(paramCount > 0 ? parsedParams[0] : 0);
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
                            HandlePrivateMode(parsedParams[pIdx], enable);
                    }
                    break;
                case 'M':
                case 'm':
                    if (paramCount >= 3 && (isMouse || !isPrivate))
                    {
                        int cb = parsedParams[0];
                        int cx = parsedParams[1];
                        int cy = parsedParams[2];
                        bool isPress = isMouse
                            ? final == 'M'
                            : (cb & 0x03) != 0x03;
                        Handler?.OnMouseEvent(cb, cx, cy, isPress);
                    }
                    else if (!isMouse && !isPrivate && final == 'M')
                    {
                        Handler?.OnDeleteLines(paramCount > 0 ? parsedParams[0] : 1);
                    }
                    break;
                default:
                    break;
            }
        }

        private void HandlePrivateMode(int code, bool enabled)
        {
            switch (code)
            {
                case 1049:
                    Handler?.OnSetAlternateScreen(enabled);
                    break;
                case 25:
                    Handler?.OnSetCursorVisibility(enabled);
                    break;
                case 6:
                    Handler?.OnSetOriginMode(enabled);
                    break;
                case 1:
                    Handler?.OnSetApplicationCursorKeys(enabled);
                    break;
                case 7:
                    Handler?.OnSetAutoWrap(enabled);
                    break;
                case 2004:
                    Handler?.OnSetBracketedPasteMode(enabled);
                    break;
                case 1004:
                    Handler?.OnSetFocusReporting(enabled);
                    break;
                case 1000:
                case 1002:
                case 1003:
                case 1005:
                case 1006:
                case 1015:
                    Handler?.OnSetMouseMode(code, enabled);
                    break;
                case 2026:
                    Handler?.OnSetSynchronizedUpdate(enabled);
                    break;
            }
        }


        private static bool TryParseParams(
            ReadOnlySpan<byte> paramBytes,
            Span<int> outParams,
            out int count,
            out bool isPrivate,
            out bool isMouse)
        {
            count = 0;
            isPrivate = false;
            isMouse = false;

            int start = 0;
            bool fieldStart = true;
            if (!paramBytes.IsEmpty)
            {
                if (paramBytes[0] == '?' || paramBytes[0] == '>')
                {
                    isPrivate = true;
                    start = 1;
                    fieldStart = false;
                }
                else if (paramBytes[0] == '<')
                {
                    isMouse = true;
                    start = 1;
                    fieldStart = false;
                }
            }

            int current = 0;
            bool hasDigit = false;
            for (int i = start; i < paramBytes.Length; i++)
            {
                byte b = paramBytes[i];
                if ((b == '?' || b == '>') && fieldStart && isPrivate)
                {
                    fieldStart = false;
                }
                else if (b >= '0' && b <= '9')
                {
                    int digit = b - '0';
                    current = current > (int.MaxValue - digit) / 10
                        ? int.MaxValue
                        : (current * 10) + digit;
                    hasDigit = true;
                    fieldStart = false;
                }
                else if (b == ';')
                {
                    StoreParameter(hasDigit ? current : 0, outParams, ref count);
                    current = 0;
                    hasDigit = false;
                    fieldStart = true;
                }
                else if (b != ' ')
                {
                    // Unsupported intermediates are rejected without a
                    // string-parsing fallback (which used to allocate).
                    return false;
                }
            }

            if (hasDigit || start < paramBytes.Length)
                StoreParameter(current, outParams, ref count);

            return true;
        }


        private static void StoreParameter(int value, Span<int> output, ref int count)
        {
            // Explicit excess-parameter policy: retain only the first eight
            // numeric fields used by the handler and ignore the rest, matching
            // terminal behaviour for forms with more parameters.
            if (count < output.Length)
            {
                output[count] = value;
                count++;
            }
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
                return;

            if (_utf8LeftoverLen > 0)
            {
                int expected = GetUtf8ExpectedLength(_utf8Leftover[0]);
                int needed = expected - _utf8LeftoverLen;
                int available = Math.Min(Math.Max(needed, 0), run.Length);
                bool continuation = expected > 0;
                for (int i = 0; i < available; i++)
                    continuation &= IsUtf8Continuation(run[i]);

                if (!continuation)
                {
                    FlushUtf8Leftover();
                }
                else if (available < needed)
                {
                    run.Slice(0, available).CopyTo(_utf8Leftover.AsSpan(_utf8LeftoverLen));
                    _utf8LeftoverLen += available;
                    return;
                }
                else
                {
                    Span<byte> completed = stackalloc byte[4];
                    _utf8Leftover.AsSpan(0, _utf8LeftoverLen).CopyTo(completed);
                    run.Slice(0, needed).CopyTo(completed.Slice(_utf8LeftoverLen));
                    _utf8LeftoverLen = 0;
                    DecodeUtf8Chunk(completed.Slice(0, expected));
                    run = run.Slice(needed);
                    if (run.IsEmpty)
                        return;
                }
            }

            int trailing = GetUtf8IncompleteTailLength(run);
            if (trailing > 0)
            {
                ReadOnlySpan<byte> complete = run.Slice(0, run.Length - trailing);
                if (!complete.IsEmpty)
                    DecodeUtf8Chunk(complete);
                run.Slice(run.Length - trailing).CopyTo(_utf8Leftover);
                _utf8LeftoverLen = trailing;
                return;
            }

            DecodeUtf8Chunk(run);
        }

        private void FlushUtf8Leftover()
        {
            if (_utf8LeftoverLen == 0)
                return;

            int length = _utf8LeftoverLen;
            _utf8LeftoverLen = 0;
            DecodeUtf8Chunk(_utf8Leftover.AsSpan(0, length));
        }

        private void DecodeUtf8Chunk(ReadOnlySpan<byte> run)
        {
            if (run.IsEmpty)
                return;

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
                            charSpan[i] = mapped;
                    }
                }

                Handler?.OnPrint(charSpan);
            }
            finally
            {
                ReturnScratch(rented);
            }
        }

        private static int GetUtf8ExpectedLength(byte first)
        {
            if (first < 0x80)
                return 1;
            if (first >= 0xC2 && first <= 0xDF)
                return 2;
            if (first >= 0xE0 && first <= 0xEF)
                return 3;
            if (first >= 0xF0 && first <= 0xF4)
                return 4;
            return 0;
        }

        private static bool IsUtf8Continuation(byte value) => (value & 0xC0) == 0x80;

        private static int GetUtf8IncompleteTailLength(ReadOnlySpan<byte> run)
        {
            if (run.IsEmpty || run[^1] < 0x80)
                return 0;

            int lead = run.Length - 1;
            while (lead >= 0 && IsUtf8Continuation(run[lead]))
                lead--;
            if (lead < 0)
                return 0;

            int expected = GetUtf8ExpectedLength(run[lead]);
            int actual = run.Length - lead;
            return expected > actual && actual <= 4 ? actual : 0;
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
