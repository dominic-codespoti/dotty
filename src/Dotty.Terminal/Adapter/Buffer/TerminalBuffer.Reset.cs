namespace Dotty.Terminal.Adapter;

public partial class TerminalBuffer
{
    /// <summary>
    /// DECSTR (<c>CSI ! p</c>) buffer-side state. Resets cursor visibility and shape, margins (without homing the
    /// cursor), origin mode, autowrap, reverse-wrap modes and the saved cursor (to home). Leaves screen contents,
    /// the live cursor position, tab stops, bracketed paste and the alternate screen untouched.
    /// </summary>
    public void SoftReset() => ApplySoftResetCore();

    /// <summary>Shared by <see cref="SoftReset"/> and <see cref="FullReset"/> so the two cannot drift.</summary>
    private void ApplySoftResetCore()
    {
        _cursor.SetVisible(true);
        ResetCursorStyle();
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
        _leftRightMarginMode = false;
        _leftMargin = 0;
        _rightMargin = Columns - 1;
        _originMode = false;
        _autoWrap = true;
        InsertMode = false;
        NewLineMode = false;
        _reverseWraparound = false;
        _extendedReverseWraparound = false;
        _protectionActive = false;
        _respectIsoProtection = false;
        SaveCursorAtHome();
    }

    /// <summary>RIS extras on top of the DECSTR core: leave alt screen state, drop prompt marks.</summary>
    private void ResetFullOnlyState()
    {
        _promptMarks.Clear();
        _hasMainSavedCursor = false;
        _hasAlternateSavedCursor = false;
    }

    private void SaveCursorAtHome()
    {
        var home = new SavedCursor(new CursorState(0, 0, Visible: true, WrapPending: false), false, false);
        if (_isAlternate)
        {
            _alternateSavedCursor = home;
            _hasAlternateSavedCursor = true;
        }
        else
        {
            _mainSavedCursor = home;
            _hasMainSavedCursor = true;
        }
    }

}
