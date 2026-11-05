#!/usr/bin/env python3
"""Generate Dotty's Unicode cell-width tables from Python's built-in unicodedata.

Outputs (both committed, both regenerated together):
  src/Dotty.Terminal/Adapter/UnicodeWidthTables.g.cs   lookup ranges used by UnicodeWidth
  tests/Dotty.Terminal.Tests/UnicodeWidthGolden.g.cs   (code point, width) golden pairs at
                                                       every range boundary, checked by
                                                       UnicodeWidthTests

Per-scalar rules (the cluster rules live in UnicodeWidth.cs):
  0  General_Category Cc, Cf, Mn or Me (except U+00AD SOFT HYPHEN, which stays 1),
     and Hangul medial/final jamo U+1160..U+11FF, U+D7B0..U+D7FF
  2  East_Asian_Width W or F (this includes every Emoji_Presentation character)
  1  everything else (ambiguous width is narrow)
The emoji-variation bases (X U+FE0F) are embedded below: U+FE0F widens exactly these
bases to 2 and U+FE0E narrows exactly these bases to 1.

Usage:
  python3 tools/gen-width-tables.py           rewrite both files
  python3 tools/gen-width-tables.py --check   verify the rules and that both files are current
"""

import argparse
import bisect
import pathlib
import sys
import unicodedata

UNICODE_VERSION = "16.0.0"
MAX_CODE_POINT = 0x10FFFF
SOFT_HYPHEN = 0x00AD
ZERO_WIDTH_CATEGORIES = frozenset(("Cc", "Cf", "Mn", "Me"))
HANGUL_ZERO_WIDTH_JAMO = ((0x1160, 0x11FF), (0xD7B0, 0xD7FF))

ROOT = pathlib.Path(__file__).resolve().parent.parent
TABLES_PATH = ROOT / "src" / "Dotty.Terminal" / "Adapter" / "UnicodeWidthTables.g.cs"
GOLDEN_PATH = ROOT / "tests" / "Dotty.Terminal.Tests" / "UnicodeWidthGolden.g.cs"

