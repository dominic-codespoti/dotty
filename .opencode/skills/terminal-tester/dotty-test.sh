#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"
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
                grep -n "class.*:.*E2ETestBase\|class.*:.*IAsyncLifetime\|\[Fact\]\|\[Theory\]\|\[AvaloniaFact\]" "$src" 2>/dev/null || true
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

    local build_flag=""
    if [ "$no_build" = "true" ]; then
        build_flag="--no-build"
    fi

    local project_flag=""
    if [ -n "$project" ]; then
        project_flag="--project \"$PROJECT_ROOT/tests/$project/$project.csproj\""
    fi

    local filter_flag=""
    if [ -n "$filter" ]; then
        filter_flag="--filter \"$filter\""
    fi

    local timestamp
    timestamp=$(date +%Y%m%d_%H%M%S)
    local result_file="$RESULTS_DIR/dotty_results_$timestamp.trx"
    mkdir -p "$RESULTS_DIR"

    echo "=== RUNNING DOTTY TESTS ==="
    echo "Filter: ${filter:-<none>}"
    echo "Project: ${project:-<all>}"
    echo "Results: $result_file"
    echo ""

    # Run dotnet test
    local cmd="dotnet test \"$PROJECT_ROOT/Dotty.slnx\""
    if [ -n "$project_flag" ]; then
        cmd="$project_flag"
    fi
    cmd="$cmd --logger \"trx;LogFileName=$result_file\""
    cmd="$cmd --results-directory \"$RESULTS_DIR\""
    if [ -n "$filter_flag" ]; then
        cmd="$cmd $filter_flag"
    fi
    cmd="$cmd $build_flag"
    cmd="$cmd --verbosity minimal 2>&1"

    echo "\$ $cmd"
    echo ""
    eval "$cmd" || true

    # Parse and display results
    echo ""
    if [ -f "$result_file" ]; then
        parse_trx "$result_file" "$verbose"
    else
        echo "ERROR: Test results file not found at $result_file"
        return 1
    fi
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

    if ! command -v xmllint &>/dev/null; then
        echo "WARNING: xmllint not found. Install libxml2-utils for detailed parsing."
        echo ""
        grep -o 'outcome="[^"]*"' "$trx_file" | sort | uniq -c | while read -r count outcome; do
            echo "  $outcome: $count"
        done
        return
    fi

    local total=0
    local passed=0
    local failed=0
    local skipped=0

    # Count results
    total=$(xmllint --xpath 'count(//UnitTestResult)' "$trx_file" 2>/dev/null || echo 0)
    passed=$(xmllint --xpath 'count(//UnitTestResult[@outcome="Passed"])' "$trx_file" 2>/dev/null || echo 0)
    failed=$(xmllint --xpath 'count(//UnitTestResult[@outcome="Failed"])' "$trx_file" 2>/dev/null || echo 0)
    skipped=$(xmllint --xpath 'count(//UnitTestResult[@outcome="NotExecuted"])' "$trx_file" 2>/dev/null || echo 0)

    echo "=== TEST RESULTS ==="
    echo "Total:  $total"
    echo "Passed: $passed"
    echo "Failed: $failed"
    echo "Skipped: $skipped"
    echo ""

    if [ "$failed" -gt 0 ]; then
        echo "--- FAILED TESTS ---"
        local fail_count
        fail_count=$(xmllint --xpath '//UnitTestResult[@outcome="Failed"]/@testName' "$trx_file" 2>/dev/null | tr ' ' '\n' | sed 's/testName="//;s/"//g')
        while IFS= read -r name; do
            [ -z "$name" ] && continue
            echo "  FAIL: $name"

            if [ "$verbose" = "true" ]; then
                # Extract error message and stack trace
                local msg
                msg=$(xmllint --xpath "string(//UnitTestResult[@outcome='Failed' and @testName='$name']/Output/ErrorInfo/Message)" "$trx_file" 2>/dev/null || echo "")
                local stack
                stack=$(xmllint --xpath "string(//UnitTestResult[@outcome='Failed' and @testName='$name']/Output/ErrorInfo/StackTrace)" "$trx_file" 2>/dev/null || echo "")
                if [ -n "$msg" ]; then
                    echo "    Message: $msg" | head -5
                fi
                if [ -n "$stack" ]; then
                    echo "    Stack trace (first 5 lines):"
                    echo "$stack" | head -5 | sed 's/^/      /'
                fi
            fi
        done <<< "$fail_count"
        echo ""
    fi

    if [ "$passed" -gt 0 ] && [ "$verbose" = "true" ]; then
        echo "--- PASSED TESTS (first 20) ---"
        local pass_count
        pass_count=$(xmllint --xpath '//UnitTestResult[@outcome="Passed"]/@testName' "$trx_file" 2>/dev/null | tr ' ' '\n' | sed 's/testName="//;s/"//g' | head -20)
        while IFS= read -r name; do
            [ -z "$name" ] && echo "  PASS: $name"
        done <<< "$pass_count"
        if [ "$(echo "$pass_count" | wc -l)" -gt 20 ]; then
            echo "  ... and $(echo "$pass_count" | wc -l) more passed tests"
        fi
        echo ""
    fi
}

parse_failed_test_names() {
    local trx_file="$1"
    if ! command -v xmllint &>/dev/null; then
        grep -o 'testName="[^"]*"' "$trx_file" | sed 's/testName="//;s/"//g'
        return
    fi
    xmllint --xpath '//UnitTestResult[@outcome="Failed"]/@testName' "$trx_file" 2>/dev/null | \
        tr ' ' '\n' | sed 's/testName="//;s/"//g'
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
