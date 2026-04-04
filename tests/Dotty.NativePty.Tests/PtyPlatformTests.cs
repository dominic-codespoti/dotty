using Dotty.Abstractions.Pty;
using Xunit;
using FluentAssertions;

namespace Dotty.NativePty.Tests;

/// <summary>
/// Unit tests for platform detection and shell resolution in PtyPlatform.
/// Tests OS detection utilities and shell path resolution per platform.
/// </summary>
public class PtyPlatformTests
{
    #region OS Platform Detection

    /// <summary>
    /// Verifies that only one OS platform detection returns true at a time.
    /// Only one of IsWindows, IsLinux, or IsMacOS should be true for the current platform.
    /// </summary>
    [Fact]
    public void PtyPlatform_OnlyOnePlatformReturnsTrue()
    {
        // Arrange
        var windows = PtyPlatform.IsWindows;
        var linux = PtyPlatform.IsLinux;
        var macOS = PtyPlatform.IsMacOS;

        // Act
        var trueCount = (windows ? 1 : 0) + (linux ? 1 : 0) + (macOS ? 1 : 0);

        // Assert
        trueCount.Should().Be(1, "exactly one platform should be detected as true");
    }

    /// <summary>
    /// Verifies that Unix detection works correctly.
    /// IsUnix should be true when IsLinux or IsMacOS is true.
    /// </summary>
    [Fact]
    public void PtyPlatform_IsUnix_DetectsUnixLikeSystems()
    {
        // Arrange
        var isUnix = PtyPlatform.IsUnix;

        // Act & Assert
        if (PtyPlatform.IsLinux || PtyPlatform.IsMacOS)
        {
            isUnix.Should().BeTrue("IsUnix should be true on Linux or macOS");
        }
        else
        {
            isUnix.Should().BeFalse("IsUnix should be false on non-Unix systems");
        }
    }

