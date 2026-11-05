#!/usr/bin/env python3
"""Run pinned esctest2 against Dotty's headless and native GUI terminal paths.

Classes run in isolated conformance-host processes except four selection/window
cases, which run sequentially as the child command of Dotty under Xvfb/Openbox.
Outcomes come from esctest's logfile (not its exit status), are merged and
compared with tests/esctest/expected-failures.txt (see esctest_results.py).

Exit codes: 0 ok; 1 gate failure (regression / unexpected pass / stale entry /
not-run); 2 usage or setup error; 3 harness fault.

Gating: the gate is armed only once tests/esctest/expected-failures.txt carries
the '# baseline-captured: <commit>' marker written by --update-expectations.
Until then the run still executes and reports everything, but gate failures
(exit 1) are downgraded to exit 0 and the output says "NOT GATING". Use
--gate on|off to override.

The host receives an explicit --working-directory for the esctest suite;
setting subprocess cwd alone does not set the working directory of its PTY child.
"""

from __future__ import annotations

import argparse
import concurrent.futures
import difflib
import json
import os
import platform
import re
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import socket
from pathlib import Path
from typing import Callable, Sequence

sys.path.insert(0, str(Path(__file__).resolve().parent))

import esctest_results as er  # noqa: E402

REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_CONFIG = REPO_ROOT / "tests/esctest/esctest.json"
DEFAULT_EXPECTATIONS = REPO_ROOT / "tests/esctest/expected-failures.txt"
DEFAULT_ESCTEST_DIR = REPO_ROOT / ".esctest/esctest2"
DEFAULT_OUT = REPO_ROOT / "artifacts/esctest"
HOST_RELATIVE = Path("tests/Dotty.Conformance.Host/bin/Release/net11.0")
GUI_TESTS = (
    "ManipulateSelectionDataTests.test_ManipulateSelectionData_default",
    "XtermWinopsTests.test_XtermWinops_IconifyDeiconfiy",
    "XtermWinopsTests.test_XtermWinops_MoveToXY",
    "XtermWinopsTests.test_XtermWinops_MoveToXY_Defaults",
)
GUI_TOOLS = ("xvfb-run", "openbox", "xprop", "xauth", "xdotool")

def require_gui_prerequisites(config: dict, root: Path) -> tuple[Path, dict[str, str]]:
    """Resolve the real product binary and required X11 session tools."""
    gui = config.get("gui")
    if not isinstance(gui, dict) or not isinstance(gui.get("executable"), str):
        raise SetupError("config.gui.executable must name the Dotty GUI executable")
    executable = (root / gui["executable"]).resolve()
    if not executable.is_file():
        raise SetupError(f"Dotty GUI executable does not exist: {executable}")
    tools = {name: shutil.which(name) for name in GUI_TOOLS}
    missing = [name for name, path in tools.items() if path is None]
    if missing:
        raise SetupError(f"GUI cases require native X11 tools: {', '.join(missing)}")
    if os.name != "posix" or sys.platform != "linux":
        raise SetupError("GUI conformance cases require Linux/X11 with Xvfb and Openbox")
    return executable, {name: path for name, path in tools.items() if path is not None}
CANARY_CLASS = "CUUTests"

EXIT_OK, EXIT_GATE, EXIT_USAGE, EXIT_HARNESS = 0, 1, 2, 3

# Signature of the injectable process runner: (argv, cwd, timeout_seconds, host_log_path) -> exit code
# (124 on timeout). Unit tests replace it; no dotnet is needed there.
CommandRunner = Callable[[Sequence[str], Path, float, Path], int]


class SetupError(Exception):
    pass


# --------------------------------------------------------------------------- config / esctest checkout

def load_config(path: Path) -> dict:
    try:
        config = json.loads(Path(path).read_text(encoding="utf-8"))
    except (OSError, ValueError) as ex:
        raise SetupError(f"cannot read config {path}: {ex}") from ex
    for key in ("repository", "commit", "rows", "columns", "esctestArgs", "perClassTimeoutSeconds"):
        if key not in config:
            raise SetupError(f"{path}: missing key {key!r}")
    if not re.fullmatch(r"[0-9a-f]{40}", str(config["commit"])):
        raise SetupError(f"{path}: commit must be a full 40-hex SHA")
    config.setdefault("hostArgs", [])
    config.setdefault("minPassed", 0)
    return config


def _git(args: Sequence[str], cwd: Path) -> str:
    proc = subprocess.run(["git", *args], cwd=cwd, capture_output=True, text=True)
    if proc.returncode != 0:
        raise SetupError(f"git {' '.join(args)} failed: {proc.stderr.strip()}")
    return proc.stdout.strip()


def ensure_esctest(esctest_dir: Path, config: dict) -> str:
    """Make sure esctest_dir holds the pinned commit (fetching it if absent). Returns the commit."""
    pinned = config["commit"]
    if not (esctest_dir / "esctest" / "esctest.py").exists():
        esctest_dir.mkdir(parents=True, exist_ok=True)
        if not (esctest_dir / ".git").exists():
            _git(["init", "-q"], esctest_dir)
        _git(["fetch", "--depth", "1", config["repository"], pinned], esctest_dir)
        _git(["checkout", "-q", "--detach", "FETCH_HEAD"], esctest_dir)
    head = _git(["rev-parse", "HEAD"], esctest_dir)
    if head != pinned:
        raise SetupError(f"{esctest_dir} is at {head}, expected pinned commit {pinned}; delete it to refetch")
    return head


