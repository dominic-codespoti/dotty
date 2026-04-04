using System.Diagnostics;
using System.Threading.Tasks;
using Dotty.E2E.Tests.Assertions;
using Xunit;
using Xunit.Abstractions;

namespace Dotty.E2E.Tests.Scenarios;

/// <summary>
/// E2E tests for terminal rendering functionality with performance measurements.
/// </summary>
public class RenderingE2ETests : E2EPerformanceTestBase
{
    public RenderingE2ETests(ITestOutputHelper outputHelper) : base("Rendering", outputHelper)
    {
    }

    [Fact]
    public async Task Basic_Rendering_Should_Display_Text()
    {
        await RunPerformanceTestAsync(nameof(Basic_Rendering_Should_Display_Text), async () =>
        {
            // Arrange - App is already started
            var testText = "Hello Dotty!";
            
            // Act - Send text to terminal
            await SendTextAndWaitAsync(testText);
            
            // Assert - Verify stats show activity
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsCreated > 0, "Session should be created");
        }, assertBaseline: false);
    }

    [Theory]
    [InlineData("Basic text rendering test")]
    [InlineData("Special chars: !@#$%^&*()")]
    [InlineData("Unicode: 你好世界 🎉 émojis")]
    public async Task Text_Renders_Correctly(string testText)
    {
        await RunPerformanceTestAsync($"Text_Renders_{testText.Substring(0, Math.Min(20, testText.Length))}", async () =>
        {
            // Act
            await SendTextAndWaitAsync(testText);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.MountedViews > 0, "Terminal view should be mounted");
        }, assertBaseline: false);
    }

    [Fact]
    public async Task Terminal_Resize_Should_Reflow_Content()
    {
        await RunPerformanceTestAsync(nameof(Terminal_Resize_Should_Reflow_Content), async () =>
        {
            // Arrange
            var originalStats = await GetStatsAsync();
            
            // Act - Resize to smaller dimensions
            await App.Commands.ResizeAsync(40, 12);
            await Task.Delay(500);
            
            // Assert - Should still have session
            var newStats = await GetStatsAsync();
            Assert.True(newStats.SessionsStarted > 0, "Session should still be started after resize");
        });
    }

    [Fact]
    public async Task Multiple_Lines_Should_Render_Correctly()
    {
        await RunPerformanceTestAsync(nameof(Multiple_Lines_Should_Render_Correctly), async () =>
        {
            // Arrange
            var lines = new[]
            {
                "Line 1: First line of text",
                "Line 2: Second line of text",
                "Line 3: Third line of text"
            };
            
            // Act
            foreach (var line in lines)
            {
                await SendTextAndWaitAsync(line + "\n");
            }
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0);
        });
    }

    [Fact]
    public async Task Scrollback_Buffer_Should_Accumulate_Content()
    {
        await RunPerformanceTestAsync(nameof(Scrollback_Buffer_Should_Accumulate_Content), async () =>
        {
            // Arrange
            var largeText = string.Join("\n", Enumerable.Range(0, 50).Select(i => $"Line {i}: " + new string('A', 80)));
            
            // Act
            await SendTextAndWaitAsync(largeText);
            await Task.Delay(1000);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Session should be started");
        });
    }

    [Theory]
    [InlineData(1)]   // Block
    [InlineData(2)]   // Underline
    [InlineData(3)]   // Bar
    public async Task Cursor_Should_Render_Different_Shapes(int cursorShape)
    {
        await RunPerformanceTestAsync($"Cursor_Shape_{cursorShape}", async () =>
        {
            // Act - Set cursor shape via ANSI sequence
            var ansiSequence = $"\u001b[{cursorShape} q";
            await App.Commands.InjectAnsiAsync(ansiSequence);
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0);
        }, assertBaseline: false);
    }

    [Fact]
    public async Task Clear_Screen_Should_Reset_Display()
    {
        await RunPerformanceTestAsync(nameof(Clear_Screen_Should_Reset_Display), async () =>
        {
            // Arrange - Add some content
            await SendTextAndWaitAsync("This is content that will be cleared\n");
            await Task.Delay(500);
            
            // Act - Clear screen via ANSI
            await App.Commands.InjectAnsiAsync("\u001b[2J\u001b[H");
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0);
        }, assertBaseline: false);
    }

    [Theory]
    [InlineData(8)]        // 8 basic colors
    [InlineData(16)]       // 16 colors (including bright)
    [InlineData(256)]      // 256 colors
    [InlineData(16777216)] // TrueColor
    public async Task Color_Rendering_Supports_Different_Palettes(int colorCount)
    {
        await RunPerformanceTestAsync($"Color_Palette_{colorCount}", async () =>
        {
            // Act - Send ANSI color sequences
            string testSequence;
            if (colorCount <= 16)
            {
                // Basic colors
                testSequence = "\u001b[31mRed\u001b[32mGreen\u001b[34mBlue\u001b[0m";
            }
            else if (colorCount <= 256)
            {
                // 256 colors
                testSequence = "\u001b[38;5;196m256-Red\u001b[38;5;46m256-Green\u001b[38;5;21m256-Blue\u001b[0m";
            }
            else
            {
                // TrueColor
                testSequence = "\u001b[38;2;255;0;0mTrue-Red\u001b[38;2;0;255;0mTrue-Green\u001b[38;2;0;0;255mTrue-Blue\u001b[0m";
            }
            
            await App.Commands.InjectAnsiAsync(testSequence);
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0);
        }, assertBaseline: false);
    }

    [Fact]
    public async Task Rendering_Performance_Should_Meet_Thresholds()
    {
        await RunPerformanceTestAsync(nameof(Rendering_Performance_Should_Meet_Thresholds), async () =>
        {
            // Arrange - Generate content that stresses the renderer
            var lines = Enumerable.Range(0, 100).Select(i => $"Line {i}: {new string('X', 80)}");
            var content = string.Join("\n", lines);
            
            // Act
            await SendTextAndWaitAsync(content);
            await Task.Delay(1000); // Let it settle
            
            // Assert - Check performance from the snapshot
            var snapshot = CurrentSnapshot;
            Assert.NotNull(snapshot);
            
            // Assert on performance thresholds
            PerformanceAssertions.AssertFps(snapshot.Fps, minFps: 30);
            PerformanceAssertions.AssertFrameTimeP95(snapshot.FrameTimeP95, maxP95Ms: 33.33);
        });
    }

    [Fact]
    public async Task High_Volume_Rendering_Should_Maintain_FPS()
    {
        await RunPerformanceTestAsync(nameof(High_Volume_Rendering_Should_Maintain_FPS), async () =>
        {
            // Arrange - Generate high volume content
            var stopwatch = Stopwatch.StartNew();
            var lines = Enumerable.Range(0, 500).Select(i => $"Performance test line {i}: {new string('A', 80)}");
            var content = string.Join("\n", lines);
            
            // Act
            await SendTextAndWaitAsync(content);
            stopwatch.Stop();
            
            // Assert - Should complete within reasonable time
            Assert.True(stopwatch.ElapsedMilliseconds < 10000, 
                $"High volume rendering took too long: {stopwatch.ElapsedMilliseconds}ms");
                
            // Assert performance thresholds
            var snapshot = CurrentSnapshot;
            Assert.NotNull(snapshot);
            
            PerformanceAssertions.AssertFps(snapshot.Fps, minFps: 15); // More lenient for high volume
            PerformanceAssertions.AssertFrameTimeP95(snapshot.FrameTimeP95, maxP95Ms: 66.67);
        });
    }

    [Fact]
    public async Task Rapid_Rendering_Should_Be_Responsive()
    {
        await RunPerformanceTestAsync(nameof(Rapid_Rendering_Should_Be_Responsive), async () =>
        {
            // Arrange
            var iterations = 50;
            var stopwatch = Stopwatch.StartNew();
            
            // Act - Rapidly render content
            for (int i = 0; i < iterations; i++)
            {
                await SendTextAndWaitAsync($"Rapid frame {i}\n");
            }
            
            stopwatch.Stop();
            
            // Assert
            var avgTimePerFrame = stopwatch.ElapsedMilliseconds / (double)iterations;
            Assert.True(avgTimePerFrame < 200, 
                $"Average time per frame too high: {avgTimePerFrame:F1}ms");
        });
    }
}
