# Native GLFW dependency

The desktop application builds and ships the clear-code GLFW IME fork from the checked-in source at `vendor/glfw/source/`. `vendor/glfw/SOURCE.json` records the approved commit (`068858eece20cd6b6128f6f07a1d61f2dc2cbfaf`), HTTPS archive URL and verified archive SHA-256. The upstream zlib/libpng license is retained in `vendor/glfw/source/LICENSE.md`. Build and publish do not download GLFW or follow a branch; an offline build uses this vendored tree.

`scripts/native/build-glfw.py --rid <rid>` builds the native shared library for the current host architecture from that source and writes the platform library to the requested output directory. The .NET project invokes it for both build and publish outputs and overwrites any GLFW library supplied by transitive packages, so app and test runs use the pinned fork. Supported RIDs are `linux-x64`, `linux-arm64`, `win-x64`, `win-arm64`, `osx-x64`, and `osx-arm64`; cross-compiling a different host architecture is not configured.

The build requires CMake and a C compiler. Linux also requires X11 development headers (`xorg-dev`), Wayland development headers and protocols (`libwayland-dev`, `wayland-protocols`), and `libxkbcommon-dev`; Windows requires Visual Studio C/C++ build tools, a Windows SDK, and CMake; macOS requires Xcode Command Line Tools and CMake. CI installs the Linux packages before the .NET builds.

# Native desktop verification and IME composition

## Running native desktop smoke lanes

`Native desktop smoke` in GitHub Actions is an opt-in `workflow_dispatch` workflow. Choose one `lane`; each lane has a 20-minute job timeout and runs `scripts/native/desktop-smoke.py` against a published host. The smoke waits for the host's loopback control port, checks the actual GLFW-selected window backend, creates a real new tab and vertical split, and requires distinct Unicode-marked child-shell output lines in each PTY (not merely echoed input). It requests a 63x17 PTY grid followed by 91x31 and checks both reported grids, exercising shrinking and growing the window, then verifies the focused working directory and tab/pane counts and confirms a rendered frame was presented. It reports a JSON pass record; failures include the captured host log. It does not synthesize physical keyboard/focus gestures or assert pixels.

| Lane | Runner/session prerequisite | What it establishes |
|---|---|---|
| `linux-x11` | GitHub-hosted Ubuntu, Xvfb, Mesa software GL | GLFW's X11 window/context creation, actual frame presentation, POSIX PTY shell output, resize, Unicode |
| `linux-wayland` | Self-hosted Linux x64 runner labeled `dotty-wayland`, with `WAYLAND_DISPLAY` and `XDG_RUNTIME_DIR` from its compositor session | The smoke removes `DISPLAY` and checks GLFW's reported backend is native Wayland; when the session is Hyprland and `hyprctl` is installed, it additionally PID-matches `hyprctl -j clients` and requires `xwayland: false`. It then checks frame presentation and terminal behavior. |
| `windows-interactive` | Self-hosted Windows x64 runner labeled `dotty-interactive` running in an interactive user session (not Session 0/service desktop), with a working OpenGL 3.3 driver | Win32/GL context/presentation and ConPTY terminal checks |
| `macos-windowserver` | Self-hosted Apple Silicon macOS runner labeled `dotty-windowserver`, logged into an active WindowServer desktop session; OpenGL 3.3 available | Cocoa/GL context/presentation and POSIX PTY terminal checks |

Install .NET 11 SDK and Python 3 on self-hosted runners; Unix runners additionally need a C compiler and `make`. The workflow builds the POSIX PTY helper and publishes the host itself. Workflows are manually dispatched to avoid claiming hosted macOS/Windows build runners have a usable desktop: the existing CI matrix validates those platforms' compile/package assets only. The labels above are explicit operator prerequisites; the repo does not provision or own those interactive machines. A missing session, display, compositor, driver, or .NET dependency must fail the lane rather than being recorded as a pass.

Locally, after publishing the host and building the Unix PTY helper, run (Linux X11 shown):

```bash
xvfb-run -a python3 scripts/native/desktop-smoke.py --backend x11 --executable ./publish/dotty
# Wayland desktop session only: the smoke verifies GLFW's backend; Hyprland also gets a PID-specific native-client check.
python3 scripts/native/desktop-smoke.py --backend wayland --executable ./publish/dotty
```

