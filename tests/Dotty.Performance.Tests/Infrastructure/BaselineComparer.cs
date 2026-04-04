using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dotty.Performance.Tests.Infrastructure;

/// <summary>
/// Compares benchmark results against baseline thresholds
/// </summary>
public class BaselineComparer
{
    private readonly Dictionary<string, BaselineThreshold> _baselines;
    private readonly double _regressionThreshold;

    public BaselineComparer(double regressionThreshold = 0.10)
    {
        _baselines = new Dictionary<string, BaselineThreshold>();
        _regressionThreshold = regressionThreshold;
    }

    public BaselineComparer(string baselineFilePath, double regressionThreshold = 0.10)
        : this(regressionThreshold)
    {
        LoadBaselines(baselineFilePath);
    }

    /// <summary>
    /// Set baseline threshold for a specific benchmark
    /// </summary>
    public void SetBaseline(string benchmarkName, double expectedMeanMs, double maxLatencyMs = 0, double minThroughput = 0)
    {
        _baselines[benchmarkName] = new BaselineThreshold
        {
            ExpectedMeanMs = expectedMeanMs,
            MaxLatencyMs = maxLatencyMs > 0 ? maxLatencyMs : expectedMeanMs * 2,
            MinThroughput = minThroughput,
            RegressionThreshold = _regressionThreshold
        };
    }

    /// <summary>
    /// Load baselines from JSON file
    /// </summary>
    public void LoadBaselines(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        var json = File.ReadAllText(filePath);
        var baselines = JsonSerializer.Deserialize<Dictionary<string, BaselineThreshold>>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        if (baselines != null)
        {
            foreach (var baseline in baselines)
            {
                _baselines[baseline.Key] = baseline.Value;
            }
        }
    }

    /// <summary>
    /// Save current baselines to JSON file
    /// </summary>
    public void SaveBaselines(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(_baselines, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });

        File.WriteAllText(filePath, json);
    }

    /// <summary>
    /// Compare a benchmark result against its baseline
    /// </summary>
    public ComparisonResult Compare(string benchmarkName, BenchmarkResult result)
    {
        if (!_baselines.TryGetValue(benchmarkName, out var baseline))
        {
            return new ComparisonResult
            {
                HasBaseline = false,
                Passed = true,
                Message = $"No baseline defined for {benchmarkName}"
            };
        }

        var comparisons = new List<ThresholdComparison>();
        bool passed = true;
        var messages = new List<string>();

        // Check mean latency
        if (baseline.ExpectedMeanMs > 0)
        {
            var maxAllowed = baseline.ExpectedMeanMs * (1 + baseline.RegressionThreshold);
            var comparison = new ThresholdComparison
            {
                Metric = "Mean Latency",
                Baseline = baseline.ExpectedMeanMs,
                Actual = result.MeanMs,
                Threshold = maxAllowed,
                Unit = "ms",
                Passed = result.MeanMs <= maxAllowed
            };
            comparisons.Add(comparison);
            
            if (!comparison.Passed)
            {
                passed = false;
                messages.Add($"Mean latency {result.MeanMs:F2}ms exceeds threshold {maxAllowed:F2}ms");
            }
        }

        // Check P95 latency
        if (baseline.MaxLatencyMs > 0)
        {
            var comparison = new ThresholdComparison
            {
                Metric = "P95 Latency",
                Baseline = baseline.MaxLatencyMs,
                Actual = result.P95Ms,
                Threshold = baseline.MaxLatencyMs,
                Unit = "ms",
                Passed = result.P95Ms <= baseline.MaxLatencyMs
            };
            comparisons.Add(comparison);
            
            if (!comparison.Passed)
            {
                passed = false;
                messages.Add($"P95 latency {result.P95Ms:F2}ms exceeds threshold {baseline.MaxLatencyMs:F2}ms");
            }
        }

        // Check throughput
        if (baseline.MinThroughput > 0 && result.ThroughputOpsPerSec > 0)
        {
            var comparison = new ThresholdComparison
            {
                Metric = "Throughput",
                Baseline = baseline.MinThroughput,
                Actual = result.ThroughputOpsPerSec,
                Threshold = baseline.MinThroughput,
                Unit = "ops/sec",
                Passed = result.ThroughputOpsPerSec >= baseline.MinThroughput
            };
            comparisons.Add(comparison);
            
            if (!comparison.Passed)
            {
                passed = false;
                messages.Add($"Throughput {result.ThroughputOpsPerSec:F0} ops/sec below minimum {baseline.MinThroughput:F0} ops/sec");
            }
        }

        // Check allocations if baseline defined
        if (baseline.MaxAllocationsPerOp > 0)
        {
            var comparison = new ThresholdComparison
            {
                Metric = "Allocations/Op",
                Baseline = baseline.MaxAllocationsPerOp,
                Actual = result.AllocatedBytesPerOp,
                Threshold = baseline.MaxAllocationsPerOp,
                Unit = "bytes",
                Passed = result.AllocatedBytesPerOp <= baseline.MaxAllocationsPerOp
            };
            comparisons.Add(comparison);
            
            if (!comparison.Passed)
            {
                passed = false;
                messages.Add($"Allocations {result.AllocatedBytesPerOp:F0} bytes/op exceeds threshold {baseline.MaxAllocationsPerOp:F0} bytes/op");
            }
        }

        return new ComparisonResult
        {
            HasBaseline = true,
            BenchmarkName = benchmarkName,
            Passed = passed,
            Message = passed ? "All thresholds passed" : string.Join("; ", messages),
            Comparisons = comparisons.ToArray()
        };
    }

    /// <summary>
    /// Get default baselines for common terminal operations
    /// </summary>
    public static Dictionary<string, BaselineThreshold> GetDefaultBaselines()
    {
        return new Dictionary<string, BaselineThreshold>
        {
            // Parser benchmarks
            ["Parser_PlainText_1KB"] = new() { ExpectedMeanMs = 0.1, MaxAllocationsPerOp = 1024 },
            ["Parser_PlainText_10KB"] = new() { ExpectedMeanMs = 0.5, MaxAllocationsPerOp = 4096 },
            ["Parser_PlainText_100KB"] = new() { ExpectedMeanMs = 5.0, MaxAllocationsPerOp = 16384 },
            ["Parser_AnsiBasic_1KB"] = new() { ExpectedMeanMs = 0.15, MaxAllocationsPerOp = 2048 },
            ["Parser_AnsiExtended_1KB"] = new() { ExpectedMeanMs = 0.2, MaxAllocationsPerOp = 3072 },
            ["Parser_AnsiTrueColor_1KB"] = new() { ExpectedMeanMs = 0.25, MaxAllocationsPerOp = 4096 },
            
            // Rendering benchmarks
            ["Render_FullScreen_80x24"] = new() { ExpectedMeanMs = 1.0, MaxLatencyMs = 5.0 },
            ["Render_FullScreen_120x40"] = new() { ExpectedMeanMs = 2.0, MaxLatencyMs = 10.0 },
            ["Render_Scroll_LargeBuffer"] = new() { ExpectedMeanMs = 0.5, MaxLatencyMs = 2.0 },
            ["Render_PartialUpdate"] = new() { ExpectedMeanMs = 0.2, MaxLatencyMs = 1.0 },
            
            // Memory benchmarks
            ["Memory_GridAllocation_80x24"] = new() { MaxAllocationsPerOp = 10000 },
            ["Memory_GridAllocation_120x40"] = new() { MaxAllocationsPerOp = 20000 },
            ["Memory_BufferResize"] = new() { MaxAllocationsPerOp = 50000 },
            ["Memory_ScrollbackCompaction"] = new() { MaxAllocationsPerOp = 100000 },
            
            // Throughput benchmarks
            ["Throughput_Sustained_10MB"] = new() { MinThroughput = 10000000 }, // 10 MB/s
            ["Throughput_Peak_ShortBurst"] = new() { MinThroughput = 50000000 }, // 50 MB/s
            
            // Startup benchmarks
            ["Startup_Cold"] = new() { ExpectedMeanMs = 500, MaxLatencyMs = 1000 },
            ["Startup_Warm"] = new() { ExpectedMeanMs = 100, MaxLatencyMs = 200 },
        };
    }
}

