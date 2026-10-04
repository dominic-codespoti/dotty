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

    def test_requested_missing_terminal_makes_throughput_capture_partial(self):
        source = {"kind": "terminal-output-benchmark", "status": "ok", "metadata": {"effective_args": {"app": "/tmp/dotty", "lines": 10, "runs": 1, "include": "dotty,kitty"}, "host": {}}, "results": [{"terminal": "dotty", "status": "ok", "throughput_mb_s": 2, "output_ms": 1}]}
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "compare.json"
            path.write_text(json.dumps(source))
            snapshot = module.capture(path, "throughput")
        self.assertEqual(snapshot["status"], "partial")

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
    def test_dotty_build_identity_changes_are_reported_not_discarded(self):
        def snapshot(kind, dotty, metrics):
            return {"schema_version":1,"kind":kind,"status":"complete","compatibility":{**metrics["compatibility"],"terminal_provenance":{"dotty":dotty}},"runs":{"dotty":{"status":"ok","metrics":metrics["metrics"]}}}
        metric={"median":100,"mad_ratio":0,"p95":100,"n":5}
        metrics={"compatibility":{"lines":500,"host_platform":"Linux"},"metrics":{"throughput_mb_s":metric,"output_ms":metric,"process_tree_peak_rss_mb":metric}}
        before={"binary":"/old/dotty","sha256":"old-exe","glfw_native_sha256":{"libglfw.so.3":"old-glfw"},"native_dependency_sha256":{"libglfw.so.3":"old-glfw","libSkiaSharp.so":"same"},"loaded_native_dependency_sha256":{"/usr/lib/libc.so.6":"same"}}
        after={"binary":"/new/dotty","sha256":"new-exe","glfw_native_sha256":{"libglfw.so.3":"new-glfw"},"native_dependency_sha256":{"libglfw.so.3":"new-glfw","libSkiaSharp.so":"same"},"loaded_native_dependency_sha256":{"/usr/lib/libc.so.6":"same"}}
        baseline=snapshot("throughput",before,metrics); candidate=snapshot("throughput",after,metrics)
        result=module.compare(baseline,candidate,.05)
        self.assertEqual(result["status"],"ok")
        self.assertEqual({item["field"] for item in result["build_changes"]},{"terminal_provenance.dotty.binary","terminal_provenance.dotty.sha256","terminal_provenance.dotty.glfw_native_sha256.libglfw.so.3","terminal_provenance.dotty.native_dependency_sha256.libglfw.so.3"})
        self.assertEqual(baseline["compatibility"]["terminal_provenance"]["dotty"]["binary"],"/old/dotty")
        external={**after,"loaded_native_dependency_sha256":{"/usr/lib/libc.so.6":"changed"}}
        candidate=snapshot("throughput",external,metrics)
        self.assertEqual(module.compare(baseline,candidate,.05)["status"],"incompatible")
        workload=snapshot("throughput",after,{**metrics,"compatibility":{"lines":501,"host_platform":"Linux"}})
        self.assertEqual(module.compare(baseline,workload,.05)["status"],"incompatible")

    def test_nvim_dotty_identity_is_controlled_but_capture_cadence_is_not(self):
        metric={"median":100,"mad_ratio":0,"p95":100,"n":5}
        metrics={name:metric for name in ("lines_per_second","traversal_ms","process_tree_peak_rss_mb","longest_visible_stall_ms","eof_to_visible_ms")}
        def snapshot(identity,capture_hz=120):
            return {"schema_version":1,"kind":"nvim","status":"complete","compatibility":{"lines":1000,"capture_hz":capture_hz,"terminal_versions":{"dotty":identity,"kitty":{"binary":"/kitty","sha256":"kitty-hash"}}},"runs":{"dotty":{"status":"ok","metrics":metrics}}}
        before=snapshot({"binary":"/old/dotty","sha256":"old","glfw_native_sha256":{"libglfw.so.3":"old"},"native_dependency_sha256":{"libglfw.so.3":"old","libSkiaSharp.so":"same"},"loaded_native_dependency_sha256":{"/usr/lib/libc.so.6":"same"}})
        after=snapshot({"binary":"/new/dotty","sha256":"new","glfw_native_sha256":{"libglfw.so.3":"new"},"native_dependency_sha256":{"libglfw.so.3":"new","libSkiaSharp.so":"same"},"loaded_native_dependency_sha256":{"/usr/lib/libc.so.6":"same"}})
        result=module.compare(before,after,.05)
        self.assertEqual(result["status"],"ok")
        self.assertEqual(len(result["build_changes"]),4)
        self.assertEqual(module.compare(before,snapshot(after["compatibility"]["terminal_versions"]["dotty"],60),.05)["status"],"incompatible")
        changed_external=snapshot(after["compatibility"]["terminal_versions"]["dotty"])
        changed_external["compatibility"]["terminal_versions"]["kitty"]["sha256"]="other"
        self.assertEqual(module.compare(before,changed_external,.05)["status"],"incompatible")
    def test_capture_requires_uniform_dotty_provenance_within_candidate(self):
        provenance=lambda digest:{"binary":f"/tmp/{digest}/dotty","sha256":digest,"glfw_native_sha256":{"libglfw.so.3":digest},"native_dependency_sha256":{"libglfw.so.3":digest},"loaded_native_dependency_sha256":{"/usr/lib/libc.so.6":"fixed"}}
        with tempfile.TemporaryDirectory() as directory:
            paths=[]
            for index,digest in enumerate(("same","different")):
                source={"kind":"terminal-output-benchmark","status":"ok","metadata":{"effective_args":{"app":"/tmp/dotty","lines":10,"runs":1,"sample_interval_ms":50,"include":"dotty"},"host":{"machine":"x64","platform":"linux"},"terminal_provenance":{"dotty":provenance(digest)},"bytes_written":500},"results":[{"terminal":"dotty","status":"ok","throughput_mb_s":10+index,"output_ms":20,"peak_tree_rss_mb":40}]}
                path=Path(directory)/f"run-{index}.json"; path.write_text(json.dumps(source)); paths.append(path)
            with self.assertRaisesRegex(ValueError,"incompatible"):
                module.capture(paths,"throughput")

    def test_nvim_capture_retains_dotty_provenance_and_capture_hz(self):
        record={"binary":"/tmp/dotty/dotty","sha256":"exe","glfw_native_sha256":{"libglfw.so.3":"glfw"},"native_dependency_sha256":{"libglfw.so.3":"glfw","libSkiaSharp.so":"skia"},"loaded_native_dependency_sha256":{"/usr/lib/libc.so.6":"libc"}}
        source={"status":"ok","config":{"app":"/tmp/dotty/dotty","lines":5,"cols":80,"rows":24,"runs":1,"warmup_runs":0,"profile":"plain","capture":"auto","sample_hz":60,"capture_hz":120,"display_kind":"real","include":"dotty"},"runs":[{"terminal":"dotty","status":"ok","lines_per_second":3,"traversal_ms":10,"process_tree_peak_rss_mb":20,"visual":{"status":"ok","observed_update_gaps_ms":[16],"eof_to_visible_ms":10,"backend":"test"}}],"fixture":{"sha256":"fixture","bytes":100},"font":{},"display":{"monitors":[]},"terminal_versions":{"dotty":record}}
        with tempfile.TemporaryDirectory() as directory:
            path=Path(directory)/"nvim.json"; path.write_text(json.dumps(source)); snapshot=module.capture(path,"nvim")
        self.assertEqual(snapshot["compatibility"]["capture_hz"],120)
        self.assertEqual(snapshot["compatibility"]["terminal_versions"]["dotty"],record)

if __name__ == "__main__": unittest.main()
