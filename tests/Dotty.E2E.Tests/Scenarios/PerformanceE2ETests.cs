using System.Diagnostics;
using System.Threading.Tasks;
using Dotty.E2E.Tests.Assertions;
using Xunit;
using Xunit.Abstractions;

namespace Dotty.E2E.Tests.Scenarios;

/// <summary>
/// E2E performance tests for terminal under various loads.
/// These tests measure and assert on performance metrics.
/// </summary>
public class PerformanceE2ETests : E2EPerformanceTestBase
{
    public PerformanceE2ETests(ITestOutputHelper outputHelper) : base("Performance", outputHelper)
    {
    }

    [Fact]
    public async Task Rendering_High_Throughput_Should_Maintain_Performance()
    {
        await RunPerformanceTestAsync(nameof(Rendering_High_Throughput_Should_Maintain_Performance), async () =>
        {
            // Arrange
            var lines = Enumerable.Range(0, 1000).Select(i => $"Performance test line {i}: {new string('A', 80)}");
            var content = string.Join("\n", lines);
            
            // Act
            await SendTextAndWaitAsync(content);
            await Task.Delay(1000); // Let rendering settle
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0);
            
            // Assert performance thresholds
            var snapshot = CurrentSnapshot;
            Assert.NotNull(snapshot);
            
            // Use conservative thresholds for CI
            var thresholds = ShouldRunHeadless() ? PerformanceThresholds.Headless : PerformanceThresholds.Conservative;
            
            PerformanceAssertions.AssertFps(snapshot.Fps, thresholds.MinFps);
            PerformanceAssertions.AssertFrameTime(snapshot.FrameTimeAvg, snapshot.FrameTimeP95, snapshot.FrameTimeP99,
                thresholds.MaxFrameTimeAvgMs, thresholds.MaxFrameTimeP95Ms, thresholds.MaxFrameTimeP99Ms);
        });
    }

    [Fact]
    public async Task Large_Buffer_Should_Be_Handled_Efficiently()
    {
        await RunPerformanceTestAsync(nameof(Large_Buffer_Should_Be_Handled_Efficiently), async () =>
        {
            // Arrange - Generate large content
            var largeContent = string.Join("\n", 
                Enumerable.Range(0, 5000).Select(i => $"Line {i}: {Guid.NewGuid()}"));
            
            // Act
            var stopwatch = Stopwatch.StartNew();
            await SendTextAndWaitAsync(largeContent);
            await Task.Delay(2000); // Give time for processing
            stopwatch.Stop();
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Session should handle large buffer");
            Assert.True(stats.Scrollback?.ScrollbackCount > 100, "Should have scrollback content");
            
            // Assert performance - large buffer should not cause severe degradation
            var snapshot = CurrentSnapshot;
            Assert.NotNull(snapshot);
            
            // Memory should not balloon
            var maxHeapMB = ShouldRunHeadless() ? 1024 : 512;
            PerformanceAssertions.AssertHeapSize(snapshot.HeapSizeBytes, maxHeapSizeBytes: maxHeapMB * 1024 * 1024);
            
            // Processing time should be reasonable
            Assert.True(stopwatch.ElapsedMilliseconds < 30000, 
                $"Large buffer processing took too long: {stopwatch.ElapsedMilliseconds}ms");
        });
    }

    [Fact]
    public async Task Rapid_Resize_Should_Be_Responsive()
    {
        await RunPerformanceTestAsync(nameof(Rapid_Resize_Should_Be_Responsive), async () =>
        {
            // Arrange
            var sizes = new[] { (80, 24), (100, 30), (60, 20), (120, 40), (80, 24) };
            var stopwatch = Stopwatch.StartNew();
            
            // Act - Rapidly resize
            foreach (var (cols, rows) in sizes)
            {
                await App.Commands.ResizeAsync(cols, rows);
                await Task.Delay(100);
            }
            stopwatch.Stop();
            
            // Assert
            Assert.True(stopwatch.ElapsedMilliseconds < 5000, 
                "Rapid resize operations took too long");
                
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0);
            
            // Resize should not severely impact FPS
            var snapshot = CurrentSnapshot;
            if (snapshot != null)
            {
                // Be lenient for resize operations
                PerformanceAssertions.AssertFps(snapshot.Fps, minFps: 15);
            }
        });
    }

    [Fact]
    public async Task Multiple_Tabs_Performance()
    {
        await RunPerformanceTestAsync(nameof(Multiple_Tabs_Performance), async () =>
        {
            // Arrange
            var stopwatch = Stopwatch.StartNew();
            
            // Act - Create multiple tabs
            for (int i = 0; i < 5; i++)
            {
                await App.Commands.CreateTabAsync();
                await Task.Delay(500);
                await SendTextAndWaitAsync($"Tab {i} content");
            }
            
            // Switch between tabs rapidly
            for (int i = 0; i < 10; i++)
            {
                await App.Commands.NextTabAsync();
                await Task.Delay(200);
            }
            stopwatch.Stop();
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.TotalTabs >= 5, "Should have multiple tabs");
            Assert.True(stopwatch.ElapsedMilliseconds < 15000, 
                "Tab operations took too long");
            
            // Tab switching performance
            var snapshot = CurrentSnapshot;
            Assert.NotNull(snapshot);
            
            // Calculate approximate tab switch time
            var avgSwitchTime = (stopwatch.ElapsedMilliseconds - 2500) / 10.0; // Subtract creation delays
            PerformanceAssertions.AssertTabSwitchPerformance(avgSwitchTime, maxAvgSwitchTimeMs: 200);
        });
    }

    [Fact]
    public async Task Startup_Time_Should_Be_Reasonable()
    {
        // This test is special - we restart the app
        await DisposeAsync(); // Clean up first
        
        var stopwatch = Stopwatch.StartNew();
        
        // Re-initialize
        await InitializeAsync();
        
        stopwatch.Stop();
        
        // Assert
        var thresholds = ShouldRunHeadless() 
            ? TimeSpan.FromSeconds(15) 
            : TimeSpan.FromSeconds(10);
            
        PerformanceAssertions.AssertStartupTime(stopwatch.Elapsed, thresholds);
    }

    [Fact]
    public async Task Rapid_Input_Should_Be_Buffered()
    {
        await RunPerformanceTestAsync(nameof(Rapid_Input_Should_Be_Buffered), async () =>
        {
            // Act - Send many characters rapidly
            var stopwatch = Stopwatch.StartNew();
            
            for (int i = 0; i < 1000; i++)
            {
                await App.Commands.SendTextAsync("x");
            }
            
            await Task.Delay(2000); // Allow processing
            stopwatch.Stop();
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0, "Should handle rapid input");
            Assert.True(stopwatch.ElapsedMilliseconds < 5000, 
                "Rapid input took too long to process");
            
            // Check performance metrics
            var snapshot = CurrentSnapshot;
            Assert.NotNull(snapshot);
            
            // Input latency should be reasonable
            PerformanceAssertions.AssertInputLatency(snapshot.InputLatencyAvgMs, snapshot.InputLatencyP95Ms,
                maxAvgMs: 50, maxP95Ms: 100);
        });
    }

    [Fact]
    public async Task Complex_Ansi_Rendering_Performance()
    {
        await RunPerformanceTestAsync(nameof(Complex_Ansi_Rendering_Performance), async () =>
        {
            // Arrange - Generate complex ANSI content
            var ansiSequences = new[]
            {
                "\u001b[31mRed\u001b[0m",
                "\u001b[32mGreen\u001b[0m",
                "\u001b[1mBold\u001b[0m",
                "\u001b[4mUnderline\u001b[0m",
                "\u001b[38;5;196m256-Color\u001b[0m",
                "\u001b[38;2;255;128;0mTrueColor\u001b[0m"
            };
            
            var stopwatch = Stopwatch.StartNew();
            
            // Act - Send many ANSI sequences
            for (int i = 0; i < 100; i++)
            {
                var content = string.Join(" ", ansiSequences.Select((s, idx) => $"{s} Line{i}-{idx}"));
                await App.Commands.InjectAnsiAsync(content + "\n");
            }
            
            await Task.Delay(2000);
            stopwatch.Stop();
            
            // Assert
            var stats = await GetStatsAsync();
            Assert.True(stats.SessionsStarted > 0);
            Assert.True(stopwatch.ElapsedMilliseconds < 10000, 
                "Complex ANSI rendering took too long");
            
            // Check parser performance
            var snapshot = CurrentSnapshot;
            Assert.NotNull(snapshot);
            
            PerformanceAssertions.AssertParserThroughput(
                snapshot.ParserBytesPerSecond,
                snapshot.ParserSequencesPerSecond,
                minBytesPerSec: 50000.0,
                minSequencesPerSec: 100.0);
        });
    }

    [Fact]
    public async Task Memory_Stability_Under_Load()
    {
        await RunPerformanceTestAsync(nameof(Memory_Stability_Under_Load), async () =>
        {
            // Arrange - Perform operations and check memory doesn't grow unbounded
            var initialStats = await GetStatsAsync();
            
            // Act - Generate moderate load
            for (int i = 0; i < 100; i++)
            {
                await SendTextAndWaitAsync($"Line {i}: {new string('X', 100)}\n");
            }
            
            await Task.Delay(1000);
            
            // Force GC to get stable memory reading
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            
            await Task.Delay(500);
            
            // Assert - Memory should be stable
            var snapshot = CurrentSnapshot;
            Assert.NotNull(snapshot);
            
            var maxHeapMB = ShouldRunHeadless() ? 1024 : 512;
            PerformanceAssertions.AssertHeapSize(snapshot.HeapSizeBytes, maxHeapSizeBytes: maxHeapMB * 1024 * 1024);
            
            // GC collections should be reasonable
            PerformanceAssertions.AssertGCCollections(
                snapshot.Gen0Collections, 
                snapshot.Gen1Collections, 
                snapshot.Gen2Collections,
                maxGen0: 100, maxGen1: 50, maxGen2: 10);
        });
    }

    [Fact]
    public async Task Scrollback_Scrolling_Performance()
    {
        await RunPerformanceTestAsync(nameof(Scrollback_Scrolling_Performance), async () =>
        {
            // Arrange - Fill scrollback buffer
            var lines = Enumerable.Range(0, 200).Select(i => $"Scrollback line {i}: {new string('A', 80)}");
            var content = string.Join("\n", lines);
            await SendTextAndWaitAsync(content);
            await Task.Delay(1000);
            
            // Act - Scroll through buffer multiple times
            var scrollOperations = 50;
            var stopwatch = Stopwatch.StartNew();
            
            for (int i = 0; i < scrollOperations; i++)
            {
                await App.Commands.ScrollAsync(5);
                await Task.Delay(50);
                await App.Commands.ScrollAsync(-5);
                await Task.Delay(50);
            }
            
            stopwatch.Stop();
            
            // Assert
            var avgScrollTime = stopwatch.ElapsedMilliseconds / (double)(scrollOperations * 2);
            Assert.True(avgScrollTime < 100, 
                $"Average scroll time too high: {avgScrollTime:F1}ms");
            
            // Check scroll performance metrics
            var snapshot = CurrentSnapshot;
            Assert.NotNull(snapshot);
            
            PerformanceAssertions.AssertScrollPerformance(
                snapshot.ScrollLinesPerSecond,
                snapshot.ScrollTimeAvgMs,
                minLinesPerSec: 10.0,
                maxAvgTimeMs: 100.0);
        });
    }

    [Fact]
    public async Task Cell_Update_Rate_Should_Be_High()
    {
        await RunPerformanceTestAsync(nameof(Cell_Update_Rate_Should_Be_High), async () =>
        {
            // Arrange - Create content that requires many cell updates
            var rows = 24;
            var cols = 80;
            var updatesPerFrame = rows * cols; // Full screen update
            
            // Act - Perform multiple full screen updates
            var frames = 10;
            for (int i = 0; i < frames; i++)
            {
                var screenContent = string.Join("\n", 
                    Enumerable.Range(0, rows).Select(r => $"Frame{i} Row{r}: {new string('X', cols - 20)}"));
                await SendTextAndWaitAsync(screenContent + "\n");
                await Task.Delay(100);
            }
            
            await Task.Delay(1000);
            
            // Assert
            var snapshot = CurrentSnapshot;
            Assert.NotNull(snapshot);
            
            // Cell updates should be high
            var minUpdates = updatesPerFrame * 30; // At least 30 FPS worth of updates
            PerformanceAssertions.AssertCellUpdateRate(snapshot.CellUpdatesPerSecond, minUpdatesPerSec: minUpdates);
        });
    }
}
