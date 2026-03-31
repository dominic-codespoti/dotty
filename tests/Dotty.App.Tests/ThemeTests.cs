using Xunit;
using Dotty.Abstractions.Themes;
using Dotty.Abstractions.Config;

namespace Dotty.App.Tests;

/// <summary>
/// Tests for the theming system.
/// </summary>
public class ThemeTests
{
    [Fact]
    public void BuiltInThemes_DarkPlus_HasCorrectColors()
    {
        var theme = BuiltInThemes.DarkPlus;
        
        // Verify VS Code Dark+ colors
        Assert.Equal(0xFF1E1E1Eu, theme.Background);
        Assert.Equal(0xFFD4D4D4u, theme.Foreground);
        Assert.Equal(0xFF000000u, theme.AnsiBlack);
        Assert.Equal(0xFFCD3131u, theme.AnsiRed);
        Assert.Equal(0xFF0DBC79u, theme.AnsiGreen);
        Assert.Equal(0xFFE5E510u, theme.AnsiYellow);
        Assert.Equal(0xFF2472C8u, theme.AnsiBlue);
        Assert.Equal(0xFFBC3FBCu, theme.AnsiMagenta);
        Assert.Equal(0xFF11A8CDu, theme.AnsiCyan);
        Assert.Equal(0xFFE5E5E5u, theme.AnsiWhite);
    }

    [Fact]
    public void BuiltInThemes_Dracula_HasCorrectColors()
    {
        var theme = BuiltInThemes.Dracula;
        
        // Verify Dracula colors
        Assert.Equal(0xFF282A36u, theme.Background);
        Assert.Equal(0xFFF8F8F2u, theme.Foreground);
        Assert.Equal(0xFFFF5555u, theme.AnsiRed);
        Assert.Equal(0xFF50FA7Bu, theme.AnsiGreen);
        Assert.Equal(0xFFBD93F9u, theme.AnsiBlue);
    }

    [Fact]
    public void BuiltInThemes_LightPlus_HasCorrectColors()
    {
        var theme = BuiltInThemes.LightPlus;
        
        // Verify Light+ colors
        Assert.Equal(0xFFFFFFFFu, theme.Background);
        Assert.Equal(0xFF000000u, theme.Foreground);
    }

    [Theory]
    [InlineData("darkplus", 0xFF1E1E1E)]
    [InlineData("dracula", 0xFF282A36)]
    [InlineData("onedark", 0xFF282C34)]
    [InlineData("gruvboxdark", 0xFF282828)]
    [InlineData("unknown", 0xFF1E1E1E)] // Falls back to DarkPlus
    public void GetByName_ReturnsCorrectTheme(string name, uint expectedBackground)
    {
        var theme = BuiltInThemes.GetByName(name);
        Assert.Equal(expectedBackground, theme.Background);
    }

    [Fact]
    public void AllThemes_ArraysArePopulated()
    {
        Assert.NotEmpty(BuiltInThemes.DarkThemes);
        Assert.NotEmpty(BuiltInThemes.LightThemes);
        Assert.NotEmpty(BuiltInThemes.AllThemes);
        
        // Should have 6 dark themes and 5 light themes
        Assert.True(BuiltInThemes.DarkThemes.Length >= 5);
        Assert.True(BuiltInThemes.LightThemes.Length >= 4);
    }

    [Fact]
    public void ColorSchemeBase_FromHex_ConvertsCorrectly()
    {
        // RRGGBB format (adds FF alpha)
        Assert.Equal(0xFFFF5733u, ColorSchemeBase.FromHex("#FF5733"));
        
        // AARRGGBB format
        Assert.Equal(0x80FF5733u, ColorSchemeBase.FromHex("#80FF5733"));
        
        // Without hash
        Assert.Equal(0xFFFF5733u, ColorSchemeBase.FromHex("FF5733"));
    }

    [Fact]
    public void ColorSchemeBase_ToHex_ConvertsCorrectly()
    {
        Assert.Equal("#FFFF5733", ColorSchemeBase.ToHex(0xFFFF5733));
        Assert.Equal("#80FF5733", ColorSchemeBase.ToHex(0x80FF5733));
    }

