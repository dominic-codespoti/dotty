using System;
using System.Text;

namespace Dotty.Runtime.Input;

/// <summary>
/// Encodes terminal keyboard events into byte sequences following standard xterm legacy
/// sequences and the Kitty keyboard protocol. Mouse reports are <see cref="MouseEncoder"/>'s.
/// </summary>
public class TerminalInputEncoder
{
    /// <summary>Encodes a negotiated Kitty keyboard key event, falling back to legacy encoding when unselected.</summary>
    public int EncodeKeyEvent(
        TerminalKey key,
        TerminalKeyModifiers modifiers,
        int primaryCodepoint,
        TerminalKeyEventType eventType,
        ReadOnlySpan<char> associatedText,
        Span<byte> destination,
        int kittyFlags,
        bool keypadApplicationMode = false,
        bool applicationCursorKeys = false,
        int shiftedCodepoint = 0,
        int baseCodepoint = 0,
        bool backarrowSendsBackspace = false)
    {
        // Section references are to the kitty keyboard protocol specification
        // (https://sw.kovidgoyal.net/kitty/keyboard-protocol/).
        bool reportAllKeys = (kittyFlags & 8) != 0;
        bool disambiguate = (kittyFlags & 1) != 0;
        // K6(b), "Report all keys as escape codes": only this enhancement reports modifier (and lock) key events.
        if (!reportAllKeys && IsModifierOrLockKey(key))
            return 0;
        // K6(a), "Report event types": Enter, Tab and Backspace get no release events without report-all-keys.
        if (!reportAllKeys && eventType == TerminalKeyEventType.Release &&
            key is TerminalKey.Enter or TerminalKey.Tab or TerminalKey.Backspace)
            return 0;

        TerminalKeyModifiers active = modifiers &
            (TerminalKeyModifiers.Shift | TerminalKeyModifiers.Alt | TerminalKeyModifiers.Control | TerminalKeyModifiers.Meta);
        bool reportText = (kittyFlags & 16) != 0 && eventType != TerminalKeyEventType.Release &&
            ContainsReportableText(associatedText);
        bool unknownTextEvent = key == TerminalKey.Unknown && primaryCodepoint == 0 &&
            (kittyFlags & (8 | 16)) == (8 | 16) && eventType != TerminalKeyEventType.Release && reportText;

        // "Functional key definitions": keys with a legacy form keep it (CSI 1;m A, CSI 2;m ~, ...).
        if (!unknownTextEvent && TryGetFunctionalForm(key, out int functionalNumber, out byte functionalFinal))
        {
            if (!reportAllKeys && !disambiguate)
                return eventType == TerminalKeyEventType.Release ? 0 : Encode(key, modifiers, destination,
                    keypadApplicationMode, applicationCursorKeys, modifyOtherKeysLevel: 0,
                    backarrowSendsBackspace: backarrowSendsBackspace);
            return WriteFunctionalKey(destination, functionalNumber, functionalFinal, GetModifier(modifiers),
                eventType, kittyFlags);
        }

        bool keypadMapped = TryGetKeypadCode(key, out int codepoint);
        bool mapped = keypadMapped || TryGetKittyCodepoint(key, out codepoint);
        if (primaryCodepoint > 0 && !keypadMapped)
            codepoint = primaryCodepoint;
        else if (!mapped && !unknownTextEvent)
            return eventType == TerminalKeyEventType.Release ? 0 : Encode(key, modifiers, destination,
                keypadApplicationMode, applicationCursorKeys, modifyOtherKeysLevel: 0,
                backarrowSendsBackspace: backarrowSendsBackspace);

        // "Disambiguate escape codes": Esc, alt/ctrl/ctrl+alt/shift+alt + key and non-text keypad keys become
        // CSI u; Enter, Tab and Backspace keep their legacy bytes unless modified (K6(a)); text keys with no
        // modifier other than Shift and the lock keys (K6(c)) stay text.
        bool textKey = !unknownTextEvent && IsTextProducingKey(key, codepoint);
        bool selected = unknownTextEvent || reportAllKeys || (disambiguate && (
            textKey ? (active & ~TerminalKeyModifiers.Shift) != 0
            : key is TerminalKey.Enter or TerminalKey.Tab or TerminalKey.Backspace ? active != TerminalKeyModifiers.None
            : true));
        if (!selected)
        {
            if (eventType == TerminalKeyEventType.Release || (textKey && keypadMapped))
                return 0;
            return Encode(key, modifiers, destination, keypadApplicationMode, applicationCursorKeys,
                modifyOtherKeysLevel: 0, backarrowSendsBackspace: backarrowSendsBackspace);
        }

        // K6(c), "Disambiguate escape codes": lock modifiers are not reported for text-producing keys unless
        // report-all-keys is set.
        int modifier = GetModifier(textKey && !reportAllKeys ? active : modifiers);
        // K6(d), "Key codes": the shifted key is present whenever (and only when) Shift is in the modifiers.
        int shifted = (active & TerminalKeyModifiers.Shift) != 0 && shiftedCodepoint != codepoint ? shiftedCodepoint : 0;

        int offset = WriteLiteral(destination, "\x1b[");
        offset = WriteNumber(destination, offset, codepoint);
        if (!unknownTextEvent && (kittyFlags & 4) != 0 && (shifted > 0 || baseCodepoint > 0))
        {
            destination[offset++] = (byte)':';
            if (shifted > 0)
                offset = WriteNumber(destination, offset, shifted);
            if (baseCodepoint > 0)
            {
                destination[offset++] = (byte)':';
                offset = WriteNumber(destination, offset, baseCodepoint);
            }
        }
        destination[offset++] = (byte)';';
        if (unknownTextEvent)
        {
            destination[offset++] = (byte)';';
        }
        else
        {
            offset = WriteNumber(destination, offset, modifier);
            if ((kittyFlags & 2) != 0)
            {
                destination[offset++] = (byte)':';
                offset = WriteNumber(destination, offset, (int)eventType);
            }
            if (reportText)
                destination[offset++] = (byte)';';
        }
        if (reportText)
        {
            int index = 0;
            bool wroteScalar = false;
            while (index < associatedText.Length)
            {
                int scalar = ReadScalar(associatedText, ref index);
                if (IsControlScalar(scalar))
                    continue;
                if (wroteScalar)
                    destination[offset++] = (byte)':';
                offset = WriteNumber(destination, offset, scalar);
                wroteScalar = true;
            }
        }
        destination[offset++] = (byte)'u';
        return offset;
    }

