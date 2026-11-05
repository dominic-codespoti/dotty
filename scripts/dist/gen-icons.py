#!/usr/bin/env python3
"""Rasterise assets/icons/dotty.svg into the checked-in PNG/ICO/ICNS icon set.

Standard library only (no Skia/Pillow dependency): the master uses a small SVG subset
(rect with rx, polyline with round caps/joins, circle), which is rasterised with 4x4
supersampling. Run from anywhere; outputs are written beside the master:

    dotty-<size>.png  for 16 32 48 64 128 256 512
    dotty.ico         PNG-compressed entries 16-256 (Windows ApplicationIcon)
    dotty.icns        PNG-compressed entries 16-512 (macOS bundle icon)
"""
import math
import struct
import sys
import xml.etree.ElementTree as ET
import zlib
from pathlib import Path

ICON_DIR = Path(__file__).resolve().parents[2] / "assets" / "icons"
MASTER = ICON_DIR / "dotty.svg"
PNG_SIZES = (16, 32, 48, 64, 128, 256, 512)
ICO_SIZES = (16, 32, 48, 64, 128, 256)
# ICNS OSType per PNG size.
ICNS_TYPES = {16: b"icp4", 32: b"icp5", 64: b"icp6", 128: b"ic07", 256: b"ic08", 512: b"ic09"}
SAMPLES = 4


def parse_color(value):
    value = value.strip().lstrip("#")
    if len(value) != 6:
        raise ValueError(f"unsupported colour {value!r}")
    return tuple(int(value[i:i + 2], 16) for i in (0, 2, 4))


def parse_shapes(path):
    root = ET.parse(path).getroot()
    view = [float(v) for v in root.get("viewBox").split()]
    shapes = []
    for element in root:
        tag = element.tag.split("}")[-1]
        if tag == "rect":
            x, y = float(element.get("x", 0)), float(element.get("y", 0))
            w, h = float(element.get("width")), float(element.get("height"))
            rx = float(element.get("rx", 0))
            shapes.append(("rect", (x, y, w, h, rx), parse_color(element.get("fill"))))
        elif tag == "polyline":
            pts = [tuple(float(c) for c in p.split(",")) for p in element.get("points").split()]
            shapes.append(("line", (pts, float(element.get("stroke-width")) / 2), parse_color(element.get("stroke"))))
        elif tag == "circle":
            shapes.append(("circle", (float(element.get("cx")), float(element.get("cy")), float(element.get("r"))),
                           parse_color(element.get("fill"))))
        else:
            raise ValueError(f"unsupported element <{tag}>")
    return view, shapes


def inside(kind, geometry, x, y):
    if kind == "rect":
        rx0, ry0, w, h, r = geometry
        cx = min(max(x, rx0 + r), rx0 + w - r)
        cy = min(max(y, ry0 + r), ry0 + h - r)
        if not (rx0 <= x <= rx0 + w and ry0 <= y <= ry0 + h):
            return False
        return (x - cx) ** 2 + (y - cy) ** 2 <= r * r
    if kind == "circle":
        cx, cy, r = geometry
        return (x - cx) ** 2 + (y - cy) ** 2 <= r * r
    pts, half = geometry
    for (ax, ay), (bx, by) in zip(pts, pts[1:]):
        dx, dy = bx - ax, by - ay
        t = max(0.0, min(1.0, ((x - ax) * dx + (y - ay) * dy) / (dx * dx + dy * dy)))
        if (x - ax - t * dx) ** 2 + (y - ay - t * dy) ** 2 <= half * half:
            return True
    return False


def bounds(kind, geometry):
    if kind == "rect":
        x, y, w, h, _ = geometry
        return x, y, x + w, y + h
    if kind == "circle":
        cx, cy, r = geometry
        return cx - r, cy - r, cx + r, cy + r
    pts, half = geometry
    return (min(p[0] for p in pts) - half, min(p[1] for p in pts) - half,
            max(p[0] for p in pts) + half, max(p[1] for p in pts) + half)


def rasterise(view, shapes, size):
    """Returns straight-alpha RGBA rows; shapes are composited source-over in document order."""
    vx, vy, vw, vh = view
    scale = size / vw
    # Premultiplied float accumulation buffer.
    buf = [[0.0, 0.0, 0.0, 0.0] for _ in range(size * size)]
    offsets = [(i + 0.5) / SAMPLES for i in range(SAMPLES)]
    for kind, geometry, color in shapes:
        x0, y0, x1, y1 = bounds(kind, geometry)
        px0, py0 = max(0, int(math.floor((x0 - vx) * scale))), max(0, int(math.floor((y0 - vy) * scale)))
        px1, py1 = min(size, int(math.ceil((x1 - vx) * scale))), min(size, int(math.ceil((y1 - vy) * scale)))
        r, g, b = (c / 255 for c in color)
        for py in range(py0, py1):
            for px in range(px0, px1):
                hits = 0
                for oy in offsets:
                    sy = vy + (py + oy) / scale
                    for ox in offsets:
                        if inside(kind, geometry, vx + (px + ox) / scale, sy):
                            hits += 1
                if hits == 0:
                    continue
                a = hits / (SAMPLES * SAMPLES)
                p = buf[py * size + px]
                p[0] = r * a + p[0] * (1 - a)
                p[1] = g * a + p[1] * (1 - a)
                p[2] = b * a + p[2] * (1 - a)
                p[3] = a + p[3] * (1 - a)
    rows = []
    for py in range(size):
        row = bytearray()
        for px in range(size):
            pr, pg, pb, pa = buf[py * size + px]
            if pa <= 0:
                row += b"\0\0\0\0"
            else:
                row += bytes((round(pr / pa * 255), round(pg / pa * 255), round(pb / pa * 255), round(pa * 255)))
        rows.append(bytes(row))
    return rows


def encode_png(rows, size):
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)
    raw = b"".join(b"\0" + row for row in rows)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def encode_ico(pngs):
    header = struct.pack("<HHH", 0, 1, len(pngs))
    offset = 6 + 16 * len(pngs)
    entries, payload = b"", b""
    for size, data in pngs:
        dim = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset + len(payload))
        payload += data
    return header + entries + payload


def encode_icns(pngs):
    body = b"".join(ICNS_TYPES[size] + struct.pack(">I", len(data) + 8) + data for size, data in pngs)
    return b"icns" + struct.pack(">I", len(body) + 8) + body


def main():
    view, shapes = parse_shapes(MASTER)
    pngs = {}
    for size in PNG_SIZES:
        pngs[size] = encode_png(rasterise(view, shapes, size), size)
        (ICON_DIR / f"dotty-{size}.png").write_bytes(pngs[size])
        print(f"dotty-{size}.png", file=sys.stderr)
    (ICON_DIR / "dotty.ico").write_bytes(encode_ico([(s, pngs[s]) for s in ICO_SIZES]))
    (ICON_DIR / "dotty.icns").write_bytes(encode_icns([(s, pngs[s]) for s in sorted(ICNS_TYPES)]))


if __name__ == "__main__":
    main()
