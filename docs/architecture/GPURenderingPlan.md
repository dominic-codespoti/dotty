# GPU Rendering Migration Plan

Branch: `feat/gpu-rendering` · Status: **PLANNING** · Last updated: 2026-08-13

## 1. Goal

Replace the CPU raster path (Skia per-cell text → `WriteableBitmap` → full-surface
upload) with a GPU glyph-atlas + quad-batched renderer so Dotty can sustain
full-screen output at 4K/120 Hz instead of being raster-bound at ~5 ms/frame for a
125×41 viewport (measured 2026-08-13). Interactive 1080p/60 workloads are already in
the right feel-class (Step C dirty rows: 0.16 ms class); the GPU path is a headroom
and large-window play, **not** a day-to-day responsiveness fix.

### 1.1 What is explicitly NOT being built

- **Repeating Experiment A** (`ICustomDrawOperation` + `ISkiaSharpApiLeaseFeature`
  with the existing per-cell Skia pipeline). Measured verdict (§10.9 of
  `AvaloniaOptimizationPlan.md`): content frames −6%, per-frame raster +3.5%, render
  rate −43% (no content caching), p95 unchanged. The draw op re-executes the whole
  CPU pipeline every frame. Rejected and deleted; do not resurrect.
- **Rebuilding the old SKSL shader path** (deleted in the 2026-08-13 tier sweep).
  It uploaded a per-frame cell-data *texture* (lossy, whole-grid upload per frame)
  with baked RGB glyphs and a key mismatch that made atlas lookups always miss.
  Its failure modes are the spec for doing the atlas correctly (§3).
- **Going below Skia.** No bespoke Vulkan/Metal/GL; Skia's `DrawVertices` +
  `SKRuntimeEffect` is sufficient and keeps one renderer for all platforms.

## 2. Decision context (what changed since the last backend decision)

The 15% gate verdict in `AvaloniaOptimizationPlan.md` §10.4–10.9 was measured against
the pre-tier-sweep codebase. Since then (commit `3af71cb`, branch point for this
plan):

- `IRenderSource` abstracts the composer's reads (live `TerminalBuffer` or
  `RenderSnapshot`) — a GPU renderer consumes the same surface with no buffer work.
- Step C per-row dirty tracking already computes the exact row set that changed per
  frame — the quad path rebuilds its vertex buffer for exactly those rows.
- The composer's per-row classification cache (`CellClass[]` rows, generation-keyed,
  zero-copy) is the quad builder's input.
- The atlas infrastructure was deleted, but its defects are documented (§3.2); the
  render snapshot (visible-row scope) and the strict no-motion gate are proven
  patterns to reuse.

**The 15% gate still applies to any shipped backend swap**, but the comparison must
be quad-path vs bitmap-path on the *same* post-sweep codebase, measured at sustained
flood under bare X11 (§6).

## 3. Phase 1 — A8 coverage glyph atlas (3–5 days)

### 3.1 Design

- **Format:** single-channel coverage (alpha), not baked RGBA. Glyph color is applied
  at draw time via vertex color / shader — one atlas entry serves every fg color.
- **Key:** `(grapheme, typeface, size, bold, shapingPolicy)`. No fg color in the key.
  Bold must actually apply to rasterization (the old code keyed on Bold but never set
  it on the font — duplicate identical entries).
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
- **Threading:** atlas build happens on the UI thread (as the old discovery did) or
  eagerly at first use; snapshot path never touches a live atlas mid-draw. Reuse the
  `RenderSnapshot` lifetime pattern.

### 3.2 Failure modes of the deleted atlas (spec)

| Defect (observed 2026-08-13) | Required behavior |
|---|---|
| `Rgba8888` premul with fg RGB baked at rasterize | A8 coverage only |
| Key included `ForegroundHex`; discovery inserted `#RRGGBB`, shader looked up null → guaranteed miss | No color in key; single key contract |
| `Bold` in key but never applied to font | Bold applied or removed from key |
| No bearings; shader centered by bounds width | Bearings + advance + baseline in `GlyphInfo` |
| No wide-cell/continuation/fallback-typeface representation | Width, continuation, typeface identity in key/metadata |
| Unbounded doubling growth, no per-atlas cap; `Dispose`/`AtlasBitmap` unsynchronized | Byte budget + eviction; locked lifetime |

## 4. Phase 2 — Quad renderer (5–8 days, the real work)

- Replace `DrawGlyphs`' per-cell `DrawText` with one CPU-built vertex buffer per
  frame: per visible glyph cell, 2 triangles (4 vertices) carrying position, atlas UV,
  and vertex color (fg). Box-drawing/block-element rects, underlines, strikethrough,
  and overline join the same buffer (axis-aligned quads). Curl underline stays a rare
  per-cell Skia path.
- One `canvas.DrawVertices(..., SKBlendMode.Modulate)` with a small `SKRuntimeEffect`
  shader sampling the A8 atlas (fg = vertex color × coverage).
- Backgrounds remain the existing merged-region Skia rects (already batched by
  `BackgroundSynth`); do not pull them into the quad buffer in v1.
- **The per-frame cost is dirty rows only**: rebuild the buffer for the Step C dirty
  set, keep the previous buffer's draw for unchanged regions (two `DrawVertices` calls
  max, or one realloc'd buffer). SIMD-able CPU fill (`Vector256`), kitty-style.
- The old shader's scroll-clip misrender class must be impossible by construction:
  the quad buffer carries absolute cell coordinates under the same translate matrix as
  today; no per-frame cell texture, no clip-dependent sampling.

## 5. Phase 3 — Canvas integration (2–4 days)

