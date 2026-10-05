using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Dotty.Silk;

internal static unsafe partial class WindowsConsole
{
    internal static void DetachOwnedConsole()
    {
        if (!OperatingSystem.IsWindows())
            return;

        uint processId = 0;
        // A larger result means the console is shared; the one-element buffer
        // need not be expanded because inherited consoles must remain attached.
        if (GetConsoleProcessList(&processId, 1) != 1 || processId != (uint)Environment.ProcessId)
            return;

        nint input = GetRedirectedHandle(-10);
        nint output = GetRedirectedHandle(-11);
        nint error = GetRedirectedHandle(-12);
        if (FreeConsole() == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        RestoreRedirectedHandle(-10, input);
        RestoreRedirectedHandle(-11, output);
        RestoreRedirectedHandle(-12, error);
    }

    private static nint GetRedirectedHandle(int standardHandle)
    {
        nint handle = GetStdHandle(standardHandle);
        return handle != 0 && handle != -1 && GetConsoleMode(handle, out _) == 0 ? handle : 0;
    }

    private static void RestoreRedirectedHandle(int standardHandle, nint handle)
    {
        if (handle != 0 && SetStdHandle(standardHandle, handle) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetConsoleProcessList(uint* processList, uint processCount);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int FreeConsole();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetStdHandle(int standardHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GetConsoleMode(nint handle, out uint mode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int SetStdHandle(int standardHandle, nint handle);
}
