using System;
using System.IO;
using System.Threading;
using Silk.NET.GLFW;

namespace Dotty.Silk;

/// <summary>
/// Routes GLFW diagnostics to stderr, not the crash log: a callback does not mean the process failed.
/// Install after the window exists: Silk sets its own callback when it
/// initializes GLFW (that one throws on Windows and keeps every error in an unbounded list elsewhere).
/// </summary>
internal static class GlfwErrorLog
{
    // GLFW keeps only the native function pointer; this field keeps the delegate behind it alive.
    private static readonly GlfwCallbacks.ErrorCallback Callback = OnError;
    private const int MaxErrors = 16;
    private static int _errors;

    public static void Install(Glfw glfw) => glfw.SetErrorCallback(Callback);

    private static void OnError(ErrorCode error, string description)
    {
        int count = Interlocked.Increment(ref _errors);
        if (count > MaxErrors)
            return;
        try
        {
            Console.Error.WriteLine($"dotty: GLFW diagnostic {(int)error:X} ({error}): {description}");
            if (count == MaxErrors)
                Console.Error.WriteLine($"dotty: further GLFW diagnostics are omitted after {MaxErrors} messages.");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // No managed exception may escape the native error callback.
        }
    }
}
