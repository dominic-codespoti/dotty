# Native desktop verification and IME feasibility

## Running native desktop smoke lanes

`Native desktop smoke` in GitHub Actions is an opt-in `workflow_dispatch` workflow. Choose one `lane`; each lane has a 20-minute job timeout and runs `scripts/native/desktop-smoke.py` against a published host. The smoke waits for the host's loopback control port, checks the actual GLFW-selected window backend, creates a real new tab and vertical split, and requires distinct Unicode-marked child-shell output lines in each PTY (not merely echoed input). It also requests a 91x31 PTY grid resize and checks the reported grid, verifies the focused working directory and tab/pane counts, and confirms a rendered frame was presented. It reports a JSON pass record; failures include the captured host log. It does not synthesize physical keyboard/focus gestures or assert pixels.

| Lane | Runner/session prerequisite | What it establishes |
|---|---|---|
| `linux-x11` | GitHub-hosted Ubuntu, Xvfb, Mesa software GL | GLFW's X11 window/context creation, actual frame presentation, POSIX PTY shell output, resize, Unicode |
| `linux-wayland` | Self-hosted Linux x64 runner labeled `dotty-wayland`, with `WAYLAND_DISPLAY` and `XDG_RUNTIME_DIR` from its compositor session | The smoke removes `DISPLAY` and checks GLFW's reported backend is native Wayland; when the session is Hyprland and `hyprctl` is installed, it additionally PID-matches `hyprctl -j clients` and requires `xwayland: false`. It then checks frame presentation and terminal behavior. |
| `windows-interactive` | Self-hosted Windows x64 runner labeled `dotty-interactive` running in an interactive user session (not Session 0/service desktop), with a working OpenGL 3.3 driver | Win32/GL context/presentation and ConPTY terminal checks |
| `macos-windowserver` | Self-hosted Apple Silicon macOS runner labeled `dotty-windowserver`, logged into an active WindowServer desktop session; OpenGL 3.3 available | Cocoa/GL context/presentation and POSIX PTY terminal checks |

Install .NET 10 SDK and Python 3 on self-hosted runners; Unix runners additionally need a C compiler and `make`. The workflow builds the POSIX PTY helper and publishes the host itself. Workflows are manually dispatched to avoid claiming hosted macOS/Windows build runners have a usable desktop: the existing CI matrix validates those platforms' compile/package assets only. The labels above are explicit operator prerequisites; the repo does not provision or own those interactive machines. A missing session, display, compositor, driver, or .NET dependency must fail the lane rather than being recorded as a pass.

Locally, after publishing the host and building the Unix PTY helper, run (Linux X11 shown):

```bash
xvfb-run -a python3 scripts/native/desktop-smoke.py --backend x11 --executable ./publish/dotty
# Wayland desktop session only: the smoke verifies GLFW's backend; Hyprland also gets a PID-specific native-client check.
python3 scripts/native/desktop-smoke.py --backend wayland --executable ./publish/dotty
```

On Windows use `python scripts/native/desktop-smoke.py --backend windows --executable .\publish\dotty.exe` from an interactive desktop. On macOS use `python3 scripts/native/desktop-smoke.py --backend macos --executable ./publish/dotty` from a logged-in WindowServer session. Keep `DOTTY_TEST_PORT` unset externally; the script sets it to `0` to request an ephemeral loopback port and creates isolated config/home directories.

This is deterministic app/host integration coverage. Separate real-user passes are still required for focus transitions, physical key layout/dead keys, mouse capture/selection, clipboard, compositor/window-manager decoration, HiDPI, sleep/resume, and screenshots on each native backend; they are not covered by the control socket. A successful Linux X11 lane is not Wayland or macOS/Windows evidence.

## IME feasibility assessment (no IME implementation)

