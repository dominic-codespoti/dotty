#!/usr/bin/env python3
"""Unit tests for run_esctest; process execution is replaced by a fake runner
(no dotnet, esctest2 checkout or network needed)."""
from pathlib import Path
import importlib.util
import json
import re
import sys
import tempfile
import unittest
from unittest.mock import patch

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


class FilterTests(unittest.TestCase):
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



    def test_gui_routing_preserves_class_and_case_filters(self):
        self.assertEqual(RUN.select_gui_tests(["ManipulateSelectionDataTests", "XtermWinopsTests"], None),
                         list(RUN.GUI_TESTS))
        xterm = RUN.select_gui_tests(["XtermWinopsTests"], None)
        self.assertEqual(len(xterm), 3)
        self.assertEqual(
            RUN.select_gui_tests(["XtermWinopsTests"], "MoveToXY_Defaults"),
            ["XtermWinopsTests.test_XtermWinops_MoveToXY_Defaults"],
        )
        excluded = RUN.build_include_regex("XtermWinopsTests", xterm)
        self.assertFalse(re.search(excluded, xterm[0]))
        self.assertTrue(re.search(excluded, "XtermWinopsTests.test_another_case"))

    def test_report_coverage_detects_duplicates_missing_gui_and_short_full_run(self):
        gui = RUN.GUI_TESTS[0]
        one = ER.TestResult(gui, ER.PASS)
        self.assertEqual(RUN.result_coverage_faults([one], [gui], 1, True), [])
        duplicate = RUN.result_coverage_faults([one, one], [gui], 2, True)
        self.assertTrue(any("duplicate" in fault for fault in duplicate))
        missing = RUN.result_coverage_faults([], [gui], 1, False)
        self.assertTrue(any("produced 0 outcomes" in fault for fault in missing))
        short = RUN.result_coverage_faults([one], [gui], 567, True)
        self.assertTrue(any("expected 567" in fault for fault in short))
class GuiRunnerTests(unittest.TestCase):
    CASE = "XtermWinopsTests.test_XtermWinops_MoveToXY"

    def run_case(self, code=0, *, include_port=True, result=True):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        executable = root / "dotty"
        executable.write_text("native executable placeholder")
        out = root / "artifacts"
        seen = {}

        def runner(argv, cwd, timeout, host_log):
            argv = list(argv)
            seen["argv"] = argv
            seen["timeout"] = timeout
            config_home = next(arg.partition("=")[2] for arg in argv if arg.startswith("DOTTY_CONFIG_HOME="))
            seen["config"] = json.loads((Path(config_home) / "config.json").read_text())
            wrapper_path = next(arg for arg in argv if arg.endswith("run-esctest-child.py"))
            seen["wrapper"] = Path(wrapper_path).read_text()
            openbox_rc = Path(argv[argv.index("dotty-esctest") + 2])
            seen["openbox_rc"] = openbox_rc.read_text()
            host_log.write_text("DOTTY_TEST_PORT=42123\n" if include_port else "startup failed\n")
            log_path = Path(argv[argv.index("--logfile") + 1])
            if result:
                log_path.write_text(
                    f"Run test: {self.CASE}\nPassed.\n*** 1 tests passed, 0 known bugs ***\n"
                )
            return code

        run, results = RUN.run_gui_test(
            self.CASE, [sys.executable, "esctest.py"], root, 5, out, runner,
            executable, {"xvfb-run": "/usr/bin/xvfb-run"},
            {"rows": 25, "columns": 80, "esctestArgs": ["--v", "2"]},
        )
        return root, seen, run, results

    def test_real_gui_command_records_one_upstream_outcome(self):
        root, seen, run, results = self.run_case()
        argv = seen["argv"]
        self.assertTrue(run.complete)
        self.assertEqual([result.name for result in results], [self.CASE])
        self.assertIn("openbox", argv[argv.index("-c") + 1])
        self.assertIn("DOTTY_TEST_PORT=0", argv)
        self.assertIn("HOME=", " ".join(argv))
        dotty = argv.index(str(root / "dotty"))
        self.assertEqual(argv[dotty + 1:dotty + 4],
                         ["--working-directory", str(root), "--"])
        include = argv[argv.index("--include") + 1]
        self.assertEqual(include, "^" + re.escape(self.CASE) + "$")
        self.assertEqual(seen["timeout"], 35)
        self.assertEqual(seen["config"]["window"], {"columns": 80, "rows": 25})
        self.assertTrue(seen["config"]["vt"]["windowControlAllowed"])
        self.assertIn("stdout=None", seen["wrapper"])
        self.assertIn("<focusNew>yes</focusNew>", seen["openbox_rc"])
        self.assertIn("<followMouse>no</followMouse>", seen["openbox_rc"])
        self.assertEqual(seen["config"]["mouse"]["copyOnSelect"], "none")

    def test_nonzero_gui_exit_or_missing_log_is_harness_failure(self):
        _, _, failed_exit, results = self.run_case(code=9)
        self.assertFalse(failed_exit.complete)
        self.assertIn("exited 9", failed_exit.reason)
        self.assertEqual(len(results), 1)
        _, _, missing, results = self.run_case(include_port=False, result=False)
        self.assertFalse(missing.complete)
        self.assertEqual(results, [])
        _, _, timed_out, results = self.run_case(code=124)
        self.assertFalse(timed_out.complete)
        self.assertEqual(timed_out.reason, "timeout")
        self.assertEqual(len(results), 1)
