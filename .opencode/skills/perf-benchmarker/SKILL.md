---
name: perf-benchmarker
description: Run and analyze Dotty terminal performance benchmarks — microbenchmarks, cross-terminal output tests, and GUI harness measurements
---

# Performance Benchmarker Skill for Dotty

This skill lets you benchmark the Dotty terminal emulator using three complementary test suites: isolated microbenchmarks (BenchmarkDotNet), cross-terminal output throughput comparisons, and real GUI application measurements.

## Prerequisites

- .NET 10 SDK installed
- Python 3 (for harness scripts)
- `make`, `gcc`/`clang` (for native PTY helper)
- .NET diagnostics tools installed and on `PATH`: `dotnet-trace`, `dotnet-counters`, and `dotnet-gcdump`
- If a tool is missing, install it as a global .NET tool (one command per tool):
  `dotnet tool install --global dotnet-trace`,
  `dotnet tool install --global dotnet-counters`, and
  `dotnet tool install --global dotnet-gcdump`
- For fastest results: ReadyToRun publish first
- Competitor terminals (optional, for cross-terminal comparison):
  - `/usr/bin/kitty`
  - `/usr/bin/ghostty`
  - WezTerm via `WEZTERM_BIN` env var

## Quick Overview

Use `eval_suite.py` as the normal entry point. It runs the comparison and .NET
inspection phases in one uniquely named UTC output directory, preserving each
child's JSON, logs, and raw diagnostics artifacts. The older direct scripts
remain useful for focused troubleshooting.

```
Benchmark Type                         Recommended Entry Point
─────────────────────────────────────  ────────────────────────────
Consolidated compare + profile         scripts/perf/eval_suite.py all
Cross-terminal output only             scripts/perf/eval_suite.py compare
.NET CPU/counters/alloc/heap only      scripts/perf/eval_suite.py profile
Parser/buffer microbenchmarks          dotnet run --mode quick --filter bulk
GUI tab/memory harness                 scripts/perf/gui_harness_bench.py
```

## 1. Consolidated Evaluation Suite (Recommended)

Run one consolidated evaluation before drawing conclusions from separate
experiments:

```bash
python3 scripts/perf/eval_suite.py all --runs 3 --lines 500000 --include dotty,ghostty,kitty --profile-lines 500000 --captures cpu,counters,alloc,gcdump
```

The suite defaults to the lowercase Release apphost (published lowercase
`dotty` when available, then the Release `dotty` apphost). Override it with
`--app /path/to/dotty` when needed. `compare`, `profile`, and `all` are
available:

```bash
python3 scripts/perf/eval_suite.py compare --runs 3 --lines 500000 --include dotty,ghostty,kitty
python3 scripts/perf/eval_suite.py profile --profile-lines 500000 --captures cpu,counters,alloc,gcdump
python3 scripts/perf/eval_suite.py all --runs 3 --lines 500000 --include dotty,ghostty,kitty \
  --profile-lines 500000 --captures cpu,counters,alloc,gcdump
```

Each invocation creates a unique UTC directory under
`artifacts/perf/eval/` (for example, `20260918T120000Z-a1b2c3`) containing
`summary.json`, `report.md`, component JSON and stdout/stderr logs, plus the
requested raw artifacts. CPU and allocation captures retain nettrace and
speedscope files; heap capture retains the gcdump and heap report; counters
retains its raw JSON. Start with `summary.json` for machine-readable status and
configuration, then read `report.md` and follow its links to raw files and
logs. These local artifact paths are run outputs, not durable repository links.

Status is evidence, not a claim that every requested measurement succeeded:
`ok` means the component completed; `partial` means usable output exists but a
capture or derived artifact is incomplete; `skipped` means an optional
competitor was unavailable; and `failed` means setup/orchestration or a
required child failed. A missing competitor is normally recorded as skipped,
not as a Dotty comparison failure. The overall suite is `partial` when any
component is partial or skipped, and `failed` only when a component fails.

The comparison report prefers process-tree RSS, which includes the app's child
processes, and falls back to root-process RSS only when tree data is absent.
Do not treat a root-only or legacy `peak_rss` value as equivalent to total
application memory.

The profiler starts separate Dotty processes for each capture. Profiling
changes scheduling and startup behavior, so never merge profiled throughput
with the unprofiled comparison means. Treat three-run means/medians/p95s as
descriptive observations, not estimates of a stable population p95.

If a host emits malformed or empty `Events` JSON from `dotnet-counters`, the
capture is marked `partial` while its raw counters artifact and logs are
retained. Inspect the error in `summary.json`/`report.md`; do not silently
interpret the file as valid counters data.

The suite's direct child scripts are still useful when isolating a failure;
see sections 2–4 for focused commands:

## 2. Microbenchmarks (BenchmarkDotNet)

Isolated parser, buffer, and rendering benchmarks. Run the JIT-compiled project directly:

