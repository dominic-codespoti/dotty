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

## Example

```json
{
  "font": {
    "family": "JetBrains Mono, Cascadia Code, Liberation Mono, monospace",
    "size": 14,
    "lineHeight": 1.25
  },
  "window": {
    "padding": { "left": 14, "top": 8, "right": 14, "bottom": 8 },
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
trailing commas/comments are accepted by the source-generated JSON context.

## Options

### `font`

| Property | Type | Default | Notes |
|---|---|---|---|
| `family` | string | platform-neutral fallback stack | Comma-separated family names; first installed family wins. |
| `size` | number | `14` | Font size in points; non-finite or non-positive values are normalized. |
| `lineHeight` | number | `1.25` | Line-height multiplier; values below `0.1` are clamped. |

The renderer measures the selected typeface at the current framebuffer scale.
Cell width and height remain finite and positive even when a platform font
reports incomplete metrics.

### `window`

| Property | Type | Default |
|---|---|---|
| `padding.left` / `top` / `right` / `bottom` | number | `14`, `8`, `14`, `8` |
| `opacity` | number | `1` |
| `title` | string | `Dotty` |

### `tabBar`

| Property | Type | Default |
|---|---|---|
| `show` | boolean | `true` |
| `height` | number | `38` |
| `style` | string | `Pill` |

Supported styles are `Pill`, `Compact`, and `Minimal`.

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

### `keybindings`

Keys are normalized chords containing `ctrl`, `shift`, `alt`, or `super` plus a
key name. Values are `TerminalAction` names:

- tabs: `NewTab`, `CloseTab`, `NextTab`, `PreviousTab`, `SwitchTab1` … `SwitchTab9`;
- panes: `ClosePane`, `SplitVertical`, `SplitHorizontal`, `FocusPaneLeft`,
  `FocusPaneRight`, `FocusPaneUp`, `FocusPaneDown`;
- editing: `Copy`, `Paste`, `Search`, `Clear`.

Unknown action names are ignored and built-in bindings remain active.

## Lua and theme paths

Lua startup scripts (`config.lua` or `init.lua`) are loaded from the
configuration directory. User themes are loaded from
`<config-directory>/themes`. `LuaScriptHost.GetConfigLuaPath()` and
`UserThemeLoader` use the same platform path resolver as `config.json`.

## Live reload lifecycle

The watcher handles `Changed`, `Created`, `Deleted`, `Renamed`, and watcher
error events. Reloads are versioned and debounced so an editor's temporary file
cannot apply an older snapshot after a newer write. The host sets
`UserConfigService.CallbackDispatcher` to queue callbacks on the window thread.
On shutdown it unsubscribes the event, cancels pending reloads, disposes the
watcher, and clears the dispatcher.

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
