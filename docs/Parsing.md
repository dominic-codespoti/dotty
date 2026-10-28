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
| TAB | 0x09 | Horizontal Tab | `OnTab()` - moves to next tab stop |
| LF | 0x0A | Line Feed | `OnLineFeed()` |
| VT | 0x0B | Vertical Tab | `OnLineFeed()` (treated as LF) |
| FF | 0x0C | Form Feed | `OnLineFeed()` (treated as LF) |
| CR | 0x0D | Carriage Return | `OnCarriageReturn()` |
| ESC | 0x1B | Escape | Begins escape sequence parsing |
| DEL | 0x7F | Delete | Ignored |

#### 2. CSI Sequences (Control Sequence Introducer)

Format: `ESC [ <params> <final byte>`

The parser handles all standard CSI sequences:

| Final | Params | Description | Handler Called |
|-------|--------|-------------|----------------|
| `A` | `[n]` | Cursor Up | `OnCursorUp(n)` |
| `B` | `[n]` | Cursor Down | `OnCursorDown(n)` |
| `C` | `[n]` | Cursor Forward | `OnCursorForward(n)` |
| `D` | `[n]` | Cursor Back | `OnCursorBack(n)` |
| `E` | `[n]` | Cursor Next Line | `OnCursorNextLine(n)` |
| `F` | `[n]` | Cursor Previous Line | `OnCursorPreviousLine(n)` |
| `G` | `[n]` | Cursor Horizontal Absolute | `OnCursorHorizontalAbsolute(n)` |
| `H` | `[row;col]` | Cursor Position | `OnMoveCursor(row, col)` |
| `J` | `[mode]` | Erase Display (`0/1/2`) | `OnEraseDisplay(mode)` |
| `K` | `[mode]` | Erase Line | `OnEraseLine(mode)` |
| `L` | `[n]` | Insert Lines | `OnInsertLines(n)` |
| `M` | `[n]` | Delete Lines | `OnDeleteLines(n)` |
| `P` | `[n]` | Delete Chars | `OnDeleteChars(n)` |
| `S` | `[n]` | Scroll Up | `OnScrollUp(n)` |
| `T` | `[n]` | Scroll Down | `OnScrollDown(n)` |
| `X` | `[n]` | Erase Chars | `OnEraseCharacters(n)` |
| `@` | `[n]` | Insert Chars | `OnInsertChars(n)` |
| `d` | `[n]` | Cursor Vertical Absolute | `OnCursorVerticalAbsolute(n)` |
| `m` | `[attrs]` | SGR (Graphics) | `OnSetGraphicsRendition(attrs)` |
| `n` | `[code]` | Device Status Report | `OnDeviceStatusReport(code)` |
| `q` | `[shape]` | Set Cursor Shape | `OnSetCursorShape(shape)` |
| `r` | `[top;bottom]` | Set Scroll Region (DECSTBM) | `OnSetScrollRegion(top, bottom)` |
| | | | Note: DECSTBM now interacts correctly with alternate screen and DECOM (origin mode) |
| `s` | none | Save Cursor (SCO) | `OnSaveCursor()` |
| `u` | none | Restore Cursor (SCO) | `OnRestoreCursor()` |
| `h`/`l` | `?<mode>` | Set/Reset Mode | `OnSetAlternateScreen()`, etc. |

Kitty keyboard protocol CSI commands (`CSI =`, `>`, `<`, and `?` ending in `u`) are handled separately from legacy numeric CSI parameters; DA1, DA2, and unsupported DA3 requests are distinguished by the `CSI c` private introducer. See [Keyboard Protocol and Compatibility](KeyboardProtocol.md) for the negotiation stack, supported flags, screen scoping, and exact replies.
Note: `CSI 3 J` is handled separately and maps to `OnClearScrollback()` rather than `OnEraseDisplay(3)`.
`CSI ? 6 n` (DEC private CPR query) is routed to `OnCursorPositionReport()` so shells like PSReadLine
receive a private CPR response (`ESC[?row;colR`) instead of the standard DSR response format.
`CSI ? Ps n` with `Ps` other than 6 is routed to `OnPrivateDeviceStatusReport(Ps)`; `CSI > Ps n` is not a status report and is ignored. The adapter does not implement `OnPrivateDeviceStatusReport`, so these queries receive no reply.
`CSI Ps ; Ps ; Ps t` is routed to `OnWindowOperation(operation, arg1, arg2)`; omitted parameters are 0 and the `?`, `>` and `<` forms are ignored. The adapter implements operation 14 (reply `CSI 4 ; height ; width t` in pixels), 18 (reply `CSI 8 ; rows ; cols t`) and 20/21 (reply an empty `ESC ] 0 ; ST` title); every other operation is ignored. This handler replaces the former `OnWindowReport(int)`.

