# Dotty shell integration for fish. Explicit opt-in: source in the current shell.
# No startup files are read or changed by this script.
if functions -q fish_prompt
    functions --copy fish_prompt __dotty_original_fish_prompt
end
set -g __dotty_command_active 0
function __dotty_osc --argument-names payload
    printf '\e]%s\e\\' $payload
end
function fish_prompt
    if functions -q __dotty_original_fish_prompt
        __dotty_original_fish_prompt
    end
end
function __dotty_preexec --on-event fish_preexec
    set -g __dotty_command_active 1
    __dotty_osc '133;B'
    __dotty_osc '133;C'
end
function __dotty_postexec --on-event fish_postexec
    if test $__dotty_command_active -eq 1
        set -l result $status
        __dotty_osc "133;D;$result"
        __dotty_osc '133;A'
        set -l path (string escape --style=url -- "$PWD")
        __dotty_osc "7;file://$path"
        set -g __dotty_command_active 0
    end
end
__dotty_osc '133;A'
set -l path (string escape --style=url -- "$PWD")
__dotty_osc "7;file://$path"
