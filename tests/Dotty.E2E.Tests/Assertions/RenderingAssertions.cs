using System;
using Dotty.Abstractions.Config;
using Xunit;

namespace Dotty.E2E.Tests.Assertions;

/// <summary>
/// Assertions for terminal rendering state.
/// </summary>
public static class RenderingAssertions
{
    /// <summary>
    /// Asserts that the terminal has rendered.
    /// </summary>
    public static void HasRendered(bool rendered)
    {
        Assert.True(rendered, "Expected terminal to have rendered, but it did not");
    }
    
    /// <summary>
    /// Asserts that the terminal dimensions match expected values.
    /// </summary>
    public static void DimensionsMatch(int actualCols, int actualRows, int expectedCols, int expectedRows)
    {
        Assert.Equal(expectedCols, actualCols);
        Assert.Equal(expectedRows, actualRows);
    }
    
    /// <summary>
    /// Asserts that the cell size is within expected range.
    /// </summary>
    public static void CellSizeWithinRange(double actualWidth, double actualHeight, double minWidth, double maxWidth, double minHeight, double maxHeight)
    {
        Assert.True(actualWidth >= minWidth && actualWidth <= maxWidth,
            $"Cell width {actualWidth} is not within range [{minWidth}, {maxWidth}]");
        Assert.True(actualHeight >= minHeight && actualHeight <= maxHeight,
            $"Cell height {actualHeight} is not within range [{minHeight}, {maxHeight}]");
    }
    
    /// <summary>
    /// Asserts that the font size matches expected value.
    /// </summary>
    public static void FontSizeMatches(double actualSize, double expectedSize, double tolerance = 0.1)
    {
        var diff = Math.Abs(actualSize - expectedSize);
        Assert.True(diff <= tolerance,
            $"Font size {actualSize} differs from expected {expectedSize} by {diff}, which exceeds tolerance {tolerance}");
    }
}

/// <summary>
/// Assertions for terminal configuration.
/// </summary>
public static class ConfigurationAssertions
{
    /// <summary>
    /// Asserts that the theme has the expected background color.
    /// </summary>
    public static void BackgroundColorEquals(uint actualColor, uint expectedColor)
    {
        Assert.Equal(expectedColor, actualColor);
    }
    
    /// <summary>
    /// Asserts that the theme has the expected foreground color.
    /// </summary>
    public static void ForegroundColorEquals(uint actualColor, uint expectedColor)
    {
        Assert.Equal(expectedColor, actualColor);
    }
    
    /// <summary>
    /// Asserts that opacity is within expected range.
    /// </summary>
    public static void OpacityWithinRange(int actualOpacity, int minOpacity, int maxOpacity)
    {
        Assert.True(actualOpacity >= minOpacity && actualOpacity <= maxOpacity,
            $"Opacity {actualOpacity} is not within range [{minOpacity}, {maxOpacity}]");
    }
    
    /// <summary>
    /// Asserts that the ANSI palette contains the expected color.
    /// </summary>
    public static void AnsiPaletteContains(uint[] palette, int index, uint expectedColor)
    {
        Assert.True(index >= 0 && index < palette.Length,
            $"Palette index {index} is out of range [0, {palette.Length})");
        Assert.Equal(expectedColor, palette[index]);
    }
    
    /// <summary>
    /// Asserts that the theme is valid (not null and has required colors).
    /// </summary>
    public static void ThemeIsValid(IColorScheme? theme)
    {
        Assert.NotNull(theme);
        Assert.NotEqual(0u, theme.Background);
        Assert.NotEqual(0u, theme.Foreground);
    }
}

/// <summary>
/// Assertions for GUI elements.
/// </summary>
public static class GuiAssertions
{
    /// <summary>
    /// Asserts that the window is focused.
    /// </summary>
    public static void WindowIsFocused(bool isFocused)
    {
        Assert.True(isFocused, "Expected window to be focused, but it is not");
    }
    
    /// <summary>
    /// Asserts that the window has the expected dimensions.
    /// </summary>
    public static void WindowDimensionsMatch(int actualWidth, int actualHeight, int expectedWidth, int expectedHeight)
    {
        Assert.Equal(expectedWidth, actualWidth);
        Assert.Equal(expectedHeight, actualHeight);
    }
    
    /// <summary>
    /// Asserts that the window is visible.
    /// </summary>
    public static void WindowIsVisible(bool isVisible)
    {
        Assert.True(isVisible, "Expected window to be visible, but it is not");
    }
    
    /// <summary>
    /// Asserts that the expected number of tabs is present.
    /// </summary>
    public static void TabCountEquals(int actualCount, int expectedCount)
    {
        Assert.Equal(expectedCount, actualCount);
    }
    
    /// <summary>
    /// Asserts that the active tab index matches expected.
    /// </summary>
    public static void ActiveTabIndexEquals(int actualIndex, int expectedIndex)
    {
        Assert.Equal(expectedIndex, actualIndex);
    }
    
    /// <summary>
    /// Asserts that a tab exists with the expected title.
    /// </summary>
    public static void TabExistsWithTitle(string[] tabTitles, string expectedTitle, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        Assert.Contains(tabTitles, t => t.Equals(expectedTitle, comparison));
    }
}
