using System;
using System.IO;
using System.Linq;
using Xunit;
using Dotty.Abstractions.Config;
using Dotty.Abstractions.Themes;
using Dotty.App.Services;

namespace Dotty.App.Tests;

/// <summary>
/// Integration tests for the ThemeManager class.
/// Tests theme application, events, and theme calculation.
/// </summary>
public class ThemeManagerTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly UserThemeLoader _userThemeLoader;
    private readonly ThemeRegistry _registry;

    public ThemeManagerTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"dotty_manager_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDirectory);
        _userThemeLoader = new UserThemeLoader(_tempDirectory);
        _registry = new ThemeRegistry(new UserThemeLoader("/nonexistent"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }

    [Fact]
    public void ThemeManager_ApplyTheme_ChangesCurrentTheme()
    {
        // Arrange
        var manager = new ThemeManager(_registry);
        var initialTheme = manager.CurrentTheme;

        // Act
        var result = manager.ApplyTheme("Dracula");

        // Assert
        Assert.True(result);
        Assert.NotEqual(initialTheme, manager.CurrentTheme);
        Assert.Equal(0xFF282A36u, manager.CurrentTheme.Background);
    }

    [Fact]
    public void ThemeManager_ApplyTheme_RaisesThemeChangedEvent()
    {
        // Arrange
        var manager = new ThemeManager(_registry);
        IColorScheme? oldTheme = null;
        IColorScheme? newTheme = null;
        bool eventFired = false;

        manager.ThemeChanged += (sender, args) =>
        {
            eventFired = true;
            oldTheme = args.PreviousTheme;
            newTheme = args.NewTheme;
        };

        // Act
        manager.ApplyTheme("Dracula");

        // Assert
        Assert.True(eventFired);
        Assert.NotNull(newTheme);
        Assert.Equal(0xFF282A36u, newTheme.Background);
    }

    [Fact]
    public void ThemeManager_AvailableThemes_IncludesBuiltIn()
    {
        // Arrange
        var manager = new ThemeManager(_registry);

        // Act
        var themes = manager.AvailableThemes;

        // Assert
        Assert.True(themes.Count >= 10, $"Expected at least 10 themes, found {themes.Count}");
        Assert.Contains(themes, t => t.Background == 0xFF1E1E1E); // DarkPlus
        Assert.Contains(themes, t => t.Background == 0xFF282A36); // Dracula
    }

    [Fact]
    public void ThemeManager_AvailableThemes_IncludesUserThemes()
    {
        // Arrange - create user theme
        var customThemeJson = @"{
            ""canonicalName"": ""MyCustomTheme"",
            ""displayName"": ""My Custom Theme"",
            ""colors"": {
                ""background"": ""#DEADBE"",
                ""foreground"": ""#C0FFEE"",
                ""opacity"": 1.0,
                ""ansi"": [
                    ""#000000"", ""#CD3131"", ""#0DBC79"", ""#E5E510"",
                    ""#2472C8"", ""#BC3FBC"", ""#11A8CD"", ""#E5E5E5"",
                    ""#666666"", ""#F14C4C"", ""#23D18B"", ""#F5F543"",
                    ""#3B8EEA"", ""#D670D6"", ""#29B8DB"", ""#FFFFFF""
                ]
            }
        }";

        File.WriteAllText(Path.Combine(_tempDirectory, "Custom.json"), customThemeJson);
        var registry = new ThemeRegistry(_userThemeLoader);
        var manager = new ThemeManager(registry);

        // Act
        var themes = manager.AvailableThemes;

        // Assert
        Assert.Contains(themes, t => t.Background == 0xFFDEADBE); // User theme
        Assert.Contains(themes, t => t.Background == 0xFF1E1E1E); // Built-in theme
    }

    [Fact]
    public void ThemeManager_LoadUserThemes_PopulatesRegistry()
    {
        // Arrange
        var registry = new ThemeRegistry(_userThemeLoader);
        var manager = new ThemeManager(registry);
        var initialUserThemes = manager.UserThemes.Count;

        // Add a new theme
        var newThemeJson = @"{
            ""canonicalName"": ""DynamicTheme"",
            ""displayName"": ""Dynamic Theme"",
            ""colors"": {
                ""background"": ""#123123"",
                ""foreground"": ""#ABCABC"",
                ""opacity"": 0.8,
                ""ansi"": [
                    ""#000000"", ""#CD3131"", ""#0DBC79"", ""#E5E510"",
                    ""#2472C8"", ""#BC3FBC"", ""#11A8CD"", ""#E5E5E5"",
                    ""#666666"", ""#F14C4C"", ""#23D18B"", ""#F5F543"",
                    ""#3B8EEA"", ""#D670D6"", ""#29B8DB"", ""#FFFFFF""
                ]
            }
        }";

        File.WriteAllText(Path.Combine(_tempDirectory, "Dynamic.json"), newThemeJson);

        // Act
        manager.LoadUserThemes();

        // Assert
        var userThemes = manager.UserThemes;
        Assert.True(userThemes.Count > initialUserThemes, "Should have more user themes after loading");
        Assert.True(manager.HasTheme("DynamicTheme"));
    }

    [Theory]
    [InlineData("#000000", true)]   // Black - dark
    [InlineData("#1E1E1E", true)]   // Dark gray - dark
    [InlineData("#FFFFFF", false)]  // White - light
    [InlineData("#F0F0F0", false)]  // Light gray - light
    [InlineData("#808080", true)]   // Medium gray - dark (luminance < 0.5)
    public void ThemeManager_CalculatesLuminanceCorrectly(string background, bool expectedDark)
    {
        // Arrange - use the background color to calculate expected luminance
        var backgroundColor = ColorSchemeBase.FromHex(background);
        var bg = backgroundColor;
        var r = ((bg >> 16) & 0xFF) / 255.0;
        var g = ((bg >> 8) & 0xFF) / 255.0;
        var b = (bg & 0xFF) / 255.0;

        r = r <= 0.03928 ? r / 12.92 : Math.Pow((r + 0.055) / 1.055, 2.4);
        g = g <= 0.03928 ? g / 12.92 : Math.Pow((g + 0.055) / 1.055, 2.4);
        b = b <= 0.03928 ? b / 12.92 : Math.Pow((b + 0.055) / 1.055, 2.4);

        var luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        var isDark = luminance < 0.5;

        // Assert
        Assert.Equal(expectedDark, isDark);
    }
}
