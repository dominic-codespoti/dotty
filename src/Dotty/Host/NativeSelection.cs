using System;
using System.Runtime.InteropServices;
using System.Text;
using Dotty.Runtime.Clipboard;
using Silk.NET.GLFW;

namespace Dotty.Silk;

/// <summary>
/// PRIMARY selection backed by the GLFW 3.4 X11-only exports <c>glfwGetX11SelectionString</c> and
/// <c>glfwSetX11SelectionString</c>. Available only on X11 with those exports; elsewhere PRIMARY is unavailable and
/// never emulated in process. Window (main) thread only: GLFW is not thread safe and the pointer returned by the
/// getter is owned by GLFW and invalidated by the next selection call, so it is copied at once. No member throws.
/// </summary>
internal sealed unsafe class NativeSelection : IPrimarySelection
{
    private const int PlatformX11 = 0x00060004;

    private readonly delegate* unmanaged[Cdecl]<byte*> _get;
    private readonly delegate* unmanaged[Cdecl]<byte*, void> _set;
    private readonly nint _display;
    public NativeSelection(Glfw glfw)
    {
        ArgumentNullException.ThrowIfNull(glfw);
        try
        {
            bool hasPlatform = NativeGlfwMetadata.TryGetPlatform(out int platform);
            if (!hasPlatform || platform != PlatformX11)
            {
                return;
            }
            if (glfw.Context.TryGetProcAddress("glfwGetX11Display", out nint display) && display != 0)
                _display = ((delegate* unmanaged[Cdecl]<nint>)display)();
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
    public bool SupportsCutBuffer0 => _display != 0;

    public string? GetCutBuffer0()
    {
        if (_display == 0)
            return null;
        try
        {
            nint bytes = XFetchBuffer(_display, out int length, 0);
            if (bytes == 0 || length < 0)
                return null;
            try { return Marshal.PtrToStringUTF8(bytes, length); }
            finally { XFree(bytes); }
        }
        catch (Exception)
        {
            return null;
        }
    }

    public bool SetCutBuffer0(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_display == 0)
            return false;
        try
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(text);
            fixed (byte* data = utf8)
            {
                if (XStoreBuffer(_display, data, utf8.Length, 0) == 0)
                    return false;
            }
            XSync(_display, 0);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [DllImport("libX11.so.6", EntryPoint = "XFetchBuffer")]
    private static extern nint XFetchBuffer(nint display, out int nbytes, int buffer);

    [DllImport("libX11.so.6", EntryPoint = "XStoreBuffer")]
    private static extern unsafe int XStoreBuffer(nint display, byte* bytes, int nbytes, int buffer);

    [DllImport("libX11.so.6", EntryPoint = "XFree")]
    private static extern int XFree(nint data);

    [DllImport("libX11.so.6", EntryPoint = "XSync")]
    private static extern int XSync(nint display, int discard);

    public string? Get()
    {
        if (!IsSystemSelection)
            return null;

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
            return;

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
