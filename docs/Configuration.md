# Dotty Configuration

Dotty uses a JSON configuration file at runtime. It loads the last valid
configuration at startup, watches the file for editor write/rename patterns,
and applies accepted changes on the desktop UI thread.

## File location

The configuration directory is selected by the host platform:

- Linux: `$XDG_CONFIG_HOME/dotty`, or `~/.config/dotty`
- macOS: `~/Library/Application Support/Dotty`
- Windows: `%APPDATA%/Dotty`

The file is `config.json`. Set `DOTTY_CONFIG_HOME` to override the complete
configuration directory. This override is useful for tests, portable installs,
and parallel development sessions.

Dotty creates the directory and a default `config.json` when the file is
missing. Writes are atomic: a temporary file is written and moved over the
previous file. A malformed replacement leaves the last valid in-memory config
active and records the parse error in `UserConfigService.LastError`.

## Session launch context

New tabs and split panes inherit the selected interactive shell and the active
terminal's working directory when available. A command and its argument vector
passed directly to Dotty apply only to the initial session; they are not replayed
when creating later tabs or panes.

Every shell or command Dotty starts receives these environment variables, set
before the caller-supplied environment (so an explicit entry for the same name
wins):

| Variable | Value |
|---|---|
| `TERM_PROGRAM` | `dotty` |
| `TERM_PROGRAM_VERSION` | The release version without SemVer build metadata (`0.3.0+sha` becomes `0.3.0`; prerelease labels are kept). |
| `DOTTY_SURFACE_ID` | A stable per-session identifier (a GUID in `N` format) that is also the owning pane's `Id`. |

The Unix `pty-helper` protocol variables `DOTTY_CONTROL_SOCKET`,
`DOTTY_INITIAL_COLS`, and `DOTTY_INITIAL_ROWS` are consumed by the helper and
removed from the environment before the shell or command is executed, so they
never reach user shells. Shell-integration scripts skip silently in
non-interactive shells; see [Shell Integration](ShellIntegration.md).

When a tab or pane is created from an existing one, the starting directory is
resolved at that moment: the shell's OSC 7 directory if it still exists,
otherwise the session's launch directory if it still exists, otherwise the
default launch directory. A reported directory that has since been deleted or
become unreadable therefore no longer prevents a new tab from opening.

## Example

```json
{
  "font": {
    "family": "JetBrains Mono, Cascadia Code, Liberation Mono, monospace",
    "size": 14,
    "lineHeight": 1.25,
    "symbolMap": ["U+2500-U+257F: Symbols Nerd Font Mono, Symbols Nerd Font"]
  },
  "window": {
    "padding": { "left": 14, "top": 8, "right": 14, "bottom": 8 },
    "decorations": "custom",
    "opacity": 1,
    "title": "Dotty"
  },
  "tabBar": {
    "show": true,
    "height": 38,
    "style": "Pill"
  },
  "cursor": {
    "shape": "Block",
    "blink": true,
    "blinkIntervalMs": 500
  },
  "theme": "DarkPlus",
  "selectionColor": "#264F78",
  "panes": {
    "dividerThickness": 2,
    "activeBorder": true
  },
  "clipboard": {
    "allowOsc52Write": false,
    "osc52Read": "deny",
    "osc52ReadLimitBytes": 1048576,
    "confirmPaste": true,
    "bracketedPasteIsSafe": true
  },
  "hyperlinks": {
    "osc8": true,
    "confirm": "risky",
    "allowedSchemes": ["http", "https", "mailto", "file", "ssh", "git"]
  },
  "mouse": {
    "copyOnSelect": "none",
    "middleClickAction": "clipboard-paste"
  },
  "vt": {
    "windowResizeAllowed": false,
    "checksumReportEnabled": false
  },
  "shellIntegration": "none",
  "keybindings": {
    "ctrl+shift+t": "NewTab",
    "ctrl+shift+w": "ClosePane",
    "ctrl+shift+c": "Copy",
    "ctrl+shift+v": "Paste",
    "ctrl+shift+f": "Search"
  }
}
```

