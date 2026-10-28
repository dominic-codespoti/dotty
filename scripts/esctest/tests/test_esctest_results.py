#!/usr/bin/env python3
"""Unit tests for esctest_results using synthetic logs built from the log
format documented in plan-05 (needs-runtime-verification against real logs)."""
import importlib.util
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("esctest_results", ROOT / "esctest_results.py")
ER = importlib.util.module_from_spec(SPEC)
sys.modules["esctest_results"] = ER  # dataclasses needs the module registered
SPEC.loader.exec_module(ER)

TRACE_FAIL = """\
*** TEST CUUTests.test_CUU_StopsAtTopLine FAILED:
Traceback (most recent call last):
  File "esctest.py", line 100, in RunTest
    test()
  File "tests/cuu.py", line 20, in test_CUU_StopsAtTopLine
    AssertEQ(position.y(), 1)
  File "escutil.py", line 40, in AssertEQ
    raise esc.TestFailure("expected 1 got 3")
esc.TestFailure: expected 1 got 3
"""

TRACE_RESET = """\
*** TEST CUUTests.test_CUU_DefaultParam FAILED:
Traceback (most recent call last):
  File "esctest.py", line 100, in RunTest
    reset()
  File "esctest.py", line 61, in reset
    size = GetScreenSize()
  File "escio.py", line 80, in read
    raise InternalError("Timeout waiting to read.")
esc.InternalError: Timeout waiting to read.
"""

TRACE_SHOULD = """\
*** TEST DECSTRTests.test_DECSTR_Bug FAILED:
Traceback (most recent call last):
  File "esctest.py", line 100, in RunTest
    test()
esc.InternalError: Should have failed
"""


def log(*parts, summary=True):
    text = "".join(parts)
    if summary:
        text += "*** 2 tests passed, 1 known bugs, 1 TESTS FAILED ***\nFailing tests:\nCUUTests.test_CUU_StopsAtTopLine\n"
    return text


def run(name, body):
    return f"Run test: {name}\n{body}"


class ParseLogTests(unittest.TestCase):
    def test_all_outcomes_and_summary(self):
        text = log(
            run("CUUTests.test_CUU_Default", "Passed.\n"),
            run("CUUTests.test_CUU_Other", "Fails as expected: xterm bug\n"),
            run("VtTests.test_Vt", "Skipped because terminal lacks requisite capability: vt level 6\n"),
            run("CUUTests.test_CUU_StopsAtTopLine", TRACE_FAIL),
        )
        results, saw = ER.parse_log(text)
        self.assertTrue(saw)
        self.assertEqual(
            [(r.name, r.outcome) for r in results],
            [("CUUTests.test_CUU_Default", "pass"), ("CUUTests.test_CUU_Other", "known-bug"),
             ("VtTests.test_Vt", "skipped"), ("CUUTests.test_CUU_StopsAtTopLine", "fail")])
        self.assertEqual(results[3].message, "esc.TestFailure: expected 1 got 3")
        self.assertIn("Traceback", results[3].traceback)
        self.assertEqual(results[1].message, "xterm bug")

    def test_truncated_log_marks_last_test_not_run(self):
        text = run("A.test_a", "Passed.\n") + run("A.test_hang", "")
        results, saw = ER.parse_log(text)
        self.assertFalse(saw)
        self.assertEqual([r.outcome for r in results], ["pass", "not-run"])

    def test_missing_outcome_between_tests(self):
        text = run("A.test_a", "") + run("A.test_b", "Passed.\n")
        results, _ = ER.parse_log(text)
        self.assertEqual([(r.name, r.outcome) for r in results], [("A.test_a", "not-run"), ("A.test_b", "pass")])

    def test_failing_tests_list_is_not_reparsed(self):
        text = log(run("A.test_a", "Passed.\n")) + "Run test: B.test_ghost\n"
        results, saw = ER.parse_log(text)
        self.assertTrue(saw)
        self.assertEqual([r.name for r in results], ["A.test_a"])

    def test_empty_log(self):
        self.assertEqual(ER.parse_log(""), ([], False))

    def test_crlf_lines(self):
        results, _ = ER.parse_log("Run test: A.test_a\r\nPassed.\r\n")
        self.assertEqual([r.outcome for r in results], ["pass"])