#### Routed sequences with default no-op handlers

The parser routes the sequences below to `ITerminalHandler` methods that have empty default bodies. Only the rows marked "implemented" change terminal state or produce replies; the rest are recognized, consumed whole, and otherwise ignored, so they no longer leak into the cursor, SGR, or text path.

| Sequence | Handler | Behaviour in `TerminalAdapter` |
|---|---|---|
| `CSI ! p` (DECSTR; numeric parameters before `!` are ignored) | `OnSoftReset()` | Not implemented: the adapter has no override, so terminal state is **not** reset. The parser does reset its own G0/G1 charset to ASCII. |
| `CSI ? Ps n`, `Ps != 6` | `OnPrivateDeviceStatusReport(Ps)` | Not implemented; no reply. (`CSI ? 6 n` is implemented via `OnCursorPositionReport()`.) |
| `CSI Ps ; Ps ; Ps t` | `OnWindowOperation(op, a1, a2)` | Implemented for operations 14, 18, 20 and 21 (see above). |
| `CSI Pi ; Pg ; Pt ; Pl ; Pb ; Pr * y` (DECRQCRA) | `OnRequestChecksum(...)` | Not implemented; no reply. A well-formed request is consumed; a malformed one is rejected by the numeric parser and dropped. |
| `CSI ? 66 h/l` (DECNKM) | `OnSetKeypadApplicationMode(enabled)` | Implemented: shares state with `ESC =` / `ESC >` (DECKPAM/DECKPNM). |
| `CSI ? 67 h/l` (DECBKM) | `OnSetBackarrowKeyMode(sendsBackspace)` | Not implemented; Backspace encoding is unchanged. |
| `CSI ? 2027 h/l` | none | Accepted and ignored: grapheme clustering is always on. |
| `CSI ? 2033 h/l` | `OnSetVisibilityReports(enabled)` | Not implemented; no visibility reports are sent. |

DECRQM replies are unchanged by this routing: modes 66, 67, 2027, and 2033 still report `0` (not recognized). Mode 9 (X10 mouse) is deliberately **not** routed because the input encoder has no press-only X10 encoding.

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

### String states and controls inside sequences

