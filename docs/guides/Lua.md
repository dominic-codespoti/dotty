# Lua Scripting Guide

Dotty optionally loads a Lua script from its configuration directory. This guide describes the Lua API exposed by the running application.

## Loading

`config.lua` is preferred; if it is absent, Dotty uses `init.lua`. Both files live in the configuration directory used by `config.json`. Set `DOTTY_CONFIG_HOME` to override that directory. The script can use either the global `dotty` table or `require("dotty")`:

```lua
local dotty = require("dotty")
```

Use `:` for tab and pane handle methods, as in `pane:split("right")`. Call module functions such as `dotty.bind`, `dotty.on`, and `dotty.tabs.select` with `.`.

## Evaluation and reloads

Dotty loads `config.json` and evaluates the Lua script at startup, before applying the theme and font settings. Each evaluation creates a fresh Lua state, clears the previous script's key bindings, event hooks, timers, and tab/pane handles, and rebinds `dotty.config` to the loaded configuration. A change to any `.lua` file in the script directory, or a change to `config.json`, causes the host to reload configuration and reevaluate the selected script. The first successful evaluation is followed by normal startup; each later successful evaluation emits `config_reloaded()` to handlers registered by the new script.

During config evaluation, these runtime mutations are unavailable and raise a Lua error: `dotty.tabs.new/close/select/next/prev`, tab and pane handle methods `send/close/select/split/focus`, `dotty.action`, and `dotty.spawn`. The error says the function is unavailable while `config.lua` is being evaluated and recommends `dotty.on('gui_startup', fn)`. Register startup mutations in that event instead. Read-only operations, config writes, binding/event registration, and timer creation are allowed during evaluation.

`dotty.config` writes made during evaluation are part of the configuration passed into the host. At runtime, config writes take effect and schedule one coalesced application. On the next config reload, JSON is loaded and the Lua script is evaluated again, so the script can reapply its own settings.

## Configuration: `dotty.config`

The configuration table is readable and writable at any depth. Fields exposed to Lua are:

| Field | Lua type | Default |
|---|---|---|
| `theme` | string | `"DarkPlus"` |
| `selection_color` | string or `nil` | `nil` |
| `font.family` | string | `"JetBrainsMono Nerd Font Mono, JetBrains Mono, Fira Code, Cascadia Code, monospace"` |
| `font.size` | number | `14` |
| `font.line_height` | number | `1.25` |
| `window.opacity` | number | `1` |
| `window.title` | string | `"Dotty"` |
| `window.padding.left` | number | `14` |
| `window.padding.top` | number | `8` |
| `window.padding.right` | number | `14` |
| `window.padding.bottom` | number | `8` |
| `tab_bar.show` | boolean | `true` |
| `tab_bar.height` | number | `38` |
| `tab_bar.style` | string | `"Pill"` |
| `cursor.shape` | string | `"Block"` |
| `cursor.blink` | boolean | `true` |
| `cursor.blink_interval_ms` | integer | `500` |
| `panes.divider_thickness` | number | `2` |
| `panes.active_border` | boolean | `true` |
| `keybindings` | table mapping chords to action names | empty map |

Nested reads and writes work directly. `dotty.config.apply_table(table)` and its alias `dotty.config.apply(table)` apply recognized values recursively. Assigning a table to a nested section applies its fields; assigning a table to `keybindings` replaces the map. Reads of `keybindings` return a copy. Unknown fields are ignored.

```lua
dotty.config.font.size = 16
print(dotty.config.font.size)
dotty.config.apply_table {
  theme = "TokyoNight",
  window = { opacity = 0.95 },
  cursor = { blink_interval_ms = 600 },
}
```

Wrong value types raise a Lua error naming the configuration path, such as `dotty.config.font.size`; numeric settings require numbers, `cursor.blink_interval_ms` requires a number (converted to an integer), booleans and strings require their corresponding types, and `selection_color` also accepts `nil`. Field names use snake_case. Legacy spellings `tabbar`, `lineheight`, and `blinkintervalms` are accepted.