class NormalizeTests(unittest.TestCase):
    def test_should_have_failed_becomes_pass(self):
        results, _ = ER.parse_log(log(run("DECSTRTests.test_DECSTR_Bug", "") .replace("Run test: DECSTRTests.test_DECSTR_Bug\n", "Run test: DECSTRTests.test_DECSTR_Bug\n") + TRACE_SHOULD))
        r = ER.normalize(results[0])
        self.assertEqual(r.outcome, "pass")
        self.assertIn("xterm", r.message)

    def test_reset_failure_is_harness_fault(self):
        results, _ = ER.parse_log(log(run("CUUTests.test_CUU_DefaultParam", TRACE_RESET)))
        self.assertEqual(ER.normalize(results[0]).outcome, "harness-fault")

    def test_plain_failure_unchanged(self):
        results, _ = ER.parse_log(log(run("CUUTests.test_CUU_StopsAtTopLine", TRACE_FAIL)))
        self.assertEqual(ER.normalize(results[0]).outcome, "fail")


def R(name, outcome, msg=""):
    return ER.TestResult(name, outcome, msg)


class ExpectationsTests(unittest.TestCase):
    def test_parse(self):
        exp = ER.parse_expectations(
            "# header\n# baseline-captured: abc123\n\nxfail A.test_a  # why\nskip B.test_b # hang\n")
        self.assertEqual(exp.xfail, {"A.test_a": "why"})
        self.assertEqual(exp.skip, {"B.test_b": "hang"})
        self.assertEqual(exp.baseline_commit, "abc123")
        self.assertTrue(exp.baseline_captured)

    def test_header_only_is_not_a_baseline(self):
        text = (Path(__file__).resolve().parents[3] / "tests/esctest/expected-failures.txt").read_text()
        exp = ER.parse_expectations(text)
        self.assertFalse(exp.baseline_captured)
        self.assertEqual(exp.xfail, {})

    def test_rejections(self):
        for bad in ("xfail A.test_a\nxfail A.test_a\n", "xfail A.test_a\nskip A.test_a\n",
                    "maybe A.test_a\n", "xfail A.*\n", "xfail\n", "xfail A.test_a extra\n",
                    "# baseline-captured: a\n# baseline-captured: b\n"):
            with self.assertRaises(ValueError, msg=bad):
                ER.parse_expectations(bad)

    def test_round_trip_is_sorted_and_idempotent(self):
        previous = ER.parse_expectations("xfail B.test_b  # keep me\nskip C.test_c  # hang\n")
        results = [R("B.test_b", "fail"), R("A.test_a", "fail"), R("A.test_ok", "pass"), R("D.t", "known-bug")]
        text = ER.render_expectations(results, previous, commit="deadbeef")
        entries = [ln for ln in text.splitlines() if ln.startswith(("xfail", "skip"))]
        self.assertEqual(entries, ["xfail A.test_a  # unclassified", "xfail B.test_b  # keep me",
                                   "skip C.test_c  # hang"])
        exp = ER.parse_expectations(text)
        self.assertEqual(exp.baseline_commit, "deadbeef")
        again = ER.render_expectations(results, exp, commit="deadbeef")
        self.assertEqual(text, again)

    def test_quarantine(self):
        text = ER.render_expectations([], ER.Expectations(), commit="c", quarantine=["X.test_hang"])
        self.assertIn("skip X.test_hang", text)


