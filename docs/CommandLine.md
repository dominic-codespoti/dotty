# Command-line usage

Dotty accepts options before an optional command:

```text
 dotty [--working-directory DIR|-d DIR] [--shell PATH] [--] [COMMAND [ARG...]]
```

- `-h`, `--help`: print usage and exit without loading configuration or initializing graphics.
- `--version`: print the application version and exit without initializing the UI.
- `-d DIR`, `--working-directory DIR`: use DIR as the initial working directory. Relative paths resolve against Dotty's invocation directory.
- `--shell PATH`: start PATH as an interactive shell. This option is mutually exclusive with COMMAND. New tabs and split panes inherit this selected shell.
- `-- COMMAND [ARG...]`: launch COMMAND directly. Everything following the separator is passed as individual process arguments unchanged, including empty strings, leading dashes, spaces, and option-looking values. The command's exit status becomes Dotty's exit status. COMMAND and its arguments run only in the initial session; they are not replayed in new tabs or split panes.

An invalid option, working directory, or executable is reported before graphics startup and exits nonzero. With no command or explicit shell, Dotty retains its configured/default interactive shell behavior; new tabs and split panes inherit the selected interactive shell and active terminal working directory when available.

Examples:

```sh
 dotty -d ~/src -- nvim .
 dotty --working-directory /tmp -- /usr/bin/printf '<%s>\n' '' '-n'
 dotty --shell /usr/bin/fish
```
