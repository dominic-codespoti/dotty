using System;
using System.Collections.Generic;
using Dotty.Abstractions.Config;

namespace Dotty.Runtime.Input;

/// <summary>
/// Centralized keybinding manager that maps key chords to <see cref="TerminalAction"/> values
/// or custom delegate actions, supporting default presets, JSON configuration, and Lua bindings.
/// </summary>
public sealed class KeybindingManager
{
    private readonly Dictionary<string, TerminalAction> _actionBindings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Action> _delegateBindings = new(StringComparer.OrdinalIgnoreCase);

    public KeybindingManager()
    {
        RegisterDefaults();
    }

    public void RegisterDefaults()
    {
        _actionBindings.Clear();

        // Tabs
        _actionBindings[NormalizeChord("ctrl+shift+t")] = TerminalAction.NewTab;
        _actionBindings[NormalizeChord("ctrl+shift+w")] = TerminalAction.ClosePane;
        _actionBindings[NormalizeChord("ctrl+tab")] = TerminalAction.NextTab;
        _actionBindings[NormalizeChord("ctrl+pagedown")] = TerminalAction.NextTab;
        _actionBindings[NormalizeChord("ctrl+shift+tab")] = TerminalAction.PreviousTab;
        _actionBindings[NormalizeChord("ctrl+pageup")] = TerminalAction.PreviousTab;

        // Numbered Tabs (Alt+1..9)
        _actionBindings[NormalizeChord("alt+1")] = TerminalAction.SwitchTab1;
        _actionBindings[NormalizeChord("alt+2")] = TerminalAction.SwitchTab2;
        _actionBindings[NormalizeChord("alt+3")] = TerminalAction.SwitchTab3;
        _actionBindings[NormalizeChord("alt+4")] = TerminalAction.SwitchTab4;
        _actionBindings[NormalizeChord("alt+5")] = TerminalAction.SwitchTab5;
        _actionBindings[NormalizeChord("alt+6")] = TerminalAction.SwitchTab6;
        _actionBindings[NormalizeChord("alt+7")] = TerminalAction.SwitchTab7;
        _actionBindings[NormalizeChord("alt+8")] = TerminalAction.SwitchTab8;
        _actionBindings[NormalizeChord("alt+9")] = TerminalAction.SwitchTab9;

        // Clipboard & Search
        _actionBindings[NormalizeChord("ctrl+shift+c")] = TerminalAction.Copy;
        _actionBindings[NormalizeChord("ctrl+shift+v")] = TerminalAction.Paste;
        _actionBindings[NormalizeChord("ctrl+shift+f")] = TerminalAction.Search;

        // Split Panes
        _actionBindings[NormalizeChord("ctrl+shift+d")] = TerminalAction.SplitVertical;
        _actionBindings[NormalizeChord("ctrl+shift+s")] = TerminalAction.SplitHorizontal;
        _actionBindings[NormalizeChord("alt+left")] = TerminalAction.FocusPaneLeft;
        _actionBindings[NormalizeChord("alt+right")] = TerminalAction.FocusPaneRight;
        _actionBindings[NormalizeChord("alt+up")] = TerminalAction.FocusPaneUp;
        _actionBindings[NormalizeChord("alt+down")] = TerminalAction.FocusPaneDown;
    }

    public void Bind(string chord, TerminalAction action)
    {
        if (string.IsNullOrWhiteSpace(chord)) return;
        _actionBindings[NormalizeChord(chord)] = action;
    }

    public void Bind(string chord, Action callback)
    {
        if (string.IsNullOrWhiteSpace(chord) || callback == null) return;
        _delegateBindings[NormalizeChord(chord)] = callback;
    }

    public void ApplyCustomBindings(IDictionary<string, string>? customMap)
    {
        if (customMap == null) return;

        foreach (var (chord, actionName) in customMap)
        {
            if (Enum.TryParse<TerminalAction>(actionName, ignoreCase: true, out var action))
            {
                Bind(chord, action);
            }
        }
    }

    public bool TryGetAction(bool ctrl, bool shift, bool alt, bool super, string keyName, out TerminalAction action)
    {
        string chord = BuildChord(ctrl, shift, alt, super, keyName);

        if (_actionBindings.TryGetValue(chord, out action))
        {
            return true;
        }

        action = TerminalAction.None;
        return false;
    }

    public bool TryExecuteCustomDelegate(bool ctrl, bool shift, bool alt, bool super, string keyName)
    {
        string chord = BuildChord(ctrl, shift, alt, super, keyName);

        if (_delegateBindings.TryGetValue(chord, out var callback))
        {
            callback.Invoke();
            return true;
        }

        return false;
    }

    public static string BuildChord(bool ctrl, bool shift, bool alt, bool super, string keyName)
    {
        var parts = new List<string>(4);
        if (ctrl) parts.Add("ctrl");
        if (shift) parts.Add("shift");
        if (alt) parts.Add("alt");
        if (super) parts.Add("super");
        parts.Add(keyName.ToLowerInvariant());
        return string.Join("+", parts);
    }

    public static string NormalizeChord(string chord)
    {
        var rawParts = chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool ctrl = false, shift = false, alt = false, super = false;
        string key = string.Empty;

        foreach (var p in rawParts)
        {
            string lower = p.ToLowerInvariant();
            switch (lower)
            {
                case "ctrl" or "control": ctrl = true; break;
                case "shift": shift = true; break;
                case "alt" or "option": alt = true; break;
                case "super" or "win" or "cmd": super = true; break;
                default: key = lower; break;
            }
        }

        return BuildChord(ctrl, shift, alt, super, key);
    }
}