    // CSI number ; modifier[:event] final. The number 1 of the letter forms and the whole parameter list are
    // omitted when there are no modifiers and no event type to report.
    private static int WriteFunctionalKey(Span<byte> destination, int number, byte final, int modifier,
        TerminalKeyEventType eventType, int kittyFlags)
    {
        int offset = WriteLiteral(destination, "\x1b[");
        bool reportEvent = (kittyFlags & 2) != 0;
        if (modifier != 1 || reportEvent)
        {
            offset = WriteNumber(destination, offset, number);
            destination[offset++] = (byte)';';
            offset = WriteNumber(destination, offset, modifier);
            if (reportEvent)
            {
                destination[offset++] = (byte)':';
                offset = WriteNumber(destination, offset, (int)eventType);
            }
        }
        else if (final == (byte)'~')
        {
            offset = WriteNumber(destination, offset, number);
        }
        destination[offset++] = final;
        return offset;
    }

    // Spec "Functional key definitions": keys whose escape code is CSI 1 letter or CSI number ~.
    private static bool TryGetFunctionalForm(TerminalKey key, out int number, out byte final)
    {
        (number, final) = key switch
        {
            TerminalKey.Insert => (2, (byte)'~'),
            TerminalKey.Delete => (3, (byte)'~'),
            TerminalKey.PageUp => (5, (byte)'~'),
            TerminalKey.PageDown => (6, (byte)'~'),
            TerminalKey.Up => (1, (byte)'A'),
            TerminalKey.Down => (1, (byte)'B'),
            TerminalKey.Right => (1, (byte)'C'),
            TerminalKey.Left => (1, (byte)'D'),
            TerminalKey.KeypadBegin => (1, (byte)'E'),
            TerminalKey.Home => (1, (byte)'H'),
            TerminalKey.End => (1, (byte)'F'),
            TerminalKey.F1 => (1, (byte)'P'),
            TerminalKey.F2 => (1, (byte)'Q'),
            TerminalKey.F3 => (13, (byte)'~'),
            TerminalKey.F4 => (1, (byte)'S'),
            TerminalKey.F5 => (15, (byte)'~'),
            TerminalKey.F6 => (17, (byte)'~'),
            TerminalKey.F7 => (18, (byte)'~'),
            TerminalKey.F8 => (19, (byte)'~'),
            TerminalKey.F9 => (20, (byte)'~'),
            TerminalKey.F10 => (21, (byte)'~'),
            TerminalKey.F11 => (23, (byte)'~'),
            TerminalKey.F12 => (24, (byte)'~'),
            _ => (0, (byte)0)
        };
        return number != 0;
    }

