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

The performance project includes startup benchmarks for host initialization and
first-frame work. Configuration loading uses the runtime JSON model and watcher;
the current host does not compile a user configuration project.

| Benchmark | Description | Target |
|---|---|---|
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

- **FPS**: 60+ FPS for rendering (<16ms frame budget)
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

- `DOTTY_BENCH_MODE` - Set default mode (`detailed`, `quick`, `ci`, `memory`,
  `parser`, or `rendering`)
- `CI=true` - Automatically enables quick mode with regression checking

### Consolidated end-to-end evaluation

For a reproducible comparison of the built app and available reference
terminals, run the evaluation suite from the repository root:

```bash
python3 scripts/perf/eval_suite.py all --runs 3 --lines 500000 \
  --include dotty,ghostty,kitty --profile-lines 500000 \
  --captures cpu,counters,alloc,gcdump
```

The suite exposes three subcommands: `compare` runs the unprofiled reference
comparison, `profile` collects diagnostics for the Dotty process, and `all`
does both. Replace `all` in the command above with `compare` or `profile` when
you need only one phase; keep `--captures` with `profile` to select capture
types.

The app default is the lowercase `Release` apphost. If a requested competitor
is not installed, it is reported as **skipped** rather than treated as a zero
or failed run.
Output comparison defaults to one unmeasured warmup round (`--warmup-runs 1`)
followed by the requested measured rounds (`--runs`). Each round visits every
participant serially; participant order rotates by round so a terminal is not
always favored by first/last position. Warmup records remain in JSON but are
excluded from statistics. Requested competitors that are unavailable remain
`skipped`, and any missing requested round makes the comparison partial; a
partial set is not a complete cross-terminal comparison. JSON includes run IDs,
workload line hash, terminal executable hashes and reported versions. The 5M output
workload checks both the child PTY ioctl and `stty size` report 80x24 before its
timing start event; other geometry is partial evidence, never an apparently valid run.
For skipped or partial launches, result JSON retains the generated workload script and
child stderr/log tail so terminal startup and shell failures remain diagnosable after
the temporary workload directory is removed.
Required .NET diagnostics tools for `profile` are `dotnet-trace`,
`dotnet-counters`, and `dotnet-gcdump`.


Each invocation writes a unique UTC directory below `artifacts/perf/eval/`.
The directory contains `summary.json`, `report.md`, per-run child logs and
JSON, and (when requested and supported) raw nettrace, speedscope, and
gcdump/report artifacts. In reports, **passed** means the command completed
with usable measurements, **partial** means some requested capture or metric
was unavailable while other output was retained, **skipped** means a requested
program or optional dependency was absent, and **failed** means the operation
could not produce its expected result. Inspect the raw child artifact before
discarding a partial result.

This command is intentionally separate from the BenchmarkDotNet commands
above: it exercises sustained terminal output and external-process diagnostics,
so its throughput and memory numbers should not be merged with BenchmarkDotNet
means.

### Real Neovim scroll benchmark

Use the separate `nvim-scroll` command for real Neovim/compositor scrolling; it is
not part of `all` or synthetic output-throughput comparison. Build the current
native PTY helper and Release apphost first. Pass `--app` explicitly to avoid an
older published binary:

```bash
make -C src/Dotty.NativePty
dotnet build src/Dotty/Dotty.csproj -c Release
python3 scripts/perf/eval_suite.py nvim-scroll \
  --app src/Dotty/bin/Release/net11.0/dotty \
  --include dotty,ghostty,kitty --lines 1000000 --cols 200 --rows 60 \
  --runs 5 --warmup-runs 1 --profile plain --capture auto --sample-hz 60 \
  --startup-timeout 30 --run-timeout 1800 --timeout 86400
```

