using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Csv;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Order;

namespace Dotty.Performance.Tests.Infrastructure;

/// <summary>
/// BenchmarkDotNet configuration optimized for terminal emulator performance testing.
/// Supports both detailed local profiling and quick CI execution.
/// </summary>
public static class BenchmarkConfig
{
    /// <summary>
    /// Configuration for detailed local benchmarking with full diagnostics
    /// </summary>
    public static IConfig GetDetailedConfig() =>
        ManualConfig.Create(DefaultConfig.Instance)
            .AddJob(Job.Default
                .WithGcServer(true)
                .WithGcForce(false)
                .WithStrategy(BenchmarkDotNet.Engines.RunStrategy.Monitoring)
                .WithIterationCount(20)
                .WithWarmupCount(5)
                .WithInvocationCount(100)
                .WithUnrollFactor(16))
            .AddDiagnoser(MemoryDiagnoser.Default)
            .AddExporter(HtmlExporter.Default)
            .AddExporter(JsonExporter.Full)
            .AddExporter(CsvExporter.Default)
            .WithOrderer(new DefaultOrderer(SummaryOrderPolicy.Method))
            .WithOptions(ConfigOptions.JoinSummary);

    /// <summary>
    /// Configuration for CI/CD - faster execution with essential metrics only
    /// </summary>
    public static IConfig GetQuickConfig() =>
        ManualConfig.Create(DefaultConfig.Instance)
            .AddJob(Job.Default
                .WithGcServer(true)
                .WithGcForce(false)
                .WithStrategy(BenchmarkDotNet.Engines.RunStrategy.Throughput)
                .WithIterationCount(5)
                .WithWarmupCount(2)
                .WithInvocationCount(12)
                .WithUnrollFactor(4))
            .AddDiagnoser(MemoryDiagnoser.Default)
            .AddExporter(JsonExporter.Brief)
            .WithOptions(ConfigOptions.DisableLogFile)
            .WithOptions(ConfigOptions.JoinSummary);

    /// <summary>
    /// Configuration for memory-focused benchmarks
    /// </summary>
    public static IConfig GetMemoryConfig() =>
        ManualConfig.Create(DefaultConfig.Instance)
            .AddJob(Job.Default
                .WithGcServer(true)
                .WithGcForce(true)
                .WithGcConcurrent(true)
                .WithStrategy(BenchmarkDotNet.Engines.RunStrategy.Monitoring)
                .WithIterationCount(15)
                .WithWarmupCount(3)
                .WithInvocationCount(50))
            .AddDiagnoser(MemoryDiagnoser.Default)
            .AddExporter(HtmlExporter.Default)
            .AddExporter(JsonExporter.Full)
            .WithOrderer(new DefaultOrderer(SummaryOrderPolicy.Method));

    /// <summary>
    /// Configuration for parser benchmarks - focuses on throughput
    /// </summary>
    public static IConfig GetParserConfig() =>
        ManualConfig.Create(DefaultConfig.Instance)
            .AddJob(Job.Default
                .WithGcServer(true)
                .WithGcForce(false)
                .WithStrategy(BenchmarkDotNet.Engines.RunStrategy.Throughput)
                .WithIterationCount(10)
                .WithWarmupCount(3)
                .WithInvocationCount(100)
                .WithUnrollFactor(32))
            .AddDiagnoser(MemoryDiagnoser.Default)
            .AddExporter(JsonExporter.Full)
            .WithOrderer(new DefaultOrderer(SummaryOrderPolicy.Method));

    /// <summary>
    /// Configuration for rendering benchmarks - uses monitoring strategy for stable frame times
    /// </summary>
    public static IConfig GetRenderingConfig() =>
        ManualConfig.Create(DefaultConfig.Instance)
            .AddJob(Job.Default
                .WithGcServer(true)
                .WithGcForce(false)
                .WithStrategy(BenchmarkDotNet.Engines.RunStrategy.Monitoring)
                .WithIterationCount(15)
                .WithWarmupCount(5)
                .WithInvocationCount(50))
            .AddDiagnoser(MemoryDiagnoser.Default)
            .AddExporter(HtmlExporter.Default)
            .AddExporter(JsonExporter.Full)
            .WithOrderer(new DefaultOrderer(SummaryOrderPolicy.Method));

    /// <summary>
    /// Get the appropriate configuration based on environment
    /// </summary>
    public static IConfig GetConfig(BenchmarkMode mode) => mode switch
    {
        BenchmarkMode.Detailed => GetDetailedConfig(),
        BenchmarkMode.Quick => GetQuickConfig(),
        BenchmarkMode.Memory => GetMemoryConfig(),
        BenchmarkMode.Parser => GetParserConfig(),
        BenchmarkMode.Rendering => GetRenderingConfig(),
        _ => GetDetailedConfig()
    };
}

/// <summary>
/// Benchmark execution modes
/// </summary>
public enum BenchmarkMode
{
    Detailed,
    Quick,
    Memory,
    Parser,
    Rendering
}

/// <summary>
/// Attribute to specify benchmark mode for a class
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class BenchmarkModeAttribute : Attribute
{
    public BenchmarkMode Mode { get; }

    public BenchmarkModeAttribute(BenchmarkMode mode)
    {
        Mode = mode;
    }
}
