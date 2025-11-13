using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Dotty.Core;

namespace Dotty.App;

public partial class MainWindow : Window
{
    // We no longer call UnixPty.Start() from the GUI thread (POSIX: fork() unsafe in multithreaded process)
    // Instead we spawn the `Dotty.Subprocess` helper in `--interactive` mode and proxy its stdio.
    private Process? _childProcess;
    private Stream? _childOutputStream;
    private Stream? _childErrorStream;
    private StreamWriter? _childInputWriter;
    private CancellationTokenSource? _readCancellation;
    private readonly object _writeLock = new();
    

    public MainWindow()
    {
        InitializeComponent();

        InputBox.AddHandler(
            KeyDownEvent,
            InputBox_OnKeyDown,
            RoutingStrategies.Bubble,
            handledEventsToo: true);

        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            {
                OutputBox.Text = "Unix PTY only for now.\n";
                return;
            }

            OutputBox.Text = "";

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

            _childProcess = Process.Start(psi);

            if (_childProcess == null)
            {
                OutputBox.Text = "Failed to start helper subprocess.\n";
                return;
            }


            _childOutputStream = _childProcess.StandardOutput.BaseStream;
            _childErrorStream = _childProcess.StandardError.BaseStream;
            _childInputWriter = _childProcess.StandardInput;

            

            // Start reading subprocess output on dedicated background threads
            _readCancellation = new CancellationTokenSource();
            StartBackgroundReaders(_readCancellation.Token);

            InputBox.Focus();
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
            byte[] buffer = new byte[4096];

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    int bytesRead = await reader.ReadAsync(buffer, 0, buffer.Length, cancellationToken);

                    if (bytesRead > 0)
                    {
                        string output = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        string processed = ProcessAnsiCodes(output);

                        // Update UI on GUI thread
                        Dispatcher.UIThread.Post(() =>
                        {
                            OutputBox.Text += processed;
                            OutputBox.CaretIndex = OutputBox.Text.Length;
                        });
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
                        string output = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        // Prefix stderr lines so it's clear
                        string processed = ProcessAnsiCodes(output);

                        Dispatcher.UIThread.Post(() =>
                        {
                            OutputBox.Text += processed;
                            OutputBox.CaretIndex = OutputBox.Text.Length;
                        });
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

    private void InputBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_childInputWriter == null) return;

        if (e.Key != Key.Enter && e.Key != Key.Return)
            return;

        e.Handled = true;

        var line = InputBox.Text ?? string.Empty;
        InputBox.Text = string.Empty;

        if (string.IsNullOrEmpty(line))
            return;

        try
        {
            lock (_writeLock)
            {
                // Use the text writer for proper newline handling/encoding
                _childInputWriter.WriteLine(line);
                _childInputWriter.Flush();
            }
        }
        catch
        {
            // Ignore write errors
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _readCancellation?.Cancel();

        try
        {
            if (_childInputWriter != null)
            {
                try { _childInputWriter.Close(); } catch { }
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

        _readCancellation?.Dispose();
    }
}

