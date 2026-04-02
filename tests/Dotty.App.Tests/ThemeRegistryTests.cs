using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Dotty.Abstractions.Config;
using Dotty.Abstractions.Themes;
using Dotty.App.Services;

namespace Dotty.App.Tests;

/// <summary>
/// Integration tests for the ThemeRegistry class.
/// Tests theme registration, retrieval, and thread-safety.
/// </summary>
public class ThemeRegistryTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly UserThemeLoader _userThemeLoader;

    public ThemeRegistryTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"dotty_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDirectory);
        _userThemeLoader = new UserThemeLoader(_tempDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }

    [Fact]
    public void ThemeRegistry_ContainsAllBuiltInThemes()
    {
        // Arrange
        var registry = new ThemeRegistry(new UserThemeLoader("/nonexistent"));

        // Act
        var builtInThemes = registry.GetBuiltInThemes();

        // Assert
        Assert.True(builtInThemes.Count >= 10, $"Expected at least 10 built-in themes, found {builtInThemes.Count}");
        Assert.Contains(builtInThemes, t => t.Background == 0xFF1E1E1E); // DarkPlus background
        Assert.Contains(builtInThemes, t => t.Background == 0xFFFFFFFF); // LightPlus background
    }

    [Fact]
    public void ThemeRegistry_UserThemeOverridesBuiltIn()
    {
        // Arrange - create a user theme with same name as built-in but different colors
        var customThemeJson = """
            {
                "canonicalName": "DarkPlus",
                "displayName": "Custom DarkPlus",
                "colors": {
                    "background": "#FF0000",
                    "foreground": "#00FF00",
                    "opacity": 1.0,
                    "ansi": [
                        "#000000", "#CD3131", "#0DBC79", "#E5E510",
                        "#2472C8", "#BC3FBC", "#11A8CD", "#E5E5E5",
                        "#666666", "#F14C4C", "#23D18B", "#F5F543",
                        "#3B8EEA", "#D670D6", "#29B8DB", "#FFFFFF"
                    ]
                }
            }
            """;

        File.WriteAllText(Path.Combine(_tempDirectory, "CustomDarkPlus.json"), customThemeJson);

        // Act
        var registry = new ThemeRegistry(_userThemeLoader);

        // Assert - user theme should override built-in
        var darkPlus = registry.GetByName("DarkPlus");
        Assert.NotNull(darkPlus);
        Assert.Equal(0xFFFF0000u, darkPlus.Background); // Custom background
        Assert.Equal(0xFF00FF00u, darkPlus.Foreground); // Custom foreground
        Assert.True(registry.IsUserTheme("DarkPlus"), "DarkPlus should be recognized as user theme");
    }

    [Fact]
    public void ThemeRegistry_GetByName_FallsBackToDarkPlus()
    {
        // Arrange
        var registry = new ThemeRegistry(new UserThemeLoader("/nonexistent"));

        // Act
        var theme = registry.GetByNameOrDefault("NonExistentTheme", null);

        // Assert
        Assert.NotNull(theme);
        Assert.Equal(0xFF1E1E1Eu, theme.Background); // Should fallback to DarkPlus
    }

    [Fact]
    public void ThemeRegistry_IsThreadSafe()
    {
        // Arrange
        var registry = new ThemeRegistry(new UserThemeLoader("/nonexistent"));
        var themes = new List<IColorScheme>();
        var tasks = new List<Task>();

        // Act - spawn multiple threads accessing themes
        for (int i = 0; i < 50; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                var theme = registry.GetByName("DarkPlus");
                lock (themes)
                {
                    themes.Add(theme!);
                }
            }));
        }

        Task.WaitAll(tasks.ToArray());

        // Assert - all threads should get valid theme
        Assert.Equal(50, themes.Count);
        Assert.All(themes, t => Assert.NotNull(t));
        Assert.All(themes, t => Assert.Equal(0xFF1E1E1Eu, t.Background));
    }

    [Fact]
    public void ThemeRegistry_Refresh_ReloadsUserThemes()
    {
        // Arrange
        var registry = new ThemeRegistry(_userThemeLoader);
        var initialCount = registry.GetUserThemes().Count;

        // Add a new theme after registry creation
        var newThemeJson = """
            {
                "canonicalName": "NewTestTheme",
                "displayName": "New Test Theme",
                "colors": {
                    "background": "#123456",
                    "foreground": "#ABCDEF",
                    "opacity": 0.9,
                    "ansi": [
                        "#000000", "#CD3131", "#0DBC79", "#E5E510",
                        "#2472C8", "#BC3FBC", "#11A8CD", "#E5E5E5",
                        "#666666", "#F14C4C", "#23D18B", "#F5F543",
                        "#3B8EEA", "#D670D6", "#29B8DB", "#FFFFFF"
                    ]
                }
            }
            """;

        File.WriteAllText(Path.Combine(_tempDirectory, "NewTheme.json"), newThemeJson);

        // Act
        registry.Refresh();

        // Assert
        var userThemes = registry.GetUserThemes();
        Assert.True(userThemes.Count > initialCount, "Should have more themes after refresh");
        var newTheme = registry.GetByName("NewTestTheme");
        Assert.NotNull(newTheme);
        Assert.Equal(0xFF123456u, newTheme.Background);
    }

    [Theory]
    [InlineData("DarkPlus", true)]
    [InlineData("Dracula", true)]
    [InlineData("OneDark", true)]
    [InlineData("GruvboxDark", true)]
    [InlineData("LightPlus", false)]
    [InlineData("OneLight", false)]
    [InlineData("CatppuccinLatte", false)]
    public void ThemeRegistry_DarkThemes_ReturnsOnlyDark(string themeName, bool shouldBeDark)
    {
        // Arrange
        var registry = new ThemeRegistry(new UserThemeLoader("/nonexistent"));
        var darkThemes = registry.GetAllThemes().Where(t => IsDarkTheme(t)).ToList();

        // Act
        var theme = registry.GetByName(themeName);

        // Assert
        Assert.NotNull(theme);
        var isDark = IsDarkTheme(theme);
        Assert.Equal(shouldBeDark, isDark);
    }

    [Theory]
    [InlineData("LightPlus", true)]
    [InlineData("OneLight", true)]
    [InlineData("DarkPlus", false)]
    [InlineData("Dracula", false)]
    public void ThemeRegistry_LightThemes_ReturnsOnlyLight(string themeName, bool shouldBeLight)
    {
        // Arrange
        var registry = new ThemeRegistry(new UserThemeLoader("/nonexistent"));

        // Act
        var theme = registry.GetByName(themeName);

        // Assert
        Assert.NotNull(theme);
        var isLight = !IsDarkTheme(theme);
        Assert.Equal(shouldBeLight, isLight);
    }

    [Fact]
    public void ThemeRegistry_MergesAliases()
    {
        // Arrange - create theme with aliases
        var themeWithAliases = """
            {
                "canonicalName": "MyCoolTheme",
                "displayName": "My Cool Theme",
                "aliases": ["cool-theme", "ct", "awesome-theme"],
                "colors": {
                    "background": "#123456",
                    "foreground": "#ABCDEF",
                    "opacity": 1.0,
                    "ansi": [
                        "#000000", "#CD3131", "#0DBC79", "#E5E510",
                        "#2472C8", "#BC3FBC", "#11A8CD", "#E5E5E5",
                        "#666666", "#F14C4C", "#23D18B", "#F5F543",
                        "#3B8EEA", "#D670D6", "#29B8DB", "#FFFFFF"
                    ]
                }
            }
            """;

        File.WriteAllText(Path.Combine(_tempDirectory, "AliasedTheme.json"), themeWithAliases);

        // Act
        var registry = new ThemeRegistry(_userThemeLoader);

        // Assert - should be accessible by any alias
        var byCanonical = registry.GetByName("MyCoolTheme");
        var byAlias1 = registry.GetByName("cool-theme");
        var byAlias2 = registry.GetByName("ct");
        var byAlias3 = registry.GetByName("awesome-theme");

        Assert.NotNull(byCanonical);
        Assert.NotNull(byAlias1);
        Assert.NotNull(byAlias2);
        Assert.NotNull(byAlias3);

        // All should point to the same theme
        Assert.Same(byCanonical, byAlias1);
        Assert.Same(byCanonical, byAlias2);
        Assert.Same(byCanonical, byAlias3);
    }

    /// <summary>
    /// Helper method to determine if a theme is dark based on background luminance.
    /// </summary>
    private static bool IsDarkTheme(IColorScheme theme)
    {
        var bg = theme.Background;
        var r = ((bg >> 16) & 0xFF) / 255.0;
        var g = ((bg >> 8) & 0xFF) / 255.0;
        var b = (bg & 0xFF) / 255.0;

        r = r <= 0.03928 ? r / 12.92 : Math.Pow((r + 0.055) / 1.055, 2.4);
        g = g <= 0.03928 ? g / 12.92 : Math.Pow((g + 0.055) / 1.055, 2.4);
        b = b <= 0.03928 ? b / 12.92 : Math.Pow((b + 0.055) / 1.055, 2.4);

        var luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        return luminance < 0.5;
    }
}
