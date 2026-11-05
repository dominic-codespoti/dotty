import importlib.util
import io
import json
import sys
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "check_notices.py"
spec = importlib.util.spec_from_file_location("check_notices", SCRIPT)
module = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = module
spec.loader.exec_module(module)

NOTICES = """# Third-party notices

## Components

| Component | Version | How it ships | License |
|---|---|---|---|
| Silk.NET (`Silk.NET.Windowing`) | 2.23.0 | Compiled | MIT |
| SkiaSharp | 4.151.1 | Compiled | MIT |

## NuGet package inventory

| Package | Version | Platforms | Component above |
|---|---|---|---|
| Silk.NET.Core | 2.23.0 | all | Silk.NET |
| SkiaSharp.NativeAssets.Linux | 4.151.1 | linux | SkiaSharp |

## License texts
"""


def assets(*packages):
    libraries = {p: {"type": "package"} for p in packages}
    libraries["Dotty.Runtime/1.0.0"] = {"type": "project"}
    return {"libraries": libraries}


class CheckTests(unittest.TestCase):
    def rows(self):
        return module.parse_inventory(NOTICES)

    def test_inventory_parsing_expands_all(self):
        rows = self.rows()
        self.assertEqual(rows[0], module.Row("Silk.NET.Core", "2.23.0", ("linux", "osx", "win"), "Silk.NET"))
        self.assertEqual(rows[1].platforms, ("linux",))

    def test_cases(self):
        components = module.component_names(NOTICES)
        cases = [
            ("clean linux", {"Silk.NET.Core": {"2.23.0"}, "SkiaSharp.NativeAssets.Linux": {"4.151.1"}}, "linux", []),
            ("clean windows", {"Silk.NET.Core": {"2.23.0"}}, "win", []),
            ("missing package", {"Silk.NET.Core": {"2.23.0"}, "SkiaSharp.NativeAssets.Linux": {"4.151.1"}, "New.Pkg": {"1.0.0"}}, "linux",
             ["New.Pkg 1.0.0: restored but missing from the inventory"]),
            ("version drift", {"Silk.NET.Core": {"2.24.0"}, "SkiaSharp.NativeAssets.Linux": {"4.151.1"}}, "linux",
             ["Silk.NET.Core: inventory says 2.23.0, restore has 2.24.0"]),
            ("stale entry", {"SkiaSharp.NativeAssets.Linux": {"4.151.1"}}, "linux",
             ["Silk.NET.Core 2.23.0: in the inventory but not restored on linux (stale?)"]),
            ("platform not listed", {"Silk.NET.Core": {"2.23.0"}, "SkiaSharp.NativeAssets.Linux": {"4.151.1"}}, "osx",
             ["SkiaSharp.NativeAssets.Linux: restored on osx but the inventory lists only linux"]),
        ]
        for name, packages, platform, expected in cases:
            with self.subTest(name):
                self.assertEqual(module.check(packages, self.rows(), components, platform), expected)

    def test_unknown_component_is_reported(self):
        rows = [module.Row("Foo", "1.0", ("linux",), "Foo Library")]
        findings = module.check({"Foo": {"1.0"}}, rows, module.component_names(NOTICES), "linux")
        self.assertEqual(findings, ["Foo: component 'Foo Library' is not in the Components table"])

    def test_component_match_requires_whole_name(self):
        rows = [module.Row("Silk", "1.0", ("linux",), "Silk")]
        findings = module.check({"Silk": {"1.0"}}, rows, "Silk.NET (`x`)", "linux")
        self.assertEqual(len(findings), 1)

    def test_build_only_tools_are_excluded(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "project.assets.json"
            path.write_text(json.dumps(assets(
                "Microsoft.DotNet.ILCompiler/11.0.0", "runtime.linux-x64.Microsoft.DotNet.ILCompiler/11.0.0",
                "Microsoft.NET.ILLink.Tasks/11.0.0", "KeraLua/1.4.9")))
            self.assertEqual(module.restored_packages([path]), {"KeraLua": {"1.4.9"}})

    def test_platform_names(self):
        for name, expected in [("linux", "linux"), ("darwin", "osx"), ("win32", "win")]:
            with self.subTest(name):
                self.assertEqual(module.current_platform(name), expected)
        with self.assertRaises(ValueError):
            module.current_platform("sunos5")

    def test_bad_platform_cell_is_an_error(self):
        with self.assertRaises(ValueError):
            module.parse_inventory(NOTICES.replace("| linux |", "| beos |"))


class MainTests(unittest.TestCase):
    def run_main(self, root, *extra):
        out, err = io.StringIO(), io.StringIO()
        with redirect_stdout(out), redirect_stderr(err):
            code = module.main(["--root", str(root), *extra])
        return code, out.getvalue(), err.getvalue()

    def test_main_exit_codes(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "THIRD-PARTY-NOTICES.md").write_text(NOTICES)
            code, _, err = self.run_main(root)
            self.assertEqual(code, 2)
            self.assertIn("run dotnet restore", err)

            path = root / "src/Dotty/obj/project.assets.json"
            path.parent.mkdir(parents=True)
            path.write_text(json.dumps(assets("Silk.NET.Core/2.23.0", "SkiaSharp.NativeAssets.Linux/4.151.1")))
            code, out, _ = self.run_main(root, "--platform", "linux")
            self.assertEqual(code, 0)
            self.assertIn("matches 2 packages", out)

            path.write_text(json.dumps(assets("Silk.NET.Core/2.23.0")))
            code, _, err = self.run_main(root, "--platform", "linux")
            self.assertEqual(code, 1)
            self.assertIn("stale", err)

    def test_repository_notices_parse(self):
        root = SCRIPT.parents[2]
        text = (root / "THIRD-PARTY-NOTICES.md").read_text(encoding="utf-8")
        rows = module.parse_inventory(text)
        components = module.component_names(text)
        packages = {row.package: {row.version} for row in rows}
        self.assertEqual(module.check(packages, rows, components, "linux"), [])


if __name__ == "__main__":
    unittest.main()
