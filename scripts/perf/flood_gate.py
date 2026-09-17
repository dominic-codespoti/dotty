#!/usr/bin/env python3
"""Sustained-flood presentation-gate measurement (GPURenderingPlan Phase 0)."""
import argparse
import json
import os
import socket
import subprocess
import sys
import time


def send_command(port, command, timeout=5.0):
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
        return b"".join(chunks).decode("utf-8-sig", "ignore").strip()


def wait_ready(port, timeout):
    deadline = time.perf_counter() + timeout
    while time.perf_counter() < deadline:
        try:
            return send_command(port, "PREV_TAB", timeout=0.5)
        except OSError:
            time.sleep(0.2)
    raise TimeoutError(f"Timed out waiting for port {port}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--app", required=True)
    ap.add_argument("--flood-shell", required=True)
    ap.add_argument("--workdir", default="/home/dom/projects/dotnet-term")
    ap.add_argument("--port", type=int, default=9978)
    ap.add_argument("--window", type=float, default=12.0)
    ap.add_argument("--log", default="/tmp/dotty-flood.log")
    ap.add_argument("--env", action="append", default=[])
    ap.add_argument("--capture", action="store_true")
    args = ap.parse_args()

    env = os.environ.copy()
    env["SHELL"] = args.flood_shell
    env["DOTTY_TEST_PORT"] = str(args.port)
    env["DOTTY_SKIP_CONFIG_COMPILE"] = "1"
    for pair in args.env:
        if "=" in pair:
            k, v = pair.split("=", 1)
            if v == "":
                env.pop(k, None)
            else:
                env[k] = v

    with open(args.log, "wb") as log_file:
        proc = subprocess.Popen([args.app], cwd=args.workdir, env=env,
                                stdout=log_file, stderr=subprocess.STDOUT)
        try:
            started = time.perf_counter()
            ready = wait_ready(args.port, 30.0)
            startup_ms = (time.perf_counter() - started) * 1000.0
            if ready != "OK":
                raise RuntimeError(f"Unexpected ready response: {ready!r}")

            send_command(args.port, "PERF:START")
            time.sleep(args.window)
            if args.capture:
                send_command(args.port, "CAPTURE_CANVAS", timeout=15)
            perf = json.loads(send_command(args.port, "PERF:GET"))
            print(json.dumps({"startup_ms": round(startup_ms, 1), "window_s": args.window, "telemetry": perf}))
        finally:
            proc.terminate()
            try:
                proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                proc.kill()


if __name__ == "__main__":
    sys.exit(main())
