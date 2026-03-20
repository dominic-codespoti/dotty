# Parsing & Abstractions Development Guide

Welcome to the parser documentation. This covers how we interpret PTY bytes as terminal states (text, color, cursor position, scroll events, modes, etc.).

## Relevant Folders
- `src/Dotty.Abstractions/`
- `src/Dotty.Terminal/Parser/`
- `src/Dotty.Terminal/Adapter/`

## Key Concepts & Design Patterns
- **VT100 / Xterm Parsing:** Interpreting Escape sequences (`ESC`), Control Sequence Intro (`CSI`), Operating System Commands (`OSC`), Device Control Strings (`DCS`).
- **State Machine:** Processing incoming bytes dynamically from the PTY stream. It must handle fragmented reads securely where a sequence might span two different read cycles.
- **The Protocol/Delegate Pattern:** The parser reads the bytes and triggers abstract commands on the `ITerminalHandler` interface. The `TerminalBuffer` logic processes these events to modify screen memory. 

## Architectural Rules 🛠

1. **Avoid Buffer Re-allocations:** `Span<byte>` and `ReadOnlySequence<byte>` are mandatory for interpreting byte streams without excessive copying or pinning. Limit array instantiations strictly.
2. **Deterministic Sequence Handling:** Ensure sequences aren't mutated or overwritten during multi-part parses. Use precise offset boundary checks for incoming buffer slices.
3. **Graceful Failures:** If it encounters a malformed or unsupported CSI/OSC, log it and ignore it rather than crashing. Terminal emulation has many legacy idiosyncrasies.
4. **Encoding Checks:** Assume UTF-8 encoded text for parsing text runs unless configured differently. Wide characters (Emoji, full-width CJK) must be parsed cleanly into the buffer to ensure accurate glyph metrics during layout.
5. **Separation of Parsing and Buffer Manipulation**: Parsing identifies the command. The Buffer executes the command (modifies its internal array matrix layout). Don't mix them within the same structural unit.