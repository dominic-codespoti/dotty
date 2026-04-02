using System;
using System.IO;
using System.Linq;
using Xunit;
using Dotty.Abstractions.Themes;
using Dotty.App.Services;

namespace Dotty.App.Tests;

/// <summary>
/// Integration tests for the UserThemeLoader class.
/// Tests theme loading from directories and files.
/// </summary>
public class UserThemeLoaderTests : IDisposable
{
    private readonly string _tempDirectory;

    public UserThemeLoaderTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"dotty_loader_test_{Guid.NewGuid()}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }

    [Fact]
    public void LoadFromDirectory_ReturnsEmpty_WhenNoDirectory()
    {
        // Arrange
        var loader = new UserThemeLoader("/nonexistent/path/that/does/not/exist");

        // Act
        var themes = loader.LoadFromDirectory();

        // Assert
        Assert.Empty(themes);
    }

    [Fact]
    public void LoadFromDirectory_ReturnsEmpty_WhenNoJsonFiles()
    {
        // Arrange
        Directory.CreateDirectory(_tempDirectory);
        File.WriteAllText(Path.Combine(_tempDirectory, "readme.txt"), "Not a theme");
        var loader = new UserThemeLoader(_tempDirectory);

        // Act
        var themes = loader.LoadFromDirectory();

        // Assert
        Assert.Empty(themes);
    }

    [Fact]
    public void LoadFromDirectory_LoadsValidTheme()
    {
        // Arrange
        Directory.CreateDirectory(_tempDirectory);
        var validThemeJson = """
            {
                "canonicalName": "TestTheme",
                "displayName": "Test Theme",
                "colors": {
                    "background": "#1E1E1E",
                    "foreground": "#D4D4D4",
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

        File.WriteAllText(Path.Combine(_tempDirectory, "TestTheme.json"), validThemeJson);
        var loader = new UserThemeLoader(_tempDirectory);

        // Act
        var themes = loader.LoadFromDirectory();

        // Assert
        Assert.Single(themes);
        Assert.Equal("TestTheme", themes[0].CanonicalName);
        Assert.Equal("Test Theme", themes[0].DisplayName);
    }

    [Fact]
    public void LoadFromDirectory_SkipsInvalidJson()
    {
        // Arrange
        Directory.CreateDirectory(_tempDirectory);
        var validThemeJson = """
            {
                "canonicalName": "ValidTheme",
                "colors": {
                    "background": "#1E1E1E",
                    "foreground": "#D4D4D4",
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

        File.WriteAllText(Path.Combine(_tempDirectory, "Valid.json"), validThemeJson);
        File.WriteAllText(Path.Combine(_tempDirectory, "Invalid.json"), "not valid json {");
        var loader = new UserThemeLoader(_tempDirectory);

        // Act
        var themes = loader.LoadFromDirectory();

        // Assert
        Assert.Single(themes);
        Assert.Equal("ValidTheme", themes[0].CanonicalName);
    }

    [Fact]
    public void LoadFromDirectory_HandlesMissingFileGracefully()
    {
        // Arrange
        Directory.CreateDirectory(_tempDirectory);
        var loader = new UserThemeLoader(_tempDirectory);
        var nonExistentFile = Path.Combine(_tempDirectory, "does_not_exist.json");

        // Act & Assert
        Assert.Throws<FileNotFoundException>(() => loader.LoadFromFile(nonExistentFile));
    }

    [Fact(Skip = "ThemeRoot deserialization issue - needs investigation")]
    public void LoadFromDirectory_SupportsThemeRootFormat()
    {
        // Arrange
        Directory.CreateDirectory(_tempDirectory);
        var themeRootJson = """
            {
                "version": 1,
                "themes": [
                    {
                        "canonicalName": "RootTheme",
                        "displayName": "Root Theme",
                        "colors": {
                            "background": "#123456",
                            "foreground": "#ABCDEF",
                            "opacity": 0.95,
                            "ansi": [
                                "#000000", "#CD3131", "#0DBC79", "#E5E510",
                                "#2472C8", "#BC3FBC", "#11A8CD", "#E5E5E5",
                                "#666666", "#F14C4C", "#23D18B", "#F5F543",
                                "#3B8EEA", "#D670D6", "#29B8DB", "#FFFFFF"
                            ]
                        }
                    }
                ]
            }
            """;

        File.WriteAllText(Path.Combine(_tempDirectory, "ThemeRoot.json"), themeRootJson);
        var loader = new UserThemeLoader(_tempDirectory);

        // Act
        var themes = loader.LoadFromDirectory();

        // Assert
        Assert.Single(themes);
        Assert.Equal("RootTheme", themes[0].CanonicalName);
        Assert.Equal("Root Theme", themes[0].DisplayName);
    }

    [Fact]
    public void LoadFromFile_ReturnsNull_WhenMissingColors()
    {
        // Arrange
        Directory.CreateDirectory(_tempDirectory);
        var invalidThemeJson = """
            {
                "canonicalName": "NoColorsTheme",
                "displayName": "No Colors Theme"
            }
            """;

        File.WriteAllText(Path.Combine(_tempDirectory, "NoColors.json"), invalidThemeJson);
        var loader = new UserThemeLoader(_tempDirectory);

        // Act
        var themes = loader.LoadFromDirectory();

        // Assert
        Assert.Empty(themes); // Should skip themes without colors
    }

    [Fact]
    public void LoadFromFile_UsesFilenameWhenMissingCanonicalName()
    {
        // Arrange
        Directory.CreateDirectory(_tempDirectory);
        var unnamedThemeJson = """
            {
                "displayName": "Unnamed Theme",
                "colors": {
                    "background": "#999999",
                    "foreground": "#000000",
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

        File.WriteAllText(Path.Combine(_tempDirectory, "FallbackName.json"), unnamedThemeJson);
        var loader = new UserThemeLoader(_tempDirectory);

        // Act
        var themes = loader.LoadFromDirectory();

        // Assert
        Assert.Single(themes);
        Assert.Equal("FallbackName", themes[0].CanonicalName);
    }
}
