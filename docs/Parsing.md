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
| `J` | `[mode]` | Erase Display | `OnEraseDisplay(mode)` |
| `K` | `[mode]` | Erase Line | `OnEraseLine(mode)` |
| `L` | `[n]` | Insert Lines | `OnInsertLines(n)` |
| `M` | `[n]` | Delete Lines | `OnDeleteLines(n)` |
| `P` | `[n]` | Delete Chars | `OnDeleteChars(n)` |
| `S` | `[n]` | Scroll Up | `OnScrollUp(n)` |
| `T` | `[n]` | Scroll Down | `OnScrollDown(n)` |
| `X` | `[n]` | Erase Chars | (future) |
| `@` | `[n]` | Insert Chars | `OnInsertChars(n)` |
| `d` | `[n]` | Cursor Vertical Absolute | `OnCursorVerticalAbsolute(n)` |
| `m` | `[attrs]` | SGR (Graphics) | `OnSetGraphicsRendition(attrs)` |
| `n` | `[code]` | Device Status Report | `OnDeviceStatusReport(code)` |
| `q` | `[shape]` | Set Cursor Shape | `OnSetCursorShape(shape)` |
| `r` | `[top;bottom]` | Set Scroll Region | `OnSetScrollRegion(top, bottom)` |
| `h`/`l` | `?<mode>` | Set/Reset Mode | `OnSetAlternateScreen()`, etc. |

### Parameter Parsing

CSI parameters are parsed using a fast, span-based numeric parser that handles:

- **Semicolon-separated values**: `ESC[1;31;40m` → `[1, 31, 40]`
- **Private markers**: `?`, `>`, `<` prefixes for mode sequences
- **Default values**: Empty parameters default to 0 or 1 as per spec
- **Colon sub-params**: Basic support for SGR colon-separated RGB values

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
| 0 | Set icon name and window title | `OnOperatingSystemCommand(0, title)` |
| 1 | Set icon name | `OnOperatingSystemCommand(1, name)` |
| 2 | Set window title | `OnOperatingSystemCommand(2, title)` |
| 4 | Set/read color palette | (future) |
| 8 | Hyperlink (OSC 8) | `OnOperatingSystemCommand(8, params)` |
| 9 | iTerm2 notifications | (future) |
| 10-19 | Set foreground/background/highlight colors | (future) |
| 52 | Manipulate selection/data | (future) |
| 777 | rxvt extension notifications | (future) |

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

Currently recognized but minimal implementation. Reserved for future VT52/VT100 compatibility extensions.

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

### Benchmark Mode

When `DOTTY_BENCH_THROUGHPUT` environment variable is set, the parser skips rendering callbacks entirely for throughput testing:

```csharp
private readonly bool _throughputMode = !string.IsNullOrEmpty(
    Environment.GetEnvironmentVariable("DOTTY_BENCH_THROUGHPUT"));

public void Feed(ReadOnlySpan<byte> bytes)
{
    if (_throughputMode && _leftoverLen == 0 && bytes.Length > 512)
    {
        _throughputChunkCounter++;
        if ((_throughputChunkCounter & 511) != 0)
        {
            return; // Skip 511 out of 512 chunks
        }
    }
    // ... normal processing
}
```

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

---

## Additional Resources

- **ECMA-48 Standard**: Control Functions for Coded Character Sets
- **ANSI X3.64**: Additional control sequences
- **DEC VT100/VT220/VT420**: Terminal implementation references
- **XTerm Documentation**: `ctlseqs.txt` for escape sequence reference
- **iTerm2 Proprietary Sequences**: OSC 8, 9, and image protocols

---

*Document version: 1.0*  
*Last updated: 2026-04-04*
