#!/usr/bin/env python3
"""Summarize evented Speedscope profiles by represented time.

The analyzer deliberately reports represented interval weights.  Those weights are
not CPU utilization: evented traces include blocked and background activity.
"""

from __future__ import annotations

import argparse
import json
import math
import re
import sys
import unicodedata
from collections import defaultdict
from pathlib import Path
from typing import Any, Iterable, Mapping, Sequence


class SummaryError(ValueError):
    """Raised when a Speedscope input does not satisfy the evented schema."""

_BACKGROUND_PATTERNS: tuple[tuple[str, re.Pattern[str]], ...] = (
    (
        "profile_root",
        re.compile(r"^(?:root|profile root|process\d+.*|threads?|\(non-activities\))$", re.I),
    ),
    ("watcher/inotify", re.compile(r"(?:file.?watch|watcher|inotify)", re.I)),
    ("socket/network", re.compile(r"(?:socket|network|epoll|select|accept|connect)", re.I)),
    ("poll/futex", re.compile(r"(?:\bpoll\b|\bfutex\b|spinwait|spin\s*wait)", re.I)),
    ("wait/semaphore/sleep/delay", re.compile(r"(?:\bwait(?:one|any|all)?\b|semaphore|\bsleep\b|\bdelay\b)", re.I)),
    ("io/pipe", re.compile(r"(?:\bpipe\b|\b(?:read|write)(?:async|file)?\b|io\s*wait)", re.I)),
    ("finalizer/gc/threadpool/timer", re.compile(r"(?:finalizer|garbage.?collector|\bgc\b|thread.?pool|\btimer\b)", re.I)),
)


def _error(message: str) -> SummaryError:
    return SummaryError(message)


