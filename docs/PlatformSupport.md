# Platform Support

Dotty is a native desktop terminal host backed by a platform-neutral terminal
core. The desktop host uses Silk.NET, GLFW, OpenGL, and SkiaSharp. PTY startup
is selected by `PtyFactory` and is reported by `PtyCapabilities`.

## Support matrix

| Platform | Release target | Build-only target | PTY backend | Desktop requirement |
|---|---|---|---|---|
| Linux | x64 | arm64 | POSIX `pty-helper` | X11 or Wayland desktop with OpenGL 3.3 |
| macOS | x64, arm64 | — | POSIX `pty-helper` | macOS desktop with OpenGL 3.3 |
| Windows 10 build 17763+ / Windows 11 | x64 | arm64 | ConPTY | OpenGL 3.3 driver |

Linux arm64 and Windows arm64 remain unpromoted local/manual candidates; there
is no automated nightly or release-validation lane for them. Promotion requires
native PTY and desktop smoke runs on native arm64 hosts. See [Release Policy](Releasing.md).

## Requirements

All platforms require the .NET 11 SDK for source builds and an OpenGL 3.3 core
capable driver for the desktop host. Release artifacts are self-contained.

Linux and macOS source builds also require a C compiler and `make` for the POSIX
helper. Windows uses the operating system ConPTY API and does not need a helper
compiler. Windows ConPTY requires build 17763 or newer.

