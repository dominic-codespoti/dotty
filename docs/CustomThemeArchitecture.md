# Custom Themes

Dotty loads user themes as JSON from the platform-specific themes directory.
The directory is resolved by `Dotty.Runtime.Config.PlatformPaths`:

- Linux: `$XDG_CONFIG_HOME/dotty/themes`, or `~/.config/dotty/themes`
- macOS: `~/.config/dotty/themes` when `~/.config/dotty` exists, otherwise
  `~/Library/Application Support/Dotty/themes`
- Windows: `%APPDATA%/Dotty/themes`

Set `DOTTY_CONFIG_HOME` to override the parent directory on every platform.

## Create a theme

```bash
mkdir -p "$HOME/.config/dotty/themes"
cat > "$HOME/.config/dotty/themes/ocean.json" <<'JSON'
{
  "canonicalName": "Ocean",
  "displayName": "Ocean",
  "description": "A calm blue theme",
  "isDark": true,
  "aliases": ["ocean"],
  "colors": {
    "background": "#0D1B2A",
    "foreground": "#E0E1DD",
    "ansi": [
      "#1B263B", "#FF6B6B", "#4ECDC4", "#FFE66D",
      "#45B7D1", "#C792EA", "#7FDBDA", "#E0E1DD",
      "#415A77", "#FF8E8E", "#6EE7DE", "#FFF0A3",
      "#6FCBE0", "#D7B5F5", "#A8F0F0", "#FFFFFF"
    ]
  }
}
JSON
```

Use the theme name in `config.json`:

```json
{ "theme": "Ocean" }
```

User files accept either a single definition, as above, or a `ThemeRoot` wrapper
of the form `{"version": 1, "themes": [...]}`. Put one theme in each user file:
wrapped user files currently import their first definition only. Both loading
and validation inspect the inner definition rather than mistaking the wrapper
for an empty single theme.

Malformed JSON and definitions without colours are ignored with a diagnostic.
Watched updates also pass the inner definition through `ThemeValidator` for
required-field, colour-format and palette checks. Built-in themes remain
available when the user directory is empty.

## Runtime behavior

The theme registry combines built-in and user themes. The host reloads its
active theme after accepted configuration changes. The `reload_config` action
can apply an edited user theme immediately. Theme files should be replaced
atomically when an editor writes them so the watcher observes a complete JSON
document.

See [Configuration](Configuration.md) for the full path override and
[Platform Support](PlatformSupport.md) for troubleshooting.
