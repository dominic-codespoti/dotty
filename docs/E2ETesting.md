# End-to-End Testing Guide

Dotty has two layers of compatibility tests:

1. Headless terminal-core tests for parser, input, buffer, resize, and protocol behavior.
2. Native host smoke tests for PTY startup, rendering, input, resize, focus, paste,
   and shutdown.

The executable host is `src/Dotty/Dotty.csproj`. There is no separate
`Dotty.E2E.Tests` project.

## Headless contract tests

Run the full solution tests from the repository root:

```bash
dotnet test --solution Dotty.slnx -c Release --nologo
```

The following tests are platform-neutral and should run on every supported OS:

- `Dotty.App.Tests.ParserEdgeCaseTests`
- `Dotty.App.Tests.P0TerminalCompatibilityTests`
- `Dotty.App.Tests.TerminalInputEncoderTests`
- `Dotty.App.Tests.TerminalKeyboardDispatcherTests`
- `Dotty.App.Tests.TerminalBufferCursorTests`
- `Dotty.Terminal.Tests.ReflowResizeTests`

Native PTY tests must run on the host platform:

```bash
dotnet test --project tests/Dotty.NativePty.Tests/Dotty.NativePty.Tests.csproj \
  -c Release --nologo
```

Windows CI must execute `WindowsPtyTests`; Unix CI must build `pty-helper`
before executing `UnixPtyTests`.

## Host control interface

When `DOTTY_TEST_PORT` is set, the host exposes the TCP control interface used
by the developer smoke harness. Commands are newline-delimited:

| Command | Purpose |
|---|---|
| `TYPE:<text>` | Send text input |
| `KEY:<name>` | Send a special key |
| `RESIZE:<columns>:<rows>` | Resize the active terminal |
| `DUMP` | Return visible terminal text |
| `GET_STATE` | Return dimensions, cursor, and scrollback |
| `WAIT_FOR_IDLE[:<timeoutMs>]` | Answer `IDLE` once the render pipeline is quiet, or `TIMEOUT` after the timeout (default 5000 ms, at most 9000 ms) |
| `STATS` | Return tab/session statistics and frame-timing percentiles (`frameTimings`) |
| `GL` | Return the OpenGL driver and window diagnostics as JSON |
| `SCREENSHOT:<path>` | Write the next presented frame to `<path>` as PNG; answers `OK <width>x<height> <full path>` |
| `SHUTDOWN` | Close the host cleanly |

The protocol is transport-only. Assertions about parser modes, exact PTY bytes,
focus reports, paste wrappers, and reflow remain in deterministic tests.

`WAIT_FOR_IDLE` is answered on a frame that found nothing to draw and only when
every condition holds: no pending frame reasons, no pending host wake, no
synchronized-update hold, every visible pane presented at its current buffer
generation with no unparsed PTY output chunks, no deferred image upload, and
the framebuffer size and glyph atlas version already presented. Until then the
request is parked (the UI thread keeps running); the deadline is part of the
idle wait, so `TIMEOUT` arrives on time. The control server abandons any
request after 10 s, hence the 9000 ms ceiling.

`SCREENSHOT:<path>` forces one frame and reads its back buffer with
`glReadPixels` after the last draw and before `SwapBuffers` (the back buffer is
undefined after a swap, notably on Wayland). The image is the framebuffer in
device pixels (HiDPI sizes are not scaled down). Rows are flipped to top-down,
alpha is forced opaque unless the window has a transparent framebuffer, and
premultiplied colour is un-premultiplied by Skia's PNG encoder. Encoding and the
file write run on the thread pool; a relative path resolves against the host's
working directory. A frame withheld by a synchronized update delays the capture.

`STATS` adds `frameTimings` with `compose`, `render`, `swap`, `presentInterval`
and `inputToSwap`, each `{"count","p50Ms","p95Ms","maxMs"}` over the newest 512
samples (nearest-rank percentiles). `compose` and `render` are CPU time on the
UI thread; `swap` is the `SwapBuffers` call; `presentInterval` is the time
between two swap returns; `inputToSwap` runs from the oldest input event not yet
presented to the return of the swap that presents it (the internal part of
input-to-photon latency, without compositor or display time).

