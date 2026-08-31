using System;
using System.Collections.Generic;
using Dotty.Runtime.Tabs;
using NLua;

namespace Dotty.Runtime.Scripting;

/// <summary>
/// Manages user-registered event hooks in Lua.
/// </summary>
public sealed class LuaHookManager
{
    private readonly Dictionary<string, List<LuaFunction>> _hooks = new(StringComparer.OrdinalIgnoreCase);

    public void Register(string eventName, LuaFunction callback)
    {
        if (string.IsNullOrWhiteSpace(eventName) || callback == null) return;
        string key = eventName.Trim().ToLowerInvariant();

        if (!_hooks.TryGetValue(key, out var list))
        {
            list = new List<LuaFunction>();
            _hooks[key] = list;
        }

        list.Add(callback);
    }

    public string? FormatTabTitle(TerminalTab tab, int index)
    {
        if (!_hooks.TryGetValue("format_tab_title", out var list) || list.Count == 0)
        {
            return null;
        }

        var handle = new LuaTabHandle(tab, index);
        foreach (var func in list)
        {
            try
            {
                var result = func.Call(handle);
                if (result != null && result.Length > 0 && result[0] is string formatted && !string.IsNullOrWhiteSpace(formatted))
                {
                    return formatted;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Lua Hook Error] 'format_tab_title': {ex.Message}");
            }
        }

        return null;
    }

    public bool TryOpenUrl(string url)
    {
        if (!_hooks.TryGetValue("open_url", out var list) || list.Count == 0)
        {
            return false;
        }

        foreach (var func in list)
        {
            try
            {
                var result = func.Call(url);
                if (result != null && result.Length > 0 && result[0] is bool handled && handled)
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Lua Hook Error] 'open_url': {ex.Message}");
            }
        }

        return false;
    }

    public void Clear()
    {
        _hooks.Clear();
    }
}
