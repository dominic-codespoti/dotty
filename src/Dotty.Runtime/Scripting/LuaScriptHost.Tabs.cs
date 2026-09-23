using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dotty.Runtime.Panes;
using Dotty.Runtime.Tabs;
using KeraLua;

namespace Dotty.Runtime.Scripting;

public sealed partial class LuaScriptHost
{
    private unsafe partial void RegisterTabs(Lua lua, int dottyIndex)
    {
        lua.NewTable();
        int tabs = lua.AbsIndex(-1);
        SetFunction(lua, tabs, "new", &TabsNewEntry);
        SetFunction(lua, tabs, "close", &TabsCloseEntry);
        SetFunction(lua, tabs, "select", &TabsSelectEntry);
        SetFunction(lua, tabs, "next", &TabsNextEntry);
        SetFunction(lua, tabs, "prev", &TabsPrevEntry);
        SetFunction(lua, tabs, "all", &TabsAllEntry);
        SetFunction(lua, tabs, "get", &TabsGetEntry);
        lua.CreateTable(0, 0);
        int mt = lua.AbsIndex(-1);
        SetFunction(lua, mt, "__index", &TabsIndexEntry);
        lua.SetMetaTable(tabs);
        lua.SetField(dottyIndex, "tabs");
    }

    private static unsafe int TabsIndex(LuaScriptHost host, Lua lua)
    {
        if (IsLuaStringEqualIgnoreCase(lua, 2, "count"))
        {
            lua.PushInteger(host._tabManager.Count);
        }
        else if (IsLuaStringEqualIgnoreCase(lua, 2, "active_index"))
        {
            lua.PushInteger(host._tabManager.ActiveIndex + 1);
        }
        else if (IsLuaStringEqualIgnoreCase(lua, 2, "active"))
        {
            if (host._tabManager.ActiveTab is { } active)
            {
                host.PushTabHandle(lua, active);
            }
            else
            {
                lua.PushNil();
            }
        }
        else
        {
            lua.PushNil();
        }

        return 1;
    }

    private static int TabsNew(LuaScriptHost host, Lua lua)
    {
        host.RequireRuntime(lua, "dotty.tabs.new");
        string? cwd = null;
        string? shell = null;
        int index = lua.Type(1) == LuaType.Table ? 1 : 2;
        if (lua.Type(index) == LuaType.Table)
        {
            using var stack = new LuaStackScope(lua);
            int table = lua.AbsIndex(index);
            lua.GetField(table, "cwd");
            cwd = ReadString(lua, -1);
            lua.Pop(1);
            lua.GetField(table, "shell");
            shell = ReadString(lua, -1);
        }

        var tab = host._services.CreateTab(cwd, shell);
        host.PushTabHandle(lua, tab);
        return 1;
    }

    private static int TabsClose(LuaScriptHost host, Lua lua)
    {
        host.RequireRuntime(lua, "dotty.tabs.close");
        int index = ReadIndex(lua, 1) - 1;
        if (index >= 0 && index < host._tabManager.Tabs.Count)
        {
            host._tabManager.CloseTab(host._tabManager.Tabs[index]);
        }

        return 0;
    }

    private static int TabsSelect(LuaScriptHost host, Lua lua)
    {
        host.RequireRuntime(lua, "dotty.tabs.select");
        int index = ReadIndex(lua, 1) - 1;
        if (index >= 0 && index < host._tabManager.Tabs.Count)
        {
            host._tabManager.SelectTab(index);
        }

        return 0;
    }

    private static int TabsNext(LuaScriptHost host, Lua lua)
    {
        host.RequireRuntime(lua, "dotty.tabs.next");
        host._tabManager.SelectNextTab();
        return 0;
    }

    private static int TabsPrev(LuaScriptHost host, Lua lua)
    {
        host.RequireRuntime(lua, "dotty.tabs.prev");
        host._tabManager.SelectPreviousTab();
        return 0;
    }

