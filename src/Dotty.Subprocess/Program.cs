using System;
using System.Diagnostics;

namespace Dotty.Subprocess;

class Program
{
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--interactive")
        {
            return RunInteractiveShell();
        }

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