## Key bindings

Register a Lua callback or a `TerminalAction` name with `dotty.bind(chord, callback_or_action)`. Chords use the same syntax and normalization as `config.json` keybindings: modifier names `ctrl`, `shift`, `alt`, and `super`, followed by a key name, joined with `+` (for example, `ctrl+shift+t` or `alt+1`). Invalid chords raise a Lua error. A Lua binding takes precedence over the JSON binding for the same chord. A callback that returns exactly `false` declines the key and lets default handling continue; every other return value, including `nil`, handles it.

```lua
dotty.bind("ctrl+shift+t", function()
  dotty.tabs.new()
end)

dotty.bind("alt+1", "SwitchTab1")
```

Action names are case-insensitive. Passing `"None"` removes the Lua binding for that chord. Unknown action names raise a Lua error. The supported names are:

`NewTab`, `CloseTab`, `NextTab`, `PreviousTab`, `SwitchTab1`, `SwitchTab2`, `SwitchTab3`, `SwitchTab4`, `SwitchTab5`, `SwitchTab6`, `SwitchTab7`, `SwitchTab8`, `SwitchTab9`, `Copy`, `Paste`, `Clear`, `ToggleFullscreen`, `ZoomIn`, `ZoomOut`, `ResetZoom`, `Search`, `DuplicateTab`, `CloseOtherTabs`, `Quit`, `SplitVertical`, `SplitHorizontal`, `FocusPaneLeft`, `FocusPaneRight`, `FocusPaneUp`, `FocusPaneDown`, and `ClosePane`.

## Actions

`dotty.action(name)` runs a supported action by name and returns `true` if the host ran it, otherwise `false`. An unknown name or `"None"` raises a Lua error. `dotty.actions` is an array of supported action names and excludes `"None"`. `dotty.action` is unavailable during config evaluation.

## Tabs and panes

`dotty.tabs` has the properties `count`, `active_index`, and `active`; `active_index` is 1-based, or `0` when there is no active tab. Its functions are `all()`, `get(index)`, `new({ cwd, shell })`, `close(index)`, `select(index)`, `next()`, and `prev()`. Indices are 1-based. `get` returns `nil` for an invalid index; `close` and `select` silently do nothing for an invalid index and return no values.

Tab handles expose `id`, `index`, `title`, `cwd`, `is_active`, `is_valid`, `has_bell`, and `active_pane`; their methods are `send(text)`, `close()`, `select()`, and `panes()`. `send`, `close`, and `select` return a boolean indicating success; `panes()` returns an array of pane handles.

Pane handles expose `id`, `tab`, `index`, `cols`, `rows`, `is_active`, `is_valid`, `is_alt_screen`, `cursor` (with 1-based `row` and `col`), and `scroll_offset`. Their methods are `send(text)`, `split(direction, options)`, `focus()`, `close()`, `text()`, and `selection()`. `send`, `focus`, and `close` return a success boolean. Closing the only pane in a tab closes that tab. `split` returns the new pane handle, or `nil` if its receiver is no longer valid. `text()` returns visible rows joined with newlines, trims trailing spaces on each row, and removes empty rows at the end. `selection()` returns selected text or `nil`.

The only split directions are `"right"` and `"down"`; they create a vertical or horizontal split, respectively. Any other value raises a Lua error. The optional split options table accepts `cwd` and `shell`.

Handles remain Lua values after their tab or pane is closed, but report `is_valid == false`. Their `id` remains readable; unavailable properties return `nil` or `false`, and methods fail harmlessly (`nil` for pane `split`/`text`/`selection` and tab `panes`, `false` for boolean-returning methods). A handle is valid only in the Lua state in which it was created; reevaluation replaces that state and its handles.

