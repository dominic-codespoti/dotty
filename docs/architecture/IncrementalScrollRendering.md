# Incremental Scroll Rendering

Status: **Reverted from the render hot path after live testing found correctness bugs beyond
the four field fixes below (Section 9); the tested primitives remain in the codebase for a
future, more rigorously verified re-attempt.**

## TL;DR

Scrolling in an nvim session costs ~8–12 ms of render time per wheel notch because every nvim
repaint triggers a **full-screen composer render** (~7 ms of cell classification + glyph drawing
for all 73 rows at 136 columns). Ghostty and WezTerm avoid this by treating a terminal scroll as a
*move of existing rows* rather than a content rewrite: their render caches are invalidated only for
the newly exposed rows, and everything else is repositioned (memmove / texture-quad shift) with
zero re-rasterization.

Dotty already has the right primitive — the `pureScroll` pixel memmove in `TerminalCanvas` — but
it is only used for viewport (scrollback) offset changes. Buffer scrolls (`DECSTBM` + `SU`/`SD`/`LF`)
mark the **entire region dirty**, so the renderer can never tell a scroll from a rewrite.

**Read Section 0 before anything else.** It covers two things the first draft of this document
skipped: confirming the problem described here is the one actually present, and two low-risk
changes that may already clear the target with none of the new correctness surface Sections 1–5
introduce. The architectural fix below is only worth its risk if those don't land the win.

If it is still needed, the fix has three parts:

1. **Buffer side**: scroll operations shift the per-row *generation* array with the content and
   bump generations only for the exposed rows, recording the scroll as a `(top, bottom, delta)`
   event for the renderer (the WezTerm `seqno`-travels-with-line model).
2. **Renderer side**: consume recorded scrolls as region memmoves on the cached bitmap, then
   re-render exactly the rows whose generation differs from the renderer's mirror — the
   generalization of the existing `pureScroll` band logic.
3. **Renderer side (follow-up)**: cache per-row cell classification so non-scroll updates
   (statusline, cursor row, LSP spinner) re-render only dirty rows while background-region
   synthesis stays exact.

Measured impact (73×136, Release, Section 6): nvim's line-at-a-time scroll (`LF`, 1 row) drops
from ~11.7 ms to **0.011 ms**; nvim's page scroll (`SD(26)`, the realistic mouse-wheel/`Ctrl-D`
case) drops to **1.87 ms** (~6×); a 1-row statusline/cursor/spinner update drops to **0.16 ms**.
All three beat the Section 6 acceptance targets (<0.5 / <3.0 / <0.5 ms).

---

## 0. Before implementing this: validate the problem, try the cheap wins

This section did not exist in the document's first draft; it is now a blocking prerequisite —
do not start Section 4 without working through it.

### 0.1 Confirm the actual regime

Everything in Sections 1–5 assumes nvim is running in the **alt screen**, scrolling via `SD`/`LF`
under a `DECSTBM` region with mouse reporting enabled. That assumption is **unverified** for the
real environment this was written for. An earlier harness run in this same investigation showed
nvim landing in the **main screen** (no alt screen) when `$TERM` wasn't propagated to the child
shell — in that regime, wheel scrolling routes through the `pureScroll` viewport path, which is
already fast (0.15–0.17 ms, measured). If that's what's actually happening for the reported
session, Sections 1–5 below fix a problem that doesn't exist here.

Also unverified: the "1–3 render flushes per wheel notch" figure in Section 1.3 came from an
**arbitrary 400-byte chunk size** chosen for the synthetic benchmark harness, not from observed
PTY read sizes or a measured Avalonia invalidation-coalescing rate. If the real app already
coalesces bursts into one render per compositor frame, the actual cost is the single full render
in Section 1.2, and the "multiple renders per notch" framing in the TL;DR is wrong.

**Action before writing code:** confirm which screen mode is live (`buffer.IsAlternateScreenActive`
at the point of slowness) and instrument the real app — not the synthetic benchmark — to count
actual `RenderToBitmap` invocations per wheel notch.

### 0.2 Try the cheap wins first

Two changes get most of the plausible win with none of the new correctness surface Section 4
introduces (no buffer-generation semantics change, no renderer-side mirror, no scroll-region
replay):