On Windows use `python scripts/native/desktop-smoke.py --backend windows --executable .\publish\dotty.exe` from an interactive desktop. On macOS use `python3 scripts/native/desktop-smoke.py --backend macos --executable ./publish/dotty` from a logged-in WindowServer session. Keep `DOTTY_TEST_PORT` unset externally; the script sets it to `0` to request an ephemeral loopback port and creates isolated config/home directories.

The Windows smoke defaults to `--windows-console owned`: it creates a separate
console for the host and requires that the GUI has detached from it. A detached
probe process checks console attachment without disturbing the harness console.
Run the same command with `--windows-console inherited` from an attached Windows
console to require that both the harness and host remain attached to that shared
console. Both modes retain the desktop/PTY checks and report console evidence in
the JSON result.

Windows custom decorations are installed during the window load callback, after
the native HWND and rendering resources exist. Programmatic client sizes use
the HWND's measured outer/client rectangle difference rather than GLFW's standard
caption adjustment or a fixed caption-height offset. Native decorations and
non-Windows windows retain GLFW's size setter.

This is deterministic app/host integration coverage. Separate real-user passes are still required for focus transitions, physical key layout/dead keys, mouse capture/selection, clipboard, compositor/window-manager decoration, HiDPI, sleep/resume, and screenshots on each native backend; they are not covered by the control socket. A successful Linux X11 lane is not Wayland or macOS/Windows evidence.

## Native IME implementation

The host installs committed-character, physical-key, and preedit callbacks on the
same GLFW window and loaded library context. Silk.NET's generated GLFW bindings
provide the ordinary window/input API; the host-owned `NativeTextInputBridge` resolves
the pinned fork's composition exports explicitly. Missing required exports fail
initialization rather than silently reverting to committed characters alone.

The native platform paths are Wayland text-input-v3, X11 XIM, Windows IMM32, and
Cocoa's text-input client/marked-text lifecycle. These are native implementations,
not synthetic control-socket composition. The fork is a GLFW 3.6 development fork,
not an upstream stable release; `vendor/glfw/SOURCE.json` distinguishes the pinned
archive from Dotty's checked-in ownership and scalar-caret patches.

Preedit text, caret, and selected clause are kept as ephemeral composition state.
Native Unicode scalar offsets are converted to UTF-16 offsets at the managed
boundary. The scene composer uses the terminal's canonical grapheme width rules
and configured font fallback. `PreeditLayout` places the composition from the
cursor and wraps it onto the next rows of the active pane (a wide grapheme never
straddles the right edge); on the last rows it is shifted up over the previous
rows, and when it is taller than the pane, earlier rows scroll away so the caret
row stays visible (the one-row search input therefore shifts left). Cells are
drawn opaque in the pane's default foreground/background, the focused clause in
the theme selection colour and the other clauses underlined; the terminal cells
under the composition are hidden for that frame. Nothing is written to the
terminal buffer. Stable composition frames reuse
their emitted instances rather than repeatedly allocating grapheme strings.

After composition, the host converts the completed frame's framebuffer caret
rectangle to logical client coordinates before setting the native candidate
position. The active terminal pane or search query caret owns that rectangle.
Initial focus is read from GLFW; focus loss, pane/tab changes, and context-menu
ownership cancel/reset composition. Wayland uses text-input enable/disable
transactions instead of calling an unsupported native reset operation.

Committed Unicode continues through the full-scalar character callback and
existing keyboard/text route. Native composition ownership suppresses consumed
physical presses and their matching releases, so negotiated Kitty key reporting
does not also deliver the keys used to produce an IME commit. Win32 handles
`GCS_RESULTSTR` as the result route rather than broadly suppressing ordinary
`WM_CHAR` input. Callback errors are returned to the host render/event thread,
not allowed to escape across the unmanaged callback boundary.

With the opt-in `DOTTY_TEST_PORT` control socket, `IME` reports the selected backend,
current preedit, caret, and selection offsets for observation. It cannot inject
composition. Real qualification must combine native keyboard events, an actual
input-method service, visible candidate/preedit screenshots, exact committed PTY
bytes under Kitty negotiation, and focus/pane cancellation. Conversion and scene
tests, CI compilation, and the desktop control-socket smoke above are not
substitutes for that qualification.

Windows and macOS interactive qualification requires the real desktop sessions
listed above. It remains explicitly blocked when those hosts are unavailable;
Linux results must not be represented as Windows/macOS passes.

## Manual lanes for selection, window reports, and injection

