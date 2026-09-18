#!/usr/bin/env python3
import argparse
import datetime as _datetime
import json
import math
import os
import platform
import shutil
import signal
import statistics
import subprocess
import tempfile
import time
from pathlib import Path


LINE = "The quick brown fox jumps over the lazy dog 0123456789\n"


def _positive_int(value):
    try:
        parsed = int(value)
    except ValueError as exc:
        raise argparse.ArgumentTypeError("must be an integer") from exc
    if parsed <= 0:
        raise argparse.ArgumentTypeError("must be positive")
    return parsed


def _positive_float(value):
    try:
        parsed = float(value)
    except ValueError as exc:
        raise argparse.ArgumentTypeError("must be a number") from exc
    if not math.isfinite(parsed) or parsed <= 0:
        raise argparse.ArgumentTypeError("must be positive and finite")
    return parsed


def default_app(root=None):
    root = Path(root) if root is not None else Path(__file__).resolve().parents[2]
    release = root / "src" / "Dotty" / "bin" / "Release" / "net10.0"
    # Keep the lowercase apphost as the convention, while accepting older
    # uppercase artifacts when that is all the build tree contains.
    candidates = (
        release / "linux-x64" / "publish" / "dotty",
        release / "dotty",
        release / "linux-x64" / "publish" / "Dotty",
        release / "Dotty",
    )
    return next((candidate for candidate in candidates if candidate.is_file()), candidates[0])


def parse_args(argv=None):
    root = Path(__file__).resolve().parents[2]
    parser = argparse.ArgumentParser(
        description="Launch terminal emulators with the same high-output child workload."
    )
    parser.add_argument("--runs", type=_positive_int, default=3)
    parser.add_argument("--lines", type=_positive_int, default=500_000)
    parser.add_argument("--sample-interval-ms", type=_positive_float, default=50.0)
    parser.add_argument("--startup-timeout", type=_positive_float, default=20.0)
    parser.add_argument("--json-out", type=Path, default=None)
    parser.add_argument("--app", default=str(default_app(root)))
    parser.add_argument("--include", default="dotty,kitty,ghostty,wezterm")
    return parser.parse_args(argv)


def write_workload(script_path):
    script_path.write_text(
        "#!/bin/sh\n"
        "set -eu\n"
        "python3 - <<'PY'\n"
        "import os, sys, time\n"
        "line = b'The quick brown fox jumps over the lazy dog 0123456789\\n'\n"
        "lines = int(os.environ['TERMINAL_BENCH_LINES'])\n"
        "log = os.environ['TERMINAL_BENCH_LOG']\n"
        "with open(log, 'a', encoding='utf-8') as handle:\n"
        "    handle.write(f'{time.time_ns()} start\\n')\n"
        "out = sys.stdout.buffer\n"
        "chunk = line * 1000\n"
        "full_chunks, remainder = divmod(lines, 1000)\n"
        "for _ in range(full_chunks):\n"
        "    out.write(chunk)\n"
        "if remainder:\n"
        "    out.write(line * remainder)\n"
        "out.flush()\n"
        "with open(log, 'a', encoding='utf-8') as handle:\n"
        "    handle.write(f'{time.time_ns()} end\\n')\n"
        "time.sleep(float(os.environ.get('TERMINAL_BENCH_HOLD_SECONDS', '0.25')))\n"
        "PY\n",
        encoding="utf-8",
    )
    script_path.chmod(0o755)


def rss_mb(pid, proc_root="/proc"):
    try:
        with open(Path(proc_root) / str(pid) / "status", "r", encoding="utf-8") as handle:
            for line in handle:
                if line.startswith("VmRSS:"):
                    return int(line.split()[1]) / 1024.0
    except (OSError, ValueError):
        return None
    return None


def _walk_process_tree(pid, proc_root="/proc"):
    root = Path(proc_root)
    visited = set()
    complete = True

    def visit(current):
        nonlocal complete
        if current in visited:
            return
        if not (root / str(current)).exists():
            complete = False
            return
        visited.add(current)
        children_path = root / str(current) / "task" / str(current) / "children"
        try:
            children = children_path.read_text(encoding="utf-8").split()
        except (OSError, UnicodeError):
            complete = False
            return
        for child_text in children:
            try:
                child = int(child_text)
            except ValueError:
                complete = False
                continue
            if child not in visited:
                visit(child)

    try:
        initial_pid = int(pid)
    except (TypeError, ValueError):
        return set(), False
    visit(initial_pid)
    return visited, complete


