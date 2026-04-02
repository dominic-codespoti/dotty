using Xunit;
using FluentAssertions;

namespace Dotty.Config.SourceGenerator.Tests;

/// <summary>
/// Unit tests for the ThemeResolver class
/// </summary>
public class ThemeResolverTests
{
    private readonly ThemeResolver _resolver;

    public ThemeResolverTests()
    {
        _resolver = new ThemeResolver();
    }

    [Fact]
    public void Resolve_ReturnsThemeByCanonicalName_ForDarkPlus()
    {
        // Arrange
        var themeName = "Dark+";

        // Act
        var result = _resolver.Resolve(themeName);

        // Assert
        result.Should().NotBeNull();
        result.Name.Should().Be(themeName);
        result.AnsiColors.Should().HaveCount(16);
    }

    [Theory]
    [InlineData("dracula")]
    [InlineData("Dracula")]
    [InlineData("DRACULA")]
    public void Resolve_ReturnsThemeByAlias(string alias)
    {
        // Act
        var result = _resolver.Resolve(alias);

        // Assert
        result.Should().NotBeNull();
        result.Name.Should().Be("Dracula");
    }

    [Theory]
    [InlineData("nonexistent")]
    [InlineData("unknown")]
    [InlineData("invalid-theme-name")]
    public void Resolve_ReturnsDarkPlusForUnknownTheme(string unknownTheme)
    {
        // Act
        var result = _resolver.Resolve(unknownTheme);

        // Assert
        result.Should().NotBeNull();
        result.Name.Should().Be("Dark+");
    }

    [Theory]
    [InlineData("dark+", "Dark+")]
    [InlineData("DARK+", "Dark+")]
    [InlineData("Dark+", "Dark+")]
    [InlineData("monokai", "Monokai")]
    [InlineData("MONOKAI", "Monokai")]
    [InlineData("MoNoKaI", "Monokai")]
    public void Resolve_IsCaseInsensitive(string input, string expectedName)
    {
        // Act
        var result = _resolver.Resolve(input);

        // Assert
        result.Should().NotBeNull();
        result.Name.Should().Be(expectedName);
    }

    [Fact]
    public void Resolve_HandlesNull()
    {
        // Arrange
        string? themeName = null;

        // Act
        var result = _resolver.Resolve(themeName!);

        // Assert
        result.Should().NotBeNull();
        result.Name.Should().Be("Dark+");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_HandlesEmptyString(string emptyTheme)
    {
        // Act
        var result = _resolver.Resolve(emptyTheme);

        // Assert
        result.Should().NotBeNull();
        result.Name.Should().Be("Dark+");
    }

    [Theory]
    [InlineData("Dark+")]
    [InlineData("Light+")]
    [InlineData("Monokai")]
    [InlineData("Dracula")]
    [InlineData("Nord")]
    [InlineData("OneDark")]
    [InlineData("Solarized Dark")]
    [InlineData("Solarized Light")]
    [InlineData("Tokyo Night")]
    [InlineData("GitHub Dark")]
    [InlineData("GitHub Light")]
    public void Resolve_AllThemesAreResolvable(string themeName)
    {
        // Act
        var result = _resolver.Resolve(themeName);

        // Assert
        result.Should().NotBeNull();
        result.Name.Should().NotBeNullOrEmpty();
        result.AnsiColors.Should().HaveCount(16);
    }

    [Theory]
    [InlineData("Dark+", "#1e1e1e")]
    [InlineData("Light+", "#ffffff")]
    [InlineData("Monokai", "#272822")]
    [InlineData("Dracula", "#282a36")]
    [InlineData("Nord", "#2e3440")]
    [InlineData("OneDark", "#282c34")]
    [InlineData("Solarized Dark", "#002b36")]
    [InlineData("Solarized Light", "#fdf6e3")]
    [InlineData("Tokyo Night", "#1a1b26")]
    [InlineData("GitHub Dark", "#0d1117")]
    [InlineData("GitHub Light", "#ffffff")]
    public void Resolve_CorrectBackgroundColors(string themeName, string expectedBackground)
    {
        // Act
        var result = _resolver.Resolve(themeName);

        // Assert
        result.Background.Should().Be(expectedBackground);
    }

    [Theory]
    [InlineData("Dark+")]
    [InlineData("Light+")]
    [InlineData("Monokai")]
    [InlineData("Dracula")]
    [InlineData("Nord")]
    [InlineData("OneDark")]
    [InlineData("Solarized Dark")]
    [InlineData("Solarized Light")]
    [InlineData("Tokyo Night")]
    [InlineData("GitHub Dark")]
    [InlineData("GitHub Light")]
    public void Resolve_CorrectAnsiColorCount(string themeName)
    {
        // Act
        var result = _resolver.Resolve(themeName);

        // Assert
        result.AnsiColors.Should().HaveCount(16);
    }

    [Theory]
    [InlineData("Dark+", 1.0)]
    [InlineData("Light+", 1.0)]
    [InlineData("Monokai", 1.0)]
    [InlineData("Dracula", 1.0)]
    [InlineData("Nord", 0.95)]
    [InlineData("OneDark", 1.0)]
    [InlineData("Solarized Dark", 1.0)]
    [InlineData("Solarized Light", 1.0)]
    [InlineData("Tokyo Night", 0.95)]
    [InlineData("GitHub Dark", 1.0)]
    [InlineData("GitHub Light", 1.0)]
    public void Resolve_CorrectOpacityValues(string themeName, double expectedOpacity)
    {
        // Act
        var result = _resolver.Resolve(themeName);

        // Assert
        result.Opacity.Should().Be(expectedOpacity);
    }
}