_CLASS_IMPORT_RE = re.compile(r"^from tests\.\w+ import (\w+)", re.MULTILINE)


def discover_classes(init_text: str) -> list[str]:
    classes = _CLASS_IMPORT_RE.findall(init_text)
    if not classes:
        raise SetupError("no test classes found in esctest/tests/__init__.py")
    return classes


def build_include_regex(klass: str, skips: Sequence[str] = (), user_include: str | None = None) -> str:
    """Regex for esctest's --include (re.search against 'Class.test_name')."""
    pattern = "^" + re.escape(klass) + r"\."
    names = sorted(s.split(".", 1)[1] for s in skips if s.split(".", 1)[0] == klass)
    if names:
        pattern += "(?!(?:" + "|".join(re.escape(n) for n in names) + ")$)"
    if user_include:
        pattern += "(?=.*(?:" + user_include + "))"
    return pattern


def select_gui_tests(classes: Sequence[str], user_include: str | None) -> list[str]:
    """Apply the same class/test filter to the fixed real-GUI case set."""
    return [
        name for name in GUI_TESTS
        if er.class_of(name) in classes and (not user_include or re.search(user_include, name))
    ]


def default_host(root: Path = REPO_ROOT) -> list[str]:
    base = root / HOST_RELATIVE
    exe = base / "dotty-conformance-host"
    if exe.exists():
        return [str(exe)]
    dll = base / "dotty-conformance-host.dll"
    return ["dotnet", str(dll)]


def build_class_command(
    host: Sequence[str], config: dict, cwd: Path, klass: str, log_path: Path, transcript: Path | None,
    skips: Sequence[str], user_include: str | None,
) -> list[str]:
    cmd = list(host)
    cmd += ["--rows", str(config["rows"]), "--columns", str(config["columns"]),
            "--timeout-seconds", str(config["perClassTimeoutSeconds"]),
            "--working-directory", str(cwd)]
    cmd += list(config.get("hostArgs", []))
    if transcript is not None:
        cmd += ["--transcript", str(transcript)]
    cmd += ["--", sys.executable, "esctest.py", "--logfile", str(log_path),
            "--include", build_include_regex(klass, skips, user_include)]
    cmd += list(config["esctestArgs"])
    return cmd


# --------------------------------------------------------------------------- process execution

def run_command(argv: Sequence[str], cwd: Path, timeout: float, host_log: Path) -> int:
    host_log.parent.mkdir(parents=True, exist_ok=True)
    with open(host_log, "wb") as sink:
        proc = subprocess.Popen(list(argv), cwd=cwd, stdout=sink, stderr=subprocess.STDOUT,
                                stdin=subprocess.DEVNULL, start_new_session=True)
        try:
            return proc.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            try:
                os.killpg(proc.pid, signal.SIGKILL)
            except (ProcessLookupError, PermissionError):
                proc.kill()
            proc.wait()
            return 124


def _desktop_control(port: int, command: str, timeout: float = 3) -> str:
    with socket.create_connection(("127.0.0.1", port), timeout=timeout) as connection:
        connection.settimeout(timeout)
        connection.sendall((command + "\n").encode("utf-8"))
        response = bytearray()
        while not response.endswith(b"\n"):
            chunk = connection.recv(4096)
            if not chunk:
                break
            response.extend(chunk)
        if not response:
            raise OSError(f"no response to Dotty control command {command}")
        return response.decode("utf-8", errors="replace").strip()


def _desktop_shutdown(port: int, timeout: float = 3) -> None:
    with socket.create_connection(("127.0.0.1", port), timeout=timeout) as connection:
        connection.sendall(b"SHUTDOWN\n")

def _native_window_focused(display: str, authority: str, tools: dict[str, str]) -> bool:
    """Require the active Dotty window to be mapped and natively focused on isolated X11."""
    env = os.environ.copy()
    env["DISPLAY"] = display
    env["XAUTHORITY"] = authority
    env.pop("WAYLAND_DISPLAY", None)
    try:
        focus = subprocess.run(
            [tools["xdotool"], "getwindowfocus"], env=env, capture_output=True,
            text=True, timeout=2, check=True,
        ).stdout.strip()
        mapped_text = subprocess.run(
            [tools["xdotool"], "search", "--onlyvisible", "--name", ".*"],
            env=env, capture_output=True, text=True, timeout=2, check=True,
        ).stdout
        mapped = {int(value, 0) for value in mapped_text.split()}
        window_id = int(focus, 0)
        if window_id not in mapped:
            return False
        active_text = subprocess.run(
            [tools["xprop"], "-root", "_NET_ACTIVE_WINDOW"], env=env,
            capture_output=True, text=True, timeout=2, check=True,
        ).stdout
        match = re.search(r"window id # (0x[0-9a-fA-F]+)", active_text)
        return match is not None and int(match.group(1), 0) == window_id
    except (OSError, subprocess.SubprocessError, ValueError):
        return False


