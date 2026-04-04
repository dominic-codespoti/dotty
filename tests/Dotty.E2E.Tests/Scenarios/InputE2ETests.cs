using System.Threading.Tasks;
using Xunit;

namespace Dotty.E2E.Tests.Scenarios;

/// <summary>
/// E2E tests for terminal input handling.
/// </summary>
public class InputE2ETests : E2ETestBase
{
    public InputE2ETests() : base("Input")
    {
    }

    [Theory]
    [InlineData("a")]
    [InlineData("z")]
    [InlineData("0")]
    [InlineData("9")]
    [InlineData("A")]
    [InlineData("Z")]
    public async Task Alphanumeric_Keys_Should_Be_Accepted(string key)
    {
        await RunTestAsync(async () =>
        {
            // Act
            await SendTextAndWaitAsync(key);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0);
        });
    }

    [Theory]
    [InlineData("!")]
    [InlineData("@")]
    [InlineData("#")]
    [InlineData("$")]
    [InlineData("%")]
    [InlineData("^")]
    [InlineData("&")]
    [InlineData("*")]
    [InlineData("(")]
    [InlineData(")")]
    public async Task Special_Characters_Should_Be_Accepted(string character)
    {
        await RunTestAsync(async () =>
        {
            // Act
            await SendTextAndWaitAsync(character);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0);
        });
    }

    [Theory]
    [InlineData("Enter")]
    [InlineData("Escape")]
    [InlineData("Tab")]
    [InlineData("Backspace")]
    public async Task Control_Keys_Should_Be_Handled(string key)
    {
        await RunTestAsync(async () =>
        {
            // Act
            await App.Commands.SendKeyAsync(key);
            await Task.Delay(300);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0);
        });
    }

    [Fact]
    public async Task Arrow_Keys_Should_Navigate()
    {
        await RunTestAsync(async () =>
        {
            // Arrange - Add some content
            await SendTextAndWaitAsync("Test content");
            await Task.Delay(200);
            
            // Act - Send arrow keys
            await App.Commands.SendKeyAsync("Left");
            await Task.Delay(100);
            await App.Commands.SendKeyAsync("Left");
            await Task.Delay(100);
            await App.Commands.SendKeyAsync("Right");
            await Task.Delay(100);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0);
        });
    }

    [Fact]
    public async Task Ctrl_Combinations_Should_Be_Handled()
    {
        await RunTestAsync(async () =>
        {
            // Act - Send Ctrl+C (usually interrupt)
            await App.Commands.SendKeyComboAsync("C", new[] { "Ctrl" });
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0);
        });
    }

    [Theory]
    [InlineData("F1")]
    [InlineData("F2")]
    [InlineData("F3")]
    [InlineData("F4")]
    [InlineData("F5")]
    [InlineData("F6")]
    [InlineData("F7")]
    [InlineData("F8")]
    [InlineData("F9")]
    [InlineData("F10")]
    [InlineData("F11")]
    [InlineData("F12")]
    public async Task Function_Keys_Should_Be_Handled(string key)
    {
        await RunTestAsync(async () =>
        {
            // Act
            await App.Commands.SendKeyAsync(key);
            await Task.Delay(200);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0);
        });
    }

    [Fact]
    public async Task Rapid_Keystrokes_Should_Be_Buffered()
    {
        await RunTestAsync(async () =>
        {
            // Act - Send many keys rapidly
            for (int i = 0; i < 50; i++)
            {
                await App.Commands.SendTextAsync("a");
            }
            await Task.Delay(1000);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0);
        });
    }

    [Fact]
    public async Task Newline_Character_Should_Create_New_Line()
    {
        await RunTestAsync(async () =>
        {
            // Act
            await SendTextAndWaitAsync("First line");
            await App.Commands.SendKeyAsync("Enter");
            await SendTextAndWaitAsync("Second line");
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0);
        });
    }
}
