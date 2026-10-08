import importlib.util
import unittest
from pathlib import Path


SOURCE = Path(__file__).resolve().parents[1] / "output_write.py"
SPEC = importlib.util.spec_from_file_location("output_write", SOURCE)
writer = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(writer)


class PrefixSink:
    def __init__(self, cap=12287):
        self.cap = cap
        self.data = bytearray()
        self.requests = []
        self.flushes = 0

    def write(self, data):
        self.requests.append(len(data))
        count = min(self.cap, len(data))
        self.data.extend(data[:count])
        return count

    def flush(self):
        self.flushes += 1


class OutputWriteTests(unittest.TestCase):
    def test_observed_raw_console_cap_preserves_suffix_exactly(self):
        data = bytes(range(256)) * 214 + b"partial final suffix"
        sink = PrefixSink()
        written, calls, short = writer.write_all(sink, data)
        self.assertEqual(sink.data, data)
        self.assertEqual(written, len(data))
        self.assertEqual(calls, 5)
        self.assertEqual(short, 4)
        self.assertEqual(sink.requests[-1], len(data) - 4 * 12287)
        self.assertEqual(sink.flushes, 0)

    def test_multiple_chunks_and_partial_final_chunk_are_byte_exact(self):
        line = b"The quick brown fox jumps over the lazy dog 0123456789\n"
        sink = PrefixSink()
        counts = [writer.write_all(sink, payload) for payload in (line * 1000, line * 1000, line * 237)]
        self.assertEqual(sink.data, line * 2237)
        self.assertEqual(sum(count[0] for count in counts), len(line) * 2237)
        self.assertEqual(sum(count[1] for count in counts), 12)
        self.assertEqual(sum(count[2] for count in counts), 9)

    def test_arbitrary_prefix_sizes_preserve_nonrepeating_payload(self):
        data = bytes(range(251)) * 17
        for cap in (1, 7, 127, len(data), len(data) + 1):
            with self.subTest(cap=cap):
                sink = PrefixSink(cap)
                written, calls, short = writer.write_all(sink, data)
                self.assertEqual(sink.data, data)
                self.assertEqual(written, len(data))
                self.assertEqual(calls, (len(data) + cap - 1) // cap)
                self.assertEqual(short, calls - 1)

    def test_empty_payload_does_not_write(self):
        sink = PrefixSink()
        self.assertEqual(writer.write_all(sink, b""), (0, 0, 0))
        self.assertEqual(sink.requests, [])

    def test_no_progress_after_valid_prefix_is_rejected_without_retry(self):
        for invalid in (None, 0, -1, 99, 1.5, True):
            with self.subTest(invalid=invalid):
                class StopsAfterPrefix:
                    def __init__(self):
                        self.calls = 0
                        self.data = bytearray()

                    def write(self, data):
                        self.calls += 1
                        if self.calls == 1:
                            self.data.extend(data[:3])
                            return 3
                        return invalid

                sink = StopsAfterPrefix()
                with self.assertRaises(OSError):
                    writer.write_all(sink, b"abcdefgh")
                self.assertEqual(sink.calls, 2)
                self.assertEqual(sink.data, b"abc")

    def test_sink_errors_propagate(self):
        class BrokenSink:
            def write(self, data):
                raise BrokenPipeError("closed")

        with self.assertRaisesRegex(BrokenPipeError, "closed"):
            writer.write_all(BrokenSink(), b"data")


if __name__ == "__main__":
    unittest.main()
