#!/bin/sh
set -eu

LOG_FILE="${DOTTY_BENCH_LOG:-/tmp/dotty-yes-bench.log}"
start_ms=$(date +%s%3N)
printf '%s start\n' "$start_ms" >>"$LOG_FILE"

yes "The quick brown fox jumps over the lazy dog 0123456789" | head -n 500000

end_ms=$(date +%s%3N)
printf '%s end\n' "$end_ms" >>"$LOG_FILE"

exec sh
