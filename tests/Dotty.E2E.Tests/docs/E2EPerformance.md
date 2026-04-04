# E2E Performance Testing Guide

This document describes the performance measurement infrastructure for Dotty's E2E test suite.

## Overview

The E2E performance testing framework enables:

- **Automated performance metrics collection** during test execution
- **Baseline tracking** to detect regressions over time
- **Threshold assertions** to validate performance requirements
- **Comprehensive reporting** with HTML/JSON output
- **CI/CD integration** with regression detection

## Architecture

### Components

```
E2E Tests
├── Infrastructure/
│   ├── PerformanceCounterCollector.cs    # Collects metrics from running app
│   ├── PerformanceBaselineManager.cs     # Manages baseline storage/comparison
│   └── E2EPerformanceReport.cs           # Generates performance reports
├── Assertions/
│   └── PerformanceAssertions.cs          # Performance threshold assertions
├── E2EPerformanceTestBase.cs               # Base class for performance tests
└── Scenarios/
    └── *Tests.cs                           # Tests with performance measurements
```

### Data Flow

1. Test starts → `E2EPerformanceTestBase` initializes `PerformanceCounterCollector`
2. Performance monitoring starts via TCP command interface
3. Test scenario executes
4. Performance monitoring stops → `PerformanceSnapshot` created
5. Snapshot compared against baseline (if exists)
6. Thresholds validated using `PerformanceAssertions`
7. Results reported and baselines updated

## Available Performance Counters

### FPS Metrics
- **FPS**: Current frames per second
- **FPS Min/Max/Avg**: Frame rate statistics

### Frame Time Metrics (milliseconds)
- **FrameTime Min/Max/Avg**: Basic frame timing
- **FrameTime P95/P99**: Percentile frame times for consistency
- **FrameTime Count**: Total frames rendered

### Parser Throughput
- **ParserBytesPerSecond**: ANSI data processing rate
- **ParserSequencesPerSecond**: Escape sequence parsing rate
- **TotalBytesProcessed**: Cumulative data volume
- **TotalSequencesProcessed**: Cumulative sequence count

### Memory Metrics
- **HeapSizeBytes**: Current managed heap size
- **AllocatedBytes**: Total allocated since start
- **WorkingSetBytes**: Process working set
- **AllocationsPerSecond**: Allocation rate

### GC Metrics
- **Gen0/Gen1/Gen2 Collections**: Collection counts per generation
- **TotalGCTime**: Time spent in garbage collection
- **GCPausePercentage**: Percentage of time paused for GC

### Input Latency
- **InputLatency Min/Max/Avg**: Input processing times
- **InputLatency P95**: 95th percentile latency

### Scroll Performance
- **ScrollLinesPerSecond**: Scroll throughput
- **ScrollTime Avg**: Average scroll operation time
- **ScrollOperationsCount**: Total scroll operations

### Cell Update Rate
- **CellUpdatesPerSecond**: Screen cell update rate
- **TotalCellsUpdated**: Cumulative cell updates

## Writing Performance Tests

### Basic Structure

```csharp
public class MyPerformanceTests : E2EPerformanceTestBase
{
    public MyPerformanceTests(ITestOutputHelper outputHelper) 
        : base("MyTestCategory", outputHelper) { }

    [Fact]
    public async Task My_Test_With_Performance()
    {
        await RunPerformanceTestAsync(nameof(My_Test_With_Performance), async () =>
        {
            // Your test logic here
            await SendTextAndWaitAsync("test content");
            
            // Access performance snapshot
            var snapshot = CurrentSnapshot;
            Assert.NotNull(snapshot);
            
            // Assert on performance
            PerformanceAssertions.AssertFps(snapshot.Fps, minFps: 30);
        });
    }
}
```

### Manual Performance Monitoring

For more control, use manual start/stop:

```csharp
[Fact]
public async Task Manual_Performance_Monitoring()
{
    // Start monitoring
    await StartPerformanceMonitoringAsync("MyTest");
    
    try
    {
        // Execute test scenario
        await SendTextAndWaitAsync("content");
        await Task.Delay(1000);
        
        // Get intermediate metrics
        var intermediate = await PerformanceCollector.GetCurrentMetricsAsync();
        Logger.Log($"Intermediate FPS: {intermediate.Fps:F1}");
    }
    finally
    {
        // Stop and get final results
        var snapshot = await StopPerformanceMonitoringAsync();
        
        // Assert thresholds
        PerformanceAssertions.AssertFrameTimeP95(
            snapshot.FrameTimeP95, 
            maxP95Ms: 33.33);
        
        // Compare to baseline
        await AssertNoPerformanceRegressionAsync(
            "MyTest", 
            tolerancePercentage: 10.0);
    }
}
```

