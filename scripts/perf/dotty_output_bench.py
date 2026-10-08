#!/usr/bin/env python3
"""Native Windows/POSIX Dotty output benchmark with acknowledged byte counts.

Requires psutil and a desktop session. Endpoints observe parser drain and a
matching generation after SwapBuffers; they do not measure physical presentation.
"""
import argparse
import ctypes
import datetime as dt
import hashlib
import json
import math
import os
from pathlib import Path
import platform
import secrets
import socket
import statistics
import subprocess
import sys
import threading
import time

from output_write import write_all

LINE = b"The quick brown fox jumps over the lazy dog 0123456789\n"
SAMPLE_INTERVAL = 0.05
POLL_INTERVAL = 0.01
ENDPOINT_SEMANTICS = (
    "First control observation after producer end: marker in DUMP, parser queue "
    "empty, PTY bytes read equal parsed, and presentedGeneration equal to "
    "modelGeneration with a new presentCount. Generation is submitted after "
    "SwapBuffers, not a GPU fence or compositor/physical presentation."
)


def event(path, kind, **values):
    record = {"event": kind, "timestamp_ns": time.perf_counter_ns(), **values}
    with path.open("a", encoding="utf-8") as handle:
        handle.write(json.dumps(record) + "\n")


def events(path):
    result = {}
    try:
        text = path.read_text(encoding="utf-8")
    except FileNotFoundError:
        return result
    for line in text.splitlines():
        try:
            item = json.loads(line)
        except json.JSONDecodeError:
            continue  # The producer can be in the middle of appending one event.
        result[item["event"]] = item
    return result


def write_payload(out, lines, marker):
    """Emit complete chunks, a partial final chunk, then the final marker."""
    payload_written = calls = short_writes = 0
    chunk = LINE * 1000
    full, remainder = divmod(lines, 1000)
    for _ in range(full):
        written, count, short = write_all(out, chunk)
        payload_written += written
        calls += count
        short_writes += short
    if remainder:
        written, count, short = write_all(out, LINE * remainder)
        payload_written += written
        calls += count
        short_writes += short
    marker_written, count, short = write_all(out, marker)
    out.flush()
    if payload_written != lines * len(LINE) or marker_written != len(marker):
        raise OSError("incomplete acknowledged workload")
    return {"payload_bytes_written": payload_written, "marker_bytes_written": marker_written,
            "write_calls": calls + count, "short_writes": short_writes + short,
            "stdout_buffer_type": type(out).__name__}


def child():
    log = Path(os.environ["DOTTY_OUTPUT_EVENTS"])
    go = Path(os.environ["DOTTY_OUTPUT_GO"])
    stop = Path(os.environ["DOTTY_OUTPUT_STOP"])
    cols, rows = int(os.environ["DOTTY_OUTPUT_COLS"]), int(os.environ["DOTTY_OUTPUT_ROWS"])
    timeout = float(os.environ["DOTTY_OUTPUT_STARTUP_TIMEOUT"])
    if os.name == "nt":
        import msvcrt
        msvcrt.setmode(sys.stdout.fileno(), os.O_BINARY)
    deadline = time.monotonic() + timeout
    while not go.exists():
        size = os.get_terminal_size(sys.stdout.fileno())
        event(log, "ready", cols=size.columns, rows=size.lines)
        if time.monotonic() >= deadline:
            raise TimeoutError("child geometry gate timed out")
        time.sleep(SAMPLE_INTERVAL)
    size = os.get_terminal_size(sys.stdout.fileno())
    if (size.columns, size.lines) != (cols, rows):
        raise RuntimeError(f"child TTY grid mismatch: {size}, expected {cols}x{rows}")
    lines = int(os.environ["DOTTY_OUTPUT_LINES"])
    marker = b"\r\n" + os.environ["DOTTY_OUTPUT_MARKER"].encode("ascii") + b"\r\n"
    event(log, "start", cols=size.columns, rows=size.lines, lines=lines,
          payload_bytes=lines * len(LINE), marker_bytes=len(marker), python=sys.version)
    counts = write_payload(sys.stdout.buffer, lines, marker)
    event(log, "end", **counts)
    deadline = time.monotonic() + float(os.environ["DOTTY_OUTPUT_TIMEOUT"]) + 15
    while not stop.exists() and time.monotonic() < deadline:
        time.sleep(SAMPLE_INTERVAL)


def marker_visible(dump, marker):
    # DUMP inserts row separators even for terminal soft wraps. Only inspect
    # visible rows, not the geometry header or protocol footer.
    return marker in "".join(row.rstrip() for row in dump.splitlines()[1:-1])


