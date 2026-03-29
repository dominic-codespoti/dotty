#!/bin/sh
set -eu
export SHELL="$(pwd)/artifacts/perf/gui-bench-shell.sh"
export DOTTY_BENCH_LOG="${DOTTY_BENCH_LOG:-/tmp/dotty-yes-bench.log}"
exec dotnet run -c Release --no-build --project src/Dotty.App