```bash
# All benchmarks (detailed mode — takes several minutes)
dotnet run --project tests/Dotty.Performance.Tests -c Release

# Quick mode for rapid iteration (~60s)
dotnet run --project tests/Dotty.Performance.Tests -c Release -- --mode quick

# Specific category
dotnet run --project tests/Dotty.Performance.Tests -c Release -- --mode quick --filter bulk
dotnet run --project tests/Dotty.Performance.Tests -c Release -- --mode quick --filter parser
dotnet run --project tests/Dotty.Performance.Tests -c Release -- --mode quick --filter throughput
dotnet run --project tests/Dotty.Performance.Tests -c Release -- --mode quick --filter rendering
dotnet run --project tests/Dotty.Performance.Tests -c Release -- --mode quick --filter memory
dotnet run --project tests/Dotty.Performance.Tests -c Release -- --mode quick --filter startup
```

### Benchmark Categories

| Category | What It Tests | Key Metrics |
|---|---|---|
| `bulk` | 500k-line write: full pipeline, write-only, linefeed-only | ms/op |
| `parser` | Plain text, ANSI, 256-color, TrueColor, complex sequences | μs/op, MB/s |
| `throughput` | 1MB/10MB sustained, 10K/100K lines, mixed workloads | ms, MB/s |
| `rendering` | Full-screen redraw, scroll, progressive updates, cursor | μs/op |
| `memory` | Grid/buffer allocation, resize, scrollback | ms, allocated bytes |
| `latency` | Single char, 10 chars, SGR parse, cursor move, tab | ns/op |

### Example Output

```
| Method                       | Mean     | Error   | StdDev  | Allocated |
|----------------------------- |--------:|--------:|--------:|----------:|
| 'FullPipeline 500k lines'    | 135.8ms | 8.92 ms | 1.38 ms |         - |
| 'WriteOnly 500k lines'       |  96.9ms | 4.18 ms | 1.09 ms |   42 KB   |
```

### Running With ReadyToRun

The microbenchmarks always run under the JIT. For R2R speed, use the consolidated suite or direct cross-terminal harness (section 3).

## 3. Direct Cross-Terminal Output Troubleshooting

Launches Dotty, Kitty, Ghostty, and WezTerm (if found) with the same high-output child workload and measures wall-clock time, RSS, and throughput. Prefer `eval_suite.py compare` for recorded evaluation runs; use this direct script to isolate child-workload or terminal-launch issues.

```bash
# Default: all available terminals
python3 scripts/perf/terminal_output_bench.py --runs 3 --lines 500000

# Specific terminals
python3 scripts/perf/terminal_output_bench.py --runs 2 --lines 500000 --include dotty,kitty

# Custom Dotty binary (e.g. ReadyToRun publish)
python3 scripts/perf/terminal_output_bench.py --runs 2 --lines 500000 --include dotty --app /path/to/dotty
```

### How It Works

1. Creates a temporary `workload.sh` that outputs `500,000` lines of text via a Python one-liner
2. Sets `DOTTY_SHELL` to the workload for Dotty; passes `-e`/`start` args for others
3. Samples RSS every 50ms, preferring the process tree (root plus descendants) for the consolidated report
4. The workload writes `start`/`end` timestamps to a log file with nanosecond precision
5. Reads the log to compute `launch_to_child_start_ms` and `output_ms`
6. Reports throughput in MiB/s (total bytes / output_ms)

### What to Look For

| Metric | Meaning |
|---|---|
| `launch_to_child_start_ms` | Total startup time — app launch to first byte of output |
| `output_ms` | Time for the child to produce all 500k lines through the PTY |
| `throughput_mb_s` | Throughput = total bytes / output_ms |
| process-tree RSS | Preferred memory comparison: sampled peak RSS for the app and descendants |
| root/legacy `peak_rss_mb` | Fallback only when tree RSS is unavailable; not total application memory |


### Environment Variables

| Variable | Effect |
|---|---|
| `DOTTY_SKIP_CONFIG_COMPILE=1` | Skip Roslyn config compilation on startup (set by harness) |
| `DOTTY_BENCH_STARTUP_LOG` | Write nanosecond stage timestamps (set manually) |
| `WEZTERM_BIN` | Path to WezTerm binary if not on PATH |
| `KITTY_BIN` | Override Kitty path |
| `GHOSTTY_BIN` | Override Ghostty path |

## 4. Direct .NET Profiling Troubleshooting

Use the child profiler directly when a capture fails and you need to isolate a
diagnostics-tool problem. The consolidated suite remains the preferred way to
record a comparison plus profile with matching configuration.

```bash
python3 scripts/perf/dotnet_profile.py \
  --app /path/to/dotty \
  --output-dir /tmp/dotty-profile \
  --lines 500000 \
  --captures cpu,counters,alloc,gcdump
```

The profiler writes `profile.json` plus per-capture raw files and collector
logs. CPU capture produces a nettrace, top-methods report, and speedscope
conversion; allocation capture retains nettrace/speedscope (typed allocation
totals are not promised); counters produces JSON; gcdump produces a heap dump
and heap-stat report. A malformed or empty counters `Events` list makes that
capture `partial`, but the raw JSON is retained for diagnosis.

