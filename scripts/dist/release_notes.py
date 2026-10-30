#!/usr/bin/env python3
"""Generate RELEASE_NOTES.md from the measured baselines.json and SHA256SUMS.

System requirements come only from baselines.json (written by check_baseline.py from the
shipped binaries); this script never states a platform minimum of its own. Every archive
listed in SHA256SUMS must have a baseline without violations, otherwise generation fails.
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

ARCHIVE = re.compile(r"^dotty-(?P<version>.+)-(?P<rid>(?:linux|osx|win)-(?:x64|arm64))\.(?:tar\.gz|zip)$")
RID_TITLES = {
    "linux-x64": "Linux x64", "linux-arm64": "Linux arm64",
    "osx-x64": "macOS Intel (x64)", "osx-arm64": "macOS Apple Silicon (arm64)",
    "win-x64": "Windows x64", "win-arm64": "Windows arm64",
}
RID_ORDER = ["linux-x64", "linux-arm64", "osx-x64", "osx-arm64", "win-x64", "win-arm64"]
WINDOWS_BUILDS = {"10.0.17763": "Windows 10 version 1809 (build 17763)"}


class NotesError(ValueError):
    pass


def parse_checksums(text: str) -> list[tuple[str, str]]:
    entries = []
    for line in text.splitlines():
        if not line.strip():
            continue
        digest, _, name = line.partition("  ")
        if not re.fullmatch(r"[0-9a-f]{64}", digest) or not name:
            raise NotesError(f"malformed SHA256SUMS line: {line!r}")
        entries.append((digest, name.lstrip("*")))
    return entries


def requirement(rid: str, baseline: dict) -> str:
    if baseline.get("violations"):
        raise NotesError(f"{rid} baseline has violations: {'; '.join(baseline['violations'])}")
    measured = baseline.get("measured", {})
    if rid.startswith("linux-"):
        glibc = measured.get("glibc")
        if glibc is None:
            raise NotesError(f"{rid} baseline has no measured glibc version")
        libraries = ", ".join(f"`{name}`" for name in measured.get("systemLibraries", []))
        required_by = ", ".join(f"`{name}`" for name in measured.get("glibcRequiredBy", []))
        return f"glibc {glibc} or newer (measured: highest `GLIBC_` symbol version, required by {required_by}). System libraries loaded: {libraries}."
    if rid.startswith("osx-"):
        macos = measured.get("macos")
        if macos is None:
            raise NotesError(f"{rid} baseline has no measured macOS deployment target")
        required_by = ", ".join(f"`{name}`" for name in measured.get("macosRequiredBy", []))
        return f"macOS {macos} or newer (measured: highest Mach-O deployment target, set by {required_by})."
    declared = baseline.get("declared")
    if not declared:
        raise NotesError(f"{rid} baseline has no declared Windows minimum")
    name = WINDOWS_BUILDS.get(declared["windows"], f"Windows {declared['windows']}")
    return f"{name} or newer (declared, not measured: {declared['source']})."


def install_steps(rid: str, archive: str) -> list[str]:
    if rid.startswith("win-"):
        return [f"Extract `{archive}` and run `dotty.exe`, keeping it beside the DLLs from the archive."]
    folder = archive[: -len(".tar.gz")]
    return ["```bash", f"tar -xzf {archive}", f"cd {folder}", "./dotty", "```"]


def render(version: str, repository: str, baselines: dict, checksums: list[tuple[str, str]]) -> str:
    archives = {}
    for _, name in checksums:
        match = ARCHIVE.match(name)
        if match is None:
            continue
        if match["version"] != version:
            raise NotesError(f"{name} does not belong to version {version}")
        archives[match["rid"]] = name
    if not archives:
        raise NotesError("SHA256SUMS lists no release archives")
    entries = baselines.get("baselines", {})
    for rid in archives:
        if rid not in entries:
            raise NotesError(f"no baseline for {rid} ({archives[rid]})")

    ordered = [rid for rid in RID_ORDER if rid in archives]
    lines = [f"## Dotty v{version}", "", "### Installation", ""]
    for rid in ordered:
        lines += [f"**{RID_TITLES[rid]}:**", "", *install_steps(rid, archives[rid]), ""]
    lines += [
        "Every archive contains `License.md`, `THIRD-PARTY-NOTICES.md` and `licenses/`. The builds are not signed with an Apple Developer ID or a Windows Authenticode certificate.",
        "",
        "### System requirements",
        "",
        "Measured from the shipped binaries by `scripts/dist/check_baseline.py` (attached as `baselines.json`). All platforms need an OpenGL 3.3 core driver.",
        "",
    ]
    for rid in ordered:
        lines.append(f"- **{RID_TITLES[rid]}:** {requirement(rid, entries[rid])}")
    lines += [
        "",
        "### Verification",
        "",
        "```bash",
        "sha256sum -c SHA256SUMS --ignore-missing",
        f"gh attestation verify <archive> --repo {repository}",
        "```",
        "",
        "### Checksums (SHA256SUMS)",
        "",
        "```",
        *(f"{digest}  {name}" for digest, name in checksums),
        "```",
        "",
    ]
    return "\n".join(lines)


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", required=True, help="release version without the leading v")
    parser.add_argument("--repository", required=True, help="owner/name, for the attestation command")
    parser.add_argument("--baselines", required=True, type=Path)
    parser.add_argument("--checksums", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    options = parser.parse_args(argv)
    try:
        notes = render(
            options.version,
            options.repository,
            json.loads(options.baselines.read_text(encoding="utf-8")),
            parse_checksums(options.checksums.read_text(encoding="utf-8")),
        )
    except NotesError as error:
        print(f"release notes: {error}", file=sys.stderr)
        return 1
    options.output.write_text(notes, encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main())
