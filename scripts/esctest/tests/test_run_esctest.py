#!/usr/bin/env python3
"""Unit tests for run_esctest; process execution is replaced by a fake runner
(no dotnet, esctest2 checkout or network needed)."""
import importlib.util
import json
import re
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
SPEC = importlib.util.spec_from_file_location("run_esctest", ROOT / "run_esctest.py")
RUN = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(RUN)
ER = RUN.er

COMMIT = "a" * 40


def make_tree(tmp: Path, classes=("CUUTests", "DECSTRTests")):
    esc = tmp / "esctest2"
    (esc / "esctest" / "tests").mkdir(parents=True)
    (esc / "esctest" / "esctest.py").write_text("")
    (esc / "esctest" / "tests" / "__init__.py").write_text(
        "".join(f"from tests.{c.lower()} import {c}\n" for c in classes))
    cfg = {"repository": "x", "commit": COMMIT, "rows": 25, "columns": 80, "esctestArgs": ["--v", "2"],
           "hostArgs": ["--checksum-reports"], "perClassTimeoutSeconds": 5, "minPassed": 0}
    (tmp / "esctest.json").write_text(json.dumps(cfg))
    return esc, tmp / "esctest.json"


def fake_runner(logs: dict, codes=None):
    codes = codes or {}

    def runner(argv, cwd, timeout, host_log):
        argv = list(argv)
        log_path = Path(argv[argv.index("--logfile") + 1])
        klass = log_path.stem
        if klass in logs:
            log_path.write_text(logs[klass])
        return codes.get(klass, 0)

    return runner


PASS_LOG = "Run test: {c}.test_a\nPassed.\n*** 1 tests passed, 0 known bugs ***\n"
FAIL_LOG = ("Run test: {c}.test_a\n*** TEST {c}.test_a FAILED:\nTraceback (most recent call last):\n"
            "  File \"x.py\", line 1, in test_a\nesc.TestFailure: bad\n*** 0 tests passed, 0 known bugs, 1 TESTS FAILED ***\n")
RESET_LOG = ("Run test: {c}.test_a\n*** TEST {c}.test_a FAILED:\nTraceback (most recent call last):\n"
             "  File \"esctest.py\", line 61, in reset\nesc.InternalError: Timeout waiting to read.\n"
             "*** 0 tests passed, 0 known bugs, 1 TESTS FAILED ***\n")


class StaticTests(unittest.TestCase):
    def test_discover_classes(self):
        text = "import x\nfrom tests.a import ATests\nfrom tests.b import BTests\n# from tests.c import Nope\n"
        self.assertEqual(RUN.discover_classes(text), ["ATests", "BTests"])
        with self.assertRaises(RUN.SetupError):
            RUN.discover_classes("nothing")

    def test_include_regex(self):
        rx = RUN.build_include_regex("DECSTRTests", ["DECSTRTests.test_hang", "Other.test_x", "DECSTRTests.test_a.b"][:2])
        self.assertTrue(re.search(rx, "DECSTRTests.test_ok"))
        self.assertFalse(re.search(rx, "DECSTRTests.test_hang"))
        self.assertTrue(re.search(rx, "DECSTRTests.test_hang2"))
        self.assertFalse(re.search(rx, "OtherTests.test_ok"))
        self.assertFalse(re.search(rx, "XDECSTRTests.test_ok"))
        plain = RUN.build_include_regex("CUUTests")
        self.assertTrue(re.search(plain, "CUUTests.test_anything"))
        user = RUN.build_include_regex("CUUTests", [], "Default")
        self.assertTrue(re.search(user, "CUUTests.test_CUU_Default"))
        self.assertFalse(re.search(user, "CUUTests.test_CUU_Other"))

    def test_class_command(self):
        cmd = RUN.build_class_command(["host"], json.loads('{"rows":25,"columns":80,"perClassTimeoutSeconds":9,'
                                      '"hostArgs":["--window-resize"],"esctestArgs":["--v","2"]}'),
                                      "CUUTests", Path("/o/CUUTests.log"), None, [], None)
        self.assertEqual(cmd[0], "host")
        self.assertIn("--window-resize", cmd)
        self.assertLess(cmd.index("--timeout-seconds"), cmd.index("--"))
        self.assertEqual(cmd[cmd.index("--") + 2], "esctest.py")
        self.assertEqual(cmd[-2:], ["--v", "2"])

    def test_pin_verification(self):
        with tempfile.TemporaryDirectory() as t:
            esc, cfg = make_tree(Path(t))
            config = RUN.load_config(cfg)
            orig = RUN._git
            try:
                RUN._git = lambda args, cwd: "b" * 40
                with self.assertRaises(RUN.SetupError):
                    RUN.ensure_esctest(esc, config)
                RUN._git = lambda args, cwd: COMMIT
                self.assertEqual(RUN.ensure_esctest(esc, config), COMMIT)
            finally:
                RUN._git = orig

    def test_config_requires_full_sha(self):
        with tempfile.TemporaryDirectory() as t:
            esc, cfg = make_tree(Path(t))
            data = json.loads(cfg.read_text())
            data["commit"] = "main"
            cfg.write_text(json.dumps(data))
            with self.assertRaises(RUN.SetupError):
                RUN.load_config(cfg)

    def test_shipped_config_is_valid(self):
        config = RUN.load_config(RUN.DEFAULT_CONFIG)
        self.assertEqual(config["rows"], 25)