- Reach the GPU via `ISkiaSharpApiLeaseFeature` on a custom draw operation (the
  deleted Experiment A plumbing, rebuilt). Unlike A, the caching problem is inherent:
  the atlas is a persistent GPU texture; the vertex buffer is small and dirty-scoped.
- **GPU detection is required.** Avalonia falls back to software X11 when GLX/EGL is
  unavailable; on a CPU surface the quad path is a slower CPU renderer. Runtime check
  (`GpuDetector` exists only in docs today — build it) + env-gated opt-out
  (`DOTTY_GPU_RENDER=0`), with the bitmap path as the permanent software fallback
  (the §10.4 "rejected default" logic does not apply — the bitmap path stays).
- Fractional-DPI correctness at 1x/2x and non-integer scales: quad positions must snap
  like the current `SnapDip` geometry (device-pixel snapping is a known pain point —
  see `AvaloniaOptimizationPlan.md` DPI phase).

## 6. Phase 0 — Presentation foundation (precondition, 0–2 weeks, upstream risk)

The GPU only pays when frames present. Recorded finding (`AvaloniaOptimizationPlan.md`
§10.8, 2026-08-13): on XWayland/Hyprland, sustained output collapses gate callbacks
to ~1.75/s because composition batches were driven only by blink; bare X11 delivers
~108 renders/s on the same build. A GPU raster does not fix that.

Spikes, in order:
1. **Native Wayland:** Avalonia 12.1 exposes no `UseWayland` API (verified against
   assemblies). Test whether unsetting `DISPLAY` (Wayland-only session) changes
   backend selection; test under an actual Wayland compositor. If negative, file an
   upstream issue and proceed with the 50 ms cadence-fallback floor (shipped) —
   GPU work is then invisible on Hyprland until upstream moves.
2. **Lease availability audit:** which backends actually return a GPU-backed surface
   from `ISkiaSharpApiLeaseFeature`; measure quad-path raster on it under bare X11.
3. **Present timing:** confirm there is still no present callback; the gate cadence
   measurement remains the proxy (§6.1 of `AvaloniaOptimizationPlan.md`).

**Gate:** Phase 1–3 work must not start until at least one presentation path
sustains 60+ gate callbacks/s under flood on the target desktop, OR the cadence
fallback floor is accepted as the v1 target (documented decision).

## 7. Phase 4 — Verification (2–3 days)

- **Pixel-diff:** capture the same deterministic scenario (styled screen, flood,
  scroll, alt-screen, wide chars, overlays, theme, DPI transitions) via the existing
  `CAPTURE_CANVAS`/`RenderTargetBitmap` path (MainWindow) and compare quad-path vs
  bitmap-path pixels. This is the discipline the reverted incremental work required
  (`IncrementalScrollRendering.md` §8); the harness must also cover the quad path's
  clip behavior under manual scroll — the exact bug class that killed the old shader.
- **Gates:** the 15% complexity gate (p95 frame cost or sustained CPU, no quality
  loss) re-measured per §10.4 convention on the post-sweep baseline; the benchmark
  quick suite thresholds (`baselines.json`) must stay green.
- **Memory:** atlas byte budget, eviction under font churn, GPU texture lifetime
  (deferred disposal — §14 risk table), RSS comparison.
- **Regression:** full test suite + flood A/B script (15 s Xvfb, the one used for the
  `3af71cb` measurement) must not regress content frames, lock misses, or alloc/render.

## 8. Implementation slices (independently verifiable)

1. Phase 0 spikes + presentation decision (this branch's first deliverable).
2. A8 atlas + unit tests (key contract, packing, budget, bearings).
3. Quad builder behind an env-gated switch, bitmap path unchanged.
4. Lease integration + GPU detection + software fallback.
5. Pixel-diff suite for the quad path.
6. Gate measurement + cleanup + docs.

Each slice records before/after command, environment, metrics, visual evidence, and
rejected alternatives (repo convention).

## 9. Risks

| Risk | Control |
|---|---|
| Presentation cadence blocks the payoff (upstream) | Phase 0 gate; fallback-floor acceptance |
| Quad path clip/scroll corruption (old shader's bug class) | Pixel-diff harness incl. manual-scroll capture |
| GPU surface absent on backend → slower CPU path | Runtime GPU detection + permanent bitmap fallback |
| Color emoji/script shaping regress | Explicit v1 tofu/fallback scope cut; follow-up RGBA atlas |
| Fractional-DPI crispness drift | Snap-quad geometry to device pixels; 1x/2x/fractional captures |
| Atlas memory/lifetime leaks | Byte budget, LRU, deferred disposal, RSS gate |

## 10. Decision log

| Date | Decision | Evidence |
|---|---|---|
| 2026-08-13 | Plan GPU migration on `feat/gpu-rendering`; bitmap path stays the default until the 15% gate passes | Tier sweep measurement + Experiment A verdict + cadence finding |
| 2026-08-13 | A8 coverage atlas + quad batching; no cell-texture shader; no per-cell DrawText on the GPU path | Old shader failure modes; kitty/Ghostty/Wezterm architecture |
| 2026-08-13 | Phase 0 (presentation) precedes rendering work | §10.8 cadence finding: GPU raster does not fix a stalled present path |
| 2026-08-13 | v1 emoji = tofu/fallback | Color-font path is a separate subsystem; scope cut |

## 11. Open questions

- Does a Wayland-only session (no `DISPLAY`) route around the XWayland cadence
  collapse with the current backend?
- What does the leased surface actually report on each backend (GPU vs CPU)?
- Should the atlas live per-font-config (service) or per-view? (Old service shared
  across tabs — keep, with the byte budget fixed.)
