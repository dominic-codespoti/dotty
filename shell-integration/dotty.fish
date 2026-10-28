# Dotty shell integration for fish. Explicit opt-in: source in the current shell.
# No startup files are read or changed by this script. Sourcing it more than once is a no-op.
if status is-interactive; and not set -q __dotty_installed; and test "$TERM" != dumb
    set -g __dotty_installed 1
    set -g __dotty_command_active 0
    function __dotty_osc --argument-names payload
        printf '\e]%s\e\\' $payload
    end
    # Capture the exit status before running anything else; every command resets $status.
    function __dotty_postexec --on-event fish_postexec
        set -l result $status
        if test "$__dotty_command_active" = 1
            __dotty_osc "133;D;$result"
            set -g __dotty_command_active 0
        end
    end
    function __dotty_preexec --on-event fish_preexec
        set -g __dotty_command_active 1
        __dotty_osc '133;B'
        __dotty_osc '133;C'
    end
    # fish_prompt fires before every prompt, including the first and after an empty Enter.
    function __dotty_prompt --on-event fish_prompt
        __dotty_osc '133;A'
        set -l path (string escape --style=url -- "$PWD")
        __dotty_osc "7;file://$path"
    end
end
