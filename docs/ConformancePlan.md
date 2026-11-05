# Terminal conformance completion plan

## Goal and scope

Resolve every case in the original **263-test failing inventory** through correct, observable terminal behavior. No case is excluded merely because the implementation currently lacks the feature. The exact test-by-test targets appear below, grouped by shared behavior so one underlying fix can resolve several cases.

The initial inventory remains historical evidence; completed work is recorded below. Its cases are not necessarily independent bugs or regressions introduced by the Ghostty-parity branch. Implementation paths identify investigation entry points; root causes must be established from the byte transcript and production state before editing.

The compatibility target combines ECMA-48 control functions, DEC/VT semantics and xterm extensions. Passing this profile is not certification against every terminal standard or proof of complete Ghostty parity. Relevant references are the [ECMA-48 scope](https://ecma-international.org/publications-and-standards/standards/ecma-48/), [xterm control sequences](https://invisible-island.net/xterm/ctlseqs/ctlseqs.html) and the pinned upstream test bodies linked for every case.

## Fixed evidence snapshot

- Source: the initial Linux run following the palette reply compatibility fix, before the completed batches below; original local report: `/tmp/dotty-check/esctest-palette-fixed/results.json`.
- Suite: `ThomasDickey/esctest2` at `2798f12149a19c3295e9b4853ab2da4b2eff1b2b`.
- Configuration: [`tests/esctest/esctest.json`](../tests/esctest/esctest.json), 25 rows by 80 columns, production terminal core through a real PTY and headless `TerminalSession`.
- Host switches: `--checksum-reports --window-resize`.
- Suite arguments: `--expected-terminal=xterm --xterm-checksum=334 --max-vt-level=5 --options xtermWinopsEnabled --timeout 1 --v 2`.
- Results: 567 total; 270 passed; 263 failed across 66 classes; 34 upstream `known-bug` outcomes; no harness faults, skipped or not-run outcomes. The upstream annotations include one case whose body was deliberately not attempted despite its result bucket not being named `skipped`.
- Local expected-failure file: no entries or captured-baseline marker. The run was reporting-only, not a successful conformance gate.

The tables preserve the inventory and upstream anchors so the plan does not depend on that temporary report remaining available. A recorded message is the first reported failure only, not a complete diagnosis; terse screen-content fragments and reply timeouts must be interpreted with the linked test and transcript.

## Completed batch 1 (2026-11-04)

**G01, G02 and G03 are complete:** all 41 of their original failing cases pass. Shared behavior fixes also resolve nine cases in G06/G07/G13, bringing the original inventory to **50 passing targets and 213 remaining failures**. The full run has 320 passes and 34 upstream expected-failure annotations; all 270 original passes are retained, and all 567 names remain accounted for with no harness faults, skips or not-run outcomes.

The additional resolved cases in partially complete families are:

- `DECSETTests`: `test_DECSET_DECAWM_NoLineWrapOnTabWithLeftRightMargin`, `test_DECSET_DECOM`, `test_DECSET_DECOM_DECRQCRA`, `test_DECSET_ReverseWraparoundLastCol_BS`, `test_DECSET_ReverseWraparound_BS`, `test_DECSET_ReverseWraparound_Multi`.
- `DECALNTests`: `test_DECALN_FillsScreen`, `test_DECALN_MovesCursorHome`.
- `DECRQMTests`: `test_DECRQM_DEC_DECLRMM`.

The user approved modern xterm reverse-wrap semantics, so the configuration now explicitly includes `--xterm-reverse-wrap=383`. Mode 45 is limited to soft-wrapped lines; mode 1045 permits extended wrapping. The suite commit, `--xterm-checksum=334`, VT level and complete test inventory are unchanged. No test bodies, xfails or skip lists were changed.

Verification: warning-as-error Release build with zero warnings/errors; 3,129 terminal tests and 5,812 application tests passed, with four existing platform skips and one existing missing-golden skip. An actual X11 GUI under private Xvfb passed 20 PTY request/reply scenarios; its entire visible alignment grid was checked and screenshots were inspected. The app exited cleanly. The reverse-wrap reset regression failed before its fix and passes in the final suite.

Run evidence is retained in the documented batch inventory and outcomes below; temporary local reports and GUI artifacts are not durable repository evidence. This was a completed starting batch, not a full conformance pass; the CI run was reporting-only.

## Completed batch 2 (2026-11-04)

**G04 is complete:** all 18 original line-movement and bounded-scrolling failures now pass. The original inventory stands at **68 passing targets and 195 remaining failures**. The full run has **338 passes and 34 unchanged upstream expected-failure annotations**. All 320 passes from batch 1 are retained, and the same 567 names are accounted for with no harness faults, skips or not-run outcomes. The pinned suite and approved profile are unchanged; no suite bodies, xfails or skip lists were edited.

LF/VT/FF/IND and RI now use consistent two-axis scroll eligibility and physical-screen movement outside the vertical region. NEL indexes before its margin-aware carriage return. SU/SD use a native rectangle operation for partial-width regions: hot/cold cell metadata moves together, crossing wide glyphs are repaired as whole glyphs, and the ring and scrollback remain unchanged. Full-width ring/history paths are retained. Ordinary image placements move only when initially contained within the applicable region; outside and straddling placements stay anchored. Review also caught a pending-wrap handoff that reset the local column to zero in both ASCII span writers; the wrong-column writes were reproduced in the real GUI before fixing the handoff, with char-span and byte-span regressions retained.

Verification: warning-as-error Release build with **zero warnings/errors**; **3,145 terminal tests and 5,817 application tests passed**, with four existing platform skips and one existing missing-golden skip. The final real X11 GUI under private Xvfb passed **16/16 PTY scenarios** with cursor replies, visible screen grids and scrollback checked after each operation, including normal and bottom-region pending wraps. Screenshots were inspected, and both the app and Xvfb exited cleanly.

Run evidence is summarized by the inventory and verification record above; temporary local report paths are intentionally omitted. The next work package was G05: character/line editing and ordinary erase, with 18 original failures open at that point. Conformance remained reporting-only.

## Completed batch 3 (2026-11-04)

**G05 is complete:** all 18 original character/line editing and ordinary-erase failures now pass. Shared fixes also resolve **17 cases in G06/G09**, bringing the original inventory to **103 passing targets and 160 remaining failures**. The full pinned run has **373 passes and 34 unchanged upstream expected-failure annotations**. All 338 passes from batch 2 remain passing; the same 567 names are accounted for with no harness faults, skips or not-run outcomes. The suite, profile, assertions and annotation lists are unchanged.

ICH/DCH now edit only the eligible horizontal span; IL/DL reject an outside cursor and shift only the eligible rectangle without rotating history. All writer paths share right-margin bounds, including REP's saturated-count calculation. SPA/EPA marking is independent of SGR; glyph-safe ordinary erase preserves ISO-protected cells, and protection survives native moves and wide-glyph reflow. Protected erasure refreshes row extents once rather than rescanning the row for every unprotected run.

Preserving existing passes required separating DEC selective erase from ordinary ISO erase and applying actual DECSCL feature-level gates. This does not adopt xterm's annotated ISO/selective-erase compatibility exception. Ordinary ED 2 now drops its reported history count together with the actual history; selective ED 2 preserves history and cursor. Review also caught and fixed a wide-glyph row-link regression, lost continuation protection during reflow and REP count reduction for output starting beyond the right margin.

Additional resolved cases:

- `DECSCLTests.test_DECSCL_Level2DoesntSupportDECRQM`.
- `DECSETTests`: `test_DECSET_DECAWM_OffRespectsLeftRightMargin`, `test_DECSET_DECAWM_OnRespectsLeftRightMargin`, `test_DECSET_DECLRMM`.
- `DECSEDTests`: `test_DECSED_Default_Protection`, `test_DECSED_DECSCA_2`, `test_DECSED_0_Protection`, `test_DECSED_1_Protection`, `test_DECSED_2_Protection`, and the three `test_DECSED_{0,1,2}_WithScrollRegion_Protection` cases.
- `DECSELTests`: `test_DECSEL_Default_Protection`, `test_DECSEL_0_Protection`, `test_DECSEL_1_Protection`, `test_DECSEL_2_Protection`, `test_DECSEL_IgnoresScrollRegion_Protection`.

Verification: warning-as-error Release build with **zero warnings/errors**; **3,178 terminal tests and 5,821 application tests passed**, with four existing platform skips and one existing missing-golden skip. The final actual X11 GUI under private Xvfb passed **31/31 PTY scenarios**, checking cursor replies, screen contents and scrollback after each operation. Screenshots were inspected; the app and Xvfb exited cleanly. Obsolete tests that required phantom erased-history rows or stationary images during line edits were removed rather than repinned; memory-reclamation, row-model and image-movement behavior remains covered.

Run evidence: temporary local report and GUI artifact paths are omitted; the results and behavior verified in this batch are recorded above.

**At the end of batch 3,** the next work package was the 11 remaining G06 cases; G09 still had six open cases. Conformance remained reporting-only.

## Completed batch 4 (2026-11-04)

**G06 is complete:** all 11 remaining targets pass, completing its 21-case original inventory. Shared fixes resolve **nine additional cases in G07/G13**, bringing the original inventory to **123 passing targets and 140 remaining failures**. The full pinned run has **393 passes and the same 34 upstream expected-failure annotations**. All 373 passes from batch 3 remain passing; the same 567 names are accounted for without harness faults, skips or not-run outcomes. The suite, approved profile, assertions, annotation lists and host switches are unchanged.

Implemented ANSI insert/newline modes through the parser, text writer and real keyboard path. Insert mode shifts each ASCII row segment once and uses glyph-width deltas for wide/combining text; replace mode retains its bulk path. Mode 41 consumes pending wrap before tab traversal, while a no-op tab with the mode off preserves the pending printable wrap.

Mode 47 now retains alternate text/images; 1047 clears before exit, and 1049 uses the ordinary main-screen saved cursor/rendition slot. Saved slots are screen-local, reusable and reflowed against their own screen, and include origin/protection/pending-wrap state. Column modes 3/40/95 perform immediate clear/home/margin effects but request real geometry through the existing host permission policy. No-clear mode is VT5-only, actual dimensions drive mode-3 replies, and full-width margins follow resized grids. Denied host resizing still reports and renders the unchanged geometry.

The pinned `DECSET_DECNCSM` test relies on mode 40 enabled by an earlier test in its class: the runner launches one process per class, and upstream `reset()` sends DECSCL/DECSTR without resetting mode 40. Preserving that permission across soft resets satisfies the real prerequisite without changing defaults, the harness or the profile.

Additional resolved cases:

- `DECRCTests` and `SCORCTests`: `test_SaveRestoreCursor_AltVsMain`, `test_SaveRestoreCursor_ResetsOriginMode`.
- `RISTests.test_RIS_ResetDECCOLM`.
- `DECRQMTests`: `test_DECRQM_ANSI_IRM`, `test_DECRQM_ANSI_LNM`, `test_DECRQM_DEC_DECCOLM`, `test_DECRQM_DEC_DECNCSM`.

Verification: warning-as-error Release build with **zero warnings/errors**; **3,198 terminal tests and 5,825 application tests passed**, with four existing platform skips and one existing missing-golden skip. The actual X11 GUI under private Xvfb passed **31/31 scenarios plus a separate host-denial scenario**. Checks covered visible cells, cursor replies, scrollback, real window/grid/PTY dimensions, and Return-key bytes delivered through X11 keyboard events. Screenshots were inspected. The GUI waits for accepted asynchronous geometry before assertions that depend on the new layout; the conformance host remains synchronous. Owned app/Xvfb processes exited cleanly.

Batch 4 results are recorded in the inventory and verification summaries above; temporary local report paths and GUI artifacts are omitted.

At the end of batch 4, the four remaining G07 cases were RIS title-mode reset, SCO save/restore with horizontal margins, and xterm saved set/reset mode state; conformance remained reporting-only, not a full standards certification.





## Completed batch 5 (2026-11-04)

**G07 is complete:** its four remaining targets pass, completing all 11 original cases. Shared title work also resolves **six G14 cases**, bringing the original inventory to **133 passing targets and 130 remaining failures**. The full pinned run has **403 passes and the same 34 upstream expected-failure annotations**. All 393 passes from batch 4 remain passing; the same 567 names are accounted for without harness faults, skips or not-run outcomes.

Default-parameter `CSI s` now respects DECLRMM: it sets default horizontal margins when enabled instead of overwriting the saved cursor. `CSI u` still restores the prior save. XTSAVE/XTRESTORE implement the supported live private modes rather than special-casing autowrap: reusable cache slots, full bounded parameter lists, shared mouse/encoding/alternate-buffer slots, mode-1048 cursor actions, and effectful restores through the existing setters. The cache starts at zero and survives both RIS and DECSTR, following [xterm's save/restore and reset implementation](https://github.com/ThomasDickey/xterm-snapshots/blob/master/charproc.c).

Window/icon titles now have independent canonical state and set/query encoding modes. RIS restores native UTF-8 title defaults while retaining both titles. The raw-byte reply path preserves Latin-1 and UTF-8 through the real PTY; decoded control characters are filtered and malformed hex updates are rejected. Unchanged titles reuse strings, OSC 0 shares one retained value, and private-mode caching adds no per-command heap allocation.

**Explicitly approved profile amendment:** the user selected a dedicated, default-off title-report permission. Product configuration adds `vt.titleReportEnabled`; the fixture alone adds `--title-reports` alongside `--checksum-reports --window-resize`. This enables the RIS title-reset case and the six title cases below. It does not enable product title replies by default, piggyback on resizing, enable clipboard reads, or waive any test. The suite commit, suite arguments, checksum/reverse-wrap profiles, VT level, assertions and annotation lists are unchanged.

Additional resolved cases:

- `SMTitleTests`: `test_SMTitle_SetHexQueryHex`, `test_SMTitle_SetHexQueryUTF8`, `test_SMTitle_SetUTF8QueryHex`, `test_SMTitle_SetUTF8QueryUTF8`.
- `XtermWinopsTests`: `test_XtermWinops_ReportIconLabel`, `test_XtermWinops_ReportWindowLabel`.

Verification: warning-as-error Release build with **zero warnings/errors**; **3,221 terminal tests and 5,826 application tests passed**, with four existing platform skips and one existing missing-golden skip. Three obsolete cursor-forwarding tests were removed rather than repinned; real saved-cursor/margin behavior is covered by regressions. The actual X11 GUI under private Xvfb passed **13/13 scenarios plus a separate default-denial scenario**, checking cursor replies, visible cells, scrollback, exact title reply bytes and rendered title state. Latin-1/UTF-8/hex combinations, reset-persistent mode caches, alternate-screen retention, mode-1048 actions and control filtering were exercised. Screenshots were inspected; owned app/Xvfb processes exited cleanly.

Batch 5 results are recorded in the inventory and verification summaries above; temporary local report paths and GUI artifacts are omitted.

At the end of batch 5, the remaining work was G08 DEC column editing and horizontal indexing (20 original failures); G14 also retained 12 open targets. These are historical counts; conformance remained reporting-only.

## Completed batch 6 (2026-11-04)

**G08 is complete:** all 20 original DEC column-editing and horizontal-indexing failures now pass. The original inventory has **153 passing targets and 110 remaining failures**. The full pinned run has **423 passes and the same 34 upstream expected-failure annotations**. All 403 passes from batch 5 remain passing, and the same 567 names are accounted for without harness faults, skips or not-run outcomes. The host profile, suite arguments, commit, assertions and annotation lists are unchanged.

VT4+ now implements DECIC (`CSI Pn ' }`), DECDC (`CSI Pn ' ~`), DECBI (`ESC 6`) and DECFI (`ESC 9`). Column edits cover every scrolling row from the cursor column to the right margin, with both-axis eligibility, default/zero counts of one and saturated counts bounded to the edit width. Horizontal indexing shifts the complete margin rectangle at its corresponding edge; away from that edge it uses ordinary backward/forward cursor movement. Pending-wrap and outgoing soft-wrap links follow the operation's semantics. Native cell editing retains wide-glyph repair, cold metadata and protection without rotating the ring or adding history.

The two `WholeScreenScrolls` comments were misleading, not grounds for exceptions: the tests leave full-page margins active, so the page edge is the relevant scrolling margin. [DEC's DECBI description](https://vt100.net/docs/vt510-rm/DECBI.html), [DECFI description](https://vt100.net/docs/vt510-rm/DECFI.html) and [xterm's index/column-scroll implementation](https://github.com/ThomasDickey/xterm-snapshots/blob/master/util.c) support the asserted shifts. Reference source was inspected; an xterm executable was not installed, so no xterm runtime comparison is claimed.

Contained normal Kitty placements follow the actual edited rectangle, crop their source at horizontal boundaries and are deleted when fully displaced. Reversing direction does not regrow clipped pixels. Placements left of the cursor's edit start, outside the region, straddling its bounds or in history stay anchored; relative children follow their parent and virtual placeholders travel with native cell metadata. Ordinary ICH/DCH behavior is unchanged.

Verification: warning-as-error Release build with **zero warnings/errors**; **3,229 terminal tests and 5,836 application tests passed**, with four existing platform skips and one existing missing-golden skip. The actual X11 GUI under private Xvfb passed **35/35 scenarios**, including eight screenshot-backed image stages. Pixel bounds and source colors verified left/right crop, irreversible clipping, both index directions and stationary image sentinels. Cursor replies, visible cells, pending-wrap transitions, VT3 denial/VT4 acceptance, margins and unchanged scrollback were checked. Owned app/Xvfb processes exited cleanly.

Batch 6 results are recorded in the inventory and verification summaries above; temporary local report paths and GUI artifacts are omitted.

At the end of batch 6, G10 rectangle operations and six dependent G09 DECSERA cases remained (24 original failures sharing rectangle-coordinate and protection semantics). Conformance remained reporting-only.

## Completed batch 7 (2026-11-04)

**G09 and G10 are complete:** the 18 original rectangle copy/fill/erase failures and the six remaining DECSERA failures now pass. The original inventory has **177 passing targets and 86 remaining failures**. The full pinned run has **447 passes and the same 34 upstream expected-failure annotations**. All 423 passes from batch 6 remain passing. The same 567 names are accounted for without harness faults, skips or not-run outcomes; the host profile, suite arguments, commit, assertions and annotation lists are unchanged.

VT4+ now implements [DECCRA](https://vt100.net/docs/vt510-rm/DECCRA.html), [DECFRA](https://vt100.net/docs/vt510-rm/DECFRA.html), [DECERA](https://vt100.net/docs/vt510-rm/DECERA.html) and [DECSERA](https://vt100.net/docs/vt510-rm/DECSERA.html). Shared coordinate conversion applies one-based defaults, clipping, origin-relative bounds and independent source/destination defaults without moving the cursor or consuming pending wrap. With origin mode off, scrolling margins do not constrain these commands. Copy page selectors resolve to the single active page, not to the alternate screen.

Rendition follows the DEC command descriptions: fill installs current SGR and selected protection, ordinary rectangle erase removes rendition and protection, and selective rectangle erase retains rendition while honoring DEC rather than ISO protection. The parser rejects invalid fill characters and malformed forms, and translates valid fill characters through the selected DEC graphics charset. DEC documentation and xterm source were inspected; no xterm executable was available, so no xterm runtime comparison is claimed.

Native copy uses overlap-safe row ordering and span moves without a rectangle-sized scratch allocation. Cell styles, protection and cold metadata travel together; rows without cold metadata avoid unnecessary cold-plane copies. Wide-glyph cuts are repaired after the source data has been captured. Cursor, history, ring position and destination logical-line links are preserved. Final edge checks found and fixed a separate blank-copy defect: unwritten source cells must become explicit spaces at the destination, or a copied blank row can lose its extent and collapse during reflow. A failing-before/passing-after invariant/reflow test and the actual GUI reproduce and verify that correction.

Verification: warning-as-error Release build with **zero warnings/errors**; **3,229 terminal tests and 5,864 application tests passed**, with four existing platform skips and one existing missing-golden skip. The actual X11 GUI under private Xvfb passed **47/47 scenarios**, including five screenshot-backed rendition checks. Full-grid observations cover defaults, clipping, overlap, origin and margins, VT3 denial/VT4 acceptance, DEC/ISO protection, pending wrap and the blank-copy resize regression. Pixel comparisons verify retained selective-erase backgrounds, ordinary-erase reset, current fill colors and copied source rendition. Owned app/Xvfb processes exited cleanly.

Batch 7 results are recorded in the inventory and verification summaries above; temporary local report paths and GUI artifacts are omitted.

At the end of batch 7, G11 palette input formats, special colors and resets remained, covering 41 original failures. Conformance remained reporting-only.

## Completed batch 8 (2026-11-05)

**G11 is complete:** all 41 original palette/color-format/special-color/reset failures now pass. The original inventory has **218 passing targets and 45 remaining failures**. The full pinned run has **488 passes and the same 34 upstream expected-failure annotations**. All 447 passes from batch 7 remain passing. The same 567 names are accounted for without harness faults, skips or not-run outcomes. The suite commit, arguments, checksum profile, VT level, assertions and annotation lists are unchanged; the explicitly approved fixture color-baseline adjustment is described below.

OSC hash colors now use X11's left-aligned 16-bit channel convention, independently of CSS theme shorthand. `rgb:`, `rgbi:`, CIEXYZ, CIExyY, CIEuvY, CIELab, CIELuv and TekHVC use the fixed [libX11 Xcms fallback profile and algorithms](https://github.com/mirror/libX11/tree/master/src/xcms): the Tektronix 19-inch Sony CRT matrices, response curves, gamut handling and quantization. This is not a sample-value lookup, generic sRGB conversion or a claim to match arbitrary display calibration. The allocation-free managed implementation matched actual libX11 1.8.14 under Xvfb on **437/437 sampled colors**, including signed chroma, low-lightness and gamut boundaries. No xterm executable comparison is claimed.

OSC 4 special indices 256–260 and OSC 5 indices 0–4 now share the five attribute colors. Ordered set/query mixtures, dynamic-color sequences, selected/whole resets and theme defaults are implemented. OSC 6/106 modes affect actual cached and newly rendered cells; foreground precedence is blink, bold, underline, italic, with reverse selecting background before inverse swaps. Explicit colors remain authoritative unless mode 5 is enabled; independent SGR 58 decoration is preserved. RIS and DECSTR reset palette and special-color overrides while retaining special-mode bits; only RIS clears dynamic foreground/background overrides.

Two additional GUI-reproduced palette defects are fixed by preserving indexed identity: changing a palette entry no longer recolors matching explicit RGB, and active/saved indexed colors follow palette changes and resets. Existing indexed foreground, background and underline colors across all 256 entries repaint correctly. Renderer settings are value snapshots with cache invalidation and no per-cell allocation.

**Approved fixture adjustment:** the first otherwise-correct run reached 487 passes but exposed the upstream reset helper's black-on-white assumption. Its `reset()` explicitly sets OSC 10 to black and OSC 11 to white, while `ResetSpecialColorTests.test_ResetSpecialColor_Dynamic` expects OSC 110 to restore black. The headless host previously had a grey foreground theme. With explicit user approval, only `ConformanceHost` now establishes black/white theme defaults. No upstream assertions, product themes or correct reset-to-theme behavior changed. Four additional real-GUI frames verify the actual theme foreground, black override, second override and restoration of the original theme rather than the previous override.

Verification: warning-as-error Release build with **zero warnings/errors**; **3,287 terminal tests, 5,865 application tests and 43 affected Skia renderer/cache tests passed**. Four existing platform skips and the existing missing-reflow-golden skip remain unchanged. After the fixture adjustment, the strict build and all **30 conformance-host tests** passed again, followed by the complete pinned suite. The actual X11 GUI passed **42/42 scenarios**, including **39 pixel-checked frames**, with full-grid/cursor/PTY-reply observations. Owned GUI and Xvfb processes exited cleanly.

Run evidence for batch 8 is summarized in its results and verification record; temporary local reports and GUI artifacts are omitted.

At the end of batch 8, G12's five device-identification cases remained open, including the truthful-identity/profile review. Conformance was reporting-only.

## Completed batch 9 (2026-11-05): G12 protocol fixes and identity review

**G12 is partially resolved, not complete:** `DECIDTests.test_DECID_Basic` now passes. The full pinned run has **489 passes, 44 failures and the same 34 upstream expected-failure annotations**; **219 of the original 263 targets are resolved**. All 488 passes from batch 8 remain passing. The same 567 names are accounted for without harness faults, skips or not-run outcomes. Host settings, suite commit/arguments, assertions and annotation lists are unchanged.

`ESC Z` now implements DECID as the [obsolete primary-DA request alias](https://invisible-island.net/xterm/ctlseqs/ctlseqs.html). Investigation also reproduced a real reply-loop defect: the parser answered echoed primary/secondary replies and nonzero DA parameters. Identification requests are now classified before permissive numeric parsing; only omitted or all-zero decimal parameters are accepted, with the appropriate primary/secondary/tertiary introducer. Replies, parameter lists, intermediates and nonzero forms stay silent, and subsequent text/queries remain synchronized. DA3 remains unsupported. No raw C1 input-mode expansion was made.

**User-approved identity policy: keep the strict xterm report.** `DATests.test_DA_0`, `DATests.test_DA_NoParameter`, `DA2Tests.test_DA2_0` and `DA2Tests.test_DA2_NoParameter` remain visibly failing and open. The pinned xterm primary handler requires model 65 plus `[1,2,6,9,15,16,17,18,21,22,28,29]`; its VT5 secondary handler requires model 64 and a firmware number from 314 through 999. Dotty retains its conservative primary reply and actual product version instead of asserting that identity/capability set. The observed application version was `0.3.0-dev+b9036c9`, corresponding to DA2 version 300. These are neither waived tests nor accepted baseline entries; no alternate profile, exclusion or fabricated xterm version was introduced.

Verification: warning-as-error Release build with **zero warnings/errors**; **3,296 terminal tests and 5,863 application tests passed**, with four existing platform skips and the existing missing-reflow-golden skip unchanged. Obsolete identity/wiring-only tests were removed rather than repinned; protocol regressions exercise the real parser, adapter and buffer. The actual X11 GUI passed **12/12 scenarios**, covering ordered aliases, genuine DA2/XTVERSION, silent reply/non-request forms, cancellation/resynchronization, UTF-8, resets and retained pending wrap. Pixel checks verify rendition across DECID. Both original protocol defects were reproduced in the old GUI binary before editing; the same request paths pass afterward. Owned GUI and Xvfb processes exited cleanly.

Batch 9's result and verification record are summarized above; temporary local report and GUI artifact paths are omitted.

**Historical pause:** work stopped here at the user's request and resumed in batch 10 below. G12's four identity mismatches remained open in the strict report; conformance was still reporting-only.

## Completed batch 10 (2026-11-05): G13/G14

**38 of the 40 remaining G13/G14 targets now pass.** G14 is complete (18/18 original targets); G13 has 31/33 passing, with its two identity-dependent arity mismatches deliberately still visible. The full pinned run has **527 passes, 6 failures and the same 34 upstream expected-failure annotations**. **257 of the original 263 failures are resolved.** All 489 previous passes and exactly the same 567 names are retained, without harness faults, local xfails, skips or not-run outcomes.

The implemented behavior is more than query-state bookkeeping:

- KAM gates native key/text/paste input; SRM controls real local echo. NRCS designation/shift and mode 42 translate the classic national sets without altering Unicode input. Mode 35 implements **xterm font shifting**, not Hebrew keyboard behavior despite the upstream test's DECHEBM name.
- Status reports distinguish unavailable hardware from implemented state. DECRQSS reports actual protection, conformance level, cursor style, margins, rendition, attribute extent and screen dimensions. DECSACE controls observable stream/rectangle attribute changes, including unprinted cells.
- The user-approved printer is an opt-in file sink, disabled by default, with no external command execution. CSI print output honors print extent and form feed; native verification checked exact Unicode/grapheme bytes, region versus page output and form-feed termination.
- Smooth scrolling now renders both directions and bounded regions, with disjoint outgoing/incoming clips and correct final pixels. Verification caught and fixed missing GPU-delta comparisons for pixel offset/clip bounds, lingering outgoing glyphs and overlapping composition. Disabled mode remains a direct jump. Regression timing uses the animation's actual epoch rather than wall-clock scheduling delays.
- Paired icon/window title stacks preserve the required push/pop semantics. Real native window position, iconification and restoration replace unavailable headless observations. OSC 52 uses actual CLIPBOARD, PRIMARY/SELECT and CUT_BUFFER0 targets, with literal `0` isolated from the other selections and safe read/write defaults retained.

**Approved real-host fixture:** exactly four original cases now execute unchanged in isolated Xvfb/Openbox Dotty windows: default-selection OSC 52, iconify/deiconify, move-to-XY and omitted-axis movement. The other 563 names retain the headless PTY lane. Startup checks establish mapped/focused 80×25 windows before releasing the original child; clipboard and window permissions are fixture-only. All four originals pass, including their original assertions, with clean lifecycle accounting. Native cross-application selection checks and default-denied clipboard/window-mutation checks also passed. The full baseline workflow is Linux-only because it includes this real X11 lane; unrelated macOS/Windows build coverage is unchanged.

**OSC 52 reference finding:** the pinned Python 3 [`strip_binary` helper](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/escoding.py#L16-L22) leaves a trailing quote in the original write payload, `dGVzdGluZyAxMjM='`. xterm's [`AppendToSelectionBuffer` / `CompleteSelection`](https://raw.githubusercontent.com/ThomasDickey/xterm-snapshots/master/button.c) ignore non-base64-alphabet input and discard incomplete trailing bits. Dotty now follows those general OSC 52 rules, including unpadded and internally noisy data, within the existing input/output limits. No quote-specific workaround, upstream helper edit or relaxed graphics decoder was used. The exact original ST-terminated payload and independent selection-isolation scenarios pass in the real GUI.

**Approved identity policy:** the four G12 DA/DA2 failures and `DECDSRTests.test_DECDSR_DECXCPR` / `DECDSRTests.test_DECDSR_DSRKeyboard` remain open, not waived or counted as fixed. The latter tests infer reply arity from DA2's conservative model identity, while the implemented reports expose the selected VT-level state. No xterm model/version impersonation, alternate profile, exclusion, baseline capture or assertion rewrite was introduced. Suite commit, arguments, VT level, checksum/reverse-wrap settings and annotation names are unchanged.

Verification: warning-as-error Release build with **zero warnings/errors**; **3,318 terminal tests, 5,891 application tests, 43 affected Skia renderer/cache tests and 51 Python runner tests passed**. Four existing platform skips and the existing missing-reflow-golden skip remain unchanged. Actual X11 checks covered input modes, font metrics, NRCS, native titles, byte-accurate clipboard replies, printer files, attribute pixels and smooth-scroll intermediate/final pixels in both directions. No additional performance benchmark was run. The full terminal-standard milestone remains open because the six identity failures are intentionally visible and the separate upstream-annotation audit is not a claim of 34 fixed product bugs.

**Final integration review:** the expanded Linux checks also passed all **142 Skia tests under Xvfb** and **167 native PTY tests**, plus **79 distribution-script tests** and **117 benchmark-tooling unit tests**. Repository whitespace checks, regenerated man-page/completion checks and the shipping-package notice inventory passed. Review found and fixed a dropped native position query while a window move was pending. Pausing the private window manager reproduced the missing reply before the fix; afterward, delayed movement preserved both queued replies, and a stalled move returned the actual position on deadline expiry. The full pinned rerun retained every one of the same 567 outcomes. Existing saved-cursor/charset limitations are explicitly documented in [Parsing](Parsing.md), not represented as fixed by these conformance counts.

**CI performance follow-up (2026-11-06):** the renderer now keeps cached cells compact and carries smooth-scroll translation/clipping in reusable per-frame runs; allocating frame builds size their buffers to visible content instead of the unconditional decoration worst case. Existing allocation limits passed locally, including zero allocation for the caller-span benchmark. The same run exceeded two runner-specific latency tripwires; those failures were retained, not waived or recalibrated. See [PR #10](https://github.com/dominic-codespoti/dotty/pull/10) for the replacement CI verdict. Benchmark lifecycle method names no longer collide with generated BenchmarkDotNet methods, and parser-construction timing uses a larger sample without changing its operation or limits.

The follow-up warning-as-error build, whitespace verification, **5,893 application tests** and all **142 Skia tests** passed; the existing application golden-file skip remains. Actual X11 captures exposed and fixed double-applied outgoing-row coordinates during smooth scrolling. Before/after checks now verify ordered, contiguous intermediate rows, region clipping and settled content pixels in both directions, plus an upward disabled-mode jump; the changing scrollbar overlay is excluded from the content-pixel comparison. Regressions also cover independent split-pane transitions, metadata-only dirty ranges, range-capacity growth and clearing completed/disabled transforms. The conformance identity policy above is unchanged.

The cross-platform follow-up isolates the split-pane fixture with the existing silent PTY instead of launching a competing shell. Huge-count REP tests retain `int.MaxValue` input but assert exact retained-window contents, cursor/wrap state and scrollback rather than a five-second wall-clock limit. The native GLFW script keeps `CMAKE_BUILD_TYPE=Release` for Linux/macOS and uses Visual Studio's existing build/install `--config Release` on Windows, removing the unused configure-variable warning.

Batch 10 result and verification are summarized above; temporary local report and GUI artifact paths are omitted. The exact suite-name accounting and disposition of all six remaining failures are recorded above and in the current [conformance status](Conformance.md).

## Non-negotiable rules

1. Keep the pinned suite, test coverage and profile stable while fixing behavior. Do not lower the VT level, silently remove tests, rewrite assertions to match Dotty, or blanket-capture the current failures as accepted debt.
2. Treat every unresolved listed case as an open target. A contradictory expectation must be checked against the relevant protocol and a reference terminal. Any proposed exception or profile change requires explicit agreement, recorded evidence and a list of affected cases; it is not a completed implementation fix.
3. Do not impersonate xterm, hard-code another emulator's version or advertise unsupported capabilities to satisfy identity assertions. Resolve identity/profile conflicts openly.
4. Do not enable unsafe clipboard reads, unsolicited replies or unrestricted window control in the product merely to make a headless test pass. Exercise permission-gated behavior through explicit fixture configuration and verify the denied path remains secure.
5. Reply/state queries must report implemented state. Do not add fake mutable mode bits or fixed GUI geometry solely for tests.
6. Preserve currently passing cases and normal application behavior. Changes on parser/buffer hot paths must not add avoidable per-byte/per-cell allocation, copying or work.

## Execution order and dependencies

Start with active coordinate bounds, origin mode and margin-state invariants. Implement the dependent operations against those invariants rather than patching individual expected coordinates. Priorities below are a work order, not a reason to serialize independent slices: palette work can proceed independently; host-policy investigation can proceed while core semantics are repaired. Changes sharing the adapter/buffer need one integration owner.

| Family | Priority | Initial failures | Passed in completed batches | Still failing | Prerequisites |
|---|---|---:|---:|---:|---|
| [G01: Cursor addressing, origin mode and margin bounds](#g01) | P1 | 25 | 25 | 0 | None |
| [G02: Tab-stop state and forward tabulation](#g02) | P1 | 5 | 5 | 0 | G01 |
| [G03: Backspace, carriage return and reverse wrapping](#g03) | P1 | 11 | 11 | 0 | G01 |
| [G04: Line movement and bounded scrolling](#g04) | P2 | 18 | 18 | 0 | G01 |
| [G05: Character/line editing and ordinary erase](#g05) | P2 | 18 | 18 | 0 | G01, G04 |
| [G06: Modes, alternate screens and conformance level](#g06) | P2 | 21 | 21 | 0 | G01 |
| [G07: Save/restore, reset and screen alignment](#g07) | P3 | 11 | 11 | 0 | G01, G06 |
| [G08: DEC column editing and horizontal indexing](#g08) | P3 | 20 | 20 | 0 | G01, G04, G05 |
| [G09: DEC protection and selective erase](#g09) | P3 | 19 | 19 | 0 | G05, G10 |
| [G10: Rectangle copy, fill and erase](#g10) | P3 | 18 | 18 | 0 | G01, G05 |
| [G11: Palette input formats, special colors and resets](#g11) | Parallel | 41 | 41 | 0 | None |
| [G12: Device identity and identification requests](#g12) | P4 / early review | 5 | 1 | 4 | G06 |
| [G13: Mode, device-status and status-string replies](#g13) | P4 | 33 | 31 | 2 | G01, G06, G11 |
| [G14: Clipboard, title state and window operations](#g14) | P4 / early host review | 18 | 18 | 0 | G06 |

## Required specification and profile decisions

These are investigation tasks, not approved exclusions. The affected test rows remain in the inventory.

- **Identity versus xterm literals (G12), reviewed; strict failures retained by user decision:** primary DA under this profile requires model 65 and a broad capability list; secondary DA requires model 64 at VT5 and firmware 314–999. Dotty retains its conservative primary identity and real version. The four DA/DA2 cases remain visibly failing and open: no spoofing, alternate profile, exclusions or baseline exceptions. DECID is fixed; see batch 9.
- **Reverse-wrap version (G01/G03), resolved by user approval:** the initial arguments omitted `--xterm-reverse-wrap`, whose independent default is 0. Four BS/CUB cases consequently selected pre-383 assertions, while `BS_InitialReverseWraparound` expected newer behavior. The approved target explicitly selects version 383 and implements modern modes 45/1045; no tests were removed. The original first-failure messages below remain evidence from the older profile.
- **Edge-index expectations (G08), resolved without a profile change:** the DECBI/DECFI `WholeScreenScrolls` tests retain full-page margins. Their page edges are therefore the active scrolling margins, and the asserted shifts agree with DEC's command descriptions and xterm's implementation. The docstrings are misleading; assertions and inventory remain unchanged. See batch 6 for the source evidence and runtime-verification limit.
- **Color interpretation (G11), resolved:** OSC hash colors use X11 rather than CSS shorthand. Numerical formats use the documented fixed Xcms CRT fallback profile, verified against actual libX11 on 437 inputs; no claim is made for arbitrary calibrated displays. OSC 4 special indices 256–260 agree with the reported 256 indexed colors and alias OSC 5 indices 0–4.
- **Dynamic-color fixture baseline (G11), resolved by user approval:** the upstream reset helper installs black/white overrides, and `ResetSpecialColorTests.test_ResetSpecialColor_Dynamic` assumes the underlying default foreground is black. Only the headless fixture now explicitly sets a black-on-white theme. OSC 110/111 still restore the actual configured theme, not a previous override; product themes and upstream assertions are unchanged. See batch 8 for both pre-adjustment and final evidence.
- **Device/status honesty (G13), reviewed and implemented with two open identity conflicts:** reports use real mutable state or the protocol's unavailable/unsupported status, not invented devices. DECXCPR and keyboard-report arity remain strict failures by explicit user decision because the upstream helper infers a lower level from conservative DA2 identity. See batch 10; these are not exclusions or completed targets.
- **Host state and permission policy (G14), resolved by user approval:** the four original host-dependent cases run unchanged in a real, isolated X11 GUI with explicit fixture permissions. Native state, not constants or clipboard echo, supplies the result. Product clipboard reads/writes and native window mutations remain permission-gated, including verified denied paths.
- **Printer behavior (G13), resolved by user approval:** provide a real, disabled-by-default file printer with no external commands, operational status, print-extent selection and form-feed behavior. A mutable mode bit without output is insufficient; exact native file output was verified.
- **Title-report permission (G07/G14), resolved by user approval:** add `vt.titleReportEnabled`, off by default, and explicitly pass `--title-reports` in the fixture. The affected cases are `RISTests.test_RIS_ResetTitleMode`, the four `SMTitleTests` cases and the two `XtermWinopsTests` label-report cases listed in batch 5. Verify both authorized byte-accurate replies and product-default denial; title text remains independently settable.

## Per-test implementation inventory

Every row below comes from the original `fail` bucket. Completion is recorded in the batch summary and family progress table; unresolved rows remain open targets. Test names link to their actual upstream definition at the pinned commit, including inherited definitions where applicable. The acceptance criterion summarizes the distinguishing behavior; the linked body and helpers remain authoritative for all assertions. A review note means additional evidence is required, not that the test has been waived.

### G01

**Cursor addressing, origin mode and margin bounds**

**Implementation entry points**

- [`src/Dotty.Terminal/Parser/BasicAnsiParser.cs`](../src/Dotty.Terminal/Parser/BasicAnsiParser.cs)
- [`src/Dotty.Terminal/Adapter/TerminalAdapter.cs`](../src/Dotty.Terminal/Adapter/TerminalAdapter.cs)
- [`src/Dotty.Terminal/Adapter/Buffer/TerminalBuffer.cs`](../src/Dotty.Terminal/Adapter/Buffer/TerminalBuffer.cs)
- [`src/Dotty.Terminal/Adapter/Buffer/CursorController.cs`](../src/Dotty.Terminal/Adapter/Buffer/CursorController.cs)

**Work package**

1. Trace parser movement callbacks through adapter coordinate conversion into buffer clamping and origin/margin handling.
2. Investigate consistent 1-based defaults, saturation, and resulting cursor position for absolute, relative, and line-start movement.
3. Candidate changes: adjust movement dispatch or shared buffer coordinate rules only where required by the family assertions.

**Constraints and review**

- Treat cursor coordinate, margins, and origin behavior as a shared foundation for dependent movement and report families.
- The two CUB cases sharing a reverse-wrap profile branch with G03 now use the user-approved `--xterm-reverse-wrap=383` target; their recorded first failures came from the original default-0 profile.

#### CHATests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`CHATests.test_CHA_RespectsOriginMode`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/cha.py#L64) | In origin mode with horizontal margins 5–10, CHA(1) must place the cursor at the left margin; writing X there must appear at screen cell (5,6). | X | — |

#### CNLTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`CNLTests.test_CNL_StopsAtBottomLineWhenBegunBelowScrollRegion`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/cnl.py#L37) | With cursor below the vertical scroll region and horizontal margins 5–10, CNL by screen height must stop at the screen's last row and left margin column 5. | esctypes.TestFailure: Test failed: expected '5' but got '1' | — |
| [`CNLTests.test_CNL_StopsAtBottomMarginInScrollRegion`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/cnl.py#L59) | With cursor inside vertical scroll region rows 2–4 and horizontal margins 5–10, CNL(99) must stop at row 4, column 5. | esctypes.TestFailure: Test failed: expected '4' but got '25' | — |

#### CPLTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`CPLTests.test_CPL_StopsAtTopLineWhenBegunAboveScrollRegion`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/cpl.py#L37) | With cursor above the vertical scroll region and horizontal margins 5–10, CPL by screen height must stop at screen row 1, column 5. | esctypes.TestFailure: Test failed: expected '5' but got '1' | — |
| [`CPLTests.test_CPL_StopsAtTopMarginInScrollRegion`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/cpl.py#L59) | With cursor inside vertical scroll region rows 2–4 and horizontal margins 5–10, CPL(99) must stop at row 2, column 5. | esctypes.TestFailure: Test failed: expected '2' but got '1' | — |

#### CUBTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`CUBTests.test_CUB_AfterNoWrappedInlines`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/cub.py#L82) | After writing two lines that do not wrap, move left by twice the screen width; stop at (1,4) for reverse-wrap version 383 or later, otherwise at (5,3). | esctypes.TestFailure: Test failed: expected 'Point(x=5, y=3)' but got 'Point(x=1, y=5)' | Resolved with the user-approved modern profile, `--xterm-reverse-wrap=383`; the recorded failure used legacy default 0. |
| [`CUBTests.test_CUB_AfterOneWrappedInline`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/cub.py#L103) | After writing wrapped lines and moving left by five screen widths, stop at (1,6) for reverse-wrap version 383 or later, otherwise at (9,3). | esctypes.TestFailure: Test failed: expected 'Point(x=9, y=3)' but got 'Point(x=1, y=8)' | Resolved with the user-approved modern profile, `--xterm-reverse-wrap=383`; the recorded failure used legacy default 0. |
| [`CUBTests.test_CUB_StopsAtLeftMarginInScrollRegion`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/cub.py#L63) | With left-right margins set to columns 5–10 and the cursor at column 7, CUB by 99 positions must stop at column 5. | esctypes.TestFailure: Test failed: expected '5' but got '1' | — |

#### CUDTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`CUDTests.test_CUD_StopsAtBottomMarginInScrollRegion`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/cud.py#L49) | With vertical margins at rows 2–4 and the cursor at row 3, CUD by 99 positions must stop at the bottom margin, row 4. | esctypes.TestFailure: Test failed: expected '4' but got '25' | — |

#### CUFTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`CUFTests.test_CUF_StopsAtRightMarginInScrollRegion`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/cuf.py#L53) | With horizontal margins active and the cursor inside their region, CUF beyond the right boundary must stop at the right margin, column 10. | esctypes.TestFailure: Test failed: expected '10' but got '80' | — |

#### CUPTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`CUPTests.test_CUP_RespectsOriginMode`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/cup.py#L78) | With vertical margins at rows 6–11 and horizontal margins at columns 5–10, CUP (1,1) in origin mode must report relative position (1,1) and place X at screen cell (5,6). | X | — |

#### CUUTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`CUUTests.test_CUU_StopsAtTopMarginInScrollRegion`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/cuu.py#L47) | With the cursor inside the rows 2–4 scrolling region, CUU(99) stops at top-margin row 2 rather than moving above it. | esctypes.TestFailure: Test failed: expected '2' but got '1' | — |

#### HPATests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`HPATests.test_HPA_DefaultParams`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/hpa.py#L9) | HPA with an explicit column must move to that column; HPA with no parameter must move to column 1. | esctypes.TestFailure: Test failed: expected '6' but got '1' | — |
| [`HPATests.test_HPA_DoesNotChangeRow`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/hpa.py#L39) | HPA must move the cursor to the requested column while leaving its current row unchanged. | esctypes.TestFailure: Test failed: expected '2' but got '5' | — |
| [`HPATests.test_HPA_IgnoresOriginMode`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/hpa.py#L51) | With origin mode and horizontal margins active, HPA(2) reports origin-relative column 2, physically column 6 for a left margin of 5. | esctypes.TestFailure: Test failed: expected '2' but got '1' | The test name is misleading: its assertion reads origin-relative CPR, not absolute screen column 2. HPA shares CHA's origin-aware addressing. |
| [`HPATests.test_HPA_StopsAtRightEdge`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/hpa.py#L23) | HPA requested beyond the screen width must stop at the rightmost column and leave the cursor on its original row. | esctypes.TestFailure: Test failed: expected '80' but got '5' | — |

#### HPRTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`HPRTests.test_HPR_DefaultParams`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/hpr.py#L16) | With the cursor at column 6, row 1, HPR with no parameter must move it one column right to column 7. | esctypes.TestFailure: Test failed: expected '7' but got '6' | — |
| [`HPRTests.test_HPR_DoesNotChangeRow`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/hpr.py#L42) | With the cursor at column 5, row 6, HPR(2) must place it at column 7 without changing row 6. | esctypes.TestFailure: Test failed: expected '7' but got '5' | — |
| [`HPRTests.test_HPR_IgnoresOriginMode`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/hpr.py#L55) | With origin mode and margins active, HPR(2) preserves row 7 and advances relative to the cursor; X remains at column 6 and Y appears at column 9. | X Y | — |
| [`HPRTests.test_HPR_StopsAtRightEdge`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/hpr.py#L26) | Moving right beyond the screen width with HPR must clamp the cursor to the last screen column while leaving it on row 6. | esctypes.TestFailure: Test failed: expected '80' but got '5' | — |

#### HVPTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`HVPTests.test_HVP_RespectsOriginMode`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/hvp.py#L78) | With scrolling margins at rows 6–11 and columns 5–10, origin mode makes HVP(1,1) position at that region’s top-left; writing `X` must place it at screen column 5, row 6. | X | — |

#### VPRTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`VPRTests.test_VPR_DefaultParams`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/vpr.py#L14) | VPR with no parameter must move the cursor down exactly one row. | esctypes.TestFailure: Test failed: expected '7' but got '6' | — |
| [`VPRTests.test_VPR_DoesNotChangeColumn`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/vpr.py#L38) | VPR by two rows must move the cursor down two rows without changing its column. | esctypes.TestFailure: Test failed: expected '8' but got '6' | — |
| [`VPRTests.test_VPR_IgnoresOriginMode`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/vpr.py#L49) | With origin mode and scroll and horizontal margins active, VPR by two must place the written Y two rows below X at the asserted absolute screen position. | Y | — |
| [`VPRTests.test_VPR_StopsAtBottomEdge`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/vpr.py#L23) | VPR requested beyond the screen height must stop at the bottom row and leave the cursor in its original column. | esctypes.TestFailure: Test failed: expected '25' but got '6' | — |

### G02

**Tab-stop state and forward tabulation**

**Implementation entry points**

- [`src/Dotty.Terminal/Parser/BasicAnsiParser.cs`](../src/Dotty.Terminal/Parser/BasicAnsiParser.cs)
- [`src/Dotty.Terminal/Adapter/TerminalAdapter.cs`](../src/Dotty.Terminal/Adapter/TerminalAdapter.cs)
- [`src/Dotty.Terminal/Adapter/Buffer/TerminalBuffer.cs`](../src/Dotty.Terminal/Adapter/Buffer/TerminalBuffer.cs)

**Work package**

1. Trace HT, CHT, HTS and TBC through parser callbacks into the shared tab-stop state.
2. Fix omitted/explicit forward-tab counts, default stops, custom stops, clear-all behavior and right-margin clamping.
3. Retain already passing backward-tab behavior while repairing the forward/set/clear operations.

#### CHTTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`CHTTests.test_CHT_ExplicitParameter`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/cht.py#L14) | From the test’s initial cursor position, CHT with parameter 2 must advance to column 17, the second configured forward tab stop. | esctypes.TestFailure: Test failed: expected '17' but got '1' | — |
| [`CHTTests.test_CHT_IgnoresScrollingRegion`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/cht.py#L21) | With horizontal margins 5–30, CHT advances from column 7 to 17, clamps at column 30, and from column 1 with parameter 9 also stops at column 30. | esctypes.TestFailure: Test failed: expected '17' but got '7' | — |
| [`CHTTests.test_CHT_OneTabStopByDefault`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/cht.py#L8) | With the default tab-stop configuration, CHT with its omitted parameter advances the cursor from its initial position to column 9. | esctypes.TestFailure: Test failed: expected '9' but got '1' | — |

#### HTSTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`HTSTests.test_HTS_Basic`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/hts.py#L11) | After TBC(3) clears all stops, HTS at column 20 makes a horizontal tab from column 1 land at column 20. | esctypes.TestFailure: Test failed: expected '20' but got '9' | — |

#### TBCTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`TBCTests.test_TBC_3`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/tbc.py#L33) | After TBC(3) clears all tab stops, setting one at column 30 and tabbing from column 1 must move the cursor to column 30. | esctypes.TestFailure: Test failed: expected '30' but got '9' | — |

### G03

**Backspace, carriage return and reverse wrapping**

**Implementation entry points**

- [`src/Dotty.Terminal/Parser/BasicAnsiParser.cs`](../src/Dotty.Terminal/Parser/BasicAnsiParser.cs)
- [`src/Dotty.Terminal/Adapter/TerminalAdapter.cs`](../src/Dotty.Terminal/Adapter/TerminalAdapter.cs)
- [`src/Dotty.Terminal/Adapter/Buffer/TerminalBuffer.cs`](../src/Dotty.Terminal/Adapter/Buffer/TerminalBuffer.cs)
- [`src/Dotty.Terminal/Adapter/Buffer/CursorController.cs`](../src/Dotty.Terminal/Adapter/Buffer/CursorController.cs)
- [`src/Dotty.Terminal/Adapter/Buffer/BufferTextWriter.cs`](../src/Dotty.Terminal/Adapter/Buffer/BufferTextWriter.cs)

**Work package**

1. Trace backspace and carriage-return controls from parser through adapter to cursor and write/wrap state.
2. Investigate wrap-pending and last-column behavior, including whether controls preserve or clear pending wrap as asserted.
3. Candidate changes: adjust the shared cursor-control or text-write transition at the tested boundary.

**Constraints and review**

- The suite now explicitly selects `--xterm-reverse-wrap=383`, as approved by the user; the original evidence snapshot omitted it. Normal mode 45 and extended mode 1045 have distinct modern semantics.

#### BSTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`BSTests.test_BS_AfterNoWrappedInlines`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/bs.py#L173) | After two non-wrapped lines and sufficient backspaces, the cursor must stop at (1,4) for xterm reverse-wrap version 383 or later, otherwise at (5,3). | esctypes.TestFailure: Test failed: expected 'Point(x=5, y=3)' but got 'Point(x=1, y=5)' | Resolved with the user-approved modern profile, `--xterm-reverse-wrap=383`; the recorded failure used legacy default 0. |
| [`BSTests.test_BS_AfterOneWrappedInline`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/bs.py#L192) | After backspacing across wrapped text, the cursor must end at (1,6) for xterm reverse-wrap version 383 or later, otherwise at (9,3). | esctypes.TestFailure: Test failed: expected 'Point(x=9, y=3)' but got 'Point(x=1, y=8)' | Resolved with the user-approved modern profile, `--xterm-reverse-wrap=383`; the recorded failure used legacy default 0. |
| [`BSTests.test_BS_ReverseWrapGoesToBottom`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/bs.py#L94) | With reverse-wrap enabled and vertical margins at rows 2–5, backspace from the upper-left margin must wrap to the screen's rightmost column on row 5. | esctypes.TestFailure: Test failed: expected 'Point(x=80, y=5)' but got 'Point(x=1, y=2)' | — |
| [`BSTests.test_BS_ReverseWrapStartingInDoWrapPosition`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/bs.py#L160) | Writing at the right-edge do-wrap position, then backspacing and writing X, must leave the final two screen cells on row 1 as aX. | aX | — |
| [`BSTests.test_BS_ReverseWrapWithLeftRight`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/bs.py#L69) | With reverse-wrap and horizontal margins at columns 5–10, backspace from (5,3) must wrap to the right margin at (10,2). | esctypes.TestFailure: Test failed: expected 'Point(x=10, y=2)' but got 'Point(x=4, y=3)' | — |
| [`BSTests.test_BS_ReversewrapFromLeftEdgeToRightMargin`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/bs.py#L80) | With reverse-wrap and horizontal margins at columns 5–10, backspace from screen column 1 at row 3 must wrap to (10,2). | esctypes.TestFailure: Test failed: expected 'Point(x=10, y=2)' but got 'Point(x=1, y=3)' | — |
| [`BSTests.test_BS_StopsAtLeftMargin`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/bs.py#L122) | With horizontal margins at columns 5–10 and reverse-wrap disabled, backspace at the left margin must leave the cursor at (5,1). | esctypes.TestFailure: Test failed: expected 'Point(x=5, y=1)' but got 'Point(x=4, y=1)' | — |
| [`BSTests.test_BS_WrapsInWraparoundMode`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/bs.py#L36) | With auto-wrap and reverse-wrap enabled, backspace at screen position (1,3) must wrap to the last screen column on row 2. | esctypes.TestFailure: Test failed: expected 'Point(x=80, y=2)' but got 'Point(x=1, y=3)' | — |

#### CRTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`CRTests.test_CR_MovesToLeftMarginWhenLeftOfLeftMarginInOriginMode`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/cr.py#L49) | In origin mode with horizontal margins active, CR from left of the left margin must move to that margin, so the subsequent character appears there. | x | — |
| [`CRTests.test_CR_MovesToLeftMarginWhenRightOfLeftMargin`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/cr.py#L17) | With horizontal margins active, CR from right of the left margin must move the cursor to the left margin on the same row. | esctypes.TestFailure: Test failed: expected 'Point(x=5, y=1)' but got 'Point(x=1, y=1)' | — |
| [`CRTests.test_CR_StaysPutWhenAtLeftMargin`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/cr.py#L39) | With horizontal margins active, CR issued when already at the left margin must leave the cursor at that column and row. | esctypes.TestFailure: Test failed: expected 'Point(x=5, y=1)' but got 'Point(x=1, y=1)' | — |

### G04

**Line movement and bounded scrolling**

**Implementation entry points**

- [`src/Dotty.Terminal/Parser/BasicAnsiParser.cs`](../src/Dotty.Terminal/Parser/BasicAnsiParser.cs)
- [`src/Dotty.Terminal/Adapter/TerminalAdapter.cs`](../src/Dotty.Terminal/Adapter/TerminalAdapter.cs)
- [`src/Dotty.Terminal/Adapter/Buffer/TerminalBuffer.cs`](../src/Dotty.Terminal/Adapter/Buffer/TerminalBuffer.cs)
- [`src/Dotty.Terminal/Adapter/Buffer/Screen.cs`](../src/Dotty.Terminal/Adapter/Buffer/Screen.cs)
- [`src/Dotty.Terminal/Graphics/KittyImageStore.Notifications.cs`](../src/Dotty.Terminal/Graphics/KittyImageStore.Notifications.cs)

**Work package**

1. Trace LF, FF, VT, IND, NEL, RI, and scroll commands to buffer line movement and scroll-region operations.
2. Investigate when line movement scrolls the full screen versus the configured region and whether column position is retained or reset.
3. Candidate changes: align the relevant control-to-buffer operation and scrolling boundary with the asserted rows.

#### FFTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`FFTests.test_FF_MovesDoesNotScrollOutsideLeftRight`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/ff.py#L82) | With vertical margins 2–5 and horizontal margins 2–5, FF outside the horizontal margins moves down only within screen bounds, never scrolls, and preserves the marked cell at (3,5). | x | — |
| [`FFTests.test_FF_StopsAtBottomLineWhenBegunBelowScrollRegion`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/ff.py#L124) | With vertical margins 4–5 and the cursor below them at row 6, repeated FF moves to the screen’s last row without scrolling or erasing the marker at (1,6). | esctypes.TestFailure: Test failed: expected '25' but got '6' | — |

#### INDTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`INDTests.test_IND_MovesDoesNotScrollOutsideLeftRight`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/ind.py#L80) | With vertical margins 2–5 and horizontal margins 2–5, IND outside the horizontal margins stays within screen bounds, does not scroll, and preserves the marked cell at (3,5). | x | — |

#### LFTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`LFTests.test_LF_MovesDoesNotScrollOutsideLeftRight`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/lf.py#L80) | With vertical and horizontal margins active, LF outside the horizontal region must neither scroll nor cross the applicable bottom boundary, while preserving the marked cell. | x | — |
| [`LFTests.test_LF_StopsAtBottomLineWhenBegunBelowScrollRegion`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/lf.py#L122) | Starting below the scroll region, repeated LF must move only to the screen's bottom row, not scroll, and leave the marked cell intact. | esctypes.TestFailure: Test failed: expected '25' but got '6' | — |

#### NELTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`NELTests.test_NEL_MovesDoesNotScrollOutsideLeftRight`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/nel.py#L80) | With vertical and horizontal margins active, NEL outside the horizontal region must not scroll; it moves to column 2 only when permitted and preserves the marked cell. | esctypes.TestFailure: Test failed: expected 'Point(x=2, y=5)' but got 'Point(x=1, y=5)' | — |

#### RITests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`RITests.test_RI_MovesDoesNotScrollOutsideLeftRight`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/ri.py#L78) | With vertical rows 2–5 and horizontal columns 2–5 margins, reverse index outside the horizontal region must not move the cursor or scroll, including at screen edges and outside the vertical region. | x | — |
| [`RITests.test_RI_ScrollsInTopBottomRegionStartingBelow`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/ri.py#L49) | Starting below vertical rows 4–5, reverse index must move the cursor to row 4 and then scroll that region down one line, moving its X from row 4 to row 5. | esctypes.TestFailure: Test failed: expected 'Point(x=2, y=4)' but got 'Point(x=2, y=6)' | — |

#### SDTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`SDTests.test_SD_BigScrollLeftRightAndTopBottomScrollRegion`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/sd.py#L178) | Scrolling down 99 lines within horizontal columns 2–4 and vertical rows 2–4 must clear only that rectangular region, leaving all outside cells unchanged. | uvwxy | — |
| [`SDTests.test_SD_LeftRightAndTopBottomScrollRegion`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/sd.py#L158) | Scrolling down two lines in the horizontal columns 2–4 and vertical rows 2–4 region must move its contents down two positions and preserve all outside cells. | uvwxy | — |
| [`SDTests.test_SD_OutsideLeftRightScrollRegion`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/sd.py#L137) | With horizontal margins at columns 2–4, scrolling down two lines from outside that region must scroll only those columns across the screen, preserving the other columns. | vwx | — |
| [`SDTests.test_SD_RespectsLeftRightScrollRegion`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/sd.py#L119) | With horizontal margins at columns 2–4, scrolling down two lines from inside that region must scroll only those columns across the screen and preserve the outside columns. | ulmny | — |

#### SUTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`SUTests.test_SU_BigScrollLeftRightAndTopBottomScrollRegion`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/su.py#L166) | SU(99) scrolls the intersection of columns 2–4 and rows 2–4 upward by the region height, blanking that intersection while preserving all surrounding screen cells. | uvwxy | — |
| [`SUTests.test_SU_LeftRightAndTopBottomScrollRegion`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/su.py#L146) | SU(2) with cursor outside the combined margins scrolls only columns 2–4 within rows 2–4 upward twice, blanking exposed cells and preserving the surrounding screen. | uvwxy | — |
| [`SUTests.test_SU_OutsideLeftRightScrollRegion`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/su.py#L127) | With only left-right margins set, SU(2) scrolls columns 2–4 upward across the screen rows, blanking exposed cells while preserving columns 1 and 5. | u y | — |
| [`SUTests.test_SU_RespectsLeftRightScrollRegion`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/su.py#L109) | With only left-right margins set, SU(2) scrolls columns 2–4 upward across the screen rows even when the cursor is inside those margins, preserving columns 1 and 5. | u y | — |

#### VTTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`VTTests.test_VT_MovesDoesNotScrollOutsideLeftRight`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/vt.py#L80) | With rows 2–5 and columns 2–5 as margins, VT outside the horizontal margins never scrolls: it stops at the applicable bottom boundary, moves down when possible, and preserves the marked cell. | x | — |
| [`VTTests.test_VT_StopsAtBottomLineWhenBegunBelowScrollRegion`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/vt.py#L122) | Starting below the rows 4–5 scrolling region, repeated VT moves the cursor only to the screen's final row and does not scroll or erase the marker at row 6. | esctypes.TestFailure: Test failed: expected '25' but got '6' | — |

### G05

**Character/line editing and ordinary erase**

**Implementation entry points**

- [`src/Dotty.Terminal/Parser/BasicAnsiParser.cs`](../src/Dotty.Terminal/Parser/BasicAnsiParser.cs)
- [`src/Dotty.Terminal/Adapter/TerminalAdapter.cs`](../src/Dotty.Terminal/Adapter/TerminalAdapter.cs)
- [`src/Dotty.Terminal/Adapter/Buffer/TerminalBuffer.cs`](../src/Dotty.Terminal/Adapter/Buffer/TerminalBuffer.cs)
- [`src/Dotty.Terminal/Adapter/Buffer/BufferEraser.cs`](../src/Dotty.Terminal/Adapter/Buffer/BufferEraser.cs)
- [`src/Dotty.Terminal/Adapter/Buffer/BufferTextWriter.cs`](../src/Dotty.Terminal/Adapter/Buffer/BufferTextWriter.cs)

**Work package**

1. Trace character/line insertion, deletion, erasure, display erasure, and REP to their buffer primitives.
2. Investigate clipping at row and screen boundaries, vacated-cell contents, and REP’s previous-character and repeat-count behavior.
3. Implement or correct ISO SPA/EPA protected-cell semantics for ordinary ECH, ED and EL; keep them distinct from DEC selective protection.
4. Candidate changes: update the owning edit primitive or parser default/count dispatch for the observed assertion boundary.

#### DCHTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`DCHTests.test_DCH_DeleteAllWithMargins`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/dch.py#L42) | With horizontal margins at columns 2–4, deleting 99 characters from column 3 must erase through column 4 only, leaving columns 1–2 and 5 unchanged. | ab e | — |
| [`DCHTests.test_DCH_DoesNothingOutsideLeftRightMargin`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/dch.py#L55) | With horizontal margins at columns 2–4, deleting characters from column 1 must leave the entire row unchanged. | abcde | — |
| [`DCHTests.test_DCH_RespectsMargins`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/dch.py#L29) | With horizontal margins at columns 2–4, deleting one character from column 3 must shift the remaining in-margin content left and blank column 4, without changing columns 1 or 5. | abd e | — |

#### DLTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`DLTests.test_DL_ClearOutLeftRightAndTopBottomScrollRegion`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/dl.py#L198) | With horizontal margins at columns 2–4 and vertical margins at rows 2–4, delete more lines than the region contains; only columns 2–4 in rows 2–4 become blank, with outside cells unchanged. | uvwxy | — |
| [`DLTests.test_DL_InLeftRightAndTopBottomScrollRegion`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/dl.py#L178) | With horizontal margins at columns 2–4 and vertical margins at rows 2–4, delete one line at row 2; shift the bounded columns upward within rows 2–4 and blank the bottom bounded cells. | uvwxy | — |
| [`DLTests.test_DL_InLeftRightScrollRegion`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/dl.py#L142) | With horizontal margins at columns 2–4, delete one line at row 2; shift only those columns upward through the screen and blank their bottom cells, preserving columns 1 and 5. | u y | — |
| [`DLTests.test_DL_OutsideLeftRightAndTopBottomScrollRegion`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/dl.py#L218) | With both margin types set, delete lines while the cursor is outside the bounded region; the entire prepared 5-by-5 character area must remain unchanged. | uvwxy | — |
| [`DLTests.test_DL_OutsideLeftRightScrollRegion`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/dl.py#L160) | With horizontal margins at columns 2–4, delete a line while the cursor is outside those margins; the entire prepared 5-by-5 character area must remain unchanged. | uvwxy | — |
| [`DLTests.test_DL_OutsideScrollRegion`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/dl.py#L124) | With vertical margins at rows 2–4, delete a line while the cursor is above the scroll region; the entire prepared 5-by-5 character area must remain unchanged. | uvwxy | — |

#### ECHTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`ECHTests.test_ECH_respectsISOProtection`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/ech.py#L73) | After marking only c protected with SPA/EPA, ECH over columns 1–3 must erase a and b while preserving c. | c | — |

#### EDTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`EDTests.test_ED_respectsISOProtection`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/ed.py#L179) | ED(0) clears the unprotected `a` and `b` cells but preserves the character `c` written between SPA and EPA. | c | — |

#### ELTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`ELTests.test_EL_respectsISOProtection`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/el.py#L82) | After marking `c` protected with SPA/EPA in a row containing `abc`, EL(2) from column 1 must erase the unprotected `a` and `b` while preserving `c`. | c | — |

#### ICHTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`ICHTests.test_ICH_IsNoOpWhenCursorBeginsOutsideScrollRegion`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/ich.py#L43) | With left-right margins at columns 2–5, ICH(10) from column 1 outside the margins must leave the existing `abcdefg` unchanged. | abcdefg | — |
| [`ICHTests.test_ICH_ScrollOffRightMarginInScrollRegion`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/ich.py#L100) | With left-right margins at columns 2–5, inserting one blank at column 3 must shift characters only within that region, dropping the character beyond its right edge; the checked row becomes `ab`, blank, `cdfg`. | ab cdfg | — |

#### ILTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`ILTests.test_IL_AboveScrollRegion`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/il.py#L124) | IL at row 1, above the configured scrolling region, is a no-op and leaves all five prepared screen rows unchanged. | uvwxy | — |
| [`ILTests.test_IL_RespectsScrollRegion`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/il.py#L90) | With both horizontal and vertical margins at 2–4, IL at row 2 shifts only columns 2–4 downward through row 4 and blanks their exposed cells, preserving outside rows and columns. | uvwxy | — |
| [`ILTests.test_IL_RespectsScrollRegion_Over`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/il.py#L108) | IL(99) within the rows 2–4, columns 2–4 rectangle clears that rectangle when the count exceeds its height, leaving outside rows and columns unchanged. | uvwxy | — |

#### REPTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`REPTests.test_REP_RespectsLeftRightMargins`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/rep.py#L33) | With margins 2–4, printing a at (2,1) then REP(3) writes aaa across row 1 and wraps the final a to (2,2). | a | — |

### G06

**Modes, alternate screens and conformance level**

**Implementation entry points**

- [`src/Dotty.Terminal/Parser/BasicAnsiParser.cs`](../src/Dotty.Terminal/Parser/BasicAnsiParser.cs)
- [`src/Dotty.Terminal/Adapter/TerminalAdapter.cs`](../src/Dotty.Terminal/Adapter/TerminalAdapter.cs)
- [`src/Dotty.Terminal/Adapter/TerminalAdapter.SoftReset.cs`](../src/Dotty.Terminal/Adapter/TerminalAdapter.SoftReset.cs)
- [`src/Dotty.Terminal/Adapter/Buffer/TerminalBuffer.cs`](../src/Dotty.Terminal/Adapter/Buffer/TerminalBuffer.cs)
- [`src/Dotty.Terminal/Adapter/Buffer/TerminalBuffer.Reset.cs`](../src/Dotty.Terminal/Adapter/Buffer/TerminalBuffer.Reset.cs)
- [`src/Dotty.Terminal/Adapter/TerminalAdapter.ColumnModes.cs`](../src/Dotty.Terminal/Adapter/TerminalAdapter.ColumnModes.cs)
- [`src/Dotty.Terminal/Adapter/Buffer/BufferTextWriter.cs`](../src/Dotty.Terminal/Adapter/Buffer/BufferTextWriter.cs)
- [`src/Dotty.Terminal/Adapter/Buffer/ScreenManager.cs`](../src/Dotty.Terminal/Adapter/Buffer/ScreenManager.cs)
- [`src/Dotty.Terminal/Adapter/Buffer/TerminalBuffer.Reflow.cs`](../src/Dotty.Terminal/Adapter/Buffer/TerminalBuffer.Reflow.cs)
- [`src/Dotty.Runtime/Input/TerminalInputEncoder.cs`](../src/Dotty.Runtime/Input/TerminalInputEncoder.cs)
- [`src/Dotty/Input/TerminalKeyboardDispatcher.Keyboard.cs`](../src/Dotty/Input/TerminalKeyboardDispatcher.Keyboard.cs)

**Work package**

1. Trace private and ANSI mode parsing into state setters, alternate-screen switching, and reset handling.
2. Investigate mode state and persistence across DECSTR/RIS, alternate-screen variants, and conformance-level requests against the assertions.
3. Candidate changes: revise only the specific mode transition or reset-state ownership needed for the observed behavior.

**Constraints and review**

- Do not make ignored or unsupported modes report fabricated active behavior; preserve explicit conformance boundaries.

#### DECSCLTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`DECSCLTests.test_DECSCL_Level2DoesntSupportDECRQM`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decscl.py#L28) | At VT level 2, DECRQM is unsupported: requesting a mode report must fail to produce a DECRQM response rather than succeed. | esctypes.TestFailure: Test failed: expected 'True' but got 'False' | — |
| [`DECSCLTests.test_DECSCL_Level4_SupportsDECSLRMDoesntSupportDECNCSM`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decscl.py#L76) | At VT level 4, DECCOLM clears the screen despite DECNCSM being set, and DECSLRM is supported so writing from its left margin advances the cursor within the horizontal margins. | Expected: | — |

#### DECSETTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`DECSETTests.test_DECSET_ALTBUF`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decset.py#L461) | DECSET 47 enters the alternate screen without erasing its existing contents, and DECRESET 47 returns to the main screen without clearing the alternate screen. | def | — |
| [`DECSETTests.test_DECSET_Allow80To132`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decset.py#L233) | When Allow80To132 is enabled, DECCOLM changes 80 columns to 132 and back; when disabled, both attempted width changes leave the current width unchanged. | esctypes.TestFailure: Test failed: expected '132' but got '80' | — |
| [`DECSETTests.test_DECSET_DECAWM_NoLineWrapOnTabWithLeftRightMargin`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decset.py#L298) | With autowrap, left-right margins 10–20, and the cursor at column 1, successive tabs stop at columns 9, 17, then 20, and further tabs remain at 20. | esctypes.TestFailure: Test failed: expected 'Point(x=20, y=1)' but got 'Point(x=25, y=1)' | — |
| [`DECSETTests.test_DECSET_DECAWM_OffRespectsLeftRightMargin`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decset.py#L220) | With autowrap off and margins 5–9, writing abcdef from (8,9) leaves a at (8,9), f at (9,9), and the cursor at column 9. | esctypes.TestFailure: Test failed: expected '9' but got '14' | — |
| [`DECSETTests.test_DECSET_DECAWM_OnRespectsLeftRightMargin`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decset.py#L207) | With autowrap on and margins at columns 5–9, writing six characters from column 8, row 9 writes the first two at columns 8–9 and wraps the remaining four to the next row within the margins. | cdef | — |
| [`DECSETTests.test_DECSET_DECCOLM`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decset.py#L65) | With 80-to-132 mode allowed and no-clear-on-column-change reset, DECCOLM switches to 132 columns, clears the screen, homes the cursor, and resets the scrolling and horizontal margins. | esctypes.TestFailure: Test failed: expected '132' but got '80' | — |
| [`DECSETTests.test_DECSET_DECLRMM`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decset.py#L476) | With left-right margins 2–4 enabled, text is confined to those columns; after disabling the mode, writing from column 1 proceeds across the full screen width. | h | — |
| [`DECSETTests.test_DECSET_DECNCSM`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decset.py#L522) | When no-clear-on-column-change is set, setting or resetting DECCOLM preserves screen contents; when it is reset, either column-mode change clears the screen. | Expected: | The test requires xterm window-operation support to change columns; preserve that host prerequisite rather than treating unavailable resizing as terminal behavior. |
| [`DECSETTests.test_DECSET_DECOM`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decset.py#L112) | With rows and columns 5–7 as margins and origin mode enabled, CUP(1,1) writes X at absolute (5,5), confirmed after the margins are reset. | X | — |
| [`DECSETTests.test_DECSET_DECOM_DECRQCRA`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decset.py#L149) | After enabling origin mode with margins at rows and columns 5–7, a rectangle checksum request for (1,1) addresses the previously written X at absolute screen position (5,5). | X | — |
| [`DECSETTests.test_DECSET_MoreFix`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decset.py#L318) | With MoreFix enabled, filling a line then tabbing places the cursor at the first tab stop and a following character there; with it disabled, tabbing leaves the cursor at the right margin. | esctypes.TestFailure: Test failed: expected '9' but got '80' | — |
| [`DECSETTests.test_DECSET_ReverseWraparoundLastCol_BS`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decset.py#L350) | With reverse-wraparound and autowrap enabled, after writing through the last column, backspace at the pending-wrap position leaves the cursor in the last column. | esctypes.TestFailure: Test failed: expected '80' but got '79' | — |
| [`DECSETTests.test_DECSET_ReverseWraparound_BS`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decset.py#L339) | With reverse-wraparound and autowrap enabled, backspace from column 1 of row 2 moves the cursor to the final column of the preceding row. | esctypes.TestFailure: Test failed: expected '80' but got '1' | — |
| [`DECSETTests.test_DECSET_ReverseWraparound_Multi`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decset.py#L365) | After writing across the right edge with autowrap and enabling reverse-wraparound, moving backward four columns from the resulting cursor lands at the penultimate screen column. | esctypes.TestFailure: Test failed: expected '79' but got '1' | — |

#### DECSETTiteInhibitTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`DECSETTiteInhibitTests.test_SaveRestoreCursor_AltVsMain`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decset_tite_inhibit.py#L34) | DEC mode 1048 save/restore maintains independent screen-local cursor states: restore (2,3) on the main screen and (6,7) on the alternate screen. | esctypes.TestFailure: Test failed: expected 'Point(x=2, y=3)' but got 'Point(x=6, y=7)' | The class name does not enable an xterm titeInhibit resource; validate mode 1048 semantics and any product policy separately. |
| [`DECSETTiteInhibitTests.test_SaveRestoreCursor_ResetsOriginMode`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decset_tite_inhibit.py#L26) | After saving with DECSET 1048 while origin mode is off, DECRESET 1048 restores that mode state so CUP(1,1) writes at the true screen origin. | X | Validate mode 1048 behavior against the actual product alternate-screen/save policy; do not infer an enabled resource from the test class name. |

#### SMTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`SMTests.test_SM_IRM`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/sm.py#L18) | After writing `abc`, positioning at column 1, and enabling insert mode, writing `X` must leave `Xabc` in columns 1–4. | Xabc | — |
| [`SMTests.test_SM_IRM_TruncatesAtRightMargin`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/sm.py#L44) | With left-right margins at columns 5–10, inserting `X` at column 7 must shift the margin-bounded text right and discard overflow at the right margin, yielding `abXcde` followed by one blank in the checked columns. | abXcde | — |
| [`SMTests.test_SM_LNM`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/sm.py#L74) | With linefeed mode enabled, LF, VT, and FF from column 5, row 1 must each index to row 2 and perform carriage return to column 1; with the mode reset, each preserves column 5. | esctypes.TestFailure: Test failed: expected 'Point(x=1, y=2)' but got 'Point(x=5, y=2)' | — |

### G07

**Save/restore, reset and screen alignment**

**Implementation entry points**

- [`src/Dotty.Terminal/Parser/BasicAnsiParser.cs`](../src/Dotty.Terminal/Parser/BasicAnsiParser.cs)
- [`src/Dotty.Terminal/Adapter/TerminalAdapter.cs`](../src/Dotty.Terminal/Adapter/TerminalAdapter.cs)
- [`src/Dotty.Terminal/Adapter/TerminalAdapter.SoftReset.cs`](../src/Dotty.Terminal/Adapter/TerminalAdapter.SoftReset.cs)
- [`src/Dotty.Terminal/Adapter/Buffer/TerminalBuffer.cs`](../src/Dotty.Terminal/Adapter/Buffer/TerminalBuffer.cs)
- [`src/Dotty.Terminal/Adapter/Buffer/TerminalBuffer.Reset.cs`](../src/Dotty.Terminal/Adapter/Buffer/TerminalBuffer.Reset.cs)

**Work package**

1. Trace save/restore cursor, RIS, DECSTR, and alignment-test dispatch to adapter and buffer reset/save state.
2. Investigate which cursor attributes, modes, screen contents, and parser state each operation is required to preserve or reset.
3. Candidate changes: align the relevant reset or save/restore path without conflating soft reset with full reset.

**Constraints and review**

- Check reset scope against the specific behavior asserted; DECSTR and RIS intentionally differ.

#### DECALNTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`DECALNTests.test_DECALN_FillsScreen`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decaln.py#L14) | DECALN fills the screen with E; each of the four corners and the center cell must contain E. | E | — |
| [`DECALNTests.test_DECALN_MovesCursorHome`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decaln.py#L32) | After positioning the cursor at (5,5), DECALN must place it at the home position (1,1). | esctypes.TestFailure: Test failed: expected 'Point(x=1, y=1)' but got 'Point(x=6, y=5)' | — |

#### DECRCTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`DECRCTests.test_SaveRestoreCursor_AltVsMain`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/save_restore_cursor.py#L119) | DECSC/DECRC saved cursor positions must be independent per screen: restoring on main returns to (2,3), and restoring on alternate returns to (6,7). | esctypes.TestFailure: Test failed: expected 'Point(x=2, y=3)' but got 'Point(x=6, y=7)' | — |
| [`DECRCTests.test_SaveRestoreCursor_ResetsOriginMode`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/save_restore_cursor.py#L72) | After saving the cursor, enabling origin mode and restoring with DECRC must reset origin mode, so CUP to (1,1) writes X at the true screen origin. | X | — |

#### RISTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`RISTests.test_RIS_ResetDECCOLM`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/ris.py#L83) | After enabling 80/132-column switching and setting 132 columns, full reset must restore the terminal width to 80 columns. | esctypes.TestFailure: Test failed: expected '132' but got '80' | — |
| [`RISTests.test_RIS_ResetTitleMode`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/ris.py#L50) | After full reset, setting and querying window and icon titles in the configured UTF-8/hex modes must round-trip the strings ab and a exactly. | esctypes.InternalError: Timeout waiting to read. | — |

#### SCORCTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`SCORCTests.test_SaveRestoreCursor_AltVsMain`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/save_restore_cursor.py#L119) | Saving cursor positions on the main and alternate screens maintains separate saved state: returning to each screen and restoring its cursor recovers that screen’s saved position. | esctypes.TestFailure: Test failed: expected 'Point(x=2, y=3)' but got 'Point(x=6, y=7)' | — |
| [`SCORCTests.test_SaveRestoreCursor_ResetsOriginMode`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/save_restore_cursor.py#L72) | Restoring the saved cursor resets origin mode; after margins are removed, CUP(1,1) and writing X place it at the screen’s true upper-left cell. | X | — |
| [`SCORCTests.test_SaveRestoreCursor_WorksInLRM`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/scorc.py#L16) | CSI s while left-right-margin mode is active must not overwrite the earlier saved cursor; CSI u restores (2,3), not the later (5,6). | esctypes.TestFailure: Test failed: expected 'Point(x=2, y=3)' but got 'Point(x=5, y=6)' | Distinguish overloaded CSI s margin-setting semantics from ESC 7/8 and DEC mode 1048 save/restore. |

#### XtermSaveTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`XtermSaveTests.test_XtermSave_SaveResetState`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/xterm_save.py#L33) | After saving DECAWM while disabled, enabling it, and restoring the saved state, writing across the right edge leaves the cursor at the final column without wrapping. | esctypes.TestFailure: Test failed: expected '80' but got '2' | — |
| [`XtermSaveTests.test_XtermSave_SaveSetState`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/xterm_save.py#L11) | After saving DECAWM while enabled, disabling it, and restoring the saved state, writing across the right edge wraps the cursor to column 2 of the next row. | esctypes.TestFailure: Test failed: expected '2' but got '80' | — |

### G08

**DEC column editing and horizontal indexing**

**Implementation entry points**

- [`src/Dotty.Terminal/Parser/BasicAnsiParser.cs`](../src/Dotty.Terminal/Parser/BasicAnsiParser.cs)
- [`src/Dotty.Terminal/Adapter/TerminalAdapter.cs`](../src/Dotty.Terminal/Adapter/TerminalAdapter.cs)
- [`src/Dotty.Terminal/Adapter/Buffer/TerminalBuffer.cs`](../src/Dotty.Terminal/Adapter/Buffer/TerminalBuffer.cs)

**Work package**

1. Completed VT4+ parser/adapter dispatch and shared native column-region mutation for all four commands.
2. Verified both-axis eligibility, full-page/default-margin shifts, pending-wrap transitions, soft-wrap link invalidation and unchanged history.
3. Added horizontal Kitty placement containment/cropping and behavioral regressions; all 20 targets pass in batch 6.

**Constraints and review**

- Do not infer complete support from neighboring ordinary insert/delete commands; verify DEC operation semantics separately.
- The `WholeScreenScrolls` comment discrepancy is resolved: reset leaves full-page margins, so the page edge is the active margin and the asserted shift is correct. No assertion, profile or annotation was changed.

#### DECBITests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`DECBITests.test_DECBI_Basic`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decbi.py#L18) | DECBI at column 5, row 6 must move the cursor one column left to column 4 on the same row. | esctypes.TestFailure: Test failed: expected 'Point(x=4, y=6)' but got 'Point(x=5, y=6)' | — |
| [`DECBITests.test_DECBI_LeftOfMargin`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decbi.py#L62) | With left-right margins at columns 3–5 and the cursor at column 2, DECBI must move left to column 1 on row 1. | esctypes.TestFailure: Test failed: expected 'Point(x=1, y=1)' but got 'Point(x=2, y=1)' | — |
| [`DECBITests.test_DECBI_Scrolls`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decbi.py#L33) | At the left margin, DECBI shifts columns 3–5 right by one within rows 4–6, blanks column 3 and preserves surrounding rows and columns. | uvwxy | — |
| [`DECBITests.test_DECBI_WholeScreenScrolls`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decbi.py#L75) | After writing x at (1,1), DECBI at the left screen edge must satisfy the asserted first two cells: a blank followed by x. | x | Resolved: default full-page margins make this the active left margin; DEC and xterm require the asserted right shift. The misleading docstring does not change the assertion. |

#### DECDCTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`DECDCTests.test_DECDC_CursorWithinTopBottom`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decdc.py#L67) | DECDC deletes columns only within the active top-bottom scrolling region, shifting its affected row contents while leaving rows outside that region unchanged. | ZYXWVUT | — |
| [`DECDCTests.test_DECDC_DefaultParam`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decdc.py#L33) | DECDC with its default count deletes one character at the cursor in each affected row, shifting remaining characters left and blanking the final column. | ACDEFG | — |
| [`DECDCTests.test_DECDC_DeleteAll`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decdc.py#L124) | A DECDC count larger than the remaining row width deletes all characters to the right of the cursor, blanks those cells, and does not wrap into another row. | A | — |
| [`DECDCTests.test_DECDC_DeleteAllWithLeftRightMargins`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decdc.py#L173) | With horizontal margins at columns 2–5, a large DECDC count from column 3 blanks the remainder of that margin region on each row while preserving characters outside it. | AB FG | — |
| [`DECDCTests.test_DECDC_DeleteWithLeftRightMargins`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decdc.py#L145) | With horizontal margins at columns 2–5, DECDC from column 3 deletes one cell within the margin on each affected row, shifts the remaining in-margin characters left, and preserves outside-margin cells. | ABDE FG | — |
| [`DECDCTests.test_DECDC_ExplicitParam`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decdc.py#L49) | DECDC(2) from column 2 deletes two characters on the cursor row and the rows above and below, shifting each affected row left and blanking its final two cells. | zwvut | — |

#### DECFITests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`DECFITests.test_DECFI_Basic`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decfi.py#L40) | DECFI at cursor position (5,6) advances the cursor one column to (6,6). | esctypes.TestFailure: Test failed: expected 'Point(x=6, y=6)' but got 'Point(x=5, y=6)' | — |
| [`DECFITests.test_DECFI_RightOfMargin`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decfi.py#L85) | With horizontal margins at columns 3–5, DECFI from column 6, outside the right margin, advances the cursor to column 7. | esctypes.TestFailure: Test failed: expected 'Point(x=7, y=1)' but got 'Point(x=6, y=1)' | — |
| [`DECFITests.test_DECFI_Scrolls`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decfi.py#L56) | DECFI at the right edge of a defined horizontal and vertical scrolling region shifts its contents left by one column, blanks the region’s rightmost cells, and preserves rows outside the region. | uvwxy | — |
| [`DECFITests.test_DECFI_WholeScreenScrolls`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decfi.py#L96) | At the right screen edge, DECFI must satisfy the asserted last two cells: x moves to the penultimate column and the last column becomes empty. | x | Resolved: default full-page margins make this the active right margin; DEC and xterm require the asserted left shift. The misleading docstring does not change the assertion. |

#### DECICTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`DECICTests.test_DECIC_CursorWithinTopBottom`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decic.py#L51) | With horizontal margins 1–20 and vertical scroll region rows 2–3, DECIC(2) at row 2, column 2 must insert two blanks only on rows 2–3, preserving rows 1 and 4. | ZYXWVUT | — |
| [`DECICTests.test_DECIC_DefaultParam`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decic.py#L17) | DECIC with its default count at column 2 must insert one blank at that column on both populated rows, shifting the remaining characters right. | A BCDEFG | — |
| [`DECICTests.test_DECIC_ExplicitParam`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decic.py#L33) | DECIC(2) at column 2 must insert two blanks and shift text right on the cursor row and the rows immediately above and below it. | z yxwvut | — |
| [`DECICTests.test_DECIC_ScrollEntirelyOffRightEdge`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decic.py#L126) | DECIC(width) at the first column of two full-width rows must leave both rows blank and must not wrap displaced characters onto another row. | Expected: | — |
| [`DECICTests.test_DECIC_ScrollOffRightEdge`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decic.py#L105) | DECIC at one column past the start of a right-edge string must insert one blank on each affected row, shift text right, discard overflow, and not wrap. | A BCDEF | — |
| [`DECICTests.test_DECIC_ScrollOffRightMarginInScrollRegion`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decic.py#L147) | With horizontal margins 2–5, DECIC at column 3 must insert a blank within the margin on both rows, shift cells right, and drop the character displaced beyond the right margin. | AB CDFG | — |

### G09

**DEC protection and selective erase**

**Complete:** all 19 original targets pass. Batch 7 resolves the six remaining DECSERA cases; the earlier DECSED/DECSEL fixes remain passing.

**Implementation entry points**

- [`src/Dotty.Terminal/Parser/BasicAnsiParser.cs`](../src/Dotty.Terminal/Parser/BasicAnsiParser.cs)
- [`src/Dotty.Terminal/Adapter/TerminalAdapter.cs`](../src/Dotty.Terminal/Adapter/TerminalAdapter.cs)
- [`src/Dotty.Terminal/Adapter/Buffer/TerminalBuffer.cs`](../src/Dotty.Terminal/Adapter/Buffer/TerminalBuffer.cs)
- [`src/Dotty.Terminal/Adapter/Buffer/BufferEraser.cs`](../src/Dotty.Terminal/Adapter/Buffer/BufferEraser.cs)
- [`src/Dotty.Terminal/Adapter/Buffer/CellAttributes.cs`](../src/Dotty.Terminal/Adapter/Buffer/CellAttributes.cs)
- [`src/Dotty.Terminal/Adapter/Buffer/TerminalBuffer.Rectangles.cs`](../src/Dotty.Terminal/Adapter/Buffer/TerminalBuffer.Rectangles.cs)
- [`src/Dotty.Terminal/Adapter/Buffer/Screen.Rectangles.cs`](../src/Dotty.Terminal/Adapter/Buffer/Screen.Rectangles.cs)

**Work package**

1. Inspect cell attributes and erase primitives for protected-cell representation and selective erase support.
2. Distinguish DEC DECSCA protection from ISO SPA/EPA protection: DECSERA must not accidentally respect the ISO-only protected flag.
3. Trace DECSED, DECSEL, and DECSERA parsing to determine whether parameters and rectangle bounds reach an implementation.
4. Candidate changes: add or revise protected-cell state and selective erase traversal only as required by the asserted regions.

**Constraints and review**

- Avoid treating unrelated rendition attributes as protected-cell state.
- Ordinary ISO-protected erase is addressed in G05; selective DEC erase and rectangle behavior must not conflate the two protection mechanisms.

#### DECSEDTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`DECSEDTests.test_DECSED_0_Protection`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decsed.py#L220) | DECSED(0) erases the unprotected X at row 5, column 2 and other unprotected cells from the cursor through screen end, while preserving protected text and cells before the cursor. | e | — |
| [`DECSEDTests.test_DECSED_0_WithScrollRegion_Protection`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decsed.py#L311) | DECSED(0) ignores the configured scrolling margins and erases unprotected cells from the cursor through screen end, preserving protected text and the preceding rows. | lmno | — |
| [`DECSEDTests.test_DECSED_1_Protection`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decsed.py#L244) | DECSED(1) erases the unprotected X at row 1, column 2 and other unprotected cells from screen start through the cursor, preserving protected text and cells after it. | e | — |
| [`DECSEDTests.test_DECSED_1_WithScrollRegion_Protection`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decsed.py#L342) | DECSED(1) ignores the configured scrolling margins and erases unprotected cells from screen start through the cursor, preserving protected text and subsequent rows. | klmno | — |
| [`DECSEDTests.test_DECSED_2_Protection`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decsed.py#L266) | DECSED(2) erases all unprotected cells across the screen while retaining protected text. | e | — |
| [`DECSEDTests.test_DECSED_2_WithScrollRegion_Protection`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decsed.py#L373) | DECSED(2) erases unprotected cells across the entire screen, ignoring configured scrolling margins and retaining protected text. | klmno | — |
| [`DECSEDTests.test_DECSED_DECSCA_2`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decsed.py#L198) | DECSCA(2) makes newly written cells unprotected like DECSCA(0), so default DECSED erases the X while preserving protected text. | e | — |
| [`DECSEDTests.test_DECSED_Default_Protection`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decsed.py#L176) | Default DECSED behaves like DECSED(0): it erases unprotected cells from the cursor through screen end while preserving protected text and preceding cells. | e | — |

#### DECSELTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`DECSELTests.test_DECSEL_0_Protection`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decsel.py#L92) | Selective erase to end of line from column 5 must preserve protected text in columns 1–9 while erasing the unprotected X at column 10. | abcdefghi | — |
| [`DECSELTests.test_DECSEL_1_Protection`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decsel.py#L111) | Selective erase from start of line through the cursor must erase the unprotected X at column 1 while preserving protected text in columns 2–10. | bcdefghij | — |
| [`DECSELTests.test_DECSEL_2_Protection`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decsel.py#L129) | Selective erase of the entire line must erase the unprotected X at column 1 while preserving protected text in columns 2–10. | bcdefghij | — |
| [`DECSELTests.test_DECSEL_Default_Protection`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decsel.py#L74) | Default selective erase from column 5 to line end must preserve protected text in columns 1–9 and erase the unprotected X at column 10. | abcdefghi | — |
| [`DECSELTests.test_DECSEL_IgnoresScrollRegion_Protection`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decsel.py#L146) | Selective erase of the line must erase the unprotected X at column 1 and preserve protected text in columns 2–10, even with horizontal margins set. | bcdefghij | — |

#### DECSERATests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`DECSERATests.test_DECSERA_basic`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decsera.py#L47) | Erase the test helper’s specified rectangle, clearing its unprotected cells while preserving protected cells and leaving cells outside the rectangle unchanged. | YZ6789!@ | — |
| [`DECSERATests.test_DECSERA_defaultArgs`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decsera.py#L54) | Apply DECSERA with omitted rectangle parameters and verify the default-argument coverage produces blank cells throughout the expected screen area. | Expected: | — |
| [`DECSERATests.test_DECSERA_doesNotRespectISOProtect`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decsera.py#L80) | After marking the two cells with ISO protection, erase the one-row, two-column rectangle with DECSERA; both cells must be blank. | Expected: | — |
| [`DECSERATests.test_DECSERA_ignoresMargins`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decsera.py#L73) | Erase the helper’s specified rectangle while margins are configured; the requested rectangle must be erased regardless of those margins. | YZ6789!@ | — |
| [`DECSERATests.test_DECSERA_overlyLargeSourceClippedToScreenSize`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decsera.py#L66) | Request a DECSERA rectangle extending beyond the screen; clip the erase to screen bounds and verify the in-bounds portion is cleared. | c | — |
| [`DECSERATests.test_DECSERA_respectsOriginMode`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decsera.py#L62) | With origin mode enabled, interpret DECSERA’s rectangle coordinates relative to the configured origin and erase the expected in-region cells. | YZ6789!@ | — |

### G10

**Rectangle copy, fill and erase**

**Complete in batch 7:** all 18 original targets pass, alongside the six dependent G09 DECSERA targets.

**Implementation entry points**

- [`src/Dotty.Terminal/Parser/BasicAnsiParser.cs`](../src/Dotty.Terminal/Parser/BasicAnsiParser.cs)
- [`src/Dotty.Terminal/Adapter/TerminalAdapter.cs`](../src/Dotty.Terminal/Adapter/TerminalAdapter.cs)
- [`src/Dotty.Terminal/Adapter/Buffer/TerminalBuffer.cs`](../src/Dotty.Terminal/Adapter/Buffer/TerminalBuffer.cs)
- [`src/Dotty.Terminal/Adapter/Buffer/BufferEraser.cs`](../src/Dotty.Terminal/Adapter/Buffer/BufferEraser.cs)
- [`src/Dotty.Terminal/Adapter/Buffer/Screen.cs`](../src/Dotty.Terminal/Adapter/Buffer/Screen.cs)
- [`src/Dotty.Terminal/Adapter/Buffer/TerminalBuffer.Rectangles.cs`](../src/Dotty.Terminal/Adapter/Buffer/TerminalBuffer.Rectangles.cs)
- [`src/Dotty.Terminal/Adapter/Buffer/Screen.Rectangles.cs`](../src/Dotty.Terminal/Adapter/Buffer/Screen.Rectangles.cs)

**Work package**

1. Search parser dispatch and buffer APIs for DECCRA copy, DECFRA fill, and DECERA erase rectangle operations.
2. Investigate coordinate conversion, clipping, source/destination bounds, and cell-attribute handling against each assertion’s region.
3. Make rectangular copy overlap-safe and preserve copied cell state; apply command defaults, clipping and origin-relative coordinates without moving the cursor.
4. Candidate changes: introduce or revise the shared rectangle mutation path and dispatch for the required operations.

**Constraints and review**

- Keep page and out-of-range semantics explicit; do not assume source and destination regions are valid.

#### DECCRATests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`DECCRATests.test_DECCRA_defaultValuesInDest`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/deccra.py#L115) | With destination coordinates omitted, DECCRA must copy the specified source rectangle to the default destination origin, matching all asserted cells. | YZ6789!@ | — |
| [`DECCRATests.test_DECCRA_defaultValuesInSource`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/deccra.py#L96) | With source top and left omitted, DECCRA must use the default source origin and copy the specified rectangle to the requested destination, matching all asserted cells. | YZ6789!@ | — |
| [`DECCRATests.test_DECCRA_destinationPartiallyOffscreen`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/deccra.py#L75) | When the copied rectangle extends beyond the bottom-right screen edge, DECCRA must clip the destination and copy the visible 2-by-2 portion. | rs | — |
| [`DECCRATests.test_DECCRA_ignoresMargins`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/deccra.py#L196) | With vertical and horizontal margins set, DECCRA must copy the requested rectangle to its destination without restricting the copy to those margins. | YZ6789!@ | — |
| [`DECCRATests.test_DECCRA_nonOverlappingSourceAndDest`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/deccra.py#L29) | For non-overlapping source and destination rectangles, DECCRA must copy the source's 3-by-3 characters to the destination and leave other asserted cells unchanged. | YZ6789!@ | — |
| [`DECCRATests.test_DECCRA_overlappingSourceAndDest`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/deccra.py#L52) | For overlapping source and destination rectangles, DECCRA must produce the asserted copied 3-by-3 contents without corrupting the source data through overlap. | YZ6789!@ | — |
| [`DECCRATests.test_DECCRA_overlyLargeSourceClippedToScreenSize`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/deccra.py#L232) | When the requested source rectangle extends past the lower-right screen boundary, DECCRA must clip it and copy the available X into the destination, yielding the asserted 2-by-2 cells. | cX | — |
| [`DECCRATests.test_DECCRA_respectsOriginMode`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/deccra.py#L157) | With origin mode and margins active, DECCRA coordinates must be interpreted relative to the origin, placing the copied 3-by-3 rectangle at the asserted screen cells. | YZ6789!@ | — |

#### DECERATests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`DECERATests.test_DECERA_basic`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decera.py#L15) | DECERA clears every cell in the requested basic rectangle to blank cells, leaving cells outside the rectangle unchanged. | YZ6789!@ | — |
| [`DECERATests.test_DECERA_defaultArgs`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decera.py#L22) | DECERA with omitted arguments applies the rectangle operation using its default coordinates, clearing the expected target cells while leaving cells outside that rectangle unchanged. | Expected: | — |
| [`DECERATests.test_DECERA_ignoresMargins`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decera.py#L37) | DECERA clears the requested rectangle without restricting its effect to active scrolling margins, leaving cells outside the rectangle unchanged. | YZ6789!@ | — |
| [`DECERATests.test_DECERA_overlyLargeSourceClippedToScreenSize`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decera.py#L30) | DECERA clips an oversized requested rectangle at the screen boundaries, clearing in-bounds target cells without affecting cells beyond the screen. | c | — |
| [`DECERATests.test_DECERA_respectsOriginMode`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decera.py#L26) | With origin mode enabled, DECERA interprets rectangle coordinates relative to the active origin and clears the corresponding in-bounds cells. | YZ6789!@ | — |

#### DECFRATests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`DECFRATests.test_DECFRA_basic`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decfra.py#L14) | DECFRA fills the basic test rectangle with the requested character, leaving cells outside that rectangle unchanged. | YZ6789!@ | — |
| [`DECFRATests.test_DECFRA_defaultArgs`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decfra.py#L20) | DECFRA with omitted rectangle parameters uses the command’s default bounds and fills exactly the resulting default region with the requested character. | % | — |
| [`DECFRATests.test_DECFRA_ignoresMargins`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decfra.py#L32) | DECFRA fills its specified rectangle even where it extends beyond active scrolling or horizontal margins; those margins do not clip the operation. | YZ6789!@ | — |
| [`DECFRATests.test_DECFRA_overlyLargeSourceClippedToScreenSize`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decfra.py#L26) | DECFRA clips an oversized requested rectangle to the screen boundaries and fills only the on-screen cells within that clipped region. | c% | — |
| [`DECFRATests.test_DECFRA_respectsOriginMode`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decfra.py#L23) | With origin mode enabled, DECFRA interprets its rectangle coordinates relative to the origin and fills the corresponding region without changing cells outside it. | YZ6789!@ | — |

### G11

**Palette input formats, special colors and resets**

**Completed in batch 8:** all 41 listed targets pass. Original first-failure messages below are retained as historical evidence; the approved fixture baseline and native/GUI verification are recorded above.

**Implementation entry points**

- [`src/Dotty.Terminal/Parser/BasicAnsiParser.cs`](../src/Dotty.Terminal/Parser/BasicAnsiParser.cs)
- [`src/Dotty.Terminal/Adapter/TerminalAdapter.cs`](../src/Dotty.Terminal/Adapter/TerminalAdapter.cs)
- [`src/Dotty.Terminal/Adapter/TerminalAdapter.Palette.cs`](../src/Dotty.Terminal/Adapter/TerminalAdapter.Palette.cs)
- [`src/Dotty.Terminal/Adapter/OscColorParser.cs`](../src/Dotty.Terminal/Adapter/OscColorParser.cs)
- [`src/Dotty.Terminal/Adapter/SpecialColorSettings.cs`](../src/Dotty.Terminal/Adapter/SpecialColorSettings.cs)
- [`src/Dotty.Terminal/Adapter/TerminalPalette.cs`](../src/Dotty.Terminal/Adapter/TerminalPalette.cs)
- [`src/Dotty.Terminal/Adapter/SgrParserArgb.cs`](../src/Dotty.Terminal/Adapter/SgrParserArgb.cs)
- [`src/Dotty.Terminal/Adapter/Buffer/StyleSet.cs`](../src/Dotty.Terminal/Adapter/Buffer/StyleSet.cs)

**Work package**

1. Trace OSC palette and dynamic/special-color parsing, queries, resets, and notifications into palette state and SGR color resolution.
2. Investigate accepted color encodings, indexed range boundaries, reset scope, and reply formatting from the assigned assertions.
3. Match special-color OSC 4 indices to the negotiated XTGETTCAP Co indexed-color count and support the equivalent OSC 5 indices; inspect the upstream helper's bounded fallback rather than assuming an offset.
4. Keep X11 OSC hash-color decoding separate from CSS-style #RGB theme/configuration parsing; changing terminal color compatibility must not alter existing theme semantics.
5. Candidate changes: refine palette parsing/state or reset/reply handling at the specific demonstrated boundary.

**Constraints and review**

- Keep this family independently runnable from cursor and mode-state work.
- CIE, rgbi and TekHVC expected RGB values require X11/Xcms conversion and reference-environment review; do not hard-code the sample colors.

#### ChangeColorTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`ChangeColorTests.test_ChangeColor_CIELab`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/change_color.py#L93) | Setting color 0 with each tested CIELab value and querying it must return the corresponding expected RGB components: 6c6c/6767/6767 and 5252/4f4f/4f4f. | esctypes.TestFailure: Test failed: expected ';0;rgb:6c6c/6767/6767' but got ';0;rgb:0000/0000/0000' | Review X11/Xcms conversion, gamut, calibration and rounding dependencies before treating the reference RGB literals as universal color values. |
| [`ChangeColorTests.test_ChangeColor_CIELuv`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/change_color.py#L99) | Setting color 0 with each tested CIELuv value and querying it must return the corresponding expected RGB components: 1616/1414/0e0e and 0e0e/1313/0e0e. | esctypes.TestFailure: Test failed: expected ';0;rgb:1616/1414/0e0e' but got ';0;rgb:0000/0000/0000' | Review X11/Xcms conversion, gamut, calibration and rounding dependencies before treating the reference RGB literals as universal color values. |
| [`ChangeColorTests.test_ChangeColor_CIEXYZ`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/change_color.py#L75) | Setting color 0 with each tested CIEXYZ value and querying it must return the corresponding expected RGB components: ffff/ffff/ffff and dddd/b5b5/a0a0. | esctypes.TestFailure: Test failed: expected ';0;rgb:ffff/ffff/ffff' but got ';0;rgb:0000/0000/0000' | Review X11/Xcms conversion, gamut, calibration and rounding dependencies before treating the reference RGB literals as universal color values. |
| [`ChangeColorTests.test_ChangeColor_CIEuvY`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/change_color.py#L81) | Setting color 0 with each tested CIEuvY value and querying it must return the corresponding expected RGB components: ffff/ffff/ffff and ffff/a3a3/aeae. | esctypes.TestFailure: Test failed: expected ';0;rgb:ffff/ffff/ffff' but got ';0;rgb:0000/0000/0000' | Review X11/Xcms conversion, gamut, calibration and rounding dependencies before treating the reference RGB literals as universal color values. |
| [`ChangeColorTests.test_ChangeColor_CIExyY`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/change_color.py#L87) | Setting color 0 with each tested CIExyY value and querying it must return the corresponding expected RGB components: ffff/ffff/ffff and f7f7/b3b3/0e0e. | esctypes.TestFailure: Test failed: expected ';0;rgb:ffff/ffff/ffff' but got ';0;rgb:0000/0000/0000' | Review X11/Xcms conversion, gamut, calibration and rounding dependencies before treating the reference RGB literals as universal color values. |
| [`ChangeColorTests.test_ChangeColor_Hash12`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/change_color.py#L63) | Setting color 0 with each tested 12-digit hexadecimal color and querying it must return RGB components f0f0/f0f0/f0f0 and 8080/8080/8080. | esctypes.TestFailure: Test failed: expected ';0;rgb:f0f0/f0f0/f0f0' but got ';0;rgb:0000/0000/0000' | — |
| [`ChangeColorTests.test_ChangeColor_Hash3`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/change_color.py#L45) | Setting color 0 with each tested three-digit hexadecimal color and querying it must return RGB components f0f0/f0f0/f0f0 and 8080/8080/8080. | esctypes.TestFailure: Test failed: expected ';0;rgb:f0f0/f0f0/f0f0' but got ';0;rgb:ffff/ffff/ffff' | — |
| [`ChangeColorTests.test_ChangeColor_Hash9`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/change_color.py#L57) | Setting color 0 with each tested nine-digit hexadecimal color and querying it must return RGB components f0f0/f0f0/f0f0 and 8080/8080/8080. | esctypes.TestFailure: Test failed: expected ';0;rgb:f0f0/f0f0/f0f0' but got ';0;rgb:0000/0000/0000' | — |
| [`ChangeColorTests.test_ChangeColor_RGBI`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/change_color.py#L69) | Setting color 0 with each tested rgbi intensity and querying it must return the corresponding expected RGB components: ffff/ffff/ffff and c1c1/bbbb/bbbb. | esctypes.TestFailure: Test failed: expected ';0;rgb:ffff/ffff/ffff' but got ';0;rgb:0000/0000/0000' | Review X11/Xcms conversion, gamut, calibration and rounding dependencies before treating the reference RGB literals as universal color values. |
| [`ChangeColorTests.test_ChangeColor_TekHVC`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/change_color.py#L105) | Setting color 0 with each tested TekHVC value and querying it must return the corresponding expected RGB components: 1a1a/1313/0f0f and 1111/1313/0e0e. | esctypes.TestFailure: Test failed: expected ';0;rgb:1a1a/1313/0f0f' but got ';0;rgb:0000/0000/0000' | Review X11/Xcms conversion, gamut, calibration and rounding dependencies before treating the reference RGB literals as universal color values. |

#### ChangeDynamicColorTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`ChangeDynamicColorTests.test_ChangeDynamicColor_CIELab`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/change_dynamic_color.py#L89) | Setting dynamic color 10 with CIELab specifications 1/1/1 and 0.5/0.5/0.5 must produce query replies rgb:6c6c/6767/6767 and rgb:5252/4f4f/4f4f, respectively. | esctypes.TestFailure: Test failed: expected ';rgb:6c6c/6767/6767' but got ';rgb:0000/0000/0000' | Review X11/Xcms conversion, gamut, calibration and rounding dependencies before treating the reference RGB literals as universal color values. |
| [`ChangeDynamicColorTests.test_ChangeDynamicColor_CIELuv`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/change_dynamic_color.py#L95) | Setting dynamic color 10 with CIELuv specifications 1/1/1 and 0.5/0.5/0.5 must produce query replies rgb:1616/1414/0e0e and rgb:0e0e/1313/0e0e, respectively. | esctypes.TestFailure: Test failed: expected ';rgb:1616/1414/0e0e' but got ';rgb:0000/0000/0000' | Review X11/Xcms conversion, gamut, calibration and rounding dependencies before treating the reference RGB literals as universal color values. |
| [`ChangeDynamicColorTests.test_ChangeDynamicColor_CIEXYZ`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/change_dynamic_color.py#L71) | Setting dynamic color 10 with CIEXYZ specifications 1/1/1 and 0.5/0.5/0.5 must produce query replies rgb:ffff/ffff/ffff and rgb:dddd/b5b5/a0a0, respectively. | esctypes.TestFailure: Test failed: expected ';rgb:ffff/ffff/ffff' but got ';rgb:0000/0000/0000' | Review X11/Xcms conversion, gamut, calibration and rounding dependencies before treating the reference RGB literals as universal color values. |
| [`ChangeDynamicColorTests.test_ChangeDynamicColor_CIEuvY`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/change_dynamic_color.py#L77) | Setting dynamic color 10 with CIEuvY specifications 1/1/1 and 0.5/0.5/0.5 must produce query replies rgb:ffff/ffff/ffff and rgb:ffff/a3a3/aeae, respectively. | esctypes.TestFailure: Test failed: expected ';rgb:ffff/ffff/ffff' but got ';rgb:0000/0000/0000' | Review X11/Xcms conversion, gamut, calibration and rounding dependencies before treating the reference RGB literals as universal color values. |
| [`ChangeDynamicColorTests.test_ChangeDynamicColor_CIExyY`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/change_dynamic_color.py#L83) | Setting dynamic color 10 with CIExyY specifications 1/1/1 and 0.5/0.5/0.5 must produce query replies rgb:ffff/ffff/ffff and rgb:f7f7/b3b3/0e0e, respectively. | esctypes.TestFailure: Test failed: expected ';rgb:ffff/ffff/ffff' but got ';rgb:0000/0000/0000' | Review X11/Xcms conversion, gamut, calibration and rounding dependencies before treating the reference RGB literals as universal color values. |
| [`ChangeDynamicColorTests.test_ChangeDynamicColor_Hash12`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/change_dynamic_color.py#L59) | Setting dynamic color 10 with 12-digit hash colors #f000f000f000 and #800080008000 must report rgb:f0f0/f0f0/f0f0 and rgb:8080/8080/8080, respectively. | esctypes.TestFailure: Test failed: expected ';rgb:f0f0/f0f0/f0f0' but got ';rgb:0000/0000/0000' | — |
| [`ChangeDynamicColorTests.test_ChangeDynamicColor_Hash3`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/change_dynamic_color.py#L41) | Setting dynamic color 10 with 3-digit hash colors #fff and #888 must report rgb:f0f0/f0f0/f0f0 and rgb:8080/8080/8080, respectively. | esctypes.TestFailure: Test failed: expected ';rgb:f0f0/f0f0/f0f0' but got ';rgb:ffff/ffff/ffff' | — |
| [`ChangeDynamicColorTests.test_ChangeDynamicColor_Hash9`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/change_dynamic_color.py#L53) | Setting dynamic color 10 with 9-digit hash colors #f00f00f00 and #800800800 must report rgb:f0f0/f0f0/f0f0 and rgb:8080/8080/8080, respectively. | esctypes.TestFailure: Test failed: expected ';rgb:f0f0/f0f0/f0f0' but got ';rgb:0000/0000/0000' | — |
| [`ChangeDynamicColorTests.test_ChangeDynamicColor_Multiple`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/change_dynamic_color.py#L10) | A single dynamic-color command must set colors 10 and 11; querying both returns their specified RGB values, and after changing both, replies reflect the replacement values. | esctypes.InternalError: Timeout waiting to read. | — |
| [`ChangeDynamicColorTests.test_ChangeDynamicColor_RGBI`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/change_dynamic_color.py#L65) | Setting dynamic color 10 with rgbi specifications 1/1/1 and 0.5/0.5/0.5 must produce query replies rgb:ffff/ffff/ffff and rgb:c1c1/bbbb/bbbb, respectively. | esctypes.TestFailure: Test failed: expected ';rgb:ffff/ffff/ffff' but got ';rgb:0000/0000/0000' | Review X11/Xcms conversion, gamut, calibration and rounding dependencies before treating the reference RGB literals as universal color values. |
| [`ChangeDynamicColorTests.test_ChangeDynamicColor_TekHVC`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/change_dynamic_color.py#L101) | Setting dynamic color 10 with TekHVC specifications 1/1/1 and 0.5/0.5/0.5 must produce query replies rgb:1a1a/1313/0f0f and rgb:1111/1313/0e0e, respectively. | esctypes.TestFailure: Test failed: expected ';rgb:1a1a/1313/0f0f' but got ';rgb:0000/0000/0000' | Review X11/Xcms conversion, gamut, calibration and rounding dependencies before treating the reference RGB literals as universal color values. |

#### ChangeSpecialColorTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`ChangeSpecialColorTests.test_ChangeSpecialColor_CIELab`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/change_special_color.py#L136) | Through both OSC 4 index Co+0 and OSC 5 index 0, setting special color 0 with CIELab values 1/1/1 and 0.5/0.5/0.5 must make queries return respectively 6c6c/6767/6767 and 5252/4f4f/4f4f. | esctypes.InternalError: Timeout waiting to read. | Review X11/Xcms conversion, gamut, calibration and rounding dependencies before treating the reference RGB literals as universal color values. |
| [`ChangeSpecialColorTests.test_ChangeSpecialColor_CIELuv`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/change_special_color.py#L142) | Through both OSC 4 index Co+0 and OSC 5 index 0, setting special color 0 with CIELuv values 1/1/1 and 0.5/0.5/0.5 must make queries return respectively 1616/1414/0e0e and 0e0e/1313/0e0e. | esctypes.InternalError: Timeout waiting to read. | Review X11/Xcms conversion, gamut, calibration and rounding dependencies before treating the reference RGB literals as universal color values. |
| [`ChangeSpecialColorTests.test_ChangeSpecialColor_CIEXYZ`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/change_special_color.py#L118) | Through both OSC 4 index Co+0 and OSC 5 index 0, setting special color 0 with CIEXYZ values 1/1/1 and 0.5/0.5/0.5 must make queries return respectively ffff/ffff/ffff and dddd/b5b5/a0a0. | esctypes.InternalError: Timeout waiting to read. | Review X11/Xcms conversion, gamut, calibration and rounding dependencies before treating the reference RGB literals as universal color values. |
| [`ChangeSpecialColorTests.test_ChangeSpecialColor_CIEuvY`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/change_special_color.py#L124) | Through both OSC 4 index Co+0 and OSC 5 index 0, setting special color 0 with CIEuvY values 1/1/1 and 0.5/0.5/0.5 must make queries return respectively ffff/ffff/ffff and ffff/a3a3/aeae. | esctypes.InternalError: Timeout waiting to read. | Review X11/Xcms conversion, gamut, calibration and rounding dependencies before treating the reference RGB literals as universal color values. |
| [`ChangeSpecialColorTests.test_ChangeSpecialColor_CIExyY`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/change_special_color.py#L130) | Through both OSC 4 index Co+0 and OSC 5 index 0, setting special color 0 with CIExyY values 1/1/1 and 0.5/0.5/0.5 must make queries return respectively ffff/ffff/ffff and f7f7/b3b3/0e0e. | esctypes.InternalError: Timeout waiting to read. | Review X11/Xcms conversion, gamut, calibration and rounding dependencies before treating the reference RGB literals as universal color values. |
| [`ChangeSpecialColorTests.test_ChangeSpecialColor_Hash12`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/change_special_color.py#L106) | Through both OSC 4 index Co+0 and OSC 5 index 0, setting special color 0 with 12-digit hash colors #f000f000f000 and #800080008000 must query back respectively f0f0/f0f0/f0f0 and 8080/8080/8080. | esctypes.InternalError: Timeout waiting to read. | — |
| [`ChangeSpecialColorTests.test_ChangeSpecialColor_Hash3`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/change_special_color.py#L88) | Through both OSC 4 index Co+0 and OSC 5 index 0, setting special color 0 with 3-digit hash colors #fff and #888 must query back respectively f0f0/f0f0/f0f0 and 8080/8080/8080. | esctypes.InternalError: Timeout waiting to read. | — |
| [`ChangeSpecialColorTests.test_ChangeSpecialColor_Hash6`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/change_special_color.py#L94) | Through both OSC 4 index Co+0 and OSC 5 index 0, setting special color 0 with 6-digit hash colors #f0f0f0 and #808080 must query back respectively f0f0/f0f0/f0f0 and 8080/8080/8080. | esctypes.InternalError: Timeout waiting to read. | — |
| [`ChangeSpecialColorTests.test_ChangeSpecialColor_Hash9`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/change_special_color.py#L100) | Through both OSC 4 index Co+0 and OSC 5 index 0, setting special color 0 with 9-digit hash colors #f00f00f00 and #800800800 must query back respectively f0f0/f0f0/f0f0 and 8080/8080/8080. | esctypes.InternalError: Timeout waiting to read. | — |
| [`ChangeSpecialColorTests.test_ChangeSpecialColor_Multiple`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/change_special_color.py#L17) | OSC 4 must process two indexed-color set/query pairs in order, reporting indices offset and offset+1 with their respective RGB values; repeat updates must query the new values. | esctypes.InternalError: Timeout waiting to read. | — |
| [`ChangeSpecialColorTests.test_ChangeSpecialColor_Multiple2`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/change_special_color.py#L41) | OSC 5 must process two special-color set/query pairs and return each requested index with its RGB value; subsequent updates must be reflected in queries. | esctypes.InternalError: Timeout waiting to read. | — |
| [`ChangeSpecialColorTests.test_ChangeSpecialColor_RGB`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/change_special_color.py#L82) | Through both OSC 4 index Co+0 and OSC 5 index 0, setting special color 0 with rgb:f0f0/f0f0/f0f0 and rgb:8080/8080/8080 must return those exact channel values in subsequent queries. | esctypes.InternalError: Timeout waiting to read. | — |
| [`ChangeSpecialColorTests.test_ChangeSpecialColor_RGBI`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/change_special_color.py#L112) | Through both OSC 4 index Co+0 and OSC 5 index 0, setting special color 0 with rgbi values 1/1/1 and 0.5/0.5/0.5 must return respectively ffff/ffff/ffff and c1c1/bbbb/bbbb. | esctypes.InternalError: Timeout waiting to read. | Review X11/Xcms conversion, gamut, calibration and rounding dependencies before treating the reference RGB literals as universal color values. |
| [`ChangeSpecialColorTests.test_ChangeSpecialColor_TekHVC`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/change_special_color.py#L148) | Through both OSC 4 index Co+0 and OSC 5 index 0, setting special color 0 with TekHVC values 1/1/1 and 0.5/0.5/0.5 must return respectively 1a1a/1313/0f0f and 1111/1313/0e0e. | esctypes.InternalError: Timeout waiting to read. | Review X11/Xcms conversion, gamut, calibration and rounding dependencies before treating the reference RGB literals as universal color values. |

#### ResetColorTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`ResetColorTests.test_ResetColor_Standard`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/reset_color.py#L9) | OSC 4 standard color index 0 must report rgb:aaaa/bbbb/cccc after setting it, and ResetColor must restore its exact original query response. | esctypes.TestFailure: Test failed: expected ';0;rgb:aaaa/bbbb/cccc' but got ';0;rgb:0000/0000/0000' | — |

#### ResetSpecialColorTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`ResetSpecialColorTests.test_ResetSpecialColor_Dynamic`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/reset_special_color.py#L93) | Setting OSC 10 to #aaaabbbbcccc must query as rgb:aaaa/bbbb/cccc; resetting dynamic color 110 must restore the exact original OSC 10 query response. | esctypes.TestFailure: Test failed: expected ';rgb:aaaa/bbbb/cccc' but got ';rgb:0000/0000/0000' | — |
| [`ResetSpecialColorTests.test_ResetSpecialColor_Multiple`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/reset_special_color.py#L44) | OSC 4 indices offset and offset+1 must report their assigned RGB values, then ResetSpecialColor for both must restore each index's exact original query response. | esctypes.InternalError: Timeout waiting to read. | — |
| [`ResetSpecialColorTests.test_ResetSpecialColor_Multiple2`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/reset_special_color.py#L69) | OSC 5 indices 0 and 1 must report their assigned RGB values, then ResetSpecialColor for both must restore each index's exact original query response. | esctypes.InternalError: Timeout waiting to read. | — |
| [`ResetSpecialColorTests.test_ResetSpecialColor_Single`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/reset_special_color.py#L11) | OSC 4 index offset must report rgb:aaaa/bbbb/cccc after setting it, and ResetSpecialColor must restore its exact original query response. | esctypes.InternalError: Timeout waiting to read. | — |
| [`ResetSpecialColorTests.test_ResetSpecialColor_Single2`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/reset_special_color.py#L28) | OSC 5 index 0 must report rgb:aaaa/bbbb/cccc after setting it, and ResetSpecialColor must restore its exact original query response. | esctypes.InternalError: Timeout waiting to read. | — |

### G12

**Device identity and identification requests**

**Batch 9 status:** DECID passes. The four DA/DA2 rows remain open under the explicit decision to keep the strict xterm report without spoofing identity or changing assertions.

**Implementation entry points**

- [`src/Dotty.Terminal/Parser/BasicAnsiParser.cs`](../src/Dotty.Terminal/Parser/BasicAnsiParser.cs)
- [`src/Dotty.Terminal/Adapter/TerminalAdapter.cs`](../src/Dotty.Terminal/Adapter/TerminalAdapter.cs)
- [`src/Dotty.Terminal/Adapter/TerminalAdapter.Queries.cs`](../src/Dotty.Terminal/Adapter/TerminalAdapter.Queries.cs)

**Work package**

1. Trace DA, DA2, and DECID sequence recognition and reply generation to the adapter identity/profile values.
2. Investigate exact reply prefixes, parameters, and terminators against the assertions and the supported implementation profile.
3. Candidate changes: revise advertised identity only when the complete supported behavior justifies the claimed response.

**Constraints and review**

- Do not spoof identity/version literals or claim capabilities the terminal does not implement.

#### DA2Tests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`DA2Tests.test_DA2_0`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/da2.py#L39) | DA2 with parameter 0 returns a secondary device-attributes response whose parameters match the configured terminal profile, including the expected parameter count and version bounds. | esctypes.TestFailure: Test failed: expected '64' but got '1' | The expected response depends on configured terminal identity and VT level; preserve truthful profile-specific identity rather than spoofing it. |
| [`DA2Tests.test_DA2_NoParameter`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/da2.py#L35) | DA2 without a parameter returns a secondary device-attributes response whose parameters match the configured terminal profile, including the expected parameter count and version bounds. | esctypes.TestFailure: Test failed: expected '64' but got '1' | The expected response depends on configured terminal identity and VT level; preserve truthful profile-specific identity rather than spoofing it. |

#### DATests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`DATests.test_DA_0`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/da.py#L66) | DA(0) must produce a primary device-attributes response satisfying the shared handler’s expected leading level, minimum length, and required feature-parameter checks for the configured terminal. | esctypes.TestFailure: Test failed: expected '13' but got '2' | The xterm VT5 handler requires model 65 plus a broad capability list; advertise only implemented capabilities and resolve any profile conflict explicitly. |
| [`DATests.test_DA_NoParameter`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/da.py#L61) | DA with no parameter must produce a primary device-attributes response satisfying the shared handler’s expected leading level, minimum length, and required feature-parameter checks for the configured terminal. | esctypes.TestFailure: Test failed: expected '13' but got '2' | The xterm VT5 handler requires model 65 plus a broad capability list; advertise only implemented capabilities and resolve any profile conflict explicitly. |

#### DECIDTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`DECIDTests.test_DECID_Basic`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decid.py#L12) | DECID must return a CSI device-attributes response with the expected '?' prefix and at least one parameter. | esctypes.InternalError: Timeout waiting to read. | — |

### G13

**Mode, device-status and status-string replies**

**Batch 10 outcome:** 31 of these 33 original cases pass. Every row below is resolved except `DECDSRTests.test_DECDSR_DECXCPR` and `DECDSRTests.test_DECDSR_DSRKeyboard`, which remain visibly failing under the approved identity policy. Recorded first-failure messages remain historical evidence.

**Implementation entry points**

- [`src/Dotty.Terminal/Parser/BasicAnsiParser.cs`](../src/Dotty.Terminal/Parser/BasicAnsiParser.cs)
- [`src/Dotty.Terminal/Adapter/TerminalAdapter.Queries.cs`](../src/Dotty.Terminal/Adapter/TerminalAdapter.Queries.cs)
- [`src/Dotty.Terminal/Adapter/TerminalAdapter.ModeReports.cs`](../src/Dotty.Terminal/Adapter/TerminalAdapter.ModeReports.cs)
- [`src/Dotty.Terminal/Adapter/TerminalAdapter.StatusReports.cs`](../src/Dotty.Terminal/Adapter/TerminalAdapter.StatusReports.cs)
- [`src/Dotty.Terminal/Adapter/Buffer/TerminalBuffer.StatusReports.cs`](../src/Dotty.Terminal/Adapter/Buffer/TerminalBuffer.StatusReports.cs)
- [`src/Dotty.Terminal/Adapter/TerminalAdapter.Checksum.cs`](../src/Dotty.Terminal/Adapter/TerminalAdapter.Checksum.cs)

**Work package**

1. Trace DSR, DECRQM, and DECRQSS parsing through state lookup and reply formatting.
2. Investigate CPR coordinates and requested mode/string state against implemented modes, margins, origin mode, and active attributes.
3. For absent hardware/features, implement the protocol-defined unavailable/error status where allowed; do not claim an operational printer, locator, macro store or keyboard feature that is not present.
4. Candidate changes: align report state or serialization with implemented state and the precise requested status.

**Constraints and review**

- Reports must reflect actual implemented state; do not synthesize fake query state.

#### DECDSRTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`DECDSRTests.test_DECDSR_DECCKSR`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decdsr.py#L171) | A memory-checksum DECDSR request with identifier 123 must reply with DCS payload `123!~0000`. | esctypes.InternalError: Timeout waiting to read. | — |
| [`DECDSRTests.test_DECDSR_DECMSR`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decdsr.py#L158) | A macro-space DECDSR request must reply with a `CSI * {` report containing exactly one parameter, zero. | esctypes.InternalError: Timeout waiting to read. | — |
| [`DECDSRTests.test_DECDSR_DECXCPR`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decdsr.py#L21) | After positioning at column 5, row 6, cursor-position DECDSR must report `[6,5,1]` at VT level 4 or higher, and `[6,5]` below that level. | esctypes.TestFailure: Test failed: expected '[6, 5]' but got '[6, 5, 1]' | **Still open:** identity-derived arity conflict retained by user decision; no spoofing or exclusion. |
| [`DECDSRTests.test_DECDSR_DSRDECLocatorStatus`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decdsr.py#L137) | A locator-status DECDSR request must return exactly one status parameter, one of 50, 53, or 55. | esctypes.InternalError: Timeout waiting to read. | — |
| [`DECDSRTests.test_DECDSR_DSRDataIntegrity`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decdsr.py#L180) | A data-integrity DECDSR request must return a private CSI report with exactly one parameter, 70, indicating an OK link. | esctypes.InternalError: Timeout waiting to read. | — |
| [`DECDSRTests.test_DECDSR_DSRKeyboard`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decdsr.py#L82) | A keyboard-status report must begin with 27 and contain 2, 3, or 4 parameters at VT levels 2, 3, or 4+, respectively; present language, status, and type values must be in the test’s allowed sets. | esctypes.InternalError: Timeout waiting to read. | **Still open:** report implemented; current failure is expected arity 2 versus actual 4, inferred from conservative DA2 identity. |
| [`DECDSRTests.test_DECDSR_DSRMultipleSessionStatus`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decdsr.py#L190) | A multiple-session DECDSR request must return a private CSI report containing exactly one parameter, 83 or 87. | esctypes.InternalError: Timeout waiting to read. | **Resolved without a profile change:** reply 83; no operational SSU or multiple-session hardware is claimed. |
| [`DECDSRTests.test_DECDSR_DSRPrinterPort`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decdsr.py#L46) | A printer-port DECDSR request must return a private CSI report containing exactly one legal status: 10, 11, 13, 18, or 19. | esctypes.InternalError: Timeout waiting to read. | — |
| [`DECDSRTests.test_DECDSR_DSRUDKLocked`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decdsr.py#L65) | A UDK-status DECDSR request must return a private CSI report containing exactly one parameter: 20 (unlocked) or 21 (locked). | esctypes.InternalError: Timeout waiting to read. | — |
| [`DECDSRTests.test_DECDSR_LocatorType`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decdsr.py#L143) | A locator-type DECDSR request must report leading parameter 57 and a second parameter identifying type 0, 1, or 2. | esctypes.InternalError: Timeout waiting to read. | — |

#### DECRQMTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`DECRQMTests.test_DECRQM_ANSI_IRM`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqm.py#L69) | DECRQM for ANSI IRM must report the mode as settable, and report set after SM and reset after RM. | esctypes.TestFailure: Test failed: expected '[4, 2]' but got '[4, 4]' | — |
| [`DECRQMTests.test_DECRQM_ANSI_KAM`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqm.py#L63) | DECRQM for ANSI KAM must report the mode as settable, and report set after SM and reset after RM. | esctypes.TestFailure: Test failed: expected '[2, 2]' but got '[2, 4]' | — |
| [`DECRQMTests.test_DECRQM_ANSI_LNM`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqm.py#L81) | DECRQM for ANSI LNM must report the mode as settable, and report set after SM and reset after RM. | esctypes.TestFailure: Test failed: expected '[20, 2]' but got '[20, 4]' | — |
| [`DECRQMTests.test_DECRQM_ANSI_SRM`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqm.py#L75) | DECRQM for ANSI SRM must report the mode as settable, and report set after SM and reset after RM. | esctypes.TestFailure: Test failed: expected '[12, 2]' but got '[12, 3]' | — |
| [`DECRQMTests.test_DECRQM_DEC_DECCOLM`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqm.py#L171) | DECRQM for DEC DECCOLM must report settable, then report set after DECSET and reset after DECRESET; xterm-family runs enable permission first. | esctypes.TestFailure: Test failed: expected '[3, 2]' but got '[3, 4]' | — |
| [`DECRQMTests.test_DECRQM_DEC_DECHEBM`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqm.py#L239) | DECRQM for private mode 35 must report settable, then report set after DECSET and reset after DECRESET. | esctypes.TestFailure: Test failed: expected '[35, 2]' but got '[35, 0]' | **Resolved:** this xterm profile uses mode 35 for font-shift permission, not the historical Hebrew-mode meaning in the test name; native font changes were verified. |
| [`DECRQMTests.test_DECRQM_DEC_DECLRMM`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqm.py#L299) | DECRQM for DEC DECLRMM must report settable, then report set after DECSET and reset after DECRESET. | esctypes.TestFailure: Test failed: expected '[69, 2]' but got '[69, 4]' | — |
| [`DECRQMTests.test_DECRQM_DEC_DECNCSM`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqm.py#L321) | DECRQM for DEC DECNCSM must report settable, then report set after DECSET and reset after DECRESET; xterm-family runs enable permission first. | esctypes.TestFailure: Test failed: expected '[95, 2]' but got '[95, 4]' | — |
| [`DECRQMTests.test_DECRQM_DEC_DECNRCM`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqm.py#L253) | DECRQM for DEC DECNRCM must report settable, then report set after DECSET and reset after DECRESET. | esctypes.TestFailure: Test failed: expected '[42, 2]' but got '[42, 4]' | — |
| [`DECRQMTests.test_DECRQM_DEC_DECPEX`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqm.py#L220) | DECRQM for DEC DECPEX must report settable, then report set after DECSET and reset after DECRESET. | esctypes.TestFailure: Test failed: expected '[19, 2]' but got '[19, 4]' | — |
| [`DECRQMTests.test_DECRQM_DEC_DECPFF`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqm.py#L214) | DECRQM for DEC DECPFF must report settable, then report set after DECSET and reset after DECRESET. | esctypes.TestFailure: Test failed: expected '[18, 2]' but got '[18, 4]' | — |
| [`DECRQMTests.test_DECRQM_DEC_DECSCLM`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqm.py#L182) | DECRQM for DEC DECSCLM must report settable, then report set after DECSET and reset after DECRESET. | esctypes.TestFailure: Test failed: expected '[4, 2]' but got '[4, 4]' | — |

#### DECRQSSTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`DECRQSSTests.test_DECRQSS_DECSACE`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqss.py#L47) | After setting DECSACE to 0, DECRQSS('*x') returns the successful status-string reply `1$r0*x`. | esctypes.InternalError: Timeout waiting to read. | — |
| [`DECRQSSTests.test_DECRQSS_DECSASD`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqss.py#L36) | After setting DECSASD to 0, DECRQSS('$}') returns the successful status-string reply `1$r0$}`. | esctypes.InternalError: Timeout waiting to read. | — |
| [`DECRQSSTests.test_DECRQSS_DECSCA`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqss.py#L57) | After setting DECSCA to 1, DECRQSS('"q') returns the successful status-string reply `1$r1"q`. | esctypes.InternalError: Timeout waiting to read. | — |
| [`DECRQSSTests.test_DECRQSS_DECSCL`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqss.py#L68) | After DECSCL(65, 1), DECRQSS('"p') reports VT level 5 and the control-encoding parameter with payload `1$r65;1"p`. | esctypes.InternalError: Timeout waiting to read. | — |
| [`DECRQSSTests.test_DECRQSS_DECSCUSR`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqss.py#L96) | After setting cursor style 4, DECRQSS(" q") returns the successful status-string reply `1$r4 q`. | esctypes.InternalError: Timeout waiting to read. | — |
| [`DECRQSSTests.test_DECRQSS_DECSLPP`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqss.py#L117) | After setting the page line count to 27, DECRQSS("t") returns the successful status-string reply `1$r27t`. | esctypes.InternalError: Timeout waiting to read. | — |
| [`DECRQSSTests.test_DECRQSS_DECSLRM`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqss.py#L106) | With left-right margin mode enabled and margins set to columns 3–4, DECRQSS("s") returns `1$r3;4s`. | esctypes.InternalError: Timeout waiting to read. | — |
| [`DECRQSSTests.test_DECRQSS_DECSNLS`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqss.py#L140) | After setting the number of screen lines to 24, DECRQSS("*\|") returns the successful status-string reply `1$r24*\|`. | esctypes.InternalError: Timeout waiting to read. | — |
| [`DECRQSSTests.test_DECRQSS_DECSSDT`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqss.py#L151) | After setting status-line type to 0, DECRQSS("$~") returns the successful status-string reply `1$r0$~`. | esctypes.InternalError: Timeout waiting to read. | — |
| [`DECRQSSTests.test_DECRQSS_DECSTBM`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqss.py#L78) | After setting top and bottom margins to rows 5–6, DECRQSS("r") returns the successful status-string reply `1$r5;6r`. | esctypes.InternalError: Timeout waiting to read. | — |
| [`DECRQSSTests.test_DECRQSS_SGR`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqss.py#L87) | After selecting bold with SGR(1), DECRQSS("m") returns the successful rendition reply `1$r0;1m`. | esctypes.InternalError: Timeout waiting to read. | — |

### G14

**Clipboard, title state and window operations**

**Batch 10 outcome:** all 18 original cases below pass. The four clipboard/native-window originals use the real GUI lane; title protocol state continues to use the headless PTY lane. Original assertions and recorded first-failure evidence are unchanged.

**Implementation entry points**

- [`src/Dotty.Terminal/Parser/BasicAnsiParser.cs`](../src/Dotty.Terminal/Parser/BasicAnsiParser.cs)
- [`src/Dotty.Terminal/Adapter/TerminalAdapter.cs`](../src/Dotty.Terminal/Adapter/TerminalAdapter.cs)
- [`src/Dotty.Terminal/Adapter/TerminalAdapter.Clipboard.cs`](../src/Dotty.Terminal/Adapter/TerminalAdapter.Clipboard.cs)
- [`src/Dotty.Terminal/Adapter/TerminalAdapter.WindowOps.cs`](../src/Dotty.Terminal/Adapter/TerminalAdapter.WindowOps.cs)

**Work package**

1. Trace OSC selection-data, title, and CSI window-operation parsing to adapter host events, state, and replies.
2. Investigate title operation distinctions, window-size inputs/reports, and clipboard selection/query/write boundaries from the assertions.
3. Model window/icon titles and title stacks as protocol state with real host integration; reserve window position/iconification reports for authoritative host state, not test-only constants.
4. Candidate changes: adjust protocol dispatch or host-facing event/reply behavior only where supported by available host state.

**Constraints and review**

- Do not weaken clipboard security policy or expose clipboard reads without host authorization.
- Window pixel size, position, and GUI values depend on host-provided resources; headless defaults cannot establish unavailable GUI state.
- xterm-specific window operation expectations may depend on resources/profile settings and should be distinguished from host limitations.

#### ManipulateSelectionDataTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`ManipulateSelectionDataTests.test_ManipulateSelectionData_default`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/manipulate_selection_data.py#L16) | With permission explicitly granted, default-selection OSC 52 set/query round-trips testing 123 as the OSC payload ;s0;dGVzdGluZyAxMjM=. | esctypes.InternalError: Timeout waiting to read. | Validate default selection targets and read/write permissions in the host; retain the product denial policy and never add a test-only clipboard echo. |

#### SMTitleTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`SMTitleTests.test_SMTitle_SetHexQueryHex`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/sm_title.py#L71) | With title set and query modes both hexadecimal, setting window and icon titles to 6162 then 61 must return those literal hex strings on each query. | esctypes.InternalError: Timeout waiting to read. | — |
| [`SMTitleTests.test_SMTitle_SetHexQueryUTF8`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/sm_title.py#L14) | With hexadecimal title-setting and UTF-8 query modes, setting window and icon titles to 6162 then 61 must query as ab then a. | esctypes.InternalError: Timeout waiting to read. | — |
| [`SMTitleTests.test_SMTitle_SetUTF8QueryHex`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/sm_title.py#L52) | With UTF-8 title-setting and hexadecimal query modes, setting window and icon titles to ab then a must query as 6162 then 61. | esctypes.InternalError: Timeout waiting to read. | — |
| [`SMTitleTests.test_SMTitle_SetUTF8QueryUTF8`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/sm_title.py#L33) | With title set and query modes both UTF-8, setting window and icon titles to ab then a must return those same strings on each query. | esctypes.InternalError: Timeout waiting to read. | — |

#### XtermWinopsTests

| Failing test | Required passing behavior | Recorded first failure | Review before fixing |
|---|---|---|---|
| [`XtermWinopsTests.test_XtermWinops_IconifyDeiconfiy`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/xterm_winops.py#L142) | Iconify and then deiconify the terminal window; the reported iconified state must be true after iconifying and false after deiconifying. | esctypes.InternalError: Timeout waiting to read. | — |
| [`XtermWinopsTests.test_XtermWinops_MoveToXY`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/xterm_winops.py#L152) | Move the window to each requested diagonal, horizontal, and vertical position within the test range, with reported coordinates matching each request relative to the initial origin. | esctypes.TestFailure: Test failed: expected 'Point(x=1, y=1)' but got 'Point(x=0, y=0)' | Window-manager constraints at screen boundaries may affect position reports; preserve the test’s relative-position assertions without assuming an unconstrained absolute origin. |
| [`XtermWinopsTests.test_XtermWinops_MoveToXY_Defaults`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/xterm_winops.py#L181) | Move to the requested offset in both axes, then supply only the x or y argument and verify the omitted coordinate defaults to zero. | esctypes.TestFailure: Test failed: expected 'Point(x=10, y=10)' but got 'Point(x=0, y=0)' | Window-manager constraints at screen boundaries may affect position reports; preserve the test’s relative-position assertions without assuming an unconstrained absolute origin. |
| [`XtermWinopsTests.test_XtermWinops_PushIconAndWindow_PopIcon`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/xterm_winops.py#L598) | Push both titles, change both, then pop only the icon title: restore the saved icon title while leaving the changed window title untouched; a subsequent window pop does nothing. | esctypes.InternalError: Timeout waiting to read. | — |
| [`XtermWinopsTests.test_XtermWinops_PushIconAndWindow_PopIconAndWindow`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/xterm_winops.py#L567) | Push both titles together, change both, and pop both together; the saved window and icon titles must both be restored. | esctypes.InternalError: Timeout waiting to read. | — |
| [`XtermWinopsTests.test_XtermWinops_PushIconAndWindow_PopWindow`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/xterm_winops.py#L635) | Push both titles, change both, then pop only the window title: restore the saved window title while leaving the changed icon title untouched; a subsequent icon pop does nothing. | esctypes.InternalError: Timeout waiting to read. | — |
| [`XtermWinopsTests.test_XtermWinops_PushIconThenWindowThenPopBoth`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/xterm_winops.py#L708) | Push the icon title and then the window title separately; one combined pop must restore both titles to their respective saved values. | esctypes.InternalError: Timeout waiting to read. | — |
| [`XtermWinopsTests.test_XtermWinops_PushIcon_PopIcon`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/xterm_winops.py#L666) | Push the current icon title, change it, and pop the icon title; restore its saved value without changing the window title. | esctypes.InternalError: Timeout waiting to read. | — |
| [`XtermWinopsTests.test_XtermWinops_PushMultiplePopMultiple_Icon`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/xterm_winops.py#L735) | Push two successive icon-title values, change the title, and pop twice; restore the second saved value first and the first saved value next. | esctypes.InternalError: Timeout waiting to read. | — |
| [`XtermWinopsTests.test_XtermWinops_PushMultiplePopMultiple_Window`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/xterm_winops.py#L763) | Push two successive window-title values, change the title, and pop twice; restore the second saved value first and the first saved value next. | esctypes.InternalError: Timeout waiting to read. | — |
| [`XtermWinopsTests.test_XtermWinops_PushWindow_PopWindow`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/xterm_winops.py#L687) | Push the current window title, change it, and pop the window title; restore its saved value without changing the icon title. | esctypes.InternalError: Timeout waiting to read. | — |
| [`XtermWinopsTests.test_XtermWinops_ReportIconLabel`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/xterm_winops.py#L554) | Set the icon title to a unique string and verify that the reported icon title equals that string. | esctypes.InternalError: Timeout waiting to read. | The assertion observes a GUI-host icon title; in a headless host, do not fabricate a GUI value or claim support for an unavailable windowing surface. |
| [`XtermWinopsTests.test_XtermWinops_ReportWindowLabel`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/xterm_winops.py#L561) | Set the window title to a unique string and verify that the reported window title equals that string. | esctypes.InternalError: Timeout waiting to read. | The assertion observes a GUI-host window title; in a headless host, do not fabricate a GUI value or claim support for an unavailable windowing surface. |

## Verification and completion criteria

### Per implementation batch

1. Use the saved failure and byte transcript to identify the triggering state. Reproduce only as necessary to develop the fix; the recorded failure is already evidence and does not need a confirmation-only rerun.
2. Establish the applicable ECMA/DEC/xterm rule for the exact scenario, especially where a row below requires review. Write down any discrepancy before proposing a profile change.
3. Fix the shared production behavior and migrate affected callers. Retain a focused, deterministic regression test for a meaningful behavioral boundary rather than adding a duplicate test for every esctest row.
4. Run the affected upstream cases, including previously passing siblings in the same class, then run the full pinned suite after integration. Compare test-name outcomes, not just a total that could conceal a new failure elsewhere.
5. Exercise the actual terminal surface: raw-sequence cursor/margin/editing reproducers and a real TUI for core changes; visible color/erase results for palette/protection changes; real host state and both allowed/denied policy paths for clipboard/window operations. A headless pass does not verify the GUI.
6. Run relevant existing .NET tests and the warning-as-error build once after the integrated batch. If hot-path behavior changes, use a focused throughput/allocation comparison; do not repeat unrelated performance work.
7. Update the support documentation with the implemented behavior and its limits. Mark a row complete only with its passing run and any required surface verification recorded in the implementation change.

### Commands for implementation work

Run from the repository root after implementing a batch; these are future verification commands, not checks executed to create this plan:

```bash
make -C src/Dotty.NativePty
dotnet build tests/Dotty.Conformance.Host/Dotty.Conformance.Host.csproj -c Release --nologo -warnaserror
dotnet build src/Dotty/Dotty.csproj -c Release --nologo -warnaserror

# Start with the complete cursor/margin class, including currently passing siblings.
python3 scripts/esctest/run_esctest.py --only-class CUUTests --gate off --keep-transcripts --out artifacts/esctest-cuu

# Narrow one case only while diagnosing it; this is not full-suite acceptance.
python3 scripts/esctest/run_esctest.py --only-class CUUTests --include '^CUUTests\.test_CUU_StopsAtTopMarginInScrollRegion$' --gate off --keep-transcripts --out artifacts/esctest-cuu-one

# The full run also requires the Linux X11 prerequisites listed in Conformance.md.
# Integrate and inspect results.json; gate-off exit 0 alone is not success.
python3 scripts/esctest/run_esctest.py --jobs 8 --gate off --keep-transcripts --out artifacts/esctest-conformance

dotnet build Dotty.slnx -c Release --no-incremental --nologo -warnaserror
```

### Milestone exit

- Every one of the 263 exact failing names below has an observed passing outcome, with no unapproved test/profile/coverage changes. A pending review remains open work, not an accepted exclusion.
- Preserve the existing 270 passing cases. With an unchanged inventory this implies at least 533 passes and zero failures; improvements in the separate upstream-annotation bucket may increase that pass count further.
- No harness faults, missing logs, skipped or not-run cases; all 567 outcomes remain accounted for. The separate annotation audit must also account for upstream bodies not attempted under the xterm profile.
- Review all 34 upstream expected-failure cases in the appendix. Record whether each is implemented, a genuine limitation, a control-encoding/profile mismatch or an upstream annotation issue, with evidence; do not relabel them as either confirmed Dotty bugs or automatic successes.
- No regressions in the relevant .NET suites, actual terminal smoke scenarios, warning-as-error build or targeted performance checks required by the changes.
- After the failures are resolved, arm CI regression gating using the documented baseline mechanism without generating a blanket xfail list. Set the pinned run's `minPassed` to the observed accepted value and verify the CI summary propagates conformance failures. Explicitly keep visibility of upstream expected-failure annotations, because the existing gate does not require their count to be zero.

## Appendix: upstream expected-failure audit

These are the separate 34 `known-bug` outcomes, not additional members of the 263-test failure target. The label comes from upstream xterm `@knownBug` / `@optionRequired` annotations, not a Dotty issue triage list. Most bodies ran and failed under those annotations; `DECSCL_RISOnChange` explicitly did not run. Inspect raw outcomes and actual assertions when auditing them; an inherited label is neither proof of the same underlying xterm defect nor an exemption for Dotty.

For the 8-bit cases, establish the negotiated byte/control policy while preserving valid UTF-8 decoding. The xterm profile's missing `disableWideChars` option is an annotation assumption, not a recommendation to disable Unicode in Dotty. For legacy mode reports, implement behavior before declaring mutable mode state; otherwise record the truthful protocol status and the unresolved test/profile conflict.

| Upstream-annotated test | Annotation recorded by the suite | Required audit |
|---|---|---|
| [`APCTests.test_APC_8bit`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/apc.py#L23) | Terminal "xterm" requires option "disableWideChars" for this test to pass. | Validate raw 8-bit APC/ST consumption: discard xyz and render only the following A, without corrupting valid UTF-8. |
| [`DECIDTests.test_DECID_8bit`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decid.py#L21) | Terminal "xterm" requires option "disableWideChars" for this test to pass. | Validate DECID reply framing after S8C1T; return a non-empty primary DA response using the negotiated control encoding. |
| [`DECRQMTests.test_DECRQM_DEC_DECAAM`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqm.py#L362) | Not supported | Audit DECAAM capability and DECRQM status transitions against implemented semantics; do not simulate mutable state for an unimplemented mode. |
| [`DECRQMTests.test_DECRQM_DEC_DECARM`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqm.py#L208) | xterm always returns 4 (permanently reset) | Audit DECARM capability and DECRQM status transitions against implemented semantics; do not simulate mutable state for an unimplemented mode. |
| [`DECRQMTests.test_DECRQM_DEC_DECARSM`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqm.py#L348) | Not supported | Audit DECARSM capability and DECRQM status transitions against implemented semantics; do not simulate mutable state for an unimplemented mode. |
| [`DECRQMTests.test_DECRQM_DEC_DECCANSM`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqm.py#L369) | Not supported | Audit DECCANSM capability and DECRQM status transitions against implemented semantics; do not simulate mutable state for an unimplemented mode. |
| [`DECRQMTests.test_DECRQM_DEC_DECCRTSM`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqm.py#L341) | Not supported | Audit DECCRTSM capability and DECRQM status transitions against implemented semantics; do not simulate mutable state for an unimplemented mode. |
| [`DECRQMTests.test_DECRQM_DEC_DECESKM`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqm.py#L391) | Not supported | Audit DECESKM capability and DECRQM status transitions against implemented semantics; do not simulate mutable state for an unimplemented mode. |
| [`DECRQMTests.test_DECRQM_DEC_DECHDPXM`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqm.py#L383) | Not supported | Audit DECHDPXM capability and DECRQM status transitions against implemented semantics; do not simulate mutable state for an unimplemented mode. |
| [`DECRQMTests.test_DECRQM_DEC_DECHEM`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqm.py#L246) | Not supported | Audit DECHEM capability and DECRQM status transitions against implemented semantics; do not simulate mutable state for an unimplemented mode. |
| [`DECRQMTests.test_DECRQM_DEC_DECKBUM`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqm.py#L293) | Not supported | Audit DECKBUM capability and DECRQM status transitions against implemented semantics; do not simulate mutable state for an unimplemented mode. |
| [`DECRQMTests.test_DECRQM_DEC_DECKPM`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqm.py#L313) | Not supported | Audit DECKPM capability and DECRQM status transitions against implemented semantics; do not simulate mutable state for an unimplemented mode. |
| [`DECRQMTests.test_DECRQM_DEC_DECMCM`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqm.py#L355) | Not supported | Audit DECMCM capability and DECRQM status transitions against implemented semantics; do not simulate mutable state for an unimplemented mode. |
| [`DECRQMTests.test_DECRQM_DEC_DECNAKB`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqm.py#L260) | Not supported | Audit DECNAKB capability and DECRQM status transitions against implemented semantics; do not simulate mutable state for an unimplemented mode. |
| [`DECRQMTests.test_DECRQM_DEC_DECNULM`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqm.py#L376) | Not supported | Audit DECNULM capability and DECRQM status transitions against implemented semantics; do not simulate mutable state for an unimplemented mode. |
| [`DECRQMTests.test_DECRQM_DEC_DECOSCNM`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqm.py#L398) | Not supported | Audit DECOSCNM capability and DECRQM status transitions against implemented semantics; do not simulate mutable state for an unimplemented mode. |
| [`DECRQMTests.test_DECRQM_DEC_DECPCCM`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqm.py#L274) | Not supported | Audit DECPCCM capability and DECRQM status transitions against implemented semantics; do not simulate mutable state for an unimplemented mode. |
| [`DECRQMTests.test_DECRQM_DEC_DECRLCM`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqm.py#L334) | Not supported | Audit DECRLCM capability and DECRQM status transitions against implemented semantics; do not simulate mutable state for an unimplemented mode. |
| [`DECRQMTests.test_DECRQM_DEC_DECRLM`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqm.py#L233) | Not supported | Audit DECRLM capability and DECRQM status transitions against implemented semantics; do not simulate mutable state for an unimplemented mode. |
| [`DECRQMTests.test_DECRQM_DEC_DECVCCM`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqm.py#L267) | Not supported | Audit DECVCCM capability and DECRQM status transitions against implemented semantics; do not simulate mutable state for an unimplemented mode. |
| [`DECRQMTests.test_DECRQM_DEC_DECXRLM`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqm.py#L306) | Not supported | Audit DECXRLM capability and DECRQM status transitions against implemented semantics; do not simulate mutable state for an unimplemented mode. |
| [`DECRQSSTests.test_DECRQSS_DECELF`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqss.py#L13) | Not implemented. | Audit local-functions support and the +q query; upstream expects 1$r0+q after DECELF(0). |
| [`DECRQSSTests.test_DECRQSS_DECLFKC`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqss.py#L25) | Not implemented. | Audit local-function-key support and the *} query; upstream expects 1$r0*} after DECLFKC(0). |
| [`DECRQSSTests.test_DECRQSS_DECSMKR`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decrqss.py#L129) | Not implemented. | Audit modifier-key reporting state and the +r query; upstream expects 1$r0+r after DECSMKR(0). |
| [`DECSCLTests.test_DECSCL_RISOnChange`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/decscl.py#L119) | xterm always turns on 8-bit controls. (not trying) | Force an actual execution outside the inherited shouldTry=False annotation; verify the selected level's reset semantics for screen, saved cursor and insert mode. |
| [`HTSTests.test_HTS_8bit`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/hts.py#L29) | Terminal "xterm" requires option "disableWideChars" for this test to pass. | After clearing tabs, 8-bit HTS at column 20 must make the next horizontal tab from column 1 land at column 20. |
| [`INDTests.test_IND_8bit`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/ind.py#L142) | Terminal "xterm" requires option "disableWideChars" for this test to pass. | 8-bit IND must move (5,3) to (5,4), preserving the column. |
| [`NELTests.test_NEL_8bit`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/nel.py#L147) | Terminal "xterm" requires option "disableWideChars" for this test to pass. | 8-bit NEL must move (5,3) to (1,4). |
| [`PMTests.test_PM_8bit`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/pm.py#L28) | Terminal "xterm" requires option "disableWideChars" for this test to pass. | Consume 8-bit PM through ST, discard xyz and render only the following A. |
| [`RITests.test_RI_8bit`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/ri.py#L139) | Terminal "xterm" requires option "disableWideChars" for this test to pass. | 8-bit RI must move (5,3) to (5,2), preserving the column. |
| [`S8C1TTests.test_S8C1T_CSI`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/s8c1t.py#L22) | Terminal "xterm" requires option "disableWideChars" for this test to pass. | Dispatch 8-bit CUP after S8C1T and leave the cursor at (1,2); restore S7C1T correctly. |
| [`S8C1TTests.test_S8C1T_DCS`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/s8c1t.py#L35) | Terminal "xterm" requires option "disableWideChars" for this test to pass. | After margins 5..6 and S8C1T, DECRQSS must return the DCS payload 1$r5;6r with compatible framing. |
| [`S8C1TTests.test_S8C1T_SPA_EPA`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/s8c1t.py#L52) | Terminal "xterm" requires option "disableWideChars" for this test to pass. | 8-bit SPA/EPA must protect c so ECH(3) erases a and b but leaves c. |
| [`SOSTests.test_SOS_8bit`](https://github.com/ThomasDickey/esctest2/blob/2798f12149a19c3295e9b4853ab2da4b2eff1b2b/esctest/tests/sos.py#L28) | Terminal "xterm" requires option "disableWideChars" for this test to pass. | Consume 8-bit SOS through ST, discard xyz and render only the following A. |
