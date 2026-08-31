using System;
using Dotty.Runtime.Config;
using NLua;

namespace Dotty.Runtime.Scripting;

/// <summary>
/// Lua proxy for reading and mutating <see cref="DottyUserConfig"/> from Lua scripts.
/// </summary>
public sealed class LuaConfigProxy
{
    private readonly DottyUserConfig _config;

    public LuaConfigProxy(DottyUserConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public string theme
    {
        get => _config.Theme;
        set => _config.Theme = value ?? "DarkPlus";
    }

    public void apply_table(LuaTable table)
    {
        if (table == null) return;

        foreach (var keyObj in table.Keys)
        {
            string key = keyObj?.ToString()?.ToLowerInvariant() ?? string.Empty;
            var val = table[keyObj];

            switch (key)
            {
                case "theme" when val is string t:
                    _config.Theme = t;
                    break;
                case "font" when val is LuaTable fontTable:
                    ApplyFontTable(fontTable);
                    break;
                case "window" when val is LuaTable winTable:
                    ApplyWindowTable(winTable);
                    break;
                case "tab_bar" or "tabbar" when val is LuaTable tabTable:
                    ApplyTabBarTable(tabTable);
                    break;
                case "cursor" when val is LuaTable cursorTable:
                    ApplyCursorTable(cursorTable);
                    break;
            }
        }
    }

    private void ApplyFontTable(LuaTable table)
    {
        foreach (var keyObj in table.Keys)
        {
            string k = keyObj?.ToString()?.ToLowerInvariant() ?? string.Empty;
            var val = table[keyObj];
            switch (k)
            {
                case "family" when val is string fam:
                    _config.Font.Family = fam;
                    break;
                case "size" when val is double or long or int:
                    _config.Font.Size = Convert.ToDouble(val);
                    break;
                case "line_height" or "lineheight" when val is double or long or int:
                    _config.Font.LineHeight = Convert.ToDouble(val);
                    break;
            }
        }
    }

    private void ApplyWindowTable(LuaTable table)
    {
        foreach (var keyObj in table.Keys)
        {
            string k = keyObj?.ToString()?.ToLowerInvariant() ?? string.Empty;
            var val = table[keyObj];
            switch (k)
            {
                case "opacity" when val is double or long or int:
                    _config.Window.Opacity = Convert.ToDouble(val);
                    break;
                case "title" when val is string title:
                    _config.Window.Title = title;
                    break;
                case "padding" when val is LuaTable padTable:
                    ApplyPaddingTable(padTable);
                    break;
            }
        }
    }

    private void ApplyPaddingTable(LuaTable table)
    {
        foreach (var keyObj in table.Keys)
        {
            string k = keyObj?.ToString()?.ToLowerInvariant() ?? string.Empty;
            var val = table[keyObj];
            if (val is double or long or int)
            {
                double v = Convert.ToDouble(val);
                switch (k)
                {
                    case "left": _config.Window.Padding.Left = v; break;
                    case "top": _config.Window.Padding.Top = v; break;
                    case "right": _config.Window.Padding.Right = v; break;
                    case "bottom": _config.Window.Padding.Bottom = v; break;
                }
            }
        }
    }

    private void ApplyTabBarTable(LuaTable table)
    {
        foreach (var keyObj in table.Keys)
        {
            string k = keyObj?.ToString()?.ToLowerInvariant() ?? string.Empty;
            var val = table[keyObj];
            switch (k)
            {
                case "show" when val is bool b:
                    _config.TabBar.Show = b;
                    break;
                case "height" when val is double or long or int:
                    _config.TabBar.Height = Convert.ToDouble(val);
                    break;
                case "style" when val is string s:
                    _config.TabBar.Style = s;
                    break;
            }
        }
    }

    private void ApplyCursorTable(LuaTable table)
    {
        foreach (var keyObj in table.Keys)
        {
            string k = keyObj?.ToString()?.ToLowerInvariant() ?? string.Empty;
            var val = table[keyObj];
            switch (k)
            {
                case "shape" when val is string s:
                    _config.Cursor.Shape = s;
                    break;
                case "blink" when val is bool b:
                    _config.Cursor.Blink = b;
                    break;
                case "blink_interval_ms" or "blinkintervalms" when val is double or long or int:
                    _config.Cursor.BlinkIntervalMs = Convert.ToInt32(val);
                    break;
            }
        }
    }
}
