using System.Collections.Generic;
using Silk.NET.Input;

namespace Dotty.App.Tests;

/// <summary>Characters a physical key produces on one layout, per shift level.</summary>
/// <param name="Plain">Unmodified text; also what <c>glfwGetKeyName</c> reports (the primary codepoint).</param>
/// <param name="Shifted">Text with Shift.</param>
/// <param name="AltGr">Text with AltGr (ISO Level 3) on Linux/Windows; null when the layout defines none.</param>
/// <param name="Option">Text with Option on macOS; null when the layout defines none.</param>
/// <param name="OptionShifted">Text with Shift+Option on macOS.</param>
internal readonly record struct KeyChars(string? Plain, string? Shifted, string? AltGr = null, string? Option = null, string? OptionShifted = null);

/// <summary>
/// A keyboard layout as GLFW exposes it: physical keys (GLFW/Silk key tokens, which follow the US
/// positions) mapped to the text each shift level produces. <see cref="HasAltGr"/> means the right Alt
/// key is ISO_Level3_Shift (Linux) / AltGr (Windows) rather than a plain Alt.
/// </summary>
internal sealed class LayoutFixture
{
    private readonly Dictionary<Key, KeyChars> _keys;

    public LayoutFixture(string name, bool hasAltGr, Dictionary<Key, KeyChars> keys)
    {
        Name = name;
        HasAltGr = hasAltGr;
        _keys = keys;
    }

    public string Name { get; }
    public bool HasAltGr { get; }

    public KeyChars this[Key key] => _keys.TryGetValue(key, out var chars) ? chars : default;

    /// <summary>The unmodified character of the key, as <c>glfwGetKeyName</c> would name it; 0 for unnamed keys.</summary>
    public int PrimaryCodepoint(Key key)
    {
        string? plain = this[key].Plain;
        if (string.IsNullOrEmpty(plain))
            return 0;
        return char.ConvertToUtf32(plain, 0);
    }

    public override string ToString() => Name;
}

/// <summary>Layout fixtures used by the key replay harness (plan-12 WI-0).</summary>
internal static class KeyboardLayoutFixtures
{
    public static readonly LayoutFixture Qwerty = new("QWERTY (US)", hasAltGr: false, UsKeys());

    public static readonly LayoutFixture QwertzAltGr = new("QWERTZ (German, AltGr)", hasAltGr: true, GermanKeys());

    public static readonly LayoutFixture Azerty = new("AZERTY (French)", hasAltGr: true, FrenchKeys());

    public static readonly LayoutFixture Dvorak = new("Dvorak (US)", hasAltGr: false, DvorakKeys());

    public static readonly LayoutFixture Russian = new("Russian (JCUKEN)", hasAltGr: false, RussianKeys());

    public static readonly LayoutFixture MacAbc = new("macOS ABC", hasAltGr: false, MacAbcKeys());

    public static IEnumerable<LayoutFixture> All()
    {
        yield return Qwerty;
        yield return QwertzAltGr;
        yield return Azerty;
        yield return Dvorak;
        yield return Russian;
        yield return MacAbc;
    }

    private static Dictionary<Key, KeyChars> UsKeys()
    {
        var keys = new Dictionary<Key, KeyChars>();
        for (Key key = Key.A; key <= Key.Z; key++)
        {
            char letter = (char)('a' + (key - Key.A));
            keys[key] = new KeyChars(letter.ToString(), char.ToUpperInvariant(letter).ToString());
        }
        const string shiftedDigits = ")!@#$%^&*(";
        for (int digit = 0; digit <= 9; digit++)
            keys[Key.Number0 + digit] = new KeyChars(((char)('0' + digit)).ToString(), shiftedDigits[digit].ToString());
        keys[Key.Space] = new KeyChars(" ", " ");
        keys[Key.Minus] = new KeyChars("-", "_");
        keys[Key.Equal] = new KeyChars("=", "+");
        keys[Key.LeftBracket] = new KeyChars("[", "{");
        keys[Key.RightBracket] = new KeyChars("]", "}");
        keys[Key.BackSlash] = new KeyChars("\\", "|");
        keys[Key.Semicolon] = new KeyChars(";", ":");
        keys[Key.Apostrophe] = new KeyChars("'", "\"");
        keys[Key.GraveAccent] = new KeyChars("`", "~");
        keys[Key.Comma] = new KeyChars(",", "<");
        keys[Key.Period] = new KeyChars(".", ">");
        keys[Key.Slash] = new KeyChars("/", "?");
        AddKeypad(keys);
        return keys;
    }

