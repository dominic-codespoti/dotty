#!/usr/bin/env python3
import importlib.util
import sys
import unittest
from pathlib import Path

PATH = Path(__file__).resolve().parents[1] / "nvim_scroll_observer.py"
SPEC = importlib.util.spec_from_file_location("observer_under_test", PATH)
OBS = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
sys.modules[SPEC.name] = OBS
SPEC.loader.exec_module(OBS)


def frame(line=12, phase=1, *, corrupt=None, orange=False, cw=3, height=3):
    bits = [(line >> shift) & 1 for shift in range(31, -1, -1)]
    bits += [(phase >> 1) & 1, phase & 1]
    bits += [sum(bits) & 1]
    colors = list(OBS._SYNC) + [OBS.MAGENTA if b else OBS.CYAN for b in bits] + list(OBS._SYNC)
    if corrupt == "parity":
        colors[38] = OBS.CYAN if colors[38] == OBS.MAGENTA else OBS.MAGENTA
    elif corrupt == "tail":
        colors[-1] = OBS.CYAN
    row = bytearray([0, 0, 0] * (cw * (len(colors) + 8) + 20))
    for i, color in enumerate(colors):
        for x in range(i * cw, (i + 1) * cw):
            row[x * 3:x * 3 + 3] = bytes(color)
    rows = [bytes(row) for _ in range(height)]
    if orange:
        mutable = bytearray(rows[-1])
        for x in range(43 * cw, 51 * cw):
            mutable[x * 3:x * 3 + 3] = bytes(OBS.ORANGE)
        rows[-1] = bytes(mutable)
    return OBS._Pixels(cw * (len(colors) + 8) + 20, height, b"".join(rows))


class DecoderTests(unittest.TestCase):
    def test_decodes_line_phase_and_eof_band(self):
        decoded = OBS._decode(frame(line=0x12345678, phase=2, orange=True), check_eof=True)
        self.assertEqual(decoded["line"], 0x12345678)
        self.assertEqual(decoded["phase"], "done")
        self.assertTrue(decoded["eof_visible"])
        self.assertEqual(decoded["cell_width"], 3)

    def test_eof_requires_aligned_orange_run_at_least_eight_cells(self):
        short = frame(phase=2, orange=True, cw=3)
        short_rgb = bytearray(short.rgb)
        final_orange_x = 43 * 3 + 8 * 3 - 1
        last_pixel = ((short.height - 1) * short.width + final_orange_x) * 3
        short_rgb[last_pixel:last_pixel + 3] = bytes((0, 0, 0))

        rotated = frame(phase=2, cw=3)
        rotated_rgb = bytearray(rotated.rgb)
        row_bytes = rotated.width * 3
        rotated_rgb[:row_bytes] = bytes((0, 255, 136)) * rotated.width

        for pixels in (
            OBS._Pixels(short.width, short.height, bytes(short_rgb)),
            OBS._Pixels(rotated.width, rotated.height, bytes(rotated_rgb)),
        ):
            decoded = OBS._decode(pixels, check_eof=True)
            self.assertEqual(decoded["phase"], "done")
            self.assertFalse(decoded["eof_visible"])
    def test_rejects_corrupt_parity_and_tail_sync(self):
        self.assertIsNone(OBS._decode(frame(corrupt="parity")))
        self.assertIsNone(OBS._decode(frame(corrupt="tail")))

    def test_literal_line_one_ready_protocol_frame(self):
        cells = list(OBS._SYNC) + [OBS.CYAN] * 31 + [OBS.MAGENTA, OBS.CYAN, OBS.CYAN, OBS.MAGENTA] + list(OBS._SYNC)
        width = 43 * 4
        row = bytearray(width * 3)
        for cell, color in enumerate(cells):
            for x in range(cell * 4, (cell + 1) * 4):
                row[x * 3:x * 3 + 3] = bytes(color)
        decoded = OBS._decode(OBS._Pixels(width, 1, bytes(row)))
        self.assertEqual((decoded["line"], decoded["phase"]), (1, "ready"))
    def test_tolerates_one_channel_capture_noise_and_left_clip(self):
        original = frame(line=0x12345678, cw=10)
        rgb = bytearray(original.rgb)
        row_bytes = original.width * 3
        for y in range(original.height):
            for x in range(10):
                offset = y * row_bytes + x * 3
                rgb[offset:offset + 3] = bytes((0, 0, 254))
            for x in range(10, 20):
                offset = y * row_bytes + x * 3
                rgb[offset:offset + 3] = bytes((254, 254, 254))
        clipped_rows = []
        for y in range(original.height):
            clipped_rows.append(bytes(rgb[y * row_bytes + 3:(y + 1) * row_bytes]))
        clipped = OBS._Pixels(original.width - 1, original.height, b"".join(clipped_rows))
        decoded = OBS._decode(clipped)
        self.assertEqual(decoded["line"], 0x12345678)
        self.assertEqual(decoded["cell_width"], 10)

