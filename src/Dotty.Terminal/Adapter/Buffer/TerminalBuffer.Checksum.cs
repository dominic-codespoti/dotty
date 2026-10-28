namespace Dotty.Terminal.Adapter;

public partial class TerminalBuffer
{
    /// <summary>
    /// The 0-based inclusive top/bottom rows of the DECSTBM scroll region, the area origin mode
    /// (DECOM) makes coordinates relative to.
    /// </summary>
    internal void GetOriginRegionRows(out int top, out int bottom)
    {
        top = _scrollTop;
        bottom = _scrollBottom;
    }
}
