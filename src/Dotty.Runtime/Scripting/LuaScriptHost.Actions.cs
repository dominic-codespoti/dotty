using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dotty.Abstractions.Config;
using KeraLua;

namespace Dotty.Runtime.Scripting;

public sealed partial class LuaScriptHost
{
    private unsafe partial void RegisterActions(Lua lua, int dottyIndex)
    {
        unsafe
        {
            SetFunction(lua, dottyIndex, "action", &ActionEntry);
        }
        lua.NewTable();
        int actions = lua.AbsIndex(-1);
        int index = 0;
        foreach (var info in ActionRegistry.All)
        {
            if (info.Action == TerminalAction.None)
            {
                continue;
            }

            PushLuaString(lua, info.Name);
            lua.RawSetInteger(actions, ++index);
        }

        lua.SetField(dottyIndex, "actions");
    }

    private static int Action(LuaScriptHost host, Lua lua)
    {
        host.RequireRuntime(lua, "dotty.action");

        string? name = ReadString(lua, 1);
        if (name == null || !ActionRegistry.TryParse(name, out BoundAction action, out _) ||
            action.Action == TerminalAction.None)
        {
            return host.Fail(lua, $"unknown action '{name ?? string.Empty}'");
        }

        lua.PushBoolean(host._services.TryExecuteAction(action));
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int ActionEntry(IntPtr state) => CallbackBoundary(state, &Action);
}
