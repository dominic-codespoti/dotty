using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dotty.Abstractions.Pty;

namespace Dotty.NativePty.Unix;

/// <summary>
/// Unix PTY implementation using the native pty-helper process.
/// Supports Linux and macOS via the C-based forkpty helper.
/// </summary>
public sealed class UnixPty : IPty
{
    private Process? _helperProcess;
    private Stream? _inputStream;
    private Stream? _outputStream;
    private Stream? _errorStream;
    private string? _controlSocketPath;
    private Stream? _controlSocketStream;
    private (int Columns, int Rows)? _pendingResize;
    private readonly SemaphoreSlim _controlWriteLock = new(1, 1);
    private int _startupColumns = 80;
    private int _startupRows = 24;
    private bool _isDisposed;
    private bool _isStarted;
    private readonly object _stateLock = new();

    public string? LastError { get; private set; }
    public PtyErrorCode? LastErrorCode { get; private set; }

    /// <inheritdoc />
    public bool IsRunning 
    { 
        get
        {
            try
            {
                return _helperProcess?.HasExited == false;
            }
            catch (InvalidOperationException)
            {
                // Process has been disposed or is not associated
                return false;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <inheritdoc />
    public int ProcessId => _helperProcess?.Id ?? -1;

    /// <inheritdoc />
    public Stream? OutputStream => _outputStream;

    /// <inheritdoc />
    public Stream? InputStream => _inputStream;

    /// <inheritdoc />
    public event EventHandler<int>? ProcessExited;

    /// <summary>
    /// Creates a new Unix PTY instance.
    /// </summary>
    public UnixPty()
    {
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

            string? helperExe = FindHelperExecutableForCurrentProcess();
            if (string.IsNullOrEmpty(helperExe))
            {
                throw new PtyException(
                    PtyErrorCode.NativeHelperMissing,
                    "The pty-helper executable was not found. Place it beside the application or add it to PATH.");
            }

            if (!IsExecutable(helperExe))
            {
                throw new PtyException(
                    PtyErrorCode.NativeHelperNotExecutable,
                    $"The pty-helper at '{helperExe}' is not executable.");
            }

            string resolvedShell = string.IsNullOrWhiteSpace(shell)
                ? PtyPlatform.GetDefaultShell()
                : shell;
            List<string> shellArguments;
            try
            {
                shellArguments = ParseCommandLine(resolvedShell);
            }
            catch (FormatException ex)
            {
                throw new PtyException(PtyErrorCode.InvalidShell, ex.Message, ex);
            }

            if (shellArguments.Count == 0)
            {
                throw new PtyException(PtyErrorCode.InvalidShell, "The configured shell command is empty.");
            }

            string executable = shellArguments[0];
            if (Path.IsPathFullyQualified(executable) && !File.Exists(executable))
            {
                throw new PtyException(
                    PtyErrorCode.InvalidShell,
                    $"The configured shell '{executable}' does not exist.");
            }

            string workingDirectoryPath = workingDirectory
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(workingDirectoryPath))
                workingDirectoryPath = Environment.CurrentDirectory;
            if (!Directory.Exists(workingDirectoryPath))
            {
                throw new PtyException(
                    PtyErrorCode.InvalidWorkingDirectory,
                    $"The PTY working directory '{workingDirectoryPath}' does not exist.");
            }

            var psi = new ProcessStartInfo
            {
                FileName = helperExe,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectoryPath,
            };
            foreach (var argument in shellArguments)
                psi.ArgumentList.Add(argument);
            if (shellArguments.Count == 1 && IsInteractiveShell(executable))
                psi.ArgumentList.Add("-i");
            // Unix-domain socket path limits vary; stay below the portable
            // sockaddr_un sun_path limit.
            string controlPath = CreateControlSocketPath();
            psi.EnvironmentVariables["DOTTY_CONTROL_SOCKET"] = controlPath;
            _controlSocketPath = controlPath;
            _startupColumns = Math.Max(1, columns);
            _startupRows = Math.Max(1, rows);
            _pendingResize = null;
            psi.EnvironmentVariables["DOTTY_INITIAL_COLS"] = _startupColumns.ToString();
            psi.EnvironmentVariables["DOTTY_INITIAL_ROWS"] = _startupRows.ToString();

            // Add environment variables before forcing terminal identity.
            if (environmentVariables != null)
            {
                foreach (var kvp in environmentVariables)
                    psi.EnvironmentVariables[kvp.Key] = kvp.Value;
            }

            psi.EnvironmentVariables["TERM"] = "xterm-256color";
            psi.EnvironmentVariables["COLORTERM"] = "truecolor";

            try
            {
                _helperProcess = Process.Start(psi);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                throw new PtyException(
                    PtyErrorCode.ProcessStartFailed,
                    $"Failed to start pty-helper at '{helperExe}'.",
                    ex);
            }

            if (_helperProcess == null)
            {
                LastError = "Process.Start returned null.";
                throw new PtyException(
                    PtyErrorCode.ProcessStartFailed,
                    "Failed to start the pty-helper process.");
            }

            _inputStream = _helperProcess.StandardInput.BaseStream;
            _outputStream = _helperProcess.StandardOutput.BaseStream;
            _errorStream = _helperProcess.StandardError.BaseStream;

            _isStarted = true;

            // Monitor process exit - attach handler first
            _helperProcess.Exited += (sender, e) =>
            {
                try
                {
                    var exitCode = _helperProcess?.ExitCode ?? -1;
                    ProcessExited?.Invoke(this, exitCode);
                }
                catch { }
            };
            
            // Enable raising events AFTER handler is attached
            // This ensures we don't miss any exit events
            _helperProcess.EnableRaisingEvents = true;

            // Check if process has already exited (race condition)
            // Fire the event synchronously if it has
            try
            {
                if (_helperProcess.HasExited)
                {
                    var exitCode = _helperProcess.ExitCode;
                    ProcessExited?.Invoke(this, exitCode);
                }
            }
            catch { }

            // Connect to control socket in background
            if (!string.IsNullOrEmpty(_controlSocketPath))
            {
                _ = Task.Run(() => ConnectToControlSocketAsync(_controlSocketPath));
            }
        }
    }

    /// <inheritdoc />
    public void Resize(int columns, int rows)
    {
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);

        Stream? controlSocket;
        lock (_stateLock)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(UnixPty));