Minimum OS versions of the release archives are build targets checked against
the shipped binaries, not promises: the Linux x64 build runs in a Debian 11
container and targets glibc 2.31, the macOS builds target macOS 15.0 (the bundled Lua library from KeraLua
is built for macOS 15), and
Windows needs build 17763 for ConPTY (declared, not measured). CI fails when a
shipped binary needs more than its target (`scripts/dist/check_baseline.py`,
`scripts/dist/baseline-targets.json`), and each release's notes and attached
`baselines.json` state the measured values and the system libraries the Linux
and macOS binaries load. None of these minimums has been verified by running
the archives on that oldest OS version. See
[Release policy](Releasing.md#platform-baselines).

## Build and run

From the repository root:

```bash
# Linux or macOS
make -C src/Dotty.NativePty
dotnet build Dotty.slnx -c Release
dotnet run --project src/Dotty/Dotty.csproj
```

```powershell
# Windows
dotnet build Dotty.slnx -c Release
dotnet run --project src\Dotty\Dotty.csproj
```

Run the host-native tests on the machine that will run the host:

```bash
dotnet test --project tests/Dotty.NativePty.Tests/Dotty.NativePty.Tests.csproj -c Release
```

The CI matrix builds and tests Linux x64, macOS Intel, macOS arm64, and Windows x64
on native runners, and NativeAOT-publishes the same four RIDs with the release
configuration (`aot-publish`): file asserts, `dotty --version` and
`dotty +explain-config font.size` run on each, and IL2xxx/IL3xxx warnings in
Dotty's own code fail the job. Linux arm64 and Windows arm64 have no automated
nightly or release-publishing validation lane; work for those candidates is local/manual.

## Desktop presentation and native verification

The desktop host requests an OpenGL 3.3 core context on every platform, with
VSync disabled and automatic buffer swaps disabled. It renders and explicitly
swaps only after a pending invalidation produces a complete dirty frame. On a
native Wayland session this is a manual-swap, demand-driven presentation
policy: clean frames retain the current front buffer instead of continuously
swapping. Focus, resize, content, input, tab/pane topology, overlays, cursor
blink, theme/atlas changes, selection, and selection autoscroll all invalidate
the next frame.

Window focus loss resets transient keyboard and mouse state before focus can
return, including held modifiers, pressed buttons, pointer/reporting ownership,
selection-drag ownership, hover state, and click tracking. When terminal focus
reporting is enabled, the active pane receives the corresponding focus report.
Each pane keeps its own selection and scroll offset; while a pane is scrolled
through its retained scrollback, its cursor is not drawn.

Keep these verification targets separate:

- The CI Xvfb startup check is Linux X11-only and is not native desktop
  evidence. The opt-in [native desktop smoke workflow](NativeDesktopAndIme.md)
  uses the loopback control protocol to prove window/context creation, a
  presented frame, PTY shell output including Unicode, and terminal resize.
- Its hosted X11 lane uses Xvfb and software Mesa. Native Wayland verification
  must run inside a real Wayland compositor.
  Exercise startup, focus loss/return during a held key or mouse drag,
  scrolling and selection autoscroll, pane/tab changes, resize, and cursor
  suppression while scrolled.
- Local native smoke runs have passed on Linux/Hyprland through both the native
  Wayland backend and X11/XWayland. Each passed the requested 91x31 resize,
  two-tab/two-pane checks, and Unicode child-shell output in a new tab and split
  pane. The X11/XWayland run reported an active floating Dotty window at
  756x610. These control-driven results do not establish physical keyboard or
  focus behavior.
- Native desktop lanes for macOS and Windows have not been run in this Linux
  session; their rendering, input, OpenGL, and presentation behavior remains
  unverified here. Workflow execution on native WindowServer/interactive runners
  is still required. Passing Linux X11/XWayland does not establish these paths.
- The control-driven smoke does not verify physical focus/input, clipboard,
  window-manager behavior, HiDPI, or IME composition. See the linked native
  verification and IME assessment for lane prerequisites and manual checks.

## Configuration and user data

The configuration file is JSON and is watched for atomic changes:

- Linux: `$XDG_CONFIG_HOME/dotty/config.json`, or `~/.config/dotty/config.json`
- macOS: `~/.config/dotty/config.json` (or `$XDG_CONFIG_HOME/dotty`) when that
  directory exists, otherwise `~/Library/Application Support/Dotty/config.json`
- Windows: `%APPDATA%/Dotty/config.json`

Set `DOTTY_CONFIG_HOME` to override the complete configuration directory. Theme
files are in `<config-directory>/themes`; Lua startup scripts (`config.lua` or
`init.lua`) are in the configuration directory. The watcher debounces writes,
rename/write patterns, and dispatches accepted changes to the desktop UI thread.

Dotty does not write `config.json` itself; `dotty +init-config` creates a
commented starter file. Files Dotty writes for itself go to the per-user state
directory: `$XDG_STATE_HOME/dotty` or `~/.local/state/dotty` on Linux,
`~/Library/Application Support/Dotty` on macOS, and `%LOCALAPPDATA%\Dotty` on
Windows. `dotty +diagnose` prints both directories (see
[Configuration](Configuration.md#file-location)).

## Diagnostics

`PtyFactory.GetCapabilities()` reports:

- derived runtime identifier and process architecture;
- selected backend (`UnixHelper` or `ConPty`);
- native dependency availability;
- an actionable diagnostic when the platform cannot start a PTY.

For a desktop startup failure, Dotty reports the required OpenGL version and the
initialization exception before closing the window so the process does not leave
an orphaned PTY. Fonts are resolved from the configured comma-separated stack;
missing families fall back to the platform default while cell metrics clamp
non-finite or non-positive values to safe dimensions.

### Crash log

The desktop process keeps a local crash log in the state directory (see
[Configuration](Configuration.md#file-location)): `logs/crash.log`, for example
`~/.local/state/dotty/logs/crash.log` on Linux. It is never uploaded; Dotty has
no network reporting. An entry is written for:

- an unhandled exception on any thread (`AppDomain.UnhandledException`) and an
  exception that ends `Main`, after which Dotty prints
  `dotty: ... crash report written to <path>` to standard error;
- an unobserved faulted task (`TaskScheduler.UnobservedTaskException`), which does
  not end the process;
- a GLFW error reported through GLFW's error callback, at most 16 per process
  (the 16th entry says later ones are dropped). Dotty installs this callback
  after window creation in place of Silk.NET's default, which throws on Windows
  and collects every error in memory elsewhere.

Each entry has a UTC timestamp, the source, the Dotty version, the .NET runtime
and identifier, the OS description and architecture, then the exception text
(type, message, stack trace, inner exceptions), cut after 32768 characters. It
contains no environment variables, terminal content, or configuration. When an
append would grow `crash.log` past 256 KiB, the file is rotated to
`crash.1.log` and `crash.2.log` (the oldest is deleted), so the log stays below
about 768 KiB. Command-line actions and usage errors never write it. Native
crashes (for example `SIGSEGV` inside a driver) bypass the managed handlers and
are not recorded. `dotty +diagnose` prints the log path and whether it has
entries.

## Troubleshooting

### `pty-helper` is missing or not executable

Build it beside the repository source and verify its mode:

```bash
make -C src/Dotty.NativePty
test -x src/Dotty.NativePty/bin/pty-helper
```

Published Unix artifacts must contain an executable `pty-helper` beside
`dotty`. `DOTTY_PTY_HELPER=/path/to/pty-helper` overrides the search. Otherwise
the runtime checks the published directory, `../libexec/dotty/` and
`../lib/dotty/`; development builds (everything except `dotnet publish -c
Release`) then try the repository build locations and each directory in `PATH`.
The error message lists every path it probed. See
[Native PTY](NativePty.md#locating-the-helper).

### PTY reports an unsupported platform or architecture

Run the native test project and inspect the capability diagnostic. Supported
runtime identifiers are `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`,
`win-x64`, and `win-arm64`; only the release targets in the matrix above are
promoted for distribution.

### ConPTY is unavailable

Use Windows 10 build 17763 or newer, or Windows 11. Confirm the Windows SDK is
installed and run the Windows native PTY tests without excluding
`WindowsPtyTests`.

### OpenGL startup fails

Dotty requests an OpenGL 3.3 core context. Update the graphics driver, confirm
the process has access to the desktop display, and retry under the platform's
native session. CI runs only the Linux X11 smoke under Xvfb; Wayland/Weston,
macOS desktop, and Windows desktop paths are verified manually or on local
native sessions. X11/Xvfb startup does not prove Wayland, macOS, or Windows GUI
behavior.

### The configured font is not found

Provide a comma-separated fallback stack, for example
`JetBrains Mono, Cascadia Code, Liberation Mono, monospace`. Dotty keeps the
terminal grid usable when a requested family is absent and clamps invalid font
size, line-height, and scale values.

### Configuration changes are not applied

Check `DOTTY_CONFIG_HOME` and confirm that `config.json` is replaced atomically
rather than written into another directory. Invalid JSON preserves the last
valid configuration; the host reports the parse error through its diagnostics.

## Desktop smoke contract

The optional loopback control interface is enabled with `DOTTY_TEST_PORT` and
is used only by local/CI smoke harnesses. It accepts one newline-delimited
command per connection: `TYPE`, `KEY`, `RESIZE`, `DUMP`, `GET_STATE`, `STATS`,
`WAIT_FOR_IDLE`, and `SHUTDOWN`. It binds to `127.0.0.1` only.

The checked-in harness is:

```bash
DOTTY_TEST_STATE_DIR=/tmp/dotty-smoke \
  .opencode/skills/terminal-tester/dotty-interact.sh launch
DOTTY_TEST_STATE_DIR=/tmp/dotty-smoke \
  .opencode/skills/terminal-tester/dotty-interact.sh state
DOTTY_TEST_STATE_DIR=/tmp/dotty-smoke \
  .opencode/skills/terminal-tester/dotty-interact.sh close
```

The harness uses Python sockets instead of assuming `nc`, propagates build and
host failures, scopes its PID/state files, and only terminates the PID it
started. CI uses `xvfb-run` for Linux X11 smoke only. Wayland, macOS, and
Windows desktop runs are manual/local.

## Manual link opener checks

Ctrl+click on a link hands an approved, canonical URI to the operating system
(`SystemHyperlinkOpener`): `xdg-open` on Linux, `open` on macOS, and ShellExecute
on Windows. Unit tests cover the policy and the launch arguments
(`HyperlinkPolicyTests`, `HyperlinkLauncherTests`) but **nothing in CI starts a
real handler or draws the confirmation prompt**, so the following are manual,
per-platform checks to run on a native desktop session before a release (setup
and prompt expectations are in
[E2E Testing](E2ETesting.md#manual-smoke-osc-52-read-paste-confirm-and-link-confirm)):

| Platform | Check |
|---|---|
| Windows 10 build 17763+ / 11 | An `https` link opens the default browser; a `mailto:` link opens the mail client (or shows a "Could not open link" notice when none is registered). Print an OSC 8 link whose target contains a space, a double quote, or `^` (for example `https://example.com/a b"c`): exactly one page opens and nothing else is executed. A `file:///C:/Users/<you>/note.txt` link opens in the associated application; `file://server/share/x.txt` (UNC), a path with `..`, and `file:///C:/tools/x.exe` show a "Link blocked" notice. A target longer than 2083 characters is blocked. |
| macOS (Intel and Apple Silicon) | `https` and `mailto:` links open in the default handlers; a `file:///` link to a text file opens it; a `.app`, `.command`, or `.dmg` target and any file with an execute bit are refused. |
| Linux | With `xdg-open` installed, `https`, `mailto:`, and a `file:///` link to a text file open in the desktop defaults; a script with an execute bit is refused. With `xdg-open` absent from `PATH` the user sees a "Could not open link" notice naming `xdg-open`. Verify on X11 and on Wayland. |

On every platform, confirm that Cancel (and Escape or a click outside the prompt)
launches nothing, and that **Copy link address** copies the canonical URL, not the
text printed in the terminal.

## Primary selection, window reports, and visibility by platform

These behaviors depend on what GLFW and the window system expose. The decision logic is unit-tested; the per-platform results below are **manual verification** (recipes in [E2E Testing](E2ETesting.md#manual-smoke-x11-primary-selection-copy-on-select-and-middle-click)), and CI exercises none of them.

| Behavior | Linux X11 | Linux Wayland | macOS | Windows |
|---|---|---|---|---|
| PRIMARY selection (`mouse.copyOnSelect` `primary`/`both`, `primary-paste`, OSC 52 `p`) | The system PRIMARY selection, through GLFW's X11-only `glfwGetX11SelectionString`/`glfwSetX11SelectionString` | In-process buffer only; OSC 52 `p`/`s` is dropped | In-process buffer only; OSC 52 `p`/`s` is dropped | In-process buffer only; OSC 52 `p`/`s` is dropped |
| `CSI 13 t` window position, `CSI 15 t` screen size | `glfwGetWindowPos` and the primary monitor's video mode, polled every 250 ms | the position query is skipped (a Wayland client cannot know its position), so `13 t` reports `0;0`; the screen size uses the same monitor query | same calls as X11 | same calls as X11 |
| Visibility (mode 2033, `CSI ? 998 n`) | Not-visible when GLFW reports the window iconified, the framebuffer is zero-sized, or the tab is not active | Minimizing is not observable (no GLFW iconified state); zero-size framebuffer and tab switches still count | as X11 | as X11 |
| `CSI 8 t` resize (`vt.windowResizeAllowed`) | Sets the window client size; the window manager may constrain it | same; compositor behavior is unverified | same | same |

Notes:

- The "same calls" cells state what the code does, not that the reported values are meaningful on that platform: if an export is missing the position stays `0;0` and the screen size falls back to the text-area size, and the screen size is that of the **primary** monitor, not necessarily the one holding the window. Whether the monitor query returns a useful size on Wayland was not verified.
- Resize requests are ignored when the gate is off, with several tabs, with split panes, or when the window is maximized, fullscreen, or minimized; see [Parsing](Parsing.md#window-operations-csi-ps--ps--ps-t).
- **Windows ConPTY:** that window-report, DECXCPR, DECRQCRA, and visibility queries (and their replies) pass through ConPTY unchanged to the application is **unverified**. The conformance run uses the Unix PTY path only (see [Conformance](Conformance.md)).
- Shell-integration auto-injection on Windows supports PowerShell and bash only; zsh and fish are not injected there (see [Shell Integration](ShellIntegration.md#automatic-injection)).

## Kitty graphics and pixel sizes by platform

The [Kitty graphics protocol](KittyGraphics.md) is off by default
(`graphics.kittyProtocol`). Nothing below has been run on any platform; it is what
the code does, and every cell `needs runtime verification`.

| Behavior | Linux | macOS | Windows |
|---|---|---|---|
| Direct transmission (`t=d`) | yes | yes | yes, if the APC reaches Dotty (see below) |
| File and temp-file media (`t=f`, `t=t`) | yes, with the path policy | yes, with the path policy | yes, with the path policy (UNC and device paths denied) |
| Shared memory (`t=s`) | `/dev/shm/<name>` | **unsupported**: the reply is `EINVAL:Unsupported transmission type` and clients fall back to direct | named file mapping |
| APC delivery | direct | direct | ConPTY drops APC unless `graphics.windowsPassthrough` is enabled and honoured by the OS build [INFERENCE]; experimental |
| Pixel size in `TIOCGWINSZ` | resize protocol carries `xpixel`/`ypixel`; the child's first `TIOCGWINSZ` uses `DOTTY_INITIAL_XPIXEL/YPIXEL` | same as Linux | no `TIOCGWINSZ` and ConPTY has no pixel channel; the pixel arguments are ignored by the backend |
| `CSI 14 t` / `16 t` / `18 t` | answered from host-supplied sizes | same | same |

The pixel size is the text area in physical pixels (cell size times grid), sent
with every resize, including a pixel-only change such as a zoom or DPI change.
See [Native PTY](NativePty.md#pixel-sizes) and [Windows ConPTY](WindowsConPty.md#kitty-graphics-passthrough).

## Promotion gates

Support promotion is staged:

1. **Release tier:** Linux x64, macOS x64/arm64, and Windows x64 pass native
   PTY tests, NativeAOT publish with native-asset validation and a windowless
   `--version`/`+explain-config` run, extracted artifact checks,
   checksum/manifest generation, and the measured glibc/macOS baseline within
   its target. Release CI also renders frames from the extracted Linux archive
   under Xvfb with software Mesa through the control interface; macOS and
   Windows desktop startup is verified manually/local.
2. **Architecture candidate tier:** Linux arm64 and Windows arm64 remain
   unpromoted local/manual candidates. There is no automated nightly or release
   validation lane for these targets. Promotion requires native arm64 PTY,
   desktop, and extracted artifact smoke on native hosts; these GUI checks are
   manual/local rather than CI coverage.
3. **Regression hold:** Any failed native matrix, missing native asset, OpenGL
   startup failure, orphan process, or checksum mismatch blocks promotion until
   the failing scenario is reproduced and rerun successfully.

## Release checklist

- [ ] Restore and build `Dotty.slnx` on every release runner.
- [ ] Build and package `pty-helper` for Unix artifacts.
- [ ] Run native PTY tests without filtering the host backend.
- [ ] Verify `dotty`/`dotty.exe`, native GLFW/Skia/Lua assets, and helper placement (`scripts/dist/verify-publish.*`).
- [ ] Confirm every archive carries `License.md`, `THIRD-PARTY-NOTICES.md` and `licenses/GLFW-LICENSE.md` (`scripts/dist/verify_archive.py`), and that `THIRD-PARTY-NOTICES.md` lists every shipped dependency.
- [ ] Render a frame from the extracted Linux archive under Xvfb with software Mesa (`desktop-smoke.py`, the automated GUI coverage).
- [ ] Check the measured baselines in `baselines.json` and the generated "System requirements" notes.
- [ ] Manually verify Wayland, macOS, and Windows desktop startup on native sessions.
- [ ] Extract every archive into a clean directory and run the host.
- [ ] Generate `MANIFEST.txt`, `SHA256SUMS` and the provenance attestation; verify checksums (`sha256sum -c SHA256SUMS`) and `gh attestation verify`.
- [ ] Record unsupported architecture candidates separately from release assets.

## Desktop identity

| Item | Value / file |
|---|---|
| App id, Wayland `app_id`, X11 `WM_CLASS` class | `io.github.dominic_codespoti.Dotty` (X11 instance `dotty`), set with `glfwWindowHintString` before the window is created |
| Window title | `window.title`, default `Dotty`; then the active tab title |
| Icon master | `assets/icons/dotty.svg`; `scripts/dist/gen-icons.py` (standard-library Python) writes `dotty-{16..512}.png`, `dotty.ico`, `dotty.icns` |
| Window icon | Embedded PNGs set with `glfwSetWindowIcon` on X11 and Windows; Wayland and macOS take it from the desktop entry / app bundle |
| Linux desktop entry | `packaging/linux/io.github.dominic_codespoti.Dotty.desktop` (`StartupWMClass` = app id) |
| macOS bundle | `packaging/macos/Info.plist` (bundle id = app id, `NSHighResolutionCapable`, `dotty.icns`) |
| Windows | `packaging/windows/app.manifest` (per-monitor-v2 DPI awareness), `ApplicationIcon` `dotty.ico`, GUI subsystem (`WinExe`); see [Command Line](CommandLine.md) for console output |

Not yet verified on a real desktop: X11 class / Wayland app_id matching the desktop entry, the window icon,
Windows console attach and DPI manifest, macOS bundle launch.

## Changelog

| Date | Change |
|------|--------|
| 2026-11-01 | W4-C13: desktop identity (app id, icon set, desktop entry, Info.plist, Windows manifest). |
