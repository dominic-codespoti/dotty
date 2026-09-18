#!/usr/bin/env python3
import argparse
import importlib.util
import json
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest import mock


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("dotnet_profile", ROOT / "dotnet_profile.py")
PROFILE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PROFILE)


class FakeProc:
    def __init__(self, pid=99, code=0):
        self.pid = pid
        self.returncode = None
        self.code = code
        self.signals = []
        self.terminated = False
        self.killed = False

    def poll(self):
        return self.returncode

    def wait(self, timeout=None):
        self.returncode = self.code
        return self.returncode

    def send_signal(self, signal):
        self.signals.append(signal)
        self.returncode = self.code

    def terminate(self):
        self.terminated = True
        self.returncode = self.code

    def kill(self):
        self.killed = True
        self.returncode = self.code


class DotnetProfileTests(unittest.TestCase):
    def proc(self, root, entries):
        for pid, (ppid, command, maps, rss) in entries.items():
            base = root / str(pid)
            base.mkdir()
            (base / "status").write_text(f"Name:\tx\nPPid:\t{ppid}\nVmRSS:\t{rss} kB\n")
            (base / "cmdline").write_bytes(b"\0".join(part.encode() for part in command) + b"\0")
            (base / "maps").write_text(maps)

    def test_managed_pid_preferred_and_wrapper_rejected(self):
        with tempfile.TemporaryDirectory() as td:
            proc = Path(td)
            self.proc(proc, {
                10: (1, ["/usr/bin/wrapper"], "", 10),
                11: (10, ["/bin/sh", "workload.py"], "libcoreclr.so", 20),
                12: (10, ["/build/Dotty"], "libcoreclr.so", 30),
            })
            self.assertEqual(PROFILE.select_managed_pid(10, "/build/Dotty", proc), 12)

    def test_dotnet_dll_and_ambiguity_failure(self):
        with tempfile.TemporaryDirectory() as td:
            proc = Path(td)
            self.proc(proc, {
                20: (1, ["dotnet", "/x/Dotty.dll"], "libcoreclr.so", 1),
            })
            self.assertEqual(PROFILE.select_managed_pid(20, "/x/Dotty", proc), 20)
            self.proc(proc, {
                21: (1, ["/build/Dotty"], "libcoreclr.so", 1),
                22: (1, ["/build/Dotty"], "libcoreclr.so", 1),
            })
            with self.assertRaises(RuntimeError):
                PROFILE.select_managed_pid(1, "/build/Dotty", proc)

    def test_proc_tree_and_rss(self):
        with tempfile.TemporaryDirectory() as td:
            proc = Path(td)
            self.proc(proc, {
                1: (0, ["/build/Dotty"], "libcoreclr.so", 3),
                2: (1, ["child"], "", 4),
                3: (2, ["grandchild"], "", 5),
            })
            self.assertEqual(PROFILE.proc_descendants(1, proc), [1, 2, 3])
            self.assertEqual(PROFILE.read_rss_bytes(1, proc), 3 * 1024)
            self.assertEqual(PROFILE.sample_rss(1, proc)["tree_bytes"], 12 * 1024)

    def test_all_command_argv(self):
        expected = {
            "cpu": "dotnet-sampled-thread-time",
            "alloc": "gc-verbose",
        }
        with tempfile.TemporaryDirectory() as td:
            for capture, profile in expected.items():
                command = PROFILE.tool_commands(capture, 42, td)[0]
                self.assertEqual(command[:6], ["dotnet-trace", "collect", "--process-id", "42", "--profile", profile])
            counters = PROFILE.tool_commands("counters", 42, td)[0]
            self.assertEqual(counters[counters.index("--counters") + 1], "System.Runtime")
            self.assertEqual(counters.count("--counters"), 1)
            self.assertIn("--format", counters)
            self.assertIn("json", counters)
            gcdump = PROFILE.tool_commands("gcdump", 42, td)[0]
            self.assertEqual(gcdump[:4], ["dotnet-gcdump", "collect", "--process-id", "42"])
            self.assertNotIn("dotnet-alloc", json.dumps([counters, gcdump]))
 
    def test_run_capture_propagates_app_pid_to_discovery_and_sampling(self):
        app = FakeProc(pid=77, code=0)
        collector = FakeProc(pid=88, code=7)
        discovered_roots = []
        sampled_roots = []
 
        def fake_workload(script, marker, gate_path, lines, hold_seconds):
            marker.write_text("READY\nSTART 100\nEND 200\n")

 
        def fake_select(root_pid, app_path, proc_root):
            discovered_roots.append(root_pid)
            return 77
 
        def fake_sample(root_pid, proc_root):
            sampled_roots.append(root_pid)
            return {"root_bytes": 1, "tree_bytes": 1, "timestamp_ns": 1, "pids": [77]}
 
        args = argparse.Namespace(
            app="/build/Dotty",
            lines=10,
            hold_seconds=0,
            startup_timeout=0.1,
            collector_start_delay=0,
        )
        tool = {"name": "dotnet-counters", "path": "/fake/dotnet-counters", "available": True}
        with tempfile.TemporaryDirectory() as td, mock.patch.object(PROFILE, "write_workload", side_effect=fake_workload), mock.patch.object(PROFILE, "select_managed_pid", side_effect=fake_select), mock.patch.object(PROFILE, "process_tree", return_value=[{"pid": 77, "cmdline": ["/build/Dotty"]}]), mock.patch.object(PROFILE, "sample_rss", side_effect=fake_sample), mock.patch.object(PROFILE.subprocess, "Popen", side_effect=[app, collector]):
            result = PROFILE.run_capture("counters", args, Path(td), {"dotnet-counters": tool})
 
        self.assertEqual(result["status"], "failed")
        self.assertTrue(discovered_roots)
        self.assertTrue(all(root == app.pid for root in discovered_roots))
        self.assertTrue(sampled_roots)
        self.assertTrue(all(root == app.pid for root in sampled_roots))
 
    def test_gcdump_report_uses_heapstat_option(self):
        app = FakeProc(pid=77, code=0)
        collector = FakeProc(pid=88, code=0)
        derived_commands = []
 
        def fake_workload(script, marker, gate_path, lines, hold_seconds):
            marker.write_text("READY\nSTART 100\nEND 200\n")

 
        def fake_paths(output_dir, capture):
            folder = output_dir / capture
            folder.mkdir(parents=True, exist_ok=True)
            raw = folder / "heap.gcdump"
            raw.write_bytes(b"dump")
            return {"raw": raw, "report": folder / "heap-stat.txt"}
 
        def fake_derived(command, stdout_path, stderr_path, timeout=30.0):
            derived_commands.append(command)
            return {"command": command, "exit_code": 0, "stdout": str(stdout_path), "stderr": str(stderr_path)}
 
        args = argparse.Namespace(
            app="/build/Dotty",
            lines=10,
            hold_seconds=0,
            startup_timeout=0.1,
            collector_start_delay=0,
        )
        tool = {"name": "dotnet-gcdump", "path": "/fake/dotnet-gcdump", "available": True}
        with tempfile.TemporaryDirectory() as td, mock.patch.object(PROFILE, "write_workload", side_effect=fake_workload), mock.patch.object(PROFILE, "_capture_paths", side_effect=fake_paths), mock.patch.object(PROFILE, "select_managed_pid", return_value=77), mock.patch.object(PROFILE, "process_tree", return_value=[]), mock.patch.object(PROFILE, "sample_rss", return_value={"root_bytes": 1, "tree_bytes": 1, "timestamp_ns": 1, "pids": [77]}), mock.patch.object(PROFILE, "_run_derived", side_effect=fake_derived), mock.patch.object(PROFILE.subprocess, "Popen", side_effect=[app, collector]):
            result = PROFILE.run_capture("gcdump", args, Path(td), {"dotnet-gcdump": tool})
 
        self.assertEqual(result["status"], "ok")
        self.assertEqual(derived_commands, [["/fake/dotnet-gcdump", "report", str(Path(td) / "gcdump" / "heap.gcdump"), "-t", "HeapStat"]])

    def test_counter_duration_formatting_and_command(self):
        self.assertEqual(PROFILE.format_duration(0), "00:00:00:00")
        self.assertEqual(PROFILE.format_duration(1.01), "00:00:00:02")
        self.assertEqual(PROFILE.format_duration(86_400 + 3_661), "01:01:01:01")
        with tempfile.TemporaryDirectory() as td:
            command = PROFILE.tool_commands("counters", 42, td, "00:00:00:17")[0]
        self.assertEqual(command[command.index("--counters") + 1], "System.Runtime")
        self.assertEqual(command.count("--counters"), 1)
        self.assertEqual(command[command.index("--duration") + 1], "00:00:00:17")
        self.assertNotIn("stdin", command)

    def test_counter_artifact_validation_accepts_non_empty_events(self):
        with tempfile.TemporaryDirectory() as td:
            path = Path(td) / "counters.json"
            path.write_text(json.dumps({"Events": [{"timestamp": 1}]}))
            self.assertEqual(PROFILE.validate_counter_artifact(path)["Events"], [{"timestamp": 1}])

    def test_counter_artifact_validation_rejects_malformed_json(self):
        with tempfile.TemporaryDirectory() as td:
            path = Path(td) / "counters.json"
            path.write_text('{"Events": ]}')
            with self.assertRaisesRegex(ValueError, "invalid counters JSON"):
                PROFILE.validate_counter_artifact(path)

    def test_counter_artifact_validation_rejects_empty_events(self):
        with tempfile.TemporaryDirectory() as td:
            path = Path(td) / "counters.json"
            path.write_text(json.dumps({"Events": []}))
            with self.assertRaisesRegex(ValueError, "non-empty Events list"):
                PROFILE.validate_counter_artifact(path)

    def test_counters_natural_exit_registers_json_artifact(self):
        app = FakeProc(pid=77, code=0)
        collector = FakeProc(pid=88, code=0)

        def fake_workload(script, marker, gate_path, lines, hold_seconds):
            marker.write_text("READY\nSTART 100\nEND 200\n")

        def fake_paths(output_dir, capture):
            folder = output_dir / capture
            folder.mkdir(parents=True, exist_ok=True)
            raw = folder / "counters.json"
            raw.write_text(json.dumps({"Events": [{"timestamp": 1}]}))
            return {"raw": raw}
        args = argparse.Namespace(
            app="/build/Dotty",
            lines=10,
            hold_seconds=1,
            startup_timeout=0.1,
            collector_start_delay=0.25,
        )
        tool = {"name": "dotnet-counters", "path": "/fake/dotnet-counters", "available": True}
        with tempfile.TemporaryDirectory() as td, mock.patch.object(
            PROFILE,
            "write_workload",
            side_effect=fake_workload,
        ), mock.patch.object(
            PROFILE,
            "_capture_paths",
            side_effect=fake_paths,
        ), mock.patch.object(
            PROFILE,
            "select_managed_pid",
            return_value=77,
        ), mock.patch.object(
            PROFILE,
            "process_tree",
            return_value=[],
        ), mock.patch.object(
            PROFILE,
            "sample_rss",
            return_value={"root_bytes": 1, "tree_bytes": 1, "timestamp_ns": 1, "pids": [77]},
        ), mock.patch.object(
            PROFILE.subprocess,
            "Popen",
            side_effect=[app, collector],
        ) as popen:
            result = PROFILE.run_capture("counters", args, Path(td), {"dotnet-counters": tool})

        self.assertEqual(result["status"], "ok")
        self.assertEqual(result["artifacts"]["raw"], "counters/counters.json")
        command = popen.call_args_list[1].args[0]
        self.assertEqual(command[command.index("--duration") + 1], "00:00:00:04")
        self.assertEqual(collector.returncode, 0)
        self.assertFalse(collector.terminated)
        self.assertFalse(collector.killed)

    def test_counter_target_hold_exceeds_duration_but_other_captures_keep_requested_hold(self):
        requested_hold = 3.0
        observed_holds = {}
        current_capture = None

        def fake_workload(script, marker, gate_path, lines, hold_seconds):
            observed_holds[current_capture] = hold_seconds
            marker.write_text("READY\nSTART 100\nEND 200\n")

        def fake_paths(output_dir, capture):
            folder = output_dir / capture
            folder.mkdir(parents=True, exist_ok=True)
            raw_name = {
                "cpu": "dotnet-trace.nettrace",
                "alloc": "alloc.nettrace",
                "counters": "counters.json",
                "gcdump": "heap.gcdump",
            }[capture]
            raw = folder / raw_name
            if capture == "counters":
                raw.write_text(json.dumps({"Events": [{"timestamp": 1}]}))
            else:
                raw.write_bytes(b"raw")
            paths = {"raw": raw}
            if capture == "cpu":
                paths.update({"top": folder / "top-methods.txt", "speedscope": folder / "dotnet-trace.speedscope.json"})
            elif capture == "alloc":
                paths["speedscope"] = folder / "alloc.speedscope.json"
            elif capture == "gcdump":
                paths["report"] = folder / "heap-stat.txt"
            return paths

        def fake_derived(command, stdout_path, stderr_path, timeout=30.0):
            return {"command": command, "exit_code": 1, "stdout": str(stdout_path), "stderr": str(stderr_path)}

        args = argparse.Namespace(
            app="/build/Dotty",
            lines=10,
            hold_seconds=requested_hold,
            startup_timeout=0.1,
            collector_start_delay=0.25,
        )
        tools = {
            "cpu": ("dotnet-trace", "/fake/dotnet-trace"),
            "alloc": ("dotnet-trace", "/fake/dotnet-trace"),
            "counters": ("dotnet-counters", "/fake/dotnet-counters"),
            "gcdump": ("dotnet-gcdump", "/fake/dotnet-gcdump"),
        }
        with tempfile.TemporaryDirectory() as td:
            for capture, (tool_name, tool_path) in tools.items():
                current_capture = capture
                app = FakeProc(pid=77, code=0)
                collector = FakeProc(pid=88, code=0)
                tool = {"name": tool_name, "path": tool_path, "available": True}
                with mock.patch.object(PROFILE, "write_workload", side_effect=fake_workload), mock.patch.object(PROFILE, "_capture_paths", side_effect=fake_paths), mock.patch.object(PROFILE, "select_managed_pid", return_value=77), mock.patch.object(PROFILE, "process_tree", return_value=[]), mock.patch.object(PROFILE, "sample_rss", return_value={"root_bytes": 1, "tree_bytes": 1, "timestamp_ns": 1, "pids": [77]}), mock.patch.object(PROFILE, "_run_derived", side_effect=fake_derived), mock.patch.object(PROFILE.subprocess, "Popen", side_effect=[app, collector]):
                    PROFILE.run_capture(capture, args, Path(td), {tool_name: tool})

                if capture == "counters":
                    duration = PROFILE.counter_duration_seconds(
                        args.startup_timeout,
                        args.collector_start_delay,
                        requested_hold,
                    )
                    self.assertGreater(observed_holds[capture], duration)
                else:
                    self.assertEqual(observed_holds[capture], requested_hold)

    def test_trace_conversion_uses_prefix_and_registers_actual_speedscope(self):
        app = FakeProc(pid=77, code=0)
        collector = FakeProc(pid=88, code=0)
        derived_commands = []

        def fake_workload(script, marker, gate_path, lines, hold_seconds):
            marker.write_text("READY\nSTART 100\nEND 200\n")

        def fake_paths(output_dir, capture):
            folder = output_dir / capture
            folder.mkdir(parents=True, exist_ok=True)
            raw = folder / "alloc.nettrace"
            speedscope = folder / "alloc.speedscope.json"
            raw.write_bytes(b"trace")
            return {"raw": raw, "speedscope": speedscope}

        def fake_derived(command, stdout_path, stderr_path, timeout=30.0):
            derived_commands.append(command)
            if command[1] == "convert":
                prefix = Path(command[command.index("--output") + 1])
                prefix.with_name(prefix.name + ".speedscope.json").write_bytes(b"speedscope")
            return {"command": command, "exit_code": 0, "stdout": str(stdout_path), "stderr": str(stderr_path)}

        args = argparse.Namespace(
            app="/build/Dotty",
            lines=10,
            hold_seconds=0,
            startup_timeout=0.1,
            collector_start_delay=0,
        )
        tool = {"name": "dotnet-trace", "path": "/fake/dotnet-trace", "available": True}
        with tempfile.TemporaryDirectory() as td, mock.patch.object(PROFILE, "write_workload", side_effect=fake_workload), mock.patch.object(PROFILE, "_capture_paths", side_effect=fake_paths), mock.patch.object(PROFILE, "select_managed_pid", return_value=77), mock.patch.object(PROFILE, "process_tree", return_value=[]), mock.patch.object(PROFILE, "sample_rss", return_value={"root_bytes": 1, "tree_bytes": 1, "timestamp_ns": 1, "pids": [77]}), mock.patch.object(PROFILE, "_run_derived", side_effect=fake_derived), mock.patch.object(PROFILE.subprocess, "Popen", side_effect=[app, collector]):
            result = PROFILE.run_capture("alloc", args, Path(td), {"dotnet-trace": tool})

        self.assertEqual(result["status"], "ok")
        self.assertEqual(derived_commands[0][derived_commands[0].index("--output") + 1], str(Path(td) / "alloc" / "alloc"))
        self.assertEqual(result["artifacts"]["speedscope"], "alloc/alloc.speedscope.json")

    def test_marker_parsing(self):
        markers = PROFILE.parse_markers("READY\nSTART 100\nEND: 250\n")
        self.assertEqual(markers, {"ready": 1, "start": 100, "end": 250})
        with tempfile.TemporaryDirectory() as td:
            path = Path(td) / "markers"
            path.write_text("123 start\n456 end\n")
            self.assertEqual(PROFILE.parse_marker_file(path), {"start": 123, "end": 456})

    def test_missing_tool_and_status_aggregation(self):
        with mock.patch.object(PROFILE.shutil, "which", return_value=None):
            item = PROFILE.resolve_tool("not-installed-tool")
        self.assertFalse(item["available"])
        self.assertEqual(PROFILE.aggregate_status(["unsupported", "skipped"]), "skipped")
        self.assertEqual(PROFILE.aggregate_status(["ok", "failed"]), "failed")

    def test_artifact_relative_and_outside_rejected(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            path = root / "cpu" / "trace.nettrace"
            path.parent.mkdir()
            path.write_bytes(b"trace")
            artifacts = {}
            self.assertEqual(PROFILE.register_artifact(root, path, artifacts, "raw"), "cpu/trace.nettrace")
            self.assertEqual(artifacts["raw"], "cpu/trace.nettrace")
            with self.assertRaises(ValueError):
                PROFILE.register_artifact(root, Path(td).parent / "outside", {}, "raw")

    def test_stop_process_interrupt_then_terminate_cleanup(self):
        proc = FakeProc(code=0)
        PROFILE.stop_process(proc, interrupt=True)
        self.assertEqual(len(proc.signals), 1)
        stuck = FakeProc(code=0)
        stuck.wait = mock.Mock(side_effect=__import__("subprocess").TimeoutExpired(["x"], 1))
        PROFILE.stop_process(stuck, timeout=0.01)
        self.assertTrue(stuck.killed)


    def test_collector_failure_is_structured_and_cleans_temp_files(self):
        marker_paths = []
        app = FakeProc(pid=10, code=0)
        collector = FakeProc(pid=20, code=7)

        def fake_workload(script, marker, gate_path, lines, hold_seconds):
            marker_paths.append(marker)
            marker.write_text("READY\nSTART 100\nEND 200\n")


        args = argparse.Namespace(
            app="/build/Dotty",
            lines=10,
            hold_seconds=0,
            startup_timeout=0.1,
            collector_start_delay=0,
        )
        tool = {"name": "dotnet-counters", "path": "/fake/dotnet-counters", "available": True}
        with tempfile.TemporaryDirectory() as td, mock.patch.object(PROFILE, "write_workload", side_effect=fake_workload), mock.patch.object(PROFILE, "select_managed_pid", return_value=10), mock.patch.object(PROFILE, "process_tree", return_value=[{"pid": 10, "cmdline": ["/build/Dotty"]}]), mock.patch.object(PROFILE, "sample_rss", return_value={"root_bytes": 1, "tree_bytes": 1, "timestamp_ns": 1, "pids": [10]}), mock.patch.object(PROFILE.subprocess, "Popen", side_effect=[app, collector]):
            result = PROFILE.run_capture("counters", args, Path(td), {"dotnet-counters": tool})
        self.assertEqual(result["status"], "failed")
        self.assertIn("collector exited", result["errors"][0])
        self.assertFalse(collector.signals)
        self.assertFalse(collector.terminated)
        self.assertTrue(app.terminated)
        self.assertFalse(marker_paths[0].exists())
    def test_workload_uses_explicit_gate_path(self):
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            script = root / "workload.py"
            marker = root / "markers.log"
            gate = root / "capture.gate"
            PROFILE.write_workload(script, marker, gate, lines=1, hold_seconds=0)
            generated = script.read_text()
            self.assertIn(f"gate = pathlib.Path({str(gate)!r})", generated)
            self.assertNotIn("marker.with_suffix('.gate')", generated)
            self.assertIn("marker.write_text('READY\\n', encoding='utf-8')", generated)
            self.assertLess(generated.index("marker.write_text('READY"), generated.index("while not gate.exists()"))
            self.assertLess(generated.index("while not gate.exists()"), generated.index("write(f'START"))
            self.assertLess(generated.index("out.flush()"), generated.index("write(f'END"))

if __name__ == "__main__":
    unittest.main()
