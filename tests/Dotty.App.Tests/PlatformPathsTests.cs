using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dotty.Runtime.Config;
using Dotty.Runtime.Scripting;
using Dotty.Runtime.Themes;
using Xunit;

namespace Dotty.App.Tests;

public sealed class PlatformPathsTests
{
    private static readonly PlatformFolders LinuxFolders = new("/home/u", "", "");
    private static readonly PlatformFolders MacFolders = new("/Users/u", "", "");

    private static Func<string, string?> Env(params string[] pairs) => name =>
    {
        for (int i = 0; i < pairs.Length; i += 2)
        {
            if (pairs[i] == name)
                return pairs[i + 1];
        }
        return null;
    };

    private static bool NoDirectories(string path) => false;

    [Theory]
    [InlineData(new string[0], "/home/u/.config/dotty", "/home/u/.local/state/dotty")]
    [InlineData(new[] { "XDG_CONFIG_HOME", "/c" }, "/c/dotty", "/home/u/.local/state/dotty")]
    [InlineData(new[] { "XDG_CONFIG_HOME", "/c/" }, "/c/dotty", "/home/u/.local/state/dotty")]
    [InlineData(new[] { "XDG_CONFIG_HOME", "" }, "/home/u/.config/dotty", "/home/u/.local/state/dotty")]
    [InlineData(new[] { "XDG_CONFIG_HOME", "relative/cfg" }, "/home/u/.config/dotty", "/home/u/.local/state/dotty")]
    [InlineData(new[] { "XDG_STATE_HOME", "/s" }, "/home/u/.config/dotty", "/s/dotty")]
    [InlineData(new[] { "XDG_STATE_HOME", "state" }, "/home/u/.config/dotty", "/home/u/.local/state/dotty")]
    public void LinuxFollowsXdgAndIgnoresRelativeValues(string[] environment, string config, string state)
    {
        PlatformPathSet paths = PlatformPaths.Resolve(HostPlatform.Linux, Env(environment), LinuxFolders, NoDirectories);

        Assert.Equal(config, paths.ConfigRoot);
        Assert.Equal(state, paths.StateRoot);
    }

    [Theory]
    [InlineData(new string[0], new string[0], "/Users/u/Library/Application Support/Dotty")]
    [InlineData(new string[0], new[] { "/Users/u/.config/dotty" }, "/Users/u/.config/dotty")]
    [InlineData(new[] { "XDG_CONFIG_HOME", "/x" }, new[] { "/x/dotty" }, "/x/dotty")]
    [InlineData(new[] { "XDG_CONFIG_HOME", "/x" }, new[] { "/Users/u/.config/dotty" }, "/Users/u/Library/Application Support/Dotty")]
    [InlineData(new[] { "XDG_CONFIG_HOME", "rel" }, new[] { "/Users/u/.config/dotty" }, "/Users/u/.config/dotty")]
    public void MacOsPrefersAnExistingXdgDirectory(string[] environment, string[] existing, string config)
    {
        PlatformPathSet paths = PlatformPaths.Resolve(
            HostPlatform.MacOS, Env(environment), MacFolders, path => Array.IndexOf(existing, path) >= 0);

        Assert.Equal(config, paths.ConfigRoot);
        Assert.Equal("/Users/u/Library/Application Support/Dotty", paths.StateRoot);
    }

