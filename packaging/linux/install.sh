#!/bin/sh
# User-local install of a Dotty Linux publish directory (no root needed).
#
#   install.sh [--prefix DIR] [--terminfo-dir DIR] [PUBLISH_DIR]
#   install.sh [--prefix DIR] [--terminfo-dir DIR] --uninstall
#
# Layout (PREFIX defaults to ~/.local):
#   PREFIX/lib/dotty/            the publish directory, unchanged (dotty finds its libraries beside itself)
#   PREFIX/bin/dotty             symlink to ../lib/dotty/dotty
#   PREFIX/share/applications/   desktop entry       } each only when the file is present
#   PREFIX/share/icons/hicolor/  icons               } in this checkout or the publish
#   PREFIX/share/man/man1/       dotty.1             } directory; missing ones are
#   PREFIX/share/{bash-completion,zsh,fish}/         } skipped with a note
#   TERMINFO_DIR (~/.terminfo)   xterm-dotty
# Every file placed outside PREFIX/lib/dotty is recorded in PREFIX/lib/dotty/.install-manifest,
# which --uninstall replays.
set -eu

APP_ID=io.github.dominic_codespoti.Dotty
script_dir=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd)
repo_root=$(CDPATH='' cd -- "$script_dir/../.." && pwd)
prefix=${HOME:?HOME is not set}/.local
terminfo_dir=$HOME/.terminfo
source_dir=
uninstall=0

usage() {
    sed -n '2,7p' "$0" | sed 's/^# \{0,1\}//'
}

die() {
    printf 'install.sh: %s\n' "$1" >&2
    exit "${2:-1}"
}

note() {
    printf '%s\n' "$1"
}

while [ $# -gt 0 ]; do
    case $1 in
        --prefix) [ $# -ge 2 ] || die "--prefix needs a directory" 2; prefix=$2; shift 2 ;;
        --terminfo-dir) [ $# -ge 2 ] || die "--terminfo-dir needs a directory" 2; terminfo_dir=$2; shift 2 ;;
        --uninstall) uninstall=1; shift ;;
        -h|--help) usage; exit 0 ;;
        -*) die "unknown option: $1" 2 ;;
        *) [ -z "$source_dir" ] || die "only one PUBLISH_DIR may be given" 2; source_dir=$1; shift ;;
    esac
done

