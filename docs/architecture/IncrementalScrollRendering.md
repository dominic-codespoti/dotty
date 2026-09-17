# Incremental Scroll Rendering

Status: **Design for a future optimization; not enabled in the current Silk.NET/OpenGL render path** · Last updated: 2026-09-17

The current host captures visible terminal rows into an immutable
`RenderSnapshot`, builds GPU cell instances, and submits a complete scene for
each rendered pane. This document preserves the row-motion design and the
verification requirements for a future incremental scene builder; it does not
describe a live incremental renderer.

## TL;DR

Terminal scrolls can be represented as movement of already-built row instances
instead of rebuilding and re-rasterizing every visible row. Ghostty, WezTerm,
and kitty use this principle: cache row content, move unchanged rows, and
rebuild only newly exposed or content-dirty rows.

Dotty's terminal core exposes the ingredients needed for this design: rows have
bump-only identity generations, the screen stores rows in a ring, and an
immutable snapshot gives the renderer a coherent frame. The remaining work is
to add a renderer-side row cache keyed by those generations and to define exact
scroll-region replay semantics.

The design has three parts:

1. **Buffer side:** scroll operations preserve row identity and report the
   exposed band, while writes bump the affected rows.
2. **Scene-builder side:** consume scroll events as row moves, then rebuild only
   rows whose content generation changed.
3. **GPU side:** retain cell instances and translate unchanged rows; redraw
   backgrounds and decorations where their inputs changed.

The correctness requirement is stronger than a faster benchmark: the
incremental scene must be pixel-equivalent to a complete scene for scroll,
write, resize, alternate-screen, selection, and fractional-scale transitions.


---

## 0. Before implementing this: validate the problem, try the cheap wins

This is a blocking prerequisite for any future incremental scene work. Confirm
that the slow session uses an alternate-screen scroll region and measure actual
host frame submissions rather than relying on synthetic PTY chunk sizes.

Cheap wins should be measured first:

- Coalesce redundant `TerminalSession.FlushRender()` requests only if host
  instrumentation shows multiple complete scene builds for one frame.
- Keep shaping and atlas work batched where the current GPU builder already
  supports it; do not add a row cache until the complete-scene cost is measured.

Proceed only if the complete snapshot-to-instance path remains the bottleneck
and line-at-a-time scroll smoothness requires row movement.

---

## 1. Problem

### 1.1 Symptom

Scrolling up/down inside an nvim session currently rebuilds the complete
visible scene. The user's measured window was 73 rows × 136 columns.

### 1.2 Baseline measurements from the former CPU scene path

The following measurements are retained as renderer-owned baseline context for
the design; they are not measurements of the current OpenGL host:

| Path | Cost / frame |
|---|
| Complete scene (73×136) | **11.7 ms** |
| Cell classification | 2.2 ms |
| Glyph drawing | ~4.8 ms |
| Bitmap clear/allocation | 1.9 ms |
| Scrollback text drawing | 1.5 ms |

They identify the work a row cache must remove, but a new comparison must use
the current snapshot, instance-build, texture-upload, and OpenGL-submit stages.

### 1.3 Why nvim pays this cost

### 1.3 Why a terminal scroll is a useful optimization target

nvim's scroll protocol (captured with `TERM=xterm-256color`) uses
`DECSTBM` regions, `SD`, `LF`, and repaint of newly exposed lines. A complete
scene rebuild treats a region scroll like a rewrite, even when most row
content moved unchanged. The proposed design instead carries row identity
through the scroll and invalidates only newly exposed or rewritten rows.

---

## 2. Reference architectures

### 2.1 WezTerm (`term/src/screen.rs`, `terminalstate/mod.rs`)

- Each `Line` carries a `last_change_seqno` (monotonic). The renderer re-rasterizes a line only
  when its seqno is newer than the last-rendered seqno.
- **Scrolls move line objects without invalidating them.** Full-screen `scroll_up` with scrollback
  pushes lines in a `VecDeque`; invalidation is explicitly gated:

  ```rust
  // We only need invalidate if the StableRowIndex of the row would be changed by the scroll.
  if !scrollback_ok { for y in phys_scroll { line_mut(y).update_last_change_seqno(seqno); } }
  ```

  Only the newly exposed blank line receives a fresh seqno. The seqno therefore *travels with the
  content*.
