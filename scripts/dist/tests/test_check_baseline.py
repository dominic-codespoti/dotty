"""check_baseline.py against tiny synthetic ELF and Mach-O files built byte by byte below."""
import importlib.util
import io
import json
import struct
import sys
import tempfile
import unittest
from contextlib import redirect_stdout
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "check_baseline.py"
spec = importlib.util.spec_from_file_location("check_baseline", SCRIPT)
module = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = module
spec.loader.exec_module(module)

TARGETS = {
    "linux-x64": {"glibc": "2.31"},
    "linux-arm64": {"glibc": "2.31"},
    "osx-x64": {"macos": "13.0"},
    "osx-arm64": {"macos": "13.0"},
    "win-x64": {"windows": "10.0.17763", "windowsSource": "ConPTY floor"},
}

# ---------------------------------------------------------------------------- ELF builder

BASE = 0x400000


def build_elf(needed=(), verneed=None, elf_class=64, endian="<", machine=62, dynamic=True):
    """A minimal dynamic ELF: header, PT_LOAD + PT_DYNAMIC, .dynstr, version needs, .dynamic."""
    verneed = verneed or {}
    is64 = elf_class == 64
    header_size, ph_size, dyn_size = (64, 56, 16) if is64 else (52, 32, 8)
    phnum = 2 if dynamic else 1

    strings = bytearray(b"\0")
    offsets = {}

    def intern(text):
        if text not in offsets:
            offsets[text] = len(strings)
            strings.extend(text.encode() + b"\0")
        return offsets[text]

    for name in needed:
        intern(name)
    for library, versions in verneed.items():
        intern(library)
        for version in versions:
            intern(version)

    strtab_offset = header_size + phnum * ph_size
    verneed_offset = (strtab_offset + len(strings) + 7) & ~7
    records = bytearray()
    libraries = list(verneed.items())
    for index, (library, versions) in enumerate(libraries):
        entry_size = 16 + 16 * len(versions)
        records += struct.pack(endian + "HHIII", 1, len(versions), offsets[library], 16, entry_size if index < len(libraries) - 1 else 0)
        for aux_index, version in enumerate(versions):
            records += struct.pack(endian + "IHHII", 0, 0, 0, offsets[version], 16 if aux_index < len(versions) - 1 else 0)
    dynamic_offset = (verneed_offset + len(records) + 7) & ~7

    entries = [(1, offsets[name]) for name in needed] + [(5, BASE + strtab_offset), (10, len(strings))]
    if libraries:
        entries += [(0x6FFFFFFE, BASE + verneed_offset), (0x6FFFFFFF, len(libraries))]
    entries.append((0, 0))
    dynamic_bytes = b"".join(struct.pack(endian + ("qQ" if is64 else "iI"), tag, value) for tag, value in entries)
    total = dynamic_offset + len(dynamic_bytes)

    ident = b"\x7fELF" + bytes([2 if is64 else 1, 1 if endian == "<" else 2, 1]) + bytes(9)
    if is64:
        header = ident + struct.pack(endian + "HHIQQQIHHHHHH", 3, machine, 1, 0, header_size, 0, 0, header_size, ph_size, phnum, 0, 0, 0)
        load = struct.pack(endian + "IIQQQQQQ", 1, 5, 0, BASE, BASE, total, total, 0x1000)
        dyn = struct.pack(endian + "IIQQQQQQ", 2, 6, dynamic_offset, BASE + dynamic_offset, BASE + dynamic_offset, len(dynamic_bytes), len(dynamic_bytes), 8)
    else:
        header = ident + struct.pack(endian + "HHIIIIIHHHHHH", 3, machine, 1, 0, header_size, 0, 0, header_size, ph_size, phnum, 0, 0, 0)
        load = struct.pack(endian + "IIIIIIII", 1, 0, BASE, BASE, total, total, 5, 0x1000)
        dyn = struct.pack(endian + "IIIIIIII", 2, dynamic_offset, BASE + dynamic_offset, BASE + dynamic_offset, len(dynamic_bytes), len(dynamic_bytes), 6, 4)

    image = bytearray(total)
    image[0:header_size] = header
    image[header_size:header_size + ph_size] = load
    if dynamic:
        image[header_size + ph_size:header_size + 2 * ph_size] = dyn
    image[strtab_offset:strtab_offset + len(strings)] = strings
    image[verneed_offset:verneed_offset + len(records)] = records
    image[dynamic_offset:] = dynamic_bytes
    return bytes(image)


# ---------------------------------------------------------------------------- Mach-O builder

CPU = {"x86_64": 0x01000007, "arm64": 0x0100000C}