# Bases of the emoji-style variation sequences (X U+FE0F) in Unicode/Emoji 16.0
# emoji-variation-sequences.txt (dated 2024-05-01,
# https://www.unicode.org/Public/16.0.0/ucd/emoji/emoji-variation-sequences.txt): 371 entries,
# 158 of which are already East_Asian_Width W.
EMOJI_VARIATION_BASES = (
    0x0023, 0x002A, 0x0030, 0x0031, 0x0032, 0x0033, 0x0034, 0x0035, 0x0036, 0x0037,
    0x0038, 0x0039, 0x00A9, 0x00AE, 0x203C, 0x2049, 0x2122, 0x2139, 0x2194, 0x2195,
    0x2196, 0x2197, 0x2198, 0x2199, 0x21A9, 0x21AA, 0x231A, 0x231B, 0x2328, 0x23CF,
    0x23E9, 0x23EA, 0x23EB, 0x23EC, 0x23ED, 0x23EE, 0x23EF, 0x23F0, 0x23F1, 0x23F2,
    0x23F3, 0x23F8, 0x23F9, 0x23FA, 0x24C2, 0x25AA, 0x25AB, 0x25B6, 0x25C0, 0x25FB,
    0x25FC, 0x25FD, 0x25FE, 0x2600, 0x2601, 0x2602, 0x2603, 0x2604, 0x260E, 0x2611,
    0x2614, 0x2615, 0x2618, 0x261D, 0x2620, 0x2622, 0x2623, 0x2626, 0x262A, 0x262E,
    0x262F, 0x2638, 0x2639, 0x263A, 0x2640, 0x2642, 0x2648, 0x2649, 0x264A, 0x264B,
    0x264C, 0x264D, 0x264E, 0x264F, 0x2650, 0x2651, 0x2652, 0x2653, 0x265F, 0x2660,
    0x2663, 0x2665, 0x2666, 0x2668, 0x267B, 0x267E, 0x267F, 0x2692, 0x2693, 0x2694,
    0x2695, 0x2696, 0x2697, 0x2699, 0x269B, 0x269C, 0x26A0, 0x26A1, 0x26A7, 0x26AA,
    0x26AB, 0x26B0, 0x26B1, 0x26BD, 0x26BE, 0x26C4, 0x26C5, 0x26C8, 0x26CE, 0x26CF,
    0x26D1, 0x26D3, 0x26D4, 0x26E9, 0x26EA, 0x26F0, 0x26F1, 0x26F2, 0x26F3, 0x26F4,
    0x26F5, 0x26F7, 0x26F8, 0x26F9, 0x26FA, 0x26FD, 0x2702, 0x2705, 0x2708, 0x2709,
    0x270A, 0x270B, 0x270C, 0x270D, 0x270F, 0x2712, 0x2714, 0x2716, 0x271D, 0x2721,
    0x2728, 0x2733, 0x2734, 0x2744, 0x2747, 0x274C, 0x274E, 0x2753, 0x2754, 0x2755,
    0x2757, 0x2763, 0x2764, 0x2795, 0x2796, 0x2797, 0x27A1, 0x27B0, 0x27BF, 0x2934,
    0x2935, 0x2B05, 0x2B06, 0x2B07, 0x2B1B, 0x2B1C, 0x2B50, 0x2B55, 0x3030, 0x303D,
    0x3297, 0x3299, 0x1F004, 0x1F170, 0x1F171, 0x1F17E, 0x1F17F, 0x1F202, 0x1F21A, 0x1F22F,
    0x1F237, 0x1F30D, 0x1F30E, 0x1F30F, 0x1F315, 0x1F31C, 0x1F321, 0x1F324, 0x1F325, 0x1F326,
    0x1F327, 0x1F328, 0x1F329, 0x1F32A, 0x1F32B, 0x1F32C, 0x1F336, 0x1F378, 0x1F37D, 0x1F393,
    0x1F396, 0x1F397, 0x1F399, 0x1F39A, 0x1F39B, 0x1F39E, 0x1F39F, 0x1F3A7, 0x1F3AC, 0x1F3AD,
    0x1F3AE, 0x1F3C2, 0x1F3C4, 0x1F3C6, 0x1F3CA, 0x1F3CB, 0x1F3CC, 0x1F3CD, 0x1F3CE, 0x1F3D4,
    0x1F3D5, 0x1F3D6, 0x1F3D7, 0x1F3D8, 0x1F3D9, 0x1F3DA, 0x1F3DB, 0x1F3DC, 0x1F3DD, 0x1F3DE,
    0x1F3DF, 0x1F3E0, 0x1F3ED, 0x1F3F3, 0x1F3F5, 0x1F3F7, 0x1F408, 0x1F415, 0x1F41F, 0x1F426,
    0x1F43F, 0x1F441, 0x1F442, 0x1F446, 0x1F447, 0x1F448, 0x1F449, 0x1F44D, 0x1F44E, 0x1F453,
    0x1F46A, 0x1F47D, 0x1F4A3, 0x1F4B0, 0x1F4B3, 0x1F4BB, 0x1F4BF, 0x1F4CB, 0x1F4DA, 0x1F4DF,
    0x1F4E4, 0x1F4E5, 0x1F4E6, 0x1F4EA, 0x1F4EB, 0x1F4EC, 0x1F4ED, 0x1F4F7, 0x1F4F9, 0x1F4FA,
    0x1F4FB, 0x1F4FD, 0x1F508, 0x1F50D, 0x1F512, 0x1F513, 0x1F549, 0x1F54A, 0x1F550, 0x1F551,
    0x1F552, 0x1F553, 0x1F554, 0x1F555, 0x1F556, 0x1F557, 0x1F558, 0x1F559, 0x1F55A, 0x1F55B,
    0x1F55C, 0x1F55D, 0x1F55E, 0x1F55F, 0x1F560, 0x1F561, 0x1F562, 0x1F563, 0x1F564, 0x1F565,
    0x1F566, 0x1F567, 0x1F56F, 0x1F570, 0x1F573, 0x1F574, 0x1F575, 0x1F576, 0x1F577, 0x1F578,
    0x1F579, 0x1F587, 0x1F58A, 0x1F58B, 0x1F58C, 0x1F58D, 0x1F590, 0x1F5A5, 0x1F5A8, 0x1F5B1,
    0x1F5B2, 0x1F5BC, 0x1F5C2, 0x1F5C3, 0x1F5C4, 0x1F5D1, 0x1F5D2, 0x1F5D3, 0x1F5DC, 0x1F5DD,
    0x1F5DE, 0x1F5E1, 0x1F5E3, 0x1F5E8, 0x1F5EF, 0x1F5F3, 0x1F5FA, 0x1F610, 0x1F687, 0x1F68D,
    0x1F691, 0x1F694, 0x1F698, 0x1F6AD, 0x1F6B2, 0x1F6B9, 0x1F6BA, 0x1F6BC, 0x1F6CB, 0x1F6CD,
    0x1F6CE, 0x1F6CF, 0x1F6E0, 0x1F6E1, 0x1F6E2, 0x1F6E3, 0x1F6E4, 0x1F6E5, 0x1F6E9, 0x1F6F0,
    0x1F6F3,
)
EMOJI_VARIATION_BASE_COUNT = 371
EMOJI_VARIATION_WIDE_BASE_COUNT = 158

