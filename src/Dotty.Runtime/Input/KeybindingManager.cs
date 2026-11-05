using System;
using System.Collections.Generic;
using System.Text;
using Dotty.Abstractions.Config;

namespace Dotty.Runtime.Input;

/// <summary>Platform whose built-in binding set applies (macOS adds the <c>cmd</c> set, decision K2).</summary>
public enum KeybindingPlatform
{
    Linux,
    Windows,
    MacOS,
}

/// <summary>A rejected configured binding: the chord text, the value it was bound to, and why it was dropped.</summary>
public readonly record struct KeybindingDiagnostic(string Chord, string Value, string Message)
{
    public override string ToString() => $"keybindings: \"{Chord}\": \"{Value}\": {Message}";
}

/// <summary>
/// Centralized keybinding manager that maps key chords to <see cref="BoundAction"/> values,
/// supporting per-platform defaults and JSON configuration.
/// </summary>
/// <remarks>
/// A key event matches in this order (plan-12 §5.3), first hit wins: (1) a chord written with the
/// <c>physical:</c> prefix, compared with the physical key name; (2) when the key's unshifted layout
/// character is a Latin letter or digit, the chord whose key is that character (Dvorak <c>ctrl+shift+c</c>
/// is the key labelled C); (3) otherwise the physical key name (non-Latin layouts keep the US positions).
/// </remarks>
public sealed class KeybindingManager
{
    public const string PhysicalPrefix = "physical:";

    private readonly Dictionary<TerminalChordKey, BoundAction> _actionBindings = new();
    private readonly List<KeybindingDiagnostic> _diagnostics = new();

    public KeybindingManager() : this(CurrentPlatform)
    {
    }

    public KeybindingManager(KeybindingPlatform platform)
    {
        Platform = platform;
        RegisterDefaults();
    }

    public static KeybindingPlatform CurrentPlatform =>
        OperatingSystem.IsMacOS() ? KeybindingPlatform.MacOS :
        OperatingSystem.IsWindows() ? KeybindingPlatform.Windows :
        KeybindingPlatform.Linux;

    public KeybindingPlatform Platform { get; }

    /// <summary>Bindings rejected by the last <see cref="ApplyCustomBindings"/> (cleared by <see cref="RegisterDefaults"/>).</summary>
    public IReadOnlyList<KeybindingDiagnostic> Diagnostics => _diagnostics;

    /// <summary>The live table as canonical chord text, sorted by chord.</summary>
    public IReadOnlyList<KeyValuePair<string, BoundAction>> GetBindings()
    {
        var list = new List<KeyValuePair<string, BoundAction>>(_actionBindings.Count);
        foreach (var (key, action) in _actionBindings)
            list.Add(new(FormatChord(key), action));
        list.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        return list;
    }

    public void RegisterDefaults()
    {
        _actionBindings.Clear();
        _diagnostics.Clear();

        // Tabs
        Bind("ctrl+shift+t", TerminalAction.NewTab);
        Bind("ctrl+shift+w", TerminalAction.ClosePane);
        Bind("ctrl+tab", TerminalAction.NextTab);
        Bind("ctrl+pagedown", TerminalAction.NextTab);
        Bind("ctrl+shift+tab", TerminalAction.PreviousTab);
        Bind("ctrl+pageup", TerminalAction.PreviousTab);

        // Numbered Tabs (Alt+1..9)
        Bind("alt+1", TerminalAction.SwitchTab1);
        Bind("alt+2", TerminalAction.SwitchTab2);
        Bind("alt+3", TerminalAction.SwitchTab3);
        Bind("alt+4", TerminalAction.SwitchTab4);
        Bind("alt+5", TerminalAction.SwitchTab5);
        Bind("alt+6", TerminalAction.SwitchTab6);
        Bind("alt+7", TerminalAction.SwitchTab7);
        Bind("alt+8", TerminalAction.SwitchTab8);
        Bind("alt+9", TerminalAction.SwitchTab9);

        // Clipboard & Search
        Bind("ctrl+shift+c", TerminalAction.Copy);
        Bind("ctrl+shift+v", TerminalAction.Paste);
        Bind("ctrl+shift+a", TerminalAction.SelectAll);
        Bind("ctrl+shift+f", TerminalAction.Search);

        // Split Panes
        Bind("ctrl+shift+d", TerminalAction.SplitVertical);
        Bind("ctrl+shift+s", TerminalAction.SplitHorizontal);
        Bind("alt+left", TerminalAction.FocusPaneLeft);
        Bind("alt+right", TerminalAction.FocusPaneRight);
        Bind("alt+up", TerminalAction.FocusPaneUp);
        Bind("alt+down", TerminalAction.FocusPaneDown);
        Bind("ctrl+shift+pageup", TerminalAction.PreviousPrompt);
        Bind("ctrl+shift+pagedown", TerminalAction.NextPrompt);
        Bind("ctrl+alt+o", TerminalAction.CopyCommandOutput);
        // Scrollback (decision K2): Shift+arrows/Home/End go to the application.
        Bind("shift+pageup", TerminalAction.ScrollPageUp);
        Bind("shift+pagedown", TerminalAction.ScrollPageDown);
        Bind("ctrl+shift+up", TerminalAction.ScrollLineUp);
        Bind("ctrl+shift+down", TerminalAction.ScrollLineDown);
        Bind("ctrl+shift+home", TerminalAction.ScrollToTop);
        Bind("ctrl+shift+end", TerminalAction.ScrollToBottom);
        // Window, zoom, and application controls
        Bind("f11", TerminalAction.ToggleFullscreen);
        Bind("ctrl+equal", TerminalAction.ZoomIn);
        Bind("ctrl+plus", TerminalAction.ZoomIn);
        Bind("ctrl+minus", TerminalAction.ZoomOut);
        Bind("ctrl+0", TerminalAction.ResetZoom);
        Bind("ctrl+shift+q", TerminalAction.Quit);

        if (Platform == KeybindingPlatform.MacOS)
            RegisterMacDefaults();
    }