    private static bool IsModifierOrLockKey(TerminalKey key) => key is
        TerminalKey.ShiftLeft or TerminalKey.ShiftRight or TerminalKey.ControlLeft or TerminalKey.ControlRight or
        TerminalKey.AltLeft or TerminalKey.AltRight or TerminalKey.SuperLeft or TerminalKey.SuperRight or
        TerminalKey.CapsLock or TerminalKey.NumLock or TerminalKey.ScrollLock;

    // Keys whose plain press produces text: printable codepoints outside the private-use functional range and the
    // keypad keys that type characters. Esc, Enter, Tab, Backspace and the keypad Enter/navigation keys do not.
    private static bool IsTextProducingKey(TerminalKey key, int codepoint)
    {
        if (key is >= TerminalKey.Keypad0 and <= TerminalKey.KeypadEqual)
            return key != TerminalKey.KeypadEnter;
        if (key is >= TerminalKey.KeypadHome and <= TerminalKey.KeypadDelete)
            return false;
        return codepoint >= 32 && codepoint != 127 && codepoint is < 57344 or > 63743;
    }

    private static bool ContainsReportableText(ReadOnlySpan<char> text)
    {
        int index = 0;
        while (index < text.Length)
        {
            if (!IsControlScalar(ReadScalar(text, ref index)))
                return true;
        }
        return false;
    }

    private static bool IsControlScalar(int scalar) => scalar <= 0x1F || scalar is >= 0x7F and <= 0x9F;