Requires Neovim 0.10+, .NET 11, Python 3, the desired terminals, and a Linux
display: Hyprland with `hyprctl`/`grim`, or X11 with `xdotool`. Neovim's experimental
`nvim__redraw` API is used per step with `valid=true`, `statusline=true`, and
`flush=true`; record the Neovim version because behavior can change. Use
`--profile syntax` for built-in C syntax highlighting. For virtual X11, explicitly
use `--display-kind virtual` and unset `WAYLAND_DISPLAY` and
`HYPRLAND_INSTANCE_SIGNATURE` (e.g. under Xvfb); this does not prove physical
compositor presentation. The published long-run example requests 200x60; the
benchmark also permits larger stress grids, which must fit the physical display
at the selected terminal font size. Dotty uses DejaVu Sans Mono at 16px and
competitors at 12pt; treat these as configurations, not equivalent cell sizes.
The child JSON records each terminal's measured Neovim grid and window rectangle,
plus monitor geometry, configured font, binary hash/version, and capture backend.
Inspect those actual values instead of inferring equal cell geometry from the
requested grid or font settings. A comparison is complete only for participants
with matching observed grid/display conditions; keep mismatches as partial evidence.
Before measured rounds, use this one-run physical-display pilot (100k lines, 200x60).
The example acquires at 120 Hz with a strict 60-Hz minimum observation-quality floor;
verify every participant and the actual cadence, not just the requested rates:

```bash
dotnet publish src/Dotty/Dotty.csproj -c Release -o /tmp/dotty-portable-benchmark \
  -p:RequireUnixPtyHelper=true -p:IncludeSourceRevisionInInformationalVersion=false
APP=/tmp/dotty-portable-benchmark/dotty
python3 scripts/perf/eval_suite.py nvim-scroll --app "$APP" \
  --include dotty,ghostty,kitty --lines 100000 --cols 200 --rows 60 \
  --runs 1 --warmup-runs 0 --profile plain --capture auto --sample-hz 60 --capture-hz 120 \
  --startup-timeout 30 --run-timeout 600 --timeout 1200
```

The full matched plan uses two output warmups and five measured output rounds;
each 1M-line plain and syntax Neovim profile uses one warmup and five measured rounds.
Terminal order rotates between rounds. A clean pilot qualifies the full visual
comparison. With explicit approval, a full campaign may instead retain partial
capture evidence: preserve every failed-quality round and report its visual gate
as inconclusive, never as a protected latency tail or a clean terminal ranking.
Identical requested/actual grids and capture settings remain required.

```bash
APP=/tmp/dotty-portable-benchmark/dotty
python3 scripts/perf/eval_suite.py compare --app "$APP" --include dotty,ghostty,kitty \
  --lines 5000000 --runs 5 --warmup-runs 2 --timeout 86400
for PROFILE in plain syntax; do
  python3 scripts/perf/eval_suite.py nvim-scroll --app "$APP" \
    --include dotty,ghostty,kitty --lines 1000000 --cols 200 --rows 60 \
    --runs 5 --warmup-runs 1 --profile "$PROFILE" --capture auto --sample-hz 60 --capture-hz 120 \
    --startup-timeout 30 --run-timeout 1800 --timeout 86400
done
```
Reserve the display for the batch: pause other GUI-opening workflows, keep benchmark
windows visible, and avoid workspace changes or occlusion. The harness restores the
original workspace. `--capture-hz` controls the persistent Wayland producer;
`--sample-hz` remains the minimum temporal-quality target. Omit `--capture-hz` to use
the same rate as `--sample-hz`. Both configured and observed rates are recorded, and
both settings must match between baseline and candidate. Oversampling does not
ignore late/unrecognized frames or make a partial result pass. Even a failed marker
decode retains the captured frame's native timestamp; a no-frame observation is
untimed instead of inventing a Python delivery-time acquisition. All missing
observations continue to break continuity and censor affected stall intervals.
Sampler shutdown cancels a pending capture wait explicitly: the observer marks
capture stopping and notifies its condition, and the pending sample exits with
a distinct stopped outcome rather than waiting indefinitely or being reported as
a timed observation. Capture errors remain distinct and take precedence when
present. Closing the observer also verifies that its reader thread has joined.
This lifecycle fix prevents a shutdown wait from hanging; it does not restore
missing frames or qualify any historical capture. Previously recorded failed
or partial rows retain their original status and are not rescored after the fix.
A post-fix real 10,000-line, three-terminal smoke is recorded at
`artifacts/perf/eval/20261004T225306Z-668e8a/nvim-scroll.json`. Dotty, Ghostty,
and Kitty each completed 9,999 moves and verified line 10,000/EOF, with no
capture errors. The runner exited 0, but the campaign remains **PARTIAL**: Dotty
and Kitty each had one late sample (zero dropped); Ghostty had no late or dropped
samples. Exit 0 records runner completion, not visual-quality qualification. A
separate real Wayland cancellation probe paused the producer while a consumer
waited on the capture condition; cancellation surfaced as `VisualObserverStopped`,
and the worker, producer, and reader all joined without synthesizing EOF ([retained
native cancellation probe](../artifacts/perf/competitive-portable/native-capture-cancel-smoke.json)).


