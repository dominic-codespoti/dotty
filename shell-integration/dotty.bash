# Dotty shell integration for Bash. Explicit opt-in: source this file from the current shell.
# No startup files are read or changed by this script. Sourcing it more than once is a no-op.
# Bash 3.2 compatible (macOS /bin/bash): no bash-4-only expansions.
[[ $- == *i* ]] || return 0 2>/dev/null
[[ ${TERM-} == dumb ]] && return 0 2>/dev/null
[[ -n ${_dotty_bash_installed-} ]] && return 0 2>/dev/null
_dotty_bash_active=0
_dotty_bash_exit_status=0
_dotty_bash_prompt_running=0
_dotty_bash_old_debug_trap=$(trap -p DEBUG)
_dotty_bash_old_debug_command=
_dotty_bash_read_old_debug_command() {
    eval "set -- $_dotty_bash_old_debug_trap"
    printf '%s' "$3"
}
if [[ -n $_dotty_bash_old_debug_trap ]]; then
    _dotty_bash_old_debug_command=$(_dotty_bash_read_old_debug_command)
    # A leftover Dotty hook (e.g. inherited trap text) must never be chained to itself.
    [[ $_dotty_bash_old_debug_command == _dotty_bash_debug ]] && _dotty_bash_old_debug_command=
fi
_dotty_bash_old_prompt_command=
_dotty_bash_old_prompt_hooks=()
if [[ $(declare -p PROMPT_COMMAND 2>/dev/null) == 'declare -a'* ]]; then
    for _dotty_bash_hook in "${PROMPT_COMMAND[@]}"; do
        # An exported PROMPT_COMMAND can already name our hook in a nested shell; drop it so
        # the hook never evals itself.
        [[ $_dotty_bash_hook == _dotty_bash_prompt ]] || _dotty_bash_old_prompt_hooks+=("$_dotty_bash_hook")
    done
    unset _dotty_bash_hook
else
    _dotty_bash_old_prompt_command=${PROMPT_COMMAND-}
    [[ $_dotty_bash_old_prompt_command == _dotty_bash_prompt ]] && _dotty_bash_old_prompt_command=
fi
_dotty_bash_osc() { printf '\033]%s\033\\' "$1"; }
_dotty_bash_file_uri() {
    local LC_ALL=C input="$PWD" out='file://' byte code hex i
    for ((i=0; i<${#input}; i++)); do
        byte="${input:i:1}"
        case $byte in
            [a-zA-Z0-9/._~-]) out+="$byte" ;;
            *)
                # printf may sign-extend bytes >= 0x80 (e.g. FFFFFFC3); keep the low byte only.
                printf -v code '%d' "'$byte"
                printf -v hex '%%%02X' $(( code & 255 ))
                out+=$hex
                ;;
        esac
    done
    _dotty_bash_osc "7;$out"
}
_dotty_bash_debug() {
    local status=$?
    if (( _dotty_bash_active )); then
        _dotty_bash_exit_status=$status
    elif (( !_dotty_bash_prompt_running )) && [[ $BASH_COMMAND != _dotty_bash_prompt ]]; then
        _dotty_bash_active=1
        _dotty_bash_osc '133;B'
        _dotty_bash_osc '133;C'
    fi
    if [[ -n $_dotty_bash_old_debug_command ]]; then eval "$_dotty_bash_old_debug_command"; fi
}
_dotty_bash_prompt() {
    _dotty_bash_prompt_running=1
    if (( _dotty_bash_active )); then _dotty_bash_osc "133;D;$_dotty_bash_exit_status"; _dotty_bash_active=0; fi
    _dotty_bash_osc '133;A'
    _dotty_bash_file_uri
    local hook
    if (( ${#_dotty_bash_old_prompt_hooks[@]} )); then
        for hook in "${_dotty_bash_old_prompt_hooks[@]}"; do eval "$hook"; done
    elif [[ -n $_dotty_bash_old_prompt_command ]]; then
        eval "$_dotty_bash_old_prompt_command"
    fi
    _dotty_bash_prompt_running=0
}
trap '_dotty_bash_debug' DEBUG
PROMPT_COMMAND=_dotty_bash_prompt
_dotty_bash_installed=1