def run_gui_command(
    argv: Sequence[str], cwd: Path, timeout: float, host_log: Path,
    child_status: Path, child_release: Path, display_file: Path, authority_file: Path,
    result_log: Path, columns: int, rows: int, tools: dict[str, str],
) -> int:
    """Wait for a mapped, focused, correctly sized Dotty window before starting esctest."""
    host_log.parent.mkdir(parents=True, exist_ok=True)
    deadline = time.monotonic() + timeout
    startup_deadline = min(deadline, time.monotonic() + 15)
    diagnostics: list[str] = []
    child_exit: int | None = None
    shutdown_ok = False
    state_ok = False
    shutdown_timed_out = False
    natural_exit = False
    state_checked = False
    barrier_released = False
    with open(host_log, "wb") as sink:
        proc = subprocess.Popen(list(argv), cwd=cwd, stdout=sink, stderr=subprocess.STDOUT,
                                stdin=subprocess.DEVNULL, start_new_session=True)
        port: int | None = None
        while True:
            if host_log.exists():
                for line in host_log.read_text(encoding="utf-8", errors="replace").splitlines():
                    match = re.fullmatch(r"DOTTY_TEST_PORT=(\d+)", line)
                    if match:
                        port = int(match.group(1))
                        break
            if port is not None and display_file.exists() and not barrier_released:
                try:
                    if not state_checked:
                        state = json.loads(_desktop_control(port, "GET_STATE"))
                        diagnostics.append("DOTTY_GUI_STATE=" + json.dumps(state, separators=(",", ":")))
                        state_ok = state.get("rows") == rows and state.get("cols") == columns
                        if not state_ok:
                            diagnostics.append(f"DOTTY_GUI_STATE_ERROR=expected {rows}x{columns}")
                            raise RuntimeError("Dotty window grid does not match configured rows and columns")
                        state_checked = True
                    display = display_file.read_text(encoding="ascii").strip()
                    authority = authority_file.read_text(encoding="ascii").strip()
                    if display and authority and _native_window_focused(display, authority, tools):
                        diagnostics.append("DOTTY_X11_WINDOW_FOCUSED=1")
                        child_release.touch()
                        barrier_released = True
                except (OSError, ValueError, RuntimeError) as ex:
                    if isinstance(ex, RuntimeError):
                        diagnostics.append(f"GUI_START_BARRIER_ERROR={ex}")
                        try:
                            os.killpg(proc.pid, signal.SIGKILL)
                        except (ProcessLookupError, PermissionError):
                            proc.kill()
                        proc.wait()
                        with open(host_log, "a", encoding="utf-8") as log:
                            log.write("\n".join(diagnostics) + "\n")
                        return 125
            if child_status.exists() and port is not None and barrier_released:
                try:
                    child_exit = int(child_status.read_text(encoding="ascii").strip())
                except (OSError, ValueError):
                    diagnostics.append("ESCTEST_CHILD_EXIT=invalid")
                else:
                    diagnostics.append(f"ESCTEST_CHILD_EXIT={child_exit}")
                result_text = result_log.read_text(encoding="utf-8", errors="replace") if result_log.exists() else ""
                parsed_results, _ = er.parse_log(result_text)
                diagnostics.append("ESCTEST_RESULTS=" + json.dumps([
                    {"name": result.name, "outcome": result.outcome, "message": result.message}
                    for result in parsed_results
                ], separators=(",", ":")))
                try:
                    _desktop_shutdown(port)
                    proc.wait(timeout=max(0.1, deadline - time.monotonic()))
                    shutdown_ok = True
                    diagnostics.append("DOTTY_SHUTDOWN=OK")
                except (OSError, subprocess.TimeoutExpired) as ex:
                    if proc.poll() is None:
                        try:
                            proc.wait(timeout=min(2.0, max(0.1, deadline - time.monotonic())))
                        except subprocess.TimeoutExpired:
                            pass
                    if proc.poll() is not None and child_exit == proc.returncode:
                        natural_exit = True
                        shutdown_ok = True
                        diagnostics.append(f"DOTTY_SHUTDOWN=NATURAL_EXIT ({ex})")
                    else:
                        shutdown_timed_out = isinstance(ex, subprocess.TimeoutExpired)
                        diagnostics.append(f"DOTTY_SHUTDOWN_ERROR={ex}")
                if not shutdown_ok:
                    try:
                        os.killpg(proc.pid, signal.SIGKILL)
                    except (ProcessLookupError, PermissionError):
                        proc.kill()
                    proc.wait()
                break
            if proc.poll() is not None:
                break
            now = time.monotonic()
            if not barrier_released and now >= startup_deadline:
                diagnostics.append("GUI_START_BARRIER_ERROR=Dotty did not reach configured grid and native focus")
                try:
                    os.killpg(proc.pid, signal.SIGKILL)
                except (ProcessLookupError, PermissionError):
                    proc.kill()
                proc.wait()
                with open(host_log, "a", encoding="utf-8") as log:
                    log.write("\n".join(diagnostics) + "\n")
                return 125
            if now >= deadline:
                try:
                    os.killpg(proc.pid, signal.SIGKILL)
                except (ProcessLookupError, PermissionError):
                    proc.kill()
                proc.wait()
                diagnostics.append("GUI_TIMEOUT=1")
                with open(host_log, "a", encoding="utf-8") as log:
                    log.write("\n".join(diagnostics) + "\n")
                return 124
            time.sleep(0.05)
        code = proc.returncode if proc.returncode is not None else 125
    if child_exit is None:
        diagnostics.append("ESCTEST_CHILD_EXIT=missing")
    if not state_ok:
        diagnostics.append("DOTTY_GUI_STATE_ERROR=GUI state was not verified")
    if not shutdown_ok:
        diagnostics.append("DOTTY_SHUTDOWN_ERROR=GUI shutdown was not confirmed")
    if natural_exit:
        diagnostics.append("DOTTY_NATURAL_EXIT=1")
    with open(host_log, "a", encoding="utf-8") as log:
        log.write("\n".join(diagnostics) + "\n")
    return 124 if shutdown_timed_out else code


