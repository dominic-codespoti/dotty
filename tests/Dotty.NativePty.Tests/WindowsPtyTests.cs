#if WINDOWS

using Dotty.Abstractions.Pty;
using Xunit;
using FluentAssertions;
using System.Text;

namespace Dotty.NativePty.Tests;

/// <summary>
/// Integration tests for Windows ConPTY implementation.
/// These tests only run on Windows with ConPTY support.
/// </summary>
public class WindowsPtyTests : IDisposable
{
    private IPty? _pty;

    public void Dispose()
    {
        PtyTestHelpers.SafeCleanup(_pty);
    }

    #region Constructor and Factory Tests

    /// <summary>
    /// Verifies that WindowsPty can be instantiated.
    /// </summary>
    [Fact]
    public void WindowsPty_Constructor_CreatesInstance()
    {
        // Act
        var pty = new Windows.WindowsPty();

        // Assert
        pty.Should().NotBeNull();
        pty.IsRunning.Should().BeFalse();
        pty.ProcessId.Should().Be(-1);

        // Cleanup
        pty.Dispose();
    }

    /// <summary>
    /// Verifies that WindowsPty implements IPty.
    /// </summary>
    [Fact]
    public void WindowsPty_ImplementsIPty()
    {
        // Act
        var pty = new Windows.WindowsPty();

        // Assert
        pty.Should().BeAssignableTo<IPty>();

        // Cleanup
        pty.Dispose();
    }

    #endregion

    #region Start() Tests

