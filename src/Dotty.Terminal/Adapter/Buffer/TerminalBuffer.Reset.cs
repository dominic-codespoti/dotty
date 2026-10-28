using Dotty.Abstractions.Config;

namespace Dotty.Terminal.Adapter;

public partial class TerminalBuffer
{
    /// <summary>
    /// DECSTR (<c>CSI ! p</c>) buffer-side state. Resets cursor visibility and shape, margins (without homing the
    /// cursor), origin mode, autowrap and the saved cursor (to home). Leaves screen contents, the live cursor
    /// position, tab stops, bracketed paste and the alternate screen untouched.
    /// </summary>
    public void SoftReset() => ApplySoftResetCore();

    /// <summary>Shared by <see cref="SoftReset"/> and <see cref="FullReset"/> so the two cannot drift.</summary>
    private void ApplySoftResetCore()
    {
        _cursor.SetVisible(true);
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
        _originMode = false;
        _autoWrap = true;
        SetCursorStyle(TerminalCursorShape.Block, blinking: true);
        _hasSavedCursor = true;
        _savedCursorState = new CursorState(0, 0, Visible: true, WrapPending: false);
    }

    /// <summary>RIS extras on top of the DECSTR core: leave alt screen state, drop prompt marks.</summary>
    private void ResetFullOnlyState()
    {
        _promptMarks.Clear();
        _hasAlternateSavedCursor = false;
    }
}