The deterministic ~95 MB, one-million-line fixture checks exact one-line
cursor advance for each of N-1 steps and flushes every redraw. Phase/parity
markers correlate Neovim progress with visible pixels. `--capture none` makes
visible metrics partial.
A Wayland run requires the persistent native ROI helper; build it once with
`scripts/perf/build_wayland_roi_observer.sh` (or set `WAYLAND_ROI_OBSERVER_BIN` to the
resulting executable). Its JSONL frames use monotonic capture timestamps and a single
latest-frame slot, so the consumer never drains stale queued images.
Before releasing the Neovim go gate, capture must yield a continuous decoded ready/line-1
marker with positive cell dimensions matching the initial observation; the pre-roll sample
and monotonic timestamp are retained. Helper startup is outside the traversal capture
window instead of being hidden by dropping the first late sample.
The persistent Wayland ROI includes the marker and EOF orange band. EOF is proved
only when both appear in the same captured frame, using that native frame timestamp;
the observer does not launch a competing timed `grim` capture or backdate later
verification onto an earlier marker. Non-continuous/X11 capture retains its separate
EOF verification worker. Full-window PNGs are retained after timed sampling. The
observer records requested and observed Hz, cadence-met status, and capture metadata;
cadence is met only at >=90% of the requested rate with zero late or dropped samples.
Each visual result also stores `capture_metadata.native_helper` with the absolute binary
path and SHA-256, plus the source C and pinned screencopy protocol XML paths and SHA-256
values. This identifies the actual helper invoked, including when a stale cache binary is
present; rebuild it with `scripts/perf/build_wayland_roi_observer.sh` before benchmarking.
Preserve helper stderr in `wayland-observer.stderr`. Missing/late markers, geometry or
style mismatches, missing participants/rounds, or cadence gaps make visual evidence
partial/unknown; preserve usable traversal values and never substitute zero for censored
visual values. Samples are compositor observations, not GPU fences or proof of root cause.

JSON records launch-to-ready, file-open-to-ready, geometry-ready, traversal,
visual threshold counts, largest positive visible-line jump, EOF visibility
tail, and RSS. Markdown reports traversal/lines per second, longest observed
stall, EOF tail, RSS, status, and evidence/artifact links; threshold counts,
opening timings, and maximum jump are JSON-only. RSS is a sampled process-tree
maximum spanning startup, traversal, and EOF presentation tail; it is not an
exact peak and excludes the external observer/supervisor. Cleanup and
stop-release memory are outside the measurement. Artifacts under
`artifacts/perf/eval/<run-id>/` include `nvim-scroll.json`, `report.md`, per-run logs,
raw samples/events, and compositor screenshots when available. The per-run
timeout includes traversal and EOF presentation tail; suite timeout covers the
whole suite. The runner cleans up owned descendants/windows, restores the
original Hyprland workspace, and leaves terminal user data untouched.

### Neovim output processing and synchronized presentation

Neovim's line-by-line redraw stream exercises bounded-region delete-line
operations and erase-to-end-of-line on every move. These paths move contiguous
hot/cold row storage together with row metadata, rather than copying normalized
cells individually. Erasure clears a span, repairs wide-glyph boundaries, and
recalculates row extents once per range instead of once per cell. This removes
quadratic work from line erasure without changing the PTY queue or dropping
intermediate terminal updates.