    private static int TabsAll(LuaScriptHost host, Lua lua)
    {
        lua.CreateTable(host._tabManager.Count, 0);
        int result = lua.AbsIndex(-1);
        for (int i = 0; i < host._tabManager.Tabs.Count; i++)
        {
            host.PushTabHandle(lua, host._tabManager.Tabs[i]);
            lua.RawSetInteger(result, i + 1);
        }

        return 1;
    }

    private static int TabsGet(LuaScriptHost host, Lua lua)
    {
        int index = ReadIndex(lua, ReadArgument(lua, 1)) - 1;
        if (index < 0 || index >= host._tabManager.Tabs.Count)
        {
            lua.PushNil();
            return 1;
        }

        host.PushTabHandle(lua, host._tabManager.Tabs[index]);
        return 1;
    }

    private static int TabSend(LuaScriptHost host, Lua lua)
    {
        host.RequireRuntime(lua, "tab.send");
        long id = lua.ToInteger(LuaUpvalueIndex1);
        int argument = ReadArgument(lua, 1);
        if (!host._tabHandles.TryGetValue(id, out var tab) ||
            IndexOf(host._tabManager, tab) < 0 ||
            !TryReadLuaUtf8(lua, argument, out ReadOnlySpan<byte> text))
        {
            lua.PushBoolean(false);
            return 1;
        }

        tab.ActivePane.Session.WriteInput(text);
        lua.PushBoolean(true);
        return 1;
    }

    private static int TabClose(LuaScriptHost host, Lua lua)
    {
        host.RequireRuntime(lua, "tab.close");
        long id = lua.ToInteger(LuaUpvalueIndex1);
        bool closed = host._tabHandles.TryGetValue(id, out var tab) &&
            IndexOf(host._tabManager, tab) >= 0 &&
            host._tabManager.CloseTab(tab);
        lua.PushBoolean(closed);
        return 1;
    }

    private static int TabSelect(LuaScriptHost host, Lua lua)
    {
        host.RequireRuntime(lua, "tab.select");
        long id = lua.ToInteger(LuaUpvalueIndex1);
        bool selected = host._tabHandles.TryGetValue(id, out var tab) && IndexOf(host._tabManager, tab) >= 0;
        if (selected)
        {
            host._tabManager.SelectTab(tab!);
        }

        lua.PushBoolean(selected);
        return 1;
    }

    private static int TabPanes(LuaScriptHost host, Lua lua)
    {
        long id = lua.ToInteger(LuaUpvalueIndex1);
        if (!host._tabHandles.TryGetValue(id, out var tab) || IndexOf(host._tabManager, tab) < 0)
        {
            lua.PushNil();
            return 1;
        }

        lua.CreateTable(0, 0);
        int result = lua.AbsIndex(-1);

        var leaves = tab.PaneTree.Leaves;
        for (int i = 0; i < leaves.Count; i++)
        {
            host.PushPaneHandle(lua, leaves[i], tab);
            lua.RawSetInteger(result, i + 1);
        }

        return 1;
    }

    private static int TabIndex(LuaScriptHost host, Lua lua)
    {
        long id = lua.ToInteger(LuaUpvalueIndex1);
        if (!host._tabHandles.TryGetValue(id, out var tab))
        {
            lua.PushNil();
            return 1;
        }

        bool valid = IndexOf(host._tabManager, tab) >= 0;
        if (IsLuaStringEqualIgnoreCase(lua, 2, "id"))
        {
            host.PushGuid(lua, tab.Id);
        }
        else if (IsLuaStringEqualIgnoreCase(lua, 2, "title"))
        {
            host.PushNullableString(lua, valid ? tab.Title : null);
        }
        else if (IsLuaStringEqualIgnoreCase(lua, 2, "cwd"))
        {
            host.PushNullableString(lua, valid ? tab.WorkingDirectory : null);
        }
        else if (IsLuaStringEqualIgnoreCase(lua, 2, "index"))
        {
            int index = valid ? IndexOf(host._tabManager, tab) : -1;
            if (index < 0)
            {
                lua.PushNil();
            }
            else
            {
                lua.PushInteger(index + 1);
            }
        }
        else if (IsLuaStringEqualIgnoreCase(lua, 2, "is_active"))
        {
            lua.PushBoolean(valid && ReferenceEquals(host._tabManager.ActiveTab, tab));
        }
        else if (IsLuaStringEqualIgnoreCase(lua, 2, "is_valid"))
        {
            lua.PushBoolean(valid);
        }
        else if (IsLuaStringEqualIgnoreCase(lua, 2, "has_bell"))
        {
            lua.PushBoolean(valid && tab.HasBellAlert);
        }
        else if (IsLuaStringEqualIgnoreCase(lua, 2, "active_pane"))
        {
            if (valid)
            {
                host.PushPaneHandle(lua, tab.PaneTree.ActivePane, tab);
            }
            else
            {
                lua.PushNil();
            }
        }
        else
        {
            lua.PushNil();
        }

        return 1;
    }

