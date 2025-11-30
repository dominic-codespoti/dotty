using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Dotty.Abstractions.Adapter;
using Dotty.Abstractions.Parser;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;

namespace Dotty.App;

public partial class MainWindow : Window
{
    private Process? _childProcess;
    private Stream? _childOutputStream;
    private Stream? _childErrorStream;
    private Stream? _childInputStream;
    private CancellationTokenSource? _readCancellation;
    private string? _controlSocketPath;
    private Stream? _controlSocketStream;
    private readonly object _writeLock = new();
    private int _openedInitialized = 0;
    private string _currentWorkingDirectory = Environment.CurrentDirectory;
    private ITerminalParser? _parser;
    private ITerminalHandler? _terminalAdapter;

    public MainWindow()
    {
        InitializeComponent();

        Title = "Dotty";
        Opacity = Services.Defaults.DefaultWindowOpacity;
        Background = new SolidColorBrush(Color.Parse(Services.Defaults.DefaultBackgroundAlpha));

        TerminalView.Submitted += InputControl_Submitted;
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            if (Interlocked.Exchange(ref _openedInitialized, 1) != 0)
            {
                return;
            }

            TerminalView.SetPlainText("");
            TerminalView.PropertyChanged += TerminalViewOnPropertyChanged;
            Dispatcher.UIThread.Post(() =>
            {
                try { _ = SendResizeMessageAsync(CalculateCols(), CalculateRows()); } catch { }
            }, DispatcherPriority.Render);

            string projectPath = FindPtyTestsProjectPath();
            string? helperExe = Environment.GetEnvironmentVariable("DOTTY_PTY_HELPER");
            if (string.IsNullOrEmpty(helperExe))
            {
                var candidate = Path.Combine(projectPath, "Dotty.NativePty", "bin", "pty-helper");
                if (File.Exists(candidate)) helperExe = Path.GetFullPath(candidate);
            }

            ProcessStartInfo psi;
            if (!string.IsNullOrEmpty(helperExe) && File.Exists(helperExe))
            {
                var helperShell = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/sh";
                psi = new ProcessStartInfo
                {
                    FileName = helperExe,
                    Arguments = $"\"{helperShell}\"",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
            }
            else
            {
                TerminalView.SetPlainText("Failed to start helper subprocess.\n");
                return;
            }

            var controlPath = Path.Combine(Path.GetTempPath(), $"dotty-control-{Guid.NewGuid():N}.sock");
            psi.Environment["DOTTY_CONTROL_SOCKET"] = controlPath;
            _controlSocketPath = controlPath;

            var userShell = Environment.GetEnvironmentVariable("SHELL");
            if (!string.IsNullOrEmpty(userShell))
            {
                psi.Environment["DOTTY_SHELL"] = userShell;
                psi.Environment["SHELL"] = userShell;
            }

            _childProcess = Process.Start(psi);
            if (_childProcess == null)
            {
                TerminalView.SetPlainText("Failed to start helper subprocess.\n");
                return;
            }

            _childOutputStream = _childProcess.StandardOutput.BaseStream;
            _childErrorStream = _childProcess.StandardError.BaseStream;
            _childInputStream = _childProcess.StandardInput.BaseStream;

            if (!string.IsNullOrEmpty(_controlSocketPath))
            {
                _ = Task.Run(() => ConnectToControlSocketAsync(_controlSocketPath));
            }

            _parser = new BasicAnsiParser();
            _terminalAdapter = new TerminalAdapter(rows: 24, columns: 80);
            _parser.Handler = _terminalAdapter;
            _terminalAdapter.RenderRequested += (display) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    try {
                        var tb = _terminalAdapter.Buffer as TerminalBuffer;
                        if (tb != null) TerminalView.SetBuffer(tb);
                    } catch { }
                });
            };

            _readCancellation = new CancellationTokenSource();
            StartBackgroundReaders(_readCancellation.Token);

            TerminalView.RawInput += (bytes) =>
            {
                if (bytes == null || _childInputStream == null) return;
                try
                {
                    lock (_writeLock)
                    {
                        _childInputStream.Write(bytes, 0, bytes.Length);
                        _childInputStream.Flush();
                    }
                }
                catch { }
            };

            try { this.Activate(); } catch { }
            Dispatcher.UIThread.Post(() =>
            {
                try { TerminalView.FocusInput(); } catch { }
            }, DispatcherPriority.Render);

            try { TerminalView.WorkingDirectory = _currentWorkingDirectory; } catch { }
        }
        catch (Exception ex)
        {
            try { TerminalView.SetPlainText($"Error: {ex.Message}\n"); } catch { }
        }
    }

    private async Task ReadChildOutputAsync(CancellationToken cancellationToken)
    {
        if (_childOutputStream == null) return;

        try
        {
            var reader = _childOutputStream;
            var readerId = Guid.NewGuid().ToString("N");
            byte[] buffer = new byte[4096];

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    int bytesRead = await reader.ReadAsync(buffer, 0, buffer.Length, cancellationToken);

                    if (bytesRead > 0)
                    {
                        _parser?.Feed(buffer.AsSpan(0, bytesRead));
                    }
                    else
                    {
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    break;
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    private async Task ReadChildErrorAsync(CancellationToken cancellationToken)
    {
        if (_childErrorStream == null) return;

        try
        {
            var reader = _childErrorStream;
            byte[] buffer = new byte[4096];

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    int bytesRead = await reader.ReadAsync(buffer, 0, buffer.Length, cancellationToken);

                    if (bytesRead > 0)
                    {
                        // Stderr retained for diagnostics only; not forwarded to the main parser.
                    }
                    else
                    {
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    break;
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    private void StartBackgroundReaders(CancellationToken cancellationToken)
    {
        Task.Factory.StartNew(() => ReadChildOutputAsync(cancellationToken), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        Task.Factory.StartNew(() => ReadChildErrorAsync(cancellationToken), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    private string FindPtyTestsProjectPath()
    {
        try
        {
            var cur = new DirectoryInfo(AppContext.BaseDirectory ?? ".");
            for (int i = 0; i < 8 && cur != null; i++)
            {
                string candidate1 = Path.Combine(cur.FullName, "src", "Dotty.NativePty");
                string candidate2 = Path.Combine(cur.FullName, "Dotty.NativePty");

                if (Directory.Exists(candidate1)) return Path.GetFullPath(Path.Combine(cur.FullName, "src"));
                if (Directory.Exists(candidate2)) return Path.GetFullPath(cur.FullName);

                cur = cur.Parent;
            }
        }
        catch { }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory ?? ".", "..", "..", "..", "..", "src"));
    }

    private void InputControl_Submitted(object? sender, string? text)
    {
        if (_childInputStream == null) return;

        var line = text ?? string.Empty;

        // Clear only the input buffer (don't clear the displayed terminal)
        TerminalView.ClearInput();

        if (string.IsNullOrEmpty(line))
            return;

        // Heuristic: if user ran a cd command, update our best-effort cwd for the inline prompt.
        try
        {
            var t = line.Trim();
            if (t == "cd")
            {
                _currentWorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                TerminalView.WorkingDirectory = _currentWorkingDirectory;
            }
            else if (t.StartsWith("cd "))
            {
                var arg = t.Substring(3).Trim();
                string newPath;
                if (arg == "~")
                    newPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                else if (Path.IsPathRooted(arg))
                    newPath = arg;
                else
                    newPath = Path.GetFullPath(Path.Combine(_currentWorkingDirectory, arg));

                // update (best-effort) and show in prompt
                _currentWorkingDirectory = newPath;
                TerminalView.WorkingDirectory = _currentWorkingDirectory;
            }
        }
        catch { /* ignore path parsing errors */ }

        try
        {
            _terminalAdapter?.RequestRenderExtern();
        }
        catch { }
    }

    private async Task ConnectToControlSocketAsync(string path)
    {
        try
        {
            // Try to connect for a short duration while helper starts and binds the socket
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

            // Send an initial size message
            await SendResizeMessageAsync(CalculateCols(), CalculateRows());

            // Subscribe to window bounds changes and send resize messages
            this.PropertyChanged += async (s, e) =>
            {
                if (e.Property == BoundsProperty)
                {
                    try { await SendResizeMessageAsync(CalculateCols(), CalculateRows()); } catch { }
                }
            };
        }
        catch { }
    }

    private void TerminalViewOnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.BoundsProperty)
        {
            try { _ = SendResizeMessageAsync(CalculateCols(), CalculateRows()); } catch { }
        }
    }

    private int CalculateCols()
    {
        try
        {
            var width = TerminalView.Bounds.Width;
            if (double.IsNaN(width) || width <= 0)
            {
                var tb = _terminalAdapter?.Buffer as TerminalBuffer;
                return tb?.Columns ?? 80;
            }

            TerminalView.TryGetTerminalMetrics(out var cellWidth, out _, out var padding);
            cellWidth = Math.Max(1.0, cellWidth);
            width -= padding.Left + padding.Right;
            if (width <= 0)
            {
                width = cellWidth;
            }

            int cols = (int)Math.Floor(width / cellWidth);
            cols = Math.Clamp(cols, 2, 400);
            return cols;
        }
        catch { var tb = _terminalAdapter?.Buffer as TerminalBuffer; return tb?.Columns ?? 80; }
    }

    private int CalculateRows()
    {
        try
        {
            var height = TerminalView.Bounds.Height;
            if (double.IsNaN(height) || height <= 0)
            {
                var tb = _terminalAdapter?.Buffer as TerminalBuffer;
                return tb?.Rows ?? 24;
            }

            TerminalView.TryGetTerminalMetrics(out _, out var cellHeight, out var padding);
            cellHeight = Math.Max(1.0, cellHeight);
            height -= padding.Top + padding.Bottom;
            if (height <= 0)
            {
                height = cellHeight;
            }

            int rows = (int)Math.Floor(height / cellHeight);
            rows = Math.Clamp(rows, 2, 400);
            return rows;
        }
        catch { var tb = _terminalAdapter?.Buffer as TerminalBuffer; return tb?.Rows ?? 24; }
    }

    private async Task SendResizeMessageAsync(int cols, int rows)
    {
        if (_controlSocketStream == null) return;
        try { _terminalAdapter?.ResizeBuffer(rows, cols); } catch { }
        try
        {
            var msg = $"{{\"type\":\"resize\",\"cols\":{cols},\"rows\":{rows}}}\n";
            var bytes = Encoding.UTF8.GetBytes(msg);
            await _controlSocketStream.WriteAsync(bytes, 0, bytes.Length);
            await _controlSocketStream.FlushAsync();
        }
        catch { }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // Allow re-opening initialization if window is re-created
        try { System.Threading.Interlocked.Exchange(ref _openedInitialized, 0); } catch { }
        _readCancellation?.Cancel();

        try
        {
            if (_childInputStream != null)
            {
                try { _childInputStream.Close(); } catch { }
            }

            if (_childProcess != null)
            {
                if (!_childProcess.HasExited)
                {
                    try { _childProcess.Kill(); } catch { }
                }

                try { _childProcess.Dispose(); } catch { }
            }
        }
        catch { }

        try { _controlSocketStream?.Dispose(); } catch { }
        _readCancellation?.Dispose();
    }
}