def pack_version(text):
    parts = [int(part) for part in text.split(".")] + [0, 0]
    return (parts[0] << 16) | (parts[1] << 8) | parts[2]


def build_thin_macho(cpu="arm64", minos="13.0", sdk="15.0", platform=1, dylibs=(), legacy=False):
    commands = []
    if minos is not None:
        if legacy:
            commands.append(struct.pack("<IIII", 0x24, 16, pack_version(minos), pack_version(sdk)))
        else:
            commands.append(struct.pack("<IIIIII", 0x32, 24, platform, pack_version(minos), pack_version(sdk), 0))
    for name in dylibs:
        encoded = name.encode() + b"\0"
        size = (24 + len(encoded) + 7) & ~7
        commands.append(struct.pack("<IIIIII", 0xC, size, 24, 2, 0x10000, 0x10000) + encoded.ljust(size - 24, b"\0"))
    body = b"".join(commands)
    return struct.pack("<IiiIIIII", 0xFEEDFACF, CPU[cpu], 0, 6, len(commands), len(body), 0, 0) + body


def build_fat(*slices):
    header = struct.pack(">II", 0xCAFEBABE, len(slices))
    offset = 0x1000
    arches, payload = b"", b""
    for thin in slices:
        (cputype,) = struct.unpack_from("<i", thin, 4)
        arches += struct.pack(">iiIII", cputype, 0, offset + len(payload), len(thin), 12)
        payload += thin.ljust(0x1000, b"\0")
    return (header + arches).ljust(offset, b"\0") + payload


def write_tree(root, files):
    for relative, content in files.items():
        path = Path(root) / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(content)


# ---------------------------------------------------------------------------- tests


class ElfTests(unittest.TestCase):
    def test_glibc_requirement_and_needed_table(self):
        cases = [
            # (class, endian, needed, verneed, expected glibc)
            (64, "<", ["libc.so.6"], {"libc.so.6": ["GLIBC_2.2.5", "GLIBC_2.17"]}, "2.17"),
            (64, "<", ["libm.so.6", "libc.so.6"], {"libm.so.6": ["GLIBC_2.29"], "libc.so.6": ["GLIBC_2.2.5", "GLIBC_2.28", "GLIBC_PRIVATE"]}, "2.29"),
            (64, "<", ["libc.so.6"], {"libc.so.6": ["GLIBC_2.9", "GLIBC_2.10"]}, "2.10"),
            (64, "<", ["libstdc++.so.6"], {"libstdc++.so.6": ["GLIBCXX_3.4.30", "CXXABI_1.3"]}, None),
            (64, "<", ["libfontconfig.so.1"], {}, None),
            (32, "<", ["libc.so.6"], {"libc.so.6": ["GLIBC_2.0", "GLIBC_2.34"]}, "2.34"),
            (64, ">", ["libc.so.6"], {"libc.so.6": ["GLIBC_2.17"]}, "2.17"),
            (32, ">", ["libc.so.6"], {"libc.so.6": ["GLIBC_2.4"]}, "2.4"),
        ]
        for elf_class, endian, needed, verneed, expected in cases:
            with self.subTest(elf_class=elf_class, endian=endian, verneed=verneed):
                info = module.parse_elf(build_elf(needed, verneed, elf_class, endian))
                self.assertEqual(info["glibc"], expected)
                self.assertEqual(info["needed"], needed)

    def test_machine_names(self):
        for machine, name in ((62, "x86_64"), (183, "arm64"), (3, "x86"), (999, "elf-999")):
            with self.subTest(machine=machine):
                self.assertEqual(module.parse_elf(build_elf(machine=machine))["machine"], name)

    def test_static_executable_requires_nothing(self):
        info = module.parse_elf(build_elf(dynamic=False))
        self.assertEqual((info["glibc"], info["needed"]), (None, []))

    def test_malformed_files_raise(self):
        image = build_elf(["libc.so.6"], {"libc.so.6": ["GLIBC_2.17"]})
        for data in (image[:40], b"\x7fELF" + bytes([3, 1]) + bytes(60), b"MZ" + bytes(100)):
            with self.subTest(size=len(data)):
                with self.assertRaises(module.BinaryFormatError):
                    module.parse_elf(data)


