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
   stores coverage in a shared atlas.
4. `SilkTerminalRenderer` uploads atlas changes and submits instanced quads to
   the OpenGL 3.3 core context.
5. `WindowPresentationGate` coalesces invalidation reasons and allows an
   explicit swap only for a dirty frame. Synchronized terminal updates hold
   presentation until they end.

The host creates an OpenGL 3.3 window with `VSync = false` and
`ShouldSwapAutomatically = false`, then calls `SwapBuffers` after rendering a
dirty frame. This manual-swap policy is also used on Wayland: presentation is
demand-driven rather than a continuously swapped/vblank-paced loop. Clean
frames keep the existing front buffer and the render loop sleeps briefly
instead of swapping stale back-buffer contents. PTY output can be coalesced
while a session reports a backlog, while interactive input keeps normal
latency.

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

`GlyphAtlas` is a single-channel A8 coverage atlas: glyphs are rasterized once
per grapheme/typeface/size/bold key, and foreground color is applied by the
shader at draw time. This keeps one atlas entry reusable across terminal
colors, but it is grayscale coverage rather than RGB subpixel coverage. Color
font layers are not preserved, so color emoji fonts may render as
monochrome/grayscale glyphs (or use the tofu fallback when a glyph cannot be
represented within the atlas or cell bounds). The atlas also has finite glyph
and texture-size limits; an unrepresentable glyph is not silently uploaded.

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
