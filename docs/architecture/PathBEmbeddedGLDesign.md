# Path B — Embedded OpenGL Rendering Design

Branch: `feat/gpu-rendering` · Status: **DESIGN COMPLETE — ready to implement** · Validated against Alacritty/kitty/Ghostty source · Last updated: 2026-08-16

## 1. Goal

Replace the terminal content area's CPU raster pipeline (WriteableBitmap →
SKSurface.Create(address) → per-cell Skia DrawText) with an embedded OpenGL
surface rendering via instanced quads against a persistent A8 coverage atlas
texture. Eliminates CPU pixel production entirely; one draw call per pass.

## 2. Architecture Overview

```
UI Thread                                    Render Thread
─────────                                    ─────────────
PTY parse → buffer update                    
gate coalesces → animation frame            
CaptureSnapshotBounded() // ~0.3ms          
build QuadFrame (vertex data) // ~0.1ms     
publish to slot → InvalidateVisual()        
                                             OpenGlControlBase.OnOpenGlRender()
                                             → bind program + atlas texture
                                             → upload dirty vertex ranges
                                             → glDrawElementsInstanced ×2 passes
                                             → swap
```

No WriteableBitmap. No CPU pixel production. No full-surface bitmap upload.
The GPU fills quads; the CPU only writes instance data (~20 B/cell).

## 3. OpenGlControlBase Integration

### 3.1 Source

`Avalonia.OpenGL.Controls.OpenGlControlBase` in `Avalonia.OpenGL.dll`
(verified: package ships with Avalonia 12.1).

### 3.2 Lifecycle Hooks

| Hook | Purpose |
|---|---|
| `OnOpenGlInit(GlInterface gl)` | Compile shaders, create VAO/VBO/EBO, upload atlas texture |
| `OnOpenGlRender(GlInterface gl, int fb)` | Bind published frame data, issue draw calls |
| `OnOpenGlDeinit(GlInterface gl)` | Delete GL resources |
| `OnOpenGlLost()` | Release references; next init recreates |

Context is only valid inside these hooks. No GL calls elsewhere.
`RequestNextFrameRendering()` queues a compositor update after state changes.

### 3.3 Platform Matrix

| Platform | Backend | Hardware GL |
|---|---|---|
| Linux X11 GPU | GLX/EGL | Yes (radeonsi etc.) |
| Linux X11 Xvfb | Software GL | No (llvmpipe) |
| Linux Wayland native | EGL (+ dmabuf) | Yes |
| Windows | WGL or ANGLE | Yes |
| macOS | CGL | Yes |

Initialization requires compositor GPU interop or
`IPlatformGraphicsOpenGlContextFactory`. When unavailable, the control renders
nothing; the parent canvas detects this and falls back to the bitmap pipeline.

## 4. GL Pipeline Design

### 4.1 Rendering Model

Follows Alacritty's proven architecture (verified from source):
instanced quads with per-instance cell data, two render passes
(background then glyph), one draw call per pass via
`glDrawElementsInstanced`.

### 4.2 Per-Instance Data Layout

Interleaved instance struct, stride 76 bytes (Pack=4):

| Location | Attribute | GL Type | Size | Offset | Description |
|---|---|---|---|---|---|
| — | corner | vec2 float | 8 | — | shared quad corners, divisor=0 |
| 1 | gridPx | vec2 float | 8 | 0 | cell position in pixels |
| 2 | atlasPx | vec4 float | 16 | 8 | glyph rect in atlas pixels |
| 3 | metrics | vec4 float | 16 | 24 | advance, baselineOffset, leftBearing, topBearing |
| 4 | fgColor | vec3 float | 12 | 40 | foreground RGB |
| 5 | bgColor | vec3 float | 12 | 52 | background RGB |
| 6 | flags | uint int | 4 | 64 | bit 0=bold, 1=wide, 2=wide-cont |
| — | padding | 8 | 68–76 | alignment |

