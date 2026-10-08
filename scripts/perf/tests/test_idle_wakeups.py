#!/usr/bin/env python3
import importlib.util
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("idle_wakeups", ROOT / "idle_wakeups.py")
IDLE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(IDLE)


class IdleWakeupsTests(unittest.TestCase):
    def test_parse_context_switches(self):
        status = "Name:\tdotty\nvoluntary_ctxt_switches: 12\nnonvoluntary_ctxt_switches: 3\n"
        self.assertEqual(IDLE.parse_ctxt_switches(status), (12, 3))

    def test_rate(self):
        self.assertEqual(IDLE.rate(10, 40, 6), 5)
        self.assertEqual(IDLE.rate(4, 4, 2), 0)


if __name__ == "__main__":
    unittest.main()
