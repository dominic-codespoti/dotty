# Testing & Validation Guide

When bugs are fixed or new behaviors are defined, especially in the parsing layer or specific rendering configurations, you generally need to implement automated tests.

We employ rigorous validation pipelines to ensure the `dotnet-term` runs predictably logic-wise without rendering or crashing regressions.

## Relevant Locations
- `tests/Dotty.App.Tests/` (where logic meets the buffer or screen). 

## Types of Tests
- **Buffer & Parser Testing:** Ensure ANSI/VT controls mutate the terminal state correctly (e.g. `BasicAnsiParserTests.cs`, `SgrColorTests.cs`, `TerminalBufferCursorTests.cs`).
- **Rendering & State Handling:** Ensuring rendering engines deal correctly with permutations inside the buffer (e.g., `AsciiArtRenderTests.cs`, `PermutationScrollRenderTests.cs`).
- **Reproduction Cases (`MoreReproTests.cs`, `ReproAttemptsTests.cs`):** Tests directly written to ensure fixed issues do not regress when the codebase moves forward.
- **Fuzzing & Memory:** Ensuring invalid inputs don't crash the server, nor do overlapping terminal loops (e.g., `StressFuzzReproTests.cs`). 

## Writing Effective Tests 🛠

1. **Be Exact:** Emulation needs exact byte sequences parsed cleanly into specific array indices or cursor offsets. If `\u001b[5;10H` changes the cursor to line 5, col 10, assert those precise buffer state variables.
2. **Headless Execution:** Test the `ITerminalHandler` abstraction, the Buffer structs, or Mock dependencies where possible over standing up full Avalonia environments which slows down unit boundaries.
3. **Memory Safety Checks:** If developing logic dealing with ref structs and memory spans, use tests that loop and assert for leaks/throws specifically under multithreaded loading logic where appropriate.