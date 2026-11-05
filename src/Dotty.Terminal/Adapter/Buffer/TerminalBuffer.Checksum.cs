namespace Dotty.Terminal.Adapter;

public partial class TerminalBuffer
{
    /// <summary>
    /// The 0-based inclusive scroll-region bounds that origin mode (DECOM)
    /// makes coordinates relative to. Horizontal bounds require DECLRMM.
    /// </summary>
    internal void GetOriginRegion(out int top, out int bottom, out int left, out int right)
    {
        top = _scrollTop;
        bottom = _scrollBottom;
        left = EffectiveLeftMargin;
        right = EffectiveRightMargin;
    }
}
