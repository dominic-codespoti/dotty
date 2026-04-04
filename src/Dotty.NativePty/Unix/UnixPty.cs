using System;
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
    private bool _isDisposed;
    private bool _isStarted;
    private readonly object _stateLock = new();

    /// <inheritdoc />
    public bool IsRunning => _helperProcess?.HasExited == false;

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

            string? helperExe = FindHelperExecutable();
            if (string.IsNullOrEmpty(helperExe) || !File.Exists(helperExe))
            {
                throw new PtyException("Failed to find pty-helper executable. Please build the native helper: cd src/Dotty.NativePty && make");
            }

            shell ??= PtyPlatform.GetDefaultShell();
            
            var psi = new ProcessStartInfo
            {
                FileName = helperExe,
                Arguments = $"\"{shell}\"",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory ?? Environment.GetEnvironmentVariable("HOME") ?? "/"
            };

            // Add environment variables
            if (environmentVariables != null)
            {
                foreach (var kvp in environmentVariables)
                {
                    psi.EnvironmentVariables[kvp.Key] = kvp.Value;
                }
            }

            // Create a unique control socket path for resize messages
            var controlPath = Path.Combine(Path.GetTempPath(), $"dotty-control-{Guid.NewGuid():N}.sock");
            psi.EnvironmentVariables["DOTTY_CONTROL_SOCKET"] = controlPath;
            _controlSocketPath = controlPath;

            _helperProcess = Process.Start(psi);
            if (_helperProcess == null)
            {
                throw new PtyException("Failed to start pty-helper process.");
            }

            _inputStream = _helperProcess.StandardInput.BaseStream;
            _outputStream = _helperProcess.StandardOutput.BaseStream;
            _errorStream = _helperProcess.StandardError.BaseStream;

            _isStarted = true;

            // Connect to control socket in background
            if (!string.IsNullOrEmpty(_controlSocketPath))
            {
                _ = Task.Run(() => ConnectToControlSocketAsync(_controlSocketPath));
            }

            // Monitor process exit
            _helperProcess.Exited += (sender, e) =>
            {
                try
                {
                    var exitCode = _helperProcess?.ExitCode ?? -1;
                    ProcessExited?.Invoke(this, exitCode);
                }
                catch { }
            };
            _helperProcess.EnableRaisingEvents = true;
        }
    }

    /// <inheritdoc />
    public void Resize(int columns, int rows)
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(UnixPty));
        
        if (_controlSocketStream == null)
            return; // Silently ignore if control socket not connected yet

        _ = SendResizeMessageAsync(columns, rows);
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
                    }
                    else
                    {
                        // Try graceful termination by closing input
                        _inputStream?.Close();
                        
                        // Wait a bit for graceful exit
                        if (!_helperProcess.WaitForExit(2000))
                        {
                            _helperProcess.Kill();
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

        await _helperProcess.WaitForExitAsync(cancellationToken);
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
        try
        {
            var sw = Stopwatch.StartNew();
            Socket? sock = null;
            
            while (sw.Elapsed < TimeSpan.FromSeconds(5))
            {
                try
                {
                    sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    var end = new UnixDomainSocketEndPoint(path);
                    await Task.Run(() => sock.Connect(end));
                    break;
                }
                catch
                {
                    try { sock?.Dispose(); } catch { }
                    await Task.Delay(100);
                }
            }

            if (sock == null || !sock.Connected)
            {
                try { sock?.Dispose(); } catch { }
                return;
            }

            _controlSocketStream = new NetworkStream(sock, ownsSocket: true);
            
            // Send initial resize to set the size
            await SendResizeMessageAsync(80, 24);
        }
        catch { }
    }

    private async Task SendResizeMessageAsync(int cols, int rows)
    {
        if (_controlSocketStream == null) return;
        
        try
        {
            var msg = $"{{\"type\":\"resize\",\"cols\":{cols},\"rows\":{rows}}}\n";
            var bytes = Encoding.UTF8.GetBytes(msg);
            await _controlSocketStream.WriteAsync(bytes, 0, bytes.Length);
            await _controlSocketStream.FlushAsync();
        }
        catch { }
    }

    private string? FindHelperExecutable()
    {
        try
        {
            var cur = new DirectoryInfo(AppContext.BaseDirectory ?? ".");
            for (int i = 0; i < 8 && cur != null; i++)
            {
                string candidate1 = Path.Combine(cur.FullName, "src", "Dotty.NativePty", "bin", "pty-helper");
                string candidate2 = Path.Combine(cur.FullName, "Dotty.NativePty", "bin", "pty-helper");

                if (File.Exists(candidate1)) return Path.GetFullPath(candidate1);
                if (File.Exists(candidate2)) return Path.GetFullPath(candidate2);

                cur = cur.Parent;
            }
        }
        catch { }

        // Check in PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (var dir in pathEnv.Split(':'))
            {
                var fullPath = Path.Combine(dir, "pty-helper");
                if (File.Exists(fullPath))
                    return fullPath;
            }
        }

        return null;
    }
}