| Situation | Behaviour |
|---|---|
| `ESC` inside OSC or DCS | Only `ESC \` (ST) terminates the string and dispatches it. `ESC ESC` is not payload: the second `ESC` may still begin ST. `ESC` followed by any other byte abandons the string **without dispatch** and that byte is processed as an escape sequence. |
| `BEL` | Terminates and dispatches an OSC. Inside DCS it is a control that aborts the string. |
| `CAN` (0x18) / `SUB` (0x1A) | Abandon the current string or sequence without dispatch. |
| Any other C0 control inside OSC/DCS/APC/PM/SOS | Aborts the string without dispatch, then executes as a control. |
| C0 control inside CSI or after a bare `ESC` | Executes (BEL, BS, TAB, LF/VT/FF, CR) and the sequence continues; the control never becomes part of the parameter string. DEL inside CSI is ignored the same way. Other controls are ignored. |
| C1 `0x9C` | Not an ST. Bytes `0x80-0x9F` are UTF-8 continuation bytes in the stream and are never interpreted as C1 controls; only `ESC \` ends a string. |
| APC (`ESC _`), PM (`ESC ^`), SOS (`ESC X`) | Consumed up to ST and discarded; nothing is buffered or dispatched. |
| DCS longer than 4 KiB | Consumed until ST without dispatch (only short XTGETTCAP queries are captured). |
| CSI longer than 256 parameter bytes, OSC longer than 1 MiB | Consumed until the terminating byte or ST without dispatch. |
| `ESC c` (RIS) | Also resets the parser's charset to ASCII. |

The sequence buffer (`_leftover`) grows on demand up to the string's cap. When a sequence ends or is abandoned and the buffer exceeds 4096 bytes it is reallocated back to 32 bytes, so one large OSC 52 does not pin a megabyte for the life of the session.

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
| 0 | Set icon name and window title | `OnOperatingSystemCommand(0, title)`; title truncated to `TerminalAdapter.MaxTitleChars` (4096 UTF-16 chars, never splitting a surrogate pair) |
| 1 | Set icon name | `OnOperatingSystemCommand(1, name)` |
| 2 | Set window title | `OnOperatingSystemCommand(2, title)`; same 4096-char cap |
| 4 | Set/query session palette (index ; spec pairs; ? queries) | OSC 4 sets are silent; replies are ESC ] 4 ; index ; rgb:RRRR/GGGG/BBBB ESC backslash with 16-bit components; malformed colors are ignored. ANSI indices 0-15 remap existing ANSI-colored styles in this session; indices 16-255 affect later SGR lookups. |
| 7 | Working directory (shell integration) | `OnOperatingSystemCommand(7, uri)`; strict acceptance rules in [OSC 7](#osc-7-working-directory). |
| 8 | Hyperlink (OSC 8) | `OnOperatingSystemCommand(8, params)`; validated at ingest, see [OSC 8](#osc-8-hyperlinks) |
| 9 | iTerm2 notifications | (future) |
| 10-12 | Query dynamic foreground/background/cursor colors | OSC 10/11 queries return rgb:RRRR/GGGG/BBBB ESC backslash; sets store per-session foreground/background overrides without replying. OSC 12 queries reply in the same format; set/reset are accepted but cursor color is not rendered. |
| 13-19 | Set foreground/background/highlight colors | (future) |
| 52 | Manipulate selection data (clipboard write and read) | `OnOperatingSystemCommand(52, ...)`; see [OSC 52](#osc-52-clipboard) |
| 777 | rxvt extension notifications | (future) |
| 1337 | PromptMark (shell integration) | `OnPromptMark(level)` — tracks shell prompt start/end via `OSC 1337 ; A` / `OSC 1337 ; B` |
| 104 | Reset palette entries | No payload resets all OSC 4 overrides; index ; ... resets listed indices, ignoring invalid and empty tokens. |
| 110/111 | Reset dynamic foreground/background | Clear the session's OSC 10/11 override; no reply. |
| 112 | Reset cursor color | Accepted without effect; cursor color is not rendered. |

Palette overrides and dynamic-color overrides are scoped to one terminal adapter/session. OSC 4/10/11 set requests never reply; only ? queries do. OSC 104 resets to the current ANSI theme baseline and stock xterm ramp. RIS (ESC c) clears all palette and OSC 10/11 overrides to their baselines. Changing the theme ANSI baseline also clears OSC 4 overrides; reapplying an identical baseline is a no-op and preserves them.

#### OSC payload limit

The OSC payload buffer is capped at 1 MiB (1,048,576 bytes, counted after `ESC ]` and including the `52;c;` style prefix). It was 64 KiB before OSC 52 transport needed larger strings. A longer string is consumed to ST and **discarded without dispatch**. Because the cap covers the whole payload, the largest OSC 52 write the parser can deliver carries slightly less than 1,048,576 base64 characters. The buffer shrinks back after each sequence (see above).

#### OSC 52 (clipboard)

Format: `ESC ] 52 ; Pc ; Pd ST`. Handled by `TerminalAdapter.HandleOsc52`; the adapter only decodes and raises events, and policy lives in the host.

| Part | Behaviour |
|---|---|
| `Pc` selectors | Empty or `c` = system clipboard; `p` or `s` = Primary selection; letters combine (`cp`); unknown letters are ignored. If no recognized selector remains, or the payload has no `;`, the request is ignored. |
| `Pd` = `?` | Clipboard **read** request: raises `TerminalAdapter.ClipboardReadRequested(ClipboardReadRequest)` (concrete adapter only, not on `ITerminalHandler`); `TerminalSession` forwards it. |
| `Pd` empty | Write of the empty string. |
| other `Pd` | Base64 decoded, then UTF-8 decoded, then `ClipboardWriteRequested(ClipboardWriteRequest(selection, text))`. Invalid base64 is ignored. |

**Write policy (host, `DottyWindowHost.EnqueueClipboardWrite`):** dropped unless `clipboard.allowOsc52Write` is true; dropped when the selection does not include the clipboard (Primary-only writes are ignored until an X11 selection helper exists, so they never clobber the clipboard); dropped when the decoded text exceeds `MaxOsc52WriteBytes` = 786,432 bytes (checked as UTF-16 length and as UTF-8 byte count). Accepted writes are coalesced (a session's newer write replaces its pending one) and queued with at most 32 entries and 4 MiB of pending text; excess is dropped. The Lua `clipboard_write` hook can still veto. See [Configuration](Configuration.md#clipboard).

**Read policy (`osc52Read`, `ClipboardReadService`):** `deny` (default), `ask`, `allow`; unknown values mean `deny`.

- The request is dropped before queueing when the selection does not include the clipboard (Primary reads are not served), when the mode is `deny`, or when the window is closing. Denials never produce a reply: not for policy, a Lua `clipboard_read` veto, a user "Deny", an oversize clipboard (more than `osc52ReadLimitBytes` UTF-8 bytes), an unavailable clipboard, or expiry. Only a genuinely empty clipboard answers (with an empty payload).
- Pending requests live in a queue of at most 4 requests in total and 1 per session (the oldest wins; later ones are dropped), and expire after 30 s. They are served on the UI thread only while the window is focused and the requesting session's tab is the active tab; under `ask` they also wait while another menu or prompt is visible. Lowering the mode to `deny` while requests wait clears the queue.
- The clipboard is read once. `allow` replies immediately with `ESC ] 52 ; c ; base64(text) ESC \` (always ST-terminated). `ask` shows a centered host prompt naming the (sanitized, truncated) tab title and previewing the same snapshot that will be sent, with **Deny** focused; Escape, click-outside, or replacement by another menu send nothing. Choosing **Allow** re-validates first: if `osc52Read` was changed to `deny` or the session left the window while the prompt was open, no reply is sent.

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

`CSI n b` repeats the previous printable character. Printable ASCII is capped at the buffer's cell capacity (`rows + scrollback + 1` rows of columns) plus the remainder modulo the column count, which is output-equivalent to the unbounded repeat because every further full row of identical characters is a no-op on the visible grid and cursor column. Other characters (possibly wide or zero-width) are capped at that capacity; only the final cursor parity can differ from an unbounded repeat when the count exceeds it. `CSI 2147483647 b` therefore cannot stall the parser.

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

DCS strings are recognized, and short ones (up to 4 KiB) are captured for XTGETTCAP (`DCS + q`); everything else is consumed to ST and ignored. APC, PM, and SOS strings are discarded unconditionally (see [String states](#string-states-and-controls-inside-sequences)); `BeginDiscardedString` is the single place a future APC capture, such as the Kitty graphics protocol, would hook in.

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

### Charset Selection (DECSCL/DESIGNATE)

Format: `ESC ( <charset>` or `ESC ) <charset>`

| Selector | Charset |
|----------|---------|
| `B` | ASCII (US) |
| `0` | DEC Special Graphics (line drawing) |
| `1` | Alternate ROM |
| `2` | Alternate ROM Special Graphics |

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
| `tests/Dotty.Terminal.Tests/Osc8ParserTests.cs` | Tests for OSC 8 hyperlink parsing |
| `tests/Dotty.Terminal.Tests/ParserStringStateTests.cs` | ESC/CAN/SUB/C0 handling in OSC, DCS, APC/PM/SOS, and CSI |
| `tests/Dotty.Terminal.Tests/ParserRoutingTests.cs` | DECSTR, `CSI ? Ps n`, `CSI Ps t`, DECRQCRA, and modes 66/67/2027/2033 routing |
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
| 2026-10-28 | Hardening pass: ESC/C0-correct string states (no 0x9C ST), APC/PM/SOS discard, saturating integers, routed DECSTR/`CSI ? Ps n`/`CSI Ps t`/DECRQCRA/modes 66,67,2027,2033 (default no-op handlers unless noted), `OnWindowOperation`, 1 MiB OSC cap with shrink-on-reset, window title cap, REP bound, ICH/DCH/ECH clamp, OSC 52 write/read policy, OSC 8 ingest validation and bounded URI table, strict OSC 7. |
| 2026-10-07 | Palette behavior: adapter-local ANSI/xterm palette and dynamic-color overrides; OSC 4/10/11 set requests are silent, queries use rgb:RRRR/GGGG/BBBB and ST; OSC 104/110/111/112 resets and RIS restore overrides. |
| 2026-10-06 | P0/P1: DECRQM (CSI ? Ps $ p / CSI Ps $ p), XTGETTCAP (DCS + q), modifyOtherKeys (CSI > 4 ; Pv m), OSC 4/10/11/12 behavior |
| 2026-06-17 | Updated OSC table: OSC 1337 PromptMark (shell integration) |
| 2026-06-05 | Updated DECSTBM/DECOM interaction with alternate screen and origin mode |
| 2026-06-05 | Documented PromptMark OSC 1337 sequence support |
*Document version: 1.1*  
*Last updated: 2026-10-28*
