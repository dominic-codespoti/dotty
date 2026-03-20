# Architecture Guidelines

Dotty follows a strictly decoupled architecture, isolating the terminal emulation logic from the platform-specific UI rendering and OS-level PTY integration.

## Layering

1. **`Dotty.Abstractions`**: Shared interfaces (e.g., `ITerminalHandler`, `ITerminalParser`). Contains pure definitions. Zero dependencies.
   - Defines the core contracts for the terminal domain.
   - Ensures decoupling by providing interfaces that both the parsing logic and the UI can rely on without knowing each other's implementations.
   - Key interfaces include `ITerminalHandler` for terminal actions and `ITerminalParser` for input parsing.
2. **`Dotty.Terminal`**: Core business logic. Implements ANSI/VT parsing, state management (cursor, buffers, color palettes), and memory handling (`TerminalBuffer`). **Rule:** Fully headless, NO UI or OS-specific code allowed here. Must be highly optimized.
   - **Parsing**: Translates PTY byte streams into terminal commands.
   - **Buffer Management**: Organizes cells into grid structures, dealing with text runs, colors, and attributes using low-allocations strategies.
   - **Scrollback & State**: Strictly manages the logical aspects—like tracking the raw circular buffer lines for scroll history and maintaining logical start/end coordinates for text selection without any visual awareness.
   - **State Machine**: Tracks cursor positions, current colors, and VT modes (e.g., alternate screen buffer).
3. **`Dotty.NativePty`**: C-based Native module for pseudo-terminal generation (`pty-helper.c`). Deals with UNIX pipes and terminal forks. 
   - Interfaces directly with the OS to spawn shells (e.g., bash, zsh).
   - Strictly handles PTY IO, file descriptors, and terminal resizing signals (`TIOCSWINSZ`) at the POSIX level.
   - Manages interop streams between the C-level pseudo-terminal and the .NET application.
4. **`Dotty.App`**: The Avalonia-based UI layer. Wires up `Dotty.Terminal` state to the screen natively. Handles user inputs, windowing, drawing, and font fallback.
   - **Scrolling Engine**: Uses Avalonia's scroll primitives but intercepts logic to strictly map the UI scrollbar offset directly to the headless `TerminalBuffer`'s scrollback index.
   - **Mouse & Selection**: Captures Avalonia pointer/drag events (raw pixel X/Y coordinates), translates those into discrete terminal cell grid coordinates, informs the logical buffer of the selection range, and delegates drawing the highlight background to `BackgroundSynth`.
   - **Clipboard Integration**: Bridges Avalonia's native OS clipboard API with the logical text extracted from the `TerminalBuffer`'s selection range.
   - **Rendering Engine (`TerminalCanvas` / `TerminalVisualHandler`)**: 
     - Maps logical grid coordinates to physical pixel dimensions.
     - Defers text layout caching to `GlyphAtlas` (rendering cached textures rather than real-time typography) to maintain 60+ FPS during rapid updates.

## Design Philosophy
- **Separation of Concerns:** UI issues stay in `Dotty.App`. Parsing/Buffer issues stay in `Dotty.Terminal`. PTY issues stay in `Dotty.NativePty`.
- **Performance First:** The critical execution path runs continuously. Preallocate buffers and avoid garbage collection overhead in hot loops. 
- **Platform Agnostic UI:** Rely on Avalonia for window creation and graphics contexts to ensure it can run anywhere .NET can. 

**Next Steps depending on your task:**
- Modifying UI or drawing? Read [Rendering Docs](./rendering.md).
- Changing ANSI parser behavior? Read [Parsing Docs](./parsing.md).
- Adding tests? Read [Testing Docs](./testing.md).