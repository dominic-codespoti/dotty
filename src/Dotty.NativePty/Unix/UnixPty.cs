using System;
using System.Buffers.Text;
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
    private (int Columns, int Rows, int PixelWidth, int PixelHeight)? _pendingResize;
    private readonly object _controlWriteLock = new();
    private int _startupColumns = 80;
    private int _startupRows = 24;
    private int _startupPixelWidth;
    private int _startupPixelHeight;
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
        System.Collections.Generic.IDictionary<string, string>? environmentVariables = null,
        System.Collections.Generic.IReadOnlyList<string>? command = null,
        bool shellIsExecutable = false,
        int initialPixelWidth = 0,
        int initialPixelHeight = 0)
    {
        lock (_stateLock)
        {
            if (_isStarted)
                throw new InvalidOperationException("PTY session is already started.");

            PtyHelperResolution helper = PtyHelperLocator.ResolveForCurrentProcess();
            string? helperExe = helper.Path;
            if (helperExe is null)
            {
                throw new PtyException(
                    PtyErrorCode.NativeHelperMissing,
                    $"The pty-helper executable was not found. Place it beside the application or set {PtyHelperLocator.OverrideVariable}. {helper.Describe()}");
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
            if (command is not null)
            {
                if (command.Count == 0)
                    throw new PtyException(PtyErrorCode.InvalidShell, "The command argv must contain an executable.");
                shellArguments = new List<string>(command);
            }
            else if (shellIsExecutable)
            {
                shellArguments = IsInteractiveShell(resolvedShell)
                    ? new List<string> { resolvedShell, "-i" }
                    : new List<string> { resolvedShell };
            }
            else
            {
                try
                {
                    shellArguments = ParseCommandLine(resolvedShell);
                }
                catch (FormatException ex)
                {
                    throw new PtyException(PtyErrorCode.InvalidShell, ex.Message, ex);
                }
                if (shellArguments.Count == 0)
                    throw new PtyException(PtyErrorCode.InvalidShell, "The configured shell command is empty.");
            }

            string executable = shellArguments[0];
            if (Path.IsPathFullyQualified(executable) && !File.Exists(executable))
                throw new PtyException(PtyErrorCode.InvalidShell, $"The configured executable '{executable}' does not exist.");

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
            if (command is null && !shellIsExecutable && shellArguments.Count == 1 && IsInteractiveShell(executable))
                psi.ArgumentList.Add("-i");
            // Unix-domain socket path limits vary; stay below the portable
            // sockaddr_un sun_path limit.
            string controlPath = CreateControlSocketPath();
            psi.EnvironmentVariables["DOTTY_CONTROL_SOCKET"] = controlPath;
            _controlSocketPath = controlPath;
            _startupColumns = Math.Max(1, columns);
            _startupRows = Math.Max(1, rows);
            _startupPixelWidth = ClampPixels(initialPixelWidth);
            _startupPixelHeight = ClampPixels(initialPixelHeight);
            _pendingResize = null;
            psi.EnvironmentVariables["DOTTY_INITIAL_COLS"] = _startupColumns.ToString();
            psi.EnvironmentVariables["DOTTY_INITIAL_ROWS"] = _startupRows.ToString();
            psi.EnvironmentVariables["DOTTY_INITIAL_XPIXEL"] = _startupPixelWidth.ToString();
            psi.EnvironmentVariables["DOTTY_INITIAL_YPIXEL"] = _startupPixelHeight.ToString();

            // Add environment variables before setting terminal identity.
            if (environmentVariables != null)
            {
                foreach (var kvp in environmentVariables)
                    psi.EnvironmentVariables[kvp.Key] = kvp.Value;
            }

            psi.EnvironmentVariables["TERM"] = TerminalEnvironment.Term(environmentVariables);
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
    public void Resize(int columns, int rows, int pixelWidth, int pixelHeight)
    {
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);
        pixelWidth = ClampPixels(pixelWidth);
        pixelHeight = ClampPixels(pixelHeight);

        Stream? controlSocket;
        lock (_stateLock)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(UnixPty));

            controlSocket = _controlSocketStream;
            if (controlSocket == null)
            {
                _pendingResize = (columns, rows, pixelWidth, pixelHeight);
                return;
            }
        }

        SendResizeMessage(columns, rows, pixelWidth, pixelHeight, controlSocket);
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
            (int Columns, int Rows, int PixelWidth, int PixelHeight)? pending;

            // Hold the write lock from publishing the stream until the replay is sent: a Resize that sees the
            // published stream must not reach the helper before the startup/pending sizes, or they would overwrite it.
            lock (_controlWriteLock)
            {
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

                SendResizeMessage(_startupColumns, _startupRows, _startupPixelWidth, _startupPixelHeight, stream);
                if (pending.HasValue)
                    SendResizeMessage(pending.Value.Columns, pending.Value.Rows, pending.Value.PixelWidth, pending.Value.PixelHeight, stream);
            }
        }
        catch (Exception ex)
        {
            try { socket?.Dispose(); } catch { }
            SetError(PtyErrorCode.ControlSocketUnavailable, ex.Message);
        }
    }

    private void SendResizeMessage(int cols, int rows, int pixelWidth, int pixelHeight, Stream? controlSocket = null)
    {
        controlSocket ??= _controlSocketStream;
        if (controlSocket == null)
            return;

        lock (_controlWriteLock)
        {
            try
            {
                Span<byte> message = stackalloc byte[ResizeMessageMaxBytes];
                int length = FormatResizeMessage(message, cols, rows, pixelWidth, pixelHeight);
                controlSocket.Write(message[..length]);
                controlSocket.Flush();
            }
            catch (Exception ex)
            {
                SetError(PtyErrorCode.ResizeFailed, ex.Message);
            }
        }
    }

    /// <summary>Upper bound of <see cref="FormatResizeMessage"/> output: fixed text plus four 10-digit ints.</summary>
    internal const int ResizeMessageMaxBytes = 128;

    /// <summary>Clamps a pixel dimension to the <c>unsigned short</c> range of <c>struct winsize</c>; negatives become 0 (unknown).</summary>
    internal static int ClampPixels(int pixels) => Math.Clamp(pixels, 0, ushort.MaxValue);

    /// <summary>
    /// Writes the newline-terminated resize control line understood by pty-helper:
    /// <c>{"type":"resize","cols":N,"rows":M,"xpixel":W,"ypixel":H}</c>.
    /// </summary>
    /// <returns>Number of bytes written.</returns>
    internal static int FormatResizeMessage(Span<byte> message, int cols, int rows, int pixelWidth, int pixelHeight)
    {
        int offset = 0;
        AppendLiteral(message, ref offset, "{\"type\":\"resize\",\"cols\":"u8);
        AppendInt(message, ref offset, cols);
        AppendLiteral(message, ref offset, ",\"rows\":"u8);
        AppendInt(message, ref offset, rows);
        AppendLiteral(message, ref offset, ",\"xpixel\":"u8);
        AppendInt(message, ref offset, ClampPixels(pixelWidth));
        AppendLiteral(message, ref offset, ",\"ypixel\":"u8);
        AppendInt(message, ref offset, ClampPixels(pixelHeight));
        AppendLiteral(message, ref offset, "}\n"u8);
        return offset;
    }

    private static void AppendLiteral(Span<byte> message, ref int offset, ReadOnlySpan<byte> literal)
    {
        literal.CopyTo(message[offset..]);
        offset += literal.Length;
    }

    private static void AppendInt(Span<byte> message, ref int offset, int value)
    {
        Utf8Formatter.TryFormat(value, message[offset..], out int written);
        offset += written;
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
