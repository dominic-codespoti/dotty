#!/usr/bin/env python3
"""Measure a Dotty publish directory: sizes, `--version` start time and memory.

Writes one JSON document (schema 1) with:
  files              per-file sizes in bytes, sorted by path
  unpackedBytes      sum of all file sizes
  archiveBytes       size of a gzip-compressed tar of the directory (mtime/owner normalised)
  versionStartMs     median/min/max wall time of `<exe> --version` over --runs runs
  versionPeakRssKiB  peak RSS of one `--version` run (Unix; null elsewhere)
  idleRssKiB         VmRSS after --idle-seconds of a launched window (Linux, --idle-rss only)

With --budgets FILE the measurement is compared against FILE[rid]; any metric above its budget
fails with exit 1. Budgets must come from a recorded run (see docs/Packaging.md); a missing rid entry
is reported and does not fail.
"""

from __future__ import annotations

import argparse
import gzip
import io
import json
import os
import platform
import statistics
import subprocess
import sys
import tarfile
import tempfile
import time
from pathlib import Path

SCHEMA = 1
BUDGET_KEYS = ("unpackedBytes", "archiveBytes", "versionStartMsMedian", "versionPeakRssKiB", "idleRssKiB")


def file_sizes(root: Path) -> dict[str, int]:
    sizes = {}
    for path in sorted(root.rglob("*")):
        if path.is_file() and not path.is_symlink():
            sizes[path.relative_to(root).as_posix()] = path.stat().st_size
    return sizes


def archive_size(root: Path, name: str) -> int:
    """Size of a reproducible .tar.gz of root (entries sorted, mtime 0, numeric owner 0)."""

    def normalise(info: tarfile.TarInfo) -> tarfile.TarInfo:
        info.mtime = 0
        info.uid = info.gid = 0
        info.uname = info.gname = ""
        return info

    buffer = io.BytesIO()
    with gzip.GzipFile(fileobj=buffer, mode="wb", compresslevel=9, mtime=0) as gz:
        with tarfile.open(fileobj=gz, mode="w", format=tarfile.PAX_FORMAT) as tar:
            tar.add(root, arcname=name, filter=normalise, recursive=False)
            for path in sorted(root.rglob("*")):
                tar.add(path, arcname=f"{name}/{path.relative_to(root).as_posix()}", filter=normalise, recursive=False)
    return len(buffer.getvalue())


def find_executable(root: Path) -> Path | None:
    for candidate in ("dotty", "dotty.exe"):
        path = root / candidate
        if path.is_file():
            return path
    return None


def time_version(exe: Path, runs: int, runner=subprocess.run, clock=time.perf_counter) -> dict:
    samples = []
    for _ in range(runs):
        start = clock()
        result = runner([str(exe), "--version"], stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False)
        elapsed = (clock() - start) * 1000.0
        if result.returncode != 0:
            raise RuntimeError(f"{exe} --version exited with {result.returncode}")
        samples.append(elapsed)
    return {
        "runs": runs,
        "medianMs": round(statistics.median(samples), 2),
        "minMs": round(min(samples), 2),
        "maxMs": round(max(samples), 2),
    }


def version_peak_rss_kib(exe: Path) -> int | None:
    try:
        import resource
    except ImportError:
        return None
    pid = os.fork()
    if pid == 0:  # pragma: no cover - child
        devnull = os.open(os.devnull, os.O_WRONLY)
        os.dup2(devnull, 1)
        os.dup2(devnull, 2)
        try:
            os.execv(str(exe), [str(exe), "--version"])
        finally:
            os._exit(127)
    _, status, usage = os.wait4(pid, 0)
    if os.waitstatus_to_exitcode(status) != 0:
        return None
    rss = usage.ru_maxrss
    return rss // 1024 if sys.platform == "darwin" else rss  # macOS reports bytes, Linux KiB


def read_vm_rss_kib(status_text: str) -> int | None:
    for line in status_text.splitlines():
        if line.startswith("VmRSS:"):
            return int(line.split()[1])
    return None


