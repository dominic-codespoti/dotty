#!/usr/bin/env python3
"""Real Neovim traversal, with independently sampled compositor-visible progress."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import shlex
import shutil
import signal
import statistics
import subprocess
import threading
import time
from pathlib import Path

from terminal_output_bench import default_app, process_tree_pids, process_tree_rss_mb, _positive_int, _positive_float

HERE = Path(__file__).resolve().parent


def parse_args(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--app", default=str(default_app()))
    parser.add_argument("--include", default="dotty,ghostty,kitty")
    parser.add_argument("--lines", type=_positive_int, default=1_000_000)
    parser.add_argument("--cols", type=_positive_int, default=200)
    parser.add_argument("--rows", type=_positive_int, default=60)
    parser.add_argument("--runs", type=_positive_int, default=5)
    parser.add_argument("--warmup-runs", type=int, default=1)
    parser.add_argument("--profile", choices=("plain", "syntax"), default="plain")
    parser.add_argument("--capture", choices=("auto", "none"), default="auto")
    parser.add_argument("--sample-hz", type=_positive_float, default=60)
    parser.add_argument("--startup-timeout", type=_positive_float, default=30)
    parser.add_argument("--timeout", type=_positive_float, default=1800)
    parser.add_argument("--display-kind", choices=("real", "virtual"), default="real")
    parser.add_argument("--output-dir", type=Path)
    parser.add_argument("--json-out", type=Path)
    args = parser.parse_args(argv)
    if args.cols < 60 or args.rows < 5:
        parser.error("the visible progress marker requires at least 60 columns and 5 rows")
    if args.lines > 0xffffffff or args.warmup_runs < 0:
        parser.error("lines must fit the 32-bit visual marker and warmup-runs cannot be negative")
    names = args.include.split(",")
    if not names or any(name not in ("dotty", "ghostty", "kitty", "wezterm") for name in names) or len(set(names)) != len(names):
        parser.error("include must be unique names from dotty,ghostty,kitty,wezterm")
    return args


def write_fixture(path, lines, profile):
    digest = hashlib.sha256()
    size = 0
    with path.open("wb") as handle:
        for line in range(1, lines + 1):
            if profile == "syntax":
                text = f"static const unsigned row_{line:010d} = {line % 65536:05d}; /* deterministic source line {line:010d} */\n"
            else:
                text = f"line {line:010d} | value {line * 2654435761 % 2**32:010d} | abcdefghijklmnopqrstuvwxyz 0123456789 benchmark traversal\n"
            if line == lines:
                text = f"/* EOF line {line:010d} */\n" if profile == "syntax" else f"EOF line {line:010d}\n"
            data = text.encode("ascii")
            handle.write(data)
            digest.update(data)
            size += len(data)
    return {"path": str(path), "sha256": digest.hexdigest(), "bytes": size, "lines": lines}


def command_output(argv):
    result = subprocess.run(argv, capture_output=True, text=True, timeout=5)
    if result.returncode:
        raise RuntimeError(f"{argv[0]} failed: {result.stderr.strip() or result.stdout.strip()}")
    return result.stdout


class WindowController:
    """Manage only benchmark-owned windows; restore the prior Wayland workspace."""
    def __init__(self):
        self.hyprland = bool(os.environ.get("WAYLAND_DISPLAY") and shutil.which("hyprctl") and os.environ.get("HYPRLAND_INSTANCE_SIGNATURE"))
        if os.environ.get("WAYLAND_DISPLAY") and not self.hyprland:
            raise RuntimeError("Wayland window control requires Hyprland/hyprctl; grim alone cannot position owned windows")
        if not self.hyprland and not shutil.which("xdotool"):
            raise RuntimeError("window geometry control requires Hyprland/hyprctl or X11/xdotool")
        self.original_workspace = None
        self.workspace = f"dotty-nvim-bench-{os.getpid()}"

    def enter(self):
        if self.hyprland:
            self.original_workspace = json.loads(command_output(["hyprctl", "-j", "activeworkspace"]))["name"]
            command_output(["hyprctl", "dispatch", "workspace", f"name:{self.workspace}"])

    def close(self):
        if self.original_workspace is not None:
            command_output(["hyprctl", "dispatch", "workspace", f"name:{self.original_workspace}"])
            self.original_workspace = None

    def find(self, pid):
        owned = process_tree_pids(pid) | {pid}
        if self.hyprland:
            clients = json.loads(command_output(["hyprctl", "-j", "clients"]))
            return next((client["address"] for client in clients if client["pid"] in owned and client["mapped"]), None)
        for child in owned:
            result = subprocess.run(["xdotool", "search", "--onlyvisible", "--pid", str(child)], capture_output=True, text=True, timeout=5)
            if result.returncode == 0 and result.stdout.strip():
                return result.stdout.splitlines()[-1]
        return None

    def rect(self, window):
        if self.hyprland:
            client = next(item for item in json.loads(command_output(["hyprctl", "-j", "clients"])) if item["address"] == window)
            return (*client["at"], *client["size"])
        values = dict(line.split("=", 1) for line in command_output(["xdotool", "getwindowgeometry", "--shell", window]).splitlines() if "=" in line)
        return tuple(int(values[key]) for key in ("X", "Y", "WIDTH", "HEIGHT"))

    def prepare(self, window):
        if self.hyprland:
            selector = f"address:{window}"
            command_output(["hyprctl", "dispatch", "movetoworkspacesilent", f"name:{self.workspace},{selector}"])
            command_output(["hyprctl", "dispatch", "setfloating", selector])
            command_output(["hyprctl", "dispatch", "movewindowpixel", f"exact 20 60,{selector}"])
            command_output(["hyprctl", "dispatch", "focuswindow", selector])
        else:
            command_output(["xdotool", "windowmove", window, "20", "60"])
            # Xvfb need not have a window manager; raising still makes pixels observable.
            command_output(["xdotool", "windowraise", window])

    def resize(self, window, width, height):
        width, height = max(100, round(width)), max(100, round(height))
        if self.hyprland:
            command_output(["hyprctl", "dispatch", "resizewindowpixel", f"exact {width} {height},address:{window}"])
        else:
            command_output(["xdotool", "windowsize", window, str(width), str(height)])


def launch_command(name, args, shell):
    font = "DejaVu Sans Mono"
    if name == "dotty":
        return [str(Path(args.app).resolve())] if Path(args.app).is_file() else None
    binary = os.environ.get(f"{name.upper()}_BIN") or shutil.which(name)
    if not binary:
        return None
    if name == "kitty":
        return [binary, "--config", "NONE", "--detach=no", "--title", "nvim-scroll-bench", "-o", f"font_family={font}", "-o", "font_size=12", "-o", "background_opacity=1", "-o", "window_padding_width=0", str(shell)]
    if name == "ghostty":
        return [binary, "--config-default-files=false", "--gtk-single-instance=false", f"--font-family={font}", "--font-size=12", "--background-opacity=1", "--window-padding-x=0", "--window-padding-y=0", "-e", str(shell)]
    return [binary, "--config-file", str(shell.parent / "wezterm.lua"), "start", "--always-new-process", "--", str(shell)]


def read_json(path):
    try:
        return json.loads(path.read_text())
    except (OSError, json.JSONDecodeError):
        return None


class EventReader:
    def __init__(self, path):
        self.path = path
        self.offset = 0
        self.pending = b""
        self.events = []

    def update(self):
        if not self.path.exists():
            return
        with self.path.open("rb") as handle:
            handle.seek(self.offset)
            data = handle.read()
            self.offset = handle.tell()
        parts = (self.pending + data).split(b"\n")
        self.pending = parts.pop()
        self.events.extend(json.loads(part) for part in parts if part)


class Sampler:
    def __init__(self, observer, rect, hz):
        self.observer, self.rect, self.interval = observer, rect, 1 / hz
        self.samples, self.errors = [], []
        self.stop = threading.Event()
        self.thread = threading.Thread(target=self.run, daemon=True)

    def run(self):
        while not self.stop.is_set():
            started = time.monotonic()
            try:
                sample = self.observer.sample(self.rect)
                self.samples.append(sample or {"monotonic_ns": time.monotonic_ns(), "line": None, "phase": None, "eof_visible": False})
            except Exception as exc:
                self.errors.append(str(exc))
                break
            self.stop.wait(max(0, self.interval - (time.monotonic() - started)))

    def close(self):
        self.stop.set()
        self.thread.join(timeout=10)
        if self.thread.is_alive():
            raise RuntimeError("visual capture did not stop")


def cleanup(proc, run_dir):
    (run_dir / "release").touch()
    try:
        proc.wait(timeout=3)
        return
    except subprocess.TimeoutExpired:
        pass
    # Descendant shells/Neovim can have separate PTY sessions. Target captured
    # owned PIDs, never a global process-name kill.
    descendants = process_tree_pids(proc.pid) - {proc.pid}
    for pid in descendants:
        try:
            os.kill(pid, signal.SIGTERM)
        except ProcessLookupError:
            pass
    proc.terminate()
    try:
        proc.wait(timeout=3)
    except subprocess.TimeoutExpired:
        proc.kill()
        proc.wait(timeout=3)
    for pid in descendants:
        try:
            os.kill(pid, signal.SIGKILL)
        except ProcessLookupError:
            pass


def run_once(name, args, fixture, root, controller, index, warmup):
    run_dir = root / f"{index:03d}-{name}-{'warmup' if warmup else 'measured'}"
    run_dir.mkdir()
    result = {"terminal": name, "run": index, "warmup": warmup, "status": "failed", "errors": [], "artifacts": {"directory": str(run_dir), "events": str(run_dir / "events.jsonl"), "samples": str(run_dir / "visual-samples.jsonl"), "terminal_log": str(run_dir / "terminal.log")}}
    shell = run_dir / "workload.sh"
    nvim = shutil.which("nvim")
    if not nvim:
        result["errors"].append("nvim is not installed")
        return result
    argv = [nvim, "--clean", "-n", "-i", "NONE", "--cmd", "lua _G.nvim_scroll_bench_open_ns=(vim.uv or vim.loop).hrtime()", str(fixture), "-c", "lua dofile(" + json.dumps(str(HERE / "nvim_scroll_workload.lua")) + ")"]
    shell.write_text("#!/bin/sh\nexec " + shlex.join(argv) + "\n")
    shell.chmod(0o755)
    (run_dir / "wezterm.lua").write_text("local w=require 'wezterm'; return {font=w.font('DejaVu Sans Mono'),font_size=12,window_background_opacity=1,enable_tab_bar=false}\n")
    cmd = launch_command(name, args, shell)
    if cmd is None:
        result.update(status="failed" if name == "dotty" else "skipped", reason="terminal binary unavailable")
        return result
    config_dir = run_dir / "config"
    config_dir.mkdir()
    (config_dir / "config.json").write_text(json.dumps({"font": {"family": "DejaVu Sans Mono", "size": 16, "lineHeight": 1}, "window": {"opacity": 1, "padding": {"left": 0, "right": 0, "top": 0, "bottom": 0}}, "tabBar": {"show": False}}))
    env = os.environ.copy()
    env.update(NVIM_BENCH_RUN_DIR=str(run_dir), NVIM_BENCH_LINES=str(args.lines), NVIM_BENCH_PROFILE=args.profile, DOTTY_SHELL=str(shell), DOTTY_CONFIG_HOME=str(config_dir), DOTTY_SKIP_CONFIG_COMPILE="1")
    result["command"] = cmd
    def sample_memory():
        current = process_tree_rss_mb(proc.pid)
        if current is not None:
            rss.append(current)
    proc, observer, sampler = None, None, None
    rss = []
    capture_error = "capture explicitly disabled" if args.capture == "none" else None
    events = EventReader(run_dir / "events.jsonl")
    launched_ns = time.monotonic_ns()
    try:
        with (run_dir / "terminal.log").open("w") as log:
            proc = subprocess.Popen(cmd, env=env, stdout=log, stderr=log, start_new_session=True)
        result["pid"] = proc.pid
        deadline = time.monotonic() + args.startup_timeout
        window, ready = None, None
        while time.monotonic() < deadline:
            ready = read_json(run_dir / "ready.json")
            sample_memory()
            window = controller.find(proc.pid)
            if ready and window:
                break
            if proc.poll() is not None:
                raise RuntimeError(f"terminal exited before Neovim ready ({proc.returncode})")
            time.sleep(.05)
        if not ready or not window:
            raise RuntimeError("timed out waiting for Neovim and its owned visible window")
        controller.prepare(window)
        # Correct from Neovim's actual dimensions, including terminal decorations.
        stable = 0
        for attempt in range(24):
            ready = read_json(run_dir / "ready.json") or ready
            sample_memory()
            rect = controller.rect(window)
            if (ready["cols"], ready["rows"]) == (args.cols, args.rows):
                stable += 1
                if stable >= 4:
                    break
                time.sleep(.15)
                continue
            stable = 0
            cw, ch = rect[2] / ready["cols"], rect[3] / ready["rows"]
            controller.resize(window, rect[2] + (args.cols - ready["cols"]) * cw, rect[3] + (args.rows - ready["rows"]) * ch)
            time.sleep(.15)
        else:
            raise RuntimeError(f"could not establish {args.cols}x{args.rows}; actual {ready['cols']}x{ready['rows']}")
        # Native toolkit placement can override our first move during startup.
        controller.prepare(window)
        time.sleep(.5)  # Let owned-window compositor placement animations finish.
        rect = controller.rect(window)
        ready = read_json(run_dir / "ready.json") or ready
        if (ready["cols"], ready["rows"]) != (args.cols, args.rows):
            raise RuntimeError("grid changed while finalizing window placement")
        result.update(cols=ready["cols"], rows=ready["rows"], geometry_ready_ms=(time.monotonic_ns() - launched_ns) / 1e6, window_rect=rect)
        result["launch_to_ready_ms"] = (ready["initial_ready_ns"] - launched_ns) / 1e6 if ready.get("initial_ready_ns") is not None else None
        result["file_open_to_ready_ms"] = ready["file_open_to_ready_ns"] / 1e6 if ready.get("file_open_to_ready_ns") is not None else None
        if args.capture == "auto":
            if os.environ.get("WAYLAND_DISPLAY") and shutil.which("grim"):
                screenshot = run_dir / "initial.png"
                captured = subprocess.run(["grim", "-s", "1", "-g", f"{rect[0]},{rect[1]} {rect[2]}x{rect[3]}", str(screenshot)], capture_output=True, timeout=5)
                if captured.returncode == 0:
                    result["artifacts"]["initial_screen"] = str(screenshot)
            try:
                from nvim_scroll_observer import VisualObserver
                observer = VisualObserver()
                visible_deadline = time.monotonic() + min(10, args.startup_timeout)
                while time.monotonic() < visible_deadline:
                    sample = observer.sample(rect)
                    if sample and sample["line"] == 1 and sample["phase"] == "ready":
                        result["marker_cell_width"] = sample["cell_width"]
                        result["marker_cell_height"] = sample["cell_height"]
                        break
                    time.sleep(.02)
                else:
                    raise RuntimeError("initial Neovim progress marker not visible in compositor capture")
                sampler = Sampler(observer, rect, args.sample_hz)
                sampler.samples.append(sample)
                sampler.thread.start()
            except Exception as exc:
                capture_error = str(exc)
                result["visual"] = {"status": "partial", "reason": capture_error, "final_screen_verified": False}
                if observer is not None:
                    observer.close()
                    observer = None
        (run_dir / "go").touch()
        deadline = time.monotonic() + args.timeout
        end = start = None
        while time.monotonic() < deadline:
            events.update()
            error = next((event for event in events.events if event["type"] == "error"), None)
            if error:
                raise RuntimeError(f"Neovim workload failed: {error}")
            start = next((event for event in events.events if event["type"] == "start"), None)
            end = next((event for event in events.events if event["type"] == "end"), None)
            sample_memory()
            if end:
                break
            if proc.poll() is not None:
                raise RuntimeError(f"terminal exited during traversal ({proc.returncode})")
            time.sleep(.05)
        if not start or not end:
            raise RuntimeError("traversal timed out")
        if end["steps"] != args.lines - 1 or end["final_line"] != args.lines or (end["cols"], end["rows"]) != (args.cols, args.rows):
            raise RuntimeError(f"invalid traversal or grid changed: {end}")
        duration = end["duration_ns"] / 1e6
        result.update(traversal_ms=duration, lines_per_second=end["steps"] / (duration / 1000) if duration > 0 else None, steps=end["steps"], final_line=end["final_line"])
        if sampler:
            tail_deadline = deadline
            while time.monotonic() < tail_deadline:
                sample_memory()
                if any(sample.get("phase") == "done" and sample.get("line") == args.lines and sample.get("eof_visible") for sample in sampler.samples):
                    break
                if sampler.errors:
                    break
                time.sleep(.02)
            sampler.close()
            from nvim_scroll_observer import summarize_samples
            result["visual"] = summarize_samples(sampler.samples, start["monotonic_ns"], end["monotonic_ns"], args.lines, args.sample_hz)
            result["visual"]["capture_errors"] = sampler.errors
            result["visual"]["backend"] = getattr(observer, "backend", type(observer).__name__)
            result["visual"]["capture_metadata"] = observer.metadata
            progress = [event for event in events.events if event["type"] == "progress"]
            result["visual"]["workload_progress_events"] = len(progress)
            result["visual"]["attribution"] = "Correlate raw workload progress events with visual samples; observed gaps alone do not identify whether Neovim or the terminal stalled."
            if sampler.errors:
                result["visual"]["status"] = "partial"
            # Retain a screenshot of the actual compositor, not an offscreen window.
            if os.environ.get("WAYLAND_DISPLAY") and shutil.which("grim"):
                screenshot = run_dir / "final.png"
                done = subprocess.run(["grim", "-s", "1", "-g", f"{rect[0]},{rect[1]} {rect[2]}x{rect[3]}", str(screenshot)], capture_output=True, timeout=5)
                if done.returncode == 0:
                    result["artifacts"]["final_screen"] = str(screenshot)
        else:
            result["visual"] = {"status": "partial", "reason": capture_error, "final_screen_verified": False, "longest_visible_stall_ms": None, "eof_to_visible_ms": None}
        result.update(process_tree_peak_rss_mb=max(rss) if rss else None, process_tree_rss_samples=len(rss))
        result["status"] = "ok" if result["visual"].get("status") == "ok" else "partial"
    except (OSError, RuntimeError, KeyError, ValueError, subprocess.SubprocessError) as exc:
        result["errors"].append(str(exc))
    finally:
        try:
            if sampler and sampler.thread.is_alive():
                sampler.close()
            if sampler:
                with (run_dir / "visual-samples.jsonl").open("w") as handle:
                    for sample in sampler.samples:
                        handle.write(json.dumps(sample) + "\n")
        except (OSError, RuntimeError) as exc:
            result["errors"].append(f"saving visual evidence failed: {exc}")
            if result["status"] == "ok":
                result["status"] = "partial"
        try:
            if observer:
                observer.close()
            if proc:
                cleanup(proc, run_dir)
        except (OSError, RuntimeError, subprocess.SubprocessError) as exc:
            result["errors"].append(f"cleanup failed: {exc}")
            if result["status"] == "ok":
                result["status"] = "partial"
    result["artifacts"] = {key: value for key, value in result["artifacts"].items() if Path(value).exists()}
    return result


def summarize(runs):
    summary = {}
    for name in dict.fromkeys(run["terminal"] for run in runs):
        measured = [run for run in runs if run["terminal"] == name and not run["warmup"]]
        usable = [run for run in measured if run.get("traversal_ms") is not None]
        states = [run["status"] for run in measured]
        status = "failed" if "failed" in states else "skipped" if states and all(state == "skipped" for state in states) else "partial" if any(state in ("partial", "skipped") for state in states) else "ok"
        def values(key, visual=False):
            return [value for run in usable if (value := (run.get("visual", {}) if visual else run).get(key)) is not None]
        def median(key, visual=False):
            data = values(key, visual)
            return statistics.median(data) if data else None
        stalls, memory = values("longest_visible_stall_ms", True), values("process_tree_peak_rss_mb")
        summary[name] = {"status": status, "runs": len(usable), "traversal_ms_median": median("traversal_ms"), "lines_per_second_median": median("lines_per_second"), "longest_visible_stall_ms_max": max(stalls) if stalls else None, "eof_to_visible_ms_median": median("eof_to_visible_ms", True), "process_tree_peak_rss_mb_max": max(memory) if memory else None}
    return summary


def main(argv=None):
    args = parse_args(argv)
    root = (args.output_dir or HERE.parents[1] / "artifacts" / "perf" / f"nvim-scroll-{time.time_ns()}").resolve()
    root.mkdir(parents=True, exist_ok=True)
    json_path = (args.json_out or root / "nvim-scroll.json").resolve()
    json_path.parent.mkdir(parents=True, exist_ok=True)
    payload = {"status": "failed", "errors": [], "config": {key: str(value) if isinstance(value, Path) else value for key, value in vars(args).items()}, "runs": [], "summary": {}, "artifacts": {"directory": str(root)}}
    controller = None
    try:
        payload["terminal_versions"] = {}
        for name in args.include.split(","):
            binary = Path(args.app) if name == "dotty" else Path(os.environ.get(f"{name.upper()}_BIN") or shutil.which(name) or "/nonexistent")
            if binary.is_file():
                digest = hashlib.sha256()
                with binary.open("rb") as handle:
                    for block in iter(lambda: handle.read(1024 * 1024), b""):
                        digest.update(block)
                record = {"binary": str(binary.resolve()), "sha256": digest.hexdigest()}
                if name == "dotty":
                    record["managed_module_sha256"] = {module.name: hashlib.sha256(module.read_bytes()).hexdigest() for module in sorted(binary.parent.glob("*otty*.dll"))}
                if name != "dotty":
                    record["version"] = command_output([str(binary), "+version" if name == "ghostty" else "--version"]).strip()
                payload["terminal_versions"][name] = record
        fixture = root / ("massive.c" if args.profile == "syntax" else "massive.txt")
        payload["fixture"] = write_fixture(fixture, args.lines, args.profile)
        payload["artifacts"]["fixture"] = str(fixture)
        payload["nvim_version"] = command_output(["nvim", "--version"]).splitlines()[0]
        payload["font"] = {"family": "DejaVu Sans Mono", "competitor_size_points": 12, "dotty_size_pixels": 16}
        payload["display"] = {"kind": args.display_kind, "DISPLAY": os.environ.get("DISPLAY"), "WAYLAND_DISPLAY": os.environ.get("WAYLAND_DISPLAY"), "desktop": os.environ.get("XDG_CURRENT_DESKTOP")}
        if os.environ.get("WAYLAND_DISPLAY") and shutil.which("hyprctl"):
            payload["display"]["monitors"] = json.loads(command_output(["hyprctl", "-j", "monitors"]))
        controller = WindowController()
        controller.enter()
        terminals = args.include.split(",")
        total = args.warmup_runs + args.runs
        for round_number in range(total):
            order = terminals[round_number % len(terminals):] + terminals[:round_number % len(terminals)]
            for name in order:
                run = run_once(name, args, fixture, root, controller, len(payload["runs"]) + 1, round_number < args.warmup_runs)
                payload["runs"].append(run)
                print(f"{name}: {run['status']}; traversal={run.get('traversal_ms')} ms; visual={run.get('visual', {}).get('status', 'unavailable')}", flush=True)
                json_path.write_text(json.dumps(payload, indent=2) + "\n")
        payload["summary"] = summarize(payload["runs"])
        states = [run["status"] for run in payload["runs"]]
        payload["status"] = "failed" if "failed" in states else "partial" if any(state in ("partial", "skipped") for state in states) else "ok"
    except (OSError, RuntimeError, subprocess.SubprocessError, ValueError, KeyboardInterrupt) as exc:
        payload["status"] = "failed"
        payload["errors"].append(str(exc) or "benchmark interrupted")
    finally:
        if controller:
            try:
                controller.close()
            except (OSError, RuntimeError, subprocess.SubprocessError) as exc:
                payload["errors"].append(f"workspace restoration failed: {exc}")
                if payload["status"] == "ok":
                    payload["status"] = "partial"
        json_path.write_text(json.dumps(payload, indent=2) + "\n")
    print(f"Results: {json_path}")
    return 1 if payload["status"] == "failed" else 0


if __name__ == "__main__":
    def interrupted(signum, frame):
        raise KeyboardInterrupt(f"benchmark interrupted by signal {signum}")
    signal.signal(signal.SIGTERM, interrupted)
    raise SystemExit(main())
