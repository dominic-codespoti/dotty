# Dotty shell-integration injection for fish: found because Dotty prepends its inject/xdg
# directory to XDG_DATA_DIRS for the launch. Nothing here may stop the shell from starting.
if set -q DOTTY_SHELL_INTEGRATION_DIR
    # Do not leak our directory into child processes.
    set -l __dotty_ours "$DOTTY_SHELL_INTEGRATION_DIR/inject/xdg"
    set -l __dotty_kept
    for __dotty_dir in (string split -- : "$XDG_DATA_DIRS")
        if test "$__dotty_dir" != "$__dotty_ours"; and test -n "$__dotty_dir"
            set __dotty_kept $__dotty_kept $__dotty_dir
        end
    end
    if test (count $__dotty_kept) -gt 0
        set -gx XDG_DATA_DIRS (string join -- : $__dotty_kept)
    else
        set -e XDG_DATA_DIRS
    end
    if status is-interactive; and test -r "$DOTTY_SHELL_INTEGRATION_DIR/dotty.fish"
        source "$DOTTY_SHELL_INTEGRATION_DIR/dotty.fish"
    end
end
