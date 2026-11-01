# Packaging

How the distributable artifacts are produced and checked. The release
workflow itself is described in [Release Policy](Releasing.md); end-user
installation in [Install](Install.md).

## Layout

Every platform ships the flat NativeAOT publish directory. `dotty` finds
`pty-helper`, shell integration and the bundled `terminfo/` through
`AppContext.BaseDirectory`, which is the directory of the resolved executable:
started through a symlink in another directory, `dotty +diagnose` still reports
the helper `BesideExecutable` in the real directory. The directory must stay
together; Linux packages and `install.sh` therefore copy it whole to
`lib/dotty` and expose it through a `bin/dotty` symlink instead of splitting
files across `bin`/`lib`.

## Linux packages (`packaging/linux/`)

| File | Purpose |
|---|---|
| `install.sh` | POSIX `sh` user-local installer and uninstaller (see [Install](Install.md#from-a-release-archive-user-local-no-root)) |
| `nfpm.yaml` | `.deb`/`.rpm` definition: publish dir → `/usr/lib/dotty`, `/usr/bin/dotty` symlink, `/usr/share/terminfo/x/xterm-dotty`, man page, completions, desktop entry, hicolor icons, license files under `/usr/share/doc/dotty` |
| `dotty.1`, `completions/` | Generated; do not edit (below) |
| `io.github.dominic_codespoti.Dotty.desktop` | Desktop entry; `install.sh` skips it when absent, `nfpm` requires it |

Build a package from the repository root after an AOT publish whose
`terminfo/` directory is populated (the `aot-publish` workflow downloads it
into the publish directory):

```sh
DOTTY_VERSION=0.3.0 DOTTY_ARCH=amd64 DOTTY_PUBLISH_DIR=publish/linux-x64 \
  nfpm package -f packaging/linux/nfpm.yaml -p deb -t dist/
```

`DOTTY_ARCH` takes Go architecture names (`amd64`, `arm64`). Runtime
dependencies are declared per packager in `nfpm.yaml` (fontconfig, libGL,
xkbcommon; Wayland and X11 client libraries as recommendations). The linux-x64
build links against glibc 2.31 (see `scripts/dist/baseline-targets.json`).
Package building is not yet wired into a workflow, and no package has been
installed on a real distribution in CI.

## Generated man page and completions

`scripts/dist/gen_packaging.py` writes `packaging/linux/dotty.1` and the bash,
zsh and fish completions. It parses, without building:

- `LaunchOptions.HelpText` in `src/Dotty/LaunchOptions.cs` for the usage lines,
  options (`-d/--working-directory DIR` completes directories, `--shell PATH`
  files) and `+actions` with their flags;
- the `Opt(...)` entries of `ConfigOptionCatalog.CreateLive()` for the
  CONFIGURATION section of the man page and the `+explain-config` argument
  completion. Descriptions must stay C# string literals (or `+`
  concatenations of them); anything else fails generation.

After changing either source run `python3 scripts/dist/gen_packaging.py` and
commit the outputs. CI (`code-quality` job) runs it with `--check`, which
fails when an output is missing or stale.

## Third-party notices and NuGet inventory

`THIRD-PARTY-NOTICES.md` has a "NuGet package inventory" table listing every
package restored for `src/Dotty` with its version, the host OSes whose restore
includes it, and the component row that carries its license.
`scripts/dist/check_notices.py` (CI `code-quality`, after `dotnet restore`)
compares it with `src/Dotty/obj/project.assets.json` and fails on a package
missing from the table, a version mismatch, a row that the restore no longer
contains, or a component name absent from the Components table. NativeAOT
build tools (`Microsoft.DotNet.ILCompiler`, its runtime pack and
`Microsoft.NET.ILLink.Tasks`) are excluded because nothing from them ships as
a package. The check covers NuGet packages only: native code inside
`libSkiaSharp` and the vendored GLFW are maintained by hand in the Components
table.

## Size and start-time measurement

`scripts/dist/measure.py --publish-dir DIR --rid RID` prints one JSON
document: per-file sizes, total unpacked bytes, the size of a reproducible
`.tar.gz` of the directory, the median/min/max wall time of `dotty --version`
over `--runs` runs (default 11), the peak RSS of one `--version` run on Unix,
and, with `--idle-rss SECONDS` on Linux with a display, the `VmRSS` of a
launched window after that delay. `--summary` appends a Markdown table to a
file such as `$GITHUB_STEP_SUMMARY`.

The `aot-publish` workflow measures every rid and uploads
`dist/measure-<rid>.json` with the CI artifact. `--budgets
scripts/dist/size-budgets.json` fails the step when a metric exceeds the
budget of that rid; a rid without an entry is only reported. Budgets are
written only from a recorded run, kept with that run's numbers under
`recorded`. Recorded so far:

| rid | Unpacked | Archive | `--version` median | `--version` peak RSS | Host |
|---|---|---|---|---|---|
| linux-musl-arm64 | 21,131,314 B | 8,508,767 B | 84.54 ms | 13,308 KiB | Alpine aarch64 on Android, shared with other jobs; no terminfo directory |

The timing and RSS figures come from a single shared machine and are not
budgeted; the size budgets are the recorded size plus 10 %. CI rids
(linux-x64, osx-*, win-x64) have no budget until a CI run is recorded.

## Changelog

| Date | Change |
|------|--------|
| 2026-11-01 | W4-C19/C20/C23: first version: Linux `install.sh`/nfpm, generated man page and completions, notices inventory check, `measure.py` and size budgets. |
