using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dotty.Runtime.Panes;
using Dotty.Runtime.Tabs;
using KeraLua;

namespace Dotty.Runtime.Scripting;

public sealed partial class LuaScriptHost
{
    private unsafe void PushPaneHandle(Lua lua, LeafPane pane, TerminalTab tab)
    {
        var key = (pane, tab);
        if (!_paneHandleIds.TryGetValue(key, out long id))
        {
            id = ++_nextHandleId;
            _paneHandleIds.Add(key, id);
            _paneHandles.Add(id, key);
        }

        if (TryPushCachedHandle(lua, _paneHandleCacheReference, id, out int cache))
        {
            return;
        }

        lua.NewTable();
        int table = lua.AbsIndex(-1);
        PushHandleClosure(lua, id, &PaneSendEntry);
        lua.SetField(table, "send");
        PushHandleClosure(lua, id, &PaneSplitEntry);
        lua.SetField(table, "split");
        PushHandleClosure(lua, id, &PaneFocusEntry);
        lua.SetField(table, "focus");
        PushHandleClosure(lua, id, &PaneCloseEntry);
        lua.SetField(table, "close");
        PushHandleClosure(lua, id, &PaneTextEntry);
        lua.SetField(table, "text");
        PushHandleClosure(lua, id, &PaneSelectionEntry);
        lua.SetField(table, "selection");
        lua.CreateTable(0, 1);
        int mt = lua.AbsIndex(-1);
        PushHandleClosure(lua, id, &PaneIndexEntry);
        lua.SetField(mt, "__index");
        lua.SetMetaTable(table);
        StoreCachedHandle(lua, cache, id, table);
    }

    private static int PaneSend(LuaScriptHost host, Lua lua)
    {
        host.RequireRuntime(lua, "pane.send");
        long id = lua.ToInteger(LuaUpvalueIndex1);
        int argument = ReadPaneArgument(lua, 1);
        if (!host._paneHandles.TryGetValue(id, out var pair) ||
            !IsPaneValid(host, pair.Pane, pair.Tab) ||
            !TryReadLuaUtf8(lua, argument, out ReadOnlySpan<byte> text))
        {
            lua.PushBoolean(false);
            return 1;
        }

        pair.Pane.Session.WriteInput(text);
        lua.PushBoolean(true);
        return 1;
    }
    private static int PaneSplit(LuaScriptHost host, Lua lua)
    {
        host.RequireRuntime(lua, "pane.split");
        long id = lua.ToInteger(LuaUpvalueIndex1);
        int argument = ReadPaneArgument(lua, 1);
        if (!host._paneHandles.TryGetValue(id, out var pair) || !IsPaneValid(host, pair.Pane, pair.Tab))
        {
            lua.PushNil();
            return 1;
        }

        SplitDirection direction = IsLuaStringEqualIgnoreCase(lua, argument, "right")
            ? SplitDirection.Vertical
            : IsLuaStringEqualIgnoreCase(lua, argument, "down")
                ? SplitDirection.Horizontal
                : (SplitDirection)(-1);
        if ((int)direction < 0)
        {
            return host.Fail(lua, "pane.split direction must be 'right' or 'down'");
        }

        string? cwd = null;
        string? shell = null;
        int options = argument + 1;
        if (lua.Type(options) == LuaType.Table)
        {
            using var stack = new LuaStackScope(lua);
            int table = lua.AbsIndex(options);
            lua.GetField(table, "cwd");
            cwd = ReadString(lua, -1);
            lua.Pop(1);
            lua.GetField(table, "shell");
            shell = ReadString(lua, -1);
        }

        LeafPane newPane = host._services.SplitPane(pair.Tab, pair.Pane, direction, cwd, shell);
        host.PushPaneHandle(lua, newPane, pair.Tab);
        return 1;
    }


    private static int PaneFocus(LuaScriptHost host, Lua lua)
    {
        host.RequireRuntime(lua, "pane.focus");
        long id = lua.ToInteger(LuaUpvalueIndex1);
        bool focused = host._paneHandles.TryGetValue(id, out var pair) && IsPaneValid(host, pair.Pane, pair.Tab);
        if (focused)
        {
            host._tabManager.SelectTab(pair.Tab);
            pair.Tab.PaneTree.ActivePane = pair.Pane;
        }

        lua.PushBoolean(focused);
        return 1;
    }

    private static int PaneClose(LuaScriptHost host, Lua lua)
    {
        host.RequireRuntime(lua, "pane.close");
        long id = lua.ToInteger(LuaUpvalueIndex1);
        bool closed = false;
        if (host._paneHandles.TryGetValue(id, out var pair) && IsPaneValid(host, pair.Pane, pair.Tab))
        {
            if (pair.Tab.PaneTree.Leaves.Count > 1)
            {
                closed = pair.Tab.PaneTree.Close(pair.Pane);
            }
            else
            {
                closed = host._tabManager.CloseTab(pair.Tab);
            }
        }

        lua.PushBoolean(closed);
        return 1;
    }