`GL` returns `vendor`, `renderer`, `version`, `shadingLanguageVersion`,
`maxTextureSize`, `swapInterval`, window and framebuffer sizes,
`contentScaleX`/`contentScaleY` (`glfwGetWindowContentScale`, `0` when
unavailable), `glfwPlatform`, the `GraphicsCapabilities.Classify` result
(`software`, `family`, `summary`) and whether `DOTTY_GL_DEBUG` is active
(`glDebug`, `khrDebugCallback`). `DOTTY_GL_INFO=1` prints the same JSON to
stderr at startup, prefixed with `DOTTY_GL_INFO `.

The `MEMORY` command returns the host's memory counters as JSON, including
`terminalArenaCapacityBytes` (reserved virtual), `terminalArenaCommittedBoundBytes`
(upper bound of committed pages, not resident size) and `terminalArenaBackend`.
It also reports the Kitty graphics ownership counters `imagePixelBytes`, `imageCount`, `placementCount`, `pendingTransferBytes`, and the estimate `imageTextureBytesEstimate` (see [Kitty graphics](KittyGraphics.md#memory-model)).
`scripts/perf/scrollback_memory.py` is the harness built on it: it launches the
host under `xvfb-run`, opens tabs (`ACTION:NewTab`), fills history and issues
`RESIZE:`, ED 2 and CSI 3 J through `TYPE:`/`KEY:`, and samples process PSS from
`/proc/<pid>/smaps_rollup` after each stage (Linux only). It has not been run in
CI; see [Performance](Performance.md#scrollback-memory-measurement).

## Linux smoke

Linux GUI smoke requires an X11 display server. Xvfb is the CI setup:

```bash
make -C src/Dotty.NativePty
dotnet build src/Dotty/Dotty.csproj -c Release --nologo
mkdir -p /tmp/dotty-empty-home
HOME=/tmp/dotty-empty-home timeout 5s \
  xvfb-run -a dotnet run --project src/Dotty/Dotty.csproj --no-build
```

Exit status `124` is expected because the host remains running. Any earlier
exit, native loader error, PTY startup error, or unhandled exception fails the
smoke test. CI runs this X11 smoke in `gui-smoke-linux`; Wayland/Weston,
macOS desktop, and Windows desktop smoke are not run in CI. X11/Xvfb startup
does not prove Wayland, macOS, or Windows GUI behavior.

## macOS smoke (manual/local)

Run this workflow on a local native macOS session for Intel or Apple Silicon;
hosted CI runners build and test the native code but do not provide a usable
desktop OpenGL session:

```bash
make -C src/Dotty.NativePty
dotnet build Dotty.slnx -c Release --nologo
dotnet test --solution Dotty.slnx -c Release --nologo
DOTTY_TEST_PORT=19000 dotnet run --project src/Dotty/Dotty.csproj
```

Exercise shell startup, UTF-8 text, resize, clipboard, focus changes, and clean
window shutdown. Retina scale changes must be included in GUI runs.

## Windows smoke (manual/local)

Run this workflow on Windows 10 build 17763+ or Windows 11. Hosted CI runs the
ConPTY tests and validates published assets, but does not run desktop GUI smoke:

```powershell
dotnet build Dotty.slnx -c Release --nologo
dotnet test --project tests\Dotty.NativePty.Tests\Dotty.NativePty.Tests.csproj `
  -c Release --filter "FullyQualifiedName~WindowsPtyTests"
$env:DOTTY_TEST_PORT = "19000"
dotnet run --project src\Dotty\Dotty.csproj -c Release
```

Exercise `cmd.exe`, Windows PowerShell, and `pwsh.exe`; verify ConPTY input,
output, resize, focus, clipboard, process exit, and shutdown.

## Manual smoke: OSC 52 read, paste confirm, and link confirm

**These checks are manual. No CI job exercises them**: the Xvfb smoke only proves
startup, and the loopback control interface cannot press Ctrl+click or answer a
prompt. Run them in a real Dotty window on each platform you ship (Linux X11 and
Wayland, macOS, Windows), using a throw-away configuration directory so your own
settings are untouched:

```sh
export DOTTY_CONFIG_HOME=/tmp/dotty-manual-smoke
mkdir -p "$DOTTY_CONFIG_HOME"
# edit $DOTTY_CONFIG_HOME/config.json between runs; reload is live
dotnet run --project src/Dotty/Dotty.csproj -c Release
```

### OSC 52 clipboard read

Put the text `hello` on the system clipboard from another application, then in
Dotty (Bash) request it and show whatever the terminal sends back:

```sh
printf '\033]52;c;?\033\\'; IFS= read -rs -t 5 -d '\\' reply; printf '%s\n' "$reply" | cat -v
```

| `clipboard.osc52Read` | Expected |
|---|---|
| `"deny"` (or unset, or any unknown value) | No prompt, no reply; `read` times out after 5 s and prints an empty line. |
| `"ask"` | A centered prompt `Tab "..." wants to read the clipboard` shows the preview `hello` and `(5 characters)` with **Deny** focused. Enter or Escape or a click outside: no reply. **Allow**: `^[]52;c;aGVsbG8=^[` is printed. |
| `"allow"` | `^[]52;c;aGVsbG8=^[` is printed immediately with no prompt. |

Then, under `"ask"`, also verify:

1. `printf '\033]52;p;?\033\\'` never prompts or replies on Wayland, macOS, and Windows (Primary reads are served on X11 only; on X11 it prompts exactly like a clipboard read).
2. With `"osc52ReadLimitBytes": 4`, the `hello` request is dropped silently.
3. Issue the request, switch to another tab or focus another window before
   answering: no prompt appears until the requesting tab is active and Dotty is
   focused; a request left unanswered for 30 s is discarded.
4. A clipboard containing control characters (set `"allowOsc52Write": true`, then run
   `printf '\033]52;c;%s\033\\' "$(printf 'a\033[31mb' | base64)"`) shows visible `\x1B`
   escapes in the preview, never live escape sequences.
5. A Lua hook `dotty.on("clipboard_read", function() return false end)` suppresses the
   prompt and the reply in both `ask` and `allow`.

### Paste confirmation

Prepare clipboard text `echo one` newline `echo two` from another application. In
a plain shell run `stty -icanon -icrnl -echo; cat -v` (leave with Ctrl+C, then
`stty sane`) and press Ctrl+Shift+V (repeat with middle-click and the context-menu Paste):

1. Default config: a prompt `Paste 2 lines, N characters?` shows a preview and the
   row `Multiple lines: the shell would run them immediately.` with **Cancel**
   focused. Cancel, Escape, or a click outside pastes nothing. **Paste** delivers
   the text with each line break as `^M`.
2. Run `printf '\033[?2004h'` first: the same multi-line paste now goes through
   without a prompt, wrapped as `^[[200~`...`^[[201~` with the line breaks kept.
   With `"bracketedPasteIsSafe": false` it prompts again.
3. Set `"allowOsc52Write": true` and put text with an escape character on the clipboard:
   `printf '\033]52;c;%s\033\\' "$(printf 'a\033[201~b' | base64)"`. Pasting it, even in
   bracketed mode, prompts, lists the control-character and paste-end-marker reasons,
   and after **Paste** the escape arrives as a space.
4. With `"confirmPaste": false` no prompt appears, but the text is still sanitized.
5. Put multi-line text containing an escape character on the clipboard as in step 3
   (add a `\n`), keep bracketed paste on, and run `(sleep 10; printf '\033[?2004l') &`
   before pasting. The prompt lists only the control-character reasons. Wait for the
   `sleep` to finish, then choose **Paste**: nothing is pasted, because ending bracketed
   mode exposed a newline risk the prompt had not shown.

### Link confirmation

Print one link of each kind, then Ctrl+click each. Plain left-click must do nothing,
and with mouse reporting enabled the click goes to the application unless Shift is held.

```sh
printf '\033]8;;https://example.com\033\\plain web link\033]8;;\033\\\n'
printf '\033]8;;https://evil.example\033\\https://paypal.com\033]8;;\033\\\n'
printf '\033]8;;file:///etc/hostname\033\\local file\033]8;;\033\\\n'
printf '\033]8;;ssh://host.example\033\\ssh link\033]8;;\033\\\n'
printf '\033]8;;javascript:alert(1)\033\\blocked\033]8;;\033\\\n'
```

| Link | Expected with the default `confirm: "risky"` |
|---|---|
| plain web link | Opens in the default browser with no prompt. Ctrl+click works for OSC 8 links (and for plain `https://...` text). |
| `https://paypal.com` pointing at `evil.example` | Prompt `Open link?` with `Link text differs from the target:`, the text, and the canonical `https://evil.example/`. **Cancel** focused. |
| local file | Prompt `Opens a local file`. Executables and files with an execute bit are refused (a notice, not a prompt). |
| ssh link | Prompt `Non-web link (ssh)`. |
| `javascript:` | A `Link blocked` notice with a **Dismiss** row; nothing is launched. |

Also verify: **Copy link address** places the canonical URL on the clipboard;
Escape and click-outside never open anything; `"confirm": "always"` prompts even for the
plain link and `"never"` opens everything that is allowed (the `javascript:` notice still
appears); removing `ssh` from `allowedSchemes` turns the ssh link into a blocked notice;
`"osc8": false` makes OSC 8 text neither hoverable nor clickable (new output after the
reload), while a typed `https://example.com` still is; a Lua
`dotty.on("open_url", function(url, source) dotty.log(source, url) return true end)` logs
`explicit` or `detected` and suppresses the prompt and the open.

Opening itself goes through `xdg-open` (Linux), `open` (macOS), and ShellExecute
(Windows); see [Platform Support](PlatformSupport.md#manual-link-opener-checks) for the
per-platform checks.

## Manual smoke: X11 primary selection, copy-on-select, and middle-click

**Manual verification only; nothing in CI draws a window, selects text, or reads the X11 PRIMARY selection.** The unit tests (`MouseOptionsTests`, `TerminalMouseControllerTests`, `ClipboardReadServiceTests`) cover the policy and host seams. Use an isolated configuration, a native X11 session (Xvfb with a pointer-injection tool also works), and any independent PRIMARY reader such as `xclip -o -selection primary`:

```bash
mkdir -p /tmp/dotty-primary
cat > /tmp/dotty-primary/config.json <<'EOF'
{ "mouse": { "copyOnSelect": "primary", "middleClickAction": "primary-paste" },
  "clipboard": { "allowOsc52Write": true, "osc52Read": "allow" } }
EOF
DOTTY_CONFIG_HOME=/tmp/dotty-primary dotnet run --project src/Dotty/Dotty.csproj -c Release
```

(`osc52Read: allow` is for this throwaway profile only.) Then:

| Check | Expected |
|---|---|
| Print some text and drag-select it | `xclip -o -selection primary` in another terminal prints it; the selection stays highlighted; the clipboard (Ctrl+Shift+V) is unchanged |
| Set `copyOnSelect` to `both`, reselect | PRIMARY and the clipboard both hold the text |
| Set `copyOnSelect` to `clipboard`, reselect | only the clipboard changes |
| Double-click a word, triple-click a line | the word/line is copied the same way; `none` copies nothing |
| Middle-click in a second pane or tab with `primary-paste` | the PRIMARY text is pasted (a multi-line paste still asks to confirm, per `clipboard.confirmPaste`) |
| `clipboard-paste` / `ignore` | middle-click pastes the clipboard / does nothing |
| Enable mouse reporting (`printf '\e[?1000h\e[?1006h'; cat`), middle-click | the app receives the report; Shift+middle-click pastes locally |
| `printf '\e]52;p;%s\a' "$(printf hello \| base64)"` | `xclip -o -selection primary` prints `hello` |
| `printf '\e]52;p;?\a'` | the reply `ESC ] 52 ; p ; <base64> ESC \` arrives (view it with `cat -v`) |

On Wayland, macOS, and Windows the PRIMARY selection is an in-process buffer: repeat the copy-on-select and middle-click rows and confirm that pasting between panes works but no other application sees the text, that `printf '\e]52;p;...'` does not change the clipboard, and that a primary-only OSC 52 read gets no reply. These per-platform results are manual evidence only.

## Manual smoke: shell-integration injection

**Manual verification only.** `ShellIntegrationLauncherTests` check the argv/environment rewrite and `ShellIntegrationInjectionTests` run the bash wrapper; no automated test launches zsh, fish, or PowerShell through Dotty. With a throwaway profile (`DOTTY_CONFIG_HOME=/tmp/dotty-inject`, `config.json` containing `{ "shellIntegration": "detect" }`) start Dotty once per shell and confirm in each:

| Shell | Start with | Check |
|---|---|---|
| bash | `dotty --shell /usr/bin/bash` | the idempotence guard from [Shell Integration](ShellIntegration.md#what-each-script-guarantees) is set; `echo $DOTTY_SHELL_INTEGRATION $DOTTY_SHELL_INTEGRATION_DIR` prints `1` and the directory; your `~/.bashrc` still ran (an alias or prompt of yours is present) |
| zsh | `dotty --shell /usr/bin/zsh` (also once with `ZDOTDIR` pointing at a directory of your own) | the guard is set after the first prompt; `echo $ZDOTDIR` is your value or empty, never Dotty's `inject/zsh`; your own `.zshenv`/`.zshrc` ran |
| fish | `dotty --shell /usr/bin/fish` | the guard is set; `echo $XDG_DATA_DIRS` does not contain `inject/xdg` |
| PowerShell 7 (Windows and Linux) | `dotty --shell pwsh` | `$global:DottyShellIntegrationInstalled` is true; no error text appears at startup |

In every shell, `cd /tmp`, then open a new tab with Ctrl+Shift+T: it starts in `/tmp` (OSC 7), and Ctrl+Shift+PageUp jumps to the previous prompt (OSC 133). Negative checks: `DOTTY_SHELL_INTEGRATION=0 dotty --shell /usr/bin/bash` leaves the guard unset; `dotty -- bash` and `dotty --shell /bin/sh` are not injected; `DOTTY_SHELL_INTEGRATION_DIR=/nonexistent dotty ...` injects nothing and the shell starts normally; with the config set to `none`, nothing is injected. For a published archive repeat the bash and zsh rows from the extracted directory, which proves that the dotfile `inject/zsh/.zshenv` survived packaging. If your system `/etc/zshenv` sets `ZDOTDIR` (for example under Nix), zsh injection is expected to do nothing; see [Shell Integration](ShellIntegration.md#automatic-injection).

## Manual smoke: `+explain-config`

The action is unit-tested (`ConfigCatalogTests`, `DottyLaunchOptionsTests`); running the built binary is a quick manual check of the early-exit contract:

```bash
dotty +explain-config | head            # exit 0: one line per option
dotty +explain-config mouse.copyOnSelect # type, default, values, scope, description
dotty +explain-config mouse             # the group
dotty +explain-config nosuch; echo $?   # message and suggestions on stderr, exit 1
dotty +explain-config a b; echo $?      # usage error, exit 2
DOTTY_CONFIG_HOME=/tmp/never-created env -u DISPLAY -u WAYLAND_DISPLAY dotty +explain-config vt
ls /tmp/never-created                   # must not exist: no config was loaded or created
```

## Manual smoke: window operations and visibility reports

**Manual verification only**: the adapter side is covered by `WindowOpsTests` and `VisibilityReportTests` and the host policy by `VtHostPolicyTests`, but nothing in CI resizes or minimizes a real window. Use a single-tab, single-pane, non-maximized window. Replies are visible with `cat -v` after the query.

| Check | Expected |
|---|---|
| `printf '\e[18t\e[14t\e[16t\e[13t\e[15t\e[19t'; cat -v` | `^[[8;rows;colst`, `^[[4;h;wt`, `^[[6;h;wt`, `^[[3;x;yt`, `^[[5;h;wt`, `^[[9;rows;colst`; `20t`/`21t` print nothing; on a platform where the position cannot be read, `13t` reports `0;0` |
| `printf '\e[8;30;100t'` with `vt.windowResizeAllowed` at its default | nothing happens |
| the same with `"vt": { "windowResizeAllowed": true }` | the window becomes 100 x 30 cells (give it a frame, then `printf '\e[18t'; cat -v`); the window manager may still constrain the size, which is its decision |
| the same with two tabs open, a split pane, or a maximized/fullscreen window | ignored |
| `printf '\e[?2033h'; cat -v` | immediately `^[[?999;1n`; switch to another tab and back: the first tab then shows `^[[?999;2n` followed by `^[[?999;1n` (the report for the hidden state is already in its output when you return). Minimizing and restoring should behave the same where GLFW reports iconification; Dotty cannot observe minimize on Wayland (see the code comment in `NativeGlfwMetadata.IsWindowIconified`), so only tab switches report there |
| `printf '\e[?998n'; cat -v` | one `^[[?999;1n` while visible, with mode 2033 untouched |

Whether a Windows ConPTY session delivers these queries and replies unchanged to the application is **unverified**: ConPTY sits between the application and Dotty, and nothing in this repository tests window or visibility queries through it.

## Artifact smoke

Release verification inspects every published archive and automatically
extracts and starts the Linux archive under Xvfb. Other platform archives are
run from clean directories on their native systems during manual/local
verification. Unix artifacts must contain an executable `pty-helper` beside the
host binary; Windows artifacts must contain the host binary and require no Unix
helper.

Minimum artifact checks:

1. Start the host.
2. Create a session and run a shell command.
3. Verify UTF-8 and VT output.
4. Resize the terminal.
5. Verify focus and paste paths.
6. Shut down without orphan processes.

## Failure handling

Smoke scripts must propagate build, test, and host exit codes. They must not
silently convert failures into success. Capture host logs and the extracted
artifact manifest for every failed matrix job.
