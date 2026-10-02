import importlib.util
import json
import sys
import tempfile
import unittest
from pathlib import Path

PERF = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PERF))
import nvim_scroll_bench as bench


class NvimScrollBenchTests(unittest.TestCase):
    def test_event_reader_retains_partial_records_without_duplicates(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "events.jsonl"
            reader = bench.EventReader(path)
            reader.update()
            path.write_bytes(b'{"type":"start","monotonic_ns":123}\n{"type":"en')
            reader.update()
            self.assertEqual(reader.events, [{"type": "start", "monotonic_ns": 123}])
            with path.open("ab") as handle:
                handle.write(b'd","steps":9}\n')
            reader.update()
            reader.update()
            self.assertEqual(reader.events, [{"type": "start", "monotonic_ns": 123}, {"type": "end", "steps": 9}])

    def test_missing_visual_measurements_stay_missing_and_warmup_excluded(self):
        runs = [
            {"terminal": "dotty", "warmup": True, "status": "ok", "traversal_ms": 999, "lines_per_second": 1, "visual": {"longest_visible_stall_ms": 999}},
            {"terminal": "dotty", "warmup": False, "status": "partial", "traversal_ms": 100, "lines_per_second": 90, "visual": {"longest_visible_stall_ms": None}},
            {"terminal": "dotty", "warmup": False, "status": "partial", "traversal_ms": 200, "lines_per_second": 45, "visual": {"longest_visible_stall_ms": None}},
        ]
        summary = bench.summarize(runs)["dotty"]
        self.assertEqual(summary["traversal_ms_median"], 150)
        self.assertEqual(summary["lines_per_second_median"], 67.5)
        self.assertEqual(summary["runs"], 2)
        self.assertEqual(summary["status"], "partial")
        self.assertIsNone(summary["longest_visible_stall_ms_max"])
        self.assertIsNone(summary["eof_to_visible_ms_median"])

    def test_skipped_or_failed_runs_cannot_be_reported_as_all_successful(self):
        for state, expected in (("skipped", "partial"), ("failed", "failed")):
            with self.subTest(state=state):
                runs = [{"terminal": "kitty", "warmup": False, "status": "ok", "traversal_ms": 100}, {"terminal": "kitty", "warmup": False, "status": state}]
                self.assertEqual(bench.summarize(runs)["kitty"]["status"], expected)


if __name__ == "__main__":
    unittest.main()
