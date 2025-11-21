using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using Avalonia.Controls;
using Avalonia.Threading;
using Dotty.Terminal;

namespace Dotty.App;

public partial class MainWindow : Window
{
    // We no longer call UnixPty.Start() from the GUI thread (POSIX: fork() unsafe in multithreaded process)
    // Instead we spawn the `Dotty.Subprocess` helper in `--interactive` mode and proxy its stdio.
    private Process? _childProcess;
    private Stream? _childOutputStream;
    private Stream? _childErrorStream;
    private Stream? _childInputStream;
    private CancellationTokenSource? _readCancellation;
    private string? _controlSocketPath;
    private Stream? _controlSocketStream;
    private readonly object _writeLock = new();
    // Guard to ensure OnOpened initialization is executed only once even if the Opened event fires
    // multiple times (race conditions in the UI event loop can cause re-entry).
    private int _openedInitialized = 0;
    // Track a best-effort current working directory for the inline prompt.
    private string _currentWorkingDirectory = Environment.CurrentDirectory;
    // Terminal integration
    private BasicAnsiParser? _parser;
    private TerminalAdapter? _terminalAdapter;

    public MainWindow()
    {
        InitializeComponent();

    // Wire the TerminalView Submitted event
    TerminalView.Submitted += InputControl_Submitted;

        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            // Prevent double-initialization if OnOpened fires more than once (use atomic check)
            if (System.Threading.Interlocked.Exchange(ref _openedInitialized, 1) != 0)
            {
                return;
            }
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            {
                TerminalView.SetPlainText("Unix PTY only for now.\n");
                return;
            }

            TerminalView.SetPlainText("");

            // Spawn the helper subprocess which will itself spawn the user's shell and inherit the redirected stdio.
            // Prefer a small native helper binary (DOTTY_PTY_HELPER or repo-built) to perform PTY allocation safely.
            string projectPath = FindPtyTestsProjectPath();

            // Look for an explicit native helper override, then a repo-built helper.
            string? helperExe = Environment.GetEnvironmentVariable("DOTTY_PTY_HELPER");
            if (string.IsNullOrEmpty(helperExe))
            {
                var candidate = Path.Combine(projectPath, "..", "Dotty.NativePty", "bin", "pty-helper");
                if (File.Exists(candidate)) helperExe = Path.GetFullPath(candidate);
            }

            ProcessStartInfo psi;
            if (!string.IsNullOrEmpty(helperExe) && File.Exists(helperExe))
            {
                // Launch the native helper and tell it which shell to exec.
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
                // Fallback to the managed helper (Dotty.Subprocess) using dotnet
                string dllPath = Path.Combine(projectPath, "bin", "Debug", "net9.0", "Dotty.Subprocess.dll");
                if (File.Exists(dllPath))
                {
                    psi = new ProcessStartInfo
                    {
                        FileName = "dotnet",
                        Arguments = $"\"{dllPath}\" --interactive",
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    };
                }
                else
                {
                    psi = new ProcessStartInfo
                    {
                        FileName = "dotnet",
                        Arguments = $"run --project \"{projectPath}\" -- --interactive",
                        UseShellExecute = false,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    };
                }
            }

            psi.Environment["TERM_PROGRAM"] = "wezterm";
            psi.Environment["WEZTERM"] = "1";
            psi.Environment["COLORTERM"] = "wezterm";
            // No shell integration script: let the shell draw its own prompt natively.
            // Create a temporary unix-domain socket path for resize/control messages and pass to helper
            try
            {
                var controlPath = Path.Combine(Path.GetTempPath(), $"dotty-control-{Guid.NewGuid():N}.sock");
                psi.Environment["DOTTY_CONTROL_SOCKET"] = controlPath;
                _controlSocketPath = controlPath;
            }
            catch { }
            // Ensure the subprocess uses the same shell as the user
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

            // Attempt to connect to helper control socket (helper will bind and listen). Retry for a short period.
            if (!string.IsNullOrEmpty(_controlSocketPath))
            {
                _ = Task.Run(() => ConnectToControlSocketAsync(_controlSocketPath));
            }

            // Create parser and adapter for terminal-style rendering
            _parser = new BasicAnsiParser();
            _terminalAdapter = new TerminalAdapter(rows: 24, columns: 80);
            _parser.Handler = _terminalAdapter;
            // Subscribe to render requests and update the TerminalView
            _terminalAdapter.RenderRequested += (display) =>
            {
                // Render from the adapter's buffer snapshot
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        TerminalView.SetBuffer(_terminalAdapter.Buffer);
                    }
                    catch
                    {
                    }
                });
            };
            // NOTE: We previously used prompt-detection to extract and render
            // the shell prompt semantically (TerminalView.Prompt / SetPromptSegments).
            // To let the shell draw the prompt natively (match typical terminal behavior)
            // we no longer apply prompt segments to the UI here. The adapter will
            // still emit OSC markers (for diagnostics) but we don't strip or
            // re-render prompt bytes from the main output stream.
            // OSC/shell-integration disabled: no raw OSC subscription.
            // If you want to keep semantic prompt detection for other features
            // (e.g., inline path display or diagnostics) you can subscribe here
            // and use the data non-destructively. For now we leave it unused so
            // the shell's prompt appears exactly as the shell emits it.
            // No static prompt; shell prompt will appear in the terminal output

            

            // Start reading subprocess output on dedicated background threads
            _readCancellation = new CancellationTokenSource();
            StartBackgroundReaders(_readCancellation.Token);

            // Hook raw input events from the TerminalView and forward bytes to child stdin
            TerminalView.RawInput += (bytes) =>
            {
                if (bytes == null || _childInputStream == null) return;
                try
                {
                    try { /* suppressed optional GUI stdin hex debug */ } catch { }

                    lock (_writeLock)
                    {
                        _childInputStream.Write(bytes, 0, bytes.Length);
                        _childInputStream.Flush();
                    }
                }
                catch { }
            };

            // Send initial window size once the control socket is connected (handled by ConnectToControlSocketAsync)

            TerminalView.FocusInput();
            // Set initial working directory for prompt
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
                        // Optional raw I/O diagnostics: when DOTTY_DEBUG_IO=1, write a compact
                        // hex preview of each chunk to stderr so we can tell whether identical
                        // byte chunks are being observed more than once.
                        try { /* suppressed optional stdout chunk debug */ } catch { }

                        // Optional debugging: when DOTTY_DEBUG_RAW=1, show a short marker in the UI
                        try { /* suppressed optional raw output UI dump */ } catch { }

                        // Feed raw bytes into the parser; the adapter will request renders on the UI thread
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
                            // Do not feed stderr into the main parser by default — in some
                            // environments the same terminal output can appear on both stdout
                            // and stderr which causes duplicate/doubled rendering. Keep stderr
                            // for diagnostics only.
                            try { /* suppressed stderr diagnostics */ } catch { }
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
        // Run stdout reader on a dedicated long-running background thread
        Task.Factory.StartNew(() => ReadChildOutputAsync(cancellationToken), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

        // Run stderr reader on a dedicated long-running background thread
        Task.Factory.StartNew(() => ReadChildErrorAsync(cancellationToken), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }



    

    private string FindPtyTestsProjectPath()
    {
    // Try walking up from the AppContext.BaseDirectory to locate either "src/Dotty.Subprocess" or "Dotty.Subprocess"
        try
        {
            var cur = new DirectoryInfo(AppContext.BaseDirectory ?? ".");
            for (int i = 0; i < 8 && cur != null; i++)
            {
        string candidate1 = Path.Combine(cur.FullName, "src", "Dotty.Subprocess");
        string candidate2 = Path.Combine(cur.FullName, "Dotty.Subprocess");

                if (Directory.Exists(candidate1)) return Path.GetFullPath(candidate1);
                if (Directory.Exists(candidate2)) return Path.GetFullPath(candidate2);

                cur = cur.Parent;
            }
        }
        catch { }

    // Fallback to previous relative calculation (may be wrong in some environments)
    return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory ?? ".", "..", "..", "..", "..", "src", "Dotty.Subprocess"));
    }

    

    private string ProcessAnsiCodes(string text)
    {
        var result = new StringBuilder();
        int i = 0;

        while (i < text.Length)
        {
            // Check for ANSI escape sequence: ESC[...
            if (i < text.Length - 1 && text[i] == '\u001b' && text[i + 1] == '[')
            {
                // Find the end of the escape sequence
                int endIndex = i + 2;
                while (endIndex < text.Length && !char.IsLetter(text[endIndex]))
                {
                    endIndex++;
                }

                if (endIndex < text.Length)
                {
                    char command = text[endIndex];
                    string sequence = text.Substring(i, endIndex - i + 1);

                    // Handle specific ANSI codes
                    if (command == 'J') // Clear display
                    {
                        // ESC[2J = clear entire screen
                        if (sequence.Contains("2J"))
                        {
                            result.Clear();
                        }
                        // ESC[0J = clear from cursor to end of screen
                        // ESC[1J = clear from cursor to start of screen
                    }
                    else if (command == 'K') // Clear line
                    {
                        // ESC[K or ESC[0K = clear from cursor to end of line
                        // ESC[1K = clear from cursor to start of line
                        // ESC[2K = clear entire line
                        // For simplicity, we'll just ignore these as they're typically used
                        // for inline terminal formatting that doesn't apply to TextBox
                    }
                    else if (command == 'H' || command == 'f') // Cursor position
                    {
                        // ESC[H or ESC[nH = move cursor to home or specific position
                        // We can't reposition in a simple TextBox, so we'll clear if it's a home command
                        if (sequence == "\u001b[H")
                        {
                            result.Clear();
                        }
                    }
                    // Other ANSI codes (colors, formatting) are ignored for now
                    // They would require a more sophisticated text rendering system

                    i = endIndex + 1;
                    continue;
                }
            }

            // Regular character
            result.Append(text[i]);
            i++;
        }

        return result.ToString();
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
            // NOTE: TerminalView already sends per-key bytes to the PTY (RawInput),
            // so do NOT resend the whole line here — that caused duplicated input
            // (each keystroke was sent, then Submit re-sent the full line).
            // We keep this hook to update our cwd heuristic and request a render.
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

    private int CalculateCols()
    {
        try
        {
            var w = TerminalView.Bounds.Width;
            // Estimate character width from the configured font size. This is an approximation
            // but much better than a magic constant; it reduces mismatch between shell wrap
            // width and our rendered width which causes staircase artifacts.
            double fontSize = 13.0;
            try { fontSize = TerminalView.FontSize; } catch { }
            // Average character width roughly ~0.55-0.65 * fontSize depending on font.
            double avgCharWidth = Math.Max(4.0, fontSize * 0.6);
            int cols = Math.Max(20, (int)Math.Floor(w / avgCharWidth));
            return cols;
        }
        catch { return 80; }
    }

    private int CalculateRows()
    {
        try
        {
            var h = TerminalView.Bounds.Height;
            double fontSize = 13.0;
            try { fontSize = TerminalView.FontSize; } catch { }
            // Line height approximated as 1.2 * fontSize
            double lineHeight = Math.Max(8.0, fontSize * 1.2);
            int rows = Math.Max(5, (int)Math.Floor(h / lineHeight));
            return rows;
        }
        catch { return 24; }
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

