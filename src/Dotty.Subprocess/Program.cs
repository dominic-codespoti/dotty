using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dotty.Core;
using System.Runtime.Versioning;
using System.Net.Sockets;
using System.Text.Json;

namespace Dotty.Subprocess;

class Program
{
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--interactive")
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                return RunInteractiveShell();
            }
            Console.Error.WriteLine("Dotty.Subprocess: interactive mode only supported on linux/macos.");
            return 1;
        }

        return 0;
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    static int RunInteractiveShell()
    {
        string shell = GetPreferredShell();
        string? helperPath = Environment.GetEnvironmentVariable("DOTTY_SHELL_INTEGRATION_SCRIPT");

        // Prepare environment to pass into the child PTY process.
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);

        // Forward a small set of important environment variables from our environment
        // so the child has the same locale, path and shell context.
        string[] forwardKeys = new[] { "LANG", "LC_ALL", "LC_CTYPE", "USER", "HOME", "PATH", "SHELL", "LOGNAME", "USERNAME" };
        foreach (var k in forwardKeys)
        {
            var v = Environment.GetEnvironmentVariable(k);
            if (!string.IsNullOrEmpty(v)) env[k] = v;
        }

        // Ensure TERM is set to something sensible if caller didn't provide it
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TERM")))
        {
            env["TERM"] = "xterm-256color";
        }

        string? command = null;
        string? rcPathToRemove = null;
        string? zdotdirToRemove = null;

        try
        {
            if (!string.IsNullOrEmpty(helperPath))
            {
                if (shell.EndsWith("/zsh", StringComparison.Ordinal) || shell.EndsWith("zsh", StringComparison.Ordinal))
                {
                    // For zsh, create a temporary ZDOTDIR and write .zshrc that sources user's .zshrc then helper
                    var tmpDir = Path.Combine(Path.GetTempPath(), "dotty-zdotdir-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(tmpDir);
                    var zshRc = Path.Combine(tmpDir, ".zshrc");
                    File.WriteAllText(zshRc, BuildZshRcContent(helperPath));
                    env["ZDOTDIR"] = tmpDir;
                    zdotdirToRemove = tmpDir;
                }
                else if (shell.EndsWith("/bash", StringComparison.Ordinal) || shell.EndsWith("bash", StringComparison.Ordinal))
                {
                    // For bash, create a temporary rcfile which sources the user's rc then our helper
                    var rcPath = BuildTempRcForShell(shell, helperPath);
                    rcPathToRemove = rcPath;
                    // Use a shell command that sources our rcfile and then execs the real shell interactively
                    command = $". {QuoteForShell(rcPath)}; exec {QuoteForShell(shell)} -i";
                }
                else
                {
                    // Generic fallback: source the helper and exec the shell interactive
                    command = $". {QuoteForShell(helperPath)} && exec {QuoteForShell(shell)} -i";
                }
            }

            // Start the shell inside a real PTY so full-screen TUI apps work correctly
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                const int initialCols = 80;
                const int initialRows = 24;
                using var pty = UnixPty.Start(shell, Directory.GetCurrentDirectory(), initialCols, initialRows, command, env);

                Console.Error.WriteLine($"Dotty.Subprocess: Started shell {shell} (helper={(helperPath ?? "(none)")}) in PTY");

                // If caller provided a control socket path, bind and accept a connection to receive control messages (e.g., resize).
                string? controlSocket = Environment.GetEnvironmentVariable("DOTTY_CONTROL_SOCKET");
                string? socketPathToRemove = null;
                CancellationTokenSource? controlCts = null;
                Task? controlTask = null;
                if (!string.IsNullOrEmpty(controlSocket) && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
                {
                    try
                    {
                        // Remove leftover socket path if present
                        try { if (File.Exists(controlSocket)) File.Delete(controlSocket); } catch { }
                        socketPathToRemove = controlSocket;

                        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                        var endPoint = new UnixDomainSocketEndPoint(controlSocket);
                        listener.Bind(endPoint);
                        listener.Listen(1);

                        controlCts = new CancellationTokenSource();
                        var controlToken = controlCts.Token;

                        controlTask = Task.Run(async () =>
                        {
                            try
                            {
                                using var accepted = await Task.Run(() => listener.Accept());
                                using var ns = new NetworkStream(accepted, ownsSocket: true);
                                using var sr = new StreamReader(ns, Encoding.UTF8);
                                while (!controlToken.IsCancellationRequested)
                                {
                                    var line = await sr.ReadLineAsync();
                                    if (line == null) break;
                                    try
                                    {
                                        using var doc = JsonDocument.Parse(line);
                                        if (doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "resize")
                                        {
                                            int cols = 80, rows = 24;
                                            if (doc.RootElement.TryGetProperty("cols", out var c) && c.TryGetInt32(out var cc)) cols = cc;
                                            if (doc.RootElement.TryGetProperty("rows", out var r) && r.TryGetInt32(out var rr)) rows = rr;
                                            try { pty.Resize(cols, rows); } catch { }
                                        }
                                    }
                                    catch { }
                                }
                            }
                            catch { }
                            finally
                            {
                                try { listener.Close(); } catch { }
                            }
                        }, controlToken);
                    }
                    catch
                    {
                        // best-effort: ignore control socket failures
                        try { /* ignore */ } catch { }
                    }
                }

                    // Proxy data between the PTY master and our stdio using efficient stream copy.
                    var master = pty.Input; // master stream (read/write)
                        // Optionally probe the slave device and emit a one-shot `tty` probe
                        // when DOTTY_DEBUG_TTY=1 is set in the environment. This is an
                        // opt-in diagnostic that helps confirm whether the child shell
                        // has a proper controlling terminal.
                        try
                        {
                            var dbg = Environment.GetEnvironmentVariable("DOTTY_DEBUG_TTY");
                            if (!string.IsNullOrEmpty(dbg) && dbg != "0")
                            {
                                try
                                {
                                    var slave = pty.SlaveName();
                                    Console.Error.WriteLine($"Dotty.Subprocess: PTY slave={slave}");
                                }
                                catch { }

                                try
                                {
                                    var probe = System.Text.Encoding.UTF8.GetBytes("tty\n");
                                    // Write probe directly to master; it will execute in the shell and print the tty path
                                    master.Write(probe, 0, probe.Length);
                                    master.Flush();
                                }
                                catch { }
                            }
                        }
                        catch { }
                    var stdIn = Console.OpenStandardInput();
                    var stdOut = Console.OpenStandardOutput();

                    using var cts = new CancellationTokenSource();
                    var token = cts.Token;

                    // Use CopyToAsync which will block on reads until data is available (we ensured blocking fd)
                    var t1 = Task.Run(async () =>
                    {
                        try
                        {
                            await master.CopyToAsync(stdOut, 8192, token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { }
                        catch { }
                    }, token);

                    var t2 = Task.Run(async () =>
                    {
                        try
                        {
                            await stdIn.CopyToAsync(master, 4096, token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { }
                        catch { }
                    }, token);

                    // Wait for child to exit
                    int exitCode = pty.WaitForExit();

                    // Signal proxy tasks to stop and wait briefly
                    cts.Cancel();
                    try { Task.WaitAll(new[] { t1, t2 }, TimeSpan.FromSeconds(2)); } catch { }

                // Stop control task and cleanup socket
                try
                {
                    if (controlCts != null)
                    {
                        controlCts.Cancel();
                        try { controlTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }
                        controlCts.Dispose();
                    }
                }
                catch { }
                try { if (!string.IsNullOrEmpty(socketPathToRemove) && File.Exists(socketPathToRemove)) File.Delete(socketPathToRemove); } catch { }

                return exitCode;
            }
            else
            {
                Console.Error.WriteLine("Dotty.Subprocess: PTY support is only available on Linux/macOS in this build.");
                return 1;
            }
        }
        finally
        {
            // Clean up any temporary files/dirs we created for helper integration
            try { if (rcPathToRemove != null && File.Exists(rcPathToRemove)) File.Delete(rcPathToRemove); } catch { }
            try { if (zdotdirToRemove != null && Directory.Exists(zdotdirToRemove)) Directory.Delete(zdotdirToRemove, true); } catch { }
        }
    }

    private static string GetPreferredShell()
    {
        var shell = Environment.GetEnvironmentVariable("DOTTY_SHELL");
        if (!string.IsNullOrWhiteSpace(shell))
        {
            return shell;
        }

        shell = Environment.GetEnvironmentVariable("SHELL");
        if (!string.IsNullOrWhiteSpace(shell))
        {
            return shell;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                var username = Environment.UserName;
                foreach (var line in File.ReadLines("/etc/passwd"))
                {
                    if (line.StartsWith(username + ":", StringComparison.Ordinal))
                    {
                        var parts = line.Split(':');
                        if (parts.Length >= 7 && !string.IsNullOrWhiteSpace(parts[6]))
                        {
                            return parts[6];
                        }

                        break;
                    }
                }
            }
            catch
            {
            }
        }

        return "/bin/bash";
    }

    private static string QuoteForShell(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "''";
        }

        return "'" + value.Replace("'", "'\"'\"'") + "'";
    }

    private static string BuildTempRcForShell(string shell, string helperPath)
    {
        var rcPath = Path.Combine(Path.GetTempPath(), $"dotty-rc-{Guid.NewGuid():N}.sh");
        var sb = new StringBuilder();
        sb.AppendLine("# dotty temporary rcfile - sources original rc and then our helper");
        sb.AppendLine("# Source user's ~/.bashrc if available");
        sb.AppendLine("if [ -f \"$HOME/.bashrc\" ]; then");
        sb.AppendLine("  source \"$HOME/.bashrc\"");
        sb.AppendLine("fi");
        sb.AppendLine();
        // Source our helper after user's rc so starship and others are already set up
        sb.AppendLine($"source {QuoteForShell(helperPath)}");
        sb.AppendLine();
        // Wrap PROMPT_COMMAND so that dotty markers surround starship's prompt
    sb.AppendLine("# Ensure dotty markers wrap the user's prompt generation");
    sb.AppendLine("# We wrap the user's PS1 so that start/end markers are emitted around the visible prompt text\n# and not only via PROMPT_COMMAND which runs before PS1 is printed.");
    sb.AppendLine("if [ -n \"$PROMPT_COMMAND\" ]; then");
    sb.AppendLine("  DOTTY_OLD_PROMPT_COMMAND=\"$PROMPT_COMMAND\"");
    sb.AppendLine("  PROMPT_COMMAND=\"dotty_emit_prompt_precmd; $DOTTY_OLD_PROMPT_COMMAND\"");
    sb.AppendLine("else");
    sb.AppendLine("  PROMPT_COMMAND=\"dotty_emit_prompt_precmd\"");
    sb.AppendLine("fi");
    sb.AppendLine("# Wrap PS1 so the end marker is printed after the user's prompt (i.e. after starship's output)");
    sb.AppendLine("if [ -n \"$PS1\" ]; then");
    sb.AppendLine("  DOTTY_OLD_PS1=\"$PS1\"");
    sb.AppendLine("  PS1=\"$(dotty_emit_prompt_start)\"\"$DOTTY_OLD_PS1\"\"$(dotty_emit_prompt_end)\"");
    sb.AppendLine("fi");
    sb.AppendLine("export -f dotty_emit_prompt_precmd dotty_emit_prompt_start dotty_emit_prompt_end dotty_emit_prompt_flush");
        File.WriteAllText(rcPath, sb.ToString());
        return rcPath;
    }

    private static string BuildZshRcContent(string helperPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# dotty temporary zshrc - sources user's .zshrc then our helper");
        sb.AppendLine("if [ -f \"$HOME/.zshrc\" ]; then");
        sb.AppendLine("  source \"$HOME/.zshrc\"");
        sb.AppendLine("fi");
        sb.AppendLine();
        sb.AppendLine($"source {QuoteForShell(helperPath)}");
        sb.AppendLine();
    sb.AppendLine("# Add our dotty precmd to run before prompt rendering and wrap PROMPT so end is executed after the prompt prints\n# This avoids printing the end marker before the prompt (precmd runs before prompt printing).");
    sb.AppendLine("if [ -z \"${precmd_functions:-}\" ]; then");
    sb.AppendLine("  precmd_functions=(dotty_emit_prompt_precmd)");
    sb.AppendLine("else");
    sb.AppendLine("  precmd_functions=(dotty_emit_prompt_precmd \"${precmd_functions[@]}\")");
    sb.AppendLine("fi");
    sb.AppendLine("# Wrap PROMPT so the end marker is printed after user prompt text (zsh prints PROMPT after precmd)");
    sb.AppendLine("if [ -n \"$PROMPT\" ]; then");
    sb.AppendLine("  DOTTY_OLD_PROMPT=\"$PROMPT\"");
    sb.AppendLine("  PROMPT=\"$(dotty_emit_prompt_start)\"\"$DOTTY_OLD_PROMPT\"\"$(dotty_emit_prompt_end)\"");
    sb.AppendLine("fi");
    sb.AppendLine("typeset -fx dotty_emit_prompt_precmd dotty_emit_prompt_start dotty_emit_prompt_end dotty_emit_prompt_flush");
        return sb.ToString();
    }
}
