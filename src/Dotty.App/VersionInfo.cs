namespace Dotty;

/// <summary>
/// Centralized version information for Dotty Terminal.
/// This file is manually kept in sync with Directory.Build.props.
/// When updating the version, update both files to maintain consistency.
/// </summary>
public static class VersionInfo
{
    /// <summary>
    /// The current version of Dotty Terminal (e.g., "0.1.0")
    /// </summary>
    public const string Version = "0.1.0";
    
    /// <summary>
    /// The NuGet package version for Dotty.Abstractions (should match Version)
    /// </summary>
    public const string NuGetPackageVersion = "0.1.0";
    
    /// <summary>
    /// The assembly version (4-part version number)
    /// </summary>
    public const string AssemblyVersion = "0.1.0.0";
    
    /// <summary>
    /// The file version (4-part version number)
    /// </summary>
    public const string FileVersion = "0.1.0.0";
    
    /// <summary>
    /// The informational version (can include build metadata)
    /// </summary>
    public const string InformationalVersion = "0.1.0";
    
    /// <summary>
    /// Gets the full version string including name.
    /// </summary>
    public static string GetVersionString() => $"Dotty Terminal v{Version}";
    
    /// <summary>
    /// Gets detailed version information for --version output.
    /// </summary>
    public static string GetDetailedVersionString()
    {
        return $""""
Dotty Terminal v{Version}
Configuration Package: v{NuGetPackageVersion}
Assembly: v{AssemblyVersion}
"""";
    }
}
