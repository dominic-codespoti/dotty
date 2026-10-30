using System;
using System.Threading;
using Dotty.Abstractions.Adapter;
using Dotty.Abstractions.Config;

namespace Dotty.Terminal.Adapter;

public delegate void TerminalReplyHandler(ReadOnlySpan<char> reply);

/// <summary>
/// Adapter that connects the parser callbacks to a TerminalBuffer and exposes a render event.
/// Keeps responsibilities minimal: buffer management and render notification.
/// </summary>
public partial class TerminalAdapter : ITerminalHandler, IDisposable
{
    public enum MouseMode
    {
        None = 0,
        X10 = 9,
        Normal = 1000,
        ButtonEvent = 1002,
        AnyEvent = 1003
    }

    public enum MouseEncoding
    {
        Default = 0,
        UTF8 = 1005,
        SGR = 1006,
        URXVT = 1015,
        SGRPixels = 1016
    }

    private readonly TerminalBuffer _buffer;
    private TerminalPalette _palette = new();
    private readonly object _paletteSync = new();
    private CellAttributes _currentAttributes = CellAttributes.Default;
    private CellAttributes _savedAttributes = CellAttributes.Default;
    private int _defaultForegroundOverrideArgb;
    private string _defaultFgHex = "#CCCCCC";
    private int _defaultBackgroundOverrideArgb;
    private string _defaultBgHex = "#1E1E1E";
    private string _da2Response = "\u001b[>1;0;0c";
    private string _xtVersionReply = "\u001bP>|Dotty\u001b\\";
    private string _termName = DefaultTermName;
    private bool _hasSavedAttributes;
    private string? _windowTitle;
    private char _lastPrintedChar;

    public int CursorShape { get; private set; }
    public bool KeypadApplicationMode { get; private set; }
    public bool ApplicationCursorKeysEnabled { get; private set; }
    public MouseMode CurrentMouseMode { get; private set; } = MouseMode.None;
    public MouseEncoding CurrentMouseEncoding { get; private set; } = MouseEncoding.Default;
    public bool MouseReportingEnabled => CurrentMouseMode != MouseMode.None;

    /// <summary>
    /// Alternate scroll (DECSET 1007): while the alternate screen is shown and no mouse mode is set, the wheel sends
    /// cursor up/down. Set at power-on and after RIS (kitty/foot/Alacritty behaviour); the <c>mouse.alternateScroll</c>
    /// setting can disable the translation in the host.
    /// </summary>
    public bool AlternateScrollMode { get; private set; } = true;

    public TerminalAdapter(int rows = 24, int columns = 80, TimeProvider? timeProvider = null)
        : this(rows, columns, ScrollbackPolicy.Default, timeProvider)
    {
    }

    public TerminalAdapter(int rows, int columns, int scrollbackCapacity, TimeProvider? timeProvider = null)
        : this(rows, columns, ScrollbackPolicy.Fixed(scrollbackCapacity), timeProvider)
    {
    }

    public TerminalAdapter(int rows, int columns, ScrollbackPolicy scrollbackPolicy, TimeProvider? timeProvider = null)
    {
        _buffer = new TerminalBuffer(rows, columns, scrollbackPolicy);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _buffer.GraphicsClock = _timeProvider;
    }

    public event Action<string>? RenderRequested;
    public event Action<Dotty.Abstractions.Clipboard.ClipboardWriteRequest>? ClipboardWriteRequested;
    public event Action<string>? TitleChanged;
    public event Action? PaletteChanged;
    public event Action? Bell;

    public void OnHyperlink(string uri) => SetActiveHyperlink(uri.AsSpan());

    public event TerminalReplyHandler? ReplyRequested;
    private void SendTwoNumberReply(ReadOnlySpan<char> prefix, int first, int second, char suffix)
    {
        var handler = ReplyRequested;
        if (handler is null)
            return;

        Span<char> reply = stackalloc char[48];
        prefix.CopyTo(reply);
        int length = prefix.Length;
        first.TryFormat(reply[length..], out int written);
        length += written;
        reply[length++] = ';';
        second.TryFormat(reply[length..], out written);
        length += written;
        reply[length++] = suffix;
        handler(reply[..length]);
    }

