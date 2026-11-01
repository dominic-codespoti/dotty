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
| `theme_light` | string or `nil` | `nil` (LightPlus when auto) |
| `theme_dark` | string or `nil` | `nil` (theme value when auto) |
| `theme_auto` | boolean | `false` |
| `selection_color` | string or `nil` | `nil` |
| `font.family` | string | `"JetBrainsMono Nerd Font Mono, JetBrains Mono, Fira Code, Cascadia Code, monospace"` |
| `font.size` | number | `11.25` (points) |
| `font.line_height` | number | `1.25` |
| `font.adjust_cell_width`, `font.adjust_cell_height` | string | `""` (`"2"` logical px or `"10%"`) |
| `font.symbol_map` | array of strings | empty |
| `font.hinting` | string (`"none"`, `"slight"`, `"normal"`, `"full"`) | `"full"` |
| `font.antialias` | string (`"gray"`, `"none"`) | `"gray"` |
| `text.blending` | string (`"native"`, `"linear"`, `"linear-corrected"`) | `"native"` |
| `text.contrast` | number | `0.5` (0–1, used by `linear-corrected`) |
| `text.minimum_contrast` | number | `1` (off; WCAG ratio 1–21) |
| `search.regex`, `search.case_sensitive`, `search.whole_word` | boolean | `false` |
| `clipboard.allow_osc52_write` | boolean | `false` |
| `clipboard.osc52_read` | string (`"deny"`, `"ask"`, `"allow"`) | `"deny"` |
| `clipboard.osc52_read_limit_bytes` | integer | `1048576` (clamped to 1 through 8388608) |
| `clipboard.confirm_paste` | boolean | `true` |
| `clipboard.bracketed_paste_is_safe` | boolean | `true` |
| `bell.attention` | boolean | `true` |
| `bell.visual` | boolean | `false` |
| `bell.audible` | boolean | `false` (accepted; no sound is played on any platform yet) |
| `hyperlinks.osc8` | boolean | `true` |
| `hyperlinks.confirm` | string (`"risky"`, `"always"`, `"never"`) | `"risky"` |
| `hyperlinks.allowed_schemes` | array of strings | `{"http", "https", "mailto", "file", "ssh", "git"}` |
| `keyboard.left_alt` | string (`"auto"`, `"meta"`, `"compose"`) | `"auto"` |
| `keyboard.right_alt` | string (`"auto"`, `"meta"`, `"compose"`) | `"auto"` |
| `mouse.copy_on_select` | string (`"none"`, `"primary"`, `"clipboard"`, `"both"`) | `"none"` |
| `mouse.scroll_multiplier` | number | `3` |
| `mouse.alternate_scroll` | string (`"auto"`, `"off"`) | `"auto"` |
| `mouse.report_bypass_modifier` | string (`"shift"`, `"ctrl"`, `"alt"`, `"super"`, `"none"`) | `"shift"` |
| `mouse.middle_click_action` | string (`"primary-paste"`, `"clipboard-paste"`, `"ignore"`) | `"clipboard-paste"` |
| `selection.word_chars` | string | `"@-./_~?&=%+#"` |
| `selection.clear_on_copy` | boolean | `true` |
| `selection.clear_on_typing` | boolean | `true` |
| `selection.rectangle_modifier` | string (`"alt"`, `"ctrl"`, `"shift"`, `"super"`, `"none"`) | `"alt"` |
| `vt.window_resize_allowed` | boolean | `false` |
| `vt.checksum_report_enabled` | boolean | `false` |
| `shell_integration` | string (`"none"`, `"detect"`) | `"none"` |
| `term` | string | `"xterm-256color"` (see [`term`](../Configuration.md#term)) |
| `scrollback.limit_lines` | integer | `5000` (`0` = no history, at most `1000000`) |
| `scrollback.limit_bytes` | integer | `67108864` (`0` = no byte cap; raw ring bytes `(lines + rows) * columns * 12`) |
| `graphics.kitty_protocol` | boolean | `false` |
| `graphics.image_storage_limit_mib` | integer | `320` (non-positive = default, at most `4096`) |
| `graphics.max_payload_mib` | integer | `65` (non-positive = default, at most `1024`) |
| `graphics.allow_file_transfer` | boolean | `true` |
| `graphics.allow_temp_file_transfer` | boolean | `true` |
| `graphics.allow_shared_memory` | boolean | `true` |
| `graphics.windows_passthrough` | boolean | `false` (Windows only, experimental) |

The `graphics.*` keys behave exactly like the JSON [`graphics`](../Configuration.md#graphics) options and control the [Kitty graphics protocol](../KittyGraphics.md), which is off by default. For example `dotty.config.graphics.kitty_protocol = true` turns it on. The settings are re-applied to running panes on reload except `graphics.windows_passthrough`, which is read when a PTY is created (`needs runtime verification`).
The clipboard and hyperlink keys behave exactly like the JSON `clipboard` and `hyperlinks` options in the [configuration reference](../Configuration.md#clipboard). In particular `clipboard.osc52_read = "allow"` lets any program that can write to the terminal read the clipboard without a prompt; prefer `"ask"`.
The `bell.*` keys behave exactly like the JSON [`bell`](../Configuration.md#bell) options and are read at each bell; the `bell` event below fires for every bell regardless of them.
The `font.hinting`/`font.antialias` and `text.*` keys behave exactly like the JSON [`font`](../Configuration.md#font) and [`text`](../Configuration.md#text) options; the `text.*` keys are read every frame.
The `keyboard.*` keys behave exactly like the JSON [`keyboard`](../Configuration.md#keyboard) options and are read at the next key press; for example `dotty.config.keyboard.left_alt = "meta"` makes the left Option key Meta on macOS.
The `mouse`, `vt`, and `shell_integration` keys behave exactly like the JSON [`mouse`](../Configuration.md#mouse), [`vt`](../Configuration.md#vt), and [`shellIntegration`](../Configuration.md#shellintegration) options. `mouse.copy_on_select` takes a string, so the Ghostty-style `"true"`/`"false"` spellings are strings here too. The two `vt` switches are off by default because they let a program resize the window or read the screen back; see the configuration reference for the security rationale. Reload behavior: `mouse.*` and `vt.window_resize_allowed` are read live, `vt.checksum_report_enabled` is applied to sessions on reload, and `shell_integration` applies to tabs and panes created after the reload. Run `dotty +explain-config OPTION` (JSON path or Lua key) for the type, default, and scope of any key.
`font.symbol_map` entries use `U+START-END: Preferred Font, Fallback Font`; the first installed family that contains the glyph wins. OpenType features and ligatures are not supported: rendering is one grapheme per terminal cell.
Nested reads and writes work directly. `dotty.config.apply_table(table)` and its alias `dotty.config.apply(table)` apply recognized values recursively. Assigning a table to a nested section applies its fields; assigning a table to `keybindings` replaces the map. Reads of `keybindings` return a copy. Unknown fields are ignored.

```lua
dotty.config.font.size = 16
print(dotty.config.font.size)
dotty.config.apply_table {
  theme = "TokyoNight",
  window = { padding = { left = 10 } },
  cursor = { blink_interval_ms = 600 },
}
```

Wrong value types raise a Lua error naming the configuration path, such as `dotty.config.font.size`; numeric settings require numbers, `cursor.blink_interval_ms` requires a number (converted to an integer), booleans and strings require their corresponding types, and `selection_color` also accepts `nil`. Field names use snake_case. Legacy spellings `tabbar`, `lineheight`, and `blinkintervalms` are accepted.

## Key bindings

Register a Lua callback or an action name with `dotty.bind(chord, callback_or_action)`. Chords use the same syntax, aliases, `physical:` prefix and layout-aware matching as `config.json` keybindings (see [Configuration](../Configuration.md#keybindings)); for example `ctrl+shift+t`, `cmd+k` or `alt+1`. Invalid chords raise a Lua error that says why. A Lua binding takes precedence over the JSON binding for the same chord. A callback that returns exactly `false` declines the key and lets default handling continue; every other return value, including `nil`, handles it.

```lua
dotty.bind("ctrl+shift+t", function()
  dotty.tabs.new()
end)

dotty.bind("alt+1", "switch_tab_1")
dotty.bind("ctrl+alt+u", "send_text:\\x15")
```

Action names are the snake_case names listed in [Configuration](../Configuration.md#keybindings) (`new_tab`, `copy_or_send`, `send_text:<escaped text>`, …); the older enum names such as `SwitchTab1` are accepted too, case-insensitively. Passing `"none"` removes the Lua binding for that chord. Unknown action names and malformed `send_text` arguments raise a Lua error. `dotty.action(name)` accepts the same names.

## Actions

`dotty.action(name)` runs a supported action by name and returns `true` if the host ran it, otherwise `false`. An unknown name or `"none"` raises a Lua error. `dotty.actions` is an array of the snake_case action names and excludes `"none"`. `dotty.action` is unavailable during config evaluation.

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
| `open_url` | `url` (string), `source` (string) | Value hook run when a link is activated (Ctrl+click) **after** structural validation and the hard scheme blocks but **before** the scheme allowlist and confirmation prompts. `url` is the canonical, ASCII, control-character-free URL; `source` is `"explicit"` for an OSC 8 link or `"detected"` for a URL found in text. Return `true` to mark the link handled, which skips the built-in open and any prompt (so a handling hook is responsible for its own safety checks); `false` continues with the built-in policy. Hooks run in registration order and the first non-`nil` result decides, so only the first hook that returns a value is consulted; a hook that raises an error is skipped. A callback declared with one parameter ignores `source`. |
| `update_status` | none | Value hook: the first non-empty string is shown in the tab bar status area. Refreshed about once per second and when tabs are added, closed, or activated, the active tab title changes, window focus changes, or configuration changes. |
| `clipboard_write` | `pane`, `text` (string) | Value hook for OSC 52 clipboard writes: the global `allowOsc52Write` setting must be enabled; with permission enabled, no hook or a non-vetoing result allows the write, while an explicit `false` or callback invocation error denies it. Hook errors remain logged through normal Lua diagnostics. |
| `clipboard_read` | `pane`, `selection` (string, `"clipboard"` or `"primary"`) | Value hook for OSC 52 clipboard reads: only consulted when `clipboard.osc52_read` is `ask` or `allow`, just before the clipboard (or, on X11 only, the PRIMARY selection) is read and the reply or prompt is produced. `"primary"` is only passed on X11; elsewhere primary-only requests are dropped before the hook. The first non-`nil` result decides: `false` denies (no reply is sent), anything else allows; a callback invocation error denies; no hook allows. The hook cannot grant a read when the policy is `deny`. |

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

## Changelog

| Date | Change |
|------|--------|
| 2026-11-01 | W4-C2: `font.size` is in points (default 11.25); added `font.adjust_cell_width`, `font.adjust_cell_height`. |
| 2026-11-01 | W4-C15/C16: `dotty.bind`/`dotty.action` take snake_case action names and `send_text:`; chords match like `config.json` keybindings; added `search.regex`, `search.case_sensitive`, `search.whole_word`. |
| 2026-11-01 | W4-C5: added `font.hinting`, `font.antialias`, `text.blending`, `text.contrast`, `text.minimum_contrast`. |
