#!/usr/bin/env python3
"""Sample and decode the benchmark's 43-cell visual progress marker.

Timings are sampled-observation estimates, not compositor or GPU frame timestamps.
"""
from __future__ import annotations

import hashlib
import base64
import json
import threading
import ctypes
import os
import re
import shutil
import subprocess
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any

_ORANGE_PIXEL = re.compile(rb"(?:[\xfc-\xff][\x85-\x8b][\x00-\x03])")


BLUE = (0, 0, 255)
WHITE = (255, 255, 255)
CYAN = (0, 255, 255)
MAGENTA = (255, 0, 255)
ORANGE = (255, 136, 0)
_SYNC = (BLUE, WHITE, BLUE, WHITE)


class VisualObserverUnavailable(RuntimeError):
    pass


class VisualObserverStopped(VisualObserverUnavailable):
    """A pending sample was cancelled because its consumer is stopping."""


class _XImage(ctypes.Structure):
    _fields_ = [
        ("width", ctypes.c_int), ("height", ctypes.c_int),
        ("xoffset", ctypes.c_int), ("format", ctypes.c_int),
        ("data", ctypes.POINTER(ctypes.c_ubyte)),
        ("byte_order", ctypes.c_int), ("bitmap_unit", ctypes.c_int),
        ("bitmap_bit_order", ctypes.c_int), ("bitmap_pad", ctypes.c_int),
        ("depth", ctypes.c_int), ("bytes_per_line", ctypes.c_int),
        ("bits_per_pixel", ctypes.c_int), ("red_mask", ctypes.c_ulong),
        ("green_mask", ctypes.c_ulong), ("blue_mask", ctypes.c_ulong),
        ("obdata", ctypes.c_void_p),
    ]