def command(port, value):
    with socket.create_connection(("127.0.0.1", port), timeout=1) as connection:
        connection.settimeout(1)
        connection.sendall((value + "\n").encode("utf-8"))
        response = bytearray()
        while True:
            chunk = connection.recv(65536)
            if not chunk:
                break
            response.extend(chunk)
    text = response.decode("utf-8").rstrip("\r\n")
    if text.startswith("ERROR"):
        raise RuntimeError(text)
    return text


def native_window(pid):
    """Record native Windows geometry; portable host geometry is in GET_STATE."""
    if os.name != "nt":
        return None
    from ctypes import wintypes
    api = ctypes.WinDLL("user32", use_last_error=True)
    api.IsWindowVisible.argtypes = [wintypes.HWND]
    api.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
    api.GetClientRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
    api.GetWindowRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
    api.GetDpiForWindow.argtypes = [wintypes.HWND]
    api.GetDpiForWindow.restype = wintypes.UINT
    api.ShowWindow.argtypes = [wintypes.HWND, ctypes.c_int]
    api.SetForegroundWindow.argtypes = [wintypes.HWND]
    callback_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    api.EnumWindows.argtypes = [callback_type, wintypes.LPARAM]
    found = []

    @callback_type
    def visit(hwnd, _):
        owner = wintypes.DWORD()
        api.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and api.IsWindowVisible(hwnd):
            client, rect = wintypes.RECT(), wintypes.RECT()
            api.GetClientRect(hwnd, ctypes.byref(client))
            api.GetWindowRect(hwnd, ctypes.byref(rect))
            found.append({"id": int(hwnd), "client": [client.right, client.bottom],
                          "rect": [rect.left, rect.top, rect.right, rect.bottom],
                          "dpi": api.GetDpiForWindow(hwnd)})
        return True

    api.EnumWindows(visit, 0)
    if found:
        api.ShowWindow(found[0]["id"], 5)
        found[0]["foreground_requested"] = bool(api.SetForegroundWindow(found[0]["id"]))
    return found[0] if found else None


def sample_memory(root, stop, samples, psutil):
    while not stop.is_set():
        try:
            root_size = root.memory_info().rss
            tree_size = root_size
            pids = [root.pid]
            complete = True
            for process in root.children(recursive=True):
                try:
                    tree_size += process.memory_info().rss
                    pids.append(process.pid)
                except (psutil.NoSuchProcess, psutil.AccessDenied):
                    complete = False
            samples.append({"timestamp_ns": time.perf_counter_ns(), "root_bytes": root_size,
                            "tree_bytes": tree_size if complete else None, "pids": pids,
                            "tree_complete": complete})
        except (psutil.NoSuchProcess, psutil.AccessDenied):
            pass
        stop.wait(SAMPLE_INTERVAL)


def digest(path):
    hasher = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            hasher.update(chunk)
    return hasher.hexdigest()


def cleanup(process, root, port, psutil):
    descendants = []
    if root is not None:
        try:
            descendants = root.children(recursive=True)
        except (psutil.NoSuchProcess, psutil.AccessDenied):
            pass
    if process is not None:
        try:
            command(port, "SHUTDOWN")
        except (OSError, RuntimeError):
            pass
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.terminate()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
    for descendant in descendants:
        try:
            descendant.terminate()
        except (psutil.NoSuchProcess, psutil.AccessDenied):
            pass
    _, alive = psutil.wait_procs(descendants, timeout=2)
    for descendant in alive:
        try:
            descendant.kill()
        except (psutil.NoSuchProcess, psutil.AccessDenied):
            pass
    psutil.wait_procs(alive, timeout=2)


