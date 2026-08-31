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

Place user theme files in `<config-directory>/themes`. Lua startup scripts
(`config.lua` or `init.lua`) live in the configuration directory. Both follow
the same `DOTTY_CONFIG_HOME` override as `config.json`.

## Safe live reload

Write a complete replacement file, preferably by writing a temporary file and
atomically renaming it over `config.json`. The watcher debounces the resulting
`Changed`, `Created`, and `Renamed` events. Invalid JSON leaves the last valid
configuration active and exposes the error through `UserConfigService.LastError`.

The old C# source-generator examples in this file belonged to a retired host
and are intentionally not presented as supported configuration syntax.
