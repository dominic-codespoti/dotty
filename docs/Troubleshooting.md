# Troubleshooting

Start with `dotty +diagnose --gl`: it prints the version, platform, config and
state paths, config status, the `pty-helper` and shell-integration locations,
the chosen `TERM`, and the result of an OpenGL probe, without opening a
terminal window ([Command-Line Usage](CommandLine.md#diagnose)). Include its
output in bug reports. Platform-specific notes are in
[Platform Support](PlatformSupport.md#troubleshooting).

## GPU requirements

Dotty renders every frame with OpenGL and requests an **OpenGL 3.3 core
profile** context on all platforms. There is no software renderer inside
Dotty; without a 3.3 core context the window cannot be created. Any GPU and
driver from roughly the last decade provides it (Intel HD 2000+, AMD
GCN/TeraScale 2 with Mesa, NVIDIA Fermi+), as do current Mesa software
drivers.

## OpenGL 3.3 is not available

Symptoms: Dotty exits at startup with an OpenGL/GLFW context error, or
`dotty +diagnose --gl` reports that the probe failed or found a version below
3.3.

1. Check what the system offers (Linux): `glxinfo -B` (package
   `mesa-utils`) and look at "Max core profile version". Under Wayland,
   `eglinfo` (`mesa-utils-bin`/`egl-utils`) reports the same for EGL.
2. Update the graphics driver, or install the Mesa drivers for the GPU
   (`mesa-dri-drivers` / `libgl1-mesa-dri`).
3. Make sure the process can reach the display: `DISPLAY` (X11) or
   `WAYLAND_DISPLAY` (Wayland) must be set, which is not the case in SSH
   sessions without forwarding, containers, or system services.

### Mesa llvmpipe workaround (Linux)

Virtual machines, remote desktops and machines without a working GPU driver
can still run Dotty on Mesa's **llvmpipe** CPU rasteriser, which implements
OpenGL 4.5 core:

```sh
LIBGL_ALWAYS_SOFTWARE=1 dotty
```

`LIBGL_ALWAYS_SOFTWARE=1` makes Mesa skip the hardware driver;
`GALLIUM_DRIVER=llvmpipe` selects llvmpipe explicitly when several software
drivers are installed. llvmpipe draws on the CPU, so rendering competes with
other work for CPU time (Dotty has no llvmpipe measurements). The CI GUI smoke
test runs under Xvfb, which has no GPU, so it also renders on software Mesa
([Platform Support](PlatformSupport.md#desktop-presentation-and-native-verification)). To make it permanent for the
desktop entry, change its `Exec=` line to
`env LIBGL_ALWAYS_SOFTWARE=1 /path/to/dotty`.

Do not use `MESA_GL_VERSION_OVERRIDE=3.3` to force a driver that reports a
lower version: features the driver lacks fail at draw time instead of at
startup.

## The terminal starts but the shell does not

`dotty +diagnose` shows where `pty-helper` was found. "not found; probed …" means the
publish directory was split: keep `pty-helper` beside the `dotty` executable,
or point `DOTTY_PTY_HELPER` at it. See [Native PTY](NativePty.md).

## Programs complain about an unknown terminal type

With `term = "xterm-dotty"` Dotty falls back to `xterm-256color` when no
terminfo directory has the entry. Install it with the packages or
`install.sh` (see [Install](Install.md)), or copy it to a remote host with
`infocmp -x xterm-dotty | ssh host tic -x -`.

## Shell completions or the man page are missing after `install.sh`

The installer places them under `~/.local/share`. bash-completion and fish
search there by default; zsh needs `~/.local/share/zsh/site-functions` on
`fpath` before `compinit`; `man` finds `~/.local/share/man` when `~/.local/bin`
is on `PATH`.

## Changelog

| Date | Change |
|------|--------|
| 2026-11-01 | W4-C23: first version: GPU requirements, OpenGL 3.3 diagnosis, Mesa llvmpipe workaround, helper/terminfo/completion problems. |
