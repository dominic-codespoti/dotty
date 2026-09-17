#!/usr/bin/env python3
import argparse
import json
import os
import shutil
import signal
import statistics
import subprocess
import tempfile
import time
from pathlib import Path


LINE = "The quick brown fox jumps over the lazy dog 0123456789\n"


def parse_args():
    root = Path(__file__).resolve().parents[2]
    parser = argparse.ArgumentParser(
        description="Launch terminal emulators with the same high-output child workload."
    )
    parser.add_argument("--runs", type=int, default=3)
    parser.add_argument("--lines", type=int, default=500_000)
    parser.add_argument("--sample-interval-ms", type=float, default=50.0)
    parser.add_argument("--startup-timeout", type=float, default=20.0)

    # Prefer ReadyToRun publish binary if available (much faster startup).
    r2r = root / "src" / "Dotty" / "bin" / "Release" / "net10.0" / "linux-x64" / "publish" / "Dotty"
    jit = root / "src" / "Dotty" / "bin" / "Release" / "net10.0" / "Dotty"
    parser.add_argument("--app", default=str(r2r if r2r.exists() else jit))
    parser.add_argument("--include", default="dotty,kitty,ghostty,wezterm")
    return parser.parse_args()


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


def rss_mb(pid):
    try:
        with open(f"/proc/{pid}/status", "r", encoding="utf-8") as handle:
            for line in handle:
                if line.startswith("VmRSS:"):
                    return int(line.split()[1]) / 1024.0
    except OSError:
        return None
    return None


def read_events(log_path):
    events = {}
    try:
        for line in log_path.read_text(encoding="utf-8").splitlines():
            parts = line.split()
            if len(parts) == 2:
                events[parts[1]] = int(parts[0])
    except OSError:
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
    if proc.poll() is not None:
        return
    try:
        proc.terminate()
        proc.wait(timeout=2)
    except subprocess.TimeoutExpired:
        try:
            proc.kill()
            proc.wait(timeout=2)
        except OSError:
            pass


def run_once(name, args, workload, run_number):
    log_path = Path(tempfile.gettempdir()) / f"terminal-output-bench-{name}-{os.getpid()}-{run_number}.log"
    try:
        log_path.unlink()
    except OSError:
        pass

    cmd = terminal_command(name, args, workload)
    if cmd is None:
        return {"terminal": name, "run": run_number, "skipped": True, "reason": "binary not found"}

    env = os.environ.copy()
    env["TERMINAL_BENCH_LINES"] = str(args.lines)
    env["TERMINAL_BENCH_LOG"] = str(log_path)
    if name == "dotty":
        env["DOTTY_SHELL"] = str(workload)
    env["DOTTY_SKIP_CONFIG_COMPILE"] = "1"

    started_ns = time.time_ns()
    proc = subprocess.Popen(cmd, env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    samples = []
    started_event_seen = False
    deadline = time.monotonic() + args.startup_timeout
    sample_interval = args.sample_interval_ms / 1000.0

    try:
        while time.monotonic() < deadline:
            current_rss = rss_mb(proc.pid)
            if current_rss is not None:
                samples.append(current_rss)

            events = read_events(log_path)
            started_event_seen = started_event_seen or "start" in events
            if "end" in events:
                output_ms = (events["end"] - events["start"]) / 1_000_000.0
                launch_to_start_ms = (events["start"] - started_ns) / 1_000_000.0
                stop_process(proc)
                bytes_written = len(LINE.encode("utf-8")) * args.lines
                return {
                    "terminal": name,
                    "run": run_number,
                    "skipped": False,
                    "pid": proc.pid,
                    "launch_to_child_start_ms": launch_to_start_ms,
                    "output_ms": output_ms,
                    "throughput_mb_s": (bytes_written / (1024 * 1024)) / (output_ms / 1000.0),
                    "initial_rss_mb": samples[0] if samples else None,
                    "peak_rss_mb": max(samples) if samples else None,
                    "final_sample_rss_mb": samples[-1] if samples else None,
                    "sample_count": len(samples),
                    "command": cmd,
                }

            if proc.poll() is not None and not started_event_seen:
                return {
                    "terminal": name,
                    "run": run_number,
                    "skipped": True,
                    "reason": f"process exited before workload started ({proc.returncode})",
                    "command": cmd,
                }

            time.sleep(sample_interval)

        return {
            "terminal": name,
            "run": run_number,
            "skipped": True,
            "reason": "timed out waiting for workload completion",
            "started_event_seen": started_event_seen,
            "command": cmd,
        }
    finally:
        stop_process(proc)


def summarize(results):
    summary = {}
    for terminal in sorted({result["terminal"] for result in results}):
        values = [result for result in results if result["terminal"] == terminal and not result.get("skipped")]
        skipped = [result for result in results if result["terminal"] == terminal and result.get("skipped")]
        if not values:
            summary[terminal] = {"skipped": True, "reasons": [item.get("reason") for item in skipped]}
            continue
        summary[terminal] = {
            "runs": len(values),
            "launch_to_child_start_ms_avg": statistics.mean(item["launch_to_child_start_ms"] for item in values),
            "output_ms_avg": statistics.mean(item["output_ms"] for item in values),
            "throughput_mb_s_avg": statistics.mean(item["throughput_mb_s"] for item in values),
            "peak_rss_mb_avg": statistics.mean(item["peak_rss_mb"] for item in values if item["peak_rss_mb"] is not None),
        }
    return summary


def main():
    args = parse_args()
    terminals = [item.strip() for item in args.include.split(",") if item.strip()]
    with tempfile.TemporaryDirectory(prefix="terminal-output-bench-") as temp_dir:
        workload = Path(temp_dir) / "workload.sh"
        write_workload(workload)
        results = [
            run_once(terminal, args, workload, run_number)
            for terminal in terminals
            for run_number in range(1, args.runs + 1)
        ]
    print(json.dumps({"line_count": args.lines, "results": results, "summary": summarize(results)}, indent=2))


if __name__ == "__main__":
    main()
