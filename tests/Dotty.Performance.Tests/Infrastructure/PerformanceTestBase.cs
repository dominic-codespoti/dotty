using System.Diagnostics;
using System.Runtime;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

namespace Dotty.Performance.Tests.Infrastructure;

/// <summary>
/// Base class for all performance benchmarks providing common setup,
/// measurement utilities, and infrastructure.
/// </summary>
public abstract class PerformanceTestBase
{
    private readonly Stopwatch _stopwatch = new();
    private long _gcCount0, _gcCount1, _gcCount2;
    private long _allocatedBytes;

    /// <summary>
    /// Global setup executed once per benchmark run
    /// </summary>
    [GlobalSetup]
    public virtual void GlobalSetup()
    {
        // Force GC to get clean state
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        
        // Record baseline GC counts
        _gcCount0 = GC.CollectionCount(0);
        _gcCount1 = GC.CollectionCount(1);
        _gcCount2 = GC.CollectionCount(2);
        _allocatedBytes = GC.GetTotalAllocatedBytes();
        
        // Disable GC pressure during benchmarks for more consistent results
        GC.TryStartNoGCRegion(100 * 1024 * 1024, true);
    }

    /// <summary>
    /// Global cleanup executed once per benchmark run
    /// </summary>
    [GlobalCleanup]
    public virtual void GlobalCleanup()
    {
        try
        {
            GC.EndNoGCRegion();
        }
        catch (InvalidOperationException)
        {
            // NoGCRegion wasn't started or was already ended
        }
        
        // Force final cleanup
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }

    /// <summary>
    /// Per-iteration setup
    /// </summary>
    [IterationSetup]
    public virtual void IterationSetup()
    {
        _stopwatch.Restart();
    }

    /// <summary>
    /// Per-iteration cleanup
    /// </summary>
    [IterationCleanup]
    public virtual void IterationCleanup()
    {
        _stopwatch.Stop();
    }

    /// <summary>
    /// Start high-resolution timing
    /// </summary>
    protected void StartTiming() => _stopwatch.Restart();

    /// <summary>
    /// Stop timing and return elapsed milliseconds
    /// </summary>
    protected double StopTiming()
    {
        _stopwatch.Stop();
        return _stopwatch.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// Get current GC collection counts
    /// </summary>
    protected (long Gen0, long Gen1, long Gen2) GetGCCollectionCounts() => 
        (GC.CollectionCount(0) - _gcCount0, 
         GC.CollectionCount(1) - _gcCount1, 
         GC.CollectionCount(2) - _gcCount2);

    /// <summary>
    /// Get bytes allocated since baseline
    /// </summary>
    protected long GetAllocatedBytes() => 
        GC.GetTotalAllocatedBytes() - _allocatedBytes;

    /// <summary>
    /// Calculate throughput in operations per second
    /// </summary>
    protected double CalculateThroughput(int operations, double elapsedMs) =>
        operations / (elapsedMs / 1000.0);

    /// <summary>
    /// Format bytes to human-readable string
    /// </summary>
    protected static string FormatBytes(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB" };
        int suffixIndex = 0;
        double value = bytes;
        
        while (value >= 1024 && suffixIndex < suffixes.Length - 1)
        {
            value /= 1024;
            suffixIndex++;
        }
        
        return $"{value:F2} {suffixes[suffixIndex]}";
    }

    /// <summary>
    /// Validate that throughput meets minimum threshold
    /// </summary>
    protected void ValidateThroughput(double actualThroughput, double minThroughput, string operation)
    {
        if (actualThroughput < minThroughput)
        {
            throw new PerformanceThresholdException(
                $"{operation} throughput {actualThroughput:F0} ops/sec is below minimum {minThroughput:F0} ops/sec");
        }
    }

    /// <summary>
    /// Validate that latency meets maximum threshold
    /// </summary>
    protected void ValidateLatency(double actualLatencyMs, double maxLatencyMs, string operation)
    {
        if (actualLatencyMs > maxLatencyMs)
        {
            throw new PerformanceThresholdException(
                $"{operation} latency {actualLatencyMs:F2}ms exceeds maximum {maxLatencyMs:F2}ms");
        }
    }

    /// <summary>
    /// Run a warmup iteration to ensure JIT compilation
    /// </summary>
    protected void Warmup(Action operation, int iterations = 10)
    {
        for (int i = 0; i < iterations; i++)
        {
            operation();
        }
        
        // Force JIT compilation to complete
        GC.Collect(0, GCCollectionMode.Default, blocking: false);
    }

    /// <summary>
    /// Calculate statistics for a set of measurements
    /// </summary>
    protected static Statistics CalculateStatistics(List<double> measurements)
    {
        if (measurements.Count == 0)
            return new Statistics(0, 0, 0, 0, 0);

        var sorted = measurements.OrderBy(x => x).ToList();
        double sum = sorted.Sum();
        double mean = sum / sorted.Count;
        double variance = sorted.Sum(x => Math.Pow(x - mean, 2)) / sorted.Count;
        double stdDev = Math.Sqrt(variance);
        
        double p50 = sorted[sorted.Count * 50 / 100];
        double p95 = sorted[sorted.Count * 95 / 100];
        double p99 = sorted.Count >= 100 ? sorted[sorted.Count * 99 / 100] : p95;
        
        return new Statistics(mean, stdDev, p50, p95, p99);
    }

    /// <summary>
    /// Run multiple iterations and collect measurements
    /// </summary>
    protected List<double> CollectMeasurements(Action operation, int iterations)
    {
        var measurements = new List<double>(iterations);
        
        for (int i = 0; i < iterations; i++)
        {
            GC.Collect(0, GCCollectionMode.Default, blocking: false);
            
            _stopwatch.Restart();
            operation();
            _stopwatch.Stop();
            
            measurements.Add(_stopwatch.Elapsed.TotalMilliseconds);
        }
        
        return measurements;
    }
}

/// <summary>
/// Statistical summary of performance measurements
/// </summary>
public readonly struct Statistics
{
    public readonly double Mean;
    public readonly double StdDev;
    public readonly double P50;
    public readonly double P95;
    public readonly double P99;

    public Statistics(double mean, double stdDev, double p50, double p95, double p99)
    {
        Mean = mean;
        StdDev = stdDev;
        P50 = p50;
        P95 = p95;
        P99 = p99;
    }

    public override string ToString() =>
        $"Mean: {Mean:F3}ms, StdDev: {StdDev:F3}ms, P50: {P50:F3}ms, P95: {P95:F3}ms, P99: {P99:F3}ms";
}

/// <summary>
/// Exception thrown when performance thresholds are not met
/// </summary>
public class PerformanceThresholdException : Exception
{
    public PerformanceThresholdException(string message) : base(message) { }
    public PerformanceThresholdException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Utility class for measuring operation counts
/// </summary>
public static class OperationCounter
{
    private static long _count;

    public static void Increment() => Interlocked.Increment(ref _count);
    public static long GetCount() => Interlocked.Read(ref _count);
    public static void Reset() => Interlocked.Exchange(ref _count, 0);
}