# Hand-picked scalar expectations that pin the rules (code point, width).
SPOT_CHECKS = (
    (0x0000, 0), (0x001B, 0), (0x0020, 1), (0x007E, 1), (0x007F, 0), (0x009F, 0), (0x00A0, 1),
    (0x00AD, 1), (0x00E9, 1), (0x0301, 0), (0x0600, 0), (0x093E, 1), (0x1100, 2), (0x115F, 2),
    (0x1160, 0), (0x11A8, 0), (0x11FF, 0), (0x200B, 0), (0x200C, 0), (0x200D, 0), (0x200E, 0),
    (0x2060, 0), (0x20E3, 0), (0x231A, 2), (0x2764, 1), (0x3000, 2), (0x302A, 0), (0x4E00, 2),
    (0xAC00, 2), (0xD7A3, 2), (0xD7B0, 0), (0xD7FF, 0), (0xFE0E, 0), (0xFE0F, 0), (0xFEFF, 0),
    (0xFF01, 2), (0xFF61, 1), (0x1F1E6, 1), (0x1F3FB, 2), (0x1F600, 2), (0x1F7E0, 2),
    (0x1FB00, 1), (0x1FBF9, 1), (0xE0001, 0), (0xE0100, 0), (0x20000, 2), (0x2FFFD, 2),
    (0x30000, 2), (0x3FFFD, 2), (0x10EEEE, 1),
)


def scalar_width(cp):
    for start, end in HANGUL_ZERO_WIDTH_JAMO:
        if start <= cp <= end:
            return 0
    if cp == SOFT_HYPHEN:
        return 1
    ch = chr(cp)
    if unicodedata.category(ch) in ZERO_WIDTH_CATEGORIES:
        return 0
    if unicodedata.east_asian_width(ch) in ("W", "F"):
        return 2
    return 1


def build_ranges(widths, value):
    ranges = []
    start = None
    for cp, width in enumerate(widths):
        if width == value:
            if start is None:
                start = cp
        elif start is not None:
            ranges.append((start, cp - 1))
            start = None
    if start is not None:
        ranges.append((start, len(widths) - 1))
    return ranges


def compress(code_points):
    ranges = []
    for cp in code_points:
        if ranges and ranges[-1][1] == cp - 1:
            ranges[-1] = (ranges[-1][0], cp)
        else:
            ranges.append((cp, cp))
    return ranges


def in_ranges(ranges, cp):
    i = bisect.bisect_right(ranges, (cp, MAX_CODE_POINT + 1)) - 1
    return i >= 0 and ranges[i][0] <= cp <= ranges[i][1]


def format_ranges(ranges, indent, per_line):
    items = [f"0x{s:X}, 0x{e:X}," for s, e in ranges]
    lines = []
    for i in range(0, len(items), per_line):
        lines.append(indent + " ".join(items[i:i + per_line]))
    return "\n".join(lines)


def emit_tables(zero, wide, bases):
    return f"""// <auto-generated>
// Generated by tools/gen-width-tables.py from Python unicodedata {UNICODE_VERSION}. Do not edit by hand;
// rerun the generator and commit its output (`python3 tools/gen-width-tables.py --check` verifies it).
// </auto-generated>

namespace Dotty.Terminal.Adapter;

internal static class UnicodeWidthTables
{{
    /// <summary>Unicode version of the data the tables were generated from.</summary>
    internal const string UnicodeVersion = "{UNICODE_VERSION}";

    /// <summary>
    /// Sorted, non-overlapping inclusive [start, end] pairs of zero-width scalars: General_Category Cc, Cf, Mn,
    /// Me (except U+00AD) and Hangul medial/final jamo U+1160..U+11FF, U+D7B0..U+D7FF.
    /// </summary>
    internal static ReadOnlySpan<uint> ZeroWidth => new uint[]
    {{
{format_ranges(zero, "        ", 4)}
    }};

    /// <summary>
    /// Sorted, non-overlapping inclusive [start, end] pairs of two-cell scalars: East_Asian_Width W or F, minus
    /// the zero-width scalars.
    /// </summary>
    internal static ReadOnlySpan<uint> Wide => new uint[]
    {{
{format_ranges(wide, "        ", 4)}
    }};

    /// <summary>
    /// Sorted inclusive [start, end] pairs of the {len(bases)} bases of emoji-style variation sequences
    /// (emoji-variation-sequences.txt, Unicode {UNICODE_VERSION}).
    /// </summary>
    internal static ReadOnlySpan<uint> EmojiVariationBases => new uint[]
    {{
{format_ranges(compress(bases), "        ", 4)}
    }};
}}
"""


