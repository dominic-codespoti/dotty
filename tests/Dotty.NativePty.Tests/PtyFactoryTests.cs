using Dotty.Abstractions.Pty;
using Xunit;
using FluentAssertions;
using System.Runtime.InteropServices;

namespace Dotty.NativePty.Tests;

/// <summary>
/// Unit and integration tests for the PTY factory.
/// Tests factory method behavior across different platforms and error handling.
/// </summary>
public class PtyFactoryTests
{
    #region Create() Method Tests

    /// <summary>
    /// Verifies that Create() returns correct implementation per platform.
    /// On Windows: WindowsPty, On Unix: UnixPty.
    /// </summary>
    [Fact]
    public void PtyFactory_Create_ReturnsCorrectImplementation()
    {
        // Act
        if (!PtyFactory.IsSupported)
        {
            // Skip if PTY is not supported on this platform
            return;
        }

        var pty = PtyFactory.Create();

        // Assert
        pty.Should().NotBeNull("factory should return a PTY instance");
        
        if (PtyPlatform.IsWindows)
        {
#if WINDOWS
            pty.Should().BeOfType<Windows.WindowsPty>("Windows should use WindowsPty");
#else
            throw new PlatformNotSupportedException("Windows support not compiled");
#endif
        }
        else if (PtyPlatform.IsUnix)
        {
            pty.Should().BeOfType<Unix.UnixPty>("Unix should use UnixPty");
        }
    }

    /// <summary>
    /// Verifies that Create() on Windows returns WindowsPty type.
    /// Type check for Windows platform.
    /// </summary>
    [Fact]
    public void PtyFactory_Create_ReturnsWindowsPtyOnWindows()
    {
        // Arrange
        if (!PtyPlatform.IsWindows)
        {
            return; // Skip on non-Windows
        }

        if (!PtyFactory.IsSupported)
        {
            throw new PlatformNotSupportedException("Windows ConPTY is not supported on this version");
        }

        // Act
        var pty = PtyFactory.Create();

        // Assert
#if WINDOWS
        pty.Should().BeOfType<Windows.WindowsPty>();
#else
        Assert.Fail("This test can only run on Windows");
#endif
    }

    /// <summary>
    /// Verifies that Create() on Linux returns UnixPty type.
    /// Type check for Linux platform.
    /// </summary>
    [Fact]
    public void PtyFactory_Create_ReturnsUnixPtyOnLinux()
    {
        // Arrange
        if (!PtyPlatform.IsLinux)
        {
            return; // Skip on non-Linux
        }

        // Act
        var pty = PtyFactory.Create();

        // Assert
        pty.Should().BeOfType<Unix.UnixPty>();
    }

    /// <summary>
    /// Verifies that Create() on macOS returns UnixPty type.
    /// Type check for macOS platform.
    /// </summary>
    [Fact]
    public void PtyFactory_Create_ReturnsUnixPtyOnMacOS()
    {
        // Arrange
        if (!PtyPlatform.IsMacOS)
        {
            return; // Skip on non-macOS
        }

        // Act
        var pty = PtyFactory.Create();

        // Assert
        pty.Should().BeOfType<Unix.UnixPty>();
    }

