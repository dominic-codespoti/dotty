using System;
using System.Runtime.InteropServices;
using System.Text;
using Dotty.Runtime.Clipboard;
using Silk.NET.GLFW;

namespace Dotty.Silk;

/// <summary>
/// PRIMARY selection backed by the GLFW 3.4 X11-only exports <c>glfwGetX11SelectionString</c> and
/// <c>glfwSetX11SelectionString</c>. Active only when the running GLFW platform is X11; on every other platform (or when
/// the exports are missing) it behaves as an in-process buffer. Window (main) thread only: GLFW is not thread safe and
/// the pointer returned by the getter is owned by GLFW and invalidated by the next selection call, so it is copied at once.
/// No member throws.
/// </summary>
internal sealed unsafe class NativeSelection : IPrimarySelection
{
    private const int PlatformX11 = 0x00060004;

    private readonly delegate* unmanaged[Cdecl]<byte*> _get;
    private readonly delegate* unmanaged[Cdecl]<byte*, void> _set;
    private readonly InProcessPrimarySelection _fallback = new();

    public NativeSelection(Glfw glfw)
    {
        ArgumentNullException.ThrowIfNull(glfw);
        try
        {
            if (!NativeGlfwMetadata.TryGetPlatform(out int platform) || platform != PlatformX11)
                return;
            if (!glfw.Context.TryGetProcAddress("glfwGetX11SelectionString", out nint get) || get == 0)
                return;
            if (!glfw.Context.TryGetProcAddress("glfwSetX11SelectionString", out nint set) || set == 0)
                return;

            _get = (delegate* unmanaged[Cdecl]<byte*>)get;
            _set = (delegate* unmanaged[Cdecl]<byte*, void>)set;
        }
        catch (Exception)
        {
            _get = null;
            _set = null;
        }
    }

    public bool IsSystemSelection => _get != null && _set != null;

    public string? Get()
    {
        if (!IsSystemSelection)
            return _fallback.Get();

        try
        {
            byte* utf8 = _get();
            return utf8 == null ? null : Marshal.PtrToStringUTF8((nint)utf8);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Set(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!IsSystemSelection)
        {
            _fallback.Set(text);
            return;
        }

        try
        {
            byte[] buffer = new byte[Encoding.UTF8.GetByteCount(text) + 1];
            Encoding.UTF8.GetBytes(text, 0, text.Length, buffer, 0);
            fixed (byte* p = buffer)
                _set(p);
        }
        catch (Exception)
        {
            // Selection is best effort; never disturb the host loop.
        }
    }
}
