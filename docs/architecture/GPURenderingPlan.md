# GPU Rendering Plan

Status: **Implemented in the Silk.NET/OpenGL host; current work is validation and incremental optimization** · Last updated: 2026-09-17

## 1. Goal

The shipped host renders terminal cells through a persistent A8 glyph atlas and OpenGL
3.3 instanced quads. `TerminalSceneComposer` captures immutable visible snapshots,
`QuadFrameBuilder` converts them to cell instances, and `SilkTerminalRenderer`
uploads the instances and atlas texture to the GLFW/OpenGL context. The GPU path is
the production path; parser, buffer, snapshot, atlas, and scene-composition costs
remain separately measurable.

The atlas and instance design provide headroom for large windows while preserving
the terminal-core contract: capture a consistent frame under a bounded buffer lock,
then rasterize and submit it after releasing that lock.

## 1. Scope and constraints

- The renderer uses OpenGL 3.3 through Silk.NET and GLFW. It does not depend on a
  UI-framework draw-operation or a framework-owned bitmap surface.
- Glyph coverage is stored once in an A8 atlas; foreground and background colors
  remain per-instance data.
- The host keeps the terminal buffer and renderer decoupled through
  `IRenderSource`, which is implemented by both the live buffer and immutable
  `RenderSnapshot`.
- Vulkan, Metal, and bespoke platform renderers are outside this design. The
  current host has one OpenGL submission path.

## 2. Current implementation context

The renderer consumes the same terminal surface regardless of whether the source
is a live `TerminalBuffer` or a captured `RenderSnapshot`. The snapshot path copies
the visible rows and metadata while the caller holds `SyncRoot`; the OpenGL scene
is then composed without that lock.

- `src/Dotty.Terminal/Adapter/Buffer/RenderSnapshot.cs` owns the immutable,
  caller-disposed copy used by the host.
- `src/Dotty.Rendering.Gpu/QuadFrameBuilder.cs` resolves cells, styles, wide-cell
  continuation, and atlas placement into `CellInstance` values.
- `src/Dotty/Rendering/TerminalSceneComposer.cs` handles panes, selection,
  overlays, and chrome around the terminal instances.
- `src/Dotty/SilkTerminalRenderer.cs` performs instanced OpenGL draws for cell
  backgrounds, glyph coverage, decorations, and chrome.

The GPU work is therefore compared against the actual shipped OpenGL path, not
against a framework presentation backend.

## 3. Glyph atlas

### 3.1 Design

- **Format:** single-channel coverage (alpha), not baked RGBA. Glyph color is applied
  at draw time via vertex color / shader — one atlas entry serves every fg color.
- **Key:** `(grapheme, typeface, size, bold)`. No fg color or shaping policy is
  in the key. Bold must actually apply to rasterization (the old code keyed on
  Bold but never set it on the font — duplicate identical entries).
- **GlyphInfo:** atlas X/Y, width/height, advance, baseline offset, **left/top
  bearings** (the old atlas omitted bearings — placement was unverifiable).
- **Cells:** width-2 wide glyphs and continuation semantics must be represented
  (the old atlas ignored `cc.Width` and drew wide glyphs into one cell).
- **Packing:** shelf packing with a byte budget + LRU eviction (service-level, whole
  atlas per font config; budget per `GlyphAtlasService.MaxTotalBytes` precedent: 32 MB).
- **Color glyphs (emoji):** explicit decision required — separate RGBA atlas or
  documented tofu/fallback. Scope for v1: **tofu/fallback** (documented), RGBA atlas
  as a follow-up. This is a deliberate scope cut; Wezterm/kitty have a full color-font
  path that is out of scope for v1.
- **Threading:** atlas entries are ensured while composing a frame; snapshot
  capture never touches mutable atlas state while holding the buffer lock.
  Reuse the `RenderSnapshot` lifetime pattern.

### 3.2 Failure modes of the deleted atlas (spec)

| Defect (observed 2026-08-13) | Required behavior |
|---|---|
| `Rgba8888` premul with fg RGB baked at rasterize | A8 coverage only |
| Key included `ForegroundHex`; discovery inserted `#RRGGBB`, shader looked up null → guaranteed miss | No color in key; single key contract |
| `Bold` in key but never applied to font | Bold applied or removed from key |
| No bearings; shader centered by bounds width | Bearings + advance + baseline in `GlyphInfo` |
| No wide-cell/continuation/fallback-typeface representation | Width, continuation, typeface identity in key/metadata |
| Unbounded doubling growth, no per-atlas cap; `Dispose`/`AtlasBitmap` unsynchronized | Byte budget + eviction; locked lifetime |

### 3.3 Atlas implementation

The implementation lives in `src/Dotty.Rendering.Gpu/GlyphAtlas.cs` and
`GlyphAtlasService.cs`; coverage and packing behavior are exercised by
`tests/Dotty.App.Tests/GlyphAtlasTests.cs`.

- A8 rasterization stores coverage, while the instance foreground supplies color.
- `GlyphKey` contains grapheme, typeface instance, text size, and bold state; it
  deliberately excludes foreground color.
