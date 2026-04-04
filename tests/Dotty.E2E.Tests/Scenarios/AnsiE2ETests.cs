using System.Diagnostics;
using System.Threading.Tasks;
using Dotty.E2E.Tests.Assertions;
using Xunit;
using Xunit.Abstractions;

namespace Dotty.E2E.Tests.Scenarios;

/// <summary>
/// E2E tests for ANSI escape sequence handling with parser throughput measurements.
/// </summary>
public class AnsiE2ETests : E2EPerformanceTestBase
{
    public AnsiE2ETests(ITestOutputHelper outputHelper) : base("Ansi", outputHelper)
    {
    }

    [Theory]
    [InlineData("\u001b[31m", "red")]
    [InlineData("\u001b[32m", "green")]
    [InlineData("\u001b[34m", "blue")]
    [InlineData("\u001b[1m", "bold")]
    [InlineData("\u001b[4m", "underline")]
    public async Task Sgr_Codes_Should_Be_Processed(string sequence, string description)
    {
        await RunPerformanceTestAsync($"Sgr_Code_{description}", async () =>
        {
            // Act
            await App.Commands.InjectAnsiAsync(sequence + $"{description} text\u001b[0m");
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, $"SGR code for {description} should be processed");
        }, assertBaseline: false);
    }

    [Fact]
    public async Task Cursor_Movement_Should_Work()
    {
        await RunPerformanceTestAsync(nameof(Cursor_Movement_Should_Work), async () =>
        {
            // Act - Move cursor up, down, forward, back
            await App.Commands.InjectAnsiAsync("Start\u001b[AUp\u001b[BDn\u001b[CRight\u001b[DLeft");
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Cursor movement should work");
        });
    }

    [Theory]
    [InlineData("\u001b[2J", "clear screen")]
    [InlineData("\u001b[K", "clear line from cursor")]
    [InlineData("\u001b[0K", "clear line from cursor (explicit)")]
    [InlineData("\u001b[1K", "clear line to cursor")]
    [InlineData("\u001b[2K", "clear entire line")]
    public async Task Erase_Sequences_Should_Clear_Content(string sequence, string description)
    {
        await RunPerformanceTestAsync($"Erase_{description.Replace(" ", "_")}", async () =>
        {
            // Arrange - Add content
            await SendTextAndWaitAsync("Content to be cleared\n");
            await Task.Delay(200);
            
            // Act
            await App.Commands.InjectAnsiAsync(sequence);
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, $"Erase sequence {description} should work");
        }, assertBaseline: false);
    }

    [Fact]
    public async Task Scroll_Sequences_Should_Move_Content()
    {
        await RunPerformanceTestAsync(nameof(Scroll_Sequences_Should_Move_Content), async () =>
        {
            // Arrange - Add content
            await SendTextAndWaitAsync("Line 1\nLine 2\nLine 3\nLine 4\nLine 5");
            await Task.Delay(500);
            
            // Act - Scroll region
            await App.Commands.InjectAnsiAsync("\u001b[1;3r\u001b[2;1H\u001b[S");
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Scroll sequences should work");
        });
    }

    [Fact]
    public async Task Title_Change_Sequence_Should_Update_Title()
    {
        await RunPerformanceTestAsync(nameof(Title_Change_Sequence_Should_Update_Title), async () =>
        {
            // Act
            await App.Commands.InjectAnsiAsync("\u001b]2;Test Title\u0007");
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0);
        }, assertBaseline: false);
    }

    [Fact]
    public async Task Alternate_Screen_Should_Switch_Buffers()
    {
        await RunPerformanceTestAsync(nameof(Alternate_Screen_Should_Switch_Buffers), async () =>
        {
            // Arrange
            await SendTextAndWaitAsync("Main screen content");
            
            // Act - Switch to alternate screen
            await App.Commands.InjectAnsiAsync("\u001b[?1049h");
            await Task.Delay(300);
            await SendTextAndWaitAsync("Alternate screen content");
            
            // Switch back
            await App.Commands.InjectAnsiAsync("\u001b[?1049l");
            await Task.Delay(300);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Alternate screen switching should work");
        });
    }

    [Fact]
    public async Task Complex_Mixed_Sequences_Should_Be_Handled()
    {
        await RunPerformanceTestAsync(nameof(Complex_Mixed_Sequences_Should_Be_Handled), async () =>
        {
            // Act - Complex sequence with colors, cursor movement, and clearing
            var complexSequence = 
                "\u001b[31m\u001b[1;1H" +          // Red, move to top-left
                "\u001b[2J" +                      // Clear screen
                "\u001b[32mGreen\u001b[33mYellow\u001b[0m" +  // Colors
                "\u001b[2;5H" +                   // Move cursor
                "Positioned";                    // Text
            
            await App.Commands.InjectAnsiAsync(complexSequence);
            await Task.Delay(1000);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Complex ANSI sequences should be handled");
        });
    }

    [Fact]
    public async Task _256_Color_Sequences_Should_Be_Supported()
    {
        await RunPerformanceTestAsync(nameof(_256_Color_Sequences_Should_Be_Supported), async () =>
        {
            // Act - Test 256 color sequences
            var sequence = "\u001b[38;5;196mRed\u001b[38;5;46mGreen\u001b[38;5;21mBlue\u001b[48;5;226mYellowBG\u001b[0m";
            await App.Commands.InjectAnsiAsync(sequence);
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "256 color sequences should be supported");
        }, assertBaseline: false);
    }

    [Fact]
    public async Task TrueColor_Sequences_Should_Be_Supported()
    {
        await RunPerformanceTestAsync(nameof(TrueColor_Sequences_Should_Be_Supported), async () =>
        {
            // Act - Test TrueColor sequences
            var sequence = 
                "\u001b[38;2;255;0;0m" +       // Pure red foreground
                "\u001b[48;2;0;0;255m" +       // Pure blue background
                "TrueColor\u001b[0m";
            
            await App.Commands.InjectAnsiAsync(sequence);
            await Task.Delay(500);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "TrueColor sequences should be supported");
        }, assertBaseline: false);
    }

    [Fact]
    public async Task Mouse_Mode_Should_Be_Settable()
    {
        await RunPerformanceTestAsync(nameof(Mouse_Mode_Should_Be_Settable), async () =>
        {
            // Act - Enable mouse tracking
            await App.Commands.InjectAnsiAsync("\u001b[?1000h");  // X10 mouse tracking
            await Task.Delay(200);
            await App.Commands.InjectAnsiAsync("\u001b[?1002h");  // Button event tracking
            await Task.Delay(200);
            await App.Commands.InjectAnsiAsync("\u001b[?1006h");  // SGR mouse mode
            await Task.Delay(200);
            
            // Disable
            await App.Commands.InjectAnsiAsync("\u001b[?1000l");
            await Task.Delay(200);
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Mouse mode sequences should be handled");
        }, assertBaseline: false);
    }

    [Fact]
    public async Task Parser_Throughput_Should_Meet_Thresholds()
    {
        await RunPerformanceTestAsync(nameof(Parser_Throughput_Should_Meet_Thresholds), async () =>
        {
            // Arrange - Generate a mix of ANSI sequences
            var sequences = new List<string>();
            for (int i = 0; i < 1000; i++)
            {
                sequences.Add($"\u001b[38;5;{i % 256}mColor{i}\u001b[0m");
            }
            var content = string.Join("", sequences);
            
            var stopwatch = Stopwatch.StartNew();
            
            // Act
            await App.Commands.InjectAnsiAsync(content);
            await Task.Delay(2000); // Allow processing
            
            stopwatch.Stop();
            
            // Assert
            var snapshot = CurrentSnapshot;
            Assert.NotNull(snapshot);
            
            // Assert parser throughput
            PerformanceAssertions.AssertParserThroughput(
                snapshot.ParserBytesPerSecond,
                snapshot.ParserSequencesPerSecond,
                minBytesPerSec: 100000.0,
                minSequencesPerSec: 500.0);
        });
    }

    [Fact]
    public async Task High_Volume_Ansi_Should_Be_Efficient()
    {
        await RunPerformanceTestAsync(nameof(High_Volume_Ansi_Should_Be_Efficient), async () =>
        {
            // Arrange - Generate many ANSI sequences
            var stopwatch = Stopwatch.StartNew();
            
            // Act - Send many ANSI sequences
            for (int i = 0; i < 100; i++)
            {
                var sequence = 
                    "\u001b[31mRed\u001b[0m " +
                    "\u001b[32mGreen\u001b[0m " +
                    "\u001b[1mBold\u001b[0m " +
                    "\u001b[4mUnderline\u001b[0m " +
                    "\u001b[38;5;196m256-Color\u001b[0m";
                
                await App.Commands.InjectAnsiAsync(sequence + "\n");
            }
            
            await Task.Delay(2000);
            stopwatch.Stop();
            
            // Assert - Should complete efficiently
            Assert.True(stopwatch.ElapsedMilliseconds < 8000, 
                $"High volume ANSI processing took too long: {stopwatch.ElapsedMilliseconds}ms");
            
            // Check performance metrics
            var snapshot = CurrentSnapshot;
            Assert.NotNull(snapshot);
            
            // Parser should handle at least 50KB/s
            PerformanceAssertions.AssertParserThroughput(
                snapshot.ParserBytesPerSecond, 
                snapshot.ParserSequencesPerSecond,
                minBytesPerSec: 50000.0,
                minSequencesPerSec: 100.0);
        });
    }

    [Fact]
    public async Task Rapid_Sequence_Processing_Should_Maintain_Performance()
    {
        await RunPerformanceTestAsync(nameof(Rapid_Sequence_Processing_Should_Maintain_Performance), async () =>
        {
            // Arrange
            var sequences = 50;
            var stopwatch = Stopwatch.StartNew();
            
            // Act - Send sequences rapidly
            for (int i = 0; i < sequences; i++)
            {
                await App.Commands.InjectAnsiAsync($"\u001b[38;5;{i % 256}m\u001b[48;5;{(i+1) % 256}mText{i}\u001b[0m\n");
            }
            
            await Task.Delay(1000);
            stopwatch.Stop();
            
            // Assert
            var avgTime = stopwatch.ElapsedMilliseconds / (double)sequences;
            Assert.True(avgTime < 100, 
                $"Average processing time per sequence too high: {avgTime:F1}ms");
            
            // Verify parser throughput
            var snapshot = CurrentSnapshot;
            Assert.NotNull(snapshot);
            Assert.True(snapshot.ParserSequencesPerSecond > 100,
                $"Parser sequences/sec too low: {snapshot.ParserSequencesPerSecond:F0}");
        });
    }
}
