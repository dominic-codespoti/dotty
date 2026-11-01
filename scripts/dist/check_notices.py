#!/usr/bin/env python3
"""Check THIRD-PARTY-NOTICES.md against the restored NuGet package set (a minimal SBOM check).

Reads the `libraries` of type `package` from one or more obj/project.assets.json files (run
`dotnet restore` first) and compares them with the "NuGet package inventory" table of
THIRD-PARTY-NOTICES.md:

  - a restored package missing from the table, or listed with another version, fails;
  - a table row for this platform that the restore does not contain fails (stale entry);
  - a row whose "Component above" is not named in the Components table fails.

Build-only tools (NativeAOT compiler, ILLink) are excluded. Exit 0 clean, 1 on findings, 2 on usage
errors.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from dataclasses import dataclass
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DEFAULT_ASSETS = (Path("src/Dotty/obj/project.assets.json"),)
NOTICES = Path("THIRD-PARTY-NOTICES.md")
BUILD_ONLY = (
    re.compile(r"^Microsoft\.DotNet\.ILCompiler$"),
    re.compile(r"^runtime\.[\w.-]+\.Microsoft\.DotNet\.ILCompiler$"),
    re.compile(r"^Microsoft\.NET\.ILLink\.Tasks$"),
)
PLATFORMS = ("linux", "osx", "win")


@dataclass(frozen=True)
class Row:
    package: str
    version: str
    platforms: tuple[str, ...]
    component: str


def current_platform(name: str = sys.platform) -> str:
    if name.startswith("linux"):
        return "linux"
    if name == "darwin":
        return "osx"
    if name in ("win32", "cygwin"):
        return "win"
    raise ValueError(f"unsupported platform {name}")


def restored_packages(assets_files: list[Path]) -> dict[str, set[str]]:
    packages: dict[str, set[str]] = {}
    for path in assets_files:
        data = json.loads(path.read_text(encoding="utf-8"))
        for key, library in data.get("libraries", {}).items():
            if library.get("type") != "package":
                continue
            name, _, version = key.partition("/")
            if any(pattern.match(name) for pattern in BUILD_ONLY):
                continue
            packages.setdefault(name, set()).add(version)
    return packages


def _section(text: str, heading: str) -> str:
    match = re.search(rf"^## {re.escape(heading)}\n(.*?)(?=^## |\Z)", text, re.S | re.M)
    if match is None:
        raise ValueError(f"THIRD-PARTY-NOTICES.md has no '## {heading}' section")
    return match.group(1)


def _table_rows(section: str) -> list[list[str]]:
    rows = []
    for line in section.splitlines():
        if not line.startswith("|") or re.match(r"^\|[-| ]+\|$", line):
            continue
        rows.append([cell.strip() for cell in line.strip().strip("|").split("|")])
    return rows[1:]  # drop the header


def parse_inventory(text: str) -> list[Row]:
    rows = []
    for cells in _table_rows(_section(text, "NuGet package inventory")):
        if len(cells) != 4:
            raise ValueError(f"inventory row needs 4 cells: {cells}")
        platforms = tuple(p.strip() for p in cells[2].split(","))
        if platforms == ("all",):
            platforms = PLATFORMS
        unknown = [p for p in platforms if p not in PLATFORMS]
        if unknown:
            raise ValueError(f"{cells[0]}: unknown platform {unknown}")
        rows.append(Row(cells[0], cells[1], platforms, cells[3]))
    return rows


def component_names(text: str) -> str:
    return "\n".join(cells[0] for cells in _table_rows(_section(text, "Components")))


def check(packages: dict[str, set[str]], rows: list[Row], components: str, platform: str) -> list[str]:
    findings = []
    by_name: dict[str, Row] = {}
    for row in rows:
        if row.package in by_name:
            findings.append(f"{row.package}: listed twice in the inventory")
        by_name[row.package] = row
        if not re.search(rf"(?<![\w.]){re.escape(row.component)}(?!\.?\w)", components):
            findings.append(f"{row.package}: component '{row.component}' is not in the Components table")

    for name in sorted(packages):
        versions = sorted(packages[name])
        row = by_name.get(name)
        if row is None:
            findings.append(f"{name} {', '.join(versions)}: restored but missing from the inventory")
        elif row.version not in versions or len(versions) > 1:
            findings.append(f"{name}: inventory says {row.version}, restore has {', '.join(versions)}")
        elif platform not in row.platforms:
            findings.append(f"{name}: restored on {platform} but the inventory lists only {', '.join(row.platforms)}")

    for row in rows:
        if platform in row.platforms and row.package not in packages:
            findings.append(f"{row.package} {row.version}: in the inventory but not restored on {platform} (stale?)")
    return findings


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--root", type=Path, default=ROOT)
    parser.add_argument("--assets", type=Path, action="append", help="project.assets.json (repeatable); default src/Dotty")
    parser.add_argument("--platform", choices=PLATFORMS, help="host OS of the restore (default: this host)")
    args = parser.parse_args(argv)

    assets = args.assets or [args.root / path for path in DEFAULT_ASSETS]
    missing = [str(path) for path in assets if not path.is_file()]
    if missing:
        print(f"error: {', '.join(missing)} not found; run dotnet restore first", file=sys.stderr)
        return 2
    text = (args.root / NOTICES).read_text(encoding="utf-8")
    try:
        rows = parse_inventory(text)
        components = component_names(text)
    except ValueError as error:
        print(f"error: {error}", file=sys.stderr)
        return 2
    platform = args.platform or current_platform()
    findings = check(restored_packages(assets), rows, components, platform)
    for finding in findings:
        print(f"error: {finding}", file=sys.stderr)
    if not findings:
        print(f"THIRD-PARTY-NOTICES.md inventory matches {len(rows)} packages ({platform}).")
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main())