            controlSocket = _controlSocketStream;
            if (controlSocket == null)
            {
                _pendingResize = (columns, rows);
                return;
            }
        }

        _ = SendResizeMessageAsync(columns, rows, controlSocket);
    }

    /// <inheritdoc />
    public void Kill(bool force = false)
    {
        lock (_stateLock)
        {
            if (_isDisposed || _helperProcess == null)
                return;

            try
            {
                if (!_helperProcess.HasExited)
                {
                    if (force)
                    {
                        _helperProcess.Kill();
                        // Wait for the process to actually terminate
                        _helperProcess.WaitForExit(5000);
                    }
                    else
                    {
                        // Try graceful termination by closing input
                        _inputStream?.Close();
                        
                        // Wait a bit for graceful exit
                        if (!_helperProcess.WaitForExit(2000))
                        {
                            _helperProcess.Kill();
                            // Wait for the process to actually terminate after kill
                            _helperProcess.WaitForExit(3000);
                        }
                    }
                }
            }
            catch { }
        }
    }

    /// <inheritdoc />
    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        if (_helperProcess == null)
            throw new InvalidOperationException("Process is not started.");

        using var registration = cancellationToken.Register(() => Kill(force: true));

        try
        {
            await _helperProcess.WaitForExitAsync(cancellationToken);
        }
        catch (TaskCanceledException)
        {
            // Convert TaskCanceledException to OperationCanceledException for consistent API behavior
            throw new OperationCanceledException(cancellationToken);
        }
        
        return _helperProcess.ExitCode;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_isDisposed)
                return;

            Kill(force: true);
            
            try { _inputStream?.Dispose(); } catch { }
            try { _outputStream?.Dispose(); } catch { }
            try { _errorStream?.Dispose(); } catch { }
            try { _controlSocketStream?.Dispose(); } catch { }
            try { _helperProcess?.Dispose(); } catch { }

            // Clean up control socket file
            if (!string.IsNullOrEmpty(_controlSocketPath) && File.Exists(_controlSocketPath))
            {
                try { File.Delete(_controlSocketPath); } catch { }
            }

            _isDisposed = true;
        }
    }

    private async Task ConnectToControlSocketAsync(string path)
    {
        Socket? socket = null;
        Exception? lastFailure = null;
        try
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < TimeSpan.FromSeconds(5))
            {
                try
                {
                    socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(path)).ConfigureAwait(false);
                    break;
                }
                catch (Exception ex)
                {
                    lastFailure = ex;
                    try { socket?.Dispose(); } catch { }
                    socket = null;
                    await Task.Delay(100).ConfigureAwait(false);
                }
            }

            if (socket == null || !socket.Connected)
            {
                try { socket?.Dispose(); } catch { }
                SetError(
                    PtyErrorCode.ControlSocketUnavailable,
                    lastFailure?.Message ?? $"Timed out connecting to '{path}'.");
                return;
            }

            var stream = new NetworkStream(socket, ownsSocket: true);
            (int Columns, int Rows)? pending;
            lock (_stateLock)
            {
                if (_isDisposed)
                {
                    stream.Dispose();
                    return;
                }

                _controlSocketStream = stream;
                pending = _pendingResize;
                _pendingResize = null;
            }

            await SendResizeMessageAsync(_startupColumns, _startupRows, stream).ConfigureAwait(false);
            if (pending.HasValue)
            {
                await SendResizeMessageAsync(
                    pending.Value.Columns,
                    pending.Value.Rows,
                    stream).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            try { socket?.Dispose(); } catch { }
            SetError(PtyErrorCode.ControlSocketUnavailable, ex.Message);
        }
    }

    private async Task SendResizeMessageAsync(int cols, int rows, Stream? controlSocket = null)
    {
        controlSocket ??= _controlSocketStream;
        if (controlSocket == null)
            return;

        await _controlWriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var msg = $"{{\"type\":\"resize\",\"cols\":{cols},\"rows\":{rows}}}\n";
            var bytes = Encoding.UTF8.GetBytes(msg);
            await controlSocket.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            await controlSocket.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SetError(PtyErrorCode.ResizeFailed, ex.Message);
        }
        finally
        {
            _controlWriteLock.Release();
        }
    }

    private static bool IsInteractiveShell(string executable)
    {
        string name = Path.GetFileNameWithoutExtension(executable).ToLowerInvariant();
        return name is "sh" or "bash" or "zsh" or "fish" or "dash"
            or "ksh" or "csh" or "tcsh" or "pwsh" or "powershell";
    }
 
    private static List<string> ParseCommandLine(string command)
    {
        var arguments = new List<string>();
        var token = new StringBuilder();
        char quote = '\0';
        bool escaped = false;
        bool tokenStarted = false;

        foreach (char character in command)
        {
            if (escaped)
            {
                token.Append(character);
                escaped = false;
                tokenStarted = true;
                continue;
            }

            if (character == '\\' && quote != '\'')
            {
                escaped = true;
                tokenStarted = true;
                continue;
            }

            if (quote != '\0')
            {
                if (character == quote)
                    quote = '\0';
                else
                    token.Append(character);
                tokenStarted = true;
                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
                tokenStarted = true;
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (tokenStarted)
                {
                    arguments.Add(token.ToString());
                    token.Clear();
                    tokenStarted = false;
                }
                continue;
            }

            token.Append(character);
            tokenStarted = true;
        }

        if (escaped)
            token.Append('\\');
        if (quote != '\0')
            throw new FormatException("The configured shell command has an unterminated quote.");
        if (tokenStarted)
            arguments.Add(token.ToString());
        return arguments;
    }

    private static string CreateControlSocketPath()
    {
        const string fileNamePrefix = "dotty-control-";
        string fileName = $"{fileNamePrefix}{Guid.NewGuid():N}.sock";
        string path = Path.Combine(Path.GetTempPath(), fileName);
        if (path.Length < 90)
            return path;

        if (Directory.Exists("/tmp"))
        {
            path = Path.Combine("/tmp", fileName);
            if (path.Length < 90)
                return path;
        }

        throw new PtyException(
            PtyErrorCode.ControlSocketUnavailable,
            "The temporary directory path is too long for a Unix-domain socket.");
    }

    internal static string? FindHelperExecutableForCurrentProcess()
    {
        try
        {
            string baseDirectory = AppContext.BaseDirectory;
            string adjacent = Path.Combine(baseDirectory, "pty-helper");
            if (File.Exists(adjacent))
                return Path.GetFullPath(adjacent);

            var current = new DirectoryInfo(baseDirectory);
            for (int i = 0; i < 16 && current != null; i++)
            {
                string repoCandidate = Path.Combine(
                    current.FullName,
                    "src",
                    "Dotty.NativePty",
                    "bin",
                    "pty-helper");
                string projectCandidate = Path.Combine(
                    current.FullName,
                    "Dotty.NativePty",
                    "bin",
                    "pty-helper");

                if (File.Exists(repoCandidate))
                    return Path.GetFullPath(repoCandidate);
                if (File.Exists(projectCandidate))
                    return Path.GetFullPath(projectCandidate);

                current = current.Parent;
            }
        }
        catch
        {
        }

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (var directory in pathEnv.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string candidate = Path.Combine(directory, "pty-helper");
                    if (File.Exists(candidate))
                        return Path.GetFullPath(candidate);
                }
                catch
                {
                }
            }
        }

        return null;
    }

    internal static bool IsExecutable(string path)
    {
        try
        {
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
                return false;

            var mode = File.GetUnixFileMode(path);
            return mode.HasFlag(UnixFileMode.UserExecute)
                || mode.HasFlag(UnixFileMode.GroupExecute)
                || mode.HasFlag(UnixFileMode.OtherExecute);
        }
        catch
        {
            return false;
        }
    }
    private void SetError(PtyErrorCode code, string message)
    {
        LastErrorCode = code;
        LastError = $"{code}: {message}";
    }
}
