#if WINDOWS

using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dotty.Abstractions.Pty;
using Microsoft.Win32.SafeHandles;

namespace Dotty.NativePty.Windows;

/// <summary>
/// Windows Console Pseudo Terminal (ConPTY) implementation using the Windows Console API.
/// Requires Windows 10 version 1809 (build 17763) or later.
/// </summary>
public sealed class WindowsPty : IPty
{
    private IntPtr _pseudoConsoleHandle;
    private ProcessInformation _processInfo;
    private SafeFileHandle? _inputReadHandle;
    private SafeFileHandle? _inputWriteHandle;
    private SafeFileHandle? _outputReadHandle;
    private SafeFileHandle? _outputWriteHandle;
    private Stream? _inputStream;
    private Stream? _outputStream;
    private bool _isDisposed;
    private bool _isStarted;
    private readonly object _stateLock = new();

    /// <inheritdoc />
    public bool IsRunning { get; private set; }

    /// <inheritdoc />
    public int ProcessId => _processInfo.dwProcessId > 0 ? _processInfo.dwProcessId : -1;

    /// <inheritdoc />
    public Stream? OutputStream => _outputStream;

    /// <inheritdoc />
    public Stream? InputStream => _inputStream;

    /// <inheritdoc />
    public event EventHandler<int>? ProcessExited;

    /// <summary>
    /// Creates a new Windows ConPTY instance.
    /// </summary>
    public WindowsPty()
    {
        _pseudoConsoleHandle = IntPtr.Zero;
        _processInfo = new ProcessInformation();
    }

    /// <inheritdoc />
    public void Start(
        string? shell = null, 
        int columns = 80, 
        int rows = 24,
        string? workingDirectory = null,
        System.Collections.Generic.IDictionary<string, string>? environmentVariables = null)
    {
        lock (_stateLock)
        {
            if (_isStarted)
                throw new InvalidOperationException("PTY session is already started.");
            
            if (!PtyPlatform.IsConPtySupported)
                throw new PtyException("ConPTY is not supported on this Windows version. Requires Windows 10 build 17763 or later.");

            try
            {
                // Create the pseudo console
                CreatePseudoConsole(columns, rows);
                
                // Start the shell process attached to the pseudo console
                StartShellProcess(shell, workingDirectory, environmentVariables);
                
                // CreatePipe produces synchronous handles; wrapping them as async
                // streams throws ArgumentException on Windows.
                _inputStream = new FileStream(_inputWriteHandle!, FileAccess.Write, 4096, false);
                _outputStream = new FileStream(_outputReadHandle!, FileAccess.Read, 4096, false);
                
                _isStarted = true;
                IsRunning = true;
                
                // Monitor process exit
                _ = Task.Run(MonitorProcessExit);
            }
            catch (Exception ex) when (ex is not PtyException and not InvalidOperationException)
            {
                CleanupResources();
                throw new PtyException($"Failed to start Windows ConPTY: {ex.Message}", ex);
            }
        }
    }

