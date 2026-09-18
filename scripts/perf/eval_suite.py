#!/usr/bin/env python3
"""Consolidated, evidence-first performance evaluation runner.

The runner intentionally treats terminal_output_bench.py and dotnet_profile.py as
independent command line programs.  Their JSON files are the source of truth;
stdout is retained as an audit log but is never parsed as data.
"""
from __future__ import annotations

import argparse
import datetime as _datetime
import json
import os
import platform
import secrets
import statistics
import subprocess
import sys
import time
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parents[2]
PERF_DIR = ROOT / "scripts" / "perf"
TERMINAL_BENCH = PERF_DIR / "terminal_output_bench.py"
DOTNET_PROFILE = PERF_DIR / "dotnet_profile.py"


def repository_root() -> Path:
    """Return the repository containing this script, rather than cwd."""
    return Path(__file__).resolve().parents[2]


def default_app(root: Path | None = None) -> Path:
    root = root or repository_root()
    release = root / "src" / "Dotty" / "bin" / "Release" / "net10.0"
    # Prefer the lowercase apphost convention, with deterministic compatibility
    # for build trees that only contain legacy uppercase apphosts.
    candidates = (
        release / "linux-x64" / "publish" / "dotty",
        release / "dotty",
        release / "linux-x64" / "publish" / "Dotty",
        release / "Dotty",
    )
    return next((candidate for candidate in candidates if candidate.is_file()), candidates[0])


