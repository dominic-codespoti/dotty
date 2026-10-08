#!/usr/bin/env python3
import argparse
import os
import signal
import subprocess
import sys
import time
from pathlib import Path


def parse_ctxt_switches(status_text):
    values = {}
    for line in status_text.splitlines():
        key, separator, value = line.partition(":")
        if separator and key.strip() in ("voluntary_ctxt_switches", "nonvoluntary_ctxt_switches"):
            values[key.strip()] = int(value.strip())
    return values.get("voluntary_ctxt_switches", 0), values.get("nonvoluntary_ctxt_switches", 0)


def rate(before, after, seconds):
    return (after - before) / seconds


def _proc_stat(pid):
    text = Path(f"/proc/{pid}/stat").read_text()
    fields = text[text.rfind(")") + 2:].split()
    return int(fields[11]) + int(fields[12])


def _is_dotty(pid):
    try:
        comm = Path(f"/proc/{pid}/comm").read_text().strip()
        exe = Path(f"/proc/{pid}/exe").resolve().name
        return comm.lower() == "dotty" or exe.lower() == "dotty"
    except (FileNotFoundError, ProcessLookupError, PermissionError, OSError):
        return False


def _descendants(root_pid):
    found = set()
    pending = [root_pid]
    while pending:
        parent = pending.pop()
        try:
            for child_file in Path(f"/proc/{parent}/task").glob("*/children"):
                pending.extend(int(pid) for pid in child_file.read_text().split() if int(pid) not in found)
        except (FileNotFoundError, ProcessLookupError, PermissionError, OSError, ValueError):
            pass
        found.add(parent)
    return found


def _find_dotty(root_pid):
    return next((pid for pid in _descendants(root_pid) if _is_dotty(pid)), None)


def _snapshot(pid):
    switches = 0
    threads = 0
    for status in Path(f"/proc/{pid}/task").glob("*/status"):
        try:
            switches += sum(parse_ctxt_switches(status.read_text()))
            threads += 1
        except (FileNotFoundError, ProcessLookupError, PermissionError, OSError):
            continue
    return switches, _proc_stat(pid), threads


def _terminate_group(process):
    try:
        os.killpg(process.pid, signal.SIGTERM)
    except ProcessLookupError:
        pass
    try:
        process.wait(timeout=2)
    except subprocess.TimeoutExpired:
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        process.wait()


def main(argv=None):
    parser = argparse.ArgumentParser(description="Measure idle Dotty process wakeups")
    parser.add_argument("--max-switches-per-s", type=float, required=True)
    parser.add_argument("--settle", type=float, default=6)
    parser.add_argument("--window", type=float, default=6)
    parser.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args(argv)
    command = args.command[1:] if args.command[:1] == ["--"] else args.command
    if not command:
        parser.error("a command is required after --")
    process = subprocess.Popen(command, start_new_session=True)
    try:
        time.sleep(args.settle)
        pid = _find_dotty(process.pid)
        if pid is None or process.poll() is not None:
            print("idle: Dotty process not found or exited early", file=sys.stderr)
            return 2
        try:
            before_switches, before_cpu, _ = _snapshot(pid)
        except (FileNotFoundError, ProcessLookupError, PermissionError):
            print("idle: Dotty process exited early", file=sys.stderr)
            return 2
        time.sleep(args.window)
        if process.poll() is not None or not Path(f"/proc/{pid}").exists():
            print("idle: Dotty process exited early", file=sys.stderr)
            return 2
        try:
            after_switches, after_cpu, threads = _snapshot(pid)
        except (FileNotFoundError, ProcessLookupError, PermissionError):
            print("idle: Dotty process exited early", file=sys.stderr)
            return 2
        switches_per_s = rate(before_switches, after_switches, args.window)
        cpu_percent = rate(before_cpu, after_cpu, args.window) / os.sysconf("SC_CLK_TCK") * 100
        print(f"idle: {switches_per_s:.1f} ctx switches/s, {cpu_percent:.1f}% of one core over {args.window:g}s, {threads} threads")
        return 1 if switches_per_s > args.max_switches_per_s else 0
    finally:
        _terminate_group(process)


if __name__ == "__main__":
    raise SystemExit(main())
