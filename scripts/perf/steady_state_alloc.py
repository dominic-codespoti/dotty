#!/usr/bin/env python3
"""Measure managed allocations in Dotty's steady-state interactive paths.

Launches Dotty under a private Xvfb display, drives real X input with xdotool,
and reads process-wide allocated bytes through the `ALLOC` control command
(GC.GetTotalAllocatedBytes(precise: true)). Each scenario is warmed up once and
then measured; the control transport's own allocation cost is calibrated and
subtracted. Pass --trace to also record a gc-verbose EventPipe trace (JIT
apphost only; NativeAOT does not emit allocation ticks).

Example:
  python3 scripts/perf/steady_state_alloc.py --app src/Dotty/bin/Release/net10.0/dotty
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import socket
import subprocess
import sys
import tempfile
import time
from pathlib import Path

LUA_CONFIG = """local dotty = require('dotty')
dotty.on('format_tab_title', function(tab) return tab.index .. ': ' .. tab.title end)
dotty.on('update_status', function() return os.date('%H:%M:%S') end)
dotty.on('tab_title_changed', function(tab, title) end)
"""


def free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


class Dotty:
    def __init__(self, app: str, lua: bool, trace: Path | None, every_object: bool = False):
        self.work = Path(tempfile.mkdtemp(prefix="dotty-alloc-"))
        self.display = f":{150 + os.getpid() % 100}"
        self.port = free_port()
        self.xvfb = subprocess.Popen(["Xvfb", self.display, "-screen", "0", "1600x1000x24"],
                                     stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        time.sleep(0.8)
        config = self.work / "config"
        config.mkdir()
        if lua:
            (config / "config.lua").write_text(LUA_CONFIG)
        env = dict(os.environ, DISPLAY=self.display, HOME=str(self.work), DOTTY_CONFIG_HOME=str(config),
                   DOTTY_TEST_PORT=str(self.port), DOTTY_SHELL="/bin/sh", ENV="", PS1="$ ")
        env.pop("WAYLAND_DISPLAY", None)
        if trace is not None and every_object:
            # GCSampledObjectAllocationHigh only takes effect when enabled at startup, so
            # configure EventPipe through the environment instead of attaching later.
            # GCBulkType (0x80000) supplies type names. The file is flushed at exit.
            env.update(DOTNET_EnableEventPipe="1", DOTNET_EventPipeOutputPath=str(trace),
                       DOTNET_EventPipeConfig="Microsoft-Windows-DotNETRuntime:0x280001:5",
                       DOTNET_EventPipeCircularMB="2048")
        self.log = open(self.work / "app.log", "w")
        self.launched = time.monotonic()
        self.proc = subprocess.Popen([app], env=env, stdout=self.log, stderr=subprocess.STDOUT)
        self._wait_ready()
        self.window = self._find_window()
        self.tracer = None
        if trace is not None and not every_object:
            # gc-verbose samples one AllocationTick per ~100 KB of allocation.
            self.tracer = subprocess.Popen(["dotnet-trace", "collect", "--process-id", str(self.proc.pid),
                                            "--profile", "gc-verbose", "--output", str(trace)],
                                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            time.sleep(3)

    def _wait_ready(self) -> None:
        for _ in range(120):
            try:
                self.send("WAIT_FOR_IDLE")
                return
            except OSError:
                if self.proc.poll() is not None:
                    raise RuntimeError(f"dotty exited early; see {self.work / 'app.log'}")
                time.sleep(0.25)
        raise RuntimeError("dotty control port never became ready")

    def _find_window(self) -> str:
        for _ in range(40):
            out = subprocess.run(["xdotool", "search", "--pid", str(self.proc.pid)],
                                 env=self.xenv, capture_output=True, text=True).stdout.split()
            if out:
                wid = out[-1]
                self.xdo("windowfocus", "--sync", wid)
                return wid
            time.sleep(0.25)
        raise RuntimeError("could not find the Dotty X window")

    @property
    def xenv(self) -> dict:
        return dict(os.environ, DISPLAY=self.display)

    def xdo(self, *args: str) -> None:
        subprocess.run(["xdotool", *args], env=self.xenv, check=True, capture_output=True)

    def send(self, command: str) -> str:
        with socket.create_connection(("127.0.0.1", self.port), timeout=10) as c:
            c.sendall((command + "\n").encode())
            data = b""
            while chunk := c.recv(65536):
                data += chunk
        return data.decode().strip()

    def allocated(self) -> int:
        return json.loads(self.send("ALLOC"))["totalAllocatedBytes"]

    def idle(self) -> None:
        self.send("WAIT_FOR_IDLE")

    def close(self) -> None:
        if self.tracer is not None:
            self.tracer.send_signal(2)
            try:
                self.tracer.wait(timeout=30)
            except subprocess.TimeoutExpired:
                self.tracer.kill()
        try:
            self.send("SHUTDOWN")
            self.proc.wait(timeout=15)
        except Exception:
            self.proc.kill()
        self.xvfb.terminate()
        self.log.close()


def scenarios(d: Dotty, repeat: int = 1):
    w = d.window

    def idle():
        time.sleep(5)

    def typing():
        d.xdo("type", "--window", w, "--delay", "15", "the quick brown fox jumps over the lazy dog " * 3)
        d.xdo("key", "--window", w, "ctrl+u")

    def bulk_output():
        # Real keystrokes, not the TYPE control command, so the test transport is not measured.
        d.xdo("type", "--window", w, "--delay", "10", "seq 1 100000")
        d.xdo("key", "--window", w, "Return")
        time.sleep(4)

    def scroll():
        for _ in range(40):
            d.xdo("mousemove", "--window", w, "400", "400", "click", "4")
        for _ in range(40):
            d.xdo("mousemove", "--window", w, "400", "400", "click", "5")

    def hover():
        for i in range(120):
            d.xdo("mousemove", "--window", w, str(20 + (i * 13) % 900), str(10 + (i * 7) % 500))

    def selection():
        d.xdo("mousemove", "--window", w, "60", "120", "mousedown", "1")
        for i in range(40):
            d.xdo("mousemove", "--window", w, str(60 + i * 12), str(120 + (i % 10) * 18))
        d.xdo("mouseup", "1")

    def tab_switch():
        for _ in range(60 * repeat):
            d.xdo("key", "--window", w, "ctrl+Tab")
            time.sleep(0.02)

    def resize():
        for i in range(8 * repeat):
            d.xdo("windowsize", w, str(900 + (i % 2) * 200), str(600 + (i % 2) * 120))
            time.sleep(0.25)

    return [("idle_5s", idle), ("typing_132_chars", typing), ("bulk_output_100k_lines", bulk_output),
            ("scroll_80_wheel", scroll), ("hover_120_moves", hover), ("selection_drag", selection),
            ("tab_switch_60", tab_switch), ("resize_8", resize)]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--app", default="src/Dotty/bin/Release/net10.0/dotty")
    parser.add_argument("--no-lua", action="store_true", help="run without the Lua title/status/event hooks")
    parser.add_argument("--trace", type=Path, help="also record a gc-verbose nettrace to this path")
    parser.add_argument("--repeat", type=int, default=1,
                        help="multiply tab-switch/resize iterations (for allocation-tick attribution)")
    parser.add_argument("--every-object", action="store_true",
                        help="with --trace, record every sampled object allocation instead of 100 KB ticks")
    parser.add_argument("--only", nargs="*", help="scenario names to run")
    parser.add_argument("--json", type=Path, help="write results as JSON")
    args = parser.parse_args()
    for tool in ("Xvfb", "xdotool"):
        if shutil.which(tool) is None:
            print(f"{tool} is required", file=sys.stderr)
            return 2

    d = Dotty(os.path.abspath(args.app), lua=not args.no_lua, trace=args.trace, every_object=args.every_object)
    results = {}
    try:
        d.xdo("key", "--window", d.window, "ctrl+shift+t")  # second tab for tab switching
        time.sleep(1.5)
        d.idle()
        # Transport calibration: the measurement itself does one WAIT_FOR_IDLE and one
        # ALLOC round trip between the two snapshots, so calibrate that exact sequence
        # with no scenario in between.
        samples = []
        for _ in range(7):
            a = d.allocated()
            d.idle()
            time.sleep(0.3)
            b = d.allocated()
            samples.append(b - a)
        overhead = sorted(samples)[len(samples) // 2]
        for name, run in scenarios(d, args.repeat):
            if args.only and name not in args.only:
                continue
            run()  # warm-up pass: first-use caches, atlas glyphs, JIT tiers
            d.idle()
            time.sleep(0.5)
            before = d.allocated()
            window_start = time.monotonic() - d.launched
            run()
            d.idle()
            time.sleep(0.3)
            window_end = time.monotonic() - d.launched
            after = d.allocated()
            results[name] = max(0, after - before - overhead)
            print(f"{name:28} {results[name]:>12,} bytes   window {window_start:.2f}s..{window_end:.2f}s",
                  flush=True)
    finally:
        d.close()
    print(f"(transport overhead subtracted per scenario: {overhead:,} bytes; app log: {d.work / 'app.log'})")
    if args.json:
        args.json.write_text(json.dumps({"overhead": overhead, "scenarios": results}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