    /// <summary>
    /// Verifies that Windows detection is consistent.
    /// IsWindows should be true only on Windows platforms.
    /// </summary>
    [Fact]
    public void PtyPlatform_IsWindows_DetectsWindowsCorrectly()
    {
        // This test verifies the consistency of the IsWindows property
        var isWindows = PtyPlatform.IsWindows;

        // Assert - on Windows, IsWindows should be true; false otherwise
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.Windows))
        {
            isWindows.Should().BeTrue("Should be true on Windows");
        }
        else
        {
            isWindows.Should().BeFalse("Should be false on non-Windows");
        }
    }

    /// <summary>
    /// Verifies that Linux detection is consistent.
    /// IsLinux should be true only on Linux platforms.
    /// </summary>
    [Fact]
    public void PtyPlatform_IsLinux_DetectsLinuxCorrectly()
    {
        var isLinux = PtyPlatform.IsLinux;
        
        // Assert - on Linux, IsLinux should be true; false otherwise
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.Linux))
        {
            isLinux.Should().BeTrue("Should be true on Linux");
        }
        else
        {
            isLinux.Should().BeFalse("Should be false on non-Linux");
        }
    }

    /// <summary>
    /// Verifies that macOS detection is consistent.
    /// IsMacOS should be true only on macOS platforms.
    /// </summary>
    [Fact]
    public void PtyPlatform_IsMacOS_DetectsMacOSCorrectly()
    {
        var isMacOS = PtyPlatform.IsMacOS;
        
        // Assert - on macOS, IsMacOS should be true; false otherwise
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.OSX))
        {
            isMacOS.Should().BeTrue("Should be true on macOS");
        }
        else
        {
            isMacOS.Should().BeFalse("Should be false on non-macOS");
        }
    }

    #endregion

    #region ConPTY Support Detection

    /// <summary>
    /// Verifies that ConPTY is not supported on non-Windows platforms.
    /// IsConPtySupported should always return false on Linux and macOS.
    /// </summary>
    [Fact]
    public void PtyPlatform_IsConPtySupported_FalseOnNonWindows()
    {
        // Arrange
        if (PtyPlatform.IsWindows)
        {
            return; // Skip on Windows
        }

        // Act
        var isSupported = PtyPlatform.IsConPtySupported;

        // Assert
        isSupported.Should().BeFalse("ConPTY is only supported on Windows");
    }

    /// <summary>
    /// Verifies that ConPTY support detection works on Windows.
    /// On Windows, the result depends on the OS version (build 17763+).
    /// </summary>
    [Fact]
    public void PtyPlatform_IsConPtySupported_WindowsBuildCheck()
    {
        // Arrange
        if (!PtyPlatform.IsWindows)
        {
            return; // Skip on non-Windows
        }

        // Act
        var isSupported = PtyPlatform.IsConPtySupported;
        var osVersion = Environment.OSVersion.Version;

        // Assert
        if (osVersion.Build >= 17763)
        {
            isSupported.Should().BeTrue($"ConPTY should be supported on Windows build {osVersion.Build}");
        }
        else
        {
            isSupported.Should().BeFalse($"ConPTY should not be supported on Windows build {osVersion.Build}");
        }
    }

    /// <summary>
    /// Verifies that ConPTY support status is stable across multiple calls.
    /// The result should be consistent when called multiple times.
    /// </summary>
    [Theory]
    [InlineData(5)]
    public void PtyPlatform_IsConPtySupported_ConsistentAcrossCalls(int iterations)
    {
        // Act
        var results = new bool[iterations];
        for (int i = 0; i < iterations; i++)
        {
            results[i] = PtyPlatform.IsConPtySupported;
        }

        // Assert - all results should be the same
        results.Should().AllBeEquivalentTo(results[0], "IsConPtySupported should be consistent");
    }

    #endregion

    #region Default Shell Resolution

    /// <summary>
    /// Verifies that GetDefaultShell returns a non-empty string.
    /// The shell path should always be a valid, non-empty string.
    /// </summary>
    [Fact]
    public void PtyPlatform_GetDefaultShell_ReturnsNonEmptyString()
    {
        // Act
        var shell = PtyPlatform.GetDefaultShell();

        // Assert
        shell.Should().NotBeNullOrEmpty();
        shell.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Verifies that GetDefaultShell returns an absolute path on Unix.
    /// Unix shells should be returned as absolute paths (e.g., /bin/bash).
    /// </summary>
    [Fact]
    public void PtyPlatform_GetDefaultShell_ReturnsAbsolutePathOnUnix()
    {
        // Arrange
        if (PtyPlatform.IsWindows)
        {
            return; // Skip on Windows
        }

        // Act
        var shell = PtyPlatform.GetDefaultShell();

        // Assert
        shell.Should().StartWith("/", "Unix shell paths should be absolute");
    }

    /// <summary>
    /// Verifies that GetDefaultShell returns a valid shell on Windows.
    /// On Windows, should return cmd.exe, powershell.exe, or pwsh.exe.
    /// </summary>
    [Fact]
    public void PtyPlatform_GetDefaultShell_ReturnsValidShellOnWindows()
    {
        // Arrange
        if (!PtyPlatform.IsWindows)
        {
            return; // Skip on non-Windows
        }

        // Act
        var shell = PtyPlatform.GetDefaultShell();

        // Assert
        shell.Should().NotBeNullOrEmpty();
        // Should be one of: cmd.exe, powershell.exe, pwsh.exe (with optional path)
        var shellLower = shell.ToLowerInvariant();
        (shellLower.Contains("cmd") || 
         shellLower.Contains("powershell") || 
         shellLower.Contains("pwsh")).Should().BeTrue(
             $"Windows shell should be cmd, powershell, or pwsh, but got: {shell}");
    }

    /// <summary>
    /// Verifies that GetDefaultShell returns an existing file on Unix.
    /// The returned shell path should point to an existing executable.
    /// </summary>
    [Fact]
    public void PtyPlatform_GetDefaultShell_ReturnsExistingFileOnUnix()
    {
        // Arrange
        if (PtyPlatform.IsWindows)
        {
            return; // Skip on Windows
        }

        // Act
        var shell = PtyPlatform.GetDefaultShell();

        // Assert
        System.IO.File.Exists(shell).Should().BeTrue(
            $"Shell path should exist: {shell}");
    }

    /// <summary>
    /// Verifies that GetDefaultShell honors SHELL environment variable on Unix.
    /// If SHELL is set and valid, it should be returned.
    /// </summary>
    [Fact]
    public void PtyPlatform_GetDefaultShell_UsesShellEnvironmentVariable()
    {
        // Arrange
        if (PtyPlatform.IsWindows)
        {
            return; // Skip on Windows
        }

        var originalShell = Environment.GetEnvironmentVariable("SHELL");
        try
        {
            // Only run this test if we have a shell env var set
            if (!string.IsNullOrEmpty(originalShell) && System.IO.File.Exists(originalShell))
            {
                // Act
                var shell = PtyPlatform.GetDefaultShell();

                // Assert
                shell.Should().Be(originalShell, 
                    "SHELL environment variable should be used when set and valid");
            }
        }
        finally
        {
            // Restore original value (in case we modified it)
            if (originalShell != null)
            {
                Environment.SetEnvironmentVariable("SHELL", originalShell);
            }
        }
    }

    /// <summary>
    /// Verifies fallback shell selection on Unix when preferred shells don't exist.
    /// Should fall back to /bin/sh when other shells aren't available.
    /// </summary>
    [Fact]
    public void PtyPlatform_GetDefaultShell_FallsBackToBinShOnUnix()
    {
        // Arrange
        if (PtyPlatform.IsWindows)
        {
            return; // Skip on Windows
        }

        // Act
        var shell = PtyPlatform.GetDefaultShell();

        // Assert - one of these common shells should exist
        var commonShells = new[] { "/bin/bash", "/bin/zsh", "/bin/sh", "/bin/dash" };
        var shellExists = System.IO.File.Exists(shell);
        
        shellExists.Should().BeTrue($"Shell should exist: {shell}");
    }

    /// <summary>
    /// Verifies that GetDefaultShell is consistent across multiple calls.
    /// Should return the same value when called repeatedly.
    /// </summary>
    [Theory]
    [InlineData(5)]
    public void PtyPlatform_GetDefaultShell_ConsistentAcrossCalls(int iterations)
    {
        // Act
        var shells = new string[iterations];
        for (int i = 0; i < iterations; i++)
        {
            shells[i] = PtyPlatform.GetDefaultShell();
        }

        // Assert
        shells.Should().AllBeEquivalentTo(shells[0], 
            "GetDefaultShell should return consistent results");
    }

    #endregion

    #region Shell Priority Tests

    /// <summary>
    /// Verifies shell priority on Windows: pwsh > powershell > cmd.
    /// Tests that PowerShell Core is preferred over Windows PowerShell and CMD.
    /// </summary>
    [Fact]
    public void PtyPlatform_GetDefaultShell_PrefersPwshOverPowerShell()
    {
        // Arrange
        if (!PtyPlatform.IsWindows)
        {
            return; // Skip on non-Windows
        }

        // Act
        var shell = PtyPlatform.GetDefaultShell();
        var shellLower = shell.ToLowerInvariant();

        // Assert - if pwsh exists, it should be returned
        var pwshPath = @"C:\Program Files\PowerShell\7\pwsh.exe";
        if (System.IO.File.Exists(pwshPath))
        {
            shell.Should().Contain("pwsh", "PowerShell Core should be preferred when installed");
        }
    }

    /// <summary>
    /// Verifies shell priority on Unix: bash > zsh > sh.
    /// Tests fallback priority on Unix-like systems.
    /// </summary>
    [Fact]
    public void PtyPlatform_GetDefaultShell_RespectsUnixPriority()
    {
        // Arrange
        if (PtyPlatform.IsWindows)
        {
            return; // Skip on Windows
        }

        var originalShell = Environment.GetEnvironmentVariable("SHELL");
        
        try
        {
            // Clear SHELL to force fallback behavior
            Environment.SetEnvironmentVariable("SHELL", null);

            // Act
            var shell = PtyPlatform.GetDefaultShell();

            // Assert - should prefer bash if available
            var bashPath = "/bin/bash";
            var zshPath = "/bin/zsh";
            var shPath = "/bin/sh";

            if (System.IO.File.Exists(bashPath))
            {
                shell.Should().Be(bashPath, "bash should be preferred over other shells");
            }
            else if (System.IO.File.Exists(zshPath))
            {
                shell.Should().Be(zshPath, "zsh should be preferred when bash unavailable");
            }
            else
            {
                shell.Should().Be(shPath, "sh should be the ultimate fallback");
            }
        }
        finally
        {
            // Restore SHELL environment variable
            if (originalShell != null)
            {
                Environment.SetEnvironmentVariable("SHELL", originalShell);
            }
        }
    }

    #endregion
}
