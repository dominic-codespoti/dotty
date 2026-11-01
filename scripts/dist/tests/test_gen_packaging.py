import importlib.util
import io
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "gen_packaging.py"
spec = importlib.util.spec_from_file_location("gen_packaging", SCRIPT)
module = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = module
spec.loader.exec_module(module)

REPO = SCRIPT.parents[2]
INSTALL = REPO / "packaging/linux/install.sh"

LAUNCH = '''
    public const string HelpText = """
Usage: dotty [--working-directory DIR|-d DIR] [--] [COMMAND [ARG...]]
       dotty --help|-h

Options:
  -h, --help                    Show this help and exit
  -d, --working-directory DIR   Start in DIR
  --                            End Dotty options

Actions (print and exit without opening a window):
  +explain-config [OPTION]      List every config option, or document OPTION
                                (JSON path or Lua key, e.g. font.size)
  +diagnose [--gl]              Print version; --gl also probes OpenGL
""";
'''

CATALOG = '''
    private static IReadOnlyList<ConfigOption> CreateLive() =>
    [
        Opt("window.decorations", "window.decorations", "string", ["custom", "native"], c => c.Window.Decorations,
            "Windows only: 'custom' draws \\"caption\\" controls. Second sentence."),
        Opt("font.size", "font.size", "number", null, c => Math.Max(1, c.Font.Size),
            "Font size in points; " + "clamped to 1-512.", "Applies on reload"),
    ];
'''


class ParseTests(unittest.TestCase):
    def test_help_options_and_actions(self):
        help_ = module.parse_help(module.extract_help_text(LAUNCH))
        self.assertEqual(help_.usage[0], "dotty [--working-directory DIR|-d DIR] [--] [COMMAND [ARG...]]")
        self.assertEqual(help_.options, (
            module.CliOption("-h", "--help", None, "Show this help and exit"),
            module.CliOption("-d", "--working-directory", "DIR", "Start in DIR"),
            module.CliOption(None, "--", None, "End Dotty options"),
        ))
        self.assertEqual(help_.actions[0].description, "List every config option, or document OPTION (JSON path or Lua key, e.g. font.size)")
        self.assertEqual(help_.actions[1].flags, ("--gl",))

    def test_help_errors(self):
        cases = [
            ("no literal", "class X {}"),
            ("garbage line", LAUNCH.replace("  -h, --help", "?? -h, --help")),
            ("no actions", LAUNCH.split("Actions")[0] + '""";'),
        ]
        for name, source in cases:
            with self.subTest(name), self.assertRaises(ValueError):
                module.parse_help(module.extract_help_text(source))

    def test_catalog_entries_sorted_ordinally_with_concatenation_and_escapes(self):
        entries = module.parse_catalog(CATALOG)
        self.assertEqual([e.path for e in entries], ["font.size", "window.decorations"])
        self.assertEqual(entries[0].description, "Font size in points; clamped to 1-512.")
        self.assertEqual(entries[1].values, ("custom", "native"))
        self.assertIn('"caption"', entries[1].description)

    def test_catalog_rejects_non_literal_description(self):
        with self.assertRaises(ValueError):
            module.parse_catalog(CATALOG.replace('"Font size in points; "', "Describe()"))

    def test_first_sentence_keeps_abbreviations(self):
        cases = [
            ("List options (e.g. font.size). Then more.", "List options (e.g. font.size)"),
            ("Check config; exit 1. Done", "Check config; exit 1"),
            ("One", "One"),
        ]
        for text, expected in cases:
            with self.subTest(text):
                self.assertEqual(module._first_sentence(text), expected)

    def test_roff_escapes(self):
        self.assertEqual(module.roff(".hidden"), "\\&.hidden")
        self.assertEqual(module.roff("a-b\\c"), "a\\-b\\ec")


class RenderTests(unittest.TestCase):
    def setUp(self):
        self.help = module.parse_help(module.extract_help_text(LAUNCH))
        self.config = module.parse_catalog(CATALOG)

    def test_man_page_sections(self):
        man = module.render_man(self.help, self.config)
        for needle in (".TH DOTTY 1", ".SH OPTIONS", "\\fB\\-d\\fR, \\fB\\-\\-working\\-directory\\fR \\fIDIR\\fR",
                       ".SH ACTIONS", "\\fB+diagnose\\fR", "\\fBfont.size\\fR (number; Lua \\fBfont.size\\fR)",
                       "One of: custom, native."):
            self.assertIn(needle, man)

    @unittest.skipUnless(shutil.which("bash"), "bash not installed")
    def test_bash_completion_completes_actions_and_config_keys(self):
        script = module.render_bash(self.help, self.config)
        with tempfile.NamedTemporaryFile("w", suffix=".bash", delete=False) as handle:
            handle.write(script)
        try:
            probe = (f"source {handle.name}; "
                     "COMP_WORDS=(dotty +d); COMP_CWORD=1; _dotty; echo \"${COMPREPLY[*]}\"; "
                     "COMP_WORDS=(dotty +explain-config fo); COMP_CWORD=2; _dotty; echo \"${COMPREPLY[*]}\"; "
                     "COMP_WORDS=(dotty +diagnose -); COMP_CWORD=2; _dotty; echo \"${COMPREPLY[*]}\"")
            out = subprocess.run(["bash", "-c", probe], capture_output=True, text=True, check=True).stdout.splitlines()
            self.assertEqual(out, ["+diagnose", "font.size", "--gl"])
        finally:
            os.unlink(handle.name)

    def test_zsh_and_fish_mention_every_option(self):
        zsh = module.render_zsh(self.help, self.config)
        fish = module.render_fish(self.help, self.config)
        self.assertTrue(zsh.startswith("#compdef dotty\n"))
        self.assertIn("'(-d --working-directory)'{-d,--working-directory}'[Start in DIR]:directory:_files -/'", zsh)
        self.assertIn("'window.decorations:Windows only\\: 'custom' draws \"caption\" controls'", zsh.replace("'\\''", "'"))
        self.assertIn("complete -c dotty -s d -l working-directory -r -a '(__fish_complete_directories)'", fish)
        self.assertIn("-a 'font.size'", fish)
        self.assertIn("__fish_seen_subcommand_from +diagnose' -l gl", fish)