Unknown JSON properties are ignored. Property names are case-insensitive and
trailing commas/comments are accepted by the JSON parser.

## Options

### `font`

| Property | Type | Default | Notes |
|---|---|---|---|
| `family` | string | platform-neutral fallback stack | Comma-separated family names; first installed family wins. |
| `size` | number | `14` | Font size in points; non-finite or non-positive values are normalized. |
| `lineHeight` | number | `1.25` | Line-height multiplier; values below `0.1` are clamped. |
| `symbolMap` | string[] | `[]` | Per-range font overrides; use `U+2500-U+257F: Preferred Font, Fallback Font`. For each range, the first installed family that contains the glyph wins; overlapping ranges use the first matching entry. |
OpenType features and programming ligatures are not supported. The renderer draws
one grapheme per terminal cell rather than shaping ligatures across cells.
The renderer measures the selected typeface at the current framebuffer scale.
Cell width and height remain finite and positive even when a platform font
reports incomplete metrics.

### `theme`, `themeLight`, `themeDark`, `themeAuto`

| Property | Type | Default | Notes |
|---|---|---|---|
| `theme` | string | `"DarkPlus"` | Explicit theme; also the dark fallback when `themeAuto` is true. `DOTTY_THEME` env overrides everything. |
| `themeLight` | string | `"LightPlus"` | Theme used when `themeAuto` is true and the OS reports light. |
| `themeDark` | string | theme value | Theme used when `themeAuto` is true and the OS reports dark. |
| `themeAuto` | boolean | `false` | Follow the OS light/dark color scheme. Linux: xdg-desktop-portal `org.freedesktop.appearance color-scheme`, then `gsettings org.gnome.desktop.interface color-scheme`, then the GTK theme name; macOS: `AppleInterfaceStyle`; Windows: `AppsUseLightTheme`. Detection runs on a background thread (checked every 5 s), never on the render loop. When the scheme cannot be determined, `themeDark` (or `theme`) is used. |

### `window`

| Property | Type | Default | Notes |
|---|---|---|---|
| `padding.left` / `top` / `right` / `bottom` | number | `14`, `8`, `14`, `8` | Terminal content padding; tab-bar backgrounds remain flush with the client edges. |
| `opacity` | number | `1` | |
| `title` | string | `Dotty` | |
| `decorations` | string | `custom` | Windows only: `custom` removes the native title bar and draws the tab-bar caption controls; `native` keeps native decorations. Other platforms retain native decorations regardless of this option. The custom caption strip remains visible when `tabBar.show` is false. |

### `tabBar`

| Property | Type | Default |
|---|---|---|
| `show` | boolean | `true` |
| `height` | number | `38` |
| `style` | string | `Pill` |

Tab surfaces fill the strip height, with labels and close controls inset inside.
The new-tab button sits flush against the last visible tab, with the same strip
height and square edges. Status text and custom Windows caption controls stay
right-aligned. The desktop renderer currently uses one
chrome style; the stored `style` value does not select a different appearance.

### `cursor`

| Property | Type | Default |
|---|---|---|
| `shape` | string | `Block` |
| `blink` | boolean | `true` |
| `blinkIntervalMs` | integer | `500` |

Supported shapes are `Block`, `Beam`, and `Underline`.

### `theme`, `selectionColor`, and `panes`

`theme` names a built-in or user theme. `selectionColor` accepts the color
syntax understood by the active theme loader. `panes.dividerThickness` controls
the split-pane divider and `panes.activeBorder` controls the active-pane border.

User theme JSON files are loaded from `<config-directory>/themes`. See
[Themes](Themes.md) for the schema and validation rules.

### `clipboard`

