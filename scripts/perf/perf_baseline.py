#!/usr/bin/env python3
"""Capture immutable benchmark snapshots and compare compatible runs."""
from __future__ import annotations
import argparse, datetime as dt, hashlib, json, math, platform, statistics, sys
from pathlib import Path
from typing import Any

SCHEMA = 1
THROUGHPUT = {"throughput_mb_s": ("higher", .05), "output_ms": ("lower", .05), "process_tree_peak_rss_mb": ("lower", .10)}
NVIM = {"lines_per_second": ("higher", .05), "traversal_ms": ("lower", .05), "longest_visible_stall_ms": ("lower", .10), "eof_to_visible_ms": ("lower", .10), "process_tree_peak_rss_mb": ("lower", .10)}

def load(path: Path) -> dict[str, Any]:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict): raise ValueError(f"expected JSON object: {path}")
    return value

def stats(values: list[float]) -> dict[str, float | int]:
    ordered = sorted(values); median = statistics.median(ordered); mad = statistics.median(abs(v - median) for v in ordered)
    return {"n": len(ordered), "median": median, "p95": ordered[min(len(ordered)-1, math.ceil(len(ordered)*.95)-1)], "mad": mad, "mad_ratio": mad / abs(median) if median else 0.0}

def numbers(values: list[Any]) -> list[float]: return [float(x) for x in values if isinstance(x,(int,float)) and math.isfinite(x)]

def capture_one(source: Path, kind: str) -> dict[str, Any]:
    data=load(source); status=data.get("status","unknown")
    if kind == "throughput":
        if data.get("kind") != "terminal-output-benchmark": raise ValueError("throughput capture requires terminal-output-benchmark JSON (compare.json)")
        config=data.get("metadata",{}).get("effective_args",{}); host=data.get("metadata",{}).get("host",{}); raw=data.get("results",[])
        rows={}
        for terminal in sorted({r.get("terminal") for r in raw if r.get("terminal")}):
            runs=[r for r in raw if r.get("terminal")==terminal and not r.get("warmup")]; metrics={}; samples={}
            for metric,field in (("throughput_mb_s","throughput_mb_s"),("output_ms","output_ms"),("process_tree_peak_rss_mb","peak_tree_rss_mb")):
                vals=numbers([r.get(field) for r in runs if r.get("status")=="ok" and not r.get("skipped")]); metrics[metric]=stats(vals) if vals else None; samples[metric]=vals
            rows[terminal]={"status":"ok" if len(runs)==config.get("runs") and runs and all(r.get("status")=="ok" and not r.get("skipped") for r in runs) else "partial","metrics":metrics,"_samples":samples}
        requested=[name.strip() for name in str(config.get("include","")).split(",") if name.strip()]
        complete=status=="ok" and bool(requested) and set(requested).issubset(rows) and all(rows[name]["status"]=="ok" for name in requested)
        app=Path(str(config.get("app","")))
        provenance=data.get("metadata",{}).get("terminal_provenance",{})
        compat={"lines":config.get("lines"),"runs_requested":config.get("runs"),"sample_interval_ms":config.get("sample_interval_ms"),"include":config.get("include"),"bytes_written":data.get("metadata",{}).get("bytes_written"),"build_flavor":"apphost-executable" if app.suffix=="" else "managed-dll","host_machine":host.get("machine"),"host_platform":host.get("platform")}
        compat["terminal_provenance"]={name:provenance.get(name) for name in requested}
        compat["terminal_configs"]={name:next((r.get("terminal_config") for r in raw if r.get("terminal")==name and not r.get("warmup")),None) for name in requested}
        compat["actual_grids"]=sorted({(r.get("terminal"),(r.get("terminal_grid") or {}).get("cols"),(r.get("terminal_grid") or {}).get("rows")) for r in raw if not r.get("warmup")})
        compat["display_protocols"]=sorted({r.get("display_protocol") for r in raw if not r.get("warmup") and r.get("display_protocol")})
        compat["window_backends"]={name:next((r.get("window_backend") for r in raw if r.get("terminal")==name and not r.get("warmup")),None) for name in requested}
        compat["workload_line_sha256"]=data.get("metadata",{}).get("workload_line_sha256")
        compat["benchmark_script_sha256"]=data.get("metadata",{}).get("benchmark_script_sha256")
    else:
        if not isinstance(data.get("config"),dict) or not isinstance(data.get("runs"),list): raise ValueError("nvim capture requires nvim-scroll JSON")
        config=data["config"]; display=data.get("display",{}); grouped={}
        for run in data["runs"]:
            if run.get("terminal"): grouped.setdefault(run["terminal"],[]).append(run)
        rows={}
        for terminal,runs in grouped.items():
            metrics={}; samples={}
            for metric,field in (("lines_per_second","lines_per_second"),("traversal_ms","traversal_ms"),("process_tree_peak_rss_mb","process_tree_peak_rss_mb")):
                vals=numbers([r.get(field) for r in runs if not r.get("warmup")]); metrics[metric]=stats(vals) if vals else None; samples[metric]=vals
            for metric,field in (("longest_visible_stall_ms","observed_update_gaps_ms"),("eof_to_visible_ms","eof_to_visible_ms")):
                vals=[]
                for run in runs:
                    visual=run.get("visual",{})
                    if run.get("warmup") or visual.get("status")!="ok": continue
                    value=visual.get(field)
                    if metric=="longest_visible_stall_ms" and isinstance(value,list):
                        observed=numbers(value)
                        if observed: vals.append(max(observed))
                    elif isinstance(value,(int,float)): vals.append(float(value))
                metrics[metric]=stats(vals) if vals else None; samples[metric]=vals
            measured=[r for r in runs if not r.get("warmup")]
            rows[terminal]={"status":"ok" if len(measured)==config.get("runs") and measured and all(r.get("status")=="ok" for r in measured) else "partial","metrics":metrics,"_samples":samples}
        fixture=data.get("fixture",{}); font=data.get("font",{}); app=Path(str(config.get("app","")))
        compat={"lines":config.get("lines"),"cols":config.get("cols"),"rows":config.get("rows"),"runs_requested":config.get("runs"),"warmup_runs":config.get("warmup_runs"),"profile":config.get("profile"),"capture":config.get("capture"),"sample_hz":config.get("sample_hz"),"display_kind":config.get("display_kind"),"build_flavor":"apphost-executable" if app.suffix=="" else "managed-dll","host_machine":platform.machine(),"host_platform":platform.platform(),"nvim_version":data.get("nvim_version"),"fixture_sha256":fixture.get("sha256"),"fixture_bytes":fixture.get("bytes"),"font":font,"display_geometry":[(m.get("name"),m.get("width"),m.get("height"),m.get("refreshRate"),m.get("scale")) for m in display.get("monitors",[])],"desktop":display.get("desktop"),"display_kind_recorded":display.get("kind")}
        compat["capture_hz"]=config.get("capture_hz")
        compat["terminal_versions"]=data.get("terminal_versions",{})
        compat["actual_grids"]=sorted({(run.get("terminal"),run.get("cols"),run.get("rows")) for run in data["runs"] if not run.get("warmup")})
        compat["capture_backends"]=sorted({run.get("visual",{}).get("backend") for run in data["runs"] if not run.get("warmup") and run.get("visual",{}).get("backend")})
        requested=[name.strip() for name in str(config.get("include","")).split(",") if name.strip()]
        complete=status=="ok" and bool(requested) and set(requested).issubset(rows) and all(rows[name]["status"]=="ok" for name in requested)
        host=display
    return {"schema_version":SCHEMA,"kind":kind,"captured_at_utc":dt.datetime.now(dt.timezone.utc).isoformat(),"source":str(source.resolve()),"source_sha256":hashlib.sha256(source.read_bytes()).hexdigest(),"status":"complete" if complete else "partial","source_status":status,"compatibility":compat,"host":host,"runs":rows}