    private void SendNumberReply(ReadOnlySpan<char> prefix, int value, char suffix)
    {
        var handler = ReplyRequested;
        if (handler is null)
            return;

        Span<char> reply = stackalloc char[32];
        prefix.CopyTo(reply);
        int length = prefix.Length;
        value.TryFormat(reply[length..], out int written);
        length += written;
        reply[length++] = suffix;
        handler(reply[..length]);
    }


    public TerminalBuffer Buffer => _buffer;
    object? ITerminalHandler.Buffer => _buffer;
    public Buffer.StyleSet StyleSet => _buffer.StyleSet;

    /// <summary>
    /// Optional trace hook for diagnostics. Subscribe to receive snapshot events
    /// at key buffer-modifying operations. String parameter is the reason/event name.
    /// </summary>
    public Action<string, TerminalBuffer>? Trace { get; set; }

    public string? WindowTitle => _windowTitle;
    public uint? DefaultForegroundOverrideArgb => ReadColorOverride(ref _defaultForegroundOverrideArgb);
    public uint? DefaultBackgroundOverrideArgb => ReadColorOverride(ref _defaultBackgroundOverrideArgb);
    private string DefaultForegroundThemeHex => Volatile.Read(ref _defaultFgHex);
    private string DefaultBackgroundThemeHex => Volatile.Read(ref _defaultBgHex);
    private static uint? ReadColorOverride(ref int value)
    {
        int color = Volatile.Read(ref value);
        return color == 0 ? null : unchecked((uint)color);
    }

    public void SetPaletteBaseline(ReadOnlySpan<uint> ansi16)
    {
        if (ansi16.Length != 16)
            throw new ArgumentException("ANSI palette must have exactly 16 colors", nameof(ansi16));
        lock (_paletteSync)
        {
            TerminalPalette currentPalette = Volatile.Read(ref _palette);
            if (ansi16.SequenceEqual(currentPalette.BaselineAnsi16)) return;
            Span<uint> previous = stackalloc uint[16];
            currentPalette.EffectiveAnsi16.CopyTo(previous);
            var replacement = new TerminalPalette();
            replacement.SetBaseline(ansi16);
            Volatile.Write(ref _palette, replacement);
            RemapAnsiStyles(previous);
            _buffer.InvalidateRowsForPaletteChange();
            PaletteChanged?.Invoke();
            RequestRender();
        }
    }

    /// <summary>
    /// Sets the default foreground and background hex colors (without #) for
    /// OSC 10/11 queries. Called by the app layer on startup and theme change.
    /// </summary>
    public void SetDefaultColors(string fgHex, string bgHex)
    {
        uint oldForeground = DefaultForegroundOverrideArgb ?? ParseHexDefault(DefaultForegroundThemeHex);
        uint oldBackground = DefaultBackgroundOverrideArgb ?? ParseHexDefault(DefaultBackgroundThemeHex);
        if (!string.IsNullOrWhiteSpace(fgHex))
        {
            string value = fgHex.StartsWith('#') ? fgHex : "#" + fgHex;
            if (!string.Equals(Volatile.Read(ref _defaultFgHex), value, StringComparison.Ordinal))
                Volatile.Write(ref _defaultFgHex, value);
        }
        if (!string.IsNullOrWhiteSpace(bgHex))
        {
            string value = bgHex.StartsWith('#') ? bgHex : "#" + bgHex;
            if (!string.Equals(Volatile.Read(ref _defaultBgHex), value, StringComparison.Ordinal))
                Volatile.Write(ref _defaultBgHex, value);
        }
        if (oldForeground != (DefaultForegroundOverrideArgb ?? ParseHexDefault(DefaultForegroundThemeHex)) ||
            oldBackground != (DefaultBackgroundOverrideArgb ?? ParseHexDefault(DefaultBackgroundThemeHex)))
            NotifyPaletteChanged();
    }

