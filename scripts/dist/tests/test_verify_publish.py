import os
import stat
import subprocess
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "verify-publish.sh"

UNIX_FILES = {
    "linux-x64": ["libglfw.so.3", "libSkiaSharp.so", "liblua54.so"],
    "osx-arm64": ["libglfw.3.dylib", "libSkiaSharp.dylib", "liblua54.dylib"],
}
SHELL_INTEGRATION = [
    "shell-integration/dotty.bash",
    "shell-integration/inject/bash/dotty-rcfile.bash",
    "shell-integration/inject/zsh/.zshenv",
    "shell-integration/inject/xdg/fish/vendor_conf.d/dotty-shell-integration.fish",
]
LICENSES = ["License.md", "THIRD-PARTY-NOTICES.md", "licenses/GLFW-LICENSE.md"]
TERMINFO = ["terminfo/x/xterm-dotty", "terminfo/78/xterm-dotty"]


def fake_dotty(version_output, explain_output="font.size\nLua key: font.size\n", version_status=0):
    # Mirrors the CLI contract verify-publish relies on; also records that the run was sandboxed.
    return f"""#!/usr/bin/env bash
case "$1" in
  --version) printf '%s\\n' {version_output!r}; exit {version_status} ;;
  +explain-config) [ "$2" = font.size ] || exit 3; printf '%s' {explain_output!r}; printf '%s\\n' "$DOTTY_CONFIG_HOME" > "$(dirname "$0")/config-home.txt"; exit 0 ;;
esac
exit 9
"""


def make_tree(root, rid, version_output="0.3.0", omit=(), non_executable=(), **dotty_options):
    files = ["dotty", "pty-helper", *UNIX_FILES[rid], *SHELL_INTEGRATION, *LICENSES, *TERMINFO]
    for relative in files:
        if relative in omit:
            continue
        path = root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(fake_dotty(version_output, **dotty_options) if relative == "dotty" else "stub\n")
        if relative in ("dotty", "pty-helper") and relative not in non_executable:
            path.chmod(path.stat().st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)


def run(root, rid, version="0.3.0", mode="exact"):
    return subprocess.run(
        ["bash", str(SCRIPT), "--rid", rid, "--dir", str(root), "--version", version, "--version-mode", mode],
        capture_output=True, text=True, env={**os.environ, "HOME": str(root / "runner-home")},
    )


class VerifyPublishTests(unittest.TestCase):
    def test_complete_trees_pass_for_each_unix_rid(self):
        for rid in UNIX_FILES:
            with self.subTest(rid=rid), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                make_tree(root, rid)
                result = run(root, rid)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertIn(f"{rid} publish output", result.stdout)
                # The executable ran against a sandboxed config directory, not the runner's.
                config_home = (root / "config-home.txt").read_text().strip()
                self.assertTrue(config_home.endswith("/config/dotty"), config_home)
                self.assertNotIn(str(root / "runner-home"), config_home)

    def test_every_missing_file_is_reported(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            make_tree(root, "linux-x64", omit=("liblua54.so", "shell-integration/inject/zsh/.zshenv"), non_executable=("pty-helper",))
            result = run(root, "linux-x64")
        self.assertEqual(result.returncode, 1)
        self.assertIn("missing file liblua54.so", result.stderr)
        self.assertIn("missing file shell-integration/inject/zsh/.zshenv", result.stderr)
        self.assertIn("pty-helper is not executable", result.stderr)
        self.assertIn("3 check(s) failed", result.stderr)

    def test_license_and_terminfo_payload_is_required(self):
        for missing in [*LICENSES, *TERMINFO]:
            with self.subTest(missing=missing), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                make_tree(root, "osx-arm64", omit=(missing,))
                result = run(root, "osx-arm64")
                self.assertEqual(result.returncode, 1)
                self.assertIn(f"missing file {missing}", result.stderr)

    def test_version_mode_table(self):
        cases = [
            # (printed, expected, mode, passes)
            ("0.3.0", "0.3.0", "exact", True),
            ("0.3.0-dev+abc1234", "0.3.0", "exact", False),
            ("0.3.1", "0.3.0", "exact", False),
            ("0.4.0-beta.1", "0.4.0-beta.1", "exact", True),
            ("0.3.0", "0.3.0", "dev", True),
            ("0.3.0-dev+abc1234", "0.3.0", "dev", True),
            ("0.4.0-beta.1.dev+abc1234", "0.4.0-beta.1", "dev", True),
            ("0.3.0-dev+", "0.3.0", "dev", False),
            ("0.3.0+abc1234", "0.3.0", "dev", False),
            ("0.3.10", "0.3.1", "dev", False),
            ("10.3.0", "0.3.0", "dev", False),
        ]
        for printed, expected, mode, passes in cases:
            with self.subTest(printed=printed, expected=expected, mode=mode), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                make_tree(root, "linux-x64", version_output=printed)
                result = run(root, "linux-x64", version=expected, mode=mode)
                self.assertEqual(result.returncode, 0 if passes else 1, result.stderr)

    def test_failing_commands_are_reported(self):
        cases = [
            ({"version_status": 4}, "dotty --version exited with status 4"),
            ({"explain_output": "unknown option\n"}, "did not document font.size"),
        ]
        for options, message in cases:
            with self.subTest(message=message), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                make_tree(root, "osx-arm64", **options)
                result = run(root, "osx-arm64")
                self.assertEqual(result.returncode, 1)
                self.assertIn(message, result.stderr)

    def test_rejects_windows_rid_and_bad_arguments(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.assertEqual(run(root, "win-x64").returncode, 2)
            self.assertEqual(run(root, "linux-x64", mode="loose").returncode, 2)
            self.assertEqual(run(root / "absent", "linux-x64").returncode, 1)


if __name__ == "__main__":
    unittest.main()