class RepositoryTests(unittest.TestCase):
    def test_checked_in_outputs_are_current(self):
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            code = module.main(["--check"])
        self.assertEqual(code, 0, err.getvalue())

    def test_check_reports_stale_file(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            for path in (module.LAUNCH_OPTIONS, module.CATALOG):
                (root / path).parent.mkdir(parents=True, exist_ok=True)
                shutil.copy(REPO / path, root / path)
            err = io.StringIO()
            with redirect_stdout(io.StringIO()), redirect_stderr(err):
                self.assertEqual(module.main(["--root", str(root), "--check"]), 1)
                self.assertIn("dotty.1 is out of date", err.getvalue())
                self.assertEqual(module.main(["--root", str(root)]), 0)
                self.assertEqual(module.main(["--root", str(root), "--check"]), 0)


@unittest.skipUnless(sys.platform.startswith("linux") and shutil.which("sh"), "install.sh is Linux-only")
class InstallScriptTests(unittest.TestCase):
    def run_install(self, home, *args):
        env = {"HOME": str(home), "PATH": os.environ.get("PATH", "/usr/bin:/bin")}
        return subprocess.run(["sh", str(INSTALL), "--terminfo-dir", str(home / "ti"), *args],
                              capture_output=True, text=True, env=env)

    def make_publish(self, root):
        root.mkdir()
        exe = root / "dotty"
        exe.write_text("#!/bin/sh\necho 9.9.9\n")
        exe.chmod(0o755)
        (root / "libglfw.so.3").write_bytes(b"\0")
        (root / "terminfo/x").mkdir(parents=True)
        (root / "terminfo/x/xterm-dotty").write_bytes(b"ti")

    def test_install_and_uninstall_round_trip(self):
        with tempfile.TemporaryDirectory() as tmp:
            home = Path(tmp)
            publish = home / "publish"
            self.make_publish(publish)
            result = self.run_install(home, str(publish))
            self.assertEqual(result.returncode, 0, result.stderr)
            local = home / ".local"
            link = local / "bin/dotty"
            self.assertEqual(os.readlink(link), "../lib/dotty/dotty")
            self.assertEqual(subprocess.run([str(link)], capture_output=True, text=True).stdout, "9.9.9\n")
            for rel in ("share/man/man1/dotty.1", "share/bash-completion/completions/dotty",
                        "share/zsh/site-functions/_dotty", "share/fish/vendor_completions.d/dotty.fish"):
                self.assertTrue((local / rel).is_file(), rel)
            self.assertEqual((home / "ti/x/xterm-dotty").read_bytes(), b"ti")

            again = self.run_install(home, str(publish))
            self.assertEqual(again.returncode, 0, again.stderr)
            self.assertIn("Replacing", again.stdout)

            removed = self.run_install(home, "--uninstall")
            self.assertEqual(removed.returncode, 0, removed.stderr)
            self.assertFalse(link.exists() or link.is_symlink())
            self.assertFalse((local / "lib/dotty").exists())
            self.assertFalse((local / "share/man/man1/dotty.1").exists())
            self.assertFalse((home / "ti/x/xterm-dotty").exists())

    def test_errors(self):
        with tempfile.TemporaryDirectory() as tmp:
            home = Path(tmp)
            cases = [
                (["--bogus"], 2, "unknown option"),
                (["/nonexistent/publish"], 2, "is not a directory"),
                (["--prefix", "relative", str(home)], 2, "absolute"),
                (["--uninstall"], 1, "no Dotty install"),
                ([str(home)], 2, "missing or not executable"),
            ]
            for args, code, message in cases:
                with self.subTest(args=args):
                    result = self.run_install(home, *args)
                    self.assertEqual(result.returncode, code)
                    self.assertIn(message, result.stderr)

    def test_refuses_to_replace_a_regular_file(self):
        with tempfile.TemporaryDirectory() as tmp:
            home = Path(tmp)
            publish = home / "publish"
            self.make_publish(publish)
            (home / ".local/bin").mkdir(parents=True)
            (home / ".local/bin/dotty").write_text("mine")
            result = self.run_install(home, str(publish))
            self.assertEqual(result.returncode, 1)
            self.assertIn("not a symlink", result.stderr)
            self.assertEqual((home / ".local/bin/dotty").read_text(), "mine")


if __name__ == "__main__":
    unittest.main()