    /// <summary>TERM value assumed until <see cref="SetTerminalIdentity"/> reports the one the child received.</summary>
    public const string DefaultTermName = "xterm-256color";

    /// <summary>
    /// Sets the identity reported to applications: DA2 (<c>CSI &gt; c</c> → <c>CSI &gt; 1 ; Pv ; 0 c</c> with
    /// Pv = major·10000 + minor·100 + patch), XTVERSION (<c>CSI &gt; q</c> → <c>DCS &gt; | Dotty &lt;version&gt; ST</c>)
    /// and the XTGETTCAP <c>TN</c> name (the TERM value the child process was given). Called by the session
    /// before the PTY starts; blank arguments keep the previous value.
    /// </summary>
    public void SetTerminalIdentity(string version, string termName)
    {
        if (!string.IsNullOrWhiteSpace(version))
        {
            _da2Response = $"\u001b[>1;{Da2VersionNumber(version)};0c";
            _xtVersionReply = $"\u001bP>|Dotty {version}\u001b\\";
        }
        if (!string.IsNullOrWhiteSpace(termName)) _termName = termName;
    }

    /// <summary>The TERM name answered for XTGETTCAP <c>TN</c>.</summary>
    public string TermName => _termName;

    // major.minor.patch from the leading numeric core ("0.4.0-beta.1" → 400); missing parts are 0.
    internal static int Da2VersionNumber(ReadOnlySpan<char> version)
    {
        Span<int> parts = stackalloc int[3];
        int index = 0;
        foreach (char c in version)
        {
            if (char.IsAsciiDigit(c))
                parts[index] = Math.Min(parts[index] * 10 + (c - '0'), 99_999);
            else if (c == '.' && index < 2)
                index++;
            else
                break;
        }
        return parts[0] * 10_000 + Math.Min(parts[1], 99) * 100 + Math.Min(parts[2], 99);
    }

    public void ResizeBuffer(int rows, int columns)
    {
        try
        {
            _buffer.Resize(rows, columns);
            RequestRender();
        }
        catch { }
    }

    public void OnPrint(ReadOnlySpan<char> text)
    {
        _buffer.WriteText(text, _currentAttributes);
        if (!text.IsEmpty)
        {
            _lastPrintedChar = text[text.Length - 1];
        }
        if (text.Length > 40)
            Trace?.Invoke($"Print({text.Length}chars)", _buffer);
        RequestRender();
    }

    /// <summary>
    /// Fast path: writes ASCII bytes directly to the buffer, bypassing the
    /// byte→char conversion that the public ITerminalHandler interface requires.
    /// Called from BasicAnsiParser when it detects a pure-ASCII run.
    /// </summary>
    internal void OnPrintAscii(ReadOnlySpan<byte> text)
    {
        _buffer.WriteAscii(text, _currentAttributes);
        if (!text.IsEmpty)
            _lastPrintedChar = (char)text[text.Length - 1];
        RequestRender();
    }


    /// <summary>Longest window title retained from OSC 0/2, in UTF-16 chars.</summary>
    public const int MaxTitleChars = 4096;

    public void OnOperatingSystemCommand(int code, ReadOnlySpan<char> payload)
    {
        if (TryHandleShellIntegration(code, payload)) return;
        if (code == 0 || code == 2)
        {
            // The parser admits OSC strings up to 1 MiB (OSC 52); a title never needs that.
            if (payload.Length > MaxTitleChars)
            {
                int keep = MaxTitleChars;
                if (char.IsHighSurrogate(payload[keep - 1]))
                    keep--;
                payload = payload[..keep];
            }
            if (_windowTitle is null || !payload.SequenceEqual(_windowTitle.AsSpan()))
                _windowTitle = payload.ToString();
            TitleChanged?.Invoke(_windowTitle);
            RequestRender();
        }
        else if (code == 4)
        {
            HandleOscPalette(payload);
        }
        else if (code == 104)
        {
            HandleOscPaletteReset(payload);
        }
        else if (code == 8)
        {
            HandleOsc8(payload);
        }
        else if (code == 52)
        {
            HandleOsc52(payload);
        }
        else if (code == 10 || code == 11 || code == 12)
        {
            HandleOscDynamicColor(code, payload);
        }
        else if (code is 110 or 111)
        {
            ClearDynamicColorOverride(code == 110 ? 10 : 11);
        }
        else if (code == 112)
        {
            // Cursor color is accepted but not rendered.
        }
    }


