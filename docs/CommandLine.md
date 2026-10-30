# Command-line usage

Dotty accepts options before an optional command:

```text
 dotty [--working-directory DIR|-d DIR] [--shell PATH] [--] [COMMAND [ARG...]]
 dotty +explain-config [OPTION]
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

## `+explain-config`

`dotty +explain-config [OPTION]` prints documentation for configuration options and exits; it is the one command-line *action* (arguments starting with `+`).

- With no argument it lists every option, one per line: the JSON path, then the description.
- With `OPTION` it documents one option: JSON path, `Lua key`, `Type`, `Default`, `Values` (for enumerations), `Scope` (when a change does not apply everywhere at once, for example "Applies to new tabs and panes only"), then a blank line and the description.
- `OPTION` is a JSON path (`mouse.copyOnSelect`), a Lua key (`mouse.copy_on_select`), or a last segment that is unambiguous (`copy-on-select`); case, `-` and `_` are ignored.
- A group name such as `mouse` or `window.padding` lists the options below it.
- The defaults shown are those of a fresh default configuration, not of your `config.json`.

Exit codes: `0` on success; `1` when nothing matches (the message `dotty: no such option 'X'`, up to five `Did you mean:` suggestions and a hint to list every option go to standard error); `2` for a usage error, such as a second `OPTION`, a second action, an unknown `+action`, an option-looking argument after the action, or combining the action with `--shell`, `-d`, or a command. `--help` and `--version` win over the action.

Early-exit contract: the action runs before launch validation, console detach, configuration loading, and any window or graphics initialization (`Program.Main` returns the result of `CliActions.Run` immediately). It therefore works on a machine with no display and does not read, create, or validate `config.json`. For how to keep the catalog complete when adding an option, see [Configuration](Configuration.md#adding-an-option).

```sh
 dotty +explain-config
 dotty +explain-config vt
 dotty +explain-config mouse.copyOnSelect
```
