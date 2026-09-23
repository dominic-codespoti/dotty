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
        foreach (TerminalAction action in Enum.GetValues<TerminalAction>())
        {
            if (action == TerminalAction.None)
            {
                continue;
            }

            PushLuaString(lua, Enum.GetName(action)!);
            lua.RawSetInteger(actions, ++index);
        }

        lua.SetField(dottyIndex, "actions");
    }

    private static int Action(LuaScriptHost host, Lua lua)
    {
        host.RequireRuntime(lua, "dotty.action");

        string? name = ReadString(lua, 1);
        if (name == null || !Enum.TryParse(name, true, out TerminalAction action) ||
            action == TerminalAction.None ||
            !string.Equals(Enum.GetName(action), name, StringComparison.OrdinalIgnoreCase))
        {
            return host.Fail(lua, $"unknown action '{name ?? string.Empty}'");
        }

        lua.PushBoolean(host._services.TryExecuteAction(action));
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int ActionEntry(IntPtr state) => CallbackBoundary(state, &Action);
}