CPU profiles contain blocked/background samples as well as active work. Wait,
thread-pool, and file-watcher samples can dominate totals without representing
rendering or parsing work; do not call them hot application work without
filtering by active workload/thread.

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


## 5. GUI Harness Benchmark


Launches Dotty as a real GUI app, communicates over TCP, and measures tab creation, switching, and memory.

```bash
# Build Release first
dotnet build src/Dotty/Dotty.csproj -c Release

# Eager tabs (default): each tab is activated immediately
python3 scripts/perf/gui_harness_bench.py --runs 2 --new-tabs 20 --switches 200

# Lazy background tabs: tabs created in background, then activated
python3 scripts/perf/gui_harness_bench.py --runs 2 --new-tabs 20 --background-new-tabs --switches 200
```

### TCP Commands

The app listens on `DOTTY_TEST_PORT` for these commands:

| Command | Effect |
|---|---|
| `NEW_TAB` | Create + activate a new tab |
| `NEW_TAB_BG` | Create a background tab |
| `NEXT_TAB` / `PREV_TAB` | Switch tabs |
| `STATS` | Return JSON with session/view/tab counts |
| `WAIT_FOR_IDLE` | Block until UI thread is idle |
| `DUMP` | Return terminal buffer text |
| `TYPE:text` | Send text to active terminal |
| `SHUTDOWN` | Quit the app |

### Key Statistics

| Field | Meaning |
|---|---|
| `totalTabs` | Total TabViewModel instances |
| `sessionsCreated` | TerminalSession objects allocated |
| `sessionsStarted` | Sessions with PTY spawned |
| `mountedViews` | TerminalView instances attached to visual tree |
| `inactiveTimers` | Tabs with inactive-timer armed (for GC) |
| `scrollbackCount` | Lines in scrollback buffer |
| `rss_before_mb` / `rss_after_mb` | RSS sampled from `/proc/pid/status` |

## 6. Interpreting Performance Regressions

### Run-to-Run Variance

Expect ~5-10% variance in wall-clock benchmarks due to:
- CPU frequency scaling / thermal throttling
- Compositor load / GPU contention
- Disk and memory bus contention
- ASLR / code alignment effects


### Current Reference Evidence

The following is a measured reference comparison from three 500k-line runs
(27.5 MB per run). Values are descriptive for this run set; in particular,
do not treat a three-run p95 as a stable population percentile.

| Terminal | Throughput mean / median / p95 (MiB/s) | Output mean / p95 (ms) | Process-tree RSS (MiB) | Dotty throughput deficit |
|---|---:|---:|---:|---:|
| Dotty | 20.93 / 20.89 / 21.61 | 1254.30 / 1294.29 | 300.26 | — |
| Ghostty | 33.27 / 32.50 / 34.79 | 789.44 / 812.46 | 217.58 | 37.10% |
| Kitty | 50.79 / 50.85 / 54.69 | 518.86 / 560.05 | 216.22 | 58.80% |

The profiling reference also measured sampled-thread-time aggregates for active
Dotty work: `OnRenderCore` was 6.72% inclusive, and the
`StartPtyPipeline` PTY/parser subtree was 6.46–6.48%. Wait, thread-pool, and
file-watcher samples dominated other totals but are blocked/background time,
not automatically useful work.

CPU, allocation, and gcdump captures succeeded in that reference run. The
gcdump retained heap was 7,141,208 bytes across 9,506 objects, including at
least 4,801,520 bytes of `System.Byte[]`, 635,080 bytes of `System.Single[]`,
and 630,744 bytes of `CellInstance[]`. Profile process-tree RSS was about
269–279 MiB while managed heap was about 6.81 MiB. Native, GPU, and runtime
ownership of that gap is an inference from the difference, not a measured
breakdown; managed heap must not be reported as total RSS.

On this host, `dotnet-counters` runs 9 and 10 emitted malformed empty `Events`
JSON. The suite therefore marks counters partial and keeps the raw artifact;
inspect it and its logs before relying on counters conclusions.

### Next Investigation Order

Prioritize experiments in this order:

1. Active-work-only CPU filtering.
2. Render-path ablation (composition versus upload/present).
3. ASCII/ANSI/scroll parser matrix.
4. Typed allocation aggregation.
5. Native/GPU memory accounting.

These are follow-up hypotheses and measurements, not explanations established
by the reference table alone.

### Comparing JIT vs ReadyToRun

Microbenchmarks always run under the JIT. The cross-terminal harness can test either build:

```bash
# JIT build
dotnet build src/Dotty/Dotty.csproj -c Release
python3 scripts/perf/terminal_output_bench.py --app src/Dotty/bin/Release/net10.0/dotty

# R2R publish
dotnet publish src/Dotty/Dotty.csproj -c Release -r linux-x64 --self-contained true -p:PublishReadyToRun=true
python3 scripts/perf/terminal_output_bench.py --app src/Dotty/bin/Release/net10.0/linux-x64/publish/dotty
```

The harness auto-detects the R2R binary if present.
