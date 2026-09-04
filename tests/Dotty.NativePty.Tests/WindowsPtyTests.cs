#if WINDOWS

using Dotty.Abstractions.Pty;
using Xunit;
using FluentAssertions;
using System.Text;
using System.Reflection;

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

    private static (string? ApplicationName, string CommandLine) InvokeBuildProcessStartInfo(string shell)
    {
        var method = typeof(Windows.WindowsPty).GetMethod("BuildProcessStartInfo", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        var result = method!.Invoke(null, [shell]);
        result.Should().NotBeNull();

        var resultType = result!.GetType();
        var applicationName = (string?)resultType.GetField("Item1")!.GetValue(result);
        var commandLine = (StringBuilder)resultType.GetField("Item2")!.GetValue(result)!;
        return (applicationName, commandLine.ToString());
    }

    private static string CreateTempExecutablePath(string fileName)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"Dotty Windows Pty Tests {Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        var executablePath = Path.Combine(directory, fileName);
        File.WriteAllText(executablePath, string.Empty);
        return executablePath;
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
    [Fact]
    public void WindowsPty_Start_WithDefaultShell()
    {
        Assert.SkipUnless(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
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
    [Fact]
    public void WindowsPty_Start_WithCmd()
    {
        Assert.SkipUnless(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
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
    [Fact]
    public void WindowsPty_Start_WithPowerShell()
    {
        Assert.SkipUnless(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
        // Arrange
        var psPath = Path.Combine(
            Environment.GetEnvironmentVariable("windir") ?? "C:\\Windows",
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        
        Assert.SkipUnless(File.Exists(psPath), "PowerShell not available");

        _pty = new Windows.WindowsPty();

        // Act
        _pty.Start(shell: psPath);

        // Assert
        PtyTestHelpers.AssertPtyRunning(_pty);
    }

    /// <summary>
    /// Verifies that WindowsPty can start with custom dimensions.
    /// </summary>
    [Fact]
    public void WindowsPty_Start_WithCustomDimensions()
    {
        Assert.SkipUnless(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
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
    [Fact]
    public void WindowsPty_Start_WithWorkingDirectory()
    {
        Assert.SkipUnless(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
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
    [Fact]
    public void WindowsPty_Start_WithEnvironmentVariables()
    {
        Assert.SkipUnless(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
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
    [Fact]
    public void WindowsPty_Start_ThrowsWhenAlreadyStarted()
    {
        Assert.SkipUnless(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
        // Arrange
        _pty = new Windows.WindowsPty();
        _pty.Start();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => _pty.Start());
    }

    /// <summary>
    /// Verifies that existing executable paths containing spaces are quoted for CreateProcess.
    /// </summary>
    [Fact]
    public void WindowsPty_BuildProcessStartInfo_QuotesExistingExecutablePathWithSpaces()
    {
        var executablePath = CreateTempExecutablePath("dotty test shell.exe");

        var (applicationName, commandLine) = InvokeBuildProcessStartInfo(executablePath);

        applicationName.Should().Be(executablePath);
        commandLine.Should().Be($"\"{executablePath}\"");
    }

    /// <summary>
    /// Verifies that unquoted executable paths with spaces preserve trailing arguments.
    /// </summary>
    [Fact]
    public void WindowsPty_BuildProcessStartInfo_ResolvesUnquotedExecutablePathWithSpacesAndArguments()
    {
        var executablePath = CreateTempExecutablePath("dotty test shell.exe");
        var shell = $"{executablePath} /c \"echo hello world\"";

        var (applicationName, commandLine) = InvokeBuildProcessStartInfo(shell);

        applicationName.Should().Be(executablePath);
        commandLine.Should().Be($"\"{executablePath}\" /c \"echo hello world\"");
    }

    /// <summary>
    /// Verifies that quoted executable paths preserve arguments without reparsing them.
    /// </summary>
    [Fact]
    public void WindowsPty_BuildProcessStartInfo_PreservesQuotedExecutableAndArguments()
    {
        var executablePath = CreateTempExecutablePath("dotty test shell.exe");
        var scriptPath = CreateTempExecutablePath("dotty script file.cmd");
        var shell = $"\"{executablePath}\" -NoLogo -File \"{scriptPath}\"";

        var (applicationName, commandLine) = InvokeBuildProcessStartInfo(shell);

        applicationName.Should().Be(executablePath);
        commandLine.Should().Be(shell);
    }

    /// <summary>
    /// Verifies that PATH-resolved commands are left unchanged.
    /// </summary>
    [Fact]
    public void WindowsPty_BuildProcessStartInfo_LeavesSearchPathCommandsUnchanged()
    {
        const string shell = "cmd.exe /c echo hello";

        var (applicationName, commandLine) = InvokeBuildProcessStartInfo(shell);

        applicationName.Should().BeNull();
        commandLine.Should().Be(shell);
    }

    #endregion

    #region I/O Tests

    /// <summary>
    /// Verifies that WindowsPty can write input.
    /// </summary>
    [Fact]
    public async Task WindowsPty_Write_SendsInputToProcess()
    {
        Assert.SkipUnless(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
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
    [Fact]
    public async Task WindowsPty_Read_ReturnsProcessOutput()
    {
        Assert.SkipUnless(PtyPlatform.IsConPtySupported, "ConPTY not supported");

        // Arrange
        _pty = new Windows.WindowsPty();
        _pty.Start(shell: "cmd.exe", columns: 80, rows: 24);

        var outputStream = _pty.OutputStream;
        outputStream.Should().NotBeNull();

        // ConPTY requires the output pipe to be serviced continuously while
        // input is sent. A blocking reader on its own thread matches the
        // documented pipe-handling model and keeps startup output from
        // interfering with the command response.
        const string marker = "TEST_OUTPUT_UNIQUE";
        var output = new StringBuilder();
        var markerObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = Task.Run(() =>
        {
            var buffer = new byte[4096];

            try
            {
                while (true)
                {
                    var read = outputStream!.Read(buffer, 0, buffer.Length);
                    if (read == 0)
                    {
                        break;
                    }

                    lock (output)
                    {
                        output.Append(Encoding.UTF8.GetString(buffer, 0, read));
                        if (output.ToString().Contains(marker, StringComparison.Ordinal))
                        {
                            markerObserved.TrySetResult();
                            break;
                        }
                    }
                }
            }
            catch (IOException)
            {
                // The test cleanup closes the synchronous pipe if the
                // bounded wait expires.
            }
            catch (ObjectDisposedException)
            {
                // The test cleanup may close the stream while the blocking
                // reader is being released.
            }
        });

        try
        {
            await Task.Delay(500); // Wait for shell to start

            var inputStream = _pty.InputStream!;
            var command = $"echo {marker}\r\n";
            var bytes = Encoding.ASCII.GetBytes(command);
            inputStream.Write(bytes, 0, bytes.Length);
            inputStream.Flush();

            await markerObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (!reader.IsCompleted)
            {
                if (_pty.IsRunning)
                {
                    _pty.Kill(force: true);
                }

                _pty.Dispose();
            }

            await reader;
        }

        lock (output)
        {
            var capturedOutput = output.ToString();
            capturedOutput.Should().Contain(
                marker,
                "The captured ConPTY output was: {0}",
                capturedOutput);
        }
    }

    /// <summary>
    /// Verifies that WindowsPty input/output streams are functional.
    /// </summary>
    [Fact]
    public void WindowsPty_Streams_AreFunctional()
    {
        Assert.SkipUnless(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
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
    [Fact]
    public void WindowsPty_Resize_ChangesConsoleSize()
    {
        Assert.SkipUnless(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
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
    [Fact]
    public void WindowsPty_Resize_MultipleOperations()
    {
        Assert.SkipUnless(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
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

    [Fact]
    public void WindowsPty_Start_RejectsInvalidDimensions()
    {
        Assert.SkipUnless(PtyPlatform.IsConPtySupported, "ConPTY not supported");

        using var pty = new Windows.WindowsPty();
        var exception = Assert.Throws<PtyException>(() => pty.Start(columns: 0, rows: 24));

        exception.Code.Should().Be(PtyErrorCode.InvalidDimensions);
    }

    #endregion

    #region Kill Tests

    /// <summary>
    /// Verifies that WindowsPty can kill the process gracefully.
    /// </summary>
    [Fact]
    public void WindowsPty_Kill_GracefulTermination()
    {
        Assert.SkipUnless(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
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
    [Fact]
    public void WindowsPty_Kill_ForceTermination()
    {
        Assert.SkipUnless(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
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
    [Fact]
    public void WindowsPty_Kill_IsIdempotent()
    {
        Assert.SkipUnless(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
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
    [Fact]
    public async Task WindowsPty_ProcessExited_FiresOnProcessTermination()
    {
        Assert.SkipUnless(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
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
    [Fact]
    public async Task WindowsPty_ProcessExited_FiresWithNonZeroExitCode()
    {
        Assert.SkipUnless(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
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
    [Fact]
    public async Task WindowsPty_WaitForExitAsync_ReturnsExitCode()
    {
        Assert.SkipUnless(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
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
    [Fact]
    public async Task WindowsPty_WaitForExitAsync_ReturnsCorrectExitCode()
    {
        Assert.SkipUnless(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
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
    [Fact]
    public async Task WindowsPty_WaitForExitAsync_RespectsCancellation()
    {
        Assert.SkipUnless(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
        string windowsDirectory = Environment.GetEnvironmentVariable("windir") ?? "C:\\Windows";
        string powerShellPath = Path.Combine(
            windowsDirectory, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        Assert.SkipUnless(File.Exists(powerShellPath), "Windows PowerShell not available");

        _pty = new Windows.WindowsPty();
        _pty.Start(shell: $"\"{powerShellPath}\" -NoLogo -NoProfile -Command Start-Sleep -Seconds 10");
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
    [Fact]
    public async Task WindowsPty_Read_LargeOutput()
    {
        Assert.SkipUnless(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
        // Arrange
        _pty = new Windows.WindowsPty();
        _pty.Start(shell: "cmd.exe");
        await Task.Delay(500);

        // Generate large output command
        var inputStream = _pty.InputStream!;
        var outputStream = _pty.OutputStream!;
        
        // Use a command that produces significant output
        var command = "for /L %i in (1,1,500) do @echo WINDOWS_PTY_TEST_LINE_%i\r\n";
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
    [Fact]
    public void WindowsPty_Dispose_CleansUpResources()
    {
        Assert.SkipUnless(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
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
    [Fact]
    public void WindowsPty_Dispose_IsIdempotent()
    {
        Assert.SkipUnless(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
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
    [Fact]
    public void WindowsPty_Start_ThrowsOnInvalidShell()
    {
        Assert.SkipUnless(PtyPlatform.IsConPtySupported, "ConPTY not supported");
        
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
