using System;
using System.IO;
using Xunit;
using Dotty.App.Services;

namespace Dotty.App.Tests;

/// <summary>
/// Integration tests for the ThemeValidator class.
/// Tests theme validation including required fields, formats, and accessibility.
/// </summary>
public class ThemeValidatorTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly ThemeValidator _validator;

    public ThemeValidatorTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"dotty_validator_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDirectory);
        _validator = new ThemeValidator();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }

    [Fact]
    public void ValidateFile_ReturnsValid_ForGoodTheme()
    {
        // Arrange
        var validThemeJson = """
            {
                "canonicalName": "ValidTheme",
                "displayName": "Valid Theme",
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
        var filePath = Path.Combine(_tempDirectory, "ValidTheme.json");
        File.WriteAllText(filePath, validThemeJson);

        // Act
        var result = _validator.ValidateFile(filePath);

        // Assert
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ValidateFile_ReturnsInvalid_WhenMissingCanonicalName()
    {
        // Arrange
        var invalidThemeJson = """
            {
                "displayName": "Theme Without Name",
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
        var filePath = Path.Combine(_tempDirectory, "InvalidTheme.json");
        File.WriteAllText(filePath, invalidThemeJson);

        // Act
        var result = _validator.ValidateFile(filePath);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("canonicalName"));
    }

    [Fact]
    public void ValidateFile_ReturnsInvalid_WhenMissingBackground()
    {
        // Arrange
        var invalidThemeJson = """
            {
                "canonicalName": "NoBackgroundTheme",
                "colors": {
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
        var filePath = Path.Combine(_tempDirectory, "NoBackground.json");
        File.WriteAllText(filePath, invalidThemeJson);

        // Act
        var result = _validator.ValidateFile(filePath);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("background"));
    }

    [Fact]
    public void ValidateFile_ReturnsInvalid_WhenWrongAnsiCount()
    {
        // Arrange - ANSI palette with only 8 colors instead of 16
        var invalidThemeJson = """
            {
                "canonicalName": "BadAnsiTheme",
                "colors": {
                    "background": "#1E1E1E",
                    "foreground": "#D4D4D4",
                    "opacity": 1.0,
                    "ansi": [
                        "#000000", "#CD3131", "#0DBC79", "#E5E510",
                        "#2472C8", "#BC3FBC", "#11A8CD", "#E5E5E5"
                    ]
                }
            }
            """;
        var filePath = Path.Combine(_tempDirectory, "BadAnsi.json");
        File.WriteAllText(filePath, invalidThemeJson);

        // Act
        var result = _validator.ValidateFile(filePath);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("16"));
    }

    [Fact]
    public void ValidateFile_ReturnsInvalid_WhenBadHexFormat()
    {
        // Arrange - invalid hex color format
        var invalidThemeJson = """
            {
                "canonicalName": "BadHexTheme",
                "colors": {
                    "background": "not-a-hex-color",
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
        var filePath = Path.Combine(_tempDirectory, "BadHex.json");
        File.WriteAllText(filePath, invalidThemeJson);

        // Act
        var result = _validator.ValidateFile(filePath);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("hex") || e.Contains("Invalid"));
    }

    [Fact]
    public void ValidateFile_ReturnsInvalid_WhenFileTooLarge()
    {
        // Arrange - create a file larger than 10KB
        var largeContent = new string('x', 11 * 1024); // 11KB of x's
        var filePath = Path.Combine(_tempDirectory, "LargeFile.json");
        File.WriteAllText(filePath, largeContent);

        // Act
        var result = _validator.ValidateFile(filePath);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("size") || e.Contains("exceeds"));
    }

    [Fact]
    public void ValidateFile_Warns_LowContrast()
    {
        // Arrange - theme with low contrast (similar foreground and background)
        var lowContrastTheme = """
            {
                "canonicalName": "LowContrastTheme",
                "displayName": "Low Contrast Theme",
                "colors": {
                    "background": "#1E1E1E",
                    "foreground": "#202020",
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
        var filePath = Path.Combine(_tempDirectory, "LowContrast.json");
        File.WriteAllText(filePath, lowContrastTheme);

        // Act
        var result = _validator.ValidateFile(filePath);

        // Assert - should have warning about low contrast
        Assert.True(result.Warnings.Count > 0, "Should have warnings for low contrast");
        Assert.Contains(result.Warnings, w => w.Contains("contrast") || w.Contains("Low"));
    }

    [Theory]
    [InlineData("0xFF0000", true)]      // 0x prefix 6 chars
    [InlineData("0xFFFF0000", true)]    // 0x prefix 8 chars
    [InlineData("#FF0000", true)]       // Hash prefix 6 chars
    [InlineData("#FFFF0000", true)]     // Hash prefix 8 chars
    [InlineData("FF0000", true)]        // No prefix 6 chars
    [InlineData("FFFF0000", true)]      // No prefix 8 chars
    [InlineData("0Xff0000", false)]     // Uppercase 0X prefix - not supported
    [InlineData("invalid", false)]      // Invalid format
    [InlineData("#GGG", false)]         // Invalid hex chars
    public void ValidateFile_Accepts_0xAndHashPrefixes(string colorValue, bool shouldBeValid)
    {
        // Arrange
        var themeJson = $$"""
            {
                "canonicalName": "HexTestTheme",
                "colors": {
                    "background": "{{colorValue}}",
                    "foreground": "{{colorValue}}",
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
        var filePath = Path.Combine(_tempDirectory, "HexTest.json");
        File.WriteAllText(filePath, themeJson);

        // Act
        var result = _validator.ValidateFile(filePath);

        // Assert
        Assert.Equal(shouldBeValid, result.IsValid);
    }
}
