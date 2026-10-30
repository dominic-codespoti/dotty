#!/usr/bin/env python3
"""Measure the platform baseline of a Dotty publish directory (pure Python, no binutils).

ELF (Linux): the highest GLIBC_x.y symbol version any shipped file requires (from the
dynamic segment's DT_VERNEED table) and the DT_NEEDED libraries loaded from the system.
Mach-O (macOS, thin or universal): the deployment target (LC_BUILD_VERSION minos, else
LC_VERSION_MIN_MACOSX) of the slice for the RID's architecture and the system dylibs.
Windows has no measurable floor here; its minimum comes from baseline-targets.json and is
reported as declared.

Subcommands:
  scan   measure one publish directory, compare with baseline-targets.json, write JSON
  merge  combine per-RID JSON files into the release's baselines.json
  env    print the build environment a RID's target implies (MACOSX_DEPLOYMENT_TARGET)
"""
from __future__ import annotations

import argparse
import json
import re
import struct
import sys
from pathlib import Path

SCHEMA = 1

# ---------------------------------------------------------------------------- versions


def parse_version(text: str) -> tuple:
    return tuple(int(part) for part in text.split("."))


def format_version(version: tuple) -> str:
    return ".".join(str(part) for part in version)


def _max_version(values):
    best = None
    for value in values:
        if value is not None and (best is None or parse_version(value) > parse_version(best)):
            best = value
    return best


# ---------------------------------------------------------------------------- ELF

PT_LOAD, PT_DYNAMIC = 1, 2
DT_NULL, DT_NEEDED, DT_STRTAB, DT_STRSZ = 0, 1, 5, 10
DT_VERNEED, DT_VERNEEDNUM = 0x6FFFFFFE, 0x6FFFFFFF
ELF_MACHINES = {3: "x86", 40: "arm", 62: "x86_64", 183: "arm64"}
GLIBC_VERSION = re.compile(r"^GLIBC_(\d+(?:\.\d+)+)$")


class BinaryFormatError(ValueError):
    pass


def _c_string(data: bytes, offset: int) -> str:
    if offset < 0 or offset >= len(data):
        raise BinaryFormatError(f"string offset {offset} outside the file")
    end = data.find(b"\0", offset)
    if end < 0:
        raise BinaryFormatError(f"unterminated string at {offset}")
    return data[offset:end].decode("utf-8", errors="replace")


def parse_elf(data: bytes) -> dict:
    """GLIBC requirement and DT_NEEDED list of an ELF file, read through its program headers."""
    if data[:4] != b"\x7fELF":
        raise BinaryFormatError("not an ELF file")
    elf_class, encoding = data[4], data[5]
    if elf_class not in (1, 2) or encoding not in (1, 2):
        raise BinaryFormatError(f"unsupported ELF class {elf_class} / data encoding {encoding}")
    endian = "<" if encoding == 1 else ">"
    try:
        if elf_class == 2:
            (_, machine, _, _, phoff, _, _, _, phentsize, phnum) = struct.unpack_from(endian + "HHIQQQIHHH", data, 16)
            program_header = endian + "IIQQQQQQ"  # type, flags, offset, vaddr, paddr, filesz, memsz, align
            dynamic_entry = endian + "qQ"
        else:
            (_, machine, _, _, phoff, _, _, _, phentsize, phnum) = struct.unpack_from(endian + "HHIIIIIHHH", data, 16)
            program_header = endian + "IIIIIIII"  # type, offset, vaddr, paddr, filesz, memsz, flags, align
            dynamic_entry = endian + "iI"

        loads = []
        dynamic = None
        for index in range(phnum):
            fields = struct.unpack_from(program_header, data, phoff + index * phentsize)
            if elf_class == 2:
                p_type, _, p_offset, p_vaddr, _, p_filesz, _, _ = fields
            else:
                p_type, p_offset, p_vaddr, _, p_filesz, _, _, _ = fields
            if p_type == PT_LOAD:
                loads.append((p_vaddr, p_filesz, p_offset))
            elif p_type == PT_DYNAMIC:
                dynamic = (p_offset, p_filesz)

        result = {"format": "elf", "machine": ELF_MACHINES.get(machine, f"elf-{machine}"), "glibc": None, "needed": []}
        if dynamic is None:
            return result  # statically linked: nothing loaded from the system

        def file_offset(address: int) -> int:
            for vaddr, filesz, offset in loads:
                if vaddr <= address < vaddr + filesz:
                    return offset + (address - vaddr)
            raise BinaryFormatError(f"address {address:#x} is not in a loaded segment")

        needed_offsets = []
        strtab = verneed = None
        verneed_count = 0
        entry_size = struct.calcsize(dynamic_entry)
        for position in range(dynamic[0], dynamic[0] + dynamic[1], entry_size):
            tag, value = struct.unpack_from(dynamic_entry, data, position)
            if tag == DT_NULL:
                break
            if tag == DT_NEEDED:
                needed_offsets.append(value)
            elif tag == DT_STRTAB:
                strtab = value
            elif tag == DT_VERNEED:
                verneed = value
            elif tag == DT_VERNEEDNUM:
                verneed_count = value
        if strtab is None:
            if needed_offsets or verneed is not None:
                raise BinaryFormatError("dynamic section has no DT_STRTAB")
            return result
        strings = file_offset(strtab)
        result["needed"] = [_c_string(data, strings + offset) for offset in needed_offsets]

        glibc = []
        if verneed is not None:
            entry = file_offset(verneed)
            for _ in range(verneed_count):
                _, aux_count, _, aux_offset, next_offset = struct.unpack_from(endian + "HHIII", data, entry)
                aux = entry + aux_offset
                for _ in range(aux_count):
                    _, _, _, name_offset, aux_next = struct.unpack_from(endian + "IHHII", data, aux)
                    match = GLIBC_VERSION.match(_c_string(data, strings + name_offset))
                    if match:
                        glibc.append(match.group(1))
                    if aux_next == 0:
                        break
                    aux += aux_next
                if next_offset == 0:
                    break
                entry += next_offset
        result["glibc"] = _max_version(glibc)
        return result
    except struct.error as error:
        raise BinaryFormatError(f"truncated ELF: {error}") from error


