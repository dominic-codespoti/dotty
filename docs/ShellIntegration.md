# Dotty shell integration

Shell integration is opt-in: either source a script by hand, or enable [automatic injection](#automatic-injection) with `shellIntegration: "detect"`. Dotty does not install itself into shell startup files and none of these scripts edits a profile, rc file, or user configuration. Source the script in the shell where integration is wanted; closing that shell removes the setup.

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

## Automatic injection

Instead of sourcing a script by hand, set `"shellIntegration": "detect"` (default `"none"`; see [Configuration](Configuration.md#shellintegration)). Dotty then launches recognized shells so that they load the scripts above on their own. It is still opt-in and edits no dotfile: the rewrite is an argument or environment change for that one launch, and it is computed each time a session starts, so it applies to new tabs and panes only. `DOTTY_SHELL_INTEGRATION=0` (also `none`, `false`, `off`) in Dotty's own environment opts out regardless of the config.

**Finding the scripts.** `ShellIntegrationLocator` uses `DOTTY_SHELL_INTEGRATION_DIR` when set (re-read on every launch), otherwise `shell-integration/` beside the executable, otherwise `../share/dotty/shell-integration`. A directory counts only if it contains `dotty.bash`; if none is found nothing is injected.

**Which shell.** The shell is classified by file-name stem, case-insensitively and without `.exe`: `bash`, `zsh`, `fish`, `pwsh`, `powershell`. Everything else (`sh`, `dash`, `nu`, `cmd`, ...) launches unchanged.

| Shell | Mechanism |
|---|---|
| bash | argv becomes `bash --rcfile <dir>/inject/bash/dotty-rcfile.bash -i`. `--rcfile` replaces the normal startup files, so the wrapper itself reads `/etc/bash.bashrc` (when present) and `~/.bashrc`, then sources `dotty.bash`. |
| zsh | environment `ZDOTDIR=<dir>/inject/zsh` (not on Windows); a user `ZDOTDIR` is passed along as `DOTTY_ZSH_ZDOTDIR`. The shim `.zshenv` restores the user's `ZDOTDIR` (or unsets it), sources the user's own `.zshenv`, and registers a one-shot `precmd` function that sources `dotty.zsh` at the first prompt, so its hooks come after every rc file, and then emits the first prompt marks. |
| fish | environment `XDG_DATA_DIRS=<dir>/inject/xdg:<existing, or /usr/local/share:/usr/share>` (not on Windows, and not when the directory path contains `:`). fish loads `vendor_conf.d/dotty-shell-integration.fish`, which removes Dotty's entry from `XDG_DATA_DIRS` again so children do not inherit it, and sources `dotty.fish` in interactive shells. |
| PowerShell (`pwsh`, `powershell`) | argv becomes `<shell> -NoLogo -NoExit -Command "try { Invoke-Expression ([System.IO.File]::ReadAllText('<dir>/Dotty.ps1')) } catch { }"`, so a failure to load never stops the shell. |

Every injected launch also gets `DOTTY_SHELL_INTEGRATION=1` and `DOTTY_SHELL_INTEGRATION_DIR=<dir>` in its environment; variables the caller supplies win over these.

**What is not injected.** Nothing is rewritten for an explicit command (`dotty -- CMD ...`), for a shell string with arguments (`/usr/bin/env bash`, `bash --login`; a whole-string path that is an existing file, such as a Windows path with spaces, still counts), for an unrecognized shell, when the mode is `none` or misspelled, when the opt-out variable is set, or when the scripts directory is not found. Any unexpected error also means "launch unchanged". Shells that are already running, shells you start yourself inside a pane (for example over `ssh`), and shells Dotty did not classify never get integration; source the script by hand there.

**Caveats.**

- zsh reads `/etc/zshenv` before `$ZDOTDIR/.zshenv`. Dotty only sets `ZDOTDIR` in the child's environment, so a system-wide or distribution-managed zsh setup (for example a Nix or home-manager `/etc/zshenv`) that reassigns `ZDOTDIR` makes zsh skip Dotty's shim, and zsh injection then does nothing. This follows from zsh's startup order and has not been tested here; source `dotty.zsh` from your own `.zshrc` in that case.
- The bash wrapper reproduces `/etc/bash.bashrc` and `~/.bashrc` only. A distribution that expects other files for non-login interactive shells is not replayed.
- The shipped archive must contain the dotfile `inject/zsh/.zshenv`; the build packages `shell-integration/**` recursively and the release workflow asserts the files (see [Releasing](Releasing.md#archive-assertions)).

## OSC 7 acceptance rules

Dotty only trusts a working directory it can validate. A report is **ignored** (the previous directory is kept) unless all of these hold:

- It is a `file://` URI whose host is empty, `localhost`, `127.0.0.1`, `[::1]`, or this machine's name. Any other host, including a Windows UNC server, is rejected.
- `?` and `#` appear only as `%3F` and `%23`; a raw one rejects the report.
- The URI has no control characters, C1 characters, or U+FFFD (before or after decoding) and, on Windows, no backslash.
- Percent-escapes are well formed and decode to valid UTF-8, and the decoded path is 1 to 4096 characters.
- The path is absolute (`/...`, or `/C:/...` on Windows) and is normalized (`.` and `..` resolved, empty segments dropped).

Dotty does not check that the directory exists when the report arrives; that happens when a new tab or pane is opened (see above). The full parser rules are in [Parsing](Parsing.md#osc-7-working-directory).

## Environment seen by the shell

Dotty starts every shell with `TERM_PROGRAM=dotty`, `TERM_PROGRAM_VERSION`, and `DOTTY_SURFACE_ID`; scripts and prompts can use them to detect Dotty. The Unix helper's own variables (`DOTTY_CONTROL_SOCKET`, `DOTTY_INITIAL_COLS`, `DOTTY_INITIAL_ROWS`, `DOTTY_INITIAL_XPIXEL`, `DOTTY_INITIAL_YPIXEL`) are removed before the shell starts and do not appear in the environment. See [Configuration](Configuration.md#session-launch-context).

## Verification scope

The unit tests in `tests/Dotty.NativePty.Tests/ShellIntegrationScriptTests.cs` run Bash for real on Unix when `bash` is present (idempotence, byte-exact OSC 7 encoding, silent skip) and only check the text of the zsh, fish, and PowerShell scripts. Running zsh, fish, and PowerShell interactively (the PowerShell script on Windows with `pwsh`, and Windows PowerShell if you use it) is manual verification; there is no CI coverage of them.

Injection is covered at three levels:

- `tests/Dotty.App.Tests/ShellIntegrationLauncherTests.cs` checks the pure argv/environment rewrite for every shell and every non-injection case, and `SessionLaunchEnvironmentTests` checks that the injected variables merge into the launch environment.
- `tests/Dotty.NativePty.Tests/ShellIntegrationInjectionTests.cs` runs the bash rcfile wrapper for real on Unix when `bash` is present (marks appear with injection and are absent without it) and, for zsh and fish, only reads the shim text; it also asserts that `Dotty.csproj` packages the injection directory recursively including dotfiles.
- The CI Ubuntu `build-and-test` job installs `zsh` and `fish`, but no test starts either shell. **Manual verification:** a real zsh, fish, and PowerShell launch through Dotty with `shellIntegration: "detect"` (see [E2E Testing](E2ETesting.md#manual-smoke-shell-integration-injection)), and the Nix/`ZDOTDIR` situation above. None of this has CI coverage.
