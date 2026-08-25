# Path B — Embedded OpenGL Rendering Design

Branch: `feat/gpu-rendering` · Status: **DESIGN** · Last updated: 2026-08-16

## 1. Goal

Replace the terminal content area's CPU raster pipeline (WriteableBitmap →
SKSurface.Create(address) → per-cell Skia DrawText) with an embedded OpenGL
surface that renders glyphs via a persistent A8 coverage atlas texture,
instanced quads, and a minimal shader — eliminating CPU pixel production and
full-surface bitmap upload entirely.

## 2. Architecture Overview

```
┌─────────────────────────────────────────────────────────────┐
│ UI Thread                                                    │
│                                                              │
│  PTY parse → TerminalBuffer update (under SyncRoot)          │
│  → presentation gate coalesces → one animation frame         │
│  → CaptureRenderSnapshotBounded() // short SyncRoot hold     │
│  → publish QuadFrame { snapshot, damage, geometry }          │
│  → InvalidateVisual()                                        │
└──────────────────────────┬──────────────────────────────────┘
                           │ frame boundary
┌──────────────────────────▼──────────────────────────────────┐
│ Render Thread (OpenGlControlBase.OnOpenGlRender)             │
│                                                              │
│  bind program + atlas texture                                │
│  rebuild vertex buffer for damaged rows (~0.1ms CPU)         │
│  glDrawElementsInstanced (one call, GPU fills quads)         │
│  swap buffers                                                │
└─────────────────────────────────────────────────────────────┘
```

No WriteableBitmap. No `SKSurface.Create(info, address)`. No CPU pixel
production. The GPU does all fill work; the CPU only writes vertex data.

## 3. OpenGlControlBase Integration

### 3.1 Control Structure

Replace the current `TerminalCanvas : Control, ILogicalScrollable` content
area with a subclass of `Avalonia.OpenGL.Controls.OpenGlControlBase`:

```csharp
public sealed class TerminalGLSurface : OpenGlControlBase, ILogicalScrollable
{
    // Scrollable interface delegates to the parent TerminalCanvas
    // OpenGlControlBase provides: GL context, framebuffer, resize, DPI
}
```

The existing `TerminalCanvas` becomes a container that hosts the
`TerminalGLSurface` alongside cursor overlay primitives and handles
scrolling/selection hit-testing.

### 3.2 Lifecycle

| Hook | What happens |
|---|---|
| `OnOpenGlInit(GlInterface gl)` | Compile shaders, create VAO/VBO/EBO, upload atlas texture, query GL version |
| `OnOpenGlRender(GlInterface gl, int fb)` | Consume latest published `QuadFrame`, rebuild dirty-row vertices, issue draw calls |
| `OnOpenGlDeinit(GlInterface gl)` | Delete GL resources (textures, buffers, program) |
| `OnOpenGlLost()` | Release all GL resource references; next init recreates |

Context is only valid inside these hooks. No GL calls elsewhere.

### 3.3 Platform Requirements

OpenGlControlBase requires compositor GPU interop or an
`IPlatformGraphicsOpenGlContextFactory`. Initialization fails when:

- No GPU/driver available (headless server)
- Compositor doesn't support external-object sharing
- EGL/GFX context creation fails

**Fallback:** when `OnOpenGlInit` fails or is not called (context unavailable),
the control renders nothing. The parent `TerminalCanvas` detects this and
falls back to the existing WriteableBitmap path. Detection: if
`OnOpenGlInit` hasn't been called within N ms of attach, set
`_gpuAvailable = false` and route frames to the bitmap pipeline.

### 3.4 Platform Support Matrix

| Platform | Backend | Hardware GL | Expected Result |
|---|---|---|---|
| Linux X11 (GPU) | GLX/EGL | Yes (radeonsi etc.) | Full acceleration |
| Linux X11 (Xvfb) | Software GL | No (llvmpipe) | Works but no speed gain |
| Linux Wayland native | EGL | Yes | Full acceleration + correct cadence |
| Linux Wayland (XWayland) | GLX via XWayland | Yes but cadence issues | Partial improvement |
| Windows | WGL or ANGLE | Yes | Full acceleration |
| macOS | CGL | Yes | Full acceleration |
| Headless/CI | None | N/A | Bitmap fallback |

## 4. GL Pipeline Design

### 4.1 Vertex Format (Instanced)

Follows Alacritty's proven design: one instance per cell, shared quad
geometry, instanced rendering via `glDrawArraysInstanced` or
`glDrawElementsInstanced`.