    /// <summary>Writes a key sequence into caller-owned storage. Returns zero for unsupported keys.</summary>
    public int Encode(
        TerminalKey key,
        TerminalKeyModifiers modifiers,
        Span<byte> destination,
        bool keypadApplicationMode = false,
        bool applicationCursorKeys = false,
        int modifyOtherKeysLevel = 0,
        bool backarrowSendsBackspace = false)
    {
        // Lock state never changes a legacy encoding (kitty spec, "Legacy functional keys").
        modifiers &= TerminalKeyModifiers.Shift | TerminalKeyModifiers.Alt | TerminalKeyModifiers.Control | TerminalKeyModifiers.Meta;
        if (TryEncodeKeypad(key, modifiers, keypadApplicationMode, destination, out int keypadLength))
            return keypadLength;
        key = KeypadNavigationTarget(key);

        bool ctrl = (modifiers & TerminalKeyModifiers.Control) != 0;
        bool alt = (modifiers & TerminalKeyModifiers.Alt) != 0;
        bool shift = (modifiers & TerminalKeyModifiers.Shift) != 0;
        int mod = GetModifier(modifiers);

        if (key == TerminalKey.Backspace)
        {
            if (ctrl && alt) return WriteLiteral(destination, "\x1b\x17");
            if (ctrl) return WriteLiteral(destination, "\x17");
            // DECBKM (mode 67) only swaps the plain/Alt forms; Ctrl+Backspace stays word-erase.
            if (alt) return WriteLiteral(destination, backarrowSendsBackspace ? "\x1b\x08" : "\x1b\x7f");
            return WriteLiteral(destination, backarrowSendsBackspace ? "\x08" : "\x7f");
        }
        if (key == TerminalKey.Tab)
        {
            if (shift) return WriteLiteral(destination, alt ? "\x1b\x1b[Z" : "\x1b[Z");
            if (alt) return WriteLiteral(destination, "\x1b\x09");
            return WriteLiteral(destination, "\x09");
        }
        if (key == TerminalKey.Enter) return alt ? WriteLiteral(destination, "\x1b\x0d") : WriteLiteral(destination, "\x0d");
        if (key == TerminalKey.Escape) return alt ? WriteLiteral(destination, "\x1b\x1b") : WriteLiteral(destination, "\x1b");

        if (TryGetLegacyEditingKey(key, out byte final, out int code))
        {
            if (final != 0)
            {
                // Cursor keys, Home/End and keypad Begin: SS3 in DECCKM when unmodified, CSI 1;m otherwise.
                if (mod > 1)
                {
                    int offset = WriteLiteral(destination, "\x1b[1;");
                    offset = WriteNumber(destination, offset, mod);
                    destination[offset++] = final;
                    return offset;
                }
                destination[0] = 0x1b;
                destination[1] = applicationCursorKeys ? (byte)'O' : (byte)'[';
                destination[2] = final;
                return 3;
            }

            int tildeOffset = WriteLiteral(destination, "\x1b[");
            tildeOffset = WriteNumber(destination, tildeOffset, code);
            if (mod > 1)
            {
                destination[tildeOffset++] = (byte)';';
                tildeOffset = WriteNumber(destination, tildeOffset, mod);
            }
            destination[tildeOffset++] = (byte)'~';
            return tildeOffset;
        }

        if ((key >= TerminalKey.F1 && key <= TerminalKey.F24) || key == TerminalKey.F25)
        {
            int fNum = key == TerminalKey.F25 ? 25 : (int)(key - TerminalKey.F1) + 1;
            if (fNum <= 4)
            {
                if (mod > 1)
                {
                    int offset = WriteLiteral(destination, "\x1b[1;");
                    offset = WriteNumber(destination, offset, mod);
                    destination[offset++] = (byte)('P' + fNum - 1);
                    return offset;
                }
                destination[0] = 0x1b; destination[1] = (byte)'O'; destination[2] = (byte)('P' + fNum - 1);
                return 3;
            }
            int functionCode = FunctionCode(fNum);
            if (functionCode > 0)
            {
                int offset = WriteLiteral(destination, "\x1b[");
                offset = WriteNumber(destination, offset, functionCode);
                if (mod > 1)
                {
                    destination[offset++] = (byte)';';
                    offset = WriteNumber(destination, offset, mod);
                }
                destination[offset++] = (byte)'~';
                return offset;
            }
        }
        // At level 2 xterm reports every modified ordinary key, even when the
        // key has a legacy encoding. Shifted letters use uppercase; shifted
        // punctuation and digits use the unshifted base key because this API
        // receives TerminalKey rather than the produced character.
        if (modifyOtherKeysLevel == 2 && (ctrl || alt))
        {
            int codepoint = ModifyOtherKeysCodepoint(key, shift);
            if (codepoint > 0)
                return WriteModifyOtherKeys(destination, modifiers, codepoint);
        }

        if (ctrl)
        {
            int control = LegacyControlByte(key);
            if (control >= 0)
            {
                int offset = 0;
                if (alt)
                    destination[offset++] = 0x1b;
                destination[offset++] = (byte)control;
                return offset;
            }
            return alt ? 0 : EncodeModifyOtherKeysFallback(key, modifiers, modifyOtherKeysLevel, destination);
        }

        if (alt)
        {
            // US-layout fallback for the physical key; the dispatcher prefers the layout's character.
            char altChar = UsLayoutCharacter(key, shift);
            if (altChar != '\0')
            {
                destination[0] = 0x1b;
                destination[1] = (byte)altChar;
                return 2;
            }
        }
        return 0;
    }

    private static TerminalKey KeypadNavigationTarget(TerminalKey key) => key switch
    {
        TerminalKey.KeypadHome => TerminalKey.Home,
        TerminalKey.KeypadUp => TerminalKey.Up,
        TerminalKey.KeypadPageUp => TerminalKey.PageUp,
        TerminalKey.KeypadLeft => TerminalKey.Left,
        TerminalKey.KeypadRight => TerminalKey.Right,
        TerminalKey.KeypadEnd => TerminalKey.End,
        TerminalKey.KeypadDown => TerminalKey.Down,
        TerminalKey.KeypadPageDown => TerminalKey.PageDown,
        TerminalKey.KeypadInsert => TerminalKey.Insert,
        TerminalKey.KeypadDelete => TerminalKey.Delete,
        _ => key,
    };

    // Editing/cursor keys: either a final letter (CSI/SS3 letter forms) or a CSI number ~ code.
    private static bool TryGetLegacyEditingKey(TerminalKey key, out byte final, out int code)
    {
        final = key switch
        {
            TerminalKey.Up => (byte)'A',
            TerminalKey.Down => (byte)'B',
            TerminalKey.Right => (byte)'C',
            TerminalKey.Left => (byte)'D',
            TerminalKey.KeypadBegin => (byte)'E',
            TerminalKey.Home => (byte)'H',
            TerminalKey.End => (byte)'F',
            _ => 0
        };
        code = key switch
        {
            TerminalKey.Insert => 2,
            TerminalKey.Delete => 3,
            TerminalKey.PageUp => 5,
            TerminalKey.PageDown => 6,
            _ => 0
        };
        return final != 0 || code != 0;
    }

