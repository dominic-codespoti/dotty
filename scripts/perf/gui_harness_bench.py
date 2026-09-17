#!/usr/bin/env python3
import argparse
import glob
import json
import os
import socket
import statistics
import subprocess
import time
from pathlib import Path


def parse_args():
    root = Path(__file__).resolve().parents[2]
    default_app = (
        root / "src" / "Dotty.App" / "bin" / "Release" / "net10.0" / "Dotty.App"
    )

    parser = argparse.ArgumentParser(
        description="Benchmark Dotty GUI via the TCP test harness."
    )
    parser.add_argument(
        "--app", default=str(default_app), help="Path to the Dotty.App executable"
    )
    parser.add_argument(
        "--workdir", default=str(root), help="Working directory for the app process"
    )
    parser.add_argument("--port", type=int, default=9876, help="DOTTY_TEST_PORT value")
    parser.add_argument("--runs", type=int, default=3, help="Number of benchmark runs")
    parser.add_argument(
        "--new-tabs", type=int, default=40, help="Number of NEW_TAB commands per run"
    )
    parser.add_argument(
        "--background-new-tabs",
        action="store_true",
        help="Create tabs in the background so sessions start only when activated",
    )
    parser.add_argument(
        "--switches",
        type=int,
        default=1000,
        help="Number of NEXT_TAB/PREV_TAB commands per run",
    )
    parser.add_argument(
        "--capture-mode",
        choices=["canvas", "window", "none"],
        default="none",
        help="Capture command to use for end-to-end verification; none waits for UI idle",
    )
    parser.add_argument(
        "--startup-timeout",
        type=float,
        default=20.0,
        help="Seconds to wait for harness readiness",
    )
    parser.add_argument(
        "--capture-timeout",
        type=float,
        default=30.0,
        help="Seconds to wait for screenshot creation",
    )
    parser.add_argument(
        "--idle-seconds",
        type=float,
        default=0.0,
        help="Optional idle interval to measure before active workloads",
    )
    parser.add_argument(
        "--render-scenario",
        action="store_true",
        help="Load and measure the deterministic styled render scenario",
    )
    parser.add_argument("--log-dir", default="/tmp", help="Directory for per-run logs")
    return parser.parse_args()


def send_command(port, command, timeout=5.0):
    started = time.perf_counter()
    with socket.create_connection(("127.0.0.1", port), timeout=timeout) as sock:
        sock.settimeout(timeout)
        sock.sendall((command + "\n").encode("utf-8"))
        chunks = []
        while True:
            chunk = sock.recv(4096)
            if not chunk:
                break
            chunks.append(chunk)
            if b"\n" in chunk:
                break
        response = b"".join(chunks).decode("utf-8-sig", "ignore").strip()
    return (time.perf_counter() - started) * 1000.0, response


def fetch_stats(port, timeout=5.0):
    _, response = send_command(port, "STATS", timeout=timeout)
    return json.loads(response)

def fetch_perf(port, timeout=5.0):
    _, response = send_command(port, "PERF:GET", timeout=timeout)
    return json.loads(response)


def wait_ready(port, timeout):
    deadline = time.perf_counter() + timeout
    while time.perf_counter() < deadline:
        try:
            return send_command(port, "PREV_TAB", timeout=0.5)
        except OSError:
            time.sleep(0.2)
    raise TimeoutError(f"Timed out waiting for port {port}")


def wait_for_screenshot(pattern, before, timeout):
    started = time.perf_counter()
    deadline = started + timeout
    while time.perf_counter() < deadline:
        diff = set(glob.glob(pattern)) - before
        if diff:
            newest = max(diff, key=os.path.getmtime)
            return newest, (time.perf_counter() - started) * 1000.0
        time.sleep(0.05)
    return None, timeout * 1000.0


def capture_or_wait_idle(args, port, screenshot_pattern, capture_command):
    started = time.perf_counter()
    _, response = send_command(port, "WAIT_FOR_IDLE", timeout=args.capture_timeout)
    if response != "OK":
        raise RuntimeError(f"Unexpected idle response: {response}")

    if capture_command is None:
        return None, (time.perf_counter() - started) * 1000.0

    before = set(glob.glob(screenshot_pattern))
    _, response = send_command(port, capture_command, timeout=args.capture_timeout)
    if response != "OK":
        raise RuntimeError(f"Unexpected capture response: {response}")
    screenshot, _ = wait_for_screenshot(
        screenshot_pattern, before, args.capture_timeout
    )
    return screenshot, (time.perf_counter() - started) * 1000.0


def rss_mb(pid):
    with open(f"/proc/{pid}/status", "r", encoding="utf-8") as handle:
        for line in handle:
            if line.startswith("VmRSS:"):
                return int(line.split()[1]) / 1024.0
    return None


def p95(values):
    if len(values) == 1:
        return values[0]
    return statistics.quantiles(values, n=20, method="inclusive")[18]