**Per-instance attributes** (divisor = 1):

```csharp
[StructLayout(LayoutKind.Sequential)]
struct CellInstance
{
    // Grid position (column, row) in u16 — GPU converts to clip space
    public ushort Col;
    public ushort Row;

    // Atlas UV rect (pixel coords in atlas texture)
    public short GlyphX;
    public short GlyphY;
    public short GlyphW;
    public short GlyphH;

    // Foreground color (RGB) + flags byte
    public byte FgR;
    public byte FgG;
    public byte FgB;
    public byte Flags;   // bit 0: bold, bit 1: inverse, bit 2: wide, bit 3: wide-continuation

    // Background color (RGB)
    public byte BgR;
    public byte BgG;
    public byte BgB;
    public byte BgFlags; // reserved
}
// Total: 20 bytes per instance (packed)
```

**Shared quad vertices** (per-vertex, divisor = 0, constant):

```csharp
// Two triangles covering unit cell [0,1]×[0,1]
// Position attribute: corner offset (0 or 1)
// UV attribute: corner selector (0 or 1) — used to pick glyph texel corner
float[] quadVertices = {
    // pos   uv
    0f, 0f,   0f, 0f,   // top-left
    1f, 0f,   1f, 0f,   // top-right
    1f, 1f,   1f, 1f,   // bottom-right
    0f, 0f,   0f, 0f,   // top-left (duplicate)
    1f, 1f,   1f, 1f,   // bottom-right (duplicate)
    0f, 1f,   0f, 1f,   // bottom-left
};
```

**Index buffer** (static): `[0, 1, 2, 0, 2, 3]` per quad, offset by 4×instance_id.

Total per-frame vertex data at 200×50 cells: 10,000 instances × 20 bytes =
200 KB. Rebuilt only for damaged rows.

### 4.2 Shaders

**Vertex shader (GLSL 330 core):**

```glsl
#version 330 core

// Per-vertex (shared quad corners)
layout(location = 0) in vec2 aCorner;
layout(location = 1) in vec2 aCornerUV;

// Per-instance (one per cell)
layout(location = 2) in vec2 aGridPos;      // col, row
layout(location = 3) in vec4 aGlyphRect;    // x, y, w, h in atlas pixels
layout(location = 4) in vec4 aFgColor;      // r, g, b, a (normalized)
layout(location = 5) in vec4 aBgColor;      // r, g, b, a (normalized)

// Uniforms
uniform vec2 uCellSize;       // cell width, height in pixels
uniform vec2 uViewportSize;   // total grid size in pixels
uniform sampler2D uAtlas;     // A8 coverage atlas

out vec4 vFgColor;
out vec4 vBgColor;
out vec2 vGlyphUV;
out vec2 vCellLocal;

void main()
{
    // Cell position in pixels
    vec2 cellOrigin = aGridPos * uCellSize;
    vec2 localPos = aCorner * uCellSize;

    // Screen position
    vec2 screenPos = cellOrigin + localPos;
    vec2 clipPos = (screenPos / uViewportSize) * 2.0 - 1.0;
    gl_Position = vec4(clipPos.x, -clipPos.y, 0.0, 1.0);

    // Atlas UV: map corner selector into the glyph's atlas sub-rect
    vGlyphUV = aGlyphRect.xy + aCornerUV * aGlyphRect.zw;

    // Pass colors to fragment shader
    vFgColor = aFgColor;
    vBgColor = aBgColor;
    vCellLocal = localPos / uCellSize;
}
```

**Fragment shader (GLSL 330 core):**

```glsl
#version 330 core

in vec4 vFgColor;
in vec4 vBgColor;
in vec2 vGlyphUV;
in vec2 vCellLocal;

uniform sampler2D uAtlas;   // A8 coverage texture
uniform int uRenderPass;    // 0 = background, 1 = glyph

out vec4 fragColor;

void main()
{
    if (uRenderPass == 0)
    {
        // Background pass: output cell background color
        fragColor = vBgColor;
    }
    else
    {
        // Glyph pass: sample A8 coverage, multiply by fg color
        float coverage = texture(uAtlas, vGlyphUV).r;
        vec3 rgb = mix(vBgColor.rgb, vFgColor.rgb, coverage);
        float alpha = mix(vBgColor.a, vFgColor.a, coverage);
        fragColor = vec4(rgb, alpha);
    }
}
```

