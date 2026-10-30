using System;
using System.IO;

namespace Dotty.Runtime.Config;

/// <summary>Operating-system families with distinct path conventions.</summary>
public enum HostPlatform
{
    Linux,
    MacOS,
    Windows,
}

/// <summary>Known folders of the current user, as reported by the OS.</summary>
/// <param name="Home">User profile (home) directory.</param>
/// <param name="ApplicationData">Windows roaming application data (<c>%APPDATA%</c>); empty when unknown.</param>
/// <param name="LocalApplicationData">Windows local application data (<c>%LOCALAPPDATA%</c>); empty when unknown.</param>
public readonly record struct PlatformFolders(string Home, string? ApplicationData, string? LocalApplicationData);

/// <summary>Resolved per-user directories.</summary>
/// <param name="ConfigRoot">Holds <c>config.json</c>, <c>config.lua</c>/<c>init.lua</c> and <c>themes/</c>.</param>
/// <param name="StateRoot">Machine-local state Dotty writes itself, such as crash logs; never read as configuration.</param>
public sealed record PlatformPathSet(string ConfigRoot, string StateRoot);

/// <summary>
/// Resolves user-writable application paths using the conventions of the host OS.
/// DOTTY_CONFIG_HOME overrides the platform default for portable deployments and tests.
/// </summary>
public static class PlatformPaths
{
    public const string ConfigHomeVariable = "DOTTY_CONFIG_HOME";

    /// <summary>Paths for the running process; re-resolved on every call so environment changes apply.</summary>
    public static PlatformPathSet Current => Resolve(
        CurrentPlatform,
        Environment.GetEnvironmentVariable,
        new PlatformFolders(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
        Directory.Exists);

    public static HostPlatform CurrentPlatform =>
        OperatingSystem.IsWindows() ? HostPlatform.Windows
        : OperatingSystem.IsMacOS() ? HostPlatform.MacOS
        : HostPlatform.Linux;

    public static string ConfigRoot => Current.ConfigRoot;
    public static string StateRoot => Current.StateRoot;
    public static string ConfigFile => Path.Combine(ConfigRoot, "config.json");
    public static string LuaDirectory => ConfigRoot;
    public static string ThemesDirectory => Path.Combine(ConfigRoot, "themes");

    /// <summary>
    /// Pure path resolution. Configuration:
    /// <list type="bullet">
    /// <item><c>DOTTY_CONFIG_HOME</c> (any OS) wins, made absolute.</item>
    /// <item>Linux: <c>$XDG_CONFIG_HOME/dotty</c> when the variable is absolute, else <c>~/.config/dotty</c>.</item>
    /// <item>macOS: that same XDG directory when it already exists, else <c>~/Library/Application Support/Dotty</c>.</item>
    /// <item>Windows: <c>%APPDATA%\Dotty</c>, else <c>&lt;profile&gt;\AppData\Roaming\Dotty</c>.</item>
    /// </list>
    /// State: Linux <c>$XDG_STATE_HOME/dotty</c> (absolute only) else <c>~/.local/state/dotty</c>;
    /// macOS <c>~/Library/Application Support/Dotty</c>; Windows <c>%LOCALAPPDATA%\Dotty</c>, else
    /// <c>&lt;profile&gt;\AppData\Local\Dotty</c>. Relative XDG values are ignored, as the XDG Base Directory
    /// specification requires. Paths are joined with the separator of <paramref name="platform"/>.
    /// </summary>
    public static PlatformPathSet Resolve(
        HostPlatform platform,
        Func<string, string?> environment,
        PlatformFolders folders,
        Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(directoryExists);

        char separator = platform == HostPlatform.Windows ? '\\' : '/';
        string? overridePath = environment(ConfigHomeVariable);
        string configRoot = !string.IsNullOrWhiteSpace(overridePath)
            ? Path.GetFullPath(overridePath)
            : ResolveConfigRoot(platform, environment, folders, directoryExists, separator);
        return new PlatformPathSet(configRoot, ResolveStateRoot(platform, environment, folders, separator));
    }

    private static string ResolveConfigRoot(
        HostPlatform platform,
        Func<string, string?> environment,
        PlatformFolders folders,
        Func<string, bool> directoryExists,
        char separator)
    {
        switch (platform)
        {
            case HostPlatform.Windows:
                return string.IsNullOrWhiteSpace(folders.ApplicationData)
                    ? Join(separator, folders.Home, "AppData", "Roaming", "Dotty")
                    : Join(separator, folders.ApplicationData, "Dotty");
            case HostPlatform.MacOS:
                string xdg = XdgDirectory(environment, "XDG_CONFIG_HOME", folders.Home, ".config", separator);
                return directoryExists(xdg)
                    ? xdg
                    : Join(separator, folders.Home, "Library", "Application Support", "Dotty");
            default:
                return XdgDirectory(environment, "XDG_CONFIG_HOME", folders.Home, ".config", separator);
        }
    }

    private static string ResolveStateRoot(
        HostPlatform platform,
        Func<string, string?> environment,
        PlatformFolders folders,
        char separator) => platform switch
        {
            HostPlatform.Windows => string.IsNullOrWhiteSpace(folders.LocalApplicationData)
                ? Join(separator, folders.Home, "AppData", "Local", "Dotty")
                : Join(separator, folders.LocalApplicationData, "Dotty"),
            HostPlatform.MacOS => Join(separator, folders.Home, "Library", "Application Support", "Dotty"),
            _ => XdgDirectory(environment, "XDG_STATE_HOME", folders.Home, Join(separator, ".local", "state"), separator),
        };

    /// <summary><c>$variable/dotty</c> when the variable holds an absolute path, else <c>~/fallback/dotty</c>.</summary>
    private static string XdgDirectory(
        Func<string, string?> environment,
        string variable,
        string home,
        string homeRelativeFallback,
        char separator)
    {
        string? value = environment(variable);
        return !string.IsNullOrEmpty(value) && value[0] == '/'
            ? Join(separator, value, "dotty")
            : Join(separator, home, homeRelativeFallback, "dotty");
    }

    private static string Join(char separator, params ReadOnlySpan<string> parts)
    {
        string result = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            result = result.Length > 0 && (result[^1] == separator || result[^1] == '/')
                ? result + parts[i]
                : result + separator + parts[i];
        }
        return result;
    }
}