class SummaryTests(unittest.TestCase):
    @staticmethod
    def s(t, line, phase="running", eof=False):
        return {"monotonic_ns": t, "line": line, "phase": phase, "eof_visible": eof}

    def test_constant_marker_includes_trailing_active_stall(self):
        samples = [self.s(0, 4), self.s(100_000_000, 4), self.s(200_000_000, 4),
                   self.s(300_000_000, 4)]
        out = OBS.summarize_samples(samples, 0, 400_000_000, 99, 10)
        self.assertEqual(out["longest_visible_stall_ms"], 400)
        self.assertEqual(out["stalls_over_250ms"], 1)
        self.assertFalse(out["final_screen_verified"])
        self.assertEqual(out["status"], "partial")

    def test_missing_samples_censor_stall_instead_of_claiming_success(self):
        samples = [self.s(0, 1), None, self.s(500_000_000, 99, "done", True)]
        out = OBS.summarize_samples(samples, 0, 500_000_000, 99, 10)
        self.assertEqual(out["dropped_samples"], 1)
        self.assertEqual(out["late_samples"], 1)
        self.assertEqual(out["observed_update_gaps_ms"], [])
        self.assertIsNone(out["longest_visible_stall_ms"])
        self.assertIsNone(out["stalls_over_100ms"])
        self.assertEqual(out["status"], "partial")
    def test_decode_loss_breaks_even_short_interval_continuity(self):
        samples = [self.s(0, 1), self.s(50_000_000, None, None), self.s(100_000_000, 90)]
        out = OBS.summarize_samples(samples, 0, 100_000_000, 90, 10)
        self.assertEqual(out["lines_advanced_between_updates"], [])
        self.assertEqual(out["observed_update_gaps_ms"], [])
        self.assertIsNone(out["longest_visible_stall_ms"])
        self.assertEqual(out["dropped_samples"], 1)
    def test_active_boundary_ignores_prestart_and_post_eof_idle(self):
        samples = [self.s(50_000_000, 1, "ready"), self.s(100_000_000, 1, "ready"),
                   self.s(200_000_000, 2, "running"),
                   self.s(450_000_000, 99, "done", True),
                   self.s(700_000_000, 1, "ready")]
        out = OBS.summarize_samples(samples, 100_000_000, 400_000_000, 99, 2)
        self.assertTrue(out["final_screen_verified"])
        self.assertEqual(out["eof_to_visible_ms"], 50)
        self.assertEqual(out["longest_visible_stall_ms"], 250)
        self.assertEqual(out["stalls_over_100ms"], 1)

    def test_contiguous_changes_and_final_eof_verification(self):
        samples = [self.s(0, 1), self.s(100_000_000, 2), self.s(200_000_000, 99, "done", True)]
        out = OBS.summarize_samples(samples, 0, 150_000_000, 99, 10)
        self.assertEqual(out["observed_update_gaps_ms"], [100, 100])
        self.assertEqual(out["eof_to_visible_ms"], 50)
        self.assertTrue(out["final_screen_verified"])
        self.assertEqual(out["lines_advanced_between_updates"], [1, 97])
        self.assertEqual(out["max_line_jump"], 97)
        self.assertEqual(out["status"], "ok")


if __name__ == "__main__":
    unittest.main()