def run_once(args, output, run_number, warmup, provenance, psutil):
    output.mkdir()
    config_dir = output / "config"
    config_dir.mkdir()
    config = {"font": {"family": args.font_family, "size": args.font_size, "lineHeight": 1},
              "window": {"opacity": 1, "decorations": "native",
                         "padding": {"left": 0, "right": 0, "top": 0, "bottom": 0}},
              "tabBar": {"show": False}, "cursor": {"blink": False}}
    (config_dir / "config.json").write_text(json.dumps(config), encoding="utf-8")
    with socket.socket() as reservation:
        reservation.bind(("127.0.0.1", 0))
        port = reservation.getsockname()[1]
    log_path, go_path, stop_path = output / "child.jsonl", output / "go", output / "stop"
    marker = "DOTTY_BENCH_END_" + secrets.token_hex(6)
    env = os.environ.copy()
    # Do not inherit a developer's shell, diagnostics file, config, or test gate.
    for key in list(env):
        if key.startswith("DOTTY_"):
            del env[key]
    env.update(DOTTY_TEST_PORT=str(port), DOTTY_CONFIG_HOME=str(config_dir),
               DOTTY_SKIP_CONFIG_COMPILE="1", DOTTY_OUTPUT_EVENTS=str(log_path),
               DOTTY_OUTPUT_GO=str(go_path), DOTTY_OUTPUT_STOP=str(stop_path),
               DOTTY_OUTPUT_LINES=str(args.lines), DOTTY_OUTPUT_MARKER=marker,
               DOTTY_OUTPUT_COLS=str(args.cols), DOTTY_OUTPUT_ROWS=str(args.rows),
               DOTTY_OUTPUT_TIMEOUT=str(args.timeout),
               DOTTY_OUTPUT_STARTUP_TIMEOUT=str(args.startup_timeout))
    argv = [str(args.app), "--", sys.executable, "-u", str(Path(__file__).resolve()), "--child"]
    record = {"run": run_number, "warmup": warmup, "status": "failed", "artifacts": str(output),
              "lines": args.lines, "payload_bytes": args.lines * len(LINE),
              "config": config, "command_argv": argv, "provenance": provenance,
              "memory_kind": "working_set" if os.name == "nt" else "rss",
              "memory_semantics": "sampled maximum of root and process tree from launch through endpoint; excludes observer and cleanup; not an exact peak",
              "sample_interval_ms": SAMPLE_INTERVAL * 1000,
              "endpoint_poll_interval_ms": POLL_INTERVAL * 1000,
              "endpoint_semantics": ENDPOINT_SEMANTICS}
    samples, observations = [], []
    memory_stop = threading.Event()
    process = sampler = root = None
    try:
        with (output / "app.log").open("w", encoding="utf-8") as app_log:
            launched = time.perf_counter_ns()
            process = subprocess.Popen(argv, cwd=str(args.app.parent), env=env,
                                       stdout=app_log, stderr=subprocess.STDOUT,
                                       creationflags=subprocess.CREATE_NEW_CONSOLE if os.name == "nt" else 0)
            root = psutil.Process(process.pid)
            sampler = threading.Thread(target=sample_memory, args=(root, memory_stop, samples, psutil), daemon=True)
            sampler.start()
            deadline = time.monotonic() + args.startup_timeout
            resized = False
            while time.monotonic() < deadline:
                if process.poll() is not None:
                    raise RuntimeError(f"Dotty exited before ready: {process.returncode}")
                try:
                    state = json.loads(command(port, "GET_STATE"))
                    stats = json.loads(command(port, "STATS"))
                except (OSError, json.JSONDecodeError):
                    time.sleep(SAMPLE_INTERVAL)
                    continue
                if not resized:
                    if command(port, f"RESIZE:{args.cols}:{args.rows}") != "OK":
                        raise RuntimeError("resize failed")
                    resized = True
                    time.sleep(0.1)
                    continue
                ready = events(log_path).get("ready", {})
                geometry = (state["cols"], state["rows"], ready.get("cols"), ready.get("rows"))
                if geometry == (args.cols, args.rows, args.cols, args.rows) and stats["presentCount"] > 0:
                    native = native_window(process.pid)
                    if os.name != "nt" or native:
                        break
                time.sleep(SAMPLE_INTERVAL)
            else:
                raise TimeoutError("child/host geometry and initial frame not ready")
            time.sleep(0.2)
            baseline = json.loads(command(port, "STATS"))
            record.update(initial_state=state, initial_stats=baseline, native_window=native)
            go_path.touch()
            deadline = time.monotonic() + args.timeout
            while time.monotonic() < deadline:
                child_events = events(log_path)
                if "end" in child_events:
                    break
                if process.poll() is not None:
                    raise RuntimeError(f"Dotty exited during workload: {process.returncode}")
                time.sleep(POLL_INTERVAL)
            else:
                raise TimeoutError("workload output timed out")
            completed = child_events["end"]
            if (completed["payload_bytes_written"] != record["payload_bytes"]
                    or completed["marker_bytes_written"] != child_events["start"]["marker_bytes"]):
                raise RuntimeError("child byte counts do not match requested payload and marker")
            marker_found, parsed_at, frame_at = False, None, None
            final_dump = ""
            while time.monotonic() < deadline:
                if not marker_found:
                    final_dump = command(port, "DUMP")
                    marker_found = marker_visible(final_dump, marker)
                stats = json.loads(command(port, "STATS"))
                observed = time.perf_counter_ns()
                observations.append({"timestamp_ns": observed, "marker_found": marker_found, **stats})
                drained = marker_found and stats["pendingOutputChunks"] == 0 and stats["ptyBytesRead"] == stats["ptyBytesParsed"]
                if drained and parsed_at is None:
                    parsed_at = observed
                if (drained and stats["modelGeneration"] is not None
                        and stats["presentedGeneration"] == stats["modelGeneration"]
                        and stats["presentCount"] > baseline["presentCount"]):
                    frame_at = observed
                    break
                time.sleep(POLL_INTERVAL)
            if frame_at is None:
                raise TimeoutError("marker, parser drain, and matching submitted generation not observed")
            memory_stop.set()
            sampler.join(timeout=2)
            start, end = child_events["start"]["timestamp_ns"], completed["timestamp_ns"]
            output_ms, frame_ms = (end - start) / 1e6, (frame_at - start) / 1e6
            tree_sizes = [sample["tree_bytes"] for sample in samples if sample["tree_bytes"] is not None]
            record.update(status="ok" if samples and tree_sizes else "partial", child_events=child_events,
                          final_stats=stats, final_state=json.loads(command(port, "GET_STATE")),
                          launch_to_child_start_ms=(start - launched) / 1e6, output_ms=output_ms,
                          parser_observed_ms=(parsed_at - start) / 1e6, frame_observed_ms=frame_ms,
                          parser_tail_observed_ms=max(0, (parsed_at - end) / 1e6),
                          frame_tail_observed_ms=max(0, (frame_at - end) / 1e6),
                          producer_mib_s=completed["payload_bytes_written"] / 1024**2 / (output_ms / 1000),
                          frame_observed_mib_s=completed["payload_bytes_written"] / 1024**2 / (frame_ms / 1000),
                          peak_root_memory_mib=max((s["root_bytes"] for s in samples), default=0) / 1024**2 if samples else None,
                          peak_tree_memory_mib=max(tree_sizes) / 1024**2 if tree_sizes else None,
                          memory_sample_count=len(samples), complete_tree_sample_count=len(tree_sizes), final_marker=marker)
            if record["status"] == "partial":
                record["error"] = "required sampled memory evidence unavailable"
            (output / "final-screen.txt").write_text(final_dump, encoding="utf-8")
    except Exception as error:
        record.update(status="failed", error=f"{type(error).__name__}: {error}", child_events=events(log_path))
    finally:
        memory_stop.set()
        if sampler is not None:
            sampler.join(timeout=2)
        stop_path.touch()
        try:
            cleanup(process, root, port, psutil)
        except Exception as error:
            record.update(status="failed", cleanup_error=f"{type(error).__name__}: {error}")
        (output / "memory.json").write_text(json.dumps(samples, indent=2), encoding="utf-8")
        (output / "observations.json").write_text(json.dumps(observations, indent=2), encoding="utf-8")
        (output / "result.json").write_text(json.dumps(record, indent=2), encoding="utf-8")
    return record


