using Dotty.Runtime.Diagnostics;
using Silk.NET.GLFW;

namespace Dotty.Silk;

/// <summary>
/// Routes GLFW errors into the crash log. Install after the window exists: Silk sets its own callback when it
/// initializes GLFW (that one throws on Windows and keeps every error in an unbounded list elsewhere).
/// </summary>
internal static class GlfwErrorLog
{
    // GLFW keeps only the native function pointer; this field keeps the delegate behind it alive.
    private static readonly GlfwCallbacks.ErrorCallback Callback = OnError;

    public static void Install(Glfw glfw) => glfw.SetErrorCallback(Callback);

    private static void OnError(ErrorCode error, string description) =>
        CrashReporter.ReportGlfwError($"{(int)error:X} ({error})", description);
}
