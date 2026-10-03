namespace Dotty.Runtime.Input;

/// <summary>Physical key event phase reported by the Kitty keyboard protocol.</summary>
public enum TerminalKeyEventType : byte
{
    Press = 1,
    Repeat = 2,
    Release = 3,
}
