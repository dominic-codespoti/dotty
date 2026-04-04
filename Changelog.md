# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Initial project structure with modular architecture
- Dotty.App: Avalonia-based GUI application with hardware-accelerated rendering
- Dotty.Terminal: High-performance terminal core with zero-allocation ANSI/VT parser
- Dotty.NativePty: POSIX-native PTY helper for Linux and macOS
- Dotty.Abstractions: Clean interfaces for extensibility
- Dotty.Config.SourceGenerator: Compile-time configuration system
- 11 built-in color themes (DarkPlus, Dracula, TokyoNight, Catppuccin, Gruvbox, and more)
- Hardware-accelerated rendering via SkiaSharp
- Efficient buffer management with scrollback support
- Native AOT support for self-contained deployments
- Cross-platform CI/CD with GitHub Actions
- Comprehensive test suite with xUnit and Avalonia.Headless

### Changed
- N/A (initial release)

### Deprecated
- N/A (initial release)

### Removed
- N/A (initial release)

### Fixed
- N/A (initial release)

### Security
- N/A (initial release)

---

## Template for Future Releases

When releasing a new version, follow this pattern:

```markdown
## [X.Y.Z] - YYYY-MM-DD

### Added
- New features

### Changed
- Changes in existing functionality

### Deprecated
- Soon-to-be removed features

### Removed
- Now removed features

### Fixed
- Bug fixes

### Security
- Security improvements and vulnerability fixes
```

---

## Release Categories

### Version Numbering

Dotty follows [Semantic Versioning](https://semver.org/):

- **MAJOR** (X.y.z): Incompatible API changes
- **MINOR** (x.Y.z): Added functionality (backwards compatible)
- **PATCH** (x.y.Z): Bug fixes (backwards compatible)

### Pre-release Versions

Pre-release versions use suffixes:

- `v1.0.0-alpha.1` - Early testing
- `v1.0.0-beta.1` - Feature complete, testing
- `v1.0.0-rc.1` - Release candidate

---

## How to Update This Changelog

### For Contributors

When submitting a PR, add an entry under `[Unreleased]` in the appropriate section:

1. **Added** - New features, capabilities, or documentation
2. **Changed** - Changes to existing functionality
3. **Deprecated** - Features marked for removal
4. **Removed** - Deleted features
5. **Fixed** - Bug fixes
6. **Security** - Security-related changes

### For Maintainers

When preparing a release:

1. Update the `[Unreleased]` header to the new version number and date
2. Add a new empty `[Unreleased]` section at the top
3. Ensure all changes are properly categorized
4. Link to the full commit comparison at the bottom

Example:
```markdown
## [0.3.0] - 2026-04-04

### Added
- Feature description here

[Unreleased]: https://github.com/dominic-codespoti/dotty/compare/v0.3.0...HEAD
[0.3.0]: https://github.com/dominic-codespoti/dotty/releases/tag/v0.3.0
```

---

## Links

- Full Changelog: https://github.com/dominic-codespoti/dotty/commits/main
- Releases: https://github.com/dominic-codespoti/dotty/releases
- Compare Versions: Use GitHub's compare feature (e.g., `v0.2.0...v0.3.0`)
