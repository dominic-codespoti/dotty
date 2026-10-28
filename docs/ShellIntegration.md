# Dotty shell integration

Shell integration is opt-in. Dotty does not install itself into shell startup files and none of these scripts edits a profile, rc file, or user configuration. Source the script in the shell where integration is wanted; closing that shell removes the setup.

From the repository checkout, use the script for the shell currently running:

- Bash: `source shell-integration/dotty.bash`
- zsh: `source shell-integration/dotty.zsh`
- fish: `source shell-integration/dotty.fish`
- PowerShell 7 with PSReadLine 2.2+: `. ./shell-integration/Dotty.ps1`

Published Dotty app builds also include these opt-in scripts in the `shell-integration/` directory beside the app. From the published app directory, source the matching path (for example, `source ./shell-integration/dotty.bash` or `. ./shell-integration/Dotty.ps1`).

New tabs and split panes inherit the active pane's selected shell and its live OSC 7 working directory. A shell explicitly supplied by a Lua tab or pane action continues to take precedence. The directory is resolved when the tab or pane is created, not when it was reported: the OSC 7 directory is used if it still exists, otherwise the session's launch directory, otherwise the default launch directory, so a deleted or unreadable directory cannot leave "New Tab" doing nothing.

The Bash, zsh, and fish scripts use the shell's current-session hooks; PowerShell wraps the current `prompt` function and PSReadLine AddToHistoryHandler. Existing prompt functions/hooks are retained where the shell API allows. Re-sourcing the PowerShell script is idempotent: it preserves the current command marker and does not wrap the prompt/history hooks again. Dotty receives local OSC 7 working-directory reports and OSC 133 prompt/command/output/end markers. OSC 133 D includes the command's exit status. Fish is supported by the script, but requires fish installed. PowerShell command boundaries use PSReadLine 2.2+'s history callback.

The markers power previous/next prompt navigation (`Ctrl+Shift+PageUp` / `Ctrl+Shift+PageDown`) and Copy Command Output (`Ctrl+Alt+O`). Output copy uses the OSC 133 C/D boundaries, excluding the typed command and following prompt. Without shell integration, these actions are unavailable; normal terminal input and selection continue to work.

Shell marker navigation uses the terminal's existing bounded prompt-mark collection, including its scrollback trim and resize/reflow mapping. It does not keep a second shell command history.

## What each script guarantees

- **Opt-in and idempotent.** Nothing edits a profile or rc file. Sourcing a script a second time does nothing (Bash `_dotty_bash_installed`, zsh `_dotty_zsh_installed`, fish `__dotty_installed`, PowerShell `$global:DottyShellIntegrationInstalled`), so a nested or re-sourced shell does not stack hooks. The Bash script also refuses to chain to its own hook when an inherited, exported `PROMPT_COMMAND` or `DEBUG` trap already names it.
- **Silent skip where integration cannot work.** The Bash, zsh, and fish scripts do nothing in non-interactive shells or when `TERM=dumb`. The PowerShell script returns without effect and without errors on `TERM=dumb`, a PowerShell older than 5, a non-console host, a non-`FullLanguage` language mode, or a missing/older-than-2.2 PSReadLine, and every other failure inside it is swallowed so shell startup is never affected.
- **Existing hooks are kept.** Bash chains the previous `DEBUG` trap and every `PROMPT_COMMAND` entry (array or string); PowerShell wraps the previous `prompt` function and `AddToHistoryHandler`; zsh uses `add-zsh-hook`; fish uses `--on-event` handlers (`fish_prompt` for the prompt marker and OSC 7, so it also fires on the first prompt and after an empty Enter; `fish_preexec`/`fish_postexec` for command boundaries).
- **OSC 7 is always percent-encoded.** Every script writes `file://HOST/PATH` with each byte outside `A-Z a-z 0-9 / . _ ~ -` encoded as `%XX` of the path's UTF-8 bytes (fish uses `string escape --style=url`). Spaces, `?`, `#`, `%`, and non-ASCII names are therefore always safe for Dotty's strict parser. PowerShell does the encoding by hand (not `System.Uri`), skips non-filesystem providers, and on Windows reports `file:///C:/...`; a UNC location is reported with its server as the host and is ignored by Dotty because the host is not local.

## OSC 7 acceptance rules

Dotty only trusts a working directory it can validate. A report is **ignored** (the previous directory is kept) unless all of these hold:

- It is a `file://` URI whose host is empty, `localhost`, `127.0.0.1`, `[::1]`, or this machine's name. Any other host, including a Windows UNC server, is rejected.
- `?` and `#` appear only as `%3F` and `%23`; a raw one rejects the report.
- The URI has no control characters, C1 characters, or U+FFFD (before or after decoding) and, on Windows, no backslash.
- Percent-escapes are well formed and decode to valid UTF-8, and the decoded path is 1 to 4096 characters.
- The path is absolute (`/...`, or `/C:/...` on Windows) and is normalized (`.` and `..` resolved, empty segments dropped).

Dotty does not check that the directory exists when the report arrives; that happens when a new tab or pane is opened (see above). The full parser rules are in [Parsing](Parsing.md#osc-7-working-directory).

## Environment seen by the shell

Dotty starts every shell with `TERM_PROGRAM=dotty`, `TERM_PROGRAM_VERSION`, and `DOTTY_SURFACE_ID`; scripts and prompts can use them to detect Dotty. The Unix helper's own variables (`DOTTY_CONTROL_SOCKET`, `DOTTY_INITIAL_COLS`, `DOTTY_INITIAL_ROWS`) are removed before the shell starts and do not appear in the environment. See [Configuration](Configuration.md#session-launch-context).

## Verification scope

The unit tests in `tests/Dotty.NativePty.Tests/ShellIntegrationScriptTests.cs` run Bash for real on Unix when `bash` is present (idempotence, byte-exact OSC 7 encoding, silent skip) and only check the text of the zsh, fish, and PowerShell scripts. Running zsh, fish, and PowerShell interactively (the PowerShell script on Windows with `pwsh`, and Windows PowerShell if you use it) is manual verification; there is no CI coverage of them.
