#!/usr/bin/env python3
"""Build the checked-in, hash-pinned GLFW fork for one host RID."""
from __future__ import annotations

import argparse
import platform
import shutil
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "vendor" / "glfw" / "source"
RID_INFO = {
    "linux-x64": ("Linux", "x86_64", "libglfw.so.3"),
    "linux-arm64": ("Linux", "aarch64", "libglfw.so.3"),
    "win-x64": ("Windows", "x86_64", "glfw3.dll"),
    "win-arm64": ("Windows", "aarch64", "glfw3.dll"),
    "osx-x64": ("Darwin", "x86_64", "libglfw.3.dylib"),
    "osx-arm64": ("Darwin", "arm64", "libglfw.3.dylib"),
}


def run(args: list[str]) -> None:
    print("+", " ".join(args), flush=True)
    subprocess.run(args, check=True)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", required=True, choices=RID_INFO)
    parser.add_argument("--output-dir", type=Path)
    options = parser.parse_args()

    host_os, host_arch, library = RID_INFO[options.rid]
    actual_os = platform.system()
    actual_arch = platform.machine().lower()
    arch_aliases = {"amd64": "x86_64", "x64": "x86_64", "aarch64": "arm64", "arm64": "arm64"}
    actual_arch = arch_aliases.get(actual_arch, actual_arch)
    expected_arch = {"x86_64": "x86_64", "aarch64": "arm64", "arm64": "arm64"}[host_arch]
    expected_os = "Darwin" if host_os == "Darwin" else host_os
    if actual_os != expected_os or actual_arch != expected_arch:
        raise SystemExit(f"Cannot build {options.rid} on {actual_os}/{actual_arch}; native cross-compilation is not configured")
    if not (SOURCE / "CMakeLists.txt").is_file() or not (SOURCE / "LICENSE.md").is_file():
        raise SystemExit(f"Pinned GLFW source or license is missing: {SOURCE}")

    build = ROOT / "obj" / "native" / "glfw" / options.rid / "build"
    stage = ROOT / "obj" / "native" / "glfw" / options.rid / "install"
    output = (options.output_dir or ROOT / "obj" / "native" / "glfw" / options.rid / "output").resolve()
    configure = [
        "cmake", "-S", str(SOURCE), "-B", str(build),
        "-DBUILD_SHARED_LIBS=ON",
        "-DGLFW_BUILD_EXAMPLES=OFF", "-DGLFW_BUILD_TESTS=OFF", "-DGLFW_BUILD_DOCS=OFF",
        "-DGLFW_INSTALL=ON",
        "-DCMAKE_BUILD_TYPE=Release",
    ]
    if host_os == "Linux":
        configure.extend(["-DGLFW_BUILD_X11=ON", "-DGLFW_BUILD_WAYLAND=ON"])
    if host_os == "Darwin":
        configure.append(f"-DCMAKE_OSX_ARCHITECTURES={host_arch}")
    if host_os == "Windows":
        configure.extend(["-A", "ARM64" if host_arch == "aarch64" else "x64"])
    run(configure)
    run(["cmake", "--build", str(build), "--config", "Release", "--target", "glfw", "--parallel"])
    run(["cmake", "--install", str(build), "--config", "Release", "--prefix", str(stage)])

    matches = list(stage.rglob(library))
    if len(matches) != 1 or not matches[0].is_file():
        raise SystemExit(f"Expected exactly one installed {library} in {stage}, found {matches}")
    output.mkdir(parents=True, exist_ok=True)
    destination = output / library
    shutil.copy2(matches[0], destination)
    print(f"Built {destination}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except subprocess.CalledProcessError as error:
        raise SystemExit(error.returncode)