    // Legacy Ctrl mapping of ASCII keys (kitty spec table "Legacy ctrl mapping of ASCII keys"; VT100 Table 3-5),
    // by US key position. Returns -1 for keys Ctrl leaves untouched.
    private static int LegacyControlByte(TerminalKey key)
    {
        if (key >= TerminalKey.A && key <= TerminalKey.Z)
            return (key - TerminalKey.A) + 1;
        return key switch
        {
            TerminalKey.Space or TerminalKey.Number2 => 0x00,
            TerminalKey.LeftBracket or TerminalKey.Number3 => 0x1b,
            TerminalKey.BackSlash or TerminalKey.Number4 => 0x1c,
            TerminalKey.RightBracket or TerminalKey.Number5 => 0x1d,
            TerminalKey.GraveAccent or TerminalKey.Number6 => 0x1e,
            TerminalKey.Minus or TerminalKey.Slash or TerminalKey.Number7 => 0x1f,
            TerminalKey.Number8 => 0x7f,
            _ => -1
        };
    }

    private static char UsLayoutCharacter(TerminalKey key, bool shift)
    {
        if (key >= TerminalKey.A && key <= TerminalKey.Z)
            return (char)((shift ? 'A' : 'a') + (key - TerminalKey.A));
        if (key >= TerminalKey.Number0 && key <= TerminalKey.Number9)
            return shift ? ")!@#$%^&*("[key - TerminalKey.Number0] : (char)('0' + (key - TerminalKey.Number0));
        return key switch
        {
            TerminalKey.Space => ' ',
            TerminalKey.Minus => shift ? '_' : '-',
            TerminalKey.Equal => shift ? '+' : '=',
            TerminalKey.LeftBracket => shift ? '{' : '[',
            TerminalKey.RightBracket => shift ? '}' : ']',
            TerminalKey.BackSlash => shift ? '|' : '\\',
            TerminalKey.Semicolon => shift ? ':' : ';',
            TerminalKey.Quote => shift ? '"' : '\'',
            TerminalKey.GraveAccent => shift ? '~' : '`',
            TerminalKey.Comma => shift ? '<' : ',',
            TerminalKey.Period => shift ? '>' : '.',
            TerminalKey.Slash => shift ? '?' : '/',
            _ => '\0'
        };
    }

    private static int WriteLiteral(Span<byte> destination, ReadOnlySpan<char> text) =>
        Encoding.ASCII.GetBytes(text, destination);

    // Level 1 applies only when Ctrl has no legacy control encoding.
    private static int EncodeModifyOtherKeysFallback(
        TerminalKey key,
        TerminalKeyModifiers modifiers,
        int modifyOtherKeysLevel,
        Span<byte> destination)
    {
        if (modifyOtherKeysLevel != 1)
            return 0;
        if ((modifiers & TerminalKeyModifiers.Control) == 0)
            return 0;
        int codepoint = ModifyOtherKeysCodepoint(key, shifted: false);
        if (codepoint <= 0)
            return 0;
        return WriteModifyOtherKeys(destination, modifiers, codepoint);
    }

    private static int WriteModifyOtherKeys(Span<byte> destination, TerminalKeyModifiers modifiers, int codepoint)
    {
        int mod = GetModifier(modifiers & (TerminalKeyModifiers.Shift | TerminalKeyModifiers.Alt | TerminalKeyModifiers.Control));
        int offset = WriteLiteral(destination, "\x1b[27;");
        offset = WriteNumber(destination, offset, mod);
        destination[offset++] = (byte)';';
        offset = WriteNumber(destination, offset, codepoint);
        destination[offset++] = (byte)'~';
        return offset;
    }