def benchmark_run(args, run_number):
    if args.capture_mode == "none":
        capture_command = None
        screenshot_pattern = None
    else:
        capture_command = "CAPTURE_CANVAS" if args.capture_mode == "canvas" else "CAPTURE"
        screenshot_pattern = (
            "/tmp/dotty_canvas_*.png"
            if args.capture_mode == "canvas"
            else "/tmp/dotty_gui_*.png"
        )
    env = os.environ.copy()
    env["DOTTY_TEST_PORT"] = str(args.port + run_number - 1)
    env["DOTTY_SKIP_CONFIG_COMPILE"] = "1"
    log_path = Path(args.log_dir) / f"dotty_gui_harness_run_{run_number}.log"

    with open(log_path, "wb") as log_file:
        proc = subprocess.Popen(
            [args.app],
            cwd=args.workdir,
            env=env,
            stdout=log_file,
            stderr=subprocess.STDOUT,
        )
        try:
            startup_started = time.perf_counter()
            first_rtt_ms, response = wait_ready(
                args.port + run_number - 1, args.startup_timeout
            )
            startup_ms = (time.perf_counter() - startup_started) * 1000.0
            if response != "OK":
                raise RuntimeError(f"Unexpected ready response: {response}")

            port = args.port + run_number - 1
            before_rss = rss_mb(proc.pid)
            initial_stats = fetch_stats(port)

            _, response = send_command(port, "PERF:START")
            if response != "OK":
                raise RuntimeError(f"Unexpected PERF:START response: {response}")

            idle_phase = None
            if args.idle_seconds > 0:
                time.sleep(args.idle_seconds)
                _, idle_settle_ms = capture_or_wait_idle(
                    args, port, screenshot_pattern, None
                )
                idle_phase = {
                    "duration_seconds": args.idle_seconds,
                    "settle_ms": idle_settle_ms,
                    "rss_mb": rss_mb(proc.pid),
                    "telemetry": fetch_perf(port),
                }
                _, response = send_command(port, "PERF:RESET")
                if response != "OK":
                    raise RuntimeError(f"Unexpected PERF:RESET response: {response}")

            render_scenario = None
            if args.render_scenario:
                _, response = send_command(port, "RENDER_SCENARIO")
                if response != "OK":
                    raise RuntimeError(
                        f"Unexpected RENDER_SCENARIO response: {response}"
                    )
                scenario_shot, scenario_capture_ms = capture_or_wait_idle(
                    args, port, screenshot_pattern, capture_command
                )
                render_scenario = {
                    "capture_ms": scenario_capture_ms,
                    "capture_ok": args.capture_mode == "none"
                    or scenario_shot is not None,
                    "screenshot": scenario_shot,
                    "rss_mb": rss_mb(proc.pid),
                    "stats": fetch_stats(port),
                    "telemetry": fetch_perf(port),
                }
                _, response = send_command(port, "PERF:RESET")
                if response != "OK":
                    raise RuntimeError(f"Unexpected PERF:RESET response: {response}")

            new_tab_rtts = []
            new_tab_command = "NEW_TAB_BG" if args.background_new_tabs else "NEW_TAB"
            enqueue_started = time.perf_counter()
            for _ in range(args.new_tabs):
                rtt_ms, response = send_command(port, new_tab_command)
                if response != "OK":
                    raise RuntimeError(
                        f"Unexpected {new_tab_command} response: {response}"
                    )
                new_tab_rtts.append(rtt_ms)
            new_tab_enqueue_ms = (time.perf_counter() - enqueue_started) * 1000.0

            new_tab_shot, new_tab_capture_ms = capture_or_wait_idle(
                args, port, screenshot_pattern, capture_command
            )
            after_new_tabs_stats = fetch_stats(port)
            after_new_tabs_rss = rss_mb(proc.pid)
            new_tab_telemetry = fetch_perf(port)

            activation_sweep = None
            if args.background_new_tabs and args.new_tabs > 0:
                _, response = send_command(port, "PERF:RESET")
                if response != "OK":
                    raise RuntimeError(f"Unexpected PERF:RESET response: {response}")

                sweep_rtts = []
                enqueue_started = time.perf_counter()
                for _ in range(args.new_tabs):
                    rtt_ms, response = send_command(port, "NEXT_TAB")
                    if response != "OK":
                        raise RuntimeError(
                            f"Unexpected activation sweep response: {response}"
                        )
                    sweep_rtts.append(rtt_ms)
                sweep_enqueue_ms = (time.perf_counter() - enqueue_started) * 1000.0

                sweep_shot, sweep_capture_ms = capture_or_wait_idle(
                    args, port, screenshot_pattern, capture_command
                )

                activation_sweep = {
                    "count": args.new_tabs,
                    "enqueue_ms": sweep_enqueue_ms,
                    "throughput_cmds_per_s": args.new_tabs
                    / (sweep_enqueue_ms / 1000.0),
                    "avg_rtt_ms": statistics.mean(sweep_rtts),
                    "p95_rtt_ms": p95(sweep_rtts),
                    "capture_ms": sweep_capture_ms,
                    "capture_ok": args.capture_mode == "none"
                    or sweep_shot is not None,
                    "screenshot": sweep_shot,
                    "rss_mb": rss_mb(proc.pid),
                    "stats": fetch_stats(port),
                    "telemetry": fetch_perf(port),
                }

            _, response = send_command(port, "PERF:RESET")
            if response != "OK":
                raise RuntimeError(f"Unexpected PERF:RESET response: {response}")

            switch_rtts = []
            enqueue_started = time.perf_counter()
            for index in range(args.switches):
                command = "NEXT_TAB" if index % 2 == 0 else "PREV_TAB"
                rtt_ms, response = send_command(port, command)
                if response != "OK":
                    raise RuntimeError(f"Unexpected switch response: {response}")
                switch_rtts.append(rtt_ms)
            switch_enqueue_ms = (time.perf_counter() - enqueue_started) * 1000.0

            switch_shot, switch_capture_ms = capture_or_wait_idle(
                args, port, screenshot_pattern, capture_command
            )
            after_switches_stats = fetch_stats(port)
            switch_telemetry = fetch_perf(port)
            after_rss = rss_mb(proc.pid)
            send_command(port, "PERF:STOP")

            return {
                "run": run_number,
                "startup_ms": startup_ms,
                "first_command_rtt_ms": first_rtt_ms,
                "rss_before_mb": before_rss,
                "rss_after_mb": after_rss,
                "rss_after_new_tabs_mb": after_new_tabs_rss,
                "rss_delta_mb": None
                if before_rss is None or after_rss is None
                else after_rss - before_rss,
                "rss_delta_after_new_tabs_mb": None
                if before_rss is None or after_new_tabs_rss is None
                else after_new_tabs_rss - before_rss,
                "stats": {
                    "initial": initial_stats,
                    "after_new_tabs": after_new_tabs_stats,
                    "after_switches": after_switches_stats,
                },
                "idle": idle_phase,
                "render_scenario": render_scenario,
                "new_tabs": {
                    "count": args.new_tabs,
                    "enqueue_ms": new_tab_enqueue_ms,
                    "throughput_cmds_per_s": 0
                    if args.new_tabs == 0
                    else args.new_tabs / (new_tab_enqueue_ms / 1000.0),
                    "avg_rtt_ms": 0
                    if not new_tab_rtts
                    else statistics.mean(new_tab_rtts),
                    "p95_rtt_ms": 0 if not new_tab_rtts else p95(new_tab_rtts),
                    "capture_ms": new_tab_capture_ms,
                    "capture_ok": args.capture_mode == "none"
                    or new_tab_shot is not None,
                    "screenshot": new_tab_shot,
                    "telemetry": new_tab_telemetry,
                },
                "activation_sweep": activation_sweep,
                "switches": {
                    "count": args.switches,
                    "enqueue_ms": switch_enqueue_ms,
                    "throughput_cmds_per_s": 0
                    if args.switches == 0
                    else args.switches / (switch_enqueue_ms / 1000.0),
                    "avg_rtt_ms": 0
                    if not switch_rtts
                    else statistics.mean(switch_rtts),
                    "p95_rtt_ms": 0 if not switch_rtts else p95(switch_rtts),
                    "capture_ms": switch_capture_ms,
                    "capture_ok": args.capture_mode == "none"
                    or switch_shot is not None,
                    "screenshot": switch_shot,
                    "telemetry": switch_telemetry,
                },
                "log_path": str(log_path),
            }
        finally:
            proc.terminate()
            try:
                proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait(timeout=5)