def _mask_channel(pixel: int, mask: int) -> int:
    if not mask:
        return 0
    shift = (mask & -mask).bit_length() - 1
    value = (pixel & mask) >> shift
    maximum = mask >> shift
    return (value * 255 + maximum // 2) // maximum


def _ppm(data: bytes) -> tuple[int, int, bytes]:
    # P6 permits comments and arbitrary whitespace in its header.
    i = 0
    tokens: list[bytes] = []
    while len(tokens) < 4:
        while i < len(data) and data[i] in b"\t\n\r ":
            i += 1
        if i < len(data) and data[i] == 35:
            while i < len(data) and data[i] not in b"\r\n":
                i += 1
            continue
        start = i
        while i < len(data) and data[i] not in b"\t\n\r #":
            i += 1
        if start == i:
            raise VisualObserverUnavailable("invalid PPM capture")
        tokens.append(data[start:i])
    if tokens[0] != b"P6" or tokens[3] != b"255":
        raise VisualObserverUnavailable("grim returned unsupported PPM format")
    # Consume exactly one header separator; CRLF is one logical separator.
    if i >= len(data) or data[i] not in b"\t\n\r ":
        raise VisualObserverUnavailable("invalid PPM separator")
    if data[i:i + 2] == b"\r\n":
        i += 2
    else:
        i += 1
    width, height = int(tokens[1]), int(tokens[2])
    pixels = data[i:]
    if width <= 0 or height <= 0 or len(pixels) < width * height * 3:
        raise VisualObserverUnavailable("truncated PPM capture")
    return width, height, pixels[:width * height * 3]


@dataclass
class _Pixels:
    width: int
    height: int
    rgb: bytes

    def color(self, x: int, y: int) -> tuple[int, int, int]:
        off = (y * self.width + x) * 3
        return self.rgb[off], self.rgb[off + 1], self.rgb[off + 2]


def _normalized_color(color: tuple[int, int, int]) -> tuple[int, int, int] | None:
    red, green, blue = color
    if red <= 3:
        if green >= 252 and blue >= 252:
            return CYAN
        if green <= 3 and blue >= 252:
            return BLUE
    elif red >= 252:
        if green >= 252 and blue >= 252:
            return WHITE
        if green <= 3 and blue >= 252:
            return MAGENTA
        if 133 <= green <= 139 and blue <= 3:
            return ORANGE
    return None


def _runs(pixels: _Pixels, y: int) -> list[tuple[int, int, tuple[int, int, int] | None]]:
    result = []
    left = 0
    last = _normalized_color(pixels.color(0, y))
    for x in range(1, pixels.width):
        current = _normalized_color(pixels.color(x, y))
        if current != last:
            result.append((left, x - left, last))
            left, last = x, current
    result.append((left, pixels.width - left, last))
    return result


def _has_orange_band(pixels: _Pixels, minimum_width: int) -> bool:
    needed = max(1, minimum_width)
    pattern = re.compile(_ORANGE_PIXEL.pattern * needed)
    row_bytes = pixels.width * 3
    for y in range(pixels.height - 1, -1, -1):
        row = pixels.rgb[y * row_bytes:(y + 1) * row_bytes]
        match = pattern.search(row)
        while match is not None:
            if match.start() % 3 == 0:
                return True
            match = pattern.search(row, match.start() + 1)
    return False

def _decode(pixels: _Pixels, final_line: int | None = None, *, check_eof: bool = False) -> dict[str, Any] | None:
    # Sync colors delimit fixed-width cells; adjacent equal data bits merge.
    for y in range(pixels.height - 1, -1, -1):
        runs = _runs(pixels, y)
        for i in range(max(0, len(runs) - 3)):
            if tuple(r[2] for r in runs[i:i + 4]) != _SYNC:
                continue
            cell_widths = [runs[i + j][1] for j in range(4)]
            cw = sorted(cell_widths)[len(cell_widths) // 2]
            if cw < 1 or any(abs(width - cw) > max(1, cw // 8) for width in cell_widths):
                continue
            x0 = runs[i][0]
            if x0 + 43 * cw > pixels.width:
                continue
            if tuple(_normalized_color(pixels.color(x0 + (39 + j) * cw + cw // 2, y)) for j in range(4)) != _SYNC:
                continue
            bits = []
            for j in range(35):
                color = _normalized_color(pixels.color(x0 + (4 + j) * cw + cw // 2, y))
                if color == CYAN:
                    bits.append(0)
                elif color == MAGENTA:
                    bits.append(1)
                else:
                    break
            if len(bits) != 35 or (sum(bits[:34]) & 1) != bits[34]:
                continue
            line = 0
            for bit in bits[:32]:
                line = (line << 1) | bit
            phase = (bits[32] << 1) | bits[33]
            if phase not in (0, 1, 2):
                continue
            eof_visible = False
            if check_eof and phase == 2:
                eof_visible = _has_orange_band(pixels, 8 * cw)
            anchors = tuple((cell, color) for cell, color in enumerate(_SYNC)) + tuple((39 + cell, color) for cell, color in enumerate(_SYNC))
            def matches_row(row):
                return all(_normalized_color(pixels.color(x0 + cell * cw + cw // 2, row)) == color for cell, color in anchors)
            top = y
            while top > 0 and matches_row(top - 1):
                top -= 1
            bottom = y
            while bottom + 1 < pixels.height and matches_row(bottom + 1):
                bottom += 1
            return {"line": line, "phase": ("ready", "running", "done")[phase],
                    "phase_code": phase, "eof_visible": eof_visible,
                    "cell_width": cw, "cell_height": bottom - top + 1,
                    "_marker_bounds": (x0, y, 43 * cw, 1)}
    return None


class VisualObserver:
    def __init__(self, display: str | None = None):
        self.backend = ""
        self._lib = None
        self._display = None
        self._root = None
        self._proc = None
        self._display_name = display
        self._statusline_rect: tuple[int, int, int, int] | None = None
        self._metadata = {"backend": None, "capture_scale": 1,
                          "timing_limitations": "sampled timings, not GPU frame times"}
        self._capture_process = None
        self._capture_stderr_file = None
        self._capture_condition = threading.Condition()
        self._capture_generation = 0
        self._capture_frame = None
        self._capture_error = None
        self._consumed_generation = 0
        self._capture_stopping = False
        self.continuous = False
        # Xwayland root pixels are not compositor output; never use them on Wayland.
        if os.environ.get("WAYLAND_DISPLAY"):
            grim = shutil.which("grim")
            if not grim:
                raise VisualObserverUnavailable("Wayland capture requires grim; refusing Xwayland root capture")
            self.backend = "wayland-grim"
            self._grim = grim
        else:
            self._init_x11(display)
        self._metadata["backend"] = self.backend

    @property
    def metadata(self) -> dict[str, Any]:
        return dict(self._metadata)
    def record_capture_preroll(self, sample: dict[str, Any]) -> None:
        """Retain the validated pre-GO frame as capture readiness evidence."""
        self._metadata["capture_preroll"] = {
            key: sample[key] for key in ("line", "phase", "cell_width", "cell_height", "monotonic_ns")
        }


    def _init_x11(self, display: str | None) -> None:
        try:
            lib = ctypes.CDLL("libX11.so.6")
        except OSError as exc:
            raise VisualObserverUnavailable(f"libX11 unavailable: {exc}") from exc
        lib.XOpenDisplay.argtypes = [ctypes.c_char_p]
        lib.XOpenDisplay.restype = ctypes.c_void_p
        lib.XDefaultRootWindow.argtypes = [ctypes.c_void_p]
        lib.XDefaultRootWindow.restype = ctypes.c_ulong
        lib.XGetGeometry.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.POINTER(ctypes.c_ulong),
                                     ctypes.POINTER(ctypes.c_int), ctypes.POINTER(ctypes.c_int),
                                     ctypes.POINTER(ctypes.c_uint), ctypes.POINTER(ctypes.c_uint),
                                     ctypes.POINTER(ctypes.c_uint), ctypes.POINTER(ctypes.c_uint)]
        lib.XGetGeometry.restype = ctypes.c_int
        lib.XGetImage.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.c_int, ctypes.c_int,
                                  ctypes.c_uint, ctypes.c_uint, ctypes.c_ulong, ctypes.c_int]
        lib.XGetImage.restype = ctypes.POINTER(_XImage)
        lib.XDestroyImage.argtypes = [ctypes.POINTER(_XImage)]
        lib.XDestroyImage.restype = ctypes.c_int
        dpy = lib.XOpenDisplay(display.encode() if display else None)
        if not dpy:
            raise VisualObserverUnavailable("cannot open X display")
        self._lib, self._display = lib, dpy
        self._root = lib.XDefaultRootWindow(dpy)
        self.backend = "x11-root"

    def _capture(self, rect: tuple[int, int, int, int]) -> tuple[_Pixels, tuple[int, int, int, int]]:
        x, y, width, height = map(int, rect)
        if width <= 0 or height <= 0:
            raise ValueError("capture rectangle must have positive dimensions")
        if self.backend in ("wayland-grim", "wayland-wl-shm"):
            try:
                raw = subprocess.run([self._grim, "-s", "1", "-t", "ppm", "-g", f"{x},{y} {width}x{height}", "-"],
                                     check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=5).stdout
            except (OSError, subprocess.SubprocessError) as exc:
                raise VisualObserverUnavailable(f"grim capture failed: {exc}") from exc
            w, h, rgb = _ppm(raw)
            return _Pixels(w, h, rgb), (x, y, w, h)
        root = self._root
        root_ret = ctypes.c_ulong()
        rx, ry = ctypes.c_int(), ctypes.c_int()
        rw, rh = ctypes.c_uint(), ctypes.c_uint()
        border, depth = ctypes.c_uint(), ctypes.c_uint()
        if not self._lib.XGetGeometry(self._display, root, ctypes.byref(root_ret), ctypes.byref(rx), ctypes.byref(ry), ctypes.byref(rw), ctypes.byref(rh), ctypes.byref(border), ctypes.byref(depth)):
            raise VisualObserverUnavailable("cannot query X root bounds")
        x1, y1 = max(0, x), max(0, y)
        x2, y2 = min(rw.value, x + width), min(rh.value, y + height)
        if x1 >= x2 or y1 >= y2:
            raise VisualObserverUnavailable("capture rectangle is outside the display")
        image = self._lib.XGetImage(self._display, root, x1, y1, x2 - x1, y2 - y1,
                                    ctypes.c_ulong(-1).value, 2)
        if not image:
            raise VisualObserverUnavailable("XGetImage failed")
        try:
            im = image.contents
            bpp = im.bits_per_pixel
            if bpp not in (16, 24, 32):
                raise VisualObserverUnavailable(f"unsupported XImage bits_per_pixel={bpp}")
            nbytes = (bpp + 7) // 8
            rgb = bytearray(im.width * im.height * 3)
            for py in range(im.height):
                row = ctypes.string_at(ctypes.addressof(im.data.contents) + py * im.bytes_per_line, im.bytes_per_line)
                for px in range(im.width):
                    off = px * nbytes
                    value = int.from_bytes(row[off:off + nbytes], "little" if im.byte_order == 0 else "big")
                    dst = (py * im.width + px) * 3
                    rgb[dst:dst + 3] = bytes((_mask_channel(value, im.red_mask), _mask_channel(value, im.green_mask), _mask_channel(value, im.blue_mask)))
            return _Pixels(im.width, im.height, bytes(rgb)), (x1, y1, im.width, im.height)
        finally:
            self._lib.XDestroyImage(image)

    def start_continuous(self, sample_hz: float, stderr_path: Path | None = None,
                         window_rect: tuple[int, int, int, int] | None = None) -> None:
        if self.backend != "wayland-grim":
            return
        if self._statusline_rect is None:
            raise VisualObserverUnavailable("cannot start Wayland stream before marker geometry is known")
        x, y, width, _ = self._statusline_rect
        if window_rect is None:
            raise VisualObserverUnavailable("continuous capture requires the benchmark window rectangle")
        _, window_y, _, _ = map(int, window_rect)
        height = int(self._metadata["cell_height"])
        capture_y = max(window_y, y - 2 * height + 1)
        rect = (x, capture_y, width, y - capture_y + 1)
        configured = os.environ.get("WAYLAND_ROI_OBSERVER_BIN")
        cache = Path(os.environ.get("XDG_CACHE_HOME", Path.home() / ".cache"))
        executable = Path(configured) if configured else cache / "dotty" / "wayland_roi_observer"
        executable = executable.resolve()
        if not executable.is_file():
            raise VisualObserverUnavailable(
                f"persistent Wayland observer not found at {executable}; build it with scripts/perf/build_wayland_roi_observer.sh or set WAYLAND_ROI_OBSERVER_BIN")
        helper_source = Path(__file__).with_name("wayland_roi_observer.c").resolve()
        protocol_xml = Path(__file__).with_name("wlr-screencopy-unstable-v1.xml").resolve()
        helper_provenance = {
            "path": str(executable),
            "sha256": hashlib.sha256(executable.read_bytes()).hexdigest(),
            "source_path": str(helper_source),
            "source_sha256": hashlib.sha256(helper_source.read_bytes()).hexdigest(),
            "protocol_xml_path": str(protocol_xml),
            "protocol_xml_sha256": hashlib.sha256(protocol_xml.read_bytes()).hexdigest(),
        }
        command = [str(executable), "--rect", f"{rect[0]},{rect[1]},{rect[2]},{rect[3]}", "--sample-hz", str(sample_hz)]
        try:
            self._capture_stderr_file = stderr_path.open("wb") if stderr_path else None
            self._capture_process = subprocess.Popen(command, stdout=subprocess.PIPE,
                                                     stderr=self._capture_stderr_file or subprocess.PIPE,
                                                     text=True, bufsize=1)
        except OSError as exc:
            if self._capture_stderr_file:
                self._capture_stderr_file.close()
                self._capture_stderr_file = None
            raise VisualObserverUnavailable(f"cannot start persistent Wayland observer: {exc}") from exc
        self._stream_rect = rect
        self.continuous = True
        self.backend = "wayland-wl-shm"
        self._metadata["native_helper"] = helper_provenance
        self._metadata.update({"backend": self.backend, "capture_rect": list(rect), "capture_hz": sample_hz,
                               "capture_timestamp_source": "CLOCK_MONOTONIC from persistent Wayland helper"})
        self._capture_reader = threading.Thread(target=self._read_capture_stream, name="wayland-roi-reader", daemon=True)
        self._capture_reader.start()


    def _read_capture_stream(self) -> None:
        try:
            assert self._capture_process is not None and self._capture_process.stdout is not None
            for line in self._capture_process.stdout:
                frame = json.loads(line)
                width, height = int(frame["width"]), int(frame["height"])
                rgba = base64.b64decode(frame["rgba_base64"], validate=True)
                if width != self._stream_rect[2] or height != self._stream_rect[3] or len(rgba) != width * height * 4:
                    raise ValueError("Wayland observer returned an unexpected frame size")
                rgb = bytearray(width * height * 3)
                rgb[0::3], rgb[1::3], rgb[2::3] = rgba[0::4], rgba[1::4], rgba[2::4]
                with self._capture_condition:
                    self._capture_generation += 1
                    self._capture_frame = (self._capture_generation, _Pixels(width, height, bytes(rgb)), int(frame["monotonic_ns"]))
                    self._capture_condition.notify_all()
            if not self._capture_stopping:
                status = self._capture_process.poll()
                raise VisualObserverUnavailable(f"Wayland observer stream ended unexpectedly (exit status {status})")
        except Exception as exc:
            with self._capture_condition:
                if not self._capture_stopping:
                    self._capture_error = exc
                self._capture_condition.notify_all()

    def verify_eof(self, rect: tuple[int, int, int, int], line: int, cell_width: int) -> bool:
        x, y, width, height = map(int, rect)
        marker_y = self._statusline_rect[1]
        cell_height = int(self._metadata["cell_height"])
        capture_y = max(y, marker_y - 2 * cell_height)
        eof_rect = (x, capture_y, width, max(1, min(y + height, marker_y) - capture_y))
        pixels, clipped = self._capture(eof_rect)
        visible = _has_orange_band(pixels, 8 * cell_width)
        self._metadata.update({"eof_capture_done": True, "eof_capture_rect": list(clipped),
                               "eof_capture_pixels": pixels.width * pixels.height,
                               "eof_capture_monotonic_ns": time.monotonic_ns(), "eof_visible": visible})
        if visible:
            self._metadata["eof_verified_line"] = line
        return visible

    def cancel_pending_sample(self) -> None:
        """Wake a continuous sample waiter when its consumer is shutting down."""
        if not self.continuous:
            return
        with self._capture_condition:
            self._capture_stopping = True
            self._capture_condition.notify_all()

    def sample(self, rect: tuple[int, int, int, int], timeout: float | None = None) -> dict[str, Any] | None:
        requested = tuple(map(int, rect))
        target = self._statusline_rect or requested
        if self.continuous:
            deadline = time.monotonic() + timeout if timeout is not None else None
            with self._capture_condition:
                while (self._capture_generation <= self._consumed_generation
                       and self._capture_error is None and not self._capture_stopping):
                    remaining = deadline - time.monotonic() if deadline is not None else None
                    if remaining is not None and remaining <= 0:
                        raise VisualObserverUnavailable("timed out waiting for a persistent capture frame")
                    self._capture_condition.wait(remaining)
                if self._capture_error is not None:
                    raise VisualObserverUnavailable(f"persistent Wayland capture failed: {self._capture_error}")
                if self._capture_stopping:
                    raise VisualObserverStopped("persistent Wayland capture sampling stopped")
                generation, pixels, captured_ns = self._capture_frame
                self._consumed_generation = generation
            clipped = self._stream_rect
        else:
            pixels, clipped = self._capture(target)
            captured_ns = time.monotonic_ns()
        decoded = _decode(pixels)
        if decoded is None:
            return {"line": None, "phase": None, "eof_visible": False,
                    "monotonic_ns": captured_ns, "rect": list(clipped), "backend": self.backend}
        bx, by, bw, _ = decoded.pop("_marker_bounds")
        if self._statusline_rect is None:
            self._statusline_rect = (clipped[0] + bx, clipped[1] + by, bw, 1)
            self._metadata["statusline_rect"] = list(self._statusline_rect)
            self._metadata["cell_height"] = decoded["cell_height"]
        decoded["cell_height"] = self._metadata["cell_height"]
        if self.continuous:
            eof_visible = decoded["phase_code"] == 2 and _has_orange_band(pixels, 8 * decoded["cell_width"])
            decoded["eof_visible"] = eof_visible
            if decoded["phase_code"] == 2:
                self._metadata.update({"eof_capture_done": True, "eof_capture_rect": list(clipped),
                                       "eof_capture_pixels": pixels.width * pixels.height,
                                       "eof_capture_monotonic_ns": captured_ns, "eof_visible": eof_visible})
                if eof_visible:
                    self._metadata["eof_verified_line"] = decoded["line"]
                else:
                    self._metadata.pop("eof_verified_line", None)
        elif decoded["phase_code"] == 2 and self._metadata.get("eof_verified_line") == decoded["line"]:
            decoded["eof_visible"] = True
        elif self._metadata.get("eof_verified_line") != decoded["line"]:
            self._metadata.pop("eof_verified_line", None)
        decoded.update({"monotonic_ns": captured_ns, "rect": list(clipped), "backend": self.backend})
        return decoded

    def close(self) -> None:
        if self._capture_process is not None:
            self.cancel_pending_sample()
            self._capture_process.terminate()
            try:
                self._capture_process.wait(timeout=2)
            except subprocess.TimeoutExpired:
                self._capture_process.kill()
                self._capture_process.wait()
            if hasattr(self, "_capture_reader"):
                self._capture_reader.join(timeout=2)
                if self._capture_reader.is_alive():
                    raise RuntimeError("persistent capture reader did not stop")
            if self._capture_stderr_file is not None:
                self._capture_stderr_file.close()
                self._capture_stderr_file = None
            self._capture_process = None
        if self._display:
            self._lib.XCloseDisplay.argtypes = [ctypes.c_void_p]
            self._lib.XCloseDisplay(self._display)
            self._display = None


    def __enter__(self) -> "VisualObserver":
        return self

    def __exit__(self, *_: object) -> None:
        self.close()


def summarize_samples(samples: list[dict[str, Any] | None], start_ns: int, end_ns: int,
                      final_line: int, sample_hz: float) -> dict[str, Any]:
    """Report sampled progress, censoring intervals with lost capture continuity.

    The observation window begins at traversal START and ends at the first
    verified EOF image (or the last acquisition/end when EOF is unavailable).
    Presentation tail is measured from traversal END, not START.
    """
    interval_ns = int(1e9 / sample_hz)

    def timed(item):
        return isinstance(item, dict) and isinstance(item.get("monotonic_ns"), (int, float))

    def decoded(item):
        return timed(item) and isinstance(item.get("line"), int) and item.get("phase") in ("ready", "running", "done")

    valid = [item for item in samples if decoded(item)]
    eof = next((item for item in valid if item["phase"] == "done" and
                item["line"] == final_line and item.get("eof_visible")), None)
    eof_ns = eof["monotonic_ns"] if eof else None
    final_verified = eof is not None
    last_acquired = max((item["monotonic_ns"] for item in samples if timed(item)), default=end_ns)
    active_end = max(end_ns, eof_ns) if final_verified else max(end_ns, last_acquired)
    # Keep the last pre-START observation as the initial state, not its idle time.
    first = max((i for i, item in enumerate(samples) if timed(item) and
                 item["monotonic_ns"] <= start_ns), default=0)
    relevant = [item for item in samples[first:] if not timed(item) or item["monotonic_ns"] <= active_end]
    acquisition_times = [max(start_ns, item["monotonic_ns"]) for item in relevant if timed(item)]
    acquisition_gaps = [b - a for a, b in zip(acquisition_times, acquisition_times[1:]) if b > a]
    late = sum(gap > 1.5 * interval_ns for gap in acquisition_gaps)
    dropped = sum(not decoded(item) for item in relevant)
    stalls: list[float] = []
    update_gaps: list[float] = []
    lines_advanced: list[int] = []
    previous = None
    last_change_ns = None

    def close_interval(stop_ns):
        if last_change_ns is not None and stop_ns > last_change_ns:
            stalls.append((stop_ns - last_change_ns) / 1e6)

    for item in relevant:
        if not decoded(item):
            if previous is not None:
                close_interval(max(start_ns, previous["monotonic_ns"]))
            previous = None
            last_change_ns = None
            continue
        stamp = max(start_ns, item["monotonic_ns"])
        if previous is None:
            previous = item
            last_change_ns = stamp
            continue
        prior_stamp = max(start_ns, previous["monotonic_ns"])
        if stamp - prior_stamp > 1.5 * interval_ns:
            close_interval(prior_stamp)
            previous = item
            last_change_ns = stamp
            continue
        delta = item["line"] - previous["line"]
        if delta > 0:
            lines_advanced.append(delta)
        if (item["line"], item["phase"]) != (previous["line"], previous["phase"]):
            duration = (stamp - last_change_ns) / 1e6
            if duration > 0:
                stalls.append(duration)
                update_gaps.append(duration)
            last_change_ns = stamp
        previous = item
    if previous is not None:
        last_stamp = max(start_ns, previous["monotonic_ns"])
        if active_end - last_stamp <= 1.5 * interval_ns:
            close_interval(active_end)
        else:
            close_interval(last_stamp)
            late += 1
    reasons = []
    if dropped:
        reasons.append("unrecognized captures break continuity")
    if late:
        reasons.append("capture cadence gaps censor stall intervals")
    if not final_verified:
        reasons.append("final screen not verified")
    count = sum(decoded(item) for item in relevant)
    if not count:
        reasons.append("no decoded visual samples")
    return {
        "status": "ok" if final_verified and not dropped and not late else "partial",
        "observed_update_gaps_ms": update_gaps,
        "longest_visible_stall_ms": max(stalls) if stalls else None,
        "stalls_over_100ms": sum(value > 100 for value in stalls) if stalls else None,
        "stalls_over_250ms": sum(value > 250 for value in stalls) if stalls else None,
        "stalls_over_1000ms": sum(value > 1000 for value in stalls) if stalls else None,
        "eof_to_visible_ms": max(0, eof_ns - end_ns) / 1e6 if eof_ns is not None else None,
        "samples": count, "dropped_samples": dropped, "late_samples": late,
        "lines_advanced_between_updates": lines_advanced,
        "max_line_jump": max(lines_advanced) if lines_advanced else None,
        "quality": {"reason": ", ".join(reasons) if reasons else None,
                    "sampling_limitations": "Sampled timings, not GPU frame times; capture cadence limits temporal resolution.",
                    "expected_interval_ms": interval_ns / 1e6,
                    "observed_interval_ms_mean": sum(acquisition_gaps) / len(acquisition_gaps) / 1e6 if acquisition_gaps else None},
        "final_screen_verified": final_verified,
    }
