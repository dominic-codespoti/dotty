using System.Diagnostics;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Dotty.Performance.Tests.Benchmarks;
using Dotty.Performance.Tests.Infrastructure;
using Dotty.Performance.Tests.Reporting;

namespace Dotty.Performance.Tests;

/// <summary>
/// Main entry point for the Dotty Performance Test Suite
/// </summary>
public class Program
{
    private const string OutputDirectory = "./BenchmarkDotNet.Artifacts/performance";

    private static readonly string[] ValidFilterCategories =
    [
        "parser", "memory", "rendering", "silk", "startup", "throughput", "bulk"
    ];

    public static void Main(string[] args)
    {
        Console.WriteLine("=== Dotty Terminal Emulator - Performance Test Suite ===");
        Console.WriteLine();

        var config = ParseArguments(args);
        var mode = GetBenchmarkMode(args);
        var filter = GetBenchmarkFilter(args);
        if (mode == "gate-self-test")
        {
            RunGateSelfTest();
            return;
        }

        // Validate command line arguments

        // Validate filter if specified
        if (!string.IsNullOrEmpty(filter))
        {
            bool isValid = ValidFilterCategories.Any(c => filter.Contains(c, StringComparison.OrdinalIgnoreCase));
            if (!isValid)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: Unknown filter '{filter}'.");
                Console.ResetColor();
                Console.WriteLine($"Available categories: {string.Join(", ", ValidFilterCategories)}");
                return;
            }
        }
        else
        {
            // Melt warning when running full suite without filter
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("WARNING: Running full performance benchmark suite without --filter.");
            Console.WriteLine("This will consume 100% CPU across multiple benchmarks for 45s+ and generate high system load.");
            Console.WriteLine("Tip: Target specific benchmarks using '--filter <category>' (e.g., '--filter bulk', '--filter parser') or use '--mode quick'.");
            Console.ResetColor();
            Console.WriteLine();
        }