    public void OnSaveCursor()
    {
        _buffer.SaveCursor();
        _savedAttributes = _currentAttributes;
        _hasSavedAttributes = true;
    }

    public void OnRestoreCursor()
    {
        _buffer.RestoreCursor();
        if (_hasSavedAttributes)
        {
            _currentAttributes = _savedAttributes;
            _hasSavedAttributes = false;
        }
        RequestRender();
    }

    public void OnSetAutoWrap(bool enabled)
    {
        _buffer.SetAutoWrap(enabled);
    }

    public void OnSetTabStop()
    {
        _buffer.SetTabStopAt(_buffer.CursorCol);
    }

    public void OnClearTabStop()
    {
        _buffer.ClearTabStopAt(_buffer.CursorCol);
    }

    public void OnClearAllTabStops()
    {
        _buffer.ClearAllTabStops();
    }

    public void OnReverseIndex()
    {
        _buffer.ReverseIndex();
        Trace?.Invoke($"RI cur=({_buffer.CursorRow},{_buffer.CursorCol})", _buffer);
        RequestRender();
    }

    public void OnSetBracketedPasteMode(bool enabled)
    {
        _buffer.SetBracketedPasteMode(enabled);
    }

    public void OnInsertChars(int n)
    {
        _buffer.InsertChars(n);
        RequestRender();
    }

    public void OnDeleteChars(int n)
    {
        _buffer.DeleteChars(n);
        RequestRender();
    }

    public void OnEraseCharacters(int n)
    {
        _buffer.EraseCharacters(n);
        Trace?.Invoke($"ECH({n})", _buffer);
        RequestRender();
    }

    public void OnInsertLines(int n)
    {
        _buffer.InsertLines(n);
        Trace?.Invoke($"IL({n})", _buffer);
        RequestRender();
    }

    public void OnDeleteLines(int n)
    {
        _buffer.DeleteLines(n);
        Trace?.Invoke($"DL({n})", _buffer);
        RequestRender();
    }

    public void OnClearScreen()
    {
        _buffer.EraseDisplay(2);
        Trace?.Invoke("ED(2)", _buffer);
        RequestRender();
    }

    public void OnClearScrollback()
    {
        _buffer.ClearScrollback();
        RequestRender();
    }

    public void OnEraseDisplay(int mode)
    {
        if (Environment.GetEnvironmentVariable("DOTTY_DIAG") != null)
            Console.Error.WriteLine($"[DIAG] ED({mode}) cursor=({_buffer.CursorRow},{_buffer.CursorCol})");
        _buffer.EraseDisplay(mode);
        Trace?.Invoke($"ED({mode})", _buffer);
        RequestRender();
    }

    public void OnSetGraphicsRendition(ReadOnlySpan<char> parameters)
    {
        _currentAttributes = SgrParserArgb.Apply(parameters, _currentAttributes, Volatile.Read(ref _palette));
    }

    public void OnMoveCursor(int row, int col)
    {
        _buffer.SetCursor(Math.Max(0, row - 1), Math.Max(0, col - 1));
        Trace?.Invoke($"CUP({row},{col})→({_buffer.CursorRow},{_buffer.CursorCol})", _buffer);
        RequestRender();
    }

    public void OnCursorUp(int n)
    {
        _buffer.MoveCursorBy(-Math.Max(1, n), 0);
        Trace?.Invoke($"CUU({n}) cur=({_buffer.CursorRow},{_buffer.CursorCol})", _buffer);
        RequestRender();
    }

    public void OnCursorDown(int n)
    {
        _buffer.MoveCursorBy(Math.Max(1, n), 0);
        Trace?.Invoke($"CUD({n}) cur=({_buffer.CursorRow},{_buffer.CursorCol})", _buffer);
        RequestRender();
    }

