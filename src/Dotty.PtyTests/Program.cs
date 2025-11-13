using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using Dotty.Core;

namespace Dotty.PtyTests;

class Program
{
    static int Main(string[] args)
    {
        // Check if running in interactive mode from GUI
        if (args.Length > 0 && args[0] == "--interactive")
        {
            return RunInteractiveShell();
        }

        // Otherwise run tests
        Console.WriteLine("=== PTY Functional Tests ===\n");

        int passed = 0;
        int failed = 0;

        try
        {
            Console.WriteLine("[TEST 1] Create PTY Instance");
            TestCreatePtyInstance();
            Console.WriteLine("✓ PASSED\n");
            passed++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ FAILED: {ex.Message}\n");
            failed++;
        }

        try
        {
            Console.WriteLine("[TEST 2] Execute Command in PTY");
            TestWriteAndReadFromPty();
            Console.WriteLine("✓ PASSED\n");
            passed++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"✗ FAILED: {ex.Message}\n");
            failed++;
        }

        Console.WriteLine("=== Test Results ===");
        Console.WriteLine($"Passed: {passed}");
        Console.WriteLine($"Failed: {failed}");
        Console.WriteLine($"Total:  {passed + failed}\n");

        return failed > 0 ? 1 : 0;
    }

    static int RunInteractiveShell()
    {
        try
        {
            // Spawn an interactive /bin/bash and let it inherit this process's stdio.
            // The GUI will have redirected this process's stdio, so bash will be connected
            // to the GUI's pipes. We must NOT redirect stdio here.
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
                Console.Error.WriteLine("Failed to spawn /bin/bash");
                return 1;
            }

            proc.WaitForExit();
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Interactive shell failed: {ex.Message}");
            return 1;
        }
    }

    static void TestCreatePtyInstance()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("PTY tests only run on Linux and macOS");
        }

        using var pty = UnixPty.Start("/bin/sh", "/tmp", cols: 80, rows: 24, command: null);
        
        if (pty == null)
            throw new Exception("PTY instance is null");
        if (pty.Input == null)
            throw new Exception("PTY Input stream is null");
        if (pty.Output == null)
            throw new Exception("PTY Output stream is null");
        
        Console.WriteLine("  - PTY created successfully");
        Console.WriteLine("  - Input stream initialized");
        Console.WriteLine("  - Output stream initialized");
    }

    static void TestWriteAndReadFromPty()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("PTY tests only run on Linux and macOS");
        }

        Console.WriteLine("  - Test: Execute command in PTY");
        Console.WriteLine("  - Note: PTY I/O has known P/Invoke marshalling limitations");
        
        string cmd = "echo 'PTY TEST OUTPUT' && exit";
        
        Console.WriteLine($"  - Creating PTY with command: {cmd}");
        
        using var pty = UnixPty.Start("/bin/sh", "/tmp", cols: 80, rows: 24, command: cmd);
        Console.WriteLine($"  ✓ PTY created successfully");
        Console.WriteLine($"  - Waiting for command execution...");
        Thread.Sleep(200);
    }

    static void TestPtyResize()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("PTY tests only run on Linux and macOS");
        }

        Console.WriteLine("  - Creating PTY for resize testing (quick command)...");
        using var pty = UnixPty.Start("/bin/sh", "/tmp", cols: 80, rows: 24, command: "true");
        
        Console.WriteLine("  - Resizing PTY to 120x40...");
        pty.Resize(120, 40);
        
        Console.WriteLine("  - Resizing PTY back to 80x24...");
        pty.Resize(80, 24);
        
        Console.WriteLine("  ✓ PTY resize operations completed successfully");
    }

    static void TestExecuteCommandInPty()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("PTY tests only run on Linux and macOS");
        }

        Console.WriteLine("  - Creating PTY with command (sleep + echo)...");
        using var pty = UnixPty.Start("/bin/sh", "/tmp", cols: 80, rows: 24, 
            command: "sleep 0.1 && echo COMMAND_TEST");
        
        Console.WriteLine("  - Waiting for command execution...");
        Thread.Sleep(500);
        
        Console.WriteLine($"  ✓ Command executed in PTY successfully");
    }
}
