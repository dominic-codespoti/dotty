# Release policy

Dotty uses explicit, immutable version tags for published builds. A commit on the default branch is not a release request: normal CI may run, but a release build and GitHub release happen only when an authorized maintainer explicitly requests one by pushing a version tag or manually dispatching the release workflow against that exact tag ref.

## Version source and current state

The single authoritative application/package version is <Version> in Directory.Build.props. It is currently **0.3.0**. MSBuild derives assembly, file, and informational versions from this value; do not add separate version overrides or bump it as part of ordinary development. For a stable release, the v-prefixed tag's X.Y.Z must equal the central version. For a prerelease, its complete SemVer—including prerelease suffix, such as 0.4.0-beta.1—must equal the evaluated central version. The workflow validates the full version match and passes that version through to the artifacts; its release build suppresses source-revision metadata so dotty --version reports the exact release SemVer.

There is no version bump or release implied by a merge. First decide and authorize the release, then update the central version, verify the resulting versioned binaries, and push the matching tag (or explicitly dispatch against that tag). Never move or reuse a published version tag.

## Release forms and trigger

- Stable releases use immutable tags of the form vX.Y.Z (for example, v0.4.0).
- Prereleases use immutable SemVer tags with prerelease identifiers, such as v0.4.0-beta.1 or v0.4.0-rc.1.
- The release workflow runs only for a version-tag push or explicit manual dispatch. Dispatch must target that exact tag ref and provide the required version input exactly matching the tag name (including v and any prerelease suffix). Ordinary branch pushes do not publish releases.
- The workflow validates SemVer and equality with the evaluated central version, builds the configured release targets, verifies archives/manifests/checksums, and verifies the built executable reports the expected version on its native build OS before publication.
- Keep ordinary CI on branch pushes and pull requests. Do not restore a moving nightly publisher or a second preview workflow; immutable beta/rc tags are the prerelease path.

A legacy GitHub prerelease at the moving nightly tag was published by the removed automatic-nightly workflow. It is historical and will no longer update. It is intentionally not deleted or remotely rewritten; its contents may be stale and should not be presented as current release guidance.

## Release-advisor procedure

For every release recommendation, inspect the current central version, the latest version tag, and all relevant commits and diffs since that tag. Assess user-visible effects, compatibility and contract changes, reliability, performance evidence, and security impact. State what changed, the proposed bump and why, and whether the readiness gates below are met. Do not infer a release from a milestone label or from the existence of a workflow.

Use these bump rules:

| Change | Recommendation |
|---|---|
| Backward-compatible bug fix | Patch bump |
| Backward-compatible user-facing feature | Minor bump |
| Breaking change before 1.0 | Minor bump with clear migration/compatibility notes |
| Breaking change after 1.0 | Major bump |
| In-progress candidate needing user evaluation | Prerelease identifier (beta/rc) on the intended version |
| Internal-only refactor, documentation, or tests with no meaningful user-visible effect | Usually no release |
| User-visible stability or performance improvement | Consider a release when supported by credible before/after or regression evidence |

### Current recommendation

- **Current central version:** 0.3.0.
- **Latest version tag:** must be checked against the repository before each recommendation; do not assume it from this document.
- **Candidate changes:** the 74-file keyboard/CLI/clipboard/shell milestone.
- **Proposed candidate:** **0.4.0-beta.1**, as an initial evaluation candidate, not a 1.0 release. The sample/default version alone is not evidence that the product is ready for 1.0.
- **Readiness:** candidate only, not authorized for release by this policy. Before recommending execution, confirm tag/version consistency, relevant CI and release gates, target-platform smoke coverage, archive/manifest/checksum verification, and that the native-OS dotty --version output exactly matches the full stable/prerelease SemVer. Identify any unavailable evidence or remaining gate explicitly.

A recommendation is advice, not authorization. The assistant MUST report the current version, latest tag, reviewed changes, proposed bump, and readiness/gaps, then wait for explicit user authorization before changing the central version, pushing a tag, manually dispatching publication, or otherwise publishing. A request to analyze or recommend a release is not permission to execute one.

## NativeAOT publish and archive assertions

