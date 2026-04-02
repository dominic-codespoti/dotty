using System;
using System.IO;
using Xunit;
using Dotty.Abstractions.Config;
using Dotty.Abstractions.Themes;
using Dotty.App.Services;

namespace Dotty.App.Tests;

/// <summary>
/// End-to-end integration tests for the entire theme system.
/// Tests the complete workflow from loading to application.
/// </summary>
public class EndToEndTests : IDisposable
{
    private readonly string _tempDirectory;

    public EndToEndTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"dotty_e2e_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }

    [Theory]
    [InlineData("DarkPlus")]
    [InlineData("Dracula")]
    [InlineData("OneDark")]
    [InlineData("GruvboxDark")]
    [InlineData("LightPlus")]
    [InlineData("OneLight")]
    public void EndToEnd_BuiltInTheme_ResolvesCorrectly(string themeName)
    {
        // Arrange - Full stack: Registry -> Manager
        var registry = new ThemeRegistry(new UserThemeLoader("/nonexistent"));
        var manager = new ThemeManager(registry);

        // Act
        var result = manager.ApplyTheme(themeName);
        var currentTheme = manager.CurrentTheme;

        // Assert
        Assert.True(result, $"Should successfully apply {themeName}");
        Assert.NotNull(currentTheme);

        // Verify specific known values
        var expectedTheme = BuiltInThemes.GetByName(themeName);
        Assert.Equal(expectedTheme.Background, currentTheme.Background);
    }

    [Fact]
    public void EndToEnd_UserTheme_OverridesBuiltIn()
    {
        // Arrange - Create user theme that overrides Dracula
        var customDracula = """
            {
                "canonicalName": "Dracula",
                "displayName": "Custom Dracula",
                "colors": {
                    "background": "#DEADBE",
                    "foreground": "#C0FFEE",
                    "opacity": 0.85,
                    "ansi": [
                        "#000000", "#FF0000", "#00FF00", "#FFFF00",
                        "#0000FF", "#FF00FF", "#00FFFF", "#FFFFFF",
                        "#808080", "#FF8080", "#80FF80", "#FFFF80",
                        "#8080FF", "#FF80FF", "#80FFFF", "#C0C0C0"
                    ]
                }
            }
            """;

        File.WriteAllText(Path.Combine(_tempDirectory, "Dracula.json"), customDracula);

        // Full stack
        var userLoader = new UserThemeLoader(_tempDirectory);
        var registry = new ThemeRegistry(userLoader);
        var manager = new ThemeManager(registry);

        // Act
        manager.ApplyTheme("Dracula");
        var currentTheme = manager.CurrentTheme;

        // Assert - Should be the user theme, not the built-in
        Assert.Equal(0xFFDEADBEu, currentTheme.Background);
        Assert.Equal(0xFFC0FFEEu, currentTheme.Foreground);
        Assert.Equal(85, currentTheme.Opacity);
    }

    [Fact]
    public void EndToEnd_ThemeChange_UpdatesApplication()
    {
        // Arrange
        var registry = new ThemeRegistry(new UserThemeLoader("/nonexistent"));
        var manager = new ThemeManager(registry);
        bool eventReceived = false;
        IColorScheme? capturedTheme = null;

        manager.ThemeChanged += (sender, args) =>
        {
            eventReceived = true;
            capturedTheme = args.NewTheme;
        };

        // Act - Apply DarkPlus first
        manager.ApplyTheme("DarkPlus");
        var theme1 = manager.CurrentTheme;

        // Apply Dracula
        manager.ApplyTheme("Dracula");
        var theme2 = manager.CurrentTheme;

        // Assert
        Assert.True(eventReceived, "ThemeChanged event should have fired");
        Assert.NotNull(capturedTheme);
        Assert.NotEqual(theme1.Background, theme2.Background);
        Assert.Equal(0xFF282A36u, theme2.Background); // Dracula background
    }

    [Fact]
    public void EndToEnd_InvalidTheme_FallsBackToDarkPlus()
    {
        // Arrange - Create registry and manager
        var registry = new ThemeRegistry(new UserThemeLoader("/nonexistent"));
        var manager = new ThemeManager(registry);

        // Apply a valid theme first
        manager.ApplyTheme("Dracula");
        Assert.Equal(0xFF282A36u, manager.CurrentTheme.Background);

        // Act - Try to apply non-existent theme
        var result = manager.ApplyTheme("ThisThemeDoesNotExist");
        var currentTheme = manager.CurrentTheme;

        // Assert - Should fallback to DarkPlus
        Assert.True(result); // Returns true because it applies a fallback
        Assert.Equal(0xFF1E1E1Eu, currentTheme.Background); // DarkPlus background
    }
}
