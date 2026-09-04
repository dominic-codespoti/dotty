#!/bin/bash
# dotty-bench.sh — Wrapper for Dotty performance benchmarks
set -eu

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"

usage() {
    cat <<EOF
Usage: dotty-bench.sh <command> [options]

Commands:
  micro [--quick | --filter <name>]
      Run BenchmarkDotNet microbenchmarks.
      --quick: quick mode (default: detailed)
      --filter bulk|parser|throughput|rendering|memory: run one category

  output [--runs N] [--lines N] [--include <terminals>] [--app <path>]
      Run the cross-terminal output benchmark.
      --include: comma-separated (default: dotty,kitty,ghostty)

  gui [--runs N] [--new-tabs N] [--switches N] [--background]
      Run the GUI harness benchmark.
      --background: create tabs in background instead of eager

  startup [--app <path>]
      Quick startup measurement with stage logging.

  publish
      Publish ReadyToRun binary for benchmarking.

  list
      List available competitor terminals on this system.

Examples:
  dotty-bench.sh micro --quick --filter bulk
  dotty-bench.sh output --runs 2 --lines 500000 --include dotty,kitty
  dotty-bench.sh gui --runs 2 --new-tabs 20 --switches 200
  dotty-bench.sh publish
  dotty-bench.sh list
EOF
    exit 1
}

case "${1:-help}" in
    micro)
        shift
        MODE="--mode quick"
        FILTER=""
        while [[ $# -gt 0 ]]; do
            case "$1" in
                --quick) MODE="--mode quick" ;;
                --filter) shift; FILTER="--filter $1" ;;
                *) echo "Unknown option: $1"; exit 1 ;;
            esac
            shift
        done
        cd "$PROJECT_ROOT"
        dotnet run --project tests/Dotty.Performance.Tests -c Release -- $MODE $FILTER
        ;;

    output)
        shift
        ARGS=""
        while [[ $# -gt 0 ]]; do
            case "$1" in
                --runs|--lines|--include|--app) ARGS="$ARGS $1 $2"; shift 2 ;;
                *) echo "Unknown option: $1"; exit 1 ;;
            esac
            shift
        done
        cd "$PROJECT_ROOT"
        python3 artifacts/perf/terminal_output_bench.py $ARGS
        ;;

    gui)
        shift
        ARGS=""
        while [[ $# -gt 0 ]]; do
            case "$1" in
                --runs|--new-tabs|--switches) ARGS="$ARGS $1 $2"; shift 2 ;;
                --background) ARGS="$ARGS --background-new-tabs" ;;
                *) echo "Unknown option: $1"; exit 1 ;;
            esac
            shift
        done
        cd "$PROJECT_ROOT"
        python3 artifacts/perf/gui_harness_bench.py $ARGS
        ;;

    startup)
        shift
        APP=""
        while [[ $# -gt 0 ]]; do
            case "$1" in
                --app) shift; APP="--app $1" ;;
                *) echo "Unknown option: $1"; exit 1 ;;
            esac
            shift
        done
        LOG="/tmp/dotty-startup-$$.log"
        export DOTTY_BENCH_STARTUP_LOG="$LOG"
        cd "$PROJECT_ROOT"
        python3 artifacts/perf/terminal_output_bench.py --runs 1 --lines 1000 --include dotty $APP > /dev/null 2>&1
        echo "=== Startup Stages ==="
        cat "$LOG"
        rm -f "$LOG"
        ;;

    publish)
        cd "$PROJECT_ROOT"
        dotnet publish src/Dotty/Dotty.csproj \
            -c Release -r linux-x64 --self-contained true \
            -p:PublishReadyToRun=true
        echo "Published to: src/Dotty/bin/Release/net10.0/linux-x64/publish/dotty"
        ;;

    list)
        echo "Detected terminals:"
        for term in kitty ghostty wezterm; do
            path="$(command -v "$term" 2>/dev/null || true)"
            if [ -n "$path" ]; then
                ver="$($term --version 2>/dev/null || true)"
                echo "  $term ($ver) at $path"
            else
                echo "  $term — not found (set WEZTERM_BIN if installed elsewhere)"
            fi
        done
        ;;

    help|*) usage ;;
esac