class MachOTests(unittest.TestCase):
    def test_thin_build_version_and_dylibs(self):
        info = module.parse_macho(build_thin_macho("arm64", "13.0", "15.2", dylibs=["/usr/lib/libSystem.B.dylib", "@rpath/libSkiaSharp.dylib"]))
        (slice_info,) = info["slices"]
        self.assertEqual(slice_info["cpu"], "arm64")
        self.assertEqual(slice_info["platform"], "macos")
        self.assertEqual(slice_info["minos"], "13.0")
        self.assertEqual(slice_info["sdk"], "15.2")
        self.assertEqual(slice_info["dylibs"], ["/usr/lib/libSystem.B.dylib", "@rpath/libSkiaSharp.dylib"])

    def test_version_encoding_table(self):
        for minos in ("10.15", "11.0", "13.0", "13.5.1", "15.4"):
            with self.subTest(minos=minos):
                (slice_info,) = module.parse_macho(build_thin_macho(minos=minos))["slices"]
                self.assertEqual(slice_info["minos"], minos)

    def test_legacy_version_min_and_missing_target(self):
        (legacy,) = module.parse_macho(build_thin_macho("x86_64", "10.13", legacy=True))["slices"]
        self.assertEqual((legacy["cpu"], legacy["minos"], legacy["platform"]), ("x86_64", "10.13", "macos"))
        (none,) = module.parse_macho(build_thin_macho(minos=None))["slices"]
        self.assertIsNone(none["minos"])

    def test_universal_binary_reports_every_slice(self):
        fat = build_fat(build_thin_macho("x86_64", "10.15"), build_thin_macho("arm64", "11.0"))
        self.assertEqual(module.detect_format(fat[:8]), "macho")
        slices = module.parse_macho(fat)["slices"]
        self.assertEqual([(item["cpu"], item["minos"]) for item in slices], [("x86_64", "10.15"), ("arm64", "11.0")])

    def test_java_class_magic_is_not_macho(self):
        self.assertIsNone(module.detect_format(b"\xca\xfe\xba\xbe\x00\x00\x00\x34"))


class ScanTests(unittest.TestCase):
    def scan(self, rid, files):
        with tempfile.TemporaryDirectory() as directory:
            write_tree(directory, files)
            return module.scan(rid, Path(directory), TARGETS)

    def test_linux_publish_within_target(self):
        report = self.scan("linux-x64", {
            "dotty": build_elf(["libc.so.6", "libm.so.6"], {"libc.so.6": ["GLIBC_2.17", "GLIBC_2.28"]}),
            "libSkiaSharp.so": build_elf(["libfontconfig.so.1", "libc.so.6"], {"libc.so.6": ["GLIBC_2.14"]}),
            "liblua54.so": build_elf(["libc.so.6"], {"libc.so.6": ["GLIBC_2.28"]}),
            "pty-helper": build_elf(["libc.so.6", "libSkiaSharp.so"], {"libc.so.6": ["GLIBC_2.3"]}),
            "License.md": b"MIT\n",
            "shell-integration/dotty.bash": b"#!/bin/bash\n",
        })
        self.assertEqual(report["violations"], [])
        self.assertEqual(report["measured"]["glibc"], "2.28")
        self.assertEqual(report["measured"]["glibcRequiredBy"], ["dotty", "liblua54.so"])
        # Libraries shipped beside the executable are not system dependencies.
        self.assertEqual(report["measured"]["systemLibraries"], ["libc.so.6", "libfontconfig.so.1", "libm.so.6"])
        self.assertEqual([entry["path"] for entry in report["files"]], ["dotty", "libSkiaSharp.so", "liblua54.so", "pty-helper"])

    def test_linux_violations(self):
        cases = [
            ({"dotty": build_elf(["libc.so.6"], {"libc.so.6": ["GLIBC_2.34"]})}, "requires glibc 2.34 (dotty), target is 2.31"),
            ({"dotty": build_elf(machine=183)}, "dotty: arm64 binary in a linux-x64 publish"),
            ({"dotty": build_elf(), "libx.dylib": build_thin_macho()}, "libx.dylib: macho binary in a linux-x64 publish"),
            ({"dotty": build_elf(), "broken.so": build_elf(["libc.so.6"])[:70]}, "broken.so: truncated ELF"),
            ({"README": b"text"}, "no elf binaries found"),
        ]
        for files, message in cases:
            with self.subTest(message=message):
                report = self.scan("linux-x64", files)
                self.assertTrue(any(message in violation for violation in report["violations"]), report["violations"])

    def test_macos_publish_uses_the_rid_slice(self):
        report = self.scan("osx-arm64", {
            "dotty": build_thin_macho("arm64", "12.0", dylibs=["/usr/lib/libSystem.B.dylib", "@rpath/libglfw.3.dylib"]),
            "libSkiaSharp.dylib": build_fat(build_thin_macho("x86_64", "14.0"), build_thin_macho("arm64", "13.0", dylibs=["/System/Library/Frameworks/CoreText.framework/Versions/A/CoreText"])),
            "libglfw.3.dylib": build_thin_macho("arm64", "13.0", dylibs=["libglfw.3.dylib"]),
        })
        self.assertEqual(report["violations"], [])
        self.assertEqual(report["measured"]["macos"], "13.0")
        self.assertEqual(report["measured"]["macosRequiredBy"], ["libSkiaSharp.dylib", "libglfw.3.dylib"])
        self.assertEqual(report["measured"]["systemLibraries"], ["/System/Library/Frameworks/CoreText.framework/Versions/A/CoreText", "/usr/lib/libSystem.B.dylib"])

    def test_macos_violations(self):
        cases = [
            ({"dotty": build_thin_macho("x86_64", "14.0")}, "osx-x64", "requires macOS 14.0 (dotty), target is 13.0"),
            ({"dotty": build_thin_macho("arm64", "13.0.1")}, "osx-arm64", "requires macOS 13.0.1 (dotty), target is 13.0"),
            ({"dotty": build_thin_macho("x86_64", "11.0")}, "osx-arm64", "dotty: no arm64 slice (has x86_64)"),
            ({"dotty": build_thin_macho("arm64", "13.0", platform=2)}, "osx-arm64", "dotty: built for ios, not macos"),
            ({"dotty": build_thin_macho("arm64", None)}, "osx-arm64", "no Mach-O file records a deployment target"),
            ({"dotty": build_elf()}, "osx-arm64", "dotty: elf binary in a osx-arm64 publish"),
        ]
        for files, rid, message in cases:
            with self.subTest(message=message):
                report = self.scan(rid, files)
                self.assertTrue(any(message in violation for violation in report["violations"]), report["violations"])

    def test_windows_baseline_is_declared(self):
        report = self.scan("win-x64", {"dotty.exe": b"MZ"})
        self.assertEqual(report["declared"], {"windows": "10.0.17763", "source": "ConPTY floor"})
        self.assertEqual(report["violations"], [])

    def test_unknown_rid_or_target_is_rejected(self):
        with self.assertRaises(ValueError):
            module.scan("freebsd-x64", Path("."), TARGETS)
        with self.assertRaises(ValueError):
            module.scan("linux-x64", Path("."), {})