    [Theory]
    [InlineData(@"C:\Users\u\AppData\Roaming", @"C:\Users\u\AppData\Local", @"C:\Users\u\AppData\Roaming\Dotty", @"C:\Users\u\AppData\Local\Dotty")]
    [InlineData(@"D:\Roam\", @"D:\Local\", @"D:\Roam\Dotty", @"D:\Local\Dotty")]
    [InlineData("", "", @"C:\Users\u\AppData\Roaming\Dotty", @"C:\Users\u\AppData\Local\Dotty")]
    public void WindowsUsesKnownFoldersWithProfileFallback(string roaming, string local, string config, string state)
    {
        PlatformPathSet paths = PlatformPaths.Resolve(
            HostPlatform.Windows, Env("XDG_CONFIG_HOME", "/ignored"), new PlatformFolders(@"C:\Users\u", roaming, local), NoDirectories);

        Assert.Equal(config, paths.ConfigRoot);
        Assert.Equal(state, paths.StateRoot);
    }

    [Theory]
    [InlineData(HostPlatform.Linux)]
    [InlineData(HostPlatform.MacOS)]
    [InlineData(HostPlatform.Windows)]
    public void ConfigHomeOverrideWinsOnEveryPlatformButLeavesStateAlone(HostPlatform platform)
    {
        string overrideRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "dotty-override"));
        PlatformPathSet withOverride = PlatformPaths.Resolve(
            platform, Env("DOTTY_CONFIG_HOME", overrideRoot), LinuxFolders, NoDirectories);
        PlatformPathSet without = PlatformPaths.Resolve(platform, Env(), LinuxFolders, NoDirectories);

        Assert.Equal(overrideRoot, withOverride.ConfigRoot);
        Assert.Equal(without.StateRoot, withOverride.StateRoot);
    }

    [Fact]
    public void FirstLoadDoesNotWriteADefaultConfigFile()
    {
        string? original = Environment.GetEnvironmentVariable("DOTTY_CONFIG_HOME");
        string directory = Path.Combine(Path.GetTempPath(), "dotty-first-run-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("DOTTY_CONFIG_HOME", directory);
            UserConfigService.Shutdown();

            DottyUserConfig loaded = UserConfigService.Load();

            Assert.False(File.Exists(Path.Combine(directory, "config.json")));
            Assert.True(Directory.Exists(directory));
            Assert.Equal(new DottyUserConfig().Theme, loaded.Theme);
            Assert.Null(UserConfigService.LastError);
        }
        finally
        {
            UserConfigService.Shutdown();
            Environment.SetEnvironmentVariable("DOTTY_CONFIG_HOME", original);
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ConfigOverrideIsSharedByConfigLuaAndThemes()
    {
        string? original = Environment.GetEnvironmentVariable("DOTTY_CONFIG_HOME");
        try
        {
            string expectedRoot = Path.GetFullPath(
                Path.Combine(Path.GetTempPath(), "dotty-platform-test"));
            Environment.SetEnvironmentVariable("DOTTY_CONFIG_HOME", expectedRoot);

            Assert.Equal(expectedRoot, PlatformPaths.ConfigRoot);
            Assert.Equal(Path.Combine(expectedRoot, "config.json"), UserConfigService.GetConfigPath());
            Assert.Equal(Path.Combine(expectedRoot, "config.lua"), LuaScriptHost.GetConfigLuaPath());
            Assert.Equal(Path.Combine(expectedRoot, "themes"), UserThemeLoader.DefaultThemesDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTTY_CONFIG_HOME", original);
        }
    }

    [Fact]
    public async Task ConfigWatcherDispatchesReloadOnConfiguredContext()
    {
        string? original = Environment.GetEnvironmentVariable("DOTTY_CONFIG_HOME");
        string directory = Path.Combine(Path.GetTempPath(), "dotty-config-test-" + Guid.NewGuid().ToString("N"));
        Action? queued = null;
        DottyUserConfig? observed = null;
        try
        {
            Environment.SetEnvironmentVariable("DOTTY_CONFIG_HOME", directory);
            UserConfigService.Shutdown();
            UserConfigService.Load();
            UserConfigService.CallbackDispatcher = action => queued = action;
            UserConfigService.ConfigChanged += config => observed = config;

            File.WriteAllText(UserConfigService.GetConfigPath(), "{\"theme\":\"Dracula\"}");

            Assert.True(SpinWait.SpinUntil(() => queued != null, TimeSpan.FromSeconds(3)));
            Assert.Null(observed);
            queued!();
            Assert.NotNull(observed);
            Assert.Equal("Dracula", observed!.Theme);
        }
        finally
        {
            UserConfigService.Shutdown();
            Environment.SetEnvironmentVariable("DOTTY_CONFIG_HOME", original);
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); } catch { }
            await Task.CompletedTask;
        }
    }
}
