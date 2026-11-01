# Changelog

User-facing changes are collected under **Unreleased**. Before tagging a release,
move its notes into `## [X.Y.Z] - YYYY-MM-DD` and update the application's version.
The release workflow uses that exact section as the GitHub release notes.

## [Unreleased]

## [0.1.2] - 2026-11-01

### Fixed

- Release tests no longer depend on optional Windows WebP decoders; format,
  dimensions, pixel colors and lossless image quality remain verified.
- Toolbar alignment tests use the actual window content width when Windows
  constrains the requested size on CI runners.

## [0.1.1] - 2026-11-01

### Added

- Separate versioned release packaging and per-user Inno Setup installation.
- Signed-release verification, manual staging and work-preserving update handover.
- Update backend with copied-profile health checks, atomic version selection,
  rollback and authenticated interruption recovery.
- Tag-triggered release builds and draft release notes from this changelog.
- Dedicated release-signing trust, protected owner-local key creation and verified encrypted backups.

### Safety and availability

- Existing preferences and legacy FileLens data locations are preserved.
- A dedicated production public key is pinned. Existing installations need a
  tested manual bootstrap build; the in-app update action and automatic
  installation are not enabled yet.

## [0.1.0]

### Available baseline

- Tabbed Windows file explorer with file previews, file operations and search.
- Customizable toolbar, themes, saved folder views and keyboard commands.
- Archive, image, PDF, comparison, synchronization, automation and sharing tools.
- Existing portable development publishing through `publish.ps1 -Run`.

See README.md for the complete feature list and prerequisite limitations.
