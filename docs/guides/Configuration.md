# Dotty Configuration Guide

Welcome to the Dotty Configuration Guide! This guide will help you customize your terminal experience, from simple theme changes to advanced custom color schemes.

## Table of Contents

1. [Quick Start](#quick-start)
2. [Configuration Location](#configuration-location)
3. [Available Options](#available-options)
4. [Theme Reference](#theme-reference)
5. [Advanced Examples](#advanced-examples)
6. [Troubleshooting](#troubleshooting)
7. [Rebuild Instructions](#rebuild-instructions)

---

## Quick Start

Dotty creates a JSON configuration file on first run. Edit `config.json` and
the host applies a complete valid replacement after its watcher debounce.

1. Find the file using the platform paths below.
2. Change a value such as `"theme"` or `"font.size"`.
3. Save the complete JSON document. No rebuild or restart is required.

```json
{
  "font": {
    "family": "JetBrains Mono, Cascadia Code, monospace",
    "size": 14,
    "lineHeight": 1.25
  },
  "theme": "Dracula",
  "cursor": { "shape": "Beam", "blink": true, "blinkIntervalMs": 600 }
}
```

## Configuration Location

| Platform | Location |
|---|---|
| Linux | `$XDG_CONFIG_HOME/dotty/config.json`, or `~/.config/dotty/config.json` |
| macOS | `~/Library/Application Support/Dotty/config.json` |
| Windows | `%APPDATA%/Dotty/config.json` |

Set `DOTTY_CONFIG_HOME` to override the complete configuration directory. Lua
startup scripts (`config.lua` or `init.lua`) live in that directory, and user
themes live under its `themes` subdirectory.

## Available Options

The configuration schema is the same on every supported platform. Property
names are case-insensitive; unknown properties are ignored; trailing commas and
comments are accepted.

### Font Settings

| Property | Type | Default | Notes |
|---|---|---|---|
| `font.family` | string | fallback stack | Comma-separated families; the first installed family is used. |
| `font.size` | number | `14` | Non-positive or non-finite values are normalized. |
| `font.lineHeight` | number | `1.25` | Values below `0.1` are clamped. |

```json
{
  "font": {
    "family": "JetBrainsMono Nerd Font Mono, JetBrains Mono, monospace",
    "size": 15,
    "lineHeight": 1.2
  }
}
```

### Theme Selection

Select a built-in or user theme by name:

```json
{ "theme": "TokyoNight" }
```

Built-in names are listed in [Themes](../Themes.md). User theme files are
described in [Custom Theme Architecture](../CustomThemeArchitecture.md).

### Window and tab bar settings

| Property | Type | Default |
|---|---|---|
| `window.padding.left` / `top` / `right` / `bottom` | number | `14`, `8`, `14`, `8` |
| `window.opacity` | number | `1` |
| `window.title` | string | `Dotty` |
| `tabBar.show` | boolean | `true` |
| `tabBar.height` | number | `38` |
| `tabBar.style` | string | `Pill` |

Supported tab-bar styles are `Pill`, `Compact`, and `Minimal`. Opacity applies
to the whole window and must be between `0` and `1`.

### Cursor settings

| Property | Type | Default |
|---|---|---|
| `cursor.shape` | string | `Block` |
| `cursor.blink` | boolean | `true` |
| `cursor.blinkIntervalMs` | integer | `500` |

Supported cursor shapes are `Block`, `Beam`, and `Underline`.

### Panes and keybindings

| Property | Type | Default |
|---|---|---|
| `panes.dividerThickness` | number | `2` |
| `panes.activeBorder` | boolean | `true` |
| `selectionColor` | string | theme default |
| `keybindings` | object | built-in bindings |

Keybinding keys are normalized chords containing `ctrl`, `shift`, `alt`, or
`super` plus a key name. Values are `TerminalAction` names. The [configuration
reference](../Configuration.md#keybindings) has the exact table of every
action and built-in chord, including actions such as `CloseTab`, `Clear`,
`DuplicateTab`, and `CloseOtherTabs` that have no default chord. A custom
binding replaces the action for that chord; assigning `None` unbinds it, and
unknown action names are ignored.

#### Search

Search is per tab and is opened or toggled closed with `Search` (the default
chord is `ctrl+shift+f`). It searches the recorded source pane's scrollback
and visible rows. Type to insert at the query cursor; use the arrow keys,
`Home`, and `End` to move it, `Backspace`/`Delete` to edit, and `Ctrl` with
either deletion key to remove a whitespace-delimited word. `Enter` advances to
the next match and `Shift+Enter` goes to the previous one. `Escape` closes the
overlay. Selecting a scrollback result scrolls its pane to reveal it; selecting
a visible result returns the pane to the bottom.

Search input is a case-insensitive literal query and does not go to the PTY.
Right Alt (AltGr), even when the platform also reports an implicit Ctrl, is
text composition rather than shortcut dispatch, so AltGr text can be entered.
Ordinary Ctrl/Alt-modified text is not inserted into the query.

#### Pointer controls

Wheel, scrollbar, and selection handling is pane-targeted: the pane under the
pointer becomes active. With terminal mouse reporting disabled, the wheel
scrolls that pane's scrollback, and the rightmost scrollbar strip can be
clicked or dragged to choose a scrollback position. Terminal mouse reporting
takes precedence over local wheel, scrollbar, selection, middle-paste, and
hyperlink handling. Holding `Shift` overrides reporting and restores the local
controls.

When local handling is active, left-drag selects characters, double-click
selects a word, triple-click selects a line, and dragging outside the pane
autoscrolls while extending the selection. Middle-click pastes into the
target pane. `Ctrl`+left-click opens a hyperlink resolved from a fresh buffer
snapshot at click time; a reporting pane receives the click as a terminal
mouse event unless `Shift` is held.

## Theme Reference

For built-in color values, theme names, and user-theme JSON, see
[Themes](../Themes.md) and [Custom Theme Architecture](../CustomThemeArchitecture.md).

## Advanced Examples

Write a complete replacement to a temporary file and atomically rename it over
`config.json`; this avoids exposing a partially written document to the watcher.
Use `DOTTY_CONFIG_HOME` to isolate portable or parallel development sessions:

```bash
mkdir -p /tmp/dotty-config
DOTTY_CONFIG_HOME=/tmp/dotty-config dotnet run --project src/Dotty/Dotty.csproj
```

Lua startup scripts use `config.lua` (or `init.lua` as a fallback) from the
configuration directory and can customize configuration, bindings, tabs, panes,
and event hooks. See the [Lua scripting guide](Lua.md) for the API and reload
lifecycle. User themes are loaded from `<config-directory>/themes`.

## Troubleshooting

### Changes are not applied

Confirm that the file is named `config.json` in the directory selected by
`DOTTY_CONFIG_HOME` or the platform defaults. Save a complete JSON document and
wait for the debounce interval. Invalid JSON leaves the last valid
configuration active and is reported through host diagnostics.

### Configuration file is not found

Print the configured directory and check that the process has permission to
create it. The host creates a default file on first run; source builds should
not hard-code a home-directory path.

### Font family is not found

Use a comma-separated fallback stack ending in `monospace`, for example
`JetBrains Mono, Cascadia Code, Liberation Mono, monospace`.

### Theme is not found

Check the spelling of the built-in or user theme name. Invalid user theme files
are ignored without replacing the active theme; inspect host diagnostics and
validate the JSON against [Custom Theme Architecture](../CustomThemeArchitecture.md).

## Rebuild Instructions

Configuration edits are runtime JSON changes and do not require a rebuild. A
source-code change can be built from the repository root:

```bash
dotnet build src/Dotty/Dotty.csproj -c Release
```

For Unix source builds, build the native PTY helper first:

```bash
make -C src/Dotty.NativePty
```

Run the host after a build:

```bash
dotnet run --project src/Dotty/Dotty.csproj -c Release
```

## Additional Resources

- [Configuration reference](../Configuration.md)
- [Custom Theme Architecture](../CustomThemeArchitecture.md)
- [Themes](../Themes.md)
- [Platform Support](../PlatformSupport.md)

## Getting Help

If a configuration problem is not covered here, check the
[troubleshooting section](#troubleshooting), inspect the host diagnostics, and
open an issue with the platform, configuration path, and relevant JSON.
