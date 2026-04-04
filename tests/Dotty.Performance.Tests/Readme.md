# Dotty.Performance.Tests

Comprehensive performance test suite for the Dotty terminal emulator using BenchmarkDotNet.

## Overview

This project provides detailed performance benchmarks for all critical components of the Dotty terminal emulator:

- **Parser Performance**: ANSI/VT sequence parsing speed
- **Rendering Performance**: Frame rendering, scrolling, buffer operations
- **Memory Benchmarks**: Allocation patterns, GC pressure, object pooling
- **Startup Performance**: Cold/warm start times, initialization costs
- **Throughput Benchmarks**: Sustained and peak throughput measurements

## Quick Start

### Running All Benchmarks

```bash
dotnet run -c Release
```

### Running Quick Mode (CI)

```bash
dotnet run -c Release -- --mode quick
# Or via environment variable
DOTTY_BENCH_MODE=quick dotnet run -c Release
```

### Running Specific Categories

```bash
# Parser benchmarks only
dotnet run -c Release -- --filter parser

# Memory benchmarks only
dotnet run -c Release -- --filter memory

# Rendering benchmarks only
dotnet run -c Release -- --filter rendering
```

### Running Detailed Mode (Development)

```bash
dotnet run -c Release -- --mode detailed
```

## Benchmark Modes

| Mode | Use Case | Iterations | Output |
|------|----------|------------|--------|
| `detailed` | Local profiling | 20 iterations | Full reports, logs |
| `quick` / `ci` | CI/CD | 5 iterations | JSON, brief summary |
| `memory` | Memory analysis | 15 iterations | Allocation focus |
| `parser` | Parser optimization | 10 iterations | Throughput focus |
| `rendering` | Rendering optimization | 15 iterations | Frame time focus |

## Benchmark Categories

### ParserBenchmarks

Tests ANSI sequence parsing performance:
- Plain text parsing (various sizes)
- Basic ANSI with color codes
- Extended 256-color sequences
- TrueColor (24-bit) sequences
- Complex sequences (cursor, erase, etc.)
- Throughput benchmarks (1MB+ data)

### ParserMicroBenchmarks

Micro-benchmarks for specific operations:
- Individual SGR sequences
- Cursor movements
- Erase operations
- Mode changes
- OSC sequences
- Unicode handling

### MemoryBenchmarks

Memory allocation and GC pressure tests:
- Cell grid allocations
- Buffer operations
- Scrollback handling
- Parser allocations
- Cell attribute operations
- SGR parsing allocations
- Grapheme width calculations

### RenderingBenchmarks

Frame rendering and display operations:
- Full screen redraws (various sizes)
- Scrolling performance
- Progressive updates
- Cursor operations
- Cell rendering
- Buffer operations
- Erase operations

### StartupBenchmarks

Initialization and startup performance:
- Cold start times
- Parser initialization
- Buffer initialization
- First frame rendering
- Resize operations

### ThroughputBenchmarks

Sustained throughput measurements:
- Character throughput (MB/s)
- Line throughput
- Sequence processing
- Mixed workloads
- Interactive session simulation
- Burst processing

### LatencyBenchmarks

Individual operation latency:
- Single character latency
- Parse operations
- Cursor movements
- Clear operations
- Line feed/tab latency

## Output and Reports

Benchmarks generate reports in:

- `BenchmarkDotNet.Artifacts/performance/` - Default BenchmarkDotNet output
- HTML reports - Visual performance summaries
- JSON reports - CI/CD integration
- CSV exports - Spreadsheet analysis

### Report Files

```
BenchmarkDotNet.Artifacts/performance/
├── results/
│   ├── Dotty.Performance.Tests.Benchmarks.*.md
│   ├── Dotty.Performance.Tests.Benchmarks.*.csv
│   └── Dotty.Performance.Tests.Benchmarks.*.log
├── PerformanceReport.html
├── PerformanceReport.json
├── ComparisonReport.html
└── baselines.json
```

## Baseline Tracking

Baselines define acceptable performance thresholds. Set baselines with:

