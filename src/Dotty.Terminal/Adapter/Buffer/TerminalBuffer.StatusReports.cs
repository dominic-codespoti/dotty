namespace Dotty.Terminal.Adapter;

public partial class TerminalBuffer
{
    /// <summary>
    /// Zero-based cursor row as reported by CPR/DECXCPR: relative to the top margin while
    /// origin mode (DECOM) is set, absolute otherwise.
    /// </summary>
    public int CursorRowForReport => _originMode ? _cursor.Row - _scrollTop : _cursor.Row;
}