**Two draw calls per frame:**
1. `uRenderPass = 0`: background quads (solid color, no texture sampling needed but same shader works)
2. `uRenderPass = 1`: glyph quads (atlas coverage × fg color)

Alternatively, merge into one pass by computing both in the fragment shader
and blending manually — this avoids a second draw call but requires careful
depth/stencil handling. V1 uses two passes for simplicity.

### 4.3 Texture Management

**Atlas upload (once per generation):**

```csharp
void UploadAtlas(GlInterface gl, GlyphAtlas atlas)
{
    int atlasWidth = atlas.Width;
    int atlasHeight = atlas.Height;

    // Read A8 pixels from SKBitmap
    var pixels = ExtractAlphaBytes(atlas.AtlasBitmap);

    gl.ActiveTexture(GL_TEXTURE0);
    int tex = gl.GenTexture();
    gl.BindTexture(GL_TEXTURE_2D, tex);
    gl.TexImage2D(GL_TEXTURE_2D, 0, GL_R8, atlasWidth, atlasHeight, 0,
                  GL_RED, GL_UNSIGNED_BYTE, pixels);
    gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
    gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
    gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
    gl.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
}
```

**Incremental updates:** when new glyphs are added to the atlas without growth,
use `gl.TexSubImage2D` to update only the affected region. Track the "dirty"
rect in the atlas.

**Bold simulation:** two options:
1. Rasterize bold variants separately in the atlas (key includes Bold flag) — simplest, already supported by Phase 1
2. Apply a dilation filter in the fragment shader — more complex, saves atlas space

V1 uses option 1 (separate entries).

### 4.4 Decorations

Underlines (single/double), strikethrough, and overline are rendered as thin
solid quads appended to the background pass. Their positions are computed
from font metrics (baseline offset + descent fraction).

For curl (wavy) underline: either approximate with multiple small line
segments (still quads) or fall back to Skia path rendering for those rare
cells. V1 approximates with a zigzag of short quads.

Box-drawing characters (U+2500–257F) and block elements (U+2580–259F) are
rendered as solid-fill quads using the foreground color, matching the direct
path's `BuildGeometryRects` logic. These don't touch the atlas.

### 4.5 Wide Cells (CJK)

Wide cells (Width=2) emit one instance with `aGridPos.x = col` and the
fragment shader maps UVs across the full 2-cell width. The base cell carries
the glyph; the continuation cell emits only a background quad (no glyph).
The atlas entry was rasterized at natural advance so the glyph spans both
cells naturally.

Implementation detail: add a `wide` flag to the instance data. In the vertex
shader, multiply the glyph UV extent by 2.0 horizontally when wide, and extend
the quad width to 2 cells.

### 4.6 Cursor

Cursor is drawn as an additional solid quad after the main passes, using a
separate uniform for cursor color/shape. Three shapes:
- Block: full-cell quad
- Beam: narrow vertical quad
- Underline: thin horizontal quad at baseline

Blinking is controlled by the presentation gate (toggle visibility on
animation tick), not by the shader.

## 5. Frame Lifecycle & Threading Model

### 5.1 Publication Flow

```
UI Thread                                Render Thread (compositor)
─────────                                ──────────────────────────
PTY parse → buffer update                
gate coalesces → animation frame         
frame callback:                          
  lock SyncRoot (short hold)             
  capture RenderSnapshot                
  compute damage (dirty rows)           
  publish to slot (interlocked)         
  InvalidateVisual()                    
                                         compositor invokes op.Render()
                                         op.Render reads published slot
                                         builds/reuses vertex arrays
                                         issues GL draw calls
                                         marks done
```

**Snapshot ownership:** ping-pong between two pre-allocated slots. The
producer (UI thread) captures into a free slot and atomically publishes it.
The consumer (render thread) holds the published slot until its Render
completes. A third slot absorbs bursts. If no free slot exists, the producer
skips the frame (back-pressure).

**No locks in op.Render:** the operation exclusively owns its snapshot until
Dispose. The producer never touches a published slot. This eliminates
SyncRoot contention on the render side entirely.

### 5.2 Damage Tracking Without Retained Pixels

Without a retained bitmap, there are no "old pixels" to patch. But we can
avoid rebuilding unchanged rows' vertex data:

**CPU-side vertex cache:** keyed by `(row_generation, atlas_generation,
font_metrics_hash)`. Each row's vertex array is cached; only rows whose
generation changed get rebuilt. Unchanged rows' vertex data is copied (memcpy)
into the frame's instance buffer.

