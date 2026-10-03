import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "perf_baseline.py"
spec = importlib.util.spec_from_file_location("perf_baseline", SCRIPT)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

class PerfBaselineTests(unittest.TestCase):
    def test_throughput_capture_keeps_distribution_and_process_tree_rss(self):
        source = {"kind":"terminal-output-benchmark","status":"ok","metadata":{"effective_args":{"app":"/tmp/release/dotty","lines":100,"runs":3,"sample_interval_ms":10,"include":"dotty"},"host":{"machine":"x64","platform":"linux"},"bytes_written":500},"results":[{"terminal":"dotty","status":"ok","throughput_mb_s":10,"output_ms":20,"peak_tree_rss_mb":40},{"terminal":"dotty","status":"ok","throughput_mb_s":12,"output_ms":18,"peak_tree_rss_mb":42},{"terminal":"dotty","status":"ok","throughput_mb_s":11,"output_ms":19,"peak_tree_rss_mb":41}]}
        with tempfile.TemporaryDirectory() as directory:
            path=Path(directory)/"compare.json"; path.write_text(json.dumps(source))
            snapshot=module.capture(path,"throughput")
        self.assertEqual(snapshot["status"],"complete")
        self.assertEqual(snapshot["runs"]["dotty"]["metrics"]["throughput_mb_s"]["median"],11)
        self.assertEqual(snapshot["runs"]["dotty"]["metrics"]["process_tree_peak_rss_mb"]["n"],3)

    def test_missing_visual_metric_is_partial_not_zero(self):
        source={"status":"partial","config":{"app":"/tmp/dotty","lines":5,"cols":80,"rows":24,"runs":1,"warmup_runs":0,"profile":"plain","capture":"none","sample_hz":60,"display_kind":"real"},"runs":[{"terminal":"dotty","status":"partial","lines_per_second":3,"traversal_ms":10,"process_tree_peak_rss_mb":20,"visual":{"status":"partial","observed_update_gaps_ms":[]}}],"fixture":{},"font":{},"display":{"monitors":[]}}
        with tempfile.TemporaryDirectory() as directory:
            path=Path(directory)/"nvim.json"; path.write_text(json.dumps(source)); snapshot=module.capture(path,"nvim")
        self.assertEqual(snapshot["status"],"partial")
        self.assertEqual(snapshot["runs"]["dotty"]["metrics"]["traversal_ms"]["n"],1)
        self.assertIsNone(snapshot["runs"]["dotty"]["metrics"]["longest_visible_stall_ms"])
    def test_repeated_inputs_pool_samples_and_retain_provenance(self):
        with tempfile.TemporaryDirectory() as directory:
            paths=[]
            for value in (10,11,12,13,14):
                source={"kind":"terminal-output-benchmark","status":"ok","metadata":{"effective_args":{"app":"/tmp/dotty","lines":100,"runs":1,"sample_interval_ms":10,"include":"dotty"},"host":{"machine":"x64","platform":"linux"},"bytes_written":500},"results":[{"terminal":"dotty","status":"ok","throughput_mb_s":value,"output_ms":20,"peak_tree_rss_mb":40}]}
                path=Path(directory)/f"run-{value}.json"; path.write_text(json.dumps(source)); paths.append(path)
            snapshot=module.capture(paths,"throughput")
            self.assertEqual(snapshot["runs"]["dotty"]["metrics"]["throughput_mb_s"]["n"],5)
            self.assertEqual(snapshot["runs"]["dotty"]["metrics"]["throughput_mb_s"]["median"],12)
            self.assertEqual(len(snapshot["sources"]),5)
            source["metadata"]["effective_args"]["lines"]=200
            mismatch=Path(directory)/"mismatch.json"; mismatch.write_text(json.dumps(source))
            with self.assertRaisesRegex(ValueError,"incompatible"):
                module.capture([paths[0],mismatch],"throughput")
    def test_comparison_gates_noisy_or_real_regression_and_rejects_workload_mismatch(self):
        def snap(value, spread=0, compatibility=None):
            metric={"median":value,"mad_ratio":spread,"p95":value,"n":5}
            return {"schema_version":1,"kind":"throughput","status":"complete","compatibility":compatibility or {"lines":500},"runs":{"dotty":{"status":"ok","metrics":{"throughput_mb_s":metric,"output_ms":metric,"process_tree_peak_rss_mb":metric}}}}
        noisy=module.compare(snap(100,.1),snap(90,.1),.05)
        self.assertEqual(noisy["status"],"inconclusive")
        self.assertEqual(noisy["comparisons"]["dotty"]["metrics"]["throughput_mb_s"]["status"],"inconclusive")
        self.assertTrue(noisy["repeat_required"])
        self.assertTrue(noisy["evidence_complete"])
        self.assertEqual(module.compare(snap(100),snap(80),.05)["status"],"regression")
        self.assertEqual(module.compare(snap(100),snap(100,compatibility={"lines":999}),.05)["status"],"incompatible")

if __name__ == "__main__": unittest.main()
