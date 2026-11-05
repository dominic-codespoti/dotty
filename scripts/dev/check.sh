#!/bin/sh
# One-shot local verification for a laptop or workstation: builds the solution and runs every automated test
# suite, then prints a summary. Usage: scripts/dev/check.sh [--quick]
#   --quick   skip the Skia/GL and native PTY suites (fastest inner loop)
# Exit status is non-zero when the build or any suite fails. See docs/LaptopTesting.md.
set -u

cd "$(dirname "$0")/../.." || exit 2
quick=0
[ "${1:-}" = "--quick" ] && quick=1

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

if ! command -v dotnet >/dev/null 2>&1; then
    echo "dotnet not found: install the .NET 11 SDK (see global.json)" >&2
    exit 2
fi

log_dir=$(mktemp -d)
trap 'rm -rf "$log_dir"' EXIT
failed=0
summary=""

note() { summary="$summary$1
"; }

echo "==> dotnet build Dotty.slnx -c Release"
if dotnet build Dotty.slnx -c Release > "$log_dir/build.log" 2>&1; then
    note "build                     ok"
else
    grep -E " error " "$log_dir/build.log" | sort -u | head -40
    echo "build failed (full log above is truncated)" >&2
    exit 1
fi

suites="Dotty.Terminal.Tests Dotty.App.Tests"
[ "$quick" -eq 0 ] && suites="$suites Dotty.App.SkiaTests Dotty.NativePty.Tests"

for suite in $suites; do
    echo "==> $suite"
    if dotnet test --project "tests/$suite/$suite.csproj" -c Release --no-build > "$log_dir/$suite.log" 2>&1; then
        status=ok
    else
        status=FAILED
        failed=1
        grep -E "^failed " "$log_dir/$suite.log" | head -20
    fi
    counts=$(grep -A6 "Test run summary" "$log_dir/$suite.log" | grep -E "total|failed|skipped" | tr -s ' ' | tr '\n' ' ')
    note "$(printf '%-25s %-7s %s' "$suite" "$status" "$counts")"
done

if command -v python3 >/dev/null 2>&1; then
    echo "==> scripts/dist tests + packaging check"
    if python3 -m unittest discover -s scripts/dist/tests -q > "$log_dir/py.log" 2>&1 \
        && python3 scripts/dist/gen_packaging.py --check >> "$log_dir/py.log" 2>&1; then
        note "scripts/dist              ok"
    else
        tail -20 "$log_dir/py.log"
        note "scripts/dist              FAILED"
        failed=1
    fi
fi

echo
echo "Summary"
printf '%s' "$summary"
if [ -z "${DISPLAY:-}${WAYLAND_DISPLAY:-}" ] && [ "$(uname)" = "Linux" ]; then
    echo "note: no display detected; GL tests in Dotty.App.SkiaTests were skipped (run under a desktop session or xvfb-run)."
fi
exit "$failed"