    // Additive Command set (decision K2): the Linux/Windows chords above stay bound on macOS too.
    private void RegisterMacDefaults()
    {
        Bind("cmd+c", TerminalAction.Copy);
        Bind("cmd+v", TerminalAction.Paste);
        Bind("cmd+a", TerminalAction.SelectAll);
        Bind("cmd+t", TerminalAction.NewTab);
        Bind("cmd+w", TerminalAction.ClosePane);
        Bind("cmd+q", TerminalAction.Quit);
        Bind("cmd+f", TerminalAction.Search);
        Bind("cmd+k", TerminalAction.ClearScrollback);
        Bind("cmd+equal", TerminalAction.ZoomIn);
        Bind("cmd+plus", TerminalAction.ZoomIn);
        Bind("cmd+minus", TerminalAction.ZoomOut);
        Bind("cmd+0", TerminalAction.ResetZoom);
        Bind("cmd+[", TerminalAction.PreviousTab);
        Bind("cmd+]", TerminalAction.NextTab);
        Bind("cmd+shift+[", TerminalAction.PreviousTab);
        Bind("cmd+shift+]", TerminalAction.NextTab);
        Bind("cmd+1", TerminalAction.SwitchTab1);
        Bind("cmd+2", TerminalAction.SwitchTab2);
        Bind("cmd+3", TerminalAction.SwitchTab3);
        Bind("cmd+4", TerminalAction.SwitchTab4);
        Bind("cmd+5", TerminalAction.SwitchTab5);
        Bind("cmd+6", TerminalAction.SwitchTab6);
        Bind("cmd+7", TerminalAction.SwitchTab7);
        Bind("cmd+8", TerminalAction.SwitchTab8);
        Bind("cmd+9", TerminalAction.SwitchTab9);
        Bind("cmd+enter", TerminalAction.ToggleFullscreen);
        Bind("cmd+d", TerminalAction.SplitVertical);
        Bind("cmd+shift+d", TerminalAction.SplitHorizontal);
    }

    /// <summary>Binds <paramref name="chord"/>; <see cref="TerminalAction.None"/> unbinds it. False when the chord is invalid.</summary>
    public bool Bind(string chord, BoundAction action)
    {
        if (!TryParseChord(chord, out var key)) return false;

        // None is an intentional unbind, not a dispatchable action.
        if (action.Action == TerminalAction.None)
            _actionBindings.Remove(key);
        else
            _actionBindings[key] = action;
        return true;
    }

    public bool Bind(string chord, TerminalAction action) => Bind(chord, new BoundAction(action));

