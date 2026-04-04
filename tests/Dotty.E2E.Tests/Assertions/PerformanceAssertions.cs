using System;
using System.Threading.Tasks;
using Xunit;

namespace Dotty.E2E.Tests.Assertions;

/// <summary>
/// Assertions for performance metrics and thresholds.
/// </summary>
public static class PerformanceAssertions
{
    // Default thresholds
    private const double DefaultMinFps = 30.0;
    private const double DefaultMaxFrameTimeP95Ms = 33.33; // 30 FPS
    private const double DefaultMaxFrameTimeP99Ms = 50.0;
    private const double DefaultMinParserBytesPerSec = 100000.0; // 100KB/s
    private const double DefaultMinParserSequencesPerSec = 1000.0; // 1000 sequences/s
    private const double DefaultMaxAllocationsPerOperation = 100.0;
    private const double DefaultMaxInputLatencyP95Ms = 50.0;
    private const double DefaultRegressionTolerancePercentage = 10.0;
    
    /// <summary>
    /// Asserts that the FPS meets the minimum threshold.
    /// </summary>
    public static void AssertFps(double actualFps, double minFps = DefaultMinFps, string? message = null)
    {
        if (actualFps < minFps)
        {
            var errorMessage = message ?? $"FPS too low: {actualFps:F1} (minimum: {minFps:F1})";
            Assert.True(false, errorMessage);
        }
    }
    
    /// <summary>
    /// Asserts that the average frame time meets the threshold.
    /// </summary>
    public static void AssertFrameTimeAvg(double actualAvgMs, double maxAvgMs = DefaultMaxFrameTimeP95Ms, string? message = null)
    {
        if (actualAvgMs > maxAvgMs)
        {
            var errorMessage = message ?? $"Average frame time too high: {actualAvgMs:F2}ms (maximum: {maxAvgMs:F2}ms)";
            Assert.True(false, errorMessage);
        }
    }
    
    /// <summary>
    /// Asserts that the p95 frame time meets the threshold.
    /// </summary>
    public static void AssertFrameTimeP95(double actualP95Ms, double maxP95Ms = DefaultMaxFrameTimeP95Ms, string? message = null)
    {
        if (actualP95Ms > maxP95Ms)
        {
            var errorMessage = message ?? $"P95 frame time too high: {actualP95Ms:F2}ms (maximum: {maxP95Ms:F2}ms)";
            Assert.True(false, errorMessage);
        }
    }
    
    /// <summary>
    /// Asserts that the p99 frame time meets the threshold.
    /// </summary>
    public static void AssertFrameTimeP99(double actualP99Ms, double maxP99Ms = DefaultMaxFrameTimeP99Ms, string? message = null)
    {
        if (actualP99Ms > maxP99Ms)
        {
            var errorMessage = message ?? $"P99 frame time too high: {actualP99Ms:F2}ms (maximum: {maxP99Ms:F2}ms)";
            Assert.True(false, errorMessage);
        }
    }
    
    /// <summary>
    /// Asserts that all frame time percentiles meet thresholds.
    /// </summary>
    public static void AssertFrameTime(double actualAvgMs, double actualP95Ms, double actualP99Ms, 
        double maxAvgMs = DefaultMaxFrameTimeP95Ms, double maxP95Ms = DefaultMaxFrameTimeP95Ms, 
        double maxP99Ms = DefaultMaxFrameTimeP99Ms, string? testName = null)
    {
        var prefix = testName != null ? $"[{testName}] " : "";
        
        AssertFrameTimeAvg(actualAvgMs, maxAvgMs, $"{prefix}Average frame time too high: {actualAvgMs:F2}ms (maximum: {maxAvgMs:F2}ms)");
        AssertFrameTimeP95(actualP95Ms, maxP95Ms, $"{prefix}P95 frame time too high: {actualP95Ms:F2}ms (maximum: {maxP95Ms:F2}ms)");
        AssertFrameTimeP99(actualP99Ms, maxP99Ms, $"{prefix}P99 frame time too high: {actualP99Ms:F2}ms (maximum: {maxP99Ms:F2}ms)");
    }
    