class StaticTests(unittest.TestCase):
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
        self.assertEqual(config["expectedTestCount"], 567)
        self.assertEqual(len(RUN.GUI_TESTS), 4)

    def test_missing_gui_tools_are_a_setup_error_not_a_fallback(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            executable = root / "dotty"
            executable.touch()
            with patch.object(RUN.shutil, "which", return_value=None):
                with self.assertRaisesRegex(RUN.SetupError, "xvfb-run"):
                    RUN.require_gui_prerequisites({"gui": {"executable": "dotty"}}, root)


    def test_native_focus_gate_requires_active_mapped_window(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            commands = {
                "xdotool": "#!/bin/sh\nprintf '4194307\\n'\n",
                "xprop": "#!/bin/sh\nprintf '_NET_ACTIVE_WINDOW(WINDOW): window id # 0x400003\\n'\n",
            }
            tools = {}
            for name, text in commands.items():
                path = root / name
                path.write_text(text, encoding="utf-8")
                path.chmod(0o755)
                tools[name] = str(path)
            self.assertTrue(RUN._native_window_focused(":42", "/tmp/xvfb-cookie", tools))
            (root / "xprop").write_text(
                "#!/bin/sh\nprintf '_NET_ACTIVE_WINDOW(WINDOW): window id # 0x400004\\n'\n",
                encoding="utf-8",
            )
            self.assertFalse(RUN._native_window_focused(":42", "/tmp/xvfb-cookie", tools))
            (root / "xprop").write_text(commands["xprop"], encoding="utf-8")
            (root / "xdotool").write_text(
                "#!/bin/sh\nif [ \"$1\" = search ]; then exit 1; fi\nprintf '4194307\\n'\n",
                encoding="utf-8",
            )
            self.assertFalse(RUN._native_window_focused(":42", "/tmp/xvfb-cookie", tools))

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


    def test_full_run_merges_headless_and_four_serial_gui_outcomes_once(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            classes = ("ManipulateSelectionDataTests", "XtermWinopsTests", "CUUTests")
            esc, cfg = make_tree(root, classes)
            config = json.loads(cfg.read_text())
            config["expectedTestCount"] = 7
            cfg.write_text(json.dumps(config))
            expectations = root / "expected.txt"
            expectations.write_text(self.BASELINE)
            executable = root / "dotty"
            executable.touch()
            out = root / "out"
            observed_gui = []

            def runner(argv, cwd, timeout, host_log):
                argv = list(argv)
                logfile = Path(argv[argv.index("--logfile") + 1])
                if argv[0] == "/usr/bin/xvfb-run":
                    include = argv[argv.index("--include") + 1]
                    test_name = next(name for name in RUN.GUI_TESTS
                                     if include == "^" + re.escape(name) + "$")
                    observed_gui.append(test_name)
                    host_log.write_text("DOTTY_TEST_PORT=41234\n")
                    logfile.write_text(
                        f"Run test: {test_name}\nPassed.\n*** 1 tests passed, 0 known bugs ***\n"
                    )
                else:
                    klass = logfile.stem
                    host_log.write_text("")
                    logfile.write_text(PASS_LOG.format(c=klass))
                return 0

            with patch.object(RUN, "_git", return_value=COMMIT), \
                    patch.object(RUN, "kill_stragglers"), \
                    patch.object(RUN, "require_gui_prerequisites",
                                 return_value=(executable, {"xvfb-run": "/usr/bin/xvfb-run"})):
                code = RUN.main([
                    "--esctest-dir", str(esc), "--config", str(cfg),
                    "--expectations", str(expectations), "--out", str(out),
                    "--host", "fakehost", "--jobs", "2",
                ], runner=runner)

            report = json.loads((out / "results.json").read_text())
            names = [result["name"] for result in report["tests"]]
            self.assertEqual(code, 0)
            self.assertEqual(len(names), 7)
            self.assertEqual(len(set(names)), 7)
            self.assertEqual(observed_gui, list(RUN.GUI_TESTS))
            self.assertEqual(report["lanes"]["headless"]["testCount"], 3)
            self.assertEqual(report["lanes"]["gui"]["testCount"], 4)
            merged = (out / "merged.log").read_text()
            self.assertTrue(all(f"==== GUI {name} ====" in merged for name in RUN.GUI_TESTS))
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
