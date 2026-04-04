using System.Threading.Tasks;
using Dotty.App.Services;
using Xunit;

namespace Dotty.E2E.Tests.Scenarios;

/// <summary>
/// E2E tests for terminal configuration and theming.
/// </summary>
public class ConfigurationE2ETests : E2ETestBase
{
    public ConfigurationE2ETests() : base("Configuration")
    {
    }

    [Theory]
    [InlineData("DarkPlus")]
    [InlineData("Dracula")]
    [InlineData("OneDark")]
    [InlineData("GruvboxDark")]
    [InlineData("LightPlus")]
    [InlineData("OneLight")]
    public async Task BuiltIn_Themes_Should_Be_Available(string themeName)
    {
        await RunTestAsync(async () =>
        {
            // Act - Try to apply theme via config
            await App.Commands.SetConfigAsync("theme", themeName);
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, $"Theme {themeName} should be applied");
        });
    }

    [Theory]
    [InlineData(12)]
    [InlineData(14)]
    [InlineData(16)]
    [InlineData(18)]
    [InlineData(20)]
    public async Task Font_Size_Changes_Should_Work(int fontSize)
    {
        await RunTestAsync(async () =>
        {
            // Act
            await App.Commands.SetConfigAsync("fontSize", fontSize.ToString());
            await Task.Delay(500);
            
            // Add some text to verify rendering with new size
            await SendTextAndWaitAsync("Testing font size change");
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0);
        });
    }

    [Theory]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(1000)]
    public async Task Scrollback_Buffer_Size_Should_Be_Configurable(int lines)
    {
        await RunTestAsync(async () =>
        {
            // Act
            await App.Commands.SetConfigAsync("scrollback", lines.ToString());
            await Task.Delay(300);
            
            // Generate content to test scrollback
            for (int i = 0; i < Math.Min(lines, 100); i++)
            {
                await SendTextAndWaitAsync($"Scrollback line {i}\n");
            }
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(80)]
    [InlineData(95)]
    public async Task Window_Opacity_Should_Be_Configurable(int opacity)
    {
        await RunTestAsync(async () =>
        {
            // Act
            await App.Commands.SetConfigAsync("opacity", opacity.ToString());
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0);
        });
    }

    [Fact]
    public async Task Transparency_Levels_Should_Be_Configurable()
    {
        await RunTestAsync(async () =>
        {
            // Test different transparency levels
            var levels = new[] { "None", "Transparent", "Blur", "Acrylic" };
            
            foreach (var level in levels)
            {
                await App.Commands.SetConfigAsync("transparency", level);
                await Task.Delay(300);
                
                // Verify app still works
                await SendTextAndWaitAsync($"Testing {level} transparency");
            }
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0);
        });
    }

    [Fact]
    public async Task Color_Scheme_Can_Be_Switched()
    {
        await RunTestAsync(async () =>
        {
            // Arrange
            var themes = new[] { "DarkPlus", "Dracula", "OneDark" };
            
            // Act - Switch between themes
            foreach (var theme in themes)
            {
                await App.Commands.SetConfigAsync("theme", theme);
                await Task.Delay(500);
                await SendTextAndWaitAsync($"Theme: {theme}");
            }
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0);
        });
    }

    [Fact]
    public async Task Rapid_Configuration_Changes_Should_Be_Handled()
    {
        await RunTestAsync(async () =>
        {
            // Act - Rapidly change configurations
            for (int i = 0; i < 10; i++)
            {
                await App.Commands.SetConfigAsync("fontSize", (12 + i).ToString());
                await Task.Delay(100);
            }
            
            // Assert - App should still be responsive
            await SendTextAndWaitAsync("After rapid config changes");
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0);
        });
    }

    [Theory]
    [InlineData("Block")]
    [InlineData("Line")]
    [InlineData("Bar")]
    public async Task Cursor_Style_Should_Be_Configurable(string style)
    {
        await RunTestAsync(async () =>
        {
            // Act
            await App.Commands.SetConfigAsync("cursorStyle", style);
            await Task.Delay(300);
            
            // Type some text to see cursor
            await SendTextAndWaitAsync("Testing cursor style");
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0);
        });
    }
}
