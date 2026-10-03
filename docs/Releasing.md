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

## Build portability and platform readiness

Public release builds use the portable NativeAOT baseline by default. Do not force a host-specific CPU instruction set into published artifacts: a previous GitHub artifact failed on a consumer CPU with an unsupported-instruction exit (134). Local performance experiments MAY opt into native CPU tuning explicitly with -p:IlcInstructionSet=native; that is for comparable local measurements, not a portable release artifact.

The configured release target list and CI gates are not a substitute for complete platform qualification. Real GUI smoke coverage on Windows and macOS and platform signing are not implemented by this policy/workflow; treat those as known readiness gaps rather than claiming they are covered. Do not claim an installer or signed build unless one is actually produced and verified.
