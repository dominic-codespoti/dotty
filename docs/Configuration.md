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
trailing commas/comments are accepted by the JSON parser.

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

When local handling is active, middle-clicking inside a pane pastes the clipboard
into that pane. Holding `Ctrl` and left-clicking a detected hyperlink opens it;
the link is resolved from a fresh buffer snapshot at click time rather than
from a stale hover result. A reporting pane can receive that click as a
terminal mouse event instead, unless `Shift` is held.

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
