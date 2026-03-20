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
        event Action<string>? ClipboardWriteRequested;
        event Action<string>? TitleChanged;
        event Action<string>? LinkOpened;
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
        void OnSetScrollRegion(int top1Based, int bottom1Based);
        void OnSetOriginMode(bool enabled);
        void OnSetAlternateScreen(bool enabled);
        void OnSetCursorVisibility(bool visible);
        void OnSaveCursor();
        void OnRestoreCursor();
        void OnInsertChars(int n);
        void OnDeleteChars(int n);
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

        // Mouse support
        void OnMouseEvent(int button, int col, int row, bool isPress);
        void OnSetMouseMode(int mode, bool enabled);

        // Synchronized Update
        void OnSetSynchronizedUpdate(bool enabled);

        // Render batching
        void FlushRender();
    }
}
