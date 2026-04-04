using Xunit;
using FluentAssertions;
using Dotty.Abstractions.Pty;
using System.Text;

namespace Dotty.NativePty.Tests;

/// <summary>
/// Test helpers and utilities for PTY testing.
/// Provides common functionality for PTY integration tests.
/// </summary>
public static class PtyTestHelpers
{
    /// <summary>
    /// Gets a test timeout value appropriate for PTY operations.
    /// </summary>
    public static TimeSpan DefaultTimeout => TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets a short timeout for quick operations.
    /// </summary>
    public static TimeSpan ShortTimeout => TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets a long timeout for complex operations.
    /// </summary>
    public static TimeSpan LongTimeout => TimeSpan.FromSeconds(30);

    /// <summary>
    /// Creates a unique test directory path.
    /// </summary>
    public static string CreateTestDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotty-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Reads all available data from a stream with timeout.
    /// </summary>
    public static async Task<string> ReadAllAvailableAsync(Stream stream, TimeSpan? timeout = null)
    {
        timeout ??= DefaultTimeout;
        var cts = new CancellationTokenSource(timeout.Value);
        var buffer = new byte[4096];
        var result = new List<byte>();

        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                // Try to read - if no data available, ReadAsync will wait
                var read = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
                if (read == 0) break;
                result.AddRange(buffer.Take(read));
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout reached, return what we have
        }

