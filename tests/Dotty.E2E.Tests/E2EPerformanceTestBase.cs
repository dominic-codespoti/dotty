using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dotty.E2E.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Dotty.E2E.Tests;

/// <summary>
/// Base class for E2E tests that measure and assert on performance metrics.
/// Extends E2ETestBase with performance monitoring capabilities.
/// </summary>
public abstract class E2EPerformanceTestBase : E2ETestBase
{
    private IPerformanceCounterCollector _performanceCollector;
    private PerformanceBaselineManager _baselineManager;
    private readonly ITestOutputHelper? _outputHelper;
    private PerformanceSnapshot? _currentSnapshot;
    private bool _isMonitoring;
    private readonly List<PerformanceSnapshot> _snapshots;
    
    /// <summary>
    /// Gets the performance counter collector.
    /// </summary>
    protected IPerformanceCounterCollector PerformanceCollector => _performanceCollector;
    
    /// <summary>
    /// Gets the current performance snapshot.
    /// If monitoring has been stopped, returns the final snapshot.
    /// If monitoring is active, returns the most recent metrics from the collector.
    /// Returns null if monitoring was never started.
    /// </summary>
    protected PerformanceSnapshot? CurrentSnapshot 
    { 
        get
        {
            if (_currentSnapshot != null)
                return _currentSnapshot;
                
            // If monitoring is active, try to get current metrics synchronously
            if (_isMonitoring && _performanceCollector != null)
            {
                try
                {
                    // Get metrics with a short timeout to avoid blocking
                    var task = _performanceCollector.GetCurrentMetricsAsync();
                    if (task.Wait(TimeSpan.FromSeconds(2)))
                        return task.Result;
                }
                catch { /* Ignore errors, return null */ }
            }
            
            return null;
        }
    }
    
    /// <summary>
    /// Creates a new performance test base instance.
    /// </summary>
    protected E2EPerformanceTestBase(string testName, ITestOutputHelper? outputHelper = null) 
        : base(testName)
    {
        _outputHelper = outputHelper;
        _snapshots = new List<PerformanceSnapshot>();
        
        // Initialize components (actual initialization happens in InitializeAsync)
        _performanceCollector = null!;
        _baselineManager = null!;
    }
    
    /// <summary>
    /// Initializes the test environment with performance monitoring.
    /// </summary>
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        
        // Initialize performance collector
        var config = GetPerformanceConfig();
        _performanceCollector = new PerformanceCounterCollector(
            App.Commands,
            logger: null,  // Logger is TestLogger, not ILogger<T>
            config);
        
        // Subscribe to threshold exceeded events
        _performanceCollector.ThresholdExceeded += OnPerformanceThresholdExceeded;
        
        // Initialize baseline manager
        var baselinesPath = GetBaselinesPath();
        _baselineManager = new PerformanceBaselineManager(
            baselinesPath,
            logger: null);
        