# ---------------------------------------------------------------------------- Mach-O

MACHO_THIN = {
    b"\xcf\xfa\xed\xfe": ("<", 64), b"\xce\xfa\xed\xfe": ("<", 32),
    b"\xfe\xed\xfa\xcf": (">", 64), b"\xfe\xed\xfa\xce": (">", 32),
}
FAT_MAGIC, FAT_MAGIC_64 = b"\xca\xfe\xba\xbe", b"\xca\xfe\xba\xbf"
LC_VERSION_MIN_MACOSX, LC_BUILD_VERSION = 0x24, 0x32
LC_DYLIB_COMMANDS = {0xC, 0x20, 0x80000018, 0x8000001F, 0x80000023}
CPU_TYPES = {7: "x86", 0x01000007: "x86_64", 12: "arm", 0x0100000C: "arm64"}
PLATFORMS = {1: "macos", 2: "ios", 3: "tvos", 4: "watchos", 6: "maccatalyst", 11: "visionos"}


def _packed_version(value: int) -> str:
    major, minor, patch = value >> 16, (value >> 8) & 0xFF, value & 0xFF
    return f"{major}.{minor}.{patch}" if patch else f"{major}.{minor}"


def _parse_thin(data: bytes, start: int) -> dict:
    endian, bits = MACHO_THIN[data[start:start + 4]]
    _, cputype, _, _, ncmds, _, _ = struct.unpack_from(endian + "IiiIIII", data, start)
    position = start + (32 if bits == 64 else 28)
    slice_info = {"cpu": CPU_TYPES.get(cputype & 0xFFFFFFFF, f"cpu-{cputype}"), "platform": None, "minos": None, "sdk": None, "dylibs": []}
    for _ in range(ncmds):
        cmd, cmdsize = struct.unpack_from(endian + "II", data, position)
        if cmdsize < 8:
            raise BinaryFormatError(f"load command {cmd:#x} has size {cmdsize}")
        if cmd == LC_BUILD_VERSION:
            platform, minos, sdk = struct.unpack_from(endian + "III", data, position + 8)
            slice_info.update(platform=PLATFORMS.get(platform, f"platform-{platform}"), minos=_packed_version(minos), sdk=_packed_version(sdk))
        elif cmd == LC_VERSION_MIN_MACOSX and slice_info["minos"] is None:
            version, sdk = struct.unpack_from(endian + "II", data, position + 8)
            slice_info.update(platform="macos", minos=_packed_version(version), sdk=_packed_version(sdk))
        elif cmd in LC_DYLIB_COMMANDS:
            (name_offset,) = struct.unpack_from(endian + "I", data, position + 8)
            name_end = data.find(b"\0", position + name_offset, position + cmdsize)
            name_end = position + cmdsize if name_end < 0 else name_end
            slice_info["dylibs"].append(data[position + name_offset:name_end].decode("utf-8", errors="replace"))
        position += cmdsize
    return slice_info