def kill_stragglers(esctest_dir: Path) -> None:
    pkill = shutil.which("pkill")
    if pkill:
        subprocess.run([pkill, "-f", str(esctest_dir / "esctest" / "esctest.py")],
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def run_class(
    klass: str, argv: Sequence[str], cwd: Path, timeout: float, out: Path, runner: CommandRunner,
) -> tuple[er.ClassRun, list[er.TestResult]]:
    log_path = out / "logs" / f"{klass}.log"
    log_path.parent.mkdir(parents=True, exist_ok=True)
    if log_path.exists():
        log_path.unlink()
    started = time.monotonic()
    code = runner(argv, cwd, timeout + 30, out / "logs" / f"{klass}.host.txt")
    seconds = time.monotonic() - started
    text = log_path.read_text(encoding="utf-8", errors="replace") if log_path.exists() else ""
    results, saw_summary = er.parse_log(text)
    complete = saw_summary and code not in (124, 125, 130)
    reason = ""
    if code in (124, 125, 130):
        reason = {124: "timeout", 125: "host error", 130: "interrupted"}[code]
    elif not saw_summary:
        reason = "no summary line in log" if text else "no log produced"
    return er.ClassRun(klass, code, seconds, complete, reason), results


def run_gui_test(
    test_name: str, command: Sequence[str], cwd: Path, timeout: float, out: Path,
    runner: CommandRunner, gui_executable: Path, tools: dict[str, str], config: dict,
) -> tuple[er.ClassRun, list[er.TestResult]]:
    """Run one upstream case as Dotty's real GUI child in an isolated X11 session."""
    klass = er.class_of(test_name)
    slug = re.sub(r"[^A-Za-z0-9_.-]", "_", test_name)
    log_path = out / "logs" / "gui" / f"{slug}.log"
    host_log = out / "logs" / "gui" / f"{slug}.host.txt"
    wm_log = out / "logs" / "gui" / f"{slug}.openbox.txt"
    for path in (log_path, host_log, wm_log):
        path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=f"dotty-esctest-{slug}-") as temporary:
        home = Path(temporary) / "home"
        config_home = home / ".config"
        dotty_config = config_home / "dotty"
        child_status = Path(temporary) / "esctest-child.status"
        child_release = Path(temporary) / "esctest-child.release"
        display_file = Path(temporary) / "x-display"
        authority_file = Path(temporary) / "x-authority"
        child_wrapper = Path(temporary) / "run-esctest-child.py"
        child_wrapper.write_text(
            "import os, pathlib, subprocess, sys, time\n"
            "status = pathlib.Path(sys.argv[1])\n"
            "release = pathlib.Path(sys.argv[2])\n"
            "while not release.exists(): time.sleep(0.01)\n"
            "code = subprocess.run(sys.argv[3:], stdin=None, stdout=None, stderr=None, check=False).returncode\n"
            "temporary = status.with_name(status.name + '.tmp')\n"
            "temporary.write_text(str(code), encoding='ascii')\n"
            "os.replace(temporary, status)\n"
            "raise SystemExit(code)\n",
            encoding="utf-8",
        )
        dotty_config.mkdir(parents=True)
        openbox_config = config_home / "openbox"
        openbox_config.mkdir(parents=True)
        openbox_rc = openbox_config / "rc.xml"
        openbox_rc.write_text(
            '<?xml version="1.0"?>\n'
            '<openbox_config xmlns="http://openbox.org/3.4/rc"><focus><focusNew>yes</focusNew>'
            "<followMouse>no</followMouse><raiseOnFocus>yes</raiseOnFocus></focus></openbox_config>\n",
            encoding="utf-8",
        )
        (dotty_config / "config.json").write_text(json.dumps({
            "vt": {
                "windowResizeAllowed": True,
                "windowControlAllowed": True,
                "checksumReportEnabled": True,
                "titleReportEnabled": True,
            },
            "window": {"columns": config["columns"], "rows": config["rows"]},
            "clipboard": {"allowOsc52Write": True, "osc52Read": "allow"},
            "mouse": {"copyOnSelect": "none"},
        }), encoding="utf-8")
        shell_script = (
            'openbox --sm-disable --config-file "$2" >"$1" 2>&1 & wm=$!; '
            'trap \'kill "$wm" 2>/dev/null || true\' EXIT; '
            'i=0; while ! xprop -root _NET_SUPPORTING_WM_CHECK 2>/dev/null | '
            'grep -q "window id"; do i=$((i+1)); '
            '[ "$i" -lt 100 ] || { echo "Openbox did not become the X11 window manager" >&2; exit 125; }; '
            'kill -0 "$wm" 2>/dev/null || { echo "Openbox exited" >&2; exit 125; }; '
            'sleep 0.1; done; printf \'%s\\n\' "$DISPLAY" >"$3"; '
            'printf \'%s\\n\' "$XAUTHORITY" >"$4"; shift 4; "$@"; status=$?; '
            'kill "$wm" 2>/dev/null || true; wait "$wm" 2>/dev/null || true; exit "$status"'
        )
        esctest_command = [
            *command, "--logfile", str(log_path),
            "--include", "^" + re.escape(test_name) + "$", *config["esctestArgs"],
        ]
        argv = [
            tools["xvfb-run"], "-a", "-s", "-screen 0 1280x1024x24",
            "sh", "-c", shell_script, "dotty-esctest", str(wm_log), str(openbox_rc),
            str(display_file), str(authority_file),
            "env", "WAYLAND_DISPLAY=",
            f"HOME={home}", f"DOTNET_CLI_HOME={home}",
            f"XDG_CONFIG_HOME={config_home}", f"DOTTY_CONFIG_HOME={dotty_config}",
            "XDG_SESSION_TYPE=x11", "DOTTY_TEST_PORT=0",
            str(gui_executable), "--working-directory", str(cwd), "--",
            sys.executable, str(child_wrapper), str(child_status), str(child_release), *esctest_command,
        ]
        started = time.monotonic()
        if runner is run_command:
            code = run_gui_command(
                argv, cwd, timeout + 30, host_log, child_status, child_release, display_file,
                authority_file, log_path, int(config["columns"]), int(config["rows"]), tools,
            )
        else:
            code = runner(argv, cwd, timeout + 30, host_log)
        seconds = time.monotonic() - started
        text = log_path.read_text(encoding="utf-8", errors="replace") if log_path.exists() else ""
        results, saw_summary = er.parse_log(text)
        host_text = host_log.read_text(encoding="utf-8", errors="replace") if host_log.exists() else ""
        port_announced = re.search(r"^DOTTY_TEST_PORT=\d+\s*$", host_text, re.MULTILINE) is not None
        lifecycle_required = runner is run_command
        child_status_match = re.search(r"^ESCTEST_CHILD_EXIT=(-?\d+)\s*$", host_text, re.MULTILINE)
        state_ok = "DOTTY_GUI_STATE=" in host_text and "DOTTY_GUI_STATE_ERROR=" not in host_text
        focus_ok = "DOTTY_X11_WINDOW_FOCUSED=1" in host_text
        natural_exit = re.search(r"^DOTTY_NATURAL_EXIT=1\s*$", host_text, re.MULTILINE) is not None
        shutdown_ok = (
            re.search(r"^DOTTY_SHUTDOWN=OK\s*$", host_text, re.MULTILINE) is not None
            or natural_exit
        )
        child_status_ok = child_status_match is not None and int(child_status_match.group(1)) == code
        complete = saw_summary and code == 0 and port_announced and (
            not lifecycle_required or (state_ok and focus_ok and shutdown_ok and child_status_ok)
        )
        reason = ""
        if code == 124:
            reason = "timeout"
        elif code == 125:
            reason = "GUI/window-manager/lifecycle error"
        elif code == 130:
            reason = "interrupted"
        elif code != 0:
            reason = f"GUI process exited {code}"
        elif not port_announced:
            reason = "Dotty did not announce DOTTY_TEST_PORT"
        elif lifecycle_required and not child_status_ok:
            reason = "esctest child exit status was not captured or did not match Dotty"
        elif lifecycle_required and not state_ok:
            reason = "Dotty GUI state was not verified at the configured grid size"
        elif lifecycle_required and not focus_ok:
            reason = "Dotty window was not mapped and natively focused before esctest started"
        elif lifecycle_required and not shutdown_ok:
            reason = "Dotty did not confirm SHUTDOWN or natural process exit"
        elif not saw_summary:
            reason = "no summary line in GUI esctest log" if text else "no GUI esctest log produced"
        if len(results) != 1 or (results and results[0].name != test_name):
            complete = False
            reason = f"GUI invocation returned {len(results)} results; expected exactly {test_name}"
        return er.ClassRun(klass, code, seconds, complete, reason), results