def utc_run_name() -> str:
    stamp = _datetime.datetime.now(_datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    return f"{stamp}-{secrets.token_hex(3)}"


def create_run_dir(output_root: Path) -> Path:
    output_root.mkdir(parents=True, exist_ok=True)
    for _ in range(100):
        candidate = output_root / utc_run_name()
        try:
            candidate.mkdir()
            return candidate
        except FileExistsError:
            continue
    raise RuntimeError(f"could not create a unique run directory under {output_root}")


def _terminate(proc: Any) -> None:
    """Terminate a child, escalating to kill when it does not cooperate."""
    poll = getattr(proc, "poll", None)
    if poll is not None and poll() is not None:
        return
    try:
        proc.terminate()
        proc.wait(timeout=2)
    except (subprocess.TimeoutExpired, TimeoutError):
        try:
            proc.kill()
            proc.wait(timeout=2)
        except (OSError, subprocess.TimeoutExpired, TimeoutError):
            pass
    except OSError:
        pass


def run_child(command: list[str], cwd: Path, stdout_path: Path, stderr_path: Path, timeout: float) -> dict[str, Any]:
    """Run one child with argv-only execution and retained streams."""
    started = time.time_ns()
    proc = None
    try:
        with stdout_path.open("w", encoding="utf-8") as stdout, stderr_path.open("w", encoding="utf-8") as stderr:
            proc = subprocess.Popen(command, cwd=str(cwd), stdout=stdout, stderr=stderr, shell=False)
            try:
                proc.wait(timeout=timeout)
                timed_out = False
            except subprocess.TimeoutExpired:
                timed_out = True
                _terminate(proc)
        return {
            "command": command,
            "exit_code": proc.returncode if proc is not None else None,
            "timed_out": timed_out,
            "duration_ms": (time.time_ns() - started) / 1_000_000,
        }
    except (OSError, ValueError) as exc:
        if proc is not None:
            _terminate(proc)
        return {
            "command": command,
            "exit_code": None,
            "timed_out": False,
            "duration_ms": (time.time_ns() - started) / 1_000_000,
            "error": f"unable to launch child: {exc}",
        }


def _relpath(value: Any, run_dir: Path) -> str | None:
    if value is None:
        return None
    text = str(value)
    path = Path(text)
    if path.is_absolute():
        path = path.resolve()
        run_root = run_dir.resolve()
        try:
            return path.relative_to(run_root).as_posix()
        except ValueError:
            return Path(os.path.relpath(path, run_root)).as_posix()
    return path.as_posix()


def _artifact_path(value: str | Path, run_dir: Path, artifact_root: Path | None) -> str:
    """Return an artifact path relative to the evaluation run directory."""
    candidate = Path(value)
    if candidate.is_absolute():
        return _relpath(candidate, run_dir) or ""

    repo = repository_root().resolve()
    run_root = run_dir.resolve()
    if artifact_root is not None:
        artifact_root = artifact_root.resolve()

    # A child can report a repository-relative path that already contains the
    # run directory.  Treat it as such rather than nesting it below the child
    # output directory.
    try:
        run_prefix = run_root.relative_to(repo)
    except ValueError:
        run_prefix = Path()
    if run_prefix.parts and candidate.parts[: len(run_prefix.parts)] == run_prefix.parts:
        candidate = repo / candidate
    elif artifact_root is not None:
        # Otherwise the child path is relative to its output directory.  A
        # child may also include that directory's run-relative prefix already.
        try:
            artifact_prefix = artifact_root.relative_to(run_root)
        except ValueError:
            artifact_prefix = Path()
        if artifact_prefix.parts and candidate.parts[: len(artifact_prefix.parts)] == artifact_prefix.parts:
            candidate = run_root / candidate
        else:
            candidate = artifact_root / candidate
    return _relpath(candidate, run_root) or ""


def _artifact_values(value: Any, run_dir: Path) -> list[str]:
    if value is None:
        return []
    if isinstance(value, dict):
        if "path" in value:
            return _artifact_values(value["path"], run_dir)
        return [item for child in value.values() for item in _artifact_values(child, run_dir)]
    if isinstance(value, (list, tuple)):
        return [item for child in value for item in _artifact_values(child, run_dir)]
    path = _relpath(value, run_dir)
    return [path] if path else []


def _collect_artifacts(value: Any, run_dir: Path) -> list[str]:
    found: list[str] = []
    if isinstance(value, dict):
        if "artifacts" in value:
            found.extend(_artifact_values(value["artifacts"], run_dir))
        for child in value.values():
            found.extend(_collect_artifacts(child, run_dir))
    elif isinstance(value, list):
        for child in value:
            found.extend(_collect_artifacts(child, run_dir))
    return found


def read_child_json(path: Path, run_dir: Path) -> tuple[dict[str, Any] | None, str | None]:
    try:
        with path.open(encoding="utf-8") as handle:
            value = json.load(handle)
    except FileNotFoundError:
        return None, f"child JSON is missing: {path.relative_to(run_dir).as_posix()}"
    except (OSError, json.JSONDecodeError) as exc:
        return None, f"child JSON is malformed ({path.relative_to(run_dir).as_posix()}): {exc}"
    if not isinstance(value, dict):
        return None, f"child JSON must contain an object: {path.relative_to(run_dir).as_posix()}"
    return value, None


def _normalize_artifacts(value: Any, run_dir: Path, artifact_root: Path | None) -> Any:
    """Normalize every path-like scalar below an ``artifacts`` key."""
    if isinstance(value, dict):
        return {name: _normalize_artifacts(item, run_dir, artifact_root) for name, item in value.items()}
    if isinstance(value, list):
        return [_normalize_artifacts(item, run_dir, artifact_root) for item in value]
    if isinstance(value, tuple):
        return tuple(_normalize_artifacts(item, run_dir, artifact_root) for item in value)
    if isinstance(value, (str, Path)):
        return _artifact_path(value, run_dir, artifact_root)
    return value


def _normalize_payload(value: Any, run_dir: Path, key: str | None = None, artifact_root: Path | None = None) -> Any:
    if key == "artifacts":
        return _normalize_artifacts(value, run_dir, artifact_root)
    if isinstance(value, dict):
        return {name: _normalize_payload(item, run_dir, name, artifact_root) for name, item in value.items()}
    if isinstance(value, list):
        return [_normalize_payload(item, run_dir, artifact_root=artifact_root) for item in value]
    return value


def _component(name: str, command: list[str], json_path: Path, run_dir: Path, timeout: float) -> dict[str, Any]:
    stdout_path = run_dir / f"{name}.stdout.log"
    stderr_path = run_dir / f"{name}.stderr.log"
    execution = run_child(command, ROOT, stdout_path, stderr_path, timeout)
    payload, parse_error = read_child_json(json_path, run_dir)
    errors: list[str] = []
    if execution.get("error"):
        errors.append(execution["error"])
    if execution.get("timed_out"):
        errors.append(f"child timed out after {timeout:g}s")
    if execution.get("exit_code") not in (0, None):
        errors.append(f"child exited with code {execution['exit_code']}")
    if parse_error:
        errors.append(parse_error)
    fatal_error = bool(errors)
    if payload is not None and isinstance(payload.get("errors"), list):
        errors.extend(str(error) for error in payload["errors"])

    status = "ok"
    if fatal_error:
        status = "failed"
    elif payload is not None:
        status = payload.get("status", "ok")
        if status not in {"ok", "partial", "skipped", "failed"}:
            errors.append(f"child returned invalid status: {status!r}")
            status = "failed"
    else:
        status = "failed"

    native = _normalize_payload(payload, run_dir, artifact_root=json_path.parent) if payload is not None else None
    artifacts = [json_path.relative_to(run_dir).as_posix(), stdout_path.relative_to(run_dir).as_posix(), stderr_path.relative_to(run_dir).as_posix()]
    if native:
        artifacts.extend(_collect_artifacts(native, run_dir))
    # Keep ordering stable while avoiding duplicate artifact links.
    artifacts = list(dict.fromkeys(artifacts))
    result: dict[str, Any] = {"status": status, "artifacts": artifacts, "errors": errors, "execution": execution}
    if native is not None:
        result["native"] = native
        result["native_summary"] = native.get("summary", native.get("native_summary"))
    return result


def _host_metadata() -> dict[str, Any]:
    return {
        "platform": platform.platform(),
        "python": platform.python_version(),
        "machine": platform.machine(),
        "processor": platform.processor() or None,
    }


def _git_metadata(root: Path) -> dict[str, Any]:
    metadata: dict[str, Any] = {}
    for key, argv in (("commit", ["git", "rev-parse", "HEAD"]), ("branch", ["git", "branch", "--show-current"])):
        try:
            completed = subprocess.run(argv, cwd=str(root), stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, timeout=5, check=False, shell=False)
            if completed.returncode == 0 and completed.stdout.strip():
                metadata[key] = completed.stdout.strip()
        except (OSError, subprocess.SubprocessError):
            pass
    return metadata


def _add_common(parser: argparse.ArgumentParser, root: Path) -> None:
    parser.add_argument("--output-root", type=Path, default=root / "artifacts" / "perf" / "eval")
    parser.add_argument("--app", default=str(default_app(root)))
    parser.add_argument("--timeout", type=float, default=180.0)


def parser_for(root: Path | None = None) -> argparse.ArgumentParser:
    root = root or repository_root()
    parser = argparse.ArgumentParser(description="Run consolidated Dotty performance evaluations.")
    subparsers = parser.add_subparsers(dest="command", required=True)

    compare = subparsers.add_parser("compare", help="compare Dotty with available terminal competitors")
    _add_common(compare, root)
    compare.add_argument("--runs", type=int, default=3)
    compare.add_argument("--lines", type=int, default=500_000)
    compare.add_argument("--include", default="dotty,kitty,ghostty,wezterm")
    compare.add_argument("--sample-interval-ms", type=float, default=50.0)
    compare.add_argument("--startup-timeout", type=float, default=20.0)

    profile = subparsers.add_parser("profile", help="capture .NET CPU, counters, allocations, and heap")
    _add_common(profile, root)
    profile.add_argument("--profile-lines", type=int, default=500_000)
    profile.add_argument("--captures", default="cpu,counters,alloc,gcdump")
    profile.add_argument("--collector-start-delay", type=float, default=1.0)
    profile.add_argument("--hold-seconds", type=float, default=2.0)

    all_parser = subparsers.add_parser("all", help="run compare and profile")
    _add_common(all_parser, root)
    all_parser.add_argument("--runs", type=int, default=3)
    all_parser.add_argument("--lines", type=int, default=500_000)
    all_parser.add_argument("--include", default="dotty,kitty,ghostty,wezterm")
    all_parser.add_argument("--sample-interval-ms", type=float, default=50.0)
    all_parser.add_argument("--startup-timeout", type=float, default=20.0)
    all_parser.add_argument("--profile-lines", type=int, default=500_000)
    all_parser.add_argument("--captures", default="cpu,counters,alloc,gcdump")
    all_parser.add_argument("--collector-start-delay", type=float, default=1.0)
    all_parser.add_argument("--hold-seconds", type=float, default=2.0)
    return parser


def _validate(args: argparse.Namespace, root: Path) -> list[str]:
    errors = []
    for path, label in ((TERMINAL_BENCH, "terminal benchmark"), (DOTNET_PROFILE, ".NET profiler")):
        if not path.exists():
            errors.append(f"missing {label} script: {path}")
    if not Path(args.app).exists():
        errors.append(f"missing Dotty app: {args.app}")
    if args.timeout <= 0:
        errors.append("--timeout must be positive")
    return errors


def _cli_number(value: int | float) -> str:
    """Format parsed numeric options without a redundant decimal suffix."""
    if isinstance(value, float) and value.is_integer():
        return str(int(value))
    return str(value)


def _compare_command(args: argparse.Namespace, json_path: Path) -> list[str]:
    return [sys.executable, str(TERMINAL_BENCH), "--app", str(args.app), "--json-out", str(json_path), "--runs", _cli_number(args.runs), "--lines", _cli_number(args.lines), "--include", str(args.include), "--sample-interval-ms", _cli_number(args.sample_interval_ms), "--startup-timeout", _cli_number(args.startup_timeout)]


def _profile_command(args: argparse.Namespace, output_dir: Path) -> list[str]:
    return [sys.executable, str(DOTNET_PROFILE), "--app", str(args.app), "--output-dir", str(output_dir), "--lines", _cli_number(args.profile_lines), "--captures", str(args.captures), "--collector-start-delay", _cli_number(args.collector_start_delay), "--hold-seconds", _cli_number(args.hold_seconds)]


def _config(args: argparse.Namespace) -> dict[str, Any]:
    return {key: value for key, value in vars(args).items() if key != "command" and not isinstance(value, Path)} | {"output_root": str(args.output_root), "app": str(args.app)}


def _run_one(name: str, args: argparse.Namespace, run_dir: Path, root: Path) -> dict[str, Any]:
    if name == "compare":
        json_path = run_dir / "compare.json"
        return _component("compare", _compare_command(args, json_path), json_path, run_dir, args.timeout)
    profile_dir = run_dir / "profile"
    profile_dir.mkdir()
    json_path = profile_dir / "profile.json"
    return _component("profile", _profile_command(args, profile_dir), json_path, run_dir, args.timeout)


def _num(mapping: Any, *keys: str) -> float | int | None:
    if not isinstance(mapping, dict):
        return None
    for key in keys:
        value = mapping.get(key)
        if isinstance(value, (int, float)) and not isinstance(value, bool):
            return value
    return None


def _terminal_summary(payload: dict[str, Any] | None) -> dict[str, Any]:
    if not payload:
        return {}
    value = payload.get("summary", payload)
    return value if isinstance(value, dict) else {}


def _rss(summary: dict[str, Any]) -> float | int | None:
    # Prefer process-tree RSS; root-process RSS is the deliberate fallback.
    for key in ("tree_rss_mb_avg", "process_tree_peak_rss_mb_avg", "process_tree_peak_rss_avg_mb", "process_tree_rss_mb_avg"):
        value = _num(summary, key)
        if value is not None:
            return value
    for key in ("tree_rss_bytes_avg", "process_tree_peak_rss_bytes_avg"):
        value = _num(summary, key)
        if value is not None:
            return value / (1024 * 1024)
    process_tree = summary.get("process_tree")
    if isinstance(process_tree, dict):
        nested = _rss(process_tree)
        if nested is not None:
            return nested
    for key in ("root_rss_mb_avg", "peak_rss_mb_avg", "peak_rss_avg_mb"):
        value = _num(summary, key)
        if value is not None:
            return value
    for key in ("root_rss_bytes_avg", "peak_rss_bytes_avg"):
        value = _num(summary, key)
        if value is not None:
            return value / (1024 * 1024)
    return None

def throughput_deficit(dotty: float | None, competitor: float | None) -> float | None:
    if dotty is None or competitor is None or competitor == 0:
        return None
    return 1.0 - (dotty / competitor)


def _comparison_rows(payload: dict[str, Any] | None) -> list[dict[str, Any]]:
    summaries = _terminal_summary(payload)
    dotty = summaries.get("dotty") if isinstance(summaries.get("dotty"), dict) else {}
    dotty_tput = _num(dotty, "throughput_mb_s_avg", "throughput_mean_mb_s", "throughput_mean", "throughput_mb_s")
    rows = []
    for terminal, summary in summaries.items():
        if not isinstance(summary, dict):
            continue
        values = {
            "terminal": terminal,
            "throughput_mean": _num(summary, "throughput_mb_s_avg", "throughput_mean_mb_s", "throughput_mean"),
            "throughput_median": _num(summary, "throughput_mb_s_median", "throughput_median_mb_s", "throughput_median"),
            "throughput_p95": _num(summary, "throughput_mb_s_p95", "throughput_p95_mb_s", "throughput_p95"),
            "output_mean": _num(summary, "output_ms_avg", "output_mean_ms", "output_mean"),
            "output_p95": _num(summary, "output_ms_p95", "output_p95_ms", "output_p95"),
            "rss": _rss(summary),
            "runs": _num(summary, "runs"),
            "status": "skipped" if summary.get("skipped") else "ok",
        }
        values["deficit"] = None if terminal == "dotty" else throughput_deficit(dotty_tput, values["throughput_mean"])
        rows.append(values)
    return rows


def _fmt(value: Any, suffix: str = "") -> str:
    if value is None:
        return "—"
    if isinstance(value, float):
        return f"{value:.2f}{suffix}"
    return f"{value}{suffix}"


def _links(artifacts: Any, report_dir: Path | None = None) -> str:
    base = report_dir or Path(".")
    paths: list[str] = []
    for value in _artifact_values(artifacts, base):
        path = _artifact_path(value, base, None)
        if path and path not in paths:
            paths.append(path)
    return ", ".join(f"[{Path(path).name}]({path})" for path in paths) if paths else "—"


def render_report(summary: dict[str, Any], path: Path) -> None:
    comparison = summary.get("components", {}).get("compare", {}).get("native")
    profile = summary.get("components", {}).get("profile", {}).get("native")
    rows = _comparison_rows(comparison)
    lines = ["# Dotty performance evaluation", "", "## Measured evidence", ""]
    lines.append("This report contains only values supplied by child JSON payloads; missing measurements are shown as —.")
    lines.append("")
    lines.append("## Comparison")
    lines.append("")
    lines.append("| Terminal | Throughput mean (MiB/s) | median | p95 | Output mean (ms) | output p95 (ms) | process-tree peak RSS avg (MiB) | runs | status | Dotty throughput deficit |")
    lines.append("|---|---:|---:|---:|---:|---:|---:|---:|---|---:|")
    for row in rows:
        rss = row["rss"]
        if rss is not None:
            rss = float(rss)
        deficit = "—" if row["deficit"] is None else f"{row['deficit'] * 100:.2f}% (1−Dotty/competitor)"
        lines.append("| {terminal} | {mean} | {median} | {p95} | {out} | {outp95} | {rss} | {runs} | {status} | {deficit} |".format(terminal=row["terminal"], mean=_fmt(row["throughput_mean"]), median=_fmt(row["throughput_median"]), p95=_fmt(row["throughput_p95"]), out=_fmt(row["output_mean"]), outp95=_fmt(row["output_p95"]), rss=_fmt(rss), runs=_fmt(row["runs"]), status=row["status"], deficit=deficit))
    if not rows:
        lines.append("| — | — | — | — | — | — | — | — | unavailable | — |")

    lines.extend(["", "## .NET inspection artifacts", ""])
    if profile:
        artifacts = summary["components"]["profile"].get("artifacts", [])
        lines.append(f"Profiler status: **{summary['components']['profile']['status']}**. {_links(artifacts, path.parent)}")
        psummary = profile.get("summary", profile.get("captures", {})) if isinstance(profile, dict) else {}
        if isinstance(psummary, dict):
            for label in ("counters", "heap", "gcdump", "top_methods", "top-methods"):
                value = psummary.get(label)
                if value is not None:
                    lines.append(f"- {label}: `{json.dumps(value, sort_keys=True)}`")
    else:
        lines.append("Profiler was not requested.")

    lines.extend(["", "## Failures and skips", ""])
    components = summary.get("components", {})
    found_issue = False
    for name, component in components.items():
        status = component.get("status", "unknown")
        errors = component.get("errors", [])
        if status != "ok" or errors:
            found_issue = True
            lines.append(f"- **{name}**: {status}." + (" " + "; ".join(str(item) for item in errors) if errors else ""))
    for row in rows:
        if row["status"] == "skipped":
            terminal_summary = _terminal_summary(comparison).get(row["terminal"], {})
            reasons = terminal_summary.get("reasons", []) if isinstance(terminal_summary, dict) else []
            detail = f" {'; '.join(str(reason) for reason in reasons)}" if reasons else ""
            lines.append(f"- **{row['terminal']}**: skipped.{detail}")
            found_issue = True
    if not found_issue:
        lines.append("- None.")

    lines.extend(["", "## Hypotheses", "", "No hypotheses are asserted by the runner. Investigate causes only from the measured evidence above.", ""])
    path.write_text("\n".join(lines), encoding="utf-8")


def _final_status(components: dict[str, Any]) -> str:
    statuses = [component.get("status") for component in components.values()]
    if any(status == "failed" for status in statuses):
        return "failed"
    if any(status in {"partial", "skipped"} for status in statuses):
        return "partial"
    return "ok"


def run(args: argparse.Namespace) -> tuple[int, Path, dict[str, Any]]:
    root = repository_root()
    validation = _validate(args, root)
    run_dir = create_run_dir(Path(args.output_root))
    components: dict[str, Any] = {}
    errors: list[str] = []
    if validation:
        errors.extend(validation)
        requested = ["compare", "profile"] if args.command == "all" else [args.command]
        for name in requested:
            components[name] = {"status": "failed", "artifacts": [], "errors": validation}
    else:
        if args.command in {"compare", "all"}:
            try:
                components["compare"] = _run_one("compare", args, run_dir, root)
            except Exception as exc:
                components["compare"] = {"status": "failed", "artifacts": [], "errors": [f"compare orchestration failed: {exc}"]}
        if args.command in {"profile", "all"}:
            try:
                components["profile"] = _run_one("profile", args, run_dir, root)
            except Exception as exc:
                components["profile"] = {"status": "failed", "artifacts": [], "errors": [f"profile orchestration failed: {exc}"]}
    summary: dict[str, Any] = {
        "schema_version": 1,
        "kind": "eval_suite",
        "status": _final_status(components),
        "metadata": {"config": _config(args), "host": _host_metadata(), "git": _git_metadata(root)},
        "artifacts": {"summary": "summary.json", "report": "report.md"},
        "components": components,
        "errors": errors + [error for component in components.values() for error in component.get("errors", [])],
    }
    render_report(summary, run_dir / "report.md")
    (run_dir / "summary.json").write_text(json.dumps(summary, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return (1 if any(component.get("status") == "failed" for component in components.values()) else 0), run_dir, summary


def main(argv: list[str] | None = None) -> int:
    args = parser_for().parse_args(argv)
    try:
        code, _, summary = run(args)
    except Exception as exc:
        # Keep CLI failures machine-readable whenever setup itself fails.
        print(json.dumps({"schema_version": 1, "kind": "eval_suite", "status": "failed", "metadata": {}, "artifacts": {}, "errors": [str(exc)]}, indent=2))
        return 1
    print(json.dumps(summary, indent=2, sort_keys=True))
    return code


if __name__ == "__main__":
    raise SystemExit(main())