    public void OnCursorForward(int n)
    {
        _buffer.MoveCursorBy(0, Math.Max(1, n));
        Trace?.Invoke($"CUF({n}) cur=({_buffer.CursorRow},{_buffer.CursorCol})", _buffer);
        RequestRender();
    }

    public void OnCursorBack(int n)
    {
        _buffer.MoveCursorBy(0, -Math.Max(1, n));
        Trace?.Invoke($"CUB({n}) cur=({_buffer.CursorRow},{_buffer.CursorCol})", _buffer);
        RequestRender();
    }

    public void OnEraseLine(int mode)
    {
        _buffer.EraseLine(mode);
        Trace?.Invoke($"EL({mode}) cur=({_buffer.CursorRow},{_buffer.CursorCol})", _buffer);
        RequestRender();
    }

    public void OnCarriageReturn()
    {
        _buffer.CarriageReturn();
        RequestRender();
    }

    public void OnLineFeed()
    {
        _buffer.LineFeed();
        Trace?.Invoke($"LF cur=({_buffer.CursorRow},{_buffer.CursorCol})", _buffer);
        RequestRender();
    }

    public void OnIndex()
    {
        _buffer.Index();
        Trace?.Invoke($"IND cur=({_buffer.CursorRow},{_buffer.CursorCol})", _buffer);
        RequestRender();
    }

    public void OnNextLine()
    {
        _buffer.NextLine();
        Trace?.Invoke($"NEL cur=({_buffer.CursorRow},{_buffer.CursorCol})", _buffer);
        RequestRender();
    }

    /// <summary>DECSET/DECRST 1049: alternate screen with cursor save/restore.</summary>
    public void OnSetAlternateScreen(bool enabled)
    {
        bool wasAlternate = _buffer.IsAlternateScreenActive;
        _buffer.SetAlternateScreen(enabled);
        AbortKittyGraphicsTransferIfScreenChanged(wasAlternate);
        SetKittyAlternateScreen(enabled);
        Trace?.Invoke($"AltScreen({enabled})", _buffer);
        RequestRender();
    }

    /// <summary>DECSET/DECRST 47 and 1047: alternate screen switch only; the cursor is neither saved nor restored.</summary>
    public void OnSwitchAlternateScreen(bool enabled)
    {
        bool wasAlternate = _buffer.IsAlternateScreenActive;
        _buffer.SwitchAlternateScreen(enabled);
        AbortKittyGraphicsTransferIfScreenChanged(wasAlternate);
        SetKittyAlternateScreen(enabled);
        Trace?.Invoke($"AltScreen47({enabled})", _buffer);
        RequestRender();
    }

    public void OnSetOriginMode(bool enabled)
    {
        _buffer.SetOriginMode(enabled);
        Trace?.Invoke($"DECOM({enabled})", _buffer);
        RequestRender();
    }

    public void OnSetScrollRegion(int top1Based, int bottom1Based)
    {
        // If bottom omitted (0), treat as full screen bottom
        if (bottom1Based == 0) bottom1Based = _buffer.Rows;
        _buffer.SetScrollRegion(top1Based, bottom1Based);
        Trace?.Invoke($"DECSTBM({top1Based},{bottom1Based})", _buffer);
        RequestRender();
    }

    public void OnSetCursorVisibility(bool visible)
    {
        _buffer.SetCursorVisible(visible);
        RequestRender();
    }

    public void OnBell()
    {
        Bell?.Invoke();
    }

    public void OnCursorHorizontalAbsolute(int col)
    {
        // CHA - CSI n G - move cursor to column n (1-based)
        int targetCol = Math.Max(0, col - 1);
        _buffer.MoveCursorBy(0, targetCol - _buffer.CursorCol);
        Trace?.Invoke($"CHA({col})→({_buffer.CursorRow},{_buffer.CursorCol})", _buffer);
        RequestRender();
    }

    public void OnCursorVerticalAbsolute(int row)
    {
        // VPA - CSI n d - move cursor to row n (1-based)
        _buffer.MoveCursorTo(Math.Max(0, row - 1), _buffer.CursorCol);
        Trace?.Invoke($"VPA({row})→({_buffer.CursorRow},{_buffer.CursorCol})", _buffer);
        RequestRender();
    }

