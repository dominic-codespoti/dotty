using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Dotty.Runtime.Config;
using System.Text;
using Dotty.Runtime.Tabs;
using Dotty.Runtime.Text;
using KeraLua;

namespace Dotty.Runtime.Scripting;

/// <summary>Owns the Lua state and its UI-thread-bound host services.</summary>
public sealed partial class LuaScriptHost : IDisposable
{
    private const int LuaUpvalueIndex1 = (int)LuaRegistry.Index - 1;
    private static readonly ConcurrentDictionary<IntPtr, LuaScriptHost> HostsByState = new();
    private readonly ILuaHostServices _services;
    private readonly TerminalTabManager _tabManager;
    private readonly object _lock = new();
    private readonly Dictionary<long, TerminalTab> _tabHandles = new();
    private readonly Dictionary<TerminalTab, long> _tabHandleIds = new();
    private readonly Dictionary<long, (Dotty.Runtime.Panes.LeafPane Pane, TerminalTab Tab)> _paneHandles = new();
    private readonly Dictionary<(Dotty.Runtime.Panes.LeafPane Pane, TerminalTab Tab), long> _paneHandleIds = new();
    private Lua? _lua;
    private FileSystemWatcher? _watcher;
    private Timer? _watchDebounce;
    private long _nextHandleId;
    private bool _disposed;
    private bool _isEvaluatingConfig;
    private bool _configDirty;
    private bool _applyPosted;
    private bool _guiStartupSent;
    private int _successfulEvaluations;
    private int _errorSentinelReference = -1;
    private int _tabHandleCacheReference = -1;
    private int _paneHandleCacheReference = -1;
    private byte[] _luaUtf8Scratch = new byte[256];
    private readonly Action _drainEventsAction;
    private readonly Action _drainTimerCallbacksAction;
    private int _wrapReference = -1;