def parse_macho(data: bytes) -> dict:
    """Per-architecture deployment target and dylib dependencies of a thin or universal Mach-O."""
    try:
        magic = data[:4]
        if magic in MACHO_THIN:
            return {"format": "macho", "slices": [_parse_thin(data, 0)]}
        if magic in (FAT_MAGIC, FAT_MAGIC_64):
            (count,) = struct.unpack_from(">I", data, 4)
            slices = []
            for index in range(count):
                if magic == FAT_MAGIC:
                    _, _, offset, _, _ = struct.unpack_from(">iiIII", data, 8 + index * 20)
                else:
                    _, _, offset, _, _, _ = struct.unpack_from(">iiQQII", data, 8 + index * 32)
                if data[offset:offset + 4] not in MACHO_THIN:
                    raise BinaryFormatError(f"universal slice {index} at {offset} is not Mach-O")
                slices.append(_parse_thin(data, offset))
            return {"format": "macho", "slices": slices}
    except struct.error as error:
        raise BinaryFormatError(f"truncated Mach-O: {error}") from error
    raise BinaryFormatError("not a Mach-O file")


def detect_format(head: bytes):
    if head[:4] == b"\x7fELF":
        return "elf"
    if head[:4] in MACHO_THIN:
        return "macho"
    if head[:4] in (FAT_MAGIC, FAT_MAGIC_64) and len(head) >= 8:
        # 0xcafebabe is also the Java class-file magic; a universal binary has few slices.
        (count,) = struct.unpack_from(">I", head, 4)
        if 0 < count < 32:
            return "macho"
    return None


# ---------------------------------------------------------------------------- scan

RID_ARCHITECTURES = {"x64": "x86_64", "arm64": "arm64"}


def _rid_parts(rid: str) -> tuple[str, str]:
    family, _, arch = rid.partition("-")
    if family not in ("linux", "osx", "win") or arch not in RID_ARCHITECTURES:
        raise ValueError(f"unsupported RID {rid!r}")
    return family, RID_ARCHITECTURES[arch]


def _is_bundled_dylib(name: str, shipped: set) -> bool:
    return name.startswith(("@rpath/", "@loader_path/", "@executable_path/")) or name.rsplit("/", 1)[-1] in shipped


def scan(rid: str, publish_dir: Path, targets: dict) -> dict:
    family, architecture = _rid_parts(rid)
    rid_targets = targets.get(rid)
    if rid_targets is None:
        raise ValueError(f"baseline targets have no entry for {rid}")
    report = {"schema": SCHEMA, "rid": rid, "targets": rid_targets, "measured": {}, "files": [], "violations": []}

    if family == "win":
        report["declared"] = {"windows": rid_targets["windows"], "source": rid_targets["windowsSource"]}
        return report

    paths = sorted(path for path in publish_dir.rglob("*") if path.is_file() and not path.is_symlink())
    shipped_names = {path.name for path in paths}
    expected_format = "elf" if family == "linux" else "macho"
    for path in paths:
        with path.open("rb") as handle:
            head = handle.read(8)
        kind = detect_format(head)
        if kind is None:
            continue
        relative = path.relative_to(publish_dir).as_posix()
        if kind != expected_format:
            report["violations"].append(f"{relative}: {kind} binary in a {rid} publish")
            continue
        try:
            info = (parse_elf if kind == "elf" else parse_macho)(path.read_bytes())
        except BinaryFormatError as error:
            report["violations"].append(f"{relative}: {error}")
            continue
        report["files"].append({"path": relative, **info})

    if not report["files"]:
        report["violations"].append(f"no {expected_format} binaries found in {publish_dir}")
        return report

    if family == "linux":
        for entry in report["files"]:
            if entry["machine"] != architecture:
                report["violations"].append(f"{entry['path']}: {entry['machine']} binary in a {rid} publish")
        required = [(entry["glibc"], entry["path"]) for entry in report["files"] if entry["glibc"] is not None]
        glibc = _max_version(version for version, _ in required)
        system = sorted({name for entry in report["files"] for name in entry["needed"] if name not in shipped_names})
        report["measured"] = {
            "glibc": glibc,
            "glibcRequiredBy": sorted(path for version, path in required if version == glibc),
            "systemLibraries": system,
        }
        if glibc is not None and parse_version(glibc) > parse_version(rid_targets["glibc"]):
            report["violations"].append(f"requires glibc {glibc} ({', '.join(report['measured']['glibcRequiredBy'])}), target is {rid_targets['glibc']}")
    else:
        minos_by_file = []
        system = set()
        for entry in report["files"]:
            matching = [item for item in entry["slices"] if item["cpu"] == architecture]
            if not matching:
                cpus = ", ".join(item["cpu"] for item in entry["slices"])
                report["violations"].append(f"{entry['path']}: no {architecture} slice (has {cpus})")
                continue
            selected = matching[0]
            if selected["platform"] not in (None, "macos"):
                report["violations"].append(f"{entry['path']}: built for {selected['platform']}, not macos")
            if selected["minos"] is not None:
                minos_by_file.append((selected["minos"], entry["path"]))
            system.update(name for name in selected["dylibs"] if not _is_bundled_dylib(name, shipped_names))
        macos = _max_version(version for version, _ in minos_by_file)
        report["measured"] = {
            "macos": macos,
            "macosRequiredBy": sorted(path for version, path in minos_by_file if version == macos),
            "systemLibraries": sorted(system),
        }
        if macos is None:
            report["violations"].append("no Mach-O file records a deployment target")
        elif parse_version(macos) > parse_version(rid_targets["macos"]):
            report["violations"].append(f"requires macOS {macos} ({', '.join(report['measured']['macosRequiredBy'])}), target is {rid_targets['macos']}")
    return report


