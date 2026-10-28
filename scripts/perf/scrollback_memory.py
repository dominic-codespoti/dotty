#!/usr/bin/env python3
"""End-to-end scrollback memory harness (plan-03 WI-0).

Drives the real Dotty host (under ``xvfb-run`` on Linux) through the loopback
control port (``DOTTY_TEST_PORT``): ``ACTION:NewTab`` creates tabs, ``TYPE:`` +
``KEY:return`` run shell commands that fill history or emit escape sequences,
``RESIZE:<cols>:<rows>`` resizes, ``MEMORY`` returns the host's counters.
After every stage the Dotty process (not xvfb, pty-helper or shells) is sampled
from ``/proc/<pid>/smaps_rollup``.

Matrix: tabs x history x cols x stage x repetitions. The headline number is the
median marginal per-tab PSS = (PSS_max_tabs - PSS_min_tabs) / (max_tabs - min_tabs).

Linux only for sampling (smaps_rollup). Requires Xvfb/xvfb-run. Nothing here
was measured by the author; run it on a machine with the built host.

    python3 scripts/perf/scrollback_memory.py --app artifacts/publish/dotty \
        --out artifacts/perf/scrollback-memory

Pure functions (parse/median/marginal/matrix/render/model) are covered by
scripts/perf/tests/test_scrollback_memory.py and need neither dotnet nor X.
"""
import argparse
import itertools
import json
import os
import platform
import secrets
import signal
import socket
import statistics
import subprocess
import sys
import tempfile
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

BYTES_PER_CELL = 12  # hot cell 8 B + cold cell 4 B (plan-03 section 1.1)
DEFAULT_TABS = (1, 20)
DEFAULT_HISTORY = (0, 500, 5000)
DEFAULT_COLS = (80, 200)
ROWS = 24
STAGES = ("fresh", "resize2", "alt", "ed2", "ed3")
SMAPS_FIELDS = ("Rss", "Pss", "Pss_Anon")
# MEMORY JSON fields reported when present; missing fields render blank.
MEMORY_FIELDS = (
    "terminalArenaCapacityBytes",
    "terminalArenaCommittedBoundBytes",
    "arenaCapacityAllocatedBytes",
    "compressedHistoryBytes",
    "compressedHistoryChunks",
    "rawHistoryChunks",
    "managedHeapEstimateBytes",
)
FILL_LINE = "The quick brown fox jumps over the lazy dog 0123456789"
MIB = 1024 * 1024


# ---------------------------------------------------------------- pure helpers

def parse_smaps_rollup(text):
    """Return {'Rss': bytes, 'Pss': bytes, 'Pss_Anon': bytes, ...} from smaps_rollup text.

    Only ``<Name>: <n> kB`` lines are read; the header line (address range) is ignored.
    """
    values = {}
    for line in text.splitlines():
        name, sep, rest = line.partition(":")
        if not sep:
            continue
        parts = rest.split()
        if len(parts) == 2 and parts[1] == "kB" and parts[0].isdigit():
            values[name.strip()] = int(parts[0]) * 1024
    return values


def median(values):
    """Median of numbers; None entries are ignored; None when nothing remains."""
    clean = [v for v in values if v is not None]
    return statistics.median(clean) if clean else None


def marginal_per_tab(pss_many, pss_one, tabs_many, tabs_one=1):
    """(PSS_many - PSS_one) / (tabs_many - tabs_one); None when inputs are missing."""
    if pss_many is None or pss_one is None:
        return None
    if tabs_many <= tabs_one:
        raise ValueError("tabs_many must exceed tabs_one")
    return (pss_many - pss_one) / (tabs_many - tabs_one)


def expand_matrix(tabs=DEFAULT_TABS, history=DEFAULT_HISTORY, cols=DEFAULT_COLS, reps=3):
    """List of (rep, history, cols, tabs) host launches, one launch per element; stages run inside it."""
    return [(rep, h, c, t) for rep, h, c, t in itertools.product(range(reps), history, cols, tabs)]


def fill_command(lines, marker):
    """Shell command that prints `lines` lines and then a marker the command text itself does not contain."""
    body = f"yes '{FILL_LINE}' | head -n {lines}; " if lines > 0 else ""
    return body + _marker_echo(marker)


