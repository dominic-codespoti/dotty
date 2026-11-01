# Rendering

Dotty's desktop renderer is implemented in `src/Dotty` using Silk.NET OpenGL
and SkiaSharp. The terminal core remains headless and exposes immutable render
snapshots so rendering can run without changing parser or PTY code.

## Pipeline

1. `TerminalBuffer.CaptureRenderSnapshotVisible` copies visible cells and
   metadata while holding the buffer sync root for a bounded section. When the
   active screen has Kitty image placements, the same hold advances playing
   animations and copies the visible placements, each with a lease on its
   pixels (see [Image layers](#kitty-image-layers)).
2. `TerminalSceneComposer` classifies cells and builds background, underline,
   scrollbar, tab, pane, selection, cursor, and glyph instances.
3. `GlyphAtlasService` resolves the configured font stack, shapes glyphs, and
   stores grayscale coverage and intrinsic-color pixels in shared atlas pages.
4. `SilkTerminalRenderer` uploads atlas changes and submits instanced quads to
   the OpenGL 3.3 core context.
   When images exist, the renderer draws three image layers around the cell
   passes: image quads below backgrounds, below text and above text.
5. `WindowPresentationGate` coalesces invalidation reasons and allows an
   explicit swap only for a dirty frame. Synchronized terminal updates hold
   presentation until they end.

The host creates an OpenGL 3.3 window with `VSync = false` and
`ShouldSwapAutomatically = false`, then calls `SwapBuffers` after rendering a
dirty frame. This manual-swap policy is also used on Wayland: presentation is
demand-driven rather than a continuously swapped/vblank-paced loop. Clean
frames keep the existing front buffer instead of swapping stale back-buffer
contents.

The window is created hidden and sized before it is shown. The initial client
size comes from `GridFit.ClientSize` (the same function CSI 8 t and the control
port use): 80x24 integer device-pixel cells plus tab-bar rows and device padding,
divided by the window-unit ratio and rounded up, so the first layout pass
(`GridFit.Compute`, the host's integer grid-fit rule) yields exactly 80x24 at any
display scale. The window is shown once: after the first
frame is presented, on the first render error, or 500 ms after load if no frame
could be presented yet (for example a synchronized-update hold), so it never
stays hidden. Showing invalidates one more frame: Wayland drops swaps on a
hidden surface, so the first visible content is presented after the show.

Focus and topology changes are presentation inputs, not just input-routing
events. Losing window focus invalidates a frame and resets transient keyboard
and mouse state (including held modifiers, pressed buttons, reporting and
selection-drag ownership, hover state, and click tracking) so a key or drag
cannot remain stuck after focus returns. The active pane receives a terminal
focus report when the application has enabled focus reporting. Frame
invalidation is also requested for content, input, resize, overlays, cursor
blink, theme/config, atlas updates, tab/pane topology, selection changes, and
selection autoscroll.

The renderer never reads live cells while the PTY consumer is mutating them.
Snapshot capture and the row cache preserve correctness under sustained PTY
output while avoiding a full scrollback copy for each frame.

### Idle and wake behavior

The host does not poll. When a frame iteration has nothing to do it blocks in
`glfwWaitEventsTimeout`, so input events end the wait on their own and every
other producer calls `HostWake.Request()` (a single deduplicated
`glfwPostEmptyEvent`) after queueing work: PTY output and other invalidations
through `WindowPresentationGate`, lifecycle callbacks, title, clipboard,
OSC 52 read and process-exit queues, VT resize requests, and control-server
commands (`HostWakeCoverageTests` drives the reachable enqueue APIs from another
thread and scans `src/Dotty/Host` so that every queue producer requests a
wake). The wait is bounded by the nearest deadline, computed by the pure
`IdleWait.Compute`: the next cursor blink toggle (only while the window is
focused and not iconified), the 1 s Lua status poll (only while an
`update_status` hook is registered and the window is not iconified), the
remaining frame-coalescing interval, the remaining synchronized-update failsafe
(which expires lazily, so nothing else would wake the loop), selection
autoscroll, a parked `WAIT_FOR_IDLE` deadline, and a 1000 ms safety cap (raised
from 100 ms once the wake-coverage test existed) that turns any missed wake into
bounded lag rather than a stall. A frame action re-entered from a GLFW refresh
callback never waits, because `glfwWaitEventsTimeout` must not be called from a
callback.

Without an `update_status` hook the status area shows the shell command state
(`running`, `exit N`), which only changes with PTY output, so it is refreshed on
frames that have pending invalidations instead of every second. While the window
is iconified nothing is drawn (pending reasons stay queued for the restore
frame), the blink and Lua deadlines are dropped, and the `theme: auto` poller,
which runs on a 5 s timer off the UI thread and spawns a helper process on
Linux and macOS, skips its checks.

The per-frame decisions live in the pure `FramePlanner` (`FramePlannerTests`):
`Gate` separates a synchronized-update hold from a missing surface (iconified or
zero-sized framebuffer); `Decide` returns `Idle` (nothing changed, keep the front
buffer), `Hold(ms)` or `Render`. Content-only frames are held to at least 8 ms
apart while output streams, and to 30 ms while the PTY consumer is backlogged,
so rendering does not take the buffer lock on every flush and interrupt parsing.
Frames within 250 ms of interactive input, the first frame, and frames with any
reason other than content are never held back.

`FramePlanner.MinContentFrameIntervalMs` can instead derive the content interval
from the refresh rate of the monitor that Silk.NET associates with the window
(its current video mode; re-read after the window moves or changes state): one
refresh period rounded down, the rate clamped to 30–240 Hz, 60 Hz when the rate
is unknown or implausible, and never below 30 ms while backlogged. That is
opt-in with `DOTTY_REFRESH_PACING=1` and off by default: the change ships only
after the `STATS` frame timings show it helps (plan-11 WI-3b). `STATS` reports
`refreshRateHz`, `refreshAwarePacing`, `contentFrameIntervalMs` and `iconified`.

Animated Kitty images add one more deadline: the earliest next frame change of
the visible animated placements (`RenderSnapshot.Graphics.NextAnimationDeadlineTicks`,
reduced by `TerminalSceneComposer` over the composed leaves and handed to the host
as `NextImageDeadlineTicks`). `IdleWait.Compute` takes it as
`ImageAnimationRemainingMs` (rounded up, at least 1 ms, `0` = none), so an
animation wakes the loop only at its frame deadlines; when the deadline has
passed the host invalidates the frame with `WindowFrameReason.ImageAnimation`.
Animation is pull-driven (no timer or thread): it advances when a frame is
captured. An image that is not captured, such as one in a hidden tab, neither
advances nor asks for a wake-up. Image textures left un-uploaded by the
per-frame upload budget requeue another frame with `WindowFrameReason.ImageUpload`.
Whether idle wakeups really stay at frame deadlines is `needs runtime verification`.

### Kitty image layers

Details of the protocol are in [Kitty graphics](KittyGraphics.md). The rendering
side works like this (written from the source, never run or measured):

- **Capture.** `RenderSnapshot.Graphics` (a pooled `KittyRenderSnapshot`) holds the
  visible placements, the virtual placements (for placeholders) and relative
  placements rooted at a virtual one. Capture costs a null check and a count test
  when the screen has no placements. Leases keep a deleted image's pixels alive
  until the frame has been drawn; `TerminalSceneComposer` releases the previous
  frame's leases before taking new ones.
- **Quads.** `ImageQuadBuilder` (outside the buffer lock) turns placements into
  instanced quads: sorted by layer, `z`, image id, placement id, then input order;
  batched by texture; **clipped on the CPU** to the pane rectangle intersected
  with the framebuffer, with the texture coordinates adjusted to the clipped part.
  All **four edges** are snapped to device pixels from the same absolute
  coordinates with `floor(v + 0.5)`, exactly like the cell background pass, so
  neighbours (a placeholder grid draws one quad per cell row) share edges at
  fractional cell sizes (DPI, zoom); a sub-pixel quad keeps one pixel.
- **Draw order.** Underlay chrome (pane backgrounds, pane dividers and the
  active-pane ring) is drawn first; then `z < -2^30` below cell backgrounds; `-2^30 <= z < 0` after
  backgrounds and before glyphs; `z >= 0` after glyphs. Scrollbar and menu chrome
  draw after the above-text layer. Textures hold premultiplied RGBA (or opaque
  RGB) and blend with `One, OneMinusSrcAlpha`.
- **Placeholders.** `PlaceholderResolver` runs on the snapshot copy and emits
  quads at `z = 0`. `U+10EEEE` cells never draw a glyph: `QuadFrameBuilder` swaps
  the rune for a space whatever the image store holds.
- **Texture cache.** `SilkImageTextureCache` (GL thread, created on the first
  frame that has image draws, keyed by the pixel buffer's serial) uploads at most
  16 MiB per frame, always admitting the first candidate so an image larger than
  the budget still uploads; caps resident textures at an estimated 256 MiB (mip
  chains counted as `4/3`; mipmaps when both sides are 256 px or more); evicts
  textures idle for more than 300 frames, then least recently used (larger first
  on ties) while over budget, never one used by the current frame; clamps uploads
  to `GL_MAX_TEXTURE_SIZE` with a box downscale; and deletes the textures of freed
  pixels through `ImageRetirementQueue`. The host's render recovery (see
  [Render failures and context loss](#render-failures-and-context-loss)) replaces
  the whole renderer, so a new cache starts empty and textures re-upload on
  demand. Hidden tabs are not captured, so their textures
  go idle and age out like any other.

These numbers are constants in the source, not measurements.

## Pane views, selection, and scrollback

Each `LeafPane` owns its `ScrollOffset` and `TextSelectionService`; a split
pane does not share either value with its siblings. Offset zero is the live
bottom of the pane. Scrolling increases the offset toward older scrollback and
is clamped to the session's retained scrollback.

Visible snapshots map view row `r` to logical row `r - ScrollOffset`, and the
scene composer applies the inverse mapping when drawing selection and search
overlays. Pointer hit testing first resolves the owning pane and reports view
and logical coordinates for that pane. When that pane has terminal mouse
reporting enabled, pointer events are sent to it; holding Shift overrides mouse
reporting so local selection, scrollbar, and scrollback behavior handles the
gesture instead. Selection autoscroll updates the
owning pane and invalidates presentation on every changed offset or active
selection row. The cursor is deliberately suppressed while a pane is scrolled
away from the bottom; it is drawn only for the active pane at offset zero.

`SelectionQuadBuilder` paints a pane's selection in one pass over that pane's
cell instances (recolouring the selected ones and marking them in a reused
per-cell coverage mask covering only the visible selected rows), then appends a
background-only instance for every selected visible cell no instance covers.
The cost is linear in instances plus selected visible cells and the pass does
not allocate after warm-up; linear (wrapped) and block selections follow
`TextSelectionService.IsCellSelected` exactly.

### Dividers and the active pane

`PaneTree.Layout` leaves a gap of `panes.dividerThickness` logical pixels
between split siblings, multiplied by the framebuffer scale and rounded to whole
device pixels (`PaneDecorationBuilder.ResolveThickness`: `0`, negative or
non-finite values give no gap; any positive value gives at least one pixel;
values above 32 are clamped). `PaneDecorationBuilder` fills every
`SplitPaneNode.DividerBounds` with the theme's chrome border colour. With
`panes.activeBorder` on and at least one split, it also paints the part of the
gap that surrounds the active pane (the pane rect inflated by the thickness and
clipped to the terminal area) in the theme accent colour; a lone pane gets no
border. These quads are the frame's underlay chrome
(`TerminalSceneFrame.UnderlayChromeCount`), drawn before the cell backgrounds:
pane origins snap to whole cells, so where a pane's cell grid overlaps the gap
by a fraction of a cell the grid wins and the decoration never covers text.
Edges are snapped with `floor(v + 0.5)`. A thickness of `0` draws nothing, so
there is then no active-pane indication either.

### Default colours, OSC 10/11 and reverse video

Each frame the composer resolves every pane's effective default colours once:
the pane's OSC 10/11 overrides (else the theme foreground/background), then, if
the pane's terminal has DECSCNM (`CSI ? 5 h`) set, the two are swapped. The
effective foreground draws default-coloured text and the selection fill cells;
inverse cells swap against the effective pair; cells with explicit SGR colours
keep them. Default-background cells have no background of their own, so when a
pane's effective background differs from the theme background the composer
adds one opaque underlay quad covering the pane rect and its cell grid, below
the dividers and every cell. The window is still cleared to the theme
background, so the window padding and the tab-bar area keep it. OSC 110/111
and RIS remove the overrides (and RIS clears DECSCNM), which removes the quad.
Splits with different overrides each get their own quad.

## Tab-strip geometry

`TabBarLayout` is shared by rendering and both hit-testing routes. Tab surfaces
start at the client area's left edge and fill the strip height; label and close
controls keep their insets inside those surfaces. Terminal `window.padding`
does not inset the strip. Flat tab surfaces do not add an outer rounded-pill
gutter or an elevation shadow.

The new-tab button meets the last visible tab with no gap and fills the same
strip height with a square-edged surface. It does not track the viewport's right
edge. Status text and custom Windows caption
controls remain right-aligned. When space is constrained, the visible tab range
keeps the active tab in view and the new-tab button stays inside the space left
by those reserved regions. Empty tab/button rectangles do not accept pointer
hits; the strip's bottom edge belongs to terminal content, not the caption.
Unused space between tabs and custom caption controls remains draggable.

## Font and scale behavior

`FontMetricsService.ResolveTypeface` tries each comma-separated family in order
and falls back to `SKTypeface.Default`. `MeasureCell` returns `CellMetrics` in
whole device pixels (cell size, baseline, half-leading `BaselineShift`, underline,
strikethrough and overline rows and thicknesses; see "Display scale and cell
metrics" below). It clamps non-finite or out-of-range inputs, uses full hinting
with antialiasing and subpixel rendering disabled (matching the atlas raster
policy, without color-channel-dependent subpixel coverage), and pane origins are
rounded to cell indices before instances are emitted, so grid geometry never
accumulates fractional-pixel drift.

The host resolves the typeface only when `font.family` changes (config load,
reload, zoom and resize reuse the same `SKTypeface` object, so the glyph atlas
and the composer row cache keep hitting); a font installed while Dotty runs is
picked up on the next `font.family` change or restart. `MeasureCell` results
are cached by value: typeface family name and style (weight, width, slant),
normalized size, line height, content scale and cell adjustments. Equal inputs never re-measure, separate
wrapper objects for the same face share an entry, and the cache is a
least-recently-used map bounded at 32 entries.

### Display scale and cell metrics

`DisplayScale.Compute` turns GLFW facts into two factors (`DisplayMetrics`):
`ContentScale` (device pixels per logical unit, for fonts, cells, padding and
chrome) and `WindowUnitsToDevice` (framebuffer pixels per window coordinate, for
pointer positions, the IME caret rectangle and client sizes). On Windows and X11
the framebuffer equals the window, so the content scale comes from
`glfwGetWindowContentScale` (before W4-C1 text was unscaled there); macOS uses the
framebuffer/window ratio (backing scale); Wayland uses the reported fractional
scale quantised to 1/120 steps, so GLFW's `round(width × n / 120)` framebuffer
jitter never wobbles the scale. A failed query (0, NaN) or a minimised 0x0 window
keeps the previous metrics.

Cells are integer device pixels; the logical view is `cell / ContentScale`.
`font.size` is in points: device pixels = `pt × 96/72 × ContentScale`. Padding is
`round(logical × ContentScale)`. The kitty graphics / `CSI 14/16 t` cell size is
exactly the layout cell. `CellMetricsCalculator` (pure) derives the cell from
`FontMetricsInput` (Skia `SKFontMetrics`): width = rounded advance, height =
rounded `max(px × lineHeight, ascent + descent)` adjusted by
`font.adjustCellWidth/Height`; extra height is split half above and half below
(floor above), and the composer adds `BaselineShift` to every cell glyph. Stroke
thicknesses come from the font (fallback 1/14 em, at least 1 px) and every stroke
is clamped inside the cell.

A framebuffer resize recomputes the display metrics and the (cached) cell metrics;
a GLFW content-scale callback (monitor moves, display-scale changes without a
resize) does the same on the UI thread. If the content scale changes, the host
obtains/acquires a new glyph atlas, updates
renderer and scene-composer resources, then releases the previous atlas.
Session dimensions are recalculated from the integer cell geometry. Fallback
typefaces are normalized to the primary font's cell height and constrained to a
one-cell or two-cell width; the span is the cluster's `UnicodeWidth`, the same
measure the buffer used to place it, and the bound is the font size in pixels
times that span rather than the measured cell width.
Oversized fallback ink is re-rasterized at smaller scales, then replaced by the
reserved tofu glyph if it still cannot fit.

## Glyph atlas limitations

`GlyphAtlas` stores monochrome glyphs in a single-channel A8 coverage page and
intrinsic-color glyphs in a separate, lazily allocated premultiplied RGBA page.
Colour classification is per face: a face with a `CBDT`/`CBLC`, `sbix`, `COLR` or
`SVG ` table is a colour face and its glyphs are probed with white and black paint;
any other face is rasterized once, straight to A8 coverage. Monochrome foreground
color and alpha are applied by the shader; intrinsic-color pixels preserve their own
RGB and are modulated only by terminal foreground alpha. Both pages share the
same glyph lookup key and dirty-upload lifecycle, while the color page's four
bytes per pixel are included in atlas memory accounting. The `MEMORY` diagnostic
also reports estimated R8 and RGBA8 texture pixel payloads from uploaded page
dimensions; these are estimates, not driver RSS or total GPU allocation. The
retained color-probe bitmap payload is reported separately from atlas page bytes.
Atlas pages have finite glyph and texture-size limits; an unrepresentable glyph
is not silently uploaded. When a page is full at 4096², the atlas sets
`NeedsRebuild`; the scene composer rebuilds it at the next frame boundary (all
entries dropped, `LayoutEpoch` bumped, visible glyphs re-ensured by that frame),
so a long session with many distinct glyphs recovers instead of drawing tofu until
restart. If the very frame after a rebuild overflows again, the next rebuild waits
for a later frame. Row caches key on `LayoutEpoch`, not on `ContentVersion`:
adding a glyph or growing a page keeps every placement and no longer forces a
recompose of every cached row.
A native Wayland screenshot also confirmed that green ASCII and multicolor emoji
retain their intended colors in the actual OpenGL window.

## Text and rendering support matrix

Current state of text and display features. "Planned" names the Wave 4 item
expected to change the row; it is not a release date or a commitment.

| Feature | Status | Notes | Planned |
|---|---|---|---|
| Font size unit | Supported | `font.size` is points (`px = pt × 96/72 × display scale`, default 11.25); cells are integer device pixels and HiDPI uses the OS content scale on every platform | |
| Font family list | Supported | `font.family` is an ordered list and the fallback order (Ghostty semantics): every installed family in turn, then installed built-in symbol/CJK/emoji families, then at most 16 faces the system matches for uncovered scalars. Default: the OS stack (Linux `monospace`, DejaVu Sans Mono, Noto Sans Mono, Liberation Mono, Noto Color Emoji; macOS Menlo, SF Mono, Monaco, Apple Color Emoji, Apple Symbols; Windows Cascadia Mono, Consolas, Segoe UI Emoji, Segoe UI Symbol). No Nerd Font is assumed | |
| `font.symbolMap` per-range overrides | Supported | Tried before the chain | |
| Colour emoji and emoji presentation | Supported | Intrinsic-colour glyphs use the RGBA atlas page. Cluster face selection: VS16, default-emoji bases, regional-indicator pairs, keycaps, tag sequences, skin-tone modifiers and ZWJ sequences with a pictograph prefer a colour face; VS15 prefers a monochrome face; variation selectors, ZWJ, modifiers and tags are not required in a face's character map. Sequences are still drawn without shaping (W4-C10) | |
| Character width and grapheme clusters | Supported | Widths generated from Unicode 16.0.0 (East Asian Width W/F wide; Cc/Cf/Mn/Me and Hangul medial/final jamo zero; ambiguous narrow); RI pairs, VS16/VS15 on the 371 emoji-variation bases; clusters are UAX #29 (.NET `StringInfo`) and re-join across writes, so a late VS16 widens the cell. See [Parsing](Parsing.md#combining-cap-grapheme-clusters-cell-width-and-the-grapheme-store). No `unicode.ambiguousWidth` option | |
| Text shaping (ZWJ sequences, combining-mark placement) | Not supported | Each grapheme is rasterized through the font's character map, one per cell; `TextShaper` exists but has no callers | W4-C10 |
| Programming ligatures / OpenType features | Not supported | | W4-C11 |
| Bidirectional / RTL text | Not supported | Decision: out of scope, as in Ghostty and kitty. Text is laid out left to right in logical order. Per-grapheme shaping (W4-C10) will shape within one cell only; it is not a bidi layout engine | none |
| Bold | Supported | The family's real bold face (`font.boldFamily` for the primary font); synthetic stroke only when the family has no heavier face | |
| Italic (SGR 3) | Supported | The family's real italic/oblique face (`font.italicFamily`, `font.boldItalicFamily` for the primary font); synthetic skew (`SkewX` −0.25) only when none is installed | |
| Faint (SGR 2) | Supported | Text colour mixed toward the cell background after inverse, keeping `font.faintOpacity` (default 0.5) of it; decorations follow the text colour | |
| Conceal (SGR 8) | Supported | No glyph; background and decorations stay | |
| Text blink (SGR 5, 6) | Supported | One rate for both, shared with the cursor blink phase (`cursor.blinkIntervalMs`); the glyph is hidden in the off phase, background and decorations stay; steady while the window is unfocused | |
| Inverse, strikethrough, overline | Supported | | |
| Underline | Partial | Single underline only. Double, curly, dotted, and dashed (SGR 4:2–4:5) and the SGR 58 underline colour are parsed but drawn as the same single underline | W4-C3 |
| Cursor shape | Supported | Block (text drawn in the cell background), beam and underline; DECSCUSR shape and blink bit, else `cursor.shape`/`cursor.blink`; DECTCEM hides it; wide characters get a two-cell cursor; unfocused windows draw `cursor.unfocusedStyle` (hollow outline by default) | |
| Pane dividers / active-pane border | Supported | `panes.dividerThickness` sets the gap (logical px, rounded to device px); dividers use the chrome border colour; `panes.activeBorder` paints the gap around the active pane in the accent colour when the tab has splits | |
| Default colour overrides (OSC 10/11, 110/111) | Supported | Per pane; the pane rect is painted with the override background, the window padding keeps the theme background | |
| Reverse video (DECSCNM, mode 5) | Supported | Swaps the pane's effective default foreground and background; explicit SGR colours are unchanged | |
| Window opacity | Not supported | `window.opacity` is not read; the window is opaque | W4-C12 |
| Configured window title | Not supported | `window.title` is not read; the title follows the active tab | W4-C13 |
| Rounded chrome | Supported | Tab bar, scrollbar, and context menus; window corners belong to the OS | |
| Bell | Partial | Window attention request while unfocused (`bell.attention`, default on), optional visual flash drawn as a separate overlay pass after the frame (`bell.visual`), tab marker, Lua `bell` event; rate-limited to one effect per 150 ms. No sound: `bell.audible` is accepted but not implemented | |
| Kitty graphics | Implemented, unverified | Off by default (`graphics.kittyProtocol`); see [Kitty graphics](KittyGraphics.md) | |
| Render failures and GPU resets | Partial | Exceptions retry, rebuild the renderer, then prompt; a driver reset (lost context) stops rendering with a title and stderr notice until Dotty restarts. See [Render failures and context loss](#render-failures-and-context-loss) | W4-C24 (context recreation) |

## Memory diagnostics and lifecycle measurements

The terminal buffer's live arena and its managed owner arrays are separate
measurements from the GC last-heap figure. Renderer memory diagnostics also report
estimated R8/RGBA8 texture pixel payload from uploaded atlas dimensions; that
payload is not driver RSS or total GPU allocation. Retained color-probe bitmap
payload is accounted separately from atlas pages. These figures describe
application-side ownership and estimated resource payload, not a complete GPU
memory total.

Image memory is reported separately: `MEMORY` adds `imagePixelBytes` and
`imageCount` (live reference-counted pixel buffers, which include buffers a
snapshot lease still holds), `placementCount`, `pendingTransferBytes` (staging of
the transfer in flight), and `imageTextureBytesEstimate` (estimated uploaded
image-texture bytes, mip chains included). They are ownership counters and
estimates, never RSS, and image storage is outside `scrollback.limitBytes`.


### Lazily committed cell arenas

Screen cell arenas are reserved address space that is committed lazily, through
`VirtualArena` (zero-filled memory that is not resident until first written).
The backend is chosen once per process by a self-test (reserve, dirty, discard,
verify zero): Linux/Android `mmap` + `madvise(MADV_DONTNEED)`, macOS `mmap` with
`MAP_FIXED` remap, Windows `VirtualAlloc` with decommit/recommit, and, when the
shim is unavailable or fails the self-test, a non-lazy `calloc` + explicit
zeroing fallback (`alloc-zeroed`). Discarded ranges below 64 KiB are zeroed in
place and keep their pages.

Two counters therefore describe a terminal's storage differently:

- `LiveArenaCapacityBytes` (`terminalArenaCapacityBytes` in `MEMORY`) is the
  reserved virtual size. It is not resident memory.
- `LiveArenaCommittedBoundBytes` (`terminalArenaCommittedBoundBytes`) is the
  matching upper bound of committed pages: dirty rows x columns x 12 B, summed
  over live screens. Caveat: it is a bound, not a measured resident size, and
  it ignores zeroed-in-place leftovers, so resident memory can exceed it by up to
  64 KiB per cleared range smaller than the memset threshold. On small
  ranges the bound is therefore a poor predictor; compare PSS only over larger
  workloads.

The `MEMORY` JSON also carries `terminalArenaBackend` (`mmap-madvise`,
`mmap-remap`, `virtualalloc` or `alloc-zeroed`).

History is discarded, and its pages handed back, by ED 2 and CSI 3 J on the main
screen, RIS, and leaving the alternate screen (the alternate screen is cleared on
exit; re-entry clears it again). A parked spare screen keeps its virtual
reservation (so the next resize allocates nothing) but holds zero committed
bytes. Every operation that discards or renumbers history other than by the ring
wrapping (CSI 3 J and ED 2 on the main screen, RIS, `ClearScreen`, and every
resize) raises `TerminalBuffer.HistoryInvalidated` once, under the buffer lock,
with a reason (`Cleared`, `Reset`, `CapacityShrunk`, `Rebased`).
`HistoryFloorAbsoluteRow` is the oldest absolute row the active screen still
retains (`max(0, totalScrolled - capacity)`); it is polled, not an event, is not
monotone (reverse scrolling lowers it, a reflow re-bases it), and equals the
scrolled-line count when there is no scrollback capacity. Entering or leaving
the alternate screen, and ED 2 / CSI 3 J while it is active, leave the main
history alone and raise nothing. Both are internal API.

The figures in the next two paragraphs were recorded **before lazy commit**
(when `LiveArenaCapacityBytes` was the whole committed arena) and are
historical: they are not what the current build reports, and no post-lazy-commit
measurement is recorded here. Whether lazy commit lowers PSS is a hypothesis
until `scripts/perf/scrollback_memory.py` is run (see
[Performance](Performance.md#scrollback-memory-measurement)).

In the final measured lifecycle run (pre-lazy-commit), the live terminal arena was 9.20 MiB both
cold and after resizing a terminal that had held a large workload; the large
workload's exact history contained 5,000 output lines (2,489 small and 2,453
large lines). The final resized-small-window PSS observation was 163.80 MiB,
compared with 194.18 MiB for the frozen build. These are individual lifecycle
observations, not a statistical population; PSS includes more than the terminal
arena and is not attributable to that arena alone. Avoid interpreting these PSS
values as driver/GPU RSS. The same optimized lifecycle trace recorded process
PSS of 112.25 MiB cold, 114.69 MiB after output, 152.12 MiB after split
closure, and 156.78, 156.79, and 156.80 MiB across three tab-closure
observations. These are lifecycle context, not a population or a comparison to
the frozen build except where a matching frozen measurement is explicitly given.

A separate frozen/final ownership comparison used a real 1,000,000-line file
(opened and navigated to EOF by Neovim itself) and the same 2,492-line terminal
history. At Neovim EOF, the frozen process-tree PSS was 323.635 MiB and the final
was 284.935 MiB; Dotty-root PSS was 211.064 MiB and 172.295 MiB respectively.
After Neovim closed, process-tree PSS was 212.697 MiB frozen and 173.950 MiB
final. In that final run (pre-lazy-commit), the live terminal arena was 14,469,120 bytes; the
render snapshot was 145,296 bytes; one CPU glyph-atlas page was 1,048,576 bytes
with an estimated 1,048,576-byte R8 payload and zero RGBA payload; scratch was
924 bytes. The separate managed-memory estimate was 41,356,360 bytes, while the
latest GC heap was 36,907,664 bytes. These are distinct counters, and the PSS
comparison consists of individual observations rather than a population or a
performance gate.
## Graphics contract

The host requests an OpenGL 3.3 core context and validates the active driver
version with `GraphicsCapabilities`. If context creation, version validation or
startup shader compilation fails, the host reports a diagnostic and closes
through the normal lifecycle path rather than leaving PTY processes running.
There is no silent software-renderer fallback.

CI exercises only Linux X11 startup under Xvfb; it does not run Wayland/Weston,
macOS desktop, or Windows desktop smoke because those hosted GUI sessions do
not provide a usable OpenGL context. Those paths are verified manually or on
local native sessions. X11/Xvfb startup does not prove Wayland, macOS, or
Windows GUI behavior.

### Render failures and context loss

A failure while drawing never closes the window or ends the shells. The pure
`RenderRecoveryPolicy` (`RenderRecoveryPolicyTests`) decides: the first
exception requeues the frame and retries it; a second one within 10 s creates a
new `SilkTerminalRenderer` on the same context and atlas (new programs and
buffers, a texture manager that re-uploads the whole glyph atlas, a fresh image
texture cache), disposes the old one only after that succeeded, and redraws
everything; a third shows a host prompt ("Try again" rebuilds once more, "Quit
Dotty" closes) and stops retrying, so a frame that keeps failing waits for new
input instead of spinning. A failure more than 10 s after the previous one
starts over. Each step is logged to stderr with a `[render]` prefix; `STATS`
reports `renderRecoveryState` and `renderFailures`.

The window requests a robust context (`GLFW_CONTEXT_ROBUSTNESS` =
`GLFW_LOSE_CONTEXT_ON_RESET`, ignored by GLFW where the platform lacks the
robustness extension). When the context offers `glGetGraphicsResetStatus`
(OpenGL 4.5, `GL_KHR_robustness` or `GL_ARB_robustness`) the host polls it
before each frame. A context lost on reset stays lost until it is recreated, so
rebuilding on it or drawing a prompt through it cannot work. A reported reset,
or `GL_CONTEXT_LOST` seen by the first frame after a rebuild, therefore moves
the policy to the terminal `ContextLost` state: rendering stops (the host drains
input and serves the shells and control commands, then waits for new work
without drawing, so it never spins), the window stays open and the shells keep
running. The host writes one `[render]` line to stderr and one crash-log entry
(`CrashReporter.Report`), and sets the window title to `Dotty — GPU reset,
rendering stopped; restart Dotty` through GLFW, which needs no GL. `STATS`
reports `"renderRecoveryState":"ContextLost"`; `SCREENSHOT:` answers `ERROR`.
**Genuine context loss requires restarting Dotty**: recreating the context
(a new window and context, renderer, atlas, overlay and native callbacks) is
not implemented (Wave 4 plan item W4-C24). The rebuild is exercised by
`SilkTerminalRendererRebuildTests` (GL lane, skipped without a context); a real
driver reset is a manual check.

### Software renderers

When `GraphicsCapabilities.Classify` reports a software rasterizer, the host
prints a one-time notice to stderr and holds content-only frames to at least
two refresh periods (at most refresh/2 frames per second; 60 Hz is assumed when
the monitor reports no rate). On Linux, Mesa's llvmpipe is what
`LIBGL_ALWAYS_SOFTWARE=1` or `GALLIUM_DRIVER=llvmpipe` select; both are useful
to reproduce software-renderer behaviour and should be unset on a machine with
a working GPU driver. Windows RDP sessions and VMs without a GPU driver offer
only the "GDI Generic" OpenGL 1.1 implementation, which fails the 3.3 check;
shipping Mesa's `opengl32sw.dll` next to the executable would cover them, but
its size and licensing have not been evaluated with the distribution lane, so
Dotty does not ship it. Menu shadows are drawn as before (the chrome blur is
not yet skipped on software renderers).

### GL diagnostics

Right after context creation the host reads `GL_VENDOR`, `GL_RENDERER`,
`GL_VERSION`, the GLSL version and `GL_MAX_TEXTURE_SIZE`, and classifies the
driver with the pure `GraphicsCapabilities.Classify(vendor, renderer, version)`:
`IsSoftware` is true for llvmpipe, softpipe, SwiftShader, the Mesa "Software
Rasterizer", Windows "GDI Generic", the Microsoft Basic Render Driver and the
Apple Software Renderer; hardware families (NVIDIA, AMD, Intel, Apple, Arm Mali,
Qualcomm Adreno) are named when recognised, anything else is `Unknown`. The
function accepts null, empty or localised strings. A startup failure after
context creation always prints the classification summary (renderer, vendor,
version) to stderr; `DOTTY_GL_INFO=1` prints the full diagnostics JSON (also
returned by the `GL` control command, see
[E2E testing](E2ETesting.md#host-control-interface)).

`DOTTY_GL_DEBUG=1` installs a `KHR_debug` message callback (synchronous output,
notifications filtered) when the context is 4.3+ or exposes `GL_KHR_debug`, and
drains `glGetError` after every `Render` call, which covers atlas and image
uploads and the draws. Messages go to stderr with a `[gl-debug]` prefix. The
callback is an `UnmanagedCallersOnly` function pointer (no delegate
marshalling), so it also works in the NativeAOT build. This is a debugging aid
with a per-frame cost; it is off by default. Unverified on real drivers
(`needs runtime verification`).

## Tests

Headless rendering contracts live in `tests/Dotty.App.Tests` and
`tests/Dotty.App.SkiaTests`. Host startup smoke uses the executable
project:

```bash
make -C src/Dotty.NativePty
dotnet build src/Dotty/Dotty.csproj -c Release
DOTTY_CONFIG_HOME=/tmp/dotty-config timeout 8s \
  xvfb-run -a dotnet run --project src/Dotty/Dotty.csproj -c Release --no-build
```

Exit status `124` is expected because the host remains open. Any earlier exit,
native loader error, OpenGL initialization diagnostic, or unhandled exception
fails the smoke run.

## Changelog

| Date | Change |
|------|--------|
| 2026-11-01 | W4-C6/C7/C8: ordered `font.family` fallback chain with OS default stacks, real bold/italic faces, emoji-presentation face selection, per-face colour detection by table tags, atlas layout epoch and rebuild-when-full. |
| 2026-11-01 | W4-C1/C2: `DisplayScale`/`GridFit` integer device-pixel layout, content-scale callback, `font.size` in points, `CellMetrics` (baseline, half-leading, decoration strokes) and `font.adjustCellWidth/Height`. |
| 2026-11-01 | W4-C21: the GL instance-stream diff (captured-frame comparison, source→output map for decorated cells, merged dirty ranges, partial vs full upload decision) moved from `SilkTerminalRenderer` into the pure `Dotty.Rendering.Gpu.InstanceDelta`, tested against the previous algorithm with a randomised oracle (`InstanceDeltaTests`). |
