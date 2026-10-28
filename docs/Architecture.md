# Dotty Architecture

Dotty is split into a platform-neutral terminal core and a native desktop host.
The current executable project is `src/Dotty/Dotty.csproj`.

## Layers

```text
Silk.NET window / GLFW / OpenGL 3.3
              │
       Dotty desktop host
              │
  Runtime sessions, tabs, config, input
              │
       TerminalAdapter
              │
 TerminalBuffer + ANSI parser + reflow
              │
    IPty → Unix helper or ConPTY
```

| Project | Responsibility |
|---|---|
| `Dotty.Abstractions` | Platform-neutral configuration, theme, input, and PTY contracts. |
| `Dotty.Terminal` | ANSI parsing, terminal buffer, cursor state, scrollback, reflow, and render snapshots. |
| `Dotty.NativePty` | `IPty` implementations: POSIX `pty-helper` and Windows ConPTY. |
| `Dotty.Runtime` | Sessions, tabs, panes, selection, clipboard, themes, Lua hooks, configuration, and input actions. |
| `Dotty.Rendering.Gpu` | Font fallback, glyph atlas, shaping, quad generation, and render caches. |
| `Dotty` | Silk.NET/GLFW window lifecycle, OpenGL renderer, keyboard/mouse integration, and desktop smoke control server. |

## Terminal data flow

1. `TerminalSession` starts the selected `IPty` backend.
2. PTY output is consumed by `TerminalAdapter` and parsed into
   `TerminalBuffer`.
3. The adapter exposes modes, cursor state, hyperlinks, and snapshots to the
   runtime and renderer.
4. The host captures a bounded render snapshot under the buffer lock, composes
   glyph/background/tab/pane quads, and submits them to OpenGL.
5. Keyboard text and key events are encoded by `TerminalKeyboardDispatcher`
   using the adapter's application and Kitty modes.
6. Resize events update all sessions and the PTY backend before the next frame.

The buffer itself is not thread-safe. PTY consumers write under its sync root;
rendering and user operations use bounded snapshot/copy sections so sustained
output cannot hold the UI for a whole raster pass.

