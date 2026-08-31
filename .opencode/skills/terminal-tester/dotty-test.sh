#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
PYTHON_BIN="${DOTTY_TEST_PYTHON:-python3}"
if ! command -v "$PYTHON_BIN" >/dev/null 2>&1 && command -v python >/dev/null 2>&1; then
    PYTHON_BIN=python
fi
if ! command -v "$PYTHON_BIN" >/dev/null 2>&1; then
    echo "ERROR: Python 3 is required by dotty-test.sh." >&2
    exit 1
fi
RESULTS_DIR="$PROJECT_ROOT/tests/TestResults"

usage() {
    cat <<'USAGE'
Usage: dotty-test.sh [OPTIONS]

Run Dotty test suites with structured output for AI consumption.

Options:
  --list                List available test projects and categories
  --list --verbose      List with full test method names
  --run [FILTER]        Run tests (optional xunit filter, e.g. "Category=Basic")
  --failed              Re-run only previously failed tests
  --project PROJECT     Target a specific test project (e.g. Dotty.Terminal.Tests)
  --verbose             Show detailed per-test output
  --no-build            Skip build step
  --help                Show this help

Examples:
  dotty-test.sh --list
  dotty-test.sh --run
  dotty-test.sh --run "Category=Core"
  dotty-test.sh --run "FullyQualifiedName~RenderingTest"
  dotty-test.sh --failed
  dotty-test.sh --run --project Dotty.Terminal.Tests
USAGE
}

