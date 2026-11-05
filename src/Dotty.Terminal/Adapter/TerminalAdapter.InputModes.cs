namespace Dotty.Terminal.Adapter;

public partial class TerminalAdapter
{
    /// <summary>ANSI mode 2 (KAM): ordinary terminal key and text input is locked.</summary>
    public bool KeyboardActionModeLocked { get; private set; }

    /// <summary>ANSI mode 12 (SRM): local echo is enabled when the mode is reset.</summary>
    public bool LocalEchoEnabled { get; private set; }

    /// <summary>Xterm private mode 35: Shift+keypad font-size actions are enabled (xterm default).</summary>
    public bool FontShiftModeEnabled { get; private set; } = true;

    /// <summary>Sets xterm private mode 35 (font shifting), not DEC Hebrew keyboard mode.</summary>
    public void OnSetFontShiftMode(bool enabled) => FontShiftModeEnabled = enabled;
}