Interactive checks that no automated lane covers, to run on each native desktop session before a release (recipes in [E2E Testing](E2ETesting.md#manual-smoke-x11-primary-selection-copy-on-select-and-middle-click)):

- **X11 PRIMARY selection** (`mouse.copyOnSelect`, `mouse.middleClickAction`, OSC 52 `p`/`s`): interoperation with another X11 application is checked on an X11 session only. On Wayland, macOS, and Windows the selection is an in-process buffer, so the manual check is that it is *not* visible to other applications.
- **Window position, screen size, and visibility reports** (`CSI 13/15 t`, mode 2033, `CSI ? 998 n`): depend on GLFW; minimize is not observable on Wayland.
- **`CSI 8 t` resize** with `vt.windowResizeAllowed`: the window manager or compositor can still constrain the size.
- **Shell-integration injection** for bash, zsh, fish, and PowerShell through a real Dotty window, including the Nix/`ZDOTDIR` situation described in [Shell Integration](ShellIntegration.md#automatic-injection).
- **Windows ConPTY** passthrough of the window, cursor-position, checksum, and visibility queries is unverified.

Linux results must not be represented as Windows or macOS passes.

## Input, IME, and accessibility support matrix

"Implemented" means the code path exists; "qualified" means it was exercised on
that platform with a real session as described below. "Planned" names the Wave 4
item expected to change the row; it is not a release date.

| Feature | Linux Wayland | Linux X11 | Windows | macOS | Planned |
|---|---|---|---|---|---|
| Committed Unicode text, Kitty keyboard protocol, legacy encoding | Implemented; qualified (IME commit under Kitty flags 0 and 31) | Implemented; not qualified | Implemented; not qualified | Implemented; not qualified | W4-B13 to W4-B16, W4-D1 |
| IME preedit and candidate placement | Implemented (text-input-v3); qualified with Fcitx/Rime | Implemented (XIM); not qualified | Implemented (IMM32); not qualified | Implemented (Cocoa marked text); not qualified | W4-C17, W4-D1 |
| AltGr (Right Alt) text | Implemented (`keyboard.rightAlt` `auto`: AltGr/ISO Level 3 composes, Right Alt on a layout without AltGr is Meta); headless replay only, not qualified | Same | Same (AltGr = Ctrl+Alt) | Right Option composes by default; headless replay only, not qualified | W4-D1 |
| Left Alt / Option composing text | Left Alt is Meta by default (`ESC` + the layout character, Shift kept); `keyboard.leftAlt` `compose` makes it type text; not qualified | Same | Same | Left Option composes by default (`@`, `é`); `keyboard.leftAlt` `meta` makes it Meta; not qualified | W4-D1 |
| PRIMARY selection | In-process only | System PRIMARY | In-process only | In-process only | |
| File drag and drop | Not supported | Not supported | Not supported | Not supported | W4-C18 |
| Screen-reader accessibility | Not supported | Not supported | Not supported | Not supported | W4-C22 (portable model), W4-D4 (Windows UIA, then macOS); Linux AT-SPI is not planned |

Bidirectional (RTL) text is not supported on any platform; see
[Rendering](Rendering.md#text-and-rendering-support-matrix).

## Current qualification status

The final portable-AOT publish and full multi-target test suite completed with
1,224 passed, zero failed, and one skipped test. A real native Wayland window
was exercised, and a captured terminal image confirmed that green ASCII and
multicolor emoji retain their intended colors. This is Linux Wayland evidence
only.

The final native Wayland qualification used a real Fcitx/Rime service and
wtype-generated native Wayland keyboard events; it is not a physical-hardware
keyboard test. Rime displayed the “ni hao” preedit and candidate popup at the
terminal caret, while preedit produced no PTY bytes. The commit was verified in
two input modes: with Kitty flags 31, Space emitted each committed Unicode scalar
once as CSI-u (20320, 22909); with flags 0, Space emitted the UTF-8 text
“你好” once. Splitting the pane and losing window focus both canceled composition,
with an empty IME state and no stale bytes delivered to the old or new pane.

This qualification ran on Linux Wayland only. Windows and macOS interactive
qualification remain blocked pending their required desktop hosts. The first
native IME attempt showed no preedit because its isolated run lacked system XKB
data; the successful retry used a private read-only XKB-data mount and an
explicitly activated private D-Bus input-method service. That initial failure was
an environment setup issue, not evidence of a native bridge defect.

## Changelog

| Date | Change |
|------|--------|
| 2026-11-01 | W4-C17: preedit wraps/shifts instead of clipping, theme colours on an opaque background, per-clause styles, covered cells hidden. |