- `GlyphInfo` records tight bounds, bearings, advance, and baseline placement.
- Wide cells preserve their width and continuation semantics in
  `QuadFrameBuilder`; a full atlas uses the reserved fallback glyph.
- Atlas growth is capped at 4096² A8 pixels and service-level retention is
  reference-counted and budgeted.
- `EnsureGlyphShaped` can rasterize a pre-shaped `SKTextBlob`, preserving glyph
  positions for ligature-aware callers even though the cell builder currently
  resolves individual graphemes.

The atlas exposes its backing bitmap only under its lock; the OpenGL texture
manager uploads a stable version and refreshes after atlas growth.

## 4. Instanced OpenGL rendering

`QuadFrameBuilder` emits one instance for each drawable cell and records atlas
rows that were populated during the build. `TerminalSceneComposer` appends pane
offsets, selection colors, tab/menu overlays, and chrome geometry before handing
the frame to `SilkTerminalRenderer`.

The renderer maintains reusable CPU staging arrays and retained instance
storage. Captured frames refresh the instance VBO; when presentation is skipped,
the prior uploaded data remains available. The OpenGL 3.3 shaders use normalized
atlas coordinates, device-pixel snapping for background cell boundaries, A8
coverage for glyphs, and separate decoration-only instances for underline,
strike, and overline.

`QuadFrameBuilderTests`, `QuadRowCacheTests`, `GlyphAtlasTests`, and
`TerminalSceneComposerTests` cover cell emission, wide cells, atlas misses and
growth, row invalidation metadata, and overlays. These tests validate conversion
and scene composition; end-to-end GPU presentation still requires a running
OpenGL host.

### 4.2 Geometry and color invariants

- A wide cell occupies two cell widths and its continuation cell emits no glyph.
- The atlas stores no foreground color; inverse video is resolved before upload.
- Cell background, glyph coverage, and decoration passes use premultiplied
  alpha with the instance colors.
- Absolute framebuffer coordinates are converted to clip space by the OpenGL
  vertex shader, so scrolling and pane offsets are represented in instance
  positions rather than a per-frame cell-data texture.

## 5. Snapshot and presentation boundaries

Each pane render attempts a bounded `Monitor.TryEnter` on its buffer, marks the
render boundary, captures the visible snapshot, and releases `SyncRoot` before
atlas lookup, instance construction, and OpenGL submission. A missed lock skips
that pane for the frame rather than rasterizing a partially parsed buffer.

The snapshot is immutable and caller-owned. This is the boundary that keeps
terminal mutation, CPU scene construction, and GPU submission from sharing
mutable cell storage. It also makes the same scene builder usable by diagnostics
and input features that need a stable frame.

## 6. Verification

- Unit coverage exercises atlas packing and fallback, cell-instance conversion,
  wide-cell and continuation handling, row-cache metadata, and scene overlays.
- The host checks the OpenGL version during initialization and reports an
  initialization failure before starting the render loop when the required
  version is unavailable.
- Performance work must measure snapshot capture, scene composition, atlas
  uploads, instance-buffer upload, and draw submission separately. A faster
  parser or atlas lookup is not evidence of a faster presented frame.
- Pixel comparisons are useful for deterministic scene-builder and shader
  probes, but a complete presentation result requires the running Silk.NET host.

## 7. Remaining work

The shipped path still has ordinary optimization opportunities: avoid rebuilding
unchanged pane instances, keep atlas uploads versioned, and measure the cost of
large multi-pane frames. These are OpenGL-host optimizations and must preserve
the immutable-snapshot boundary and the wide-cell, style, and decoration
contracts above.

The implementation is maintained in the source and tests named above; no
framework-specific presentation step is implied.

## 8. Risks and decisions

| Risk | Control |
|---|---|
| Snapshot capture competes with parser writes | Bounded lock acquisition, reader-priority handoff, and immutable capture |
| Atlas growth invalidates the uploaded texture | Atlas generation/version tracking and locked bitmap upload |
| Wide cells, fallback glyphs, or decorations diverge from terminal semantics | Cell-instance tests plus explicit width and flag handling |
| Fractional framebuffer scales create seams | Absolute-coordinate device-pixel snapping in the vertex shader |
| OpenGL unavailable or below the required version | `GraphicsCapabilities` check and an explicit initialization failure |

| Decision | Evidence |
|---|---|
| Use A8 coverage keyed without foreground color | `GlyphAtlas` key and packing contract |
| Keep scene composition separate from OpenGL submission | `TerminalSceneComposer` produces host-neutral instances |
| Capture visible rows before CPU/GPU work | `RenderSnapshot.CaptureVisible` and `IRenderSource` contract |
| Keep shaped-blob rasterization available at the atlas boundary | `EnsureGlyphShaped` preserves pre-shaped glyph positions |

## 9. Open questions

- Measure whether row-instance reuse materially reduces CPU composition cost for
  sustained output before adding another cache to the live path.
- Complete hardware-GL presentation measurements on each supported desktop
  platform; current repository checks cover host-native behavior and Ubuntu X11
  smoke, not every compositor.
