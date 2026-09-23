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

    private bool TryInvokeFirst(string eventName, LuaValue arg0, LuaValue arg1, out LuaValue result)
    {
        LuaCallbackReference[] callbacks = Get(eventName);
        for (int i = 0; i < callbacks.Length; i++)
        {
            if (callbacks[i].Invoke(arg0, arg1, out result) && !result.IsNil)
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

    public bool TryOpenUrl(string url)
    {
        return Owner != null
            && TryInvokeFirst("open_url", LuaValue.From(url), out LuaValue value)
            && value.TryGetBoolean(out bool handled)
            && handled;
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

    public bool AllowClipboardWrite(LeafPane pane, TerminalTab tab, string text)
    {
        return !(Owner != null
            && TryInvokeFirst("clipboard_write", LuaValue.FromPane(pane, tab), LuaValue.From(text), out LuaValue value)
            && value.TryGetBoolean(out bool allow)
            && !allow);
    }
}