def merge(reports: list) -> dict:
    baselines = {}
    for report in reports:
        if report.get("schema") != SCHEMA:
            raise ValueError(f"unsupported baseline schema {report.get('schema')!r}")
        if report["rid"] in baselines:
            raise ValueError(f"duplicate baseline for {report['rid']}")
        baselines[report["rid"]] = report
    return {"schema": SCHEMA, "baselines": {rid: baselines[rid] for rid in sorted(baselines)}}


def build_environment(rid: str, targets: dict) -> list[str]:
    family, _ = _rid_parts(rid)
    if family == "osx":
        return [f"MACOSX_DEPLOYMENT_TARGET={targets[rid]['macos']}"]
    return []


def summary_markdown(report: dict) -> str:
    rid, measured = report["rid"], report["measured"]
    lines = [f"### Platform baseline ({rid})", ""]
    if "declared" in report:
        lines.append(f"- Windows {report['declared']['windows']} (declared, not measured: {report['declared']['source']})")
    if measured.get("glibc") is not None or "glibc" in report["targets"]:
        lines.append(f"- glibc required: {measured.get('glibc')} (target {report['targets'].get('glibc')})")
    if "macos" in report["targets"]:
        lines.append(f"- macOS deployment target: {measured.get('macos')} (target {report['targets']['macos']})")
    if measured.get("systemLibraries"):
        lines.append(f"- System libraries: {', '.join(measured['systemLibraries'])}")
    for violation in report["violations"]:
        lines.append(f"- **Violation:** {violation}")
    lines.append("")
    return "\n".join(lines)


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    scan_parser = commands.add_parser("scan")
    scan_parser.add_argument("--rid", required=True)
    scan_parser.add_argument("--publish-dir", required=True, type=Path)
    scan_parser.add_argument("--targets", required=True, type=Path)
    scan_parser.add_argument("--output", required=True, type=Path)
    scan_parser.add_argument("--summary", type=Path, help="append a Markdown report (e.g. $GITHUB_STEP_SUMMARY)")
    merge_parser = commands.add_parser("merge")
    merge_parser.add_argument("--output", required=True, type=Path)
    merge_parser.add_argument("inputs", nargs="+", type=Path)
    env_parser = commands.add_parser("env")
    env_parser.add_argument("--rid", required=True)
    env_parser.add_argument("--targets", required=True, type=Path)
    options = parser.parse_args(argv)

    if options.command == "env":
        for line in build_environment(options.rid, json.loads(options.targets.read_text(encoding="utf-8"))):
            print(line)
        return 0
    if options.command == "merge":
        merged = merge([json.loads(path.read_text(encoding="utf-8")) for path in options.inputs])
        options.output.write_text(json.dumps(merged, indent=2) + "\n", encoding="utf-8")
        return 0

    report = scan(options.rid, options.publish_dir, json.loads(options.targets.read_text(encoding="utf-8")))
    options.output.parent.mkdir(parents=True, exist_ok=True)
    options.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    markdown = summary_markdown(report)
    print(markdown)
    if options.summary is not None:
        with options.summary.open("a", encoding="utf-8") as handle:
            handle.write(markdown)
    for violation in report["violations"]:
        print(f"::error title=Platform baseline ({options.rid})::{violation}")
    return 1 if report["violations"] else 0


if __name__ == "__main__":
    sys.exit(main())
