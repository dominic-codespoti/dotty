#if WINDOWS

using System;
using System.ComponentModel;
using System.Collections.Generic;
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

    public string? LastError { get; private set; }
    public PtyErrorCode? LastErrorCode { get; private set; }

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
            {
                throw new PtyException(
                    PtyErrorCode.ConPtyUnavailable,
                    "ConPTY requires Windows 10 build 17763 or later.");
            }

            ValidateDimensions(columns, rows);
            LastError = null;
            LastErrorCode = null;

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
            catch (PtyException ex)
            {
                CleanupResources();
                LastErrorCode = ex.Code;
                LastError = ex.Message;
                throw;
            }
            catch (Exception ex)
            {
                CleanupResources();
                LastErrorCode = PtyErrorCode.ProcessStartFailed;
                LastError = ex.Message;
                throw new PtyException(
                    PtyErrorCode.ProcessStartFailed,
                    $"Failed to start Windows ConPTY: {ex.Message}",
                    ex);
            }
        }
    }

    /// <inheritdoc />
    public void Resize(int columns, int rows)
    {
        lock (_stateLock)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(WindowsPty));
            if (_pseudoConsoleHandle == IntPtr.Zero)
                throw new InvalidOperationException("Pseudo console is not started.");

            ValidateDimensions(columns, rows);
            var size = new Coord((short)columns, (short)rows);
            int result = NativeMethods.ResizePseudoConsole(_pseudoConsoleHandle, size);
            if (result != 0)
            {
                string hresult = $"0x{result:X8}";
                LastErrorCode = PtyErrorCode.ResizeFailed;
                LastError = $"Windows HRESULT {hresult}.";
                throw new PtyException(
                    PtyErrorCode.ResizeFailed,
                    $"Failed to resize pseudo console. HRESULT: {hresult}");
            }
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
        cancellationToken.ThrowIfCancellationRequested();
        if (_processInfo.hProcess == IntPtr.Zero)
            throw new InvalidOperationException("Process is not started.");

        var processHandle = new IntPtr(_processInfo.hProcess);
        using var registration = cancellationToken.Register(() => Kill(force: true));

        while (true)
        {
            if (NativeMethods.WaitForSingleObject(processHandle, 100) == 0)
            {
                if (NativeMethods.GetExitCodeProcess(processHandle, out uint exitCode))
                {
                    IsRunning = false;
                    return (int)exitCode;
                }
                return -1;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
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

    private static void ValidateDimensions(int columns, int rows)
    {
        if (columns <= 0 || rows <= 0 || columns > short.MaxValue || rows > short.MaxValue)
        {
            throw new PtyException(
                PtyErrorCode.InvalidDimensions,
                $"Console dimensions must be between 1 and {short.MaxValue}; received {columns}x{rows}.");
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
        if (!string.IsNullOrWhiteSpace(workingDirectory) && !Directory.Exists(workingDirectory))
        {
            throw new PtyException(
                PtyErrorCode.InvalidWorkingDirectory,
                $"The PTY working directory '{workingDirectory}' does not exist.");
        }
        string resolvedShell = string.IsNullOrWhiteSpace(shell)
            ? PtyPlatform.GetDefaultShell()
            : shell;
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
                var (applicationName, commandLine) = BuildProcessStartInfo(resolvedShell);

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
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"Failed to create process: {resolvedShell}");
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
        if (File.Exists(shell))
            return (shell, new StringBuilder(QuoteCommandLineArgument(shell)));

        for (int whitespace = 0; whitespace < shell.Length; whitespace++)
        {
            if (!char.IsWhiteSpace(shell[whitespace]))
                continue;

            string candidate = shell[..whitespace].Trim();
            if (candidate.Length == 0 || candidate[0] == '"' || !File.Exists(candidate))
                continue;

            string trailingArguments = shell[(whitespace + 1)..].TrimStart();
            string commandLine = QuoteCommandLineArgument(candidate);
            if (trailingArguments.Length > 0)
                commandLine += " " + trailingArguments;
            return (candidate, new StringBuilder(commandLine));
        }

        var arguments = ParseWindowsCommandLine(shell);
        if (arguments.Count == 0)
            return (null, new StringBuilder("cmd.exe"));

        string executable = arguments[0];
        string? applicationName = File.Exists(executable) ? executable : null;
        if (Path.IsPathFullyQualified(executable) && applicationName == null)
        {
            throw new PtyException(
                PtyErrorCode.InvalidShell,
                $"The configured shell '{executable}' does not exist.");
        }

        return (
            applicationName,
            new StringBuilder(BuildCommandLine(executable, arguments)));
    }

    private static List<string> ParseWindowsCommandLine(string commandLine)
    {
        var arguments = new List<string>();
        var token = new StringBuilder();
        bool inQuotes = false;
        int index = 0;

        while (index < commandLine.Length)
        {
            while (index < commandLine.Length && char.IsWhiteSpace(commandLine[index]))
                index++;
            if (index >= commandLine.Length)
                break;

            token.Clear();
            while (index < commandLine.Length)
            {
                int slashCount = 0;
                while (index < commandLine.Length && commandLine[index] == '\\')
                {
                    slashCount++;
                    index++;
                }

                if (index < commandLine.Length && commandLine[index] == '"')
                {
                    token.Append('\\', slashCount / 2);
                    if ((slashCount & 1) != 0)
                    {
                        token.Append('"');
                        index++;
                    }
                    else if (index + 1 < commandLine.Length && commandLine[index + 1] == '"')
                    {
                        token.Append('"');
                        index += 2;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                        index++;
                    }
                    continue;
                }

                token.Append('\\', slashCount);
                if (index >= commandLine.Length)
                    break;
                if (!inQuotes && char.IsWhiteSpace(commandLine[index]))
                    break;
                token.Append(commandLine[index++]);
            }

            arguments.Add(token.ToString());
            inQuotes = false;
        }

        return arguments;
    }

    private static string BuildCommandLine(string executable, IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder(QuoteCommandLineArgument(executable));
        for (int index = 1; index < arguments.Count; index++)
        {
            builder.Append(' ');
            builder.Append(QuoteCommandLineArgument(arguments[index]));
        }
        return builder.ToString();
    }

    private static string QuoteCommandLineArgument(string value)
    {
        if (value.Length == 0)
            return "\"\"";
        if (!value.Any(char.IsWhiteSpace) && !value.Contains('"'))
            return value;

        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        int backslashes = 0;
        foreach (char character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
            }
            else
            {
                builder.Append('\\', backslashes);
                builder.Append(character);
            }
            backslashes = 0;
        }
        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
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
