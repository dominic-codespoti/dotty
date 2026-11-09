#!/usr/bin/env python3
"""Regression test: performance benchmarks must stay advisory-only in CI.

Context: BenchmarkDotNet medians on shared GitHub runners vary run-to-run
(4-8% noise exceeded the 2x-baseline gate even after two re-measures),
turning a zero-code-change dependabot PR and main red. Per pipeline
discipline, heavy flaky suites must not block PR checks: the
performance-tests job runs on main pushes only, never fails the workflow,
and CI Summary reports (not fails on) regressions.

Runs in CI via: python3 -m unittest discover -s scripts/perf/tests
"""

import re
import unittest
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[3]
CI_YML = REPO_ROOT / ".github" / "workflows" / "ci.yml"


def job_block(text, job_name):
    """Extract the raw YAML lines of a top-level job (two-space indent)."""
    lines = text.splitlines()
    start = next(
        i for i, line in enumerate(lines) if re.match(rf"^  {re.escape(job_name)}:\s*$", line)
    )
    block = []
    for line in lines[start + 1:]:
        if re.match(r"^  \S", line):
            break
        block.append(line)
    return "\n".join(block)


class CiPerfGateTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.ci = CI_YML.read_text()

    def test_perf_job_runs_on_main_push_only(self):
        block = job_block(self.ci, "performance-tests")
        cond = next(line for line in block.splitlines() if "if:" in line)
        self.assertNotIn(
            "pull_request", cond,
            "performance-tests must not run on pull_request events (flaky gate blocked PRs)",
        )
        self.assertIn("refs/heads/main", cond)

    def test_perf_job_cannot_fail_workflow(self):
        block = job_block(self.ci, "performance-tests")
        self.assertRegex(
            block, r"continue-on-error:\s*true",
            "performance-tests must set continue-on-error so noise never fails the run",
        )

    def test_ci_summary_does_not_hard_fail_on_perf(self):
        block = job_block(self.ci, "ci-summary")
        perf_if = next(
            line for line in block.splitlines() if "needs.performance-tests" in line
        )
        # The advisory branch must use `outcome` (actual result, unaffected by
        # continue-on-error) rather than `result`.
        self.assertIn("outcome", perf_if)
        start = block.index(perf_if)
        end = block.index("fi", start)
        branch = block[start:end]
        self.assertNotIn(
            "exit 1", branch,
            "ci-summary must not exit non-zero on performance regressions",
        )
        self.assertIn("::notice::", branch)


if __name__ == "__main__":
    unittest.main()