    /// <summary>
    /// Verifies that WindowsPty can start with default shell.
    /// </summary>
    [SkippableFact]
    public void WindowsPty_Start_WithDefaultShell()
    {
        Skip.IfNot(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
        // Arrange
        _pty = new Windows.WindowsPty();

        // Act
        _pty.Start();

        // Assert
        PtyTestHelpers.AssertPtyRunning(_pty);
    }

    /// <summary>
    /// Verifies that WindowsPty can start with cmd.exe.
    /// </summary>
    [SkippableFact]
    public void WindowsPty_Start_WithCmd()
    {
        Skip.IfNot(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
        // Arrange
        _pty = new Windows.WindowsPty();

        // Act
        _pty.Start(shell: "cmd.exe");

        // Assert
        PtyTestHelpers.AssertPtyRunning(_pty);
    }

    /// <summary>
    /// Verifies that WindowsPty can start with PowerShell.
    /// </summary>
    [SkippableFact]
    public void WindowsPty_Start_WithPowerShell()
    {
        Skip.IfNot(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
        // Arrange
        var psPath = Path.Combine(
            Environment.GetEnvironmentVariable("windir") ?? "C:\\Windows",
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        
        Skip.IfNot(File.Exists(psPath), "PowerShell not available");

        _pty = new Windows.WindowsPty();

        // Act
        _pty.Start(shell: psPath);

        // Assert
        PtyTestHelpers.AssertPtyRunning(_pty);
    }

    /// <summary>
    /// Verifies that WindowsPty can start with custom dimensions.
    /// </summary>
    [SkippableFact]
    public void WindowsPty_Start_WithCustomDimensions()
    {
        Skip.IfNot(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
        // Arrange
        _pty = new Windows.WindowsPty();
        var dimensions = new[] { (80, 24), (120, 30), (200, 50) };

        foreach (var (columns, rows) in dimensions)
        {
            // Act
            _pty.Start(columns: columns, rows: rows);

            // Assert
            PtyTestHelpers.AssertPtyRunning(_pty);
            
            // Cleanup for next iteration
            _pty.Kill(force: true);
            _pty.Dispose();
            _pty = new Windows.WindowsPty();
        }
    }

    /// <summary>
    /// Verifies that WindowsPty can start with working directory.
    /// </summary>
    [SkippableFact]
    public void WindowsPty_Start_WithWorkingDirectory()
    {
        Skip.IfNot(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
        // Arrange
        _pty = new Windows.WindowsPty();
        var workingDir = Path.GetTempPath();

        // Act
        _pty.Start(workingDirectory: workingDir);

        // Assert
        PtyTestHelpers.AssertPtyRunning(_pty);
    }

    /// <summary>
    /// Verifies that WindowsPty can start with environment variables.
    /// </summary>
    [SkippableFact]
    public void WindowsPty_Start_WithEnvironmentVariables()
    {
        Skip.IfNot(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
        // Arrange
        _pty = new Windows.WindowsPty();
        var envVars = PtyTestHelpers.CreateTestEnvironment();

        // Act
        _pty.Start(environmentVariables: envVars);

        // Assert
        PtyTestHelpers.AssertPtyRunning(_pty);
    }

    /// <summary>
    /// Verifies that Start() throws InvalidOperationException when already started.
    /// </summary>
    [SkippableFact]
    public void WindowsPty_Start_ThrowsWhenAlreadyStarted()
    {
        Skip.IfNot(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
        // Arrange
        _pty = new Windows.WindowsPty();
        _pty.Start();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => _pty.Start());
    }

    #endregion

    #region I/O Tests

    /// <summary>
    /// Verifies that WindowsPty can write input.
    /// </summary>
    [SkippableFact]
    public async Task WindowsPty_Write_SendsInputToProcess()
    {
        Skip.IfNot(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
        // Arrange
        _pty = new Windows.WindowsPty();
        _pty.Start(shell: "cmd.exe", columns: 80, rows: 24);

        await Task.Delay(500); // Wait for shell to start

        // Act
        var inputStream = _pty.InputStream;
        inputStream.Should().NotBeNull();

        var testData = "echo TEST_OUTPUT\r\n";
        var bytes = Encoding.ASCII.GetBytes(testData);
        await inputStream!.WriteAsync(bytes, 0, bytes.Length);
        await inputStream.FlushAsync();

        // Assert - just verify write completed without error
        Assert.True(true);
    }

    /// <summary>
    /// Verifies that WindowsPty can read output.
    /// </summary>
    [SkippableFact]
    public async Task WindowsPty_Read_ReturnsProcessOutput()
    {
        Skip.IfNot(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
        // Arrange
        _pty = new Windows.WindowsPty();
        _pty.Start(shell: "cmd.exe", columns: 80, rows: 24);

        await Task.Delay(500); // Wait for shell to start

        var outputStream = _pty.OutputStream;
        outputStream.Should().NotBeNull();

        // Send a command
        var inputStream = _pty.InputStream!;
        var command = "echo TEST_OUTPUT_UNIQUE\r\n";
        var bytes = Encoding.ASCII.GetBytes(command);
        await inputStream.WriteAsync(bytes, 0, bytes.Length);
        await inputStream.FlushAsync();

        // Act
        await Task.Delay(500); // Wait for output

        var buffer = new byte[4096];
        var output = new StringBuilder();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                var read = await outputStream!.ReadAsync(buffer, 0, buffer.Length, cts.Token);
                if (read > 0)
                {
                    output.Append(Encoding.UTF8.GetString(buffer, 0, read));
                    if (output.ToString().Contains("TEST_OUTPUT_UNIQUE"))
                    {
                        break;
                    }
                }
                else
                {
                    await Task.Delay(100, cts.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout - continue with what we have
        }

        // Assert
        output.ToString().Should().Contain("TEST_OUTPUT_UNIQUE");
    }

    /// <summary>
    /// Verifies that WindowsPty input/output streams are functional.
    /// </summary>
    [SkippableFact]
    public void WindowsPty_Streams_AreFunctional()
    {
        Skip.IfNot(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
        // Arrange
        _pty = new Windows.WindowsPty();
        _pty.Start(shell: "cmd.exe");

        // Act
        var inputStream = _pty.InputStream;
        var outputStream = _pty.OutputStream;

        // Assert
        inputStream.Should().NotBeNull();
        outputStream.Should().NotBeNull();
        inputStream!.CanWrite.Should().BeTrue();
        outputStream!.CanRead.Should().BeTrue();
    }

    #endregion

    #region Resize Tests

    /// <summary>
    /// Verifies that WindowsPty can resize.
    /// </summary>
    [SkippableFact]
    public void WindowsPty_Resize_ChangesConsoleSize()
    {
        Skip.IfNot(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
        // Arrange
        _pty = new Windows.WindowsPty();
        _pty.Start(columns: 80, rows: 24);

        // Act & Assert - should not throw
        var exception = Record.Exception(() => _pty.Resize(120, 30));
        exception.Should().BeNull("Resize should not throw");
    }

    /// <summary>
    /// Verifies that WindowsPty supports multiple resize operations.
    /// </summary>
    [SkippableFact]
    public void WindowsPty_Resize_MultipleOperations()
    {
        Skip.IfNot(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
        // Arrange
        _pty = new Windows.WindowsPty();
        _pty.Start(columns: 80, rows: 24);

        // Act & Assert - multiple resizes should work
        var dimensions = new[]
        {
            (60, 15),
            (80, 24),
            (120, 30),
            (200, 50),
            (40, 10)
        };

        foreach (var (cols, rows) in dimensions)
        {
            var exception = Record.Exception(() => _pty.Resize(cols, rows));
            exception.Should().BeNull($"Resize to {cols}x{rows} should not throw");
        }
    }

    /// <summary>
    /// Verifies that Resize() throws when not started.
    /// </summary>
    [Fact]
    public void WindowsPty_Resize_ThrowsWhenNotStarted()
    {
        // Arrange
        using var pty = new Windows.WindowsPty();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => pty.Resize(80, 24));
    }

    /// <summary>
    /// Verifies that Resize() throws ObjectDisposedException when disposed.
    /// </summary>
    [Fact]
    public void WindowsPty_Resize_ThrowsWhenDisposed()
    {
        // Arrange
        var pty = new Windows.WindowsPty();
        pty.Dispose();

        // Act & Assert
        Assert.Throws<ObjectDisposedException>(() => pty.Resize(80, 24));
    }

    #endregion

    #region Kill Tests

    /// <summary>
    /// Verifies that WindowsPty can kill the process gracefully.
    /// </summary>
    [SkippableFact]
    public void WindowsPty_Kill_GracefulTermination()
    {
        Skip.IfNot(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
        // Arrange
        _pty = new Windows.WindowsPty();
        _pty.Start(shell: "cmd.exe");
        var processId = _pty.ProcessId;

        // Act
        _pty.Kill(force: false);

        // Assert
        _pty.IsRunning.Should().BeFalse("Process should not be running after Kill()");
        
        // Verify process is gone
        Thread.Sleep(1000);
        PtyTestHelpers.ProcessExists(processId).Should().BeFalse("Process should be terminated");
    }

    /// <summary>
    /// Verifies that WindowsPty can force kill the process.
    /// </summary>
    [SkippableFact]
    public void WindowsPty_Kill_ForceTermination()
    {
        Skip.IfNot(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
        // Arrange
        _pty = new Windows.WindowsPty();
        _pty.Start(shell: "cmd.exe");
        var processId = _pty.ProcessId;

        // Act
        _pty.Kill(force: true);

        // Assert
        _pty.IsRunning.Should().BeFalse("Process should not be running after force Kill()");
        
        // Verify process is gone
        Thread.Sleep(500);
        PtyTestHelpers.ProcessExists(processId).Should().BeFalse("Process should be terminated");
    }

    /// <summary>
    /// Verifies that Kill() is idempotent.
    /// </summary>
    [SkippableFact]
    public void WindowsPty_Kill_IsIdempotent()
    {
        Skip.IfNot(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
        // Arrange
        _pty = new Windows.WindowsPty();
        _pty.Start(shell: "cmd.exe");

        // Act & Assert - multiple kills should not throw
        var exception1 = Record.Exception(() => _pty.Kill());
        var exception2 = Record.Exception(() => _pty.Kill());
        var exception3 = Record.Exception(() => _pty.Kill());

        exception1.Should().BeNull();
        exception2.Should().BeNull();
        exception3.Should().BeNull();
    }

    #endregion

    #region ProcessExited Event Tests

    /// <summary>
    /// Verifies that ProcessExited event fires when process exits.
    /// </summary>
    [SkippableFact]
    public async Task WindowsPty_ProcessExited_FiresOnProcessTermination()
    {
        Skip.IfNot(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
        // Arrange
        _pty = new Windows.WindowsPty();
        var eventFired = false;
        int receivedExitCode = -999;
        var tcs = new TaskCompletionSource<int>();
        
        _pty.ProcessExited += (sender, exitCode) =>
        {
            eventFired = true;
            receivedExitCode = exitCode;
            tcs.TrySetResult(exitCode);
        };

        _pty.Start(shell: "cmd.exe");

        // Act - send exit command
        var inputStream = _pty.InputStream!;
        var exitCommand = "exit\r\n";
        var bytes = Encoding.ASCII.GetBytes(exitCommand);
        await inputStream.WriteAsync(bytes, 0, bytes.Length);
        await inputStream.FlushAsync();

        // Wait for process to exit
        await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));

        // Assert
        eventFired.Should().BeTrue("ProcessExited event should fire");
        receivedExitCode.Should().Be(0, "Exit code should be 0 for successful exit");
    }

    /// <summary>
    /// Verifies that ProcessExited fires with non-zero exit code on error.
    /// </summary>
    [SkippableFact]
    public async Task WindowsPty_ProcessExited_FiresWithNonZeroExitCode()
    {
        Skip.IfNot(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
        // Arrange
        _pty = new Windows.WindowsPty();
        var exitCodeReceived = -1;
        var tcs = new TaskCompletionSource<int>();
        
        _pty.ProcessExited += (sender, exitCode) =>
        {
            exitCodeReceived = exitCode;
            tcs.TrySetResult(exitCode);
        };

        _pty.Start(shell: "cmd.exe /c exit 42");

        // Act - wait for exit
        await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));

        // Assert
        exitCodeReceived.Should().Be(42, "Exit code should be 42");
    }

    #endregion

    #region WaitForExitAsync Tests

    /// <summary>
    /// Verifies that WaitForExitAsync returns exit code.
    /// </summary>
    [SkippableFact]
    public async Task WindowsPty_WaitForExitAsync_ReturnsExitCode()
    {
        Skip.IfNot(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
        // Arrange
        _pty = new Windows.WindowsPty();
        _pty.Start(shell: "cmd.exe /c exit 0");

        // Act
        var exitCode = await _pty.WaitForExitAsync(TimeSpan.FromSeconds(10));

        // Assert
        exitCode.Should().Be(0);
    }

    /// <summary>
    /// Verifies that WaitForExitAsync returns correct exit code.
    /// </summary>
    [SkippableFact]
    public async Task WindowsPty_WaitForExitAsync_ReturnsCorrectExitCode()
    {
        Skip.IfNot(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
        // Arrange
        var exitCodes = new[] { 0, 1, 42 };

        foreach (var expectedExitCode in exitCodes)
        {
            _pty = new Windows.WindowsPty();
            _pty.Start(shell: $"cmd.exe /c exit {expectedExitCode}");

            // Act
            var exitCode = await _pty.WaitForExitAsync(TimeSpan.FromSeconds(10));

            // Assert
            exitCode.Should().Be(expectedExitCode);
            
            // Cleanup for next iteration
            _pty.Dispose();
        }
    }

    /// <summary>
    /// Verifies that WaitForExitAsync respects cancellation token.
    /// </summary>
    [SkippableFact]
    public async Task WindowsPty_WaitForExitAsync_RespectsCancellation()
    {
        Skip.IfNot(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
        // Arrange
        _pty = new Windows.WindowsPty();
        _pty.Start(shell: "cmd.exe /c timeout /t 10");
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() => 
            _pty.WaitForExitAsync(cts.Token));
    }

    #endregion

    #region Large Output Tests

    /// <summary>
    /// Verifies that WindowsPty can handle large output.
    /// </summary>
    [SkippableFact]
    public async Task WindowsPty_Read_LargeOutput()
    {
        Skip.IfNot(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
        // Arrange
        _pty = new Windows.WindowsPty();
        _pty.Start(shell: "cmd.exe");
        await Task.Delay(500);

        // Generate large output command
        var inputStream = _pty.InputStream!;
        var outputStream = _pty.OutputStream!;
        
        // Use a command that produces significant output
        var command = "dir /s C:\\Windows\\System32\\*.dll | head -100\r\n";
        var bytes = Encoding.ASCII.GetBytes(command);
        await inputStream.WriteAsync(bytes, 0, bytes.Length);
        await inputStream.FlushAsync();

        // Act
        await Task.Delay(1000);

        var buffer = new byte[8192];
        var totalRead = 0;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            while (!cts.Token.IsCancellationRequested && totalRead < 10000)
            {
                var read = await outputStream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
                totalRead += read;
                if (read == 0) break;
            }
        }
        catch (OperationCanceledException)
        {
            // Expected - continue
        }

        // Assert
        totalRead.Should().BeGreaterThan(0, "Should have read some output");
    }

    #endregion

    #region Dispose Tests

    /// <summary>
    /// Verifies that Dispose() cleans up resources.
    /// </summary>
    [SkippableFact]
    public void WindowsPty_Dispose_CleansUpResources()
    {
        Skip.IfNot(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
        // Arrange
        _pty = new Windows.WindowsPty();
        _pty.Start(shell: "cmd.exe");
        var processId = _pty.ProcessId;

        // Act
        _pty.Dispose();

        // Assert
        _pty.IsRunning.Should().BeFalse();
        
        // Verify process is gone
        Thread.Sleep(500);
        PtyTestHelpers.ProcessExists(processId).Should().BeFalse();
    }

    /// <summary>
    /// Verifies that Dispose() is idempotent.
    /// </summary>
    [SkippableFact]
    public void WindowsPty_Dispose_IsIdempotent()
    {
        Skip.IfNot(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
        // Arrange
        _pty = new Windows.WindowsPty();
        _pty.Start(shell: "cmd.exe");

        // Act & Assert - multiple disposes should not throw
        var exception1 = Record.Exception(() => _pty.Dispose());
        var exception2 = Record.Exception(() => _pty.Dispose());
        var exception3 = Record.Exception(() => _pty.Dispose());

        exception1.Should().BeNull();
        exception2.Should().BeNull();
        exception3.Should().BeNull();
    }

    #endregion

    #region Exception Handling Tests

    /// <summary>
    /// Verifies that invalid shell path throws PtyException.
    /// </summary>
    [SkippableFact]
    public void WindowsPty_Start_ThrowsOnInvalidShell()
    {
        Skip.IfNot(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
        // Arrange
        _pty = new Windows.WindowsPty();

        // Act & Assert
        var exception = Assert.Throws<PtyException>(() => 
            _pty.Start(shell: "nonexistent_shell.exe"));
        exception.Message.Should().NotBeNullOrEmpty();
    }

    #endregion
}

/// <summary>
/// Extension methods for PTY testing.
/// </summary>
internal static class WindowsPtyTestExtensions
{
    /// <summary>
    /// Waits for exit with a timeout.
    /// </summary>
    public static async Task<int> WaitForExitAsync(this IPty pty, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        return await pty.WaitForExitAsync(cts.Token);
    }
}

#endif