case $prefix in
    /*) ;;
    *) die "--prefix must be an absolute path" 2 ;;
esac

lib_dir=$prefix/lib/dotty
bin_link=$prefix/bin/dotty
share=$prefix/share
manifest=$lib_dir/.install-manifest

do_uninstall() {
    [ -d "$lib_dir" ] || die "no Dotty install found in $lib_dir"
    if [ -f "$manifest" ]; then
        while IFS= read -r path; do
            [ -n "$path" ] || continue
            rm -f -- "$path"
            rmdir -- "$(dirname -- "$path")" 2>/dev/null || true
        done < "$manifest"
    fi
    if [ -L "$bin_link" ]; then
        rm -f -- "$bin_link"
    fi
    rm -rf -- "$lib_dir"
    note "Removed Dotty from $prefix"
}

# record PATH: remember an installed file for --uninstall
record() {
    printf '%s\n' "$1" >> "$manifest"
}

# place SRC DEST: copy one file and record it
place() {
    mkdir -p -- "$(dirname -- "$2")"
    cp -P -- "$1" "$2"
    record "$2"
}

# first_file CANDIDATE...: print the first existing regular file
first_file() {
    for candidate in "$@"; do
        if [ -f "$candidate" ]; then
            printf '%s\n' "$candidate"
            return 0
        fi
    done
    return 1
}

install_desktop() {
    desktop=$(first_file "$source_dir/share/applications/$APP_ID.desktop" "$script_dir/$APP_ID.desktop") || {
        note "skipped desktop entry (no $APP_ID.desktop)"
        return 0
    }
    dest=$share/applications/$APP_ID.desktop
    mkdir -p -- "$share/applications"
    # Point Exec/TryExec at the installed binary: ~/.local/bin is not always on a session PATH.
    sed -e "s|^Exec=dotty\$|Exec=$bin_link|" -e "s|^Exec=dotty |Exec=$bin_link |" \
        -e "s|^TryExec=dotty\$|TryExec=$bin_link|" "$desktop" > "$dest"
    record "$dest"
    if command -v update-desktop-database >/dev/null 2>&1; then
        update-desktop-database -q "$share/applications" 2>/dev/null || true
    fi
}

install_icons() {
    found=0
    for icons in "$source_dir/share/icons/hicolor" "$repo_root/assets/icons/hicolor"; do
        [ -d "$icons" ] || continue
        found=1
        (cd "$icons" && find . -type f) | while IFS= read -r rel; do
            place "$icons/$rel" "$share/icons/hicolor/${rel#./}"
        done
        break
    done
    if [ "$found" -eq 0 ]; then
        svg=$(first_file "$repo_root/assets/icons/$APP_ID.svg" "$repo_root/assets/icons/dotty.svg") || {
            note "skipped icons (no assets/icons)"
            return 0
        }
        place "$svg" "$share/icons/hicolor/scalable/apps/$APP_ID.svg"
    fi
    if command -v gtk-update-icon-cache >/dev/null 2>&1; then
        gtk-update-icon-cache -q -t "$share/icons/hicolor" 2>/dev/null || true
    fi
}

install_terminfo() {
    if [ -d "$source_dir/terminfo" ]; then
        (cd "$source_dir/terminfo" && find . ! -type d) | while IFS= read -r rel; do
            place "$source_dir/terminfo/$rel" "$terminfo_dir/${rel#./}"
        done
        return 0
    fi
    if [ -f "$repo_root/terminfo/xterm-dotty.src" ] && command -v tic >/dev/null 2>&1; then
        mkdir -p -- "$terminfo_dir"
        before=$(find "$terminfo_dir" ! -type d | sort)
        tic -x -o "$terminfo_dir" "$repo_root/terminfo/xterm-dotty.src"
        find "$terminfo_dir" ! -type d | sort | while IFS= read -r path; do
            printf '%s\n' "$before" | grep -Fxq -- "$path" || record "$path"
        done
        return 0
    fi
    note "skipped terminfo (no compiled terminfo/ and no tic)"
}

install_docs() {
    if man=$(first_file "$source_dir/share/man/man1/dotty.1" "$script_dir/dotty.1"); then
        place "$man" "$share/man/man1/dotty.1"
    else
        note "skipped man page"
    fi
    completions=$script_dir/completions
    [ -d "$source_dir/share/completions" ] && completions=$source_dir/share/completions
    if [ -f "$completions/dotty.bash" ]; then
        place "$completions/dotty.bash" "$share/bash-completion/completions/dotty"
    fi
    if [ -f "$completions/_dotty" ]; then
        place "$completions/_dotty" "$share/zsh/site-functions/_dotty"
    fi
    if [ -f "$completions/dotty.fish" ]; then
        place "$completions/dotty.fish" "$share/fish/vendor_completions.d/dotty.fish"
    fi
}

do_install() {
    if [ -z "$source_dir" ]; then
        if [ -x "$script_dir/dotty" ]; then
            source_dir=$script_dir
        else
            die "give the publish directory (the one containing the dotty executable)" 2
        fi
    fi
    [ -d "$source_dir" ] || die "$source_dir is not a directory" 2
    source_dir=$(CDPATH='' cd -- "$source_dir" && pwd)
    [ -x "$source_dir/dotty" ] || die "$source_dir/dotty is missing or not executable" 2

    if [ -d "$lib_dir" ]; then
        note "Replacing the existing install in $lib_dir"
        do_uninstall >/dev/null
    fi
    if [ -e "$bin_link" ] && [ ! -L "$bin_link" ]; then
        die "$bin_link exists and is not a symlink; refusing to replace it"
    fi

    mkdir -p -- "$lib_dir" "$prefix/bin"
    cp -R -- "$source_dir/." "$lib_dir/"
    rm -f -- "$manifest"
    : > "$manifest"
    ln -sf -- ../lib/dotty/dotty "$bin_link"

    install_desktop
    install_icons
    install_terminfo
    install_docs

    note "Installed Dotty to $lib_dir"
    case :${PATH:-}: in
        *:"$prefix/bin":*) ;;
        *) note "Add $prefix/bin to PATH to run 'dotty' from a shell." ;;
    esac
}

if [ "$uninstall" -eq 1 ]; then
    [ -z "$source_dir" ] || die "--uninstall takes no PUBLISH_DIR" 2
    do_uninstall
else
    do_install
fi