    public void OnCursorNextLine(int n)
    {
        // CNL - CSI n E - move cursor down n lines, to column 1
        _buffer.MoveCursorBy(Math.Max(1, n), -_buffer.CursorCol);
        Trace?.Invoke($"CNL({n})→({_buffer.CursorRow},{_buffer.CursorCol})", _buffer);
        RequestRender();
    }

    public void OnCursorPreviousLine(int n)
    {
        // CPL - CSI n F - move cursor up n lines, to column 1
        _buffer.MoveCursorBy(-Math.Max(1, n), -_buffer.CursorCol);
        Trace?.Invoke($"CPL({n})→({_buffer.CursorRow},{_buffer.CursorCol})", _buffer);
        RequestRender();
    }

    public void OnScrollUp(int n)
    {
        // SU - CSI n S - scroll up n lines within scroll region
        _buffer.ScrollUpLines(Math.Max(1, n));
        Trace?.Invoke($"SU({n})", _buffer);
        RequestRender();
    }

    public void OnScrollDown(int n)
    {
        // SD - CSI n T - scroll down n lines within scroll region
        _buffer.ScrollDownLines(Math.Max(1, n));
        Trace?.Invoke($"SD({n})", _buffer);
        RequestRender();
    }

    public void OnFullReset()
    {
        // RIS - ESC c - full terminal reset: the DECSTR core plus everything below (title is kept, like xterm).
        _buffer.FullReset();
        ResetKittyGraphics();
        ResetTerminalModesCore();
        _lastPrintedChar = '\0';
        CurrentMouseMode = MouseMode.None;
        CurrentMouseEncoding = MouseEncoding.Default;
        AlternateScrollMode = true;
        _focusReportingEnabled = false;
        OnSetSynchronizedUpdate(false);
        OnSetReverseVideo(false);
        ResetPaletteForRis();
        ResetKittyKeyboardState();
        ResetBackarrowKeyMode();
        ResetVisibilityReports();
        RequestRender();
    }

    /// <summary>
    /// REP (<c>CSI Ps b</c>): repeats the previous printed character <paramref name="n"/> times. The count is
    /// bounded to what the retained window (visible rows + scrollback) can observe, so a hostile
    /// <c>CSI 2147483647 b</c> costs O(window) instead of O(n). For printable ASCII the result is identical to an
    /// unbounded repeat; for other characters (possibly wide or zero-width, so the periodicity argument in the
    /// body does not hold) the count is capped at one window and the final cursor column can differ from an
    /// unbounded repeat when <paramref name="n"/> exceeds it.
    /// </summary>
    public void OnRepeatCharacter(int n)
    {
        // REP - CSI n b - repeat previous character n times
        if (_lastPrintedChar == '\0' || n <= 0) return;

        int columns = Math.Max(1, _buffer.Columns);
        long window = ((long)_buffer.Rows + Math.Max(0, _buffer.MaxScrollback) + 1) * columns;
        // Whole rows only: the ASCII reduction below subtracts multiples of `columns`, so `limit` must be one.
        int limit = (int)(Math.Min(window, int.MaxValue - columns) / columns * columns);

        if (_lastPrintedChar >= 0x20 && _lastPrintedChar <= 0x7E)
        {
            // Every write places the same cell (same char, style, hyperlink). With autowrap on, `columns` further
            // writes advance the cursor exactly one row (scrolling at the bottom) and leave the cursor column and
            // wrap-pending flag unchanged; with autowrap off, every write after the last column is reached lands
            // there. After `limit` = (rows + scrollback + 1) * columns writes every retained row, visible or
            // scrolled, was produced by this REP and is a full row of the repeated char (except the cursor row),
            // so dropping m * columns writes (m whole rows) changes nothing observable: grid, scrollback text,
            // ScrollbackCount (capped at capacity), cursor row/column and wrap-pending all match. Only the
            // internal _totalScrolled counter, which prompt-mark rows are relative to, differs by m.
            if (n > limit)
                n = limit + (n % columns);

            Span<byte> bytes = stackalloc byte[256];
            bytes.Fill((byte)_lastPrintedChar);
            int remaining = n;
            while (remaining > 0)
            {
                int batch = Math.Min(remaining, bytes.Length);
                _buffer.WriteAscii(bytes.Slice(0, batch), _currentAttributes);
                remaining -= batch;
            }
        }
        else
        {
            if (n > limit) n = limit;

            Span<char> chars = stackalloc char[256];
            chars.Fill(_lastPrintedChar);
            int remaining = n;
            while (remaining > 0)
            {
                int batch = Math.Min(remaining, chars.Length);
                _buffer.WriteText(chars.Slice(0, batch), _currentAttributes);
                remaining -= batch;
            }
        }
        RequestRender();
    }