    /// <summary>
    /// Applies the <c>keybindings</c> map over the current table. Every rejected entry (bad chord, unknown action
    /// or argument, a chord written twice in different spellings) is kept in <see cref="Diagnostics"/>.
    /// </summary>
    public void ApplyCustomBindings(IDictionary<string, string>? customMap)
    {
        _diagnostics.Clear();
        if (customMap == null) return;

        var seen = new Dictionary<TerminalChordKey, string>();
        foreach (var (chord, actionName) in customMap)
        {
            if (!TryParseChord(chord, out var key, out string? chordError))
            {
                _diagnostics.Add(new KeybindingDiagnostic(chord, actionName ?? string.Empty, chordError!));
                continue;
            }
            if (!ActionRegistry.TryParse(actionName, out var action, out string? actionError))
            {
                _diagnostics.Add(new KeybindingDiagnostic(chord, actionName ?? string.Empty, actionError!));
                continue;
            }
            if (seen.TryGetValue(key, out string? earlier))
                _diagnostics.Add(new KeybindingDiagnostic(chord, actionName!,
                    $"same chord as \"{earlier}\" ({FormatChord(key)}); this later entry wins"));
            seen[key] = chord;

            if (action.Action == TerminalAction.None)
                _actionBindings.Remove(key);
            else
                _actionBindings[key] = action;
        }
    }

    /// <summary>Validates a <c>keybindings</c> map without applying it (used by <c>+validate-config</c>).</summary>
    public static IReadOnlyList<KeybindingDiagnostic> Validate(IDictionary<string, string>? customMap)
    {
        var probe = new KeybindingManager(KeybindingPlatform.Linux);
        probe.ApplyCustomBindings(customMap);
        return probe._diagnostics.ToArray();
    }

    /// <summary>Looks up an event by physical key name only (no layout character).</summary>
    public bool TryGetAction(bool ctrl, bool shift, bool alt, bool super, string keyName, out TerminalAction action)
    {
        bool found = TryGetBinding(ctrl, shift, alt, super, keyName, 0, out var binding);
        action = binding.Action;
        return found;
    }

    /// <summary>
    /// Looks up a key event: <paramref name="physicalKeyName"/> is the US-position key name (Silk name),
    /// <paramref name="layoutCodepoint"/> the unshifted character the active layout gives that key (0 if unknown).
    /// </summary>
    public bool TryGetBinding(bool ctrl, bool shift, bool alt, bool super, string physicalKeyName, int layoutCodepoint, out BoundAction binding)
        => TryMatch(_actionBindings, ctrl, shift, alt, super, physicalKeyName, layoutCodepoint, out binding);

    /// <summary>The §5.3 matching order over any chord table (shared with Lua bindings). Allocation-free.</summary>
    public static bool TryMatch<T>(Dictionary<TerminalChordKey, T> table, bool ctrl, bool shift, bool alt, bool super,
        string physicalKeyName, int layoutCodepoint, out T value)
    {
        string physical = NormalizeKeyName(physicalKeyName);
        if (string.IsNullOrEmpty(physical))
        {
            value = default!;
            return false;
        }

        if (table.TryGetValue(new TerminalChordKey(ctrl, shift, alt, super, physical, physical: true), out value!))
            return true;

        string? layoutName = physical.StartsWith("keypad", StringComparison.OrdinalIgnoreCase) ? null : LayoutKeyName(layoutCodepoint);
        if (layoutName != null)
            return table.TryGetValue(new TerminalChordKey(ctrl, shift, alt, super, layoutName), out value!);

        if (table.TryGetValue(new TerminalChordKey(ctrl, shift, alt, super, physical), out value!))
            return true;

        // A key giving '+' (unshifted on German/Nordic layouts), or Shift+'=' where '+' is the shifted level:
        // ctrl+plus matches with or without the Shift it took to type '+'.
        bool plusKey = layoutCodepoint == '+' ||
            (shift && (layoutCodepoint == '=' || layoutCodepoint == 0) && string.Equals(physical, "equal", StringComparison.OrdinalIgnoreCase));
        if (plusKey && (table.TryGetValue(new TerminalChordKey(ctrl, shift, alt, super, "plus"), out value!) ||
                        (shift && table.TryGetValue(new TerminalChordKey(ctrl, false, alt, super, "plus"), out value!))))
            return true;

        value = default!;
        return false;
    }

    private static readonly string[] s_letterNames =
        ["a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m", "n", "o", "p", "q", "r", "s", "t", "u", "v", "w", "x", "y", "z"];
    private static readonly string[] s_digitNames = ["0", "1", "2", "3", "4", "5", "6", "7", "8", "9"];

