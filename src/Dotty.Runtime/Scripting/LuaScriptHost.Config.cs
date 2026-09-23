using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dotty.Runtime.Config;
using KeraLua;

namespace Dotty.Runtime.Scripting;

public sealed partial class LuaScriptHost
{
    private enum ConfigValueKind
    {
        String,
        Number,
        Integer,
        Boolean,
        Map
    }

    private sealed record ConfigField(
        ConfigValueKind Kind,
        Func<DottyUserConfig, object?> Read,
        Action<DottyUserConfig, object?> Write);

    private static readonly Dictionary<string, ConfigField> ConfigFields = CreateConfigFields();

    private static Dictionary<string, ConfigField> CreateConfigFields() => new(StringComparer.Ordinal)
    {
        ["theme"] = new(ConfigValueKind.String, c => c.Theme, (c, v) => c.Theme = (string)v!),
        ["selection_color"] = new(ConfigValueKind.String, c => c.SelectionColor, (c, v) => c.SelectionColor = (string?)v),
        ["font.family"] = new(ConfigValueKind.String, c => c.Font.Family, (c, v) => c.Font.Family = (string)v!),
        ["font.size"] = new(ConfigValueKind.Number, c => c.Font.Size, (c, v) => c.Font.Size = (double)v!),
        ["font.line_height"] = new(ConfigValueKind.Number, c => c.Font.LineHeight, (c, v) => c.Font.LineHeight = (double)v!),
        ["window.opacity"] = new(ConfigValueKind.Number, c => c.Window.Opacity, (c, v) => c.Window.Opacity = (double)v!),
        ["window.title"] = new(ConfigValueKind.String, c => c.Window.Title, (c, v) => c.Window.Title = (string)v!),
        ["window.padding.left"] = new(ConfigValueKind.Number, c => c.Window.Padding.Left, (c, v) => c.Window.Padding.Left = (double)v!),
        ["window.padding.top"] = new(ConfigValueKind.Number, c => c.Window.Padding.Top, (c, v) => c.Window.Padding.Top = (double)v!),
        ["window.padding.right"] = new(ConfigValueKind.Number, c => c.Window.Padding.Right, (c, v) => c.Window.Padding.Right = (double)v!),
        ["window.padding.bottom"] = new(ConfigValueKind.Number, c => c.Window.Padding.Bottom, (c, v) => c.Window.Padding.Bottom = (double)v!),
        ["tab_bar.show"] = new(ConfigValueKind.Boolean, c => c.TabBar.Show, (c, v) => c.TabBar.Show = (bool)v!),
        ["tab_bar.height"] = new(ConfigValueKind.Number, c => c.TabBar.Height, (c, v) => c.TabBar.Height = (double)v!),
        ["tab_bar.style"] = new(ConfigValueKind.String, c => c.TabBar.Style, (c, v) => c.TabBar.Style = (string)v!),
        ["cursor.shape"] = new(ConfigValueKind.String, c => c.Cursor.Shape, (c, v) => c.Cursor.Shape = (string)v!),
        ["cursor.blink"] = new(ConfigValueKind.Boolean, c => c.Cursor.Blink, (c, v) => c.Cursor.Blink = (bool)v!),
        ["cursor.blink_interval_ms"] = new(ConfigValueKind.Integer, c => c.Cursor.BlinkIntervalMs, (c, v) => c.Cursor.BlinkIntervalMs = (int)v!),
        ["panes.divider_thickness"] = new(ConfigValueKind.Number, c => c.Panes.DividerThickness, (c, v) => c.Panes.DividerThickness = (double)v!),
        ["panes.active_border"] = new(ConfigValueKind.Boolean, c => c.Panes.ActiveBorder, (c, v) => c.Panes.ActiveBorder = (bool)v!),
        ["keybindings"] = new(ConfigValueKind.Map, c => c.Keybindings, (c, v) => c.Keybindings = (Dictionary<string, string>)v!)
    };

    private unsafe partial void RegisterConfig(Lua lua, int dottyIndex)
    {
        lua.NewTable();
        int config = lua.AbsIndex(-1);
        SetFunction(lua, config, "apply_table", &ConfigApplyEntry);
        SetFunction(lua, config, "apply", &ConfigApplyEntry);
        lua.CreateTable(0, 2);
        int metatable = lua.AbsIndex(-1);
        SetFunction(lua, metatable, "__index", &ConfigIndexEntry);
        SetFunction(lua, metatable, "__newindex", &ConfigNewIndexEntry);
        lua.SetMetaTable(config);
        lua.SetField(dottyIndex, "config");
    }

