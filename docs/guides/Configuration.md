# Configuration Workflows

This page covers common editing workflows and terminal interaction details. It
is not a second configuration reference: [Configuration](../Configuration.md)
is the canonical source for the JSON schema, defaults, supported actions,
platform paths, and live-reload contract. Follow it whenever a setting or
property list changes.

## Quick start

Dotty creates a JSON configuration file on first run. Edit config.json and the
host applies a complete valid replacement after the watcher debounce; no
rebuild or restart is required.

```json
{
  "font": { "family": "JetBrains Mono, Cascadia Code, monospace", "size": 14 },
  "theme": "Dracula"
}
```

The canonical reference documents the complete schema and supported values.

## Themes and Lua

Choose a built-in theme by name or place a user theme file under the themes
directory inside the configuration directory. See [Themes](../Themes.md) for
available palettes and [Custom Theme Architecture](../CustomThemeArchitecture.md)
for the user file format.

Lua startup scripts use config.lua (or init.lua as a fallback) from the
configuration directory. They can customize settings, bindings, tabs, panes,
and event hooks. See the [Lua scripting guide](Lua.md) for its API and reload
lifecycle.

## Search and pointer behavior

Search is per tab and opens or toggles closed with the Search action (the
default chord is ctrl+shift+f). It searches the recorded source pane's
scrollback and visible rows. Type to insert at the query cursor; use arrow keys,
Home, and End to move it; use Backspace/Delete to edit, or Ctrl with either
deletion key to remove a whitespace-delimited word. Enter advances to the next
match and Shift+Enter goes to the previous one. Escape closes the overlay.
Selecting a scrollback result scrolls its pane to reveal it; selecting a visible
result returns the pane to the bottom.

Search uses a case-insensitive literal query and does not send text to the PTY.
Right Alt is treated as AltGr (text composition rather than shortcut dispatch),
but AltGr-composed text is currently known to be dropped in the real key-event
order, so it may not reach the query. Ordinary Ctrl/Alt-modified text is not
inserted into the query.

Wheel, scrollbar, and selection handling is pane-targeted: the pane under the
pointer becomes active. With terminal mouse reporting disabled, the wheel
scrolls that pane's scrollback (`mouse.scrollMultiplier` lines per notch, 3 by
default), or sends cursor up/down keys on the alternate screen so `less` and
`man` scroll (`mouse.alternateScroll`), and the rightmost scrollbar strip can be clicked
or dragged to choose a scrollback position. Terminal mouse reporting takes
precedence over local wheel, scrollbar, selection, middle-paste, and hyperlink
handling. Holding Shift (`mouse.reportBypassModifier`) overrides reporting and restores the local controls.

When local handling is active, left-drag selects characters, double-click
selects a word, triple-click selects a line, and dragging outside the pane
autoscrolls while extending the selection. By default middle-click pastes the
clipboard into the target
pane (see `mouse.middleClickAction` below); a paste that contains line breaks (outside an application's bracketed
paste), control characters, or a paste-end marker first asks for confirmation
unless `clipboard.confirmPaste` is false. Ctrl+left-click opens a hyperlink,
whether it is an OSC 8 link or a detected URL, resolved from a fresh buffer
snapshot at click time; risky links (non-web schemes, local files, embedded
credentials, look-alike text) ask first, and some schemes are always blocked.
A reporting pane receives the click as a terminal mouse event unless Shift is
held. See the `clipboard` and `hyperlinks` sections of the
[Configuration reference](../Configuration.md#clipboard) for the options, the
OSC 52 clipboard read policy (`osc52Read`, denied by default), and the prompts.

## Mouse, VT, and shell-integration options

Three small option groups change behavior that is off or conservative by
default. Edit them in `config.json` (or `dotty.config` in Lua) like any other
setting; the [reference](../Configuration.md#mouse) has the full tables.

```json
{
  "mouse": { "copyOnSelect": "primary", "middleClickAction": "primary-paste" },
  "vt": { "windowResizeAllowed": false, "checksumReportEnabled": false },
  "shellIntegration": "detect"
}
```

- `mouse.copyOnSelect` (`none`, `primary`, `clipboard`, `both`) copies a finished
  selection without clearing it; `mouse.middleClickAction` (`primary-paste`,
  `clipboard-paste`, `ignore`) chooses what a middle click does. The PRIMARY
  selection is shared with other applications on X11 only; elsewhere it stays
  inside Dotty. Both options apply to the next selection or click after a reload.
- `vt.windowResizeAllowed` and `vt.checksumReportEnabled` stay `false` unless you
  trust everything that prints to the terminal: one lets programs resize the
  window, the other lets them read the screen back.
- `shellIntegration: "detect"` starts bash, zsh, fish, and PowerShell with the
  OSC 7/133 scripts loaded; set `DOTTY_SHELL_INTEGRATION=0` in Dotty's
  environment to opt out. It affects new tabs and panes only. See
  [Shell Integration](../ShellIntegration.md#automatic-injection).

To see every option with its default, run `dotty +explain-config`, or
`dotty +explain-config mouse.copyOnSelect` for one. It reads no config file and
opens no window.

## Scrollback

```json
{
  "scrollback": { "limitLines": 5000, "limitBytes": 67108864 }
}
```

`limitLines` (default `5000`, `0` = no history, at most `1000000`) and
`limitBytes` (default 64 MiB, `0` = no byte cap, counted as
`(lines + rows) * columns * 12` raw ring bytes) are both limits; whichever is
reached first applies. The byte limit is checked again on every resize, so a
wider terminal keeps fewer lines. Changes apply to tabs and panes created after
the change. See the [reference](../Configuration.md#scrollback) for details.

## Graphics

The [Kitty graphics protocol](../KittyGraphics.md) (inline images from tools
such as `kitten icat`) is **off by default**. Turn it on with:

```json
{
  "graphics": { "kittyProtocol": true }
}
```

Other keys: `imageStorageLimitMiB` (default 320, per screen of each tab or pane,
at most 4096), `maxPayloadMiB` (default 65, at most 1024), `allowFileTransfer`,
`allowTempFileTransfer` and `allowSharedMemory` (default `true`; they let a
program make Dotty read a file or shared-memory object by name, so switch them
off for untrusted workloads), and `windowsPassthrough` (Windows only,
experimental, default `false`). Image memory is not part of
`scrollback.limitBytes`. The first six settings are re-applied to running panes
on reload; `windowsPassthrough` affects new tabs and panes only. These reload
semantics are read from the code and `need runtime verification`. See the
[reference](../Configuration.md#graphics) and the
[manual smoke checklist](../KittyGraphics.md#manual-smoke-checklist).

## Safe edits and troubleshooting

Write a complete replacement to a temporary file and atomically rename it over
config.json; this avoids exposing a partially written document to the watcher.
Set DOTTY_CONFIG_HOME to isolate test or portable configurations.

If a change is not applied, confirm the selected configuration directory and
file name, save a complete JSON document, and wait for the watcher debounce.
Invalid JSON leaves the last valid configuration active and is reported through
host diagnostics. For path, schema, and validation details see the
[Configuration reference](../Configuration.md).

If a font family is unavailable, use a comma-separated fallback stack ending in
monospace. If a user theme is not found, check its name and placement in the
themes directory and inspect host diagnostics. See [Themes](../Themes.md) for
format and validation details.

## Further reading

- [Configuration reference](../Configuration.md)
- [Lua scripting guide](Lua.md)
- [Themes](../Themes.md)
- [Custom Theme Architecture](../CustomThemeArchitecture.md)