    private static Dictionary<Key, KeyChars> GermanKeys()
    {
        var keys = UsKeys();
        keys[Key.Y] = new KeyChars("z", "Z");
        keys[Key.Z] = new KeyChars("y", "Y");
        keys[Key.Q] = new KeyChars("q", "Q", AltGr: "@", Option: "«");
        keys[Key.E] = new KeyChars("e", "E", AltGr: "€", Option: "€");
        keys[Key.L] = new KeyChars("l", "L", AltGr: "ł", Option: "@");
        keys[Key.Number2] = new KeyChars("2", "\"", AltGr: "²", Option: "“");
        keys[Key.Number3] = new KeyChars("3", "§", AltGr: "³", Option: "¶");
        keys[Key.Number5] = new KeyChars("5", "%", AltGr: "½", Option: "[");
        keys[Key.Number6] = new KeyChars("6", "&", AltGr: "¬", Option: "]");
        keys[Key.Number7] = new KeyChars("7", "/", AltGr: "{", Option: "|");
        keys[Key.Number8] = new KeyChars("8", "(", AltGr: "[", Option: "{");
        keys[Key.Number9] = new KeyChars("9", ")", AltGr: "]", Option: "}");
        keys[Key.Number0] = new KeyChars("0", "=", AltGr: "}", Option: "≠");
        keys[Key.Minus] = new KeyChars("ß", "?", AltGr: "\\", Option: "¿");
        keys[Key.Equal] = new KeyChars(null, null);
        keys[Key.LeftBracket] = new KeyChars("ü", "Ü");
        keys[Key.RightBracket] = new KeyChars("+", "*", AltGr: "~", Option: "±");
        keys[Key.Semicolon] = new KeyChars("ö", "Ö");
        keys[Key.Apostrophe] = new KeyChars("ä", "Ä");
        keys[Key.BackSlash] = new KeyChars("#", "'");
        keys[Key.Comma] = new KeyChars(",", ";");
        keys[Key.Period] = new KeyChars(".", ":");
        keys[Key.Slash] = new KeyChars("-", "_");
        return keys;
    }

    private static Dictionary<Key, KeyChars> FrenchKeys()
    {
        var keys = UsKeys();
        keys[Key.Q] = new KeyChars("a", "A");
        keys[Key.A] = new KeyChars("q", "Q");
        keys[Key.W] = new KeyChars("z", "Z");
        keys[Key.Z] = new KeyChars("w", "W");
        keys[Key.Semicolon] = new KeyChars("m", "M");
        keys[Key.M] = new KeyChars(",", "?");
        keys[Key.Comma] = new KeyChars(";", ".");
        keys[Key.Period] = new KeyChars(":", "/");
        keys[Key.Slash] = new KeyChars("!", "§");
        keys[Key.Number1] = new KeyChars("&", "1");
        keys[Key.Number2] = new KeyChars("é", "2");
        keys[Key.Number3] = new KeyChars("\"", "3", AltGr: "#");
        keys[Key.Number4] = new KeyChars("'", "4", AltGr: "{");
        keys[Key.Number5] = new KeyChars("(", "5", AltGr: "[");
        keys[Key.Number6] = new KeyChars("-", "6", AltGr: "|");
        keys[Key.Number7] = new KeyChars("è", "7");
        keys[Key.Number8] = new KeyChars("_", "8", AltGr: "\\");
        keys[Key.Number9] = new KeyChars("ç", "9", AltGr: "^");
        keys[Key.Number0] = new KeyChars("à", "0", AltGr: "@");
        keys[Key.Minus] = new KeyChars(")", "°", AltGr: "]");
        keys[Key.Equal] = new KeyChars("=", "+", AltGr: "}");
        return keys;
    }