def process_tree_pids(pid, proc_root="/proc"):
    """Return unique live PIDs reachable from pid through /proc children files."""
    return _walk_process_tree(pid, proc_root)[0]


def process_tree_rss_mb(pid, proc_root="/proc"):
    """Return a complete process-tree RSS sample, or None if a race occurred."""
    pids, complete = _walk_process_tree(pid, proc_root)
    values = [rss_mb(item, proc_root) for item in pids]
    if not complete or not pids or any(value is None for value in values):
        return None
    return sum(values)


def read_events(log_path):
    events = {}
    try:
        for line in log_path.read_text(encoding="utf-8").splitlines():
            parts = line.split()
            if len(parts) == 2:
                events[parts[1]] = int(parts[0])
    except (OSError, ValueError):
        pass
    return events


def terminal_command(name, args, workload):
    if name == "dotty":
        app = Path(args.app)
        return [str(app)] if app.exists() else None
    if name == "kitty":
        exe = os.environ.get("KITTY_BIN") or shutil.which("kitty")
        return [exe, "--config", "NONE", "--detach=no", "--title", "terminal-output-bench", str(workload)] if exe else None
    if name == "ghostty":
        exe = os.environ.get("GHOSTTY_BIN") or shutil.which("ghostty")
        return [exe, "-e", str(workload)] if exe else None
    if name == "wezterm":
        exe = os.environ.get("WEZTERM_BIN") or shutil.which("wezterm")
        return [exe, "start", "--always-new-process", "--", str(workload)] if exe else None
    return None


def stop_process(proc):
    """Terminate a process, then kill it if needed, returning structured errors."""
    errors = []
    if proc.poll() is not None:
        return errors
    try:
        proc.terminate()
    except OSError as exc:
        errors.append({"stage": "terminate", "error": str(exc)})
    try:
        proc.wait(timeout=2)
        return errors
    except subprocess.TimeoutExpired:
        pass
    except OSError as exc:
        errors.append({"stage": "wait", "error": str(exc)})
    try:
        proc.kill()
    except OSError as exc:
        errors.append({"stage": "kill", "error": str(exc)})
    try:
        proc.wait(timeout=2)
    except (subprocess.TimeoutExpired, OSError) as exc:
        errors.append({"stage": "kill_wait", "error": str(exc)})
    return errors


def _sample_metrics(samples, tree_samples):
    return {
        "initial_rss_mb": samples[0] if samples else None,
        "peak_rss_mb": max(samples) if samples else None,
        "final_sample_rss_mb": samples[-1] if samples else None,
        "sample_count": len(samples),
        "initial_tree_rss_mb": tree_samples[0] if tree_samples else None,
        "peak_tree_rss_mb": max(tree_samples) if tree_samples else None,
        "final_tree_rss_mb": tree_samples[-1] if tree_samples else None,
        "process_tree_sample_count": len(tree_samples),
    }