# --------------------------------------------------------------------------- orchestration

def evaluate(
    all_results: list[er.TestResult],
    class_runs: dict[str, er.ClassRun],
    expectations: er.Expectations,
    config: dict,
    *,
    full_run: bool,
    classes: Sequence[str],
    strict: bool,
) -> tuple[er.Comparison, list[str]]:
    """Returns (comparison, harness fault reasons)."""
    incomplete = [c.name for c in class_runs.values() if not c.complete]
    cmp = er.compare(all_results, expectations, strict_fixed=strict, full_run=full_run,
                     known_classes=classes if full_run else None, incomplete_classes=incomplete)
    faults: list[str] = []
    if cmp.harness_faults:
        faults.append(f"{len(cmp.harness_faults)} test(s) failed inside esctest reset() "
                      "(host not answering queries?)")
    for c in class_runs.values():
        if not c.complete:
            faults.append(f"class {c.name} incomplete: {c.reason or 'no summary line'} (exit {c.exit_code})")
    if CANARY_CLASS in class_runs and not any(
            r.klass == CANARY_CLASS and r.outcome == er.PASS for r in cmp.results):
        faults.append(f"canary class {CANARY_CLASS} has no passing test (host or log parsing is broken)")
    if full_run:
        passed = cmp.counts[er.PASS] + cmp.counts["unexpectedPass"]
        if passed < int(config.get("minPassed", 0)):
            faults.append(f"only {passed} tests passed, below minPassed={config['minPassed']}")
    return cmp, faults


