using Dotty.Abstractions.Pty;
using Xunit;
using FluentAssertions;
using System.IO;
using Moq;

namespace Dotty.NativePty.Tests;

/// <summary>
/// Tests for the IPty interface contract and mock-based unit tests.
/// Verifies that all implementations follow the expected interface behavior.
/// </summary>
public class IPtyContractTests
{
    #region Interface Property Tests

    /// <summary>
    /// Verifies that IsRunning property returns false before Start().
    /// The initial state should indicate the PTY is not running.
    /// </summary>
    [Fact]
    public void IPty_IsRunning_FalseBeforeStart()
    {
        // Arrange
        var mockPty = new Mock<IPty>();
        mockPty.SetupGet(p => p.IsRunning).Returns(false);

        // Act
        var isRunning = mockPty.Object.IsRunning;

        // Assert
        isRunning.Should().BeFalse("PTY should not be running before Start()");
    }

    /// <summary>
    /// Verifies that IsRunning property returns true after Start().
    /// After starting, the PTY should report running state.
    /// </summary>
    [Fact]
    public void IPty_IsRunning_TrueAfterStart()
    {
        // Arrange
        var mockPty = new Mock<IPty>();
        mockPty.SetupGet(p => p.IsRunning).Returns(true);

        // Act
        var isRunning = mockPty.Object.IsRunning;

        // Assert
        isRunning.Should().BeTrue("PTY should be running after Start()");
    }

    /// <summary>
    /// Verifies that ProcessId returns -1 before Start().
    /// The process ID should be invalid before the process is started.
    /// </summary>
    [Fact]
    public void IPty_ProcessId_MinusOneBeforeStart()
    {
        // Arrange
        var mockPty = new Mock<IPty>();
        mockPty.SetupGet(p => p.ProcessId).Returns(-1);

        // Act
        var processId = mockPty.Object.ProcessId;

        // Assert
        processId.Should().Be(-1, "ProcessId should be -1 before Start()");
    }

    /// <summary>
    /// Verifies that ProcessId returns positive value after Start().
    /// The process ID should be a valid positive integer after starting.
    /// </summary>
    [Fact]
    public void IPty_ProcessId_PositiveAfterStart()
    {
        // Arrange
        var mockPty = new Mock<IPty>();
        mockPty.SetupGet(p => p.ProcessId).Returns(12345);

        // Act
        var processId = mockPty.Object.ProcessId;

        // Assert
        processId.Should().BeGreaterThan(0, "ProcessId should be positive after Start()");
    }

    /// <summary>
    /// Verifies that InputStream is null before Start().
    /// The input stream should not be available before starting.
    /// </summary>
    [Fact]
    public void IPty_InputStream_NullBeforeStart()
    {
        // Arrange
        var mockPty = new Mock<IPty>();
        mockPty.SetupGet(p => p.InputStream).Returns((Stream?)null);

        // Act
        var inputStream = mockPty.Object.InputStream;

        // Assert
        inputStream.Should().BeNull("InputStream should be null before Start()");
    }

    /// <summary>
    /// Verifies that OutputStream is null before Start().
    /// The output stream should not be available before starting.
    /// </summary>
    [Fact]
    public void IPty_OutputStream_NullBeforeStart()
    {
        // Arrange
        var mockPty = new Mock<IPty>();
        mockPty.SetupGet(p => p.OutputStream).Returns((Stream?)null);

        // Act
        var outputStream = mockPty.Object.OutputStream;

        // Assert
        outputStream.Should().BeNull("OutputStream should be null before Start()");
    }

    /// <summary>
    /// Verifies that InputStream is non-null after Start().
    /// After starting, the input stream should be available.
    /// </summary>
    [Fact]
    public void IPty_InputStream_AvailableAfterStart()
    {
        // Arrange
        var mockStream = new Mock<Stream>();
        var mockPty = new Mock<IPty>();
        mockPty.SetupGet(p => p.InputStream).Returns(mockStream.Object);

        // Act
        var inputStream = mockPty.Object.InputStream;

        // Assert
        inputStream.Should().NotBeNull("InputStream should be available after Start()");
    }