def capture(sources: list[Path] | Path, kind: str) -> dict[str, Any]:
    if isinstance(sources,Path): sources=[sources]
    if not sources: raise ValueError("at least one --input is required")
    snapshots=[capture_one(path,kind) for path in sources]
    first=snapshots[0]
    if any(s["compatibility"] != first["compatibility"] for s in snapshots[1:]):
        raise ValueError("input files have incompatible workload/build/machine fingerprints")
    terminals=sorted({name for s in snapshots for name in s["runs"]})
    runs={}
    for terminal in terminals:
        metrics={}
        for metric in (THROUGHPUT if kind=="throughput" else NVIM):
            values=[]
            for snapshot in snapshots:
                record=snapshot["runs"].get(terminal,{}).get("metrics",{}).get(metric)
                if not record: continue
                values.extend(snapshot["runs"][terminal].get("_samples",{}).get(metric,[record["median"]]))
            metrics[metric]=stats(values) if values else None
        complete=all(s["runs"].get(terminal,{}).get("status")=="ok" for s in snapshots)
        runs[terminal]={"status":"ok" if complete else "partial","metrics":metrics}
    complete=all(s["status"]=="complete" for s in snapshots) and all(r["status"]=="ok" for r in runs.values())
    return {"schema_version":SCHEMA,"kind":kind,"captured_at_utc":dt.datetime.now(dt.timezone.utc).isoformat(),"sources":[{"path":s["source"],"sha256":s["source_sha256"],"status":s["source_status"]} for s in snapshots],"status":"complete" if complete else "partial","source_status":"ok" if complete else "partial","compatibility":first["compatibility"],"host":first["host"],"runs":runs}
def _controlled_build_identity_paths(kind: str) -> set[tuple[str, ...]]:
    """Fields allowed to vary only between baseline and candidate Dotty builds."""
    root = ("terminal_provenance", "dotty") if kind == "throughput" else ("terminal_versions", "dotty")
    return {root + ("binary",), root + ("sha256",), root + ("glfw_native_sha256", "libglfw.so.3"), root + ("native_dependency_sha256", "libglfw.so.3")}

