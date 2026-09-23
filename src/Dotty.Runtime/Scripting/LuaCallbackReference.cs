using System;
using System.Runtime.InteropServices;
using Dotty.Runtime.Text;
using KeraLua;

namespace Dotty.Runtime.Scripting;

internal sealed class LuaCallbackReference : IDisposable
{
    private readonly LuaScriptHost _host;
    private int _reference;

    internal LuaCallbackReference(LuaScriptHost host, int reference)
    {
        _host = host;
        _reference = reference;
    }

    internal int Reference => System.Threading.Volatile.Read(ref _reference);

    internal bool InvokeKeybind(out bool handled)
    {
        return _host.InvokeKeybind(this, out handled);
    }

    internal bool Invoke(out LuaValue result)
    {
        return _host.InvokeCallbackValue(this, out result);
    }

    internal bool Invoke(LuaValue arg0, out LuaValue result)
    {
        return _host.InvokeCallbackValue(this, arg0, out result);
    }

    internal bool Invoke(LuaValue arg0, LuaValue arg1, out LuaValue result)
    {
        return _host.InvokeCallbackValue(this, arg0, arg1, out result);
    }

    internal bool Invoke(LuaValue arg0, LuaValue arg1, LuaValue arg2, out LuaValue result)
    {
        return _host.InvokeCallbackValue(this, arg0, arg1, arg2, out result);
    }

    internal bool InvokeString(ReusableTextBuffer destination)
    {
        return _host.InvokeCallbackString(this, destination);
    }

    internal bool InvokeString(LuaValue arg0, ReusableTextBuffer destination)
    {
        return _host.InvokeCallbackString(this, arg0, destination);
    }

    /// <summary>Releases the Lua registry reference held by this callback.</summary>
    public void Dispose()
    {
        int reference = System.Threading.Interlocked.Exchange(ref _reference, -1);
        if (reference >= 0)
        {
            _host.ReleaseFunctionReference(reference);
        }
    }
}

internal static partial class LuaNative
{
    [LibraryImport("lua54", EntryPoint = "lua_pushcclosure")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial void PushCClosure(IntPtr state, IntPtr function, int upvalues);

    [LibraryImport("lua54", EntryPoint = "lua_pushlstring")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static unsafe partial IntPtr PushLString(IntPtr state, byte* text, nuint length);

    [LibraryImport("lua54", EntryPoint = "lua_tolstring")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static unsafe partial IntPtr ToLString(IntPtr state, int index, nuint* length);
}

internal ref struct LuaStackScope
{
    private readonly Lua _lua;
    private readonly int _top;

    internal LuaStackScope(Lua lua)
    {
        _lua = lua;
        _top = lua.GetTop();
    }

    /// <summary>Restores the Lua stack to its entry height.</summary>
    public void Dispose()
    {
        _lua.SetTop(_top);
    }
}