def golden_points(zero, wide, bases):
    points = set(cp for cp, _ in SPOT_CHECKS)
    for ranges in (zero, wide, compress(bases)):
        for start, end in ranges:
            for cp in (start - 1, start, end, end + 1):
                points.add(cp)
    # Surrogates are not scalar values: .NET never yields them as a Rune.
    return sorted(cp for cp in points if 0 <= cp <= MAX_CODE_POINT and not 0xD800 <= cp <= 0xDFFF)


def emit_golden(points, widths):
    items = [f"0x{cp:X}, {widths[cp]}," for cp in points]
    lines = []
    for i in range(0, len(items), 8):
        lines.append("        " + " ".join(items[i:i + 8]))
    body = "\n".join(lines)
    return f"""// <auto-generated>
// Generated by tools/gen-width-tables.py from Python unicodedata {UNICODE_VERSION}. Do not edit by hand.
// </auto-generated>

namespace Dotty.Terminal.Tests;

internal static class UnicodeWidthGolden
{{
    /// <summary>
    /// Flattened (code point, expected scalar width) pairs at both sides of every generated range boundary,
    /// plus the generator's spot checks. Computed per code point from unicodedata, independently of the
    /// range compression.
    /// </summary>
    internal static readonly int[] Pairs =
    {{
{body}
    }};
}}
"""


def verify(widths, zero, wide, bases):
    errors = []
    if unicodedata.unidata_version != UNICODE_VERSION:
        errors.append(f"Python unicodedata is {unicodedata.unidata_version}, expected {UNICODE_VERSION}")
        return errors
    if list(bases) != sorted(set(bases)):
        errors.append("emoji variation bases are not sorted and unique")
    if len(bases) != EMOJI_VARIATION_BASE_COUNT:
        errors.append(f"{len(bases)} emoji variation bases, expected {EMOJI_VARIATION_BASE_COUNT}")
    wide_bases = sum(1 for cp in bases if widths[cp] == 2)
    if wide_bases != EMOJI_VARIATION_WIDE_BASE_COUNT:
        errors.append(f"{wide_bases} emoji variation bases are wide, expected {EMOJI_VARIATION_WIDE_BASE_COUNT}")
    for cp in bases:
        if unicodedata.category(chr(cp)) == "Cn":
            errors.append(f"emoji variation base U+{cp:04X} is unassigned")
    for name, ranges in (("zero", zero), ("wide", wide)):
        for (s0, e0), (s1, _) in zip(ranges, ranges[1:]):
            if not s0 <= e0 < s1 - 1:
                errors.append(f"{name} ranges overlap or touch at U+{s0:04X}..U+{e0:04X}")
    for cp in range(MAX_CODE_POINT + 1):
        looked_up = 0 if in_ranges(zero, cp) else 2 if in_ranges(wide, cp) else 1
        if looked_up != widths[cp]:
            errors.append(f"range lookup U+{cp:04X} = {looked_up}, per-code-point rule = {widths[cp]}")
            break
    for cp, expected in SPOT_CHECKS:
        if widths[cp] != expected:
            errors.append(f"spot check U+{cp:04X}: width {widths[cp]}, expected {expected}")
    return errors


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--check", action="store_true", help="verify instead of writing")
    args = parser.parse_args()

    if unicodedata.unidata_version != UNICODE_VERSION:
        print(f"error: Python unicodedata is {unicodedata.unidata_version}, expected {UNICODE_VERSION}", file=sys.stderr)
        return 1

    widths = [scalar_width(cp) for cp in range(MAX_CODE_POINT + 1)]
    zero = build_ranges(widths, 0)
    wide = build_ranges(widths, 2)
    bases = EMOJI_VARIATION_BASES

    errors = verify(widths, zero, wide, bases)
    outputs = {
        TABLES_PATH: emit_tables(zero, wide, bases),
        GOLDEN_PATH: emit_golden(golden_points(zero, wide, bases), widths),
    }

    if args.check:
        for path, text in outputs.items():
            if not path.exists() or path.read_text(encoding="utf-8") != text:
                errors.append(f"{path.relative_to(ROOT)} is stale; rerun tools/gen-width-tables.py")
        for error in errors:
            print(f"error: {error}", file=sys.stderr)
        if errors:
            return 1
        print(f"ok: Unicode {UNICODE_VERSION}; {len(zero)} zero-width ranges, {len(wide)} wide ranges, "
              f"{len(bases)} emoji variation bases")
        return 0

    if errors:
        for error in errors:
            print(f"error: {error}", file=sys.stderr)
        return 1
    for path, text in outputs.items():
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8", newline="\n")
        print(f"wrote {path.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