    private static int PaneText(LuaScriptHost host, Lua lua)
    {
        long id = lua.ToInteger(LuaUpvalueIndex1);
        if (!host._paneHandles.TryGetValue(id, out var pair) || !IsPaneValid(host, pair.Pane, pair.Tab))
        {
            lua.PushNil();
            return 1;
        }

        var buffer = pair.Pane.Session.Adapter.Buffer;
        string text = string.Empty;
        buffer.WithSyncRoot(() =>
        {
            using var snapshot = buffer.CaptureRenderSnapshotVisible(scrollOffset: pair.Pane.ScrollOffset);
            var rows = new List<string>(snapshot.Rows);
            for (int row = 0; row < snapshot.Rows; row++)
            {
                rows.Add(snapshot.GetVisibleRowText(row).TrimEnd(' '));
            }

            while (rows.Count > 0 && rows[^1].Length == 0)
            {
                rows.RemoveAt(rows.Count - 1);
            }

            text = string.Join("\n", rows);
        });
        host.PushLuaString(lua, text);
        return 1;
    }
    private static int PaneSelection(LuaScriptHost host, Lua lua)
    {
        long id = lua.ToInteger(LuaUpvalueIndex1);
        if (!host._paneHandles.TryGetValue(id, out var pair) || !IsPaneValid(host, pair.Pane, pair.Tab) || !pair.Pane.Selection.HasSelection)
        {
            lua.PushNil();
            return 1;
        }

        host.PushLuaString(lua, pair.Pane.Selection.GetSelectedText(pair.Pane.Session.Adapter.Buffer));
        return 1;
    }
    private static int PaneIndex(LuaScriptHost host, Lua lua)
    {
        long id = lua.ToInteger(LuaUpvalueIndex1);
        if (!host._paneHandles.TryGetValue(id, out var pair))
        {
            lua.PushNil();
            return 1;
        }

        bool valid = IsPaneValid(host, pair.Pane, pair.Tab);
        if (IsLuaStringEqualIgnoreCase(lua, 2, "id"))
        {
            host.PushLuaString(lua, pair.Pane.Id);
        }
        else if (IsLuaStringEqualIgnoreCase(lua, 2, "tab"))
        {
            if (valid)
            {
                host.PushTabHandle(lua, pair.Tab);
            }
            else
            {
                lua.PushNil();
            }
        }
        else if (IsLuaStringEqualIgnoreCase(lua, 2, "index"))
        {
            int index = valid ? IndexOfPane(pair.Tab, pair.Pane) : -1;
            if (index < 0)
            {
                lua.PushNil();
            }
            else
            {
                lua.PushInteger(index + 1);
            }
        }
        else if (IsLuaStringEqualIgnoreCase(lua, 2, "cols"))
        {
            PushValidInteger(lua, valid, pair.Pane.Columns);
        }
        else if (IsLuaStringEqualIgnoreCase(lua, 2, "rows"))
        {
            PushValidInteger(lua, valid, pair.Pane.Rows);
        }
        else if (IsLuaStringEqualIgnoreCase(lua, 2, "is_active"))
        {
            lua.PushBoolean(valid && ReferenceEquals(host._tabManager.ActiveTab, pair.Tab) &&
                ReferenceEquals(pair.Tab.PaneTree.ActivePane, pair.Pane));
        }
        else if (IsLuaStringEqualIgnoreCase(lua, 2, "is_valid"))
        {
            lua.PushBoolean(valid);
        }
        else if (IsLuaStringEqualIgnoreCase(lua, 2, "is_alt_screen"))
        {
            lua.PushBoolean(valid && pair.Pane.Session.Adapter.Buffer.IsAlternateScreenActive);
        }
        else if (IsLuaStringEqualIgnoreCase(lua, 2, "cursor"))
        {
            if (valid)
            {
                var buffer = pair.Pane.Session.Adapter.Buffer;
                lua.NewTable();
                host.PushLuaString(lua, "row");
                lua.PushInteger(buffer.CursorRow + 1);
                lua.SetTable(-3);
                host.PushLuaString(lua, "col");
                lua.PushInteger(buffer.CursorCol + 1);
                lua.SetTable(-3);
            }
            else
            {
                lua.PushNil();
            }
        }
        else if (IsLuaStringEqualIgnoreCase(lua, 2, "scroll_offset"))
        {
            PushValidInteger(lua, valid, pair.Pane.ScrollOffset);
        }
        else
        {
            lua.PushNil();
        }

        return 1;
    }



    private static bool IsPaneValid(LuaScriptHost host, LeafPane pane, TerminalTab tab)
    {
        return IndexOf(host._tabManager, tab) >= 0 && IndexOfPane(tab, pane) >= 0;
    }

    private static int IndexOfPane(TerminalTab tab, LeafPane pane)
    {
        var leaves = tab.PaneTree.Leaves;
        for (int i = 0; i < leaves.Count; i++)
        {
            if (ReferenceEquals(leaves[i], pane))
            {
                return i;
            }
        }

        return -1;
    }

    private static int ReadPaneArgument(Lua lua, int first)
    {
        return lua.Type(first) == LuaType.Table ? first + 1 : first;
    }

    private static void PushValidInteger(Lua lua, bool valid, int value)
    {
        if (valid)
        {
            lua.PushInteger(value);
        }
        else
        {
            lua.PushNil();
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int PaneSendEntry(IntPtr s) => CallbackBoundary(s, &PaneSend);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int PaneSplitEntry(IntPtr s) => CallbackBoundary(s, &PaneSplit);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int PaneFocusEntry(IntPtr s) => CallbackBoundary(s, &PaneFocus);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int PaneCloseEntry(IntPtr s) => CallbackBoundary(s, &PaneClose);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int PaneTextEntry(IntPtr s) => CallbackBoundary(s, &PaneText);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int PaneSelectionEntry(IntPtr s) => CallbackBoundary(s, &PaneSelection);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int PaneIndexEntry(IntPtr s) => CallbackBoundary(s, &PaneIndex);
}