CSI 2026 presentation holds expire after 1,000 ms from the first BEGIN. Repeated
BEGIN does not renew the deadline; END releases the hold and a subsequent BEGIN
starts a fresh deadline. Expiry permits presentation even if the application
has not sent END. This is a stuck-producer failsafe, not normal frame pacing or
a guarantee of one-second end-to-end display latency.

With `DOTTY_TEST_PORT` enabled, `STATS` exposes cumulative `ptyBytesRead`,
`ptyBytesParsed`, `pendingOutputChunks`, `modelGeneration`,
`presentedGeneration`, and `presentCount`, alongside synchronization-hold
counters. Sample these alongside workload events to distinguish input/parser
backlog from presentation delay. Byte counters advance at read/chunk-completion
boundaries, and generation observations are not an atomic pipeline snapshot or
a GPU/compositor fence. A null model generation means the diagnostic lock
attempt failed, not that the terminal model is empty.

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

### Consolidated evaluation findings (2026-09-18)

The following reference run used three runs of 500,000 lines (27.5 MB per
run). Dotty throughput was 20.93 MiB/s mean, 20.89 median, and 21.61 p95;
output time was 1,254.30 ms mean and 1,294.29 ms p95; tree RSS was 300.26
MiB. Ghostty measured 33.27/32.50/34.79 MiB/s (mean/median/p95), 789.44 ms
mean and 812.46 ms p95, and 217.58 MiB RSS. Kitty measured
50.79/50.85/54.69 MiB/s, 518.86 ms mean and 560.05 ms p95, and 216.22 MiB
RSS. Relative to these runs, Dotty throughput was 37.10% below Ghostty and
58.80% below Kitty. Three runs are descriptive; their p95 values are not a
stable population estimate.

Profiling sampled thread time found `OnRenderCore` at 6.72% inclusive and the
`StartPtyPipeline` subtree at 6.46–6.48% of active Dotty samples. Wait,
thread-pool, and file-watcher samples dominate the trace but are blocked or
background time, not demonstrated useful-work bottlenecks. A gcdump retained
7,141,208 bytes across 9,506 objects; visible `System.Byte[]`,
`System.Single[]`, and `CellInstance[]` retained at least 4,801,520,
635,080, and 630,744 bytes respectively. Profile tree RSS was about
269–279 MiB while managed heap was about 6.81 MiB; native, GPU, and runtime
ownership of that gap is an inference, not a measurement. CPU, allocation, and
gcdump captures succeeded. On this host, `dotnet-counters` runs 9 and 10
produced malformed empty `Events` JSON; the suite marks this **partial** and
retains the raw artifact. Profilers perturb throughput, and each capture uses
a separate process, so profiled runs must not be combined with comparison
means.


### Neovim parser optimization findings (2026-10-03)

Profiling a captured real Neovim 200x60 output stream localized expensive work
to delete-line cell copying and repeated `ClearCell` boundary/row-extent scans.
Before optimization, warm replay measured roughly 4.7 MB/s; after row memmoves
and range clears it measured roughly 75 MB/s, retaining the same final visible
screen hash. This isolated replay measures parser/buffer work, not GUI output
throughput or full-state equivalence.

A subsequent one-million-line, 200x60 real Hyprland run used the same ~95 MB
fixture, Neovim 0.12.5, and 10 Hz compositor sampling. All three terminals
completed exactly 999,999 moves and displayed the final EOF line:

| Terminal | Traversal (s) | Observed EOF-to-visible tail (ms) | Sampled tree RSS (MiB) |
|---|---:|---:|---:|
| Dotty | 119.55 | 183.21 | 402.94 |
| Ghostty | 121.83 | 179.88 | 209.92 |
| Kitty | 115.22 | 102.56 | 315.36 |

The earlier Dotty million-line run had a 4,756.21 ms EOF tail; the new observed
tail is about 26 times shorter. These are separate runs, not a controlled
population estimate. Each terminal has only one new measured run, and capture
cadence gaps mark visual results partial (late samples: Dotty 1, Ghostty 19,
Kitty 1). Do not infer a stable speed ranking or a guaranteed maximum stall.
Raw results, samples, events, and compositor images are under the local artifact
directory `artifacts/perf/nvim-parser-optimized/20261002T154701Z-e532f1/`.