```lua
local tab = dotty.tabs.active
local pane = tab.active_pane
local new_pane = pane:split("right", { cwd = "/tmp" })
print(new_pane:text())
```

## Events and value hooks

Register callbacks with `dotty.on(name, function(...) ... end)`. Event callbacks all run in registration order; their return values are ignored. Value hooks are tried in registration order until one returns a non-`nil` value, with the additional acceptance rules in the table below. Event names are case-insensitive; unknown names are accepted, so scripts may register hooks used by optional features.

| Name | Arguments | Fires / behavior |
|---|---|---|
| `gui_startup` | none | Once when the GUI starts, at most once per host lifetime. Use for startup actions such as creating or splitting panes. |
| `window_focus_changed` | `focused` (boolean) | When window focus changes. |
| `config_reloaded` | none | After each successful Lua evaluation following the initial successful evaluation. |
| `tab_added` | `tab` | When a tab is added. |
| `tab_closed` | `tab` | When a tab closes. |
| `tab_activated` | `tab` | When the active tab changes to a tab; no event is emitted when there is no active tab. |
| `tab_title_changed` | `tab`, `title` (string) | When the terminal tab title changes. |
| `process_exited` | `tab`, `pane`, `exit_code` (number) | When a pane's terminal process exits. |
| `bell` | `tab`, `pane` | When a pane signals a bell. |
| `pane_focused` | `tab`, `pane` | When the focused pane changes; `pane` is the newly focused pane. |
| `pane_layout_changed` | `tab` | When the pane layout changes. |
| `format_tab_title` | `tab` | Value hook: the first non-empty string sets the title used in the window title and tab bar; otherwise the terminal title is used. |
| `open_url` | `url` (string) | Value hook: return `true` to mark the URL as handled. |
| `update_status` | none | Value hook: the first non-empty string is shown in the tab bar status area. Refreshed about once per second and when tabs are added, closed, or activated, the active tab title changes, window focus changes, or configuration changes. |
| `clipboard_write` | `pane`, `text` (string) | Value hook for OSC 52 clipboard writes: only an explicit `false` denies the write; other results allow it. |

Lua errors from script evaluation, API calls, and callbacks are logged and stored as the latest Lua error. While an error is stored, it takes precedence over `update_status` and appears in the tab bar as `⚠ Lua: ` followed by the first error line (truncated to 120 characters). A successful config evaluation clears the stored error; a successful callback does not.

## Utilities

`dotty.log(...)` converts arguments with Lua's `tostring`, joins them with spaces, and writes an informational log message. If converting an argument errors, that argument is logged as `"nil"`.

`dotty.after(milliseconds, fn)` schedules a one-shot callback; `dotty.every(milliseconds, fn)` schedules a repeating callback. Both return a timer ID. Intervals must be finite, non-negative numbers no greater than `2147483647` milliseconds; `every` has a 16 ms minimum, while `after` may be zero. `dotty.cancel(id)` returns `true` if it cancels a currently registered timer and `false` for an invalid or unknown ID. Timers run callbacks on the UI thread and are discarded when the script is re-evaluated. Callback errors are logged and shown as Lua status warnings; an `every` timer continues after a callback error.

`dotty.spawn(program, { cwd = path })` opens a new tab running `program`, optionally using the given working directory. The program must be a non-empty string. It returns a tab handle and is unavailable during config evaluation.

## Complete example

Save as `config.lua`:

```lua
local dotty = require("dotty")

dotty.config.theme = "DarkPlus"
dotty.config.font.size = 14

dotty.bind("ctrl+shift+t", function()
  dotty.tabs.new()
end)

dotty.on("gui_startup", function()
  local tab = dotty.tabs.active
  if tab and tab.active_pane then
    tab.active_pane:split("right")
  end
end)

dotty.on("format_tab_title", function(tab)
  return tab.title .. " — Dotty"
end)

dotty.on("update_status", function()
  return os.date("%H:%M:%S")
end)
```
