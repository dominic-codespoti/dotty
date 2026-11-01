# Dotty Configuration

Dotty uses a JSON configuration file at runtime. It loads the last valid
configuration at startup, watches the file for editor write/rename patterns,
and applies accepted changes on the desktop UI thread.

## File location

The configuration directory is selected by the host platform
(`PlatformPaths.Resolve`):

- Linux: `$XDG_CONFIG_HOME/dotty`, or `~/.config/dotty` when the variable is
  unset, empty, or relative (the XDG specification says relative values are
  ignored)
- macOS: `$XDG_CONFIG_HOME/dotty` (absolute values only), or `~/.config/dotty`,
  when that directory already exists; otherwise
  `~/Library/Application Support/Dotty`
- Windows: `%APPDATA%\Dotty`, or `<profile>\AppData\Roaming\Dotty` when the
  roaming folder is unknown

The file is `config.json`. Set `DOTTY_CONFIG_HOME` to override the complete
configuration directory. This override is useful for tests, portable installs,
and parallel development sessions.

Dotty writes no configuration on its own. Without `config.json` every option has
its built-in default, and a later release that changes a default reaches you
automatically; Dotty only creates the empty directory so a file created later is
picked up live. Run `dotty +init-config` to write a short commented starter file
(see [Command-line usage](CommandLine.md#init-config)); it lists the commands
below and leaves every option commented out. A `config.json` written by an older
Dotty that froze every default still works unchanged; delete the lines you never
meant to set so their defaults can follow future releases.

A malformed replacement leaves the last valid in-memory config active and
records the parse error in `UserConfigService.LastError`.
`dotty +validate-config` reports such problems, values of the wrong type, and
unknown (misspelled) keys with `file:line:column`; `dotty +show-config` prints
the effective JSON settings.

Dotty also has a per-user state directory for files it writes itself (never
read as configuration):

- Linux: `$XDG_STATE_HOME/dotty` (absolute values only), or
  `~/.local/state/dotty`
- macOS: `~/Library/Application Support/Dotty`
- Windows: `%LOCALAPPDATA%\Dotty`, or `<profile>\AppData\Local\Dotty`

`DOTTY_CONFIG_HOME` does not move the state directory. `dotty +diagnose` prints
both directories. The only file Dotty keeps there today is the local crash log
(`logs/crash.log`, see [Platform Support](PlatformSupport.md#crash-log)).

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
| `TERM_PROGRAM_VERSION` | The same string `dotty --version` prints: the release version for release builds (`0.3.0`, `0.4.0-beta.1`), `<version>-dev+<sha7>` for development builds. See [Command line](CommandLine.md). |
| `DOTTY_SURFACE_ID` | A stable per-session identifier (a GUID in `N` format) that is also the owning pane's `Id`. |
| `TERM` | The resolved [`term`](#term) setting (`xterm-256color` by default). Unlike the rows above it is set after the caller's variables, so it always applies. |

The Unix `pty-helper` protocol variables `DOTTY_CONTROL_SOCKET`,
`DOTTY_INITIAL_COLS`, `DOTTY_INITIAL_ROWS`, `DOTTY_INITIAL_XPIXEL`, and
`DOTTY_INITIAL_YPIXEL` are consumed by the helper and removed from the
environment before the shell or command is executed, so they never reach user
shells. (The pixel pair carries the text-area size in physical pixels for the
child's first `TIOCGWINSZ`; see [Native PTY](NativePty.md#pixel-sizes).)
Shell-integration scripts skip silently in non-interactive shells; see
[Shell Integration](ShellIntegration.md).

`DOTTY_PTY_HELPER`, read from Dotty's own environment, names the `pty-helper`
executable to use instead of searching for it; see
[Native PTY](NativePty.md#locating-the-helper).

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
    "size": 11.25,
    "lineHeight": 1.25,
    "symbolMap": ["U+2500-U+257F: Symbols Nerd Font Mono, Symbols Nerd Font"]
  },
  "window": {
    "padding": { "left": 14, "top": 8, "right": 14, "bottom": 8 },
    "decorations": "custom",
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
    "scrollMultiplier": 3,
    "alternateScroll": "auto",
    "reportBypassModifier": "shift",
    "middleClickAction": "clipboard-paste"
  },
  "vt": {
    "windowResizeAllowed": false,
    "checksumReportEnabled": false
  },
  "graphics": {
    "kittyProtocol": false,
    "imageStorageLimitMiB": 320,
    "maxPayloadMiB": 65,
    "allowFileTransfer": true,
    "allowTempFileTransfer": true,
    "allowSharedMemory": true,
    "windowsPassthrough": false
  },
  "shellIntegration": "none",
  "term": "xterm-256color",
  "keybindings": {
    "ctrl+shift+t": "new_tab",
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
| `family` | string | OS stack (below) | Comma-separated families in fallback order; the first installed one is the primary font. |
| `size` | number | `11.25` | Font size in **points**: device pixels = `size × 96/72 × display scale`, so the default is 15 px at 100 % and 30 px at 200 %. The display scale is the OS scale (Windows/X11 `glfwGetWindowContentScale`, macOS backing scale, Wayland fractional scale in 1/120 steps). Values are clamped to 1–512; a non-finite value falls back to the default. Zoom steps are 1 pt. |
| `lineHeight` | number | `1.25` | Line-height multiplier; values below `0.1` are clamped. The cell is at least `size × lineHeight` tall; extra height is split half above and half below the text (an odd pixel goes below). |
| `adjustCellWidth` | string | `""` | Cell width adjustment: signed logical pixels (`"1"`, `"-0.5"`, multiplied by the display scale) or a percentage (`"10%"`); at most 100 either way. Empty or malformed values keep the measured width; the cell never drops below 1 px. Lua: `font.adjust_cell_width`. |
| `adjustCellHeight` | string | `""` | Cell height adjustment in the same forms as `adjustCellWidth`; the change is split above and below the text like extra line height. Lua: `font.adjust_cell_height`. |
| `symbolMap` | string[] | `[]` | Per-range font overrides; use `U+2500-U+257F: Preferred Font, Fallback Font`. For each range, the first installed family that contains the glyph wins; overlapping ranges use the first matching entry. |
| `faintOpacity` | number | `0.5` | Faint (SGR 2) text keeps this share of its colour and is mixed toward the cell background (after inverse). Clamped to 0–1; a non-finite value uses `0.5`. |
| `builtinGlyphs` | boolean | `true` | Draw box drawing (U+2500–257F, including dashed lines, rounded corners and diagonals), block elements (U+2580–259F), braille (U+2800–28FF), sextants (U+1FB00–1FB3B), octants (U+1CD00–1CDE5) and Powerline separators (U+E0B0–E0BF) as built-in pixel-exact sprites sized to the cell, so lines join across cells at any `lineHeight` without a Nerd Font. `false` uses the font's glyphs. Lua: `font.builtin_glyphs`. |
| `boldFamily` | string | `""` | Comma-separated families for bold text in the primary font; the first installed one is used. Empty uses the primary family's own bold face; text is emboldened synthetically only when no heavier face exists. Fallback families always use their own bold face. Lua: `font.bold_family`. |
| `italicFamily` | string | `""` | Same for italic text (synthetic slant when no italic/oblique face exists). Lua: `font.italic_family`. |
| `boldItalicFamily` | string | `""` | Same for bold italic text. Lua: `font.bold_italic_family`. |
| `weight` | string | `"regular"` | Weight of regular text: `thin`, `extralight`, `light`, `regular`, `medium`, `semibold`, `bold`, `extrabold`, `black`, or a number 1–1000. Bold text asks for 700 (900 when `weight` is 600 or more). An invalid value prints a warning and uses regular. |
| `features` | string[] | `[]` | OpenType feature settings used for shaping: `+calt`, `-liga`, `ss01` (on), `cv01=2`, `liga=off`. A tag is four ASCII letters or digits; a later setting of a tag wins; ranged settings like `[3:5]calt` and malformed entries print a warning and are ignored. Applied when multi-scalar graphemes (combining marks, ZWJ sequences, flags) are shaped within their cell (W4-C10), and on top of `calt`, `liga` and `clig` when ligature runs are shaped (`-liga` turns ligatures off). |
| `ligatures` | string | `"cursor"` | Programming ligatures and contextual alternates (HarfBuzz) for runs of printable ASCII containing two adjacent punctuation characters, such as `->`, `!=`, `===`: `never`, `cursor` (the cell under the cursor is drawn unshaped and splits its run, so editing inside `=>` shows the separate characters) or `always`. A ligature is drawn from its first cell; the cells it covers keep their background and decorations. Runs break at blanks, wide cells, non-ASCII and style changes. Needs a font with ligatures; unknown values mean `cursor`. Lua: `font.ligatures`. |
| `hinting` | string | `"full"` | Outline hinting: `none`, `slight`, `normal` or `full`. One raster policy feeds both the cell metrics and the glyph atlas, so cells and glyphs always agree; changing it builds a new atlas. Unknown values mean `full`, the historical raster. |
| `antialias` | string | `"gray"` | Glyph coverage: `gray` (antialiased) or `none` (aliased, hard 1-bit edges). Subpixel (LCD) antialiasing is not supported: the atlas has one channel. Unknown values mean `gray`. |

`family` is an ordered list and also the fallback order (as in Ghostty): every
installed family is tried in turn for a glyph, then installed built-in symbol, CJK
and emoji families, then a bounded set of faces the system matches. Emoji-style
clusters (VS16, default-emoji characters, flags, keycaps, skin tones, ZWJ
sequences) prefer a colour face from the list, and VS15 prefers a monochrome one.
The default is the OS stack: Linux `monospace, DejaVu Sans Mono, Noto Sans Mono,
Liberation Mono, Noto Color Emoji`; macOS `Menlo, SF Mono, Monaco, Apple Color
Emoji, Apple Symbols`; Windows `Cascadia Mono, Consolas, Segoe UI Emoji, Segoe UI
Symbol`. No Nerd Font is assumed; install one and list it to get its icons.

Programming ligatures are not supported. The renderer draws
one grapheme per terminal cell rather than shaping ligatures across cells.
The renderer measures the selected typeface at the device font size and sizes cells
in whole device pixels (width = rounded advance, height = rounded
`max(size × lineHeight, ascent + descent)`, then adjusted). Window padding is in
logical pixels, multiplied by the display scale and rounded to whole pixels (14 at
125 % is 18). Underline and strikethrough positions and thicknesses come from the
font (fallback 1/14 em, at least 1 px). Cell metrics stay finite and positive even
when a platform font reports incomplete metrics.

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
| `opacity` | number | `1` | Opacity of the default background, 0–1, multiplied by the theme `opacity`. Text, explicit cell backgrounds, selection, cursor, tab bar/chrome and images stay opaque. The alpha framebuffer is a window-creation hint: it is requested only when `config.json` (Lua runs after the window exists) gives an opacity below 1 at startup, so going from opaque to translucent needs a restart; changes between translucent values apply live. X11 needs a compositing manager, Windows DWM composition. Panes with an OSC 11 background override stack their fill over the translucent clear, so they look slightly more opaque. |
| `title` | string | `Dotty` | Initial window title (blank uses `Dotty`); the title then follows the active tab. Read at startup. |
| `columns` / `rows` | integer | `0` | Initial grid size in cells; `0` uses 80 × 24, values above 1000 are clamped. Read at startup. |
| `startMaximized` | boolean | `false` | Maximize the window when it is first shown. Lua: `window.start_maximized`. |
| `startFullscreen` | boolean | `false` | Enter fullscreen when the window is first shown; wins over `startMaximized`. Lua: `window.start_fullscreen`. |
| `floating` | boolean | `false` | Keep the window above other windows (always on top); applies live. |
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
| `unfocusedStyle` | string | `hollow` |
| `text` | string | `auto` |

`shape` (`Block`, `Beam`, `Underline`) and `blink` are the default style. An
application that selects a style with DECSCUSR (`CSI Ps SP q`, `Ps` 1–6) gets
that shape and its steady/blink bit; DECSCUSR 0, DECSTR and RIS return to the
configured default. `Block` fills the cell (both cells of a wide character)
and draws the text under it in the `text` colour; `Beam` is a bar at the left
edge and `Underline` a bar at the bottom, 2 px × display scale. A cursor hidden
with DECTCEM (`CSI ? 25 l`) is not drawn. Blinking uses `blinkIntervalMs`
(raised to at least 100) and stops, showing the cursor, while the window is
unfocused. `unfocusedStyle` selects the unfocused cursor: `hollow` outlines the
cursor cell(s) whatever the shape, `same` keeps the focused shape, and `hidden`
draws none. `text` is `auto` (the cell background, or the cell foreground when
the background equals the cursor colour) or `cell-foreground` (the text keeps
its colour). The cursor colour is the pane's default foreground. Only the
active pane draws a cursor, and not while it is scrolled back.

Where the operating system exposes a caret-blink preference cheaply (Windows:
caret blink turned off in Settings), a disabled preference keeps the cursor
steady regardless of `blink` or DECSCUSR. Other platforms use the
configuration and DECSCUSR alone.

Changelog: W4-C18 — Windows caret-blink preference honoured.

### `theme`, `selectionColor`, and `panes`

`theme` names a built-in or user theme. `selectionColor` accepts the color
syntax understood by the active theme loader.

`panes.dividerThickness` is the gap between split panes in logical pixels
(multiplied by the display scale and rounded to whole device pixels; `0` removes
the gap, any positive value draws at least one pixel, values above `32` are
clamped). The gap is filled with the theme's chrome border colour.
`panes.activeBorder` paints the gap around the active pane in the theme's
accent colour while the tab has splits; a single pane never gets a border, and
with a thickness of `0` there is no gap to paint. Both are read every frame, so
a reload applies immediately.

User theme JSON files are loaded from `<config-directory>/themes`. See
[Themes](Themes.md) for the schema and validation rules.

### `bell`

What a terminal bell (BEL, `0x07`) does in addition to the tab-bar marker on a
background tab and the Lua `bell` event, which are unchanged.

| Property | Type | Default | Notes |
|---|---|---|---|
| `attention` | boolean | `true` | Request window attention while the window is not focused: GLFW `glfwRequestWindowAttention`, i.e. a taskbar flash on Windows, a dock bounce on macOS, the urgency hint on X11, and an XDG activation request on Wayland (compositor permitting). Nothing happens while the window is focused. |
| `visual` | boolean | `false` | Flash the window contents: an overlay in the theme foreground colour that fades from 35 % opacity to nothing over about 150 ms. |
| `audible` | boolean | `false` | Accepted but not yet implemented on any platform: no sound is played. |

Bells are rate-limited: bells less than 150 ms after the last one that took
effect produce no attention request and no flash, so a flood of BEL characters
flashes at most about six times per second. Every bell still reaches the Lua
`bell` event and the tab marker. All three keys are read live at each bell.

### `text`

How glyph coverage becomes colour, and a contrast floor. All keys are read every frame.

| Property | Type | Default | Notes |
|---|---|---|---|
| `blending` | string | `"native"` | `native` blends coverage in gamma space (the look Dotty always had). `linear` picks the blend alpha so the result has the luminance of a linear-light blend: light text on dark gets heavier, dark text on light gets lighter. `linear-corrected` is `linear` plus the `contrast` boost. Unknown values mean `native`. |
| `contrast` | number | `0.5` | Boost of `linear-corrected`, 0–1: coverage `a` becomes `a + k·a·(1−a)` with `k = contrast × clamp(0.5 + 0.5·(background − foreground luminance))`, so dark text on a light background gets the most. Ignored by the other modes. Clamped; non-finite uses `0.5`. |
| `minimumContrast` | number | `1` | Minimum WCAG 2 contrast ratio (1–21) of text against its visible background. Text below it is mixed toward white or black, just enough to meet it (or fully, if the floor is unreachable). Applied to the colour that is finally visible: after inverse and faint, and against the selection colour where text is selected. `1` is off. Lua: `text.minimum_contrast`. |

`native` stays the default until an A/B on real displays (dark and light themes,
low- and high-DPI) shows `linear` or `linear-corrected` is a clear win. The shader
mirrors the CPU reference `CoverageCorrection`/`TextBlend` (a GL readback test
compares them), using the cell background as the backdrop luminance.

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
| `scrollMultiplier` | number | `3` | Lines scrolled per wheel notch, for local scrollback and for alternate-scroll cursor keys. Touchpad deltas smaller than a notch accumulate (with the remainder carried to the next event, per pane) instead of each scrolling a full line. Applications that track the mouse always get one wheel report per notch. Clamped to 0.01 through 100. |
| `alternateScroll` | string | `"auto"` | `auto`: on the alternate screen, with no mouse reporting active and mode 1007 not reset by the application, the wheel sends cursor up/down keys (`SS3 A`/`SS3 B` under DECCKM) so `less`, `man` and similar programs scroll. `off`: never. Unknown values mean `auto`. |
| `reportBypassModifier` | string | `"shift"` | While held, clicks, drags and the wheel stay local (selection, scrollback, links) even when an application tracks the mouse: `shift`, `ctrl`, `alt`, `super`, or `none` (always report). Unknown values mean `shift`. |
| `middleClickAction` | string | `"clipboard-paste"` | What a middle click does in a pane that is not reporting mouse events: `primary-paste`, `clipboard-paste`, or `ignore` (`-` and `_` are interchangeable). Unknown values mean `clipboard-paste`. |
| `hideWhileTyping` | boolean | `false` | Hide the pointer when a key sends input to a pane; it reappears as soon as the mouse moves. |

Applications may set the pointer shape over their pane with OSC 22
(`OSC 22 ; name ST`): CSS names (`pointer`, `text`, `crosshair`, `ew-resize`,
`ns-resize`, `move`, `not-allowed`, `wait`, `progress`, `default`, …) and the
common X11 cursor-font names (`hand2`, `xterm`, `left_ptr`, `watch`, …); a
comma-separated list takes the first known name, an empty name or `default`
resets, and RIS resets. The shape replaces only the text beam over pane
content: divider resize, link, scrollbar, tab-bar and menu pointers keep the
host's shape, and `hideWhileTyping` hides the pointer whatever was requested.

Changelog: W4-C18 — `hideWhileTyping`, OSC 22 pointer shapes.

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

All mouse options are read live: a change applies to the next selection, click or wheel event.
Interoperation of X11 PRIMARY with other applications is a manual check; see
[E2E Testing](E2ETesting.md#manual-smoke-x11-primary-selection-copy-on-select-and-middle-click).

### `drop`

| Property | Type | Default | Notes |
|---|---|---|---|
| `mode` | string | `"paste-path"` | `paste-path`: dropping files on the window pastes their shell-escaped paths, separated by spaces, into the active pane through the normal paste path (bracketed paste, sanitising and `clipboard.confirmPaste` apply). `ignore`: drops do nothing. Unknown values mean `paste-path`. |
| `quote` | string | `"auto"` | `auto` picks the dialect from the pane's launch shell (`fish`, `pwsh`/`powershell`, `cmd`, anything else `posix`; `cmd` on Windows when no shell was given). `posix`: bare when every character is safe, otherwise single quotes (`'` becomes `'\''`). `fish`: single quotes with `\\` and `\'`. `powershell`: single quotes, every single-quote character (typographic ones too) doubled. `cmd`: double quotes; paths containing `"`, `%` or `!` are refused. |

A path containing a control character (C0, DEL, C1, U+2028/U+2029) or an
unpaired surrogate is never pasted; the other dropped paths still are.

Changelog: W4-C18 — new section.

### `selection`

| Property | Type | Default | Notes |
|---|---|---|---|
| `wordChars` | string | `"@-./_~?&=%+#"` | Punctuation a double-click keeps inside a word, besides letters, digits, combining marks, `_`, CJK and emoji clusters (ZWJ sequences included). Other punctuation selects as runs of its own; `""` keeps none. |
| `clearOnCopy` | boolean | `true` | Clear the selection after the Copy action put it on the clipboard. |
| `clearOnTyping` | boolean | `true` | Clear the selection when typed text or a key is sent to the pane. |
| `rectangleModifier` | string | `"alt"` | Held when a drag starts, makes a rectangular (block) selection: `alt`, `ctrl`, `shift`, `super`, or `none`. Unknown values mean `alt`. |

Selections are anchored to content: they follow their text while output
scrolls, while the view scrolls, and across a resize reflow. A selection is
cleared when its text is evicted from scrollback, by `CSI 3 J`, by an `ED 2`
that wipes history, by a reset and by an alternate-screen switch. Gestures:
drag selects cells, double-click a word, triple-click a logical line (across
soft-wrapped rows), Ctrl+triple-click the OSC 133 zone (prompt, command or
output) under the pointer, Shift+click extends the selection keeping the
granularity it started with, and Select All covers the whole scrollback.

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

### `graphics`

Settings for the [Kitty graphics protocol](KittyGraphics.md) (inline images).
The protocol is **off by default**: it stays off until a build has been checked
on real displays (see the manual smoke checklist in that page). While it is off,
`ESC _ G ...` sequences are discarded without a reply.

```json
{
  "graphics": {
    "kittyProtocol": true,
    "imageStorageLimitMiB": 320,
    "maxPayloadMiB": 65
  }
}
```

| Property | Lua key | Type | Default | Notes |
|---|---|---|---|---|
| `kittyProtocol` | `graphics.kitty_protocol` | boolean | `false` | Accept APC `G` commands. Turning it off deletes every image and aborts a transfer in flight. |
| `imageStorageLimitMiB` | `graphics.image_storage_limit_mib` | integer | `320` | Budget for stored image pixels, **per screen** (main and alternate each have a store) of each tab or pane. Non-positive values fall back to the default; values above `4096` are clamped to `4096`. |
| `maxPayloadMiB` | `graphics.max_payload_mib` | integer | `65` | Largest single transmission. Non-positive values fall back to the default; values above `1024` are clamped to `1024`. |
| `allowFileTransfer` | `graphics.allow_file_transfer` | boolean | `true` | Allow `t=f` (regular file named by the client). |
| `allowTempFileTransfer` | `graphics.allow_temp_file_transfer` | boolean | `true` | Allow `t=t` (temporary file, deleted after a successful read). |
| `allowSharedMemory` | `graphics.allow_shared_memory` | boolean | `true` | Allow `t=s` (Linux `/dev/shm`, Windows named mappings; unsupported on macOS whatever this says). |
| `windowsPassthrough` | `graphics.windows_passthrough` | boolean | `false` | Windows only, experimental: create the ConPTY with `PSEUDOCONSOLE_PASSTHROUGH_MODE` so APC is not dropped. Ignored elsewhere. |

Direct transmission (`t=d`) cannot be disabled. The maximum image dimension
(`10000`) and the placement cap (`8192` per screen) are built-in limits, not
configuration keys; the full list of limits is in
[Kitty graphics](KittyGraphics.md#limits-and-defaults). Image memory is outside
`scrollback.limitBytes`.

Reload behavior (read from the code, `needs runtime verification`): on a config
reload the host re-applies the first six properties to every existing tab and
pane and to sessions created later. Disabling the protocol deletes all images; a
smaller `imageStorageLimitMiB` evicts immediately; a transfer already in flight
keeps the limits it started with but its result is checked against the live
quota; media switches apply to the next transfer. `windowsPassthrough` is read
when a PTY is created, so it affects new tabs and panes only. Note that the
`Scope` line printed by `dotty +explain-config graphics.*` currently says
"Applies to sessions created after the change", which is more conservative than
the code.

Why the file and shared-memory media exist but are policy-checked: a client
that names a path makes the terminal read it, so absolute-path-only, deny-list,
symbolic-link resolution, a temp-root and marker rule for `t=t`, and a single
uniform error apply (see [Kitty graphics](KittyGraphics.md#transmission-media)).
Turn the three `allow*` switches off if you do not trust what prints to the
terminal.

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

### `term`

| Property | Type | Default | Notes |
|---|---|---|---|
| `term` | string | `"xterm-256color"` | `TERM` for shells and commands Dotty starts. `"xterm-dotty"` opts into Dotty's own terminfo entry. |

Dotty sets `TERM` for every shell or command it starts (it overrides a `TERM`
inherited from Dotty's own environment or passed by the caller) and reports the
same value for XTGETTCAP `TN`. A value other than `xterm-256color` is used only
when its terminfo entry exists in one of the directories ncurses searches:
`$TERMINFO`, `~/.terminfo`, `$TERMINFO_DIRS` (or, when that is unset,
`/etc/terminfo`, `/lib/terminfo`, `/usr/share/terminfo`, `/usr/lib/terminfo`,
`/usr/local/share/terminfo`, `/opt/homebrew/share/terminfo`), or the
`terminfo` directory shipped beside the executable; both the `x/xterm-dotty`
and the hexadecimal `78/xterm-dotty` layouts count. Otherwise the session falls
back to `xterm-256color`. Names containing a path separator or whitespace are
rejected the same way.

When the entry is found only in the shipped `terminfo` directory, that
directory is appended to the child's `TERMINFO_DIRS` (an unset variable becomes
the compiled-in default, the existing system directories above, then the
shipped one), so programs in the session can load it. `TERMINFO` is never set.
With the default `xterm-256color` the environment is left as it is. The entry
source is `terminfo/xterm-dotty.src` in the repository; see
[Keyboard protocol](KeyboardProtocol.md) for the capabilities it adds and for the
matching XTGETTCAP replies. Remote hosts reached with `ssh` usually lack the
entry, so keep the default when you work remotely. The setting is read each time
a session starts, so a reload applies to **new tabs and panes only**.

### `keybindings`

Keys are chords: `ctrl`, `shift`, `alt` (`option`, `opt`), `super` (`cmd`,
`command`, `win`) plus one key, in any order and case (`ctrl+shift+t`,
`cmd+k`, `ctrl++`). Key aliases: `esc`, `return`, `pgup`, `pgdn`, `del`, `ins`,
`bs`, `quote`, `grave`/`backtick`, `bracketleft`/`bracketright`, and single
punctuation characters (`,` `.` `/` `;` `'` `` ` `` `[` `]` `\` `-` `=` `+`).
`meta`, `hyper`, `mod` and `fn` are rejected.

A key event matches in this order; the first hit wins:

1. a chord written with the `physical:` prefix (`physical:ctrl+shift+c`)
   compared with the physical (US-position) key;
2. when the key types a Latin letter or digit on the active layout, the chord
   for that character (on Dvorak `ctrl+shift+c` is the key labelled C);
3. otherwise the physical key, so non-Latin layouts (Russian, Greek) keep the
   US positions. `ctrl+plus` matches the key that types `+`, with or without
   the Shift it takes.

Values are action names from the table below (the old enum names such as
`NewTab` are still accepted, case-insensitively). `none` unbinds a chord.
`send_text:<text>` writes the text to the active pane; it understands `\\`,
`\n`, `\r`, `\t`, `\e`, `\a`, `\b`, `\f`, `\v`, `\0`, `\xHH` and `\u{H…}`, and
any other backslash sequence is an error. Entries are added to the built-in
bindings; a custom chord replaces the built-in action for that chord. A
rejected entry (bad chord, unknown action, malformed `send_text`, the same
chord written twice) is logged on stderr when the config loads, listed by
`dotty +validate-config` with its line and column, and shown by
`dotty +explain-config keybindings` together with the live binding table.

On macOS the Command set is added to the chords below (they stay bound):
`cmd+c` copy, `cmd+v` paste, `cmd+a` select all, `cmd+t` new tab, `cmd+w`
close pane, `cmd+q` quit, `cmd+f` search, `cmd+k` clear scrollback,
`cmd+equal`/`cmd+plus`/`cmd+minus`/`cmd+0` zoom, `cmd+[`/`cmd+]` and
`cmd+shift+[`/`cmd+shift+]` previous/next tab, `cmd+1`…`cmd+9` select tab,
`cmd+enter` fullscreen, `cmd+d`/`cmd+shift+d` split. Context-menu labels show
the chord that is bound at the time the menu opens.

| Action | Built-in chord(s) | Behavior |
|---|---|---|
| `none` | — | Unbind the chord; never dispatched. |
| `new_tab` | `ctrl+shift+t` | Create a new tab. |
| `close_tab` | — | Close the current tab. |
| `next_tab` | `ctrl+tab`, `ctrl+pagedown` | Select the next tab. |
| `previous_tab` | `ctrl+shift+tab`, `ctrl+pageup` | Select the previous tab. |
| `switch_tab_1` | `alt+1` | Select tab 1. |
| `switch_tab_2` | `alt+2` | Select tab 2. |
| `switch_tab_3` | `alt+3` | Select tab 3. |
| `switch_tab_4` | `alt+4` | Select tab 4. |
| `switch_tab_5` | `alt+5` | Select tab 5. |
| `switch_tab_6` | `alt+6` | Select tab 6. |
| `switch_tab_7` | `alt+7` | Select tab 7. |
| `switch_tab_8` | `alt+8` | Select tab 8. |
| `switch_tab_9` | `alt+9` | Select tab 9. |
| `copy` | `ctrl+shift+c` | Copy the current selection. |
| `paste` | `ctrl+shift+v` | Paste the clipboard. |
| `clear` | — | Send Ctrl+L (form feed) so the shell clears the screen. |
| `toggle_fullscreen` | `f11` | Toggle fullscreen mode. |
| `zoom_in` | `ctrl+equal`, `ctrl+plus` | Increase the font size. |
| `zoom_out` | `ctrl+minus` | Decrease the font size. |
| `reset_zoom` | `ctrl+0` | Restore the configured font size. |
| `search` | `ctrl+shift+f` | Toggle the search overlay. |
| `duplicate_tab` | — | Duplicate the current tab. |
| `close_other_tabs` | — | Close every other tab. |
| `quit` | `ctrl+shift+q` | Quit the application. |
| `split_vertical` | `ctrl+shift+d` | Split the active pane left/right. |
| `split_horizontal` | `ctrl+shift+s` | Split the active pane top/bottom. |
| `focus_pane_left` | `alt+left` | Focus the pane to the left. |
| `focus_pane_right` | `alt+right` | Focus the pane to the right. |
| `focus_pane_up` | `alt+up` | Focus the pane above. |
| `focus_pane_down` | `alt+down` | Focus the pane below. |
| `close_pane` | `ctrl+shift+w` | Close the focused pane; if it is the tab's only pane, close the tab. |
| `previous_prompt` | `ctrl+shift+pageup` | Scroll to the previous shell prompt (shell integration). |
| `next_prompt` | `ctrl+shift+pagedown` | Scroll to the next shell prompt (shell integration). |
| `copy_command_output` | `ctrl+alt+o` | Select and copy the latest command's output (shell integration). |
| `scroll_page_up` | `shift+pageup` | Scroll the scrollback up by half the pane height (at least one line). |
| `scroll_page_down` | `shift+pagedown` | Scroll the scrollback down by half the pane height. |
| `scroll_line_up` | `ctrl+shift+up` | Scroll the scrollback up one line. |
| `scroll_line_down` | `ctrl+shift+down` | Scroll the scrollback down one line. |
| `scroll_to_top` | `ctrl+shift+home` | Scroll to the oldest scrollback line. |
| `scroll_to_bottom` | `ctrl+shift+end` | Return to the live screen. |
| `select_all` | `ctrl+shift+a` | Select the active pane's scrollback and screen. |
| `clear_scrollback` | — | Discard the active pane's scrollback; the screen stays. |
| `reload_config` | — | Re-read config.json and the Lua config. |
| `paste_selection` | — | Paste the primary selection. |
| `search_next` | — | Select the next search match while the overlay is open. |
| `search_previous` | — | Select the previous search match while the overlay is open. |
| `copy_or_send` | — | Copy the selection; without one the key goes to the application (useful on `ctrl+c`). |
| `move_tab_left` | — | Move the active tab one place to the left. |
| `move_tab_right` | — | Move the active tab one place to the right. |
| `toggle_tab_bar` | — | Show or hide the tab bar. |
| `send_text` | — | Write the escaped text after the colon to the active pane (`send_text:\x1bOA`). |
| `resize_pane_left` | — | Move the active pane's nearest vertical divider one cell left. |
| `resize_pane_right` | — | Move the active pane's nearest vertical divider one cell right. |
| `resize_pane_up` | — | Move the active pane's nearest horizontal divider one row up. |
| `resize_pane_down` | — | Move the active pane's nearest horizontal divider one row down. |
| `zoom_pane` | — | Zoom the active pane to fill the tab, or restore the splits. |
| `equalize_panes` | — | Reset every split in the tab to an even ratio. |

The scroll actions are not performed on the alternate screen or when the pane
has no scrollback; the key then goes to the application. `Shift+Up/Down/Home/End`
are not bound, so editors receive them (`CSI 1;2A` and so on). Any key that
produces terminal input returns a scrolled pane to the live screen and clears
its selection (unless Ctrl or Alt is held); pressing a modifier, lock key, or
Menu on its own changes neither.

Changelog: W4-C15 — snake_case action names (enum names still accepted),
`physical:` chords and layout-aware matching, `cmd`/`option`/`win` aliases,
macOS Command set, new actions `send_text`, `select_all`, `clear_scrollback`,
`reload_config`, `paste_selection`, `search_next`, `search_previous`,
`copy_or_send`, `move_tab_left`, `move_tab_right`, `toggle_tab_bar`; rejected
entries are reported instead of silently ignored.

Changelog: W4-C16 — `resize_pane_left/right/up/down`, `zoom_pane` and
`equalize_panes`. Dividers can be dragged with the left button (resize pointer
over a divider, with a wider band once hovered so it does not flicker); every
pane keeps at least 2 columns or 1 row. Zoom ends when focus moves or the
splits change.

### `keyboard`

| Property | Type | Default | Notes |
|---|---|---|---|
| `leftAlt` | string | `"auto"` | Left Alt (Option on macOS): `meta`, `compose`, or `auto`. Case-insensitive; unknown values mean `auto`. |
| `rightAlt` | string | `"auto"` | Right Alt (AltGr; right Option on macOS): `meta`, `compose`, or `auto`. Case-insensitive; unknown values mean `auto`. |

- `meta`: the key is the terminal's Meta modifier. Alt+key sends `ESC` followed
  by the character the layout produces for that key, Shift included (Alt+Shift+,
  sends `ESC <`); when the platform delivers no character, or the delivered one
  was composed by the layout (macOS Option, an AltGr layout), the key's
  unshifted name is used (`ESC l`, `ESC L` with Shift). `alt+…` keybindings match.
- `compose`: the key belongs to the keyboard layout. The character it composes
  (AltGr+Q = `@` on a German layout, Option+L = `@` on a German Mac) is typed as
  text, into the terminal or the search query; `alt+…` keybindings do not match.
- `auto` (decision K1): on Linux and Windows the left key is `meta` and the right
  key is `compose` when the layout uses it as AltGr (X11/Wayland ISO Level 3,
  Windows Ctrl+Alt) and `meta` otherwise, so Right Alt on a US layout sends
  `ESC`-prefixed input. On macOS both keys are `compose`; set `leftAlt` to `meta`
  for Option-as-Meta.

Ctrl+letter on a non-Latin layout (for example Russian) sends the control code
of the physical key (Ctrl+С = `0x03`). Characters typed while Super/Command is
held are not sent. Both options are read live at the next key press.

## Search

Search is per tab and starts with `search` (by default, `ctrl+shift+f`).
Opening it records the active pane, starts with an empty query, takes the
regex/case/whole-word modes from the `search` config object, and resets the
match state. Invoking `search` again toggles the overlay closed; `Escape` also
closes it. Closing removes the matches and active marker. Reopening starts a
new empty query.

While the overlay is open it consumes the keyboard. Printable text is inserted
at the query cursor. `Left`/`Right` move one grapheme cluster (an emoji or a
letter with combining marks counts as one); `Home` and `End` move to the
beginning and end. `Backspace` and `Delete` remove one cluster, or one
whitespace-delimited word when held with `Ctrl`. `Enter`, `Down` and `F3`
select the next match; `Shift+Enter`, `Up` and `Shift+F3` the previous one.
`Ctrl+V` pastes the clipboard's first line, neutralised like a terminal paste
(control characters removed). `Alt+R` toggles regular-expression mode, `Alt+C`
case-sensitive matching and `Alt+W` whole-word matching. The overlay's ▲ ▼ ×
buttons and the `.*` `Aa` `W` toggles are clickable. An invalid regular
expression is searched literally and its `.*` toggle turns red.

| Property | Type | Default | Notes |
|---|---|---|---|
| `regex` | boolean | `false` | Start in regular-expression mode (.NET syntax). Lua: `search.regex`. |
| `caseSensitive` | boolean | `false` | Start case-sensitive. Lua: `search.case_sensitive`. |
| `wholeWord` | boolean | `false` | Start matching whole words only (neither end touches a letter, digit, or underscore). Lua: `search.whole_word`. |

Each tab's search state is independent, and the recorded source pane is used
for its search. Results cover that pane's scrollback and visible rows. A
selected scrollback match (reported internally as a negative row) scrolls the
source pane to reveal it; selecting a visible match returns the pane to the
bottom. Text composed with a `compose` Alt key (AltGr, Option) is inserted into
the query (see [`keyboard`](#keyboard)); ordinary Ctrl/Meta-modified text is not.

Changelog: W4-C16 — keys are consumed while the overlay is open, `Up`/`Down`/`F3`
navigation, `Ctrl+V` paste, regex/case/whole-word modes (`search.*`, `Alt+R/C/W`),
clickable buttons, grapheme-aware editing, invalid-regex indicator.

## Pointer controls and mouse reporting

Pointer operations target the pane under the pointer. A wheel event activates
that pane and, when terminal mouse reporting is disabled, scrolls its scrollback
by `mouse.scrollMultiplier` lines per notch (positive wheel motion moves up;
negative motion moves down; touchpad fractions accumulate). On the alternate
screen the wheel sends cursor up/down keys instead, unless the application reset
mode 1007 or `mouse.alternateScroll` is `off`. A pane's
rightmost scrollbar strip can be clicked or dragged to map pointer position to
scrollback offset, and taking the scrollbar clears that pane's selection.

Terminal mouse reporting takes precedence over local wheel, selection, scrollbar,
paste, and hyperlink handling for a reporting pane. Holding
`mouse.reportBypassModifier` (`Shift` by default) overrides
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
| `selection.wordChars`, `selection.clearOnCopy`, `selection.clearOnTyping`, `selection.rectangleModifier` | at the next click, copy or key |
| `mouse.scrollMultiplier`, `mouse.alternateScroll`, `mouse.reportBypassModifier` | at the next pointer or wheel event |
| `vt.windowResizeAllowed` | at the next resize request |
| `vt.checksumReportEnabled` | applied to every session on reload and to sessions created later |
| `shellIntegration` | new tabs and panes only; running shells are never touched |
| `graphics.kittyProtocol` and the other `graphics.*` keys except `windowsPassthrough` | applied to every pane on reload and to sessions created later (see the [`graphics`](#graphics) note on the `+explain-config` scope text) |
| `graphics.windowsPassthrough` | new tabs and panes only (read when the PTY is created) |

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

1. Run `dotty +diagnose` to print the configuration directory Dotty uses and
   whether `config.json` and the Lua config are valid; check
   `DOTTY_CONFIG_HOME`.
2. Run `dotty +validate-config` after the editor finishes its atomic rename;
   it prints `file:line:column` for syntax errors, wrong value types, and
   unknown keys, and exits `1` when anything is wrong.
3. Run `dotty +show-config` to see the settings `config.json` produces.
4. Restore the last known-good file if a setting is rejected.
5. If a font is unavailable, provide a comma-separated fallback stack ending in
   `monospace`.

See [Platform Support](PlatformSupport.md) for native dependencies, graphics
startup failures, and release smoke requirements.

## Changelog

| Date | Change |
|------|--------|
| 2026-11-01 | W4-C12/C13: `window.opacity` and `window.title` are applied; added `window.columns`, `window.rows`, `window.startMaximized`, `window.startFullscreen`, `window.floating`. |
| 2026-11-01 | W4-C4: added `font.builtinGlyphs` (default `true`): built-in box drawing, block, braille, sextant, octant and Powerline sprites. |
| 2026-11-01 | W4-C22: headless `AccessibleTextModel` (trimmed lines, UTF-16 caret/selection offsets, char/word/line moves, coalesced change events) and `UiStrings` for every menu, prompt and chrome label; no new options. |
| 2026-11-01 | W4-C18: `drop.mode`, `drop.quote` (dropped files paste shell-escaped paths), `mouse.hideWhileTyping`, OSC 22 pointer shapes; the Windows caret-blink preference can keep the cursor steady. |
| 2026-11-01 | W4-C16: `resize_pane_*`, `zoom_pane`, `equalize_panes` actions; draggable split dividers with a resize pointer. |
| 2026-11-01 | W4-C5: added `font.hinting`, `font.antialias` (defaults keep the historical raster), `text.blending` (default `native`), `text.contrast`, `text.minimumContrast` (default 1 = off). |
| 2026-11-01 | W4-C6: `font.family` is the ordered fallback list with an OS default stack; added `font.boldFamily`, `font.italicFamily`, `font.boldItalicFamily`, `font.weight`, `font.features`. |
| 2026-11-01 | W4-C1: the display scale comes from the OS content scale (fixes unscaled text on Windows/X11 HiDPI); cells are integer device pixels; padding is rounded to device pixels. |
| 2026-11-01 | W4-C2: `font.size` is in points (was pixels; default 11.25 pt = 15 px at 100 %); added `font.adjustCellWidth`, `font.adjustCellHeight`; half-leading line height. |
| 2026-11-01 | W4-C10: `font.features` now apply to per-grapheme HarfBuzz shaping (combining-mark placement, ZWJ sequences, flags, keycaps). |
| 2026-11-01 | W4-C11: added `font.ligatures` (`never`/`cursor`/`always`, default `cursor`); `font.features` now applies to ligature shaping. |
