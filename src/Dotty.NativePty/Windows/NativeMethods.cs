#if WINDOWS

using System;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Dotty.NativePty.Windows;

/// <summary>
/// P/Invoke declarations for Windows Console PTY API and related functions.
/// </summary>
internal static class NativeMethods
{
    #region Kernel32 - Console PTY

    /// <summary>
    /// Creates a new pseudoconsole object for the calling process.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int CreatePseudoConsole(
        Coord size,
        IntPtr hInput,
        IntPtr hOutput,
        uint dwFlags,
        out IntPtr phPC);

    /// <summary>
    /// Closes a pseudoconsole handle.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = false)]
    internal static extern void ClosePseudoConsole(IntPtr hPC);

    /// <summary>
    /// Resizes the internal buffers for a pseudoconsole to the given size.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ResizePseudoConsole(IntPtr hPC, Coord size);

    #endregion

    #region Kernel32 - Process Creation

    /// <summary>
    /// Creates a new process and its primary thread.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcess(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        int dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref StartupInfoEx lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    /// <summary>
    /// Initializes the specified list of attributes for process and thread creation.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList,
        int dwAttributeCount,
        int dwFlags,
        ref IntPtr lpSize);

    /// <summary>
    /// Updates the specified attribute in a list of attributes for process and thread creation.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList,
        int dwFlags,
        IntPtr Attribute,
        IntPtr lpValue,
        IntPtr cbSize,
        IntPtr lpPreviousValue,
        IntPtr lpReturnSize);

    /// <summary>
    /// Deletes the specified list of attributes for process and thread creation.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = false)]
    internal static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    /// <summary>
    /// Retrieves the termination status of the specified process.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    /// <summary>
    /// Terminates the specified process and all of its threads.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    /// <summary>
    /// Waits until the specified object is in the signaled state or the time-out interval elapses.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    /// <summary>
    /// Closes an open object handle.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr hObject);

    #endregion

    #region Kernel32 - Pipe Operations

    /// <summary>
    /// Creates an anonymous pipe, and returns handles to the read and write ends of the pipe.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreatePipe(
        out SafeFileHandle hReadPipe,
        out SafeFileHandle hWritePipe,
        ref SecurityAttributes lpPipeAttributes,
        int nSize);

    /// <summary>
    /// Sets certain properties of an object handle.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetHandleInformation(
        SafeFileHandle hObject,
        int dwMask,
        int dwFlags);

    #endregion

    #region UserEnv - Environment

    /// <summary>
    /// Creates an environment block from the specified environment variables.
    /// </summary>
    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateEnvironmentBlock(
        out IntPtr lpEnvironment,
        IntPtr hToken,
        [MarshalAs(UnmanagedType.Bool)] bool bInherit);

    /// <summary>
    /// Frees environment strings created by CreateEnvironmentBlock.
    /// </summary>
    [DllImport("userenv.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    #endregion
}

/// <summary>
/// Represents the coordinates of a character cell in a console screen buffer.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Coord
{
    public short X;
    public short Y;

    public Coord(short x, short y)
    {
        X = x;
        Y = y;
    }
}

/// <summary>
/// Security attributes for pipes and process/thread handles.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SecurityAttributes
{
    public int nLength;
    public IntPtr lpSecurityDescriptor;
    [MarshalAs(UnmanagedType.Bool)]
    public bool bInheritHandle;
}

/// <summary>
/// Extended startup information for process creation.
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct StartupInfoEx
{
    public StartupInfo StartupInfo;
    public IntPtr lpAttributeList;
}

/// <summary>
/// Specifies the window station, desktop, standard handles, and appearance of the main window for a process at creation time.
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct StartupInfo
{
    public int cb;
    public string lpReserved;
    public string lpDesktop;
    public string lpTitle;
    public int dwX;
    public int dwY;
    public int dwXSize;
    public int dwYSize;
    public int dwXCountChars;
    public int dwYCountChars;
    public int dwFillAttribute;
    public int dwFlags;
    public short wShowWindow;
    public short cbReserved2;
    public IntPtr lpReserved2;
    public IntPtr hStdInput;
    public IntPtr hStdOutput;
    public IntPtr hStdError;
}

/// <summary>
/// Contains information about a newly created process and its primary thread.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ProcessInformation
{
    public IntPtr hProcess;
    public IntPtr hThread;
    public int dwProcessId;
    public int dwThreadId;
}

#endif
