# Dotty shell-integration injection shim for zsh. Dotty points ZDOTDIR at this directory only for
# the launch; this file restores the user's ZDOTDIR first, sources the user's own .zshenv, and
# schedules the Dotty script to load from the first prompt so its hooks come after every rc file.
# Nothing here may stop the shell from starting.
if (( ${+DOTTY_ZSH_ZDOTDIR} )); then
  ZDOTDIR=$DOTTY_ZSH_ZDOTDIR
  unset DOTTY_ZSH_ZDOTDIR
else
  unset ZDOTDIR
fi
# zsh reads .zprofile/.zshrc/.zlogin from the (now restored) ZDOTDIR, i.e. the user's own files.
if [[ -r ${ZDOTDIR:-$HOME}/.zshenv ]]; then
  builtin source -- ${ZDOTDIR:-$HOME}/.zshenv
fi
if [[ -o interactive && -n ${DOTTY_SHELL_INTEGRATION_DIR-} && -r ${DOTTY_SHELL_INTEGRATION_DIR-}/dotty.zsh ]]; then
  _dotty_zsh_inject_once() {
    precmd_functions=( ${precmd_functions:#_dotty_zsh_inject_once} )
    unfunction _dotty_zsh_inject_once 2>/dev/null
    builtin source -- "$DOTTY_SHELL_INTEGRATION_DIR/dotty.zsh" 2>/dev/null
    # The hook just registered may not run in this cycle; emit the first prompt marks now.
    (( ${+functions[_dotty_zsh_precmd]} )) && _dotty_zsh_precmd
    return 0
  }
  precmd_functions=( _dotty_zsh_inject_once $precmd_functions )
fi