    private static int ModifyOtherKeysCodepoint(TerminalKey key, bool shifted)
    {
        if (key >= TerminalKey.A && key <= TerminalKey.Z)
            return (shifted ? 'A' : 'a') + (key - TerminalKey.A);
        if (key >= TerminalKey.Number0 && key <= TerminalKey.Number9)
            return '0' + (key - TerminalKey.Number0);
        return key switch
        {
            TerminalKey.Space => 32,
            TerminalKey.Minus => '-',
            TerminalKey.Equal => '=',
            TerminalKey.LeftBracket => '[',
            TerminalKey.RightBracket => ']',
            TerminalKey.BackSlash => '\\',
            TerminalKey.Semicolon => ';',
            TerminalKey.Quote => '\'',
            TerminalKey.GraveAccent => 96,
            TerminalKey.Comma => ',',
            TerminalKey.Period => '.',
            TerminalKey.Slash => '/',
            _ => 0,
        };
    }

    private static bool TryGetKeypadCode(TerminalKey key, out int codepoint)
    {
        codepoint = key switch
        {
            TerminalKey.Keypad0 => 57399,
            TerminalKey.Keypad1 => 57400,
            TerminalKey.Keypad2 => 57401,
            TerminalKey.Keypad3 => 57402,
            TerminalKey.Keypad4 => 57403,
            TerminalKey.Keypad5 => 57404,
            TerminalKey.Keypad6 => 57405,
            TerminalKey.Keypad7 => 57406,
            TerminalKey.Keypad8 => 57407,
            TerminalKey.Keypad9 => 57408,
            TerminalKey.KeypadDecimal => 57409,
            TerminalKey.KeypadDivide => 57410,
            TerminalKey.KeypadMultiply => 57411,
            TerminalKey.KeypadSubtract => 57412,
            TerminalKey.KeypadAdd => 57413,
            TerminalKey.KeypadEnter => 57414,
            TerminalKey.KeypadEqual => 57415,
            TerminalKey.KeypadLeft => 57417,
            TerminalKey.KeypadRight => 57418,
            TerminalKey.KeypadUp => 57419,
            TerminalKey.KeypadDown => 57420,
            TerminalKey.KeypadPageUp => 57421,
            TerminalKey.KeypadPageDown => 57422,
            TerminalKey.KeypadHome => 57423,
            TerminalKey.KeypadEnd => 57424,
            TerminalKey.KeypadInsert => 57425,
            TerminalKey.KeypadDelete => 57426,
            _ => 0
        };
        return codepoint != 0;
    }

    private static bool TryGetKittyCodepoint(TerminalKey key, out int codepoint)
    {
        codepoint = key switch
        {
            >= TerminalKey.A and <= TerminalKey.Z => 'a' + (key - TerminalKey.A),
            >= TerminalKey.Number0 and <= TerminalKey.Number9 => '0' + (key - TerminalKey.Number0),
            >= TerminalKey.F13 and <= TerminalKey.F24 => 57376 + (key - TerminalKey.F13),
            TerminalKey.F25 => 57388,
            TerminalKey.Space => 32,
            TerminalKey.Minus => '-',
            TerminalKey.Equal => '=',
            TerminalKey.LeftBracket => '[',
            TerminalKey.RightBracket => ']',
            TerminalKey.BackSlash => '\\',
            TerminalKey.Semicolon => ';',
            TerminalKey.Quote => '\'',
            TerminalKey.GraveAccent => 96,
            TerminalKey.Comma => ',',
            TerminalKey.Period => '.',
            TerminalKey.Slash => '/',
            TerminalKey.Escape => 27,
            TerminalKey.Enter => 13,
            TerminalKey.Tab => 9,
            TerminalKey.Backspace => 127,
            TerminalKey.CapsLock => 57358,
            TerminalKey.ScrollLock => 57359,
            TerminalKey.NumLock => 57360,
            TerminalKey.PrintScreen => 57361,
            TerminalKey.Pause => 57362,
            TerminalKey.Menu => 57363,
            TerminalKey.ShiftLeft => 57441,
            TerminalKey.ControlLeft => 57442,
            TerminalKey.AltLeft => 57443,
            TerminalKey.SuperLeft => 57444,
            TerminalKey.ShiftRight => 57447,
            TerminalKey.ControlRight => 57448,
            TerminalKey.AltRight => 57449,
            TerminalKey.SuperRight => 57450,
            _ => 0
        };
        return codepoint != 0;
    }