Total stride: 76 bytes. All instance attributes use divisor=1.
Shared corner attribute (location 0) uses divisor=0.

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct CellInstanceData
{
    public float GridX;          // offset 0
    public float GridY;          // offset 4
    public float AtlasX;         // offset 8
    public float AtlasY;         // offset 12
    public float AtlasW;         // offset 16
    public float AtlasH;         // offset 20
    public float MetricsX;       // offset 24 (advance)
    public float MetricsY;       // offset 28 (baselineOffset)
    public float MetricsZ;       // offset 32 (leftBearing)
    public float MetricsW;       // offset 36 (topBearing)
    public float FgR;            // offset 40
    public float FgG;            // offset 44
    public float FgB;            // offset 48
    public float BgR;            // offset 52
    public float BgG;            // offset 56
    public float BgB;            // offset 60
    public uint Flags;           // offset 64
    // padding to 76 bytes for 4-byte alignment
}
```

### 4.3 Shaders

Validated against Alacritty's production GLSL (text.v.glsl / text.f.glsl,
GLSL 330 core, instanced quads with per-instance attributes and divisor=1).
Key differences from Alacritty: we use pixel-space grid positions instead of
column/row u16 (simpler DPI handling), and include per-instance metrics for
bearing-accurate glyph placement.

**Vertex shader (GLSL 330 core):**

```glsl
#version 330 core
layout(location = 0) in vec2 aCorner;
layout(location = 1) in vec2 aGridPx;
layout(location = 2) in vec4 aAtlasPx;
layout(location = 3) in vec4 aMetrics;
layout(location = 4) in vec3 aFg;
layout(location = 5) in vec3 aBg;
layout(location = 6) in uint aFlags;

uniform vec2 uFramebufferPx;
uniform vec2 uCellPx;
uniform vec2 uAtlasPx;
uniform int uPass;

flat out vec3 vFg;
flat out vec3 vBg;
out vec2 vUv;
flat out uint vFlags;

const uint WIDE = 1u;
const uint WIDE_CONT = 2u;

void main()
{
    bool wide = (aFlags & WIDE) != 0u;
    vec2 cellSize = uCellPx * vec2(wide ? 2.0 : 1.0, 1.0);
    vec2 origin = aGridPx;

    if (uPass == 1)
    {
        origin += vec2(aMetrics.z, aMetrics.y - aMetrics.w);
    }

    vec2 pixelPos = origin + aCorner * cellSize;
    vec2 clip = vec2(
        2.0 * pixelPos.x / uFramebufferPx.x - 1.0,
        1.0 - 2.0 * pixelPos.y / uFramebufferPx.y);

    gl_Position = vec4(clip, 0.0, 1.0);
    vFg = aFg;
    vBg = aBg;

    if (uPass == 1)
    {
        vUv = (aAtlasPx.xy + aCorner * aAtlasPx.zw) / uAtlasPx.xy;
    }
    else
    {
        vUv = vec2(0.0);
    }
}
```

**Fragment shader (GLSL 330 core):**

```glsl
#version 330 core
in vec3 vFg;
in vec3 vBg;
in vec2 vUv;
flat in uint vFlags;

uniform sampler2D uAtlas;
uniform int uPass;

out vec4 fragColor;

const uint WIDE_CONT = 2u;

void main()
{
    if (uPass == 0)
    {
        fragColor = vec4(vBg, 1.0);
        return;
    }

    // Discard wide-continuation cells (no glyph of their own)
    if ((vFlags & WIDE_CONT) != 0u) discard;

    float coverage = texture(uAtlas, vUv).r;
    vec3 rgb = mix(vBg.rgb, vFg.rgb, coverage);
    float alpha = max(vBg.a, coverage);
    fragColor = vec4(rgb * alpha, alpha);
}
```

**Draw sequence:**

```c
// Pass 0: backgrounds
gl.Uniform1i(uPass_location, 0);
gl.DrawElementsInstanced(GL_TRIANGLES, 6, GL_UNSIGNED_SHORT, indices, instanceCount);

