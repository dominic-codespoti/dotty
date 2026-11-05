using System;
using System.Threading;

namespace Dotty.Terminal.Adapter;

// Window manipulation (CSI Ps ; Ps ; Ps t) and the reports it requests.
//
// The adapter knows only what the host tells it: cell pixel size, optional
// text-area / screen pixel overrides and the window position. Until the host
// pushes something the reports are deterministic: the text area is the grid
// times the cell size, the screen equals the text area and the position is 0;0.
public partial class TerminalAdapter
{
    /// <summary>Largest row/column count a single CSI 8 t / CSI Ps t request may ask for.</summary>
    internal const int MaxWindowResizeCells = 1000;

    private const int DefaultCellPixelWidth = 8;
    private const int DefaultCellPixelHeight = 16;

    // Pairs are packed into one long so a reader never sees a torn width/height.
    // 0 = not provided by the host (fall back to values derived from the grid).
    private long _windowPixelSize;
    private long _screenPixelSize;
    private long _windowPosition;
    private long _cellPixelSize = PackPair(DefaultCellPixelWidth, DefaultCellPixelHeight);

    /// <summary>
    /// Raised synchronously on the parser thread (under the buffer lock) for
    /// <c>CSI 8 ; rows ; cols t</c>, <c>CSI Ps t</c> with Ps &gt;= 24, and mode-40-authorized DECCOLM.
    /// Arguments are (rows, cols), each clamped to [0, 1000]; 0 keeps the current dimension and
    /// the event is never raised when both are 0. GUI handlers must only enqueue the request;
    /// whether it is honoured is the host's decision (see <c>vt.windowResizeAllowed</c>).
    /// </summary>
    public event Action<int, int>? WindowResizeRequested;

    /// <summary>Raised for native window operations and position queries; the host applies permissions and reports actual state.</summary>
    public event Action<TerminalAdapter, int, int, int>? NativeWindowOperationRequested;

    /// <summary>
    /// Overrides the text-area pixel size reported by CSI 14 t (width, height). Without
    /// an override it is the grid size times the cell pixel size.
    /// </summary>
    public void SetWindowPixelSize(int width, int height) =>
        Volatile.Write(ref _windowPixelSize, PackPair(Math.Max(1, width), Math.Max(1, height)));

    /// <summary>Sets the character cell size in pixels reported by CSI 16 t (default 8x16).</summary>
    public void SetCellPixelSize(int width, int height)
    {
        int w = Math.Max(1, width), h = Math.Max(1, height);
        long packed = PackPair(w, h);
        lock (_buffer.SyncRoot)
        {
            long previous = Interlocked.Exchange(ref _cellPixelSize, packed);
            // Kitty placements sized by the cell (native size, one dimension, in-cell offsets) follow the new size.
            if (previous != packed)
                _buffer.OnCellPixelSizeChanged(w, h);
        }
    }

    /// <summary>Sets the screen pixel size reported by CSI 15 t; without it the text area size is reported.</summary>
    public void SetScreenPixelSize(int width, int height) =>
        Volatile.Write(ref _screenPixelSize, PackPair(Math.Max(1, width), Math.Max(1, height)));

    /// <summary>Sets the window position reported by CSI 13 t; (0, 0) when the host cannot know it.</summary>
    public void SetWindowPosition(int x, int y) =>
        Volatile.Write(ref _windowPosition, PackPair(x, y));

    public void OnWindowOperation(int operation, int arg1, int arg2)
    {
        switch (operation)
        {
            case 8:
                RequestWindowResize(arg1, arg2);
                break;
            case 1:
            case 2:
            case 3:
                NativeWindowOperationRequested?.Invoke(this, operation, arg1, arg2);
                break;
            case 11:
                NativeWindowOperationRequested?.Invoke(this, operation, arg1, arg2);
                break;
            case 13:
                if (NativeWindowOperationRequested is { } positionRequest)
                    positionRequest(this, operation, arg1, arg2);
                else
                    ReportWindowPosition();
                break;
            case 14:
                {
                    // CSI 14 t → text area size in pixels: CSI 4 ; height ; width t
                    GetTextAreaPixels(out int width, out int height);
                    SendTwoNumberReply("\x1b[4;".AsSpan(), height, width, 't');
                    break;
                }
            case 15:
                {
                    // CSI 15 t → screen size in pixels: CSI 5 ; height ; width t
                    long screen = Volatile.Read(ref _screenPixelSize);
                    int width, height;
                    if (screen != 0)
                        UnpackPair(screen, out width, out height);
                    else
                        GetTextAreaPixels(out width, out height);
                    SendTwoNumberReply("\x1b[5;".AsSpan(), height, width, 't');
                    break;
                }
            case 16:
                {
                    // CSI 16 t → character cell size in pixels: CSI 6 ; height ; width t
                    UnpackPair(Volatile.Read(ref _cellPixelSize), out int cellWidth, out int cellHeight);
                    SendTwoNumberReply("\x1b[6;".AsSpan(), cellHeight, cellWidth, 't');
                    break;
                }
            case 18:
                // CSI 18 t → text area size in characters: CSI 8 ; rows ; cols t
                SendTwoNumberReply("\x1b[8;".AsSpan(), _buffer.Rows, _buffer.Columns, 't');
                break;
            case 19:
                // CSI 19 t → screen size in characters; Dotty has one terminal grid.
                SendTwoNumberReply("\x1b[9;".AsSpan(), _buffer.Rows, _buffer.Columns, 't');
                break;
            case 20:
                ReplyTitle(icon: true);
                break;
            case 21:
                ReplyTitle(icon: false);
                break;
            case 22:
                OnPushTitle(arg1, arg2);
                break;
            case 23:
                OnPopTitle(arg1, arg2);
                break;
            default:
                // CSI Ps t with Ps >= 24 (DECSLPP) resizes to Ps lines, columns unchanged.
                if (operation >= 24)
                    RequestWindowResize(operation, 0);
                break;
        }
    }
    /// <summary>Reports the position pushed from the native window by the host.</summary>
    public void ReportWindowPosition()
    {
        UnpackPair(Volatile.Read(ref _windowPosition), out int x, out int y);
        SendTwoNumberReply("\x1b[3;".AsSpan(), x, y, 't');
    }
    /// <summary>Reports the host-observed native iconified state.</summary>
    public void ReportWindowState(bool iconified) =>
        SendNumberReply("\x1b[".AsSpan(), iconified ? 2 : 1, 't');


    private void RequestWindowResize(int rows, int cols)
    {
        rows = Math.Clamp(rows, 0, MaxWindowResizeCells);
        cols = Math.Clamp(cols, 0, MaxWindowResizeCells);
        if (rows == 0 && cols == 0)
            return;
        WindowResizeRequested?.Invoke(rows, cols);
    }

    private void GetTextAreaPixels(out int width, out int height)
    {
        long window = Volatile.Read(ref _windowPixelSize);
        if (window != 0)
        {
            UnpackPair(window, out width, out height);
            return;
        }

        UnpackPair(Volatile.Read(ref _cellPixelSize), out int cellWidth, out int cellHeight);
        width = (int)Math.Min(int.MaxValue, (long)_buffer.Columns * cellWidth);
        height = (int)Math.Min(int.MaxValue, (long)_buffer.Rows * cellHeight);
    }

    private static long PackPair(int first, int second) => ((long)first << 32) | (uint)second;

    private static void UnpackPair(long packed, out int first, out int second)
    {
        first = (int)(packed >> 32);
        second = (int)packed;
    }
}