def _compatibility_changes(old: Any, new: Any, path: tuple[str, ...] = ()) -> list[tuple[tuple[str, ...], Any, Any]]:
    if isinstance(old, dict) and isinstance(new, dict):
        changes = []
        for key in sorted(old.keys() | new.keys()):
            if key not in old or key not in new:
                changes.append((path + (key,), old.get(key), new.get(key)))
            else:
                changes.extend(_compatibility_changes(old[key], new[key], path + (key,)))
        return changes
    if old != new:
        return [(path, old, new)]
    return []
def compare(old:dict[str,Any],new:dict[str,Any],noise:float)->dict[str,Any]:
    issues=[]
    for label,key in (("schema version","schema_version"),("benchmark kind","kind")):
        if old.get(key)!=new.get(key): issues.append(f"incompatible {label}")
    changes = _compatibility_changes(old.get("compatibility"), new.get("compatibility"))
    allowed = _controlled_build_identity_paths(old.get("kind")) if old.get("kind") in ("throughput", "nvim") and old.get("kind") == new.get("kind") else set()
    uncontrolled = [change for change in changes if change[0] not in allowed]
    build_changes = [{"field":".".join(path),"baseline":before,"candidate":after} for path,before,after in changes if path in allowed]
    if uncontrolled: issues.append("incompatible workload/build/machine compatibility")
    if issues:return {"status":"incompatible","issues":issues,"comparisons":{},"build_changes":build_changes,"evidence_complete":False,"repeat_required":True}
    specs=THROUGHPUT if old["kind"]=="throughput" else NVIM; comparisons={}
    for name in sorted(set(old.get("runs",{}))|set(new.get("runs",{}))):
        before,after=old.get("runs",{}).get(name),new.get("runs",{}).get(name)
        if not before or not after: comparisons[name]={"status":"missing-run","metrics":{}}; continue
        metrics={}
        for metric,(direction,threshold) in specs.items():
            a=before.get("metrics",{}).get(metric); b=after.get("metrics",{}).get(metric)
            if not a or not b:metrics[metric]={"status":"unavailable"}; continue
            base,current=a["median"],b["median"]; change=(current-base)/abs(base) if base else None
            noise_limit=max(noise,a["mad_ratio"]+b["mad_ratio"])
            regression=change is not None and (change < -threshold if direction=="higher" else change > threshold)
            metrics[metric]={"status":"regression" if regression and abs(change)>noise_limit else "inconclusive" if regression else "ok","baseline":base,"current":current,"relative_change":change,"threshold":threshold,"noise_floor":noise_limit,"direction":direction}
        comparisons[name]={"status":"partial" if before.get("status")!="ok" or after.get("status")!="ok" else "ok","metrics":metrics}
    failed=any(v.get("status")=="regression" for r in comparisons.values() for v in r["metrics"].values())
    inconclusive=any(v.get("status")=="inconclusive" for r in comparisons.values() for v in r["metrics"].values())
    partial=old.get("status")!="complete" or new.get("status")!="complete" or any(r["status"]!="ok" or any(v.get("status")=="unavailable" for v in r["metrics"].values()) for r in comparisons.values())
    return {"status":"regression" if failed else "inconclusive" if inconclusive else "partial" if partial else "ok","issues":[],"comparisons":comparisons,"build_changes":build_changes,"evidence_complete":not partial,"repeat_required":partial or inconclusive}

def main()->int:
    parser=argparse.ArgumentParser(description=__doc__); sub=parser.add_subparsers(dest="command",required=True)
    c=sub.add_parser("capture",help="capture normalized snapshot from existing benchmark JSON"); c.add_argument("--kind",choices=("throughput","nvim"),required=True); c.add_argument("--input",type=Path,action="append",required=True,help="source JSON; repeat to pool compatible runs"); c.add_argument("--output",type=Path,required=True)
    d=sub.add_parser("compare",help="compare compatible snapshots"); d.add_argument("--baseline",type=Path,required=True); d.add_argument("--candidate",type=Path,required=True); d.add_argument("--noise-floor",type=float,default=.05); d.add_argument("--json-out",type=Path)
    a=parser.parse_args()
    try:
        if a.command=="capture":
            result=capture(a.input,a.kind); a.output.parent.mkdir(parents=True,exist_ok=True); a.output.write_text(json.dumps(result,indent=2,sort_keys=True)+"\n",encoding="utf-8"); print(json.dumps({"status":result["status"],"kind":result["kind"],"inputs":len(a.input),"output":str(a.output)})); return 0 if result["status"]=="complete" else 2
        if not 0<=a.noise_floor<1:raise ValueError("--noise-floor must be in [0,1)")
        result=compare(load(a.baseline),load(a.candidate),a.noise_floor); text=json.dumps(result,indent=2,sort_keys=True)+"\n"
        if a.json_out:a.json_out.parent.mkdir(parents=True,exist_ok=True); a.json_out.write_text(text,encoding="utf-8")
        print(text,end=""); return 1 if result["status"] in ("incompatible","regression") else 2 if result["status"] in ("partial","inconclusive") else 0
    except (OSError,ValueError,json.JSONDecodeError) as exc: print(f"perf_baseline: {exc}",file=sys.stderr); return 2
if __name__=="__main__":raise SystemExit(main())