class MainTests(unittest.TestCase):
    def run_main(self, logs, expectations_text, *extra, codes=None, classes=("CUUTests", "DECSTRTests")):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        t = Path(tmp.name)
        esc, cfg = make_tree(t, classes)
        exp = t / "expected.txt"
        exp.write_text(expectations_text)
        orig = RUN._git
        RUN._git = lambda args, cwd: COMMIT
        RUN.kill_stragglers = lambda d: None
        try:
            code = RUN.main(["--esctest-dir", str(esc), "--config", str(cfg), "--expectations", str(exp),
                             "--out", str(t / "out"), "--host", "fakehost", "--jobs", "1", *extra],
                            runner=fake_runner(logs, codes))
        finally:
            RUN._git = orig
        return code, t, exp

    def logs(self, **overrides):
        base = {c: PASS_LOG.format(c=c) for c in ("CUUTests", "DECSTRTests")}
        base.update(overrides)
        return base

    BASELINE = "# baseline-captured: abc\n"

    def test_clean_run_exit_0_and_outputs(self):
        code, t, _ = self.run_main(self.logs(), self.BASELINE)
        self.assertEqual(code, 0)
        data = json.loads((t / "out/results.json").read_text())
        self.assertEqual(data["summary"]["pass"], 2)
        self.assertTrue(data["gating"])
        self.assertTrue((t / "out/summary.md").exists())
        self.assertIn("==== CUUTests ====", (t / "out/merged.log").read_text())

    def test_regression_gates_with_baseline(self):
        code, t, _ = self.run_main(self.logs(DECSTRTests=FAIL_LOG.format(c="DECSTRTests")), self.BASELINE)
        self.assertEqual(code, 1)
        self.assertTrue((t / "out/transcripts").is_dir())

    def test_regression_does_not_gate_without_baseline(self):
        code, t, _ = self.run_main(self.logs(DECSTRTests=FAIL_LOG.format(c="DECSTRTests")), "# header only\n")
        self.assertEqual(code, 0)
        self.assertIn("NOT GATING", (t / "out/summary.md").read_text())
        self.assertFalse(json.loads((t / "out/results.json").read_text())["gating"])

    def test_gate_on_overrides_missing_baseline(self):
        code, _, _ = self.run_main(self.logs(DECSTRTests=FAIL_LOG.format(c="DECSTRTests")), "", "--gate", "on")
        self.assertEqual(code, 1)

    def test_unexpected_pass_and_stale(self):
        code, _, _ = self.run_main(self.logs(), self.BASELINE + "xfail DECSTRTests.test_a\n")
        self.assertEqual(code, 1)
        code, _, _ = self.run_main(self.logs(), self.BASELINE + "xfail DECSTRTests.test_a\n", "--no-strict")
        self.assertEqual(code, 0)
        code, _, _ = self.run_main(self.logs(), self.BASELINE + "xfail CUUTests.test_gone\n")
        self.assertEqual(code, 1)

    def test_expected_failure_is_ok(self):
        code, _, _ = self.run_main(self.logs(DECSTRTests=FAIL_LOG.format(c="DECSTRTests")),
                                   self.BASELINE + "xfail DECSTRTests.test_a  # known\n")
        self.assertEqual(code, 0)

    def test_deaf_host_is_harness_fault_even_without_baseline(self):
        code, _, _ = self.run_main(self.logs(CUUTests=RESET_LOG.format(c="CUUTests")), "")
        self.assertEqual(code, 3)

    def test_missing_summary_and_timeout_are_harness_faults(self):
        trunc = "Run test: DECSTRTests.test_a\n"
        code, _, _ = self.run_main(self.logs(DECSTRTests=trunc), self.BASELINE)
        self.assertEqual(code, 3)
        code, _, _ = self.run_main(self.logs(), self.BASELINE, codes={"DECSTRTests": 124})
        self.assertEqual(code, 3)

    def test_canary_must_pass(self):
        code, _, _ = self.run_main(self.logs(CUUTests=FAIL_LOG.format(c="CUUTests")),
                                   self.BASELINE + "xfail CUUTests.test_a\n")
        self.assertEqual(code, 3)

    def test_min_passed(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        evaluated = RUN.evaluate(
            [ER.TestResult("CUUTests.t", "pass")], {"CUUTests": ER.ClassRun("CUUTests")}, ER.Expectations(),
            {"minPassed": 5}, full_run=True, classes=["CUUTests"], strict=True)
        self.assertTrue(any("minPassed" in f for f in evaluated[1]))

    def test_skip_entry_is_excluded_via_include_regex(self):
        seen = []

        def runner(argv, cwd, timeout, host_log):
            argv = list(argv)
            seen.append(argv[argv.index("--include") + 1])
            log_path = Path(argv[argv.index("--logfile") + 1])
            log_path.write_text(PASS_LOG.format(c=log_path.stem))
            return 0

        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        t = Path(tmp.name)
        esc, cfg = make_tree(t)
        exp = t / "e.txt"
        exp.write_text(self.BASELINE + "skip DECSTRTests.test_hang\n")
        orig = RUN._git
        RUN._git = lambda a, c: COMMIT
        RUN.kill_stragglers = lambda d: None
        try:
            code = RUN.main(["--esctest-dir", str(esc), "--config", str(cfg), "--expectations", str(exp),
                             "--out", str(t / "o"), "--host", "h", "--jobs", "1"], runner=runner)
        finally:
            RUN._git = orig
        self.assertEqual(code, 0)
        self.assertTrue(any("test_hang" in s for s in seen))

    def test_update_expectations_writes_baseline(self):
        code, t, exp = self.run_main(self.logs(DECSTRTests=FAIL_LOG.format(c="DECSTRTests")), "",
                                     "--update-expectations")
        self.assertEqual(code, 0)
        parsed = ER.load_expectations(exp)
        self.assertTrue(parsed.baseline_captured)
        self.assertEqual(list(parsed.xfail), ["DECSTRTests.test_a"])

    def test_update_expectations_refuses_harness_fault(self):
        code, _, exp = self.run_main(self.logs(CUUTests=RESET_LOG.format(c="CUUTests")), "# keep\n",
                                     "--update-expectations")
        self.assertEqual(code, 3)
        self.assertEqual(exp.read_text(), "# keep\n")

    def test_update_expectations_refuses_partial_run(self):
        code, _, exp = self.run_main(self.logs(), "# keep\n", "--update-expectations", "--only-class", "CUUTests")
        self.assertEqual(code, 2)
        self.assertEqual(exp.read_text(), "# keep\n")

    def test_update_expectations_refuses_incomplete_class(self):
        hang = "Run test: DECSTRTests.test_hang\n"
        code, _, exp = self.run_main(self.logs(DECSTRTests=hang), "", "--update-expectations", codes={})
        # an incomplete class is a harness fault -> refused
        self.assertEqual(code, 3)

    def test_unknown_class_is_usage_error(self):
        code, _, _ = self.run_main(self.logs(), "", "--only-class", "NopeTests")
        self.assertEqual(code, 2)

    def test_bad_expectations_file_is_usage_error(self):
        code, _, _ = self.run_main(self.logs(), "bogus line here\n")
        self.assertEqual(code, 2)


if __name__ == "__main__":
    unittest.main()