def result_coverage_faults(
    results: Sequence[er.TestResult], gui_selected: Sequence[str], expected_count: int | None, full_run: bool,
) -> list[str]:
    """Reject duplicate outcomes, absent native cases, and incomplete full-suite reports."""
    names = [result.name for result in results]
    faults = []
    duplicates = sorted({name for name in names if names.count(name) > 1})
    if duplicates:
        faults.append(f"duplicate test outcomes: {', '.join(duplicates)}")
    for test_name in gui_selected:
        observed = names.count(test_name)
        if observed != 1:
            faults.append(f"GUI case {test_name} produced {observed} outcomes (expected exactly one)")
    if full_run and expected_count is not None and len(names) != expected_count:
        faults.append(f"full run produced {len(names)} outcomes, expected {expected_count}")
    return faults


def exit_code_for(cmp: er.Comparison, faults: Sequence[str], *, gating: bool, strict: bool) -> int:
    if faults:
        return EXIT_HARNESS
    if cmp.gate_failures(strict_fixed=strict):
        return EXIT_GATE if gating else EXIT_OK
    return EXIT_OK


def annotations(cmp: er.Comparison, expectations_path: Path, limit: int = 10) -> list[str]:
    lines = []
    for r in cmp.regressions[:limit]:
        lines.append(f"::error title=esctest regression::{r.name}: {r.message}")
    for name in cmp.unexpected_passes[:limit]:
        lines.append(f"::error file={Path(expectations_path).as_posix()},title=esctest unexpected pass::"
                     f"{name} now passes; remove its xfail line")
    for name in cmp.stale[:limit]:
        lines.append(f"::error file={Path(expectations_path).as_posix()},title=esctest stale entry::{name}")
    for name in cmp.not_run[:limit]:
        lines.append(f"::error title=esctest not run::{name} produced no outcome (hang or crash)")
    return lines