        Logger.Log($"Performance test base initialized with baselines at: {baselinesPath}");
    }
    
    /// <summary>
    /// Cleans up the test environment.
    /// </summary>
    public override async Task DisposeAsync()
    {
        // Stop any ongoing monitoring
        if (_isMonitoring)
        {
            await StopPerformanceMonitoringAsync();
        }
        
        // Unsubscribe from events
        if (_performanceCollector != null)
        {
            _performanceCollector.ThresholdExceeded -= OnPerformanceThresholdExceeded;
            await _performanceCollector.DisposeAsync();
        }
        
        await base.DisposeAsync();
    }
    
    /// <summary>
    /// Starts performance monitoring for the current test.
    /// </summary>
    protected async Task StartPerformanceMonitoringAsync(string? testName = null)
    {
        if (_isMonitoring)
        {
            Logger.Log("Performance monitoring already in progress, stopping first");
            await StopPerformanceMonitoringAsync();
        }
        
        var name = testName ?? GetCurrentTestName();
        _isMonitoring = true;
        
        await _performanceCollector.StartCollectionAsync(name);
        Logger.Log($"Started performance monitoring for: {name}");
    }
    
    /// <summary>
    /// Stops performance monitoring and returns the collected metrics.
    /// </summary>
    protected async Task<PerformanceSnapshot> StopPerformanceMonitoringAsync()
    {
        if (!_isMonitoring)
        {
            Logger.Log("Performance monitoring was not running");
            return _currentSnapshot ?? CreateEmptySnapshot();
        }
        
        _isMonitoring = false;
        _currentSnapshot = await _performanceCollector.StopCollectionAsync();
        _snapshots.Add(_currentSnapshot);
        
        // Log the summary
        var summary = _currentSnapshot.GetSummary();
        Logger.Log($"Performance monitoring stopped:\n{summary}");
        _outputHelper?.WriteLine($"Performance Results:\n{summary}");
        
        return _currentSnapshot;
    }
    
    /// <summary>
    /// Asserts that a performance metric meets a threshold.
    /// </summary>
    protected void AssertPerformanceThreshold(string metric, double actualValue, double threshold, ThresholdComparison comparison = ThresholdComparison.LessThan)
    {
        bool passed = comparison switch
        {
            ThresholdComparison.LessThan => actualValue < threshold,
            ThresholdComparison.LessThanOrEqual => actualValue <= threshold,
            ThresholdComparison.GreaterThan => actualValue > threshold,
            ThresholdComparison.GreaterThanOrEqual => actualValue >= threshold,
            ThresholdComparison.Equals => Math.Abs(actualValue - threshold) < 0.001,
            _ => false
        };
        
        var comparisonStr = comparison switch
        {
            ThresholdComparison.LessThan => "<",
            ThresholdComparison.LessThanOrEqual => "<=",
            ThresholdComparison.GreaterThan => ">",
            ThresholdComparison.GreaterThanOrEqual => ">=",
            ThresholdComparison.Equals => "==",
            _ => "?"
        };
        
        if (!passed)
        {
            var message = $"Performance threshold failed: {metric} = {actualValue:F3} (expected {comparisonStr} {threshold:F3})";
            Logger.LogError(message);
            Assert.True(false, message);
        }
        else
        {
            Logger.Log($"Performance threshold passed: {metric} = {actualValue:F3} {comparisonStr} {threshold:F3}");
        }
    }
    
    /// <summary>
    /// Gets a formatted performance report for the current or last test.
    /// </summary>
    protected string GetPerformanceReport()
    {
        var snapshot = _currentSnapshot;
        if (snapshot == null)
        {
            return "No performance data available.";
        }
        
        return snapshot.GetSummary();
    }
    
    /// <summary>
    /// Records a performance baseline for the current test.
    /// </summary>
    protected async Task RecordBaselineAsync(string testName, PerformanceSnapshot? snapshot = null)
    {
        snapshot ??= _currentSnapshot;
        if (snapshot == null)
        {
            Logger.Log("Cannot record baseline: no performance snapshot available");
            return;
        }
        
        await _baselineManager.RecordBaselineAsync(testName, snapshot);
        Logger.Log($"Recorded baseline for test: {testName}");
    }
    
    /// <summary>
    /// Compares current performance against a recorded baseline.
    /// </summary>
    protected async Task<PerformanceComparison> CompareToBaselineAsync(string testName, PerformanceSnapshot? current = null)
    {
        current ??= _currentSnapshot;
        if (current == null)
        {
            Logger.Log("Cannot compare to baseline: no current performance snapshot available");
            return new PerformanceComparison { HasBaseline = false };
        }
        
        var comparison = await _baselineManager.CompareToBaselineAsync(testName, current);
        
        if (comparison.HasBaseline)
        {
            Logger.Log($"Compared to baseline: Fps change = {comparison.FpsDeltaPercentage:F1}%, " +
                $"Frame time change = {comparison.FrameTimeDeltaPercentage:F1}%");
        }
        else
        {
            Logger.Log($"No baseline found for test: {testName}");
        }
        
        return comparison;
    }
    
    /// <summary>
    /// Asserts that there is no performance regression compared to the baseline.
    /// </summary>
    protected async Task AssertNoPerformanceRegressionAsync(string testName, double tolerancePercentage = 10.0)
    {
        if (_currentSnapshot == null)
        {
            throw new InvalidOperationException("No performance data available. Call StopPerformanceMonitoringAsync() first.");
        }
        
        var comparison = await CompareToBaselineAsync(testName, _currentSnapshot);
        
        if (!comparison.HasBaseline)
        {
            Logger.Log($"No baseline found for '{testName}', recording current as baseline");
            await RecordBaselineAsync(testName, _currentSnapshot);
            return;
        }
        
        // Check key metrics for regression
        var regressions = new List<string>();
        
        // FPS regression (lower is bad)
        if (comparison.FpsDeltaPercentage < -tolerancePercentage)
        {
            regressions.Add($"FPS regression: {comparison.FpsDeltaPercentage:F1}% (baseline: {comparison.BaselineSnapshot.Fps:F1}, current: {comparison.CurrentSnapshot.Fps:F1})");
        }
        
        // Frame time regression (higher is bad)
        if (comparison.FrameTimeDeltaPercentage > tolerancePercentage)
        {
            regressions.Add($"Frame time regression: +{comparison.FrameTimeDeltaPercentage:F1}% (baseline: {comparison.BaselineSnapshot.FrameTimeP95:F2}ms p95, current: {comparison.CurrentSnapshot.FrameTimeP95:F2}ms p95)");
        }
        
        // Memory regression
        if (comparison.MemoryDeltaPercentage > tolerancePercentage * 2) // Allow more variance for memory
        {
            regressions.Add($"Memory regression: +{comparison.MemoryDeltaPercentage:F1}% (baseline: {comparison.BaselineSnapshot.HeapSizeBytes / 1024 / 1024:F1}MB, current: {comparison.CurrentSnapshot.HeapSizeBytes / 1024 / 1024:F1}MB)");
        }
        
        if (regressions.Count > 0)
        {
            var message = "Performance regressions detected:\n" + string.Join("\n", regressions);
            Logger.LogError(message);
            Assert.True(false, message);
        }
        else
        {
            Logger.Log($"No performance regression detected within {tolerancePercentage}% tolerance");
        }
    }
    
    /// <summary>
    /// Runs a test with automatic performance monitoring.
    /// </summary>
    protected async Task RunPerformanceTestAsync(string testName, Func<Task> testAction, bool assertBaseline = true)
    {
        try
        {
            await StartPerformanceMonitoringAsync(testName);
            
            await testAction();
            
            await StopPerformanceMonitoringAsync();
            
            if (assertBaseline)
            {
                await AssertNoPerformanceRegressionAsync(testName);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"Performance test failed: {testName}", ex);
            
            // Try to stop monitoring even on failure
            if (_isMonitoring)
            {
                try
                {
                    await StopPerformanceMonitoringAsync();
                }
                catch { }
            }
            
            throw;
        }
    }
    
    /// <summary>
    /// Gets the current performance metrics without stopping monitoring.
    /// Use this to check performance during test execution.
    /// </summary>
    protected async Task<PerformanceSnapshot> GetCurrentMetricsAsync(CancellationToken cancellationToken = default)
    {
        return await _performanceCollector.GetCurrentMetricsAsync(cancellationToken);
    }
    
    /// <summary>
    /// Gets performance configuration from appsettings or defaults.
    /// </summary>
    protected virtual PerformanceCounterConfig GetPerformanceConfig()
    {
        return new PerformanceCounterConfig
        {
            SamplingInterval = TimeSpan.FromMilliseconds(500),
            MaxFrameTimeThresholdMs = 33.33, // 30 FPS
            MaxInputLatencyThresholdMs = 50,
            MinFpsThreshold = 30,
            AutoCheckThresholds = true,
            MaxSamplesInMemory = 10000
        };
    }
    
    /// <summary>
    /// Gets the path for storing performance baselines.
    /// </summary>
    protected virtual string GetBaselinesPath()
    {
        var configPath = Environment.GetEnvironmentVariable("DOTTY_E2E_BASELINES_PATH");
        if (!string.IsNullOrEmpty(configPath))
        {
            return configPath;
        }
        
        return Path.Combine("artifacts", "baselines");
    }
    
    /// <summary>
    /// Gets the current test name.
    /// </summary>
    protected virtual string GetCurrentTestName()
    {
        // Try to get from test context or caller
        var stackTrace = new System.Diagnostics.StackTrace();
        var frames = stackTrace.GetFrames();
        
        foreach (var frame in frames)
        {
            var method = frame.GetMethod();
            if (method?.DeclaringType != null && 
                method.DeclaringType.IsSubclassOf(typeof(E2EPerformanceTestBase)) &&
                method.Name != "GetCurrentTestName" &&
                !method.Name.StartsWith("<"))
            {
                return $"{method.DeclaringType.Name}_{method.Name}";
            }
        }
        
        return $"PerformanceTest_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
    }
    
    private void OnPerformanceThresholdExceeded(object? sender, PerformanceThresholdEventArgs e)
    {
        Logger.Log($"Performance threshold exceeded: {e.MetricName} = {e.CurrentValue:F2} (threshold: {e.Threshold:F2})");
        _outputHelper?.WriteLine($"[WARNING] Performance threshold exceeded: {e}");
    }
    
    private PerformanceSnapshot CreateEmptySnapshot()
    {
        return new PerformanceSnapshot
        {
            TestName = GetCurrentTestName(),
            Timestamp = DateTime.UtcNow
        };
    }
}

