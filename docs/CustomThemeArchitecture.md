# Custom Themes

Dotty loads user themes as JSON from the platform-specific themes directory.
The directory is resolved by `Dotty.Runtime.Config.PlatformPaths`:

- Linux: `$XDG_CONFIG_HOME/dotty/themes`, or `~/.config/dotty/themes`
- macOS: `~/Library/Application Support/Dotty/themes`
- Windows: `%APPDATA%/Dotty/themes`

Set `DOTTY_CONFIG_HOME` to override the parent directory on every platform.

## Create a theme

```bash
mkdir -p "$HOME/.config/dotty/themes"
cat > "$HOME/.config/dotty/themes/ocean.json" <<'JSON'
{
  "name": "Ocean",
  "description": "A calm blue theme",
  "isDark": true,
  "aliases": ["ocean"],
  "colors": {
    "background": "#0D1B2A",
    "foreground": "#E0E1DD",
    "ansiBlack": "#1B263B",
    "ansiRed": "#FF6B6B",
    "ansiGreen": "#4ECDC4",
    "ansiYellow": "#FFE66D",
    "ansiBlue": "#45B7D1",
    "ansiMagenta": "#C792EA",
    "ansiCyan": "#7FDBDA",
    "ansiWhite": "#E0E1DD",
    "ansiBrightBlack": "#415A77",
    "ansiBrightRed": "#FF8E8E",
    "ansiBrightGreen": "#6EE7DE",
    "ansiBrightYellow": "#FFF0A3",
    "ansiBrightBlue": "#6FCBE0",
    "ansiBrightMagenta": "#D7B5F5",
    "ansiBrightCyan": "#A8F0F0",
    "ansiBrightWhite": "#FFFFFF"
  }
}
JSON
```

Use the theme name in `config.json`:

```json
{ "theme": "Ocean" }
```

`UserThemeLoader` validates theme JSON before it is registered. Invalid files
are ignored with a diagnostic; they do not replace the active theme. Built-in
themes remain available when the user directory is empty.

## Runtime behavior

The theme registry combines built-in and user themes. The host reloads its
active theme after accepted configuration changes. Theme files should be
replaced atomically when an editor writes them so the watcher observes a
complete JSON document.

See [Configuration](Configuration.md) for the full path override and
[Platform Support](PlatformSupport.md) for troubleshooting.
