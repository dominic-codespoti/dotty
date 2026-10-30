# Command-line usage

Dotty accepts options before an optional command:

```text
 dotty [--working-directory DIR|-d DIR] [--shell PATH] [--] [COMMAND [ARG...]]
 dotty +ACTION [ARG...]
```

- `-h`, `--help`: print usage and exit without loading configuration or initializing graphics.
- `--version`: print the application version and exit without initializing the UI. Release builds print the release version exactly (`0.3.0`, `0.4.0-beta.1`); development builds whose version carries a source revision print `<version>-dev+<sha7>` (`0.3.0-dev+abc1234`, or `0.4.0-beta.1.dev+abc1234` for a prerelease). The same string is exported as `TERM_PROGRAM_VERSION` and answered to XTVERSION (`CSI > q` → `DCS > | Dotty <version> ST`); DA2 (`CSI > c`) reports it as `CSI > 1 ; Pv ; 0 c` with `Pv = major×10000 + minor×100 + patch`.
- `-d DIR`, `--working-directory DIR`: use DIR as the initial working directory. Relative paths resolve against Dotty's invocation directory.
- `--shell PATH`: start PATH as an interactive shell. This option is mutually exclusive with COMMAND. New tabs and split panes inherit this selected shell.
- `-- COMMAND [ARG...]`: launch COMMAND directly. Everything following the separator is passed as individual process arguments unchanged, including empty strings, leading dashes, spaces, and option-looking values. The command's exit status becomes Dotty's exit status. COMMAND and its arguments run only in the initial session; they are not replayed in new tabs or split panes.

An invalid option, working directory, or executable is reported before graphics startup and exits nonzero. With no command or explicit shell, Dotty retains its configured/default interactive shell behavior; new tabs and split panes inherit the selected interactive shell and active terminal working directory when available. The inherited directory is checked when the tab or pane is created: if the directory the shell reported (OSC 7) no longer exists, Dotty falls back to the session's launch directory (`-d`, or the default) and then to the default launch directory. Every session also receives `TERM_PROGRAM=dotty`, `TERM_PROGRAM_VERSION`, and `DOTTY_SURFACE_ID` (see [Configuration](Configuration.md#session-launch-context)).

On Windows, before GUI startup, Dotty releases a console only when it is the
console's sole attached process. Desktop and Start-menu launches therefore do not
leave an extra console alongside the GUI; inherited/shared consoles remain
attached. Help, version, and launch-validation errors return before this step.
Redirected standard input, output, and error handles are preserved.

Examples:

```sh
 dotty -d ~/src -- nvim .
 dotty --working-directory /tmp -- /usr/bin/printf '<%s>\n' '' '-n'
 dotty --shell /usr/bin/fish
```

## Actions

Arguments starting with `+` are *actions*: they print, exit, and never open a terminal window.

| Action | Purpose |
|---|---|
| `+explain-config [OPTION]` | Document configuration options |
| `+init-config [--force]` | Write a commented starter `config.json` |
| `+show-config [--default]` | Print the effective JSON settings, or the built-in defaults |
| `+validate-config` | Check `config.json` and the Lua config |
| `+list-themes` | List built-in and user theme names |
| `+diagnose [--gl]` | Print what a bug report needs |

Rules shared by every action:

- One action per invocation. Its flags (`--force`, `--default`, `--gl`) are accepted only after the action they belong to, each at most once; only `+explain-config` takes a positional argument.
- Exit code `2` for a usage error: a second action, an unknown `+action`, a flag the action does not take, an extra argument, or combining the action with `--shell`, `-d`, or a command. The message goes to standard error with a hint to run `dotty --help`. `--help` and `--version` win over the action.
- Early-exit contract: the action runs before launch validation and console detach (`Program.Main` returns the result of `CliActions.Run` immediately). No action loads the live configuration, starts the config watcher, or creates a terminal window, so all of them work on a machine with no display; the only graphics use is the hidden probe window of `+diagnose --gl`. Which files an action reads or writes is listed in its section.

## `+explain-config`

`dotty +explain-config [OPTION]` prints documentation for configuration options and exits.