def _number(value: Any, where: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise _error(f"{where} must be a finite number")
    result = float(value)
    if not math.isfinite(result):
        raise _error(f"{where} must be a finite number")
    return result


def _frame_id(value: Any, where: str, frame_count: int) -> int:
    if isinstance(value, bool) or not isinstance(value, int):
        raise _error(f"{where} must be an integer frame id")
    if value < 0 or value >= frame_count:
        raise _error(f"{where} frame id {value} is out of bounds")
    return value


def _frame_name(frame: Mapping[str, Any], frame_id: int) -> str:
    name = frame.get("name")
    if not isinstance(name, str) or not name:
        raise _error(f"shared frame {frame_id} has an invalid name")
    return name


def _dotty(name: str) -> bool:
    return name.startswith("dotty!") or "Dotty." in name


def _background_reason(names: Iterable[str]) -> str | None:
    # The order is part of the deterministic output when an interval has more
    # than one background frame.  Profile roots are scaffolding around active
    # Dotty stacks, not background work in their own right.
    stack_names = tuple(names)
    has_dotty = any(_dotty(name) for name in stack_names)
    for reason, pattern in _BACKGROUND_PATTERNS:
        if reason == "profile_root" and has_dotty:
            continue
        if any(pattern.search(name) for name in stack_names):
            return reason
    return None


def _row(frame_id: int, name: str, weight: float) -> dict[str, Any]:
    return {"frame_id": frame_id, "name": name, "weight": weight}


def _rank(weights: Mapping[int, float], names: Sequence[str], top: int) -> list[dict[str, Any]]:
    def sort_name(frame_id: int) -> str:
        return unicodedata.normalize("NFKC", names[frame_id]).casefold()

    ordered = sorted(weights, key=lambda fid: (-weights[fid], sort_name(fid), fid))
    return [_row(fid, names[fid], weights[fid]) for fid in ordered[:top]]


def _accounting(total: float, buckets: Mapping[str, float]) -> dict[str, float]:
    accounted = sum(buckets.values())
    return {
        "eligible_dotty": buckets["eligible_dotty"],
        "excluded_background": buckets["excluded_background"],
        "non_dotty": buckets["non_dotty"],
        "unaccounted": buckets["unaccounted"],
        "total": total,
        "accounted": accounted,
    }



def _parse_profile(profile: Mapping[str, Any], profile_index: int, names: Sequence[str], top: int) -> dict[str, Any]:
    where = f"profile {profile_index}"
    if not isinstance(profile, Mapping):
        raise _error(f"{where} must be an object")
    profile_name = profile.get("name")
    if not isinstance(profile_name, str) or not profile_name:
        raise _error(f"{where} name must be a non-empty string")
    profile_type = profile.get("type")
    if profile_type == "sampled":
        raise _error(f"{where} is sampled; only evented profiles are supported")
    if profile_type != "evented":
        raise _error(f"{where} type must be evented")
    unit = profile.get("unit")
    if not isinstance(unit, str) or not unit:
        raise _error(f"{where} unit must be a non-empty string")
    start = _number(profile.get("startValue"), f"{where}.startValue")
    end = _number(profile.get("endValue"), f"{where}.endValue")
    if end <= start:
        raise _error(f"{where} endValue must be greater than startValue")
    events = profile.get("events")
    if not isinstance(events, list) or not events:
        raise _error(f"{where}.events must be a non-empty list")

    stack: list[int] = []
    leaf_weights: dict[int, float] = defaultdict(float)
    inclusive_weights: dict[int, float] = defaultdict(float)
    buckets: dict[str, float] = {"eligible_dotty": 0.0, "excluded_background": 0.0, "non_dotty": 0.0, "unaccounted": 0.0}
    excluded_reasons: dict[str, float] = defaultdict(float)
    previous = start
    first_at: float | None = None
    last_at: float | None = None
    frame_count = len(names)

    def sweep(weight: float) -> None:
        if weight <= 0:
            return
        if not stack:
            buckets["unaccounted"] += weight
            return
        stack_names = [names[fid] for fid in stack]
        reason = _background_reason(stack_names)
        has_dotty = any(_dotty(name) for name in stack_names)
        if reason is not None:
            buckets["excluded_background"] += weight
            excluded_reasons[reason] += weight
        elif has_dotty:
            buckets["eligible_dotty"] += weight
            leaf_weights[stack[-1]] += weight
            for fid in set(stack):
                inclusive_weights[fid] += weight
        else:
            buckets["non_dotty"] += weight

    for event_index, event in enumerate(events):
        if not isinstance(event, Mapping):
            raise _error(f"{where}.events[{event_index}] must be an object")
        event_type = event.get("type")
        if event_type not in ("O", "C"):
            raise _error(f"{where}.events[{event_index}] has invalid type {event_type!r}")
        at = _number(event.get("at"), f"{where}.events[{event_index}].at")
        if first_at is None:
            first_at = at
        if last_at is not None and at < last_at:
            raise _error(f"{where} event timestamps must be nondecreasing")
        if at < start or at > end:
            raise _error(f"{where}.events[{event_index}].at is outside profile bounds")
        sweep(at - previous)
        previous = at
        last_at = at
        if event_type == "O":
            if "frame" not in event:
                raise _error(f"{where}.events[{event_index}] open event lacks frame id")
            stack.append(_frame_id(event["frame"], f"{where}.events[{event_index}].frame", frame_count))
        else:
            if not stack:
                raise _error(f"{where}.events[{event_index}] closes an empty stack")
            if "frame" in event:
                close_id = _frame_id(event["frame"], f"{where}.events[{event_index}].frame", frame_count)
                if close_id != stack[-1]:
                    raise _error(f"{where}.events[{event_index}] closes frame {close_id}, expected {stack[-1]}")
            stack.pop()
    if first_at != start:
        raise _error(f"{where} events do not cover profile startValue")
    if last_at != end:
        raise _error(f"{where} events do not cover profile endValue")
    if stack:
        raise _error(f"{where} has unmatched open frames")
    # A final close at end leaves no active stack, so this is normally zero.  It
    # also makes the identity explicit should the event format gain zero events.
    sweep(end - previous)

    total = end - start
    result: dict[str, Any] = {
        "index": profile_index,
        "name": profile_name,
        "unit": unit,
        "start": start,
        "end": end,
        "duration": total,
        "accounting": _accounting(total, buckets),
        "excluded_reasons": {key: excluded_reasons[key] for key in sorted(excluded_reasons)},
        "top_leaf": _rank(leaf_weights, names, top),
        "top_inclusive": _rank(inclusive_weights, names, top),
        "distinct_leaf_frames": len(leaf_weights),
        "distinct_inclusive_frames": len(inclusive_weights),
    }
    return {"profile": result, "unit": unit, "buckets": buckets, "leaf": leaf_weights, "inclusive": inclusive_weights}


def analyze(data: Mapping[str, Any], *, source: str = "", top: int = 10) -> dict[str, Any]:
    """Validate and summarize a decoded Speedscope JSON object."""
    if not isinstance(data, Mapping):
        raise _error("input must be a JSON object")
    if isinstance(top, bool) or not isinstance(top, int) or top < 0:
        raise _error("top must be a non-negative integer")
    shared = data.get("shared")
    if not isinstance(shared, Mapping):
        raise _error("shared must be an object")
    raw_frames = shared.get("frames")
    if not isinstance(raw_frames, list) or not raw_frames:
        raise _error("shared.frames must be a non-empty list")
    names: list[str] = []
    for frame_index, frame in enumerate(raw_frames):
        if not isinstance(frame, Mapping):
            raise _error(f"shared frame {frame_index} must be an object")
        names.append(_frame_name(frame, frame_index))
    profiles = data.get("profiles")
    if not isinstance(profiles, list) or not profiles:
        raise _error("profiles must be a non-empty list")

    parsed = [_parse_profile(profile, i, names, top) for i, profile in enumerate(profiles)]
    units = {entry["unit"] for entry in parsed}
    if len(units) != 1:
        raise _error("profiles must use one consistent unit")
    unit = next(iter(units))
    aggregate_buckets: dict[str, float] = {"eligible_dotty": 0.0, "excluded_background": 0.0, "non_dotty": 0.0, "unaccounted": 0.0}
    aggregate_leaf: dict[int, float] = defaultdict(float)
    aggregate_inclusive: dict[int, float] = defaultdict(float)
    for entry in parsed:
        for key in aggregate_buckets:
            aggregate_buckets[key] += entry["buckets"][key]
        for fid, weight in entry["leaf"].items():
            aggregate_leaf[fid] += weight
        for fid, weight in entry["inclusive"].items():
            aggregate_inclusive[fid] += weight
    total = sum(entry["profile"]["duration"] for entry in parsed)
    aggregate = {
        "accounting": _accounting(total, aggregate_buckets),
        "top_leaf": _rank(aggregate_leaf, names, top),
        "top_inclusive": _rank(aggregate_inclusive, names, top),
        "distinct_leaf_frames": len(aggregate_leaf),
        "distinct_inclusive_frames": len(aggregate_inclusive),
    }
    return {
        "source": source,
        "unit": unit,
        "profile_count": len(parsed),
        "profiles": [entry["profile"] for entry in parsed],
        "aggregate": aggregate,
        "note": "Weights are represented time—not CPU utilization.",
    }


def load_and_analyze(path: str | Path, *, top: int = 10) -> dict[str, Any]:
    path_obj = Path(path)
    try:
        with path_obj.open("r", encoding="utf-8") as handle:
            data = json.load(handle)
    except json.JSONDecodeError as exc:
        raise _error(f"invalid JSON: {exc.msg} at line {exc.lineno} column {exc.colno}") from exc
    except OSError as exc:
        raise _error(f"cannot read {path_obj}: {exc}") from exc
    return analyze(data, source=str(path_obj), top=top)


def _json_text(value: Mapping[str, Any]) -> str:
    return json.dumps(value, ensure_ascii=False, indent=2, sort_keys=True, allow_nan=False) + "\n"


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Summarize evented Speedscope represented-time intervals")
    parser.add_argument("input", metavar="INPUT")
    parser.add_argument("--json-out", required=True, metavar="PATH")
    parser.add_argument("--top", required=True, type=int, metavar="N")
    args = parser.parse_args(argv)
    try:
        result = load_and_analyze(args.input, top=args.top)
        text = _json_text(result)
        Path(args.json_out).write_text(text, encoding="utf-8")
    except (SummaryError, OSError) as exc:
        print(f"speedscope_summary: error: {exc}", file=sys.stderr)
        return 2
    print(text, end="")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
