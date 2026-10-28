# Dotty shell integration for zsh. Explicit opt-in: source in the current shell.
# No startup files are read or changed by this script. Sourcing it more than once is a no-op.
[[ -o interactive ]] || return 0
[[ ${TERM-} == dumb ]] && return 0
(( ${+_dotty_zsh_installed} )) && return 0
autoload -Uz add-zsh-hook 2>/dev/null || return 0
_dotty_zsh_active=0
_dotty_zsh_osc() { printf '\033]%s\033\\' "$1"; }
_dotty_zsh_file_uri() {
  emulate -L zsh
  local LC_ALL=C input="$PWD" out='file://' byte code hex i
  for (( i=1; i<=${#input}; i++ )); do
    byte="$input[i]"
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
  _dotty_zsh_osc "7;$out"
}
_dotty_zsh_preexec() {
  (( _dotty_zsh_active )) && return
  _dotty_zsh_active=1
  _dotty_zsh_osc '133;B'
  _dotty_zsh_osc '133;C'
}
_dotty_zsh_precmd() {
  local exit_code=$?
  emulate -L zsh
  if (( _dotty_zsh_active )); then _dotty_zsh_osc "133;D;$exit_code"; _dotty_zsh_active=0; fi
  _dotty_zsh_osc '133;A'
  _dotty_zsh_file_uri
}
add-zsh-hook preexec _dotty_zsh_preexec
add-zsh-hook precmd _dotty_zsh_precmd
_dotty_zsh_installed=1
