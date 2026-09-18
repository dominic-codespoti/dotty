#!/usr/bin/env python3
"""Capture repeatable CPU, counter, allocation, and heap evidence for Dotty.

The module keeps process discovery and command construction separate from the
lifecycle code so they can be tested without launching Dotty or a profiler.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import shutil
import signal
import subprocess
import tempfile
import time
from pathlib import Path
from typing import Any, Iterable, Mapping, Sequence

SCHEMA_VERSION = 1
CAPTURES = ("cpu", "counters", "alloc", "gcdump")
WORKLOADS = ("printable-ascii", "ansi-heavy", "scrolling-heavy")
DEFAULT_WORKLOAD = WORKLOADS[0]
MONOTONIC_CLOCK_DOMAIN = "monotonic_ns"
PRINTABLE_PAYLOAD = (b"A" * 79) + b"\n"
ANSI_PAYLOAD = (
    b"\x1b[38;5;196mRRRRRRRR\x1b[0m"
    b"\x1b[38;5;46mGGGGGGGG\x1b[0m"
    b"\x1b[38;5;21mBBBBBBBB\x1b[0m"
    b"\x1b[38;5;226mYYYYYYYY\x1b[0m\n"
)
SCROLLING_PAYLOAD = (b"S" * 75) + b"\n" + b"\x1b[1S"
WORKLOAD_PAYLOADS = {
    "printable-ascii": PRINTABLE_PAYLOAD,
    "ansi-heavy": ANSI_PAYLOAD,
    "scrolling-heavy": SCROLLING_PAYLOAD,
}
# Retain these names for callers that imported the original printable line.
LINE = PRINTABLE_PAYLOAD.decode("ascii")
LINE_BYTES = len(PRINTABLE_PAYLOAD)


def workload_payload(name: str = DEFAULT_WORKLOAD) -> bytes:
    """Return the immutable payload for a named complete workload iteration."""
    try:
        return WORKLOAD_PAYLOADS[name]
    except KeyError as exc:
        raise ValueError(f"unknown workload: {name}") from exc


def workload_metadata(name: str = DEFAULT_WORKLOAD, iterations: int = 0) -> dict[str, Any]:
    iterations = int(iterations)
    if iterations < 0:
        raise ValueError("iterations must be non-negative")
    payload = workload_payload(name)
    return {
        "workload": name,
        "bytes_per_iteration": len(payload),
        "iterations": int(iterations),
        "chunk_iterations": 256,
        "total_output_bytes": int(iterations) * len(payload),
        "payload_sha256": hashlib.sha256(payload).hexdigest(),
    }

# Keep a small cushion beyond the bounded setup/workload phases.  The
# collector's duration starts before the gate opens, so this covers startup,
# collector launch delay, and the workload hold without relying on Q input.
COUNTER_DURATION_MARGIN_SECONDS = 2.0
# Keep the target alive just beyond the counter session so the collector can
# finish writing its artifact before Dotty exits and tears down the shell.
COUNTER_TARGET_CLEANUP_MARGIN_SECONDS = 2.0
DEFAULT_COUNTER_DURATION_SECONDS = 60.0


# ---- /proc and marker seams -------------------------------------------------

def _pid_dirs(proc_root: Path | str = "/proc") -> list[int]:
    root = Path(proc_root)
    result = []
    try:
        for item in root.iterdir():
            if item.name.isdigit():
                result.append(int(item.name))
    except OSError:
        return []
    return result


def _read_cmdline(pid: int, proc_root: Path | str = "/proc") -> list[str]:
    try:
        raw = (Path(proc_root) / str(pid) / "cmdline").read_bytes()
    except OSError:
        return []
    return [part.decode("utf-8", "replace") for part in raw.split(b"\0") if part]


def process_cmdline(pid: int, proc_root: Path | str = "/proc") -> str:
    return " ".join(_read_cmdline(pid, proc_root))


def _read_ppid(pid: int, proc_root: Path | str = "/proc") -> int | None:
    base = Path(proc_root) / str(pid)
    try:
        status = (base / "status").read_text(encoding="utf-8", errors="replace")
        for line in status.splitlines():
            if line.startswith("PPid:"):
                return int(line.split()[1])
    except (OSError, ValueError, IndexError):
        pass
    # Linux stat has the parenthesized comm field, which can contain spaces.
    try:
        fields = (base / "stat").read_text(encoding="utf-8").split()
        return int(fields[3])
    except (OSError, ValueError, IndexError):
        return None


def proc_descendants(root_pid: int, proc_root: Path | str = "/proc") -> list[int]:
    """Return root and all currently discoverable descendants, deterministically."""
    children: dict[int, list[int]] = {}
    for pid in _pid_dirs(proc_root):
        ppid = _read_ppid(pid, proc_root)
        if ppid is not None:
            children.setdefault(ppid, []).append(pid)
    for values in children.values():
        values.sort()
    found: list[int] = []
    pending = [root_pid]
    seen: set[int] = set()
    while pending:
        pid = pending.pop(0)
        if pid in seen:
            continue
        seen.add(pid)
        found.append(pid)
        pending[0:0] = children.get(pid, [])
    return found


def process_tree(root_pid: int, proc_root: Path | str = "/proc") -> list[dict[str, Any]]:
    return [{"pid": pid, "cmdline": _read_cmdline(pid, proc_root)} for pid in proc_descendants(root_pid, proc_root)]


def has_coreclr_mapping(pid: int, proc_root: Path | str = "/proc") -> bool:
    try:
        maps = (Path(proc_root) / str(pid) / "maps").read_text(encoding="utf-8", errors="replace")
    except OSError:
        return False
    return any(token in maps.lower() for token in ("libcoreclr", "coreclr.dll", "coreclr.so"))


# Friendly aliases used by callers/tests.
coreclr_map_detected = has_coreclr_mapping
coreclr_map_detection = has_coreclr_mapping


def _same_executable(token: str, app: str) -> bool:
    try:
        return Path(token).resolve() == Path(app).resolve()
    except OSError:
        return token == app


def _is_shell_or_helper(cmd: Sequence[str]) -> bool:
    if not cmd:
        return True
    names = {Path(cmd[0]).name.lower()}
    banned = {
        "sh", "bash", "dash", "zsh", "fish", "python", "python3", "perl",
        "pwsh", "powershell", "xvfb-run", "xvfb", "pty-helper", "script",
        "dotnet-trace", "dotnet-counters", "dotnet-gcdump",
    }
    return bool(names & banned)


def managed_target_candidates(root_pid: int, app: str | Path, proc_root: Path | str = "/proc") -> list[dict[str, Any]]:
    app = str(app)
    requested_dll = Path(app)
    if requested_dll.suffix.lower() != ".dll":
        requested_dll = Path(f"{requested_dll}.dll")
    candidates: list[dict[str, Any]] = []
    for pid in proc_descendants(root_pid, proc_root):
        cmd = _read_cmdline(pid, proc_root)
        if not cmd or _is_shell_or_helper(cmd) or not has_coreclr_mapping(pid, proc_root):
            continue
        first = cmd[0]
        exact = _same_executable(first, app) or (not Path(app).is_absolute() and Path(first).name == Path(app).name)
        # An apphost is preferred over a generic dotnet host.  A process whose
        # command line does not identify the requested app is never accepted.
        dotnet_host = Path(first).name.lower() in {"dotnet", "dotnet.exe"}
        dotnet_dll = dotnet_host and any(
            _same_executable(token, str(requested_dll))
            or (not requested_dll.is_absolute() and Path(token).name == requested_dll.name)
            for token in cmd[1:]
        )
        if not exact and not dotnet_dll:
            continue
        rank = 0 if exact else 1
        candidates.append({"pid": pid, "cmdline": cmd, "rank": rank, "kind": "apphost" if exact else "dotnet-dll"})
    return sorted(candidates, key=lambda item: (item["rank"], item["pid"]))


def select_managed_pid(root_pid: int, app: str | Path, proc_root: Path | str = "/proc") -> int:
    candidates = managed_target_candidates(root_pid, app, proc_root)
    if not candidates:
        raise RuntimeError("no unique CoreCLR Dotty process found")
    best_rank = candidates[0]["rank"]
    best = [item for item in candidates if item["rank"] == best_rank]
    if len(best) != 1:
        raise RuntimeError("ambiguous CoreCLR Dotty process candidates")
    return int(best[0]["pid"])

find_descendants = proc_descendants
rank_managed_targets = managed_target_candidates
choose_managed_pid = select_managed_pid

resolve_managed_pid = select_managed_pid


def parse_markers(value: str | Path) -> dict[str, int]:
    """Parse READY/START/END markers in either ``KEY value`` or ``value key`` form."""
    if isinstance(value, Path):
        try:
            text = value.read_text(encoding="utf-8", errors="replace")
        except OSError:
            text = ""
    elif isinstance(value, str) and "\n" not in value and "\x00" not in value and len(value) < 4096:
        try:
            text = Path(value).read_text(encoding="utf-8", errors="replace")
        except (OSError, UnicodeError):
            text = value
    else:
        text = str(value)
    result: dict[str, int] = {}
    for raw in text.splitlines():
        parts = raw.strip().replace(":", " ").replace("=", " ").split()
        if not parts:
            continue
        normalized = {part.upper().replace("-", "_") for part in parts}
        if normalized & {"READY", "READY_NS"}:
            key = "READY"
        elif normalized & {"START", "START_NS", "WORKLOAD_START"}:
            key = "START"
        elif normalized & {"END", "END_NS", "WORKLOAD_END"}:
            key = "END"
        else:
            continue
        number = next((part.rstrip("nsNS") for part in parts if part.rstrip("nsNS").isdigit()), None)
        if number is not None:
            result[key.lower()] = int(number)
        elif key == "READY":
            result["ready"] = 1
    return result


def parse_marker_file(path: Path | str) -> dict[str, int]:
    return parse_markers(Path(path))


def marker_values(value: str | Path) -> dict[str, int]:
    return parse_markers(value)

parse_marker = parse_markers


def read_rss_bytes(pid: int, proc_root: Path | str = "/proc") -> int | None:
    try:
        text = (Path(proc_root) / str(pid) / "status").read_text(encoding="utf-8", errors="replace")
    except OSError:
        return None
    for line in text.splitlines():
        if line.startswith("VmRSS:"):
            try:
                return int(line.split()[1]) * 1024
            except (ValueError, IndexError):
                return None
    return None


def rss_mb(pid: int, proc_root: Path | str = "/proc") -> float | None:
    value = read_rss_bytes(pid, proc_root)
    return value / (1024 * 1024) if value is not None else None


def process_tree_rss(root_pid: int, proc_root: Path | str = "/proc") -> int | None:
    return sample_rss(root_pid, proc_root).get("tree_bytes")


def sample_rss(root_pid: int, proc_root: Path | str = "/proc") -> dict[str, Any]:
    pids = proc_descendants(root_pid, proc_root)
    values = [read_rss_bytes(pid, proc_root) for pid in pids]
    tree = sum(values) if values and all(value is not None for value in values) else None
    return {
        "timestamp_ns": time.monotonic_ns(),
        "timestamp_clock_domain": MONOTONIC_CLOCK_DOMAIN,
        "root_bytes": read_rss_bytes(root_pid, proc_root),
        "tree_bytes": tree,
        "pids": pids,
    }

rss_sample = sample_rss


def register_artifact(output_dir: Path | str, path: Path | str, artifacts: Any = None, name: str | None = None) -> str | None:
    """Register an existing output and return a POSIX path relative to output_dir."""
    root = Path(output_dir).resolve()
    target = Path(path).resolve()
    try:
        relative = target.relative_to(root).as_posix()
    except ValueError as exc:
        raise ValueError(f"artifact is outside output directory: {target}") from exc
    if not target.exists():
        return None
    if artifacts is not None:
        if isinstance(artifacts, dict):
            if name:
                artifacts[name] = relative
            else:
                artifacts.setdefault("paths", []).append(relative)
        elif hasattr(artifacts, "append"):
            artifacts.append(relative)
    return relative


def aggregate_status(statuses: Iterable[str]) -> str:
    values = list(statuses)
    if any(value == "failed" for value in values):
        return "failed"
    if not values or all(value in {"skipped", "unsupported"} for value in values):
        return "skipped"
    if any(value != "ok" for value in values):
        return "partial"
    return "ok"


status_aggregate = aggregate_status
aggregate_capture_status = aggregate_status


# ---- tool command and execution seams --------------------------------------

def _tool_version(executable: str, timeout: float = 5.0) -> dict[str, Any]:
    command = [executable, "--version"]
    try:
        completed = subprocess.run(command, capture_output=True, text=True, timeout=timeout, check=False)
        return {"command": command, "stdout": completed.stdout, "stderr": completed.stderr, "exit_code": completed.returncode}
    except (OSError, subprocess.TimeoutExpired) as exc:
        return {"command": command, "stdout": "", "stderr": str(exc), "exit_code": None}


def resolve_tool(name: str, timeout: float = 5.0) -> dict[str, Any]:
    executable = shutil.which(name)
    if not executable:
        return {"name": name, "path": None, "available": False, "version": None, "version_result": None}
    version_result = _tool_version(executable, timeout)
    return {"name": name, "path": executable, "available": True, "version": version_result.get("stdout", "").strip() or version_result.get("stderr", "").strip(), "version_result": version_result}


def format_duration(seconds: float) -> str:
    """Format seconds as the dd:hh:mm:ss accepted by dotnet-counters."""
    seconds = float(seconds)
    if not math.isfinite(seconds) or seconds < 0:
        raise ValueError("duration must be a finite non-negative number")
    total = math.ceil(seconds)
    days, remainder = divmod(total, 24 * 60 * 60)
    hours, remainder = divmod(remainder, 60 * 60)
    minutes, seconds = divmod(remainder, 60)
    return f"{days:02d}:{hours:02d}:{minutes:02d}:{seconds:02d}"


format_counter_duration = format_duration


def counter_duration_seconds(startup_timeout: float, collector_start_delay: float, hold_seconds: float) -> float:
    """Bound the counter session across setup, gate delay, and workload hold."""
    return (
        max(float(startup_timeout), 0.0)
        + max(float(collector_start_delay), 0.0)
        + max(float(hold_seconds), 0.0)
        + COUNTER_DURATION_MARGIN_SECONDS
    )


def tool_commands(
    capture: str,
    pid: int,
    output_dir: Path | str,
    duration: str | None = None,
) -> list[list[str]]:
    out = Path(output_dir)
    if capture == "cpu":
        raw = out / "cpu" / "dotnet-trace.nettrace"
        return [["dotnet-trace", "collect", "--process-id", str(pid), "--profile", "dotnet-sampled-thread-time", "--output", str(raw)]]
    if capture == "alloc":
        raw = out / "alloc" / "alloc.nettrace"
        return [["dotnet-trace", "collect", "--process-id", str(pid), "--profile", "gc-verbose", "--output", str(raw)]]
    if capture == "counters":
        raw = out / "counters" / "counters.json"
        duration = duration or format_duration(DEFAULT_COUNTER_DURATION_SECONDS)
        return [["dotnet-counters", "collect", "--process-id", str(pid), "--counters", "System.Runtime", "--format", "json", "--duration", duration, "--output", str(raw)]]
    if capture == "gcdump":
        raw = out / "gcdump" / "heap.gcdump"
        return [["dotnet-gcdump", "collect", "--process-id", str(pid), "--output", str(raw)]]
    raise ValueError(f"unknown capture: {capture}")


def construct_tool_commands(capture: str, pid: int, output_dir: Path | str, duration: str | None = None) -> list[list[str]]:
    return tool_commands(capture, pid, output_dir, duration)


build_tool_command = tool_commands
build_collector_command = tool_commands
collector_command = tool_commands

build_tool_commands = tool_commands


def stop_process(proc: Any, timeout: float = 5.0, interrupt: bool = False) -> int | None:
    if proc is None:
        return None
    if proc.poll() is None:
        try:
            if interrupt and getattr(proc, "send_signal", None):
                proc.send_signal(signal.SIGINT)
            else:
                proc.terminate()
            proc.wait(timeout=timeout)
        except (subprocess.TimeoutExpired, OSError):
            try:
                proc.kill()
                proc.wait(timeout=timeout)
            except (subprocess.TimeoutExpired, OSError):
                pass
    return proc.poll()




def _speedscope_output_prefix(path: Path) -> Path:
    suffix = ".speedscope.json"
    if not path.name.endswith(suffix):
        raise ValueError(f"unexpected speedscope artifact path: {path}")
    return path.with_name(path.name[: -len(suffix)])


def _run_derived(command: list[str], stdout_path: Path, stderr_path: Path, timeout: float = 30.0) -> dict[str, Any]:
    stdout_path.parent.mkdir(parents=True, exist_ok=True)
    with stdout_path.open("wb") as out, stderr_path.open("wb") as err:
        try:
            proc = subprocess.Popen(command, stdout=out, stderr=err)
            code = proc.wait(timeout=timeout)
        except (OSError, subprocess.TimeoutExpired) as exc:
            if "proc" in locals():
                stop_process(proc, timeout=2)
                code = proc.poll()
            else:
                code = None
            err.write(str(exc).encode())
    return {"command": command, "exit_code": code, "stdout": str(stdout_path), "stderr": str(stderr_path)}


def write_workload(
    script_path: Path,
    marker_path: Path,
    gate_path: Path,
    lines: int = 500_000,
    hold_seconds: float = 1.0,
    workload: str = DEFAULT_WORKLOAD,
) -> None:
    payload = workload_payload(workload)
    script_path.write_text(
        "#!/usr/bin/env python3\n"
        "import os, pathlib, time\n"
        f"marker = pathlib.Path({str(marker_path)!r})\n"
        f"gate = pathlib.Path({str(gate_path)!r})\n"
        f"lines = {int(lines)}\n"
        f"hold = {float(hold_seconds)!r}\n"
        f"payload = {payload!r}\n"
        "marker.write_text('READY\\n', encoding='utf-8')\n"
        "while not gate.exists(): time.sleep(0.01)\n"
        "start = time.monotonic_ns(); marker.open('a', encoding='utf-8').write(f'START {start}\\n')\n"
        "out = os.fdopen(os.dup(1), 'wb'); chunk = payload * 256\n"
        "full, rem = divmod(lines, 256)\n"
        "for _ in range(full): out.write(chunk)\n"
        "if rem: out.write(payload * rem)\n"
        "out.flush(); end = time.monotonic_ns(); marker.open('a', encoding='utf-8').write(f'END {end}\\n')\n"
        "time.sleep(hold)\n",
        encoding="utf-8",
    )
    script_path.chmod(0o755)


def _capture_paths(output_dir: Path, capture: str) -> dict[str, Path]:
    folder = output_dir / capture
    folder.mkdir(parents=True, exist_ok=True)
    if capture == "cpu":
        return {"raw": folder / "dotnet-trace.nettrace", "top": folder / "top-methods.txt", "speedscope": folder / "dotnet-trace.speedscope.json"}
    if capture == "alloc":
        return {"raw": folder / "alloc.nettrace", "speedscope": folder / "alloc.speedscope.json"}
    if capture == "counters":
        return {"raw": folder / "counters.json"}
    return {"raw": folder / "heap.gcdump", "report": folder / "heap-stat.txt"}


def _wait_until(predicate, timeout: float, interval: float = 0.05) -> bool:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if predicate():
            return True
        time.sleep(interval)
    return bool(predicate())

def validate_counter_artifact(path: Path | str) -> dict[str, Any]:
    """Parse a counters artifact and require non-empty event evidence."""
    artifact = Path(path)
    try:
        payload = json.loads(artifact.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ValueError(f"invalid counters JSON: {exc}") from exc
    if not isinstance(payload, dict):
        raise ValueError("invalid counters JSON: expected an object")
    events = payload.get("Events")
    if not isinstance(events, list) or not events:
        raise ValueError("invalid counters JSON: expected a non-empty Events list")
    return payload


parse_counter_artifact = validate_counter_artifact



def run_capture(capture: str, args: argparse.Namespace, output_dir: Path, tool_info: Mapping[str, dict[str, Any]], proc_root: Path | str = "/proc") -> dict[str, Any]:
    required = "dotnet-gcdump" if capture == "gcdump" else "dotnet-trace" if capture in {"cpu", "alloc"} else "dotnet-counters"
    info = tool_info.get(required, {})
    workload_name = getattr(args, "workload", DEFAULT_WORKLOAD)
    workload_info = workload_metadata(workload_name, getattr(args, "lines", 0))
    result: dict[str, Any] = {
        "capture": capture,
        "status": "failed",
        "artifacts": {},
        "errors": [],
        "commands": [],
        "logs": {},
        "tool": info,
        **workload_info,
        "lifecycle_clock_domain": MONOTONIC_CLOCK_DOMAIN,
        "lifecycle_timestamps_ns": {
            "app_launch": None,
            "managed_pid": None,
            "collector_start": None,
            "gate": None,
            "start": None,
            "end": None,
            "sigint": None,
            "collector_exit": None,
        },
        "lifecycle_windows_ns": {
            "app_launch_to_managed_pid": None,
            "collector_start_to_gate": None,
            "end_to_collector_start": None,
            "gate_to_start": None,
            "end_to_sigint": None,
            "sigint_to_collector_exit": None,
        },
    }
    if not info.get("available"):
        result["status"] = "unsupported"
        result["errors"].append(f"missing requested tool: {required}")
        return result
    paths = _capture_paths(output_dir, capture)
    app = str(args.app)
    root_pid = managed_pid = None
    app_proc = collector = None
    temp_dir = None
    marker = gate = None
    samples: list[dict[str, Any]] = []
    counter_seconds = None
    workload_hold_seconds = args.hold_seconds
    try:
        if capture == "counters":
            counter_seconds = counter_duration_seconds(
                args.startup_timeout,
                args.collector_start_delay,
                args.hold_seconds,
            )
            workload_hold_seconds = max(
                float(args.hold_seconds),
                counter_seconds + COUNTER_TARGET_CLEANUP_MARGIN_SECONDS,
            )
        temp_dir = tempfile.TemporaryDirectory(prefix=f"dotnet-profile-{capture}-")
        temp = Path(temp_dir.name)
        marker, gate = temp / "markers.log", temp / "gate"
        workload = temp / "workload.py"
        if workload_name == DEFAULT_WORKLOAD:
            write_workload(workload, marker, gate, args.lines, workload_hold_seconds)
        else:
            write_workload(workload, marker, gate, args.lines, workload_hold_seconds, workload_name)
        env = os.environ.copy()
        env.update({"DOTTY_SHELL": str(workload), "DOTTY_SKIP_CONFIG_COMPILE": "1"})
        app_cmd = [app] if not app.lower().endswith(".dll") else [tool_info.get("dotnet", {}).get("path") or "dotnet", app]
        result["app_command"] = app_cmd
        result["lifecycle_timestamps_ns"]["app_launch"] = time.monotonic_ns()
        app_proc = subprocess.Popen(app_cmd, env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        root_pid = app_proc.pid

        def choose() -> int:
            return select_managed_pid(root_pid, app, proc_root)

        if not _wait_until(lambda: _try_select(choose) is not None, args.startup_timeout):
            raise RuntimeError("timed out resolving managed Dotty PID")
        managed_pid = choose()
        result["managed_pid"] = managed_pid
        result["lifecycle_timestamps_ns"]["managed_pid"] = time.monotonic_ns()
        result["process_tree"] = process_tree(root_pid, proc_root)
        counter_duration = format_duration(counter_seconds) if counter_seconds is not None else None
        if counter_duration is not None:
            result["counter_duration"] = counter_duration
        command = tool_commands(capture, managed_pid, output_dir, counter_duration)[0]
        command[0] = str(info["path"])
        result["commands"].append(command)
        log_dir = output_dir / capture
        stdout_log, stderr_log = log_dir / "collector.stdout.log", log_dir / "collector.stderr.log"
        result["logs"].update({"collector_stdout": str(stdout_log.relative_to(output_dir)), "collector_stderr": str(stderr_log.relative_to(output_dir))})
        with stdout_log.open("wb") as stdout, stderr_log.open("wb") as stderr:
            # Trace and counter sessions must already be listening before
            # the gate opens.  A gcdump is deliberately taken after END.
            if capture != "gcdump":
                result["lifecycle_timestamps_ns"]["collector_start"] = time.monotonic_ns()
                collector = subprocess.Popen(
                    command,
                    stdout=stdout,
                    stderr=stderr,
                )
                if args.collector_start_delay > 0:
                    time.sleep(args.collector_start_delay)
            result["lifecycle_timestamps_ns"]["gate"] = time.monotonic_ns()
            gate.touch()
            deadline = time.monotonic() + args.startup_timeout + max(args.hold_seconds, 0)
            while time.monotonic() < deadline:
                samples.append(sample_rss(root_pid, proc_root))
                events = parse_markers(marker)
                if "end" in events:
                    samples.append(sample_rss(root_pid, proc_root))
                    break
                if app_proc.poll() is not None:
                    raise RuntimeError(f"Dotty exited before workload end ({app_proc.returncode})")
                time.sleep(0.05)
            events = parse_markers(marker)
            if "end" not in events:
                raise TimeoutError("timed out waiting for workload end marker")
            result["markers"] = events
            result["lifecycle_timestamps_ns"]["start"] = events.get("start")
            result["lifecycle_timestamps_ns"]["end"] = events.get("end")
            result["output_bytes"] = workload_info["total_output_bytes"]
            start_marker, end_marker = events.get("start"), events.get("end")
            result["output_duration_ns"] = end_marker - start_marker if start_marker is not None and end_marker is not None else None
            result["output_duration_seconds"] = result["output_duration_ns"] / 1_000_000_000 if result["output_duration_ns"] is not None else None
            if result["output_duration_seconds"] is not None and result["output_duration_seconds"] > 0:
                result["throughput_bytes_per_second"] = result["output_bytes"] / result["output_duration_seconds"]
            roots = [item["root_bytes"] for item in samples if item.get("root_bytes") is not None]
            trees = [item["tree_bytes"] for item in samples if item.get("tree_bytes") is not None]
            result["root_rss_peak_bytes"] = max(roots) if roots else None
            result["tree_rss_peak_bytes"] = max(trees) if trees else None
            if capture == "counters":
                try:
                    collector.wait(timeout=counter_seconds)
                except subprocess.TimeoutExpired:
                    stop_process(collector, timeout=args.startup_timeout, interrupt=False)
            elif capture != "gcdump":
                result["lifecycle_timestamps_ns"]["sigint"] = time.monotonic_ns()
                stop_process(collector, timeout=args.startup_timeout, interrupt=True)
            else:
                result["lifecycle_timestamps_ns"]["collector_start"] = time.monotonic_ns()
                collector = subprocess.Popen(command, stdout=stdout, stderr=stderr)
                try:
                    collector.wait(timeout=max(args.startup_timeout, args.hold_seconds + 2))
                except subprocess.TimeoutExpired:
                    stop_process(collector, timeout=2, interrupt=False)
            if collector is not None:
                result["lifecycle_timestamps_ns"]["collector_exit"] = time.monotonic_ns()
        if collector is None or collector.returncode != 0:
            raise RuntimeError(f"collector exited with code {collector.returncode if collector is not None else None}")
        result["collector_exit_code"] = collector.returncode
        if not paths["raw"].exists():
            raise RuntimeError("collector reported success but raw artifact is missing")
        counter_artifact_valid = True
        if capture == "counters":
            # Keep the raw file registered even when its contents are unusable.
            registered = register_artifact(output_dir, paths["raw"], result["artifacts"], "raw")
            if registered is not None:
                result["artifacts"]["raw"] = registered
            try:
                validate_counter_artifact(paths["raw"])
            except ValueError as exc:
                counter_artifact_valid = False
                result["errors"].append({"stage": "counter_artifact_validation", "error": str(exc)})
                result["status"] = "partial"
        # Derived artifacts are created only after a successful raw collector.
        if capture == "cpu":
            report = [str(info["path"]), "report", str(paths["raw"]), "topN", "-n", "30", "--inclusive"]
            result["commands"].append(report)
            report_stderr = output_dir / capture / "report.stderr.log"
            report_result = _run_derived(report, paths["top"], report_stderr)
            result["logs"]["report_stderr"] = str(report_stderr.relative_to(output_dir))
            result["report_exit_code"] = report_result["exit_code"]
            convert = [str(info["path"]), "convert", "--format", "Speedscope", "--output", str(_speedscope_output_prefix(paths["speedscope"])), str(paths["raw"])]
            result["commands"].append(convert)
            speed_stdout = output_dir / capture / "speedscope.stdout.log"
            speed_stderr = output_dir / capture / "speedscope.stderr.log"
            result["logs"].update({"speedscope_stdout": str(speed_stdout.relative_to(output_dir)), "speedscope_stderr": str(speed_stderr.relative_to(output_dir))})
            result["speedscope_exit_code"] = _run_derived(convert, speed_stdout, speed_stderr)["exit_code"]
        elif capture == "alloc":
            convert = [str(info["path"]), "convert", "--format", "Speedscope", "--output", str(_speedscope_output_prefix(paths["speedscope"])), str(paths["raw"])]
            result["commands"].append(convert)
            speed_stdout = output_dir / capture / "speedscope.stdout.log"
            speed_stderr = output_dir / capture / "speedscope.stderr.log"
            result["logs"].update({"speedscope_stdout": str(speed_stdout.relative_to(output_dir)), "speedscope_stderr": str(speed_stderr.relative_to(output_dir))})
            convert_code = _run_derived(convert, speed_stdout, speed_stderr)["exit_code"]
            result["speedscope_exit_code"] = convert_code
            result["analysis"] = "typed allocation totals unavailable; raw_only" if convert_code != 0 else "raw_only (allocation trace retained; typed totals unavailable)"
        elif capture == "gcdump":
            report = [str(info["path"]), "report", str(paths["raw"]), "-t", "HeapStat"]
            result["commands"].append(report)
            report_stderr = output_dir / capture / "report.stderr.log"
            result["logs"]["report_stderr"] = str(report_stderr.relative_to(output_dir))
            result["report_exit_code"] = _run_derived(report, paths["report"], report_stderr)["exit_code"]
        for key, path in paths.items():
            registered = register_artifact(output_dir, path, result["artifacts"], key)
            if registered is not None:
                result["artifacts"][key] = registered
        if counter_artifact_valid:
            result["status"] = "ok" if result.get("collector_exit_code") == 0 and paths["raw"].exists() else "failed"
        derived_codes = [result.get(key) for key in ("report_exit_code", "speedscope_exit_code")]
        if any(code not in (0, None) for code in derived_codes):
            result["status"] = "partial" if result["status"] == "ok" else result["status"]
        if result.get("speedscope_exit_code") == 0 and not paths["speedscope"].exists():
            result["status"] = "partial" if result["status"] == "ok" else result["status"]
    except Exception as exc:
        result["errors"].append(str(exc))
        result["status"] = "failed"
    finally:
        stop_process(collector, timeout=2, interrupt=False)
        stop_process(app_proc, timeout=2, interrupt=False)
        if temp_dir is not None:
            temp_dir.cleanup()
    stamps = result["lifecycle_timestamps_ns"]
    collector_start_to_gate = (
        stamps["gate"] - stamps["collector_start"]
        if stamps["collector_start"] is not None
        and stamps["gate"] is not None
        and stamps["collector_start"] <= stamps["gate"]
        else None
    )
    end_to_collector_start = (
        stamps["collector_start"] - stamps["end"]
        if capture == "gcdump"
        and stamps["collector_start"] is not None
        and stamps["end"] is not None
        and stamps["end"] <= stamps["collector_start"]
        else None
    )
    result["lifecycle_windows_ns"] = {
        "app_launch_to_managed_pid": stamps["managed_pid"] - stamps["app_launch"]
        if stamps["app_launch"] is not None and stamps["managed_pid"] is not None else None,
        "collector_start_to_gate": collector_start_to_gate,
        "end_to_collector_start": end_to_collector_start,
        "gate_to_start": stamps["start"] - stamps["gate"]
        if stamps["start"] is not None and stamps["gate"] is not None else None,
        "end_to_sigint": stamps["sigint"] - stamps["end"]
        if stamps["sigint"] is not None and stamps["end"] is not None else None,
        "sigint_to_collector_exit": stamps["collector_exit"] - stamps["sigint"]
        if stamps["collector_exit"] is not None and stamps["sigint"] is not None else None,
    }
    return result


def _try_select(function: Any) -> int | None:
    try:
        return function()
    except (RuntimeError, OSError):
        return None


def non_negative_int(value: str) -> int:
    try:
        parsed = int(value)
    except ValueError as exc:
        raise argparse.ArgumentTypeError("must be an integer") from exc
    if parsed < 0:
        raise argparse.ArgumentTypeError("must be non-negative")
    return parsed

def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Capture Dotty .NET performance profiles")
    parser.add_argument("--app", required=True)
    parser.add_argument("--output-dir", required=True)
    parser.add_argument("--lines", type=non_negative_int, default=500_000)
    parser.add_argument("--workload", choices=WORKLOADS, default=DEFAULT_WORKLOAD)
    parser.add_argument("--captures", default=",".join(CAPTURES))
    parser.add_argument("--startup-timeout", type=float, default=20.0)
    parser.add_argument("--collector-start-delay", type=float, default=0.25)
    parser.add_argument("--hold-seconds", type=float, default=1.0)
    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    args = parse_args(argv)
    captures = [item.strip() for item in args.captures.split(",") if item.strip()]
    invalid = [item for item in captures if item not in CAPTURES]
    if invalid:
        raise SystemExit(f"unknown capture(s): {', '.join(invalid)}")
    output_dir = Path(args.output_dir).resolve()
    output_dir.mkdir(parents=True, exist_ok=True)
    tool_names = {"dotnet", "dotnet-trace", "dotnet-counters", "dotnet-gcdump"}
    tool_info = {name: resolve_tool(name) for name in sorted(tool_names)}
    records = [run_capture(capture, args, output_dir, tool_info) for capture in captures]
    status = aggregate_status([item["status"] for item in records])
    workload_info = workload_metadata(args.workload, args.lines)
    payload = {
        "schema_version": SCHEMA_VERSION,
        "kind": "dotnet_profile",
        "status": status,
        "metadata": {
            "app": str(Path(args.app).resolve()),
            "lines": args.lines,
            "line_bytes": workload_info["bytes_per_iteration"],
            **workload_info,
            "captures_requested": captures,
            "runtime_and_tool_versions": tool_info,
            "timestamp_utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        },
        "artifacts": {item["capture"]: item.get("artifacts", {}) for item in records},
        "captures": records,
        "errors": [error for item in records for error in item.get("errors", [])],
    }
    profile_path = output_dir / "profile.json"
    profile_path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(payload, indent=2))
    return 1 if status == "failed" else 0


if __name__ == "__main__":
    raise SystemExit(main())
