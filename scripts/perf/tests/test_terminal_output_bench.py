import contextlib
import importlib.util
import io
import json
import tempfile
import unittest
from argparse import Namespace
from pathlib import Path
from unittest import mock


SOURCE = Path(__file__).resolve().parents[1] / "terminal_output_bench.py"
SPEC = importlib.util.spec_from_file_location("terminal_output_bench", SOURCE)
bench = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(bench)


class TerminalOutputBenchTests(unittest.TestCase):
    def args(self, **overrides):
        values = {
            "runs": 3,
            "lines": 100,
            "sample_interval_ms": 1.0,
            "startup_timeout": 2.0,
            "app": "/tmp/Dotty",
            "include": "dotty,kitty",
            "json_out": None,
        }
        values.update(overrides)
        return Namespace(**values)

    def test_schedule_separates_warmups_rotates_measured_rounds_and_ids(self):
        schedule = bench.build_schedule(["dotty", "kitty", "ghostty"], 1, 3, "session")
        warmups = [item for item in schedule if item["warmup"]]
        measured = [item for item in schedule if not item["warmup"]]
        self.assertEqual([item["terminal"] for item in warmups], ["dotty", "kitty", "ghostty"])
        self.assertEqual([item["terminal"] for item in measured], ["dotty", "kitty", "ghostty", "kitty", "ghostty", "dotty", "ghostty", "dotty", "kitty"])
        self.assertEqual(len({item["run_id"] for item in schedule}), 12)
        self.assertEqual([item["run"] for item in schedule], list(range(1, 13)))
        stats = {"terminal": "dotty", "status": "ok", "skipped": False, "launch_to_child_start_ms": 1, "peak_rss_mb": 1, "peak_tree_rss_mb": 1, "throughput_mb_s": 1}
        summary = bench.summarize([dict(stats, warmup=True, output_ms=900), dict(stats, warmup=False, output_ms=100)])
        self.assertEqual(summary["dotty"]["output_ms_avg"], 100)
    def test_actual_grid_controls_comparison_completeness(self):
        self.assertEqual(bench.compare_grid({"cols": 80, "rows": 24}), (True, None))
        self.assertEqual(bench.compare_grid({"cols": 100, "rows": 24})[0], False)
        self.assertEqual(bench.compare_grid(None)[0], False)

    def test_event_log_retains_child_pty_geometry(self):
        with tempfile.TemporaryDirectory() as directory:
            log = Path(directory) / "events.log"
            log.write_text("10 geometry 24 80\n11 stty 24 80\n12 start\n20 end\n", encoding="utf-8")
            events = bench.read_events(log)
        self.assertEqual(events["geometry"], {"timestamp_ns": 10, "rows": 24, "cols": 80})
        self.assertEqual(events["stty"], {"rows": 24, "cols": 80})
        self.assertEqual(events["start"], 12)

    def test_generated_workload_waits_for_real_pty_geometry_gate(self):
        import fcntl
        import os
        import pty
        import select
        import struct
        import subprocess
        import time
        import termios

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            workload = root / "workload.sh"
            log = root / "events.log"
            go_file = root / "go"
            bench.write_workload(workload)
            master, slave = pty.openpty()
            fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", 24, 80, 0, 0))
            attrs = termios.tcgetattr(slave)
            attrs[1] &= ~termios.OPOST
            termios.tcsetattr(slave, termios.TCSANOW, attrs)
            env = {
                **os.environ,
                "TERMINAL_BENCH_LINES": "1003",
                "TERMINAL_BENCH_LOG": str(log),
                "TERMINAL_BENCH_GO_FILE": str(go_file),
                "TERMINAL_BENCH_STARTUP_TIMEOUT": "3",
                "TERMINAL_BENCH_HOLD_SECONDS": "0.01",
            }
            process = subprocess.Popen(["/bin/sh", str(workload)], stdin=slave, stdout=slave, stderr=subprocess.PIPE, env=env)
            os.close(slave)
            try:
                deadline = time.monotonic() + 2
                while "ready" not in bench.read_events(log) and time.monotonic() < deadline:
                    time.sleep(0.01)
                events = bench.read_events(log)
                self.assertIn("ready", events)
                self.assertEqual((events["geometry"]["rows"], events["geometry"]["cols"]), (24, 80))
                self.assertEqual((events["stty"]["rows"], events["stty"]["cols"]), (24, 80))
                self.assertFalse(select.select([master], [], [], 0.1)[0], "workload wrote output before the parent released its geometry gate")
                go_file.touch()
                line_count = 1003
                output = bytearray()
                expected = bench.LINE.encode("utf-8") * line_count
                self.assertEqual(len(bench.LINE.encode("utf-8")), 55)
                while len(output) < len(expected):
                    readable, _, _ = select.select([master], [], [], 2)
                    self.assertTrue(readable, "gated workload did not produce its configured output")
                    output.extend(os.read(master, 4096))
                self.assertEqual(bytes(output), expected)
                process.wait(timeout=2)
                events = bench.read_events(log)
                self.assertIn("start", events)
                self.assertIn("end", events)
            finally:
                if process.poll() is None:
                    process.kill()
                    process.wait()
                process.stderr.close()
                os.close(master)

    def test_default_app_prefers_lowercase_jit_apphost(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            jit = root / "src" / "Dotty" / "bin" / "Release" / "net10.0" / "dotty"
            jit.parent.mkdir(parents=True)
            jit.write_text("app", encoding="utf-8")
            self.assertEqual(bench.default_app(root), jit)

    def test_default_app_falls_back_to_legacy_uppercase_publish(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            legacy = root / "src" / "Dotty" / "bin" / "Release" / "net10.0" / "linux-x64" / "publish" / "Dotty"
            legacy.parent.mkdir(parents=True)
            legacy.write_text("app", encoding="utf-8")
            self.assertEqual(bench.default_app(root), legacy)


    def test_payload_keeps_legacy_keys_and_adds_schema(self):
        payload = bench.build_payload(self.args(), [])
        self.assertEqual(payload["line_count"], 100)
        self.assertEqual(payload["results"], [])
        self.assertEqual(payload["summary"], {})
        self.assertEqual(payload["schema_version"], 1)
        self.assertEqual(payload["kind"], "terminal-output-benchmark")
        self.assertIn(payload["status"], {"skipped", "partial", "ok", "failed"})
        for key in ("metadata", "artifacts", "errors"):
            self.assertIn(key, payload)

    def test_json_out_round_trip_is_payload(self):
        payload = bench.build_payload(self.args(), [])
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "nested" / "result.json"
            bench.write_json(output, payload)
            self.assertEqual(json.loads(output.read_text(encoding="utf-8")), payload)
    def test_main_json_out_matches_stdout_payload(self):
        result = {
            "terminal": "dotty",
            "run": 1,
            "skipped": False,
            "status": "ok",
            "launch_to_child_start_ms": 1,
            "output_ms": 2,
            "throughput_mb_s": 3,
            "bytes_written": 100,
            "peak_rss_mb": None,
            "peak_tree_rss_mb": None,
        }
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "result.json"
            args = self.args(runs=1, include="dotty", json_out=output)
            stdout = io.StringIO()
            with mock.patch.object(bench, "parse_args", return_value=args), \
                    mock.patch.object(bench, "write_workload"), \
                    mock.patch.object(bench, "run_once", return_value=result), \
                    contextlib.redirect_stdout(stdout):
                bench.main()
            self.assertEqual(json.loads(output.read_text(encoding="utf-8")), json.loads(stdout.getvalue()))

    def test_skip_and_partial_aggregation(self):
        results = [
            {"terminal": "kitty", "run": 1, "skipped": True, "reason": "binary not found"},
            {"terminal": "dotty", "run": 1, "skipped": False, "status": "ok", "launch_to_child_start_ms": 1, "output_ms": 2, "throughput_mb_s": 3, "peak_rss_mb": None, "peak_tree_rss_mb": None},
            {"terminal": "dotty", "run": 2, "skipped": True, "reason": "timed out"},
        ]
        summary = bench.summarize(results)
        self.assertEqual(summary["kitty"]["status"], "skipped")
        self.assertEqual(summary["kitty"]["skip_count"], 1)
        self.assertEqual(summary["dotty"]["status"], "partial")
        self.assertEqual(summary["dotty"]["success_count"], 1)
        self.assertEqual(summary["dotty"]["skip_count"], 1)
        self.assertEqual(summary["dotty"]["partial_reasons"], ["timed out"])

    def test_p95_for_one_and_three_runs(self):
        common = {"terminal": "dotty", "skipped": False, "status": "ok", "launch_to_child_start_ms": 1, "peak_rss_mb": 10, "peak_tree_rss_mb": 20}
        one = dict(common, run=1, output_ms=10, throughput_mb_s=4)
        three = [dict(common, run=index, output_ms=value * 10, throughput_mb_s=value) for index, value in enumerate((1, 2, 3), 1)]
        self.assertEqual(bench.summarize([one])["dotty"]["throughput_mb_s_p95"], 4)
        result = bench.summarize(three)["dotty"]
        self.assertAlmostEqual(result["throughput_mb_s_p95"], 2.9)
        self.assertAlmostEqual(result["output_ms_p95"], 29.0)

    def test_empty_rss_is_safe(self):
        result = {"terminal": "dotty", "run": 1, "skipped": False, "status": "ok", "launch_to_child_start_ms": 1, "output_ms": 2, "throughput_mb_s": 3, "peak_rss_mb": None, "peak_tree_rss_mb": None}
        summary = bench.summarize([result])["dotty"]
        self.assertIsNone(summary["peak_rss_mb_avg"])
        self.assertIsNone(summary["root_rss_mb_avg"])
        self.assertIsNone(summary["tree_rss_mb_avg"])

    def write_proc(self, root, pid, rss, children):
        directory = root / str(pid)
        task = directory / "task" / str(pid)
        task.mkdir(parents=True, exist_ok=True)
        (directory / "status").write_text(f"Name:\ttest\nVmRSS:\t{rss} kB\n", encoding="utf-8")
        (task / "children").write_text(" ".join(str(item) for item in children), encoding="utf-8")

    def test_process_tree_recurses_and_deduplicates(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.write_proc(root, 1, 100, [2, 3])
            self.write_proc(root, 2, 200, [3])
            self.write_proc(root, 3, 300, [])
            self.assertEqual(bench.process_tree_pids(1, root), {1, 2, 3})
            self.assertAlmostEqual(bench.process_tree_rss_mb(1, root), 600 / 1024)

    def test_process_tree_race_returns_null(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.write_proc(root, 1, 100, [2])
            self.write_proc(root, 2, 200, [])
            (root / "2" / "status").unlink()
            self.assertIsNone(bench.process_tree_rss_mb(1, root))

    def test_invalid_positive_arguments(self):
        for option in ("--runs", "--lines", "--sample-interval-ms", "--startup-timeout"):
            with self.subTest(option=option), self.assertRaises(SystemExit):
                bench.parse_args([option, "0"])

    def test_stop_process_waits_after_kill(self):
        class FakeProcess:
            pid = 123
            returncode = None

            def __init__(self):
                self.calls = []

            def poll(self):
                return None

            def terminate(self):
                self.calls.append("terminate")

            def kill(self):
                self.calls.append("kill")

            def wait(self, timeout):
                self.calls.append(("wait", timeout))
                if "kill" not in self.calls:
                    raise bench.subprocess.TimeoutExpired("fake", timeout)
                return 0

        process = FakeProcess()
        self.assertEqual(bench.stop_process(process), [])
        self.assertEqual(process.calls, ["terminate", ("wait", 2), "kill", ("wait", 2)])


if __name__ == "__main__":
    unittest.main()