    /// <inheritdoc />
    public void Resize(int columns, int rows)
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(WindowsPty));
        
        if (_pseudoConsoleHandle == IntPtr.Zero)
            throw new InvalidOperationException("Pseudo console is not created.");

        var size = new Coord((short)columns, (short)rows);
        
        if (!NativeMethods.ResizePseudoConsole(_pseudoConsoleHandle, size))
        {
            throw new PtyException($"Failed to resize pseudo console. Error: {Marshal.GetLastWin32Error()}");
        }
    }

    /// <inheritdoc />
    public void Kill(bool force = false)
    {
        lock (_stateLock)
        {
            if (_isDisposed || !IsRunning)
                return;

            try
            {
                // Get process handle
                var processHandle = new IntPtr(_processInfo.hProcess);
                
                if (processHandle != IntPtr.Zero)
                {
                    if (force)
                    {
                        // Force terminate
                        NativeMethods.TerminateProcess(processHandle, 1);
                    }
                    else
                    {
                        // Try graceful shutdown first by closing the pseudo console
                        // This sends Ctrl+C to the attached process group
                        if (_pseudoConsoleHandle != IntPtr.Zero)
                        {
                            NativeMethods.ClosePseudoConsole(_pseudoConsoleHandle);
                            _pseudoConsoleHandle = IntPtr.Zero;
                        }
                        
                        // Wait a bit for graceful exit
                        var waitResult = NativeMethods.WaitForSingleObject(processHandle, 2000);
                        if (waitResult == 0x00000102) // WAIT_TIMEOUT
                        {
                            // Force kill if still running
                            NativeMethods.TerminateProcess(processHandle, 1);
                        }
                    }
                }
            }
            catch { }
            finally
            {
                IsRunning = false;
            }
        }
    }

    /// <inheritdoc />
    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        if (_processInfo.hProcess == IntPtr.Zero)
            throw new InvalidOperationException("Process is not started.");

        var processHandle = new IntPtr(_processInfo.hProcess);
        
        using var registration = cancellationToken.Register(() => Kill(force: true));

        // Wait for process to exit
        while (true)
        {
            if (NativeMethods.WaitForSingleObject(processHandle, 100) == 0) // WAIT_OBJECT_0
            {
                if (NativeMethods.GetExitCodeProcess(processHandle, out uint exitCode))
                {
                    IsRunning = false;
                    return (int)exitCode;
                }
                return -1;
            }

            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException();

            await Task.Delay(10, cancellationToken);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_isDisposed)
                return;

            Kill(force: true);
            CleanupResources();
            _isDisposed = true;
        }
    }

    private void CreatePseudoConsole(int columns, int rows)
    {
        // Create input pipe (from our input -> to PTY input)
        CreatePipe(out _inputReadHandle, out _inputWriteHandle, childInheritsReadEnd: true);
        
        // Create output pipe (from PTY output -> to our output)
        CreatePipe(out _outputReadHandle, out _outputWriteHandle, childInheritsReadEnd: false);

        var size = new Coord((short)columns, (short)rows);
        
        // Create the pseudo console
        // The input read handle is where PTY reads from (our writes go here)
        // The output write handle is where PTY writes to (we read from the other end)
        var result = NativeMethods.CreatePseudoConsole(
            size,
            _inputReadHandle!.DangerousGetHandle(),
            _outputWriteHandle!.DangerousGetHandle(),
            0, // No flags
            out _pseudoConsoleHandle);

        if (result != 0)
        {
            throw new Win32Exception(result, "Failed to create pseudo console");
        }

        // Once the pseudoconsole is created, it owns these ends of the pipes.
        _inputReadHandle?.Dispose();
        _inputReadHandle = null;
        _outputWriteHandle?.Dispose();
        _outputWriteHandle = null;
    }

    private void CreatePipe(
        out SafeFileHandle readHandle,
        out SafeFileHandle writeHandle,
        bool childInheritsReadEnd)
    {
        var securityAttributes = new SecurityAttributes
        {
            nLength = Marshal.SizeOf<SecurityAttributes>(),
            bInheritHandle = true
        };

        if (!NativeMethods.CreatePipe(out readHandle, out writeHandle, ref securityAttributes, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create pipe");
        }

        // Keep only the end used by the pseudo console host inheritable.
        // Input pipe: child should inherit read end.
        // Output pipe: child should inherit write end.
        var localHandle = childInheritsReadEnd ? writeHandle : readHandle;
        if (!NativeMethods.SetHandleInformation(localHandle, 1 /* HANDLE_FLAG_INHERIT */, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to set handle information");
        }
    }

    private void StartShellProcess(
        string? shell, 
        string? workingDirectory,
        System.Collections.Generic.IDictionary<string, string>? environmentVariables)
    {
        shell ??= PtyPlatform.GetDefaultShell();
        
        // Prepare startup info with pseudo console attribute
        var startupInfoEx = new StartupInfoEx();
        startupInfoEx.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
        
        // Create the attribute list for the pseudo console
        const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
        IntPtr attributeList = IntPtr.Zero;
        IntPtr attributeListSize = IntPtr.Zero;
        
        try
        {
            // Calculate attribute list size
            if (!NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != 122) // ERROR_INSUFFICIENT_BUFFER
                    throw new Win32Exception(error, "Failed to initialize proc thread attribute list");
            }

            // Allocate memory for attribute list
            attributeList = Marshal.AllocHGlobal(attributeListSize);
            
            if (!NativeMethods.InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to initialize proc thread attribute list");
            }

            // Set pseudo console attribute using the HPCON handle value.
            if (!NativeMethods.UpdateProcThreadAttribute(
                attributeList,
                0,
                (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                _pseudoConsoleHandle,
                (IntPtr)IntPtr.Size,
                IntPtr.Zero,
                IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to update proc thread attribute");
            }

            startupInfoEx.lpAttributeList = attributeList;

            // Prepare environment if needed
            IntPtr environmentPtr = IntPtr.Zero;
            if (environmentVariables != null && environmentVariables.Count > 0)
            {
                var currentEnv = Environment.GetEnvironmentVariables();
                foreach (var key in environmentVariables.Keys)
                {
                    currentEnv[key] = environmentVariables[key];
                }
                environmentPtr = CreateEnvironmentBlock(currentEnv);
            }

            try
            {
                var (applicationName, commandLine) = BuildProcessStartInfo(shell);

                // Create the process
                var creationFlags = 0x00080000 /* EXTENDED_STARTUPINFO_PRESENT */ | 0x00000400 /* CREATE_UNICODE_ENVIRONMENT */;
                
                bool success = NativeMethods.CreateProcess(
                    applicationName,     // Application name
                    commandLine,         // Command line (must be writable)
                    IntPtr.Zero,         // Process security attributes
                    IntPtr.Zero,         // Thread security attributes
                    false,               // Inherit handles
                    creationFlags,
                    environmentPtr,      // Environment
                    workingDirectory,    // Current directory
                    ref startupInfoEx,
                    out _processInfo);

                if (!success)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"Failed to create process: {shell}");
                }
            }
            finally
            {
                if (environmentPtr != IntPtr.Zero)
                {
                    NativeMethods.DestroyEnvironmentBlock(environmentPtr);
                }
            }
        }
        finally
        {
            if (attributeList != IntPtr.Zero)
            {
                NativeMethods.DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
        }

        // Close the thread handle as we don't need it
        if (_processInfo.hThread != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_processInfo.hThread);
            _processInfo.hThread = IntPtr.Zero;
        }
    }

    private static (string? ApplicationName, StringBuilder CommandLine) BuildProcessStartInfo(string shell)
    {
        if (string.IsNullOrWhiteSpace(shell))
        {
            return (null, new StringBuilder("cmd.exe"));
        }

        if (File.Exists(shell))
        {
            return (shell, new StringBuilder(QuoteCommandLineArgument(shell)));
        }

        if (TryExtractQuotedExecutable(shell, out var quotedExecutable, out var quotedArguments))
        {
            return (File.Exists(quotedExecutable) ? quotedExecutable : null, new StringBuilder(BuildCommandLine(quotedExecutable, quotedArguments)));
        }

        if (TryResolveExecutablePath(shell, out var executablePath, out var arguments))
        {
            return (executablePath, new StringBuilder(BuildCommandLine(executablePath, arguments)));
        }

        return (null, new StringBuilder(shell));
    }

    private static bool TryExtractQuotedExecutable(string shell, out string executable, out string arguments)
    {
        executable = string.Empty;
        arguments = string.Empty;

        if (string.IsNullOrEmpty(shell) || shell[0] != '"')
        {
            return false;
        }

        var closingQuote = shell.IndexOf('"', 1);
        if (closingQuote <= 1)
        {
            return false;
        }

        executable = shell[1..closingQuote];
        arguments = shell[(closingQuote + 1)..].TrimStart();
        return true;
    }

    private static bool TryResolveExecutablePath(string shell, out string executable, out string arguments)
    {
        executable = string.Empty;
        arguments = string.Empty;

        for (var index = 0; index < shell.Length; index++)
        {
            if (!char.IsWhiteSpace(shell[index]))
            {
                continue;
            }

            var candidate = shell[..index].Trim();
            if (!File.Exists(candidate))
            {
                continue;
            }

            executable = candidate;
            arguments = shell[index..].TrimStart();
            return true;
        }

        return false;
    }

    private static string BuildCommandLine(string executable, string arguments)
    {
        var quotedExecutable = QuoteCommandLineArgument(executable);
        return string.IsNullOrWhiteSpace(arguments)
            ? quotedExecutable
            : $"{quotedExecutable} {arguments}";
    }

    private static string QuoteCommandLineArgument(string value)
    {
        return value.Contains(' ') && !value.StartsWith('"')
            ? $"\"{value}\""
            : value;
    }

    private IntPtr CreateEnvironmentBlock(System.Collections.IDictionary environment)
    {
        // Build environment block
        var sb = new System.Text.StringBuilder();
        foreach (System.Collections.DictionaryEntry entry in environment)
        {
            sb.Append($"{entry.Key}={entry.Value}\0");
        }
        sb.Append('\0'); // Double null terminator

        var bytes = System.Text.Encoding.Unicode.GetBytes(sb.ToString());
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return ptr;
    }

    private async Task MonitorProcessExit()
    {
        if (_processInfo.hProcess == IntPtr.Zero)
            return;

        var processHandle = new IntPtr(_processInfo.hProcess);
        
        try
        {
            while (true)
            {
                if (NativeMethods.WaitForSingleObject(processHandle, 100) == 0) // WAIT_OBJECT_0
                {
                    uint exitCode = 0;
                    NativeMethods.GetExitCodeProcess(processHandle, out exitCode);
                    
                    IsRunning = false;
                    ProcessExited?.Invoke(this, (int)exitCode);
                    break;
                }
                
                await Task.Delay(10);
            }
        }
        catch { }
    }

    private void CleanupResources()
    {
        _inputStream?.Dispose();
        _inputStream = null;
        _outputStream?.Dispose();
        _outputStream = null;
        
        _inputReadHandle?.Dispose();
        _inputWriteHandle?.Dispose();
        _inputWriteHandle = null;
        _outputReadHandle?.Dispose();
        _outputReadHandle = null;
        _outputWriteHandle?.Dispose();
        _inputReadHandle = null;
        _outputWriteHandle = null;
        
        if (_pseudoConsoleHandle != IntPtr.Zero)
        {
            NativeMethods.ClosePseudoConsole(_pseudoConsoleHandle);
            _pseudoConsoleHandle = IntPtr.Zero;
        }

        if (_processInfo.hProcess != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_processInfo.hProcess);
        }

        if (_processInfo.hThread != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_processInfo.hThread);
        }

        _processInfo = new ProcessInformation();
        _isStarted = false;
        IsRunning = false;
    }
}

#endif