Dotty creates its GLFW window through Silk.NET.Windowing (`Window.Create`), but installs native GLFW key and committed-character callbacks through the already-loaded GLFW library context. Native key actions provide press/repeat/release phases; the character callback retains the complete Unicode scalar (`uint`) rather than Silk input's 16-bit character conversion. The project references Silk.NET.GLFW 2.23.0 and `Ultz.Native.GLFW` 3.4.0. Real Wayland keyboard injection of `a🙂` passed both negotiated Kitty flags 31 and legacy flags 0, including U+1F642 associated text and releases without text. This is committed Unicode input, not IME composition support. GLFW exposes no portable preedit, candidate-placement, or commit/cancel lifecycle contract.

Silk.NET.Windowing.Common documents `IView.Handle` as a handle to the underlying window. That is a GLFW window handle, not a portable native `HWND`, `NSWindow`/view, X11 `Window`, or Wayland surface, and GLFW's ordinary generated binding exposes no portable composition API. [INFERENCE] IME support can retain the existing renderer, but needs an explicit host-owned native integration seam and platform plumbing after the GLFW window/context exists:

- **Windows:** obtain HWND (GLFW native access, e.g. `glfwGetWin32Window`) and integrate TSF/IMM32 composition messages, candidate/commit handling, DPI-aware client caret rectangle and focus lifecycle.
- **macOS:** obtain the Cocoa window/view (e.g. `glfwGetCocoaWindow`) and implement `NSTextInputClient`/input-context marked-text, selected-range, candidate and commit callbacks on the AppKit main thread.
- **Linux X11:** obtain X11 display/window (e.g. `glfwGetX11Display`/`glfwGetX11Window`) and integrate the selected input-method framework (commonly XIM), including preedit, spot location, commit and focus.
- **Linux Wayland:** obtain the Wayland display/surface (e.g. `glfwGetWaylandDisplay`/`glfwGetWaylandWindow`) and integrate `zwp_text_input_v3` (or the compositor-supported text-input protocol), including enable/disable, surrounding/selection state as required, cursor rectangle, preedit and commit. A Wayland text-input protocol is distinct from GLFW's committed character event.

These native accessor names denote upstream GLFW native-platform APIs, not APIs demonstrated by Silk's common `IView.Handle` contract. The package metadata confirms GLFW 3.4.0 native assets for the project's supported x64/arm64 Windows, macOS, and Linux RIDs; Silk's generated managed API does not supply a portable IME callback or a platform-native window handle type. Any implementation must verify native accessor availability/version and threading on each target. The external host prerequisite is native message/event plumbing: Dotty must install platform integrations on the UI thread, expose native handles plus caret/focus/composition callbacks to input handling, and translate preedit/commit into app-level composition state and terminal committed text. No current app seam delivers composition state, and this task does not add one. The direct native integration route is **feasible in principle** because the host owns the GLFW window and its GLFW handle; callback registration alone is not a substitute for platform IME clients/protocols.

Silk's installed assemblies were probed without creating a window: `Glfw` inherits `NativeAPI`, whose public `Context` is `INativeContext`. The loaded GLFW library's native export resolver returned nonzero addresses for `glfwGetKeyName` and `glfwGetPlatform`; this is distinct from `Glfw.GetProcAddress`, which asks for a GL procedure. A reflection check found 122 shared `Silk.NET.Input.Key` / GLFW `Keys` names with zero numeric mismatches, including `Unknown=-1`. `NativeGlfwMetadata` caches the native exports. It asks for a known key's printable name first (GLFW ignores the scancode in this case); only a missing name falls back to `GLFW_KEY_UNKNOWN` with the same physical scancode. Negative synthetic scancodes are never passed to GLFW, while native scancode 0 remains valid. The helper decodes the first UTF-8 scalar without allocating and rejects invalid/control characters. Physical identity, current-layout primary Unicode, and committed text remain separate; missing platform metadata is omitted rather than guessed.

No IME composition probe was run. This pass resolves the integration boundary only; full IME requires native input-method services and per-platform composition implementations. The desktop smoke above tests committed UTF-8/Unicode, not intermediate composition, candidate placement, or commit/cancel behavior.
