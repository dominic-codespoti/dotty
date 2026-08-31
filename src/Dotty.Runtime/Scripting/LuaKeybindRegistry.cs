using System;
using System.Collections.Generic;
using NLua;

namespace Dotty.Runtime.Scripting;

/// <summary>
/// Registry for user-defined keybindings mapped to Lua functions.
/// </summary>
public sealed class LuaKeybindRegistry
{
    private readonly Dictionary<string, LuaFunction> _bindings = new(StringComparer.OrdinalIgnoreCase);

    public void Register(string chord, LuaFunction callback)
    {
        if (string.IsNullOrWhiteSpace(chord) || callback == null) return;
        string normalized = NormalizeChord(chord);
        _bindings[normalized] = callback;
    }

    public bool TryExecute(bool ctrl, bool shift, bool alt, bool super, string keyName)
    {
        string chord = BuildChord(ctrl, shift, alt, super, keyName);
        if (_bindings.TryGetValue(chord, out var func))
        {
            try
            {
                func.Call();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Lua Keybind Error] '{chord}': {ex.Message}");
            }
        }
        return false;
    }

    public void Clear()
    {
        _bindings.Clear();
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
