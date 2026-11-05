# Parsing Mechanism

The parsing system in Dotty is responsible for translating raw PTY byte streams into structured terminal actions. It implements a high-performance, zero-allocation ANSI/VT parser that handles the complete lifecycle of escape sequences, control codes, and printable text.

## Table of Contents

1. [Parser Architecture Overview](#parser-architecture-overview)
2. [ANSI Escape Sequence Parsing](#ansi-escape-sequence-parsing)
3. [State Machine Design](#state-machine-design)
4. [Input Handling Flow](#input-handling-flow)
5. [Special Sequence Handling](#special-sequence-handling)
6. [Performance Considerations](#performance-considerations)
7. [Source File References](#source-file-references)

---

## Parser Architecture Overview

The parsing architecture follows a **streaming, event-driven design** that processes byte sequences incrementally as they arrive from the PTY. This approach eliminates the need for intermediate buffers and enables real-time terminal response.

### Core Components

```
┌─────────────────────────────────────────────────────────────────┐
│                     Parser Architecture                         │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  ┌──────────────┐    ┌──────────────┐    ┌──────────────┐       │
│  │   PTY Input  │───▶│ BasicAnsi    │───▶│ ITerminal    │       │
│  │   (bytes)    │    │ Parser       │    │ Handler      │       │
│  └──────────────┘    └──────────────┘    └──────────────┘       │
│                              │                    │               │
│                              ▼                    ▼               │
│                       ┌──────────────┐    ┌──────────────┐       │
│                       │ State Machine│    │ Terminal     │       │
│                       │ (CSI/OSC/DCS)│    │ Buffer       │       │
│                       └──────────────┘    └──────────────┘       │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

### Key Design Principles

| Principle | Implementation | Benefit |
|-----------|----------------|---------|
| **Zero-allocation hot path** | `ReadOnlySpan<byte>` for all byte processing | Eliminates GC pressure during high-volume terminal output |
| **Fragment resilience** | `_leftover` buffer tracks incomplete sequences | Safe handling of split escape sequences across read boundaries |
| **Fast-path optimization** | Direct ASCII text runs bypass UTF-8 decoding | ~10x faster for common text output |
| **Charset translation** | DEC Special Graphics mapping table | Correct rendering of terminal drawing characters |

---

## ANSI Escape Sequence Parsing

The parser recognizes and processes multiple categories of ANSI escape sequences defined by the ECMA-48 and DEC terminal standards.

### Sequence Categories

#### 1. C0 Control Characters (0x00-0x1F)

Single-byte control codes that require immediate action:

| Code | Hex | Name | Action |
|------|-----|------|--------|
| NUL | 0x00 | Null | Ignored |
| BEL | 0x07 | Bell | Triggers `OnBell()` handler |
| BS | 0x08 | Backspace | `OnCursorBack(1)` |
| TAB | 0x09 | Horizontal Tab | `OnTab(1)` - moves to next tab stop |
| LF | 0x0A | Line Feed | `OnLineFeed()` |
| VT | 0x0B | Vertical Tab | `OnLineFeed()` (treated as LF) |
| FF | 0x0C | Form Feed | `OnLineFeed()` (treated as LF) |
| CR | 0x0D | Carriage Return | `OnCarriageReturn()` |
| ESC | 0x1B | Escape | Begins escape sequence parsing |
| DEL | 0x7F | Delete | Ignored |

#### 2. CSI Sequences (Control Sequence Introducer)

Format: `ESC [ <params> <final byte>`

The parser handles these CSI sequences:

| Final | Params | Description | Handler Called |
|-------|--------|-------------|----------------|
| `A` | `[n]` | Cursor Up | `OnCursorUp(n)` |
| `B` | `[n]` | Cursor Down | `OnCursorDown(n)` |
| `C` | `[n]` | Cursor Forward | `OnCursorForward(n)` |
| `D` | `[n]` | Cursor Back | `OnCursorBack(n)` |
| `E` | `[n]` | Cursor Next Line | `OnCursorNextLine(n)` |
| `F` | `[n]` | Cursor Previous Line | `OnCursorPreviousLine(n)` |
| `G` | `[n]` | Cursor Horizontal Absolute | `OnCursorHorizontalAbsolute(n)` |
| `H` / `f` | `[row;col]` | Cursor Position (CUP/HVP) | `OnMoveCursor(row, col)` |
| `J` | `[mode]` | Erase Display (`0/1/2`) | `OnEraseDisplay(mode)` |
| `K` | `[mode]` | Erase Line | `OnEraseLine(mode)` |
| `L` | `[n]` | Insert Lines | `OnInsertLines(n)` |
| `M` | `[n]` | Delete Lines | `OnDeleteLines(n)` |
| `P` | `[n]` | Delete Chars | `OnDeleteChars(n)` |
| `S` | `[n]` | Scroll Up | `OnScrollUp(n)` |
| `T` | `[n]` | Scroll Down | `OnScrollDown(n)` |
| `X` | `[n]` | Erase Chars | `OnEraseCharacters(n)` |
| `@` | `[n]` | Insert Chars | `OnInsertChars(n)` |
| `c` | `[0]`, `>[0]`, `=[0]` | Primary / secondary / unsupported tertiary device attributes | `OnSendDeviceAttributes(daType)` for valid requests only |
| `d` | `[n]` | Cursor Vertical Absolute | `OnCursorVerticalAbsolute(n)` |
| `0x60` | `[n]` | Horizontal Position Absolute (HPA; same origin-aware addressing as CHA) | `OnCursorHorizontalAbsolute(n)` |
| `a` | `[n]` | Horizontal Position Relative (HPR) | `OnHorizontalPositionRelative(n)` |
| `e` | `[n]` | Vertical Position Relative (VPR) | `OnVerticalPositionRelative(n)` |
| `I` | `[n]` | Cursor Forward Tabulation (CHT) | `OnTab(n)` |
| `m` | `[attrs]` | SGR (Graphics) | `OnSetGraphicsRendition(attrs)` |
| `n` | `[code]` | Device Status Report | `OnDeviceStatusReport(code)` |
| `q` | `[shape]` | Set Cursor Shape | `OnSetCursorShape(shape)` |
| `q` | `>[0]` | XTVERSION | `OnRequestTerminalVersion()` |
| `r` | `[top;bottom]` | Set Scroll Region (DECSTBM) | `OnSetScrollRegion(top, bottom)` |
| | | | Note: DECSTBM now interacts correctly with alternate screen and DECOM (origin mode) |
| `s` | `[left;right]` | Set horizontal margins (DECSLRM) with DECLRMM enabled; default-parameter SCO save otherwise | `OnSaveCursorOrSetMargins(left, right)` |
| `s` / `r` | `?<modes>` | Save / restore DEC private mode values (XTSAVE/XTRESTORE) | `OnSavePrivateMode(mode)` / `OnRestorePrivateMode(mode)` |
| `t` / `T` | `>[modes]` | Set / reset title encoding features | `OnSetTitleModes(modes, enabled)` |
| `u` | none | Restore Cursor (SCO) | `OnRestoreCursor()` |
| `h`/`l` | `?<mode>` | Set/Reset Mode | `OnSetAlternateScreen()`, etc. |
| `h`/`l` | `<mode>` | ANSI Insert/Replace (4) and New Line (20) | `OnSetAnsiMode(mode, enabled)` |

Kitty keyboard protocol CSI commands (`CSI =`, `>`, `<`, and `?` ending in `u`) are handled separately from legacy numeric CSI parameters. `CSI > Ps q` is XTVERSION, routed to `OnRequestTerminalVersion()` for `Ps` 0 or omitted and otherwise consumed; it never reaches DECSCUSR. See [Keyboard Protocol and Compatibility](KeyboardProtocol.md) for the negotiation stack, supported flags, screen scoping, and exact replies.
Note: `CSI 3 J` is handled separately and maps to `OnClearScrollback()` rather than `OnEraseDisplay(3)`.
`CSI Ps n` is the status report family; see [Status reports](#status-reports-dsr-cpr-and-decxcpr). `CSI ? 6 n` (DECXCPR) is routed to `OnCursorPositionReport()`, so shells like PSReadLine receive the private form `ESC[?row;col;pageR`. `CSI ? Ps n` with `Ps` other than 6 is routed to `OnPrivateDeviceStatusReport(Ps)`; only `998` is answered (see [Visibility reports](#visibility-reports-mode-2033-and-csi--998-n)). `CSI > Ps n` is not a status report and is ignored.
`CSI Ps ; Ps ; Ps t` is routed to `OnWindowOperation(operation, arg1, arg2)`; omitted parameters are 0, and the `?` and `<` forms are ignored. The separate `CSI > Pm t/T` title-mode forms are handled before window operations or scroll-down dispatch. See [Window operations](#window-operations-csi-ps--ps--ps-t) and [Title modes and replies](#title-modes-and-replies).

Device-attributes requests accept only an omitted parameter or a single decimal zero, including leading zeroes: `CSI c` / `CSI 0 c` for primary DA and `CSI > c` / `CSI > 0 c` for secondary DA. `ESC Z` (DECID) is the [obsolete primary-DA alias](https://invisible-island.net/xterm/ctlseqs/ctlseqs.html), including when ESC and Z arrive in separate input chunks. These reports preserve cursor, rendition and pending wrap. `CSI ? … c` is a primary reply, not a request; echoed primary/secondary replies, nonzero requests, parameter lists and intermediates are consumed without replying, preventing identification reply loops. Valid tertiary requests (`CSI = c` / `CSI = 0 c`) remain unsupported and silent.

The primary reply remains the conservative `CSI ? 1 ; 0 c`. Secondary DA retains compatibility model 1 and Dotty's own version: `CSI > 1 ; Pv ; 0 c`, with `Pv = major*10000 + minor*100 + patch`; XTVERSION reports the actual Dotty version string. Neither claims xterm's model, firmware patch number or full capability list. The four pinned xterm DA/DA2 identity mismatches remain visible in the [conformance plan](ConformancePlan.md#g12). This change does not add raw C1 DECID (`0x9A`) or alter UTF-8/control-encoding policy.

#### Routed sequences

| Sequence | Handler | Behaviour in `TerminalAdapter` |
|---|---|---|
| `CSI ! p` (DECSTR; numeric parameters before `!` are ignored) | `OnSoftReset()` | Implemented: see [DECSTR and RIS](#decstr-and-ris-reset-sets). The parser also resets its own G0/G1 charset to ASCII. |
| `ESC c` (RIS) | `OnFullReset()` | Implemented: see [DECSTR and RIS](#decstr-and-ris-reset-sets). The parser also resets its charset to ASCII. It also deletes every Kitty image of both screens and aborts an open graphics transfer (the configured limits stay). |
| `ESC D` (IND) | `OnIndex()` | At the bottom margin, scrolls if horizontally eligible and holds otherwise. Away from that boundary, moves down toward the physical last row. Preserves the column and clears wrap-pending. |
| `ESC E` (NEL) | `OnNextLine()` | IND, then margin-aware carriage return. Indexing uses the original column to decide whether to scroll. |
| `ESC 6` / `ESC 9` (DECBI/DECFI) | `OnBackIndex()` / `OnForwardIndex()` | VT4+: horizontal cursor movement or a bounded one-column shift at the corresponding margin; see [DEC column editing and horizontal indexing](#dec-column-editing-and-horizontal-indexing). |
| `CSI Pn ' }` / `CSI Pn ' ~` (DECIC/DECDC) | `OnInsertColumns(n)` / `OnDeleteColumns(n)` | VT4+: insert/delete columns across all scrolling rows, from the cursor column through the right margin. |
| `CSI Pt ; Pl ; Pb ; Pr ; Pp ; Ptd ; Pld ; Ppd $ v` (DECCRA) | `OnCopyRectangle(...)` | VT4+: overlap-safe cell/rendition copy with independent source and destination bounds; page selectors resolve to the single active page. |
| `CSI Pc ; Pt ; Pl ; Pb ; Pr $ x` (DECFRA) | `OnFillRectangle(...)` | VT4+: fill with a valid character, current SGR and selected protection. |
| `CSI Pt ; Pl ; Pb ; Pr $ z` / `CSI Pt ; Pl ; Pb ; Pr $ {` (DECERA/DECSERA) | `OnEraseRectangle(..., selective)` | VT4+: ordinary or selective rectangle erase; see [Rectangle copy, fill and erase](#rectangle-copy-fill-and-erase). |
| `ESC V` / `ESC W` (SPA/EPA) | `OnSetIsoProtection(active)` | Select ISO guarded-area marking / stop marking; ordinary erase still honors the written protected cells after EPA. |
| `CSI Ps " q` (DECSCA) | `OnSetDecProtection(Ps)` | Select DEC protection semantics; 1 marks subsequent glyphs, 0/2 stop marking, other parameters are ignored. |
| `CSI ? Ps J` / `CSI ? Ps K` (DECSED/DECSEL) | `OnSelectiveEraseDisplay(Ps)` / `OnSelectiveEraseLine(Ps)` | Separate selective-erase paths; see [Editing and protected erasure](#editing-and-protected-erasure). |
| `CSI Ps ; Ps " p` (DECSCL) | `OnSetConformanceLevel(level, controls)` | Accept levels 61–65 and control selectors 0–2, apply the soft-reset set, and select feature availability; UTF-8/C1 handling is unchanged. |
| `CSI Ps n` | `OnDeviceStatusReport(Ps)` | 5 and 6 answered; every other code is silent. |
| `CSI ? 6 n` | `OnCursorPositionReport()` | DECXCPR, implemented. |
| `CSI ? Ps n`, `Ps != 6` | `OnPrivateDeviceStatusReport(Ps)` | Only 998 is answered; others are silent. |
| `CSI Ps ; Ps ; Ps t` | `OnWindowOperation(op, a1, a2)` | Operations 8, 13-16, 18, 19 and Ps >= 24 act; the rest are ignored. |
| `CSI Pi ; Pg ; Pt ; Pl ; Pb ; Pr * y` (DECRQCRA) | `OnRequestChecksum(...)` | Implemented but **gated**: no reply unless `vt.checksumReportEnabled` is set. A well-formed request is always consumed; a malformed one is rejected by the numeric parser and dropped. |
| `ESC # 8` (DECALN) | `OnScreenAlignmentTest()` | Fill the visible screen with `E`, reset rendition, origin and both margin bounds, and home the cursor without growing scrollback. Autowrap is preserved. |
| `CSI ? 69 h/l` (DECLRMM) | `OnSetLeftRightMarginMode(enabled)` | Track horizontal-margin mode for cursor addressing, tabulation, line movement and bounded scrolling. Changing the mode does not move the cursor. |
| `CSI ? 45 h/l`, `CSI ? 1045 h/l` | `OnSetReverseWrapMode(mode, enabled)` | Modern normal/extended reverse wrapping; see below. |
| `CSI ? 66 h/l` (DECNKM) | `OnSetKeypadApplicationMode(enabled)` | Implemented: shares state with `ESC =` / `ESC >` (DECKPAM/DECKPNM). |
| `CSI ? 67 h/l` (DECBKM) | `OnSetBackarrowKeyMode(sendsBackspace)` | Implemented: see [Keyboard Protocol](KeyboardProtocol.md#decbkm-backarrow-key-mode-67). |
| `CSI ? 3 h/l`, `CSI ? 40 h/l`, `CSI ? 95 h/l` | `OnSetColumnMode(mode, enabled)` | 80/132-column requests, column-change permission, and VT5 no-clear behavior; see [Column modes](#column-modes). |
| `CSI ? 41 h/l` | `OnSetMoreFix(enabled)` | Compatibility tab wrapping at a pending right-edge wrap. |
| `CSI ? 47 h/l`, `CSI ? 1047 h/l` | `OnSwitchAlternateScreen(mode, enabled)` | Distinct retention/clear policies, with no cursor save/restore; see [DECSET 47, 1047, 1048, 1049](#decset-47-1047-1048-and-1049). |
| `CSI ? 1049 h/l` | `OnSetAlternateScreen(enabled)` | Cursor save + alternate screen; same section. |
| `CSI ? 1048 h/l` | `OnSaveCursor()` / `OnRestoreCursor()` | Save/restore cursor; see the same section. |
| `CSI ? 2027 h/l` | none | Accepted and ignored: grapheme clustering is always on (see [Grapheme clusters](#combining-cap-grapheme-clusters-cell-width-and-the-grapheme-store)). |
| `CSI ? 2033 h/l` | `OnSetVisibilityReports(enabled)` | Implemented: see [Visibility reports](#visibility-reports-mode-2033-and-csi--998-n). |
| `CSI ? 5 h/l` (DECSCNM) | `OnSetReverseVideo(enabled)` | Implemented: `TerminalAdapter.ReverseVideo`; the renderer swaps the pane's effective default foreground and background (after OSC 10/11 overrides). A change repaints like a palette change. Kept by DECSTR, cleared by RIS. |

Mouse modes go to `OnSetMouseMode(mode, enabled)`: tracking modes 9 (X10, presses only), 1000, 1002 and 1003 share one slot, the coordinate encodings 1005 (UTF-8), 1006 (SGR), 1015 (urxvt) and 1016 (SGR-pixels) share another (the last one set wins; resetting the active one restores the default encoding, like xterm), and 1007 (alternate scroll) is an independent flag that is **set** at power-on. The host encodes reports with `MouseEncoder`; see [Mouse reporting (host side)](#mouse-reporting-host-side).

DEC private mode 4 (DECSCLM) enables smooth presentation of eligible vertical
scrolls. The incoming rows move through fractional-pixel positions while
outgoing rows leave the viewport; the final frame settles on the exact terminal
row contents. Resetting mode 4 disables animation and presents the settled
contents immediately. DECSTR cancels an active transition but preserves the
mode setting; RIS resets the mode. DECRQM reports its current setting.

#### DEC column editing and horizontal indexing

At conformance level VT4 or higher, DECIC (`CSI Pn ' }`) and DECDC (`CSI Pn ' ~`) insert/delete columns from the cursor through the effective right margin on **every row between the top and bottom scrolling margins**, including rows above the cursor. A cursor outside either margin axis makes the command a no-op. Omitted/zero counts mean one; saturated counts are clamped to the eligible width. Private prefixes and extra intermediates are rejected.

DECBI (`ESC 6`) at the effective left margin shifts the margin rectangle right by one column; DECFI (`ESC 9`) at the effective right margin shifts it left. The cursor stays in place. At that horizontal boundary but outside the vertical region, neither cells nor cursor move. Away from the corresponding margin, the commands use ordinary CUB/CUF movement, including movement outside a narrow horizontal region toward a physical page edge. With default full-page margins, the page edge **is** the corresponding margin, so indexing there shifts the page.

Eligible column edits and margin shifts preserve pending wrap, as does an index held outside the vertical region. Ordinary cursor movement clears it; reverse-wrap-enabled DECBI consumes a pending wrap before moving, like CUB. Deletion clears each edited row's outgoing soft-wrap link; insertion retains links unless native empty-row handling detaches them. Following-row invalidation also accounts for that empty-row case.

These operations reuse native hot/cold cell movement, wide-glyph boundary repair and row-extent updates. Exposed cells are unstyled, unprotected blanks rather than copies of active SGR. They do not rotate the ring or add scrollback. Contained Kitty placements move and crop within the actual edit rectangle; see [Kitty graphics](KittyGraphics.md). Ordinary ICH/DCH semantics are unchanged. All four commands are ignored below VT4.

#### Cursor margins, tab stops and reverse wrapping

CUU/CUD/CUF/CUB stop at the applicable margin when starting on its inner side, and at the screen edge when starting beyond it. CNL/CPL combine bounded vertical movement with a margin-aware carriage return. CUP/HVP, CHA/HPA and VPA translate the addressed axes under DECOM without moving the unaddressed axis. HPR/VPR apply relative deltas through absolute-position bounds rather than the direction-dependent CUF/CUD bounds.

With DECLRMM enabled, `CSI Pl ; Pr s` sets horizontal margins; omitted/zero bounds default to the screen edges and an oversized right bound is clamped. Without that mode, only the default-parameter `CSI s` saves the cursor. `ESC 7` remains an unconditional save operation. Cursor/tab semantics, bounded scrolling, ordinary character/line editing, DEC column operations and the rectangle copy/fill/erase commands below are implemented; other outstanding conformance families are tracked in the [completion plan](ConformancePlan.md).

`CSI ? Pm s/r` saves/restores implemented DEC private modes in a one-level, reusable cache. Only the listed modes are restored, using their normal behavioral setters; unsupported/read-only modes do nothing. Unsaved cache slots start at zero, not at each mode's power-on value. Mouse tracking modes share one saved selected-mode value, mouse encodings share another, and 47/1047/1049 share the active-buffer slot. Restoring that buffer slot is non-clearing and does not save or restore the cursor; mode 1048 instead performs cursor save/restore actions without a cache slot. Both RIS and DECSTR preserve the cache, matching xterm. The whole mode list is validated before dispatch and processed within the existing 256-byte CSI limit, without the fixed-arity path's eight-field truncation.

HT and CHT share counted forward-tab traversal and stop at the effective right boundary; CBT ignores margins and traverses screen-column stops. A forward tab that cannot advance preserves a pending printable wrap. With mode 41 (the xterm `more(1)` compatibility fix) enabled and autowrap active, that pending wrap is consumed before traversing the next line's tab stops. Mode 41 defaults off, survives DECSTR, and is cleared by RIS. `CSI 3 g` removes all tab stops, rather than reinstalling the default eight-column stops. HTS (`ESC H`) can then install custom stops. Oversized tab counts stop doing work once the boundary is reached.

Mode 45 requires DECAWM and crosses only soft-wrapped line boundaries. Mode 1045 also requires DECAWM but permits extended reverse wrapping through unwrapped lines and around the applicable vertical region. The modes are independent, default to off, and are cleared by DECSTR and RIS. A pending forward wrap consumes the first reverse step without moving left. This follows the modern xterm distinction documented in [xterm's cursor implementation](https://github.com/ThomasDickey/xterm-snapshots/blob/master/cursor.c); esctest explicitly selects `--xterm-reverse-wrap=383`.

LF/VT/FF and IND index using the original column. At the bottom margin they scroll only when that column lies within the effective left/right margins; otherwise they hold at that row. Away from that boundary they move down toward the physical last row, including when starting below the vertical region. RI is symmetric at the top margin. ANSI New Line Mode (`CSI 20 h/l`) makes LF/VT/FF perform a margin-aware carriage return after indexing; IND remains column-preserving. NEL always indexes before carriage return, so an outside-right cursor cannot become eligible to scroll merely because of the return. These explicit line controls clear pending wrap.

ASCII byte/character spans, individual cells and grapheme placement use the active right margin for wrapping and no-autowrap clamping when the cursor starts at or left of it. A cursor beyond that margin writes toward the physical screen edge; a cursor left of the left margin is not forcibly moved into the region. After a pending autowrap, the next character uses the column established by the margin-aware return rather than unconditionally restarting at screen column zero. REP uses these same writer paths.

ANSI Insert/Replace Mode (`CSI 4 h/l`) inserts printable cells through the writer's active right edge, discarding overflow without moving outside cells. ASCII byte and character spans shift once per row segment; ordinary replace mode retains its bulk path. Wide graphemes insert their cell width, combining joins insert nothing, and late width growth inserts only the extra cells. Shifted styles, hyperlinks, graphemes and protection flags stay attached to their cells. IRM and LNM are global terminal modes, reported live by ANSI DECRQM and reset by DECSTR/RIS. LNM also affects legacy Return encoding; see [Keyboard Protocol](KeyboardProtocol.md#lnm-new-line-mode-20).

SU/SD always scroll the active rectangle, regardless of the cursor position, and preserve the cursor. Full-width scrolling retains the ring/history path. Partial-width scrolling copies only the selected columns without rotating the ring or adding scrollback; unrelated outside cells stay put. Cell styles, graphemes, hyperlinks and placeholder metadata move with their cells. A wide glyph crossing a rectangle edge is cleared as a whole rather than leaving an orphaned half. Affected soft-wrap links are conservatively broken instead of inventing a new logical-line relationship. Normal image placements use the containment rules in [Kitty graphics](KittyGraphics.md#screen-semantics).

#### Editing and protected erasure

ICH/DCH require the cursor to be inside the horizontal margins, but not the vertical region. They shift only from the cursor through the right margin, saturate oversized counts and preserve unrelated outside cells. IL/DL require the cursor to be inside both axes and shift the rectangle from its row through the bottom margin. Full-width line edits keep the existing row-copy path; neither full-width nor partial-width line editing adds or restores scrollback. Contained image placements follow vertical line edits using the same bounds as the text.

Glyph data and its style, hyperlink, grapheme, placeholder and protection metadata move together. A wide glyph cut by an insertion/deletion boundary is cleared whole rather than leaving an orphaned half. Empty edited rows detach any following soft-wrap link that would otherwise refer to an empty predecessor.

SPA (`ESC V`) selects ISO protection and enables marking; EPA (`ESC W`) disables subsequent marking without disabling protection of existing glyphs. SGR 0 does not change either state. Protection occupies a spare bit in the existing hot-cell flags, not a new style or per-cell allocation, and survives moves and reflow. Ordinary ECH/EL/ED honor protected glyphs only while ISO protection is selected. Protected erasure works on whole glyphs, including a range beginning on a wide continuation, and clears unprotected spans with one row-extent refresh.

DECSCA selects DEC protection instead. Ordinary erase then ignores protected marks; DECSEL/DECSED preserve them. Selective erase does not inherit ISO protection, retaining the pinned suite's passing behavior rather than reproducing xterm's annotated backward-compatibility exception. Both erase families ignore scrolling margins. Selective ED 2 preserves cursor, history and image placements. Ordinary ED 2 retains Dotty's existing cursor-home and image-discard behavior, discards actual history and its reported count, and preserves visible ISO-protected glyphs when applicable.

DECSTR and RIS clear active marking and the protection selector; saved-cursor restoration restores active marking, not the global selector. The separation follows [xterm's protection-state handling](https://github.com/ThomasDickey/xterm-snapshots/blob/master/charproc.c). DECSERA uses the same DEC/ISO distinction; its rendition-preserving behavior is described below.

DECSCL selects a stored level from 1–5: horizontal-margin mode requires level 4 or higher, DECRQM replies require level 3 or higher, and DECNCSM requires level 5. RIS restores level 5; DECSTR preserves the selected level. This is feature gating, not a claim of complete VT-model conformance or support for raw eight-bit C1 controls.

#### Rectangle copy, fill and erase

DECCRA, DECFRA, DECERA and DECSERA require VT level 4 or higher. Coordinates are one-based and inclusive: omitted/zero top and left default to the first position, while omitted/zero bottom and right default to the last. With DECOM off, the bounds are the physical page and ignore scrolling margins. With DECOM on, coordinates are offset once into the active vertical and effective horizontal margins. Each bound is clamped before reversed rectangles are rejected. Source and destination defaults are independent; copy clips its size to the destination without wrapping. Both page selectors resolve to Dotty's single active page, including selectors greater than one.

- **DECCRA** copies characters, rendition, protection and cell metadata without copying the source row's continuation flag. Directional row traversal and overlap-safe native span moves retain the original source even when rectangles overlap. Unwritten source cells become explicit destination spaces, retaining the written extent through subsequent editing and reflow.
- **DECFRA** fills with current SGR and selected protection, not the overwritten cell's rendition or the current OSC 8 hyperlink. Omitted `Pc` means space; explicit values must be 32–126 or 160–255. Explicit zero, controls, DEL, C1 and values above 255 are ignored. Valid characters use the selected DEC special-graphics mapping when active. Fill does not replace the last printed character used by REP.
- **DECERA** installs unstyled, unprotected spaces regardless of the protection selector.
- **DECSERA** removes characters while retaining visual attributes and stored protection. With DEC protection selected, protected glyphs survive; ISO protection does not prevent this command from erasing them.

These operations preserve cursor position, pending wrap, history, ring position and destination logical-line links. Wide glyphs cut by rectangle edges are repaired without orphan halves; selective erasure treats a protected wide glyph as a unit. Copy moves cold metadata only when present, and fill/erase remove character-specific hyperlink, grapheme and placeholder metadata from erased cells. Only affected destination rows are dirtied. Normal Kitty image-placement policy is unchanged.

Rendition semantics follow the DEC descriptions of [DECCRA](https://vt100.net/docs/vt510-rm/DECCRA.html), [DECFRA](https://vt100.net/docs/vt510-rm/DECFRA.html), [DECERA](https://vt100.net/docs/vt510-rm/DECERA.html) and [DECSERA](https://vt100.net/docs/vt510-rm/DECSERA.html). The pinned conformance assertions check rectangle contents; separate native tests and GUI pixel comparisons cover rendition, metadata, wide edges and reflow.

#### Column modes

DEC mode 40 permits mode 3 (DECCOLM) requests for 132 columns when set or 80 when reset. Mode 40 defaults off and survives DECSTR/DECSCL. When permission is off, both directions are no-ops. When on, mode 3 immediately homes the cursor, clears pending wrap and restores full vertical/horizontal margin bounds, including when the requested width is unchanged. It clears visible cells and visible image placements, not history; protection does not exempt cells from this structural clear. At VT level 5, mode 95 (DECNCSM) suppresses that clear but not homing or margin reset. Explicit partial margins remain bounded during resize; full-width margins follow the accepted new width.

Geometry uses the existing `WindowResizeRequested` host route. The GUI applies it asynchronously and still requires `vt.windowResizeAllowed`, one tab, one pane and a normal window. Permission denial leaves actual grid/window/PTY dimensions unchanged; it does not postpone the terminal-side clear/home operation. DECRQM for mode 3 reports actual 132-column geometry, not an unaccepted request. RIS requests 80 columns when column changes were permitted and the actual width was 132, then leaves mode 40 off. Mode 95 is reset by both DECSTR and RIS. This ordering follows [xterm's column-mode implementation](https://github.com/ThomasDickey/xterm-snapshots/blob/master/charproc.c).



#### DECSTR and RIS reset sets

DECSTR (`CSI ! p`, `TerminalAdapter.OnSoftReset`) and RIS (`ESC c`, `TerminalAdapter.OnFullReset`) share one core (`ResetTerminalModesCore` plus `TerminalBuffer.ApplySoftResetCore`) so the two cannot drift.

| State | DECSTR | RIS |
|---|---|---|
| Cursor visibility (DECTCEM) and shape (DECSCUSR) | reset (visible; shape back to the configured `cursor.shape`/`cursor.blink`, as DECSCUSR 0) | reset |
| Vertical/horizontal margins (DECSTBM/DECSLRM), DECLRMM | full-screen bounds, mode off; cursor is **not** homed | reset |
| Origin mode (DECOM), autowrap (DECAWM) | reset (off, on) | reset |
| Reverse-wrap modes 45 and 1045 | reset (off) | reset |
| Saved cursor (DECSC) and saved SGR attributes | active screen's slot reset to home / default; hidden screen's slot kept | both reset |
| Current SGR attributes (including an open OSC 8 link) | reset | reset |
| Active character protection and ISO/DEC erase selector | reset; existing cell markers remain with screen contents | reset; cells cleared |
| DECCKM, DECKPAM/DECNKM (66), modifyOtherKeys level | reset | reset |
| ANSI insert (IRM, 4) and newline (LNM, 20) modes | reset (off) | reset |
| Column-change permission (40), no-clear (95) | permission kept, no-clear reset | both reset; request 80 columns if permitted and previously 132 |
| MoreFix tab compatibility (41) | kept | reset (off) |
| ANSI palette (OSC 4 indexed overrides) | reset | reset |
| Special attribute colors (OSC 4 aliases / OSC 5, OSC 6 modes) | color overrides reset; mode bits preserved | color overrides reset; mode bits preserved |
| OSC 10/11 default fg/bg overrides | kept | reset |
| Reverse video (DECSCNM, mode 5) | kept | reset |
| Screen contents, scrolled-line count, cursor position | kept | cells cleared, count zeroed, cursor homed |
| Alternate screen | kept | left (main screen restored, both screens cleared) |
| Tab stops | kept | back to the defaults |
| Bracketed paste (2004), mouse modes and encodings, focus reporting (1004) | kept | reset |
| Alternate scroll (1007) | kept | set (its power-on state) |
| Synchronized update (2026) | kept | ended |
| Kitty keyboard flags and stacks | kept | reset |
| Prompt marks (OSC 133) | kept | cleared |
| DECBKM (67) | **kept** | reset |
| Visibility reports (2033) | **kept** | disabled |
| Window and icon titles | kept | kept |
| Title encoding modes | kept | native UTF-8 set/query defaults; hex modes off |
| KAM (mode 2), SRM (mode 12), NRC (mode 42), font-shift mode (mode 35) | kept | reset to their documented RIS power-on states |
| Printer modes DECPFF (18) and DECPEX (19) | kept | reset to their power-on states; printer output destination is host configuration |
| DECSACE/DECCARA/DECRARA rectangle attributes | current buffer rendition/effects remain | reset as terminal rendition and screen state are reset |
| Host permissions (`vt.*`) | not terminal state | not terminal state |

DECSTR also does not touch the checksum (`vt.checksumReportEnabled`) switch, which is host configuration rather than terminal state.

#### Status reports (DSR, CPR and DECXCPR)

- `CSI 5 n` answers `CSI 0 n` (ready).
- `CSI 6 n` (CPR) answers `CSI row ; col R`, 1-based.
- `CSI ? 6 n` (DECXCPR) answers `CSI ? row ; col ; page R`. The page is always `1` because Dotty has a single page.
- `CSI 15 n` (printer status) answers `CSI 10 n` when a file printer is configured and available, `CSI 11 n` when configured but not ready, and `CSI 13 n` when no file printer is configured. Other unimplemented status requests receive no reply.

#### Window operations (`CSI Ps ; Ps ; Ps t`)

| Request | Result |
|---|---|
| `CSI 13 t` | `CSI 3 ; x ; y t`: window position; `0 ; 0` until the host reports one |
| `CSI 14 t` | `CSI 4 ; height ; width t`: text area in pixels (grid size times cell size unless the host overrides it) |
| `CSI 15 t` | `CSI 5 ; height ; width t`: screen size in pixels; the text-area size until the host reports the monitor |
| `CSI 16 t` | `CSI 6 ; height ; width t`: cell size in pixels (default 8 x 16 until the host reports the real one) |
| `CSI 18 t` | `CSI 8 ; rows ; cols t`: text area in characters |
| `CSI 19 t` | `CSI 9 ; rows ; cols t`: the terminal grid (Dotty has no larger character screen) |
| `CSI 20 t`, `CSI 21 t` | **silent by default**; with `vt.titleReportEnabled`, return stored icon/window title as `OSC L/l payload ST`, respectively |
| `CSI 8 ; rows ; cols t` | resize request (below) |
| `CSI Ps t`, `Ps >= 24` | resize request to `Ps` rows, columns unchanged (DECSLPP) |
| `CSI 1 t` / `CSI 2 t` | restore / minimize the native window, only when `vt.windowControlAllowed` is true |
| `CSI 3 ; x ; y t` | move the native window when `vt.windowControlAllowed` is true; zero/omitted coordinates leave that axis unchanged |
| any other `Ps` | ignored |

The geometry replies (`CSI 13/14/15/16/18/19 t`) are unconditional; they only read state. Title reports have their own default-off permission, independent of resizing and checksums. A resize **request** changes the window, so the adapter merely clamps it to `[0, 1000]` rows and columns (0 keeps that dimension; a request of 0 x 0 is dropped) and raises `WindowResizeRequested`; whether it is honoured is the host's decision (`VtHostPolicy.TryComputeResize`). It is ignored when:

- `vt.windowResizeAllowed` is false (the default);
- there is more than one tab, or the active tab has split panes;
- the window is maximized, fullscreen or minimized;
- the requested size equals the current grid.

Otherwise the host queues the request (`VtResizeRequestQueue`: a later non-zero dimension overrides, 0 keeps), and the next frame sets the client size that holds the requested cells plus padding and tab-bar rows. The request is asynchronous, so a `CSI 18 t` sent in the same write can still report the old grid. The conformance host honours requests synchronously instead; see [Conformance](Conformance.md).

#### Visibility reports (mode 2033 and `CSI ? 998 n`)

Mode 2033 reports when the terminal becomes visible or hidden. Reports are `ESC [ ? 999 ; 1 n` (potentially visible) and `ESC [ ? 999 ; 2 n` (not visible).

- `CSI ? 2033 h` always answers with the current state immediately, even when already enabled; `CSI ? 2033 l` never reports.
- While enabled, a report is emitted on every **change** of visibility.
- `CSI ? 998 n` is a one-shot query answered regardless of mode 2033 and leaves the mode unchanged.
- RIS disables mode 2033; DECSTR does not.
- Host rule (`VtHostPolicy.IsSessionVisible`): a session is potentially visible when its window is not minimized and its tab is the active one (every pane of the active tab counts). The window counts as minimized when GLFW reports it iconified or the framebuffer has zero size. GLFW cannot observe minimizing on Wayland, so there only tab switches and zero-size framebuffers change visibility. Unknown counts as visible.

#### DECRQCRA (rectangular checksum)

`CSI Pi ; Pg ; Pt ; Pl ; Pb ; Pr * y` is answered with `DCS Pi ! ~ XXXX ST` (`XXXX` four uppercase hex digits, `Pi` echoed). Because the reply lets a program read the screen back, it is **off by default**: with `vt.checksumReportEnabled` false no reply is sent at all.

The checksum is the plain 16-bit sum of the code points in the rectangle (xterm's `checksumExtension` with positive, no-attributes, no-trim, drawn and byte flags; not negated, no attribute bits). An unwritten cell counts as a space (0x20), the continuation half of a wide character is skipped, and only the base code point of a combining cluster counts. Omitted bottom/right bounds default to the current coordinate area's edges, and oversized bottom/right bounds are clipped. With origin mode set, both axes are relative to the scroll region, including horizontal margins when DECLRMM is enabled. An empty or out-of-range rectangle, or a page other than 0 or 1, sums to 0.

#### Printer control

CSI `0 i` prints the current display to the configured `printing.outputFile`
destination. With DECPRIVMODE DECPEX (private mode 19) reset, it prints the
scrolling region; set, it prints the full page. DECPFF (private mode 18)
controls appending a form feed. Printer status requests `CSI 5 i` and `CSI ? 5 i`
report ready (`CSI 10 n`) when a destination is configured and available, not
ready (`CSI 11 n`) otherwise; `CSI ? 15 n` reports no printer (`CSI ? 13 n`).
Printing is disabled unless a file destination is configured. See
[Configuration](Configuration.md#printing).

#### Combining cap, grapheme clusters, cell width and the grapheme store

- A cell holds at most one base code point plus **64** combining code points (`GraphemeHelper.MaxCombiningCodepointsPerCell`, the Ghostty 1.4 limit). Further marks are consumed and dropped, never splitting a surrogate pair.
- Multi-code-point clusters live in one process-wide, append-only, de-duplicating `GraphemeStore` addressed by the 16-bit index in each cell: at most 32,767 distinct clusters, shared by all sessions. Entries are **never evicted**, because any live or scrollback cell may still hold an index. When the store is full a new cluster is not stored and the cell degrades to its base code point (a mark appended to an existing cell leaves the cell as it was). Indices never wrap or alias.
- The cluster string cache in `BufferTextWriter` is separately bounded (4096 entries, cleared when exceeded).
- Mode 2027 (grapheme clustering) is permanently set: `CSI ? 2027 h/l` is accepted and ignored and DECRQM reports `3`. Segmentation is .NET's `StringInfo` (UAX #29 extended grapheme clusters), pinned by `GraphemeSegmentationTests`; the runtime's Unicode version, not Dotty, decides boundaries.
- Clusters span writes. When text arrives in a later parser run or `Feed` call, its start is segmented together with the cluster in the cell before the cursor on the cursor row (the cursor cell itself while a wrap is pending). If they form one cluster, the continuation is appended to that cell (within the 64-mark cap) and the cell is re-measured: a late U+FE0F widens it in place, or moves it to the next line when it fills the last column and autowrap is on (with autowrap off it stays one cell); a late U+FE0E narrows it and the cursor moves back. ZWJ sequences, regional-indicator pairs, keycaps, skin tones, Hangul jamo and Prepend characters (such as U+0600 followed by a digit) all re-join, so splitting the bytes anywhere leaves the same screen. A continuation never reaches into the previous row. Zero-width text that does not continue the cluster (U+200B, bidi marks, or a mark at column 0) is still appended to the previous non-empty cell, which may be on the previous row.
- **Limitation:** with autowrap off the cursor stays on the last column after printing there, so a continuation that arrives later joins the cell before it, as zero-width marks always have.
- Cell width comes from `UnicodeWidth`, whose tables (`UnicodeWidthTables.g.cs`) are generated from Unicode 16.0.0 by `tools/gen-width-tables.py` (Python's `unicodedata`; `python3 tools/gen-width-tables.py --check` verifies the committed tables and their golden). A scalar is 0 cells for General_Category Cc, Cf, Mn and Me and for Hangul medial/final jamo (U+1160–U+11FF, U+D7B0–U+D7FF), except U+00AD SOFT HYPHEN, which is 1; 2 cells for East_Asian_Width W or F (this includes every Emoji_Presentation character); otherwise 1, including East Asian ambiguous width. A cluster takes the width of its first non-zero-width scalar, then: a regional-indicator pair is 2 (a lone indicator 1); U+FE0F directly after one of the 371 emoji-variation bases of `emoji-variation-sequences.txt` makes it 2 and U+FE0E after such a base makes it 1 (no other base changes); ZWJ and skin-tone modifiers never widen. A cluster with no visible base is attached to the previous cell or, at the start of the screen, takes one cell. A wide cluster that does not fit before the right edge leaves the last column blank and wraps, scrolling at the bottom of the scroll region.

#### DECSET 47, 1047, 1048 and 1049

`CSI ? 47 h/l` retains alternate text and images on entry and exit. `CSI ? 1047 h/l` preserves them on entry but clears them before returning to main. Neither saves or restores the cursor. `CSI ? 1049 h/l` saves the main cursor and rendition in the ordinary main-screen DECSC slot, clears the alternate screen on entry, and clears it before restoring main and its saved cursor/rendition on exit. Main contents and history remain intact. Per-screen Kitty keyboard state follows the active screen.

`CSI ? 1048 h/l`, `ESC 7/8`, and SCO save/restore use independent main/alternate saved slots containing cursor position, visibility, pending wrap, origin mode, active protection marking and SGR attributes. Restoring a slot does not consume it. Both slots are remapped against their own screen during resize, including the hidden main screen. The global ISO/DEC protection selector is not part of a saved slot. DECRQM for 47/1047/1049 reports live alternate-screen state regardless of which variant entered it; 1048 is an action and reports permanently reset (`4`).
**Saved-cursor limits:** when no saved snapshot exists, restore leaves the cursor position and origin mode unchanged; it does not apply DEC's home-position/DECOM-reset fallback. G0/G1 designations and shift selection are not captured or restored. These are existing limits, not behaviors covered by the current pinned conformance pass count.

#### DECRQM status table

DECRQM (`CSI ? Pd $ p`, `CSI Pd $ p`) answers `CSI ? Pd ; Ps $ y` / `CSI Pd ; Ps $ y` with `Ps`: 0 not recognized, 1 set, 2 reset, 3 permanently set, 4 permanently reset. The table lives in `TerminalAdapter.ModeReports.cs`.

| Private mode | Reply |
|---|---|
| 1 (DECCKM), 5 (DECSCNM), 6 (DECOM), 7 (DECAWM), 25 (DECTCEM), 66 (DECNKM), 67 (DECBKM) | 1 or 2, live state |
| 45 (normal reverse wrap), 69 (horizontal-margin mode), 1045 (extended reverse wrap) | 1 or 2, live state |
| 3 (DECCOLM) | 1 only when the actual grid has 132 columns; otherwise 2 |
| 40 (column-change permission), 41 (MoreFix) | 1 or 2, live state |
| 95 (DECNCSM) | 1 or 2 at VT level 5; 0 at lower levels |
| 47, 1047, 1049 (alternate screen) | 1 or 2, live state |
| 9, 1000, 1002, 1003 (mouse tracking), 1005, 1006, 1015, 1016 (mouse encoding), 1007 (alternate scroll), 1004 (focus) | 1 or 2, live state |
| 2004 (bracketed paste), 2026 (synchronized update), 2033 (visibility reports) | 1 or 2, live state |
| 2 (DECANM; there is no VT52 mode), 2027 (grapheme clustering) | 3 |
| 4, 8, 12, 18, 19, 42, 80, 1048 | 4: recognized and ignored (or, for 1048, an action) |
| 11, 16, 53, 59, 60, 61, 64, 68, 73, 81 | 4: xterm "recognized, unimplemented" modes |
| anything else | 0 |

| ANSI mode | Reply |
|---|---|
| 4 (IRM), 20 (LNM) | 1 or 2, live state |
| 1, 2, 3, 5, 7, 10, 11, 13 through 19 | 4: recognized but not implemented |
| 12 (SRM) | 3: Dotty never echoes locally |
| anything else | 0 |

The reply is built without allocation; counts in every parameter saturate (see Parameter Parsing) so an enormous mode number reports `0` rather than aliasing a real mode.

#### Cursor and character-edit clamping

Saturated CSI parameters never overflow cursor arithmetic. Relative cursor moves add with saturation (`MoveCursorBy`), ICH (`CSI @`) and DCH (`CSI P`) clamp the count to the cells remaining from the cursor to the right margin, and ECH (`CSI X`) computes its end column in 64-bit before clamping. REP (`CSI b`) is bounded; see [REP bound](#rep-bound).

### Parameter Parsing

CSI parameters are parsed using a fast, span-based numeric parser that handles:

- **Semicolon-separated values**: `ESC[1;31;40m` → `[1, 31, 40]`
- **Private markers**: `?`, `>`, `<` prefixes for mode sequences
- **Default values**: Empty parameters default to 0 or 1 as per spec
- **Colon sub-params**: Basic support for SGR colon-separated RGB values
- **Saturating integers**: each numeric field accumulates decimal digits and saturates at `int.MaxValue` instead of wrapping, so an enormous value can never alias a real mode, OSC code, or small count. The same rule applies to OSC codes, DECSTR/DECRQCRA fields, and the DEC private modes. At most the first eight numeric CSI fields are retained.

```csharp
// Example: Parsing "ESC[38;5;208m" (256-color foreground)
// paramBytes = "38;5;208"
// Result: parsedParams = [38, 5, 208], paramCount = 3
```

---

## State Machine Design

The parser uses an **implicit state machine** rather than an explicit state enum, driven by the position within the input span and local variables. This design reduces branching and improves cache locality.

### State Tracking

| State Variable | Purpose |
|----------------|---------|
| `_leftover[]` | Buffer for incomplete sequences across `Feed()` calls |
| `_leftoverLen` | Number of valid bytes in leftover buffer |
| `_charset` | Current character set (ASCII or DEC Special Graphics) |
| `_charScratch[]` | Reusable buffer for UTF-8 → char conversion |
| `_throughputMode` | Benchmark mode - skips rendering for speed tests |

### Fragment Handling

When a sequence is split across multiple `Feed()` calls:

```csharp
// Scenario: ESC[31m split as ESC[ (first call) and 31m (second call)
// First call detects incomplete CSI, saves to _leftover
SaveLeftover(inputSpan.Slice(seqStart));  // Saves "ESC["

// Second call concatenates leftover with new bytes
byte[] concat = new byte[_leftoverLen + bytes.Length];
Buffer.BlockCopy(_leftover, 0, concat, 0, _leftoverLen);
bytes.CopyTo(concat.AsSpan(_leftoverLen));
inputSpan = concat;  // Now contains "ESC[31m"
```

### State Transitions

```
                    ┌─────────────────────────────────────────────────────┐
                    │                    Parser States                      │
                    └─────────────────────────────────────────────────────┘

    ┌──────────────┐     ESC (0x1B)      ┌──────────────┐
    │   Ground     │────────────────────▶│   Escape     │
    │  (printing)  │                    │  (got ESC)   │
    └──────────────┘                    └──────┬───────┘
           │                                    │
           │ C0 controls                        │ Intermediate bytes
           │ (BS, TAB, LF, CR, etc.)            │ ( [, ], (, ), =, >, etc.)
           ▼                                    ▼
    Handler callbacks               ┌────────────────────────────────────┐
                                    │      Sequence Type Detection       │
                                    ├─────────┬──────────┬───────────────┤
                                    │   CSI   │   OSC    │   Others      │
                                    │  ([)    │   (])    │  (7,8,c,etc)  │
                                    ▼         ▼          ▼
                              ┌─────────┐ ┌─────────┐ ┌──────────┐
                              │  CSI    │ │   OSC   │ │  Simple  │
                              │ Params  │ │ Payload │ │ Sequence │
                              │ Scan    │ │ Scan    │ │ (1 byte) │
                              └────┬────┘ └────┬────┘ └────┬─────┘
                                   │           │           │
                                   ▼           ▼           ▼
                            Final byte    BEL (0x07)   Handler
                            (0x40-0x7E)   or ESC\     callback
                                 │           │
                                 ▼           ▼
                            CSI Handler  OSC Handler
```

### APC states (Kitty graphics)

`ESC _` enters `SequenceState.ApcFirst`, which looks at one byte:

```
ESC _ ──▶ ApcFirst ── 'G' and handler.KittyGraphicsEnabled ──▶ ApcControl
              │                                                     │  ';'        ──▶ Begin(control) ──▶ ApcPayload
              │                                                     │  0x9C       ──▶ Begin(control), End(true)
              │                                                     │  ESC        ──▶ ApcEscape
              │                                                     │  CAN / SUB  ──▶ reset (handler never called)
              │                                                     │  other C0   ──▶ abort, execute the control
              │                                                     │  > 512 B    ──▶ DiscardString (handler never called)
              └── anything else ──▶ DiscardString (PM/SOS path)     ▼
                                                        ApcPayload ── run of non-C0 bytes ──▶ OnKittyGraphicsPayload(run)
                                                          │  0x9C ──▶ End(true)
                                                          │  ESC  ──▶ ApcEscape
                                                          │  CAN / SUB ──▶ End(false)
                                                          │  other C0  ──▶ End(false), execute the control
                                                          ▼
                                                         ApcEscape ── '\' ──▶ End(true) (or Begin + End(true) when no ';' was seen)
                                                                   └── other ──▶ End(false) if begun, the byte starts a new ESC sequence
```

Contract with the handler (`ITerminalHandler`): `KittyGraphicsEnabled` is
asked once, at the byte after `ESC _`; `OnKittyGraphicsBegin(control)` receives the
bytes up to the first `;` (or up to the terminator when there is none);
`OnKittyGraphicsPayload(base64)` receives the payload in runs of any size (the
bulk path hands over everything up to the next C0 control or `0x9C` in one call);
`OnKittyGraphicsEnd(completed)` closes the command, with `false` for a cancelled
one. End is called only after Begin, so a command that was discarded before its
control block finished never reaches the handler. Replies and image state are in
[Kitty graphics](KittyGraphics.md).

Limits and rules specific to APC `G`:

- `MaxApcControlBytes` = **512** bytes of control data. A longer block is
  discarded to its terminator without any callback and without a reply.
- The payload is not buffered by the parser; the graphics controller accumulates
  it, bounded by its own caps.
- Unlike OSC and DCS, `0x9C` is accepted as ST inside an enabled graphics
  APC (control and payload are ASCII, so no UTF-8 continuation byte can occur
  there). `DEL` and bytes above `0x7F` are passed to the payload as far as the
  parser is concerned; the base64 decoder rejects them.
- A disabled protocol (`graphics.kittyProtocol` = `false`), any APC whose first
  byte is not `G`, and PM/SOS strings are consumed to ST and dropped without
  buffering.

### String states and controls inside sequences

| Situation | Behaviour |
|---|---|
| `ESC` inside OSC or DCS | Only `ESC \` (ST) terminates the string and dispatches it. `ESC ESC` is not payload: the second `ESC` may still begin ST. `ESC` followed by any other byte abandons the string **without dispatch** and that byte is processed as an escape sequence. |
| `BEL` | Terminates and dispatches an OSC. Inside DCS it is a control that aborts the string. |
| `CAN` (0x18) / `SUB` (0x1A) | Abandon the current string or sequence without dispatch. |
| Any other C0 control inside OSC/DCS/APC/PM/SOS | Aborts the string without dispatch, then executes as a control. |
| C0 control inside CSI or after a bare `ESC` | Executes (BEL, BS, TAB, LF/VT/FF, CR) and the sequence continues; the control never becomes part of the parameter string. DEL inside CSI is ignored the same way. Other controls are ignored. |
| C1 `0x9C` | Not an ST. Bytes `0x80-0x9F` are UTF-8 continuation bytes in the stream and are never interpreted as C1 controls; only `ESC \` ends a string. |
| APC (`ESC _`) not followed by `G`, or `ESC _ G` while the protocol is disabled; PM (`ESC ^`); SOS (`ESC X`) | Consumed up to ST and discarded; nothing is buffered or dispatched. Enabled `ESC _ G` strings are handed to the Kitty graphics handler (see [APC states](#apc-states-kitty-graphics)). |
| DCS longer than 4 KiB | Consumed until ST without dispatch (only short XTGETTCAP queries are captured). |
| CSI longer than 256 parameter bytes, OSC longer than 1 MiB | Consumed until the terminating byte or ST without dispatch. |
| `ESC c` (RIS) | Also resets the parser's charset to ASCII. |
| `ESC _ G` control data longer than 512 bytes | Consumed until ST without any handler call. |

The sequence buffer (`_leftover`) grows on demand up to the string's cap. When a sequence ends or is abandoned and the buffer exceeds 4096 bytes it is reallocated back to 32 bytes, so one large OSC 52 does not pin a megabyte for the life of the session.

**Bulk OSC scan.** While an OSC (or a discarded over-cap OSC) is open, the parser consumes payload bytes up to the next C0 control (0x00-0x1F) in one scan; the stop byte is left to the per-byte state machine, so BEL/ST/CAN/SUB/ESC/abort semantics are unchanged. When the whole OSC (payload plus BEL or `ESC \`) lies inside one input chunk and nothing is buffered, it is dispatched straight from the input without being copied into the sequence buffer. A payload that would cross the 1 MiB cap is discarded exactly as the per-byte path does.

**OSC 52 from bytes.** OSC 52 payloads are handled by `OnOperatingSystemCommandUtf8`: selectors and base64 are decoded directly from UTF-8 bytes without an intermediate base64 `string`. Canonical padded data uses the .NET decoder; noncanonical data uses the same bounded output buffer with xterm-compatible decoding. The char-based handler follows the same rules. See [OSC 52](#osc-52-clipboard) for selectors, limits and host authorization.

---

## Input Handling Flow

### Main Feed Loop

The `Feed()` method processes input in a single pass with minimal allocations:

```csharp
public void Feed(ReadOnlySpan<byte> bytes)
{
    // 1. Handle any leftover from previous call
    if (_leftoverLen > 0) {
        // Concatenate leftover with new bytes
        // Use rented array for concatenation
    }

    // 2. Main processing loop
    while (i < inputSpan.Length)
    {
        byte b = inputSpan[i];

        // 3. Branch on byte value
        if (b == ESC)      { /* Handle escape sequence */ }
        else if (b == BEL) { Handler?.OnBell(); i++; }
        else if (b == BS)  { Handler?.OnCursorBack(1); i++; }
        // ... other C0 controls
        else if (b >= 0x20 && b != 0x7F) {
            // 4. Collect printable run
            // Fast path for ASCII, decode UTF-8 for non-ASCII
            HandlePrintableRun(inputSpan, ref i);
        }
        else {
            // 5. Ignore other control characters
            i++;
        }
    }

    // 6. Clear leftover if we completed successfully
    _leftoverLen = 0;
}
```

### Printable Text Handling

Text runs are collected and decoded efficiently:

```csharp
// Fast path: Pure ASCII with no charset translation
if (!hasNonAscii && _charset != Charset.DecSpecialGraphics)
{
    Span<char> asc = GetScratch(run.Length, out char[]? rented);
    for (int j = 0; j < run.Length; j++) { asc[j] = (char)run[j]; }
    Handler?.OnPrint(asc);
    ReturnScratch(rented);
}
else
{
    // Slow path: UTF-8 decode + possible charset translation
    DecodePrintableRun(run);
}
```

### Memory Management

The parser uses several strategies to minimize allocations:

| Strategy | Implementation | Purpose |
|----------|----------------|---------|
| **Scratch buffers** | `char[] _charScratch` | Reusable buffer for char conversion |
| **Array pooling** | `ArrayPool<char>.Shared` | Large temporary buffers for UTF-8 decoding |
| **Span-based parsing** | `ReadOnlySpan<byte>` | Zero-copy parameter extraction |
| **Leftover buffer** | `byte[32] _leftover` | Fixed-size buffer for sequence fragments |

---

## Special Sequence Handling

### OSC (Operating System Command)

Format: `ESC ] <code> ; <payload> BEL` or `ESC ] <code> ; <payload> ESC \`

The parser supports OSC sequences for:

| Code | Purpose | Handler |
|------|---------|---------|
| 0 | Set icon name and window title | `OnSetTitle(0, rawBytes)`; both titles share the decoded value |
| 1 | Set icon name | `OnSetTitle(1, rawBytes)`; independent protocol metadata, not an OS icon change |
| 2 | Set window title | `OnSetTitle(2, rawBytes)`; raises the existing title-change event for the host |
| 4 | Set/query session palette (`index ; spec` pairs; `?` queries) | OSC 4 sets are silent. Indices 0–255 address indexed colors, with existing foreground, background, underline, and active/saved rendition colors remapped by palette index identity. Indices 256–260 alias OSC 5 special attribute colors (bold, underline, blink, reverse, italic); queries retain the requested spelling/index. Replies use `rgb:rrrr/gggg/bbbb` with 16-bit components and ST. |
| 7 | Working directory (shell integration) | `OnOperatingSystemCommand(7, uri)`; strict acceptance rules in [OSC 7](#osc-7-working-directory). |
| 8 | Hyperlink (OSC 8) | `OnOperatingSystemCommand(8, params)`; validated at ingest, see [OSC 8](#osc-8-hyperlinks) |
| 9 | iTerm2 notifications | (future) |
| 10–12 | Query dynamic foreground/background/cursor colors | OSC 10/11 queries return `rgb:rrrr/gggg/bbbb` with ST; sets store per-session foreground/background overrides without replying and render per pane. Sequential fields advance the code for each field, including empty or invalid fields. OSC 12 queries retain the legacy white reply; cursor-color sets/resets are accepted but not rendered. |
| 13-19 | Set foreground/background/highlight colors | (future) |
| 52 | Manipulate selection data (clipboard write and read) | `OnOperatingSystemCommand(52, ...)`; see [OSC 52](#osc-52-clipboard) |
| 777 | rxvt extension notifications | (future) |
| 1337 | PromptMark (shell integration) | `OnPromptMark(level)` — tracks shell prompt start/end via `OSC 1337 ; A` / `OSC 1337 ; B` |
| 104 | Reset palette entries | Listed indices reset OSC 4 indexed or special-color overrides; 256–260 alias special colors. Empty payload resets both tables. |
| 105 | Reset special attribute colors | Direct indices 0–4 reset special colors; empty payload resets only the special-color overrides. |
| 106 | Reset special attribute color modes | Mode 0–4 entries disable the corresponding mode; nonzero entries enable it. Empty payload resets all modes. |
| 110/111 | Reset dynamic foreground/background | Clear the session's OSC 10/11 override to the actual theme color; no reply. |
| 112 | Reset cursor color | Accepted without effect; cursor color is not rendered. |

Color specifications support `#` forms of 3, 6, 9, or 12 hexadecimal digits (X11 left-aligned component semantics, unlike CSS theme hex), `rgb:` components, `rgbi:`, `CIEXYZ:`, `CIExyY:`, `CIEuvY:`, `CIELab:`, `CIELuv:`, and `TekHVC:`. The latter color spaces use the fixed portable libX11 Xcms Tektronix 19-inch Sony CRT/Tek4300/4800 fallback profile and its gamut/conversion behavior; this is deterministic software conversion, not display calibration or a promise to match arbitrary calibrated screens.

OSC 6 configures whether special attribute colors are applied while rendering: mode 0–4 enables/disables bold, underline, blink, reverse, or italic respectively; mode 5 allows special colors to override explicit colors. All modes default off. When several special modes apply, foreground selection priority is blink, bold, underline, then italic. Reverse selects the special reverse background independently, then inverse swaps foreground and background. SGR 58 underline/decorative color is independent of special underline color.

Palette and special-color overrides are scoped to one terminal session. OSC 4 palette indices 0–255 refer to palette identity, not RGB-value equality. OSC 4/5/6/10/11 sets are silent; queries preserve ordered per-query replies. RIS and DECSTR clear indexed and special-color overrides but preserve special-color mode bits. DECSTR preserves OSC 10/11 overrides; RIS clears them. Special-color defaults track theme foreground independently of OSC 10 overrides, while explicit special-color overrides persist across theme changes until reset. OSC 104 empty resets indexed and special overrides; OSC 105 empty resets special overrides only. Changing the ANSI theme baseline clears indexed OSC 4 overrides; reapplying an identical baseline is a no-op and preserves them.

OSC colour replies use lowercase hexadecimal (`rgb:rrrr/gggg/bbbb`) with
four digits per channel, matching xterm's canonical query response. A request
containing several queries produces a separate, ordered ST-terminated reply
for each one, so a following CSI report remains independently readable.

#### Title modes and replies

OSC 0/1/2 payloads reach the adapter as bytes so Latin-1 is not accidentally decoded as UTF-8 first. Titles are limited to `TerminalAdapter.MaxTitleChars` (4096 UTF-16 units), without splitting a surrogate pair. C0, DEL and C1 control characters are removed before retention, including controls obtained from hex decoding. Malformed or odd-length hex input is rejected without changing either title. Repeated unchanged titles reuse retained strings; OSC 0 shares its value between both fields.

`CSI > Pm t` enables and `CSI > Pm T` disables the selected features: 0 sets titles from hex, 1 reports titles as hex, 2 sets titles from UTF-8 rather than Latin-1, and 3 reports titles as UTF-8 rather than Latin-1. Set/query encodings and hex flags are independent. With no parameters, either command restores Dotty's native defaults: UTF-8 set/query enabled, hex disabled. RIS restores those defaults while preserving title text and host permissions; DECSTR preserves the modes as well.

With `vt.titleReportEnabled` explicitly enabled, `CSI 20 t` returns `ESC ] L <icon title> ESC \` and `CSI 21 t` returns `ESC ] l <window title> ESC \`. These report stored protocol metadata, not fabricated external window state. `RawReplyBytesRequested` carries exact bytes through `TerminalSession`'s ordered PTY input queue, so Latin-1 reports are not re-encoded as UTF-8. The default-off permission remains important even with control filtering: title reports return application-controlled text to the input stream.

#### OSC payload limit

The OSC payload buffer is capped at 1 MiB (1,048,576 bytes, counted after `ESC ]` and including the `52;c;` style prefix). It was 64 KiB before OSC 52 transport needed larger strings. A longer string is consumed to ST and **discarded without dispatch**. Because the cap covers the whole payload, the largest OSC 52 write the parser can deliver carries slightly less than 1,048,576 base64 characters. The buffer shrinks back after each sequence (see above).

#### OSC 52 (clipboard)

Format: `ESC ] 52 ; Pc ; Pd ST`. Handled by `TerminalAdapter.HandleOsc52`; the adapter only decodes and raises events, and policy lives in the host.

| Part | Behaviour |
|---|---|
| `Pc` selectors | `c` = system clipboard; `p` = native PRIMARY; `s` = configured SELECT target (normally PRIMARY on X11, clipboard otherwise); `0` = X11 CUT_BUFFER0 only. Empty means `s0`: writes target SELECT and CUT_BUFFER0; reads try SELECT, then CUT_BUFFER0 if unavailable, not if genuinely empty. Selectors combine; unknown characters are ignored. No recognized selector or no `;` means no request. |
| `Pd` = `?` | Clipboard **read** request: raises `TerminalAdapter.ClipboardReadRequested(ClipboardReadRequest)` (concrete adapter only, not on `ITerminalHandler`); `TerminalSession` forwards it. |
| `Pd` empty | Write of the empty string. |
| other `Pd` | Decode using xterm Paste64 rules, then decode UTF-8 and raise `ClipboardWriteRequested(ClipboardWriteRequest(selection, text))`. Ignore characters outside `A-Z`, `a-z`, `0-9`, `+`, `/`, including padding; emit complete bytes and discard incomplete trailing bits. A payload producing no bytes is an empty write, still subject to authorization. |

These rules match xterm's [`AppendToSelectionBuffer` and `CompleteSelection`](https://raw.githubusercontent.com/ThomasDickey/xterm-snapshots/master/button.c), including unpadded input and non-alphabet characters in the middle or at the end. They apply only to OSC 52, not graphics-protocol base64. The existing whole-OSC input cap and 786,432-byte decoded-write ceiling still apply.

**Write policy (host, `DottyWindowHost.EnqueueClipboardWrite`):** dropped unless `clipboard.allowOsc52Write` is true. PRIMARY and CUT_BUFFER0 writes require their real native backends; an unavailable target never aliases or clobbers the system clipboard. SELECT resolves according to host configuration. Text exceeding `MaxOsc52WriteBytes` = 786,432 bytes is rejected, with host checks on UTF-16 length and UTF-8 byte count in addition to the decoder ceiling. Accepted writes are coalesced (a session's newer write replaces its pending one) and queued with at most 32 entries and 4 MiB of pending text; excess is dropped. The Lua `clipboard_write` hook can still veto. See [Configuration](Configuration.md#clipboard).

**Read policy (`osc52Read`, `ClipboardReadService`):** `deny` (default), `ask`, `allow`; unknown values mean `deny`.

- Requests with no available selected native target, mode `deny`, or a closing window are dropped before queueing. An explicitly selected clipboard has read priority; `0` alone reads only CUT_BUFFER0, never PRIMARY or the clipboard. Denials never produce a reply: not for policy, a Lua `clipboard_read` veto, a user "Deny", an oversize value (more than `osc52ReadLimitBytes` UTF-8 bytes), unavailable data, or expiry. A genuinely empty value answers with an empty payload and does not trigger the `s0` fallback.
- Pending requests live in a queue of at most 4 requests in total and 1 per session (the oldest wins; later ones are dropped), and expire after 30 s. They are served on the UI thread only while the window is focused and the requesting session's tab is the active tab; under `ask` they also wait while another menu or prompt is visible. Lowering the mode to `deny` while requests wait clears the queue.
- The selected value is sampled once. `allow` replies with `ESC ] 52 ; Pc ; base64(text) ESC \`, using the request's recognized selector field (`s0` for the default), always ST-terminated. `ask` shows a centered host prompt naming the sanitized, truncated tab title and previewing the same snapshot that will be sent, with **Deny** focused; Escape, click-outside, or replacement by another menu send nothing. Choosing **Allow** re-validates first: if `osc52Read` changed to `deny` or the session left the window, no reply is sent.

#### OSC 8 (hyperlinks)

Format: `ESC ] 8 ; params ; URI ST`; an empty URI closes the link. `HyperlinkUri` validates at ingest and rejects the whole link (never strips or rewrites):

- `params` must not contain controls, bidi/invisible format characters, or surrogates, and the `id=` value must not exceed 250 characters. A payload without `;` is invalid. The URI is everything after the first `;`, so it may contain semicolons.
- The URI must be non-empty, at most 8192 UTF-16 code units, and free of C0/C1 controls, DEL, U+2028/U+2029, bidi and zero-width format characters, and unpaired surrogates. A space is allowed (it is percent-escaped when the link is activated).
- Any invalid, over-length, or unstorable URI **closes** the active link rather than leaving the previous one active. Scheme filtering is deliberately not done at ingest; it happens when a link is activated (see [Configuration](Configuration.md#hyperlinks)).
- With `hyperlinks.osc8` set to false the adapter ignores OSC 8 entirely: nothing is stored and any active link is closed. The host applies the setting to each adapter when its tab or pane is created, when a session is first seen by the host (so panes made by the built-in split actions are covered), and to every pane on each configuration reload.

The per-buffer URI table is append-only and bounded: at most 65,535 distinct URIs (`TerminalBuffer.MaxHyperlinkEntries`; ids are 16-bit with 0 meaning none) and 4 Mi UTF-16 characters (`MaxHyperlinkChars`, 8 MiB of text) in total. When either limit is reached **new** URIs are refused (the cell gets no link); ids are never reused or wrapped, so existing cells keep their own URL and already-known URIs still resolve. Render snapshots capture the table by reference (`IRenderSource.GetHyperlinkUrl`), so a click resolves an OSC 8 link from the same snapshot that was drawn. Ctrl+click on OSC 8 links now works because of this: the click-time lookup runs on a snapshot, and the previous lookup only resolved ids against a live `TerminalBuffer`.

#### OSC 7 (working directory)

`Osc7WorkingDirectory.TryParse` is a pure, strict parser; the adapter stores the result and raises `CurrentWorkingDirectoryChanged`. A report that fails any rule is ignored and the previously reported directory is kept.

- Scheme `file://` (case-insensitive); the host must be empty, `localhost`, `127.0.0.1`, `[::1]`, or this machine's name (`Environment.MachineName` or the DNS host name; one trailing dot is tolerated). Hosts containing `@`, `%`, or `:` and every other host are rejected, so UNC and remote paths never become a working directory.
- `?` and `#` must be percent-encoded: a raw `?` or `#` rejects the report. The raw payload (at most 9 x 4096 + 16 characters) must not contain controls, DEL, C1, or U+FFFD, nor a backslash on Windows.
- Percent-escapes must be well-formed and decode to valid UTF-8; the decoded path must be non-empty, at most 4096 UTF-16 characters, and contain no controls, C1, or U+FFFD.
- The path is normalized: `.` and empty segments dropped, `..` resolved (never above the root). POSIX paths must start with `/`. Windows paths must be `/X:` or `/X:/...` (rendered `X:\...`) and may not contain `<>:"|?*\`.

Reported directories are not checked against the filesystem here; existence is resolved when a new tab or pane is opened (see [Shell Integration](ShellIntegration.md)).

#### REP bound

`CSI n b` repeats the previous printable character through the normal writer. Printable ASCII is bounded by the retained window plus a remainder modulo the steady-state wrap width. With horizontal margins, that width is the configured margin span, even if the first segment starts outside the margins and uses the physical right edge. The reduction removes only whole steady-state rows after the retained window is saturated, preserving visible content, history and cursor/pending-wrap state. Other characters (possibly wide or zero-width) retain the existing physical-capacity bound; their final cursor parity can differ from an unbounded repeat when the count exceeds it. `CSI 2147483647 b` therefore cannot stall the parser.

With scrollback capacity 0 (`scrollback.limitLines = 0`, or a width at which `limitBytes` leaves no room for history) the cap is `rows + 1` rows of columns. Reverse index (`ESC M`) at the top of a full-screen region restores only rows the ring still holds, so with capacity 0 it never pulls rows back (previously the scrolled-line count could exceed what was retained).

**Implementation details:**

```csharp
private void HandleOscPayload(ReadOnlySpan<byte> payloadBytes)
{
    int semiIdx = payloadBytes.IndexOf((byte)';');
    ReadOnlySpan<byte> codeBytes = semiIdx >= 0 ? payloadBytes.Slice(0, semiIdx) : payloadBytes;
    ReadOnlySpan<byte> dataBytes = semiIdx >= 0 ? payloadBytes.Slice(semiIdx + 1) : ReadOnlySpan<byte>.Empty;

    if (TryParseAsciiInt(codeBytes, out int oscCode))
    {
        // Decode UTF-8 payload using pooled array
        int maxChars = Encoding.UTF8.GetMaxCharCount(dataBytes.Length);
        char[] pooled = ArrayPool<char>.Shared.Rent(maxChars);
        try
        {
            int charsDecoded = Encoding.UTF8.GetChars(dataBytes, pooled.AsSpan());
            Handler?.OnOperatingSystemCommand(oscCode, pooled.AsSpan(0, charsDecoded));
        }
        finally
        {
            ArrayPool<char>.Shared.Return(pooled);
        }
    }
}
```

### DCS (Device Control String)

Format: `ESC P <intermediates> <params> <final byte> <data> ESC \`

DCS strings are recognized, and short ones (up to 4 KiB) are captured for XTGETTCAP (`DCS + q`); everything else is consumed to ST and ignored. In particular `DCS tmux ;` passthrough wrappers are **not** interpreted: tmux unwraps them itself and forwards the raw APC. PM and SOS strings, and APC strings that are not an enabled Kitty graphics command, are discarded (see [String states](#string-states-and-controls-inside-sequences)).

### APC (Kitty graphics)

Format: `ESC _ G <control> [; <base64 payload>] ST`. The parser frames the string
(see [APC states](#apc-states-kitty-graphics)) and the adapter forwards it to
`KittyGraphicsController`; the protocol itself (actions, keys, replies, limits) is
documented in [Kitty graphics](KittyGraphics.md). Graphics are off by default, in
which case the whole string is discarded.

### CSI (Control Sequence Introducer)

See [ANSI Escape Sequence Parsing](#ansi-escape-sequence-parsing) section above.

### Mouse Protocol Support

The parser implements X11 mouse tracking and SGR mouse extensions:

#### X11 Mouse Format (1000/1002/1003 mode)

```
ESC [ M Cb Cx Cy
```

Where:
- `Cb` = button code + 32 (0x20 offset)
- `Cx` = column + 32
- `Cy` = row + 32

```csharp
if (final == 'M' && paramSpan.Length == 0)
{
    if (i + 3 < inputSpan.Length)
    {
        int cbByte = inputSpan[i + 1] - 32;
        int cxByte = inputSpan[i + 2] - 32;
        int cyByte = inputSpan[i + 3] - 32;
        bool isPress = (cbByte & 3) != 3;
        Handler?.OnMouseEvent(cbByte, cxByte, cyByte, isPress);
        i += 4;
    }
}
```

#### SGR Mouse Format (1006 mode)

```
ESC [ < Cb ; Cx ; Cy M  (press)
ESC [ < Cb ; Cx ; Cy m  (release)
```

Handled via the fallback path with string-based parsing for the `<` prefix.

#### Mouse reporting (host side)

`Dotty.Runtime.Input.MouseEncoder` turns pointer events into reports for the tracking mode and encoding the application selected. It is pure and allocation-free (`Encode(in MouseReport, Span<byte>)`, at most `MouseEncoder.MaxSequenceLength` bytes).

| Tracking mode | Reports |
|---|---|
| 9 (X10) | Presses of buttons 1-3 only: no release, no motion, no wheel, no modifier bits |
| 1000 (normal) | Presses, releases, wheel (buttons 64-67) |
| 1002 (button event) | As 1000, plus motion while a button is held (`Cb + 32`) |
| 1003 (any event) | As 1002, plus motion with no button (`Cb = 35`) |

| Encoding | Format | Largest position |
|---|---|---|
| default | `CSI M Cb Cx Cy`, each byte value + 32; a release reports button 3 | 223 (larger positions send nothing) |
| 1005 (UTF-8) | as default, values of 128 and up written as 2-byte UTF-8 | 2015 (larger positions send nothing) |
| 1015 (urxvt) | `CSI Cb+32 ; x ; y M`, decimal; a release reports button 3 | none |
| 1006 (SGR) | `CSI < Cb ; x ; y M` press/motion, `m` release keeping the button number | none |
| 1016 (SGR-pixels) | as 1006 with 1-based pixel positions inside the pane (framebuffer pixels, clamped into the pane) | none |

Positions are 1-based. Modifier bits: Shift 4, Alt 8, Ctrl 16 (Super is not reported). Motion is de-duplicated per reporting pane like xterm: a motion report at the same cell as the last report (the same pixel under 1016) is dropped; presses and releases are always sent.

The wheel reports one press per notch (buttons 64 up, 65 down, 66 left, 67 right; no release); fractional touchpad deltas carry over between events (`ScrollAccumulator`) and `mouse.scrollMultiplier` does not apply, because the application decides how far a notch scrolls. Without mouse reporting the wheel scrolls the pane by `mouse.scrollMultiplier` lines per notch, again with fractional carry. On the alternate screen, with mode 1007 set, no tracking mode active and `mouse.alternateScroll` = `auto`, it sends that many cursor-up/down keys instead (`CSI A`/`CSI B`, or `SS3 A`/`SS3 B` under DECCKM), which scrolls `less`, `man` and similar programs. Holding `mouse.reportBypassModifier` (Shift by default) keeps clicks, drags and the wheel local while an application tracks the mouse.

### Character sets and font-shift mode

DEC special graphics and the classic national replacement character sets
(NRC) are selected with G0/G1 designation and invoked with SI/SO as applicable.
The twelve classic NRC sets require mode 42 to be enabled; Unicode code points
are not remapped. The corrected Dutch, German and Norwegian mappings follow
the VT320 charset chart in
[xterm's primary `charsets.h` source](https://github.com/ThomasDickey/xterm/blob/master/charsets.h).

DEC private mode 35 enables xterm's font-shifting key behavior: Shift+Keypad
Plus increases the font size; Shift+Keypad Minus and Ctrl+Shift+Keypad Plus
decrease it. With mode 35 reset (the default), those key combinations are passed
as ordinary terminal input. The upstream xterm legacy name `HEBM` for mode 35
does not describe this implementation's behavior; the behavior follows xterm's
font-shifting convention.

The parser maintains a `_charset` state variable and applies translation:

```csharp
private static readonly Dictionary<char, char> s_decSpecialGraphicsMap = new()
{
    ['j'] = '┘', ['k'] = '┐', ['l'] = '┌', ['m'] = '└',
    ['t'] = '├', ['u'] = '┤', ['v'] = '┴', ['w'] = '┬',
    ['n'] = '┼', ['q'] = '─', ['x'] = '│',
    // ... more mappings
};
```

---

## Performance Considerations

### Benchmark Boundaries

PTY output benchmarks measure the producer, platform transport, parser/buffer,
and submitted frames together. They do not isolate parser throughput. Windows
ConPTY processes console output before forwarding VT data to Dotty; POSIX PTYs
forward a byte stream with their configured line discipline.

Use `scripts/perf/dotty_output_bench.py` for complete-write, geometry-gated
end-to-end measurements. Successful runs require acknowledged payload bytes,
the final marker, drained parser counters, and a matching submitted model
generation. Short raw writes must be retried; requested bytes are not proof of
delivered bytes. For parser-only work, use the in-process performance benchmarks
rather than an environment switch that bypasses terminal processing.

### Throughput Optimizations

| Optimization | Implementation | Impact |
|--------------|----------------|--------|
| **Direct ASCII conversion** | `(char)byte` cast for 0x20-0x7E | ~10x faster than UTF-8 decode |
| **Span-based parameter parsing** | `stackalloc int[8]` for params | No heap allocation for typical sequences |
| **Pre-allocated scratch buffers** | `char[512]` reusable array | Avoids allocation for most text runs |
| **Skip validation** | No range checking for printable bytes | Faster ground state processing |
| **Bulk UTF-8 decode** | `Encoding.UTF8.GetChars()` per run | Fewer decoder state transitions |

### Performance Characteristics

| Scenario | Time Complexity | Memory Allocations |
|----------|----------------|-------------------|
| ASCII text (1000 chars) | O(n) | 0 (uses scratch buffer) |
| UTF-8 text (mixed) | O(n) | 0-1 small pooled arrays |
| Simple CSI sequence | O(1) | 0 |
| Complex SGR sequence | O(params) | 0 (stackalloc) |
| Split sequence handling | O(leftover + new) | 1 temporary concat array |
| Mouse event (X11) | O(1) | 0 |
| OSC sequence | O(payload) | 1 pooled char array |

### Micro-Benchmarks

Typical performance on modern hardware:

| Operation | Throughput | Latency |
|-----------|------------|---------|
| ASCII text processing | ~500 MB/s | <1μs per 1KB |
| UTF-8 decode + render | ~100 MB/s | ~5μs per 1KB |
| CSI sequence parsing | ~10M ops/s | ~100ns per sequence |
| Mouse event parsing | ~20M ops/s | ~50ns per event |

---

## Source File References

### Core Parser Implementation

| File | Description |
|------|-------------|
| `src/Dotty.Terminal/Parser/BasicAnsiParser.cs` | Main parser implementation with state machine and sequence handling |

### Abstractions and Interfaces

| File | Description |
|------|-------------|
| `src/Dotty.Abstractions/Parser/ITerminalParser.cs` | Parser interface definition (`Feed()`, `Handler` property) |
| `src/Dotty.Abstractions/Adapter/ITerminalHandler.cs` | Handler interface with all terminal action callbacks |

### SGR (Graphics) Parsing

| File | Description |
|------|-------------|
| `src/Dotty.Terminal/Adapter/SgrParserArgb.cs` | SGR attribute parsing (colors, styles) |
| `src/Dotty.Terminal/Adapter/SgrColorArgb.cs` | Color representation and ARGB handling |
| `src/Dotty.Terminal/Adapter/SgrColor.cs` | Legacy color support |

### Adapter Layer

| File | Description |
|------|-------------|
| `src/Dotty.Terminal/Adapter/TerminalAdapter.cs` | Connects parser to buffer - implements `ITerminalHandler` |
| `src/Dotty.Terminal/Adapter/Buffer/TerminalBuffer.cs` | Screen buffer that receives parsed output |

### Test Files

| File | Description |
|------|-------------|
| `tests/Dotty.App.Tests/BasicAnsiParserTests.cs` | Unit tests for parser functionality |
| `tests/Dotty.App.Tests/SgrParserTests.cs` | Tests for SGR attribute parsing |
| `tests/Dotty.App.Tests/SgrColorTests.cs` | Tests for color parsing and conversion |
| `tests/Dotty.App.Tests/ControlCodeTests.cs` | Tests for C0 control character handling |
| `tests/Dotty.App.Tests/MouseModeTests.cs` | Tests for mouse protocol parsing |
| `tests/Dotty.App.Tests/MouseEncoderTests.cs`, `ScrollAccumulatorTests.cs` | Mouse report golden bytes per mode and encoding (positions 1, 95, 96, 127, 128, 222, 223, 224, 2015, 2016), motion de-duplication, alternate-scroll keys, wheel accumulation |
| `tests/Dotty.Terminal.Tests/Osc8ParserTests.cs` | Tests for OSC 8 hyperlink parsing |
| `tests/Dotty.Terminal.Tests/ParserStringStateTests.cs` | ESC/CAN/SUB/C0 handling in OSC, DCS, APC/PM/SOS, and CSI |
| `tests/Dotty.Terminal.Tests/KittyApcParserTests.cs`, `KittyControlParserTests.cs` | APC framing, abort rules, the 512-byte control cap, control key parsing |
| `tests/Dotty.Terminal.Tests/IndexAndNextLineTests.cs` | `ESC D` / `ESC E` |
| `tests/Dotty.Terminal.Tests/CursorMovementConformanceTests.cs` | Origin/margin addressing, HPA/HPR/VPR, HT/CHT/CBT and tab-stop state, reverse-wrap transitions, DECALN and margin clearing |
| `tests/Dotty.Terminal.Tests/BoundedScrollingTests.cs`, `tests/Dotty.App.Tests/ScreenRectangleScrollTests.cs` | Both-axis line/scroll eligibility, partial-width SU/SD, history isolation, hot/cold cell metadata and wide-glyph edge repair |
| `tests/Dotty.Terminal.Tests/MarginEditingConformanceTests.cs`, `MarginEditingImageConformanceTests.cs`, `tests/Dotty.App.Tests/ScreenColumnEditingTests.cs` | Margin-bounded ICH/DCH/IL/DL, image/history isolation, native metadata movement and wide-glyph cuts |
| `tests/Dotty.Terminal.Tests/IsoProtectionTests.cs`, `WriterMarginConformanceTests.cs`, `EraseAndConformanceLevelTests.cs` | SPA/EPA and DECSCA precedence, ordinary/selective erasure, reflowed protection, writer margin bounds, saturated REP and VT-level transitions |
| `tests/Dotty.Terminal.Tests/BufferTextWriterInsertModeTests.cs`, `MoreFixConformanceTests.cs`, `ColumnModesTests.cs`, `SavedRenditionConformanceTests.cs` | Insert batching/fragmentation, pending-wrap tabs, host-authorized column changes, screen-local rendition and repeated restoration |
| `tests/Dotty.Terminal.Tests/ParserRoutingTests.cs` | DECSTR, `CSI ? Ps n`, `CSI Ps t`, DECRQCRA, and modes 66/67/2027/2033 routing |
| `tests/Dotty.Terminal.Tests/SoftResetTests.cs`, `FullResetTests.cs`, `tests/Dotty.App.Tests/BufferResetTests.cs` | DECSTR and RIS reset sets |
| `tests/Dotty.Terminal.Tests/DeviceStatusTests.cs` | DSR 5/6, DECXCPR, origin-relative CPR, silent unknown DSR |
| `tests/Dotty.Terminal.Tests/WindowOpsTests.cs`, `VisibilityReportTests.cs` | `CSI Ps t` replies and resize requests, mode 2033 and `CSI ? 998 n` |
| `tests/Dotty.Terminal.Tests/ChecksumReportTests.cs` | gated DECRQCRA and the checksum definition |
| `tests/Dotty.Terminal.Tests/CombiningCapTests.cs`, `tests/Dotty.App.Tests/GraphemeStorageTests.cs` | 64-mark cap and the bounded grapheme store |
| `tests/Dotty.Terminal.Tests/UnicodeWidthTests.cs`, `GraphemeSegmentationTests.cs`, `BufferTextWriterClusterTests.cs` | Generated width tables and their golden, cluster width rules, UAX #29 segmentation, clusters re-joined across writes |
| `tests/Dotty.Terminal.Tests/ModeReportTests.cs` | the DECRQM table and DECSET 47/1047/1048 |
| `tests/Dotty.Terminal.Tests/SaturatedParameterTests.cs` | Saturating numeric parsing and cursor/ICH/DCH/ECH clamping |
| `tests/Dotty.Terminal.Tests/RepeatCharacterTests.cs` | REP bound and equivalence |
| `tests/Dotty.Terminal.Tests/Osc52Tests.cs` | OSC 52 selectors, write/read decoding |
| `tests/Dotty.Terminal.Tests/Osc8IngestValidationTests.cs`, `HyperlinkUriTests.cs`, `HyperlinkTableLimitTests.cs`, `RenderSnapshotHyperlinkTests.cs` | OSC 8 validation, URI table caps, snapshot lookup |
| `tests/Dotty.Terminal.Tests/Osc7WorkingDirectoryTests.cs`, `ShellIntegrationOsc7Tests.cs` | Strict OSC 7 parsing and adapter integration |

---

## Additional Resources

- **ECMA-48 Standard**: Control Functions for Coded Character Sets
- **ANSI X3.64**: Additional control sequences
- **DEC VT100/VT220/VT420**: Terminal implementation references
- **XTerm Documentation**: `ctlseqs.txt` for escape sequence reference
- **iTerm2 Proprietary Sequences**: OSC 8, 9, and image protocols

---

## Changelog

| Date | Change |
|------|--------|
| 2026-11-05 | G13/G14: operational printer/file output, native window-control permission, KAM/SRM/NRC/font-shift, smooth-scroll composition, title/status protocol behavior, DECSTR/RIS resets, native SELECT/CUT_BUFFER0 and bounded xterm-compatible OSC 52 decoding. |
| 2026-11-05 | Native window-position reports retain a deferred `CSI 13 t` request while the host position is unavailable, so a later query can return the actual position instead of losing the queued request. |
| 2026-11-05 | G12 protocol fixes: DECID primary-DA alias, strict zero-only DA request recognition, silent echoed replies/non-request forms, preserved terminal state, and unchanged truthful product identity. Four xterm-specific identity expectations remain open under the user-approved strict report. |
| 2026-11-05 | G11 palette conformance: Xcms OSC color formats, indexed/special-color aliases and modes, rendition precedence, and accurate DECSTR/RIS color reset semantics. |
| 2026-11-04 | G09/G10 conformance batch: VT4+ DECCRA/DECFRA/DECERA/DECSERA, shared origin-relative clipped coordinates, overlap-safe native copies, distinct rendition/protection semantics, retained pending wrap and metadata, and explicit copied blanks preserving logical-line extent through reflow. |
| 2026-11-04 | G08 conformance batch: VT4+ DECIC/DECDC/DECBI/DECFI, both-axis bounded column shifts, saturated counts, pending-wrap and row-link semantics, native metadata preservation, and horizontal Kitty placement movement with irreversible source cropping. |
| 2026-11-04 | G07 conformance batch: margin-aware default SCO save, bounded XTSAVE/XTRESTORE with shared alias slots and reset-persistent cache, independent icon/window title state and title encoding modes, RIS title-mode reset, and explicit default-off title-report permission with byte-accurate PTY replies. |
| 2026-11-04 | G06 conformance batch: ANSI IRM/LNM with bulk insertion and legacy Return encoding, mode-41 pending-wrap tabs, distinct 47/1047/1049 retention, reusable screen-local cursor/rendition saves and reflow anchors, permission-gated DECCOLM/DECNCSM, truthful mode replies, and full-width margins following resized grids. |
| 2026-11-04 | G05 conformance batch: margin-bounded character/line editing, common writer right-edge and REP bounds, ISO protected ordinary erasure, separate DEC selective erasure, DECSCL feature gates, retained protection through reflow, linear protected-erase scanning, and accurate ED 2 discarded-history accounting. |
| 2026-11-04 | G04 conformance batch: margin-eligible LF/VT/FF/IND/RI, IND-before-CR NEL, rectangular SU/SD with native cell metadata and wide-glyph repair, unchanged partial-scroll history, contained image-placement movement, and margin-aware pending-wrap restart in both ASCII span writers. |
| 2026-11-04 | G01-G03 conformance batch: both-axis origin/margin cursor semantics and replies, HPA/HPR/VPR, counted CHT and true tab-stop clearing, modern reverse-wrap modes 45/1045 with reset handling, and complete DECALN. |
| 2026-10-30 | W4-B9: DECSCNM (`CSI ? 5 h/l`) routed to `OnSetReverseVideo`, reported live by DECRQM, kept by DECSTR and cleared by RIS; OSC 10/11 overrides and reverse video are drawn per pane. |
| 2026-10-30 | W4-B1: cell width from tables generated out of Unicode 16.0.0 (`tools/gen-width-tables.py`), cluster width rules (RI pairs, VS15/VS16 limited to emoji-variation bases, zero-width Cf and Hangul jamo), clusters re-joined across writes with late VS16 widening, wide clusters that wrap at the bottom row scroll, and a cell overwrite keeps its row soft-wrapped. |
| 2026-10-30 | XTVERSION (`CSI > q` / `CSI > 0 q`) routed to `OnRequestTerminalVersion()`; every `CSI > … q` form is kept away from DECSCUSR. |
| 2026-10-28 | Kitty graphics: APC `G` states (`ApcFirst`, `ApcControl`, `ApcPayload`, `ApcEscape`), 512-byte control cap, abort/termination rules, `0x9C` accepted as ST inside an enabled graphics APC, `ESC D` (IND) and `ESC E` (NEL), RIS deletes images. Written from the source; `needs runtime verification`. |
| 2026-10-28 | Wave 3: bulk OSC scan, whole-OSC in-chunk dispatch and bytes-native OSC 52; zero-capacity scrollback note; reverse index limited to retained history; ED 2 / CSI 3 J / RIS discard history pages (see [Rendering](Rendering.md#lazily-committed-cell-arenas)). |
| 2026-10-28 | Wave 2: DECSTR/RIS share one reset core; DSR/CPR/DECXCPR with silent unknown codes; DECBKM; window operations 13-19 and gated `CSI 8 t`; mode 2033 and `CSI ? 998 n`; gated DECRQCRA; 64-mark combining cap and a bounded, de-duplicating grapheme store; complete DECRQM table; DECSET 47/1047/1048. |
| 2026-10-28 | Hardening pass: ESC/C0-correct string states (no 0x9C ST), APC/PM/SOS discard, saturating integers, routed DECSTR/`CSI ? Ps n`/`CSI Ps t`/DECRQCRA/modes 66,67,2027,2033 (default no-op handlers unless noted), `OnWindowOperation`, 1 MiB OSC cap with shrink-on-reset, window title cap, REP bound, ICH/DCH/ECH clamp, OSC 52 write/read policy, OSC 8 ingest validation and bounded URI table, strict OSC 7. |
| 2026-10-07 | Palette behavior: adapter-local ANSI/xterm palette and dynamic-color overrides; OSC 4/10/11 set requests are silent, queries use rgb:RRRR/GGGG/BBBB and ST; OSC 104/110/111/112 resets and RIS restore overrides. |
| 2026-10-06 | P0/P1: DECRQM (CSI ? Ps $ p / CSI Ps $ p), XTGETTCAP (DCS + q), modifyOtherKeys (CSI > 4 ; Pv m), OSC 4/10/11/12 behavior |
| 2026-06-17 | Updated OSC table: OSC 1337 PromptMark (shell integration) |
| 2026-06-05 | Updated DECSTBM/DECOM interaction with alternate screen and origin mode |
| 2026-06-05 | Documented PromptMark OSC 1337 sequence support |
*Document version: 1.1*  
*Last updated: 2026-11-05*
