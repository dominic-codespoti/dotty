# Dotty shell integration for zsh. Explicit opt-in: source in the current shell.
# No startup files are read or changed by this script.
autoload -Uz add-zsh-hook
_dotty_zsh_active=0
_dotty_zsh_osc() { printf '\033]%s\033\\' "$1"; }
_dotty_zsh_file_uri() {
  local LC_ALL=C input="$PWD" out='file://' byte hex i
  for (( i=1; i<=${#input}; i++ )); do
    byte="$input[i]"
    case $byte in [a-zA-Z0-9/._~-]) out+="$byte" ;; *) printf -v hex '%%%02X' "'$byte"; out+=$hex ;; esac
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
  if (( _dotty_zsh_active )); then _dotty_zsh_osc "133;D;$exit_code"; _dotty_zsh_active=0; fi
  _dotty_zsh_osc '133;A'
  _dotty_zsh_file_uri
}
add-zsh-hook preexec _dotty_zsh_preexec
add-zsh-hook precmd _dotty_zsh_precmd
