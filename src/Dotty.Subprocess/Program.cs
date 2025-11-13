using System;
using System.Diagnostics;

namespace Dotty.Subprocess;

class Program
{
    static int Main(string[] args)
    {
        // Only support the interactive mode used by the GUI; otherwise exit silently.
        if (args.Length > 0 && args[0] == "--interactive")
        {
            return RunInteractiveShell();
        }

        // No test logic here anymore — this subprocess exists solely to host an interactive shell
        // when launched by the GUI. Exit success by default when not running interactively.
        return 0;
    }

    static int RunInteractiveShell()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                UseShellExecute = false,
                RedirectStandardInput = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                // Fail silently; caller can detect non-zero exit code
                return 1;
            }

            proc.WaitForExit();
            return proc.ExitCode;
        }
        catch
        {
            return 1;
        }
    }
}