/// <summary>
/// Baseline threshold definition
/// </summary>
public class BaselineThreshold
{
    [JsonPropertyName("expectedMeanMs")]
    public double ExpectedMeanMs { get; set; }
    
    [JsonPropertyName("maxLatencyMs")]
    public double MaxLatencyMs { get; set; }
    
    [JsonPropertyName("minThroughput")]
    public double MinThroughput { get; set; }
    
    [JsonPropertyName("maxAllocationsPerOp")]
    public double MaxAllocationsPerOp { get; set; }
    
    [JsonPropertyName("regressionThreshold")]
    public double RegressionThreshold { get; set; } = 0.10;
}

/// <summary>
/// Benchmark result from a single run
/// </summary>
public class BenchmarkResult
{
    public double MeanMs { get; set; }
    public double P50Ms { get; set; }
    public double P95Ms { get; set; }
    public double P99Ms { get; set; }
    public double StdDevMs { get; set; }
    public double ThroughputOpsPerSec { get; set; }
    public double AllocatedBytesPerOp { get; set; }
    public int Gen0Collections { get; set; }
    public int Gen1Collections { get; set; }
    public int Gen2Collections { get; set; }
}

/// <summary>
/// Result of comparing a benchmark against baseline
/// </summary>
public class ComparisonResult
{
    public bool HasBaseline { get; set; }
    public string BenchmarkName { get; set; } = "";
    public bool Passed { get; set; }
    public string Message { get; set; } = "";
    public ThresholdComparison[] Comparisons { get; set; } = Array.Empty<ThresholdComparison>();
}

/// <summary>
/// Single threshold comparison
/// </summary>
public class ThresholdComparison
{
    public string Metric { get; set; } = "";
    public double Baseline { get; set; }
    public double Actual { get; set; }
    public double Threshold { get; set; }
    public string Unit { get; set; } = "";
    public bool Passed { get; set; }
    public double PercentageDiff => Baseline > 0 ? ((Actual - Baseline) / Baseline) * 100 : 0;

    public override string ToString() =>
        $"{Metric}: {Actual:F2} {Unit} (baseline: {Baseline:F2}, diff: {PercentageDiff:F1}%)";
}
