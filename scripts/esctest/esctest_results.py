#!/usr/bin/env python3
"""esctest2 result parsing, normalization and expectation comparison.

Pure Python (stdlib only). esctest2 has no machine-readable output; its log
file is the interface, so everything here is written against the log format
documented in plan-05-esctest.md section 1.1.

needs-runtime-verification (no esctest2 checkout was available when this was
written; each item below is an assumption taken from the plan, not observed):
  * a test starts with the line ``Run test: <Class.test_name>``;
  * its outcome is the first following line starting with ``Passed.``,
    ``Fails as expected:``, ``Skipped because terminal lacks requisite
    capability`` or ``*** TEST <name> FAILED:`` (a Python traceback follows);
  * the run ends with ``*** N tests passed, M known bugs, K TESTS FAILED ***``
    (the exact wording is matched leniently as ``*** <n> test(s) passed``)
    followed by a ``Failing tests:`` list;
  * a failure caused by an unanswered ``reset()`` query has a traceback frame
    ``in reset``;
  * an xterm ``@knownBug`` / ``@optionRequired`` test that passes fails with
    ``InternalError: Should have failed``.
"""

from __future__ import annotations

import json
import re
from collections import Counter
from dataclasses import dataclass, field
from pathlib import Path
from typing import Iterable, Mapping

PASS = "pass"
FAIL = "fail"
KNOWN_BUG = "known-bug"
SKIPPED = "skipped"
NOT_RUN = "not-run"
HARNESS_FAULT = "harness-fault"
OUTCOMES = (PASS, FAIL, KNOWN_BUG, SKIPPED, NOT_RUN, HARNESS_FAULT)

BASELINE_MARKER = "baseline-captured:"
UNCLASSIFIED = "unclassified"

_RUN_RE = re.compile(r"^Run test:\s*(\S+)\s*$")
_FAILED_RE = re.compile(r"^\*\*\* TEST\s+(\S+)\s+FAILED:?\s*(.*)$")
_SUMMARY_RE = re.compile(r"^\*\*\*\s+\d+\s+tests?\s+passed\b")
_FAILING_LIST_RE = re.compile(r"^Failing tests:\s*$")
_RESET_FRAME_RE = re.compile(r"\bin reset\s*$", re.MULTILINE)
_NAME_RE = re.compile(r"^[A-Za-z_]\w*\.[A-Za-z_]\w*$")
_BASELINE_RE = re.compile(r"^#\s*" + re.escape(BASELINE_MARKER) + r"\s*(\S+)")


@dataclass(frozen=True)
class TestResult:
    __test__ = False  # not a unittest/pytest test class

    name: str
    outcome: str
    message: str = ""
    traceback: str = field(default="", compare=False, repr=False)

    @property
    def klass(self) -> str:
        return class_of(self.name)


def class_of(test_name: str) -> str:
    return test_name.split(".", 1)[0]


def parse_log(text: str) -> tuple[list[TestResult], bool]:
    """Parse an esctest2 log. Returns (results in log order, saw_summary_line)."""
    results: list[TestResult] = []
    current: str | None = None
    outcome: str | None = None
    message = ""
    trace: list[str] = []
    saw_summary = False
    in_failing_list = False

    def finish() -> None:
        nonlocal current, outcome, message, trace
        if current is None:
            return
        if outcome is None:
            results.append(TestResult(current, NOT_RUN, "no outcome in log (hang or crash)"))
        elif outcome == FAIL:
            tb = "\n".join(trace).strip("\n")
            lines = [ln.strip() for ln in tb.splitlines() if ln.strip()]
            results.append(TestResult(current, FAIL, lines[-1] if lines else message, tb))
        else:
            results.append(TestResult(current, outcome, message))
        current, outcome, message, trace = None, None, "", []

    for raw in text.splitlines():
        line = raw.rstrip("\r")
        if saw_summary:
            continue
        if _SUMMARY_RE.match(line):
            finish()
            saw_summary = True
            continue
        if _FAILING_LIST_RE.match(line):
            # The list repeats names already seen; ignore it (and any
            # trailing lines) but keep looking for the summary line.
            finish()
            in_failing_list = True
            continue
        if in_failing_list:
            continue
        m = _RUN_RE.match(line)
        if m:
            finish()
            current = m.group(1)
            continue
        if outcome == FAIL:
            trace.append(line)
            continue
        m = _FAILED_RE.match(line)
        if m:
            if current is None:
                current = m.group(1)
            outcome = FAIL
            message = m.group(2).strip()
            continue
        if current is None or outcome is not None:
            continue
        stripped = line.strip()
        if stripped.startswith("Passed."):
            outcome = PASS
        elif stripped.startswith("Fails as expected:"):
            outcome, message = KNOWN_BUG, stripped[len("Fails as expected:"):].strip()
        elif stripped.startswith("Skipped because terminal lacks requisite capability"):
            outcome, message = SKIPPED, stripped.partition(":")[2].strip()
    finish()
    return results, saw_summary


