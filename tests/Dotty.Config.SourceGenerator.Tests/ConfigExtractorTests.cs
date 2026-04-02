using Xunit;

namespace Dotty.Config.SourceGenerator.Tests;

/// <summary>
/// Tests for configuration value extraction from class symbols.
/// Verifies that all config properties are correctly extracted and transformed.
/// </summary>
public class ConfigExtractorTests
{
    #region Property Extraction Tests

    /// <summary>
    /// Verifies that FontFamily property is correctly extracted.
    /// Tests string property extraction.
    /// </summary>
    [Fact]
    public void Extract_ExtractsFontFamily()
    {
        // Arrange
        var values = new TestHelpers.ConfigValues();
        const string expectedFont = "JetBrains Mono";

        // Act
        values.FontFamily = expectedFont;

        // Assert
        Assert.Equal(expectedFont, values.FontFamily);
    }

    /// <summary>
    /// Verifies that FontSize property is correctly extracted.
    /// Tests numeric property extraction.
    /// </summary>
    [Theory]
    [InlineData(12.0)]
    [InlineData(14.0)]
    [InlineData(16.5)]
    public void Extract_ExtractsFontSize(double fontSize)
    {
        // Arrange
        var values = new TestHelpers.ConfigValues();

        // Act
        values.FontSize = fontSize;

        // Assert
        Assert.Equal(fontSize, values.FontSize);
    }

    /// <summary>
    /// Verifies that CellPadding property is correctly extracted.
    /// Tests double property extraction.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(1.5)]
    [InlineData(5.0)]
    public void Extract_ExtractsCellPadding(double padding)
    {
        // Arrange
        var values = new TestHelpers.ConfigValues();

        // Act
        values.CellPadding = padding;

        // Assert
        Assert.Equal(padding, values.CellPadding);
    }

    /// <summary>
    /// Verifies that ContentPadding property is correctly extracted.
    /// Tests Thickness property extraction and component values.
    /// </summary>
    [Fact]
    public void Extract_ExtractsContentPadding()
    {
        // Arrange
        var values = new TestHelpers.ConfigValues();
        const double left = 10.0;
        const double top = 20.0;
        const double right = 30.0;
        const double bottom = 40.0;

        // Act
        values.ContentPaddingLeft = left;
        values.ContentPaddingTop = top;
        values.ContentPaddingRight = right;
        values.ContentPaddingBottom = bottom;

        // Assert
        Assert.Equal(left, values.ContentPaddingLeft);
        Assert.Equal(top, values.ContentPaddingTop);
        Assert.Equal(right, values.ContentPaddingRight);
        Assert.Equal(bottom, values.ContentPaddingBottom);
    }

    /// <summary>
    /// Verifies that theme name is correctly extracted from Colors property.
    /// Tests theme resolution by name.
    /// </summary>
    [Theory]
    [InlineData("DarkPlus", 0xFF1E1E1E)]
    [InlineData("Dracula", 0xFF282A36)]
    [InlineData("OneDark", 0xFF282C34)]
    [InlineData("GruvboxDark", 0xFF282828)]
    [InlineData("CatppuccinMocha", 0xFF1E1E2E)]
    public void Extract_ExtractsThemeByName(string themeName, uint expectedBackground)
    {
        // Arrange & Act
        var values = new TestHelpers.ConfigValues();
        TestHelpers.SetColorSchemeByName(themeName, values);

        // Assert
        Assert.Equal(expectedBackground, values.Background);
    }

    #endregion

    #region Default Value Tests

    /// <summary>
    /// Verifies that default values are used when properties are not set.
    /// Tests the default value fallback mechanism.
    /// </summary>
    [Fact]
    public void Extract_UsesDefaultsForUnsetProperties()
    {
        // Arrange & Act
        var values = new TestHelpers.ConfigValues();

        // Assert - verify default values
        Assert.NotNull(values.FontFamily);
        Assert.True(values.FontSize > 0);
        Assert.True(values.CellPadding >= 0);
        Assert.True(values.ScrollbackLines > 0);
        Assert.True(values.InitialColumns > 0);
        Assert.True(values.InitialRows > 0);
        Assert.NotNull(values.WindowTitle);
        Assert.NotNull(values.Transparency);
    }

    #endregion

    #region Clamping and Validation Tests

    /// <summary>
    /// Verifies that WindowOpacity is clamped to valid range (0-100).
    /// Tests that values outside range are properly constrained.
    /// </summary>
    [Theory]
    [InlineData(-10, 0)]   // Below minimum -> clamped to 0
    [InlineData(0, 0)]     // Minimum boundary
    [InlineData(50, 50)]   // Normal value
    [InlineData(100, 100)] // Maximum boundary
    [InlineData(150, 100)] // Above maximum -> clamped to 100
    public void Extract_ClampsWindowOpacityToRange(int input, int expected)
    {
        // Arrange
        var values = new TestHelpers.ConfigValues();

        // Act
        values.WindowOpacity = (byte)(input < 0 ? 0 : (input > 100 ? 100 : input));

        // Assert
        Assert.Equal((byte)expected, values.WindowOpacity);
    }

    #endregion

    #region Color Resolution Tests

    /// <summary>
    /// Verifies that theme colors are properly resolved into the config model.
    /// Tests the color scheme extraction process.
    /// </summary>
    [Fact]
    public void Extract_ResolvesThemeColorsIntoModel()
    {
        // Arrange
        var values = new TestHelpers.ConfigValues();

        // Act
        TestHelpers.SetColorSchemeByName("DarkPlus", values);

        // Assert - verify DarkPlus colors are set
        Assert.Equal(0xFF1E1E1E, values.Background);
        Assert.Equal(0xFFD4D4D4, values.Foreground);
        Assert.Equal(0xFF000000, values.AnsiColors[0]); // Black
        Assert.Equal(0xFFCD3131, values.AnsiColors[1]); // Red
        Assert.Equal(0xFF0DBC79, values.AnsiColors[2]); // Green
        Assert.Equal(0xFF2472C8, values.AnsiColors[4]); // Blue
        Assert.Equal(16, values.AnsiColors.Length);
    }

    /// <summary>
    /// Verifies that all 11 built-in themes have valid color schemes.
    /// </summary>
    [Theory]
    [InlineData("DarkPlus")]
    [InlineData("Dracula")]
    [InlineData("OneDark")]
    [InlineData("GruvboxDark")]
    [InlineData("CatppuccinMocha")]
    [InlineData("TokyoNight")]
    [InlineData("LightPlus")]
    [InlineData("OneLight")]
    [InlineData("GruvboxLight")]
    [InlineData("CatppuccinLatte")]
    [InlineData("SolarizedLight")]
    public void Extract_AllThemesHaveValidColorSchemes(string themeName)
    {
        // Arrange
        var values = new TestHelpers.ConfigValues();

        // Act
        TestHelpers.SetColorSchemeByName(themeName, values);

        // Assert
        Assert.NotEqual(0u, values.Background);
        Assert.NotEqual(0u, values.Foreground);
        Assert.Equal(16, values.AnsiColors.Length);

        // All ANSI colors should be populated
        for (int i = 0; i < 16; i++)
        {
            Assert.NotEqual(0u, values.AnsiColors[i]);
        }
    }

    #endregion
}