- Viewport (scrollback) scrolling changes only where cached line textures are drawn — no
  re-rasterization.

### 2.2 Ghostty (`src/renderer/generic.zig`, `src/terminal/Screen.zig`)

- Rows are heap objects. `cursorScrollRegionUp` — the DECSTBM+LF hot path nvim uses — physically
  rotates row objects (`fastmem.rotateOnce(Row, rows)`); `cursorDownScroll` grows the page list.
  Moved rows keep their content and their dirty state; the exposed row is marked dirty
  (`cursorMarkDirty`, "Our new row is always dirty").
- The renderer holds a persistent GPU cell buffer keyed by viewport row:

  ```zig
  const rebuild = state.dirty == .full or grid_size_diff;
  ...
  if (!rebuild) {
      if (!dirty.*) continue;   // unchanged row: zero work
      self.cells.clear(y);
  }
  self.rebuildRow(y, ...);
  ```

  Unchanged rows cost nothing per frame — no classification, no rasterization, no upload.

### 2.3 The shared principle

> **Render-cache validity travels with the row.** A scroll is a move of existing row objects, not a
> content rewrite. Only the newly exposed rows are invalidated; the renderer repositions cached
> content (memmove of pixels or quad/texture positions) and re-rasterizes only invalidated rows.

---

## 3. Current implementation facts (Dotty)

Terminal core (`src/Dotty.Terminal/Adapter/Buffer/`):

- `TerminalBuffer._rowGenerations` is a bump-only identity array indexed by
  logical row. Writes, erases, scrolls, screen changes, and resize invalidate
  affected rows; full invalidations bump every row.
- `Screen` is a ring buffer. `ScrollUpRegion` and `ScrollDownRegion` move
  physical rows while preserving row metadata such as maximum used column and
  cold-cell flags.
- `RenderSnapshot.CaptureVisible` copies the visible row slices, row map,
  generations, styles, and cursor state while the caller holds `SyncRoot`.
  The returned object is immutable to the renderer and is disposed after use.

GPU scene path (`src/Dotty/Rendering/` and `src/Dotty.Rendering.Gpu/`):

- `TerminalSceneComposer` captures each pane's visible snapshot, then asks
  `QuadFrameBuilder` to emit cell instances for the complete visible pane.
- `QuadFrameBuilder` skips continuation cells, preserves wide-cell width, and
  resolves style, inverse colors, atlas placement, and decorations.
- `SilkTerminalRenderer` retains the last submitted instance data and uploads
  it to OpenGL when a captured frame changes. There is currently no row-level
  scroll replay or persistent per-row scene cache.

This section is the boundary between shipped behavior and the proposed design
below: only the terminal-core generation and ring-buffer facts are prerequisites
already present in the current host.
---


## 4. Proposed row-motion design

> **Risk budget.** A row cache must stay synchronized with the terminal's
> generation array across scrolls, writes, resizes, pane changes, and
> alternate-screen swaps. Every optimization must be checked against a complete
> scene with deterministic pixel comparisons.

### 4.1 Overview

```
buffer scroll op (SU/SD/LF/IL/DL)
        │
        ▼
TerminalBuffer:
   identity generations: bump rows whose content changes
   scroll event:         record [top..bottom] and delta
        │
        ▼
scene builder:
  1. move cached row instances for a fully visible region
  2. mark exposed rows dirty
  3. rebuild rows whose generation differs from the cache
  4. rebuild full scene for resize, pane, selection, or screen changes
        │
        ▼
OpenGL renderer:
  retain unchanged instances and translate moved rows
  upload only changed row data where the buffer layout permits it
```

The complete-scene path remains the fallback for ambiguous ordering or geometry
changes.

### 4.2 Buffer contract

The proposed buffer-side event is a value containing `(top, bottom, delta)`.
Scroll operations move row identity with the content and invalidate only the
newly exposed band. Writes and erases bump the identity generation of the rows
they change. A full-screen replacement, resize, reset, or alternate-screen
toggle invalidates all rows and clears pending movement.

The current `TerminalBuffer` already supplies bump-only row generations and
ring-buffer row movement. It does not currently expose a pending-scroll queue;
adding that queue is part of this future design, not a description of shipped
state.