    /// <summary>
    /// Asserts that parser throughput meets the minimum thresholds.
    /// </summary>
    public static void AssertParserThroughput(double bytesPerSecond, double sequencesPerSecond,
        double minBytesPerSec = DefaultMinParserBytesPerSec, double minSequencesPerSec = DefaultMinParserSequencesPerSec, 
        string? message = null)
    {
        var errors = new System.Collections.Generic.List<string>();
        
        if (bytesPerSecond < minBytesPerSec)
        {
            errors.Add($"Parser bytes/sec too low: {bytesPerSecond:F0} (minimum: {minBytesPerSec:F0})");
        }
        
        if (sequencesPerSecond < minSequencesPerSec)
        {
            errors.Add($"Parser sequences/sec too low: {sequencesPerSecond:F0} (minimum: {minSequencesPerSec:F0})");
        }
        
        if (errors.Count > 0)
        {
            var errorMessage = message ?? string.Join("; ", errors);
            Assert.True(false, errorMessage);
        }
    }
    
    /// <summary>
    /// Asserts that memory allocations per operation are within acceptable limits.
    /// </summary>
    public static void AssertMemoryStability(long allocationsPerOperation, double maxAllocationsPerOp = DefaultMaxAllocationsPerOperation, 
        string? message = null)
    {
        if (allocationsPerOperation > maxAllocationsPerOp)
        {
            var errorMessage = message ?? $"Too many allocations per operation: {allocationsPerOperation} (maximum: {maxAllocationsPerOp})";
            Assert.True(false, errorMessage);
        }
    }
    
    /// <summary>
    /// Asserts that heap size is within acceptable limits.
    /// </summary>
    public static void AssertHeapSize(long heapSizeBytes, long maxHeapSizeBytes, string? message = null)
    {
        if (heapSizeBytes > maxHeapSizeBytes)
        {
            var heapMb = heapSizeBytes / (1024.0 * 1024.0);
            var maxHeapMb = maxHeapSizeBytes / (1024.0 * 1024.0);
            var errorMessage = message ?? $"Heap size too large: {heapMb:F1}MB (maximum: {maxHeapMb:F1}MB)";
            Assert.True(false, errorMessage);
        }
    }
    
    /// <summary>
    /// Asserts that GC collections are within acceptable limits.
    /// </summary>
    public static void AssertGCCollections(int gen0, int gen1, int gen2, int maxGen0, int maxGen1, int maxGen2, 
        string? message = null)
    {
        var errors = new System.Collections.Generic.List<string>();
        
        if (gen0 > maxGen0)
            errors.Add($"Gen0 collections too high: {gen0} (maximum: {maxGen0})");
        if (gen1 > maxGen1)
            errors.Add($"Gen1 collections too high: {gen1} (maximum: {maxGen1})");
        if (gen2 > maxGen2)
            errors.Add($"Gen2 collections too high: {gen2} (maximum: {maxGen2})");
        
        if (errors.Count > 0)
        {
            var errorMessage = message ?? string.Join("; ", errors);
            Assert.True(false, errorMessage);
        }
    }
    
    /// <summary>
    /// Asserts that input latency is within acceptable limits.
    /// </summary>
    public static void AssertInputLatency(double avgLatencyMs, double p95LatencyMs, 
        double maxAvgMs = 16.67, double maxP95Ms = DefaultMaxInputLatencyP95Ms, string? message = null)
    {
        var errors = new System.Collections.Generic.List<string>();
        
        if (avgLatencyMs > maxAvgMs)
            errors.Add($"Average input latency too high: {avgLatencyMs:F2}ms (maximum: {maxAvgMs:F2}ms)");
        if (p95LatencyMs > maxP95Ms)
            errors.Add($"P95 input latency too high: {p95LatencyMs:F2}ms (maximum: {maxP95Ms:F2}ms)");
        
        if (errors.Count > 0)
        {
            var errorMessage = message ?? string.Join("; ", errors);
            Assert.True(false, errorMessage);
        }
    }
    
    /// <summary>
    /// Asserts that scroll performance meets the threshold.
    /// </summary>
    public static void AssertScrollPerformance(double linesPerSecond, double avgTimeMs, 
        double minLinesPerSec = 60.0, double maxAvgTimeMs = 16.67, string? message = null)
    {
        var errors = new System.Collections.Generic.List<string>();
        
        if (linesPerSecond < minLinesPerSec)
            errors.Add($"Scroll rate too low: {linesPerSecond:F0} lines/sec (minimum: {minLinesPerSec:F0})");
        if (avgTimeMs > maxAvgTimeMs)
            errors.Add($"Scroll time too high: {avgTimeMs:F2}ms (maximum: {maxAvgTimeMs:F2}ms)");
        
        if (errors.Count > 0)
        {
            var errorMessage = message ?? string.Join("; ", errors);
            Assert.True(false, errorMessage);
        }
    }
    