list_tests() {
    local verbose="${1:-false}"
    echo "=== DOTTY TEST SUITES ==="
    echo ""

    find "$PROJECT_ROOT/tests" -name "*.csproj" -path "*/tests/*" ! -path "*/BenchmarkDotNet*" | sort | while read -r proj; do
        local name
        name=$(basename "$proj" .csproj)
        local dir
        dir=$(dirname "$proj")
        echo "Project: $name"
        echo "  Location: $dir"

        # Extract test class names from source files
        if [ "$verbose" = "true" ]; then
            find "$dir" -name "*.cs" ! -path "*/obj/*" ! -path "*/bin/*" | sort | while read -r src; do
                if grep -n "class.*:.*E2ETestBase\|class.*:.*IAsyncLifetime\|\[Fact\]\|\[Theory\]\|\[AvaloniaFact\]" "$src" 2>/dev/null; then
                    :
                fi
            done | sed 's/^/    /'
        else
            # Just show file names with test classes
            grep -l "class.*Test" "$dir"/*.cs 2>/dev/null | while read -r f; do
                local class_name
                class_name=$(basename "$f" .cs)
                echo "  Tests: $class_name"
            done || echo "  (no test files found)"
        fi
        echo ""
    done
}

run_tests() {
    local filter="${1:-}"
    local project="${2:-}"
    local verbose="${3:-false}"
    local no_build="${4:-false}"

    local target="$PROJECT_ROOT/Dotty.slnx"
    local target_option="--solution"
    if [ -n "$project" ]; then
        target="$PROJECT_ROOT/tests/$project/$project.csproj"
        target_option="--project"
    fi

    local timestamp
    timestamp=$(date +%Y%m%d_%H%M%S)
    local result_file="$RESULTS_DIR/dotty_results_$timestamp.trx"
    local logger_file
    logger_file=$(basename "$result_file")
    mkdir -p "$RESULTS_DIR"

    local -a command
    command=(dotnet test "$target_option" "$target" --report-xunit-trx --report-xunit-trx-filename "$logger_file" --results-directory "$RESULTS_DIR")
    if [ -n "$filter" ]; then
        command+=(--filter "$filter")
    fi
    if [ "$no_build" = "true" ]; then
        command+=(--no-build)
    fi

    echo "=== RUNNING DOTTY TESTS ==="
    echo "Filter: ${filter:-<none>}"
    echo "Project: ${project:-<all>}"
    echo "Results: $result_file"
    printf '$'
    printf ' %q' "${command[@]}"
    printf ' --verbosity minimal\n\n'

    set +e
    "${command[@]}" --verbosity minimal 2>&1
    local test_status=$?
    set -e

    echo ""
    if [ -f "$result_file" ]; then
        parse_trx "$result_file" "$verbose"
    else
        echo "ERROR: Test results file not found at $result_file"
        return 1
    fi
    return "$test_status"
}

run_failed() {
    # Find the most recent TRX file
    local latest_trx
    latest_trx=$(ls -t "$RESULTS_DIR"/*.trx 2>/dev/null | head -1)

    if [ -z "$latest_trx" ]; then
        echo "No previous test results found. Run tests first with --run."
        return 1
    fi

    echo "=== RE-RUNNING FAILED TESTS ==="
    echo "Previous results: $latest_trx"
    echo ""

    # Extract failed test names from TRX
    local failed_tests
    failed_tests=$(parse_failed_test_names "$latest_trx")

    if [ -z "$failed_tests" ]; then
        echo "No failed tests to re-run."
        return 0
    fi

    echo "Failed tests to re-run:"
    echo "$failed_tests"
    echo ""

    # Build filter from failed test names
    local filter=""
    while IFS= read -r test_name; do
        if [ -n "$filter" ]; then
            filter="$filter|"
        fi
        filter="${filter}FullyQualifiedName~${test_name}"
    done <<< "$failed_tests"

    run_tests "$filter" "" "true" "false"
}

parse_trx() {
    local trx_file="$1"
    local verbose="$2"

    "$PYTHON_BIN" - "$trx_file" "$verbose" <<'PY'
import sys
import xml.etree.ElementTree as ET

path = sys.argv[1]
verbose = sys.argv[2] == "true"
try:
    root = ET.parse(path).getroot()
except (OSError, ET.ParseError) as error:
    print(f"ERROR: Could not parse TRX: {error}")
    raise SystemExit(1)

results = root.findall(".//{*}UnitTestResult")
counts = {
    "Passed": sum(result.get("outcome") == "Passed" for result in results),
    "Failed": sum(result.get("outcome") == "Failed" for result in results),
    "NotExecuted": sum(result.get("outcome") == "NotExecuted" for result in results),
}
print("=== TEST RESULTS ===")
print(f"Total:  {len(results)}")
print(f"Passed: {counts['Passed']}")
print(f"Failed: {counts['Failed']}")
print(f"Skipped: {counts['NotExecuted']}")
print()

failed = [result.get("testName", "") for result in results if result.get("outcome") == "Failed"]
if failed:
    print("--- FAILED TESTS ---")
    for name in failed:
        print(f"  FAIL: {name}")
    print()

if verbose:
    passed = [result.get("testName", "") for result in results if result.get("outcome") == "Passed"]
    if passed:
        print("--- PASSED TESTS (first 20) ---")
        for name in passed[:20]:
            print(f"  PASS: {name}")
        if len(passed) > 20:
            print(f"  ... and {len(passed) - 20} more passed tests")
        print()
PY
}

parse_failed_test_names() {
    local trx_file="$1"
    "$PYTHON_BIN" - "$trx_file" <<'PY'
import sys
import xml.etree.ElementTree as ET

root = ET.parse(sys.argv[1]).getroot()
for result in root.findall(".//{*}UnitTestResult"):
    if result.get("outcome") == "Failed":
        print(result.get("testName", ""))
PY
}

# Main
if [ $# -eq 0 ]; then
    usage
    exit 0
fi

MODE=""
FILTER=""
PROJECT=""
VERBOSE="false"
NO_BUILD="false"

while [ $# -gt 0 ]; do
    case "$1" in
        --help|-h)
            usage
            exit 0
            ;;
        --list)
            MODE="list"
            shift
            if [ "${1:-}" = "--verbose" ]; then
                VERBOSE="true"
                shift
            fi
            ;;
        --run)
            MODE="run"
            shift
            if [ $# -gt 0 ] && [[ "$1" != --* ]]; then
                FILTER="$1"
                shift
            fi
            ;;
        --failed)
            MODE="failed"
            shift
            ;;
        --project)
            shift
            PROJECT="$1"
            shift
            ;;
        --verbose)
            VERBOSE="true"
            shift
            ;;
        --no-build)
            NO_BUILD="true"
            shift
            ;;
        *)
            echo "Unknown option: $1"
            usage
            exit 1
            ;;
    esac
done

case "$MODE" in
    list)
        list_tests "$VERBOSE"
        ;;
    run)
        run_tests "$FILTER" "$PROJECT" "$VERBOSE" "$NO_BUILD"
        ;;
    failed)
        run_failed
        ;;
    *)
        usage
        exit 1
        ;;
esac
