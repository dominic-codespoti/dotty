using System;
using System.Runtime.InteropServices;

namespace Dotty.Abstractions.Pty;

/// <summary>
/// Provides information about the current platform and PTY capabilities.
/// </summary>
public static class PtyPlatform
{
    /// <summary>
    /// Gets a value indicating whether the current platform is Windows.
    /// </summary>
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    
    /// <summary>
    /// Gets a value indicating whether the current platform is Linux.
    /// </summary>
    public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    
    /// <summary>
    /// Gets a value indicating whether the current platform is macOS.
    /// </summary>
    public static bool IsMacOS => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    
    /// <summary>
    /// Gets a value indicating whether the current platform is a Unix-like system (Linux or macOS).
    /// </summary>
    public static bool IsUnix => IsLinux || IsMacOS;
    
    /// <summary>
    /// Gets a value indicating whether ConPTY is supported on this Windows version.
    /// ConPTY requires Windows 10 version 1809 (build 17763) or later.
    /// </summary>
    public static bool IsConPtySupported
    {
        get
        {
            if (!IsWindows) return false;
            
            // ConPTY requires Windows 10 build 17763 (version 1809) or later
            // Windows 10 build numbers: 10240 (1507), 10586 (1511), 14393 (1607), 
            // 15063 (1703), 16299 (1709), 17134 (1803), 17763 (1809), etc.
            var osVersion = Environment.OSVersion.Version;
            return osVersion.Build >= 17763;
        }
    }
    
    /// <summary>
    /// Gets the default shell path for the current platform.
    /// </summary>
    public static string GetDefaultShell()
    {
        if (IsWindows)
        {
            // Prefer Windows PowerShell first on Windows.
            // Its default path avoids spaces and is broadly available.
            var psPath = GetWindowsPowerShellPath();
            if (!string.IsNullOrEmpty(psPath))
                return psPath;

            // Fall back to PowerShell Core when available.
            var pwshPath = GetPowerShellCorePath();
            if (!string.IsNullOrEmpty(pwshPath))
                return pwshPath;
            
            // Last-resort fallback for unusual locked-down environments.
            return Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        }
        
        // Unix-like systems
        var shell = Environment.GetEnvironmentVariable("SHELL");
        if (!string.IsNullOrEmpty(shell) && System.IO.File.Exists(shell))
            return shell;
        
        // Common fallback shells
        string[] fallbackShells = { "/bin/bash", "/bin/zsh", "/bin/sh" };
        foreach (var sh in fallbackShells)
        {
            if (System.IO.File.Exists(sh))
                return sh;
        }
        
        return "/bin/sh";
    }
    
    /// <summary>
    /// Attempts to find PowerShell Core (pwsh.exe) on Windows.
    /// </summary>
    private static string? GetPowerShellCorePath()
    {
        // Check common installation paths
        string[] commonPaths = {
            @"C:\Program Files\PowerShell\7\pwsh.exe",
            @"C:\Program Files\PowerShell\6\pwsh.exe",
        };
        
        foreach (var path in commonPaths)
        {
            if (System.IO.File.Exists(path))
                return path;
        }
        
        // Try to find via PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (var dir in pathEnv.Split(';'))
            {
                var fullPath = System.IO.Path.Combine(dir, "pwsh.exe");
                if (System.IO.File.Exists(fullPath))
                    return fullPath;
            }
        }
        
        return null;
    }
    
    /// <summary>
    /// Attempts to find Windows PowerShell (powershell.exe).
    /// </summary>
    private static string? GetWindowsPowerShellPath()
    {
        var windir = Environment.GetEnvironmentVariable("windir");
        if (!string.IsNullOrEmpty(windir))
        {
            var psPath = System.IO.Path.Combine(windir, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            if (System.IO.File.Exists(psPath))
                return psPath;
        }
        
        return null;
    }
}
