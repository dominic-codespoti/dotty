using System;

namespace Dotty.Abstractions.Adapter
{
    /// <summary>
    /// Receiver interface called by the parser when terminal actions occur.
    /// Kept small and span-friendly.
    /// Note: `Buffer` is typed as `object` here to avoid coupling the abstractions
    /// project to a concrete buffer implementation. Consumers may cast to the
    /// concrete `TerminalBuffer` when available.
    /// </summary>
    public interface ITerminalHandler
    {
        object? Buffer { get; }
        event Action<string>? RenderRequested;
        event Action<Dotty.Abstractions.Clipboard.ClipboardWriteRequest>? ClipboardWriteRequested;
        event Action<string>? TitleChanged;
        event Action? PaletteChanged;
        void OnHyperlink(string uri);
        void RequestRenderExtern();
        void ResizeBuffer(int rows, int cols);

        void OnPrint(ReadOnlySpan<char> text);
        void OnEraseDisplay(int mode);
        void OnClearScrollback();
        void OnSetGraphicsRendition(ReadOnlySpan<char> parameters);
        void OnBell();
        void OnOperatingSystemCommand(int code, ReadOnlySpan<char> payload);
        void OnMoveCursor(int row, int col);
        void OnCursorUp(int n);
        void OnCursorDown(int n);
        void OnCursorForward(int n);
        void OnCursorBack(int n);
        void OnEraseLine(int mode);
        void OnCarriageReturn();
        void OnLineFeed();
        void OnIndex() { }                        // IND - ESC D: line feed without carriage return
        void OnNextLine() { }                     // NEL - ESC E: carriage return + index
        void OnSetScrollRegion(int top1Based, int bottom1Based);
        void OnSetOriginMode(bool enabled);
        void OnSetAlternateScreen(bool enabled); // DECSET/DECRST 1049: save/restore the cursor around the switch
        // DECSET/DECRST 47 and 1047: switch screens only, no cursor save/restore (as in xterm).
        void OnSwitchAlternateScreen(bool enabled) { }
        void OnSetCursorVisibility(bool visible);
        void OnSaveCursor();
        void OnRestoreCursor();
        void OnInsertChars(int n);
        void OnDeleteChars(int n);
        void OnEraseCharacters(int n);
        void OnInsertLines(int n);
        void OnDeleteLines(int n);
        void OnSetAutoWrap(bool enabled);
        void OnSetTabStop();
        void OnClearTabStop();
        void OnClearAllTabStops();
        void OnReverseIndex();
        void OnSetBracketedPasteMode(bool enabled);
        void OnDeviceStatusReport(int code); // DSR (e.g., 5=terminal status, 6=CPR)
        void OnCursorPositionReport(); // CPR - request current cursor position

        // Additional cursor movement
        void OnCursorHorizontalAbsolute(int col); // CHA - CSI n G
        void OnCursorVerticalAbsolute(int row);   // VPA - CSI n d
        void OnCursorNextLine(int n);             // CNL - CSI n E
        void OnCursorPreviousLine(int n);         // CPL - CSI n F

        // Explicit scroll commands
        void OnScrollUp(int n);   // SU - CSI n S
        void OnScrollDown(int n); // SD - CSI n T

        // Full reset
        void OnFullReset(); // RIS - ESC c

        // Repeat previous character
        void OnRepeatCharacter(int n); // REP - CSI n b

        // Horizontal tab
        void OnTab();     // HT - move to next tab stop
        void OnBackTab(int n); // CBT - CSI n Z - move back n tab stops

        // Cursor shape
        void OnSetCursorShape(int shape); // DECSCUSR - CSI n SP q
        void OnSetApplicationCursorKeys(bool enabled); // DECCKM - CSI ? 1 h/l
        void OnSetKeypadApplicationMode(bool enabled); // DECKPAM / DECKPNM - ESC = / ESC >

        // Device attributes
        void OnSendDeviceAttributes(int daType); // DA - CSI c / CSI > c
        // DA3 (CSI = c) is intentionally silent; no reply is sent.

        // Mode status (DECRQM - CSI ? Ps $ p / CSI Ps $ p)
        void OnRequestMode(int mode, bool isPrivate) { } // Default no-op for external implementors.

        // Capability query (XTGETTCAP - DCS + q hex ST)
        void OnQueryCapability(string requestHex) { }

        // Span fast path for the same query; default bridges to the string
        // form for external implementors.
        void OnQueryCapability(ReadOnlySpan<char> requestHex) => OnQueryCapability(requestHex.ToString());

        // Kitty graphics protocol (APC G ... ST).  The parser captures the APC
        // only when KittyGraphicsEnabled is true; otherwise the string is
        // discarded without any callback.  Begin receives the control data
        // (bytes between "ESC _ G" and the first ';' or the terminator), Payload
        // receives the base64 bytes in arbitrary slices (0..n calls), and End is
        // called exactly once per Begin: true when terminated by ST/0x9C, false
        // when cancelled or aborted.  Slices are only valid during the call.
        bool KittyGraphicsEnabled => false;
        void OnKittyGraphicsBegin(ReadOnlySpan<byte> control) { }
        void OnKittyGraphicsPayload(ReadOnlySpan<byte> base64) { }
        void OnKittyGraphicsEnd(bool completed) { }

        // XTerm modifyOtherKeys (CSI > 4 ; Pv m)
        void OnSetModifyOtherKeys(int level) { }

        // Mouse support
        void OnMouseEvent(int button, int col, int row, bool isPress);
        void OnSetMouseMode(int mode, bool enabled);

        // Synchronized Update
        void OnSetSynchronizedUpdate(bool enabled);

        // Kitty Keyboard Protocol
        void OnKittyKeyboardCommand(char introducer, int flags, int argument);

        // Focus reporting (DEC 1004)
        void OnSetFocusReporting(bool enabled);

        // DECSCNM reverse video (CSI ? 5 h/l): swaps the default foreground and background
        void OnSetReverseVideo(bool enabled) { }

        // Window manipulation / report (CSI Ps ; Ps ; Ps t). Omitted parameters are 0.
        void OnWindowOperation(int operation, int arg1, int arg2) { }

        // DECSTR soft terminal reset (CSI ! p)
        void OnSoftReset() { }

        // Private device status report (CSI ? Ps n, Ps != 6; Ps == 6 is OnCursorPositionReport)
        void OnPrivateDeviceStatusReport(int code) { }

        // DECBKM (CSI ? 67 h/l): true = Backspace sends BS (0x08), false = DEL (0x7F)
        void OnSetBackarrowKeyMode(bool sendsBackspace) { }

        // Visibility reports (CSI ? 2033 h/l)
        void OnSetVisibilityReports(bool enabled) { }

        // DECRQCRA (CSI Pi ; Pg ; Pt ; Pl ; Pb ; Pr * y); omitted parameters are 0.
        void OnRequestChecksum(int id, int page, int top, int left, int bottom, int right) { }

        // Render batching
        void FlushRender();
    }
}
