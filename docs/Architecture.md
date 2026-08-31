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
platform; tests do not carry a hardcoded runtime identifier. CI validates Linux
x64, Windows x64, macOS Intel, and macOS arm64, with Linux arm64 and Windows
arm64 publish validation in nightly builds.
