using System;
using System.IO;
using System.Runtime.CompilerServices;

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
}