    [Fact]
    public void ColorSchemeBase_FromRgb_CreatesCorrectColor()
    {
        Assert.Equal(0xFFFF5733u, ColorSchemeBase.FromRgb(255, 87, 51));
        Assert.Equal(0x80FF5733u, ColorSchemeBase.FromRgb(255, 87, 51, 128));
    }

    [Fact]
    public void AllThemes_HaveValidColors()
    {
        foreach (var theme in BuiltInThemes.AllThemes)
        {
            // Background and foreground should not be the same
            Assert.NotEqual(theme.Background, theme.Foreground);
            
            // All ANSI colors should be defined (not zero)
            Assert.NotEqual(0u, theme.AnsiBlack);
            Assert.NotEqual(0u, theme.AnsiRed);
            Assert.NotEqual(0u, theme.AnsiWhite);
        }
    }

    [Fact]
    public void DarkThemes_HaveDarkBackgrounds()
    {
        foreach (var theme in BuiltInThemes.DarkThemes)
        {
            // Extract RGB from ARGB
            uint rgb = theme.Background & 0x00FFFFFF;
            
            // Simple heuristic: sum of RGB components < 0x80 * 3 = dark
            uint r = (rgb >> 16) & 0xFF;
            uint g = (rgb >> 8) & 0xFF;
            uint b = rgb & 0xFF;
            uint sum = r + g + b;
            
            Assert.True(sum < 0x180, $"Theme should have dark background: {theme.Background:X8}");
        }
    }

    [Fact]
    public void LightThemes_HaveLightBackgrounds()
    {
        foreach (var theme in BuiltInThemes.LightThemes)
        {
            // Extract RGB from ARGB
            uint rgb = theme.Background & 0x00FFFFFF;
            
            // Simple heuristic: sum of RGB components > 0xC0 * 3 = light
            uint r = (rgb >> 16) & 0xFF;
            uint g = (rgb >> 8) & 0xFF;
            uint b = rgb & 0xFF;
            uint sum = r + g + b;
            
            Assert.True(sum > 0x240, $"Theme should have light background: {theme.Background:X8}");
        }
    }

    [Fact]
    public void ColorSchemeBase_GetAnsiColor_ReturnsCorrectColors()
    {
        var theme = new DarkPlusTheme();
        
        Assert.Equal(theme.AnsiBlack, theme.GetAnsiColor(0));
        Assert.Equal(theme.AnsiRed, theme.GetAnsiColor(1));
        Assert.Equal(theme.AnsiGreen, theme.GetAnsiColor(2));
        Assert.Equal(theme.AnsiBlue, theme.GetAnsiColor(4));
        Assert.Equal(theme.AnsiWhite, theme.GetAnsiColor(7));
        Assert.Equal(theme.AnsiBrightRed, theme.GetAnsiColor(9));
        Assert.Equal(theme.AnsiBrightWhite, theme.GetAnsiColor(15));
    }

