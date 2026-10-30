using System;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace Dotty.Silk.Rendering;

/// <summary>
/// Opt-in GL debugging (<c>DOTTY_GL_DEBUG=1</c>, plan-11 WI-5d): installs a <c>KHR_debug</c> message callback when
/// the context offers one and lets the host drain <c>glGetError</c> after uploads and draws. Messages go to stderr.
/// The callback is bound through the raw entry point (an <see cref="UnmanagedCallersOnlyAttribute"/> function
/// pointer), so no delegate marshalling is involved and it is safe under NativeAOT. UI thread only.
/// </summary>
internal static unsafe class GlDebugOutput
{
    internal const string EnvironmentVariable = "DOTTY_GL_DEBUG";

    private const uint DebugOutputSynchronousCap = 0x8242; // GL_DEBUG_OUTPUT_SYNCHRONOUS
    private const uint DebugOutputCap = 0x92E0;            // GL_DEBUG_OUTPUT
    private const uint GlDebugSeverityNotification = 0x826B;
    private const int MaxErrorsPerCheck = 16;

    internal static bool IsRequested() =>
        string.Equals(Environment.GetEnvironmentVariable(EnvironmentVariable), "1", StringComparison.Ordinal);

    /// <summary>
    /// Installs the debug callback when the context is OpenGL 4.3+ or exposes <c>GL_KHR_debug</c>. Returns false
    /// (and changes nothing) when neither is true or the entry point cannot be resolved.
    /// </summary>
    internal static bool TryInstall(GL gl, int majorVersion, int minorVersion, Func<string, nint> getProcAddress)
    {
        ArgumentNullException.ThrowIfNull(gl);
        ArgumentNullException.ThrowIfNull(getProcAddress);
        bool core43 = majorVersion > 4 || (majorVersion == 4 && minorVersion >= 3);
        if (!core43 && !gl.IsExtensionPresent("GL_KHR_debug"))
            return false;

        nint entry = getProcAddress("glDebugMessageCallback");
        if (entry == 0)
            return false;

        gl.Enable((EnableCap)DebugOutputCap);
        // Synchronous: the callback runs inside the offending call, so a stack trace from a debugger points at it.
        gl.Enable((EnableCap)DebugOutputSynchronousCap);
        var setCallback = (delegate* unmanaged<delegate* unmanaged<uint, uint, uint, uint, int, byte*, void*, void>, void*, void>)entry;
        setCallback(&OnMessage, null);
        return true;
    }

    /// <summary>Drains the GL error queue and reports every error with <paramref name="stage"/>; returns the count.</summary>
    internal static int CheckErrors(GL gl, string stage)
    {
        int count = 0;
        for (int i = 0; i < MaxErrorsPerCheck; i++)
        {
            GLEnum error = gl.GetError();
            if (error == GLEnum.NoError)
                break;
            count++;
            Console.Error.WriteLine($"[gl-debug] glGetError after {stage}: {error} (0x{(uint)error:X4})");
        }

        return count;
    }

    [UnmanagedCallersOnly]
    private static void OnMessage(uint source, uint type, uint id, uint severity, int length, byte* message, void* userParam)
    {
        if (severity == GlDebugSeverityNotification)
            return;

        try
        {
            string text = message == null
                ? string.Empty
                : length >= 0
                    ? Marshal.PtrToStringUTF8((nint)message, length)
                    : Marshal.PtrToStringUTF8((nint)message) ?? string.Empty;
            Console.Error.WriteLine($"[gl-debug] source=0x{source:X4} type=0x{type:X4} id={id} severity=0x{severity:X4}: {text}");
        }
        catch
        {
            // Never let an exception unwind into the driver.
        }
    }
}
