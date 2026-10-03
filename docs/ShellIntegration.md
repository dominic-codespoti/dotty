# Dotty shell integration

Shell integration is opt-in. Dotty does not install itself into shell startup files and none of these scripts edits a profile, rc file, or user configuration. Source the script in the shell where integration is wanted; closing that shell removes the setup.

From the repository checkout, use the script for the shell currently running:

- Bash: `source shell-integration/dotty.bash`
- zsh: `source shell-integration/dotty.zsh`
- fish: `source shell-integration/dotty.fish`
- PowerShell 7 with PSReadLine 2.2+: `. ./shell-integration/Dotty.ps1`

Published Dotty app builds also include these opt-in scripts in the `shell-integration/` directory beside the app. From the published app directory, source the matching path (for example, `source ./shell-integration/dotty.bash` or `. ./shell-integration/Dotty.ps1`).

New tabs and split panes inherit the active pane's selected shell and its live OSC 7 working directory. A shell explicitly supplied by a Lua tab or pane action continues to take precedence.

The Bash, zsh, and fish scripts use the shell's current-session hooks; PowerShell wraps the current `prompt` function and PSReadLine AddToHistoryHandler. Existing prompt functions/hooks are retained where the shell API allows. Re-sourcing the PowerShell script is idempotent: it preserves the current command marker and does not wrap the prompt/history hooks again. Dotty receives local OSC 7 working-directory reports and OSC 133 prompt/command/output/end markers. OSC 133 D includes the command's exit status. Fish is supported by the script, but requires fish installed. PowerShell command boundaries use PSReadLine 2.2+'s history callback.

The markers power previous/next prompt navigation (`Ctrl+Shift+PageUp` / `Ctrl+Shift+PageDown`) and Copy Command Output (`Ctrl+Alt+O`). Output copy uses the OSC 133 C/D boundaries, excluding the typed command and following prompt. Without shell integration, these actions are unavailable; normal terminal input and selection continue to work.

Shell marker navigation uses the terminal's existing bounded prompt-mark collection, including its scrollback trim and resize/reflow mapping. It does not keep a second shell command history.