/// <summary>
/// Comparison operator for performance thresholds.
/// </summary>
public enum ThresholdComparison
{
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
    Equals
}

/// <summary>
/// Represents a comparison between current and baseline performance.
/// </summary>
public record PerformanceComparison
{
    public bool HasBaseline { get; init; }
    public PerformanceSnapshot? BaselineSnapshot { get; init; }
    public PerformanceSnapshot? CurrentSnapshot { get; init; }
    
    // Delta percentages (positive = improvement/regression depending on metric)
    public double FpsDeltaPercentage { get; init; }
    public double FrameTimeDeltaPercentage { get; init; }
    public double MemoryDeltaPercentage { get; init; }
    public double ParserThroughputDeltaPercentage { get; init; }
    public double LatencyDeltaPercentage { get; init; }
    
    /// <summary>
    /// Gets a summary of the comparison.
    /// </summary>
    public string GetSummary()
    {
        if (!HasBaseline)
        {
            return "No baseline available for comparison.";
        }
        
        return $"""
        Performance Comparison:
        
        FPS: {FpsDeltaPercentage:F1}% {(FpsDeltaPercentage > 0 ? "improvement" : "regression")}
        Frame Time: {FrameTimeDeltaPercentage:F1}% {(FrameTimeDeltaPercentage < 0 ? "improvement" : "regression")}
        Memory: {MemoryDeltaPercentage:F1}% {(MemoryDeltaPercentage < 0 ? "improvement" : "regression")}
        Parser Throughput: {ParserThroughputDeltaPercentage:F1}% {(ParserThroughputDeltaPercentage > 0 ? "improvement" : "regression")}
        Latency: {LatencyDeltaPercentage:F1}% {(LatencyDeltaPercentage < 0 ? "improvement" : "regression")}
        """;
    }
}
