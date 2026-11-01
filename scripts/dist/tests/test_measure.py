import importlib.util
import io
import json
import os
import stat
import sys
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from types import SimpleNamespace

SCRIPT = Path(__file__).resolve().parents[1] / "measure.py"
spec = importlib.util.spec_from_file_location("measure", SCRIPT)
module = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = module
spec.loader.exec_module(module)


def make_publish(root: Path, executable: bool = True) -> None:
    (root / "licenses").mkdir(parents=True)
    (root / "licenses" / "GLFW-LICENSE.md").write_text("zlib\n")
    (root / "libglfw.so.3").write_bytes(b"\0" * 1000)
    if executable:
        exe = root / "dotty"
        exe.write_text("#!/bin/sh\necho 'dotty 1.2.3'\n")
        exe.chmod(exe.stat().st_mode | stat.S_IXUSR)


class FileSizeTests(unittest.TestCase):
    def test_sizes_are_relative_sorted_and_summed(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            make_publish(root, executable=False)
            sizes = module.file_sizes(root)
            self.assertEqual(list(sizes), ["libglfw.so.3", "licenses/GLFW-LICENSE.md"])
            self.assertEqual(sizes["libglfw.so.3"], 1000)

    def test_archive_size_is_reproducible_and_compressed(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            make_publish(root, executable=False)
            first = module.archive_size(root, "dotty-x")
            os.utime(root / "libglfw.so.3", (1, 1))
            self.assertEqual(first, module.archive_size(root, "dotty-x"))
            self.assertLess(first, 1000)


class TimingTests(unittest.TestCase):
    def test_median_min_max_of_runs(self):
        ticks = iter([0.0, 0.010, 1.0, 1.030, 2.0, 2.020])
        runner = lambda *a, **k: SimpleNamespace(returncode=0)
        timing = module.time_version(Path("dotty"), 3, runner=runner, clock=lambda: next(ticks))
        self.assertEqual(timing, {"runs": 3, "medianMs": 20.0, "minMs": 10.0, "maxMs": 30.0})

    def test_failing_version_raises(self):
        runner = lambda *a, **k: SimpleNamespace(returncode=3)
        with self.assertRaises(RuntimeError):
            module.time_version(Path("dotty"), 1, runner=runner, clock=lambda: 0.0)

    def test_vm_rss_parsing(self):
        cases = [
            ("Name:\tdotty\nVmRSS:\t   41234 kB\n", 41234),
            ("Name:\tdotty\n", None),
        ]
        for text, expected in cases:
            with self.subTest(text=text):
                self.assertEqual(module.read_vm_rss_kib(text), expected)


class BudgetTests(unittest.TestCase):
    RESULT = {
        "rid": "linux-x64",
        "unpackedBytes": 100,
        "archiveBytes": 50,
        "versionStartMs": {"medianMs": 12.5},
        "versionPeakRssKiB": None,
        "idleRssKiB": None,
    }

    def test_budget_cases(self):
        cases = [
            ("within", {"linux-x64": {"unpackedBytes": 100, "archiveBytes": 60}}, []),
            ("over unpacked", {"linux-x64": {"unpackedBytes": 99}}, ["unpackedBytes 100 exceeds budget 99"]),
            ("over time", {"linux-x64": {"versionStartMsMedian": 10}}, ["versionStartMsMedian 12.5 exceeds budget 10"]),
            ("unmeasured metric ignored", {"linux-x64": {"idleRssKiB": 1}}, []),
            ("no entry for rid", {"osx-arm64": {"unpackedBytes": 1}}, []),
        ]
        for name, budgets, expected in cases:
            with self.subTest(name):
                self.assertEqual(module.check_budgets(self.RESULT, budgets), expected)

    def test_checked_in_budgets_are_well_formed(self):
        budgets = json.loads((SCRIPT.parent / "size-budgets.json").read_text())
        for rid, entry in budgets.items():
            with self.subTest(rid):
                self.assertIn("recorded", entry, "budgets must come from a recorded run")
                for key in module.BUDGET_KEYS:
                    if key in entry:
                        self.assertGreaterEqual(entry[key], entry["recorded"][key])


@unittest.skipUnless(os.name == "posix", "uses a shell-script executable")
class MainTests(unittest.TestCase):
    def run_main(self, *args):
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            code = module.main(list(args))
        return code, out.getvalue(), err.getvalue()

    def test_end_to_end_json_and_summary(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp) / "publish"
            make_publish(root)
            summary = Path(tmp) / "summary.md"
            code, out, _ = self.run_main("--publish-dir", str(root), "--rid", "linux-x64", "--runs", "3", "--summary", str(summary))
            self.assertEqual(code, 0)
            data = json.loads(out)
            self.assertEqual(data["schema"], 1)
            self.assertEqual(data["versionStartMs"]["runs"], 3)
            self.assertEqual(data["unpackedBytes"], sum(data["files"].values()))
            self.assertIn("Distribution size (linux-x64)", summary.read_text())

    def test_budget_failure_exits_one(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp) / "publish"
            make_publish(root)
            budgets = Path(tmp) / "budgets.json"
            budgets.write_text(json.dumps({"linux-x64": {"unpackedBytes": 1}}))
            code, _, err = self.run_main("--publish-dir", str(root), "--rid", "linux-x64", "--runs", "0", "--budgets", str(budgets))
            self.assertEqual(code, 1)
            self.assertIn("exceeds budget", err)

    def test_missing_directory_is_usage_error(self):
        code, _, err = self.run_main("--publish-dir", "/nonexistent/dotty", "--rid", "linux-x64")
        self.assertEqual(code, 2)
        self.assertIn("not a directory", err)


if __name__ == "__main__":
    unittest.main()