- **Coalesce render flushes.** If 0.1 shows multiple `RenderToBitmap` calls per compositor frame,
  debounce `TerminalSession`'s per-chunk `FlushRender()` call to the frame cadence — it already
  has a periodic 16 ms timer path; the immediate per-chunk flush may be redundant with it and is
  the more likely source of "multiple renders per notch" than anything scroll-specific.
- **Batch glyph drawing.** The composer's shaped-run path already batches contiguous runs of 2+
  glyphs (Section 3); the ~4.8 ms/frame single-cell `DrawText` fallback (Section 1.2) is a
  candidate for the same treatment. Pure constant-factor win, zero new state.

Measure both against the bench harness in Section 6. If the full render drops from ~11.7 ms to a
few ms, re-evaluate whether Sections 1–5 are still worth the risk (see the callout in Section 4).
Proceed past this point only if the cheap wins don't clear the target, or if line-at-a-time-scroll
smoothness — which the cheap wins don't touch — is separately required.

---

## 1. Problem

### 1.1 Symptom

Scrolling up/down inside an nvim session renders correctly after the pureScroll band fix, but the
frame rate is poor. The user's live window is 73 rows × 136 columns.

### 1.2 Measured costs (benchmark harness, Release, 73×136, SkiaSharp CPU raster)

| Path | Cost / frame |
|------|--------------|
| Full render (what nvim repaints trigger) | **11.7 ms** |
| — composer.RenderTo (0..72) | 7.0 ms |
| — canvas.Clear + bitmap alloc | 1.9 ms |
| — scrollback `DrawText` (30 lines, main screen only) | 1.5 ms |
| pureScroll wheel-up (post-fix band) | 0.17 ms |
| pureScroll wheel-down (post-fix band) | 0.15 ms |

Composer internal split: **2.2 ms** cell classification (`ClassifyRowCells`, includes grapheme
resolution + per-cell style/typeface work) + **~4.8 ms** Skia glyph drawing (per-glyph
`DrawText` calls). Both are proportional to the *full visible screen*, regardless of how many rows
actually changed.

### 1.3 Why nvim pays this cost

nvim's scroll protocol (captured live from a real session, `TERM=xterm-256color`):

- Page down / wheel (mouse mode): `DECSTBM(1,rows-1)` + `CSI <n> M` (`SD`) + CUP repaint of the new
  lines; the repaint arrives in 1–3 PTY read chunks → 1–3 render flushes.
- Line-at-a-time scroll: `DECSTBM(1,rows-1)` + repeated `LF` at the region bottom.
- Page up: pure CUP + erase repaint of every row.

Every flush runs `RenderToBitmap`, which re-classifies and re-draws **all 73 rows** because:

- The buffer bumps the whole scroll region's generations on any scroll
  (`MarkRowRangeDirty(_scrollTop, _scrollBottom - _scrollTop + 1)` in `LineFeed`, `ScrollUpLines`,
  `ScrollDownLines`, `InsertLines`, `DeleteLines`), so generation-based culling is impossible.
- The canvas has no per-row render cache and no partial-frame path except the `pureScroll` memmove,
  which only fires on *viewport offset* changes with an unchanged scrollback count.

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

Buffer (`src/Dotty.Terminal/Adapter/Buffer/`):

- `TerminalBuffer._rowGenerations: ulong[]` (identity) indexed by **logical row**;
  `MarkRowDirty(row)`/`MarkRowRangeDirty(start, count)` bump rows. `_globalGeneration` is a
  monotonic counter. Phase A added `_rowScrollEpochs: ulong[]` (motion) plus the pending-scroll
  queue.
- Scroll primitives bump the whole region's identity generations (cache invalidation) *and*
  rotate the motion epochs with content, bumping only the exposed band (Phase A): `LineFeed`
  (bottom-row LF), `ScrollUpLines` (`SU`), `ScrollDownLines` (`SD`), `InsertLines` (`IL`),
  `DeleteLines` (`DL`).
- `Screen` is a ring buffer (`_head`); `ScrollUpRegion`/`ScrollDownRegion` memcpy rows within the
  ring, rotating `_rowMaxCol`/`_rowColdFlags` with the rows. **`_rowGenerations` does not rotate**
  with the ring — it lives in `TerminalBuffer` in logical space.
- `_totalScrolled` (scrollback count) is only incremented when the region top is row 0; nvim's
  `DECSTBM(1,rows-1)` region scrolls (top=1) do not touch scrollback. In the alt screen the canvas
  keeps `ScrollbackCount == 0`, so the `pureScroll` path never fires — every nvim repaint is a
  full render.

