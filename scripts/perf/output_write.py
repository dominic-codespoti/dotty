"""Byte-exact writes for benchmark producers, including raw short-write sinks."""


def write_all(out, data):
    """Write every byte, returning (acknowledged bytes, calls, short writes).

    A write acknowledges only its returned prefix. None/zero/negative results
    cannot make progress and are errors, not permission to discard the suffix.
    The caller owns flushing and any workload timestamps.
    """
    view = memoryview(data).cast("B")
    written = calls = short_writes = 0
    while written < len(view):
        remaining = len(view) - written
        count = out.write(view[written:])
        if not isinstance(count, int) or isinstance(count, bool) or not 0 < count <= remaining:
            raise OSError(f"invalid output write count: {count!r} for {remaining} bytes")
        calls += 1
        short_writes += count < remaining
        written += count
    return written, calls, short_writes