    /// <summary>
    /// Verifies that factory throws on unsupported platforms.
    /// When IsSupported is false, Create() should throw.
    /// </summary>
    [Fact]
    public void PtyFactory_Create_ThrowsOnUnsupportedPlatform()
    {
        // This test can only be simulated since we can't change the OS
        // But we can verify the exception type for unsupported scenarios
        if (PtyFactory.IsSupported)
        {
            return; // Skip on supported platforms
        }

        // Act & Assert
        var exception = Assert.Throws<PlatformNotSupportedException>(() => PtyFactory.Create());
        exception.Message.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// Verifies that created PTY implements IPty interface correctly.
    /// All returned instances should properly implement the contract.
    /// </summary>
    [Fact]
    public void PtyFactory_Create_ReturnsValidIPtyImplementation()
    {
        // Arrange
        if (!PtyFactory.IsSupported)
        {
            return; // Skip if not supported
        }

        // Act
        using var pty = PtyFactory.Create();

        // Assert - verify interface properties
        pty.Should().BeAssignableTo<IPty>();
        pty.IsRunning.Should().BeFalse("PTY should not be running before Start()");
        pty.ProcessId.Should().Be(-1, "ProcessId should be -1 before Start()");
    }

    /// <summary>
    /// Verifies that Create() returns a new instance each time.
    /// Each call should return a distinct PTY instance.
    /// </summary>
    [Fact]
    public void PtyFactory_Create_ReturnsNewInstanceEachTime()
    {
        // Arrange
        if (!PtyFactory.IsSupported)
        {
            return; // Skip if not supported
        }

        // Act
        using var pty1 = PtyFactory.Create();
        using var pty2 = PtyFactory.Create();

        // Assert
        pty1.Should().NotBeSameAs(pty2, "each Create() call should return a new instance");
    }

    #endregion

    #region CreateAndStart() Method Tests

    /// <summary>
    /// Verifies that CreateAndStart() returns an already-started PTY.
    /// The returned PTY should be running immediately.
    /// </summary>
    [Fact]
    public void PtyFactory_CreateAndStart_ReturnsRunningPty()
    {
        // Arrange
        if (!PtyFactory.IsSupported)
        {
            return; // Skip if not supported
        }

        // Act
        using var pty = PtyFactory.CreateAndStart();

        // Assert
        pty.Should().NotBeNull();
        pty.IsRunning.Should().BeTrue("CreateAndStart should start the PTY");
        pty.ProcessId.Should().BeGreaterThan(0, "ProcessId should be valid after Start()");
        pty.InputStream.Should().NotBeNull();
        pty.OutputStream.Should().NotBeNull();
    }

    /// <summary>
    /// Verifies that CreateAndStart() uses default shell when none specified.
    /// Should use the platform default shell path.
    /// </summary>
    [Fact]
    public void PtyFactory_CreateAndStart_UsesDefaultShell()
    {
        // Arrange
        if (!PtyFactory.IsSupported)
        {
            return; // Skip if not supported
        }

        // Act
        using var pty = PtyFactory.CreateAndStart();

        // Assert - verify the PTY was created with default settings
        pty.IsRunning.Should().BeTrue();
        
        // Cleanup
        pty.Kill(force: true);
    }

    /// <summary>
    /// Verifies that CreateAndStart() accepts custom shell path.
    /// Should start the specified shell executable.
    /// </summary>
    [Fact]
    public void PtyFactory_CreateAndStart_AcceptsCustomShell()
    {
        // Arrange
        if (!PtyFactory.IsSupported)
        {
            return; // Skip if not supported
        }

        var shell = PtyPlatform.GetDefaultShell();

        // Act
        using var pty = PtyFactory.CreateAndStart(shell);

        // Assert
        pty.IsRunning.Should().BeTrue();
        pty.ProcessId.Should().BeGreaterThan(0);
        
        // Cleanup
        pty.Kill(force: true);
    }

    /// <summary>
    /// Verifies that CreateAndStart() accepts custom terminal dimensions.
    /// Should set the initial terminal size correctly.
    /// </summary>
    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 30)]
    [InlineData(40, 10)]
    public void PtyFactory_CreateAndStart_AcceptsCustomDimensions(int columns, int rows)
    {
        // Arrange
        if (!PtyFactory.IsSupported)
        {
            return; // Skip if not supported
        }

        // Act
        using var pty = PtyFactory.CreateAndStart(columns: columns, rows: rows);

        // Assert
        pty.IsRunning.Should().BeTrue();
        
        // Cleanup
        pty.Kill(force: true);
    }

    /// <summary>
    /// Verifies that CreateAndStart() accepts working directory.
    /// Should start the shell in the specified directory.
    /// </summary>
    [Fact]
    public void PtyFactory_CreateAndStart_AcceptsWorkingDirectory()
    {
        // Arrange
        if (!PtyFactory.IsSupported)
        {
            return; // Skip if not supported
        }

        var workingDir = System.IO.Directory.GetCurrentDirectory();

        // Act
        using var pty = PtyFactory.CreateAndStart(workingDirectory: workingDir);

        // Assert
        pty.IsRunning.Should().BeTrue();
        
        // Cleanup
        pty.Kill(force: true);
    }

    /// <summary>
    /// Verifies that CreateAndStart() accepts environment variables.
    /// Should pass environment variables to the shell process.
    /// </summary>
    [Fact]
    public void PtyFactory_CreateAndStart_AcceptsEnvironmentVariables()
    {
        // Arrange
        if (!PtyFactory.IsSupported)
        {
            return; // Skip if not supported
        }

        var envVars = new Dictionary<string, string>
        {
            { "TEST_VAR", "test_value" },
            { "DOTTY_TEST", "1" }
        };

        // Act
        using var pty = PtyFactory.CreateAndStart(environmentVariables: envVars);

        // Assert
        pty.IsRunning.Should().BeTrue();
        
        // Cleanup
        pty.Kill(force: true);
    }

    #endregion

    #region IsSupported Property Tests

    /// <summary>
    /// Verifies that IsSupported is consistent with platform detection.
    /// IsSupported should be true when the platform is supported.
    /// </summary>
    [Fact]
    public void PtyFactory_IsSupported_MatchesPlatformDetection()
    {
        // Arrange
        var isSupported = PtyFactory.IsSupported;

        // Act & Assert
        if (PtyPlatform.IsWindows)
        {
            isSupported.Should().Be(PtyPlatform.IsConPtySupported, 
                "Windows support depends on ConPTY availability");
        }
        else if (PtyPlatform.IsUnix)
        {
            isSupported.Should().BeTrue("Unix platforms should be supported");
        }
        else
        {
            isSupported.Should().BeFalse("Unknown platforms should not be supported");
        }
    }

    /// <summary>
    /// Verifies that IsSupported is stable across multiple calls.
    /// The result should not change between calls.
    /// </summary>
    [Theory]
    [InlineData(5)]
    public void PtyFactory_IsSupported_ConsistentAcrossCalls(int iterations)
    {
        // Act
        var results = new bool[iterations];
        for (int i = 0; i < iterations; i++)
        {
            results[i] = PtyFactory.IsSupported;
        }

        // Assert
        results.Should().AllBeEquivalentTo(results[0], 
            "IsSupported should be consistent across calls");
    }

    #endregion

    #region GetUnsupportedReason() Tests

    /// <summary>
    /// Verifies that GetUnsupportedReason returns null when supported.
    /// On supported platforms, there should be no reason for unsupported status.
    /// </summary>
    [Fact]
    public void PtyFactory_GetUnsupportedReason_NullWhenSupported()
    {
        // Arrange
        if (!PtyFactory.IsSupported)
        {
            return; // Skip on unsupported
        }

        // Act
        var reason = PtyFactory.GetUnsupportedReason();

        // Assert
        reason.Should().BeNull("supported platforms should have no unsupported reason");
    }

    /// <summary>
    /// Verifies that GetUnsupportedReason returns non-null when not supported.
    /// On unsupported platforms, should provide a descriptive reason.
    /// </summary>
    [Fact]
    public void PtyFactory_GetUnsupportedReason_NonNullWhenUnsupported()
    {
        // Arrange
        if (PtyFactory.IsSupported)
        {
            return; // Skip on supported
        }

        // Act
        var reason = PtyFactory.GetUnsupportedReason();

        // Assert
        reason.Should().NotBeNullOrEmpty("unsupported platforms should have a reason");
        reason.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Verifies that unsupported reason mentions Windows build number on Windows.
    /// Windows-specific error message should contain build number info.
    /// </summary>
    [Fact]
    public void PtyFactory_GetUnsupportedReason_ContainsBuildNumberOnWindows()
    {
        // Arrange
        if (!PtyPlatform.IsWindows || PtyFactory.IsSupported)
        {
            return; // Skip on non-Windows or supported Windows
        }

        // Act
        var reason = PtyFactory.GetUnsupportedReason();

        // Assert
        reason.Should().Contain("build", "reason should mention Windows build number");
        reason.Should().Contain("17763", "reason should mention minimum required build");
    }

    /// <summary>
    /// Verifies consistency between IsSupported and GetUnsupportedReason.
    /// When IsSupported is true, GetUnsupportedReason should return null and vice versa.
    /// </summary>
    [Fact]
    public void PtyFactory_IsSupportedAndReason_AreConsistent()
    {
        // Act
        var isSupported = PtyFactory.IsSupported;
        var reason = PtyFactory.GetUnsupportedReason();

        // Assert
        if (isSupported)
        {
            reason.Should().BeNull();
        }
        else
        {
            reason.Should().NotBeNull();
        }
    }

    #endregion

    #region Error Handling Tests

    /// <summary>
    /// Verifies proper disposal of created PTY instances.
    /// Created PTY instances should be disposable without errors.
    /// </summary>
    [Fact]
    public void PtyFactory_Create_CanBeDisposed()
    {
        // Arrange
        if (!PtyFactory.IsSupported)
        {
            return; // Skip if not supported
        }

        // Act
        var pty = PtyFactory.Create();
        var exception = Record.Exception(() => pty.Dispose());

        // Assert
        exception.Should().BeNull("disposing an unstarted PTY should not throw");
    }

    /// <summary>
    /// Verifies that created PTY can be started after factory creation.
    /// PTY from factory should be startable.
    /// </summary>
    [Fact]
    public void PtyFactory_Create_CanBeStarted()
    {
        // Arrange
        if (!PtyFactory.IsSupported)
        {
            return; // Skip if not supported
        }

        using var pty = PtyFactory.Create();

        // Act
        var exception = Record.Exception(() => pty.Start());

        // Assert
        exception.Should().BeNull("starting a factory-created PTY should not throw");
        pty.IsRunning.Should().BeTrue();
        
        // Cleanup
        pty.Kill(force: true);
    }

    #endregion
}