```csharp
var comparer = new BaselineComparer();
comparer.SetBaseline("Parser_PlainText_1KB", expectedMeanMs: 0.1, maxLatencyMs: 0.5, minThroughput: 10000);
comparer.SaveBaselines("baselines.json");
```

### Default Baselines

Default baselines are included for common operations. Regression threshold is 10% by default.

### Updating Baselines

To update baselines after intentional performance improvements:

1. Run benchmarks in detailed mode
2. Review results
3. Update `BaselineComparer.GetDefaultBaselines()` or the `baselines.json` file

## CI/CD Integration

### GitHub Actions

The project is configured to run in CI with:

```yaml
- name: Run Performance Tests
  run: dotnet run --project tests/Dotty.Performance.Tests -c Release -- --mode ci
  env:
    DOTTY_BENCH_MODE: ci
```

### Performance Regression Detection

In CI mode, the test suite:
1. Runs benchmarks with reduced iterations
2. Compares results against baselines
3. Fails the build if regressions exceed 10%
4. Generates JSON reports for artifact storage

### Storing Baselines

Store baseline files as workflow artifacts:

```yaml
- uses: actions/upload-artifact@v4
  with:
    name: performance-baselines
    path: tests/Dotty.Performance.Tests/baselines.json
```

## Test Data

The `TestDataGenerator` creates realistic test content:
- Plain text (code, logs)
- ANSI-colored output
- 256-color sequences
- TrueColor sequences
- Mouse events
- OSC sequences
- Shell session simulation

### Test Data Sizes

- Tiny: 100 bytes
- Small: 1 KB
- Medium: 10 KB
- Large: 100 KB
- XLarge: 1 MB

## Adding New Benchmarks

1. Create a new class in `Benchmarks/` folder
2. Inherit from `PerformanceTestBase`
3. Add `[BenchmarkCategory("YourCategory")]` attribute
4. Implement `[GlobalSetup]` for initialization
5. Add `[Benchmark]` methods

Example:

```csharp
[BenchmarkCategory("Custom")]
public class MyBenchmarks : PerformanceTestBase
{
    private MyComponent _component = null!;

    [GlobalSetup]
    public override void GlobalSetup()
    {
        base.GlobalSetup();
        _component = new MyComponent();
        Warmup(() => _component.DoWork(), 5);
    }

    [Benchmark(Description = "My Operation")]
    public void MyOperation() => _component.DoWork();
}
```

## Interpreting Results

### Key Metrics

- **Mean**: Average execution time
- **StdDev**: Standard deviation (consistency)
- **P50/P95/P99**: Percentile latencies
- **Throughput**: Operations per second
- **Allocated**: Bytes allocated per operation
- **Gen0/1/2**: GC collection counts

### Good Performance Indicators

- StdDev < 10% of mean (consistent)
- P95 < 2x mean (no outliers)
- Allocations stable across runs
- No Gen2 collections during benchmark

### Warning Signs

- High StdDev (noisy measurements)
- Increasing allocations (memory leak)
- Gen2 collections (excessive memory pressure)
- Bimodal distribution (code path variations)

## Troubleshooting

### High Variance

- Increase warmup iterations
- Use Monitoring strategy for unstable workloads
- Check for background processes
- Ensure thermal throttling isn't occurring

### Out of Memory

- Reduce benchmark data sizes
- Increase GC threshold: `WithGcForce(false)`
- Use streaming for large datasets

### CI Failures

- Check baseline file exists
- Verify regression threshold is appropriate
- Review JSON artifacts for details

## Contributing

When adding performance benchmarks:

1. Use realistic test data
2. Include warmup iterations
3. Document expected performance
4. Add baselines for CI
5. Test locally in both detailed and quick modes

## Resources

- [BenchmarkDotNet Documentation](https://benchmarkdotnet.org/)
- [Dotty Architecture Guide](../docs/Architecture.md)
- [Dotty Rendering Performance](../docs/Rendering.md)
- [Dotty Parsing Performance](../docs/Parsing.md)