def main(argv: Sequence[str] | None = None, *, runner: CommandRunner = run_command) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--host", nargs="+", help="host command (default: built apphost / dotnet <dll>)")
    ap.add_argument("--esctest-dir", type=Path, default=DEFAULT_ESCTEST_DIR)
    ap.add_argument("--config", type=Path, default=DEFAULT_CONFIG)
    ap.add_argument("--expectations", type=Path, default=DEFAULT_EXPECTATIONS)
    ap.add_argument("--out", type=Path, default=DEFAULT_OUT)
    ap.add_argument("--jobs", type=int, default=min(os.cpu_count() or 1, 4))
    ap.add_argument("--include", help="regex restricting test names inside each class (debug)")
    ap.add_argument("--only-class", action="append", default=[], help="restrict to a class (repeatable)")
    ap.add_argument("--update-expectations", action="store_true")
    ap.add_argument("--quarantine-not-run", action="store_true",
                    help="with --update-expectations: add 'skip' entries for tests that hung/crashed")
    ap.add_argument("--allow-unexpected-pass", "--no-strict", dest="strict", action="store_false")
    ap.add_argument("--gate", choices=("auto", "on", "off"), default="auto")
    ap.add_argument("--list-classes", action="store_true")
    ap.add_argument("--keep-transcripts", action="store_true", help="keep transcripts of clean classes too")
    args = ap.parse_args(argv)

    try:
        config = load_config(args.config)
        commit = ensure_esctest(args.esctest_dir, config)
        init_py = args.esctest_dir / "esctest" / "tests" / "__init__.py"
        classes = discover_classes(init_py.read_text(encoding="utf-8"))
        expectations = er.load_expectations(args.expectations)
    except (SetupError, ValueError, OSError) as ex:
        print(f"error: {ex}", file=sys.stderr)
        return EXIT_USAGE

    if args.list_classes:
        print("\n".join(classes))
        return EXIT_OK

    unknown = [c for c in args.only_class if c not in classes]
    if unknown:
        print(f"error: unknown class(es): {', '.join(unknown)}", file=sys.stderr)
        return EXIT_USAGE
    selected = [c for c in classes if not args.only_class or c in args.only_class]
    try:
        gui_selected = select_gui_tests(selected, args.include)
        gui_executable, gui_tools = require_gui_prerequisites(config, REPO_ROOT) if gui_selected else (None, {})
        if not args.only_class and not args.include and config.get("expectedTestCount") is not None:
            absent = sorted({er.class_of(name) for name in GUI_TESTS} - set(classes))
            if absent:
                raise SetupError(f"pinned esctest checkout lacks GUI test class(es): {', '.join(absent)}")
        elif gui_selected:
            absent = sorted({er.class_of(name) for name in gui_selected} - set(classes))
            if absent:
                raise SetupError(f"pinned esctest checkout lacks GUI test class(es): {', '.join(absent)}")
    except (SetupError, re.error) as ex:
        print(f"error: {ex}", file=sys.stderr)
        return EXIT_USAGE
    full_run = not args.only_class and not args.include
    gating = {"on": True, "off": False, "auto": expectations.baseline_captured}[args.gate]
    host = args.host or default_host()
    out = args.out.resolve()
    if out.exists():
        shutil.rmtree(out)
    (out / "logs").mkdir(parents=True)
    (out / "transcripts").mkdir()
    cwd = (args.esctest_dir / "esctest").resolve()
    timeout = float(config["perClassTimeoutSeconds"])

    def task(klass: str):
        skips = [n for n in expectations.skip if er.class_of(n) == klass]
        skips += [n for n in GUI_TESTS if er.class_of(n) == klass]
        argv_ = build_class_command(host, config, cwd, klass, out / "logs" / f"{klass}.log",
                                    out / "transcripts" / f"{klass}.txt", skips, args.include)
        return run_class(klass, argv_, cwd, timeout, out, runner)

    class_runs: dict[str, er.ClassRun] = {}
    all_results: list[er.TestResult] = []
    gui_runs: dict[str, list[er.ClassRun]] = {}
    try:
        with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, args.jobs)) as pool:
            for klass, (run, results) in zip(selected, pool.map(task, selected)):
                class_runs[klass] = run
                all_results.extend(results)
        # Each window gets a fresh Xvfb, Openbox, HOME, and config; never parallelize these
        # focus/selection-sensitive native cases.
        for test_name in gui_selected:
            run, results = run_gui_test(
                test_name, [sys.executable, "esctest.py"], cwd, timeout, out, runner,
                gui_executable, gui_tools, config,
            )
            gui_runs.setdefault(run.name, []).append(run)
            all_results.extend(results)
            gui_slug = re.sub(r"[^A-Za-z0-9_.-]", "_", test_name)
            gui_host_log = out / "logs" / "gui" / f"{gui_slug}.host.txt"
            host_text = gui_host_log.read_text(encoding="utf-8", errors="replace")
            port_match = re.search(r"^DOTTY_TEST_PORT=(\d+)\s*$", host_text, re.MULTILINE)
            natural_exit = re.search(r"^DOTTY_NATURAL_EXIT=1\s*$", host_text, re.MULTILINE) is not None
            child_match = re.search(r"^ESCTEST_CHILD_EXIT=(-?\d+)\s*$", host_text, re.MULTILINE)
            focus_verified = "DOTTY_X11_WINDOW_FOCUSED=1" in host_text
            with (out / "logs" / "gui" / f"{gui_slug}.json").open("w", encoding="utf-8") as metadata:
                json.dump({
                    "test": test_name,
                    "lane": "dotty-gui-xvfb-openbox",
                    "exitCode": run.exit_code,
                    "esctestChildExitCode": int(child_match.group(1)) if child_match else None,
                    "seconds": run.seconds,
                    "complete": run.complete,
                    "reason": run.reason,
                    "display": "isolated Xvfb",
                    "windowManager": "Openbox",
                    "focusPolicy": {"focusNew": True, "followMouse": False},
                    "windowGrid": {"columns": config["columns"], "rows": config["rows"]},
                    "guiStateVerified": "DOTTY_GUI_STATE=" in host_text
                    and "DOTTY_GUI_STATE_ERROR=" not in host_text,
                    "nativeWindowFocused": focus_verified,
                    "naturalExit": natural_exit,
                    "shutdownConfirmed": natural_exit or re.search(
                        r"^DOTTY_SHUTDOWN=OK\s*$", host_text, re.MULTILINE,
                    ) is not None,
                    "terminationMode": "natural-child-exit" if natural_exit else "control-shutdown",
                    "dottyExecutable": str(gui_executable),
                    "dottyTestPort": int(port_match.group(1)) if port_match else None,
                    "esctestCommand": [
                        sys.executable, "esctest.py", "--logfile",
                        str(out / "logs" / "gui" / f"{gui_slug}.log"),
                        "--include", "^" + re.escape(test_name) + "$", *config["esctestArgs"],
                    ],
                    "stdioWrapper": "inherits PTY stdin/stdout/stderr and atomically records child exit",
                    "isolatedHomeAndConfig": True,
                }, metadata, indent=2)
                metadata.write("\n")
        for klass, runs in gui_runs.items():
            headless = class_runs[klass]
            combined = [headless, *runs]
            failures = [run for run in combined if not run.complete]
            class_runs[klass] = er.ClassRun(
                klass,
                next((run.exit_code for run in runs if run.exit_code != 0), headless.exit_code),
                sum(run.seconds for run in combined),
                not failures,
                "; ".join(f"{run.reason or 'incomplete'} (exit {run.exit_code})" for run in failures),
            )
    finally:
        kill_stragglers(args.esctest_dir)

    cmp, faults = evaluate(all_results, class_runs, expectations, config,
                           full_run=full_run, classes=classes, strict=args.strict)
    expected_count = config.get("expectedTestCount")
    faults.extend(result_coverage_faults(
        all_results, gui_selected, int(expected_count) if expected_count is not None else None, full_run,
    ))
    code = exit_code_for(cmp, faults, gating=gating, strict=args.strict)

    # Per-class merged log + transcript cleanup.
    with open(out / "merged.log", "w", encoding="utf-8") as merged:
        for klass in selected:
            log = out / "logs" / f"{klass}.log"
            merged.write(f"==== {klass} ====\n")
            if log.exists():
                merged.write(log.read_text(encoding="utf-8", errors="replace"))
            merged.write("\n")
        for test_name in gui_selected:
            gui_slug = re.sub(r"[^A-Za-z0-9_.-]", "_", test_name)
            gui_log = out / "logs" / "gui" / f"{gui_slug}.log"
            merged.write(f"==== GUI {test_name} ====\n")
            if gui_log.exists():
                merged.write(gui_log.read_text(encoding="utf-8", errors="replace"))
            merged.write("\n")
    bad_classes = {r.klass for r in cmp.regressions} | {n.split(".")[0] for n in cmp.not_run} \
        | {r.klass for r in cmp.harness_faults} | {c.name for c in class_runs.values() if not c.complete}
    if not args.keep_transcripts:
        for klass in selected:
            if klass not in bad_classes:
                (out / "transcripts" / f"{klass}.txt").unlink(missing_ok=True)

    banner = ""
    if not gating:
        banner = ("NOT GATING: tests/esctest/expected-failures.txt has no '# baseline-captured:' marker "
                  "(or --gate off); results are reported but failures do not fail the run.")
    headless_result_count = sum(result.name not in gui_selected for result in all_results)
    meta = {
        "esctest": {"commit": commit, "args": config["esctestArgs"]},
        "host": {"os": platform.system(), "arch": platform.machine(), "python": platform.python_version()},
        "gating": gating,
        "harnessFaults": faults,
        "lanes": {
            "headless": {"testCount": headless_result_count, "host": "Dotty.Conformance.Host"},
            "gui": {
                "testCount": len(gui_selected),
                "backend": "Dotty GUI under isolated Xvfb/Openbox",
                "cases": gui_selected,
                "artifacts": "logs/gui/",
            },
        },
    }
    (out / "results.json").write_text(er.dumps_json(er.render_json(cmp, meta, expectations)), encoding="utf-8")
    markdown = er.render_markdown(cmp, class_runs, banner=banner)
    markdown += (
        "\n### Execution lanes\n\n"
        f"- Headless conformance host: {headless_result_count} test outcomes.\n"
        f"- Real Dotty GUI (Xvfb/Openbox): {len(gui_selected)} case(s)"
        + (f" — {', '.join(gui_selected)}" if gui_selected else "")
        + ".\n"
    )
    if faults:
        markdown += "\n### Harness faults (exit 3)\n\n" + "\n".join(f"- {f}" for f in faults) + "\n"
    (out / "summary.md").write_text(markdown, encoding="utf-8")
    step_summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if step_summary:
        with open(step_summary, "a", encoding="utf-8") as fh:
            fh.write(markdown + "\n")

    s = er.summary_counts(cmp)
    print(f"esctest2 {commit[:12]}: {s['pass']} pass, {s['fail']} regressions, {s['expectedFail']} expected fail, "
          f"{s['unexpectedPass']} unexpected pass, {s['knownBug']} known bug, {s['skipped']} skipped, "
          f"{s['notRun']} not run, {s['harnessFault']} harness fault, {s['total']} total")
    if banner:
        print(banner)
    for fault in faults:
        print(f"harness fault: {fault}")
    if gating and not args.update_expectations:
        for line in annotations(cmp, args.expectations):
            print(line)

    if args.update_expectations:
        return update_expectations(args, cmp, faults, expectations, commit, full_run, config)
    return code


