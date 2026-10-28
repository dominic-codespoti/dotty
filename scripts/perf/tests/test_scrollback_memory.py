#!/usr/bin/env python3
import importlib.util
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("scrollback_memory", ROOT / "scrollback_memory.py")
SM = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SM)

SMAPS = """\
00400000-ffffffffff601000 ---p 00000000 00:00 0                          [rollup]
Rss:               20480 kB
Pss:               10240 kB
Pss_Dirty:          9000 kB
Pss_Anon:           8192 kB
Private_Dirty:      7000 kB
THPeligible:           0
"""


def sample(rep, history, cols, tabs, stage, pss, memory=None):
    return {"rep": rep, "history": history, "cols": cols, "tabs": tabs, "stage": stage,
            "pss": pss, "rss": pss * 2, "pss_anon": pss // 2, "memory": memory or {}}


class ParseTests(unittest.TestCase):
    def test_smaps_rollup_converts_kb_to_bytes_and_skips_header(self):
        v = SM.parse_smaps_rollup(SMAPS)
        self.assertEqual(v["Rss"], 20480 * 1024)
        self.assertEqual(v["Pss"], 10240 * 1024)
        self.assertEqual(v["Pss_Anon"], 8192 * 1024)
        self.assertNotIn("THPeligible", v)
        self.assertNotIn("00400000-ffffffffff601000 ---p 00000000 00", v)

    def test_smaps_empty(self):
        self.assertEqual(SM.parse_smaps_rollup(""), {})

    def test_memory_json_tolerant(self):
        m = SM.parse_memory_json('{"terminalArenaCapacityBytes":5,"x":"s","b":true,"f":1.5}')
        self.assertEqual(m, {"terminalArenaCapacityBytes": 5, "f": 1.5})
        self.assertEqual(SM.parse_memory_json("ERROR nope"), {})
        self.assertEqual(SM.parse_memory_json("[1]"), {})


class MathTests(unittest.TestCase):
    def test_median(self):
        self.assertEqual(SM.median([3, 1, 2]), 2)
        self.assertEqual(SM.median([4, 1, None, 2]), 2)
        self.assertIsNone(SM.median([None]))

    def test_marginal(self):
        self.assertEqual(SM.marginal_per_tab(1900, 100, 20), 1800 / 19)
        self.assertIsNone(SM.marginal_per_tab(None, 1, 20))
        with self.assertRaises(ValueError):
            SM.marginal_per_tab(1, 1, 1)

    def test_ring_model_matches_plan_numbers(self):
        # 5000 history + 24 rows, 80 cols, 12 B/cell, 20 tabs, 2 rings (main + spare) ~ 184 MiB.
        total = SM.model_ring_bytes(5000, 24, 80, rings=2) * 20
        self.assertAlmostEqual(total / SM.MIB, 184.0, delta=0.5)

    def test_committed_bound_is_page_rounded_and_capped(self):
        page = 4096
        small = SM.model_committed_bound(0, 5000, 24, 80, page)
        self.assertEqual(small % page, 0)
        self.assertLess(small, 25 * 1024)
        self.assertEqual(SM.model_committed_bound(10**6, 100, 24, 80, page),
                         SM.model_committed_bound(100, 100, 24, 80, page))
        self.assertEqual(SM.model_committed_bound(0, 5000, 24, 80, page, rings=2), 2 * small)


class MatrixTests(unittest.TestCase):
    def test_default_matrix_size_and_order(self):
        m = SM.expand_matrix()
        self.assertEqual(len(m), 3 * 3 * 2 * 2)
        self.assertEqual(m[0], (0, 0, 80, 1))
        self.assertEqual(m[1], (0, 0, 80, 20))

    def test_custom_matrix(self):
        self.assertEqual(SM.expand_matrix((1,), (5,), (80,), 2), [(0, 5, 80, 1), (1, 5, 80, 1)])

    def test_stage_names_match_constants(self):
        self.assertEqual(tuple(s for s, _ in SM.stage_commands(80)), SM.STAGES)
        resize = dict(SM.stage_commands(80))["resize2"]
        self.assertEqual(resize, [("resize", 100), ("resize", 80)])


class CommandFramingTests(unittest.TestCase):
    def test_marker_not_in_typed_command(self):
        marker = "MKabcdef0123"
        cmd = SM.fill_command(10, marker)
        self.assertNotIn(marker, cmd)
        self.assertIn("head -n 10", cmd)
        self.assertTrue(cmd.endswith("echo MK''abcdef0123"))

    def test_zero_history_has_no_fill(self):
        cmd = SM.fill_command(0, "MKzzzzzzzzzz")
        self.assertNotIn("yes", cmd)

    def test_escape_command(self):
        cmd = SM.escape_command(r"\033[2J", "MKq1w2e3r4t5")
        self.assertTrue(cmd.startswith(r"printf '\033[2J'; echo MK''"))

    def test_new_marker_unique(self):
        self.assertNotEqual(SM.new_marker(), SM.new_marker())


class SummaryTests(unittest.TestCase):
    def build(self):
        s = []
        for rep, (p1, p20) in enumerate([(100 * SM.MIB, 290 * SM.MIB), (110 * SM.MIB, 300 * SM.MIB),
                                         (105 * SM.MIB, 400 * SM.MIB)]):
            s.append(sample(rep, 500, 80, 1, "fresh", p1))
            s.append(sample(rep, 500, 80, 20, "fresh", p20, {"terminalArenaCapacityBytes": 7}))
        return s

    def test_median_marginal(self):
        rows = SM.summarize(self.build(), (1, 20))
        self.assertEqual(len(rows), 1)
        r = rows[0]
        self.assertEqual(r["pss_lo"], 105 * SM.MIB)
        self.assertEqual(r["pss_hi"], 300 * SM.MIB)
        self.assertEqual(r["pss_marginal_per_tab"], 195 * SM.MIB / 19)
        self.assertEqual(r["runs"], 3)
        self.assertEqual(r["memory"]["terminalArenaCapacityBytes"], 7)
        self.assertIsNone(r["memory"]["terminalArenaCommittedBoundBytes"])

    def test_single_tab_count_gives_no_marginal(self):
        rows = SM.summarize([sample(0, 0, 80, 1, "fresh", SM.MIB)], (1,))
        self.assertIsNone(rows[0]["pss_marginal_per_tab"])

    def test_stage_ordering(self):
        s = [sample(0, 0, 80, t, st, SM.MIB * t) for st in ("ed3", "fresh", "alt") for t in (1, 20)]
        rows = SM.summarize(s, (1, 20))
        self.assertEqual([r["stage"] for r in rows], ["fresh", "alt", "ed3"])


class RenderTests(unittest.TestCase):
    def test_table_blank_for_missing_and_columns_stable(self):
        rows = SM.summarize([sample(0, 0, 80, 1, "fresh", SM.MIB), sample(0, 0, 80, 20, "fresh", 20 * SM.MIB,
                                                                          {"terminalArenaCapacityBytes": 123})],
                            (1, 20))
        md = SM.render_markdown(rows, {"git_sha": "abc"})
        self.assertIn("- git_sha: abc", md)
        table = [l for l in md.splitlines() if l.startswith("|")]
        self.assertEqual(len(table), 3)
        counts = {l.count("|") for l in table}
        self.assertEqual(len(counts), 1)
        body = table[2].split("|")[1:-1]
        header = table[0].split("|")[1:-1]
        idx = [h.strip() for h in header].index("terminalArenaCapacityBytes")
        self.assertEqual(body[idx].strip(), "123")
        idx2 = [h.strip() for h in header].index("compressedHistoryBytes")
        self.assertEqual(body[idx2].strip(), "")
        self.assertEqual(body[4].strip(), "1.00")  # 19 MiB / 19 tabs


if __name__ == "__main__":
    unittest.main()
