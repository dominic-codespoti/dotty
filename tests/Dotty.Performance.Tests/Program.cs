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

    public static void Main(string[] args)
    {
        Console.WriteLine("=== Dotty Terminal Emulator - Performance Test Suite ===");
        Console.WriteLine();

        // Parse command line arguments
        var config = ParseArguments(args);
        var mode = GetBenchmarkMode(args);
        var filter = GetBenchmarkFilter(args);

        // Setup configuration
        var benchmarkConfig = mode switch
        {
            "quick" or "ci" => CreateQuickConfig(),
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

        if (string.IsNullOrEmpty(filter) || filter.Contains("parser"))
        {
            Console.WriteLine("Running Parser Benchmarks...");
            summaries.Add(BenchmarkRunner.Run<ParserBenchmarks>(benchmarkConfig));
            summaries.Add(BenchmarkRunner.Run<ParserMicroBenchmarks>(benchmarkConfig));
        }

        if (string.IsNullOrEmpty(filter) || filter.Contains("memory"))
        {
            Console.WriteLine("Running Memory Benchmarks...");
            summaries.Add(BenchmarkRunner.Run<MemoryBenchmarks>(benchmarkConfig));
        }

        if (string.IsNullOrEmpty(filter) || filter.Contains("rendering"))
        {
            Console.WriteLine("Running Rendering Benchmarks...");
            summaries.Add(BenchmarkRunner.Run<RenderingBenchmarks>(benchmarkConfig));
        }

        if (string.IsNullOrEmpty(filter) || filter.Contains("startup"))
        {
            Console.WriteLine("Running Startup Benchmarks...");
            summaries.Add(BenchmarkRunner.Run<StartupBenchmarks>(benchmarkConfig));
        }

        if (string.IsNullOrEmpty(filter) || filter.Contains("throughput"))
        {
            Console.WriteLine("Running Throughput Benchmarks...");
            summaries.Add(BenchmarkRunner.Run<ThroughputBenchmarks>(benchmarkConfig));
            summaries.Add(BenchmarkRunner.Run<LatencyBenchmarks>(benchmarkConfig));
        }

        // Generate reports
        GenerateReports(summaries);

        // Check for regressions if in CI mode
        if (mode == "ci" || mode == "quick")
        {
            CheckRegressions(summaries);
        }

        Console.WriteLine();
        Console.WriteLine("=== Performance Test Suite Complete ===");
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

    private static void CheckRegressions(List<BenchmarkDotNet.Reports.Summary> summaries)
    {
        var report = new PerformanceReport(OutputDirectory);
        var allRegressions = new List<string>();

        foreach (var summary in summaries)
        {
            if (summary != null)
            {
                if (!report.CheckRegressions(summary, out var regressions))
                {
                    allRegressions.AddRange(regressions);
                }
            }
        }

        if (allRegressions.Any())
        {
            Console.WriteLine();
            Console.WriteLine("!!! PERFORMANCE REGRESSIONS DETECTED !!!");
            foreach (var regression in allRegressions)
            {
                Console.WriteLine($"  - {regression}");
            }
            Console.WriteLine();
            
            // Exit with error code in CI mode
            if (Environment.GetEnvironmentVariable("CI") == "true")
            {
                Environment.Exit(1);
            }
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("All performance thresholds passed.");
        }
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
