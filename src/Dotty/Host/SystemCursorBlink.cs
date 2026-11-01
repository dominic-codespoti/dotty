using System;
using System.Runtime.InteropServices;

namespace Dotty.Silk;

/// <summary>
/// The operating system's caret-blink preference where it is cheap to read: Windows reports
/// <c>GetCaretBlinkTime() == INFINITE</c> when blinking is turned off. Elsewhere it is unknown (null) and
/// <c>cursor.blink</c> alone decides. Read once per process.
/// </summary>
internal static partial class SystemCursorBlink
{
    private const uint Infinite = 0xFFFFFFFF;

    public static bool? Allowed { get; } = Query();

    private static bool? Query()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            uint time = GetCaretBlinkTime();
            return time == 0 ? null : time != Infinite;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetCaretBlinkTime();
}