    /// <summary>The chord key name of a Latin letter or ASCII digit; null for every other character.</summary>
    public static string? LayoutKeyName(int codepoint) => codepoint switch
    {
        >= 'a' and <= 'z' => s_letterNames[codepoint - 'a'],
        >= 'A' and <= 'Z' => s_letterNames[codepoint - 'A'],
        >= '0' and <= '9' => s_digitNames[codepoint - '0'],
        _ => null,
    };

    /// <summary>Label of the chord that best represents <paramref name="action"/> (e.g. <c>Ctrl+Shift+C</c>); null when unbound.</summary>
    public string? GetShortcutLabel(TerminalAction action)
    {
        TerminalChordKey? best = null;
        foreach (var (key, bound) in _actionBindings)
        {
            if (bound.Action != action || bound.Argument != null) continue;
            if (best is not { } current || ComparePreference(key, current) < 0)
                best = key;
        }
        return best is { } chosen ? FormatLabel(chosen, Platform) : null;
    }

    // Prefer layout chords, then (on macOS) Command chords, then fewer modifiers, then the canonical text.
    private int ComparePreference(TerminalChordKey a, TerminalChordKey b)
    {
        int c = a.Physical.CompareTo(b.Physical);
        if (c != 0) return c;
        if (Platform == KeybindingPlatform.MacOS)
        {
            c = b.Super.CompareTo(a.Super);
            if (c != 0) return c;
        }
        c = ModifierCount(a).CompareTo(ModifierCount(b));
        return c != 0 ? c : string.CompareOrdinal(FormatChord(a), FormatChord(b));
    }

    private static int ModifierCount(TerminalChordKey key) =>
        (key.Control ? 1 : 0) + (key.Shift ? 1 : 0) + (key.Alt ? 1 : 0) + (key.Super ? 1 : 0);

    /// <summary>Human label of a chord: <c>Ctrl+Shift+PageUp</c>; on macOS <c>Cmd</c> and <c>Option</c>.</summary>
    public static string FormatLabel(TerminalChordKey key, KeybindingPlatform platform)
    {
        var result = new StringBuilder(24);
        if (key.Control) result.Append("Ctrl+");
        if (key.Shift) result.Append("Shift+");
        if (key.Alt) result.Append(platform == KeybindingPlatform.MacOS ? "Option+" : "Alt+");
        if (key.Super) result.Append(platform == KeybindingPlatform.MacOS ? "Cmd+" : "Super+");
        result.Append(KeyLabel(key.KeyName));
        return result.ToString();
    }

    private static string KeyLabel(string name) => name switch
    {
        "pageup" => "PageUp",
        "pagedown" => "PageDown",
        "capslock" => "CapsLock",
        "scrolllock" => "ScrollLock",
        "numlock" => "NumLock",
        "printscreen" => "PrintScreen",
        "comma" => ",",
        "period" => ".",
        "slash" => "/",
        "semicolon" => ";",
        "apostrophe" => "'",
        "graveaccent" => "`",
        "leftbracket" => "[",
        "rightbracket" => "]",
        "backslash" => "\\",
        "minus" => "-",
        "equal" => "=",
        "plus" => "+",
        _ when name.StartsWith("keypad", StringComparison.Ordinal) && name.Length > 6 =>
            "Keypad" + char.ToUpperInvariant(name[6]) + name[7..],
        _ when name.Length > 0 => char.ToUpperInvariant(name[0]) + name[1..],
        _ => name,
    };

    public static string NormalizeKeyName(string silkKeyName) => silkKeyName switch
    {
        "Number0" or "D0" => "0",
        "Number1" or "D1" => "1",
        "Number2" or "D2" => "2",
        "Number3" or "D3" => "3",
        "Number4" or "D4" => "4",
        "Number5" or "D5" => "5",
        "Number6" or "D6" => "6",
        "Number7" or "D7" => "7",
        "Number8" or "D8" => "8",
        "Number9" or "D9" => "9",
        _ => silkKeyName
    };

    // Canonical key names: the lower-cased Silk.NET key names (digits as "0".."9") plus "plus".
    private static readonly HashSet<string> s_keyNames = BuildKeyNames();

