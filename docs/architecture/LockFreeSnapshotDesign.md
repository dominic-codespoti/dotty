# Lock-Free Snapshot Capture — Design Analysis

Branch: `feat/gpu-rendering` · Status: **ANALYSIS** · Last updated: 2026-08-26

## 1. Problem statement

Under sustained flood (`yes`, 500K lines), enabling rendering costs ~300 ms of
drain throughput (804 ms parse-only → ~1120 ms with render; kitty does
parse+render at 743 ms). Hypothesis entering this analysis: the snapshot
capture's `SyncRoot` hold stalls the parser thread, and a lock-free capture
removes the stall.

## 2. Measured ground truth (this session)

| Measurement | Value | Method |
|---|---|---|
| Capture TryEnter wait (UI thread) | **0.1–0.6 µs avg** | timestamp split around `Monitor.TryEnter(SyncRoot, 4ms)`, 212–753 captures |
| Capture hold incl. wait (earlier, polluted) | 633 µs avg | timestamp before TryEnter — includes wait |
| ScrollbackText materialization skip | ~35 ms drain delta | env-gated skip, median of 3 (≈ noise) |
| Yield handshake disable | no effect | env-gated, median of 3 |
| Run-to-run noise floor | **±15–30 %** | identical binaries, batches 640–1269 ms |

**The decisive number is the first one.** `Monitor.TryEnter` returns in
sub-microsecond — the lock is already effectively uncontended. The
`ReaderWaiting` yield handshake (parser yields between 8 KB sub-chunks while
the renderer wants the lock) is doing its job. Classic lock contention —
kernel transitions, unfair handoff, 4 ms timeouts — is *not* where the
~300 ms goes.

## 3. Where the interference actually is

With contention ruled out, the render-path drain penalty decomposes into:

1. **CPU scheduling competition.** Under flood+render, active CPU consumers:
   `yes`, `head`, app reader thread, parser consumer, UI thread, render
   thread (quad build + submit), Avalonia render loop, Hyprland compositor
   (120 Hz). The parser thread gets preempted; lock-freedom does not fix
   preemption.
2. **Per-frame dead work** (both render paths): `ScrollbackText`
   materialization — ~50 strings × 136 cells rebuilt from cells *per capture*
   with **zero consumers** (verified by grep; same pattern as the deleted
   motion epochs). Plus `CaptureStyles()` allocating a fresh array per frame.
3. **Render-thread CPU**: quad building rebuilds every visible row every
   frame even though flood rows are content-identical, position-shifted.

Evidence both render paths cost the same (~1120 vs ~1116 ms) despite wildly
different raster work (GPU quads vs CPU `DrawText`) — the penalty lives in
the shared per-frame machinery, not the raster.

## 4. Lock-free design options (as asked)

Shared state the capture reads: cell arenas (`_cellsPtr`/`_coldCellsPtr`),
`_rowGenerations`, `_rowMaxCol`, `_rowColdFlags`, ring `_head`, scrollback
count, cursor, scroll region, alt-screen flag, style table.

### Option A — Global seqlock

Writer: `seq++` (odd) → mutate sub-chunk → `seq++` (even). Reader: read even
`seq` → copy → re-read; retry on change.

- Writer fast path: 2 volatile writes vs Monitor enter/exit. *Faster than today.*
- **Fatal flaw standalone**: reader copy (~20–700 µs) vs writer holds
  (~300–400 µs, back-to-back under flood) → retry storm, livelock risk.
- Workable only with writer backoff: writer checks `ReadersWaiting` at
  sub-chunk boundaries and volunteers a gap (we already have the handshake
  flag and the yield loop — this is a natural extension).
- Structural writers (resize, alt-screen swap, buffer realloc) cannot use a
  seqlock; they need mutual exclusion → SyncRoot stays for them, fast writer
  must check "structural op in flight" before starting a batch.

### Option B — Per-physical-row seqlock + global structure epoch

Key insight: the scroll ring rotates `_head`, not row data. Sequences indexed
by **physical** row change only when that physical row is written or cleared —
scroll costs bumps only on the exposed band, not a rotation. (The rotation
cost is exactly what the deleted motion-epoch system paid.)

