#!/usr/bin/env python3
"""Verify Dotty release archives: layout, required payload and executable bits.

Archives are named `dotty-<version>-<rid>.tar.gz` (Linux, macOS: one top-level directory of the
same name) or `dotty-<version>-<rid>.zip` (Windows: files at the archive root). Every problem in
every archive is reported before the script exits non-zero.
"""
from __future__ import annotations

import argparse
import re
import sys
import tarfile
import zipfile
from pathlib import Path

ARCHIVE_NAME = re.compile(r"^dotty-(?P<version>.+)-(?P<rid>(?:linux|osx|win)-(?:x64|arm64))\.(?P<kind>tar\.gz|zip)$")

SHELL_INTEGRATION_UNIX = [
    "shell-integration/dotty.bash",
    "shell-integration/inject/bash/dotty-rcfile.bash",
    "shell-integration/inject/zsh/.zshenv",
    "shell-integration/inject/xdg/fish/vendor_conf.d/dotty-shell-integration.fish",
]
SHELL_INTEGRATION_WINDOWS = [
    "shell-integration/dotty.bash",
    "shell-integration/Dotty.ps1",
    "shell-integration/inject/bash/dotty-rcfile.bash",
    "shell-integration/inject/zsh/.zshenv",
]
LICENSES = ["License.md", "THIRD-PARTY-NOTICES.md", "licenses/GLFW-LICENSE.md"]


def required_entries(rid: str) -> tuple[list[str], list[str]]:
    """(required files, files that must also be executable) relative to the package root."""
    if rid.startswith("linux-"):
        natives = ["libglfw.so.3", "libSkiaSharp.so", "liblua54.so"]
        return ["dotty", "pty-helper", *natives, *SHELL_INTEGRATION_UNIX, *LICENSES], ["dotty", "pty-helper"]
    if rid.startswith("osx-"):
        natives = ["libglfw.3.dylib", "libSkiaSharp.dylib", "liblua54.dylib"]
        return ["dotty", "pty-helper", *natives, *SHELL_INTEGRATION_UNIX, *LICENSES], ["dotty", "pty-helper"]
    return ["dotty.exe", "glfw3.dll", "libSkiaSharp.dll", "lua54.dll", *SHELL_INTEGRATION_WINDOWS, *LICENSES], []


def _tar_members(path: Path, package_dir: str) -> tuple[dict[str, int], list[str]]:
    problems: list[str] = []
    members: dict[str, int] = {}
    with tarfile.open(path, "r:gz") as archive:
        for member in archive.getmembers():
            name = member.name[2:] if member.name.startswith("./") else member.name
            top, _, relative = name.partition("/")
            if top != package_dir:
                problems.append(f"entry outside the package directory {package_dir}/: {member.name}")
                continue
            if member.isfile():
                members[relative] = member.mode
    return members, problems


def _zip_members(path: Path) -> dict[str, int]:
    with zipfile.ZipFile(path) as archive:
        # Windows PowerShell 5.1 wrote backslash separators; normalize either form.
        return {info.filename.replace("\\", "/"): 0 for info in archive.infolist() if not info.is_dir()}


def verify(path: Path) -> list[str]:
    match = ARCHIVE_NAME.match(path.name)
    if match is None:
        return [f"{path.name}: name does not match dotty-<version>-<rid>.tar.gz|zip"]
    rid, kind = match["rid"], match["kind"]
    expected_kind = "zip" if rid.startswith("win-") else "tar.gz"
    if kind != expected_kind:
        return [f"{path.name}: {rid} must be packed as .{expected_kind}"]

    problems: list[str] = []
    if kind == "tar.gz":
        members, layout_problems = _tar_members(path, path.name[: -len(".tar.gz")])
        problems.extend(f"{path.name}: {problem}" for problem in layout_problems)
    else:
        members = _zip_members(path)

    required, executables = required_entries(rid)
    for relative in required:
        if relative not in members:
            problems.append(f"{path.name}: missing {relative}")
    for relative in executables:
        if relative in members and members[relative] & 0o111 == 0:
            problems.append(f"{path.name}: {relative} is not executable")
    return problems


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archives", nargs="+", type=Path)
    parser.add_argument("--expect-count", type=int, help="fail unless exactly this many archives are given")
    options = parser.parse_args(argv)

    problems: list[str] = []
    if options.expect_count is not None and len(options.archives) != options.expect_count:
        problems.append(f"expected {options.expect_count} archives, found {len(options.archives)}")
    for archive in options.archives:
        archive_problems = verify(archive)
        problems.extend(archive_problems)
        if not archive_problems:
            print(f"ok: {archive.name}")
    for problem in problems:
        print(f"FAIL: {problem}", file=sys.stderr)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
