import importlib.util
import io
import json
import sys
import tempfile
import unittest
from contextlib import redirect_stderr
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "release_notes.py"
spec = importlib.util.spec_from_file_location("release_notes", SCRIPT)
module = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = module
spec.loader.exec_module(module)

VERSION = "0.4.0-beta.1"
DIGEST = "ab" * 32


def baseline(rid, **overrides):
    entry = {"schema": 1, "rid": rid, "violations": []}
    if rid.startswith("linux-"):
        entry["measured"] = {"glibc": "2.28", "glibcRequiredBy": ["dotty"], "systemLibraries": ["libc.so.6", "libwayland-client.so.0"]}
    elif rid.startswith("osx-"):
        entry["measured"] = {"macos": "12.3", "macosRequiredBy": ["dotty", "pty-helper"], "systemLibraries": []}
    else:
        entry["measured"] = {}
        entry["declared"] = {"windows": "10.0.17763", "source": "ConPTY floor"}
    entry.update(overrides)
    return entry


def all_baselines(**overrides):
    rids = ["linux-x64", "osx-arm64", "osx-x64", "win-x64"]
    return {"schema": 1, "baselines": {rid: overrides.get(rid, baseline(rid)) for rid in rids}}


def checksums(rids=("linux-x64", "osx-x64", "osx-arm64", "win-x64"), version=VERSION):
    names = [f"dotty-{version}-{rid}.{'zip' if rid.startswith('win-') else 'tar.gz'}" for rid in rids]
    return [(DIGEST, name) for name in names] + [(DIGEST, "baselines.json")]


class RenderTests(unittest.TestCase):
    def test_requirements_come_from_measured_values(self):
        notes = module.render(VERSION, "owner/dotty", all_baselines(), checksums())
        expected = [
            "- **Linux x64:** glibc 2.28 or newer (measured: highest `GLIBC_` symbol version, required by `dotty`). System libraries loaded: `libc.so.6`, `libwayland-client.so.0`.",
            "- **macOS Intel (x64):** macOS 12.3 or newer (measured: highest Mach-O deployment target, set by `dotty`, `pty-helper`).",
            "- **macOS Apple Silicon (arm64):** macOS 12.3 or newer (measured: highest Mach-O deployment target, set by `dotty`, `pty-helper`).",
            "- **Windows x64:** Windows 10 version 1809 (build 17763) or newer (declared, not measured: ConPTY floor).",
        ]
        for line in expected:
            self.assertIn(line, notes)
        # Ordered Linux, macOS Intel, macOS arm64, Windows; never the old hand-written claims.
        self.assertLess(notes.index("**Linux x64:**"), notes.index("**macOS Intel (x64):**"))
        self.assertLess(notes.index("**macOS Apple Silicon (arm64):**"), notes.index("**Windows x64:**"))
        self.assertNotIn("glibc 2.17", notes)
        self.assertNotIn("10.15", notes)

    def test_install_checksum_and_verification_sections(self):
        notes = module.render(VERSION, "owner/dotty", all_baselines(), checksums())
        self.assertIn(f"tar -xzf dotty-{VERSION}-linux-x64.tar.gz\ncd dotty-{VERSION}-linux-x64\n./dotty", notes)
        self.assertIn(f"Extract `dotty-{VERSION}-win-x64.zip` and run `dotty.exe`", notes)
        self.assertIn(f"{DIGEST}  dotty-{VERSION}-osx-arm64.tar.gz", notes)
        self.assertIn(f"{DIGEST}  baselines.json", notes)
        self.assertIn("gh attestation verify <archive> --repo owner/dotty", notes)

    def test_generation_fails_instead_of_guessing(self):
        cases = [
            ("missing baseline", {"baselines": {"linux-x64": baseline("linux-x64")}}, checksums(), "no baseline for osx-x64"),
            ("violation", all_baselines(**{"osx-x64": baseline("osx-x64", violations=["requires macOS 14.0"])}), checksums(), "osx-x64 baseline has violations"),
            ("no glibc", all_baselines(**{"linux-x64": baseline("linux-x64", measured={"glibc": None})}), checksums(), "no measured glibc"),
            ("no minos", all_baselines(**{"osx-arm64": baseline("osx-arm64", measured={})}), checksums(), "no measured macOS"),
            ("no declared windows", all_baselines(**{"win-x64": baseline("win-x64", declared=None)}), checksums(), "no declared Windows"),
            ("version mismatch", all_baselines(), checksums(version="0.3.0"), "does not belong to version"),
            ("no archives", all_baselines(), [(DIGEST, "baselines.json")], "lists no release archives"),
        ]
        for label, baselines, sums, message in cases:
            with self.subTest(label=label):
                with self.assertRaisesRegex(module.NotesError, message):
                    module.render(VERSION, "owner/dotty", baselines, sums)

    def test_parse_checksums(self):
        text = f"{DIGEST}  dotty-{VERSION}-linux-x64.tar.gz\n{DIGEST} *baselines.json\n\n"
        self.assertEqual(module.parse_checksums(text.replace(" *", "  *")), [(DIGEST, f"dotty-{VERSION}-linux-x64.tar.gz"), (DIGEST, "baselines.json")])
        with self.assertRaises(module.NotesError):
            module.parse_checksums("nothex  file\n")

    def test_main_reports_errors(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "baselines.json").write_text(json.dumps({"schema": 1, "baselines": {}}))
            (root / "SHA256SUMS").write_text("".join(f"{digest}  {name}\n" for digest, name in checksums()))
            arguments = ["--version", VERSION, "--repository", "owner/dotty", "--baselines", str(root / "baselines.json"), "--checksums", str(root / "SHA256SUMS"), "--output", str(root / "RELEASE_NOTES.md")]
            stderr = io.StringIO()
            with redirect_stderr(stderr):
                self.assertEqual(module.main(arguments), 1)
            self.assertIn("no baseline for", stderr.getvalue())
            self.assertFalse((root / "RELEASE_NOTES.md").exists())

            (root / "baselines.json").write_text(json.dumps(all_baselines()))
            self.assertEqual(module.main(arguments), 0)
            self.assertIn("### System requirements", (root / "RELEASE_NOTES.md").read_text())


if __name__ == "__main__":
    unittest.main()
