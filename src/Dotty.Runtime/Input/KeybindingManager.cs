using System;
using System.Collections.Generic;
using Dotty.Abstractions.Config;

namespace Dotty.Runtime.Input;

/// <summary>
/// Centralized keybinding manager that maps key chords to <see cref="TerminalAction"/> values,
/// supporting default presets and JSON configuration.
/// </summary>
public sealed class KeybindingManager
{
    private readonly Dictionary<TerminalChordKey, TerminalAction> _actionBindings = new();

    public KeybindingManager()
    {
        RegisterDefaults();
    }

    public void RegisterDefaults()
    {
        _actionBindings.Clear();

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
        Bind("ctrl+shift+f", TerminalAction.Search);

        // Split Panes
        Bind("ctrl+shift+d", TerminalAction.SplitVertical);
        Bind("ctrl+shift+s", TerminalAction.SplitHorizontal);
        Bind("alt+left", TerminalAction.FocusPaneLeft);
        Bind("alt+right", TerminalAction.FocusPaneRight);
        Bind("alt+up", TerminalAction.FocusPaneUp);
        Bind("alt+down", TerminalAction.FocusPaneDown);
        // Window, zoom, and application controls
        Bind("f11", TerminalAction.ToggleFullscreen);
        Bind("ctrl+equal", TerminalAction.ZoomIn);
        Bind("ctrl+plus", TerminalAction.ZoomIn);
        Bind("ctrl+minus", TerminalAction.ZoomOut);
        Bind("ctrl+0", TerminalAction.ResetZoom);
        Bind("ctrl+shift+q", TerminalAction.Quit);
    }

    public void Bind(string chord, TerminalAction action)
    {
        if (!TryParseChord(chord, out var key)) return;

        // None is an intentional unbind, not a dispatchable action.
        if (action == TerminalAction.None)
            _actionBindings.Remove(key);
        else
            _actionBindings[key] = action;
    }

    public void ApplyCustomBindings(IDictionary<string, string>? customMap)
    {
        if (customMap == null) return;

        foreach (var (chord, actionName) in customMap)
        {
            if (actionName == null
                || !Enum.TryParse<TerminalAction>(actionName.Trim(), ignoreCase: true, out var action)
                || !Enum.IsDefined(action))
            {
                continue;
            }

            Bind(chord, action);
        }
    }

    public bool TryGetAction(bool ctrl, bool shift, bool alt, bool super, string keyName, out TerminalAction action)
    {
        keyName = NormalizeKeyName(keyName);
        if (!string.IsNullOrEmpty(keyName)
            && _actionBindings.TryGetValue(new TerminalChordKey(ctrl, shift, alt, super, keyName), out action))
            return true;

        action = TerminalAction.None;
        return false;
    }
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

    public static string BuildChord(bool ctrl, bool shift, bool alt, bool super, string keyName)
    {
        var builder = new System.Text.StringBuilder(32);
        AppendModifier(builder, ctrl, "ctrl");
        AppendModifier(builder, shift, "shift");
        AppendModifier(builder, alt, "alt");
        AppendModifier(builder, super, "super");
        if (builder.Length != 0) builder.Append('+');
        builder.Append(NormalizeKeyName(keyName));
        return TryParseChord(builder.ToString(), out var parsed) ? FormatChord(parsed) : string.Empty;
    }

    private static string FormatChord(TerminalChordKey key)
    {
        var result = new System.Text.StringBuilder(32);
        AppendModifier(result, key.Control, "ctrl");
        AppendModifier(result, key.Shift, "shift");
        AppendModifier(result, key.Alt, "alt");
        AppendModifier(result, key.Super, "super");
        if (result.Length != 0) result.Append('+');
        result.Append(key.KeyName);
        return result.ToString();
    }

    private static void AppendModifier(System.Text.StringBuilder result, bool enabled, string modifier)
    {
        if (!enabled) return;
        if (result.Length != 0) result.Append('+');
        result.Append(modifier);
    }

    internal static bool TryParseChord(string? chord, out TerminalChordKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(chord)) return false;

        bool ctrl = false, shift = false, alt = false, super = false;
        string? name = null;
        int start = 0;
        while (start <= chord.Length)
        {
            int end = chord.IndexOf('+', start);
            if (end < 0) end = chord.Length;
            ReadOnlySpan<char> part = chord.AsSpan(start, end - start).Trim();
            if (part.IsEmpty) return false;
            foreach (char c in part)
                if (char.IsWhiteSpace(c)) return false;

            if (part.Equals("ctrl", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("control", StringComparison.OrdinalIgnoreCase))
            {
                if (ctrl) return false;
                ctrl = true;
            }
            else if (part.Equals("shift", StringComparison.OrdinalIgnoreCase))
            {
                if (shift) return false;
                shift = true;
            }
            else if (part.Equals("alt", StringComparison.OrdinalIgnoreCase) ||
                     part.Equals("option", StringComparison.OrdinalIgnoreCase))
            {
                if (alt) return false;
                alt = true;
            }
            else if (part.Equals("super", StringComparison.OrdinalIgnoreCase) ||
                     part.Equals("win", StringComparison.OrdinalIgnoreCase) ||
                     part.Equals("cmd", StringComparison.OrdinalIgnoreCase))
            {
                if (super) return false;
                super = true;
            }
            else if (part.Equals("meta", StringComparison.OrdinalIgnoreCase) ||
                     part.Equals("command", StringComparison.OrdinalIgnoreCase) ||
                     part.Equals("mod", StringComparison.OrdinalIgnoreCase) ||
                     part.Equals("hyper", StringComparison.OrdinalIgnoreCase) ||
                     part.Equals("fn", StringComparison.OrdinalIgnoreCase))
                return false;
            else
            {
                if (name != null) return false;
                name = part.ToString().ToLowerInvariant();
            }

            if (end == chord.Length) break;
            start = end + 1;
        }

        if (string.IsNullOrEmpty(name)) return false;
        name = NormalizeKeyName(name);
        key = new TerminalChordKey(ctrl, shift, alt, super, name);
        return true;
    }

    internal static TerminalChordKey CreateKey(bool ctrl, bool shift, bool alt, bool super, string keyName) =>
        new(ctrl, shift, alt, super, NormalizeKeyName(keyName));

    /// <summary>
    /// Parses and canonicalizes a chord. Canonical order is ctrl, shift, alt, super, key.
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

    public TerminalChordKey(bool control, bool shift, bool alt, bool super, string keyName)
    {
        Control = control;
        Shift = shift;
        Alt = alt;
        Super = super;
        KeyName = keyName;
    }

    public bool Equals(TerminalChordKey other) =>
        Control == other.Control && Shift == other.Shift && Alt == other.Alt && Super == other.Super &&
        StringComparer.OrdinalIgnoreCase.Equals(KeyName, other.KeyName);

    public override bool Equals(object? obj) => obj is TerminalChordKey other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(Control, Shift, Alt, Super, KeyName is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(KeyName));
}
