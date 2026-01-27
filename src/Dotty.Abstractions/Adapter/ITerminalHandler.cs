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
        void RequestRenderExtern();
        void ResizeBuffer(int rows, int cols);

        void OnPrint(ReadOnlySpan<char> text);
        void OnEraseDisplay(int mode);
        void OnClearScrollback();
        void OnSetGraphicsRendition(ReadOnlySpan<char> parameters);
        void OnBell();
        void OnOperatingSystemCommand(ReadOnlySpan<char> payload);
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
    }
}
