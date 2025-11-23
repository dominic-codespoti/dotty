using System.Diagnostics;
using System.Text;
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
        

        // Prepare environment to pass into the child PTY process.
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);

        // Forward a small set of important environment variables from our environment
        // so the child has the same locale, path and shell context.
        string[] forwardKeys = new[] { "LANG", "LC_ALL", "LC_CTYPE", "USER", "HOME", "PATH", "SHELL", "LOGNAME", "USERNAME", "PROMPT_EOL_MARK" };
        foreach (var k in forwardKeys)
        {
            var v = Environment.GetEnvironmentVariable(k);
            if (!string.IsNullOrEmpty(v)) env[k] = v;
        }

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PROMPT_EOL_MARK")) &&
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTTY_KEEP_PROMPT_EOL_MARK")))
        {
            env["PROMPT_EOL_MARK"] = string.Empty;
        }

        // Ensure TERM is set to something sensible if caller didn't provide it
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TERM")))
        {
            env["TERM"] = "xterm-256color";
        }

        string? command = null;
        // string? rcPathToRemove = null;
        // string? zdotdirToRemove = null;

        try
        {
            // No shell integration helper: do not inject any rcfiles; shell will render its own prompt.

            // Start the shell inside a real PTY so full-screen TUI apps work correctly
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                const int initialCols = 80;
                const int initialRows = 24;
                using var pty = UnixPty.Start(shell, Directory.GetCurrentDirectory(), initialCols, initialRows, command, env);

                try
                {
                    var dbg = Environment.GetEnvironmentVariable("DOTTY_DEBUG");
                    if (!string.IsNullOrEmpty(dbg) && dbg != "0")
                    {
                        Console.Error.WriteLine($"Dotty.Subprocess: Started shell {shell} in PTY");
                    }
                }
                catch { }

                // Emit diagnostics about child's open file descriptors so we can determine
                // whether the child's stdio (fd 0/1/2) are actually attached to the PTY slave.
                try
                {
                    var childPid = pty.ChildPid;
                    for (int fd = 0; fd <= 2; fd++)
                    {
                        try
                        {
                            var psi = new ProcessStartInfo("readlink", $"-f /proc/{childPid}/fd/{fd}")
                            {
                                RedirectStandardOutput = true,
                                UseShellExecute = false,
                                CreateNoWindow = true,
                            };
                            using var p = Process.Start(psi);
                                if (p != null)
                                {
                                    var outText = p.StandardOutput.ReadToEnd().Trim();
                                    p.WaitForExit(1000);
                                    try
                                    {
                                        var dbg = Environment.GetEnvironmentVariable("DOTTY_DEBUG");
                                        if (!string.IsNullOrEmpty(dbg) && dbg != "0")
                                        {
                                            Console.Error.WriteLine($"Dotty.Subprocess: child fd{fd} -> {outText}");
                                        }
                                    }
                                    catch { }
                                }
                        }
                        catch (Exception ex)
                        {
                            try
                            {
                                var dbg = Environment.GetEnvironmentVariable("DOTTY_DEBUG");
                                if (!string.IsNullOrEmpty(dbg) && dbg != "0")
                                {
                                    Console.Error.WriteLine($"Dotty.Subprocess: child fd{fd} -> (error: {ex.Message})");
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }

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
                                    try
                                    {
                                        // Emit slave device diagnostics only when DOTTY_DEBUG is enabled.
                                        var dbg = Environment.GetEnvironmentVariable("DOTTY_DEBUG");
                                        if (!string.IsNullOrEmpty(dbg) && dbg != "0")
                                        {
                                            var slave = pty.SlaveName();
                                            Console.Error.WriteLine($"Dotty.Subprocess: PTY slave={slave}");
                                            try
                                            {
                                                var isAtty = pty.IsSlaveAtty();
                                                Console.Error.WriteLine($"Dotty.Subprocess: SlaveIsAtty={isAtty}");
                                            }
                                            catch { }
                                        }
                                    }
                                    catch { }
                                }
                                catch { }

                                try
                                {
                                    var dbg = Environment.GetEnvironmentVariable("DOTTY_DEBUG_TTY");
                                    if (!string.IsNullOrEmpty(dbg) && dbg != "0")
                                    {
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
            // No temporary files/dirs to clean up since integration is removed
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

    // Shell integration helpers removed (OSC marker injection not used).
}