    public void OnTab()
    {
        // HT - horizontal tab
        int nextStop = _buffer.GetNextTabStopFrom(_buffer.CursorCol);
        _buffer.MoveCursorBy(0, nextStop - _buffer.CursorCol);
        Trace?.Invoke($"HT→({_buffer.CursorRow},{_buffer.CursorCol})", _buffer);
        RequestRender();
    }

    public void OnBackTab(int n)
    {
        // CBT - CSI n Z - cursor backward tabulation
        int col = _buffer.CursorCol;
        for (int i = 0; i < Math.Max(1, n); i++)
        {
            col = _buffer.GetPrevTabStopFrom(col);
        }
        _buffer.MoveCursorBy(0, col - _buffer.CursorCol);
        Trace?.Invoke($"CBT({n})→({_buffer.CursorRow},{_buffer.CursorCol})", _buffer);
        RequestRender();
    }
    public void OnSetCursorShape(int shape)
    {
        CursorShape = shape;
        var (cursorShape, blinking) = shape switch
        {
            0 or 1 => (TerminalCursorShape.Block, true),
            2 => (TerminalCursorShape.Block, false),
            3 => (TerminalCursorShape.Underline, true),
            4 => (TerminalCursorShape.Underline, false),
            5 => (TerminalCursorShape.Beam, true),
            6 => (TerminalCursorShape.Beam, false),
            _ => (TerminalCursorShape.Block, true),
        };
        _buffer.SetCursorStyle(cursorShape, blinking);
        RequestRender();
    }

    public void OnSetKeypadApplicationMode(bool enabled)
    {
        KeypadApplicationMode = enabled;
    }

    public void OnSetApplicationCursorKeys(bool enabled)
    {
        ApplicationCursorKeysEnabled = enabled;
    }

    public void OnSendDeviceAttributes(int daType)
    {
        switch (daType)
        {
            case 0:
            case 1:
                // DA1: report the conservative VT100 capability set supported here.
                ReplyRequested?.Invoke("\x1b[?1;0c".AsSpan());
                break;
            case 2:
                ReplyRequested?.Invoke(_da2Response.AsSpan());
                break;
                // DA3 (CSI = c) is not implemented; do not claim an identity/capability.
        }
    }

    /// <summary>XTVERSION (<c>CSI &gt; q</c> / <c>CSI &gt; 0 q</c>): replies <c>DCS &gt; | Dotty &lt;version&gt; ST</c>.</summary>
    public void OnRequestTerminalVersion() => ReplyRequested?.Invoke(_xtVersionReply.AsSpan());

    public void OnMouseEvent(int button, int col, int row, bool isPress)
    {
    }

