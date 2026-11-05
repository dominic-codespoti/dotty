using System.Threading;

namespace Dotty.Terminal.Adapter;

public partial class TerminalAdapter
{
    /// <summary>
    /// DECSTR (<c>CSI ! p</c>). Resets the xterm soft-reset set (see <see cref="ResetTerminalModesCore"/>) and the
    /// OSC 4 palette. Does not clear the screen, move the cursor, leave the alternate screen, or touch tab stops,
    /// mouse/focus/paste/synchronized-update modes, OSC 10/11/12 colors, the title or Kitty keyboard flags.
    /// </summary>
    public void OnSoftReset()
    {
        ResetTerminalModesCore();
        ResetPaletteOverrides();
        RequestRender();
    }

    /// <summary>
    /// State shared by DECSTR and RIS: buffer soft state, SGR attributes (dropping any OSC 8 link), saved
    /// attributes (home/default), cursor-key/keypad/modifyOtherKeys modes and the DECSCUSR shape.
    /// </summary>
    private void ResetTerminalModesCore()
    {
        ResetSmoothScrollAnimation();
        _buffer.SoftReset();
        ResetColumnModes();
        _currentAttributes = CellAttributes.Default;
        SaveAttributesForActiveScreen();
        KeyboardActionModeLocked = false;
        ApplicationCursorKeysEnabled = false;
        KeypadApplicationMode = false;
        ModifyOtherKeysLevel = 0;
        CursorShape = 0;
    }

    private void ResetPaletteOverrides()
    {
        lock (_paletteSync)
        {
            bool paletteChanged = Volatile.Read(ref _palette).ResetAll();
            bool specialChanged = ResetAllSpecialColors();
            if (paletteChanged) RemapPaletteStyles();
            if (paletteChanged || specialChanged) NotifyPaletteChanged();
        }
    }
}