Release archives are built by the reusable workflow `.github/workflows/aot-publish.yml`, the same one `ci.yml` runs on every push and pull request (job `aot-publish`). It NativeAOT-publishes `linux-x64` (inside a `debian:bullseye` container, see [Platform baselines](#platform-baselines)), `win-x64`, `osx-arm64` and `osx-x64` on native runners (`PublishAot=true` comes from `Dotty.csproj`; there is no single-file or JIT release path), so every push and pull request exercises the publish path a release uses. Per RID it:

1. fails when ILC or the trim/AOT analyzers report an IL2xxx/IL3xxx warning in Dotty's own code (`scripts/dist/aot_warnings.py`: origin under `src/`, or a `Dotty.*` member or assembly). Third-party warnings are tolerated and listed in the job summary. The Silk.NET suppressions in `Dotty.csproj` (`IL2104`, `IL3000`, `IL3002`, ILC only) still apply.
2. verifies the publish directory with `scripts/dist/verify-publish.sh` (Linux, macOS) or `scripts/dist/verify-publish.ps1` (Windows), which report every failed check before exiting non-zero. In CI the version check accepts the central version or a `<Version>-dev+<rev>`/`<Version>.dev+<rev>` build; a release requires the exact release version.
3. measures the platform baseline with `scripts/dist/check_baseline.py` and fails when it exceeds `scripts/dist/baseline-targets.json`.
4. for a release only, packs `dotty-<version>-<rid>.tar.gz` (Unix, one top-level directory) or `dotty-<version>-win-x64.zip` and uploads it with `baselines-<rid>.json` (artifact retention 7 days).

`release.yml` fails the build when a published archive lacks the shell-integration payload or the license payload. The injection files are required in addition to the opt-in scripts because automatic injection (`shellIntegration: "detect"`) silently does nothing without them, and `inject/zsh/.zshenv` is a dotfile that a plain `*` glob would drop (`Dotty.csproj` packages `shell-integration/**/*` recursively for this reason). The license payload is `License.md` (Dotty, MIT), `THIRD-PARTY-NOTICES.md` (every shipped third-party component with its license text) and `licenses/GLFW-LICENSE.md` (the pinned GLFW fork's zlib/libpng license, copied from `vendor/glfw/source/LICENSE.md`); `Dotty.csproj` adds all three to the publish output. When a dependency is added or upgraded, update `THIRD-PARTY-NOTICES.md` in the same change.

Linux and macOS archives also carry the compiled `xterm-dotty` terminfo entry under `terminfo/`. The `terminfo` job of `aot-publish.yml` compiles `terminfo/xterm-dotty.src` once on Ubuntu with `tic -x`, checks it with `infocmp -x`, and ships it in both ncurses directory layouts, `terminfo/x/` (first letter, Linux) and `terminfo/78/` (hex, macOS), so macOS's own older `tic` is not involved. How Dotty selects and finds the entry (the `term` key) is described in [Configuration](Configuration.md#term); the advertised capabilities in [Keyboard protocol](KeyboardProtocol.md).

| Where | Asserted |
|---|---|
| Linux and macOS publish output (`verify-publish.sh`) | executable `dotty` and `pty-helper`; `libglfw.so.3`/`libglfw.3.dylib`, `libSkiaSharp`, `liblua54`; `shell-integration/dotty.bash`, `inject/bash/dotty-rcfile.bash`, `inject/zsh/.zshenv`, `inject/xdg/fish/vendor_conf.d/dotty-shell-integration.fish`; the license payload; `terminfo/x/xterm-dotty` and `terminfo/78/xterm-dotty`; `dotty --version`; `dotty +explain-config font.size` |
| Windows publish output (`verify-publish.ps1`) | `dotty.exe`, `glfw3.dll`, `libSkiaSharp.dll`, `lua54.dll`; `shell-integration\` `dotty.bash`, `Dotty.ps1`, `inject\bash\dotty-rcfile.bash`, `inject\zsh\.zshenv`; the license payload; `dotty --version`; `dotty +explain-config font.size` |
| `smoke-release-assets` (`scripts/dist/verify_archive.py`), every archive | exactly four archives; name `dotty-<version>-<rid>`; `.tar.gz` for Linux/macOS with every entry under the `dotty-<version>-<rid>/` directory and executable `dotty`/`pty-helper`, `.zip` for Windows; the same files as the publish-output rows (Windows: `shell-integration/` with `/` or `\` separators); every missing entry is reported |
| `smoke-release-assets`, extracted Linux archive | under Xvfb with software Mesa (`libgl1-mesa-dri`, `LIBGL_ALWAYS_SOFTWARE=1`, llvmpipe), `scripts/native/desktop-smoke.py --backend x11` drives the loopback control interface (`DOTTY_TEST_PORT`): the window presents frames (`STATS` `presentCount` grows), the child shell's output renders in a new tab and a split pane, and resizes apply |

These are presence checks only for the shell-integration payload. They do not run any injected shell; see [Shell Integration](ShellIntegration.md#verification-scope). The Linux archive smoke under Xvfb and Mesa is the only automated GUI start of a release artifact; the `--version`/`+explain-config` runs above start the executable without a window on every RID.

## Platform baselines

The minimum OS versions in release notes are measured, never hand-written. `scripts/dist/check_baseline.py` (pure Python, no binutils) reads every ELF or Mach-O file in a publish directory:

- Linux: the highest `GLIBC_x.y` symbol version any shipped file requires (dynamic segment `DT_VERNEED`), plus the `DT_NEEDED` libraries that are not shipped beside `dotty` (reported as system libraries, so a `libwayland-client` dependency is documented in the notes rather than hidden). Every ELF must be built for the RID's architecture.
- macOS: the deployment target (`LC_BUILD_VERSION` `minos`, or `LC_VERSION_MIN_MACOSX`) of the slice for the RID's architecture, plus system dylibs. Every Mach-O must contain that slice.
- Windows: nothing is measured; the floor (Windows 10 1809, build 17763, the ConPTY requirement) is declared in `baseline-targets.json` and labelled as declared in the notes.

`scripts/dist/baseline-targets.json` holds the targets: glibc 2.31 for `linux-x64` (the build runs in a `debian:bullseye` container, glibc 2.31), macOS 13.0 for both macOS RIDs (exported as `MACOSX_DEPLOYMENT_TARGET`, which clang uses for `pty-helper` and CMake for GLFW). A measured value above its target fails the job; raise a target only as a reviewed change when a dependency (for example the .NET 11 NativeAOT runtime or SkiaSharp's native library) genuinely requires it. `create-release` merges the per-RID reports into `baselines.json`, attaches it to the release, and `scripts/dist/release_notes.py` renders the "System requirements" section from it; notes generation fails if any archive lacks a baseline or a baseline has violations.

## Supply chain

- **Pinned actions.** Every `uses:` names a full commit SHA with a `# vX.Y.Z` comment (`scripts/dist/tests/test_workflows.py` enforces it). `.github/dependabot.yml` updates GitHub Actions and the NuGet packages in `Directory.Packages.props` weekly.
- **Least privilege.** Every workflow declares `permissions: contents: read` at the top. Only `release.yml`'s `create-release` job gets `contents: write`, `id-token: write` and `attestations: write`; the manual `esctest-baseline` and `recalibrate-baselines` jobs keep `contents: write` for their opt-in commit.
- **Checksums and provenance.** `create-release` writes `SHA256SUMS` (archives and `baselines.json`, `sha256sum` format) and `MANIFEST.txt`, and attests build provenance for the archives and `baselines.json` with `actions/attest-build-provenance` (Sigstore, keyless). Verify with `sha256sum -c SHA256SUMS --ignore-missing` and `gh attestation verify <archive> --repo <owner>/<repo>`. This is provenance, not bit-for-bit reproducibility. Builds are not signed with an Apple Developer ID or Windows Authenticode certificate.
- **Locked restore.** On GitHub Actions, `Directory.Build.props` sets `ContinuousIntegrationBuild` and `RestorePackagesWithLockFile`, and turns on `RestoreLockedMode` for each project whose `packages.lock.json` is committed. No lock file is committed yet, so CI generates them and uploads them as `nuget-lock-files-<os>` artifacts. Before committing them, make the host-OS-conditional package references unconditional (`SkiaSharp.NativeAssets.*` in `Dotty.csproj` and `Dotty.Rendering.Gpu.csproj`, `Microsoft.Windows.CsWin32` in `Dotty.NativePty.csproj`) and declare the release RIDs in `RuntimeIdentifiers`, otherwise a lock file generated on one OS fails the locked restore on the others; then compare the lock files from all four CI runners and commit them.

## Build portability and platform readiness

Public release builds use the portable NativeAOT baseline by default. Do not force a host-specific CPU instruction set into published artifacts: a previous GitHub artifact failed on a consumer CPU with an unsupported-instruction exit (134). Local performance experiments MAY opt into native CPU tuning explicitly with -p:IlcInstructionSet=native; that is for comparable local measurements, not a portable release artifact.

The configured release target list and CI gates are not a substitute for complete platform qualification. Real GUI smoke coverage on Windows and macOS and platform signing are not implemented by this policy/workflow; treat those as known readiness gaps rather than claiming they are covered. Do not claim an installer or signed build unless one is actually produced and verified.

GitHub artifacts of a release run are kept for 7 days; the published release assets are the durable copy.
