using System;
using System.IO;
using Dotty.Runtime.Config;
using Dotty.Silk.Config;
using Xunit;

namespace Dotty.App.Tests;

[CollectionDefinition("Silk system theme", DisableParallelization = true)]
public sealed class SilkSystemThemeCollection { }

[Collection("Silk system theme")]
public sealed class SilkSystemThemeTests
{
    [Fact]
    public void ResolverUsesCachedDarkLightAndUnknownPreferenceWithoutProbing()
    {
        var oldProbe = SilkConfig.SystemThemeProbe;
        int probes = 0;
        try
        {
            SilkConfig.SystemThemeProbe = () => true;
            SilkConfig.RefreshSystemTheme();
            SilkConfig.SystemThemeProbe = () => { probes++; return false; };
            var config = new DottyUserConfig { Theme = "Explicit", ThemeAuto = true, ThemeDark = "Dark", ThemeLight = "Light" };

            Assert.Equal("Dark", SilkConfig.ResolveConfiguredThemeName(config));
            Assert.Equal(0, probes);
            Assert.True(SilkConfig.RefreshSystemTheme());
            Assert.Equal(1, probes);
            Assert.Equal("Light", SilkConfig.ResolveConfiguredThemeName(config));
            Assert.Equal(1, probes);
            Assert.False(SilkConfig.RefreshSystemTheme());
            Assert.Equal(2, probes);

            SilkConfig.SystemThemeProbe = () => { probes++; return null; };
            Assert.True(SilkConfig.RefreshSystemTheme());
            Assert.Equal(3, probes);
            Assert.Equal("Dark", SilkConfig.ResolveConfiguredThemeName(config));
            config.ThemeAuto = false;
            Assert.Equal("Explicit", SilkConfig.ResolveConfiguredThemeName(config));
            Assert.Equal(3, probes);
        }
        finally
        {
            bool? previous = SilkConfig.SystemPrefersDark;
            SilkConfig.SystemThemeProbe = () => previous;
            SilkConfig.RefreshSystemTheme();
            SilkConfig.SystemThemeProbe = oldProbe;
        }
    }

    [Fact]
    public void RefreshSystemThemeReportsOnlyPreferenceChanges()
    {
        var oldProbe = SilkConfig.SystemThemeProbe;
        try
        {
            SilkConfig.SystemThemeProbe = () => null;
            SilkConfig.RefreshSystemTheme();
            bool? next = true;
            SilkConfig.SystemThemeProbe = () => next;
            Assert.True(SilkConfig.RefreshSystemTheme());
            Assert.True(SilkConfig.SystemPrefersDark == true);
            Assert.False(SilkConfig.RefreshSystemTheme());
            next = false;
            Assert.True(SilkConfig.RefreshSystemTheme());
            Assert.True(SilkConfig.SystemPrefersDark == false);
        }
        finally
        {
            bool? previous = SilkConfig.SystemPrefersDark;
            SilkConfig.SystemThemeProbe = () => previous;
            SilkConfig.RefreshSystemTheme();
            SilkConfig.SystemThemeProbe = oldProbe;
        }
    }

    [Fact]
    public void ThemeAutoOffUsesExplicitThemeRegardlessOfCachedPreference()
    {
        var oldProbe = SilkConfig.SystemThemeProbe;
        try
        {
            SilkConfig.SystemThemeProbe = () => false;
            SilkConfig.RefreshSystemTheme();
            Assert.Equal("MyTheme", SilkConfig.ResolveConfiguredThemeName(
                new DottyUserConfig { Theme = "MyTheme", ThemeAuto = false, ThemeLight = "Light" }));
        }
        finally
        {
            bool? previous = SilkConfig.SystemPrefersDark;
            SilkConfig.SystemThemeProbe = () => previous;
            SilkConfig.RefreshSystemTheme();
            SilkConfig.SystemThemeProbe = oldProbe;
        }
    }

    [Fact]
    public void LoadActiveThemeDoesNotProbeSystemOrSpawnDetection()
    {
        string? oldConfigHome = Environment.GetEnvironmentVariable("DOTTY_CONFIG_HOME");
        string? oldTheme = Environment.GetEnvironmentVariable("DOTTY_THEME");
        var oldProbe = SilkConfig.SystemThemeProbe;
        string isolatedHome = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        int probes = 0;
        try
        {
            Environment.SetEnvironmentVariable("DOTTY_CONFIG_HOME", isolatedHome);
            Environment.SetEnvironmentVariable("DOTTY_THEME", null);
            UserConfigService.Load();
            SilkConfig.SystemThemeProbe = () => { probes++; return true; };
            UserConfigService.Current.ThemeAuto = true;
            SilkConfig.ClearThemeCache();
            SilkConfig.LoadActiveTheme();
            Assert.Equal(0, probes);
        }
        finally
        {
            SilkConfig.ClearThemeCache();
            bool? previous = SilkConfig.SystemPrefersDark;
            SilkConfig.SystemThemeProbe = () => previous;
            SilkConfig.RefreshSystemTheme();
            SilkConfig.SystemThemeProbe = oldProbe;
            try { if (Directory.Exists(isolatedHome)) Directory.Delete(isolatedHome, recursive: true); } catch { }
            Environment.SetEnvironmentVariable("DOTTY_THEME", oldTheme);
            Environment.SetEnvironmentVariable("DOTTY_CONFIG_HOME", oldConfigHome);
            UserConfigService.Load();
        }
    }
}