// Pass 1: glyphs
gl.Uniform1i(uPass_location, 1);
gl.DrawElementsInstanced(GL_TRIANGLES, 6, GL_UNSIGNED_SHORT, indices, instanceCount);
```

### 4.4 Texture Management

A8 atlas uploaded as GL_R8 internal format with GL_RED pixel format.
Sample `.r` channel in the fragment shader for coverage.

```c
// Initial upload (once per generation)
gl.TexImage2D(GL_TEXTURE_2D, 0, GL_R8, w, h, 0, GL_RED, GL_UNSIGNED_BYTE, pixels);

// Incremental update (when glyphs added without growth)
gl.TexSubImage2D(GL_TEXTURE_2D, 0, x, y, w, h, GL_RED, GL_UNSIGNED_BYTE, region_pixels);
```

Filter: GL_LINEAR for smooth scaling at fractional DPI.
Wrap: GL_CLAMP_TO_EDGE.

Generation counter tracks bitmap replacement (growth); renderer recreates
the GL texture when generation changes.

## 5. Frame Lifecycle

### 5.1 Publication Flow

```
UI Thread                                Compositor Thread
─────────                                ─────────────────
PTY parse → buffer update                
gate coalesces → animation frame         
frame callback:                          
  CaptureSnapshotBounded() // ~0.3ms    
  build CellInstance[] // ~0.1ms        
  publish to slot (interlocked exchange)
  InvalidateVisual()                    
                                         compositor invokes op.Render()
                                         op.Render reads published frame
                                         glDrawElementsInstanced ×2
```

**Snapshot ping-pong:** two pre-allocated slots with interlocked exchange.
Producer writes to the free slot; consumer reads the published slot.
If no free slot, producer skips (back-pressure).

### 5.2 Damage Tracking

Without a retained bitmap, every compositor frame re-renders all visible
glyphs. But the CPU cost is minimized by caching vertex arrays per row,
keyed by `(row_generation, atlas_generation, font_hash)`. Only rows whose
generation changed get rebuilt.

**Full rebuild triggers:** resize, font change, palette change, atlas growth,
scroll offset change, selection/cursor change, alt-screen toggle.

## 6. Composer Refactoring

Split TerminalFrameComposer into:

**Planner** (pure function):
```csharp
public static QuadFramePlan PlanFrame(IRenderSource source, FrameGeometry geo)
{
    // Classify rows → build background regions → resolve decorations
    // Returns immutable plan with all quad data pre-computed
}
```

**Executors:**
```csharp
// Bitmap executor (existing fallback path)
public static void ExecuteBitmap(SKCanvas canvas, QuadFramePlan plan);

// GL executor (new path)  
public static void ExecuteGL(GlInterface gl, QuadFramePlan plan);
```

Both consume the same plan; only the backend differs. The bitmap executor
stays as fallback; the GL executor is used when the lease/platform supports it.

## 7. Migration Strategy

Renderer-strategy based, not a second scheduling system:

```
TerminalCanvas.Render():
  if (_useLeaseRender && _gpuAvailable)
      context.Custom(new GpuFrameDrawOperation(snapshot, composer));
  else
      // existing bitmap pipeline (unchanged)
```

Fallback triggers for switching to bitmap mid-session:
- Lease feature unavailable (software backend)
- Atlas init failure
- Device/context lost
- Diagnostic opt-out (DOTTY_GPU_RENDER=0)

## 8. Risks & Mitigations

| Risk | Mitigation |
|---|---|
| OpenGlControlBase init fails | Bitmap fallback chain; auto-detect at startup |
| Shader compile fails on old drivers | GL 3.3 core widely supported; ES 3.0 fallback |
| Thread race between UI capture and op.Render | Ping-pong slots; op owns its snapshot exclusively |
| Grayscale AA vs subpixel visible difference | Documented divergence; users expect grayscale at non-integer scales |
| Atlas eviction mid-frame | Generation counter; skip frame if changed |
| Fractional-DPI positioning drift | Snap quad positions to device pixels |
| Double-buffered snapshots memory pressure | Pool snapshots; cap at 2 concurrent |