    private unsafe void PushTabHandle(Lua lua, TerminalTab tab)
    {
        long id = GetTabHandleId(tab);
        if (TryPushCachedHandle(lua, _tabHandleCacheReference, id, out int cache))
        {
            return;
        }

        lua.NewTable();
        int table = lua.AbsIndex(-1);
        PushHandleClosure(lua, id, &TabSendEntry);
        lua.SetField(table, "send");
        PushHandleClosure(lua, id, &TabCloseEntry);
        lua.SetField(table, "close");
        PushHandleClosure(lua, id, &TabSelectEntry);
        lua.SetField(table, "select");
        PushHandleClosure(lua, id, &TabPanesEntry);
        lua.SetField(table, "panes");
        lua.CreateTable(0, 1);
        int mt = lua.AbsIndex(-1);
        PushHandleClosure(lua, id, &TabIndexEntry);
        lua.SetField(mt, "__index");
        lua.SetMetaTable(table);
        StoreCachedHandle(lua, cache, id, table);
    }

    private long GetTabHandleId(TerminalTab tab)
    {
        if (_tabHandleIds.TryGetValue(tab, out long id))
        {
            return id;
        }

        id = ++_nextHandleId;
        _tabHandleIds.Add(tab, id);
        _tabHandles.Add(id, tab);
        return id;
    }

    private static int ReadArgument(Lua lua, int first)
    {
        return lua.Type(first) == LuaType.Table ? first + 1 : first;
    }

    private static int ReadIndex(Lua lua, int index)
    {
        return lua.Type(index) == LuaType.Number ? (int)lua.ToInteger(index) : 0;
    }

    private static int IndexOf(TerminalTabManager manager, TerminalTab tab)
    {
        for (int i = 0; i < manager.Tabs.Count; i++)
        {
            if (ReferenceEquals(manager.Tabs[i], tab))
            {
                return i;
            }
        }

        return -1;
    }

    private void PushNullableString(Lua lua, string? value)
    {
        if (value is null)
        {
            lua.PushNil();
        }
        else
        {
            PushLuaString(lua, value);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int TabsIndexEntry(IntPtr s) => CallbackBoundary(s, &TabsIndex);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int TabsNewEntry(IntPtr s) => CallbackBoundary(s, &TabsNew);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int TabsCloseEntry(IntPtr s) => CallbackBoundary(s, &TabsClose);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int TabsSelectEntry(IntPtr s) => CallbackBoundary(s, &TabsSelect);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int TabsNextEntry(IntPtr s) => CallbackBoundary(s, &TabsNext);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int TabsPrevEntry(IntPtr s) => CallbackBoundary(s, &TabsPrev);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int TabsAllEntry(IntPtr s) => CallbackBoundary(s, &TabsAll);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int TabsGetEntry(IntPtr s) => CallbackBoundary(s, &TabsGet);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int TabSendEntry(IntPtr s) => CallbackBoundary(s, &TabSend);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int TabCloseEntry(IntPtr s) => CallbackBoundary(s, &TabClose);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int TabSelectEntry(IntPtr s) => CallbackBoundary(s, &TabSelect);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int TabPanesEntry(IntPtr s) => CallbackBoundary(s, &TabPanes);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int TabIndexEntry(IntPtr s) => CallbackBoundary(s, &TabIndex);
}
