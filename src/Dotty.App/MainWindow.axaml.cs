using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Dotty.Core;
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
    private string? _shellIntegrationScriptPath;

    public MainWindow()
    {
        InitializeComponent();

        // Wire the new TerminalInput Submitted event
        InputControl.Submitted += InputControl_Submitted;

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
                OutputBox.Text = "Unix PTY only for now.\n";
                return;
            }

            OutputBox.Text = "";
            _shellIntegrationScriptPath = WriteShellIntegrationScript();

            // Spawn the helper subprocess which will itself spawn /bin/bash and inherit the redirected stdio.
            // Prefer launching the built DLL directly (dotnet <dll> --interactive) to avoid dotnet-run wrapper buffering.
            string projectPath = FindPtyTestsProjectPath();

            // Candidate built DLL path (development default)
            string dllPath = Path.Combine(projectPath, "bin", "Debug", "net9.0", "Dotty.Subprocess.dll");

            ProcessStartInfo psi;
            if (File.Exists(dllPath))
            {
                // Run the built DLL: dotnet <dll> --interactive
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
                // Fallback to dotnet run (slower, but works when no built DLL exists)
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

            psi.Environment["TERM_PROGRAM"] = "wezterm";
            psi.Environment["WEZTERM"] = "1";
            psi.Environment["COLORTERM"] = "wezterm";
            if (!string.IsNullOrEmpty(_shellIntegrationScriptPath))
            {
                psi.Environment["DOTTY_SHELL_INTEGRATION_SCRIPT"] = _shellIntegrationScriptPath;
            }
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
                OutputBox.Text = "Failed to start helper subprocess.\n";
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
            // Subscribe to render requests and update the existing OutputBox
            _terminalAdapter.RenderRequested += (display) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    OutputBox.Text = display;
                    OutputBox.CaretIndex = OutputBox.Text.Length;
                });
            };
            // Subscribe to prompt detection to update input placeholder
            _terminalAdapter.PromptDetected += (prompt) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    try { InputControl.Prompt = prompt; } catch { }
                });
            };
            _terminalAdapter.PromptSegmentsDetected += (segments) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    try { InputControl.SetPromptSegments(segments); } catch { }
                });
            };
            // Log raw OSC payloads for debugging
            _terminalAdapter.RawOscReceived += (payload) =>
            {
                try
                {
                    Console.WriteLine($"RAW_OSC: {payload}");
                }
                catch { }
            };
            // Log prompt segments for debugging
            _terminalAdapter.PromptSegmentsDetected += (segments) =>
            {
                try
                {
                    Console.WriteLine("PROMPT_SEGMENTS:");
                    foreach (var seg in segments)
                    {
                        Console.WriteLine($"  Seg: '{seg.Text}' fg:{seg.Foreground}");
                    }
                }
                catch { }
            };
            // No static prompt; shell prompt will appear in the terminal output

            

            // Start reading subprocess output on dedicated background threads
            _readCancellation = new CancellationTokenSource();
            StartBackgroundReaders(_readCancellation.Token);
            try { Console.Error.WriteLine("[ONOPENED] started background readers"); } catch { }

            // Hook raw text input events from input control and forward bytes to child stdin
            InputControl.RawTextInput += (s) =>
            {
                if (s == null || _childInputStream == null) return;
                try
                {
                    var bytes = Encoding.UTF8.GetBytes(s);
                    lock (_writeLock)
                    {
                        _childInputStream.Write(bytes, 0, bytes.Length);
                        _childInputStream.Flush();
                    }
                }
                catch { }
            };

            // Send initial window size once the control socket is connected (handled by ConnectToControlSocketAsync)

            InputControl.FocusInput();
            // Set initial working directory for prompt
            try { InputControl.WorkingDirectory = _currentWorkingDirectory; } catch { }
        }
        catch (Exception ex)
        {
            OutputBox.Text = $"Error: {ex.Message}\n";
        }
    }

    private async Task ReadChildOutputAsync(CancellationToken cancellationToken)
    {
        if (_childOutputStream == null) return;

        try
        {
            var reader = _childOutputStream;
            var readerId = Guid.NewGuid().ToString("N");
            try { Console.Error.WriteLine($"[READOUT_START] id={readerId} thread={System.Threading.Thread.CurrentThread.ManagedThreadId}"); } catch { }
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
                        try
                        {
                            var ioDbg = Environment.GetEnvironmentVariable("DOTTY_DEBUG_IO");
                            if (!string.IsNullOrEmpty(ioDbg) && ioDbg != "0")
                            {
                                int preview = Math.Min(bytesRead, 48);
                                var hb = new System.Text.StringBuilder(preview * 2);
                                for (int k = 0; k < preview; k++) hb.Append(buffer[k].ToString("x2"));
                                Console.Error.WriteLine($"[STDOUT_CHUNK] reader={readerId} len={bytesRead} preview={hb}");
                            }
                        }
                        catch { }

                        // Optional debugging: when DOTTY_DEBUG_RAW=1, show a short marker in the UI
                        try
                        {
                            var dbg = Environment.GetEnvironmentVariable("DOTTY_DEBUG_RAW");
                            if (!string.IsNullOrEmpty(dbg) && dbg != "0")
                            {
                                var snippet = System.Text.Encoding.UTF8.GetString(buffer, 0, Math.Min(bytesRead, 256));
                                Dispatcher.UIThread.Post(() =>
                                {
                                    try
                                    {
                                        OutputBox.Text += "[STDOUT_RAW] " + snippet + "\n";
                                    }
                                    catch { }
                                });
                            }
                        }
                        catch { }

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
                            try
                            {
                                var s = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                                // Log helper debug messages to the console for developers
                                if (!string.IsNullOrWhiteSpace(s))
                                    Console.Error.WriteLine(s.TrimEnd());
                            }
                            catch { }
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

    private string? WriteShellIntegrationScript()
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), $"dotty-shell-integration-{Guid.NewGuid():N}.sh");
            File.WriteAllText(path, BuildShellIntegrationScript());
            return path;
        }
        catch
        {
            return null;
        }
    }

    private void CleanupShellIntegrationScript()
    {
        if (_shellIntegrationScriptPath == null)
        {
            return;
        }

        try
        {
            if (File.Exists(_shellIntegrationScriptPath))
            {
                File.Delete(_shellIntegrationScriptPath);
            }
        }
        catch
        {
        }
        finally
        {
            _shellIntegrationScriptPath = null;
        }
    }

    private static string BuildShellIntegrationScript()
    {
        var esc = ((char)27).ToString();
        var bel = ((char)7).ToString();
        var sb = new StringBuilder();
        sb.AppendLine("#!/usr/bin/env sh");
        sb.AppendLine("dotty_emit_prompt_start() {");
        sb.AppendLine($"  printf '{esc}]133;A{bel}'");
        sb.AppendLine($"  printf '{esc}]133;P{bel}'");
        sb.AppendLine("}");
        sb.AppendLine("dotty_emit_prompt_end() {");
        sb.AppendLine($"  printf '{esc}]133;B{bel}'");
        sb.AppendLine("}");
        sb.AppendLine("dotty_emit_prompt_flush() {");
        sb.AppendLine($"  printf '{esc}]133;N{bel}'");
        sb.AppendLine("}");
    sb.AppendLine("dotty_emit_prompt_precmd() {");
    sb.AppendLine("  dotty_emit_prompt_flush");
    sb.AppendLine("  dotty_emit_prompt_start");
    sb.AppendLine("}");
    sb.AppendLine("dotty_wrap_prompt() {");
    sb.AppendLine("  if [ -n \"$DOTTY_PROMPT_WRAPPED\" ]; then");
    sb.AppendLine("    return");
    sb.AppendLine("  fi");
    sb.AppendLine("  DOTTY_PROMPT_WRAPPED=1");
    sb.AppendLine("  # For bash we will append our end marker to PROMPT_COMMAND to ensure it's");
    sb.AppendLine("  # executed after other PROMPT_COMMANDs (like starship_precmd) have run.");
    sb.AppendLine("  if [ -n \"$ZSH_VERSION\" ]; then");
    sb.AppendLine("    # For zsh we'll add two precmd hooks: one before shell precmds and one after.");
    sb.AppendLine("    # Prepend the start marker so it runs before user precmds; append the end marker to run after.");
    sb.AppendLine("    if [ -n \"$precmd_functions\" ]; then");
    sb.AppendLine("      precmd_functions=(dotty_emit_prompt_precmd ${precmd_functions[@]})");
    sb.AppendLine("    else");
    sb.AppendLine("      precmd_functions=(dotty_emit_prompt_precmd)");
    sb.AppendLine("    fi");
    sb.AppendLine("    # Wrap PROMPT so the end marker is printed after the prompt text");
    sb.AppendLine("    if [ -n \"$PROMPT\" ]; then");
    sb.AppendLine("      DOTTY_OLD_PROMPT=\"$PROMPT\"");
    sb.AppendLine("      PROMPT=\"$(dotty_emit_prompt_start)\"\"$DOTTY_OLD_PROMPT\"\"$(dotty_emit_prompt_end)\"");
    sb.AppendLine("    fi");
    sb.AppendLine("  else");
    sb.AppendLine("    # For bash we run start before existing PROMPT_COMMAND, and append end after.");
    sb.AppendLine("    # `PROMPT_COMMAND` is a single string; we prepend the start call and append the end call.");
    sb.AppendLine("    DOTTY_OLD_PROMPT_COMMAND=\"${PROMPT_COMMAND:-}\"");
    sb.AppendLine("    PROMPT_COMMAND=\"dotty_emit_prompt_flush; dotty_emit_prompt_start; ${DOTTY_OLD_PROMPT_COMMAND:+$DOTTY_OLD_PROMPT_COMMAND; }\"");
    sb.AppendLine("    # Wrap PS1 so the end marker is printed after PS1 is emitted (bash prints PS1 after PROMPT_COMMAND)");
    sb.AppendLine("    if [ -n \"$PS1\" ]; then");
    sb.AppendLine("      DOTTY_OLD_PS1=\"$PS1\"");
    sb.AppendLine("      PS1=\"$(dotty_emit_prompt_start)\"\"$DOTTY_OLD_PS1\"\"$(dotty_emit_prompt_end)\"");
    sb.AppendLine("    fi");
    sb.AppendLine("    export PROMPT_COMMAND");
    sb.AppendLine("  fi");
    sb.AppendLine("}");
        sb.AppendLine("dotty_setup_hooks() {");
        sb.AppendLine("  if [ -n \"$ZSH_VERSION\" ]; then");
        sb.AppendLine("    autoload -Uz add-zsh-hook >/dev/null 2>&1 || true");
        sb.AppendLine("    if command -v add-zsh-hook >/dev/null 2>&1; then");
        sb.AppendLine("      add-zsh-hook precmd dotty_emit_prompt_precmd >/dev/null 2>&1");
        sb.AppendLine("    else");
        sb.AppendLine("      precmd_functions+=(dotty_emit_prompt_precmd)");
        sb.AppendLine("    fi");
        sb.AppendLine("    typeset -fx dotty_emit_prompt_precmd dotty_emit_prompt_start dotty_emit_prompt_end dotty_emit_prompt_flush");
        sb.AppendLine("  else");
        sb.AppendLine("    DOTTY_PROMPT_COMMAND=\"dotty_emit_prompt_precmd\"");
        sb.AppendLine("    if [ -n \"$PROMPT_COMMAND\" ]; then");
        sb.AppendLine("      PROMPT_COMMAND=\"$DOTTY_PROMPT_COMMAND; $PROMPT_COMMAND\"");
        sb.AppendLine("    else");
        sb.AppendLine("      PROMPT_COMMAND=\"$DOTTY_PROMPT_COMMAND\"");
        sb.AppendLine("    fi");
        sb.AppendLine("    export -f dotty_emit_prompt_precmd dotty_emit_prompt_start dotty_emit_prompt_end dotty_emit_prompt_flush");
        sb.AppendLine("  fi");
        sb.AppendLine("}");
        sb.AppendLine("dotty_setup_hooks");
        sb.AppendLine("dotty_wrap_prompt");
        sb.AppendLine("dotty_emit_prompt_start");
        return sb.ToString();
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

        // Clear the input control immediately
        InputControl.Clear();

        if (string.IsNullOrEmpty(line))
            return;

        // Heuristic: if user ran a cd command, update our best-effort cwd for the inline prompt.
        try
        {
            var t = line.Trim();
            if (t == "cd")
            {
                _currentWorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                InputControl.WorkingDirectory = _currentWorkingDirectory;
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
                InputControl.WorkingDirectory = _currentWorkingDirectory;
            }
        }
        catch { /* ignore path parsing errors */ }

        try
        {
            // Send LF (newline) as the command terminator instead of CR. Using CR
            // has led to carriage-return characters being embedded in stdin and
            // producing tokens like $'\rpwd' in some shells. LF is the conventional
            // line terminator for Unix shells.
            var payload = line + "\n";
            var bytes = Encoding.UTF8.GetBytes(payload);
            lock (_writeLock)
            {
                _childInputStream.Write(bytes, 0, bytes.Length);
                _childInputStream.Flush();
            }

            // Request a render of the main display after input
            _terminalAdapter?.RequestRenderExtern();
        }
        catch
        {
            // Ignore write errors
        }
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
            var w = OutputBox.Bounds.Width;
            // Estimate character width from the configured font size. This is an approximation
            // but much better than a magic constant; it reduces mismatch between shell wrap
            // width and our rendered width which causes staircase artifacts.
            double fontSize = 13.0;
            try { fontSize = OutputBox.FontSize; } catch { }
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
            var h = OutputBox.Bounds.Height;
            double fontSize = 13.0;
            try { fontSize = OutputBox.FontSize; } catch { }
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

        CleanupShellIntegrationScript();
        try { _controlSocketStream?.Dispose(); } catch { }
        _readCancellation?.Dispose();
    }
}