- With no argument it lists every option, one per line: the JSON path, then the description.
- With `OPTION` it documents one option: JSON path, `Lua key`, `Type`, `Default`, `Values` (for enumerations), `Scope` (when a change does not apply everywhere at once, for example "Applies to new tabs and panes only"), then a blank line and the description.
- `OPTION` is a JSON path (`mouse.copyOnSelect`), a Lua key (`mouse.copy_on_select`), or a last segment that is unambiguous (`copy-on-select`); case, `-` and `_` are ignored.
- A group name such as `mouse` or `window.padding` lists the options below it.
- The defaults shown are those of a fresh default configuration, not of your `config.json`.

Exit codes: `0` on success; `1` when nothing matches (the message `dotty: no such option 'X'`, up to five `Did you mean:` suggestions and a hint to list every option go to standard error); `2` for a usage error. It reads no file. For how to keep the catalog complete when adding an option, see [Configuration](Configuration.md#adding-an-option).

```sh
 dotty +explain-config
 dotty +explain-config vt
 dotty +explain-config mouse.copyOnSelect
```

## `+init-config`

`dotty +init-config` writes a short starter `config.json` into the configuration directory (see [Configuration](Configuration.md#file-location)), creating the directory if needed, and prints `Wrote <path>`. The file is JSON with `//` comments: it lists the other actions and shows `theme`, `font.family` and `font.size` with their current defaults, all commented out, so it parses to the built-in defaults. Dotty itself never writes `config.json`.

When `config.json` already exists the action refuses, prints `dotty: <path> already exists; use '+init-config --force' to replace it` to standard error and exits `1`. `--force` replaces the file. The write is atomic (temporary file, then rename), so a running Dotty reloads it like any other edit. Exit `1` also when the file cannot be written.

## `+show-config`

`dotty +show-config` prints the configuration that `config.json` produces as indented JSON: every option, with the file's values over the built-in defaults (exactly what the loader reads, so unknown keys are dropped). Without a `config.json` it prints the defaults. `--default` always prints the built-in defaults and ignores the file.

A `config.lua` (or `init.lua`) runs only at launch and is not evaluated here; when one exists, a note naming it goes to standard error, so standard output stays valid JSON. Exit `1`, with the parse error and a hint to run `+validate-config`, when `config.json` cannot be read.

## `+validate-config`

`dotty +validate-config` checks the configuration files without applying them:

- `config.json`: JSON syntax (comments and trailing commas are allowed, as in the loader), that the top level and every group such as `font` are objects, unknown keys (the loader silently ignores them, so a typo has no effect), and value types such as a string where a number belongs. Key names are matched case-insensitively, like the loader. Allowed values of enumerations are not checked.
- `config.lua` or `init.lua`: compiled, never run, so syntax errors are found and runtime errors still appear only at launch.

Each problem goes to standard error as `file:line:column: message` (`file:line: message` for Lua), for example `/home/me/.config/dotty/config.json:3:5: unknown option 'font.sise'; run 'dotty +explain-config' to list every option`. Exit codes: `0` when everything present is valid, or when neither file exists (`No config file at <path>; built-in defaults apply.`); `1` when any problem was found or a file cannot be read; `2` for a usage error.

## `+list-themes`

`dotty +list-themes` prints the built-in theme names, then the user themes found in `<config-directory>/themes` under a heading that names the directory (`(none)` when there are none). Names are sorted case-insensitively; a user theme with the name of a built-in one replaces it. Exit `0`.

## `+diagnose`

`dotty +diagnose` prints one `label: value` line per fact, for pasting into a bug report:

- Dotty version (with the development marker of non-release builds), .NET version and runtime identifier, OS description and architecture
- configuration directory, `config.json` and Lua config status (missing, valid, or the number of problems with a hint to run `+validate-config`), the state directory, and the [crash log](PlatformSupport.md#crash-log) path with whether it has entries
- the `pty-helper` Dotty would start (Linux and macOS; the source of the match, or every probed path when it is missing) and the shell-integration directory
- the `TERM` value new sessions get, whether it fell back from the configured `term`, and the bundled terminfo directory
- on Linux, `WAYLAND_DISPLAY` and `DISPLAY`, or a note that neither is set

`--gl` additionally opens a hidden window with Dotty's OpenGL 3.3 core context request and prints the driver's version, vendor and renderer strings, the adapter classification (for example a software rasterizer), and whether the version meets Dotty's minimum; when no context can be created it prints `probe failed:` and the reason. The report never contains environment dumps beyond the two display variables, and exit is always `0`; problems are part of the text.

```sh
 dotty +init-config
 dotty +validate-config
 dotty +show-config --default
 dotty +diagnose --gl
```
