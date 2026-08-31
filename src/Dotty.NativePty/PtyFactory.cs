using System;
using Dotty.Abstractions.Pty;

namespace Dotty.NativePty;

/// <summary>
/// Factory for creating platform-specific PTY implementations.
/// </summary>
public static class PtyFactory
{
    /// <summary>
    /// Creates a new PTY instance appropriate for the current platform.
    /// </summary>
    /// <returns>A platform-specific IPty implementation.</returns>
    /// <exception cref="PlatformNotSupportedException">Thrown when the current platform is not supported.</exception>
    public static IPty Create()
    {
        if (!PtyPlatform.IsSupportedArchitecture)
        {
            throw new PlatformNotSupportedException(
                $"Architecture '{PtyPlatform.ProcessArchitecture}' is not supported for runtime '{PtyPlatform.RuntimeIdentifier}'.");
        }
        if (PtyPlatform.IsWindows)
        {
#if WINDOWS
            if (PtyPlatform.IsConPtySupported)
            {
                return new Windows.WindowsPty();
            }
            throw new PlatformNotSupportedException(
                "Windows ConPTY requires Windows 10 version 1809 (build 17763) or later. " +
                "Please upgrade your Windows version to use Dotty.");
#else
            throw new PlatformNotSupportedException(
                "Windows support is not enabled in this build. " +
                "Please build Dotty with Windows target support.");
#endif
        }
        else if (PtyPlatform.IsUnix)
        {
            return new Unix.UnixPty();
        }
        
        throw new PlatformNotSupportedException(
            $"Platform '{System.Runtime.InteropServices.RuntimeInformation.OSDescription}' is not supported. " +
            "Dotty supports Windows 10+ (with ConPTY), Linux, and macOS.");
    }

    /// <summary>
    /// Creates a new PTY instance and starts it with the specified shell.
    /// This is a convenience method that combines Create() and Start().
    /// </summary>
    /// <param name="shell">The shell executable path. If null, uses platform default.</param>
    /// <param name="columns">Initial terminal width in columns.</param>
    /// <param name="rows">Initial terminal height in rows.</param>
    /// <param name="workingDirectory">Optional working directory for the process.</param>
    /// <param name="environmentVariables">Optional additional environment variables.</param>
    /// <returns>A started IPty instance.</returns>
    public static IPty CreateAndStart(
        string? shell = null, 
        int columns = 80, 
        int rows = 24,
        string? workingDirectory = null,
        System.Collections.Generic.IDictionary<string, string>? environmentVariables = null)
    {
        var pty = Create();
        pty.Start(shell, columns, rows, workingDirectory, environmentVariables);
        return pty;
    }

    /// <summary>
    /// Gets a value indicating whether PTY is supported on the current platform.
    /// </summary>
    public static bool IsSupported
    {
        get
        {
            try
            {
                return GetCapabilities().IsSupported;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Gets a description of why PTY might not be supported on this platform.
    /// Returns null if PTY is supported.
    /// </summary>
    public static string? GetUnsupportedReason()
    {
        var capabilities = GetCapabilities();
        return capabilities.IsSupported
            ? null
            : capabilities.Diagnostic ?? "The current platform cannot create a PTY.";
    }
    public static PtyCapabilities GetCapabilities()
    {
        var runtime = PtyPlatform.RuntimeIdentifier;
        var architecture = PtyPlatform.ProcessArchitecture;
        if (!PtyPlatform.IsSupportedArchitecture)
        {
            return new PtyCapabilities(
                runtime,
                architecture,
                PtyBackend.None,
                PlatformSupported: false,
                NativeDependencyAvailable: false,
                $"Architecture '{architecture}' is not supported for runtime '{runtime}'.");
        }

        if (PtyPlatform.IsWindows)
        {
#if WINDOWS
            bool conPty = PtyPlatform.IsConPtySupported;
            return new PtyCapabilities(
                runtime,
                architecture,
                PtyBackend.ConPty,
                PlatformSupported: true,
                NativeDependencyAvailable: conPty,
                conPty
                    ? null
                    : "Windows ConPTY requires build 17763 or newer.");
#else
            return new PtyCapabilities(
                runtime,
                architecture,
                PtyBackend.ConPty,
                PlatformSupported: true,
                NativeDependencyAvailable: false,
                "Windows support is not enabled in this build.");
#endif
        }

        if (PtyPlatform.IsUnix)
        {
            string? helper = Unix.UnixPty.FindHelperExecutableForCurrentProcess();
            if (helper == null)
            {
                return new PtyCapabilities(
                    runtime,
                    architecture,
                    PtyBackend.UnixHelper,
                    PlatformSupported: true,
                    NativeDependencyAvailable: false,
                    "The packaged pty-helper executable was not found.");
            }

            bool executable = Unix.UnixPty.IsExecutable(helper);
            return new PtyCapabilities(
                runtime,
                architecture,
                PtyBackend.UnixHelper,
                PlatformSupported: true,
                NativeDependencyAvailable: executable,
                executable ? null : $"The pty-helper at '{helper}' is not executable.");
        }

        return new PtyCapabilities(
            runtime,
            architecture,
            PtyBackend.None,
            PlatformSupported: false,
            NativeDependencyAvailable: false,
            $"Platform '{System.Runtime.InteropServices.RuntimeInformation.OSDescription}' is not supported.");
    }
}
 