**Full rebuild triggers:** resize, font change, palette change, atlas
regeneration, scroll offset change, selection/cursor state change, alternate
screen toggle.

### 5.3 Presentation Gate Adaptation

The gate remains the sole coalescer. Changes:
- `TerminalCanvas.Render()` publishes a `QuadFrame` instead of calling
  `RenderToBitmap`
- `InvalidateVisual()` schedules the compositor to invoke `op.Render`
- The custom draw operation is re-created each frame (it's lightweight)
- `RequestNextFrameRendering()` is called when continuous animation is needed

## 6. Composer Refactoring

Split `TerminalFrameComposer` into two layers:

**Planner** (pure function, thread-safe):
```csharp
public static QuadFramePlan Plan(IRenderSource source, FrameGeometry geo)
{
    // Classify rows, build background regions, resolve decorations
    // Returns immutable plan: bg quads[], glyph instances[], decoration quads[]
    // No Skia objects, no mutable state
}
```

**Executor** (Skia or GL):
```csharp
// Bitmap executor (existing path)
public static void Execute(SKCanvas canvas, QuadFramePlan plan, ...);

// GL executor (new path)
public static void Execute(QuadGlyphDrawOperation op, QuadFramePlan plan);
```

Both consume the same plan; only the backend differs. The bitmap executor
stays as fallback; the GL executor is used when lease/platform supports it.

## 7. Migration Strategy

### 7.1 Phased Rollout

| Phase | Deliverable | Gate |
|---|---|---|
| P3-a | OpenGlControlBase shell + basic quad rendering (glyphs only, no decorations) | Text visible on screen |
| P3-b | Full pipeline: backgrounds, decorations, box drawing, wide cells | Pixel-diff vs direct path < threshold |
| P3-c | Lease-path integration: snapshot capture + custom op | Live app renders on hardware GL |
| P3-d | Performance validation: flood test, allocation profile, GC analysis | Meets or beats bitmap path |
| P4 | Formal pixel-diff gate on hardware-GL session | 15% complexity gate |

### 7.2 Fallback Chain

```
DOTTY_GPU_RENDER=0 → bitmap pipeline (always)
DOTTY_GPU_RENDER unset → auto-detect:
  OpenGlControlBase init succeeds AND lease feature available → GL path
  Otherwise → bitmap pipeline
DOTTY_GPU_RENDER=1 → force GL path (crash if unsupported)
```

### 7.3 Testing Strategy

- Unit tests: quad builder produces expected vertex data for known inputs
- Pixel tests: render both paths, compare coverage presence per row
- Stress tests: rapid resize, alt-screen toggling, scroll during flood
- Platform tests: X11, Wayland native, Xvfb software, Windows

## 8. Risks

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| OpenGlControlBase init fails (no GPU interop) | Medium | High | Bitmap fallback chain; auto-detect at startup |
| Shader compilation fails on old drivers | Low | Medium | GL 3.3 core is widely supported; fallback to ES 3.0 |
| Thread race between snapshot capture and op.Render | Medium | High | Ping-pong slots with atomic publication; no shared mutable state |
| Grayscale AA visible difference from subpixel | Certain | Low | Document; users expect grayscale on non-integer scales |
| Atlas eviction mid-frame | Low | High | Atlas generation counter; skip frame if generation changed |
| Fractional-DPI quad positioning drift | Medium | Medium | Snap to device pixels like the bitmap path |
| Memory pressure from double-buffered snapshots | Low | Medium | Pool snapshots; cap at 2–3 concurrent |

## 9. Decision Log

| Date | Decision | Evidence |
|---|---|---|
| 2026-08-16 | Use OpenGlControlBase, not CompositionCustomVisual | AvaloniaGL research: documented lifecycle, DPI handling, compositor integration |
| 2026-08-16 | Instanced rendering (Alacritty model), not expanded triangles | Alacritty source: 20 bytes/instance vs 48 bytes/vertex; proven at scale |
| 2026-08-16 | Two-pass rendering (bg then glyphs), not single-pass merged | Simpler shader; background pass is trivially parallel |
| 2026-08-16 | Process isolation for tests (SkiaTests project) | SIGABRT when atlas+quad classes coexist with other SkiaSharp load |
| 2026-08-16 | Native Wayland package exists and is wired | Contradicts earlier finding; Avalonia.Wayland 12.1.1 has UseWayland() |