def run_once(name, args, workload, run_number):
    log_path = Path(tempfile.gettempdir()) / f"terminal-output-bench-{name}-{os.getpid()}-{run_number}.log"
    try:
        log_path.unlink()
    except OSError:
        pass

    cmd = terminal_command(name, args, workload)
    base = {"terminal": name, "run": run_number}
    if cmd is None:
        base.update({"skipped": True, "reason": "binary not found", **_sample_metrics([], [])})
        return base

    env = os.environ.copy()
    env["TERMINAL_BENCH_LINES"] = str(args.lines)
    env["TERMINAL_BENCH_LOG"] = str(log_path)
    if name == "dotty":
        env["DOTTY_SHELL"] = str(workload)
    env["DOTTY_SKIP_CONFIG_COMPILE"] = "1"

    started_ns = time.time_ns()
    try:
        proc = subprocess.Popen(cmd, env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    except OSError as exc:
        base.update({"skipped": True, "status": "failed", "reason": f"failed to launch: {exc}", "command": cmd, "command_argv": cmd, **_sample_metrics([], [])})
        return base

    samples = []
    tree_samples = []
    started_event_seen = False
    deadline = time.monotonic() + args.startup_timeout
    sample_interval = args.sample_interval_ms / 1000.0
    result = None

    try:
        while time.monotonic() < deadline:
            current_rss = rss_mb(proc.pid)
            if current_rss is not None:
                samples.append(current_rss)
            current_tree_rss = process_tree_rss_mb(proc.pid)
            if current_tree_rss is not None:
                tree_samples.append(current_tree_rss)

            events = read_events(log_path)
            started_event_seen = started_event_seen or "start" in events
            if "end" in events and "start" in events:
                output_ms = (events["end"] - events["start"]) / 1_000_000.0
                launch_to_start_ms = (events["start"] - started_ns) / 1_000_000.0
                bytes_written = len(LINE.encode("utf-8")) * args.lines
                result = {
                    "terminal": name,
                    "run": run_number,
                    "skipped": False,
                    "status": "ok",
                    "pid": proc.pid,
                    "launch_to_child_start_ms": launch_to_start_ms,
                    "output_ms": output_ms,
                    "throughput_mb_s": (bytes_written / (1024 * 1024)) / (output_ms / 1000.0) if output_ms > 0 else None,
                    "bytes_written": bytes_written,
                    **_sample_metrics(samples, tree_samples),
                    "command": cmd,
                    "command_argv": cmd,
                }
                return result

            if proc.poll() is not None and not started_event_seen:
                result = {
                    "terminal": name,
                    "run": run_number,
                    "skipped": True,
                    "reason": f"process exited before workload started ({proc.returncode})",
                    "command": cmd,
                    "command_argv": cmd,
                    **_sample_metrics(samples, tree_samples),
                }
                return result

            time.sleep(sample_interval)

        result = {
            "terminal": name,
            "run": run_number,
            "skipped": True,
            "reason": "timed out waiting for workload completion",
            "started_event_seen": started_event_seen,
            "command": cmd,
            "command_argv": cmd,
            **_sample_metrics(samples, tree_samples),
        }
        return result
    finally:
        cleanup_errors = stop_process(proc)
        if result is not None and cleanup_errors:
            result["errors"] = cleanup_errors
            result["status"] = "partial" if not result.get("skipped") else result.get("status", "skipped")


def _percentile(values, percentile):
    values = sorted(values)
    if not values:
        return None
    if len(values) == 1:
        return values[0]
    position = (len(values) - 1) * percentile
    lower = int(position)
    upper = min(lower + 1, len(values) - 1)
    fraction = position - lower
    return values[lower] + (values[upper] - values[lower]) * fraction


def _mean(values):
    return statistics.mean(values) if values else None


def summarize(results):
    summary = {}
    for terminal in sorted({result["terminal"] for result in results}):
        terminal_results = [result for result in results if result["terminal"] == terminal]
        values = [result for result in terminal_results if not result.get("skipped") and result.get("status", "ok") != "failed"]
        skipped = [result for result in terminal_results if result.get("skipped")]
        reasons = [item.get("reason") for item in skipped if item.get("reason")]
        if not values:
            failed_results = [item for item in terminal_results if item.get("status") == "failed"]
            summary[terminal] = {
                "skipped": not failed_results,
                "status": "failed" if failed_results else "skipped",
                "runs": 0,
                "success_count": 0,
                "skip_count": len(skipped),
                "reasons": reasons,
                "partial": False,
                "partial_reasons": reasons,
                "launch_to_child_start_ms_avg": None,
                "output_ms_avg": None,
                "throughput_mb_s_avg": None,
                "throughput_mb_s_median": None,
                "throughput_mb_s_min": None,
                "throughput_mb_s_max": None,
                "throughput_mb_s_p95": None,
                "output_ms_p95": None,
                "peak_rss_mb_avg": None,
                "root_rss_mb_avg": None,
                "tree_rss_mb_avg": None,
            }
            continue

        throughputs = [item["throughput_mb_s"] for item in values if item.get("throughput_mb_s") is not None]
        outputs = [item["output_ms"] for item in values if item.get("output_ms") is not None]
        root_rss = [item["peak_rss_mb"] for item in values if item.get("peak_rss_mb") is not None]
        tree_rss = [item["peak_tree_rss_mb"] for item in values if item.get("peak_tree_rss_mb") is not None]
        partial = bool(skipped) or any(item.get("status") == "partial" or item.get("errors") for item in values)
        summary[terminal] = {
            "skipped": False,
            "status": "partial" if partial else "ok",
            "partial": partial,
            "partial_reasons": reasons + [error.get("error", str(error)) for item in values for error in item.get("errors", [])],
            "reasons": reasons,
            "runs": len(values),
            "success_count": len(values),
            "skip_count": len(skipped),
            "launch_to_child_start_ms_avg": _mean([item["launch_to_child_start_ms"] for item in values if item.get("launch_to_child_start_ms") is not None]),
            "output_ms_avg": _mean(outputs),
            "throughput_mb_s_avg": _mean(throughputs),
            "throughput_mb_s_median": statistics.median(throughputs) if throughputs else None,
            "throughput_mb_s_min": min(throughputs) if throughputs else None,
            "throughput_mb_s_max": max(throughputs) if throughputs else None,
            "throughput_mb_s_p95": _percentile(throughputs, 0.95),
            "output_ms_p95": _percentile(outputs, 0.95),
            "peak_rss_mb_avg": _mean(root_rss),
            "root_rss_mb_avg": _mean(root_rss),
            "tree_rss_mb_avg": _mean(tree_rss),
        }
    return summary


def _utc_now():
    return _datetime.datetime.now(_datetime.timezone.utc).isoformat().replace("+00:00", "Z")


def _effective_args(args):
    effective = {}
    for key, value in vars(args).items():
        if key.startswith("_"):
            continue
        if isinstance(value, Path):
            effective[key] = str(value)
        else:
            effective[key] = value
    return effective


def _payload_status(results):
    if not results:
        return "skipped"
    failed = [item for item in results if item.get("status") == "failed"]
    successful = [item for item in results if not item.get("skipped") and item.get("status", "ok") != "failed"]
    skipped = [item for item in results if item.get("skipped")]
    if failed and not successful:
        return "failed"
    if failed or (successful and skipped) or any(item.get("status") == "partial" for item in results):
        return "partial"
    if not successful:
        return "skipped"
    return "ok"


def build_payload(args, results):
    bytes_per_run = len(LINE.encode("utf-8")) * args.lines
    measured_bytes = [item["bytes_written"] for item in results if not item.get("skipped") and item.get("bytes_written") is not None]
    total_bytes = sum(measured_bytes) if measured_bytes else None
    json_out = getattr(args, "json_out", None)
    artifacts = [{"kind": "json", "path": str(json_out)}] if json_out is not None else []
    errors = []
    for item in results:
        if item.get("reason"):
            errors.append({"terminal": item.get("terminal"), "run": item.get("run"), "reason": item["reason"], "status": item.get("status", "skipped")})
        for error in item.get("errors", []):
            errors.append({"terminal": item.get("terminal"), "run": item.get("run"), **error})
    return {
        "schema_version": 1,
        "kind": "terminal-output-benchmark",
        "status": _payload_status(results),
        "metadata": {
            "started_at_utc": getattr(args, "_started_at_utc", _utc_now()),
            "completed_at_utc": _utc_now(),
            "effective_args": _effective_args(args),
            "bytes_written": bytes_per_run,
            "total_bytes_written": total_bytes,
            "command_argv": [item["command"] for item in results if item.get("command")],
            "host": {
                "platform": platform.platform(),
                "system": platform.system(),
                "release": platform.release(),
                "machine": platform.machine(),
                "python_version": platform.python_version(),
                "cpu_count": os.cpu_count(),
            },
        },
        "artifacts": artifacts,
        "errors": errors,
        "line_count": args.lines,
        "results": results,
        "summary": summarize(results),
    }


def write_json(path, payload):
    destination = Path(path)
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")


def main():
    args = parse_args()
    args._started_at_utc = _utc_now()
    terminals = [item.strip() for item in args.include.split(",") if item.strip()]
    with tempfile.TemporaryDirectory(prefix="terminal-output-bench-") as temp_dir:
        workload = Path(temp_dir) / "workload.sh"
        write_workload(workload)
        results = [
            run_once(terminal, args, workload, run_number)
            for terminal in terminals
            for run_number in range(1, args.runs + 1)
        ]
    payload = build_payload(args, results)
    if args.json_out is not None:
        write_json(args.json_out, payload)
    print(json.dumps(payload, indent=2))


if __name__ == "__main__":
    main()
