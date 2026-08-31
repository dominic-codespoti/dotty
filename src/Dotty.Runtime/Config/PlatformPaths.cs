using System;
using System.IO;

namespace Dotty.Runtime.Config;

/// <summary>
/// Resolves user-writable application paths using the conventions of the host OS.
/// DOTTY_CONFIG_HOME overrides the platform default for portable deployments and tests.
/// </summary>
public static class PlatformPaths
{
    public static string ConfigRoot
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable("DOTTY_CONFIG_HOME");
            if (!string.IsNullOrWhiteSpace(overridePath))
                return Path.GetFullPath(overridePath);

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (OperatingSystem.IsWindows())
            {
                string applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return string.IsNullOrWhiteSpace(applicationData)
                    ? Path.Combine(userProfile, "AppData", "Roaming", "Dotty")
                    : Path.Combine(applicationData, "Dotty");
            }

            if (OperatingSystem.IsMacOS())
                return Path.Combine(userProfile, "Library", "Application Support", "Dotty");

            var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            string configHome = string.IsNullOrWhiteSpace(xdgConfigHome)
                ? Path.Combine(userProfile, ".config")
                : xdgConfigHome;
            return Path.Combine(configHome, "dotty");
        }
    }

    public static string ConfigFile => Path.Combine(ConfigRoot, "config.json");
    public static string LuaDirectory => ConfigRoot;
    public static string ThemesDirectory => Path.Combine(ConfigRoot, "themes");
}
