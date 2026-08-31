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
5. `WindowPresentationGate` suppresses redundant swaps while synchronized
   updates are active and presents the next dirty frame.

The renderer never reads live cells while the PTY consumer is mutating them.
Snapshot capture and the row cache preserve correctness under sustained PTY
output while avoiding a full scrollback copy for each frame.

## Font and scale behavior

`FontMetricsService.ResolveTypeface` tries each comma-separated family in order
and falls back to `SKTypeface.Default`. `MeasureCell` accounts for framebuffer
scale, line height, ascent/descent, and wide glyph advance. Invalid or
non-finite font inputs are normalized; returned cell dimensions are finite and
positive.

A framebuffer resize recomputes scale and cell metrics. If the scale changes,
the host obtains/acquires a new glyph atlas, updates renderer and scene-composer
resources, then releases the previous atlas. Session dimensions are recalculated
from the scaled cell geometry.

## Graphics contract

The host requests an OpenGL 3.3 core context and validates the active driver
version with `GraphicsCapabilities`. If context creation, version validation,
shader compilation, or frame submission fails, the host reports a diagnostic and
closes through the normal lifecycle path rather than leaving PTY processes
running. There is no silent software-renderer fallback.

Linux CI exercises X11 with Xvfb and native Wayland setup with Weston. macOS
and Windows desktop smoke runs use native runners. A successful headless test
without a display does not prove graphics compatibility.

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