| Property | Type | Default | Notes |
|---|---|---|---|
| `allowOsc52Write` | boolean | `false` | Permit terminal applications to write to the system clipboard using OSC 52. A Lua `clipboard_write` hook can veto a write but cannot grant permission when this setting is false. |
| `osc52Read` | string | `"deny"` | Policy for OSC 52 clipboard **read** (`?`) requests: `deny`, `ask`, or `allow` (case-insensitive). Any other value is treated as `deny`. |
| `osc52ReadLimitBytes` | integer | `1048576` | Largest clipboard (UTF-8 bytes) an OSC 52 read may return; a larger clipboard is dropped without a reply. Clamped to 1 through 8,388,608. |
| `confirmPaste` | boolean | `true` | Ask before pasting text that could execute or inject input (see Paste below). `false` pastes without prompting. |
| `bracketedPasteIsSafe` | boolean | `true` | When the target application has enabled bracketed paste (mode 2004), multi-line text alone does not need confirmation. Control characters and paste-end markers still do. |

All five are read live, so a reload takes effect for the next request or paste.

**OSC 52 write.** With `allowOsc52Write: true`, an application can set the
clipboard with `ESC ] 52 ; c ; <base64> ST`. Decoded text over 786,432 bytes
of UTF-8 is dropped. A write that names the Primary selection (`p`/`s`) reaches
the system PRIMARY selection **on X11 only** (see [`mouse`](#mouse)); elsewhere
the Primary part is dropped, and a primary-only write does nothing (it never
falls back to the clipboard). Lua scripts may register a
`clipboard_write` hook to veto a write by returning `false`: with global
permission enabled, no hook or a non-vetoing hook allows the write; an explicit
`false` or a hook invocation error denies it. Hook errors are still reported
through the normal Lua diagnostics. The hook cannot override
`allowOsc52Write: false`.

**OSC 52 read.** `ESC ] 52 ; c ; ? ST` asks Dotty to reply with the clipboard
contents. The reply is `ESC ] 52 ; c ; <base64> ESC \`.

- `deny` (default): the request is ignored and nothing is sent back.
- `ask`: Dotty shows a centered prompt that names the requesting tab and previews
  the clipboard text (control characters shown as visible escapes). **Deny** is
  focused, so pressing Enter discloses nothing; **Allow** sends exactly the
  previewed text. Escape, clicking outside, or another menu opening denies. The
  prompt can stay open indefinitely, so **Allow** re-checks at click time: if the
  policy was changed to `deny` or the session has left the window, nothing is sent.
- `allow`: the request is answered immediately and silently.

> **Warning:** `allow` lets any program that can write to the terminal, including
> anything you `cat`, run over SSH, or paste into a remote shell, read your
> clipboard, which often holds passwords, tokens, or other secrets, without any
> prompt. Leave it at `deny`, or use `ask`, unless every program that can print
> to your terminal is trusted.

Denial never produces a reply: none is sent for the `deny` policy, a Lua
`clipboard_read` hook returning `false`, the user choosing Deny, an oversize
clipboard, an unavailable clipboard, or expiry. Only a genuinely empty clipboard
is answered (with an empty payload). Requests are queued (1 per session, 4 in
total, expiring after 30 seconds), and are served only while Dotty's window is
focused and the requesting tab is the active tab; under `ask` they also wait
while another menu or prompt is open. A read that names the Clipboard is served
from the clipboard (also when Primary is named too). A read that names only the
Primary selection is served from the system PRIMARY selection on X11 and dropped
without a reply elsewhere. A `clipboard_read` hook may veto a read
but cannot grant one under `deny`. See the [Lua guide](guides/Lua.md#events-and-value-hooks).

**Paste.** Ctrl+Shift+V, middle-click, and the context-menu Paste all go through
one path. Before delivery, pasted text is sanitized: C0 controls other than TAB,
LF, and CR, DEL, and C1 controls are replaced with spaces, so a paste can neither
inject escape sequences nor end a bracketed paste early (an embedded `ESC [ 201 ~`
is neutralized). When the application has not enabled bracketed paste, LF and
CRLF are converted to CR; when it has, the text is wrapped in
`ESC [ 200 ~` ... `ESC [ 201 ~` with line breaks unchanged.

With `confirmPaste` on, a prompt is shown (title with line and character counts,
a sanitized preview of at most six lines, one row per reason, **Paste** and
**Cancel** with Cancel focused) when the text contains line breaks, contains
controls that will be replaced, or contains a paste-end marker. The preview shows
exactly the text that will be pasted. If bracketed paste is active and
`bracketedPasteIsSafe` is true, line breaks alone do not prompt. If the
application toggles bracketed paste while the prompt is open and that exposes a
risk the prompt did not show, confirming pastes nothing. Escape or clicking
outside the prompt also pastes nothing.

### `hyperlinks`

| Property | Type | Default | Notes |
|---|---|---|---|
| `osc8` | boolean | `true` | Honor OSC 8 explicit hyperlinks. When `false`, OSC 8 sequences are ignored at ingest (nothing is stored, active links close) and no explicit link is hoverable or clickable. Detected plain URLs are unaffected. |
| `confirm` | string | `"risky"` | `risky` prompts only for links that need scrutiny, `always` prompts for every allowed link, `never` opens allowed links without a prompt. Unknown values mean `risky`. Hard blocks apply in every mode. |
| `allowedSchemes` | string[] | `["http", "https", "mailto", "file", "ssh", "git"]` | Schemes that may be opened (case-insensitive). Hard-blocked schemes cannot be enabled by listing them. |

Links open with Ctrl+left-click, for both OSC 8 links and plain `http`, `https`,
and `git@host:path` text, resolved from a fresh buffer snapshot at click time.
Activation runs in this order, and the first stage that refuses stops it:

1. **Structural validation and canonicalization.** The link must be a
   non-empty absolute URL of at most 8192 characters with no control, invisible,
   or bidirectional-text characters. It is rewritten to an ASCII, percent-escaped
   canonical form (internationalized hosts as punycode, characters that could
   break a Windows command line escaped). The canonical form is the only text
   shown in prompts or handed to the system opener. `git@host:owner/repo.git` is
   treated as `https://host/owner/repo`.
2. **Hard blocks (not configurable):** `javascript`, `vbscript`, `data`, `blob`,
   `about`, `jar`, `shell`, `ms-msdt`, `search-ms`, `search`, `ms-search`,
   `ms-officecmd`, `ms-appinstaller`, `ms-excel`, `ms-word`, `ms-powerpoint`,
   `ms-cxh`, `mhtml`, `ms-its`, `mk`, `res`, and `ie`.
3. **Lua `open_url` hook.** It receives the canonical URL and the source
   (`"explicit"` or `"detected"`); returning `true` means handled and Dotty does
   nothing more, so a handling hook bypasses `allowedSchemes` and the prompts but
   never sees an invalid or hard-blocked link. A hook error never bypasses the
   built-in pipeline.
4. **Scheme allowlist and risk classification.** A scheme not in
   `allowedSchemes` is blocked. `http`/`https` need a host. `mailto` links with an
   `attach`/`attachment` query are blocked. With `confirm: risky` a prompt
   appears when the link is a non-web scheme, a local `file:` target, a web URL
   with embedded credentials, an internationalized host, or an OSC 8 web link
   whose visible text names a different web host than the target. With
   `confirm: always` every allowed link prompts, listing whatever risks it has;
   `never` suppresses the prompts.
5. **`file:` rules.** The host must be empty, `localhost`, or this machine. The
   path may not contain control characters or `..` segments. On Windows, UNC and
   backslash paths and alternate data streams (a `:` other than the drive colon)
   are rejected. Files whose name carries any executable or auto-running
   extension (for example `exe`, `bat`, `cmd`, `ps1`, `msi`, `lnk`, `js`, `jar`,
   `app`, `pkg`, `dmg`, `desktop`, `appimage`; the check covers every dot-separated
   segment, so `a.exe.txt` is blocked) are refused. On Linux and macOS the opener
   also refuses a file (or symlink target) with any execute permission bit.

**Prompts.** A confirmation prompt (**Open**, **Copy link address**, **Cancel**,
Cancel focused) lists each risk, the visible link text for a mismatch, and the
canonical target wrapped over the remaining rows. A blocked link and a failed
launch show an informational notice with a **Dismiss** row instead. Prompts
appear at the pointer and are limited to 60 columns and 8 body lines; Escape or a
click outside closes them without acting. Linux and macOS launch `xdg-open` /
`open` with the canonical URI as a single argument and no shell; Windows hands the
canonical URI, or the validated local path for `file:` links, to the shell.

Prompts are drawn by Dotty itself, never by terminal output, and every row of
untrusted text is sanitized (controls become visible escapes, bidirectional
marks become `\u{XXXX}`). Native opening has no CI coverage; verify it manually on
each platform ([Platform Support](PlatformSupport.md#manual-link-opener-checks)).

### `mouse`

| Property | Type | Default | Notes |
|---|---|---|---|
| `copyOnSelect` | string | `"none"` | Where a finished local selection is copied automatically: `none`, `primary`, `clipboard`, or `both` (case-insensitive). The Ghostty-style boolean spellings are accepted **as strings**: `"true"` (or `"on"`) means `primary` on Linux and `clipboard` on macOS/Windows, `"false"` (or `"off"`) means `none`. Unknown values mean `none`. The property is typed as a string, so an unquoted JSON `true`/`false` is a type mismatch rather than a legacy spelling; quote it. |
| `middleClickAction` | string | `"clipboard-paste"` | What a middle click does in a pane that is not reporting mouse events: `primary-paste`, `clipboard-paste`, or `ignore` (`-` and `_` are interchangeable). Unknown values mean `clipboard-paste`. |

Copy-on-select runs when the left button is released after a drag, or after a
double- or triple-click, completed a local selection. It never clears the
selection and is independent of the Copy action and of middle-click.

The PRIMARY selection is the operating system's only when GLFW is running its
**X11** backend (it uses GLFW's X11-only selection exports). On every other
platform or backend (Wayland, macOS, Windows) it is a buffer inside the Dotty
process: `primary` copies and `primary-paste` work between panes of the same
Dotty process, but other applications never see them. Middle-click pastes go
through the normal paste path, so sanitizing and `clipboard.confirmPaste` apply
to primary pastes too. Nothing but copy-on-select writes the buffer, so with
`copyOnSelect` set to `none` or `clipboard`, `primary-paste` pastes whatever
another X11 application left in PRIMARY, or nothing elsewhere. OSC 52 `p`/`s`
reads and writes use the real PRIMARY selection on X11 only (see
[`clipboard`](#clipboard)); the in-process buffer is never exposed to OSC 52.

Both options are read live: a change applies to the next selection or click.
Interoperation of X11 PRIMARY with other applications is a manual check; see
[E2E Testing](E2ETesting.md#manual-smoke-x11-primary-selection-copy-on-select-and-middle-click).

### `vt`

Opt-in VT behaviors that expose host or screen state to the program running in
the terminal. Both default to `false`.

| Property | Type | Default | Notes |
|---|---|---|---|
| `windowResizeAllowed` | boolean | `false` | Let applications resize the window with `CSI 8 ; rows ; cols t` (and `CSI Ps t` with `Ps` >= 24). Ignored even when enabled if there are several tabs, the active tab has split panes, or the window is maximized, fullscreen, or minimized. Read live, so a reload applies to the next request. |
| `checksumReportEnabled` | boolean | `false` | Answer DECRQCRA (`CSI Pi ; Pg ; Pt ; Pl ; Pb ; Pr * y`) rectangular-area checksum requests. When `false` no reply is sent at all. Applied to a session when it is created and on config reload. |

Why they are off: a window resize changes your desktop layout on the say-so of
whatever prints to the terminal, and a checksum reply lets a program read the
screen contents back without any clipboard or prompt. Enable them only for a
trusted workload, such as a conformance run. The window-report queries
(`CSI 13/14/15/16/18/19 t`) only read geometry and are always answered; title
reports (`CSI 20/21 t`) are never answered. See
[Parsing](Parsing.md#window-operations-csi-ps--ps--ps-t) and
[DECRQCRA](Parsing.md#decrqcra-rectangular-checksum).

### `scrollback`

How much history each terminal session keeps.

```json
{
  "scrollback": { "limitLines": 5000, "limitBytes": 67108864 }
}
```

| Property | Type | Default | Notes |
|---|---|---|---|
| `limitLines` | integer | `5000` | Maximum history lines per session. `0` keeps no history. Values above `1000000` are clamped to `1000000`; negative values fall back to the default. |
| `limitBytes` | integer | `67108864` (64 MiB) | Maximum raw history ring bytes per session, counted as `(lines + rows) * columns * 12`. `0` means no byte cap. Non-zero values below `1048576` (1 MiB) and negative values fall back to the default. |

The first limit reached applies. The byte limit is re-evaluated on every resize,
so a session that is made wider keeps fewer history lines (down to `0` when
`(rows + 1) * columns * 12` already exceeds `limitBytes`) and a narrower one
keeps up to `limitLines` again; rows beyond the new capacity are discarded
oldest first. The byte figure counts the raw ring (12 bytes per cell: an 8-byte
hot and a 4-byte cold half), not resident memory: cell storage is reserved
address space that is committed lazily as rows are written (see
[Rendering](Rendering.md#memory-diagnostics-and-lifecycle-measurements)). There
is no history compression option.

Both properties apply to sessions created after the change; existing tabs keep
their capacity policy. In Lua they are `scrollback.limit_lines` and
`scrollback.limit_bytes` (see the [Lua guide](guides/Lua.md)).

### `shellIntegration`

| Property | Type | Default | Notes |
|---|---|---|---|
| `shellIntegration` | string | `"none"` | `none` never injects; `detect` injects Dotty's shell-integration scripts into recognized interactive shells that Dotty starts. Any other value is treated as `none`. |

With `detect`, Dotty starts bash, zsh, fish, and PowerShell (`pwsh` or
`powershell`) with the OSC 7 / OSC 133 scripts already loaded, without editing
any dotfile; an explicit command (`dotty -- CMD`) is never
rewritten. Two environment variables of the Dotty process refine it:
`DOTTY_SHELL_INTEGRATION=0` (also `none`, `false`, or `off`) opts out whatever the
config says, and `DOTTY_SHELL_INTEGRATION_DIR` points at a different integration
directory (it must contain `dotty.bash`). The mechanism per shell, and what is
not injected, are in [Shell Integration](ShellIntegration.md#automatic-injection).
The setting is read each time a session starts, so a reload applies to **new tabs
and panes only**; running shells are unchanged.

### `keybindings`

Keys are normalized chords containing `ctrl`, `shift`, `alt`, or `super` plus a
key name. Values are `TerminalAction` names. The following table is the exact
startup set; an em dash means that the action has no built-in chord.

| Action | Built-in chord(s) | Behavior |
|---|---|---|
| `None` | — | Special unbind value; it is not dispatched. |
| `NewTab` | `ctrl+shift+t` | Create a new tab. |
| `CloseTab` | — | Close the current tab. |
| `NextTab` | `ctrl+tab`, `ctrl+pagedown` | Select the next tab. |
| `PreviousTab` | `ctrl+shift+tab`, `ctrl+pageup` | Select the previous tab. |
| `SwitchTab1` | `alt+1` | Select tab 1. |
| `SwitchTab2` | `alt+2` | Select tab 2. |
| `SwitchTab3` | `alt+3` | Select tab 3. |
| `SwitchTab4` | `alt+4` | Select tab 4. |
| `SwitchTab5` | `alt+5` | Select tab 5. |
| `SwitchTab6` | `alt+6` | Select tab 6. |
| `SwitchTab7` | `alt+7` | Select tab 7. |
| `SwitchTab8` | `alt+8` | Select tab 8. |
| `SwitchTab9` | `alt+9` | Select tab 9. |
| `Copy` | `ctrl+shift+c` | Copy the current selection. |
| `Paste` | `ctrl+shift+v` | Paste the clipboard. |
| `Clear` | — | Clear the active terminal buffer. |
| `ToggleFullscreen` | `f11` | Toggle fullscreen mode. |
| `ZoomIn` | `ctrl+equal`, `ctrl+plus` | Increase the font size. |
| `ZoomOut` | `ctrl+minus` | Decrease the font size. |
| `ResetZoom` | `ctrl+0` | Restore the default font size. |
| `Search` | `ctrl+shift+f` | Toggle the search overlay. |
| `DuplicateTab` | — | Duplicate the current tab. |
| `CloseOtherTabs` | — | Close every other tab. |
| `Quit` | `ctrl+shift+q` | Quit the application. |
| `SplitVertical` | `ctrl+shift+d` | Split the active pane left/right. |
| `SplitHorizontal` | `ctrl+shift+s` | Split the active pane top/bottom. |
| `FocusPaneLeft` | `alt+left` | Focus the pane to the left. |
| `FocusPaneRight` | `alt+right` | Focus the pane to the right. |
| `FocusPaneUp` | `alt+up` | Focus the pane above. |
| `FocusPaneDown` | `alt+down` | Focus the pane below. |
| `ClosePane` | `ctrl+shift+w` | Close the focused pane; if it is the tab's only pane, close the tab. |

The configurable actions with no built-in chord are `CloseTab`, `Clear`,
`DuplicateTab`, and `CloseOtherTabs`. Assigning `None` to a chord explicitly
unbinds it. A custom binding replaces the built-in action for that chord;
unknown action names are ignored and leave the built-in binding unchanged.

## Search

Search is per tab and starts with `Search` (by default, `ctrl+shift+f`).
Opening it records the active pane, starts with an empty query, and resets the
match state. Invoking `Search` again toggles the overlay closed; `Escape` also
closes it. Closing removes the matches and active marker. Reopening starts a
new empty query.

While the overlay is active, printable text is inserted at the query cursor.
`Left`/`Right` move one character; `Home` and `End` move to the beginning and
end. `Backspace` and `Delete` remove one character, or one whitespace-delimited
word when held with `Ctrl`. `Enter` selects the next match and `Shift+Enter`
selects the previous match. Editing refreshes the matches; search is a
case-insensitive literal search, not a terminal input operation.

Each tab's search state is independent, and the recorded source pane is used
for its search. Results cover that pane's scrollback and visible rows. A
selected scrollback match (reported internally as a negative row) scrolls the
source pane to reveal it; selecting a visible match returns the pane to the
bottom. Right Alt (AltGr), including platforms that report it with an implicit
Ctrl, is treated as text composition rather than shortcut dispatch, so its
text can be entered into the query (or terminal). Ordinary Ctrl/Alt-modified
text is not inserted as query text.

## Pointer controls and mouse reporting

Pointer operations target the pane under the pointer. A wheel event activates
that pane and, when terminal mouse reporting is disabled, scrolls its scrollback
(positive wheel motion moves up; negative motion moves down). A pane's
rightmost scrollbar strip can be clicked or dragged to map pointer position to
scrollback offset, and taking the scrollbar clears that pane's selection.

Terminal mouse reporting takes precedence over local wheel, selection, scrollbar,
paste, and hyperlink handling for a reporting pane. Holding `Shift` overrides
that reporting path, exposing the local controls instead. When local pointer
handling is active, left-drag selects characters, a double-click selects a word,
and a triple-click selects a line. Dragging beyond the pane while selecting
autoscrolls the pane and continues updating the selection.

When local handling is active, middle-clicking inside a pane runs
`mouse.middleClickAction`: by default it pastes the clipboard into that pane
(subject to `clipboard.confirmPaste`), `primary-paste` pastes the primary
selection, and `ignore` does nothing. Holding `Ctrl` and
left-clicking a link opens it, for detected URLs and for OSC 8 hyperlinks alike
(subject to the `hyperlinks` policy); the link is resolved from a fresh buffer
snapshot at click time rather than from a stale hover result. A reporting pane
can receive that click as a terminal mouse event instead, unless `Shift` is held.

## Lua and theme paths

Lua `config.lua` (or fallback `init.lua`) scripts live beside `config.json` and
can override JSON settings, register key bindings, and react to runtime events.
See the [Lua scripting guide](guides/Lua.md) for the API and reload lifecycle.
User themes are loaded from `<config-directory>/themes`.

## Live reload lifecycle

The watcher handles `Changed`, `Created`, `Deleted`, `Renamed`, and watcher
error events. Reloads are versioned and debounced so an editor's temporary file
cannot apply an older snapshot after a newer write. The host sets
`UserConfigService.CallbackDispatcher` to queue callbacks on the window thread.
On shutdown it unsubscribes the event, cancels pending reloads, disposes the
watcher, and clears the dispatcher.

Not every option applies to running sessions. For the VT, mouse, and shell
options (the `Scope` line of `dotty +explain-config OPTION` states the same):

| Option | Takes effect |
|---|---|
| `mouse.copyOnSelect` | for selections completed after the reload |
| `mouse.middleClickAction` | at the next middle click |
| `vt.windowResizeAllowed` | at the next resize request |
| `vt.checksumReportEnabled` | applied to every session on reload and to sessions created later |
| `shellIntegration` | new tabs and panes only; running shells are never touched |

## Discovering options: `+explain-config`

`dotty +explain-config` lists every documented option, and `dotty +explain-config OPTION` prints its type, default, allowed values, scope, and description. It never loads your `config.json` and opens no window; see [Command-line usage](CommandLine.md#explain-config). Defaults are read from the live default configuration object, so they cannot drift from the code.

### Adding an option

A drift-guard test class (`ConfigCatalogTests` in `tests/Dotty.App.Tests`) fails when these steps are not all done:

1. Add the property with a `[JsonPropertyName]` to `DottyUserConfig` (or its section class under `src/Dotty.Runtime/Config/`). A new section needs a default instance (not `null`) and a null-coalescing line in the config normalization so a `null` in JSON cannot remove it.
2. Register the snake_case Lua key in the `ConfigFields` registry of `LuaScriptHost.Config.cs`; for a new section also add its name to the group-key checks there. Lua key and JSON path must differ only by case and underscores.
3. Add one `Opt(...)` entry to `CreateLive()` in `src/Dotty.Runtime/Config/ConfigOptionCatalog.cs`: JSON path, Lua key, one of the type names `string`, `number`, `integer`, `boolean`, `string[]`, `map`, allowed values for an enumeration, a getter for the default, a one-line description (no newline), and a `scope` when the change does not apply everywhere at once. Entries stay sorted by JSON path.
4. Document it here, in the [Lua guide](guides/Lua.md#configuration-dottyconfig), and, if it needs a hot-reload note, in the table above.

The guard checks that every config property and every Lua key has a catalog entry, that every catalog entry names a real property and Lua field, that the printed default equals the live default, and that entries are unique, sorted, and well formed.

## Troubleshooting

1. Print the expected directory from the platform support guide and check
   `DOTTY_CONFIG_HOME`.
2. Validate that `config.json` is complete JSON after the editor finishes its
   atomic rename.
3. Check `UserConfigService.LastError` in a diagnostic host.
4. Restore the last known-good file if a setting is rejected.
5. If a font is unavailable, provide a comma-separated fallback stack ending in
   `monospace`.

See [Platform Support](PlatformSupport.md) for native dependencies, graphics
startup failures, and release smoke requirements.
