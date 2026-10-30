using System;
using Silk.NET.OpenGL;

namespace Dotty.Silk.Rendering;

/// <summary>
/// Polls <c>glGetGraphicsResetStatus</c> (OpenGL 4.5 or <c>GL_KHR_robustness</c>; <c>glGetGraphicsResetStatusARB</c>
/// for <c>GL_ARB_robustness</c>). The entry point is resolved once and called through a raw function pointer, so a
/// 3.3 context without the extension costs nothing and nothing is resolved lazily by name per frame. Without a
/// robust context (<c>GLFW_CONTEXT_ROBUSTNESS</c>) the driver may still always answer <c>GL_NO_ERROR</c>.
/// UI thread only (the context must be current).
/// </summary>
internal sealed unsafe class GlContextResetProbe
{
    /// <summary><c>GL_CONTEXT_LOST</c>: every command on a lost robust context reports it.</summary>
    internal const uint GlContextLost = 0x0507;

    private readonly delegate* unmanaged<uint> _getGraphicsResetStatus;

    private GlContextResetProbe(nint entryPoint) =>
        _getGraphicsResetStatus = (delegate* unmanaged<uint>)entryPoint;

    internal static GlContextResetProbe? TryCreate(GL gl, int majorVersion, int minorVersion, Func<string, nint> getProcAddress)
    {
        ArgumentNullException.ThrowIfNull(gl);
        ArgumentNullException.ThrowIfNull(getProcAddress);
        bool core45 = majorVersion > 4 || (majorVersion == 4 && minorVersion >= 5);
        string? entryName = core45 || gl.IsExtensionPresent("GL_KHR_robustness")
            ? "glGetGraphicsResetStatus"
            : gl.IsExtensionPresent("GL_ARB_robustness")
                ? "glGetGraphicsResetStatusARB"
                : null;
        if (entryName == null)
            return null;

        nint entry = getProcAddress(entryName);
        return entry == 0 ? null : new GlContextResetProbe(entry);
    }

    /// <summary>
    /// 0 (<c>GL_NO_ERROR</c>) when no reset happened since the last call; otherwise <c>GL_GUILTY_CONTEXT_RESET</c>
    /// (0x8253), <c>GL_INNOCENT_CONTEXT_RESET</c> (0x8254) or <c>GL_UNKNOWN_CONTEXT_RESET</c> (0x8255).
    /// </summary>
    internal uint Poll() => _getGraphicsResetStatus();

    /// <summary>
    /// After a reset the context stays lost until it is recreated: drains the error queue and reports whether any
    /// command failed with <see cref="GlContextLost"/>.
    /// </summary>
    internal static bool ContextStillLost(GL gl)
    {
        for (int i = 0; i < 16; i++)
        {
            GLEnum error = gl.GetError();
            if (error == GLEnum.NoError)
                return false;
            if ((uint)error == GlContextLost)
                return true;
        }

        return false;
    }

    internal static string DescribeStatus(uint status) => status switch
    {
        0x8253 => "guilty context reset (Dotty's commands caused it)",
        0x8254 => "innocent context reset (another process caused it)",
        0x8255 => "context reset of unknown cause",
        GlContextLost => "context lost",
        _ => $"graphics reset status 0x{status:X4}",
    };
}