Canvas (`src/Dotty.App/Controls/Canvas/TerminalCanvas.cs`):

- `RenderToBitmap` renders under `buffer.SyncRoot` into a reused `WriteableBitmap` (5 MB at
  73×136). Full path: `canvas.Clear(bgColor)` + composer over the visible range + scrollback
  `DrawText` + overlays (selection, cursor, debug).
- Incremental path (Phase B): replays queued scrolls as region memmoves against the cached
  bitmap, mirrors `buffer.RowScrollEpochs` as `_renderEpochs`, and re-renders exactly the rows
  where buffer epoch != mirror epoch via `RenderDirty`. Viewport `pureScroll` (whole-frame
  memmove + exposed band) now routes its band through the same machinery — cheap (0.15–0.17 ms)
  and pixel-exact (validated against a full render).
- The canvas mirrors `buffer.RowGenerations` as `_lastRowGenerations` to drive glyph-atlas
  discovery; `_renderEpochs` is a separate mirror for rendering decisions.

Composer (`src/Dotty.App/Controls/Canvas/Rendering/TerminalFrameComposer.cs`):

- `RenderTo(canvas, buffer, paint, font, cellW, cellH, startRow, endRow)`: classifies each row
  (`ClassifyRowCells`), synthesizes background regions over the range (`BackgroundSynth` + vertical
  merging into rounded "pills"), then draws glyphs with per-row clipping. Stateless per call —
  deterministic and translate-invariant (verified).

---

## 4. Design

