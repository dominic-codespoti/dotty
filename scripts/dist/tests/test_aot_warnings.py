import importlib.util
import io
import sys
import tempfile
import unittest
from contextlib import redirect_stdout
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "aot_warnings.py"
spec = importlib.util.spec_from_file_location("aot_warnings", SCRIPT)
module = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = module  # dataclasses resolve annotations through sys.modules
spec.loader.exec_module(module)

LINUX_ROOT = "/home/runner/work/dotty/dotty"
WINDOWS_ROOT = "D:\\a\\dotty\\dotty"


class ParseTests(unittest.TestCase):
    def test_parses_analyzer_and_ilc_forms(self):
        cases = [
            (
                f"{LINUX_ROOT}/src/Dotty.Runtime/Config/Loader.cs(42,13): warning IL2026: Using member 'X' which has 'RequiresUnreferencedCodeAttribute'. [{LINUX_ROOT}/src/Dotty.Runtime/Dotty.Runtime.csproj]",
                ("IL2026", f"{LINUX_ROOT}/src/Dotty.Runtime/Config/Loader.cs(42,13)", "Using member 'X' which has 'RequiresUnreferencedCodeAttribute'."),
            ),
            (
                f"ILC : Trim analysis warning IL2104: Assembly 'Silk.NET.Core' produced trim warnings. For more information see https://aka.ms/il2104 [{LINUX_ROOT}/src/Dotty/Dotty.csproj]",
                ("IL2104", "ILC", "Assembly 'Silk.NET.Core' produced trim warnings. For more information see https://aka.ms/il2104"),
            ),
            (
                "ILC : AOT analysis warning IL3050: Dotty.Silk.Program.Main(String[]): Using member 'Y' which has 'RequiresDynamicCodeAttribute'.",
                ("IL3050", "ILC", "Dotty.Silk.Program.Main(String[]): Using member 'Y' which has 'RequiresDynamicCodeAttribute'."),
            ),
            (
                f"{WINDOWS_ROOT}\\src\\Dotty\\Program.cs(10,5): warning IL3000: 'System.Reflection.Assembly.Location' always returns an empty string. [{WINDOWS_ROOT}\\src\\Dotty\\Dotty.csproj]",
                ("IL3000", f"{WINDOWS_ROOT}\\src\\Dotty\\Program.cs(10,5)", "'System.Reflection.Assembly.Location' always returns an empty string."),
            ),
        ]
        for line, (code, origin, message) in cases:
            with self.subTest(line=line):
                (warning,) = module.parse([line + "\n"])
                self.assertEqual((warning.code, warning.origin, warning.message), (code, origin, message))

    def test_ignores_non_il_warnings_and_errors(self):
        lines = [
            f"{LINUX_ROOT}/src/Dotty/X.cs(1,1): warning CS0168: The variable 'e' is declared but never used [{LINUX_ROOT}/src/Dotty/Dotty.csproj]",
            f"{LINUX_ROOT}/src/Dotty/X.cs(1,1): error IL2026: promoted to an error [{LINUX_ROOT}/src/Dotty/Dotty.csproj]",
            "Build succeeded.",
            "",
        ]
        self.assertEqual(module.parse(lines), [])

    def test_repeated_summary_lines_are_deduplicated(self):
        line = "ILC : Trim analysis warning IL2104: Assembly 'Silk.NET.Core' produced trim warnings. [/p/Dotty.csproj]"
        self.assertEqual(len(module.parse([line, line, "  " + line])), 1)


