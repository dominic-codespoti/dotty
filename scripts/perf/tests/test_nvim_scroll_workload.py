#!/usr/bin/env python3
import json
import os
import shutil
import subprocess
import tempfile
import time
import unittest
from pathlib import Path


WORKLOAD = Path(__file__).resolve().parents[1] / "nvim_scroll_workload.lua"
NVIM = shutil.which("nvim")


@unittest.skipUnless(NVIM, "Neovim is required for workload integration tests")
class NvimScrollWorkloadTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.run_dir = self.root / "run"
        self.run_dir.mkdir()
        self.fixture = self.root / "fixture.c"
        self.process = None

    def tearDown(self):
        if self.process and self.process.poll() is None:
            self.process.kill()
            self.process.wait(timeout=5)
        self.temp.cleanup()

    def _launch(self, lines, expected=None, profile="plain"):
        self.fixture.write_text("".join(f"line {line}\n" for line in range(1, lines + 1)), encoding="utf-8")
        env = os.environ.copy()
        env.update({
            "NVIM_BENCH_RUN_DIR": str(self.run_dir),
            "NVIM_BENCH_LINES": str(expected if expected is not None else lines),
            "NVIM_BENCH_PROFILE": profile,
        })
        command = [NVIM, "--headless", "--clean", "-n", "-i", "NONE",
                   "--cmd", "lua _G.nvim_scroll_bench_open_ns=(vim.uv or vim.loop).hrtime()",
                   str(self.fixture), "-c", f"lua dofile({json.dumps(str(WORKLOAD))})"]
        self.process = subprocess.Popen(command, env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)

    def _wait_for(self, predicate, message):
        deadline = time.monotonic() + 8
        while time.monotonic() < deadline:
            if predicate():
                return
            if self.process.poll() is not None:
                stdout, stderr = self.process.communicate(timeout=1)
                self.fail(f"nvim exited early ({self.process.returncode}):\n{stdout}\n{stderr}")
            time.sleep(0.01)
        self.fail(message)

    def _events(self):
        path = self.run_dir / "events.jsonl"
        if not path.exists():
            return []
        return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line]

    def _finish(self):
        (self.run_dir / "release").touch()
        self.process.communicate(timeout=8)
        return self._events()

    def test_gate_holds_work_then_visits_each_line_and_waits_at_eof_for_release(self):
        self._launch(600)
        self._wait_for(lambda: (self.run_dir / "ready.json").exists(), "nvim did not publish readiness")
        ready = json.loads((self.run_dir / "ready.json").read_text(encoding="utf-8"))
        self.assertEqual(ready["line_count"], 600)
        self.assertGreaterEqual(ready["file_open_to_ready_ns"], 0)
        self.assertGreater(ready["initial_ready_ns"], 0)
        self.assertGreater(ready["cols"], 0)
        self.assertGreater(ready["rows"], 0)
        time.sleep(0.05)
        self.assertEqual(self._events(), [])

        (self.run_dir / "go").touch()
        self._wait_for(lambda: any(event["type"] == "progress" for event in self._events()), "nvim did not traverse")
        self._wait_for(lambda: any(event["type"] == "end" for event in self._events()), "nvim did not reach EOF")
        # End marks the traversal; Neovim remains at EOF until the observer samples and releases it.
        self.assertIsNone(self.process.poll())
        events = self._finish()
        self.assertEqual([event["type"] for event in events], ["start", "progress", "progress", "end"])
        self.assertEqual([(event["line"], event["steps"]) for event in events if event["type"] == "progress"], [(257, 256), (513, 512)])
        end = events[-1]
        self.assertEqual((end["final_line"], end["steps"]), (600, 599))
        self.assertEqual(end["duration_ns"], end["monotonic_ns"] - events[0]["monotonic_ns"])
        self.assertGreater(end["rows"], 0)
        self.assertGreater(end["cols"], 0)

    def test_one_line_fixture_completes_without_movement_and_holds_for_release(self):
        self._launch(1, profile="syntax")
        self._wait_for(lambda: (self.run_dir / "ready.json").exists(), "nvim did not publish readiness")
        (self.run_dir / "go").touch()
        self._wait_for(lambda: any(event["type"] == "end" for event in self._events()), "single line did not reach EOF")
        self.assertIsNone(self.process.poll())
        events = self._finish()
        self.assertEqual([event["type"] for event in events], ["start", "end"])
        self.assertEqual((events[-1]["final_line"], events[-1]["steps"]), (1, 0))

    def test_wrong_fixture_line_count_emits_error_and_exits_nonzero(self):
        self._launch(3, expected=4)
        self.process.communicate(timeout=8)
        self.assertNotEqual(self.process.returncode, 0)
        errors = self._events()
        self.assertEqual([event["type"] for event in errors], ["error"])
        self.assertIn("fixture line count mismatch", errors[0]["message"])


if __name__ == "__main__":
    unittest.main()
