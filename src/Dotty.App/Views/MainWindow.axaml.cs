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
using System.Buffers;
using Dotty.Abstractions.Adapter;
using Dotty.Abstractions.Parser;
using Dotty.Terminal.Adapter;
using Dotty.Terminal.Parser;

namespace Dotty.App;

public partial class MainWindow : Window
{
    private struct PtyChunk
    {
        public byte[] Data;
        public int Length;
    }

    private readonly System.Threading.Channels.Channel<PtyChunk> _ptyDataQueue = System.Threading.Channels.Channel.CreateUnbounded<PtyChunk>();

    private void ProcessPtyQueueAsync(CancellationToken token)
    {
        Task.Run(async () =>
        {
            try
            {
                // Continuously wait for data from the background reader thread
                while (await _ptyDataQueue.Reader.WaitToReadAsync(token))
                {
                    int processed = 0;
                    while (_ptyDataQueue.Reader.TryRead(out var chunk))
                    {
                        try { _parser?.Feed(chunk.Data.AsSpan(0, chunk.Length)); } catch { }
                        ArrayPool<byte>.Shared.Return(chunk.Data);
                        processed++;
                        if (processed > 50)
                        {
                            _terminalAdapter?.RequestRenderExtern();
                            await Task.Yield();
                            processed = 0;
                        }
                    }
                    _terminalAdapter?.RequestRenderExtern();
                }
            }
            catch { }
        }, token);
    }

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
    private int _renderScheduled;
    private Size _lastTerminalSize = new Size(0, 0);

    public MainWindow()
    {
        InitializeComponent();

        Title = "Dotty";
        Background = new SolidColorBrush(Color.Parse(Services.Defaults.DefaultBackground));

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
            TerminalView.SizeChanged += TerminalViewOnSizeChanged;
            TerminalView.LayoutUpdated += TerminalViewOnLayoutUpdated;
            Dispatcher.UIThread.Post(() =>
            {
                try { _ = SendResizeMessageAsync(CalculateCols(), CalculateRows()); } catch { }
            }, DispatcherPriority.Render);

            string projectPath = FindPtyTestsProjectPath();
            string? helperExe = null;
            var candidate = Path.Combine(projectPath, "Dotty.NativePty", "bin", "pty-helper");
            if (File.Exists(candidate)) helperExe = Path.GetFullPath(candidate);

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

            // Use the default system shell inside the helper; do not inherit user environment shell.

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
            _terminalAdapter.RenderRequested += _ => ScheduleRender();

            _readCancellation = new CancellationTokenSource();
            StartBackgroundReaders(_readCancellation.Token);
            ProcessPtyQueueAsync(_readCancellation.Token);

            // Automatic test-script execution disabled: no environment-variable-driven actions.

            TerminalView.RawInput += (bytes) =>
            {
                if (bytes == null || _childInputStream == null) return;
                
                // Copy the bytes so we don't hold references to internal buffers
                var copy = new byte[bytes.Length];
                Array.Copy(bytes, copy, bytes.Length);

                Task.Run(async () =>
                {
                    try
                    {
                        // Some PTY streams block their writes entirely rather than 
                        // returning uncompleted Tasks when the kernel buffer gets full.
                        // Executing in a background Task ensures the UI thread never freezes.
                        await _childInputStream.WriteAsync(copy, 0, copy.Length).ConfigureAwait(false);
                        await _childInputStream.FlushAsync().ConfigureAwait(false);
                    }
                    catch { }
                });
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

    private void ScheduleRender()
    {
        if (Interlocked.Exchange(ref _renderScheduled, 1) == 1)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var tb = _terminalAdapter?.Buffer as TerminalBuffer;
                if (tb != null)
                {
                    TerminalView.SetBuffer(tb);
                }
            }
            catch
            {
            }
            finally
            {
                Interlocked.Exchange(ref _renderScheduled, 0);
            }
        }, DispatcherPriority.Render);
    }

    private async Task ReadChildOutputAsync(CancellationToken cancellationToken)
    {
        if (_childOutputStream == null) return;

        try
        {
            var reader = _childOutputStream;
            var readerId = Guid.NewGuid().ToString("N");
            byte[] buffer = new byte[131072];

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    int bytesRead = await reader.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    if (bytesRead > 0)
                    {
                        var copy = ArrayPool<byte>.Shared.Rent(bytesRead);
                        Array.Copy(buffer, 0, copy, 0, bytesRead);
                        var chunk = new PtyChunk { Data = copy, Length = bytesRead };
                        
                        _ptyDataQueue.Writer.TryWrite(chunk);
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
            byte[] buffer = new byte[131072];

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

    private static string UnescapeString(string s)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                i++;
                char n = s[i];
                if (n == 'n') sb.Append('\n');
                else if (n == 'r') sb.Append('\r');
                else if (n == 't') sb.Append('\t');
                else if (n == '\\') sb.Append('\\');
                else if (n == 'u' && i + 4 < s.Length)
                {
                    var hex = s.Substring(i + 1, 4);
                    if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v))
                    {
                        sb.Append(char.ConvertFromUtf32(v));
                        i += 4;
                    }
                    else sb.Append('u');
                }
                else sb.Append(n);
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private async Task RunTestScriptAsync(string path)
    {
        // Wait for the child input stream to be ready
        int waited = 0;
        while (_childInputStream == null && waited < 5000)
        {
            await Task.Delay(50);
            waited += 50;
        }
        if (_childInputStream == null) return;

        try
        {
            var lines = File.ReadAllLines(path);
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;

                if (line.StartsWith("sleep:", StringComparison.OrdinalIgnoreCase))
                {
                    var arg = line.Substring(6).Trim();
                    if (int.TryParse(arg, out var ms)) await Task.Delay(ms);
                    continue;
                }

                if (line.StartsWith("send:", StringComparison.OrdinalIgnoreCase))
                {
                    var payload = line.Substring(5);
                    var text = UnescapeString(payload);
                    var bytes = System.Text.Encoding.UTF8.GetBytes(text);
                    try
                    {
                        lock (_writeLock)
                        {
                            _childInputStream.Write(bytes, 0, bytes.Length);
                            _childInputStream.Flush();
                        }
                    }
                    catch { }
                    // give the child some time to process
                    await Task.Delay(200);
                    continue;
                }
            }
        }
        catch { }
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

    private void TerminalViewOnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0)
        {
            return;
        }

        try { _ = SendResizeMessageAsync(CalculateCols(), CalculateRows()); } catch { }
    }

    private void TerminalViewOnLayoutUpdated(object? sender, EventArgs e)
    {
        var bounds = TerminalView.Bounds;
        var size = new Size(bounds.Width, bounds.Height);
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        if (size.Equals(_lastTerminalSize))
        {
            return;
        }

        _lastTerminalSize = size;
        try { _ = SendResizeMessageAsync(CalculateCols(), CalculateRows()); } catch { }
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

        try { TerminalView.PropertyChanged -= TerminalViewOnPropertyChanged; } catch { }
        try { TerminalView.SizeChanged -= TerminalViewOnSizeChanged; } catch { }
        try { TerminalView.LayoutUpdated -= TerminalViewOnLayoutUpdated; } catch { }
        try { _controlSocketStream?.Dispose(); } catch { }
        _readCancellation?.Dispose();
    }
}