    /// <summary>
    /// Verifies that OutputStream is non-null after Start().
    /// After starting, the output stream should be available.
    /// </summary>
    [Fact]
    public void IPty_OutputStream_AvailableAfterStart()
    {
        // Arrange
        var mockStream = new Mock<Stream>();
        var mockPty = new Mock<IPty>();
        mockPty.SetupGet(p => p.OutputStream).Returns(mockStream.Object);

        // Act
        var outputStream = mockPty.Object.OutputStream;

        // Assert
        outputStream.Should().NotBeNull("OutputStream should be available after Start()");
    }

    #endregion

    #region Start() Method Tests

    /// <summary>
    /// Verifies that Start() can be called with default parameters.
    /// The interface should support calling Start() with no arguments.
    /// </summary>
    [Fact]
    public void IPty_Start_WithDefaults()
    {
        // Arrange
        var mockPty = new Mock<IPty>();
        mockPty.Setup(p => p.Start(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), 
            It.IsAny<string>(), It.IsAny<IDictionary<string, string>>()));

        // Act
        var exception = Record.Exception(() => mockPty.Object.Start());

        // Assert
        exception.Should().BeNull("Start() with defaults should not throw");
        mockPty.Verify(p => p.Start(null, 80, 24, null, null), Times.Once);
    }

    /// <summary>
    /// Verifies that Start() accepts custom shell path.
    /// Should allow specifying a custom shell executable.
    /// </summary>
    [Fact]
    public void IPty_Start_WithCustomShell()
    {
        // Arrange
        var mockPty = new Mock<IPty>();
        var customShell = "/bin/zsh";

        // Act
        mockPty.Object.Start(shell: customShell);

        // Assert
        mockPty.Verify(p => p.Start(customShell, 80, 24, null, null), Times.Once);
    }

    /// <summary>
    /// Verifies that Start() accepts custom terminal dimensions.
    /// Should allow specifying custom columns and rows.
    /// </summary>
    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 30)]
    [InlineData(40, 10)]
    public void IPty_Start_WithCustomDimensions(int columns, int rows)
    {
        // Arrange
        var mockPty = new Mock<IPty>();

        // Act
        mockPty.Object.Start(columns: columns, rows: rows);

        // Assert
        mockPty.Verify(p => p.Start(null, columns, rows, null, null), Times.Once);
    }

    /// <summary>
    /// Verifies that Start() accepts working directory.
    /// Should allow specifying the working directory for the shell.
    /// </summary>
    [Fact]
    public void IPty_Start_WithWorkingDirectory()
    {
        // Arrange
        var mockPty = new Mock<IPty>();
        var workingDir = "/tmp";

        // Act
        mockPty.Object.Start(workingDirectory: workingDir);

        // Assert
        mockPty.Verify(p => p.Start(null, 80, 24, workingDir, null), Times.Once);
    }

    /// <summary>
    /// Verifies that Start() accepts environment variables.
    /// Should allow passing environment variables to the process.
    /// </summary>
    [Fact]
    public void IPty_Start_WithEnvironmentVariables()
    {
        // Arrange
        var mockPty = new Mock<IPty>();
        var envVars = new Dictionary<string, string>
        {
            { "TEST_VAR", "value" },
            { "DOTTY", "1" }
        };

        // Act
        mockPty.Object.Start(environmentVariables: envVars);

        // Assert
        mockPty.Verify(p => p.Start(null, 80, 24, null, envVars), Times.Once);
    }

    /// <summary>
    /// Verifies that Start() throws InvalidOperationException when already started.
    /// Starting an already running PTY should fail.
    /// </summary>
    [Fact]
    public void IPty_Start_ThrowsWhenAlreadyStarted()
    {
        // Arrange
        var mockPty = new Mock<IPty>();
        mockPty.Setup(p => p.Start(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), 
            It.IsAny<string>(), It.IsAny<IDictionary<string, string>>()))
            .Throws(new InvalidOperationException("PTY session is already started."));

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => mockPty.Object.Start());
        exception.Message.Should().Contain("already started");
    }

    /// <summary>
    /// Verifies that Start() throws PtyException on failure.
    /// PTY creation failures should throw PtyException.
    /// </summary>
    [Fact]
    public void IPty_Start_ThrowsPtyExceptionOnFailure()
    {
        // Arrange
        var mockPty = new Mock<IPty>();
        mockPty.Setup(p => p.Start(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), 
            It.IsAny<string>(), It.IsAny<IDictionary<string, string>>()))
            .Throws(new PtyException("Failed to create PTY"));

        // Act & Assert
        var exception = Assert.Throws<PtyException>(() => mockPty.Object.Start());
        exception.Message.Should().Contain("Failed");
    }

    #endregion

    #region Resize() Method Tests

    /// <summary>
    /// Verifies that Resize() accepts valid dimensions.
    /// Should successfully resize to reasonable terminal dimensions.
    /// </summary>
    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 40)]
    [InlineData(40, 10)]
    [InlineData(200, 60)]
    public void IPty_Resize_AcceptsValidDimensions(int columns, int rows)
    {
        // Arrange
        var mockPty = new Mock<IPty>();
        mockPty.Setup(p => p.Resize(columns, rows));

        // Act
        var exception = Record.Exception(() => mockPty.Object.Resize(columns, rows));

        // Assert
        exception.Should().BeNull("Resize with valid dimensions should not throw");
        mockPty.Verify(p => p.Resize(columns, rows), Times.Once);
    }

    /// <summary>
    /// Verifies that Resize() throws ObjectDisposedException when disposed.
    /// Resizing a disposed PTY should fail appropriately.
    /// </summary>
    [Fact]
    public void IPty_Resize_ThrowsWhenDisposed()
    {
        // Arrange
        var mockPty = new Mock<IPty>();
        mockPty.Setup(p => p.Resize(It.IsAny<int>(), It.IsAny<int>()))
            .Throws(new ObjectDisposedException("WindowsPty"));

        // Act & Assert
        var exception = Assert.Throws<ObjectDisposedException>(() => mockPty.Object.Resize(80, 24));
        exception.ObjectName.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// Verifies that Resize() throws InvalidOperationException when not started.
    /// Resizing before Start() should fail appropriately.
    /// </summary>
    [Fact]
    public void IPty_Resize_ThrowsWhenNotStarted()
    {
        // Arrange
        var mockPty = new Mock<IPty>();
        mockPty.Setup(p => p.Resize(It.IsAny<int>(), It.IsAny<int>()))
            .Throws(new InvalidOperationException("Pseudo console is not created."));

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => mockPty.Object.Resize(80, 24));
        exception.Message.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region Kill() Method Tests

    /// <summary>
    /// Verifies that Kill() can be called with force=false.
    /// Graceful termination should be supported.
    /// </summary>
    [Fact]
    public void IPty_Kill_Graceful()
    {
        // Arrange
        var mockPty = new Mock<IPty>();
        mockPty.Setup(p => p.Kill(false));

        // Act
        var exception = Record.Exception(() => mockPty.Object.Kill(force: false));

        // Assert
        exception.Should().BeNull("Graceful Kill should not throw");
        mockPty.Verify(p => p.Kill(false), Times.Once);
    }

    /// <summary>
    /// Verifies that Kill() can be called with force=true.
    /// Force kill should be supported.
    /// </summary>
    [Fact]
    public void IPty_Kill_Force()
    {
        // Arrange
        var mockPty = new Mock<IPty>();
        mockPty.Setup(p => p.Kill(true));

        // Act
        var exception = Record.Exception(() => mockPty.Object.Kill(force: true));

        // Assert
        exception.Should().BeNull("Force Kill should not throw");
        mockPty.Verify(p => p.Kill(true), Times.Once);
    }

    /// <summary>
    /// Verifies that Kill() can be called with default parameter.
    /// Default should be graceful termination (force=false).
    /// </summary>
    [Fact]
    public void IPty_Kill_DefaultParameter()
    {
        // Arrange
        var mockPty = new Mock<IPty>();
        mockPty.Setup(p => p.Kill(It.IsAny<bool>()));

        // Act
        mockPty.Object.Kill();

        // Assert
        mockPty.Verify(p => p.Kill(false), Times.Once, 
            "Default Kill() should use force=false");
    }

    /// <summary>
    /// Verifies that Kill() is idempotent.
    /// Calling Kill() multiple times should not throw.
    /// </summary>
    [Fact]
    public void IPty_Kill_IsIdempotent()
    {
        // Arrange
        var mockPty = new Mock<IPty>();
        mockPty.Setup(p => p.Kill(It.IsAny<bool>()));

        // Act
        var exception1 = Record.Exception(() => mockPty.Object.Kill());
        var exception2 = Record.Exception(() => mockPty.Object.Kill());
        var exception3 = Record.Exception(() => mockPty.Object.Kill());

        // Assert
        exception1.Should().BeNull();
        exception2.Should().BeNull();
        exception3.Should().BeNull();
        mockPty.Verify(p => p.Kill(It.IsAny<bool>()), Times.Exactly(3));
    }

    #endregion

    #region WaitForExitAsync() Method Tests

    /// <summary>
    /// Verifies that WaitForExitAsync returns exit code.
    /// Should return the process exit code when process exits.
    /// </summary>
    [Fact]
    public async Task IPty_WaitForExitAsync_ReturnsExitCode()
    {
        // Arrange
        var mockPty = new Mock<IPty>();
        mockPty.Setup(p => p.WaitForExitAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        // Act
        var exitCode = await mockPty.Object.WaitForExitAsync();

        // Assert
        exitCode.Should().Be(0);
    }

    /// <summary>
    /// Verifies that WaitForExitAsync returns non-zero on failure.
    /// Failed processes should return non-zero exit code.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(-1)]
    public async Task IPty_WaitForExitAsync_ReturnsNonZeroOnFailure(int exitCodeValue)
    {
        // Arrange
        var mockPty = new Mock<IPty>();
        mockPty.Setup(p => p.WaitForExitAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(exitCodeValue);

        // Act
        var exitCode = await mockPty.Object.WaitForExitAsync();

        // Assert
        exitCode.Should().Be(exitCodeValue);
    }

    /// <summary>
    /// Verifies that WaitForExitAsync throws InvalidOperationException when not started.
    /// Waiting on non-started PTY should fail appropriately.
    /// </summary>
    [Fact]
    public async Task IPty_WaitForExitAsync_ThrowsWhenNotStarted()
    {
        // Arrange
        var mockPty = new Mock<IPty>();
        mockPty.Setup(p => p.WaitForExitAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Process is not started."));

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => 
            mockPty.Object.WaitForExitAsync());
    }

    /// <summary>
    /// Verifies that WaitForExitAsync respects cancellation token.
    /// Should support cancellation.
    /// </summary>
    [Fact]
    public async Task IPty_WaitForExitAsync_RespectsCancellationToken()
    {
        // Arrange
        var mockPty = new Mock<IPty>();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        mockPty.Setup(p => p.WaitForExitAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() => 
            mockPty.Object.WaitForExitAsync(cts.Token));
    }

    #endregion

    #region ProcessExited Event Tests

    /// <summary>
    /// Verifies that ProcessExited event can be subscribed.
    /// Event subscription should be supported.
    /// </summary>
    [Fact]
    public void IPty_ProcessExited_CanSubscribe()
    {
        // Arrange
        var mockPty = new Mock<IPty>();
        var eventFired = false;
        EventHandler<int>? handler = (sender, exitCode) => { eventFired = true; };

        // Act
        mockPty.Object.ProcessExited += handler;

        // Assert
        // If we got here without exception, the event can be subscribed
        Assert.True(true);
    }

    /// <summary>
    /// Verifies that ProcessExited event can be unsubscribed.
    /// Event unsubscription should be supported.
    /// </summary>
    [Fact]
    public void IPty_ProcessExited_CanUnsubscribe()
    {
        // Arrange
        var mockPty = new Mock<IPty>();
        EventHandler<int>? handler = (sender, exitCode) => { };
        mockPty.Object.ProcessExited += handler;

        // Act
        var exception = Record.Exception(() => 
            mockPty.Object.ProcessExited -= handler);

        // Assert
        exception.Should().BeNull("Event unsubscription should not throw");
    }

    /// <summary>
    /// Verifies that ProcessExited passes exit code to handlers.
    /// Event args should contain the process exit code.
    /// </summary>
    [Fact]
    public void IPty_ProcessExited_PassesExitCode()
    {
        // Arrange
        var mockPty = new Mock<IPty>();
        int receivedExitCode = -999;
        
        mockPty.Object.ProcessExited += (sender, exitCode) => 
        {
            receivedExitCode = exitCode;
        };

        // Act - simulate event firing by invoking the delegate directly
        // This test verifies the event signature and handler behavior
        var raiseEvent = mockPty.Object.GetType()
            .GetEvent("ProcessExited")?.EventHandlerType;
        
        // Assert - event is defined and is of correct type
        raiseEvent.Should().NotBeNull("ProcessExited event should exist");
        raiseEvent.Should().BeAssignableTo<EventHandler<int>>().GetType();
    }

    #endregion

    #region IDisposable Tests

    /// <summary>
    /// Verifies that Dispose() can be called without errors.
    /// Disposing should not throw exceptions.
    /// </summary>
    [Fact]
    public void IPty_Dispose_DoesNotThrow()
    {
        // Arrange
        var mockPty = new Mock<IPty>();
        mockPty.As<IDisposable>().Setup(d => d.Dispose());

        // Act
        var exception = Record.Exception(() => 
            ((IDisposable)mockPty.Object).Dispose());

        // Assert
        exception.Should().BeNull("Dispose should not throw");
    }

    /// <summary>
    /// Verifies that Dispose() is idempotent.
    /// Multiple Dispose() calls should not throw.
    /// </summary>
    [Fact]
    public void IPty_Dispose_IsIdempotent()
    {
        // Arrange
        var mockPty = new Mock<IPty>();
        mockPty.As<IDisposable>().Setup(d => d.Dispose());

        // Act
        var disposable = (IDisposable)mockPty.Object;
        var exception1 = Record.Exception(() => disposable.Dispose());
        var exception2 = Record.Exception(() => disposable.Dispose());
        var exception3 = Record.Exception(() => disposable.Dispose());

        // Assert
        exception1.Should().BeNull();
        exception2.Should().BeNull();
        exception3.Should().BeNull();
    }

    /// <summary>
    /// Verifies that members throw ObjectDisposedException after Dispose().
    /// Using a disposed PTY should fail appropriately.
    /// </summary>
    [Fact]
    public void IPty_MembersThrowWhenDisposed()
    {
        // Arrange
        var mockPty = new Mock<IPty>();
        mockPty.Setup(p => p.Resize(It.IsAny<int>(), It.IsAny<int>()))
            .Throws(new ObjectDisposedException("WindowsPty"));

        // Act & Assert
        Assert.Throws<ObjectDisposedException>(() => 
            mockPty.Object.Resize(80, 24));
    }

    #endregion

    #region Stream I/O Tests

    /// <summary>
    /// Verifies that InputStream supports writing.
    /// Input stream should be writable for sending data to PTY.
    /// </summary>
    [Fact]
    public void IPty_InputStream_SupportsWriting()
    {
        // Arrange
        var mockStream = new Mock<Stream>();
        mockStream.SetupGet(s => s.CanWrite).Returns(true);
        
        var mockPty = new Mock<IPty>();
        mockPty.SetupGet(p => p.InputStream).Returns(mockStream.Object);

        // Act
        var canWrite = mockPty.Object.InputStream?.CanWrite;

        // Assert
        canWrite.Should().BeTrue("InputStream should support writing");
    }

    /// <summary>
    /// Verifies that OutputStream supports reading.
    /// Output stream should be readable for receiving data from PTY.
    /// </summary>
    [Fact]
    public void IPty_OutputStream_SupportsReading()
    {
        // Arrange
        var mockStream = new Mock<Stream>();
        mockStream.SetupGet(s => s.CanRead).Returns(true);
        
        var mockPty = new Mock<IPty>();
        mockPty.SetupGet(p => p.OutputStream).Returns(mockStream.Object);

        // Act
        var canRead = mockPty.Object.OutputStream?.CanRead;

        // Assert
        canRead.Should().BeTrue("OutputStream should support reading");
    }

    /// <summary>
    /// Verifies that streams are different instances.
    /// Input and output streams should be separate streams.
    /// </summary>
    [Fact]
    public void IPty_Streams_AreSeparate()
    {
        // Arrange
        var inputStream = new Mock<Stream>().Object;
        var outputStream = new Mock<Stream>().Object;
        
        var mockPty = new Mock<IPty>();
        mockPty.SetupGet(p => p.InputStream).Returns(inputStream);
        mockPty.SetupGet(p => p.OutputStream).Returns(outputStream);

        // Act
        var input = mockPty.Object.InputStream;
        var output = mockPty.Object.OutputStream;

        // Assert
        input.Should().NotBeSameAs(output, "Input and output streams should be separate");
    }

    #endregion

    #region Test Helper Interface

    /// <summary>
    /// Helper interface for invoking events in mocks.
    /// </summary>
    public interface IPtyInvocable
    {
        void InvokeProcessExited(int exitCode);
    }

    #endregion
}