### 4.3 Scene-builder replay

For each pending event:

1. If the region is fully visible, translate cached row instances by
   `delta * cellHeight` and mark the exposed band dirty.
2. If content entered from outside the visible pane, skip translation and mark
   the visible portion dirty.
3. Rebuild rows whose generation differs from the cache.
4. Rebuild the complete pane when geometry, selection, scrollback extent,
   font metrics, or alternate-screen state changes.

Multiple events replay in FIFO order. A cost cap should fall back to a complete
scene before a burst can perform unbounded row copies.

### 4.4 Classification, backgrounds, and glyphs

The scene builder must cache classification by the bump-only identity
generation, then synthesize background regions over the complete visible range
so rounded or vertically merged regions do not split at dirty-row boundaries.
Only dirty rows should emit new glyph and decoration instances. Background base
color must be restored under dirty rows before drawing their regions.

The current GPU path already keeps atlas coverage separate from per-cell color
and emits explicit decoration instances. Row reuse must preserve those
contracts, wide-cell continuation behavior, selection overlays, and inverse
video resolution.

### 4.5 Alternate screen, resize, and scrollback

Main-screen scrollback changes can alter pane translation and therefore require
a complete scene unless translation and row movement are handled together.
Alternate-screen toggles must invalidate every row before reuse; the shared
generation array cannot be treated as screen-local. Resize and font changes
rebuild geometry and all row-cache entries.

### 4.6 Out of scope

- A new presentation backend or framework-owned bitmap path.
- Color-font atlas work beyond the current fallback behavior.
- Skipping complete scenes for selection or other overlays until their
  invalidation boundaries are measured.

---


## 5. Edge cases
| Case | Handling |
|---|---|
| Scroll plus write between frames | Replay movement, then rebuild rows whose generations changed |
| Two region scrolls between frames | Replay in order; cap total movement work |
| Scroll burst exceeds cap | Discard movement events and build a complete scene |
| Alternate-screen toggle | Clear row-cache state and rebuild every row |
| Resize or font metrics change | Rebuild geometry and every cached row |
| Selection or context-menu overlay | Complete scene until overlay invalidation is explicit |
| Scrollback extent changes | Complete scene unless row movement and translation are handled together |
| Fractional framebuffer scale | Compare against the complete OpenGL scene with shader snapping enabled |

---

## 6. Verification requirements and current coverage

The current tree does not contain an incremental renderer or its former
pixel-diff harness. `tests/Dotty.Terminal.Tests/ScrollEpochTests.cs` now covers
the retained identity-generation contract:

- full-screen scroll generation bumps and scrollback growth;
- writes after a scroll and whole-region replacement;
- render-boundary reset of same-row write coalescing;
- alternate-screen invalidation;
- visible snapshot capture with a scroll offset.

The file's `ScrollGenerationTests` class documents that motion epochs and the
old replay helper were removed because they had no live readers. A future row
cache must add deterministic complete-scene comparisons for region scroll,
scroll-plus-write, burst capping, resize, selection, alternate-screen toggles,
and fractional framebuffer scales before it becomes the live path.

The retained former CPU baseline in Section 1.2 is useful for sizing the
optimization, but it is not a pass/fail threshold for the OpenGL host. New
measurements must report snapshot capture, instance construction, atlas upload,
and OpenGL submission separately.

---

## 7. Phasing

1. Instrument the current complete snapshot-to-instance path and confirm that
   row movement is the dominant cost for the target workload.
2. Add a buffer-side scroll event only if the existing generation contract can
   remain bump-only and screen-safe.
3. Add a pane-local row-instance cache with a complete-scene fallback.
4. Verify every scroll, write, overlay, resize, and alternate-screen transition
   against a complete OpenGL scene.

No phase is considered shipped until the current host's interactive behavior
and deterministic scene output remain unchanged.

---

## 8. Open questions and follow-ups

1. Should movement events be emitted by `TerminalBuffer` or by a higher-level
   session adapter that already coalesces render requests?
2. Can the current instance layout support per-row uploads without copying the
   complete pane into a new contiguous buffer?
3. How should scrollback translation and pane layout changes compose without
   invalidating every row?
4. What is the smallest deterministic scene corpus that covers wide cells,
   merged backgrounds, overlays, and fractional framebuffer scales?
