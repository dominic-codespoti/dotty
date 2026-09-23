using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Dotty.Runtime.Tabs;
using KeraLua;

namespace Dotty.Runtime.Scripting;

public sealed partial class LuaScriptHost
{
    private readonly Dictionary<long, LuaTimerEntry> _luaTimers = new();
    private long _nextLuaTimerId;
    private long _luaTimerGeneration;
    private readonly object _timerCallbackQueueLock = new();
    private readonly Queue<LuaTimerEntry> _pendingTimerCallbacks = new(32);
    private bool _timerDrainScheduled;

    private sealed class LuaTimerEntry
    {
        internal LuaTimerEntry(long id, long generation, int interval, bool repeating, LuaCallbackReference callback)
        {
            Id = id;
            Generation = generation;
            Interval = interval;
            Repeating = repeating;
            Callback = callback;
        }

        internal long Id { get; }
        internal long Generation { get; }
        internal int Interval { get; }
        internal bool Repeating { get; }
        internal LuaCallbackReference Callback { get; }
        internal Timer? Timer { get; set; }
    }

    private unsafe partial void RegisterUtilities(Lua lua, int dottyIndex)
    {
        unsafe
        {
            SetFunction(lua, dottyIndex, "after", &AfterEntry);
            SetFunction(lua, dottyIndex, "every", &EveryEntry);
            SetFunction(lua, dottyIndex, "cancel", &CancelEntry);
            SetFunction(lua, dottyIndex, "spawn", &SpawnEntry);
        }
    }

    private partial void ResetUtilities()
    {
        _luaTimerGeneration++;
        foreach (LuaTimerEntry entry in _luaTimers.Values)
        {
            entry.Timer?.Dispose();
            entry.Callback.Dispose();
        }

        _luaTimers.Clear();
        lock (_timerCallbackQueueLock)
        {
            _pendingTimerCallbacks.Clear();
        }
    }

    private static int After(LuaScriptHost host, Lua lua) => host.CreateTimer(lua, false);

    private static int Every(LuaScriptHost host, Lua lua) => host.CreateTimer(lua, true);

    private int CreateTimer(Lua lua, bool repeating)
    {
        if (lua.Type(1) != LuaType.Number || lua.Type(2) != LuaType.Function)
        {
            return Fail(lua, $"dotty.{(repeating ? "every" : "after")} expects milliseconds and a callback.");
        }

        double milliseconds = lua.ToNumber(1);
        if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds < 0 || milliseconds > int.MaxValue)
        {
            return Fail(lua, "Timer interval must be a non-negative number of milliseconds.");
        }

        int interval = (int)milliseconds;
        if (repeating)
        {
            interval = Math.Max(interval, 16);
        }

        var callback = CaptureFunction(lua, 2);
        long id = ++_nextLuaTimerId;
        var entry = new LuaTimerEntry(id, _luaTimerGeneration, interval, repeating, callback);
        entry.Timer = new Timer(_ => QueueTimerCallback(entry), null, Timeout.Infinite, Timeout.Infinite);
        _luaTimers.Add(id, entry);
        entry.Timer.Change(interval, Timeout.Infinite);
        lua.PushInteger(id);
        return 1;
    }

    private void QueueTimerCallback(LuaTimerEntry entry)
    {
        bool postDrain = false;
        lock (_timerCallbackQueueLock)
        {
            _pendingTimerCallbacks.Enqueue(entry);
            if (!_timerDrainScheduled)
            {
                _timerDrainScheduled = true;
                postDrain = true;
            }
        }

        if (postDrain)
        {
            _services.Post(_drainTimerCallbacksAction);
        }
    }

    private void DrainTimerCallbacks()
    {
        while (true)
        {
            LuaTimerEntry entry;
            lock (_timerCallbackQueueLock)
            {
                if (_pendingTimerCallbacks.Count == 0)
                {
                    _timerDrainScheduled = false;
                    return;
                }

                entry = _pendingTimerCallbacks.Dequeue();
            }

            RunTimerCallback(entry);
        }
    }

    private void RunTimerCallback(LuaTimerEntry entry)
    {
        lock (_lock)
        {
            if (_disposed || entry.Generation != _luaTimerGeneration ||
                !_luaTimers.TryGetValue(entry.Id, out LuaTimerEntry? registered) || !ReferenceEquals(entry, registered))
            {
                return;
            }

            if (!entry.Repeating)
            {
                _luaTimers.Remove(entry.Id);
            }
        }

        try
        {
            InvokeCallbackValue(entry.Callback, out _);
        }
        finally
        {
            lock (_lock)
            {
                if (entry.Repeating && !_disposed && entry.Generation == _luaTimerGeneration &&
                    _luaTimers.TryGetValue(entry.Id, out LuaTimerEntry? registered) && ReferenceEquals(entry, registered))
                {
                    entry.Timer?.Change(entry.Interval, Timeout.Infinite);
                }
                else
                {
                    entry.Timer?.Dispose();
                    entry.Callback.Dispose();
                }
            }
        }
    }

    private static int Cancel(LuaScriptHost host, Lua lua)
    {
        host.RequireRuntime(lua, "dotty.cancel");

        if (lua.Type(1) != LuaType.Number)
        {
            lua.PushBoolean(false);
            return 1;
        }

        long id = lua.ToInteger(1);
        if (!host._luaTimers.Remove(id, out LuaTimerEntry? entry))
        {
            lua.PushBoolean(false);
            return 1;
        }

        entry.Timer?.Dispose();
        entry.Callback.Dispose();
        lua.PushBoolean(true);
        return 1;
    }

    private static int Spawn(LuaScriptHost host, Lua lua)
    {
        host.RequireRuntime(lua, "dotty.spawn");

        string? program = ReadString(lua, 1);
        if (string.IsNullOrWhiteSpace(program))
        {
            return host.Fail(lua, "dotty.spawn expects a non-empty program name.");
        }

        string? cwd = null;
        if (lua.Type(2) == LuaType.Table)
        {
            using (var stack = new LuaStackScope(lua))
            {
                lua.GetField(2, "cwd");
                cwd = ReadString(lua, -1);
            }
        }

        TerminalTab tab = host._services.CreateTab(cwd, program);
        host.PushTabHandle(lua, tab);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int AfterEntry(IntPtr state) => CallbackBoundary(state, &After);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int EveryEntry(IntPtr state) => CallbackBoundary(state, &Every);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int CancelEntry(IntPtr state) => CallbackBoundary(state, &Cancel);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int SpawnEntry(IntPtr state) => CallbackBoundary(state, &Spawn);
}