def normalize(result: TestResult) -> TestResult:
    """Apply the esctest/xterm annotation rules described in the module docstring."""
    if result.outcome != FAIL:
        return result
    body = result.traceback or result.message
    if _RESET_FRAME_RE.search(body):
        return TestResult(result.name, HARNESS_FAULT,
                          "reset() failed (unanswered or garbled query): " + result.message, result.traceback)
    if "InternalError: Should have failed" in body:
        return TestResult(result.name, PASS, "passes where xterm fails", result.traceback)
    return result


# --------------------------------------------------------------------------- expectations

@dataclass
class Expectations:
    xfail: dict[str, str] = field(default_factory=dict)
    skip: dict[str, str] = field(default_factory=dict)
    baseline_commit: str | None = None

    @property
    def baseline_captured(self) -> bool:
        return self.baseline_commit is not None


def parse_expectations(text: str, source: str = "<expectations>") -> Expectations:
    exp = Expectations()
    seen: dict[str, int] = {}
    for number, raw in enumerate(text.splitlines(), start=1):
        line = raw.strip()
        if not line:
            continue
        if line.startswith("#"):
            m = _BASELINE_RE.match(line)
            if m:
                if exp.baseline_commit is not None:
                    raise ValueError(f"{source}:{number}: duplicate '{BASELINE_MARKER}' marker")
                exp.baseline_commit = m.group(1)
            continue
        body, _, note = line.partition("#")
        parts = body.split()
        if len(parts) != 2:
            raise ValueError(f"{source}:{number}: expected '<xfail|skip> <Class.test_name> [# note]', got {raw!r}")
        directive, name = parts
        if directive not in ("xfail", "skip"):
            raise ValueError(f"{source}:{number}: unknown directive {directive!r} (only xfail/skip)")
        if not _NAME_RE.match(name):
            raise ValueError(f"{source}:{number}: invalid test name {name!r} (no wildcards)")
        if name in seen:
            raise ValueError(f"{source}:{number}: duplicate entry for {name} (first at line {seen[name]})")
        seen[name] = number
        (exp.xfail if directive == "xfail" else exp.skip)[name] = note.strip()
    return exp


def load_expectations(path: Path) -> Expectations:
    path = Path(path)
    if not path.exists():
        return Expectations()
    return parse_expectations(path.read_text(encoding="utf-8"), str(path))


_HEADER = (
    "# esctest2 expected failures for the Dotty conformance run.",
    "# Regenerate with: python3 scripts/esctest/run_esctest.py --update-expectations",
    "# Directives (one per line, exact Class.test_name, no wildcards):",
    "#   xfail Class.test_name  # note   test must fail; passing it is an error (remove the line)",
    "#   skip  Class.test_name  # note   test is not executed (hang/crash quarantine)",
)


def render_expectations(
    results: Iterable[TestResult],
    previous: Expectations,
    *,
    commit: str,
    quarantine: Iterable[str] = (),
) -> str:
    """Render a fresh expectations file from a complete run (sorted, idempotent)."""
    failing = sorted({r.name for r in results if r.outcome == FAIL})
    skips = dict(previous.skip)
    for name in quarantine:
        skips.setdefault(name, f"{UNCLASSIFIED}: hung or crashed during baseline capture")
    lines = list(_HEADER)
    lines.append(f"# {BASELINE_MARKER} {commit}")
    lines.append("")
    for name in failing:
        note = previous.xfail.get(name) or UNCLASSIFIED
        lines.append(f"xfail {name}  # {note}")
    if skips:
        lines.append("")
    for name in sorted(skips):
        note = skips[name] or UNCLASSIFIED
        lines.append(f"skip {name}  # {note}")
    return "\n".join(lines) + "\n"