### Custom Thresholds

```csharp
[Fact]
public async Task Custom_Thresholds()
{
    await RunPerformanceTestAsync("CustomThresholds", async () =>
    {
        // Test logic
        await PerformOperation();
        
    }, assertBaseline: false); // Skip baseline comparison
    
    // Apply custom thresholds
    var snapshot = CurrentSnapshot;
    
    // Use different thresholds for different environments
    var thresholds = ShouldRunHeadless() 
        ? PerformanceThresholds.Headless 
        : PerformanceThresholds.Aggressive;
    
    PerformanceAssertions.AssertPerformanceSnapshot(
        snapshot.Fps,
        snapshot.FrameTimeAvg,
        snapshot.FrameTimeP95,
        snapshot.FrameTimeP99,
        snapshot.ParserBytesPerSecond,
        snapshot.HeapSizeBytes,
        thresholds);
}
```

## Configuration

### e2e.appsettings.json

```json
{
  "E2ETest": {
    "Performance": {
      "Enabled": true,
      "CollectMetrics": true,
      "SamplingIntervalMs": 500,
      "BaselinesPath": "./artifacts/baselines",
      "ReportsPath": "./artifacts/reports",
      "RegressionTolerancePercentage": 10.0,
      "FailOnRegression": false,
      "AlwaysCollectPerformance": true,
      "Thresholds": {
        "MinFps": 30.0,
        "MaxFrameTimeAvgMs": 16.67,
        "MaxFrameTimeP95Ms": 33.33,
        "MaxHeapSizeMB": 512
      }
    }
  }
}
```

### Environment Variables

- `DOTTY_E2E_BASELINES_PATH`: Override baseline storage path
- `DOTTY_E2E_HEADLESS`: Run in headless mode (affects thresholds)
- `DOTTY_PERF_FAIL_ON_REGRESSION`: Fail tests on performance regression

## Baseline Management

### Recording Baselines

Baselines are automatically recorded when a test runs without an existing baseline:

```csharp
// First run records baseline
await AssertNoPerformanceRegressionAsync("MyTest", tolerancePercentage: 10.0);
```

### Manual Baseline Operations

```csharp
// Record explicit baseline
await RecordBaselineAsync("MyTest", snapshot);

// Compare to baseline
var comparison = await CompareToBaselineAsync("MyTest", snapshot);
Logger.Log($"FPS change: {comparison.FpsDeltaPercentage:F1}%");
```

### Baseline Storage

Baselines are stored as JSON files in the configured directory:

```
artifacts/
└── baselines/
    ├── RenderingE2ETests_Basic_Rendering.json
    ├── AnsiE2ETests_Parser_Throughput.json
    └── PerformanceE2ETests_Startup_Time.json
```

Each baseline contains:
- Performance snapshot data
- Creation/update timestamps
- Environment information
- Version information

### Exporting/Importing Baselines

```csharp
var baselineManager = new PerformanceBaselineManager(baselinesPath);

// Export all baselines
var exportPath = await baselineManager.ExportBaselinesAsync();

// Import baselines (useful for CI)
var imported = await baselineManager.ImportBaselinesAsync("path/to/export.json");
```

## Performance Reports

### Generating Reports

```csharp
var report = new E2EPerformanceReport(outputDirectory);

// Add test results
report.AddSnapshot(snapshot);
report.AddComparison(comparison);

// Generate reports
var htmlPath = await report.GenerateHtmlReportAsync();
var jsonPath = await report.GenerateJsonReportAsync();
var regressionPath = await report.GenerateRegressionReportAsync(tolerancePercentage: 10.0);
```

### Report Types

**HTML Report**
- Summary dashboard with key metrics
- Tables with all performance data
- Visual charts (FPS distribution, frame times)
- Baseline comparison indicators

**JSON Report**
- Machine-readable format
- Contains all raw data
- Suitable for CI processing

**Trend Report**
- Shows metrics over time
- Useful for tracking performance trends

**Regression Report**
- Highlights performance changes
- Shows improvements and regressions

## CI/CD Integration

### GitHub Actions Example

