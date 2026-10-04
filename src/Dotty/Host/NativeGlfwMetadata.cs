using System;
using System.Text;
using Silk.NET.GLFW;

namespace Dotty.Silk;

/// <summary>Cached GLFW-native metadata queries; initialize once after GLFW loads.</summary>
internal static unsafe class NativeGlfwMetadata
{
    private static delegate* unmanaged[Cdecl]<int, int, byte*> _getKeyName;
    private static delegate* unmanaged[Cdecl]<int> _getPlatform;
    private static delegate* unmanaged[Cdecl]<nint, int, int> _getWindowAttrib;
    private static bool _initialized;

    /// <summary>Resolve core GLFW exports from the already-loaded Silk GLFW instance.</summary>
    public static void Initialize(Glfw glfw)
    {
        ArgumentNullException.ThrowIfNull(glfw);
        if (_initialized)
            return;

        if (!glfw.Context.TryGetProcAddress("glfwGetKeyName", out nint keyName))
            throw new PlatformNotSupportedException("The loaded GLFW library does not export glfwGetKeyName.");
        glfw.Context.TryGetProcAddress("glfwGetPlatform", out nint platform);

        _getKeyName = (delegate* unmanaged[Cdecl]<int, int, byte*>)keyName;
        _getPlatform = (delegate* unmanaged[Cdecl]<int>)platform;
        _getWindowAttrib = (delegate* unmanaged[Cdecl]<nint, int, int>)glfw.Context.GetProcAddress("glfwGetWindowAttrib");
        _initialized = true;
    }

    public static bool IsWindowFocused(nint window) => window != 0 && _getWindowAttrib != null && _getWindowAttrib(window, 0x00020001) != 0;

    /// <summary>Returns the first printable Unicode scalar for a key, falling back to GLFW's physical scancode mapping.</summary>
    public static bool TryGetPrimaryCodePoint(int key, int scancode, out uint codePoint)
    {
        codePoint = 0;
        if (_getKeyName == null || scancode < 0)
            return false;

        byte* utf8 = _getKeyName(key, scancode);
        if ((utf8 == null || utf8[0] == 0) && key != (int)Keys.Unknown)
            utf8 = _getKeyName((int)Keys.Unknown, scancode);
        if (utf8 == null)
            return false;

        byte first = utf8[0];
        uint scalar;
        if (first == 0)
            return false;
        if (first < 0x80)
        {
            scalar = first;
        }
        else if ((first & 0xE0) == 0xC0 && (utf8[1] & 0xC0) == 0x80)
        {
            scalar = (uint)(((first & 0x1F) << 6) | (utf8[1] & 0x3F));
            if (scalar < 0x80)
                return false;
        }
        else if ((first & 0xF0) == 0xE0 && (utf8[1] & 0xC0) == 0x80 && (utf8[2] & 0xC0) == 0x80)
        {
            scalar = (uint)(((first & 0x0F) << 12) | ((utf8[1] & 0x3F) << 6) | (utf8[2] & 0x3F));
            if (scalar < 0x800 || scalar is >= 0xD800 and <= 0xDFFF)
                return false;
        }
        else if ((first & 0xF8) == 0xF0 && (utf8[1] & 0xC0) == 0x80 && (utf8[2] & 0xC0) == 0x80 && (utf8[3] & 0xC0) == 0x80)
        {
            scalar = (uint)(((first & 0x07) << 18) | ((utf8[1] & 0x3F) << 12) | ((utf8[2] & 0x3F) << 6) | (utf8[3] & 0x3F));
            if (scalar < 0x10000 || scalar > 0x10FFFF)
                return false;
        }
        else
        {
            return false;
        }

        if (Rune.IsControl(new Rune((int)scalar)))
            return false;
        codePoint = scalar;
        return true;
    }

    /// <summary>Read the actual GLFW 3.4 platform backend, or false when the export is unavailable.</summary>
    public static bool TryGetPlatform(out int platform)
    {
        if (_getPlatform == null)
        {
            platform = 0;
            return false;
        }

        platform = _getPlatform();
        return true;
    }
}