class MergeEnvMainTests(unittest.TestCase):
    def test_merge_orders_by_rid_and_rejects_duplicates(self):
        reports = [{"schema": 1, "rid": "win-x64"}, {"schema": 1, "rid": "linux-x64"}]
        self.assertEqual(list(module.merge(reports)["baselines"]), ["linux-x64", "win-x64"])
        with self.assertRaises(ValueError):
            module.merge(reports + [{"schema": 1, "rid": "linux-x64"}])
        with self.assertRaises(ValueError):
            module.merge([{"schema": 2, "rid": "linux-x64"}])

    def test_env_sets_macos_deployment_target_only_for_macos(self):
        self.assertEqual(module.build_environment("osx-x64", TARGETS), ["MACOSX_DEPLOYMENT_TARGET=13.0"])
        self.assertEqual(module.build_environment("linux-x64", TARGETS), [])
        self.assertEqual(module.build_environment("win-x64", TARGETS), [])

    def test_scan_command_writes_report_and_exit_code(self):
        cases = [("2.17", 0), ("2.35", 1)]
        for version, expected_code in cases:
            with self.subTest(version=version), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                write_tree(root / "publish", {"dotty": build_elf(["libc.so.6"], {"libc.so.6": [f"GLIBC_{version}"]})})
                (root / "targets.json").write_text(json.dumps(TARGETS))
                with redirect_stdout(io.StringIO()):
                    code = module.main(["scan", "--rid", "linux-x64", "--publish-dir", str(root / "publish"), "--targets", str(root / "targets.json"), "--output", str(root / "out" / "baselines-linux-x64.json"), "--summary", str(root / "summary.md")])
                self.assertEqual(code, expected_code)
                written = json.loads((root / "out" / "baselines-linux-x64.json").read_text())
                self.assertEqual(written["measured"]["glibc"], version)
                self.assertIn("Platform baseline (linux-x64)", (root / "summary.md").read_text())

    def test_repository_targets_cover_every_release_rid(self):
        targets = json.loads((SCRIPT.parent / "baseline-targets.json").read_text())
        self.assertEqual(sorted(targets), ["linux-x64", "osx-arm64", "osx-x64", "win-x64"])
        for rid in ("osx-arm64", "osx-x64"):
            module.parse_version(targets[rid]["macos"])
        module.parse_version(targets["linux-x64"]["glibc"])


if __name__ == "__main__":
    unittest.main()
