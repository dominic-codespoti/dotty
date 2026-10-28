# Dotty shell-integration injection wrapper for bash (passed as: bash --rcfile <this file> -i).
# `--rcfile` replaces the normal startup files, so this reproduces what a non-login interactive
# bash would read (/etc/bash.bashrc where the distro patches it in, then ~/.bashrc) and only then
# loads the Dotty script. Nothing here may stop the shell from starting.
[ -r /etc/bash.bashrc ] && . /etc/bash.bashrc
[ -r "$HOME/.bashrc" ] && . "$HOME/.bashrc"
if [ -n "${DOTTY_SHELL_INTEGRATION_DIR-}" ] && [ -r "$DOTTY_SHELL_INTEGRATION_DIR/dotty.bash" ]; then
    . "$DOTTY_SHELL_INTEGRATION_DIR/dotty.bash"
fi
:
