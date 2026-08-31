using System;

namespace Dotty.Runtime.Input;

/// <summary>
/// Host-neutral modifier flags for terminal key events.
/// </summary>
[Flags]
public enum TerminalKeyModifiers
{
    None = 0,
    Shift = 1,
    Alt = 2,
    Control = 4,
    Meta = 8,
    CapsLock = 16,
    NumLock = 32
}
