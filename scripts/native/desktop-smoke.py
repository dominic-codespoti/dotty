#!/usr/bin/env python3
"""Opt-in live desktop smoke driven through Dotty's loopback control socket."""
import argparse
import json
import os
import pathlib
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import unicodedata


def output_line_matches(line, marker):
    # DUMP serializes the blank continuation cells used by fullwidth glyphs.
    expected = "".join(char + (" " if unicodedata.east_asian_width(char) in ("W", "F") else "") for char in marker)
    return line.rstrip() == expected


def windows_console_evidence(pid):
    # Probe from a detached process so attaching never disturbs the harness console.
    probe = """
import ctypes, json, sys
api = ctypes.WinDLL("kernel32", use_last_error=True)
api.AttachConsole.argtypes = [ctypes.c_uint32]
api.AttachConsole.restype = ctypes.c_int
api.GetConsoleProcessList.argtypes = [ctypes.POINTER(ctypes.c_uint32), ctypes.c_uint32]
api.GetConsoleProcessList.restype = ctypes.c_uint32
api.FreeConsole.argtypes = []
api.FreeConsole.restype = ctypes.c_int
attached = bool(api.AttachConsole(int(sys.argv[1])))
error = 0 if attached else ctypes.get_last_error()
processes = []
try:
    if attached:
        capacity = 4
        while True:
            ids = (ctypes.c_uint32 * capacity)()
            count = api.GetConsoleProcessList(ids, capacity)
            if not count:
                raise ctypes.WinError(ctypes.get_last_error())
            if count <= capacity:
                processes = list(ids[:count])
                break
            capacity = count
finally:
    if attached and not api.FreeConsole():
        raise ctypes.WinError(ctypes.get_last_error())
print(json.dumps({"attached": attached, "error": error, "processes": processes}))
"""
    result = subprocess.run([sys.executable, "-c", probe, str(pid)], capture_output=True, text=True, check=True, timeout=5, creationflags=subprocess.DETACHED_PROCESS)
    return json.loads(result.stdout)

def x11_window_evidence(pid):
    xdotool = shutil.which("xdotool")
    xprop = shutil.which("xprop")
    if not xdotool or not xprop:
        return {"xdotoolAvailable": bool(xdotool), "xpropAvailable": bool(xprop)}

    result = subprocess.run([xdotool, "search", "--onlyvisible", "--pid", str(pid)], capture_output=True, text=True)
    windows = []
    for window_id in result.stdout.split():
        geometry = subprocess.run([xdotool, "getwindowgeometry", "--shell", window_id], capture_output=True, text=True)
        properties = subprocess.run([xprop, "-id", window_id, "_NET_WM_STATE", "WM_STATE", "WM_NORMAL_HINTS", "_NET_WM_PID"], capture_output=True, text=True)
        windows.append({"xid": window_id, "geometry": geometry.stdout.strip(), "properties": properties.stdout.strip()})
    return {"searchExit": result.returncode, "searchError": result.stderr.strip(), "windows": windows}

