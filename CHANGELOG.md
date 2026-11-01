# Changelog

User-facing changes are collected under **Unreleased**. Before tagging a release,
move its notes into `## [X.Y.Z] - YYYY-MM-DD` and update the application's version.
The release workflow uses that exact section as the GitHub release notes.

## [Unreleased]

### Added

- Optional automatic update notifications, a dismissible in-app banner, and
  Settings → Updates with persistent Manual only/Notify only preferences.
- Notify only is the approved default for a missing update preference. Checks
  wait until after startup and respect saved check times and GitHub backoff.
- Automatic download/install choices are reserved and unavailable in this stage.
- Manual signed-release checks, plain-text release notes, cancellable download
  progress, and confirmed installation/restart through the existing safe updater.
- Manual-installer link for copies that cannot update in place, with existing
  file-operation, sharing, automation and close-cancellation protections retained.

## [0.1.5] - 2026-11-01

### Added

- MIT licence and an installer agreement page that requires acceptance before
  directory selection. Application packages include the licence text.

### Fixed

- Serialize tests that share the settings directory and isolate their fallback
  profile to prevent intermittent persistence-test failures in release builds.
- Isolate updater process tests from handover probes that start and exit under
  the application process name.

## [0.1.4] - 2026-11-01

### Added

- MIT licence and an installer agreement page that requires acceptance before
  directory selection. Application packages include the licence text.

## [0.1.3] - 2026-11-01

### Fixed

- Isolate Windows PowerShell child module paths in Python build tests so
  PowerShell 7 hosts do not break `Get-FileHash` and module loading.

### Added

- Manual GitHub build validation on `master`, without creating a tag or draft
  release, and downloadable build/test diagnostics.

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
