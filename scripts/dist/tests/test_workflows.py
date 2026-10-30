"""Supply-chain invariants of .github/workflows, checked without a YAML library."""
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
WORKFLOWS = sorted((ROOT / ".github" / "workflows").glob("*.yml"))
USES = re.compile(r"^\s*(?:-\s+)?uses:\s*(?P<ref>\S+)(?P<rest>.*)$")
PINNED = re.compile(r"^[\w.-]+/[\w./-]+@[0-9a-f]{40}$")
VERSION_COMMENT = re.compile(r"^\s+#\s+v\d+\.\d+\.\d+\s*$")


class WorkflowTests(unittest.TestCase):
    def test_workflows_exist(self):
        self.assertIn("release.yml", [path.name for path in WORKFLOWS])

    def test_every_action_is_pinned_to_a_commit_sha_with_a_version_comment(self):
        for workflow in WORKFLOWS:
            for number, line in enumerate(workflow.read_text(encoding="utf-8").splitlines(), 1):
                if line.lstrip().startswith("#"):
                    continue
                match = USES.match(line)
                if match is None:
                    continue
                with self.subTest(workflow=workflow.name, line=number):
                    ref = match["ref"]
                    if ref.startswith("./"):
                        self.assertTrue((ROOT / ref).is_file(), f"local reusable workflow {ref} does not exist")
                        continue
                    self.assertRegex(ref, PINNED)
                    self.assertRegex(match["rest"], VERSION_COMMENT)

    def test_every_workflow_declares_top_level_permissions(self):
        for workflow in WORKFLOWS:
            with self.subTest(workflow=workflow.name):
                lines = workflow.read_text(encoding="utf-8").splitlines()
                self.assertIn("permissions:", lines)
                self.assertEqual(lines[lines.index("permissions:") + 1], "  contents: read")

    def test_dependabot_covers_actions_and_nuget(self):
        text = (ROOT / ".github" / "dependabot.yml").read_text(encoding="utf-8")
        self.assertIn("package-ecosystem: github-actions", text)
        self.assertIn("package-ecosystem: nuget", text)


if __name__ == "__main__":
    unittest.main()