A separate diagnostic 100,000-line run reached identical final read/parsed byte
counts at the first sampled point 61 ms after workload END, with zero pending
chunks and matching model/presented generations. It is smaller, instrumented
evidence, not the million-line comparison. A live virtual-X11 GUI smoke also
verified that repeated CSI 2026 BEGIN commands cannot prevent the new screen
from appearing at the 1,000 ms failsafe before END arrives.

## Performance Regression Testing

### Baseline Management

The gate matches `BenchmarkCase.Descriptor.WorkloadMethodDisplayInfo` to baseline keys by ordinal exact name; description-based benchmark names include BenchmarkDotNet's surrounding single quotes.
The CI gate covers every benchmark category represented by a checked-in baseline.
An executed benchmark without a baseline is reported as `NEW` and informational, while a baseline key unused by any benchmark is warned about.
In quick/CI modes, any first-pass failures are re-measured once, and only those still failing on the second pass fail the gate. Re-measurements run only the initially failing benchmarks in BenchmarkDotNet processes; first-pass-only failures are reported as transient, while a missing second-pass result remains a failure.

The latency gate compares the BenchmarkDotNet median with:

```text
allowedMs = baselineMs * (1 + relativeTolerance) + 0.000075
```

The default relative tolerance is 50%; the 0.000075 ms absolute floor is 75 ns. This uses medians to reduce sensitivity to isolated shared-runner outliers. Example limits:

| Baseline | Allowed median | Effective multiplier |
|---:|---:|---:|
| 0.6 us | 0.975 us | 1.625x |
| 1 us | 1.575 us | 1.575x |
| 2 ms | 3.000075 ms | 1.500x |
| 10 ms | 15.000075 ms | 1.500x |

Allocation limits compare bytes per operation from BenchmarkDotNet's `GcStats.GetBytesAllocatedPerOperation(BenchmarkCase)` and allow `allowedBytes = ceilingBytes + max(64, ceilingBytes * 0.10)` to absorb small counter variation from pooling and tiering (64 B allows 128 B; 1,000,000 B allows 1,100,000 B). Null BDN allocation data is treated as 0 bytes/op; recalibration continues to store `ceil(measuredBytesPerOp) + 64`.

### CI Integration

In CI mode, benchmarks run with reduced iterations, compare medians and per-operation allocation values against exact-name baselines, and generate JSON reports. Missing and unmatched baseline names are emitted as warnings.

### Updating Baselines

Reseed baselines with the manual GitHub Actions `recalibrate-baselines.yml` workflow. Its `filter` input uses category substring matching; GitHub replaces an empty input with the configured default (`parser memory rendering silk startup throughput bulk`), so use that full string to reseed every category.

The workflow writes current medians and per-operation allocation ceilings for benchmarks it ran, removes baseline keys that match no benchmark in the full suite, and preserves matching baselines for unrun categories. Latency baselines are runner-specific; allocation ceilings should be recalibrated from a current run.

Use `dotnet run --project tests/Dotty.Performance.Tests -c Release -- --mode gate-self-test` for deterministic checks of the threshold boundaries, bytes-per-operation normalization, and exact-name orphan detection.

### Repeatable baseline snapshots

The existing benchmark JSON stays the source of truth. Capture it into a small
immutable snapshot and compare snapshots without rerunning or altering the
workload:

    python3 scripts/perf/perf_baseline.py capture --kind throughput --input <compare.json> --output artifacts/perf/baselines/output-before.json
    python3 scripts/perf/perf_baseline.py capture --kind nvim --input <nvim-scroll.json> --output artifacts/perf/baselines/nvim-before.json
For repeated primitive comparison outputs, repeat --input once per run; the
captured distribution pools raw per-run values and records every source path and
SHA-256. Each input must have the same workload, host, and build fingerprint.