def update_expectations(args, cmp: er.Comparison, faults: Sequence[str], previous: er.Expectations,
                        commit: str, full_run: bool, config: dict) -> int:
    if not full_run:
        print("error: --update-expectations needs a full run (no --only-class/--include)", file=sys.stderr)
        return EXIT_USAGE
    if faults:
        print("error: refusing to update expectations from a run with harness faults", file=sys.stderr)
        return EXIT_HARNESS
    not_run = sorted(set(cmp.not_run))
    if not_run and not args.quarantine_not_run:
        print("error: tests hung or crashed; add them as 'skip' lines or rerun with --quarantine-not-run:\n  "
              + "\n  ".join(not_run), file=sys.stderr)
        return EXIT_GATE
    text = er.render_expectations(cmp.results, previous, commit=commit,
                                  quarantine=not_run if args.quarantine_not_run else ())
    path: Path = args.expectations
    old = path.read_text(encoding="utf-8") if path.exists() else ""
    sys.stdout.writelines(difflib.unified_diff(old.splitlines(True), text.splitlines(True),
                                               "expected-failures.txt (old)", "expected-failures.txt (new)"))
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")
    passed = cmp.counts[er.PASS] + cmp.counts["unexpectedPass"]
    print(f"wrote {path}; suggested minPassed for esctest.json: {int(passed * 0.9)}")
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