Scrollback capacity is a per-session `ScrollbackPolicy` (line cap plus raw-byte
cap, from the `scrollback` configuration) held by `ScreenManager`; every resize
recomputes the ring capacity for the new size and reflows history into it.
Cell arenas are lazily committed reserved memory. Search does not use the
per-frame capture: `TerminalBuffer.CaptureRenderSnapshotForSearch` takes a
separate compact copy of the scrollback rows, once per search keystroke. See
[Rendering](Rendering.md#lazily-committed-cell-arenas) and
[Configuration](Configuration.md#scrollback).

## Native PTY selection

`PtyFactory.GetCapabilities()` reports the derived runtime identifier,
architecture, backend, dependency availability, and actionable failure reason.

- Linux/macOS: `UnixPty` launches the packaged `pty-helper` and controls resize
  through its loopback Unix-domain socket.
- Windows: `WindowsPty` calls ConPTY (`CreatePseudoConsole`, process attribute
  setup, and `ResizePseudoConsole`) directly.

The platform matrix, helper packaging contract, and troubleshooting procedures
are maintained in [Platform Support](PlatformSupport.md) and
[Native PTY](NativePty.md).

## Desktop lifecycle

`DottyWindowHost` owns the native window and runs all host callbacks on the
window thread. `WindowLifecycleCoordinator` queues configuration callbacks and
closes the queue idempotently. Shutdown unsubscribes configuration events,
terminates sessions, disposes input and renderer resources, releases the glyph
atlas, and disposes the optional loopback control server.

The host requests OpenGL 3.3 core. It checks the active driver version before
creating renderer resources and reports initialization failures before closing
the window. Framebuffer scale changes recompute font metrics, rebuild the glyph
atlas when required, update renderer resources, and resize sessions.

## Host prompts, paste, clipboard, and links

Decisions that need the user (risky paste, OSC 52 clipboard read, risky or
blocked link) are shown as host-owned prompts, drawn by the same renderer as the
context menu and never produced by terminal output.

| Piece | Project | Role |
|---|---|---|
| `HostPrompt` | `Dotty.Runtime` (`Prompt/`) | Builds a `ContextMenuModel`: a disabled title row, at most `MaxLines` = 8 disabled body rows of at most `MaxLineChars` = 60 characters, then one row per action (`Build`) or a single Dismiss row (`BuildNotice`). Every untrusted string goes through `SanitizeLine` (controls become visible `\xNN` escapes, bidi marks `\u{XXXX}`, truncation with an ellipsis), and the safe action (Deny/Cancel) is the initially focused row. |
| `ContextMenuModel.Dismissed` | `Dotty.Runtime` | Callback fired at most once when a visible menu closes without any item action running (Escape, click outside, replaced by another menu), then cleared. `Close()` is idempotent. |
| `PastePromptBuilder`, `ClipboardReadPromptBuilder`, `HyperlinkPromptBuilder` | `Dotty` (`Host/`) | Assemble the three prompts on top of `HostPrompt`. Link and paste prompts are placed at the pointer; the OSC 52 read prompt is centered in the window. |
| `PasteSafety`, `PasteEncoder` | `Dotty.Runtime` (`Input/`) | Pure classification (newlines, controls, paste-end marker), preview building, and sanitizing/bracketing of the bytes written to the PTY. |
| `ClipboardPasteRouter` | `Dotty` | Single funnel for Ctrl+Shift+V, middle click, and the context-menu Paste: reads the clipboard, evaluates `PasteSafety` against `clipboard.confirmPaste`/`bracketedPasteIsSafe` and the pane's bracketed-paste mode, then writes immediately or shows the prompt. It re-checks pane liveness and bracketed-paste state when the user confirms. |
| `ClipboardReadService`, `ClipboardReadQueue`, `Osc52Reply` | `Dotty` / `Dotty.Runtime` (`Clipboard/`) | OSC 52 read policy and flow. The queue (1 per session, 4 total, 30 s expiry) is filled on PTY consumer threads under a private lock and drained on the UI thread by the host loop; `Osc52Reply` encodes the reply. |
| `HyperlinkScanner` → `HyperlinkActivator` → `HyperlinkPolicy` (+ `FileTargetRules`, `HyperlinkTextMismatch`) → `SystemHyperlinkOpener` | `Dotty.Runtime` (`Hyperlinks/`) | Hit-testing, activation decision, policy, and the platform launch (`HyperlinkLauncher` builds the `ProcessStartInfo`). `Activate` does no I/O; opening is a separate asynchronous step so a prompt can intervene. This replaces the former `IHyperlinkHandler`/`DefaultHyperlinkHandler`. |

The context-menu slot (`ActiveContextMenu`) is a single shared slot. Setting it
to a different menu closes the previous visible menu **after** the swap, so a
`Dismissed` callback may install a follow-up prompt without being overwritten.
The mouse and keyboard handlers therefore clear the slot only when it still holds
the menu they just acted on (`ReferenceEquals` guard), because an item action or
`Dismissed` callback may already have replaced it.

Threading: the parser and adapter run on the PTY consumer thread and only raise
events (`ClipboardWriteRequested`, `ClipboardReadRequested`); policy, prompts, and
clipboard access run on the window thread. Render snapshots share the buffer's
append-only hyperlink table by reference, so activation resolves a link from the
snapshot it was drawn from without taking the buffer lock.

These flows are covered by unit tests of the pure pieces (see
[Testing](Testing.md)); prompt rendering, focus behavior, and native link
opening have no automated GUI coverage and are verified manually
([E2E Testing](E2ETesting.md#manual-smoke-osc-52-read-paste-confirm-and-link-confirm)).

## Configuration and control seams

`UserConfigService` uses the same platform path resolver for JSON config, Lua,
and themes. File watcher callbacks are debounced and dispatched to the window
thread. See [Configuration](Configuration.md).

For smoke tests, setting `DOTTY_TEST_PORT` starts a loopback-only
`DesktopControlServer`. The checked-in interaction harness uses `TYPE`, `KEY`,
`RESIZE`, `DUMP`, `GET_STATE`, `STATS`, `WAIT_FOR_IDLE`, and `SHUTDOWN` without
requiring `nc` or platform-specific process discovery.

## Build and test boundaries

The solution can be built on every supported host:

```bash
dotnet build Dotty.slnx -c Release
```

Build `pty-helper` before Unix host tests. Native PTY tests run on the host
platform; tests do not carry a hardcoded runtime identifier. CI validates the
four public release targets: Linux x64, Windows x64, macOS Intel, and macOS
arm64. Linux arm64 and Windows arm64 remain local/manual candidates without an
automated nightly or release-validation lane; see [Release Policy](Releasing.md).
