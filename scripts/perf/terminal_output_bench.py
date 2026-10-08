#!/usr/bin/env python3
import argparse
import datetime as _datetime
import json
import math
import os
import hashlib
import secrets
import platform
import shutil
import signal
import socket
import statistics
import subprocess
import tempfile
import time
from pathlib import Path


TERMINAL_COLS = 80
TERMINAL_ROWS = 24
BENCH_FONT = "DejaVu Sans Mono"
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
    release = root / "src" / "Dotty" / "bin" / "Release" / "net11.0"
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
    parser.add_argument("--warmup-runs", type=int, default=1)
    parser.add_argument("--lines", type=_positive_int, default=500_000)
    parser.add_argument("--sample-interval-ms", type=_positive_float, default=50.0)
    parser.add_argument("--startup-timeout", type=_positive_float, default=20.0)
    parser.add_argument("--json-out", type=Path, default=None)
    parser.add_argument("--app", default=str(default_app(root)))
    parser.add_argument("--include", default="dotty,kitty,ghostty,wezterm")
    args = parser.parse_args(argv)
    if args.warmup_runs < 0:
        parser.error("--warmup-runs cannot be negative")
    return args


def write_workload(script_path):
    script_path.write_text(
        "#!/bin/sh\n"
        "set -eu\n"
        "python3 - <<'PY' 2>>\"$TERMINAL_BENCH_LOG\"\n"
        "import os, sys, time, subprocess\n"
        f"sys.path.insert(0, {str(Path(__file__).resolve().parent)!r})\n"
        "from output_write import write_all\n"
        "line = b'The quick brown fox jumps over the lazy dog 0123456789\\n'\n"
        "lines = int(os.environ['TERMINAL_BENCH_LINES'])\n"
        "log = os.environ['TERMINAL_BENCH_LOG']\n"
        "ready = False\n"
        "go_file = os.environ['TERMINAL_BENCH_GO_FILE']\n"
        "deadline = time.monotonic() + float(os.environ['TERMINAL_BENCH_STARTUP_TIMEOUT'])\n"
        "while not os.path.exists(go_file):\n"
        "    size = os.get_terminal_size(sys.stdout.fileno())\n"
        "    stty = subprocess.run(['stty', 'size'], check=True, stdin=sys.stdout, capture_output=True, text=True).stdout.split()\n"
        "    tty_rows, tty_cols = map(int, stty)\n"
        "    with open(log, 'a', encoding='utf-8') as handle:\n"
        "        handle.write(f'{time.time_ns()} geometry {size.lines} {size.columns}\\n')\n"
        "        handle.write(f'{time.time_ns()} stty {tty_rows} {tty_cols}\\n')\n"
        "        if not ready:\n"
        "            handle.write(f'{time.time_ns()} ready\\n')\n"
        "            ready = True\n"
        "    if time.monotonic() >= deadline:\n"
        "        raise SystemExit('timed out waiting for terminal geometry gate')\n"
        "    time.sleep(0.05)\n"
        "size = os.get_terminal_size(sys.stdout.fileno())\n"
        "stty = subprocess.run(['stty', 'size'], check=True, stdin=sys.stdout, capture_output=True, text=True).stdout.split()\n"
        "tty_rows, tty_cols = map(int, stty)\n"
        "if (size.lines, size.columns, tty_rows, tty_cols) != (24, 80, 24, 80):\n"
        "    with open(log, 'a', encoding='utf-8') as handle:\n"
        "        handle.write(f'{time.time_ns()} invalid-grid\\n')\n"
        "    raise SystemExit(f'expected 24x80 child tty at start, got {size.lines}x{size.columns} and stty {tty_rows}x{tty_cols}')\n"
        "with open(log, 'a', encoding='utf-8') as handle:\n"
        "    handle.write(f'{time.time_ns()} start\\n')\n"
        "out = sys.stdout.buffer\n"
        "chunk = line * 1000\n"
        "full_chunks, remainder = divmod(lines, 1000)\n"
        "bytes_written = 0\n"
        "for _ in range(full_chunks):\n"
        "    bytes_written += write_all(out, chunk)[0]\n"
        "if remainder:\n"
        "    bytes_written += write_all(out, line * remainder)[0]\n"
        "out.flush()\n"
        "end_ns = time.time_ns()\n"
        "if bytes_written != len(line) * lines: raise OSError('incomplete workload write')\n"
        "with open(log, 'a', encoding='utf-8') as handle:\n"
        "    handle.write(f'{time.time_ns()} written {bytes_written}\\n')\n"
        "    handle.write(f'{end_ns} end\\n')\n"
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
            if len(parts) == 2 and parts[1] != "invalid-grid":
                events[parts[1]] = int(parts[0])
            elif len(parts) == 4 and parts[1] == "geometry":
                events["geometry"] = {"timestamp_ns": int(parts[0]), "rows": int(parts[2]), "cols": int(parts[3])}
            elif len(parts) == 4 and parts[1] == "stty":
                events["stty"] = {"rows": int(parts[2]), "cols": int(parts[3])}
            elif len(parts) == 3 and parts[1] == "written":
                events["bytes_written"] = int(parts[2])
            elif len(parts) == 2 and parts[1] == "invalid-grid":
                events["invalid_grid"] = True
    except (OSError, ValueError):
        pass
    return events


def failure_artifacts(log_path, workload):
    details = {"child_log_path": str(log_path), "workload_script": str(workload)}
    try:
        size = log_path.stat().st_size
        with log_path.open("rb") as handle:
            handle.seek(max(0, size - 8192))
            details["child_log_tail"] = handle.read().decode("utf-8", errors="replace")
        details["child_log_truncated"] = size > 8192
    except OSError as exc:
        details["child_log_error"] = str(exc)
    try:
        details["workload_script_text"] = workload.read_text(encoding="utf-8")
    except OSError as exc:
        details["workload_script_error"] = str(exc)
    return details

def terminal_command(name, args, workload, config_dir=None):
    if name == "dotty":
        app = Path(args.app)
        return [str(app)] if app.exists() else None
    if name == "kitty":
        exe = os.environ.get("KITTY_BIN") or shutil.which("kitty")
        return [exe, "--config", "NONE", "--detach=no", "--title", "terminal-output-bench",
                "-o", f"font_family={BENCH_FONT}", "-o", "font_size=12", "-o", "window_padding_width=0",
                "-o", f"initial_window_width={TERMINAL_COLS}c", "-o", f"initial_window_height={TERMINAL_ROWS}c", str(workload)] if exe else None
    if name == "ghostty":
        exe = os.environ.get("GHOSTTY_BIN") or shutil.which("ghostty")
        return [exe, "--config-default-files=false", "--gtk-single-instance=false", f"--font-family={BENCH_FONT}",
                "--font-size=12", "--window-padding-x=0", "--window-padding-y=0",
                f"--window-width={TERMINAL_COLS}", f"--window-height={TERMINAL_ROWS}", "-e", str(workload)] if exe else None
    if name == "wezterm":
        exe = os.environ.get("WEZTERM_BIN") or shutil.which("wezterm")
        config_file = Path(config_dir or workload.parent) / "wezterm.lua"
        return [exe, "--config-file", str(config_file), "start", "--always-new-process", "--", str(workload)] if exe else None
    return None
def prepare_run_config(name, workload, run_id):
    config_dir = workload.parent / f"config-{name}-{run_id}"
    config_dir.mkdir()
    if name == "dotty":
        config = {"font": {"family": BENCH_FONT, "size": 16, "lineHeight": 1},
                  "window": {"opacity": 1, "padding": {"left": 0, "right": 0, "top": 0, "bottom": 0}},
                  "tabBar": {"show": False}}
        (config_dir / "config.json").write_text(json.dumps(config), encoding="utf-8")
    elif name == "wezterm":
        config = (
            "local wezterm = require 'wezterm'\n"
            "return {\n"
            f"  font = wezterm.font('{BENCH_FONT}'), font_size = 12,\n"
            f"  initial_cols = {TERMINAL_COLS}, initial_rows = {TERMINAL_ROWS},\n"
            "  window_background_opacity = 1, enable_tab_bar = false, window_decorations = 'NONE',\n"
            "  window_padding = { left = 0, right = 0, top = 0, bottom = 0 },\n"
            "}\n"
        )
        (config_dir / "wezterm.lua").write_text(config, encoding="utf-8")
    return config_dir
def _reserve_loopback_port():
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
        listener.bind(("127.0.0.1", 0))
        return listener.getsockname()[1]


def _dotty_stats(port):
    try:
        with socket.create_connection(("127.0.0.1", port), timeout=0.25) as connection:
            connection.sendall(b"STATS\n")
            response = connection.makefile("r", encoding="utf-8").readline()
        return json.loads(response) if response else None
    except (OSError, json.JSONDecodeError):
        return None


def terminal_provenance(name, args):
    cmd = terminal_command(name, args, Path("workload.sh"))
    if not cmd:
        return {"status": "unavailable"}
    binary = Path(cmd[0]).resolve()
    record = {"status": "available", "binary": str(binary)}
    if not binary.is_file():
        return record | {"status": "unavailable"}
    record["sha256"] = hashlib.sha256(binary.read_bytes()).hexdigest()
    if name == "dotty":
        record["managed_module_sha256"] = {module.name: hashlib.sha256(module.read_bytes()).hexdigest() for module in sorted(binary.parent.glob("*otty*.dll"))}
    native_dependencies = sorted(path for path in binary.parent.rglob("*.so*") if path.is_file())
    record["native_dependency_sha256"] = {str(path.relative_to(binary.parent)): hashlib.sha256(path.read_bytes()).hexdigest() for path in native_dependencies}
    record["glfw_native_sha256"] = {name: digest for name, digest in record["native_dependency_sha256"].items() if "glfw" in name.lower()}
    dependency_tool = shutil.which("ldd")
    if dependency_tool:
        try:
            linked = subprocess.run([dependency_tool, str(binary)], capture_output=True, text=True, timeout=5)
            dependencies, missing = {}, []
            for line in linked.stdout.splitlines():
                if "not found" in line:
                    missing.append(line.split("=>", 1)[0].strip())
                for token in line.split():
                    candidate = Path(token)
                    if token.startswith("/") and candidate.is_file():
                        resolved = candidate.resolve()
                        dependencies[str(resolved)] = hashlib.sha256(resolved.read_bytes()).hexdigest()
                        break
            record["loaded_native_dependency_sha256"] = dependencies
            record["unresolved_native_dependencies"] = missing
            record["glfw_native_sha256"].update({path: digest for path, digest in dependencies.items() if "glfw" in Path(path).name.lower()})
            record["ldd_returncode"] = linked.returncode
        except (OSError, subprocess.SubprocessError):
            record["ldd_returncode"] = None
    version_args = [str(binary), "+version" if name == "ghostty" else "--version"]
    try:
        version = subprocess.run(version_args, capture_output=True, text=True, timeout=5)
        text = (version.stdout or version.stderr).strip()
        record["version"] = text.splitlines()[0] if version.returncode == 0 and text else None
    except (OSError, subprocess.SubprocessError):
        record["version"] = None
    return record


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
def compare_grid(actual):
    if not isinstance(actual, dict) or not isinstance(actual.get("cols"), int) or not isinstance(actual.get("rows"), int):
        return False, "workload could not query terminal PTY geometry"
    matches = actual["cols"] == TERMINAL_COLS and actual["rows"] == TERMINAL_ROWS
    reason = None if matches else f"terminal grid was {actual['cols']}x{actual['rows']}, requested {TERMINAL_COLS}x{TERMINAL_ROWS}"
    return matches, reason


def _display_window(pid):
    """Find and expose the benchmark-owned terminal window for live grid sizing."""
    hyprland = bool(os.environ.get("WAYLAND_DISPLAY") and os.environ.get("HYPRLAND_INSTANCE_SIGNATURE") and shutil.which("hyprctl"))
    if os.environ.get("WAYLAND_DISPLAY") and not hyprland:
        raise RuntimeError("Wayland geometry requires Hyprland/hyprctl")
    if not hyprland and not shutil.which("xdotool"):
        raise RuntimeError("terminal geometry requires Hyprland/hyprctl or X11/xdotool")
    owned = process_tree_pids(pid) | {pid}
    if hyprland:
        clients = json.loads(subprocess.check_output(["hyprctl", "-j", "clients"], text=True, timeout=5))
        window = next((client for client in clients if client.get("pid") in owned and client.get("mapped")), None)
        if not window:
            return None
        address = window["address"]
        selector = f"address:{address}"
        subprocess.run(["hyprctl", "dispatch", "setfloating", selector], check=True, capture_output=True, text=True, timeout=5)
        subprocess.run(["hyprctl", "dispatch", "focuswindow", selector], check=True, capture_output=True, text=True, timeout=5)
        clients = json.loads(subprocess.check_output(["hyprctl", "-j", "clients"], text=True, timeout=5))
        window = next(client for client in clients if client.get("address") == address)
        return {"kind": "hyprland", "id": address, "rect": (*window["at"], *window["size"])}
    for child in owned:
        found = subprocess.run(["xdotool", "search", "--onlyvisible", "--pid", str(child)], capture_output=True, text=True, timeout=5)
        if found.returncode == 0 and found.stdout.strip():
            window_id = found.stdout.splitlines()[-1]
            values = dict(line.split("=", 1) for line in subprocess.check_output(["xdotool", "getwindowgeometry", "--shell", window_id], text=True, timeout=5).splitlines() if "=" in line)
            return {"kind": "x11", "id": window_id, "rect": tuple(int(values[key]) for key in ("X", "Y", "WIDTH", "HEIGHT"))}
    return None


def _resize_display_window(window, width, height):
    width, height = max(100, round(width)), max(100, round(height))
    if window["kind"] == "hyprland":
        subprocess.run(["hyprctl", "dispatch", "resizewindowpixel", f"exact {width} {height},address:{window['id']}"], check=True, capture_output=True, text=True, timeout=5)
    else:
        subprocess.run(["xdotool", "windowsize", window["id"], str(width), str(height)], check=True, capture_output=True, text=True, timeout=5)


def run_once(name, args, workload, run_number, run_id=None, warmup=False):
    run_id = run_id or secrets.token_hex(6)
    log_path = Path(tempfile.gettempdir()) / f"terminal-output-bench-{name}-{os.getpid()}-{run_id}.log"
    try:
        log_path.unlink()
    except OSError:
        pass

    config_dir = prepare_run_config(name, workload, run_id)
    cmd = terminal_command(name, args, workload, config_dir)
    base = {"terminal": name, "run": run_number, "run_id": run_id, "warmup": warmup,
            "terminal_config": {"font_family": BENCH_FONT, "font_size": 16 if name == "dotty" else 12,
                                 "font_size_unit": "px" if name == "dotty" else "pt",
                                 "requested_cols": TERMINAL_COLS, "requested_rows": TERMINAL_ROWS,
                                 "padding_px": 0, "user_config_isolated": True}}
    if cmd is None:
        base.update({"skipped": True, "reason": "binary not found", **_sample_metrics([], [])})
        return base

    env = os.environ.copy()
    go_path = workload.parent / f"go-{name}-{run_id}"
    env["TERMINAL_BENCH_GO_FILE"] = str(go_path)
    env["TERMINAL_BENCH_STARTUP_TIMEOUT"] = str(args.startup_timeout)
    env["TERMINAL_BENCH_LINES"] = str(args.lines)
    env["TERMINAL_BENCH_LOG"] = str(log_path)
    dotty_port = _reserve_loopback_port() if name == "dotty" else None
    if name == "dotty":
        env.update(DOTTY_SHELL=str(workload), DOTTY_CONFIG_HOME=str(config_dir), DOTTY_TEST_PORT=str(dotty_port))
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
    window_info = None
    gate_released = False
    last_resize_at = 0.0

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
            if events.get("invalid_grid"):
                result = {**base, "skipped": False, "status": "partial", "reason": "child TTY did not match requested 80x24 before timing",
                          "terminal_grid": events.get("geometry"), "child_stty": events.get("stty"),
                          "command": cmd, "command_argv": cmd, **_sample_metrics(samples, tree_samples)}
                return result
            if events.get("ready") and not gate_released:
                try:
                    window_info = _display_window(proc.pid)
                except (OSError, subprocess.SubprocessError, RuntimeError, ValueError, StopIteration) as exc:
                    result = {**base, "skipped": True, "reason": f"could not prepare actual terminal window: {exc}",
                              "command": cmd, "command_argv": cmd, **_sample_metrics(samples, tree_samples)}
                    return result
                geometry = events.get("geometry")
                stty = events.get("stty")
                if window_info and geometry and stty and geometry.get("cols") == stty.get("cols") and geometry.get("rows") == stty.get("rows"):
                    if geometry["cols"] == TERMINAL_COLS and geometry["rows"] == TERMINAL_ROWS:
                        go_path.touch()
                        gate_released = True
                    elif geometry["cols"] > 0 and geometry["rows"] > 0 and time.monotonic() - last_resize_at >= 0.2:
                        _, _, width, height = window_info["rect"]
                        target_width = width * TERMINAL_COLS / geometry["cols"]
                        target_height = height * TERMINAL_ROWS / geometry["rows"]
                        _resize_display_window(window_info, target_width, target_height)
                        last_resize_at = time.monotonic()
            if "end" in events and "start" in events:
                output_ms = (events["end"] - events["start"]) / 1_000_000.0
                launch_to_start_ms = (events["start"] - started_ns) / 1_000_000.0
                bytes_written = events.get("bytes_written")
                if bytes_written != len(LINE.encode("utf-8")) * args.lines:
                    return {**base, "skipped": False, "status": "failed",
                            "reason": "missing or incomplete acknowledged child byte count",
                            "bytes_written": bytes_written, "command": cmd}
                geometry = events.get("geometry")
                result = {
                    **base, "skipped": False, "status": "ok", "pid": proc.pid,
                    "launch_to_child_start_ms": launch_to_start_ms, "output_ms": output_ms,
                    "throughput_mb_s": (bytes_written / (1024 * 1024)) / (output_ms / 1000.0) if output_ms > 0 else None,
                    "bytes_written": bytes_written, "terminal_grid": geometry,
                    "actual_window": {"kind": window_info["kind"], "id": window_info["id"], "rect": window_info["rect"]} if window_info else None,
                    "child_stty": events.get("stty"),
                    "display_protocol": "wayland" if os.environ.get("WAYLAND_DISPLAY") else "x11" if os.environ.get("DISPLAY") else "unknown",
                    **_sample_metrics(samples, tree_samples), "command": cmd, "command_argv": cmd,
                }
                geometry_match, geometry_error = compare_grid(geometry)
                stty_match, stty_error = compare_grid(events.get("stty"))
                geometry_match = geometry_match and stty_match
                geometry_error = geometry_error or stty_error
                result["geometry_match"] = geometry_match
                if not geometry_match:
                    result.update(status="partial", reason=geometry_error)
                if name == "dotty":
                    state = _dotty_stats(dotty_port)
                    result["window_backend"] = state.get("windowBackend") if state else None
                return result

            if proc.poll() is not None and not started_event_seen:
                result = {**base, "skipped": True, "reason": f"process exited before workload started ({proc.returncode})",
                          "command": cmd, "command_argv": cmd, **_sample_metrics(samples, tree_samples)}
                return result

            time.sleep(sample_interval)

        events = read_events(log_path)
        reason = "timed out preparing a visible terminal window with actual PTY geometry 80x24" if events.get("ready") and not gate_released else "timed out waiting for workload completion"
        result = {**base, "skipped": True, "reason": reason,
                  "terminal_grid": events.get("geometry"), "child_stty": events.get("stty"),
                  "actual_window": {"kind": window_info["kind"], "id": window_info["id"], "rect": window_info["rect"]} if window_info else None,
                  "started_event_seen": started_event_seen, "command": cmd, "command_argv": cmd,
                  **_sample_metrics(samples, tree_samples)}
        return result
    finally:
        try:
            go_path.unlink()
        except OSError:
            pass
        cleanup_errors = stop_process(proc)
        if result is not None:
            result.setdefault("run_id", run_id)
            result.setdefault("warmup", warmup)
            result.setdefault("terminal", name)
            result.setdefault("run", run_number)
        if result is not None and (result.get("skipped") or result.get("status") != "ok"):
            result.update(failure_artifacts(log_path, workload))
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
        terminal_results = [result for result in results if result["terminal"] == terminal and not result.get("warmup")]
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
    measured = [item for item in results if not item.get("warmup")]
    if not measured:
        return "skipped"
    failed = [item for item in measured if item.get("status") == "failed"]
    successful = [item for item in measured if not item.get("skipped") and item.get("status", "ok") != "failed"]
    skipped = [item for item in measured if item.get("skipped")]
    if failed and not successful:
        return "failed"
    if failed or (successful and skipped) or any(item.get("status") == "partial" for item in measured):
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
            "run_id": getattr(args, "_run_id", None),
            "started_at_utc": getattr(args, "_started_at_utc", _utc_now()),
            "completed_at_utc": _utc_now(),
            "effective_args": _effective_args(args),
            "bytes_written": bytes_per_run,
            "terminal_setup": {
                "grid": {"cols": TERMINAL_COLS, "rows": TERMINAL_ROWS},
                "font_family": BENCH_FONT,
                "font_sizes": {"dotty_px": 16, "references_pt": 12},
                "padding_px": 0, "user_configs_isolated": True,
                "window_backend_source": "Dotty STATS; reference terminals not exposed by stable CLI",
                "display_protocol_environment": "wayland" if os.environ.get("WAYLAND_DISPLAY") else "x11" if os.environ.get("DISPLAY") else "unknown",
            },
            "workload_line_sha256": hashlib.sha256(LINE.encode("utf-8")).hexdigest(),
            "benchmark_script_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
            "terminal_provenance": getattr(args, "_terminal_provenance", {}),
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


def build_schedule(terminals, warmup_runs, runs, run_id):
    schedule = []
    sequence = 0
    for warmup, rounds in ((True, warmup_runs), (False, runs)):
        for round_number in range(rounds):
            shift = round_number % len(terminals)
            for terminal in terminals[shift:] + terminals[:shift]:
                sequence += 1
                schedule.append({"terminal": terminal, "run": sequence, "run_id": f"{run_id}-{sequence:04d}", "warmup": warmup})
    return schedule


def main():
    args = parse_args()
    args._started_at_utc = _utc_now()
    args._run_id = secrets.token_hex(8)
    args.warmup_runs = getattr(args, "warmup_runs", 0)
    terminals = [item.strip() for item in args.include.split(",") if item.strip()]
    args._terminal_provenance = {name: terminal_provenance(name, args) for name in terminals}
    with tempfile.TemporaryDirectory(prefix="terminal-output-bench-") as temp_dir:
        workload = Path(temp_dir) / "workload.sh"
        write_workload(workload)
        schedule = build_schedule(terminals, args.warmup_runs, args.runs, args._run_id)
        results = [run_once(item["terminal"], args, workload, item["run"], item["run_id"], item["warmup"]) for item in schedule]
    payload = build_payload(args, results)
    if args.json_out is not None:
        write_json(args.json_out, payload)
    print(json.dumps(payload, indent=2))


if __name__ == "__main__":
    main()