    /// <summary>
    /// Asserts that cell update rate meets the threshold.
    /// </summary>
    public static void AssertCellUpdateRate(double updatesPerSecond, double minUpdatesPerSec = 100000.0, 
        string? message = null)
    {
        if (updatesPerSecond < minUpdatesPerSec)
        {
            var errorMessage = message ?? $"Cell update rate too low: {updatesPerSecond:F0}/sec (minimum: {minUpdatesPerSec:F0})";
            Assert.True(false, errorMessage);
        }
    }
    
    /// <summary>
    /// Asserts that there is no performance regression from the baseline.
    /// </summary>
    public static void AssertNoPerformanceRegression(string testName, 
        double baselineFps, double currentFps,
        double baselineFrameTimeP95, double currentFrameTimeP95,
        double baselineMemory, double currentMemory,
        double tolerancePercentage = DefaultRegressionTolerancePercentage,
        string? message = null)
    {
        var regressions = new System.Collections.Generic.List<string>();
        
        // FPS regression (lower is bad)
        if (baselineFps > 0)
        {
            var fpsChange = ((currentFps - baselineFps) / baselineFps) * 100;
            if (fpsChange < -tolerancePercentage)
            {
                regressions.Add($"FPS regression: {fpsChange:F1}% (baseline: {baselineFps:F1}, current: {currentFps:F1})");
            }
        }
        
        // Frame time regression (higher is bad)
        if (baselineFrameTimeP95 > 0)
        {
            var frameTimeChange = ((currentFrameTimeP95 - baselineFrameTimeP95) / baselineFrameTimeP95) * 100;
            if (frameTimeChange > tolerancePercentage)
            {
                regressions.Add($"Frame time regression: +{frameTimeChange:F1}% (baseline: {baselineFrameTimeP95:F2}ms p95, current: {currentFrameTimeP95:F2}ms p95)");
            }
        }
        
        // Memory regression (higher is bad) - allow 2x tolerance for memory
        if (baselineMemory > 0)
        {
            var memoryChange = ((currentMemory - baselineMemory) / baselineMemory) * 100;
            if (memoryChange > tolerancePercentage * 2)
            {
                var baselineMb = baselineMemory / (1024.0 * 1024.0);
                var currentMb = currentMemory / (1024.0 * 1024.0);
                regressions.Add($"Memory regression: +{memoryChange:F1}% (baseline: {baselineMb:F1}MB, current: {currentMb:F1}MB)");
            }
        }
        
        if (regressions.Count > 0)
        {
            var errorMessage = message ?? $"Performance regressions detected in '{testName}':\n" + string.Join("\n", regressions);
            Assert.True(false, errorMessage);
        }
    }
    
    /// <summary>
    /// Asserts that startup time meets the threshold.
    /// </summary>
    public static void AssertStartupTime(TimeSpan startupTime, TimeSpan maxStartupTime, string? message = null)
    {
        if (startupTime > maxStartupTime)
        {
            var errorMessage = message ?? $"Startup time too long: {startupTime.TotalMilliseconds:F0}ms (maximum: {maxStartupTime.TotalMilliseconds:F0}ms)";
            Assert.True(false, errorMessage);
        }
    }
    
    /// <summary>
    /// Asserts that tab switching performance meets the threshold.
    /// </summary>
    public static void AssertTabSwitchPerformance(double avgSwitchTimeMs, double maxAvgSwitchTimeMs = 100.0, 
        string? message = null)
    {
        if (avgSwitchTimeMs > maxAvgSwitchTimeMs)
        {
            var errorMessage = message ?? $"Tab switch time too high: {avgSwitchTimeMs:F2}ms (maximum: {maxAvgSwitchTimeMs:F2}ms)";
            Assert.True(false, errorMessage);
        }
    }
    
