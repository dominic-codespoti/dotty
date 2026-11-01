# Installing Dotty

Dotty ships as a self-contained NativeAOT build: one directory holding the
`dotty` executable, its native libraries (`libglfw`, `libSkiaSharp`,
`liblua54`), `pty-helper`, the compiled `xterm-dotty` terminfo entry, shell
integration scripts and license files. No .NET runtime is needed. Hardware
and OS requirements are in [Platform Support](PlatformSupport.md); the GPU
needs OpenGL 3.3 core (see [Troubleshooting](Troubleshooting.md#opengl-33-is-not-available)).

## Linux

### From a release archive (user-local, no root)

```sh
tar -xzf dotty-<version>-linux-x64.tar.gz
git clone --depth 1 https://github.com/dominic-codespoti/dotty   # for packaging/linux
sh dotty/packaging/linux/install.sh dotty-<version>-linux-x64
```

`packaging/linux/install.sh` copies the publish directory unchanged to
`~/.local/lib/dotty` and links `~/.local/bin/dotty` to it. It also installs,
each only when the file exists in the checkout or the publish directory:

| File | Installed to |
|---|---|
| `packaging/linux/io.github.dominic_codespoti.Dotty.desktop` | `~/.local/share/applications/` (its `Exec=`/`TryExec=` rewritten to the installed binary) |
| `assets/icons/hicolor/**` (or a single `assets/icons/*.svg`) | `~/.local/share/icons/hicolor/` |
| `terminfo/` of the publish directory (or `tic` of `terminfo/xterm-dotty.src`) | `~/.terminfo` (`--terminfo-dir` overrides) |
| `packaging/linux/dotty.1` | `~/.local/share/man/man1/` |
| `packaging/linux/completions/dotty.bash`, `_dotty`, `dotty.fish` | `~/.local/share/bash-completion/completions/`, `zsh/site-functions/`, `fish/vendor_completions.d/` |

Options: `--prefix DIR` (absolute; default `~/.local`), `--terminfo-dir DIR`,
`--uninstall`. Running it again replaces the previous install. Every file
placed outside `PREFIX/lib/dotty` is listed in
`PREFIX/lib/dotty/.install-manifest`, which `--uninstall` removes before
deleting `PREFIX/lib/dotty` and the `bin/dotty` symlink. The script refuses
to overwrite a `bin/dotty` that is not a symlink.

Add `~/.local/bin` to `PATH` if your shell does not already include it. zsh
picks up `~/.local/share/zsh/site-functions` only when it is on `fpath`:

```sh
fpath=(~/.local/share/zsh/site-functions $fpath)   # before compinit
```

### Debian/Ubuntu and Fedora packages

`.deb` and `.rpm` packages are built with [nfpm](https://nfpm.goreleaser.com)
from `packaging/linux/nfpm.yaml` (see [Packaging](Packaging.md)). They install
the same directory to `/usr/lib/dotty` with `/usr/bin/dotty` as a symlink, plus
the system terminfo entry, man page, completions, desktop entry and icons.
No package is published yet; build one locally as described in Packaging.

### From source

See [Contributing](../Contributing.md) for the SDK setup, then publish and
install:

```sh
make -C src/Dotty.NativePty
dotnet publish src/Dotty/Dotty.csproj -c Release -r linux-x64 --self-contained true -o publish/linux-x64
sh packaging/linux/install.sh publish/linux-x64
```

A local publish has no compiled `terminfo/` directory; the script then runs
`tic` on `terminfo/xterm-dotty.src` when `tic` is installed.

## macOS and Windows

Unpack the release archive and run `dotty` (`dotty.exe`) from the unpacked
directory; keep the files together. The builds are not signed with an Apple
Developer ID or Authenticode certificate (see [Release Policy](Releasing.md)).

## Verifying an install

```sh
dotty --version
dotty +diagnose --gl     # paths, helper locations and an OpenGL probe
man dotty
```

## Changelog

| Date | Change |
|------|--------|
| 2026-11-01 | W4-C19/C23: first version: user-local `install.sh`, nfpm packages, generated man page and completions. |
