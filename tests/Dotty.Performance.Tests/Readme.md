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

### Silk CPU rendering benchmarks

The Silk suite measures the CPU-side frame builder and glyph-atlas work used by
`Dotty.Silk` without creating a GLFW/OpenGL window:

```bash
dotnet run --project tests/Dotty.Performance.Tests -c Release -- --mode quick --filter silk
```

The live Silk OpenGL/VSync cadence is environment-dependent and is measured
separately from BenchmarkDotNet.

### Running Detailed Mode (Development)

```bash
dotnet run -c Release -- --mode detailed
```

## BenchmarkDotNet vs. consolidated evaluation

This project contains **BenchmarkDotNet microbenchmarks**. Use them to isolate
parser, rendering, allocation, startup, and other in-process operations:

```bash
dotnet run --project tests/Dotty.Performance.Tests -c Release
dotnet run --project tests/Dotty.Performance.Tests -c Release -- --mode quick
```

The repository-level comparison harness is separate; it is **not** a
BenchmarkDotNet test. For an end-to-end throughput, memory, and profiling
comparison of the Release apphost with other terminals, run from the
repository root:

```bash
python3 scripts/perf/eval_suite.py all \
  --runs 3 --lines 500000 \
  --include dotty,ghostty,kitty \
  --profile-lines 500000 \
  --captures cpu,counters,alloc,gcdump
```

The CLI also exposes `compare`, `profile`, and `all` subcommands. It requires
the .NET tools `dotnet-trace`, `dotnet-counters`, and `dotnet-gcdump`. The
default app selection uses the lowercase `Release` apphost; unavailable
competitors are reported as skipped rather than treated as failures.

Each invocation writes a unique UTC directory under
`artifacts/perf/eval/`:

```text
<utc-run>/
├── summary.json
├── report.md
├── child logs and JSON
├── raw nettrace and speedscope files
└── gcdump and report
```

Use BenchmarkDotNet for a repeatable, focused code change or allocation
question. Use `eval_suite.py` when the question is terminal-level throughput,
RSS, cross-terminal comparison, or profiler evidence. Do not combine profiled
throughput with unprofiled comparison means: profiling perturbs results and
captures run in separate processes.

### Focused CPU follow-up

Use the direct profiler for a cheap, CPU-only workload matrix when the
consolidated run points at active-work questions. These three commands use the
same fixed-iteration contract; start with `500000` and calibrate `--lines` per
host until the workload's `START`→`END` interval is 2–5 seconds:

```bash
python3 scripts/perf/dotnet_profile.py --app /path/to/dotty \
  --output-dir /tmp/dotty-focused-printable-ascii \
  --workload printable-ascii --lines 500000 --captures cpu --hold-seconds 1

python3 scripts/perf/dotnet_profile.py --app /path/to/dotty \
  --output-dir /tmp/dotty-focused-ansi-heavy \
  --workload ansi-heavy --lines 500000 --captures cpu --hold-seconds 1

python3 scripts/perf/dotnet_profile.py --app /path/to/dotty \
  --output-dir /tmp/dotty-focused-scrolling-heavy \
  --workload scrolling-heavy --lines 500000 --captures cpu --hold-seconds 1
```

`--lines` is the exact number of workload iterations. `printable-ascii`
emits a printable 79-character payload plus a newline; `ansi-heavy` emits
colored segments with SGR sequences plus a newline; and `scrolling-heavy`
emits printable text plus a CSI scroll-up sequence, making it the stress case
for scroll/reflow handling. `--hold-seconds` only keeps the workload process
alive after `END` so the collector can finish; it does not lengthen the
CPU-capture interval. Compare `output_duration_seconds` (the `START`→`END`
window), not the hold.

`profile.json` records lifecycle timestamps for app launch, managed PID,
collector start, gate, `START`, `END`, SIGINT, and collector exit, plus windows
such as `app_launch_to_managed_pid`, `collector_start_to_gate`,
`gate_to_start`, `end_to_sigint`, and `sigint_to_collector_exit`. Use these
fields to separate startup/collector delay from workload time. Startup resize
or reflow and wait/event-loop frames can contaminate a capture, so interpret
them separately from active parser/render work.

CPU capture writes `cpu/top-methods.txt` and
`cpu/dotnet-trace.speedscope.json`. Open the `.speedscope.json` file in
[Speedscope](https://www.speedscope.app/) for an interactive trace. To produce
a deterministic, background-aware summary:

```bash
python3 scripts/perf/speedscope_summary.py \
  /tmp/dotty-focused-printable-ascii/cpu/dotnet-trace.speedscope.json \
  --json-out /tmp/dotty-focused-printable-ascii/cpu/summary.json --top 20
```

`top-methods.txt` contains sampled CPU percentages. The summary contains
evented represented-time weights (with accounting for eligible Dotty time,
excluded background time, non-Dotty time, and unaccounted time); those weights
are not CPU percentages or CPU utilization.

### Compact reference results

The following descriptive reference compares three 500,000-line runs
(27.5 MB per run); three runs are not a stable-population estimate, so p95
should not be read as a statistically robust tail:

| App | Throughput mean / median / p95 (MiB/s) | Output mean / p95 (ms) | Tree RSS (MiB) | Dotty throughput deficit |
|-----|---------------------------------------|------------------------|----------------|--------------------------|
| Dotty | 20.93 / 20.89 / 21.61 | 1254.30 / 1294.29 | 300.26 | — |
| Ghostty | 33.27 / 32.50 / 34.79 | 789.44 / 812.46 | 217.58 | 37.10% |
| Kitty | 50.79 / 50.85 / 54.69 | 518.86 / 560.05 | 216.22 | 58.80% |

In sampled-thread-time profiling of Dotty, `OnRenderCore` accounted for
6.72% inclusive active samples and the `StartPtyPipeline` subtree for
6.46–6.48%. Wait/thread-pool/file-watcher samples dominated the remainder but
are blocked or background activity, not useful work. The gcdump retained
7,141,208 bytes across 9,506 objects; visible `System.Byte[]`,
`System.Single[]`, and `CellInstance[]` retained at least 4,801,520,
635,080, and 630,744 bytes respectively. Profile runs showed roughly
269–279 MiB tree RSS versus 6.81 MiB managed heap; native/GPU/runtime
ownership of that gap is an inference, not a measurement. CPU, allocation, and
gcdump captures succeeded. On this host, counters runs 9 and 10 emitted
malformed empty `Events` JSON; the suite marks these partial and retains the
raw artifact.

Prioritize follow-up experiments as: active-work-only CPU filtering; render
path ablation (composition versus upload/present); ASCII/ANSI/scroll parser
matrix; typed allocation aggregation; and native/GPU memory accounting.

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