    /// <summary>
    /// Mouse-related private modes. Tracking modes 9/1000/1002/1003 share one slot, as do the coordinate encodings
    /// 1005/1006/1015/1016 (the last one set wins; resetting the active one restores the default, like xterm);
    /// 1007 is the independent alternate-scroll flag.
    /// </summary>
    public void OnSetMouseMode(int mode, bool enabled)
    {
        if (mode == 1007)
        {
            AlternateScrollMode = enabled;
            return;
        }

        if (mode == 9 || mode == 1000 || mode == 1002 || mode == 1003)
        {
            if (enabled)
            {
                CurrentMouseMode = (MouseMode)mode;
            }
            else if (CurrentMouseMode == (MouseMode)mode)
            {
                CurrentMouseMode = MouseMode.None;
            }
        }
        else if (mode == 1005 || mode == 1006 || mode == 1015 || mode == 1016)
        {
            if (enabled)
            {
                CurrentMouseEncoding = (MouseEncoding)mode;
            }
            else if (CurrentMouseEncoding == (MouseEncoding)mode)
            {
                CurrentMouseEncoding = MouseEncoding.Default;
            }
        }
    }

    /// <summary>
    /// Longest a CSI 2026 hold may withhold presentation, measured from the
    /// BEGIN that opened it. A repeated BEGIN does not extend it, so a producer
    /// that never sends END (or re-sends BEGIN continuously) cannot freeze the
    /// screen. Matches Ghostty's 1 s failsafe with kitty's non-renewing deadline.
    /// </summary>
    public const int SynchronizedUpdateMaxHoldMs = 1000;

    private readonly TimeProvider _timeProvider;
    private bool _renderDirty;
    private volatile bool _synchronizedUpdateActive;
    private long _synchronizedUpdateStartedTimestamp;

    public void OnSetSynchronizedUpdate(bool enabled)
    {
        if (_synchronizedUpdateActive == enabled)
            return;

        // Publish the start before the flag so a reader that observes the
        // flag never pairs it with a previous hold's start.
        if (enabled)
            Volatile.Write(ref _synchronizedUpdateStartedTimestamp, _timeProvider.GetTimestamp());
        _synchronizedUpdateActive = enabled;
        if (!enabled)
            FlushRender();
    }

    public bool SynchronizedUpdateActive => _synchronizedUpdateActive;

    /// <summary>
    /// True while a CSI 2026 hold is active and younger than
    /// <see cref="SynchronizedUpdateMaxHoldMs"/>.
    /// </summary>
    public bool SynchronizedUpdateHolding =>
        _synchronizedUpdateActive &&
        _timeProvider.GetElapsedTime(Volatile.Read(ref _synchronizedUpdateStartedTimestamp)).TotalMilliseconds
            < SynchronizedUpdateMaxHoldMs;

    public double SynchronizedUpdateRemainingMs
    {
        get
        {
            if (!_synchronizedUpdateActive)
                return 0;

            double elapsedMs = _timeProvider.GetElapsedTime(
                Volatile.Read(ref _synchronizedUpdateStartedTimestamp)).TotalMilliseconds;
            return Math.Max(0, SynchronizedUpdateMaxHoldMs - elapsedMs);
        }
    }

    private bool _focusReportingEnabled;

    public void OnSetFocusReporting(bool enabled)
    {
        _focusReportingEnabled = enabled;
    }

    public bool FocusReportingEnabled => _focusReportingEnabled;

    private volatile bool _reverseVideo;

    /// <summary>
    /// DECSCNM (<c>CSI ? 5 h/l</c>): when set, the renderer swaps this terminal's effective default foreground and
    /// background (after OSC 10/11 overrides). Cells with explicit colours keep them. Kept by DECSTR, cleared by RIS.
    /// Written on the parser thread, read by the render thread.
    /// </summary>
    public bool ReverseVideo => _reverseVideo;

    public void OnSetReverseVideo(bool enabled)
    {
        if (_reverseVideo == enabled)
            return;
        _reverseVideo = enabled;
        // The default colours change exactly as for an OSC 10/11 set: repaint every row and wake the host.
        NotifyPaletteChanged();
    }


    public void FlushRender()
    {
        if (SynchronizedUpdateHolding) return;
        if (_renderDirty)
        {
            _renderDirty = false;
            RenderRequested?.Invoke(string.Empty);
        }
    }

    private void RequestRender()
    {
        _renderDirty = true;
    }

    public void RequestRenderExtern()
    {
        RequestRender();
        FlushRender();
    }
    public void Dispose()
    {
        DisposeKittyGraphics();
        _buffer.Dispose();
    }
}
