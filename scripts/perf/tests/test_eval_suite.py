#!/usr/bin/env python3
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path
from unittest import mock


MODULE_PATH = Path(__file__).resolve().parents[1] / "eval_suite.py"
SPEC = importlib.util.spec_from_file_location("eval_suite_under_test", MODULE_PATH)
EVAL = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(EVAL)


class EvalSuiteTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.app = self.root / "Dotty"
        self.app.write_text("app", encoding="utf-8")
        self.bench = self.root / "terminal_output_bench.py"
        self.profiler = self.root / "dotnet_profile.py"
        self.bench.write_text("", encoding="utf-8")
        self.profiler.write_text("", encoding="utf-8")
        self.output = self.root / "runs"
        self.patches = mock.patch.multiple(
            EVAL,
            TERMINAL_BENCH=self.bench,
            DOTNET_PROFILE=self.profiler,
            ROOT=self.root,
            repository_root=mock.Mock(return_value=self.root),
            _host_metadata=mock.Mock(return_value={}),
            _git_metadata=mock.Mock(return_value={}),
        )
        self.patches.start()
        self.addCleanup(self.patches.stop)
        self.commands = []

    def tearDown(self):
        self.temp.cleanup()

    def _payload(self, status="ok", summary=None, artifacts=None):
        return {
            "schema_version": 1,
            "kind": "child",
            "status": status,
            "metadata": {},
            "artifacts": artifacts or [],
            "summary": summary or {},
            "errors": [],
        }

    def _fake_child(self, command, cwd, stdout_path, stderr_path, timeout):
        self.commands.append((command, timeout))
        stdout_path.write_text("child stdout\n", encoding="utf-8")
        stderr_path.write_text("child stderr\n", encoding="utf-8")
        if "terminal_output_bench.py" in command[1]:
            path = Path(command[command.index("--json-out") + 1])
            path.write_text(json.dumps(self._payload(summary={
                "dotty": {"runs": 2, "throughput_mb_s_avg": 80.0, "throughput_mb_s_median": 79.0,
                          "throughput_mb_s_p95": 85.0, "output_ms_avg": 10.0, "output_ms_p95": 11.0,
                          "process_tree_peak_rss_mb_avg": 42.0},
                "kitty": {"skipped": True, "reasons": ["binary not found"]},
            })), encoding="utf-8")
        else:
            output_dir = Path(command[command.index("--output-dir") + 1])
            (output_dir / "cpu.nettrace").write_text("trace", encoding="utf-8")
            (output_dir / "profile.json").write_text(json.dumps(self._payload(
                artifacts=[str(output_dir / "cpu.nettrace")],
                summary={"counters": {"cpu": 1}, "heap": {"objects": 2}, "top_methods": [{"name": "Main", "percent": 50}]},
            )), encoding="utf-8")
        return {"command": command, "exit_code": 0, "timed_out": False, "duration_ms": 1.0}

    def _args(self, argv):
        return EVAL.parser_for(self.root).parse_args(argv)

    def test_compare_forwards_argv_and_keeps_skips_nonfatal(self):
        args = self._args(["compare", "--output-root", str(self.output), "--app", str(self.app), "--timeout", "9", "--runs", "4", "--lines", "12", "--include", "dotty,kitty", "--sample-interval-ms", "7.5", "--startup-timeout", "8"])
        with mock.patch.object(EVAL, "run_child", side_effect=self._fake_child):
            code, run_dir, summary = EVAL.run(args)
        self.assertEqual(code, 0)
        self.assertEqual(summary["status"], "ok")
        command, timeout = self.commands[0]
        self.assertEqual(timeout, 9.0)
        self.assertEqual(command, [
            EVAL.sys.executable, str(self.bench), "--app", str(self.app), "--json-out", str(run_dir / "compare.json"),
            "--runs", "4", "--lines", "12", "--include", "dotty,kitty", "--sample-interval-ms", "7.5", "--startup-timeout", "8",
        ])
        self.assertIn("kitty", (run_dir / "report.md").read_text(encoding="utf-8"))
        self.assertTrue((run_dir / "summary.json").exists())

    def test_profile_forwards_argv_and_preserves_relative_links(self):
        args = self._args(["profile", "--output-root", str(self.output), "--app", str(self.app), "--profile-lines", "33", "--captures", "cpu,alloc", "--collector-start-delay", "2.5", "--hold-seconds", "4"])
        with mock.patch.object(EVAL, "run_child", side_effect=self._fake_child):
            code, run_dir, summary = EVAL.run(args)
        self.assertEqual(code, 0)
        command, _ = self.commands[0]
        self.assertEqual(command[command.index("--lines") + 1], "33")
        self.assertEqual(command[command.index("--captures") + 1], "cpu,alloc")
        self.assertEqual(command[command.index("--collector-start-delay") + 1], "2.5")
        self.assertEqual(command[command.index("--hold-seconds") + 1], "4")
        self.assertIn("profile/cpu.nettrace", summary["components"]["profile"]["artifacts"])
        report = (run_dir / "report.md").read_text(encoding="utf-8")
        self.assertIn("[cpu.nettrace](profile/cpu.nettrace)", report)
        self.assertIn('"cpu": 1', report)

    def test_all_continues_after_failed_compare(self):
        calls = []

        def fake(command, cwd, stdout, stderr, timeout):
            calls.append(command[1])
            stdout.write_text("", encoding="utf-8")
            stderr.write_text("collector failed", encoding="utf-8")
            if "terminal_output_bench.py" not in command[1]:
                output_dir = Path(command[command.index("--output-dir") + 1])
                (output_dir / "profile.json").write_text(json.dumps(self._payload()), encoding="utf-8")
            return {"command": command, "exit_code": 3 if "terminal_output_bench.py" in command[1] else 0, "timed_out": False, "duration_ms": 1}

        args = self._args(["all", "--output-root", str(self.output), "--app", str(self.app)])
        with mock.patch.object(EVAL, "run_child", side_effect=fake):
            code, run_dir, summary = EVAL.run(args)
        self.assertEqual(code, 1)
        self.assertEqual(len(calls), 2)
        self.assertEqual(summary["components"]["compare"]["status"], "failed")
        self.assertEqual(summary["components"]["profile"]["status"], "ok")
        self.assertIn("Failures and skips", (run_dir / "report.md").read_text(encoding="utf-8"))

    def test_unique_run_paths(self):
        first = EVAL.create_run_dir(self.output)
        second = EVAL.create_run_dir(self.output)
        self.assertNotEqual(first, second)
        self.assertRegex(first.name, r"^\d{8}T\d{6}Z-[0-9a-f]{6}$")

    def test_timeout_nonzero_and_malformed_json_are_failed(self):
        json_path = self.root / "child.json"
        json_path.write_text("{not json", encoding="utf-8")
        stdout = self.root / "stdout.log"
        stderr = self.root / "stderr.log"
        with mock.patch.object(EVAL, "run_child", return_value={"exit_code": 2, "timed_out": True, "duration_ms": 1}):
            result = EVAL._component("compare", ["child"], json_path, self.root, 3)
        self.assertEqual(result["status"], "failed")
        self.assertTrue(any("timed out" in error for error in result["errors"]))
        self.assertTrue(any("malformed" in error for error in result["errors"]))
        self.assertTrue(stdout.exists() is False)
        self.assertTrue(stderr.exists() is False)

    def test_deficit_math_and_rss_process_tree_preference(self):
        self.assertAlmostEqual(EVAL.throughput_deficit(75, 100), 0.25)
        self.assertIsNone(EVAL.throughput_deficit(None, 100))
        payload = {"summary": {
            "dotty": {"throughput_mb_s_avg": 75, "tree_rss_mb_avg": 300.26, "root_rss_mb_avg": 284.28},
            "kitty": {"throughput_mb_s_avg": 100, "peak_rss_mb_avg": 30},
        }}
        rows = EVAL._comparison_rows(payload)
        dotty = next(row for row in rows if row["terminal"] == "dotty")
        kitty = next(row for row in rows if row["terminal"] == "kitty")
        self.assertEqual(dotty["rss"], 300.26)
        self.assertAlmostEqual(kitty["deficit"], 0.25)
        self.assertEqual(kitty["rss"], 30)

    def test_normalize_payload_does_not_duplicate_repository_run_prefix(self):
        run_dir = self.root / "artifacts" / "perf" / "eval" / "run"
        payload = {"artifacts": ["artifacts/perf/eval/run/compare.json"]}
        normalized = EVAL._normalize_payload(payload, run_dir, artifact_root=run_dir)
        self.assertEqual(normalized["artifacts"], ["compare.json"])

    def test_nested_profile_artifacts_use_one_run_relative_link(self):
        run_dir = self.root / "artifacts" / "perf" / "eval" / "run"
        output_dir = run_dir / "profile"
        output_dir.mkdir(parents=True)
        trace = output_dir / "cpu" / "dotnet-trace.nettrace"
        trace.parent.mkdir()
        trace.write_text("trace", encoding="utf-8")
        repository_path = "artifacts/perf/eval/run/profile/cpu/dotnet-trace.nettrace"
        payload = self._payload(artifacts={
            "cpu": {
                "raw": "cpu/dotnet-trace.nettrace",
                "absolute": str(trace),
                "repository": repository_path,
            },
        })
        payload["captures"] = [{"artifacts": {"raw": "cpu/dotnet-trace.nettrace"}}]
        json_path = output_dir / "profile.json"
        json_path.write_text(json.dumps(payload), encoding="utf-8")

        def fake_child(command, cwd, stdout_path, stderr_path, timeout):
            stdout_path.write_text("", encoding="utf-8")
            stderr_path.write_text("", encoding="utf-8")
            return {"command": command, "exit_code": 0, "timed_out": False, "duration_ms": 1.0}

        with mock.patch.object(EVAL, "run_child", side_effect=fake_child):
            component = EVAL._component("profile", ["profile"], json_path, run_dir, 3)

        canonical = "profile/cpu/dotnet-trace.nettrace"
        self.assertEqual(component["artifacts"].count(canonical), 1)
        self.assertNotIn("cpu/dotnet-trace.nettrace", component["artifacts"])
        self.assertNotIn(repository_path, component["artifacts"])
        report_path = run_dir / "report.md"
        report_component = dict(component)
        report_component["artifacts"] = [canonical, str(trace), repository_path]
        EVAL.render_report({"components": {"profile": report_component}}, report_path)
        report = report_path.read_text(encoding="utf-8")
        self.assertEqual(report.count(f"]({canonical})"), 1)

    def test_partial_components_do_not_set_nonzero_exit(self):
        self.assertEqual(EVAL._final_status({"compare": {"status": "partial"}}), "partial")
        self.assertEqual(EVAL._final_status({"compare": {"status": "failed"}}), "failed")
        self.assertEqual(EVAL._final_status({"compare": {"status": "ok"}}), "ok")


if __name__ == "__main__":
    unittest.main()