        try
        {
            // Setup configuration
            var benchmarkConfig = mode switch
            {
                "quick" or "ci" or "update-baselines" => CreateQuickConfig(),
                "memory" => CreateMemoryConfig(),
                "parser" => CreateParserConfig(),
                "rendering" => CreateRenderingConfig(),
                _ => CreateDetailedConfig()
            };

            // Apply filter if specified
            if (!string.IsNullOrEmpty(filter))
            {
                benchmarkConfig = benchmarkConfig.WithOptions(ConfigOptions.DisableLogFile);
                Console.WriteLine($"Running benchmarks matching: {filter}");
            }

            Console.WriteLine($"Configuration: {mode ?? "detailed"}");
            Console.WriteLine();

            // Create output directory
            Directory.CreateDirectory(OutputDirectory);

            // Run benchmarks
            var summaries = new List<BenchmarkDotNet.Reports.Summary>();

            if (string.IsNullOrEmpty(filter) || filter.Contains("parser", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Running Parser Benchmarks...");
                summaries.Add(BenchmarkRunner.Run<ParserBenchmarks>(benchmarkConfig));
                summaries.Add(BenchmarkRunner.Run<ParserMicroBenchmarks>(benchmarkConfig));
            }

            if (string.IsNullOrEmpty(filter) || filter.Contains("memory", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Running Memory Benchmarks...");
                summaries.Add(BenchmarkRunner.Run<MemoryBenchmarks>(benchmarkConfig));
            }

            if (string.IsNullOrEmpty(filter) || filter.Contains("rendering", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Running Rendering Benchmarks...");
                summaries.Add(BenchmarkRunner.Run<RenderingBenchmarks>(benchmarkConfig));
            }
            if (string.IsNullOrEmpty(filter) || filter.Contains("silk", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Running Silk CPU Rendering Benchmarks...");
                summaries.Add(BenchmarkRunner.Run<SilkRenderingBenchmarks>(benchmarkConfig));
            }

            if (string.IsNullOrEmpty(filter) || filter.Contains("startup", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Running Startup Benchmarks...");
                summaries.Add(BenchmarkRunner.Run<StartupBenchmarks>(benchmarkConfig));
            }

            if (string.IsNullOrEmpty(filter) || filter.Contains("throughput", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Running Throughput Benchmarks...");
                summaries.Add(BenchmarkRunner.Run<ThroughputBenchmarks>(benchmarkConfig));
                summaries.Add(BenchmarkRunner.Run<LatencyBenchmarks>(benchmarkConfig));
            }

            if (string.IsNullOrEmpty(filter) || filter.Contains("bulk", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Running Bulk Output Benchmarks...");
                summaries.Add(BenchmarkRunner.Run<BulkOutputBenchmark>(benchmarkConfig));
            }

            // Generate reports
            GenerateReports(summaries);

            // Refresh checked-in baselines from this run's medians.
            if (string.Equals(mode, "update-baselines", StringComparison.OrdinalIgnoreCase))
            {
                UpdateBaselines(summaries);
                return;
            }

            // Check for regressions if in CI mode
            if (mode == "ci" || mode == "quick")
            {
                CheckRegressions(summaries, benchmarkConfig);
            }

            Console.WriteLine();
            Console.WriteLine("=== Performance Test Suite Complete ===");
        }
        finally
        {
            DotNetBuildServerShutdown();
        }
    }

    /// <summary>
    /// Shuts down background MSBuild and Roslyn compiler build server daemons to prevent ghost processes from lingering.
    /// </summary>
    private static void DotNetBuildServerShutdown()
    {
        try
        {
            var psi = new ProcessStartInfo("dotnet", "build-server shutdown")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
        }
        catch
        {
            // Non-critical cleanup; ignore failure
        }
    }

    private static IConfig CreateDetailedConfig() =>
        DefaultConfig.Instance
            .AddJob(Job.Default
                .WithGcServer(true)
                .WithGcForce(false)
                .WithStrategy(BenchmarkDotNet.Engines.RunStrategy.Monitoring)
                .WithIterationCount(20)
                .WithWarmupCount(5))
            .AddDiagnoser(BenchmarkDotNet.Diagnosers.MemoryDiagnoser.Default)
            .WithOptions(ConfigOptions.JoinSummary);

    private static IConfig CreateQuickConfig() =>
        DefaultConfig.Instance
            .AddJob(Job.Default
                .WithGcServer(true)
                .WithGcForce(false)
                .WithStrategy(BenchmarkDotNet.Engines.RunStrategy.Throughput)
                .WithIterationCount(5)
                .WithWarmupCount(2)
                .WithInvocationCount(12)
                .WithUnrollFactor(4))
            .AddDiagnoser(BenchmarkDotNet.Diagnosers.MemoryDiagnoser.Default)
            .WithOptions(ConfigOptions.DisableLogFile)
            .WithOptions(ConfigOptions.JoinSummary);

    private static IConfig CreateMemoryConfig() =>
        DefaultConfig.Instance
            .AddJob(Job.Default
                .WithGcServer(true)
                .WithGcForce(true)
                .WithIterationCount(15)
                .WithWarmupCount(3))
            .AddDiagnoser(BenchmarkDotNet.Diagnosers.MemoryDiagnoser.Default)
            .WithOptions(ConfigOptions.JoinSummary);

    private static IConfig CreateParserConfig() =>
        DefaultConfig.Instance
            .AddJob(Job.Default
                .WithGcServer(true)
                .WithGcForce(false)
                .WithStrategy(BenchmarkDotNet.Engines.RunStrategy.Throughput)
                .WithIterationCount(10)
                .WithWarmupCount(3)
                .WithUnrollFactor(32))
            .AddDiagnoser(BenchmarkDotNet.Diagnosers.MemoryDiagnoser.Default)
            .WithOptions(ConfigOptions.JoinSummary);

    private static IConfig CreateRenderingConfig() =>
        DefaultConfig.Instance
            .AddJob(Job.Default
                .WithGcServer(true)
                .WithGcForce(false)
                .WithStrategy(BenchmarkDotNet.Engines.RunStrategy.Monitoring)
                .WithIterationCount(15)
                .WithWarmupCount(5))
            .AddDiagnoser(BenchmarkDotNet.Diagnosers.MemoryDiagnoser.Default)
            .WithOptions(ConfigOptions.JoinSummary);

    private static void GenerateReports(List<BenchmarkDotNet.Reports.Summary> summaries)
    {
        var report = new PerformanceReport(OutputDirectory);

        foreach (var summary in summaries)
        {
            if (summary != null)
            {
                try
                {
                    report.GenerateHtmlReport(summary, $"{summary.Title}.html");
                    report.GenerateJsonReport(summary, $"{summary.Title}.json");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: Failed to generate report for {summary.Title}: {ex.Message}");
                }
            }
        }
    }

    private static void CheckRegressions(List<BenchmarkDotNet.Reports.Summary> summaries, IConfig benchmarkConfig)
    {
        var report = new PerformanceReport(OutputDirectory);
        var firstPassFailures = summaries
            .Where(summary => summary != null)
            .SelectMany(summary => summary.Reports)
            .Select(report.EvaluateBenchmark)
            .Where(result => !result.Passed)
            .ToArray();

        var secondPassResults = new Dictionary<string, BenchmarkRegressionEvaluation>(StringComparer.Ordinal);
        foreach (var typeGroup in firstPassFailures.GroupBy(failure => failure.BenchmarkType))
        {
            var failingNames = typeGroup.Select(failure => failure.Name).ToHashSet(StringComparer.Ordinal);
            Console.WriteLine($"Re-measuring {failingNames.Count} initially failing benchmark(s) in {typeGroup.Key.Name}...");
            var filteredConfig = benchmarkConfig.AddFilter(new BenchmarkDotNet.Filters.SimpleFilter(
                benchmarkCase => failingNames.Contains(benchmarkCase.Descriptor.WorkloadMethodDisplayInfo)));
            var secondSummary = BenchmarkRunner.Run(typeGroup.Key, filteredConfig, Array.Empty<string>());
            foreach (var benchmarkReport in secondSummary.Reports)
            {
                var result = report.EvaluateBenchmark(benchmarkReport);
                secondPassResults[result.Key] = result;
            }
        }

        var resolution = ResolveRegressionRechecks(firstPassFailures, secondPassResults);
        foreach (var (first, second) in resolution.Transient)
            Console.WriteLine($"Transient (passed on re-measure): {first.Name}: {first.MedianMs:F4} ms -> {second.MedianMs:F4} ms");

        var suiteNames = GetAllBenchmarkCases()
            .Select(benchmark => benchmark.Descriptor.WorkloadMethodDisplayInfo).ToHashSet(StringComparer.Ordinal);
        var coverage = PerformanceReport.GetBaselineCoverage(suiteNames, report.BaselineNames);
        var executedNames = summaries.Where(summary => summary != null).SelectMany(summary => summary.Reports)
            .Select(benchmark => benchmark.BenchmarkCase.Descriptor.WorkloadMethodDisplayInfo).ToHashSet(StringComparer.Ordinal);
        var missingInRun = coverage.MissingBaselines.Where(executedNames.Contains).ToArray();
        if (missingInRun.Length != 0 || coverage.UnmatchedBaselines.Length != 0)
        {
            Console.WriteLine();
            Console.WriteLine("WARNING: Benchmark baseline name coverage is incomplete.");
            foreach (string name in missingInRun) Console.WriteLine($"  Benchmark has no baseline: {name}");
            foreach (string name in coverage.UnmatchedBaselines) Console.WriteLine($"  Baseline matched no benchmark: {name}");
        }

        if (resolution.Confirmed.Length != 0)
        {
            Console.WriteLine();
            Console.WriteLine("!!! PERFORMANCE REGRESSIONS CONFIRMED ON RE-MEASURE !!!");
            foreach (var regression in resolution.Confirmed)
                Console.WriteLine($"  {regression.Name}: {regression.Message}");
            Console.WriteLine();

            if (Environment.GetEnvironmentVariable("CI") == "true")
                Environment.Exit(1);
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("All performance thresholds passed.");
        }
    }

    private static RegressionResolution ResolveRegressionRechecks(
        IReadOnlyCollection<BenchmarkRegressionEvaluation> firstPassFailures,
        IReadOnlyDictionary<string, BenchmarkRegressionEvaluation> secondPassResults)
    {
        var confirmed = new List<BenchmarkRegressionEvaluation>();
        var transient = new List<(BenchmarkRegressionEvaluation First, BenchmarkRegressionEvaluation Second)>();
        foreach (var first in firstPassFailures)
        {
            if (secondPassResults.TryGetValue(first.Key, out var second) && second.Passed)
                transient.Add((first, second));
            else
                confirmed.Add(secondPassResults.TryGetValue(first.Key, out second) ? second : first);
        }

        return new RegressionResolution(confirmed.ToArray(), transient.ToArray());
    }

    private sealed record RegressionResolution(
        BenchmarkRegressionEvaluation[] Confirmed,
        (BenchmarkRegressionEvaluation First, BenchmarkRegressionEvaluation Second)[] Transient);

    // Update executed benchmarks, preserve other valid baselines, and remove suite-wide orphans.
    private static void UpdateBaselines(List<BenchmarkDotNet.Reports.Summary> summaries)
    {
        string projectDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
        string baselinePath = Path.Combine(projectDir, "baselines.json");
        var baselines = File.Exists(baselinePath)
            ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, BaselineThreshold>>(File.ReadAllText(baselinePath)) ?? new()
            : new Dictionary<string, BaselineThreshold>();
        var suiteCases = GetAllBenchmarkCases().ToArray();
        var suiteNames = suiteCases.Select(benchmark => benchmark.Descriptor.WorkloadMethodDisplayInfo).ToHashSet(StringComparer.Ordinal);
        foreach (string orphan in baselines.Keys.Where(key => !suiteNames.Contains(key)).ToArray())
            baselines.Remove(orphan);

        int updated = 0;
        foreach (var summary in summaries)
        {
            if (summary == null) continue;
            foreach (var report in summary.Reports)
            {
                string name = report.BenchmarkCase.Descriptor.WorkloadMethodDisplayInfo;
                double medianMs = (report.ResultStatistics?.Median ?? 0) / 1_000_000.0;
                if (medianMs <= 0) continue;
                double allocatedPerOp = report.GcStats.GetBytesAllocatedPerOperation(report.BenchmarkCase) ?? 0;
                baselines[name] = new BaselineThreshold
                {
                    ExpectedMeanMs = Math.Round(medianMs, 6),
                    MinThroughput = 0,
                    MaxAllocationsPerOp = Math.Ceiling(allocatedPerOp) + 64,
                    RegressionThreshold = 0.50,
                };
                updated++;
            }
        }

        string json = System.Text.Json.JsonSerializer.Serialize(baselines,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(baselinePath, json + Environment.NewLine);
        Console.WriteLine($"Updated {updated} baselines in {baselinePath}; valid unrun baselines were retained. Review the diff.");
    }

    private static readonly Type[] BenchmarkTypes =
    [
        typeof(ParserBenchmarks), typeof(ParserMicroBenchmarks), typeof(MemoryBenchmarks),
        typeof(RenderingBenchmarks), typeof(SilkRenderingBenchmarks), typeof(StartupBenchmarks),
        typeof(ThroughputBenchmarks), typeof(LatencyBenchmarks), typeof(BulkOutputBenchmark)
    ];

    private static IEnumerable<BenchmarkCase> GetAllBenchmarkCases() =>
        BenchmarkTypes.SelectMany(type => BenchmarkConverter.TypeToBenchmarks(type).BenchmarksCases);
    private static void RunGateSelfTest()
    {
        if (Math.Abs(BaselineComparer.GetAllowedLatencyMs(0.0006) - 0.000975) > 1e-12)
            throw new InvalidOperationException("Sub-microsecond latency threshold formula changed.");
        if (Math.Abs(BaselineComparer.GetAllowedLatencyMs(0.001) - 0.001575) > 1e-12)
            throw new InvalidOperationException("One-microsecond latency threshold formula changed.");
        if (Math.Abs(BaselineComparer.GetAllowedLatencyMs(2.0) - 3.000075) > 1e-12)
            throw new InvalidOperationException("Millisecond latency threshold formula changed.");

        var comparer = new BaselineComparer();
        comparer.SetBaseline("small", 0.0006);
        comparer.SetBaseline("large", 2.0);
        comparer.SetBaseline("allocation", 0);
        if (comparer.Compare("small", new BenchmarkResult { P50Ms = 0.0013 }).Passed)
            throw new InvalidOperationException("600 ns baseline accepted 1.3 us.");
        if (comparer.Compare("large", new BenchmarkResult { P50Ms = 3.01 }).Passed)
            throw new InvalidOperationException("2 ms baseline accepted 3.01 ms.");

        var allocationGate = new BaselineComparer();
        allocationGate.SetBaseline("zero-allocation", expectedMeanMs: 0, maxAllocationsPerOp: 64);
        if (!allocationGate.Compare("zero-allocation", new BenchmarkResult { AllocatedBytesPerOp = 128 }).Passed ||
            allocationGate.Compare("zero-allocation", new BenchmarkResult { AllocatedBytesPerOp = 129 }).Passed)
            throw new InvalidOperationException("64 B allocation ceiling slack changed.");

        allocationGate.SetBaseline("large-allocation", expectedMeanMs: 0, maxAllocationsPerOp: 1_000_000);
        if (!allocationGate.Compare("large-allocation", new BenchmarkResult { AllocatedBytesPerOp = 1_100_000 }).Passed ||
            allocationGate.Compare("large-allocation", new BenchmarkResult { AllocatedBytesPerOp = 1_100_001 }).Passed)
            throw new InvalidOperationException("Proportional allocation slack changed.");

        var parserThroughputName = GetAllBenchmarkCases()
            .Where(benchmark => benchmark.Descriptor.Categories.Contains("Parser"))
            .Select(benchmark => benchmark.Descriptor.WorkloadMethodDisplayInfo)
            .Single(name => name == "'Throughput - ANSI Text 1MB'");
        var parserCoverage = PerformanceReport.GetBaselineCoverage([parserThroughputName], [parserThroughputName]);
        if (parserCoverage.MissingBaselines.Length != 0 || parserCoverage.UnmatchedBaselines.Length != 0)
            throw new InvalidOperationException("Parser-category throughput baseline failed exact-name matching.");

        var coverage = PerformanceReport.GetBaselineCoverage(["'Known'", "'New'"], ["'Known'", "'Orphan'"]);
        if (!coverage.MissingBaselines.SequenceEqual(["'New'"]) || !coverage.UnmatchedBaselines.SequenceEqual(["'Orphan'"]))
            throw new InvalidOperationException("Exact-name baseline orphan detection changed.");
        var benchmarkType = typeof(Program);
        var firstA = new BenchmarkRegressionEvaluation(benchmarkType, "A", 0.003, false, "first A failure");
        var firstB = new BenchmarkRegressionEvaluation(benchmarkType, "B", 0.004, false, "first B failure");
        var firstMissing = new BenchmarkRegressionEvaluation(benchmarkType, "Missing", 0.005, false, "first missing failure");
        var secondA = new BenchmarkRegressionEvaluation(benchmarkType, "A", 0.006, false, "second A failure");
        var secondB = new BenchmarkRegressionEvaluation(benchmarkType, "B", 0.002, true, "second B passed");
        var recheck = ResolveRegressionRechecks(
            [firstA, firstB, firstMissing],
            new Dictionary<string, BenchmarkRegressionEvaluation>(StringComparer.Ordinal)
            { [secondA.Key] = secondA, [secondB.Key] = secondB });
        if (!recheck.Confirmed.Select(result => result.Name).SequenceEqual(["A", "Missing"]) ||
            recheck.Confirmed.Single(result => result.Name == "A").MedianMs != secondA.MedianMs ||
            recheck.Transient.Length != 1 || recheck.Transient[0].First.Name != "B" || recheck.Transient[0].Second.MedianMs != secondB.MedianMs)
            throw new InvalidOperationException("Confirm-before-fail regression resolution changed.");

        Console.WriteLine("Performance gate self-check passed.");
    }

    private static IConfig ParseArguments(string[] args)
    {
        return DefaultConfig.Instance;
    }

    private static string? GetBenchmarkMode(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "--mode" || args[i] == "-m") && i + 1 < args.Length)
            {
                return args[i + 1].ToLowerInvariant();
            }
        }

        // Check environment variable
        var envMode = Environment.GetEnvironmentVariable("DOTTY_BENCH_MODE");
        if (!string.IsNullOrEmpty(envMode))
        {
            return envMode.ToLowerInvariant();
        }

        // Default based on CI environment
        if (Environment.GetEnvironmentVariable("CI") == "true")
        {
            return "quick";
        }

        return null;
    }

    private static string? GetBenchmarkFilter(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "--filter" || args[i] == "-f") && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }
        return null;
    }
}