    private static unsafe int ConfigIndex(LuaScriptHost host, Lua lua)
    {
        string? key = ReadString(lua, 2);
        if (key == null)
        {
            lua.PushNil();
            return 1;
        }

        key = NormalizeConfigKey(key);
        if (key is "apply" or "apply_table")
        {
            lua.GetField(1, key);
            return 1;
        }

        if (key == "line_height")
        {
            key = "font.line_height";
        }

        if (key == "blink_interval_ms")
        {
            key = "cursor.blink_interval_ms";
        }

        if (key is "font" or "window" or "window.padding" or "tab_bar" or "cursor" or "panes")
        {
            host.PushConfigProxyTable(lua, key);
            return 1;
        }

        if (ConfigFields.ContainsKey(key))
        {
            host.PushConfigValue(lua, key);
            return 1;
        }

        lua.PushNil();
        return 1;
    }

    private static int ConfigNewIndex(LuaScriptHost host, Lua lua)
    {
        string? key = ReadString(lua, 2);
        if (key == null)
        {
            return 0;
        }

        key = NormalizeConfigKey(key);
        if (key == "line_height")
        {
            key = "font.line_height";
        }

        if (key == "blink_interval_ms")
        {
            key = "cursor.blink_interval_ms";
        }

        if (key is "font" or "window" or "window.padding" or "tab_bar" or "cursor" or "panes")
        {
            if (lua.Type(3) != LuaType.Table)
            {
                throw new InvalidOperationException($"Invalid value type for dotty.config.{key}.");
            }

            host.ApplyConfigTable(lua, 3, key);
            return 0;
        }

        if (ConfigFields.ContainsKey(key))
        {
            host.SetConfigValue(lua, key, 3);
            return 0;
        }

        return 0;
    }

    private unsafe void PushConfigProxyTable(Lua lua, string path)
    {
        lua.NewTable();
        lua.CreateTable(0, 2);
        int metatable = lua.AbsIndex(-1);
        PushWrappedClosure(lua, path, &ConfigIndexPathEntry);
        lua.SetField(metatable, "__index");
        PushWrappedClosure(lua, path, &ConfigNewIndexPathEntry);
        lua.SetField(metatable, "__newindex");
        lua.SetMetaTable(-2);
    }

    private static unsafe int ConfigIndexPath(LuaScriptHost host, Lua lua)
    {
        string parent = lua.ToString(LuaUpvalueIndex1) ?? "";
        string? key = ReadString(lua, 2);
        if (key == null)
        {
            lua.PushNil();
            return 1;
        }

        string path = NormalizeConfigKey(parent + "." + key);
        if (path is "window.padding" or "font" or "window" or "tab_bar" or "cursor" or "panes")
        {
            host.PushConfigProxyTable(lua, path);
            return 1;
        }

        if (ConfigFields.ContainsKey(path))
        {
            host.PushConfigValue(lua, path);
        }
        else
        {
            lua.PushNil();
        }

        return 1;
    }

    private static int ConfigNewIndexPath(LuaScriptHost host, Lua lua)
    {
        string parent = lua.ToString(LuaUpvalueIndex1) ?? "";
        string? key = ReadString(lua, 2);
        if (key != null)
        {
            host.SetConfigValue(lua, NormalizeConfigKey(parent + "." + key), 3);
        }

        return 0;
    }

    private void PushConfigValue(Lua lua, string path)
    {
        ConfigField field = ConfigFields[path];
        object? value = field.Read(ConfigProxy.Config);
        switch (field.Kind)
        {
            case ConfigValueKind.String:
                if (value == null)
                {
                    lua.PushNil();
                }
                else
                {
                    PushLuaString(lua, (string)value);
                }

                break;
            case ConfigValueKind.Number:
                lua.PushNumber((double)value!);
                break;
            case ConfigValueKind.Integer:
                lua.PushInteger((int)value!);
                break;
            case ConfigValueKind.Boolean:
                lua.PushBoolean((bool)value!);
                break;
            case ConfigValueKind.Map:
                lua.NewTable();
                foreach (KeyValuePair<string, string> item in (Dictionary<string, string>)value!)
                {
                    PushLuaString(lua, item.Key);
                    PushLuaString(lua, item.Value);
                    lua.SetTable(-3);
                }

                break;
        }
    }