# --------------------------------------------------------------------------- areas

# Class -> owning plan area (approximate; from plan-05 section 8). Default: "core".
AREAS: dict[str, str] = {
    "DECSTRTests": "vt-sequences:V4",
    "DECDSRTests": "vt-sequences:V5",
    "DECRQMTests": "vt-sequences:V7",
    "XtermWinopsTests": "vt-sequences:V3",
    "DECRQSSTests": "unowned",
    "DECSCLTests": "unowned",
    "DECCRATests": "unowned",
    "DECFRATests": "unowned",
    "DECERATests": "unowned",
    "DECSERATests": "unowned",
    "DECSELTests": "unowned",
    "DECSEDTests": "unowned",
    "DECICTests": "unowned",
    "DECDCTests": "unowned",
    "ChangeColorTests": "palette",
    "ChangeDynamicColorTests": "palette",
    "ChangeSpecialColorTests": "palette",
    "ResetColorTests": "palette",
    "ResetSpecialColorTests": "palette",
    "ManipulateSelectionDataTests": "clipboard",
    "SMTitleTests": "title",
}


def area_of(test_name: str) -> str:
    return AREAS.get(class_of(test_name), "core")


# --------------------------------------------------------------------------- comparison

@dataclass
class Comparison:
    results: list[TestResult]
    regressions: list[TestResult] = field(default_factory=list)
    unexpected_passes: list[str] = field(default_factory=list)
    stale: list[str] = field(default_factory=list)
    not_run: list[str] = field(default_factory=list)
    harness_faults: list[TestResult] = field(default_factory=list)
    expected_failures: list[TestResult] = field(default_factory=list)
    counts: Counter = field(default_factory=Counter)

    def gate_failures(self, *, strict_fixed: bool = True) -> bool:
        return bool(self.regressions or self.stale or self.not_run
                    or (strict_fixed and self.unexpected_passes))


def compare(
    results: Iterable[TestResult],
    expectations: Expectations,
    *,
    strict_fixed: bool = True,
    full_run: bool = True,
    known_classes: Iterable[str] | None = None,
    incomplete_classes: Iterable[str] = (),
) -> Comparison:
    """Bucket normalized results against expectations.

    ``stale`` (xfail names never seen, skip names of unknown classes) is only
    computed for full runs. ``incomplete_classes`` were cut short (hang/crash):
    their unseen xfail names count as not-run instead of stale.
    """
    normalized = [normalize(r) for r in results]
    cmp = Comparison(results=normalized)
    incomplete = set(incomplete_classes)
    seen: set[str] = set()
    for r in normalized:
        seen.add(r.name)
        if r.outcome == PASS:
            if r.name in expectations.xfail:
                cmp.unexpected_passes.append(r.name)
                cmp.counts["unexpectedPass"] += 1
            else:
                cmp.counts[PASS] += 1
        elif r.outcome == FAIL:
            if r.name in expectations.xfail:
                cmp.expected_failures.append(r)
                cmp.counts["expectedFail"] += 1
            else:
                cmp.regressions.append(r)
                cmp.counts[FAIL] += 1
        elif r.outcome == NOT_RUN:
            if r.name not in expectations.skip:
                cmp.not_run.append(r.name)
                cmp.counts[NOT_RUN] += 1
        elif r.outcome == HARNESS_FAULT:
            cmp.harness_faults.append(r)
            cmp.counts[HARNESS_FAULT] += 1
        else:
            cmp.counts[r.outcome] += 1
    for name in sorted(expectations.xfail):
        if name in seen:
            continue
        if class_of(name) in incomplete:
            cmp.not_run.append(name)
            cmp.counts[NOT_RUN] += 1
        elif full_run:
            cmp.stale.append(name)
    if full_run and known_classes is not None:
        classes = set(known_classes)
        for name in sorted(expectations.skip):
            if class_of(name) not in classes:
                cmp.stale.append(name)
    cmp.counts["total"] = len(normalized)
    return cmp


@dataclass
class ClassRun:
    name: str
    exit_code: int = 0
    seconds: float = 0.0
    complete: bool = True
    reason: str = ""