    private static HashSet<string> BuildKeyNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal)
        {
            "space", "apostrophe", "comma", "minus", "period", "slash", "semicolon", "equal", "plus",
            "leftbracket", "backslash", "rightbracket", "graveaccent", "world1", "world2",
            "escape", "enter", "tab", "backspace", "insert", "delete", "right", "left", "down", "up",
            "pageup", "pagedown", "home", "end", "capslock", "scrolllock", "numlock", "printscreen", "pause",
            "keypaddecimal", "keypaddivide", "keypadmultiply", "keypadsubtract", "keypadadd", "keypadenter", "keypadequal",
            "shiftleft", "controlleft", "altleft", "superleft", "shiftright", "controlright", "altright", "superright", "menu",
        };
        names.UnionWith(s_letterNames);
        names.UnionWith(s_digitNames);
        for (int i = 1; i <= 25; i++) names.Add("f" + i);
        for (int i = 0; i <= 9; i++) names.Add("keypad" + i);
        return names;
    }

    /// <summary>Every canonical key name a chord may use.</summary>
    public static IReadOnlyCollection<string> KeyNames => s_keyNames;

    /// <summary>Maps a key token (name, alias, or single printable character) to its canonical name.</summary>
    public static bool TryCanonicalKeyName(ReadOnlySpan<char> token, out string canonical)
    {
        if (token.Length == 1)
        {
            string? fromChar = token[0] switch
            {
                ',' => "comma",
                '.' => "period",
                '/' => "slash",
                ';' => "semicolon",
                '\'' => "apostrophe",
                '`' => "graveaccent",
                '[' => "leftbracket",
                ']' => "rightbracket",
                '\\' => "backslash",
                '-' => "minus",
                '=' => "equal",
                '+' => "plus",
                _ => null,
            };
            if (fromChar != null)
            {
                canonical = fromChar;
                return true;
            }
        }
        string lower = token.ToString().ToLowerInvariant();
        lower = lower switch
        {
            "return" => "enter",
            "esc" => "escape",
            "pgup" => "pageup",
            "pgdn" or "pgdown" => "pagedown",
            "del" => "delete",
            "ins" => "insert",
            "bs" => "backspace",
            "quote" => "apostrophe",
            "grave" or "backtick" => "graveaccent",
            "bracketleft" => "leftbracket",
            "bracketright" => "rightbracket",
            "number0" or "d0" => "0",
            "number1" or "d1" => "1",
            "number2" or "d2" => "2",
            "number3" or "d3" => "3",
            "number4" or "d4" => "4",
            "number5" or "d5" => "5",
            "number6" or "d6" => "6",
            "number7" or "d7" => "7",
            "number8" or "d8" => "8",
            "number9" or "d9" => "9",
            _ => lower,
        };
        if (s_keyNames.TryGetValue(lower, out string? known))
        {
            canonical = known;
            return true;
        }
        canonical = string.Empty;
        return false;
    }

    public static string BuildChord(bool ctrl, bool shift, bool alt, bool super, string keyName)
    {
        if (!TryCanonicalKeyName(NormalizeKeyName(keyName), out string name))
            return string.Empty;
        return FormatChord(new TerminalChordKey(ctrl, shift, alt, super, name));
    }

    /// <summary>Canonical chord text: <c>[physical:]ctrl+shift+alt+super+name</c>.</summary>
    public static string FormatChord(TerminalChordKey key)
    {
        var result = new StringBuilder(32);
        if (key.Physical) result.Append(PhysicalPrefix);
        if (key.Control) result.Append("ctrl+");
        if (key.Shift) result.Append("shift+");
        if (key.Alt) result.Append("alt+");
        if (key.Super) result.Append("super+");
        result.Append(key.KeyName);
        return result.ToString();
    }

    internal static bool TryParseChord(string? chord, out TerminalChordKey key) => TryParseChord(chord, out key, out _);

    /// <summary>Parses a chord; on failure <paramref name="error"/> says why.</summary>
    public static bool TryParseChord(string? chord, out TerminalChordKey key, out string? error)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(chord))
            return Reject("empty chord", out error);

        ReadOnlySpan<char> text = chord.AsSpan().Trim();
        bool physical = false;
        if (text.StartsWith(PhysicalPrefix, StringComparison.OrdinalIgnoreCase))
        {
            physical = true;
            text = text[PhysicalPrefix.Length..];
        }

        bool ctrl = false, shift = false, alt = false, super = false;
        string? name = null;
        int start = 0;
        while (start <= text.Length)
        {
            int relative = text[start..].IndexOf('+');
            int end = relative < 0 ? text.Length : start + relative;
            // "ctrl++" and a lone "+": the key is '+'.
            if (end == start && end == text.Length - 1 && name == null)
                end = text.Length;
            ReadOnlySpan<char> part = text[start..end].Trim();
            if (part.IsEmpty)
                return Reject("empty part between '+' separators", out error);
            foreach (char c in part)
                if (char.IsWhiteSpace(c)) return Reject($"whitespace inside '{part.ToString()}'", out error);

            if (part.Equals("ctrl", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("control", StringComparison.OrdinalIgnoreCase))
            {
                if (ctrl) return Reject("ctrl written twice", out error);
                ctrl = true;
            }
            else if (part.Equals("shift", StringComparison.OrdinalIgnoreCase))
            {
                if (shift) return Reject("shift written twice", out error);
                shift = true;
            }
            else if (part.Equals("alt", StringComparison.OrdinalIgnoreCase) ||
                     part.Equals("option", StringComparison.OrdinalIgnoreCase) ||
                     part.Equals("opt", StringComparison.OrdinalIgnoreCase))
            {
                if (alt) return Reject("alt written twice", out error);
                alt = true;
            }
            else if (part.Equals("super", StringComparison.OrdinalIgnoreCase) ||
                     part.Equals("win", StringComparison.OrdinalIgnoreCase) ||
                     part.Equals("cmd", StringComparison.OrdinalIgnoreCase) ||
                     part.Equals("command", StringComparison.OrdinalIgnoreCase))
            {
                if (super) return Reject("super written twice", out error);
                super = true;
            }
            else if (part.Equals("meta", StringComparison.OrdinalIgnoreCase) ||
                     part.Equals("mod", StringComparison.OrdinalIgnoreCase) ||
                     part.Equals("hyper", StringComparison.OrdinalIgnoreCase) ||
                     part.Equals("fn", StringComparison.OrdinalIgnoreCase))
            {
                return Reject($"modifier '{part.ToString()}' is not supported; use ctrl, shift, alt (option) or super (cmd, win)", out error);
            }
            else
            {
                if (name != null) return Reject($"two keys ('{name}' and '{part.ToString()}')", out error);
                if (!TryCanonicalKeyName(part, out name))
                    return Reject($"unknown key '{part.ToString()}'", out error);
            }

            if (end >= text.Length) break;
            start = end + 1;
        }

        if (string.IsNullOrEmpty(name))
            return Reject("no key, only modifiers", out error);
        key = new TerminalChordKey(ctrl, shift, alt, super, name, physical);
        error = null;
        return true;
    }

    private static bool Reject(string message, out string? error)
    {
        error = message;
        return false;
    }

    internal static TerminalChordKey CreateKey(bool ctrl, bool shift, bool alt, bool super, string keyName) =>
        new(ctrl, shift, alt, super, NormalizeKeyName(keyName));

    /// <summary>
    /// Parses and canonicalizes a chord. Canonical order is physical:, ctrl, shift, alt, super, key.
    /// </summary>
    public static bool TryNormalizeChord(string? chord, out string normalized)
    {
        if (!TryParseChord(chord, out var key))
        {
            normalized = string.Empty;
            return false;
        }

        normalized = FormatChord(key);
        return true;
    }

    public static string NormalizeChord(string chord)
        => TryNormalizeChord(chord, out var normalized) ? normalized : string.Empty;
}

/// <summary>A parsed key chord used for allocation-free event lookup.</summary>
public readonly struct TerminalChordKey : IEquatable<TerminalChordKey>
{
    public bool Control { get; }
    public bool Shift { get; }
    public bool Alt { get; }
    public bool Super { get; }
    public string KeyName { get; }

    /// <summary>Written with the <c>physical:</c> prefix: matches the physical key only, never the layout character.</summary>
    public bool Physical { get; }

    public TerminalChordKey(bool control, bool shift, bool alt, bool super, string keyName, bool physical = false)
    {
        Control = control;
        Shift = shift;
        Alt = alt;
        Super = super;
        KeyName = keyName;
        Physical = physical;
    }

    public bool Equals(TerminalChordKey other) =>
        Control == other.Control && Shift == other.Shift && Alt == other.Alt && Super == other.Super && Physical == other.Physical &&
        StringComparer.OrdinalIgnoreCase.Equals(KeyName, other.KeyName);

    public override bool Equals(object? obj) => obj is TerminalChordKey other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(Control, Shift, Alt, Super, Physical, KeyName is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(KeyName));
}
