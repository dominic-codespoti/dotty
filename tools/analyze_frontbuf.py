#!/usr/bin/env python3
import sys
from pathlib import Path
from PIL import Image
import hashlib

def sha1_bytes(b: bytes) -> str:
    return hashlib.sha1(b).hexdigest()


def analyze_image(path: Path, row_h: int = None, left_w: int = None):
    im = Image.open(path).convert('RGBA')
    w, h = im.size
    if row_h is None:
        # try to infer row height by scanning for common dstY spacing: default 20
        row_h = 20
    if left_w is None:
        left_w = max(1, w // 6)

    rows = h // row_h
    rows = int(rows)

    full_hashes = []
    left_hashes = []
    for r in range(rows):
        y = r * row_h
        box = (0, y, w, y + row_h)
        band = im.crop(box)
        full_hashes.append(sha1_bytes(band.tobytes()))
        left_band = band.crop((0,0,left_w,row_h))
        left_hashes.append(sha1_bytes(left_band.tobytes()))

    # detect repeats
    repeats = []
    i = 0
    while i < rows:
        j = i+1
        while j < rows and full_hashes[j] == full_hashes[i]:
            j += 1
        length = j - i
        if length > 1:
            repeats.append((i, length, full_hashes[i]))
        i = j

    # print summary
    print(f"Image: {path}\n  size={w}x{h} rows={rows} row_h={row_h} left_w={left_w}")
    print(f"  distinct_full_hashes={len(set(full_hashes))} distinct_left_hashes={len(set(left_hashes))}")
    if repeats:
        print("  repeats:")
        for start, length, hsh in repeats:
            print(f"    start_row={start} length={length} hash={hsh}")
    else:
        print("  repeats: none")

    # output CSV lines to stdout
    print('\ncsv: png,row,dstY,imageHash,leftHash')
    for r in range(rows):
        print(f"{path.name},{r},{r*row_h},{full_hashes[r]},{left_hashes[r]}")
    print('\n---\n')


if __name__ == '__main__':
    if len(sys.argv) < 2:
        print('Usage: analyze_frontbuf.py <frontbuf.png> [more ...]')
        sys.exit(2)
    paths = [Path(p) for p in sys.argv[1:]]
    for p in paths:
        if not p.exists():
            print(f'file not found: {p}')
            continue
        analyze_image(p)
