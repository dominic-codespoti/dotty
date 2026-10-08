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

    public BaselineComparer(double regressionThreshold = 0.50)
    {
        _baselines = new Dictionary<string, BaselineThreshold>();
        _regressionThreshold = regressionThreshold;
    }

    public BaselineComparer(string baselineFilePath, double regressionThreshold = 0.50)
        : this(regressionThreshold)
    {
        LoadBaselines(baselineFilePath);
    }

    /// <summary>
    /// Set baseline threshold for a specific benchmark
    /// </summary>
    public void SetBaseline(string benchmarkName, double expectedMeanMs, double minThroughput = 0, double maxAllocationsPerOp = 0)
    {
        _baselines[benchmarkName] = new BaselineThreshold
        {
            ExpectedMeanMs = expectedMeanMs,
            MinThroughput = minThroughput,
            MaxAllocationsPerOp = maxAllocationsPerOp,
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

        // Median latency avoids single-sample outliers. The fixed 75 ns floor absorbs small shared-runner jitter.
        if (baseline.ExpectedMeanMs > 0)
        {
            var maxAllowed = GetAllowedLatencyMs(baseline.ExpectedMeanMs, baseline.RegressionThreshold);
            var comparison = new ThresholdComparison
            {
                Metric = "Median Latency",
                Baseline = baseline.ExpectedMeanMs,
                Actual = result.P50Ms,
                Threshold = maxAllowed,
                Unit = "ms",
                Passed = result.P50Ms <= maxAllowed
            };
            comparisons.Add(comparison);

            if (!comparison.Passed)
            {
                passed = false;
                messages.Add($"Median latency {result.P50Ms:F4}ms exceeds threshold {maxAllowed:F4}ms");
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

        // Allocation counters vary slightly with ArrayPool reuse and tiering; allow a small proportional slack.
        if (baseline.MaxAllocationsPerOp > 0)
        {
            double maxAllowed = GetAllowedAllocationsPerOp(baseline.MaxAllocationsPerOp);
            var comparison = new ThresholdComparison
            {
                Metric = "Allocations/Op",
                Baseline = baseline.MaxAllocationsPerOp,
                Actual = result.AllocatedBytesPerOp,
                Threshold = maxAllowed,
                Unit = "bytes",
                Passed = result.AllocatedBytesPerOp <= maxAllowed
            };
            comparisons.Add(comparison);

            if (!comparison.Passed)
            {
                passed = false;
                messages.Add($"Allocations {result.AllocatedBytesPerOp:F0} bytes/op exceeds threshold {maxAllowed:F0} bytes/op");
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
    public const double AbsoluteLatencyFloorMs = 0.000075;

    // Allocation counters vary slightly with ArrayPool reuse and tiering.
    public const double MinimumAllocationSlackBytes = 64;
    public const double RelativeAllocationSlack = 0.10;

    public static double GetAllowedAllocationsPerOp(double baselineBytes) =>
        baselineBytes + Math.Max(MinimumAllocationSlackBytes, baselineBytes * RelativeAllocationSlack);

    public IReadOnlyCollection<string> BaselineNames => _baselines.Keys;

    public static double GetAllowedLatencyMs(double baselineMs, double relativeTolerance = 0.50) =>
        baselineMs * (1 + relativeTolerance) + AbsoluteLatencyFloorMs;
}

/// <summary>
/// Baseline threshold definition
/// </summary>
public class BaselineThreshold
{
    [JsonPropertyName("expectedMeanMs")]
    public double ExpectedMeanMs { get; set; }


    [JsonPropertyName("minThroughput")]
    public double MinThroughput { get; set; }

    [JsonPropertyName("maxAllocationsPerOp")]
    public double MaxAllocationsPerOp { get; set; }

    [JsonPropertyName("regressionThreshold")]
    public double RegressionThreshold { get; set; } = 0.50;
}

/// <summary>
/// Benchmark result from a single run
/// </summary>
public class BenchmarkResult
{
    public double MeanMs { get; set; }
    public double P50Ms { get; set; }
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