    /// <summary>
    /// Asserts overall performance snapshot against multiple thresholds.
    /// </summary>
    public static void AssertPerformanceSnapshot(
        double fps, double frameTimeAvg, double frameTimeP95, double frameTimeP99,
        double parserBytesPerSec, long heapSizeBytes,
        PerformanceThresholds thresholds)
    {
        AssertFps(fps, thresholds.MinFps);
        AssertFrameTime(frameTimeAvg, frameTimeP95, frameTimeP99, 
            thresholds.MaxFrameTimeAvgMs, thresholds.MaxFrameTimeP95Ms, thresholds.MaxFrameTimeP99Ms);
        
        if (parserBytesPerSec < thresholds.MinParserBytesPerSec)
        {
            Assert.True(false, $"Parser throughput too low: {parserBytesPerSec:F0} bytes/sec (minimum: {thresholds.MinParserBytesPerSec:F0})");
        }
        
        if (heapSizeBytes > thresholds.MaxHeapSizeBytes)
        {
            var heapMb = heapSizeBytes / (1024.0 * 1024.0);
            var maxHeapMb = thresholds.MaxHeapSizeBytes / (1024.0 * 1024.0);
            Assert.True(false, $"Heap size too large: {heapMb:F1}MB (maximum: {maxHeapMb:F1}MB)");
        }
    }
}

/// <summary>
/// Configuration class for performance thresholds.
/// </summary>
public class PerformanceThresholds
{
    public double MinFps { get; init; } = 30.0;
    public double MaxFrameTimeAvgMs { get; init; } = 16.67; // 60 FPS
    public double MaxFrameTimeP95Ms { get; init; } = 33.33; // 30 FPS
    public double MaxFrameTimeP99Ms { get; init; } = 50.0;
    public double MinParserBytesPerSec { get; init; } = 100000.0;
    public double MinParserSequencesPerSec { get; init; } = 1000.0;
    public long MaxHeapSizeBytes { get; init; } = 512 * 1024 * 1024; // 512MB
    public double MaxAllocationsPerOp { get; init; } = 100.0;
    public double MaxInputLatencyAvgMs { get; init; } = 16.67;
    public double MaxInputLatencyP95Ms { get; init; } = 50.0;
    public double MinScrollLinesPerSec { get; init; } = 60.0;
    public double MaxScrollTimeAvgMs { get; init; } = 16.67;
    public double MinCellUpdatesPerSec { get; init; } = 100000.0;
    public double MaxStartupTimeMs { get; init; } = 10000.0;
    public double RegressionTolerancePercentage { get; init; } = 10.0;
    
    /// <summary>
    /// Creates conservative thresholds suitable for CI environments.
    /// </summary>
    public static PerformanceThresholds Conservative => new()
    {
        MinFps = 15.0,
        MaxFrameTimeAvgMs = 66.67,
        MaxFrameTimeP95Ms = 100.0,
        MaxFrameTimeP99Ms = 150.0,
        MinParserBytesPerSec = 50000.0,
        MaxHeapSizeBytes = 1024 * 1024 * 1024, // 1GB
        MaxInputLatencyP95Ms = 100.0,
        RegressionTolerancePercentage = 15.0
    };
    
    /// <summary>
    /// Creates aggressive thresholds for high-performance testing.
    /// </summary>
    public static PerformanceThresholds Aggressive => new()
    {
        MinFps = 60.0,
        MaxFrameTimeAvgMs = 8.33,
        MaxFrameTimeP95Ms = 16.67,
        MaxFrameTimeP99Ms = 33.33,
        MinParserBytesPerSec = 500000.0,
        MinParserSequencesPerSec = 5000.0,
        MaxAllocationsPerOp = 10.0,
        MaxInputLatencyAvgMs = 8.33,
        MaxInputLatencyP95Ms = 16.67,
        MaxStartupTimeMs = 5000.0,
        RegressionTolerancePercentage = 5.0
    };
    
    /// <summary>
    /// Creates thresholds for headless/CI environments.
    /// </summary>
    public static PerformanceThresholds Headless => new()
    {
        MinFps = 1.0, // Less strict for headless
        MaxFrameTimeAvgMs = 100.0,
        MaxFrameTimeP95Ms = 200.0,
        MinParserBytesPerSec = 100000.0,
        MaxHeapSizeBytes = 512 * 1024 * 1024,
        RegressionTolerancePercentage = 20.0
    };
}
