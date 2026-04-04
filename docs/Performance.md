# Performance Testing Guide

This document provides comprehensive guidance on performance testing for the Dotty terminal emulator.

## Table of Contents

1. [Introduction](#introduction)
2. [Performance Test Suite](#performance-test-suite)
3. [Key Metrics](#key-metrics)
4. [Running Benchmarks](#running-benchmarks)
5. [Interpreting Results](#interpreting-results)
6. [Performance Regression Testing](#performance-regression-testing)
7. [Continuous Integration](#continuous-integration)
8. [Optimizing Performance](#optimizing-performance)
9. [Troubleshooting](#troubleshooting)

## Introduction

The Dotty terminal emulator requires consistent high performance to provide a smooth user experience. This performance test suite helps ensure:

- **Responsiveness**: Low latency for individual operations
- **Throughput**: Fast parsing and rendering of terminal output
- **Scalability**: Consistent performance across different terminal sizes
- **Memory Efficiency**: Minimal allocations and GC pressure
- **Stability**: Consistent performance without degradation

## Performance Test Suite

The `Dotty.Performance.Tests` project contains benchmarks organized into categories:

### Parser Performance

Tests the ANSI/VT100 parser that processes terminal escape sequences:

| Benchmark | Description | Target |
|-----------|-------------|--------|
| Plain Text | Parse text without ANSI codes | >100 MB/s |
| Basic ANSI | Parse standard color codes | >50 MB/s |
| Extended ANSI | Parse 256-color sequences | >30 MB/s |
| TrueColor | Parse 24-bit color sequences | >20 MB/s |
| Complex Sequences | Parse cursor/erase/scroll | >10k seq/s |

### Rendering Performance

Tests the rendering pipeline and buffer operations:

| Benchmark | Description | Target |
|-----------|-------------|--------|
| Full Screen 80x24 | Render standard terminal | <2ms/frame |
| Full Screen 120x40 | Render large terminal | <5ms/frame |
| Scrolling | Scroll buffer content | <1ms/line |
| Progressive Updates | Apply incremental changes | <0.5ms/update |

### Memory Performance

Tests allocation patterns and GC impact:

| Benchmark | Description | Target |
|-----------|-------------|--------|
| Grid Allocation | Create cell grids | <1ms |
| Buffer Resize | Change terminal dimensions | <2ms |
| Scrollback | Scrollback buffer operations | <0.5ms/line |
| Parser Allocations | Memory per parsed byte | <1 byte/input |

### Startup Performance

Tests initialization times:

| Benchmark | Description | Target |
|-----------|-------------|--------|
| Cold Start | First initialization | <500ms |
| Warm Start | Subsequent starts | <100ms |
| First Frame | Parse initial content | <50ms |
| Resize | Change dimensions | <10ms |

### Throughput Benchmarks

Tests sustained throughput:

| Benchmark | Description | Target |
|-----------|-------------|--------|
| Sustained 10MB | Long-running throughput | >10 MB/s |
| Burst 10K Lines | Short burst processing | >5k lines/s |
| Interactive | Realistic shell session | <5ms latency |

## Key Metrics

### Primary Metrics

1. **Latency (ms)**
   - Mean: Average execution time
   - P50: Median (50th percentile)
   - P95: 95th percentile (tail latency)
   - P99: 99th percentile (worst case)

2. **Throughput (ops/sec or MB/sec)**
   - Characters processed per second
   - Sequences parsed per second
   - Frames rendered per second

3. **Memory**
   - Allocations per operation
   - GC collections (Gen0/1/2)
   - Working set size
   - Memory traffic (bytes/sec)

### Target Performance Goals

- **FPS**: 60+ FPS for rendering (>16ms frame budget)
- **Parser**: >100 MB/s for plain text, >50 MB/s for ANSI
- **Latency**: <1ms for individual operations
- **Memory**: <1 allocation per input byte
- **GC**: Gen0 only during normal operation

## Running Benchmarks

### Local Development

```bash
# All benchmarks (detailed mode)
dotnet run --project tests/Dotty.Performance.Tests -c Release

# Quick mode for rapid iteration
dotnet run --project tests/Dotty.Performance.Tests -c Release -- --mode quick

# Specific category
dotnet run --project tests/Dotty.Performance.Tests -c Release -- --filter parser
```

### Filter Options

- `--filter parser` - Parser benchmarks only
- `--filter memory` - Memory benchmarks only
- `--filter rendering` - Rendering benchmarks only
- `--filter startup` - Startup benchmarks only
- `--filter throughput` - Throughput benchmarks only

### Environment Variables

- `DOTTY_BENCH_MODE` - Set default mode (detailed/quick/memory/parser/rendering)
- `CI=true` - Automatically enables quick mode with regression checking

## Interpreting Results

### BenchmarkDotNet Output

```
|         Method |     Mean |    Error |   StdDev |   Gen0 | Allocated |
|--------------- |---------:|---------:|---------:|-------:|----------:|
| PlainText_10KB | 45.23 us | 0.89 us | 1.12 us | 0.0916 |     584 B |
```

- **Mean**: Average execution time
- **Error**: Half of 99.9% confidence interval
- **StdDev**: Standard deviation of measurements
- **Gen0**: Gen0 GC collections per 1000 operations
- **Allocated**: Bytes allocated per operation

### Good Results

- **Low StdDev**: <10% of mean (consistent)
- **No Gen1/2**: Only Gen0 collections during benchmark
- **Stable Allocations**: Consistent bytes per operation
- **Low Tail Latency**: P95 < 2x mean, P99 < 3x mean

### Concerning Results

- **High StdDev**: >20% of mean (inconsistent)
- **Gen2 Collections**: Indicates excessive memory pressure
- **Increasing Allocations**: Memory leak or inefficient algorithm
- **Bimodal Distribution**: Two code paths with different performance

## Performance Regression Testing

### Baseline Management

Baselines define acceptable performance thresholds. They are stored in `baselines.json`.

#### Setting Baselines

```csharp
var comparer = new BaselineComparer();
comparer.SetBaseline(
    "Parser_PlainText_10KB",
    expectedMeanMs: 0.05,
    maxLatencyMs: 0.1,
    minThroughput: 100000
);
comparer.SaveBaselines("baselines.json");
```

#### Regression Threshold

Default regression threshold is 10%. A benchmark fails if:
- Mean latency increases >10%
- Throughput decreases >10%
- Allocations increase >10%

### CI Integration

In CI mode, benchmarks:
1. Run with reduced iterations (quick mode)
2. Compare against baselines
3. Generate JSON reports
4. Fail if regressions detected

### Updating Baselines

After intentional performance improvements:

1. Run benchmarks in detailed mode
2. Review results for consistency
3. Update baseline values in source code or JSON file
4. Commit updated baselines

## Continuous Integration

### GitHub Actions Workflow

```yaml
performance-tests:
  name: Performance Tests
  runs-on: ubuntu-latest
  steps:
    - uses: actions/checkout@v4
    
    - name: Setup .NET
      uses: actions/setup-dotnet@v4
      with:
        dotnet-version: '10.0.x'
    
    - name: Run Performance Tests
      run: dotnet run --project tests/Dotty.Performance.Tests -c Release
      env:
        CI: true
        DOTTY_BENCH_MODE: quick
    
    - name: Upload Results
      uses: actions/upload-artifact@v4
      if: always()
      with:
        name: performance-results
        path: |
          BenchmarkDotNet.Artifacts/performance/*.html
          BenchmarkDotNet.Artifacts/performance/*.json
```

### Artifact Storage

Store baseline files and historical results:

```yaml
- name: Store Baselines
  uses: actions/upload-artifact@v4
  with:
    name: performance-baselines
    path: tests/Dotty.Performance.Tests/Baselines/
    retention-days: 90
```

### Regression Notifications

Configure Slack/email notifications for regressions:

```yaml
- name: Check for Regressions
  run: |
    if [ -f "regressions.txt" ]; then
      echo "Performance regressions detected!"
      cat regressions.txt
      exit 1
    fi
```

## Optimizing Performance

### Parser Optimization

1. **Use span-based parsing**: Avoid string allocations
2. **Batch operations**: Process multiple characters together
3. **Skip unnecessary work**: Fast path for plain text
4. **Pool arrays**: Reuse buffers for intermediate results

### Rendering Optimization

1. **Dirty tracking**: Only redraw changed cells
2. **Double buffering**: Avoid tearing during updates
3. **GPU acceleration**: Use SkiaSharp for rasterization
4. **Incremental updates**: Don't redraw unchanged regions

### Memory Optimization

1. **ArrayPool**: Rent and return arrays instead of allocating
2. **Span<T>**: Stack-allocate small buffers
3. **Object pooling**: Reuse objects instead of creating new
4. **Struct types**: Use value types for hot paths

### Common Optimizations

```csharp
// Good: Span-based, no allocation
public void Process(ReadOnlySpan<byte> input)
{
    Span<char> buffer = stackalloc char[256];
    // Process without heap allocations
}

// Good: ArrayPool for larger buffers
public void ProcessLarge(ReadOnlySpan<byte> input)
{
    char[]? rented = ArrayPool<char>.Shared.Rent(1024);
    try
    {
        // Use rented buffer
    }
    finally
    {
        ArrayPool<char>.Shared.Return(rented);
    }
}
```

## Troubleshooting

### High Variance

**Symptoms**: StdDev > 20% of mean

**Solutions**:
- Increase warmup iterations
- Use monitoring strategy instead of throughput
- Close background applications
- Check for thermal throttling
- Disable power saving modes

### Out of Memory

**Symptoms**: Benchmark crashes with OOM

**Solutions**:
- Reduce data sizes in benchmarks
- Process data in chunks
- Use streaming instead of loading all data
- Increase available memory

### Inconsistent Results

**Symptoms**: Results vary significantly between runs

**Solutions**:
- Ensure stable system state
- Disable antivirus during benchmarks
- Run on dedicated hardware
- Use statistical tests to verify significance

### CI Failures

**Symptoms**: Benchmarks pass locally but fail in CI

**Solutions**:
- CI machines may have different specs - adjust baselines
- Use relative comparisons instead of absolute thresholds
- Allow higher variance in CI due to shared resources
- Consider using dedicated CI runners

### No Baseline Found

**Symptoms**: "No baseline defined" warnings

**Solutions**:
- Ensure baselines.json is committed
- Run with --mode detailed first to establish baselines
- Check file path in BaselineComparer constructor

## Additional Resources

- [BenchmarkDotNet Documentation](https://benchmarkdotnet.org/)
- [Dotty Architecture](../Architecture.md)
- [Dotty Rendering Performance](../Rendering.md)
- [Dotty Parsing Performance](../Parsing.md)
- [.NET Performance Best Practices](https://docs.microsoft.com/en-us/dotnet/framework/performance/)
