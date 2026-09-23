# Advanced Configuration

The current host uses the JSON schema documented in
[Configuration](Configuration.md). Platform-specific paths are resolved by
`Dotty.Runtime.Config.PlatformPaths`; do not hard-code `~/.config` when writing
portable tooling.

## Platform-specific values

External tools may choose values by inspecting the runtime platform, but the
configuration file itself remains the same JSON shape on every supported OS.
Use a comma-separated font stack so a missing platform font falls back safely.
Use `DOTTY_CONFIG_HOME` for portable or test installations.

## Themes and Lua

Place user theme files in `<config-directory>/themes`. Lua startup scripts can
override JSON settings and add bindings, tabs, panes, and event hooks. See the
[Lua scripting guide](guides/Lua.md) for the API and reload lifecycle; both
configuration files follow the `DOTTY_CONFIG_HOME` override.

## Safe live reload

Write a complete replacement file, preferably by writing a temporary file and
atomically renaming it over `config.json`. The watcher debounces the resulting
`Changed`, `Created`, and `Renamed` events. Invalid JSON leaves the last valid
configuration active and exposes the error through `UserConfigService.LastError`.

This page covers JSON configuration; see the Lua guide for the Lua API.