> **Risk budget.** The `pureScroll` memmove fix that motivated this document took a long,
> instrumented, pixel-diff-driven session to get right — and that was the simpler case: one
> whole-frame offset, no new shared state. Section 4.3 below generalizes it to arbitrary regions
> and adds new shared mutable state (the renderer's generation mirror) that must stay
> byte-for-byte synchronized with the buffer's array across scrolls, writes, resizes, and screen
> swaps. Budget verification time comparable to or exceeding that fix — this is not a quick
> follow-up.

### 4.1 Overview

```
buffer scroll op (SU/SD/LF/IL/DL)
        │
        ▼
TerminalBuffer:
   identity generations (_rowGenerations): bump whole region (cache invalidation, glyph discovery)
   motion epochs (_rowScrollEpochs):       rotate [top..bottom] by delta (content travels with its epoch),
                                          bump exposed rows only
   enqueue PendingScroll { top, bottom, delta }
        │
        ▼
canvas render (under SyncRoot):
  1. replay pending scrolls in order:
       - if region fully visible: memmove region pixels by delta * cellHeight, rotate mirror epochs,
         sentinel exposed rows
       - else (content entered from off-bitmap): sentinel the visible rows of the region, no memmove
  2. viewport pureScroll (offset changed, no pending scrolls): whole-frame memmove + sentinel band
  3. dirty rows = visible rows where bufferEpoch[r] != mirrorEpoch[r]
  4. RenderDirty: full visible-range synthesis (cached classification) + bg-color refill under dirty
     rows + regions touching dirty rows + glyphs for dirty rows; mirror = buffer epochs
  5. anything else (bitmap new, geometry change, sbCount change, selection, offset+queue mixed)
     → full render; mirror = buffer epochs, queue drained
```

The `pureScroll` viewport path stays as-is and is orthogonal (it handles offset changes; scroll
events handle buffer changes).

### 4.2 Step 1 — Buffer: two generation streams

The first draft rotated `_rowGenerations` itself. That is unsound for the composer's per-row
classification cache (Phase C): the cache keys on identity generations, and a rotated generation
can coincide with the pre-rotation value of another row, producing a false cache hit after a
scroll. The shipped design uses **two arrays**:

- `_rowGenerations` (identity, bump-only): bumped on every content change *including* whole-region
  scrolls. Drives glyph-atlas discovery and the classification cache. Never rotated, so a matching
  value always means "this row's content is byte-identical to what was classified".
- `_rowScrollEpochs` (motion, rotated): travels with content across scrolls; bumped only for rows
  whose content changed in place (writes, erases) or was newly exposed. The renderer mirrors this
  array to decide which rows to skip (pixels were memmoved) vs re-render.

New state in `TerminalBuffer`:

```csharp
public readonly record struct PendingScroll(int Top, int Bottom, int Delta);
private readonly Queue<PendingScroll> _pendingScrolls = new();
public int PendingScrollCount => _pendingScrolls.Count;
public bool TryDequeuePendingScroll(out PendingScroll scroll);
```

The scroll ops share two helpers (SU/CSI-S/LF-at-bottom → `ScrollRegionUp`, SD/CSI-T/RI-at-top →
`ScrollRegionDown`; IL/DL use the same pattern within `[cursorRow..bottom]`):

```csharp
private void ScrollRegionUp(int top, int bottom, int n)
{
    if (n <= 0) return;
    ActiveBuffer.ScrollUpRegion(top, bottom, n);
    int height = bottom - top + 1;
    int delta = Math.Min(n, height);
    if (top == 0)
        unchecked { _totalScrolled += delta; }

    // Motion epochs travel with content: row r now holds what was at r+delta.
    ScrollEpochMath.RotateRange(_rowScrollEpochs, top, bottom, -delta);
    for (int r = bottom - delta + 1; r <= bottom; r++)   // exposed band
        unchecked { _rowScrollEpochs[r]++; }

    BumpIdentity(top, height);                            // classification/glyph caches
    if (delta < height)                                   // whole-region replacement: no memmove
        _pendingScrolls.Enqueue(new PendingScroll(top, bottom, -delta));
}
```

Key points:

- **Epochs travel with content** (WezTerm seqno / Ghostty rotateOnce). A row that scrolled from r
  to r−delta keeps its epoch, so an unchanged moved row is *not* re-rendered.
- Exposed rows (blanked edge) get bumped → rendered.
- `ScrollRegionDown` mirrors this (content down, exposed top band — restored scrollback or
  blanked rows count as new content either way); the existing scrollback-restoration logic
  (`_totalScrolled` decrement + `ClearRow`) stays.
- A scroll that replaces the whole region (`n >= height`, the screen-clear branch) bumps every
  epoch and queues nothing — there is no content to memmove.
- `_pendingScrolls` is cleared on any full-grid invalidation (`MarkAllRowsDirty` — alt toggle,
  reset, clear — and `Resize`), because queued scrolls reference the old grid identity.
- `_totalScrolled` changes (full-screen scrolls) still force a full render via the `sbCount`
  rule (4.6), so the queue entries for those are drained and discarded harmlessly.
- **Writer coalescing must not span renders:** `BufferTextWriter` coalesces consecutive same-row
  writes into one dirty call. That was harmless when every frame was a full render (which reads
  current content), but the epoch mirror depends on a bump per render cycle — consecutive
  keystrokes in one row would never mark it dirty and the display would go stale. `MarkRender()`
  (called by the canvas at the start of every render) resets the coalescing so each post-render
  write bumps.
- The rotation helper lives in `ScrollEpochMath` (shared by buffer and renderer so both sides
  apply the identical transformation).

### 4.2a Precondition (blocking): dual-screen generation isolation — RESOLVED

`_rowGenerations`/`_rowScrollEpochs` are single arrays on `TerminalBuffer` shared by both the main
and alt `Screen` objects — `ScreenManager` swaps the active `Screen` reference but not the epoch
array. Under this design, a toggle must not leave the mirror believing rows are unchanged.

**Verified safe:** `SetAlternateScreen` already ends with `MarkAllRowsDirty()`, which bumps every
row's epoch (and now also clears the pending-scroll queue — added in Phase A). The renderer
therefore sees every row differ from its mirror after a toggle, and the stale queued scrolls are
dropped. No further change was needed beyond the queue clear.

### 4.3 Step 2 — Renderer: replay scrolls as region memmove

`TerminalCanvas` mirrors the buffer's motion epochs:

```csharp
private ulong[]? _renderEpochs;   // mirror of buffer.RowScrollEpochs as last rendered
```

`RenderToBitmap` decision order (all under `buffer.SyncRoot`):

1. **Full render** if: mirror invalid/geometry changed, `sbCount` changed (main-screen content
   flow — see 4.6), selection active (conservative), or offset changed **and** pending scrolls
   exist (ordering would be ambiguous). Mirror := buffer epochs, queue drained.
2. **Scroll replay**: while `TryDequeuePendingScroll(out s)`:
   - region fully visible (`[s.Top..s.Bottom] ⊆ [visStart..visEnd]`): memmove the region's pixel
     band by `s.Delta * cellHeight` (`MemmoveRegionRows`), rotate the mirror identically
     (`ScrollEpochMath.RotateRange`), sentinel the exposed rows (`ulong.MaxValue`).
   - otherwise: rows inside the region received content from outside the bitmap (no pixels to
     move) — sentinel the visible rows of the region, no memmove. This is the main-screen
     scrolled-up-viewport fallback; in the alt screen (offset 0, full-grid viewport) every region
     is fully visible.
3. **Viewport pureScroll** (offset changed, no pending scrolls): whole-frame memmove
   (`MemmoveWholeFrame`), sentinel the exposed band (same `ComputeExposedRows` math), render the
   scrollback band directly (base-color fill + `DrawText`), and let step 4 re-render the grid band.
4. **Dirty-row render** (`RenderDirty`, Phase C): rows `r` in the visible range where
   `bufferEpoch[r] != mirrorEpoch[r]`. Full visible-range background synthesis (cached
   classification), base `bgColor` re-applied under every dirty row (the incremental path does
   not `canvas.Clear`), background regions touching dirty rows redrawn (opaque → identity
   elsewhere), glyphs drawn for dirty rows only. Then mirror := buffer epochs.
5. If the dirty set is empty (pure scroll with no writes) the composer is skipped entirely.

Correctness argument:

- The mirror starts equal to the buffer epochs (last full render).
- Each replayed scroll applies the identical transformation to pixels and mirror, so moved rows
  keep `bufferEpoch == mirrorEpoch` (their cached pixels are valid, now repositioned by the
  memmove) and exposed rows differ (repainted over the memmoved band).
- Any interleaved write bumps the buffer epoch; the rotation carries that bump with the content,
  so `bufferEpoch != mirrorEpoch` exactly for rows whose visible content changed — whether the
  change came from a scroll, a write, or both, regardless of chunk timing (nvim's `SD` and
  repaint arriving in one PTY read or three).
- The base-color refill under dirty rows is required: written rows carry stale pixel remnants and
  the memmove clears exposed bands to zeros; without it, no-background cells would show garbage on
  any non-black theme. (This also fixes the pre-existing pureScroll band, which cleared to zeros
  and only looked right on black backgrounds.)
- Full-range synthesis with *current* identity generations is sound because identity generations
  are bump-only: a classification-cache hit guarantees the row's content is unchanged.

### 4.4 Scroll queue bounds

Multiple scrolls between renders are common (e.g. an app scrolling a region while the render
timer is paused). Replay costs O(region height) per scroll — a few KB of memmove plus a few dozen
`ulong` copies — essentially independent of *how many* scrolls are queued.

**Shipped as a hard cap, not a guideline.** The first draft reasoned "no cap is needed because
each replay is cheap" — wrong for bursts: `yes` queues one scroll per `LF` (tens of thousands per
64 KB chunk), and replaying them all is gigabytes of memmove (each full-screen replay copies the
whole region), freezing the UI and leaving mid-replay frames shifted. The canvas therefore caps
the **total region height** replayed per frame (8×rows); beyond it the queue is drained and the
frame falls back to a full render — the pre-incremental behavior, bounded at one full-render
cost. A flat scroll-count cap would be the wrong resource (a page-down burst of 3×72 rows should
still replay); the height sum is the memmove volume.