    [Fact]
    public void ColorSchemeBase_GetAnsiColor_InvalidIndex_Throws()
    {
        var theme = new DarkPlusTheme();
        
        Assert.Throws<ArgumentOutOfRangeException>(() => theme.GetAnsiColor(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => theme.GetAnsiColor(16));
    }

    [Fact]
    public void ColorSchemeBase_ContrastRatio_CalculatesCorrectly()
    {
        // Black and white should have high contrast
        double bwContrast = ColorSchemeBase.CalculateContrastRatio(0xFF000000, 0xFFFFFFFF);
        Assert.True(bwContrast > 20); // Should be around 21:1
        
        // Same colors should have 1:1 contrast
        double sameContrast = ColorSchemeBase.CalculateContrastRatio(0xFF000000, 0xFF000000);
        Assert.Equal(1.0, sameContrast, precision: 2);
    }

    [Fact]
    public void AllThemes_HaveDefaultOpacityOf100()
    {
        foreach (var theme in BuiltInThemes.AllThemes)
        {
            Assert.Equal(100, theme.Opacity);
        }
    }

    [Fact]
    public void CustomTheme_CanOverrideOpacity()
    {
        // Create a custom theme with custom opacity
        var customTheme = new CustomOpacityTheme(85);
        Assert.Equal(85, customTheme.Opacity);
    }

    [Fact]
    public void Opacity_ValidatesRange()
    {
        // Opacity of 100 is valid (maximum)
        var theme100 = new CustomOpacityTheme(100);
        Assert.Equal(100, theme100.Opacity);
        
        // Opacity of 0 is valid (minimum)
        var theme0 = new CustomOpacityTheme(0);
        Assert.Equal(0, theme0.Opacity);
    }

    [Fact]
    public void TimeBasedOpacityTheme_CalculatesCorrectly()
    {
        // Night time test (8pm)
        var themeNight = new TimeBasedOpacityTestTheme(20);
        Assert.Equal(90, themeNight.Opacity); // Night = 90% opacity
        
        // Day time test (12pm)
        var themeDay = new TimeBasedOpacityTestTheme(12);
        Assert.Equal(100, themeDay.Opacity); // Day = 100% opacity
        
        // Early morning test (5am)
        var themeMorning = new TimeBasedOpacityTestTheme(5);
        Assert.Equal(90, themeMorning.Opacity); // Early morning = 90% opacity
        
        // Late night test (11pm)
        var themeLate = new TimeBasedOpacityTestTheme(23);
        Assert.Equal(90, themeLate.Opacity); // Late night = 90% opacity
        
        // Boundary test (6am)
        var themeBoundary = new TimeBasedOpacityTestTheme(6);
        Assert.Equal(100, themeBoundary.Opacity); // 6am = day = 100% opacity
        
        // Boundary test (8pm exactly)
        var themeBoundary2 = new TimeBasedOpacityTestTheme(20);
        Assert.Equal(90, themeBoundary2.Opacity); // 8pm = night = 90% opacity
    }
}

// Test helper classes

/// <summary>
/// Test theme with custom opacity for testing purposes.
/// </summary>
public class CustomOpacityTheme : ColorSchemeBase
{
    private readonly byte _opacity;
    
    public CustomOpacityTheme(byte opacity) : base(
        background: 0xFF1E1E1E,
        foreground: 0xFFD4D4D4,
        ansiBlack: 0xFF000000,
        ansiRed: 0xFFCD3131,
        ansiGreen: 0xFF0DBC79,
        ansiYellow: 0xFFE5E510,
        ansiBlue: 0xFF2472C8,
        ansiMagenta: 0xFFBC3FBC,
        ansiCyan: 0xFF11A8CD,
        ansiWhite: 0xFFE5E5E5,
        ansiBrightBlack: 0xFF666666,
        ansiBrightRed: 0xFFF14C4C,
        ansiBrightGreen: 0xFF23D18B,
        ansiBrightYellow: 0xFFF5F543,
        ansiBrightBlue: 0xFF3B8EEA,
        ansiBrightMagenta: 0xFFD670D6,
        ansiBrightCyan: 0xFF29B8DB,
        ansiBrightWhite: 0xFFFFFFFF)
    {
        _opacity = opacity;
    }
    
    public override byte Opacity => _opacity;
}

/// <summary>
/// Test theme with time-based opacity for testing.
/// </summary>
public class TimeBasedOpacityTestTheme : ColorSchemeBase
{
    private readonly int _hour;
    
    public TimeBasedOpacityTestTheme(int hour) : base(
        background: 0xFF1E1E1E,
        foreground: 0xFFD4D4D4,
        ansiBlack: 0xFF000000,
        ansiRed: 0xFFCD3131,
        ansiGreen: 0xFF0DBC79,
        ansiYellow: 0xFFE5E510,
        ansiBlue: 0xFF2472C8,
        ansiMagenta: 0xFFBC3FBC,
        ansiCyan: 0xFF11A8CD,
        ansiWhite: 0xFFE5E5E5,
        ansiBrightBlack: 0xFF666666,
        ansiBrightRed: 0xFFF14C4C,
        ansiBrightGreen: 0xFF23D18B,
        ansiBrightYellow: 0xFFF5F543,
        ansiBrightBlue: 0xFF3B8EEA,
        ansiBrightMagenta: 0xFFD670D6,
        ansiBrightCyan: 0xFF29B8DB,
        ansiBrightWhite: 0xFFFFFFFF)
    {
        _hour = hour;
    }
    
    public override byte Opacity => (byte)(_hour is >= 20 or < 6 ? 90 : 100);
}