def fail(message, log_path):
    try:
        log = log_path.read_text(encoding="utf-8", errors="replace")
    except OSError:
        log = "(no host log available)"
    raise RuntimeError(f"{message}\n--- Dotty log ---\n{log}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--executable", required=True, help="published Dotty executable (.exe on Windows)")
    parser.add_argument("--backend", required=True, choices=("windows", "macos", "x11", "wayland"))
    parser.add_argument("--startup-timeout", type=float, default=30)
    parser.add_argument("--windows-console", choices=("owned", "inherited"), default="owned", help="Windows launch console ownership to verify")
    args = parser.parse_args()
    executable = pathlib.Path(args.executable).resolve()
    if not executable.is_file():
        parser.error(f"Dotty executable does not exist: {executable}")

    with tempfile.TemporaryDirectory(prefix="dotty-native-smoke-") as temporary:
        root = pathlib.Path(temporary)
        home = root / "home"
        config = root / "config"
        home.mkdir()
        config.mkdir()
        log_path = root / "dotty.log"
        env = os.environ.copy()
        env.update({
            "HOME": str(home),
            "DOTNET_CLI_HOME": str(home),
            "XDG_CONFIG_HOME": str(config),
            "DOTTY_CONFIG_HOME": str(config / "dotty"),
            "DOTTY_TEST_PORT": "0",
        })
        env.pop("GLFW_PLATFORM", None)
        if args.backend == "wayland":
            if not env.get("WAYLAND_DISPLAY") or not env.get("XDG_RUNTIME_DIR"):
                parser.error("Wayland lane requires WAYLAND_DISPLAY and XDG_RUNTIME_DIR from a live user session")
            env.pop("DISPLAY", None)
            env["XDG_SESSION_TYPE"] = "wayland"
        elif args.backend == "x11":
            if not env.get("DISPLAY"):
                parser.error("X11 lane requires DISPLAY (use xvfb-run or an X11 desktop)")
            env.pop("WAYLAND_DISPLAY", None)
            env["XDG_SESSION_TYPE"] = "x11"
        elif args.backend == "macos":
            if sys.platform != "darwin":
                parser.error("macOS lane must run on macOS with an active WindowServer session")
        elif args.backend == "windows":
            if os.name != "nt":
                parser.error("Windows lane must run on an interactive Windows desktop")

        creation_flags = subprocess.CREATE_NEW_CONSOLE if args.backend == "windows" and args.windows_console == "owned" else 0
        with log_path.open("w", encoding="utf-8") as log:
            process = subprocess.Popen([str(executable)], cwd=str(executable.parent), env=env, stdout=log, stderr=subprocess.STDOUT, creationflags=creation_flags)
            port = None
            try:
                deadline = time.monotonic() + args.startup_timeout
                while time.monotonic() < deadline:
                    if process.poll() is not None:
                        fail(f"Dotty exited before opening its desktop control socket (exit {process.returncode})", log_path)
                    contents = log_path.read_text(encoding="utf-8", errors="replace")
                    for line in contents.splitlines():
                        if line.startswith("DOTTY_TEST_PORT="):
                            port = int(line.partition("=")[2])
                            break
                    if port is not None:
                        break
                    time.sleep(0.1)
                if port is None:
                    fail(f"No DOTTY_TEST_PORT announcement within {args.startup_timeout}s", log_path)
                console_evidence = windows_console_evidence(process.pid) if args.backend == "windows" else None
                if console_evidence is not None:
                    if args.windows_console == "owned":
                        if console_evidence["attached"] or console_evidence["error"] != 6:
                            fail(f"GUI launch retained its extra console: {console_evidence!r}", log_path)
                    elif not console_evidence["attached"] or process.pid not in console_evidence["processes"] or os.getpid() not in console_evidence["processes"]:
                        fail(f"GUI launch did not preserve its inherited console: {console_evidence!r}", log_path)
                hyprctl = shutil.which("hyprctl") if os.environ.get("HYPRLAND_INSTANCE_SIGNATURE") else None
                hypr_client = None
                if hyprctl:
                    def find_hyprland_client():
                        clients = subprocess.run([hyprctl, "-j", "clients"], check=True, capture_output=True, text=True)
                        return next((client for client in json.loads(clients.stdout) if int(client.get("pid", -1)) == process.pid), None)

                    deadline = time.monotonic() + 10
                    while time.monotonic() < deadline:
                        hypr_client = find_hyprland_client()
                        if hypr_client is not None:
                            break
                        time.sleep(0.1)
                    if hypr_client is None:
                        fail(f"Hyprland did not report a window for Dotty PID {process.pid}", log_path)
                    expected_xwayland = args.backend == "x11"
                    if hypr_client.get("xwayland") is not expected_xwayland:
                        fail(f"Hyprland reports unexpected XWayland state for {args.backend}: {hypr_client!r}", log_path)

                    def hypr_dispatch(*arguments):
                        result = subprocess.run([hyprctl, "dispatch", *arguments], check=True, capture_output=True, text=True)
                        if result.stdout.strip().lower() != "ok":
                            fail(f"Hyprland dispatch {arguments!r} failed: {result.stdout.strip()!r}", log_path)

                    address = str(hypr_client["address"])
                    if not hypr_client.get("floating"):
                        hypr_dispatch("togglefloating", f"address:{address}")
                        deadline = time.monotonic() + 5
                        while time.monotonic() < deadline:
                            hypr_client = find_hyprland_client()
                            if hypr_client is not None and hypr_client.get("floating") is True:
                                break
                            time.sleep(0.1)
                        if hypr_client is None or hypr_client.get("floating") is not True:
                            fail(f"Hyprland did not float only Dotty PID {process.pid}: {hypr_client!r}", log_path)
                    hypr_dispatch("focuswindow", f"address:{address}")
                    deadline = time.monotonic() + 5
                    while time.monotonic() < deadline:
                        hypr_active_window = json.loads(subprocess.run([hyprctl, "-j", "activewindow"], check=True, capture_output=True, text=True).stdout)
                        if int(hypr_active_window.get("pid", -1)) == process.pid and hypr_active_window.get("address") == address:
                            break
                        time.sleep(0.1)
                    else:
                        fail(f"Hyprland did not focus Dotty PID {process.pid}: {hypr_active_window!r}", log_path)

                def command(value):
                    with socket.create_connection(("127.0.0.1", port), timeout=5) as connection:
                        connection.settimeout(5)
                        connection.sendall((value + "\n").encode("utf-8"))
                        response = bytearray()
                        while True:
                            chunk = connection.recv(65536)
                            if not chunk:
                                break
                            response.extend(chunk)
                    return response.decode("utf-8", errors="strict").rstrip("\r\n")

                def expect_ok(value):
                    response = command(value)
                    if response != "OK":
                        fail(f"Control command {value!r} returned {response!r}", log_path)

                initial_stats = json.loads(command("STATS"))
                if initial_stats.get("windowBackend") != args.backend:
                    actual_backend = initial_stats.get("windowBackend")
                    fail(f"GLFW selected window backend {actual_backend!r}, expected {args.backend!r}: {initial_stats!r}", log_path)

                def wait_for_output_line(marker):
                    deadline = time.monotonic() + 10
                    last_dump = ""
                    while time.monotonic() < deadline:
                        last_dump = command("DUMP")
                        if any(output_line_matches(line, marker) for line in last_dump.splitlines()[1:]):
                            return last_dump
                        if process.poll() is not None:
                            fail(f"Dotty exited before child PTY output appeared (exit {process.returncode})", log_path)
                        time.sleep(0.1)
                    fail(f"Child PTY did not render standalone output line {marker!r}; final DUMP:\n{last_dump}", log_path)

                expect_ok("ACTION:NewTab")
                tabs = json.loads(command("STATS"))
                if int(tabs.get("tabs", 0)) != int(initial_stats.get("tabs", 0)) + 1:
                    fail(f"NewTab did not create exactly one tab: before={initial_stats!r}, after={tabs!r}", log_path)
                tab_marker = "DOTTY_SMOKE_TAB_Ω_漢字_" + str(os.getpid())
                expect_ok("TYPE:echo " + tab_marker)
                expect_ok("KEY:enter")
                tab_dump = wait_for_output_line(tab_marker)

                for columns, rows in ((63, 17), (91, 31)):
                    expect_ok(f"RESIZE:{columns}:{rows}")
                    deadline = time.monotonic() + 10
                    resized_state = None
                    while time.monotonic() < deadline:
                        resized_state = json.loads(command("GET_STATE"))
                        if int(resized_state.get("rows", 0)) == rows and int(resized_state.get("cols", 0)) == columns:
                            break
                        if process.poll() is not None:
                            fail(f"Dotty exited before grid resize appeared (exit {process.returncode}): {resized_state!r}", log_path)
                        time.sleep(0.1)
                    if resized_state is None or int(resized_state.get("rows", 0)) != rows or int(resized_state.get("cols", 0)) != columns:
                        actual_window = find_hyprland_client() if hyprctl else None
                        x11_windows = x11_window_evidence(process.pid) if args.backend == "x11" else None
                        fail(f"Terminal state did not adopt requested {columns}x{rows} resize: state={resized_state!r}, Hyprland client={actual_window!r}, X11 windows={x11_windows!r}", log_path)

                expect_ok("ACTION:SplitVertical")
                state = json.loads(command("GET_STATE"))
                if int(state.get("paneCount", 0)) != 2:
                    fail(f"SplitVertical did not create a second pane: {state!r}", log_path)
                pane_marker = "DOTTY_SMOKE_PANE_Ω_漢字_" + str(os.getpid())
                expect_ok("TYPE:echo " + pane_marker)
                expect_ok("KEY:enter")
                pane_dump = wait_for_output_line(pane_marker)
                after = json.loads(command("GET_STATE"))
                working_directory = after.get("workingDirectory", "")
                if not working_directory or not pathlib.Path(working_directory).is_absolute() or not pathlib.Path(working_directory).is_dir():
                    fail(f"Focused terminal working directory is not an existing absolute path: {after!r}", log_path)

                stats = json.loads(command("STATS"))
                if int(stats.get("presentCount", 0)) <= int(initial_stats.get("presentCount", 0)):
                    fail(f"No frame was presented after the terminal actions: before={initial_stats!r}, after={stats!r}", log_path)
                if int(stats.get("tabs", 0)) != int(initial_stats.get("tabs", 0)) + 1:
                    fail(f"Desktop tab count changed unexpectedly: {stats!r}", log_path)
                if hyprctl:
                    hypr_client = find_hyprland_client()
                    hypr_active_window = json.loads(subprocess.run([hyprctl, "-j", "activewindow"], check=True, capture_output=True, text=True).stdout)
                    if hypr_client is None or hypr_client.get("floating") is not True or int(hypr_active_window.get("pid", -1)) != process.pid:
                        fail(f"Dotty's PID-scoped Hyprland client lost floating/focus state: client={hypr_client!r}, active={hypr_active_window!r}", log_path)
                hyprland_window = None if hypr_client is None else {"xwayland": hypr_client["xwayland"], "floating": hypr_client["floating"], "active": int(hypr_active_window.get("pid", -1)) == process.pid, "size": hypr_client["size"]}
                print(json.dumps({"result": "passed", "backend": stats["windowBackend"], "grid": "91x31", "tabs": stats["tabs"], "panes": after["paneCount"], "workingDirectory": working_directory, "newTabOutput": tab_marker, "splitPaneOutput": pane_marker, "visibleGrid": pane_dump.splitlines()[0], "presentCount": stats["presentCount"], "hyprlandWindow": hyprland_window, "console": console_evidence, "platform": sys.platform}, ensure_ascii=False))
            finally:
                if process.poll() is None:
                    try:
                        command("SHUTDOWN")
                    except (OSError, RuntimeError, socket.timeout):
                        pass
                    try:
                        process.wait(timeout=8)
                    except subprocess.TimeoutExpired:
                        process.terminate()
                        try:
                            process.wait(timeout=3)
                        except subprocess.TimeoutExpired:
                            process.kill()
                            process.wait()


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(f"native desktop smoke failed: {error}", file=sys.stderr)
        sys.exit(1)