class CompareTests(unittest.TestCase):
    def setUp(self):
        self.exp = ER.parse_expectations("xfail A.test_known\nxfail A.test_fixed\nxfail Gone.test_gone\nskip Q.test_q\n")

    def test_buckets(self):
        results = [R("A.test_known", "fail", "x"), R("A.test_fixed", "pass"), R("A.test_new", "fail", "boom"),
                   R("A.test_ok", "pass"), R("A.test_nr", "not-run"), R("A.test_kb", "known-bug"),
                   R("A.test_sk", "skipped"), R("Q.test_q", "not-run")]
        cmp = ER.compare(results, self.exp)
        self.assertEqual([r.name for r in cmp.regressions], ["A.test_new"])
        self.assertEqual(cmp.unexpected_passes, ["A.test_fixed"])
        self.assertEqual(cmp.stale, ["Gone.test_gone"])
        self.assertEqual(cmp.not_run, ["A.test_nr"])  # skip-listed not-run is ignored
        self.assertEqual([r.name for r in cmp.expected_failures], ["A.test_known"])
        s = ER.summary_counts(cmp)
        self.assertEqual((s["pass"], s["fail"], s["expectedFail"], s["unexpectedPass"], s["knownBug"],
                          s["skipped"], s["notRun"], s["total"]), (1, 1, 1, 1, 1, 1, 1, 8))
        self.assertTrue(cmp.gate_failures())

    def test_clean_run_has_no_gate_failure(self):
        exp = ER.parse_expectations("xfail A.t\n")
        cmp = ER.compare([R("A.t", "fail")], exp)
        self.assertFalse(cmp.gate_failures())

    def test_non_strict_ignores_unexpected_pass(self):
        exp = ER.parse_expectations("xfail A.t\n")
        cmp = ER.compare([R("A.t", "pass")], exp, strict_fixed=False)
        self.assertFalse(cmp.gate_failures(strict_fixed=False))
        self.assertTrue(cmp.gate_failures())

    def test_partial_run_skips_stale(self):
        cmp = ER.compare([R("A.test_ok", "pass")], self.exp, full_run=False)
        self.assertEqual(cmp.stale, [])

    def test_unknown_skip_class_is_stale(self):
        cmp = ER.compare([], ER.parse_expectations("skip Nope.test_x\n"), known_classes=["A"])
        self.assertEqual(cmp.stale, ["Nope.test_x"])

    def test_incomplete_class_reports_unseen_xfail_as_not_run(self):
        cmp = ER.compare([], ER.parse_expectations("xfail A.t\n"), incomplete_classes=["A"])
        self.assertEqual(cmp.not_run, ["A.t"])
        self.assertEqual(cmp.stale, [])

    def test_normalization_applied(self):
        results, _ = ER.parse_log(log(run("CUUTests.test_CUU_DefaultParam", TRACE_RESET)))
        cmp = ER.compare(results, ER.Expectations())
        self.assertEqual(len(cmp.harness_faults), 1)
        self.assertEqual(cmp.regressions, [])


class RenderTests(unittest.TestCase):
    def test_json_and_markdown(self):
        exp = ER.parse_expectations("xfail DECSTRTests.t\n")
        cmp = ER.compare([R("DECSTRTests.t", "fail", "m"), R("CUUTests.u", "pass"), R("CUUTests.v", "fail", "bad")], exp)
        data = ER.render_json(cmp, {"esctest": {"commit": "c"}}, exp)
        self.assertEqual(data["schema"], 1)
        by_name = {t["name"]: t for t in data["tests"]}
        self.assertEqual(by_name["DECSTRTests.t"]["area"], "vt-sequences:V4")
        self.assertEqual(by_name["DECSTRTests.t"]["expected"], "xfail")
        self.assertEqual(by_name["CUUTests.u"]["area"], "core")
        md = ER.render_markdown(cmp, {"CUUTests": ER.ClassRun("CUUTests", seconds=2.0)}, banner="NOT GATING")
        self.assertIn("NOT GATING", md)
        self.assertIn("Regressions", md)
        self.assertIn("`CUUTests.v`", md)


if __name__ == "__main__":
    unittest.main()
