#!/usr/bin/env python3
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("speedscope_summary", ROOT / "speedscope_summary.py")
SUMMARY = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SUMMARY)


def profile(frames, events, *, start=0, end=4, unit="milliseconds", profile_type="evented", name="p"):
    return {
        "shared": {"frames": [{"name": frame} for frame in frames]},
        "profiles": [{
            "type": profile_type,
            "name": name,
            "unit": unit,
            "startValue": start,
            "endValue": end,
            "events": events,
        }],
    }


def opened_closed(*pairs):
    events = []
    for at, frame in pairs:
        events.append({"type": "O", "frame": frame, "at": at})
    return events


class SpeedscopeSummaryTests(unittest.TestCase):
    def test_exact_nested_weights(self):
        data = profile(
            ["dotty!A", "Dotty.B"],
            [
                {"type": "O", "frame": 0, "at": 0},
                {"type": "O", "frame": 1, "at": 1},
                {"type": "C", "at": 3},
                {"type": "C", "at": 4},
            ],
        )
        result = SUMMARY.analyze(data, top=10)
        self.assertEqual(result["aggregate"]["accounting"]["eligible_dotty"], 4.0)
        self.assertEqual(result["aggregate"]["top_leaf"], [{"frame_id": 0, "name": "dotty!A", "weight": 2.0}, {"frame_id": 1, "name": "Dotty.B", "weight": 2.0}])
        self.assertEqual(result["aggregate"]["top_inclusive"][0]["weight"], 4.0)

    def test_duplicate_frame_ids_count_once_inclusive(self):
        data = profile(
            ["dotty!A"],
            [
                {"type": "O", "frame": 0, "at": 0},
                {"type": "O", "frame": 0, "at": 1},
                {"type": "C", "at": 3},
                {"type": "C", "at": 4},
            ],
        )
        result = SUMMARY.analyze(data)
        self.assertEqual(result["aggregate"]["top_inclusive"], [{"frame_id": 0, "name": "dotty!A", "weight": 4.0}])

    def test_background_exclusion_reason(self):
        data = profile(
            ["dotty!A", "FileSystemWatcher.Wait"],
            [
                {"type": "O", "frame": 0, "at": 0},
                {"type": "O", "frame": 1, "at": 0},
                {"type": "C", "at": 4},
                {"type": "C", "at": 4},
            ],
        )
        result = SUMMARY.analyze(data)
        accounting = result["aggregate"]["accounting"]
        self.assertEqual(accounting["eligible_dotty"], 0.0)
        self.assertEqual(accounting["excluded_background"], 4.0)
        self.assertEqual(result["profiles"][0]["excluded_reasons"], {"watcher/inotify": 4.0})

    def test_generic_thread_does_not_hide_dotty_work(self):
        data = profile(
            ["Thread (42)", "Dotty.Render"],
            [
                {"type": "O", "frame": 0, "at": 0},
                {"type": "O", "frame": 1, "at": 0},
                {"type": "C", "at": 4},
                {"type": "C", "at": 4},
            ],
        )
        self.assertEqual(SUMMARY.analyze(data)["aggregate"]["accounting"]["eligible_dotty"], 4.0)

    def test_malformed_streams_and_bounds(self):
        cases = [
            profile(["dotty!A"], [{"type": "O", "frame": 0, "at": 0}, {"type": "C", "at": 3}], end=4),
            profile(["dotty!A"], [{"type": "C", "at": 0}, {"type": "O", "frame": 0, "at": 4}], end=4),
            profile(["dotty!A"], [{"type": "O", "frame": 2, "at": 0}, {"type": "C", "at": 4}]),
            profile(["dotty!A"], [{"type": "O", "frame": 0, "at": 1}, {"type": "C", "at": 4}]),
            profile(["dotty!A"], [{"type": "O", "frame": 0, "at": 0}, {"type": "C", "at": 4}], start=1),
        ]
        for malformed in cases:
            with self.assertRaises(SUMMARY.SummaryError):
                SUMMARY.analyze(malformed)

    def test_sampled_profile_rejected(self):
        data = profile(["dotty!A"], [], profile_type="sampled")
        with self.assertRaisesRegex(SUMMARY.SummaryError, "sampled"):
            SUMMARY.analyze(data)

    def test_two_profile_aggregation_and_unit_mismatch(self):
        first = profile(["dotty!A"], [{"type": "O", "frame": 0, "at": 0}, {"type": "C", "at": 4}], end=4)
        second = profile(["dotty!A"], [{"type": "O", "frame": 0, "at": 0}, {"type": "C", "at": 2}], end=2)
        data = {"shared": first["shared"], "profiles": first["profiles"] + second["profiles"]}
        result = SUMMARY.analyze(data)
        self.assertEqual(result["profile_count"], 2)
        self.assertEqual(result["aggregate"]["top_leaf"][0]["weight"], 6.0)
        second["profiles"][0]["unit"] = "seconds"
        bad = {"shared": first["shared"], "profiles": first["profiles"] + second["profiles"]}
        with self.assertRaisesRegex(SUMMARY.SummaryError, "consistent unit"):
            SUMMARY.analyze(bad)

    def test_cli_writes_deterministic_json(self):
        data = profile(["dotty!A"], [{"type": "O", "frame": 0, "at": 0}, {"type": "C", "at": 4}])
        with tempfile.TemporaryDirectory() as td:
            source = Path(td) / "in.json"
            output = Path(td) / "out.json"
            source.write_text(json.dumps(data), encoding="utf-8")
            self.assertEqual(SUMMARY.main([str(source), "--json-out", str(output), "--top", "1"]), 0)
            rendered = output.read_text(encoding="utf-8")
            self.assertEqual(json.loads(rendered), SUMMARY.analyze(data, source=str(source), top=1))
            self.assertIn("represented time", rendered)


if __name__ == "__main__":
    unittest.main()