Repeat the capture commands with the matching after-run JSON, then compare each
pair using perf_baseline.py compare --baseline <before.json> --candidate
<after.json>. Exit 1 means regression or incompatible inputs; exit 2 means
partial evidence or inconclusive evidence. Inconclusive means a point estimate
crosses a configured gate but the measured noise floor prevents confidence;
repeat the benchmark and compare the pooled captures rather than treating it as
pass. The default gates are a 5% throughput or traversal regression and 10%
latency/RSS regression. Snapshots retain median, p95, MAD, sample count, status,
source hashes, and compatibility metadata. Workload dimensions, build family,
host, Neovim fixture/version, font, display geometry, and sampling settings
must match, including both the minimum observation rate and native acquisition rate.
Cross-snapshot comparison allows the intended Dotty executable path/hash and bundled
GLFW implementation identity to change; it reports their exact before/after values
in `build_changes` and retains full provenance in both snapshots. Competitor identity,
system/runtime native dependencies, and all experimental conditions remain strict.
Pooling multiple inputs within one capture still requires identical build provenance.

#### Observed Dotty before/after comparison (2026-10-02)

These captures compare frozen AOT apphosts before and after integration on the
same Linux x86_64 host. Public NativeAOT builds use the portable CPU baseline;
local benchmark builds may explicitly opt into host-native instruction tuning
with `-p:IlcInstructionSet=native`, which is not suitable for portable releases.
The output-throughput workload ran five alternating 5-million-line samples per build
(275,000,000 bytes per sample). The Neovim workload used one million lines,
Neovim 0.12.5, a real Hyprland/eDP-1 display
(2560x1600 at 120.001 Hz, scale 1), a 200x60 grid in a 2000x1140 window,
DejaVu Sans Mono 16px, and the same 94,999,925-byte fixture
(81d88047e249252f6f1d11a8394196a486be1c9fc26ad9ff7940fe08f431e31a). Every
measured Neovim window matched that geometry.

| Workload | Metric | Before median | After median | Change | Regression gate | Metric result |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| 5M output | Throughput | 76.014 MB/s | 76.548 MB/s | +0.70% | -5% | ok |
| 5M output | Output duration | 3450.182 ms | 3426.087 ms | -0.70% | +5% | ok |
| 5M output | Process-tree peak RSS | 237.352 MiB | 239.258 MiB | +0.80% | +10% | ok |
| Neovim | Traversal | 114567.331 ms | 113508.224 ms | -0.92% | +5% | ok |
| Neovim | Lines per second | 8728.483 | 8809.925 | +0.93% | -5% | ok |
| Neovim | Longest sampled visible stall | 146.720 ms | 140.727 ms | -4.08% | +10% | ok |
| Neovim | EOF-to-visible tail | 105.869 ms | 114.145 ms | +7.82% | +10% | ok; within measured noise |
| Neovim | Process-tree peak RSS | 351.047 MiB | 351.008 MiB | -0.01% | +10% | ok |

The throughput comparison used five compatible samples per build and is
complete (artifacts/perf/integration-baselines/output-5m-comparison.json).
For Neovim, the pooled before snapshot retains all three source captures and
their hashes. Traversal, lines/s, and RSS have 15 measured samples; visible
stall and EOF-tail metrics have 11 successful samples. Four of the 15 before
captures had a late sample and two had dropped samples; none had capture errors.
Each source is consequently marked partial because cadence gaps can censor
stall intervals. The after run has five measured samples, all with verified
final screens and no late/dropped samples or capture errors. Thus each Neovim
metric is below its regression gate, but the overall Neovim comparison remains
partial with repeat_required=true; it is not a complete pass. The pooled
comparison and snapshots are in artifacts/perf/integration-baselines/.

Incomplete captures retain the available metrics, represent missing metrics as
unavailable (never zero), and cannot pass as complete. Neovim visible-stall and
EOF-to-visible timings require successful visible capture; RSS uses process-tree
measurements only. A one-run Neovim capture is descriptive, not a stable tail
latency estimate. This tool does not create parser microbenchmark baselines;
BenchmarkDotNet artifacts remain separate and must be compared under their own
matching runtime/hardware conditions.

