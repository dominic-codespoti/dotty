#!/usr/bin/env bash
# Verify a Dotty Unix publish directory (linux-* or osx-*): required files are
# present and the published executable starts, reports the expected version and
# answers +explain-config. Windows output is verified by verify-publish.ps1.
#
# Every check runs; all failures are reported before the script exits non-zero.
set -euo pipefail

usage() {
  cat <<'EOF'
usage: verify-publish.sh --rid RID --dir PUBLISH_DIR --version VERSION [--version-mode exact|dev]

  --rid           linux-x64, linux-arm64, osx-x64 or osx-arm64
  --dir           dotnet publish output directory
  --version       expected version (the release version or the central MSBuild Version)
  --version-mode  exact: `dotty --version` must equal VERSION (release builds; default)
                  dev:   VERSION, VERSION-dev+<rev> or VERSION.dev+<rev> are accepted (CI builds)
EOF
}

rid=""
dir=""
version=""
version_mode="exact"
while [ "$#" -gt 0 ]; do
  case "$1" in
    --rid) rid="${2:-}"; shift 2 ;;
    --dir) dir="${2:-}"; shift 2 ;;
    --version) version="${2:-}"; shift 2 ;;
    --version-mode) version_mode="${2:-}"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "verify-publish: unknown argument '$1'" >&2; usage >&2; exit 2 ;;
  esac
done

if [ -z "$rid" ] || [ -z "$dir" ] || [ -z "$version" ]; then
  usage >&2
  exit 2
fi
case "$version_mode" in
  exact|dev) ;;
  *) echo "verify-publish: --version-mode must be exact or dev" >&2; exit 2 ;;
esac
case "$rid" in
  linux-*) native_suffix="so"; glfw="libglfw.so.3" ;;
  osx-*) native_suffix="dylib"; glfw="libglfw.3.dylib" ;;
  *) echo "verify-publish: unsupported RID '$rid' (use verify-publish.ps1 for Windows)" >&2; exit 2 ;;
esac
if [ ! -d "$dir" ]; then
  echo "verify-publish: publish directory '$dir' does not exist" >&2
  exit 1
fi

failures=0
fail() {
  echo "FAIL: $*" >&2
  failures=$((failures + 1))
}
require_file() {
  [ -f "$dir/$1" ] || fail "missing file $1"
}
require_executable() {
  if [ ! -f "$dir/$1" ]; then
    fail "missing file $1"
  elif [ ! -x "$dir/$1" ]; then
    fail "$1 is not executable"
  fi
}

require_executable dotty
require_executable pty-helper
require_file "$glfw"
require_file "libSkiaSharp.$native_suffix"
require_file "liblua54.$native_suffix"
require_file shell-integration/dotty.bash
require_file shell-integration/inject/bash/dotty-rcfile.bash
require_file shell-integration/inject/zsh/.zshenv
require_file shell-integration/inject/xdg/fish/vendor_conf.d/dotty-shell-integration.fish
require_file License.md
require_file THIRD-PARTY-NOTICES.md
require_file licenses/GLFW-LICENSE.md

if [ -x "$dir/dotty" ]; then
  # Isolate the run from the runner's real configuration directories.
  sandbox="$(mktemp -d)"
  trap 'rm -rf "$sandbox"' EXIT
  mkdir -p "$sandbox/home" "$sandbox/config"
  run_dotty() {
    env HOME="$sandbox/home" DOTNET_CLI_HOME="$sandbox/home" \
      XDG_CONFIG_HOME="$sandbox/config" DOTTY_CONFIG_HOME="$sandbox/config/dotty" \
      "$dir/dotty" "$@"
  }

  if actual_version="$(run_dotty --version 2>"$sandbox/version.err")"; then
    actual_version="${actual_version%$'\r'}"
    case "$version_mode" in
      exact)
        [ "$actual_version" = "$version" ] ||
          fail "dotty --version printed '$actual_version', expected exactly '$version'"
        ;;
      dev)
        case "$actual_version" in
          "$version"|"$version"-dev+?*|"$version".dev+?*) ;;
          *) fail "dotty --version printed '$actual_version', expected '$version' or a '$version-dev+<rev>'/'$version.dev+<rev>' build" ;;
        esac
        ;;
    esac
  else
    fail "dotty --version exited with status $?: $(cat "$sandbox/version.err")"
  fi

  if explain="$(run_dotty +explain-config font.size 2>"$sandbox/explain.err")"; then
    case "$explain" in
      *font.size*) ;;
      *) fail "dotty +explain-config font.size did not document font.size: '$explain'" ;;
    esac
  else
    fail "dotty +explain-config font.size exited with status $?: $(cat "$sandbox/explain.err")"
  fi
fi

if [ "$failures" -ne 0 ]; then
  echo "verify-publish: $failures check(s) failed for $rid in $dir" >&2
  exit 1
fi
echo "verify-publish: $rid publish output in $dir passed"
