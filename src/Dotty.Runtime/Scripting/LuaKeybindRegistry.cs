using System;
using System.Collections.Generic;
using Dotty.Abstractions.Config;
using Dotty.Runtime.Input;

namespace Dotty.Runtime.Scripting;

/// <summary>Stores Lua callbacks and terminal actions bound to keyboard chords.</summary>
public sealed class LuaKeybindRegistry
{
    private readonly object _sync = new();
    private readonly LuaScriptHost _host;
    private readonly Dictionary<TerminalChordKey, Binding> _bindings = new();

    internal LuaKeybindRegistry(LuaScriptHost host)
    {
        _host = host;
    }

    internal void Register(string chord, LuaCallbackReference callback)
    {
        Put(chord, new Binding(callback, null));
    }

    internal void Register(string chord, TerminalAction action)
    {
        Put(chord, new Binding(null, action));
    }

    internal void Unbind(string chord)
    {
        Put(chord, new Binding(null, null, true));
    }

    private void Put(string chord, Binding binding)
    {
        if (!KeybindingManager.TryParseChord(chord, out var key))
        {
            binding.Callback?.Dispose();
            throw new ArgumentException("Invalid key chord.", nameof(chord));
        }

        Binding old;
        bool had;
        lock (_sync)
        {
            had = _bindings.TryGetValue(key, out old);
            _bindings[key] = binding;
        }

        if (had)
        {
            old.Callback?.Dispose();
        }
    }

    /// <summary>Executes the binding matching the supplied modifiers and key name.</summary>
    public bool TryExecute(bool ctrl, bool shift, bool alt, bool super, string keyName)
    {
        TerminalChordKey chord = KeybindingManager.CreateKey(ctrl, shift, alt, super, keyName);
        Binding binding;
        lock (_sync)
        {
            if (!_bindings.TryGetValue(chord, out binding))
            {
                return false;
            }
        }

        if (binding.SuppressDefault)
        {
            return true;
        }

        if (binding.Action is { } action)
        {
            return _host.TryExecuteAction(action);
        }

        if (binding.Callback is not { } callback)
        {
            return false;
        }

        return callback.InvokeKeybind(out bool handled) && handled;
    }

    internal void Clear()
    {
        Binding[] entries;
        lock (_sync)
        {
            entries = new Binding[_bindings.Count];
            _bindings.Values.CopyTo(entries, 0);
            _bindings.Clear();
        }

        foreach (Binding entry in entries)
        {
            entry.Callback?.Dispose();
        }
    }

    /// <summary>Builds a normalized chord from modifier state and a key name.</summary>
    public static string BuildChord(bool ctrl, bool shift, bool alt, bool super, string keyName)
    {
        return KeybindingManager.BuildChord(ctrl, shift, alt, super, keyName);
    }

    /// <summary>Normalizes a chord using the shared keybinding rules.</summary>
    public static string NormalizeChord(string chord)
    {
        return KeybindingManager.NormalizeChord(chord);
    }

    private readonly record struct Binding(LuaCallbackReference? Callback, TerminalAction? Action, bool SuppressDefault = false);
}
