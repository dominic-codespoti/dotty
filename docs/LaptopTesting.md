# Testing Dotty on a laptop

How to build, run and check this branch on an ordinary Linux, macOS or Windows machine with a display. The automated
suites run anywhere; the manual checklist at the end covers what CI and headless runs cannot see (real GPUs, HiDPI,
IME, fonts).

## 1. Prerequisites

- .NET SDK 11.0.100-rc.1 or later (`global.json` pins it and allows prerelease roll-forward).
- Python 3 and CMake on `PATH` (the build compiles the checked-in GLFW fork).
- A C toolchain:
  - **Linux:** `gcc` or `clang`, `make`, `pkg-config`, X11 and Wayland development headers, `libxkbcommon` headers,
    fontconfig. Debian/Ubuntu: `sudo apt install build-essential cmake pkg-config libx11-dev libxrandr-dev
    libxinerama-dev libxcursor-dev libxi-dev libwayland-dev wayland-protocols libxkbcommon-dev libfontconfig1`.
  - **macOS:** Xcode Command Line Tools (`xcode-select --install`) and CMake (`brew install cmake`).
  - **Windows:** Visual Studio 2022 C/C++ build tools with a Windows SDK, plus CMake and Python.
- OpenGL 3.3 core. In a VM without GPU acceleration on Linux, `LIBGL_ALWAYS_SOFTWARE=1` uses Mesa llvmpipe
  (see [Troubleshooting](Troubleshooting.md)).

## 2. Get the code

```bash
git clone https://github.com/dominic-codespoti/dotty.git
cd dotty
git switch feat/ghostty-parity   # until it is merged into main
```

## 3. Build and run every automated check

Linux and macOS:

```bash
scripts/dev/check.sh           # build + all test suites + packaging script tests, prints a summary
scripts/dev/check.sh --quick   # Terminal and App suites only
```

Windows (PowerShell):

```powershell
dotnet build Dotty.slnx -c Release
foreach ($p in 'Dotty.Terminal.Tests','Dotty.App.Tests','Dotty.App.SkiaTests','Dotty.NativePty.Tests') {
  dotnet test --project "tests/$p/$p.csproj" -c Release --no-build
}
```

On Linux and macOS the build also builds the POSIX `pty-helper` (`src/Dotty.NativePty/bin/pty-helper`); without a
C compiler it warns and the PTY tests fail, and `make -C src/Dotty.NativePty` builds it by hand.

What to expect: every suite passes. `Dotty.App.SkiaTests` contains GL tests that run only with a display (they skip
otherwise and say so). Linux tests that read a child's window size use `/proc/<pid>/task/<pid>/children`, which some
kernels (for example Android) do not provide.

## 4. Run it

```bash
dotnet run --project src/Dotty/Dotty.csproj -c Release
dotnet run --project src/Dotty/Dotty.csproj -c Release -- +diagnose   # versions, paths, PTY helper, display
```

Use a throwaway configuration so your own settings are not touched:

```bash
What to expect: every suite passes. `Dotty.App.SkiaTests` contains GL tests that run only with a display (they skip
otherwise and say so). Linux tests that read a child's window size use `/proc/<pid>/task/<pid>/children`, which some
kernels (for example Android) do not provide. On musl distributions (Alpine) the KeraLua package ships no native Lua,
so the Lua tests fail unless a Lua 5.4 library is copied in as `runtimes/linux-musl-<arch>/native/lua54.so`.

`+show-config`, `+validate-config` and `+explain-config <key>` print the effective configuration, check a file
with line/column errors, and explain any option.

## 5. Manual checklist

Tick what you tried and attach `dotty +diagnose` output plus OS, GPU and display scale to the report.

**Display and text**
- [ ] Text has the right physical size at 100 %, 125/150 % and 200 % display scaling, and re-scales when the window
      moves between monitors with different scales (Windows and X11 were wrong before).
- [ ] `printf '\e[4:1mone \e[4:2mtwo \e[4:3mcurly \e[4:4mdotted \e[4:5mdashed\e[0m\n'` shows five underline styles;
      `printf '\e[4:3;58;2;255;0;0mred curl\e[0m\n'` uses the red underline colour; `\e[9m` strikes through.
- [ ] Box drawing and blocks join without gaps: `printf '┌─┬─┐\n│ │ │\n├─┼─┤\n└─┴─┘\n█▀▄░▒▓\n⠿⡇\n'`; a Powerline
      prompt draws its separators without a Nerd Font.
- [ ] Emoji and sequences take two cells and are one glyph: 👨‍👩‍👧 🇺🇸 👍🏽 1️⃣; `é` written as `e` + U+0301 shows one
      accented letter.
- [ ] With a ligature font (`"font": { "family": "Fira Code, JetBrains Mono" }`), `-> != === =>` ligate, and the
      characters under the cursor separate (`font.ligatures` = `cursor`).
- [ ] Italic, bold, faint (`\e[2m`) and hidden (`\e[8m`) text; cursor shapes `\e[1 q` .. `\e[6 q`; hollow cursor when
      the window loses focus.
- [ ] `"window": { "opacity": 0.85 }` (restart) makes only the background translucent.

**Input**
- [ ] On a non-US layout (German, French, Spanish, Russian) AltGr characters (`@ € { } [ ] \`) type correctly;
      on macOS Option composes characters; Alt+Shift+`,` sends `ESC <` in readline.
- [ ] Selection stays on the same text while output streams in and after resizing; Alt+drag selects a block;
      Shift+click extends; triple-click selects a wrapped line.
- [ ] Ctrl+Shift+F search: ▲ ▼ × respond, Alt+R/C/W toggle regex/case/word, typing does not reach the shell.
- [ ] IME (fcitx/ibus, macOS input sources, Microsoft Pinyin/Japanese): preedit wraps at the right edge, the
      candidate window sits at the caret, commit inserts the text.
- [ ] Dropping a file pastes its shell-quoted path; dragging a split divider resizes panes.

**Graphics and protocols**
- [ ] With `"graphics": { "kittyProtocol": true }` (restart), `kitten icat image.png` shows the image and it scrolls with
      text; follow the full checklist in [Kitty graphics](KittyGraphics.md).
- [ ] tmux, vim/neovim, htop, and `less` behave as in other terminals; resizing reflows wrapped lines.

**Platform**
- [ ] Windows: launching the published `dotty.exe` from Explorer shows no console window; ConPTY passthrough is
      off by default (`graphics.windowsPassthrough`).
- [ ] The window icon and app id (`io.github.dominic_codespoti.Dotty`) match the desktop entry on Linux.

## 6. Reporting

Open an issue with: the checklist items that failed, steps, `dotty +diagnose`, and a screenshot. A crash writes a
report to the crash log path that `+diagnose` prints.

## Changelog

| Date | Change |
|---|---|
| 2026-11-01 | Added: laptop build, test and manual verification guide; `scripts/dev/check.sh`. |