- Reader validates per row (retry single row) + global epoch for
  `_head`/scrollback/cursor consistency.
- Scroll-during-read: physical rows keep content; only head/count and the
  exposed band go stale → partial re-read instead of full retry.
- Cost: 2 bumps per row write (~1 M volatile writes per flood ≈ negligible);
  writer complexity significantly higher; two-level tear handling.

### Option C — Producer-built replica (kitty-style pointer swap)

Parser maintains a shadow copy of the visible grid, publishes via atomic
reference swap; renderer never blocks, never retries.

- Reader cost: zero. Writer cost: ~2× cell writes + replica scroll handling.
- Every buffer mutation (write, clear, erase, scroll, resize, alt-screen)
  must be mirrored. Highest complexity, highest writer overhead.

### Option D — Hybrid (recommended): Tier 0 + seqlock capture

**Tier 0 — delete dead per-frame work (no locking change needed):**
- Remove `ScrollbackText` from the per-frame capture (zero consumers). If a
  future consumer needs it, materialize at scroll time on the parser thread
  (row content hot in cache) and cache on `ScrollbackLine`.
- `CaptureStyles()`: cache the array, invalidate via a style-generation
  counter (bumped by `GetOrCreateId` on insert / palette remap). Removes a
  per-frame allocation + lock acquire.

**Tier 1 — seqlock the capture** (Option A + existing handshake):
- Parser fast path: replace `Monitor.Enter/Exit` per sub-chunk with
  `seq++/seq++` (Volatile.Write release semantics). Check `SyncRoot`-held
  flag for structural ops; check `ReaderWaiting` at boundaries → volunteer
  a ~50 µs gap (enough for the ~20 µs pure-memcpy capture once Tier 0 lands).
- Capture: seq-read → copy → seq-validate, bounded retries (8), fallback to
  the existing SyncRoot path (correctness safety net).
- Structural writers keep SyncRoot; fast writer yields while it's held.
- Memory ordering: x86-TSO is sufficient with `Volatile.Read/Write` on the
  seq; on ARM, release/acquire via `Interlocked` or `Volatile` on seq only —
  data stores are ordered by the release fence. AVX2 stores are plain stores,
  covered by the release.

**What lock-freedom buys here (honestly):** removal of the remaining Monitor
handoff latency and the 4 ms timeout pathology; a capture that cannot
block the UI thread. Given measured 0.6 µs waits, the direct win is small.
The *structural* win: once the capture is seqlock-based, the parser never
observes renderer activity at all except the volunteered gaps (~0.6 % duty),
and the bitmap path can raster from the same snapshot — unifying both render
paths on one capture mechanism.

**What it does not buy:** the scheduling-level preemption loss. That needs
render-side work reduction (per-row quad caching keyed on the row
generations we already copy — translate-on-scroll instead of rebuild) and/or
thread priority hints.

## 5. Recommended sequence

1. **Tier 0 now** (~1 h): remove ScrollbackText from capture + style capture
   caching. Dead-work deletion; zero risk; measurable via capture-work diag.
2. **Tier 1 seqlock** (~1 day): parser-side seq + reader-side validated copy
   with SyncRoot fallback. Ship behind `DOTTY_SEQLOCK_CAPTURE=1` until the
   pixel-diff gate passes on hardware.
3. **Render-side quad caching** (separate work item): per-row quad reuse
   keyed on `RowGenerations` — attacks the scheduling-level cost that
   lock-freedom cannot.

## 6. Risks

- Seqlock retry storms under pathological writers → bounded retries +
  SyncRoot fallback (never livelocks; degrades to today's behavior).
- Structural-op coordination bug → fast writer must observe SyncRoot-held
  before *every* batch start; test with resize-during-flood.
- ARM memory model → keep seq ordering via `Volatile`/`Interlocked` only;
  validate on the pixel-diff gate.
- Measurement noise (±15–30 %) → accept changes only on median-of-5 with
  interleaved A/B runs.
