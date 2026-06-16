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
- For fastest results: ReadyToRun publish first
- Competitor terminals (optional, for cross-terminal comparison):
  - `/usr/bin/kitty`
  - `/usr/bin/ghostty`
  - WezTerm via `WEZTERM_BIN` env var

## Quick Overview

```
Benchmark Types                         What It Measures
─────────────────────────────────────   ────────────────────────────
dotnet run --mode quick --filter bulk   Parser + buffer write throughput
artifacts/perf/terminal_output_bench.py End-to-end output, startup time, RSS
artifacts/perf/gui_harness_bench.py     Tab creation, switching, GUI memory
```

## 1. Microbenchmarks (BenchmarkDotNet)

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

The microbenchmarks always run under the JIT. For R2R speed, use the cross-terminal harness (section 2).

## 2. Cross-Terminal Output Benchmark

Launches Dotty, Kitty, Ghostty, and WezTerm (if found) with the same high-output child workload and measures wall-clock time, RSS, and throughput.

```bash
# Default: all available terminals
python3 artifacts/perf/terminal_output_bench.py --runs 3 --lines 500000

# Specific terminals
python3 artifacts/perf/terminal_output_bench.py --runs 2 --lines 500000 --include dotty,kitty

# Custom Dotty binary (e.g. ReadyToRun publish)
python3 artifacts/perf/terminal_output_bench.py --runs 2 --lines 500000 --include dotty --app /path/to/Dotty.App
```

### How It Works

1. Creates a temporary `workload.sh` that outputs `500,000` lines of text via a Python one-liner
2. Sets `DOTTY_SHELL` to the workload for Dotty; passes `-e`/`start` args for others
3. Monitors `/proc/<pid>/status` every 50ms to sample RSS
4. The workload writes `start`/`end` timestamps to a log file with nanosecond precision
5. Reads the log to compute `launch_to_child_start_ms` and `output_ms`
6. Reports throughput in MiB/s (total bytes / output_ms)

### What to Look For

| Metric | Meaning |
|---|---|
| `launch_to_child_start_ms` | Total startup time — app launch to first byte of output |
| `output_ms` | Time for the child to produce all 500k lines through the PTY |
| `throughput_mb_s` | Throughput = total bytes / output_ms |
| `peak_rss_mb` | Maximum RSS during the run |

### Environment Variables

| Variable | Effect |
|---|---|
| `DOTTY_SKIP_CONFIG_COMPILE=1` | Skip Roslyn config compilation on startup (set by harness) |
| `DOTTY_BENCH_STARTUP_LOG` | Write nanosecond stage timestamps (set manually) |
| `WEZTERM_BIN` | Path to WezTerm binary if not on PATH |
| `KITTY_BIN` | Override Kitty path |
| `GHOSTTY_BIN` | Override Ghostty path |

## 3. GUI Harness Benchmark

Launches Dotty as a real GUI app, communicates over TCP, and measures tab creation, switching, and memory.

```bash
# Build Release first
dotnet build src/Dotty.App/Dotty.App.csproj -c Release

# Eager tabs (default): each tab is activated immediately
python3 artifacts/perf/gui_harness_bench.py --runs 2 --new-tabs 20 --switches 200

# Lazy background tabs: tabs created in background, then activated
python3 artifacts/perf/gui_harness_bench.py --runs 2 --new-tabs 20 --background-new-tabs --switches 200
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

## 4. Interpreting Performance Regressions

### Run-to-Run Variance

Expect ~5-10% variance in wall-clock benchmarks due to:
- CPU frequency scaling / thermal throttling
- Compositor load / GPU contention
- Disk and memory bus contention
- ASLR / code alignment effects

### Comparing JIT vs ReadyToRun

Microbenchmarks always run under the JIT. The cross-terminal harness can test either build:

```bash
# JIT build
dotnet build src/Dotty.App/Dotty.App.csproj -c Release
python3 artifacts/perf/terminal_output_bench.py --app src/Dotty.App/bin/Release/net10.0/Dotty.App

# R2R publish
dotnet publish src/Dotty.App/Dotty.App.csproj -c Release -r linux-x64 --self-contained true -p:PublishReadyToRun=true
python3 artifacts/perf/terminal_output_bench.py --app src/Dotty.App/bin/Release/net10.0/linux-x64/publish/Dotty.App
```

The harness auto-detects the R2R binary if present.

## 5. Stage-Level Startup Profiling

Set `DOTTY_BENCH_STARTUP_LOG` to a file path to capture nanosecond-precision stage timestamps:

```bash
DOTTY_BENCH_STARTUP_LOG=/tmp/startup.log DOTTY_SKIP_CONFIG_COMPILE=1 \
  python3 artifacts/perf/terminal_output_bench.py --runs 1 --lines 5000 --include dotty
cat /tmp/startup.log
```

Stages recorded:
- `main_entry` — process start
- `config_check_done` — fast file-existence check done
- `avalon_framework_init` — Avalonia `OnFrameworkInitializationCompleted` begins
- `theme_manager_done` — ThemeManager loaded
- `config_watcher_done` — CSharpConfigWatcher started (or skipped)
- `defaults_applied` — `ApplyDefaultsToResources()` completed
- `avalon_window_created` — `MainWindow` constructed
- `session_start` — `TerminalSession.Start()` called