    private static int ReadScalar(ReadOnlySpan<char> text, ref int index)
    {
        char first = text[index++];
        if (char.IsHighSurrogate(first) && index < text.Length && char.IsLowSurrogate(text[index]))
            return char.ConvertToUtf32(first, text[index++]);
        return char.IsSurrogate(first) ? 0xfffd : first;
    }
    private static int FunctionCode(int function) => function switch
    {
        5 => 15,
        6 => 17,
        7 => 18,
        8 => 19,
        9 => 20,
        10 => 21,
        11 => 23,
        12 => 24,
        13 => 25,
        14 => 26,
        15 => 28,
        16 => 29,
        17 => 31,
        18 => 32,
        19 => 33,
        20 => 34,
        21 => 42,
        22 => 43,
        23 => 44,
        24 => 45,
        25 => 46,
        _ => 0
    };

    private static bool TryEncodeKeypad(
        TerminalKey key,
        TerminalKeyModifiers modifiers,
        bool applicationMode,
        Span<byte> destination,
        out int length)
    {
        length = 0;
        if (modifiers != TerminalKeyModifiers.None && modifiers != TerminalKeyModifiers.Shift) return false;
        string? sequence = applicationMode
            ? key switch
            {
                TerminalKey.Keypad0 => "\x1bOp",
                TerminalKey.Keypad1 => "\x1bOq",
                TerminalKey.Keypad2 => "\x1bOr",
                TerminalKey.Keypad3 => "\x1bOs",
                TerminalKey.Keypad4 => "\x1bOt",
                TerminalKey.Keypad5 => "\x1bOu",
                TerminalKey.Keypad6 => "\x1bOv",
                TerminalKey.Keypad7 => "\x1bOw",
                TerminalKey.Keypad8 => "\x1bOx",
                TerminalKey.Keypad9 => "\x1bOy",
                TerminalKey.KeypadDecimal => "\x1bOn",
                TerminalKey.KeypadDivide => "\x1bOl",
                TerminalKey.KeypadMultiply => "\x1bOR",
                TerminalKey.KeypadSubtract => "\x1bOS",
                TerminalKey.KeypadAdd => "\x1bOm",
                TerminalKey.KeypadEnter => "\x1bOM",
                TerminalKey.KeypadEqual => "=",
                _ => null
            }
            : key switch
            {
                TerminalKey.Keypad0 => "0",
                TerminalKey.Keypad1 => "1",
                TerminalKey.Keypad2 => "2",
                TerminalKey.Keypad3 => "3",
                TerminalKey.Keypad4 => "4",
                TerminalKey.Keypad5 => "5",
                TerminalKey.Keypad6 => "6",
                TerminalKey.Keypad7 => "7",
                TerminalKey.Keypad8 => "8",
                TerminalKey.Keypad9 => "9",
                TerminalKey.KeypadDecimal => ".",
                TerminalKey.KeypadDivide => "/",
                TerminalKey.KeypadMultiply => "*",
                TerminalKey.KeypadSubtract => "-",
                TerminalKey.KeypadAdd => "+",
                TerminalKey.KeypadEnter => "\r",
                TerminalKey.KeypadEqual => "=",
                _ => null
            };
        if (sequence is null) return false;
        length = Encoding.ASCII.GetBytes(sequence.AsSpan(), destination);
        return true;
    }

    private static int WriteNumber(Span<byte> destination, int offset, int value)
    {
        uint magnitude;
        if (value < 0)
        {
            destination[offset++] = (byte)'-';
            magnitude = (uint)(-(long)value);
        }
        else
        {
            magnitude = (uint)value;
        }

        int start = offset;
        do
        {
            destination[offset++] = (byte)('0' + magnitude % 10);
            magnitude /= 10;
        } while (magnitude != 0);
        for (int left = start, right = offset - 1; left < right; left++, right--)
            (destination[left], destination[right]) = (destination[right], destination[left]);
        return offset;
    }


    private static int GetModifier(TerminalKeyModifiers modifiers)
    {
        int value = 1;
        if ((modifiers & TerminalKeyModifiers.Shift) != 0) value += 1;
        if ((modifiers & TerminalKeyModifiers.Alt) != 0) value += 2;
        if ((modifiers & TerminalKeyModifiers.Control) != 0) value += 4;
        if ((modifiers & TerminalKeyModifiers.Meta) != 0) value += 8;
        if ((modifiers & TerminalKeyModifiers.CapsLock) != 0) value += 64;
        if ((modifiers & TerminalKeyModifiers.NumLock) != 0) value += 128;
        return value;
    }

}