### 4.5 Step 3 — Per-row culling with exact background synthesis (shipped)

For the common non-scroll updates (statusline repaint, cursor-row update, LSP spinner), the dirty
set is one or a few rows. Rendering `[minDirty..maxDirty]` through the range API would re-run
classification for the whole band and could split vertical background "pills" at the band edges.

Shipped: per-row classification cache in the composer, keyed by the buffer's identity generation:

- `CellClass[][] _rowClassCache` + `ulong[] _rowClassGen`, per-row snapshots of the classification
  (`ClassifyRowCellsCached`: copy the cached row into the working array on a hit, else classify
  and snapshot). `ResetCaches()` (called on alt-screen toggle, buffer swap, and font/metrics
  rebuild) clears the tags.
- `RenderDirty(canvas, buffer, paint, font, cellW, cellH, bgColor, visStartRow, visEndRow,
  dirtyRows)`:
  1. classify every visible row, reusing the cache entry when the identity generation matches;
  2. synthesize background regions over the full visible range from the cached classes — full
     range matches the full render's own visible-range synthesis, so pills are never split and
     the viewport-edge behavior is identical;
  3. re-apply `bgColor` under dirty rows (non-AA hard rects);
  4. draw background regions that intersect dirty rows (opaque → identity elsewhere);
  5. draw glyphs for dirty rows only.

