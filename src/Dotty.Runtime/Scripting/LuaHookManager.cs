using System;
using System.Collections.Generic;
using Dotty.Runtime.Panes;
using Dotty.Runtime.Text;
using Dotty.Runtime.Tabs;

namespace Dotty.Runtime.Scripting;

public sealed class LuaHookManager
{
    private readonly object _sync = new();
    private readonly Dictionary<string, LuaCallbackReference[]> _hooks = new(StringComparer.OrdinalIgnoreCase);

    internal LuaScriptHost? Owner { get; set; }

    internal void Register(string name, LuaCallbackReference callback)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            callback.Dispose();
            return;
        }

        lock (_sync)
        {
            string key = name.Trim();
            if (_hooks.TryGetValue(key, out LuaCallbackReference[]? previous))
            {
                var updated = new LuaCallbackReference[previous.Length + 1];
                previous.CopyTo(updated, 0);
                updated[^1] = callback;
                _hooks[key] = updated;
            }
            else
            {
                _hooks[key] = new[] { callback };
            }
        }
    }

    internal bool HasHandlers(string eventName)
    {
        LuaCallbackReference[] callbacks = Get(eventName);
        return callbacks.Length != 0;
    }

    internal void Clear()
    {
        lock (_sync)
        {
            foreach (LuaCallbackReference[] callbacks in _hooks.Values)
            {
                foreach (LuaCallbackReference callback in callbacks)
                {
                    callback.Dispose();
                }
            }

            _hooks.Clear();
        }
    }

    internal void Emit(string eventName)
    {
        LuaCallbackReference[] callbacks = Get(eventName);
        for (int i = 0; i < callbacks.Length; i++)
        {
            callbacks[i].Invoke(out _);
        }
    }

    internal void Emit(string eventName, LuaValue arg0)
    {
        LuaCallbackReference[] callbacks = Get(eventName);
        for (int i = 0; i < callbacks.Length; i++)
        {
            callbacks[i].Invoke(arg0, out _);
        }
    }

    internal void Emit(string eventName, LuaValue arg0, LuaValue arg1)
    {
        LuaCallbackReference[] callbacks = Get(eventName);
        for (int i = 0; i < callbacks.Length; i++)
        {
            callbacks[i].Invoke(arg0, arg1, out _);
        }
    }

    internal void Emit(string eventName, LuaValue arg0, LuaValue arg1, LuaValue arg2)
    {
        LuaCallbackReference[] callbacks = Get(eventName);
        for (int i = 0; i < callbacks.Length; i++)
        {
            callbacks[i].Invoke(arg0, arg1, arg2, out _);
        }
    }

    private bool TryInvokeFirst(string eventName, LuaValue arg0, out LuaValue result)
    {
        LuaCallbackReference[] callbacks = Get(eventName);
        for (int i = 0; i < callbacks.Length; i++)
        {
            if (callbacks[i].Invoke(arg0, out result) && !result.IsNil)
            {
                return true;
            }
        }

        result = LuaValue.Nil;
        return false;
    }


    private LuaCallbackReference[] Get(string name)
    {
        lock (_sync)
        {
            return _hooks.TryGetValue(name, out LuaCallbackReference[]? callbacks)
                ? callbacks
                : Array.Empty<LuaCallbackReference>();
        }
    }

    public bool TryFormatTabTitle(TerminalTab tab, int index, ReusableTextBuffer destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (Owner == null)
        {
            return false;
        }

        LuaValue argument = LuaValue.FromTab(tab);
        LuaCallbackReference[] callbacks = Get("format_tab_title");
        for (int i = 0; i < callbacks.Length; i++)
        {
            if (callbacks[i].InvokeString(argument, destination))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Runs the <c>open_url</c> hooks with the canonical URL and its <paramref name="source"/>
    /// (<c>"explicit"</c> for OSC 8, <c>"detected"</c> for scanned text). Returns true when a
    /// hook returned <c>true</c> (handled). One-argument callbacks ignore the second argument.
    /// </summary>
    public bool TryOpenUrl(string url, string source)
    {
        if (Owner == null)
        {
            return false;
        }

        LuaCallbackReference[] callbacks = Get("open_url");
        LuaValue urlArg = LuaValue.From(url);
        LuaValue sourceArg = LuaValue.From(source);
        for (int i = 0; i < callbacks.Length; i++)
        {
            if (callbacks[i].Invoke(urlArg, sourceArg, out LuaValue value)
                && !value.IsNil)
            {
                return value.TryGetBoolean(out bool handled) && handled;
            }
        }

        return false;
    }

    public bool TryFormatStatus(ReusableTextBuffer destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (Owner == null)
        {
            return false;
        }

        LuaCallbackReference[] callbacks = Get("update_status");
        for (int i = 0; i < callbacks.Length; i++)
        {
            if (callbacks[i].InvokeString(destination))
            {
                return true;
            }
        }

        return false;
    }

    public bool AllowClipboardWrite(LeafPane pane, TerminalTab tab, string text) =>
        AllowClipboardAccess("clipboard_write", pane, tab, text);

    /// <summary>
    /// Runs the <c>clipboard_read</c> hooks for an OSC 52 read request. <paramref name="selection"/> is
    /// <c>"clipboard"</c> or <c>"primary"</c> (primary is served on X11 only). Same veto semantics as
    /// <see cref="AllowClipboardWrite"/>: the first non-nil result decides (<c>false</c> denies), a nil result
    /// defers to the next hook, and a callback invocation error denies. No handler allows.
    /// </summary>
    public bool AllowClipboardRead(LeafPane pane, TerminalTab tab, string selection) =>
        AllowClipboardAccess("clipboard_read", pane, tab, selection);

    private bool AllowClipboardAccess(string hook, LeafPane pane, TerminalTab tab, string argument)
    {
        if (Owner == null)
            return true;

        LuaCallbackReference[] callbacks = Get(hook);
        for (int i = 0; i < callbacks.Length; i++)
        {
            if (!callbacks[i].Invoke(LuaValue.FromPane(pane, tab), LuaValue.From(argument), out LuaValue value))
                return false;
            if (value.IsNil)
                continue;
            return !value.TryGetBoolean(out bool allow) || allow;
        }

        return true;
    }
}