        return System.Text.Encoding.UTF8.GetString(result.ToArray());
    }

    /// <summary>
    /// Reads from a PTY output stream until a pattern is found or timeout occurs.
    /// </summary>
    public static async Task<string> ReadUntilAsync(
        Stream stream, 
        string pattern, 
        TimeSpan? timeout = null)
    {
        timeout ??= DefaultTimeout;
        var cts = new CancellationTokenSource(timeout.Value);
        var buffer = new byte[4096];
        var result = new StringBuilder();

        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                // Try to read with a short timeout
                using var readCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                try
                {
                    var read = await stream.ReadAsync(buffer, 0, buffer.Length, readCts.Token);
                    if (read > 0)
                    {
                        var chunk = Encoding.UTF8.GetString(buffer, 0, read);
                        result.Append(chunk);
                        
                        if (result.ToString().Contains(pattern))
                        {
                            break;
                        }
                    }
                    else if (read == 0)
                    {
                        // Stream closed
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    // No data available, continue waiting
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout - continue with what we have
        }

        return result.ToString();
    }

    /// <summary>
    /// Writes a string to a PTY input stream and flushes.
    /// </summary>
    public static async Task WriteToPtyAsync(Stream inputStream, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await inputStream.WriteAsync(bytes, 0, bytes.Length);
        await inputStream.FlushAsync();
    }

    /// <summary>
    /// Writes a line to a PTY input stream and flushes.
    /// </summary>
    public static async Task WriteLineToPtyAsync(Stream inputStream, string line)
    {
        await WriteToPtyAsync(inputStream, line + "\n");
    }

    /// <summary>
    /// Waits for a PTY process to become ready for input.
    /// </summary>
    public static async Task WaitForPtyReadyAsync(Stream outputStream, TimeSpan? timeout = null)
    {
        timeout ??= DefaultTimeout;
        var cts = new CancellationTokenSource(timeout.Value);
        
        // Wait for some output indicating the shell is ready
        try
        {
            var buffer = new byte[256];
            while (!cts.Token.IsCancellationRequested)
            {
                // Try to read with a short timeout
                using var readCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                try
                {
                    var read = await outputStream.ReadAsync(buffer, 0, buffer.Length, readCts.Token);
                    if (read > 0) break;
                }
                catch (OperationCanceledException)
                {
                    // No data available, continue waiting
                    await Task.Delay(100, cts.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout is acceptable - shell might be ready but quiet
        }
    }

    /// <summary>
    /// Cleans up a PTY instance and ensures all resources are released.
    /// </summary>
    public static void SafeCleanup(IPty? pty)
    {
        if (pty == null) return;

        try
        {
            if (pty.IsRunning)
            {
                pty.Kill(force: true);
            }
        }
        catch { }

        try
        {
            pty.Dispose();
        }
        catch { }
    }

    /// <summary>
    /// Kills a process by ID if it exists.
    /// </summary>
    public static void KillProcessIfExists(int processId)
    {
        if (processId <= 0) return;

        try
        {
            var process = System.Diagnostics.Process.GetProcessById(processId);
            if (process != null && !process.HasExited)
            {
                process.Kill();
                process.WaitForExit(1000);
            }
        }
        catch { }
    }

    /// <summary>
    /// Verifies that a process no longer exists.
    /// </summary>
    public static bool ProcessExists(int processId)
    {
        if (processId <= 0) return false;

        try
        {
            var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Asserts that a PTY is running.
    /// </summary>
    public static void AssertPtyRunning(IPty pty, string message = "PTY should be running")
    {
        pty.IsRunning.Should().BeTrue(message);
        pty.ProcessId.Should().BeGreaterThan(0, "Process ID should be valid");
        pty.InputStream.Should().NotBeNull("Input stream should be available");
        pty.OutputStream.Should().NotBeNull("Output stream should be available");
    }

    /// <summary>
    /// Asserts that a PTY is not running.
    /// </summary>
    public static void AssertPtyNotRunning(IPty pty, string message = "PTY should not be running")
    {
        pty.IsRunning.Should().BeFalse(message);
    }

    /// <summary>
    /// Creates a test environment with custom variables.
    /// </summary>
    public static Dictionary<string, string> CreateTestEnvironment()
    {
        return new Dictionary<string, string>
        {
            { "DOTTY_TEST", "1" },
            { "DOTTY_TEST_PID", Guid.NewGuid().ToString("N") }
        };
    }

    /// <summary>
    /// Gets the shell prompt pattern for the current platform.
    /// </summary>
    public static string GetShellPromptPattern()
    {
        if (PtyPlatform.IsWindows)
        {
            // Windows CMD/PS patterns
            return ">";
        }
        else
        {
            // Unix shell patterns ($, %, #)
            return "$";
        }
    }

    /// <summary>
    /// Gets a simple test command that works on all platforms.
    /// </summary>
    public static string GetTestCommand()
    {
        if (PtyPlatform.IsWindows)
        {
            return "echo DOTTY_TEST_OK";
        }
        else
        {
            return "echo DOTTY_TEST_OK";
        }
    }

    /// <summary>
    /// Gets an exit command for the current platform.
    /// </summary>
    public static string GetExitCommand()
    {
        return "exit";
    }

    /// <summary>
    /// Retries an action until it succeeds or timeout occurs.
    /// </summary>
    public static async Task<T> RetryAsync<T>(
        Func<Task<T>> action, 
        TimeSpan? timeout = null,
        int retryDelayMs = 100)
    {
        timeout ??= DefaultTimeout;
        var startTime = DateTime.UtcNow;
        Exception? lastException = null;

        while (DateTime.UtcNow - startTime < timeout)
        {
            try
            {
                return await action();
            }
            catch (Exception ex)
            {
                lastException = ex;
                await Task.Delay(retryDelayMs);
            }
        }

        throw new TimeoutException($"Action timed out after {timeout}", lastException);
    }
}

/// <summary>
/// Xunit attributes for conditional test execution.
/// </summary>
public static class ConditionalFacts
{
    /// <summary>
    /// Fact that only runs on Windows.
    /// </summary>
    public class WindowsOnlyFact : FactAttribute
    {
        public WindowsOnlyFact()
        {
            if (!PtyPlatform.IsWindows)
            {
                Skip = "This test only runs on Windows";
            }
        }
    }

    /// <summary>
    /// Fact that only runs on Linux.
    /// </summary>
    public class LinuxOnlyFact : FactAttribute
    {
        public LinuxOnlyFact()
        {
            if (!PtyPlatform.IsLinux)
            {
                Skip = "This test only runs on Linux";
            }
        }
    }

    /// <summary>
    /// Fact that only runs on macOS.
    /// </summary>
    public class MacOSOnlyFact : FactAttribute
    {
        public MacOSOnlyFact()
        {
            if (!PtyPlatform.IsMacOS)
            {
                Skip = "This test only runs on macOS";
            }
        }
    }

    /// <summary>
    /// Fact that only runs on Unix-like systems.
    /// </summary>
    public class UnixOnlyFact : FactAttribute
    {
        public UnixOnlyFact()
        {
            if (!PtyPlatform.IsUnix)
            {
                Skip = "This test only runs on Unix-like systems";
            }
        }
    }

    /// <summary>
    /// Fact that only runs when PTY is supported.
    /// </summary>
    public class PtySupportedFact : FactAttribute
    {
        public PtySupportedFact()
        {
            if (!PtyFactory.IsSupported)
            {
                Skip = $"PTY is not supported on this platform: {PtyFactory.GetUnsupportedReason()}";
            }
        }
    }

    /// <summary>
    /// Fact that only runs when ConPTY is supported.
    /// </summary>
    public class ConPtySupportedFact : FactAttribute
    {
        public ConPtySupportedFact()
        {
            if (!PtyPlatform.IsWindows)
            {
                Skip = "This test only runs on Windows";
            }
            else if (!PtyPlatform.IsConPtySupported)
            {
                Skip = "ConPTY is not supported on this Windows version";
            }
        }
    }
}