class ClassificationTests(unittest.TestCase):
    def test_origin_and_attribution_table(self):
        cases = [
            # (origin, message, repo_root, own?)
            (f"{LINUX_ROOT}/src/Dotty.Terminal/Parser.cs(3,1)", "Using member 'X'.", LINUX_ROOT, True),
            (f"{LINUX_ROOT}/src/Dotty.Runtime/obj/Release/net11.0/Generated.g.cs(9,9)", "Using member 'X'.", LINUX_ROOT, True),
            (f"{WINDOWS_ROOT}\\src\\Dotty\\Program.cs(10,5)", "Using member 'X'.", WINDOWS_ROOT, True),
            (f"{WINDOWS_ROOT}\\SRC\\Dotty\\Program.cs", "Using member 'X'.", WINDOWS_ROOT, True),
            (f"{LINUX_ROOT}/tests/Dotty.App.Tests/X.cs(1,1)", "Using member 'X'.", LINUX_ROOT, False),
            (f"{LINUX_ROOT}/srcgen/X.cs(1,1)", "Using member 'X'.", LINUX_ROOT, False),
            ("/home/runner/.nuget/packages/silk.net.core/2.23.0/lib/net5.0/Silk.NET.Core.dll", "Silk.NET.Core.Loader.DefaultPathResolver..ctor(): Using member 'Z'.", LINUX_ROOT, False),
            ("ILC", "Dotty.Runtime.Config.DottyUserConfig.Load(String): Using member 'Y'.", LINUX_ROOT, True),
            ("ILC", "Dotty.Silk.Program.<Main>$(String[]): Using member 'Y'.", LINUX_ROOT, True),
            ("ILC", "Silk.NET.Windowing.Window.Create(WindowOptions): Using member 'Y'.", LINUX_ROOT, False),
            ("ILC", "DottyFake.Library.Type.Method(): Using member 'Y'.", LINUX_ROOT, False),
            ("ILC", "Assembly 'dotty' produced trim warnings.", LINUX_ROOT, True),
            ("ILC", "Assembly 'Dotty.Rendering.Gpu' produced AOT analysis warnings.", LINUX_ROOT, True),
            ("ILC", "Assembly 'DottyFake' produced trim warnings.", LINUX_ROOT, False),
            ("ILC", "Assembly 'SkiaSharp' produced trim warnings.", LINUX_ROOT, False),
            ("ILC", "<Module>..cctor(): Using member 'Y'.", LINUX_ROOT, False),
        ]
        for origin, message, root, expected in cases:
            with self.subTest(origin=origin, message=message):
                warning = module.IlWarning("IL2026", origin, message)
                self.assertIs(module.is_own(warning, root), expected)


class MainTests(unittest.TestCase):
    def run_main(self, log_text):
        with tempfile.TemporaryDirectory() as directory:
            log = Path(directory) / "warnings.log"
            log.write_text(log_text, encoding="utf-8")
            summary = Path(directory) / "summary.md"
            stdout = io.StringIO()
            with redirect_stdout(stdout):
                code = module.main(["--log", str(log), "--repo-root", LINUX_ROOT, "--label", "linux-x64", "--summary", str(summary)])
            return code, stdout.getvalue(), summary.read_text(encoding="utf-8")

    def test_third_party_only_passes_and_is_listed(self):
        code, output, summary = self.run_main("ILC : Trim analysis warning IL2104: Assembly 'Silk.NET.Core' produced trim warnings. [/p/Dotty.csproj]\n")
        self.assertEqual(code, 0)
        self.assertIn("third-party (tolerated): IL2104 ILC: Assembly 'Silk.NET.Core'", output)
        self.assertIn("Third-party: 1 (tolerated)", summary)
        self.assertIn("Dotty's own code: **0**", summary)

    def test_own_warning_fails_with_annotation(self):
        log = (
            "ILC : Trim analysis warning IL2104: Assembly 'Silk.NET.Core' produced trim warnings. [/p/Dotty.csproj]\n"
            f"{LINUX_ROOT}/src/Dotty/Host/X.cs(7,9): warning IL3050: Using member 'Y'. [{LINUX_ROOT}/src/Dotty/Dotty.csproj]\n"
        )
        code, output, summary = self.run_main(log)
        self.assertEqual(code, 1)
        self.assertIn("::error title=NativeAOT warning in Dotty code::IL3050", output)
        self.assertIn("linux-x64: 1 own IL warning(s), 1 third-party IL warning(s)", output)
        self.assertIn("#### Own (fail the job)", summary)

    def test_empty_log_passes(self):
        code, output, _ = self.run_main("")
        self.assertEqual(code, 0)
        self.assertIn("0 own IL warning(s), 0 third-party", output)


if __name__ == "__main__":
    unittest.main()