The full path (`RenderTo`) also runs through the cache, so repeated full renders of mostly-static
screens stop reclassifying unchanged rows.

Cost model (73×136): a 1-row statusline update drops from ~7 ms to **0.16 ms** (measured).

### 4.6 Out of scope for this design

- **Main-screen output flow** (`sbCount` growth at the bottom): currently a full render per
  change. With Step 1 the underlying scrolls are recorded, but the canvas's translate depends on
  `sbCount`, so replay would need a combined "scroll + translate change" path. Kept as a follow-up;
  the scrollback `pureScroll` path already covers user scrolling, and nvim (alt screen) is
  unaffected.
- **Glyph draw batching** (whole-line text blobs instead of per-cell `DrawText`): the composer
  already batches contiguous 2+ glyph runs; Phase C's classification cache removes the remaining
  per-cell classification cost on unchanged rows. Further constant-factor wins are separate work.
- **GPU rendering** (the shader glyph path is currently disabled as lossy): orthogonal.

---

## 5. Edge cases

| Case | Handling |
|------|----------|
| Scroll + write between renders | Generations + mirror rotation make the dirty set exact (Section 4.3). |
| Two region scrolls between renders | Queue replays both in order; each memmove composes on the pixels. |
| Scroll queue overflow | Full render fallback (bounded cost). |
| Alt-screen toggle mid-scroll | Blocking precondition — see Section 4.2a. Must force full render + queue/mirror clear on every toggle regardless of generation-array behavior, verified before Phase B ships. |
| Resize | `_rowGenerations` resized + reflowed; canvas detects geometry change → full render + mirror rebuild. |
| Selection active | Full render (unchanged conservative behavior). |
| `_totalScrolled` change on full-screen scroll | `sbCount` change → full render (documented limitation, 4.6). |
| `_renderGenerations` length mismatch vs buffer | Treat as full render (defensive check). |
| Alt screen `_totalScrolled` growth (latent bug: full-screen scrolls in alt increment `_totalScrolled`) | Out of scope; note for a follow-up fix — it can wrongly enable the viewport `pureScroll` path in the alt screen. |

---

## 6. Verification — results

All shipped; every acceptance criterion met.

