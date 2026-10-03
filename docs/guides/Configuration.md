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
Right Alt (AltGr), even when the platform also reports an implicit Ctrl, is text
composition rather than shortcut dispatch, so AltGr text can be entered.
Ordinary Ctrl/Alt-modified text is not inserted into the query.

Wheel, scrollbar, and selection handling is pane-targeted: the pane under the
pointer becomes active. With terminal mouse reporting disabled, the wheel
scrolls that pane's scrollback, and the rightmost scrollbar strip can be clicked
or dragged to choose a scrollback position. Terminal mouse reporting takes
precedence over local wheel, scrollbar, selection, middle-paste, and hyperlink
handling. Holding Shift overrides reporting and restores the local controls.

When local handling is active, left-drag selects characters, double-click
selects a word, triple-click selects a line, and dragging outside the pane
autoscrolls while extending the selection. Middle-click pastes into the target
pane. Ctrl+left-click opens a hyperlink resolved from a fresh buffer snapshot
at click time; a reporting pane receives the click as a terminal mouse event
unless Shift is held.

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
