using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using Dotty.Runtime.Sessions;

namespace Dotty.App.Tests;

internal static class TestShellEnvironment
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (OperatingSystem.IsWindows())
        {
            // Non-interactive cmd prints no banner or prompt; `more >NUL` keeps the
            // process alive, silently consuming input until the pseudoconsole closes.
            Environment.SetEnvironmentVariable("DOTTY_SHELL", "cmd.exe /D /Q /C \"more >NUL\"");
            return;
        }

        string directory = Path.Combine(Path.GetTempPath(), $"dotty-test-shell-{Environment.ProcessId}");
        Directory.CreateDirectory(directory);
        string shellPath = Path.Combine(directory, "dotty-test-shell.sh");
        File.WriteAllText(shellPath, "#!/bin/sh\nexec cat >/dev/null\n");
        File.SetUnixFileMode(
            shellPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Environment.SetEnvironmentVariable("DOTTY_SHELL", shellPath);
    }

    private static readonly ConditionalWeakTable<TerminalSession, object> SettledSessions = new();

    /// <summary>
    /// Waits once per session until the platform PTY's own startup output has
    /// settled. ConPTY clears the screen and resets modes on startup regardless
    /// of the shell, which would otherwise overwrite state a test injects.
    /// </summary>
    internal static void WaitForStartupOutput(TerminalSession session)
    {
        lock (SettledSessions)
        {
            if (SettledSessions.TryGetValue(session, out _))
                return;
            SettledSessions.Add(session, new object());
        }

        long lastOutput = Environment.TickCount64;
        int sawOutput = 0;
        void OnRender()
        {
            Volatile.Write(ref lastOutput, Environment.TickCount64);
            Volatile.Write(ref sawOutput, 1);
        }

        session.RenderScheduled += OnRender;
        try
        {
            long deadline = Environment.TickCount64 + 5000;
            bool requireOutput = OperatingSystem.IsWindows();
            while (Environment.TickCount64 < deadline)
            {
                bool quiet = Environment.TickCount64 - Volatile.Read(ref lastOutput) >= 250
                    && !session.OutputBacklogged;
                if (quiet && (!requireOutput || Volatile.Read(ref sawOutput) != 0))
                    return;
                Thread.Sleep(10);
            }
        }
        finally
        {
            session.RenderScheduled -= OnRender;
        }
    }
}
