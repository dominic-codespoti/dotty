#!/usr/bin/env python3
"""Run the pinned esctest2 suite against the Dotty conformance host.

One host process is started per esctest class (bounds cascades and hangs);
results come from esctest's log files (it has no machine-readable output and
always exits 0), are normalized and compared with
tests/esctest/expected-failures.txt (see esctest_results.py).

Exit codes: 0 ok; 1 gate failure (regression / unexpected pass / stale entry /
not-run); 2 usage or setup error; 3 harness fault.

Gating: the gate is armed only once tests/esctest/expected-failures.txt carries
the '# baseline-captured: <commit>' marker written by --update-expectations.
Until then the run still executes and reports everything, but gate failures
(exit 1) are downgraded to exit 0 and the output says "NOT GATING". Use
--gate on|off to override.

needs-runtime-verification (assumptions made without an esctest2 checkout or
dotnet SDK; confirm on the first real run):
  * esctest2 lives at <esctest-dir>/esctest/esctest.py and lists its test
    classes as ``from tests.<module> import <Class>`` lines in
    esctest/tests/__init__.py;
  * esctest.py accepts --logfile, --include (re.search on 'Class.test_name'),
    --expected-terminal, --xterm-checksum, --max-vt-level, --options,
    --timeout and --v as documented in plan-05;
  * the log format described in esctest_results.py;
  * the host binary is the apphost
    tests/Dotty.Conformance.Host/bin/Release/net11.0/dotty-conformance-host
    (or the .dll, launched through ``dotnet``);
  * ``git fetch --depth 1 <repo> <sha>`` of the pinned commit is allowed by
    the remote (true for GitHub);
  * the pinned commit in tests/esctest/esctest.json was copied from the plan
    (HEAD at planning time) and not re-resolved.
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
import time
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


def default_host(root: Path = REPO_ROOT) -> list[str]:
    base = root / HOST_RELATIVE
    exe = base / "dotty-conformance-host"
    if exe.exists():
        return [str(exe)]
    dll = base / "dotty-conformance-host.dll"
    return ["dotnet", str(dll)]


def build_class_command(
    host: Sequence[str], config: dict, klass: str, log_path: Path, transcript: Path | None,
    skips: Sequence[str], user_include: str | None,
) -> list[str]:
    cmd = list(host)
    cmd += ["--rows", str(config["rows"]), "--columns", str(config["columns"]),
            "--timeout-seconds", str(config["perClassTimeoutSeconds"])]
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
    full_run = not args.only_class and not args.include
    gating = {"on": True, "off": False, "auto": expectations.baseline_captured}[args.gate]
    host = args.host or default_host()
    out: Path = args.out
    if out.exists():
        shutil.rmtree(out)
    (out / "logs").mkdir(parents=True)
    (out / "transcripts").mkdir()
    cwd = args.esctest_dir / "esctest"
    timeout = float(config["perClassTimeoutSeconds"])

    def task(klass: str):
        skips = [n for n in expectations.skip if er.class_of(n) == klass]
        argv_ = build_class_command(host, config, klass, out / "logs" / f"{klass}.log",
                                    out / "transcripts" / f"{klass}.txt", skips, args.include)
        return run_class(klass, argv_, cwd, timeout, out, runner)

    class_runs: dict[str, er.ClassRun] = {}
    all_results: list[er.TestResult] = []
    try:
        with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, args.jobs)) as pool:
            for klass, (run, results) in zip(selected, pool.map(task, selected)):
                class_runs[klass] = run
                all_results.extend(results)
    finally:
        kill_stragglers(args.esctest_dir)

    cmp, faults = evaluate(all_results, class_runs, expectations, config,
                           full_run=full_run, classes=classes, strict=args.strict)
    code = exit_code_for(cmp, faults, gating=gating, strict=args.strict)

    # Per-class merged log + transcript cleanup.
    with open(out / "merged.log", "w", encoding="utf-8") as merged:
        for klass in selected:
            log = out / "logs" / f"{klass}.log"
            merged.write(f"==== {klass} ====\n")
            if log.exists():
                merged.write(log.read_text(encoding="utf-8", errors="replace"))
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
    meta = {"esctest": {"commit": commit, "args": config["esctestArgs"]},
            "host": {"os": platform.system(), "arch": platform.machine(), "python": platform.python_version()},
            "gating": gating, "harnessFaults": faults}
    (out / "results.json").write_text(er.dumps_json(er.render_json(cmp, meta, expectations)), encoding="utf-8")
    markdown = er.render_markdown(cmp, class_runs, banner=banner)
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
