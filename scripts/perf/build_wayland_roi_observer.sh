#!/bin/sh
set -eu
SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
# Upstream pin: https://github.com/swaywm/wlr-protocols/blob/b010a03648b88d143236de193bddbfea0c08bc84/unstable/wlr-screencopy-unstable-v1.xml
# The vendored XML carries Simon Ser and Andri Yngvason's MIT permission notice.
# Source XML SHA-256 (verified on every build): 0f50f6187965b02782a12ba346ad950d6031454f6af6e1ac5b341a279ef16d56
XML="$SCRIPT_DIR/wlr-screencopy-unstable-v1.xml"
EXPECTED=0f50f6187965b02782a12ba346ad950d6031454f6af6e1ac5b341a279ef16d56
ACTUAL=$(sha256sum "$XML" | cut -d ' ' -f 1)
if [ "$ACTUAL" != "$EXPECTED" ]; then echo "protocol XML SHA-256 mismatch: $ACTUAL" >&2; exit 1; fi
PKGDATA=$(pkg-config --variable=pkgdatadir wayland-protocols)
XDG_XML="$PKGDATA/unstable/xdg-output/xdg-output-unstable-v1.xml"
[ -r "$XDG_XML" ] || { echo "wayland-protocols xdg-output XML not found at $XDG_XML" >&2; exit 1; }
OUT=${1:-${XDG_CACHE_HOME:-"$HOME/.cache"}/dotty/wayland_roi_observer}
mkdir -p "$(dirname -- "$OUT")"
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT HUP INT TERM
wayland-scanner client-header "$XML" "$TMP/wlr-screencopy-unstable-v1-client-protocol.h"
wayland-scanner private-code "$XML" "$TMP/wlr-screencopy-unstable-v1-protocol.c"
wayland-scanner client-header "$XDG_XML" "$TMP/xdg-output-unstable-v1-client-protocol.h"
wayland-scanner private-code "$XDG_XML" "$TMP/xdg-output-unstable-v1-protocol.c"
cc -std=c11 -O2 -Wall -Wextra -Werror -Wno-unused-parameter -I"$TMP" $(pkg-config --cflags wayland-client) \
  "$SCRIPT_DIR/wayland_roi_observer.c" "$TMP/wlr-screencopy-unstable-v1-protocol.c" "$TMP/xdg-output-unstable-v1-protocol.c" \
  -o "$OUT" $(pkg-config --libs wayland-client)
printf '%s\n' "$OUT"