1. **Pixel-diff harness** (`tests/Dotty.App.Tests/IncrementalRenderTests.cs`, 12 tests): full
   render vs the incremental path (real `MemmoveRegionRows`/`MemmoveWholeFrame`/
   `ApplyScrollToMirror`/`ComputeDirtyRows` + `RenderDirty`), byte-identical comparisons over a
   non-black base background. Scenarios: alt-screen `DL` page-down (nvim's real protocol), `SU`,
   `SD`, `LF` at region bottom, `IL`/`DL`, scroll+write interleaved, multi-scroll burst,
   main-screen `SU`/`SD` with pill synthesis, write-only (typing path), main-screen scrolled-up
   viewport region-scroll fallback, and viewport `pureScroll` both directions (grid band and
   scrollback band). **All pixel-identical.**
2. **Buffer unit tests** (`tests/Dotty.Terminal.Tests/ScrollEpochTests.cs`, 12 tests): epoch
   rotation direction and exposed-band bumps for `SU`/`SD`/`LF`/`IL`/`DL`, FIFO queue order,
   scrollback-touching full-screen scrolls, whole-region replacement (no queue entry), write
   after scroll, alt-toggle queue clear, resize array sync, and the shared rotation helper.
3. **Bench** (73×136, Release, the user's live window), incremental step per frame:

   | Path | Before | After | Target |
   |------|--------|-------|--------|
   | nvim LF single-line scroll | ~11.7 ms (full render) | **0.011 ms** | < 0.5 ms ✓ |
   | nvim `SD(26)` page scroll | ~11.7 ms (full render) | **1.87 ms** | < 3.0 ms ✓ |
   | 1-row statusline write | ~7 ms (classification) | **0.16 ms** | < 0.5 ms ✓ |

   The `SD(26)` replay at 73×136 is **pixel-identical** to the full render.
4. **Existing suites**: `Dotty.App.Tests` 630 passed / 0 failed (was 620); `Dotty.Terminal.Tests`
   185 passed / 0 failed.
5. **Live smoke**: full app launches and renders (TCP test interface responds; `STATS`/
   `GET_STATE`), and the pureScroll band fix validated earlier in this investigation remains
   covered by `ScrollExposedRowsTests` (7 tests). Window-capture verification was not possible in
   this environment (X11/Wayland split); the pixel-diff harness above is the ground truth.

---

## 7. Phasing — all shipped

> **Field fix 1 (post-ship):** typing showed stale text because the writer's same-row write
> coalescing suppressed generation/epoch bumps across renders. Fixed by resetting the coalescing
> in `MarkRender()` (the canvas's render boundary); regression tests
> `RenderBoundary_ResetsWriterCoalescing_SoTypingAlwaysBumpsEpoch` and
> `Typing_ConsecutiveSameRowKeystrokes_RendersEach`.
>
> **Field fix 3 (post-ship):** `yes`-style output bursts queued tens of thousands of scrolls per
> chunk and the replay (one region memmove each) became gigabytes of copying — frozen UI, no
> autoscroll, no wheel. Fixed with a replay cost cap (total region height ≤ 8×rows, else drain +
> full render). Regression test `LfBurst_OverReplayCap_FallsBackToFullRender`.
>
> **Field fix 4 (post-ship, the actual freeze):** the replay cap above bounds render *cost*, but
> the true freeze was upstream of rendering entirely. `RenderToBitmap` runs on the UI thread and
> did `Monitor.Enter(buffer.SyncRoot)` — an *unbounded* blocking wait. The PTY-write thread holds
> the same lock per chunk (`lock (Adapter.Buffer.SyncRoot) { Parser.Feed(chunk); }`) and, under a
> firehose (`yes`), always has a next chunk ready — it re-acquires the lock immediately after
> releasing it. `Monitor` isn't FIFO-fair, so the writer can win every re-acquisition race and
> starve the UI thread for as long as the burst lasts. Since Avalonia's entire dispatcher/input
> loop runs on that same thread, the whole app — not just the terminal view — froze: no repaint,
> no autoscroll, no wheel, no window response. Confirmed live via `dotnet-dump`: the UI thread's
> stack was parked in `Monitor.Enter` inside `RenderToBitmap` while a thread-pool worker was
> actively inside `LineFeed → ScrollRegionUp → ClearPhysicalRow`, for 70+ seconds straight
> (cursor position read via `GET_STATE` — no lock needed — was static the entire time).
> Fixed: `RenderToBitmap` now uses `Monitor.TryEnter(buffer.SyncRoot, 4, ...)`; on failure it
> skips the frame (the caller redraws the last cached bitmap) instead of blocking. The dedicated
> render timer retries every tick, so the view catches up the instant a gap opens. Verified live:
> before the fix, `GET_STATE`'s cursor was frozen for 70s+ under a real `yes` flood while
> `dotnet-dump` showed the UI thread parked in `Monitor.Enter`; after the fix, the same flood
> keeps the scrollback ring visibly live (torn reads show it overwriting mid-write) and a fresh
> dump shows the UI thread actively executing inside `RenderToBitmap` instead of blocked.
>
> **Field fix 2 (post-ship):** after a full-screen clear, cleared rows kept the old prompt
> segment's pixels in the left content-padding gutter. The canvas's `ContentPadding` is bound to
> the grid's `CanvasPadding` (16/24/16 even with a null config), so the content is translated
> 16px right — the incremental base-background fill started at local x=0 and missed the gutter
> that the full render's `canvas.Clear` covers. Fixed by filling the full clip width in
> `RenderDirty` (and the pureScroll scrollback-band fill). Regression test
> `Clear_WithContentPadding_FullyErasesPromptSegmentPill`.

## 9. Reverted (post-ship)

Two additional bugs surfaced under live desktop testing (real mouse wheel + real window, not
just the synthetic pixel-diff harness) that the four field fixes above didn't touch:

1. **Manual scroll pixel corruption.** A wheel scroll through the pureScroll (viewport-shift
   memmove) path produced a row with visibly corrupted glyphs (`LINE` rendered as `LTNF`) — a
   horizontal smear consistent with a stale/partial repaint at a fractional-pixel scroll
   boundary. Root cause not isolated before the decision below.
2. **Autoscroll never following new output**, unrelated to this design's own logic: the canvas
   posts `UpdateScrollState` and the render-scheduling `SetBuffer` callback via
   `Dispatcher.UIThread.Post` at `DispatcherPriority.Background`/default respectively. Both are
   *lower* priority than `DispatcherPriority.Render` (the compositor's own pass, scheduled every
   frame); under continuous rendering both posts were starved indefinitely, so new output never
   scrolled into view regardless of the render path used. Fixed independently by moving both
   posts to `DispatcherPriority.Render` — unrelated to the incremental design, but found while
   investigating why fixing this design's bugs didn't fix the user's reported symptom.

Given three correctness bugs found in this feature during one session — none caught by the
pixel-diff harness, which exercises the primitives synthetically but not through a real
`ScrollViewer` + real wheel/window-manager integration — the canvas's `RenderToBitmap` was
reverted to always take the full-render path (`TerminalCanvas.cs`, "Always full render" comment
block). Full render is ~11.7ms at 73×136 (Section 6), well within a frame budget, so this trades
the incremental design's performance upside for correctness until it can be re-verified with
real interactive testing, not just synthetic scenarios.

**Kept, not deleted:** `ScrollEpochMath`, `ComputeExposedRows`, `ApplyScrollToMirror`,
`MemmoveRegionRows`, `MemmoveWholeFrame`, `ComputeDirtyRows` (all `internal static` in
`TerminalCanvas.cs`), `TerminalFrameComposer.RenderDirty`, and the buffer-side `PendingScroll`
queue/epoch bookkeeping (`TerminalBuffer.cs`) — all still exercised by their own unit tests
(`ScrollExposedRowsTests`, `ScrollEpochTests`, `IncrementalRenderTests`, `LfBurst_*`), just no
longer wired into the canvas's render call site.

| Phase | Scope | Outcome |
|-------|-------|---------|
| 0 | Confirm the regime (alt vs main screen) and measure real renders/notch (Section 0.1). | nvim with `TERM=xterm-256color` uses the alt screen + DECSTBM region scrolls (observed live earlier); every repaint was a full render. Render coalescing already exists end to end (`FlushRender` dirty-gate + UI-thread dedupe + Avalonia frame coalescing) — the "1–3 renders per notch" framing from the synthetic harness does not multiply in practice, and no debounce change was made (it would only add latency). |
| 0.5 | Render-flush coalescing + glyph-draw batching (Section 0.2). | Coalescing: already in place, no change. Glyph batching: the composer already batches contiguous 2+ glyph runs; the remaining per-cell fallback is dominated by classification, which Phase C's cache eliminates on unchanged rows — no separate batching work shipped. |
| A | Step 1: buffer scroll accounting + queue (Section 4.2). | Shipped with `ScrollEpochTests` (12 tests). |
| 4.2a | Dual-screen generation isolation. | Resolved by verification: `SetAlternateScreen` → `MarkAllRowsDirty` + queue clear (Section 4.2a). |
| B | Step 2: renderer replay + mirror (Section 4.3). | Shipped; pixel-identical across all harness scenarios; `SD(26)` 11.7 → 1.87 ms. |
| C | Step 3: classification cache + `RenderDirty` (Section 4.5). | Shipped; statusline 7 → 0.16 ms; also speeds the full path for mostly-static screens. |
| D | Follow-ups (main-screen output flow, alt-screen `_totalScrolled` bug). | Not shipped — see Section 8. |

Recommended sequencing was **0 → 0.5 → A+B → C**, implemented in that order.

---

## 8. Open questions & follow-ups

1. **Main-screen output flow** (4.6): full-screen scrolls with scrollback growth still fall back
   to a full render via the `sbCount` rule. The scrolls are recorded, but replaying them would
   need a combined "scroll + translate change" path. Not shipped.
2. **Alt-screen `_totalScrolled` growth** (pre-existing, 4.6): full-screen scrolls in the alt
   screen increment `_totalScrolled`, which can wrongly enable the viewport pureScroll path.
   Not shipped.
3. **Queue bounds** (4.4): no cap was needed — replays are O(region height) and each is
   trivially cheap. A cost-based cap remains an option if an adversarial workload ever appears.
4. **`_renderEpochs` memory**: 73–200 rows × 8 bytes — trivial; no pooling needed.
5. **Skip the clear on full-dirty passes**: a memmove-less full dirty pass (every visible row
   dirty, `sbCount`/offset unchanged) could skip `canvas.Clear` — but the base-color refill under
   dirty rows already covers this; left as a possible micro-optimization.