Eval runs already write to unique UTC directories. Keep the raw source JSON and
logs alongside published snapshots so comparisons remain traceable.
### Final portable benchmark evidence (2026-10-05)

The portable AOT 5M-output comparison is a formal **PASS**: all five measured
samples per build were complete (5/5 before and 5/5 after). Median throughput
was 80.8646 to 80.0348 MiB/s (-1.03%); process-tree peak RSS was 208.7148 to
190.793 MiB (-8.59%). See
`artifacts/perf/competitive-portable/throughput-comparison-qualified.json`.

The two full Neovim campaigns each retain 18 records per side, but visual
capture was not complete: the plain baseline is partial and the final campaign
has a Kitty capture-shutdown failure despite exact cursor-to-EOF traversal.
The formal plain comparison is **REGRESSION**, not PASS: its limited qualified
longest-stall subset is 22.210654 to 31.7572125 ms (+42.98%; 4 before and 2
after samples), and the result correctly remains `evidence_complete=false` and
`repeat_required=true`. Traversal (+2.66%) and RSS (-4.81%) meet their metrics;
they do not erase the visual-tail signal or incomplete evidence. The syntax
comparison is **PARTIAL**, with unavailable visual tails (five pre-roll
failures before, two after); traversal (-6.01%) and RSS (-6.91%) are descriptive
only, not a visual gate or a complete comparison. See
`artifacts/perf/competitive-portable/nvim-plain-comparison.json` and
`artifacts/perf/competitive-portable/nvim-syntax-comparison.json`.

The final .NET gate recorded 1,224 passed, 0 failed, and 1 skipped of 1,225
project tests; the skipped test is the existing ThemeRoot skip. The final Python
check recorded 89 passed and 0 failed. The documented memory-ownership/PSS
observations are descriptive evidence of owned-capacity accounting, not a
population-wide memory claim. Windows and macOS physical-device qualification
remains blocked; these results make no such qualification claim.
## Continuous Integration

The `performance-tests` job in `.github/workflows/ci.yml` runs on Ubuntu for
pull requests and pushes to `main`. It builds the solution, then runs:

```bash
cd tests/Dotty.Performance.Tests
dotnet run -c Release -- --mode quick
```

The job sets `CI=true` and `DOTTY_BENCH_MODE=quick`, uploads
`tests/Dotty.Performance.Tests/BenchmarkDotNet.Artifacts/` and
`tests/Dotty.Performance.Tests/baselines.json`, and fails when the benchmark
project writes `regressions.txt`. CI does not run the detailed benchmark mode.

## Optimizing Performance

### Parser Optimization

1. **Use span-based parsing**: Avoid string allocations
2. **Batch operations**: Process multiple characters together
3. **Skip unnecessary work**: Fast path for plain text
4. **Pool arrays**: Reuse buffers for intermediate results

### Rendering Optimization

1. **Dirty tracking**: Only redraw changed cells
2. **Double buffering**: Avoid tearing during updates
3. **GPU acceleration**: SkiaSharp prepares glyph coverage; Silk.NET submits
   instanced quads to the OpenGL 3.3 renderer
4. **Incremental updates**: Avoid rebuilding unchanged scene data where the
   current caches permit it

### Memory Optimization

1. **ArrayPool**: Rent and return arrays instead of allocating
2. **Span<T>**: Stack-allocate small buffers
3. **Object pooling**: Reuse objects instead of creating new
4. **Struct types**: Use value types for hot paths
5. **BufferTextWriter**: Optimized bulk cell write path — reduces per-cell overhead by batching writes and minimizing buffer flushes

### Memory ownership and lifecycle

The opt-in control-port `MEMORY` response separates process RSS/PSS from owned
terminal native arena capacity, owned snapshot-array capacity, atlas bitmap bytes,
and R8/RGBA8 texture payload estimates. Texture payload is computed from page
dimensions and format; it is not measured driver allocation or total GPU memory.
Intrinsic-color probe scratch is reported separately from retained atlas pages.