    private void SetConfigValue(Lua lua, string path, int index)
    {
        if (!ConfigFields.TryGetValue(path, out ConfigField? field) || field is null)
        {
            return;
        }

        LuaType type = lua.Type(index);
        object? value = null;
        bool valid = false;
        switch (field.Kind)
        {
            case ConfigValueKind.String:
                valid = type == LuaType.String || path == "selection_color" && type == LuaType.Nil;
                if (valid)
                {
                    value = type == LuaType.Nil ? null : lua.ToString(index);
                }

                break;
            case ConfigValueKind.Number:
                valid = type == LuaType.Number;
                if (valid)
                {
                    value = lua.ToNumber(index);
                }

                break;
            case ConfigValueKind.Integer:
                valid = type == LuaType.Number;
                if (valid)
                {
                    value = (int)lua.ToInteger(index);
                }

                break;
            case ConfigValueKind.Boolean:
                valid = type == LuaType.Boolean;
                if (valid)
                {
                    value = lua.ToBoolean(index);
                }

                break;
            case ConfigValueKind.Map:
                valid = type == LuaType.Table;
                if (valid)
                {
                    value = ReadKeybindings(lua, index);
                }

                break;
        }

        if (!valid)
        {
            throw new InvalidOperationException($"Invalid value type for dotty.config.{path}.");
        }

        field.Write(ConfigProxy.Config, value);
        MarkConfigDirty();
    }

    internal void ApplyConfigTable(Lua lua, int index, string prefix = "")
    {
        using var stack = new LuaStackScope(lua);
        int table = lua.AbsIndex(index);
        lua.PushNil();
        while (lua.Next(table))
        {
            string? key = ReadString(lua, -2);
            if (key != null)
            {
                string path = NormalizeConfigKey((prefix.Length == 0 ? "" : prefix + ".") + key);
                if (lua.Type(-1) == LuaType.Table
                    && (path is "font" or "window" or "window.padding" or "tab_bar" or "cursor" or "panes"))
                {
                    ApplyConfigTable(lua, -1, path);
                }
                else if (ConfigFields.ContainsKey(path))
                {
                    SetConfigValue(lua, path, -1);
                }
            }

            lua.Pop(1);
        }
    }

    private static Dictionary<string, string> ReadKeybindings(Lua lua, int index)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var stack = new LuaStackScope(lua);
        int table = lua.AbsIndex(index);
        lua.PushNil();
        while (lua.Next(table))
        {
            string? key = ReadString(lua, -2);
            string? value = ReadString(lua, -1);
            if (key != null && value != null)
            {
                map[key] = value;
            }

            lua.Pop(1);
        }

        return map;
    }

    private static string NormalizeConfigKey(string key)
    {
        return key.Trim().ToLowerInvariant()
            .Replace("tabbar", "tab_bar")
            .Replace("lineheight", "line_height")
            .Replace("blinkintervalms", "blink_interval_ms")
            .Replace("selectioncolor", "selection_color")
            .Replace("dividerthickness", "divider_thickness")
            .Replace("activeborder", "active_border");
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int ConfigIndexEntry(IntPtr state) => CallbackBoundary(state, &ConfigIndex);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int ConfigNewIndexEntry(IntPtr state) => CallbackBoundary(state, &ConfigNewIndex);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int ConfigApplyEntry(IntPtr state) => CallbackBoundary(state, &ConfigApply);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int ConfigIndexPathEntry(IntPtr state) => CallbackBoundary(state, &ConfigIndexPath);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int ConfigNewIndexPathEntry(IntPtr state) => CallbackBoundary(state, &ConfigNewIndexPath);

    private static int ConfigApply(LuaScriptHost host, Lua lua)
    {
        int index = lua.Type(2) == LuaType.Table ? 2 : 1;
        if (lua.Type(index) == LuaType.Table)
        {
            host.ApplyConfigTable(lua, index);
        }

        return 0;
    }
}