def escape_command(sequence_octal, marker):
    """printf of a raw escape sequence given in shell octal form, e.g. '\\033[2J'."""
    return f"printf '{sequence_octal}'; " + _marker_echo(marker)


def _marker_echo(marker):
    # The empty quotes keep the typed command from containing the marker verbatim, so a hit in
    # the screen dump can only come from the echo output.
    return f"echo {marker[:2]}''{marker[2:]}"


def stage_commands(cols):
    """Ordered (stage, [actions]) pairs. Actions: ('resize', cols), ('shell', octal-escape)."""
    return [
        ("fresh", []),
        ("resize2", [("resize", cols + 20), ("resize", cols)]),
        ("alt", [("shell", r"\033[?1049h\033[?1049l")]),
        ("ed2", [("shell", r"\033[2J")]),
        ("ed3", [("shell", r"\033[3J")]),
    ]


def model_ring_bytes(lines, rows, cols, rings=1, bytes_per_cell=BYTES_PER_CELL):
    """Arithmetic model of the raw reserved ring: 12 * (lines + rows) * cols * rings."""
    return bytes_per_cell * (lines + rows) * cols * rings


def model_committed_bound(history_lines, capacity, rows, cols, page=4096, rings=1,
                          bytes_per_cell=BYTES_PER_CELL):
    """Hypothesis for lazily committed bytes: only populated lines (capped by capacity) plus the
    visible rows are touched, rounded up to whole pages per plane (hot 8 B / cold 4 B)."""
    touched_rows = min(history_lines, capacity) + rows
    cells = touched_rows * cols
    hot = -(-cells * (bytes_per_cell - 4) // page) * page
    cold = -(-cells * 4 // page) * page
    return rings * (hot + cold)


def _fmt(value, scale=1, digits=2):
    if value is None:
        return ""
    if isinstance(value, float) or scale != 1:
        return f"{value / scale:.{digits}f}"
    return str(value)


def summarize(samples, tabs_values, stages=STAGES):
    """Collapse raw samples into rows.

    samples: list of dicts {rep,history,cols,tabs,stage,rss,pss,pss_anon,memory:{...}}.
    One row per (history, cols, stage) with medians over reps for the min/max tab counts, the
    marginal per-tab PSS/RSS/Pss_Anon and the median MEMORY counters of the max-tab runs.
    """
    lo, hi = min(tabs_values), max(tabs_values)
    groups = {}
    for s in samples:
        groups.setdefault((s["history"], s["cols"], s["stage"], s["tabs"]), []).append(s)
    rows = []
    keys = sorted({(h, c, st) for h, c, st, _ in groups})
    for h, c, st in keys:
        def med(tabs, field):
            return median([x.get(field) for x in groups.get((h, c, st, tabs), [])])
        row = {"history": h, "cols": c, "stage": st, "tabs_lo": lo, "tabs_hi": hi,
               "runs": len(groups.get((h, c, st, hi), []))}
        for field in ("pss", "rss", "pss_anon"):
            row[f"{field}_lo"] = med(lo, field)
            row[f"{field}_hi"] = med(hi, field)
            row[f"{field}_marginal_per_tab"] = (
                marginal_per_tab(row[f"{field}_hi"], row[f"{field}_lo"], hi, lo) if hi > lo else None)
        mem = {}
        for name in MEMORY_FIELDS:
            mem[name] = median([(x.get("memory") or {}).get(name) for x in groups.get((h, c, st, hi), [])])
        row["memory"] = mem
        rows.append(row)
    order = {name: i for i, name in enumerate(stages)}
    rows.sort(key=lambda r: (r["history"], r["cols"], order.get(r["stage"], 99)))
    return rows


def render_markdown(rows, provenance=None):
    """Markdown table; blank cells for missing values."""
    lines = []
    if provenance:
        lines.append("## Provenance")
        lines.append("")
        for key in sorted(provenance):
            lines.append(f"- {key}: {provenance[key]}")
        lines.append("")
    lines.append("## Median marginal per-tab memory (MiB) and MEMORY counters")
    lines.append("")
    header = ["history", "cols", "stage", "runs", "PSS/tab MiB", "Pss_Anon/tab MiB", "RSS/tab MiB"]
    header += list(MEMORY_FIELDS)
    lines.append("| " + " | ".join(header) + " |")
    lines.append("|" + "|".join(["---"] * len(header)) + "|")
    for r in rows:
        cells = [str(r["history"]), str(r["cols"]), r["stage"], str(r["runs"]),
                 _fmt(r["pss_marginal_per_tab"], MIB), _fmt(r["pss_anon_marginal_per_tab"], MIB),
                 _fmt(r["rss_marginal_per_tab"], MIB)]
        for name in MEMORY_FIELDS:
            v = r["memory"].get(name)
            cells.append("" if v is None else (str(int(v)) if float(v).is_integer() else f"{v:.1f}"))
        lines.append("| " + " | ".join(cells) + " |")
    lines.append("")
    return "\n".join(lines)


def parse_memory_json(text):
    """Tolerant MEMORY response parse: {} on failure; only numeric top-level fields are kept."""
    try:
        data = json.loads(text)
    except (ValueError, TypeError):
        return {}
    if not isinstance(data, dict):
        return {}
    return {k: v for k, v in data.items() if isinstance(v, (int, float)) and not isinstance(v, bool)}


# ------------------------------------------------------------- runtime (Linux)

def read_smaps_rollup(pid):
    return parse_smaps_rollup(Path(f"/proc/{pid}/smaps_rollup").read_text())


def free_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def provenance(app):
    def run(*cmd):
        try:
            return subprocess.run(cmd, capture_output=True, text=True, timeout=10).stdout.strip()
        except (OSError, subprocess.SubprocessError):
            return ""
    thp = ""
    try:
        thp = Path("/sys/kernel/mm/transparent_hugepage/enabled").read_text().strip()
    except OSError:
        pass
    return {
        "git_sha": run("git", "-C", str(HERE), "rev-parse", "HEAD"),
        "dotnet_sdk": run("dotnet", "--version"),
        "kernel": platform.release(),
        "platform": platform.platform(),
        "libc": " ".join(platform.libc_ver()),
        "page_size": os.sysconf("SC_PAGE_SIZE") if hasattr(os, "sysconf") else "",
        "thp": thp,
        "app": str(app),
    }


class Host:
    def __init__(self, app, work):
        self.port = free_port()
        config = Path(work) / "config"
        config.mkdir(parents=True, exist_ok=True)
        (config / "config.json").write_text(json.dumps({
            "window": {"padding": {"left": 0, "right": 0, "top": 0, "bottom": 0}},
            "tabBar": {"show": False}, "cursor": {"blink": False}}), encoding="utf-8")
        env = dict(os.environ, HOME=str(work), DOTTY_CONFIG_HOME=str(config),
                   DOTTY_TEST_PORT=str(self.port), DOTTY_SHELL="/bin/sh", ENV="", PS1="$ ")
        for key in ("DOTTY_OUTPUT_EVENTS", "WAYLAND_DISPLAY"):
            env.pop(key, None)
        self.log = open(Path(work) / "app.log", "w")
        self.proc = subprocess.Popen(
            ["xvfb-run", "-a", "-s", "-screen 0 1600x1000x24", str(app)],
            env=env, stdout=self.log, stderr=subprocess.STDOUT, start_new_session=True)
        self.pid = None
        self._wait_ready()

    def send(self, command, timeout=15):
        with socket.create_connection(("127.0.0.1", self.port), timeout=timeout) as c:
            c.sendall((command + "\n").encode())
            data = b""
            while chunk := c.recv(65536):
                data += chunk
        return data.decode(errors="replace").strip()

    def _wait_ready(self):
        import idle_wakeups
        for _ in range(240):
            if self.proc.poll() is not None:
                raise RuntimeError("xvfb-run/dotty exited early; see app.log")
            try:
                self.send("WAIT_FOR_IDLE", timeout=3)
                break
            except OSError:
                time.sleep(0.25)
        else:
            raise RuntimeError("control port never became ready")
        for _ in range(40):
            self.pid = idle_wakeups._find_dotty(self.proc.pid)
            if self.pid:
                return
            time.sleep(0.25)
        raise RuntimeError("could not find the dotty process under xvfb-run")

    def shell(self, command, marker, timeout=120):
        """Type a command into the active tab and wait until its marker appears in the screen dump."""
        self._expect_ok(self.send("TYPE:" + command))
        self._expect_ok(self.send("KEY:return"))
        needle = marker
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            self.send("WAIT_FOR_IDLE")
            dump = self.send("DUMP")
            if any(line.strip() == needle for line in dump.splitlines()):
                return
            time.sleep(0.1)
        raise TimeoutError(f"marker {marker} not seen within {timeout}s")

    @staticmethod
    def _expect_ok(response):
        if response != "OK":
            raise RuntimeError(f"control command failed: {response}")

    def close(self):
        try:
            self.send("SHUTDOWN", timeout=3)
            self.proc.wait(timeout=10)
        except Exception:
            pass
        try:
            os.killpg(self.proc.pid, signal.SIGKILL)
        except (ProcessLookupError, PermissionError):
            pass
        self.proc.wait()
        self.log.close()


def new_marker():
    return "MK" + secrets.token_hex(5)


def sample(host, memory_settle):
    time.sleep(memory_settle)
    host.send("WAIT_FOR_IDLE")
    smaps = read_smaps_rollup(host.pid)
    return {"rss": smaps.get("Rss"), "pss": smaps.get("Pss"), "pss_anon": smaps.get("Pss_Anon"),
            "memory": parse_memory_json(host.send("MEMORY"))}


def run_launch(app, rep, history, cols, tabs, args):
    out = []
    with tempfile.TemporaryDirectory(prefix="dotty-scrollmem-") as work:
        host = Host(app, work)
        try:
            host._expect_ok(host.send(f"RESIZE:{cols}:{ROWS}"))
            time.sleep(args.step_delay)
            for index in range(tabs):
                if index:
                    host._expect_ok(host.send("ACTION:NewTab"))
                    time.sleep(args.step_delay)
                marker = new_marker()
                host.shell(fill_command(history, marker), marker)
            for stage, actions in stage_commands(cols):
                for kind, value in actions:
                    if kind == "resize":
                        host._expect_ok(host.send(f"RESIZE:{value}:{ROWS}"))
                        time.sleep(args.step_delay)
                    else:
                        # Escape sequences go to the active tab only; every tab is visited so the
                        # whole population experiences the stage.
                        for tab in range(tabs):
                            marker = new_marker()
                            host.shell(escape_command(value, marker), marker)
                            if tabs > 1:
                                host._expect_ok(host.send("ACTION:NextTab"))
                                time.sleep(args.step_delay)
                result = sample(host, args.settle)
                result.update(rep=rep, history=history, cols=cols, tabs=tabs, stage=stage)
                out.append(result)
        finally:
            host.close()
    return out


def parse_args(argv=None):
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--app", required=True, type=Path, help="Dotty host executable")
    p.add_argument("--out", type=Path, default=Path("artifacts/perf/scrollback-memory"))
    p.add_argument("--tabs", type=int, nargs="+", default=list(DEFAULT_TABS))
    p.add_argument("--history", type=int, nargs="+", default=list(DEFAULT_HISTORY))
    p.add_argument("--cols", type=int, nargs="+", default=list(DEFAULT_COLS))
    p.add_argument("--reps", type=int, default=3)
    p.add_argument("--settle", type=float, default=3.0,
                   help="seconds to sleep before each sample so idle-time maintenance has run")
    p.add_argument("--step-delay", type=float, default=0.5)
    return p.parse_args(argv)


def main(argv=None):
    args = parse_args(argv)
    if not sys.platform.startswith("linux"):
        print("scrollback_memory: smaps_rollup sampling is Linux-only", file=sys.stderr)
        return 2
    if not args.app.exists():
        print(f"scrollback_memory: {args.app} not found", file=sys.stderr)
        return 2
    samples = []
    for rep, history, cols, tabs in expand_matrix(args.tabs, args.history, args.cols, args.reps):
        print(f"launch rep={rep} history={history} cols={cols} tabs={tabs}", flush=True)
        samples.extend(run_launch(args.app, rep, history, cols, tabs, args))
    rows = summarize(samples, args.tabs)
    prov = provenance(args.app)
    args.out.mkdir(parents=True, exist_ok=True)
    (args.out / "summary.json").write_text(
        json.dumps({"provenance": prov, "rows": rows, "samples": samples}, indent=2), encoding="utf-8")
    (args.out / "summary.md").write_text(render_markdown(rows, prov), encoding="utf-8")
    print(render_markdown(rows, prov))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