    private static Dictionary<Key, KeyChars> DvorakKeys()
    {
        var keys = UsKeys();
        void Map(Key key, char plain, char shifted) => keys[key] = new KeyChars(plain.ToString(), shifted.ToString());
        Map(Key.Q, '\'', '"'); Map(Key.W, ',', '<'); Map(Key.E, '.', '>'); Map(Key.R, 'p', 'P');
        Map(Key.T, 'y', 'Y'); Map(Key.Y, 'f', 'F'); Map(Key.U, 'g', 'G'); Map(Key.I, 'c', 'C');
        Map(Key.O, 'r', 'R'); Map(Key.P, 'l', 'L'); Map(Key.LeftBracket, '/', '?'); Map(Key.RightBracket, '=', '+');
        Map(Key.A, 'a', 'A'); Map(Key.S, 'o', 'O'); Map(Key.D, 'e', 'E'); Map(Key.F, 'u', 'U');
        Map(Key.G, 'i', 'I'); Map(Key.H, 'd', 'D'); Map(Key.J, 'h', 'H'); Map(Key.K, 't', 'T');
        Map(Key.L, 'n', 'N'); Map(Key.Semicolon, 's', 'S'); Map(Key.Apostrophe, '-', '_');
        Map(Key.Z, ';', ':'); Map(Key.X, 'q', 'Q'); Map(Key.C, 'j', 'J'); Map(Key.V, 'k', 'K');
        Map(Key.B, 'x', 'X'); Map(Key.N, 'b', 'B'); Map(Key.M, 'm', 'M'); Map(Key.Comma, 'w', 'W');
        Map(Key.Period, 'v', 'V'); Map(Key.Slash, 'z', 'Z'); Map(Key.Minus, '[', '{'); Map(Key.Equal, ']', '}');
        return keys;
    }

    private static Dictionary<Key, KeyChars> RussianKeys()
    {
        var keys = UsKeys();
        void Map(Key key, char plain) =>
            keys[key] = new KeyChars(plain.ToString(), char.ToUpperInvariant(plain).ToString());
        Map(Key.Q, 'й'); Map(Key.W, 'ц'); Map(Key.E, 'у'); Map(Key.R, 'к'); Map(Key.T, 'е');
        Map(Key.Y, 'н'); Map(Key.U, 'г'); Map(Key.I, 'ш'); Map(Key.O, 'щ'); Map(Key.P, 'з');
        Map(Key.LeftBracket, 'х'); Map(Key.RightBracket, 'ъ');
        Map(Key.A, 'ф'); Map(Key.S, 'ы'); Map(Key.D, 'в'); Map(Key.F, 'а'); Map(Key.G, 'п');
        Map(Key.H, 'р'); Map(Key.J, 'о'); Map(Key.K, 'л'); Map(Key.L, 'д'); Map(Key.Semicolon, 'ж');
        Map(Key.Apostrophe, 'э');
        Map(Key.Z, 'я'); Map(Key.X, 'ч'); Map(Key.C, 'с'); Map(Key.V, 'м'); Map(Key.B, 'и');
        Map(Key.N, 'т'); Map(Key.M, 'ь'); Map(Key.Comma, 'б'); Map(Key.Period, 'ю');
        keys[Key.Slash] = new KeyChars(".", ",");
        keys[Key.GraveAccent] = new KeyChars("ё", "Ё");
        return keys;
    }

    private static Dictionary<Key, KeyChars> MacAbcKeys()
    {
        var keys = UsKeys();
        keys[Key.A] = new KeyChars("a", "A", Option: "å", OptionShifted: "Å");
        keys[Key.C] = new KeyChars("c", "C", Option: "ç", OptionShifted: "Ç");
        keys[Key.L] = new KeyChars("l", "L", Option: "¬", OptionShifted: "Ò");
        keys[Key.O] = new KeyChars("o", "O", Option: "ø", OptionShifted: "Ø");
        keys[Key.S] = new KeyChars("s", "S", Option: "ß", OptionShifted: "Í");
        keys[Key.Number2] = new KeyChars("2", "@", Option: "™", OptionShifted: "€");
        return keys;
    }

    private static void AddKeypad(Dictionary<Key, KeyChars> keys)
    {
        // GLFW names keypad keys after their NumLock-on text (glfw3.h, glfwGetKeyName).
        for (int digit = 0; digit <= 9; digit++)
        {
            string text = ((char)('0' + digit)).ToString();
            keys[Key.Keypad0 + digit] = new KeyChars(text, text);
        }
        keys[Key.KeypadDecimal] = new KeyChars(".", ".");
        keys[Key.KeypadDivide] = new KeyChars("/", "/");
        keys[Key.KeypadMultiply] = new KeyChars("*", "*");
        keys[Key.KeypadSubtract] = new KeyChars("-", "-");
        keys[Key.KeypadAdd] = new KeyChars("+", "+");
        keys[Key.KeypadEqual] = new KeyChars("=", "=");
    }
}
