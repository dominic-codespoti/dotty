import importlib.util
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock


PERF = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("dotty_output_bench", PERF / "dotty_output_bench.py")
bench = importlib.util.module_from_spec(SPEC)
with mock.patch.object(sys, "path", [str(PERF), *sys.path]):
    SPEC.loader.exec_module(bench)


class DottyOutputBenchTests(unittest.TestCase):
    def test_payload_and_marker_are_byte_exact_with_partial_final_chunk(self):
        class PrefixSink:
            def __init__(self):
                self.data = bytearray()
                self.flushes = 0

            def write(self, data):
                count = min(12287, len(data))
                self.data.extend(data[:count])
                return count

            def flush(self):
                self.flushes += 1

        sink = PrefixSink()
        marker = b"\r\nDOTTY_BENCH_END_test\r\n"
        counts = bench.write_payload(sink, 2237, marker)
        self.assertEqual(sink.data, bench.LINE * 2237 + marker)
        self.assertEqual(counts["payload_bytes_written"], len(bench.LINE) * 2237)
        self.assertEqual(counts["marker_bytes_written"], len(marker))
        self.assertEqual(counts["write_calls"], 13)
        self.assertEqual(counts["short_writes"], 9)
        self.assertEqual(sink.flushes, 1)

    def test_failed_write_never_claims_complete_payload(self):
        class NoProgress:
            def write(self, data):
                return 0

            def flush(self):
                raise AssertionError("must not flush after a failed payload")
        with self.assertRaises(OSError):
            bench.write_payload(NoProgress(), 1001, b"marker")

    def test_summary_excludes_warmups_failures_and_missing_memory(self):
        results = [
            {"warmup": True, "status": "ok", "output_ms": 999},
            {"warmup": False, "status": "failed", "output_ms": 888},
            {"warmup": False, "status": "ok", "output_ms": 10, "peak_tree_memory_mib": 4},
            {"warmup": False, "status": "partial", "output_ms": 20, "peak_tree_memory_mib": None},
        ]
        summary = bench.summarize(results)
        self.assertEqual(summary["output_ms"], {"count": 2, "mean": 15, "median": 15, "min": 10, "max": 20})
        self.assertEqual(summary["peak_tree_memory_mib"]["count"], 1)
        self.assertEqual(summary["peak_tree_memory_mib"]["median"], 4)
        self.assertIsNone(summary["frame_observed_ms"]["median"])

    def test_unique_directory_never_overwrites_existing_artifacts(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with mock.patch.object(bench.dt, "datetime") as clock, \
                    mock.patch.object(bench.secrets, "token_hex", side_effect=["same", "same", "new"]):
                clock.now.return_value.strftime.return_value = "fixed"
                first = bench.create_run_dir(root)
                sentinel = first / "summary.json"
                sentinel.write_text("preserved", encoding="utf-8")
                second = bench.create_run_dir(root)
            self.assertNotEqual(first, second)
            self.assertEqual(sentinel.read_text(encoding="utf-8"), "preserved")

    def test_marker_observation_handles_configured_narrow_grid(self):
        marker = "DOTTY_BENCH_END_123456789abc"
        dump = "R=3 C=20 CUR=2,0\n" + marker[:20] + "\n" + marker[20:] + "    \n\nEND"
        self.assertTrue(bench.marker_visible(dump, marker))
        self.assertFalse(bench.marker_visible(dump, marker + "wrong"))
        self.assertFalse(bench.marker_visible("R=3 C=20 " + marker + "\nempty\nEND", marker))

    def test_event_reader_ignores_only_incomplete_appends(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "child.jsonl"
            bench.event(path, "start", payload_bytes=55)
            bench.event(path, "end", payload_bytes_written=55)
            with path.open("a", encoding="utf-8") as handle:
                handle.write('{"event":')
            values = bench.events(path)
            self.assertEqual(values["start"]["payload_bytes"], 55)
            self.assertEqual(values["end"]["payload_bytes_written"], 55)
            self.assertGreaterEqual(values["end"]["timestamp_ns"], values["start"]["timestamp_ns"])


if __name__ == "__main__":
    unittest.main()