```yaml
name: E2E Tests with Performance
on: [push, pull_request]

jobs:
  e2e-performance:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v3
        with:
          fetch-depth: 0  # Needed for baseline history
      
      - name: Setup .NET
        uses: actions/setup-dotnet@v3
        with:
          dotnet-version: '10.0.x'
      
      - name: Restore baselines
        uses: actions/download-artifact@v3
        with:
          name: performance-baselines
          path: tests/Dotty.E2E.Tests/artifacts/baselines
        continue-on-error: true
      
      - name: Build
        run: dotnet build
      
      - name: Run E2E Tests with Performance
        run: dotnet test tests/Dotty.E2E.Tests/Dotty.E2E.Tests.csproj
        env:
          DOTTY_E2E_HEADLESS: 1
          DOTTY_PERF_FAIL_ON_REGRESSION: 1
      
      - name: Upload Performance Reports
        uses: actions/upload-artifact@v3
        with:
          name: performance-reports
          path: tests/Dotty.E2E.Tests/artifacts/reports/
        if: always()
      
      - name: Upload Baselines
        uses: actions/upload-artifact@v3
        with:
          name: performance-baselines
          path: tests/Dotty.E2E.Tests/artifacts/baselines/
        if: always()
      
      - name: Comment PR with Results
        uses: actions/github-script@v6
        if: github.event_name == 'pull_request' && always()
        with:
          script: |
            // Add performance summary comment to PR
```

### Performance Gates

Configure strict performance gates:

```csharp
[Fact]
public async Task Critical_Performance_Path()
{
    await RunPerformanceTestAsync("CriticalPath", async () =>
    {
        // Critical operations
    });
    
    var snapshot = CurrentSnapshot;
    
    // Hard thresholds for critical paths
    PerformanceAssertions.AssertFps(snapshot.Fps, minFps: 60);
    PerformanceAssertions.AssertFrameTimeP95(snapshot.FrameTimeP95, maxP95Ms: 16.67);
    
    // Strict regression tolerance
    await AssertNoPerformanceRegressionAsync("CriticalPath", tolerancePercentage: 5.0);
}
```

## Best Practices

### 1. Stable Test Environment
- Run performance tests on dedicated/consistent hardware
- Avoid running other workloads during tests
- Use headless mode in CI for consistency

### 2. Appropriate Thresholds
- Use `PerformanceThresholds.Conservative` for CI
- Use `PerformanceThresholds.Aggressive` for local development
- Use `PerformanceThresholds.Headless` for CI headless runs

### 3. Baseline Updates
- Update baselines after intentional performance improvements
- Document reasons for baseline updates
- Review baselines periodically for relevance

### 4. Test Isolation
- Each performance test should be independent
- Avoid dependencies between performance tests
- Clean state between tests

### 5. Metrics Selection
- Focus on metrics relevant to the test scenario
- Don't over-assert on secondary metrics
- Use p95/p99 for consistency validation

## Troubleshooting

### Performance Counters Not Available

If `PerformanceCollector.IsAvailable` returns false:

1. Check that the app exposes performance counters via TCP interface
2. Verify `PERF:START` command is supported in the app
3. Check logs for connection errors

### Unstable Performance Metrics

If metrics vary significantly between runs:

1. Increase warm-up time before measuring
2. Run multiple iterations and average results
3. Check for background processes
4. Use percentile metrics (p95/p99) instead of averages

### Baseline Comparison Failures

If baselines cause false regression alerts:

1. Check environment consistency
2. Increase `RegressionTolerancePercentage`
3. Use environment-specific baselines
4. Review if thresholds are realistic

### High Memory Usage in Tests

If tests report high memory:

1. Check for memory leaks in the application
2. Verify GC is running (check `Gen0/1/2 Collections`)
3. Review large buffer handling
4. Consider using `GC.Collect()` in setup/teardown

## Metrics Reference

### FPS Thresholds

| Environment | Min FPS | Target FPS |
|-------------|---------|------------|
| Desktop     | 30      | 60         |
| CI/Headless | 1       | 15         |
| Aggressive  | 60      | 120        |

### Frame Time Thresholds (ms)

| Environment | Avg    | P95     | P99     |
|-------------|--------|---------|---------|
| Desktop     | 16.67  | 33.33   | 50      |
| CI/Headless | 100    | 200     | 300     |
| Aggressive  | 8.33   | 16.67   | 33.33   |

### Memory Thresholds

| Metric | Warning | Critical |
|--------|---------|----------|
| Heap Size (Desktop) | 512 MB | 1 GB |
| Heap Size (Headless) | 1 GB | 2 GB |
| Allocations/Operation | 100 | 1000 |

## Additional Resources

- [xUnit Documentation](https://xunit.net/)
- [Avalonia Headless Testing](https://docs.avaloniaui.net/docs/concepts/headless/)
- [.NET Performance Best Practices](https://docs.microsoft.com/en-us/dotnet/framework/performance/performance-tips)