def idle_rss_kib(exe: Path, seconds: float) -> int | None:
    """Launch a window, wait, read /proc/<pid>/status. Needs Linux and a display."""
    if not sys.platform.startswith("linux"):
        return None
    process = subprocess.Popen([str(exe)], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        time.sleep(seconds)
        if process.poll() is not None:
            return None
        return read_vm_rss_kib(Path(f"/proc/{process.pid}/status").read_text())
    finally:
        process.terminate()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()


def measure(root: Path, rid: str, runs: int, idle_seconds: float | None) -> dict:
    files = file_sizes(root)
    result = {
        "schema": SCHEMA,
        "rid": rid,
        "host": {"system": platform.system(), "machine": platform.machine()},
        "unpackedBytes": sum(files.values()),
        "archiveBytes": archive_size(root, f"dotty-{rid}"),
        "files": files,
        "versionStartMs": None,
        "versionPeakRssKiB": None,
        "idleRssKiB": None,
    }
    exe = find_executable(root)
    if exe is not None and runs > 0:
        result["versionStartMs"] = time_version(exe, runs)
        if os.name == "posix":
            result["versionPeakRssKiB"] = version_peak_rss_kib(exe)
        if idle_seconds is not None:
            result["idleRssKiB"] = idle_rss_kib(exe, idle_seconds)
    return result


def metric(result: dict, key: str):
    if key == "versionStartMsMedian":
        timing = result.get("versionStartMs")
        return None if timing is None else timing["medianMs"]
    return result.get(key)


def check_budgets(result: dict, budgets: dict) -> list[str]:
    entry = budgets.get(result["rid"])
    if entry is None:
        return []
    failures = []
    for key in BUDGET_KEYS:
        limit = entry.get(key)
        value = metric(result, key)
        if limit is None or value is None:
            continue
        if value > limit:
            failures.append(f"{key} {value} exceeds budget {limit}")
    return failures


def summary_markdown(result: dict, failures: list[str]) -> str:
    timing = result["versionStartMs"]
    lines = [
        f"### Distribution size ({result['rid']})",
        "",
        "| Metric | Value |",
        "|---|---|",
        f"| Unpacked | {result['unpackedBytes']:,} bytes |",
        f"| Archive (.tar.gz) | {result['archiveBytes']:,} bytes |",
        f"| `--version` median | {'n/a' if timing is None else str(timing['medianMs']) + ' ms'} |",
        f"| `--version` peak RSS | {'n/a' if result['versionPeakRssKiB'] is None else str(result['versionPeakRssKiB']) + ' KiB'} |",
        f"| Idle RSS | {'n/a' if result['idleRssKiB'] is None else str(result['idleRssKiB']) + ' KiB'} |",
    ]
    largest = sorted(result["files"].items(), key=lambda item: (-item[1], item[0]))[:5]
    lines += ["", "Largest files: " + ", ".join(f"`{name}` {size:,}" for name, size in largest)]
    lines += [f"- **over budget**: {failure}" for failure in failures]
    return "\n".join(lines) + "\n"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--publish-dir", required=True, type=Path)
    parser.add_argument("--rid", required=True)
    parser.add_argument("--runs", type=int, default=11, help="--version runs for the median (0 skips timing)")
    parser.add_argument("--idle-rss", type=float, metavar="SECONDS", help="launch a window and read VmRSS after SECONDS (needs a display)")
    parser.add_argument("--budgets", type=Path, help="JSON object keyed by rid; fail when a metric exceeds its budget")
    parser.add_argument("--output", type=Path, help="write the JSON here instead of stdout")
    parser.add_argument("--summary", type=Path, help="append a Markdown summary (e.g. $GITHUB_STEP_SUMMARY)")
    args = parser.parse_args(argv)

    if not args.publish_dir.is_dir():
        print(f"error: {args.publish_dir} is not a directory", file=sys.stderr)
        return 2
    if args.runs < 0:
        print("error: --runs must be >= 0", file=sys.stderr)
        return 2

    result = measure(args.publish_dir, args.rid, args.runs, args.idle_rss)
    failures = []
    if args.budgets is not None:
        budgets = json.loads(args.budgets.read_text(encoding="utf-8"))
        if args.rid not in budgets:
            print(f"note: no budget recorded for {args.rid}", file=sys.stderr)
        failures = check_budgets(result, budgets)

    text = json.dumps(result, indent=2, sort_keys=False) + "\n"
    if args.output is not None:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(text, encoding="utf-8")
    else:
        sys.stdout.write(text)
    if args.summary is not None:
        with args.summary.open("a", encoding="utf-8") as handle:
            handle.write(summary_markdown(result, failures))
    for failure in failures:
        print(f"error: {failure}", file=sys.stderr)
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
