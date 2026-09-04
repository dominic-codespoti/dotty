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
