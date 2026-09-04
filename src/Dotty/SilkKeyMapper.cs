using System;
using SilkKey = Silk.NET.Input.Key;

namespace Dotty.Silk;

using Dotty.Runtime.Input;

/// <summary>
/// Maps Silk.NET input keys and modifiers to host-neutral terminal keys and encodes them.
/// </summary>
public static class SilkKeyMapper
{
    private static readonly TerminalInputEncoder s_encoder = new();

    /// <summary>
    /// Maps Silk.NET Key and modifier flags to <see cref="TerminalKey"/> and <see cref="TerminalKeyModifiers"/>.
    /// </summary>
    public static (TerminalKey Key, TerminalKeyModifiers Modifiers) Map(
        SilkKey key,
        bool ctrl,
        bool shift,
        bool alt,
        bool super = false)
    {
        var modifiers = TerminalKeyModifiers.None;
        if (shift) modifiers |= TerminalKeyModifiers.Shift;
        if (alt) modifiers |= TerminalKeyModifiers.Alt;
        if (ctrl) modifiers |= TerminalKeyModifiers.Control;
        if (super) modifiers |= TerminalKeyModifiers.Meta;

        var terminalKey = key switch
        {
            // Letters
            SilkKey.A => TerminalKey.A,
            SilkKey.B => TerminalKey.B,
            SilkKey.C => TerminalKey.C,
            SilkKey.D => TerminalKey.D,
            SilkKey.E => TerminalKey.E,
            SilkKey.F => TerminalKey.F,
            SilkKey.G => TerminalKey.G,
            SilkKey.H => TerminalKey.H,
            SilkKey.I => TerminalKey.I,
            SilkKey.J => TerminalKey.J,
            SilkKey.K => TerminalKey.K,
            SilkKey.L => TerminalKey.L,
            SilkKey.M => TerminalKey.M,
            SilkKey.N => TerminalKey.N,
            SilkKey.O => TerminalKey.O,
            SilkKey.P => TerminalKey.P,
            SilkKey.Q => TerminalKey.Q,
            SilkKey.R => TerminalKey.R,
            SilkKey.S => TerminalKey.S,
            SilkKey.T => TerminalKey.T,
            SilkKey.U => TerminalKey.U,
            SilkKey.V => TerminalKey.V,
            SilkKey.W => TerminalKey.W,
            SilkKey.X => TerminalKey.X,
            SilkKey.Y => TerminalKey.Y,
            SilkKey.Z => TerminalKey.Z,

            // Numbers
            SilkKey.Number0 or SilkKey.D0 => TerminalKey.Number0,
            SilkKey.Number1 => TerminalKey.Number1,
            SilkKey.Number2 => TerminalKey.Number2,
            SilkKey.Number3 => TerminalKey.Number3,
            SilkKey.Number4 => TerminalKey.Number4,
            SilkKey.Number5 => TerminalKey.Number5,
            SilkKey.Number6 => TerminalKey.Number6,
            SilkKey.Number7 => TerminalKey.Number7,
            SilkKey.Number8 => TerminalKey.Number8,
            SilkKey.Number9 => TerminalKey.Number9,

            // Function keys
            SilkKey.F1 => TerminalKey.F1,
            SilkKey.F2 => TerminalKey.F2,
            SilkKey.F3 => TerminalKey.F3,
            SilkKey.F4 => TerminalKey.F4,
            SilkKey.F5 => TerminalKey.F5,
            SilkKey.F6 => TerminalKey.F6,
            SilkKey.F7 => TerminalKey.F7,
            SilkKey.F8 => TerminalKey.F8,
            SilkKey.F9 => TerminalKey.F9,
            SilkKey.F10 => TerminalKey.F10,
            SilkKey.F11 => TerminalKey.F11,
            SilkKey.F12 => TerminalKey.F12,
            SilkKey.F13 => TerminalKey.F13,
            SilkKey.F14 => TerminalKey.F14,
            SilkKey.F15 => TerminalKey.F15,
            SilkKey.F16 => TerminalKey.F16,
            SilkKey.F17 => TerminalKey.F17,
            SilkKey.F18 => TerminalKey.F18,
            SilkKey.F19 => TerminalKey.F19,
            SilkKey.F20 => TerminalKey.F20,
            SilkKey.F21 => TerminalKey.F21,
            SilkKey.F22 => TerminalKey.F22,
            SilkKey.F23 => TerminalKey.F23,
            SilkKey.F24 => TerminalKey.F24,

            // Navigation
            SilkKey.Up => TerminalKey.Up,
            SilkKey.Down => TerminalKey.Down,
            SilkKey.Left => TerminalKey.Left,
            SilkKey.Right => TerminalKey.Right,
            SilkKey.Home => TerminalKey.Home,
            SilkKey.End => TerminalKey.End,
            SilkKey.PageUp => TerminalKey.PageUp,
            SilkKey.PageDown => TerminalKey.PageDown,
            SilkKey.Insert => TerminalKey.Insert,
            SilkKey.Delete => TerminalKey.Delete,

            // Special
            SilkKey.Enter => TerminalKey.Enter,
            SilkKey.Escape => TerminalKey.Escape,
            SilkKey.Tab => TerminalKey.Tab,
            SilkKey.Backspace => TerminalKey.Backspace,
            SilkKey.Space => TerminalKey.Space,

            // Punctuation / OEM
            SilkKey.Minus => TerminalKey.Minus,
            SilkKey.Equal => TerminalKey.Equal,
            SilkKey.LeftBracket => TerminalKey.LeftBracket,
            SilkKey.RightBracket => TerminalKey.RightBracket,
            SilkKey.BackSlash => TerminalKey.BackSlash,
            SilkKey.Semicolon => TerminalKey.Semicolon,
            SilkKey.Apostrophe => TerminalKey.Quote,
            SilkKey.GraveAccent => TerminalKey.GraveAccent,
            SilkKey.Comma => TerminalKey.Comma,
            SilkKey.Period => TerminalKey.Period,
            SilkKey.Slash => TerminalKey.Slash,

            // Keypad
            SilkKey.Keypad0 => TerminalKey.Keypad0,
            SilkKey.Keypad1 => TerminalKey.Keypad1,
            SilkKey.Keypad2 => TerminalKey.Keypad2,
            SilkKey.Keypad3 => TerminalKey.Keypad3,
            SilkKey.Keypad4 => TerminalKey.Keypad4,
            SilkKey.Keypad5 => TerminalKey.Keypad5,
            SilkKey.Keypad6 => TerminalKey.Keypad6,
            SilkKey.Keypad7 => TerminalKey.Keypad7,
            SilkKey.Keypad8 => TerminalKey.Keypad8,
            SilkKey.Keypad9 => TerminalKey.Keypad9,
            SilkKey.KeypadDecimal => TerminalKey.KeypadDecimal,
            SilkKey.KeypadDivide => TerminalKey.KeypadDivide,
            SilkKey.KeypadMultiply => TerminalKey.KeypadMultiply,
            SilkKey.KeypadSubtract => TerminalKey.KeypadSubtract,
            SilkKey.KeypadAdd => TerminalKey.KeypadAdd,
            SilkKey.KeypadEnter => TerminalKey.KeypadEnter,
            SilkKey.KeypadEqual => TerminalKey.KeypadEqual,

            // Modifiers
            SilkKey.ShiftLeft => TerminalKey.ShiftLeft,
            SilkKey.ShiftRight => TerminalKey.ShiftRight,
            SilkKey.ControlLeft => TerminalKey.ControlLeft,
            SilkKey.ControlRight => TerminalKey.ControlRight,
            SilkKey.AltLeft => TerminalKey.AltLeft,
            SilkKey.AltRight => TerminalKey.AltRight,
            SilkKey.SuperLeft => TerminalKey.SuperLeft,
            SilkKey.SuperRight => TerminalKey.SuperRight,

            // Other
            SilkKey.CapsLock => TerminalKey.CapsLock,
            SilkKey.ScrollLock => TerminalKey.ScrollLock,
            SilkKey.NumLock => TerminalKey.NumLock,
            SilkKey.PrintScreen => TerminalKey.PrintScreen,
            SilkKey.Pause => TerminalKey.Pause,
            SilkKey.Menu => TerminalKey.Menu,

            _ => TerminalKey.Unknown
        };

        return (terminalKey, modifiers);
    }

    /// <summary>
    /// Encodes Silk.NET key input into terminal escape sequences or control bytes.
    /// </summary>
    public static byte[]? Encode(
        SilkKey key,
        bool ctrl,
        bool shift,
        bool alt,
        bool keypadAppMode,
        int kittyMode = 0,
        bool super = false,
        bool applicationCursorKeys = false)
    {
        var (terminalKey, modifiers) = Map(key, ctrl, shift, alt, super);
        if (terminalKey == TerminalKey.Unknown)
            return null;

        var encoder = (kittyMode == 0) ? s_encoder : new TerminalInputEncoder { KittyMode = kittyMode };
        return encoder.Encode(terminalKey, modifiers, keypadAppMode, applicationCursorKeys);
    }
}