def positive_int(value):
    parsed = int(value)
    if parsed <= 0:
        raise argparse.ArgumentTypeError("must be positive")
    return parsed


def positive_float(value):
    parsed = float(value)
    if not math.isfinite(parsed) or parsed <= 0:
        raise argparse.ArgumentTypeError("must be positive and finite")
    return parsed


def parse_args(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--app", type=Path, required=True, help="Dotty apphost, including a NativeAOT publish")
    parser.add_argument("--output-root", type=Path, default=Path(__file__).resolve().parents[2] / "artifacts" / "perf" / "dotty-output")
    parser.add_argument("--runs", type=positive_int, default=3)
    parser.add_argument("--warmup-runs", type=int, default=1)
    parser.add_argument("--lines", type=positive_int, default=2_000_000)
    parser.add_argument("--cols", type=positive_int, default=80)
    parser.add_argument("--rows", type=positive_int, default=24)
    parser.add_argument("--font-family", default="Consolas")
    parser.add_argument("--font-size", type=positive_float, default=16)
    parser.add_argument("--startup-timeout", type=positive_float, default=60)
    parser.add_argument("--timeout", type=positive_float, default=180, help="seconds for producer plus endpoint tail per run")
    args = parser.parse_args(argv)
    if args.warmup_runs < 0:
        parser.error("--warmup-runs cannot be negative")
    args.app = args.app.resolve()
    if not args.app.is_file():
        parser.error(f"Dotty apphost not found: {args.app}")
    return args


def create_run_dir(output_root):
    output_root.mkdir(parents=True, exist_ok=True)
    while True:
        stamp = dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
        path = output_root / f"{stamp}-{secrets.token_hex(3)}"
        try:
            path.mkdir()
            return path
        except FileExistsError:
            continue


def summarize(results):
    measured = [item for item in results if not item["warmup"] and item["status"] in ("ok", "partial")]
    metrics = {}
    for key in ("output_ms", "parser_observed_ms", "frame_observed_ms", "producer_mib_s", "frame_observed_mib_s", "peak_root_memory_mib", "peak_tree_memory_mib"):
        values = [item[key] for item in measured if item.get(key) is not None]
        metrics[key] = {"count": len(values), "mean": statistics.mean(values) if values else None,
                        "median": statistics.median(values) if values else None,
                        "min": min(values) if values else None, "max": max(values) if values else None}
    return metrics


def write_report(path, payload):
    rows = ["# Dotty native output benchmark", "", f"Status: **{payload['status']}**. Warmups are retained but excluded from summaries.", "",
            "| Run | Warmup | Status | Acknowledged payload bytes | Producer ms | Parser observed ms | Submitted generation observed ms | Tree memory MiB |",
            "|---|---|---|---:|---:|---:|---:|---:|"]
    for item in payload["results"]:
        def number(key):
            value = item.get(key)
            return f"{value:.2f}" if value is not None else "—"
        written = item.get("child_events", {}).get("end", {}).get("payload_bytes_written", "—")
        rows.append(f"| {item['run']} | {item['warmup']} | {item['status']} | {written} | {number('output_ms')} | {number('parser_observed_ms')} | {number('frame_observed_ms')} | {number('peak_tree_memory_mib')} |")
    rows.extend(["", "## Measured-run medians", ""])
    for key, metric in payload["summary"].items():
        value = f"{metric['median']:.2f}" if metric["median"] is not None else "unavailable"
        rows.append(f"- `{key}`: {value} (n={metric['count']}).")
    rows.extend(["", ENDPOINT_SEMANTICS, "",
                 "Windows memory is working set; POSIX memory is RSS. Samples cover startup through the endpoint, exclude cleanup/observer, and are not exact peaks. Source byte counts exclude terminal/PTY-added bytes; parsed byte totals need not match source totals.", "",
                 "Keep grid, font availability, display scale/backend, binary hashes, and workload fixed. Keep the window visible and unoccluded. Small-run statistics are descriptive, not stable population percentiles.", "",
                 "See [summary.json](summary.json) for configuration, provenance, and errors; each `runs/` directory retains child events, memory samples, endpoint observations, logs, final screen, and result JSON."])
    for item in payload["results"]:
        for key in ("error", "cleanup_error"):
            if key in item:
                rows.append(f"\n- Run {item['run']}: {item[key]}")
    path.write_text("\n".join(rows) + "\n", encoding="utf-8")


def main(argv=None):
    if argv == ["--child"]:
        child()
        return 0
    args = parse_args(argv)
    try:
        import psutil
    except ImportError:
        print("Missing dependency psutil; install with: python -m pip install psutil", file=sys.stderr)
        return 1
    output = create_run_dir(args.output_root.resolve())
    (output / "runs").mkdir()
    script = Path(__file__).resolve()
    provenance = {"app": str(args.app), "app_sha256": digest(args.app),
                  "script_sha256": {path.name: digest(path) for path in (script, script.with_name("output_write.py"))},
                  "line_sha256": hashlib.sha256(LINE).hexdigest(), "python": sys.version,
                  "python_executable": sys.executable, "psutil": psutil.__version__, "host": platform.platform(),
                  "clock": "time.perf_counter_ns; do not compare absolute timestamps between operating systems"}
    results = []
    for index in range(args.warmup_runs + args.runs):
        warmup = index < args.warmup_runs
        name = f"{index + 1:03d}-{'warmup' if warmup else 'measured'}"
        result = run_once(args, output / "runs" / name, index + 1, warmup, provenance, psutil)
        results.append(result)
        print(f"{name}: {result['status']}", flush=True)
    status = "failed" if any(item["status"] == "failed" for item in results) else "partial" if any(item["status"] == "partial" for item in results) else "ok"
    payload = {"schema_version": 1, "status": status, "configuration": {key: str(value) if isinstance(value, Path) else value for key, value in vars(args).items()},
               "provenance": provenance, "endpoint_semantics": ENDPOINT_SEMANTICS,
               "results": results, "summary": summarize(results)}
    (output / "summary.json").write_text(json.dumps(payload, indent=2), encoding="utf-8")
    write_report(output / "report.md", payload)
    print(f"{status}: {output / 'report.md'}")
    return 1 if status == "failed" else 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