def summary_counts(cmp: Comparison) -> dict[str, int]:
    c = cmp.counts
    return {
        "pass": c[PASS], "fail": c[FAIL], "expectedFail": c["expectedFail"],
        "unexpectedPass": c["unexpectedPass"], "knownBug": c[KNOWN_BUG], "skipped": c[SKIPPED],
        "notRun": c[NOT_RUN], "harnessFault": c[HARNESS_FAULT], "total": c["total"],
    }


def render_json(cmp: Comparison, meta: Mapping[str, object], expectations: Expectations | None = None) -> dict:
    xfail = expectations.xfail if expectations else {}
    tests = []
    for r in sorted(cmp.results, key=lambda x: x.name):
        expected = "xfail" if r.name in xfail else "pass"
        tests.append({"name": r.name, "class": r.klass, "area": area_of(r.name),
                      "outcome": r.outcome, "expected": expected, "message": r.message})
    return {"schema": 1, **meta, "summary": summary_counts(cmp), "tests": tests}


def _tail(text: str, lines: int = 12) -> str:
    return "\n".join(text.splitlines()[-lines:])


def render_markdown(
    cmp: Comparison,
    class_runs: Mapping[str, ClassRun],
    *,
    banner: str = "",
) -> str:
    s = summary_counts(cmp)
    out = ["## esctest2 conformance", ""]
    if banner:
        out += [f"> {banner}", ""]
    out += ["| pass | fail (regressions) | expected fail | unexpected pass | known bug | skipped | not run | harness fault | total |",
            "|---|---|---|---|---|---|---|---|---|",
            f"| {s['pass']} | {s['fail']} | {s['expectedFail']} | {s['unexpectedPass']} | {s['knownBug']} "
            f"| {s['skipped']} | {s['notRun']} | {s['harnessFault']} | {s['total']} |", ""]
    if cmp.regressions:
        out += ["### Regressions (failing, not in expected-failures.txt)", ""]
        for r in cmp.regressions:
            out += [f"- `{r.name}`: {r.message}"]
            if r.traceback:
                out += ["", "  ```", *("  " + ln for ln in _tail(r.traceback).splitlines()), "  ```", ""]
    if cmp.unexpected_passes:
        out += ["### Unexpected passes (remove these `xfail` lines)", ""]
        out += [f"- `{n}`" for n in cmp.unexpected_passes] + [""]
    if cmp.stale:
        out += ["### Stale expectation entries (test not found in a full run)", ""]
        out += [f"- `{n}`" for n in cmp.stale] + [""]
    if cmp.not_run:
        out += ["### Not run (hang or crash)", ""]
        out += [f"- `{n}`" for n in cmp.not_run] + [""]
    if cmp.harness_faults:
        out += ["### Harness faults", ""]
        out += [f"- `{r.name}`: {r.message}" for r in cmp.harness_faults[:20]]
        if len(cmp.harness_faults) > 20:
            out.append(f"- ... and {len(cmp.harness_faults) - 20} more")
        out.append("")
    incomplete = [c for c in class_runs.values() if not c.complete]
    if incomplete:
        out += ["### Incomplete classes", ""]
        out += [f"- `{c.name}`: {c.reason or 'no summary line'} (exit {c.exit_code})" for c in incomplete] + [""]
    areas: dict[str, Counter] = {}
    for r in cmp.results:
        areas.setdefault(area_of(r.name), Counter())[r.outcome] += 1
    if areas:
        out += ["### By area", "", "| area | pass | fail | known bug | skipped | other |", "|---|---|---|---|---|---|"]
        for area in sorted(areas):
            c = areas[area]
            other = sum(v for k, v in c.items() if k not in (PASS, FAIL, KNOWN_BUG, SKIPPED))
            out.append(f"| {area} | {c[PASS]} | {c[FAIL]} | {c[KNOWN_BUG]} | {c[SKIPPED]} | {other} |")
        out.append("")
    slow = sorted(class_runs.values(), key=lambda c: c.seconds, reverse=True)[:10]
    if slow:
        out += ["### Slowest classes", "", "| class | seconds |", "|---|---|"]
        out += [f"| {c.name} | {c.seconds:.1f} |" for c in slow] + [""]
    return "\n".join(out)


def dumps_json(data: dict) -> str:
    return json.dumps(data, indent=2, sort_keys=True) + "\n"
