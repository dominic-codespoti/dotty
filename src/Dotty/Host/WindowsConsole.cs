using System.Runtime.InteropServices;

namespace Dotty.Silk;

/// <summary>
/// On Windows <c>dotty.exe</c> is a GUI-subsystem executable (<c>WinExe</c>), so starting it never flashes a
/// console window. Command-line output (<c>--help</c>, <c>--version</c>, <c>+actions</c>, usage errors) is
/// written to the console of the launching shell by attaching to it first; output redirected to a file or pipe
/// is inherited as-is. Other platforms are unaffected.
/// </summary>
internal static partial class WindowsConsole
{
    private const uint AttachParentProcess = unchecked((uint)-1);
    private static bool _attachAttempted;

    /// <summary>
    /// Attaches to the parent process's console, once, before the first <see cref="Console"/> use. A process
    /// started from Explorer has no parent console and keeps writing to its (absent or redirected) handles.
    /// cmd.exe does not wait for GUI-subsystem programs, so the output can interleave with the next prompt.
    /// </summary>
    internal static void AttachParentConsole()
    {
        if (!OperatingSystem.IsWindows() || _attachAttempted)
            return;
        _attachAttempted = true;
        _ = AttachConsole(AttachParentProcess);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int AttachConsole(uint processId);
}