`managedHeapEstimateBytes` uses `GC.GetTotalMemory(false)` without forcing a
collection; it is not a measurement of live managed objects. Latest-GC heap,
committed bytes and index describe that collection, while allocated-byte counters
are cumulative growth/traffic, not retained ownership. Do not subtract managed
heap estimates from RSS to label the remainder as GPU memory.

Tab/pane disposal releases model ownership once stopped workers and outstanding
snapshots no longer need it. After resize/reflow completes, both the completed
destination and reset spare native arena trim unused capacity to the current
screen geometry. This does not drop active cells or change history/reflow policy;
native realloc may move/copy the arena. It runs at resize completion, not per key
or render frame. Lifecycle measurements and their single-observation caveats are
recorded in [Rendering](Rendering.md).

### Steady-state allocation policy

Warmed interactive paths allocate no managed memory: idle frames, typing, PTY
output, scrolling, hover, selection, tab switching, resizing between
previously seen sizes, and Lua title/status/event hooks. Scratch storage grows
to a high-water mark and is reused. First use, config/Lua reload, tab/pane
creation, clipboard payloads, and changed title text may still allocate.

Each area has thread-local zero-allocation regression tests
(`*AllocationTests.cs` in `tests/Dotty.App.Tests` and `tests/Dotty.Terminal.Tests`).
To measure the real app, run:

```bash
dotnet build src/Dotty/Dotty.csproj -c Release
python3 scripts/perf/steady_state_alloc.py            # bytes per warmed scenario
python3 scripts/perf/steady_state_alloc.py --only resize_8 \
  --trace /tmp/alloc.nettrace --every-object         # per-object stacks (JIT build)
```

The harness runs Dotty under a private Xvfb display, drives it with `xdotool`,
and reads `GC.GetTotalAllocatedBytes` through the `ALLOC` control command
(available only with `DOTTY_TEST_PORT`). The control transport's own
allocations are calibrated and subtracted; residues of a few hundred bytes that
move between scenarios are transport noise.

### Cold-Start Optimization

1. **Startup benchmarks**: `StartupBenchmarks` records host initialization
   phases for comparison across runs.
2. **Lazy glyph atlas population**: Glyphs are added to the shared atlas as
   they are first encountered.
### Measured priorities and next experiments (2026-09-18)

Treat the comparison deficit as an outcome, not proof of one cause. Run the
next experiments in this order:

1. **Filter to active work only** in CPU traces, separating useful render and
   parser samples from waits, thread-pool activity, and file watchers.
2. **Ablate the render path** (composition versus upload/present) to determine
   whether the observed render samples explain throughput.
3. **Use an input matrix** covering ASCII, ANSI, and scrolling workloads to
   separate parser costs from rendering and terminal-state costs.
4. **Aggregate allocations by type and call path** to turn the visible
   `Byte[]`, `Single[]`, and `CellInstance[]` retained sizes into actionable
   ownership data.
5. **Account for native and GPU memory** so the RSS-to-managed-heap gap is
   measured rather than attributed by inference.

These experiments should use separate unprofiled comparison runs for
throughput and explicitly label any diagnostic capture as perturbing the
workload.


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
- [Dotty Architecture](Architecture.md)
## Changelog

| Date | Change |
|------|--------|
| 2026-06-17 | Added BufferTextWriter optimization, cold-start benchmark guidance, and lazy glyph atlas population |
| 2026-09-18 | Added consolidated evaluation commands, artifact/status semantics, and measured findings |
| 2026-06-15 | Added cold-start benchmark guidance |
| 2026-10-02 | Added real Neovim scrolling/compositor-visible benchmark guidance, measurement caveats, and artifact/cleanup details |
| 2026-10-05 | Recorded portable AOT throughput, partial Neovim evidence, and capture-lifecycle cancellation limits |
| 2026-09-24 | Added the steady-state zero-allocation policy and measurement harness |

---

- [Dotty Rendering Performance](Rendering.md)
- [Dotty Parsing Performance](Parsing.md)
- [.NET Performance Best Practices](https://docs.microsoft.com/en-us/dotnet/framework/performance/)

*Last updated: 2026-10-05*