def main():
    args = parse_args()
    runs = [benchmark_run(args, run_number) for run_number in range(1, args.runs + 1)]
    summary = {
        "app": args.app,
        "capture_mode": args.capture_mode,
        "background_new_tabs": args.background_new_tabs,
        "runs": runs,
        "averages": {
            "startup_ms": statistics.mean(run["startup_ms"] for run in runs),
            "first_command_rtt_ms": statistics.mean(
                run["first_command_rtt_ms"] for run in runs
            ),
            "rss_delta_mb": statistics.mean(
                run["rss_delta_mb"] for run in runs if run["rss_delta_mb"] is not None
            ),
            "rss_delta_after_new_tabs_mb": statistics.mean(
                run["rss_delta_after_new_tabs_mb"]
                for run in runs
                if run["rss_delta_after_new_tabs_mb"] is not None
            ),
            "new_tab_throughput_cmds_per_s": statistics.mean(
                run["new_tabs"]["throughput_cmds_per_s"] for run in runs
            ),
            "new_tab_capture_ms": statistics.mean(
                run["new_tabs"]["capture_ms"] for run in runs
            ),
            "activation_sweep_capture_ms": statistics.mean(
                run["activation_sweep"]["capture_ms"]
                for run in runs
                if run["activation_sweep"] is not None
            )
            if any(run["activation_sweep"] is not None for run in runs)
            else None,
            "switch_throughput_cmds_per_s": statistics.mean(
                run["switches"]["throughput_cmds_per_s"] for run in runs
            ),
            "switch_capture_ms": statistics.mean(
                run["switches"]["capture_ms"] for run in runs
            ),
        },
    }
    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main()