    /// <summary>Creates a Lua host bound to the supplied services and tab manager.</summary>
    public LuaScriptHost(ILuaHostServices services, TerminalTabManager tabManager)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _tabManager = tabManager ?? throw new ArgumentNullException(nameof(tabManager));
        ConfigProxy = new LuaConfigProxy(this);
        Keybinds = new LuaKeybindRegistry(this);
        Hooks = new LuaHookManager { Owner = this };
        _drainEventsAction = DrainEvents;
        _drainTimerCallbacksAction = DrainTimerCallbacks;
        AttachEvents();
    }

    /// <summary>Gets the managed configuration proxy.</summary>
    public LuaConfigProxy ConfigProxy { get; }

    /// <summary>Gets the registry of Lua key bindings.</summary>
    public LuaKeybindRegistry Keybinds { get; }

    /// <summary>Gets the Lua hook manager.</summary>
    public LuaHookManager Hooks { get; }

    /// <summary>Gets the most recent Lua error, if any.</summary>
    public string? LastError { get; private set; }

    /// <summary>Raised when the watched script file changes.</summary>
    public event Action? ScriptFileChanged;

    internal bool IsEvaluatingConfig => _isEvaluatingConfig;

    /// <summary>Gets the configured Lua script path, preferring config.lua over init.lua.</summary>
    public static string GetConfigLuaPath()
    {
        string configLua = Path.Combine(PlatformPaths.LuaDirectory, "config.lua");
        if (File.Exists(configLua))
        {
            return configLua;
        }

        string initLua = Path.Combine(PlatformPaths.LuaDirectory, "init.lua");
        return File.Exists(initLua) ? initLua : configLua;
    }

    /// <summary>Evaluates the configuration script in a fresh Lua state.</summary>
    public bool Evaluate(DottyUserConfig config, string? scriptPath)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (_lock)
        {
            ThrowIfDisposed();
            Keybinds.Clear();
            Hooks.Clear();
            _tabHandles.Clear();
            _tabHandleIds.Clear();
            _paneHandles.Clear();
            _paneHandleIds.Clear();
            _nextHandleId = 0;
            ResetEvents();
            ResetUtilities();
            CloseLuaState();
            ConfigProxy.Bind(config);
            try
            {
                var lua = new Lua { Encoding = System.Text.Encoding.UTF8 };
                _lua = lua;
                HostsByState[lua.MainThread.Handle] = this;
                InitializeDottyModule(lua);
                _isEvaluatingConfig = true;
                if (!string.IsNullOrWhiteSpace(scriptPath) && File.Exists(scriptPath))
                {
                    ExecuteFile(lua, scriptPath);
                }

                _isEvaluatingConfig = false;
                EnsureWatcher(scriptPath);
                LastError = null;
                bool notifyReload = _successfulEvaluations++ > 0;
                if (notifyReload)
                {
                    Emit("config_reloaded");
                }

                return true;
            }
            catch (Exception ex)
            {
                _isEvaluatingConfig = false;
                LastError = ex.Message;
                _services.Log(LuaMessageLevel.Error, ex.Message);
                Keybinds.Clear();
                Hooks.Clear();
                ResetEvents();
                ResetUtilities();
                _tabHandles.Clear();
                _tabHandleIds.Clear();
                _paneHandles.Clear();
                _paneHandleIds.Clear();
                CloseLuaState();
                EnsureWatcher(scriptPath);
                return false;
            }
            finally
            {
                _isEvaluatingConfig = false;
            }
        }
    }

    /// <summary>Executes Lua code in the current runtime state.</summary>
    public bool ExecuteString(string luaCode)
    {
        ArgumentNullException.ThrowIfNull(luaCode);
        lock (_lock)
        {
            ThrowIfDisposed();
            try
            {
                if (_lua != null)
                {
                    ExecuteChunk(_lua, luaCode, "=(string)");
                }

                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                throw new InvalidOperationException(ex.Message, ex);
            }
        }
    }

    /// <summary>Emits the GUI startup event at most once for this host lifetime.</summary>
    public void NotifyGuiStartup()
    {
        if (_guiStartupSent)
        {
            return;
        }

        _guiStartupSent = true;
        Emit("gui_startup");
    }

    /// <summary>Emits the window focus changed event.</summary>
    public void NotifyWindowFocusChanged(bool focused)
    {
        Emit("window_focus_changed", LuaValue.From(focused));
    }

    internal void MarkConfigDirty()
    {
        if (_isEvaluatingConfig)
        {
            return;
        }

        _configDirty = true;
        if (_applyPosted)
        {
            return;
        }

        _applyPosted = true;
        _services.Post(() =>
        {
            lock (_lock)
            {
                _applyPosted = false;
                if (_disposed || !_configDirty)
                {
                    return;
                }

                _configDirty = false;
            }

            _services.ApplyConfig();
        });
    }

    private static unsafe void PushClosure(Lua lua, delegate* unmanaged[Cdecl]<IntPtr, int> callback)
    {
        LuaNative.PushCClosure(lua.Handle, (IntPtr)callback, 0);
    }

    private unsafe void PushHandleClosure(Lua lua, long handleId, delegate* unmanaged[Cdecl]<IntPtr, int> callback)
    {
        if (_wrapReference < 0)
        {
            lua.PushInteger(handleId);
            LuaNative.PushCClosure(lua.Handle, (IntPtr)callback, 1);
            return;
        }

        lua.RawGetInteger(LuaRegistry.Index, _wrapReference);
        lua.PushInteger(handleId);
        LuaNative.PushCClosure(lua.Handle, (IntPtr)callback, 1);
        lua.RawGetInteger(LuaRegistry.Index, _errorSentinelReference);
        if (lua.PCall(2, 1, 0) != LuaStatus.OK)
        {
            throw CreateLuaException(lua);
        }
    }

    private static bool TryPushCachedHandle(Lua lua, int cacheReference, long handleId, out int cacheIndex)
    {
        if (cacheReference < 0)
        {
            cacheIndex = 0;
            return false;
        }

        lua.RawGetInteger(LuaRegistry.Index, cacheReference);
        cacheIndex = lua.AbsIndex(-1);
        lua.RawGetInteger(cacheIndex, handleId);
        if (lua.Type(-1) == LuaType.Table)
        {
            lua.Remove(cacheIndex);
            cacheIndex = 0;
            return true;
        }

        lua.Pop(1);
        return false;
    }

    private static void StoreCachedHandle(Lua lua, int cacheIndex, long handleId, int tableIndex)
    {
        if (cacheIndex == 0)
        {
            return;
        }

        lua.PushCopy(tableIndex);
        lua.RawSetInteger(cacheIndex, handleId);
        lua.Remove(cacheIndex);
    }

    private unsafe void InitializeDottyModule(Lua lua)
    {
        using var stack = new LuaStackScope(lua);
        lua.LoadString("local sentinel = {}; local function check(s, first, ...) if first == s then error((...), 2) end return first, ... end; local function wrap(f, s) return function(...) return check(s, f(...)) end end; return sentinel, wrap", "=(dotty-wrapper)");
        if (lua.PCall(0, 2, 0) != LuaStatus.OK)
        {
            throw CreateLuaException(lua);
        }

        _wrapReference = lua.Ref(LuaRegistry.Index);
        _errorSentinelReference = lua.Ref(LuaRegistry.Index);
        lua.NewTable();
        _tabHandleCacheReference = lua.Ref(LuaRegistry.Index);
        lua.NewTable();
        _paneHandleCacheReference = lua.Ref(LuaRegistry.Index);
        lua.NewTable();
        int dotty = lua.AbsIndex(-1);
        SetFunction(lua, dotty, "bind", &BindEntry);
        SetFunction(lua, dotty, "on", &HookEntry);
        SetFunction(lua, dotty, "log", &LogEntry);
        RegisterConfig(lua, dotty);
        RegisterTabs(lua, dotty);
        RegisterEvents(lua, dotty);
        RegisterActions(lua, dotty);
        RegisterUtilities(lua, dotty);
        lua.PushCopy(dotty);
        lua.SetGlobal("dotty");
        lua.GetGlobal("package");
        if (lua.Type(-1) == LuaType.Table)
        {
            lua.GetField(-1, "loaded");
            if (lua.Type(-1) == LuaType.Table)
            {
                PushLuaString(lua, "dotty");
                lua.PushCopy(dotty);
                lua.SetTable(-3);
            }
        }

        lua.Pop(2);
    }

    private static unsafe int CallbackBoundary(IntPtr state, delegate*<LuaScriptHost, Lua, int> callback)
    {
        Lua? lua = null;
        IntPtr previousExtraSpace = IntPtr.Zero;
        IntPtr callbackStateHandle = IntPtr.Zero;
        int top = 0;
        try
        {
            lua = GetCallbackLuaState(state, out previousExtraSpace, out callbackStateHandle);
            if (lua == null)
            {
                return 0;
            }

            top = lua.GetTop();
            IntPtr main = lua.MainThread.Handle;
            if (!HostsByState.TryGetValue(main, out LuaScriptHost? host))
            {
                return 0;
            }

            lock (host._lock)
            {
                if (host._disposed || host._lua == null || host._lua.MainThread.Handle != main)
                {
                    return 0;
                }

                try
                {
                    return callback(host, lua);
                }
                catch (Exception ex)
                {
                    lua.SetTop(top);
                    return host.Fail(lua, ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            if (lua != null)
            {
                try
                {
                    lua.SetTop(top);
                }
                catch
                {
                }
            }

            SafeLog(ex.Message);
            return 0;
        }
        finally
        {
            ReleaseCallbackLuaState(state, previousExtraSpace, callbackStateHandle);
        }
    }

    private unsafe void SetFunction(Lua lua, int tableIndex, string name, delegate* unmanaged[Cdecl]<IntPtr, int> entry)
    {
        int top = lua.GetTop();
        PushWrappedClosure(lua, entry);
        lua.SetField(tableIndex, name);
        lua.SetTop(top);
    }

    private unsafe void PushWrappedClosure(Lua lua, delegate* unmanaged[Cdecl]<IntPtr, int> entry)
    {
        if (_wrapReference < 0)
        {
            PushClosure(lua, entry);
            return;
        }

        lua.RawGetInteger(LuaRegistry.Index, _wrapReference);
        PushClosure(lua, entry);
        lua.RawGetInteger(LuaRegistry.Index, _errorSentinelReference);
        if (lua.PCall(2, 1, 0) != LuaStatus.OK)
        {
            throw CreateLuaException(lua);
        }
    }

    private unsafe void PushWrappedClosure(Lua lua, string upvalue, delegate* unmanaged[Cdecl]<IntPtr, int> entry)
    {
        if (_wrapReference < 0)
        {
            PushLuaString(lua, upvalue);
            LuaNative.PushCClosure(lua.Handle, (IntPtr)entry, 1);
            return;
        }

        lua.RawGetInteger(LuaRegistry.Index, _wrapReference);
        PushLuaString(lua, upvalue);
        LuaNative.PushCClosure(lua.Handle, (IntPtr)entry, 1);
        lua.RawGetInteger(LuaRegistry.Index, _errorSentinelReference);
        if (lua.PCall(2, 1, 0) != LuaStatus.OK)
        {
            throw CreateLuaException(lua);
        }
    }

    private static Lua? GetCallbackLuaState(IntPtr state, out IntPtr previousExtraSpace, out IntPtr callbackStateHandle)
    {
        IntPtr extraSpace = state - IntPtr.Size;
        previousExtraSpace = Marshal.ReadIntPtr(extraSpace);
        callbackStateHandle = IntPtr.Zero;
        Lua? previousLua = previousExtraSpace == IntPtr.Zero ? null : GCHandle.FromIntPtr(previousExtraSpace).Target as Lua;
        Lua? lua = Lua.FromIntPtr(state);
        if (lua != null && lua.Handle == state && previousLua != null && previousLua.Handle != state)
        {
            callbackStateHandle = Marshal.ReadIntPtr(extraSpace);
        }

        return lua;
    }

    private static void ReleaseCallbackLuaState(IntPtr state, IntPtr previousExtraSpace, IntPtr callbackStateHandle)
    {
        if (callbackStateHandle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            Marshal.WriteIntPtr(state - IntPtr.Size, previousExtraSpace);
            GCHandle.FromIntPtr(callbackStateHandle).Free();
        }
        catch
        {
        }
    }

    // Fail returns a sentinel and message; the Lua wrapper performs error(message, 2).
    private int Fail(Lua lua, string message)
    {
        LastError = message;
        _services.Log(LuaMessageLevel.Error, message);
        lua.RawGetInteger(LuaRegistry.Index, _errorSentinelReference);
        PushLuaString(lua, message);
        return 2;
    }

    private void RequireRuntime(Lua lua, string fn)
    {
        if (_isEvaluatingConfig)
        {
            throw new InvalidOperationException($"{fn} is not available while config.lua is being evaluated; use dotty.on('gui_startup', fn)");
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int BindEntry(IntPtr state) => CallbackBoundary(state, &Bind);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int HookEntry(IntPtr state) => CallbackBoundary(state, &Hook);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe int LogEntry(IntPtr state) => CallbackBoundary(state, &Log);

    private static int Bind(LuaScriptHost host, Lua lua)
    {
        return host.BindCore(lua);
    }

    private static int Hook(LuaScriptHost host, Lua lua)
    {
        return host.HookCore(lua);
    }

    private static int Log(LuaScriptHost host, Lua lua)
    {
        return host.LogCore(lua);
    }

    private int BindCore(Lua lua)
    {
        int chordIndex = lua.Type(1) == LuaType.String ? 1 : 2;
        int valueIndex = chordIndex + 1;
        string? chord = ReadString(lua, chordIndex);
        if (chord == null || !Dotty.Runtime.Input.KeybindingManager.TryNormalizeChord(chord, out _))
        {
            throw new InvalidOperationException("Invalid key chord.");
        }

        if (lua.Type(valueIndex) == LuaType.Function)
        {
            Keybinds.Register(chord, CaptureFunction(lua, valueIndex));
        }
        else if (lua.Type(valueIndex) == LuaType.String)
        {
            string? name = lua.ToString(valueIndex);
            if (!Enum.TryParse(name, true, out Dotty.Abstractions.Config.TerminalAction action)
                || !Enum.IsDefined(typeof(Dotty.Abstractions.Config.TerminalAction), action))
            {
                throw new InvalidOperationException($"Unknown terminal action '{name}'.");
            }

            if (action == Dotty.Abstractions.Config.TerminalAction.None)
            {
                Keybinds.Unbind(chord);
            }
            else
            {
                Keybinds.Register(chord, action);
            }
        }
        else
        {
            throw new InvalidOperationException("dotty.bind expects a callback or action name.");
        }

        return 0;
    }

    private int HookCore(Lua lua)
    {
        int index = lua.Type(1) == LuaType.String ? 1 : 2;
        string? name = ReadString(lua, index);
        if (name == null || lua.Type(index + 1) != LuaType.Function)
        {
            throw new InvalidOperationException("dotty.on expects an event name and callback.");
        }

        Hooks.Register(name, CaptureFunction(lua, index + 1));
        return 0;
    }

    private int LogCore(Lua lua)
    {
        using var stack = new LuaStackScope(lua);
        int count = lua.GetTop();
        lua.GetGlobal("tostring");
        var parts = new List<string>(count);
        for (int i = 1; i <= count; i++)
        {
            lua.PushCopy(i);
            if (lua.PCall(1, 1, 0) == LuaStatus.OK)
            {
                parts.Add(lua.ToString(-1) ?? "nil");
            }
            else
            {
                lua.Pop(1);
                parts.Add("nil");
            }

            lua.Pop(1);
        }

        _services.Log(LuaMessageLevel.Info, string.Join(" ", parts));
        return 0;
    }

    private LuaCallbackReference CaptureFunction(Lua lua, int index)
    {
        lua.PushCopy(index);
        int reference = lua.Ref(LuaRegistry.Index);
        return new LuaCallbackReference(this, reference);
    }

    internal bool InvokeKeybind(LuaCallbackReference callback, out bool result)
    {
        lock (_lock)
        {
            result = false;
            if (!Invoke(callback, 0, default, default, default, null, out LuaValue value))
            {
                return false;
            }

            result = !value.TryGetBoolean(out bool handled) || handled;
            return true;
        }
    }

    internal bool InvokeCallbackValue(LuaCallbackReference callback, out LuaValue result)
    {
        lock (_lock)
        {
            return Invoke(callback, 0, default, default, default, null, out result);
        }
    }

    internal bool InvokeCallbackValue(LuaCallbackReference callback, LuaValue arg0, out LuaValue result)
    {
        lock (_lock)
        {
            return Invoke(callback, 1, arg0, default, default, null, out result);
        }
    }

    internal bool InvokeCallbackValue(LuaCallbackReference callback, LuaValue arg0, LuaValue arg1, out LuaValue result)
    {
        lock (_lock)
        {
            return Invoke(callback, 2, arg0, arg1, default, null, out result);
        }
    }

    internal bool InvokeCallbackValue(
        LuaCallbackReference callback,
        LuaValue arg0,
        LuaValue arg1,
        LuaValue arg2,
        out LuaValue result)
    {
        lock (_lock)
        {
            return Invoke(callback, 3, arg0, arg1, arg2, null, out result);
        }
    }

    internal bool InvokeCallbackString(LuaCallbackReference callback, ReusableTextBuffer destination)
    {
        lock (_lock)
        {
            return Invoke(callback, 0, default, default, default, destination, out _);
        }
    }

    internal bool InvokeCallbackString(LuaCallbackReference callback, LuaValue arg0, ReusableTextBuffer destination)
    {
        lock (_lock)
        {
            return Invoke(callback, 1, arg0, default, default, destination, out _);
        }
    }

    private bool Invoke(
        LuaCallbackReference callback,
        int argCount,
        LuaValue arg0,
        LuaValue arg1,
        LuaValue arg2,
        ReusableTextBuffer? stringDestination,
        out LuaValue result)
    {
        result = LuaValue.Nil;
        Lua? lua = _lua;
        int reference = callback.Reference;
        if (_disposed || lua == null || reference < 0)
        {
            return false;
        }

        int top = lua.GetTop();
        try
        {
            lua.RawGetInteger(LuaRegistry.Index, reference);
            if (argCount > 0)
            {
                PushValue(lua, arg0);
            }

            if (argCount > 1)
            {
                PushValue(lua, arg1);
            }

            if (argCount > 2)
            {
                PushValue(lua, arg2);
            }

            if (lua.PCall(argCount, 1, 0) != LuaStatus.OK)
            {
                LastError = lua.ToString(-1);
                _services.Log(LuaMessageLevel.Error, LastError ?? "Lua callback failed");
                return false;
            }

            if (stringDestination != null)
            {
                return TryReadLuaString(lua, -1, stringDestination);
            }

            result = ReadValue(lua, -1);
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _services.Log(LuaMessageLevel.Error, ex.Message);
            return false;
        }
        finally
        {
            lua.SetTop(top);
        }
    }

    private LuaValue ReadValue(Lua lua, int index)
    {
        return lua.Type(index) switch
        {
            LuaType.Boolean => LuaValue.From(lua.ToBoolean(index)),
            LuaType.Number => LuaValue.From(lua.ToNumber(index)),
            LuaType.String => LuaValue.StringResult,
            _ => LuaValue.Nil
        };
    }

    private unsafe void PushValue(Lua lua, LuaValue value)
    {
        if (value.TryGetString(out string? text))
        {
            PushLuaString(lua, text!);
        }
        else if (value.TryGetBoolean(out bool boolean))
        {
            lua.PushBoolean(boolean);
        }
        else if (value.TryGetNumber(out double number))
        {
            lua.PushNumber(number);
        }
        else if (value.TryGetTab(out TerminalTab tab))
        {
            PushTabHandle(lua, tab);
        }
        else if (value.TryGetPane(out Dotty.Runtime.Panes.LeafPane pane, out TerminalTab owner))
        {
            PushPaneHandle(lua, pane, owner);
        }
        else
        {
            lua.PushNil();
        }
    }

    private ReadOnlySpan<byte> EncodeLuaStringToScratch(string text)
    {
        return EncodeLuaStringToScratch(text.AsSpan());
    }

    private ReadOnlySpan<byte> EncodeLuaStringToScratch(ReadOnlySpan<char> text)
    {
        int byteCount = Encoding.UTF8.GetByteCount(text);
        if (byteCount > _luaUtf8Scratch.Length)
        {
            Array.Resize(ref _luaUtf8Scratch, Math.Max(byteCount, _luaUtf8Scratch.Length * 2));
        }

        int written = Encoding.UTF8.GetBytes(text, _luaUtf8Scratch.AsSpan());
        return _luaUtf8Scratch.AsSpan(0, written);
    }

    private unsafe void PushLuaString(Lua lua, string text)
    {
        PushLuaString(lua, text.AsSpan());
    }

    private unsafe void PushLuaString(Lua lua, ReadOnlySpan<char> text)
    {
        ReadOnlySpan<byte> utf8 = EncodeLuaStringToScratch(text);
        fixed (byte* bytes = _luaUtf8Scratch)
        {
            LuaNative.PushLString(lua.Handle, bytes, (nuint)utf8.Length);
        }
    }

    private unsafe void PushGuid(Lua lua, Guid value)
    {
        Span<char> text = stackalloc char[36];
        value.TryFormat(text, out int written, "D");
        PushLuaString(lua, text[..written]);
    }

    private static unsafe bool TryReadLuaUtf8(Lua lua, int index, out ReadOnlySpan<byte> utf8)
    {
        if (lua.Type(index) != LuaType.String)
        {
            utf8 = default;
            return false;
        }

        nuint length = 0;
        IntPtr pointer = LuaNative.ToLString(lua.Handle, index, &length);
        if (pointer == IntPtr.Zero || length > (nuint)int.MaxValue)
        {
            utf8 = default;
            return false;
        }

        utf8 = new ReadOnlySpan<byte>((void*)pointer, (int)length);
        return true;
    }

    private static unsafe bool IsLuaStringEqualIgnoreCase(Lua lua, int index, string candidate)
    {
        if (lua.Type(index) != LuaType.String)
        {
            return false;
        }

        nuint length = 0;
        IntPtr pointer = LuaNative.ToLString(lua.Handle, index, &length);
        if (pointer == IntPtr.Zero || length != (nuint)candidate.Length)
        {
            return false;
        }

        ReadOnlySpan<byte> value = new((void*)pointer, candidate.Length);
        for (int i = 0; i < candidate.Length; i++)
        {
            char expected = candidate[i];
            if (expected > 0x7f)
            {
                return false;
            }

            byte actual = value[i];
            byte lowerActual = actual is >= (byte)'A' and <= (byte)'Z' ? (byte)(actual + ('a' - 'A')) : actual;
            char lowerExpected = expected is >= 'A' and <= 'Z' ? (char)(expected + ('a' - 'A')) : expected;
            if (lowerActual != lowerExpected)
            {
                return false;
            }
        }

        return true;
    }


    private unsafe bool TryReadLuaString(Lua lua, int index, ReusableTextBuffer destination)
    {
        if (lua.Type(index) != LuaType.String)
        {
            return false;
        }

        nuint length = 0;
        IntPtr pointer = LuaNative.ToLString(lua.Handle, index, &length);
        if (pointer == IntPtr.Zero || length == 0 || length > (nuint)int.MaxValue)
        {
            return false;
        }

        var utf8 = new ReadOnlySpan<byte>((void*)pointer, (int)length);
        if (IsWhitespaceOnly(utf8))
        {
            return false;
        }

        destination.SetUtf8(utf8);
        return true;
    }

    private static bool IsWhitespaceOnly(ReadOnlySpan<byte> utf8)
    {
        while (!utf8.IsEmpty)
        {
            OperationStatus status = Rune.DecodeFromUtf8(utf8, out Rune rune, out int consumed);
            if (status != OperationStatus.Done)
            {
                return false;
            }

            if (!Rune.IsWhiteSpace(rune))
            {
                return false;
            }

            utf8 = utf8[consumed..];
        }

        return true;
    }

    internal void Emit(string eventName)
    {
        Hooks.Emit(eventName);
    }

    internal void Emit(string eventName, LuaValue arg0)
    {
        Hooks.Emit(eventName, arg0);
    }

    internal void Emit(string eventName, LuaValue arg0, LuaValue arg1)
    {
        Hooks.Emit(eventName, arg0, arg1);
    }

    internal void Emit(string eventName, LuaValue arg0, LuaValue arg1, LuaValue arg2)
    {
        Hooks.Emit(eventName, arg0, arg1, arg2);
    }

    private static string? ReadString(Lua lua, int index)
    {
        return lua.Type(index) == LuaType.String ? lua.ToString(index) : null;
    }

    private bool ExecuteChunk(Lua lua, string source, string name)
    {
        using var stack = new LuaStackScope(lua);
        if (lua.LoadString(source, name) != LuaStatus.OK || lua.PCall(0, 0, 0) != LuaStatus.OK)
        {
            throw CreateLuaException(lua);
        }

        return true;
    }

    private bool ExecuteFile(Lua lua, string path)
    {
        using var stack = new LuaStackScope(lua);
        if (lua.LoadFile(path) != LuaStatus.OK || lua.PCall(0, 0, 0) != LuaStatus.OK)
        {
            throw CreateLuaException(lua);
        }

        return true;
    }

    private static Exception CreateLuaException(Lua lua)
    {
        return new InvalidOperationException(lua.GetTop() > 0 ? lua.ToString(-1) ?? "Lua execution failed." : "Lua execution failed.");
    }

    private void EnsureWatcher(string? path)
    {
        string? directory = string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory == null || !Directory.Exists(directory))
        {
            _watcher?.Dispose();
            _watcher = null;
            return;
        }

        if (_watcher != null && string.Equals(_watcher.Path, directory, StringComparison.Ordinal))
        {
            return;
        }

        _watcher?.Dispose();
        try
        {
            _watcher = new FileSystemWatcher(directory, "*.lua")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            _watcher.Changed += OnScriptChanged;
            _watcher.Created += OnScriptChanged;
            _watcher.Deleted += OnScriptChanged;
            _watcher.Renamed += OnScriptChanged;
        }
        catch
        {
            _watcher = null;
        }
    }

    private void OnScriptChanged(object? sender, FileSystemEventArgs e)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _watchDebounce?.Dispose();
            _watchDebounce = new Timer(_ =>
            {
                if (!_disposed)
                {
                    ScriptFileChanged?.Invoke();
                }
            }, null, 150, Timeout.Infinite);
        }
    }

    private void CloseLuaState()
    {
        Lua? lua = _lua;
        if (lua == null)
        {
            return;
        }

        IntPtr handle = Marshal.ReadIntPtr(lua.ExtraSpace);
        HostsByState.TryRemove(lua.MainThread.Handle, out _);
        _lua = null;
        try
        {
            if (_errorSentinelReference >= 0)
            {
                lua.Unref(LuaRegistry.Index, _errorSentinelReference);
            }

            if (_wrapReference >= 0)
            {
                lua.Unref(LuaRegistry.Index, _wrapReference);
            }

            if (_tabHandleCacheReference >= 0)
            {
                lua.Unref(LuaRegistry.Index, _tabHandleCacheReference);
            }

            if (_paneHandleCacheReference >= 0)
            {
                lua.Unref(LuaRegistry.Index, _paneHandleCacheReference);
            }

            _errorSentinelReference = _wrapReference = -1;
            _tabHandleCacheReference = _paneHandleCacheReference = -1;
            lua.Dispose();
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                try
                {
                    GCHandle.FromIntPtr(handle).Free();
                }
                catch
                {
                }
            }
        }
    }

    internal bool TryExecuteAction(Dotty.Abstractions.Config.TerminalAction action)
    {
        return _services.TryExecuteAction(action);
    }

    internal void ReleaseFunctionReference(int reference)
    {
        lock (_lock)
        {
            if (_lua != null && !_disposed)
            {
                try
                {
                    _lua.Unref(LuaRegistry.Index, reference);
                }
                catch
                {
                }
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(LuaScriptHost));
        }
    }

    private static void SafeLog(string message)
    {
        try
        {
            Console.Error.WriteLine(message);
        }
        catch
        {
        }
    }

    private unsafe partial void RegisterConfig(Lua lua, int dottyIndex);
    private unsafe partial void RegisterTabs(Lua lua, int dottyIndex);
    partial void RegisterEvents(Lua lua, int dottyIndex);
    private unsafe partial void RegisterActions(Lua lua, int dottyIndex);
    private unsafe partial void RegisterUtilities(Lua lua, int dottyIndex);
    partial void AttachEvents();
    partial void DetachEvents();
    partial void ResetEvents();
    private partial void ResetUtilities();

    /// <summary>Releases the Lua state and its associated resources.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _watchDebounce?.Dispose();
            _watcher?.Dispose();
            DetachEvents();
            Keybinds.Clear();
            Hooks.Clear();
            ResetEvents();
            ResetUtilities();
            _tabHandles.Clear();
            _tabHandleIds.Clear();
            _paneHandles.Clear();
            _paneHandleIds.Clear();
            CloseLuaState();
        }
    }
}
