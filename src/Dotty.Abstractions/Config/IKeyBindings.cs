namespace Dotty.Abstractions.Config;

/// <summary>
/// Key bindings configuration for the terminal.
/// Maps key combinations to terminal actions.
/// </summary>
public interface IKeyBindings
{
    /// <summary>
    /// Get the action for a specific key and modifier combination.
    /// Returns null if no action is bound to this key.
    /// </summary>
    TerminalAction? GetAction(Key key, KeyModifiers modifiers);
}

/// <summary>
/// Represents a keyboard key.
/// </summary>
public enum Key
{
    None,
    A, B, C, D, E, F, G, H, I, J, K, L, M,
    N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    NumPad0, NumPad1, NumPad2, NumPad3, NumPad4,
    NumPad5, NumPad6, NumPad7, NumPad8, NumPad9,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    Tab, Enter, Escape, Space, Back, Delete,
    Insert, Home, End, PageUp, PageDown,
    Up, Down, Left, Right,
    OemOpenBrackets, OemCloseBrackets, OemBackslash, OemTilde, OemMinus,
    Add, Subtract, Multiply, Divide, Decimal,
    Comma, Period,
    D0, D1, D2, D3, D4, D5, D6, D7, D8, D9,
}

/// <summary>
/// Key modifier flags.
/// </summary>
[System.Flags]
public enum KeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Meta = 8,
}

/// <summary>
/// Represents a single key binding entry.
/// </summary>
public readonly record struct KeyBinding(Key Key, KeyModifiers Modifiers, TerminalAction Action);
