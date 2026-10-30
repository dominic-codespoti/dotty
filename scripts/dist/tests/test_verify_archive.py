import importlib.util
import io
import sys
import tarfile
import tempfile
import unittest
import zipfile
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "verify_archive.py"
spec = importlib.util.spec_from_file_location("verify_archive", SCRIPT)
module = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = module
spec.loader.exec_module(module)


def write_tar(directory, name, files, executable=("dotty", "pty-helper"), top=None):
    path = Path(directory) / name
    top = name[: -len(".tar.gz")] if top is None else top
    with tarfile.open(path, "w:gz") as archive:
        for relative in files:
            info = tarfile.TarInfo(f"{top}/{relative}")
            payload = b"x"
            info.size = len(payload)
            info.mode = 0o755 if relative in executable else 0o644
            archive.addfile(info, io.BytesIO(payload))
    return path


def write_zip(directory, name, files, separator="/"):
    path = Path(directory) / name
    with zipfile.ZipFile(path, "w") as archive:
        for relative in files:
            archive.writestr(relative.replace("/", separator), b"x")
    return path


def complete(rid):
    return module.required_entries(rid)[0]


class VerifyArchiveTests(unittest.TestCase):
    def test_complete_archives_pass(self):
        with tempfile.TemporaryDirectory() as directory:
            archives = [
                write_tar(directory, "dotty-0.4.0-beta.1-linux-x64.tar.gz", complete("linux-x64")),
                write_tar(directory, "dotty-0.4.0-beta.1-osx-arm64.tar.gz", complete("osx-arm64")),
                write_tar(directory, "dotty-0.4.0-beta.1-osx-x64.tar.gz", complete("osx-x64")),
                write_zip(directory, "dotty-0.4.0-beta.1-win-x64.zip", complete("win-x64")),
            ]
            for archive in archives:
                with self.subTest(archive=archive.name):
                    self.assertEqual(module.verify(archive), [])

    def test_backslash_zip_entries_are_accepted(self):
        with tempfile.TemporaryDirectory() as directory:
            archive = write_zip(directory, "dotty-0.3.0-win-x64.zip", complete("win-x64"), separator="\\")
            self.assertEqual(module.verify(archive), [])

    def test_each_required_payload_entry_is_reported_when_missing(self):
        cases = [
            ("linux-x64", "License.md"),
            ("linux-x64", "THIRD-PARTY-NOTICES.md"),
            ("linux-x64", "licenses/GLFW-LICENSE.md"),
            ("linux-x64", "shell-integration/inject/zsh/.zshenv"),
            ("linux-x64", "liblua54.so"),
            ("osx-arm64", "libglfw.3.dylib"),
            ("osx-x64", "pty-helper"),
            ("win-x64", "lua54.dll"),
            ("win-x64", "licenses/GLFW-LICENSE.md"),
            ("win-x64", "shell-integration/Dotty.ps1"),
        ]
        for rid, missing in cases:
            with self.subTest(rid=rid, missing=missing), tempfile.TemporaryDirectory() as directory:
                files = [entry for entry in complete(rid) if entry != missing]
                if rid.startswith("win-"):
                    archive = write_zip(directory, f"dotty-0.3.0-{rid}.zip", files)
                else:
                    archive = write_tar(directory, f"dotty-0.3.0-{rid}.tar.gz", files)
                self.assertEqual(module.verify(archive), [f"{archive.name}: missing {missing}"])

    def test_layout_and_mode_problems(self):
        with tempfile.TemporaryDirectory() as directory:
            not_executable = write_tar(directory, "dotty-0.3.0-linux-x64.tar.gz", complete("linux-x64"), executable=("dotty",))
            self.assertEqual(module.verify(not_executable), ["dotty-0.3.0-linux-x64.tar.gz: pty-helper is not executable"])

            wrong_top = write_tar(directory, "dotty-0.3.0-osx-x64.tar.gz", ["dotty"], top="publish")
            problems = module.verify(wrong_top)
            self.assertIn("dotty-0.3.0-osx-x64.tar.gz: entry outside the package directory dotty-0.3.0-osx-x64/: publish/dotty", problems)
            self.assertIn("dotty-0.3.0-osx-x64.tar.gz: missing dotty", problems)

            wrong_kind = write_zip(directory, "dotty-0.3.0-linux-x64.zip", complete("linux-x64"))
            self.assertEqual(module.verify(wrong_kind), ["dotty-0.3.0-linux-x64.zip: linux-x64 must be packed as .tar.gz"])

            badly_named = write_zip(directory, "dotty-win-x64.zip", complete("win-x64"))
            self.assertEqual(len(module.verify(badly_named)), 1)

    def test_main_counts_archives_and_reports_every_problem(self):
        with tempfile.TemporaryDirectory() as directory:
            good = write_tar(directory, "dotty-0.3.0-linux-x64.tar.gz", complete("linux-x64"))
            bad = write_zip(directory, "dotty-0.3.0-win-x64.zip", [entry for entry in complete("win-x64") if entry != "glfw3.dll"])
            stdout, stderr = io.StringIO(), io.StringIO()
            with redirect_stdout(stdout), redirect_stderr(stderr):
                code = module.main(["--expect-count", "4", str(good), str(bad)])
        self.assertEqual(code, 1)
        self.assertIn("ok: dotty-0.3.0-linux-x64.tar.gz", stdout.getvalue())
        self.assertIn("FAIL: expected 4 archives, found 2", stderr.getvalue())
        self.assertIn("FAIL: dotty-0.3.0-win-x64.zip: missing glfw3.dll", stderr.getvalue())


if __name__ == "__main__":
    unittest.main()
