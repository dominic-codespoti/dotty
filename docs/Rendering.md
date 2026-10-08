# Rendering

Dotty's desktop renderer is implemented in `src/Dotty` using Silk.NET OpenGL
and SkiaSharp. The terminal core remains headless and exposes immutable render
snapshots so rendering can run without changing parser or PTY code.

## Pipeline

1. `TerminalBuffer.CaptureRenderSnapshotVisible` copies visible cells and
   metadata while holding the buffer sync root for a bounded section.
2. `TerminalSceneComposer` classifies cells and builds background, underline,
   scrollbar, tab, pane, selection, cursor, and glyph instances.
3. `GlyphAtlasService` resolves the configured font stack, shapes glyphs, and
   stores grayscale coverage and intrinsic-color pixels in shared atlas pages.
4. `SilkTerminalRenderer` uploads atlas changes and submits instanced quads to
   the OpenGL 3.3 core context.
5. `WindowPresentationGate` coalesces invalidation reasons and allows an
   explicit swap only for a dirty frame. Synchronized terminal updates hold
   presentation until they end.

The host creates an OpenGL 3.3 window with `VSync = false` and
`ShouldSwapAutomatically = false`, then calls `SwapBuffers` after rendering a
dirty frame. This manual-swap policy is also used on Wayland: presentation is
demand-driven rather than a continuously swapped/vblank-paced loop. Clean
frames keep the existing front buffer instead of swapping stale back-buffer
contents.

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
through `WindowPresentationGate`, lifecycle callbacks, title, clipboard and
process-exit queues, and control-server commands. The wait is bounded by the
nearest deadline, computed by the pure `IdleWait.Compute`: cursor blink (only
while the window is focused), the 1 s Lua status refresh, the remaining
frame-coalescing interval, the remaining synchronized-update failsafe (which
expires lazily, so nothing else would wake the loop), selection autoscroll,
and a 100 ms safety cap that turns any missed wake into bounded lag rather
than a stall. A frame action re-entered from a GLFW refresh callback never
waits, because `glfwWaitEventsTimeout` must not be called from a callback.

Content-only frames are held to at least 8 ms apart while output streams, and
to 30 ms while the PTY consumer is backlogged, so rendering does not take the
buffer lock on every flush and interrupt parsing. Frames within 250 ms of
interactive input, and the first frame after idle, are never held back.

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
and falls back to `SKTypeface.Default`. `MeasureCell` clamps non-finite or
out-of-range font size, line-height, and framebuffer scale inputs, uses full
hinting with antialiasing and subpixel rendering disabled, and rounds the
resulting positive cell width and height to pixel-aligned values. Pane origins
are rounded to cell indices before instances are emitted, keeping grid
geometry aligned instead of accumulating fractional-pixel drift. These
settings match the atlas raster policy and avoid color-channel-dependent
subpixel coverage.

A framebuffer resize recomputes scale and cell metrics. If the scale changes,
the host obtains/acquires a new glyph atlas, updates renderer and scene-composer
resources, then releases the previous atlas. Session dimensions are recalculated
from the scaled cell geometry. Fallback typefaces are normalized to the primary
font's cell height and constrained to the available one-cell or two-cell width;
oversized fallback ink is re-rasterized at smaller scales, then replaced by the
reserved tofu glyph if it still cannot fit.

## Glyph atlas limitations

`GlyphAtlas` stores monochrome glyphs in a single-channel A8 coverage page and
intrinsic-color glyphs in a separate, lazily allocated premultiplied RGBA page.
The rasterizer probes glyph output using white and black paint, so classification
does not depend on font names or a fixed emoji range. Monochrome foreground color
and alpha are applied by the shader; intrinsic-color pixels preserve their own
RGB and are modulated only by terminal foreground alpha. Both pages share the
same glyph lookup key and dirty-upload lifecycle, while the color page's four
bytes per pixel are included in atlas memory accounting. The `MEMORY` diagnostic
also reports estimated R8 and RGBA8 texture pixel payloads from uploaded page
dimensions; these are estimates, not driver RSS or total GPU allocation. The
retained color-probe bitmap payload is reported separately from atlas page bytes.
Atlas pages have finite glyph and texture-size limits; an unrepresentable glyph
is not silently uploaded.
A native Wayland screenshot also confirmed that green ASCII and multicolor emoji
retain their intended colors in the actual OpenGL window.

## Memory diagnostics and lifecycle measurements

The terminal buffer's live arena and its managed owner arrays are separate
measurements from the GC last-heap figure. Renderer memory diagnostics also report
estimated R8/RGBA8 texture pixel payload from uploaded atlas dimensions; that
payload is not driver RSS or total GPU allocation. Retained color-probe bitmap
payload is accounted separately from atlas pages. These figures describe
application-side ownership and estimated resource payload, not a complete GPU
memory total.

In the final measured lifecycle run, the live terminal arena was 9.20 MiB both
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
final. In that final run, the live terminal arena was 14,469,120 bytes; the
render snapshot was 145,296 bytes; one CPU glyph-atlas page was 1,048,576 bytes
with an estimated 1,048,576-byte R8 payload and zero RGBA payload; scratch was
924 bytes. The separate managed-memory estimate was 41,356,360 bytes, while the
latest GC heap was 36,907,664 bytes. These are distinct counters, and the PSS
comparison consists of individual observations rather than a population or a
performance gate.
## Graphics contract

The host requests an OpenGL 3.3 core context and validates the active driver
version with `GraphicsCapabilities`. If context creation, version validation,
shader compilation, or frame submission fails, the host reports a diagnostic and
closes through the normal lifecycle path rather than leaving PTY processes
running. There is no silent software-renderer fallback.

CI exercises only Linux X11 startup under Xvfb; it does not run Wayland/Weston,
macOS desktop, or Windows desktop smoke because those hosted GUI sessions do
not provide a usable OpenGL context. Those paths are verified manually or on
local native sessions. X11/Xvfb startup does not prove Wayland, macOS, or
Windows GUI behavior.

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
