# Installation and update infrastructure progress

Last updated: 2026-10-31.

## Execution agreement

- Work one stage at a time, build/test it, report the evidence, and stop for explicit approval before the next stage.
- Use **`master` exclusively** for future Fyltrix development. Do not create feature, development, release, or temporary branches.
- No commits, pushes, merges, tags, remote changes, or GitHub Release creation/publication. The owner handles Git operations.
- Preserve the current application, user profile, and `publish.ps1 -Run`. Installer experiments must use separate output/install directories and a distinct sandbox installer identity.
- Keep fully automatic installation disabled until manual installation, verification, rollback, and user-data preservation have passed, followed by explicit approval for any further rollout.

## Priority task — Master-only branch consolidation

Status: **Completed. Only `master` exists locally and remotely. The owner subsequently approved resuming Stage 2 directly on `master`.**

### Findings

- GitHub default branch is `master`, confirmed through repository metadata and the live remote HEAD.
- PR #1 (`https://github.com/imamulkadir/Fyltrix/pull/1`) was already merged before this audit. Its existing merge commit is `b8f51c2a46ea437c5a5790c5a665b4f3321b3aaf`.
- The feature tip `ca66593f58381fe179cbac8502ea2aef2c8666d7` adds only this progress document to baseline `c951bc6`. It is an ancestor of remote `master` and has no unique commits remaining outside it. The feature and remote-master file trees are identical.
- There were no uncommitted changes when the consolidation audit started. The interrupted Stage 2 had only inspected the reference; no packaging/launcher implementation had been written.

### Preservation and local changes

- Backed up all pre-cleanup Git refs in `all-refs-before.bundle`, plus index/worktree patches, branch/status information, and copies/hashes of all 192 tracked/untracked working files.
- Backup directory: `D:\Tools\Fyltrix-branch-consolidation-20261031-134427-5e89f485`.
- Advanced local `master` to the already-existing remote merge commit with a normal fast-forward-only fetch ref update. No new merge or commit was created.
- Switched the working directory to `master`.
- Confirmed the feature commit was contained in `master`, then removed the local feature branch using non-forced `git branch -d`.
- Compared every backed-up working-file hash after the switch: all files matched before this deliberate progress-document update.
- Local `master`, `origin/master`, and live GitHub `master` all pointed to `b8f51c2a46ea437c5a5790c5a665b4f3321b3aaf` at verification time. The documentation change below remains uncommitted and is not claimed to be on GitHub.

### Approved remote removal and final verification

The owner approved the proposed remote deletion with "proceed next". Before removal, rechecked that the feature tip remained exactly `ca66593f58381fe179cbac8502ea2aef2c8666d7` and was an ancestor of synchronized `master`. A second all-refs Git bundle was saved before the remote action.

Deleted only the remote `feature/auto-update` reference through the authenticated GitHub API, then fetched with pruning. Final local and live remote branch lists each contain only `master`; the stale remote-tracking feature reference is gone. GitHub's default remains `master`. PR #1 remains already merged; no PR mutation was required.

The owner had meanwhile committed and synchronized the previous progress update as `e037d16d585ce970d32d5cfcf1910a3083936f41`. Final local `master`, `origin/master`, and live GitHub `master` match that commit. All tracked working-file hashes matched before this deliberate final documentation edit, which remains uncommitted for the owner.

No commit, push, merge, tag, force-push, reset, GitHub Release operation, PR mutation, or default-branch change was performed during this cleanup. Git mutations were limited to fetch/prune, the local fast-forward/switch/fully-merged-branch deletion, and the explicitly approved remote branch deletion. Completion evidence: `remote-consolidation-completed.json` in the backup directory above.

### Verification and current status

- Git ancestry, tree equality, commit-head synchronization, working-file preservation, local branch list, remote branch list, and GitHub default/PR status verified.
- Application source, dependencies, user data, and `publish.ps1 -Run` are unchanged. No application build or test rerun was needed for this Git/documentation-only task; Stage 1's executed result remains 354 passing tests.
- Branch cleanup is complete and verified. The owner's later "proceed next" instruction approved resuming Stage 2 directly on `master`; its progress is recorded below.
- Suggested commit message for this document update: `docs(workflow): record master-only branch consolidation`.

## Stage 2 — Isolated release packaging and layout

Status: **Completed — stopped for Stage 3 approval.** Authorized by the owner's "proceed next" after completed branch consolidation. Working on `master`; baseline commit `1d71e13`. No Git write operations performed.

### Completed tasks and scope

- Added an independent .NET 8 NativeAOT launcher, following the reference's stable-launcher/versioned-payload approach without importing its crash supervisor, data model, updater, or signing keys.
- Added `tools/Publish-Layout.ps1`. It builds the existing WPF application self-contained for win-x64 into a new `app-<version>` payload, copies the native launcher to the layout root, writes a bounded version pointer, and creates a ZIP/checksum/build record.
- All package build intermediates and outputs are isolated from normal development and `artifacts/Fyltrix`. Existing destinations are refused; application-data overlaps and output junctions are rejected.
- Preserved the main application's source, dependencies, defaults, data folders, shutdown behavior, and `publish.ps1` byte for byte.
- Added packaging tests and a release-layout guide. No installer/updater, registration, migration, automatic check/install, remote release operation, or user-profile change is implemented in Stage 2.

### Files created or modified

- Created `src/Fyltrix.Launcher/Fyltrix.Launcher.csproj`, `app.manifest`, `Program.cs`, and `ReleaseLayout.cs`.
- Created `tests/Fyltrix.Packaging.Tests/Fyltrix.Packaging.Tests.csproj` and `ReleaseLayoutTests.cs`.
- Created `tools/Publish-Layout.ps1` and `docs/RELEASE_LAYOUT.md`.
- Updated `README.md` and `docs/PROJECT_PROGRESS.md`.

### Layout and startup contract

```text
layout/
  Fyltrix.exe                 Native launcher; future stable shortcut path
  current.json                { current: version, previous: null }
  LAYOUT.txt
  app-<version>/Fyltrix.exe    Existing application with its self-contained dependencies
```

The root launcher forwards folder arguments without shell parsing and preserves the caller's working directory. It waits for the payload's exit. It reads but never rewrites the pointer or profile. If the current executable is absent, a valid previous pointer can select an existing previous payload; this is not health/crash rollback. Malformed/oversized/ambiguous pointers, unsafe versions, and redirected payloads fail closed.

### Verification

- NativeAOT launcher build/publish: passed on this machine's Windows C++ build tools; launcher size **2,333,184 bytes** (~2.23 MiB).
- Initial `0.1.0` package: published under an external path containing spaces and `&`; ZIP/checksum verified. ZIP size **85,112,902 bytes** (~81.17 MiB).
- Full existing application suite: **354 passed, 0 failed, 0 skipped**, duration 2 minutes 54 seconds.
- New packaging suite: **28 passed, 0 failed, 0 skipped**, including an actual payload-junction rejection test.
- Actual native-root startup, ZIP extraction/relocation, previous-payload fallback fixture, and read-only-layout startup: passed; correct folder listing, preview readiness, and exit code 0. Folder arguments exercised spaces, `&`, and Unicode.
- Read-only test used an effective inherited deny-write ACL; write attempt was denied, launch succeeded, and the original package ACL was restored.
- Previous-payload fallback used the same baseline application bytes under a previous-version folder. No upgrade, downgrade, or health rollback is claimed.
- Invalid version, numeric prerelease, protected development/profile paths, nested existing release, junction output, and existing-version destination guards: rejection checks passed. Existing output contents stayed unchanged.
- Existing development x64 build: passed with existing nullable/DPI warnings; actual development launch passed with isolated settings/clipboard.
- Non-default `0.1.1-stage2` package: build, ZIP/checksum, and actual root-launcher startup passed. Launcher/payload Windows file versions are `0.1.1.0`, with the requested prerelease product version. Numeric components outside Windows metadata limits are rejected before building.
- Final hashes confirm that all original application/test/tool source files, `publish.ps1`, the existing portable-release contents, and the personal roaming profile are unchanged. Only README/progress documentation changed among previously tracked files. Original layout hashes match after startup/read-only tests. Evidence: `stage2-verification.json`.
- Inno compilation/install/uninstall and update/rollback were not performed; those belong to later stages.

### Evidence and known limits

Evidence/output directory: `D:\Tools\Fyltrix-stage2-20261031-135758-835ae43f`. It contains test TRX files, the isolated packages/build intermediates, checksum and file-hash records, output-guard results, and private-desktop startup results. Personal profile contents are not copied into repository reports.

The existing development release remains in place. NativeAOT publishing needs Windows C++/SDK tools; end users do not need those tools or a separately installed .NET runtime for the self-contained payload. WebView2 and optional codecs remain separate prerequisites. The package is unsigned and has no installed/managed identity. A future update handover must wait for both launcher/payload processes and respect existing work/sharing/cancelled closes. Small launcher/pointer tests are not proof of future installer safety or automatic rollback.

Suggested commit message: `feat(packaging): add isolated versioned release layout`.

Next stage after completion and explicit approval: Stage 3, Inno Setup per-user installer plus sandbox manual installation/upgrade/uninstall and data-preservation tests.

## Stage 1 — Baseline protection and DynamicEye reference audit

Status: **Completed — stopped for approval.** No installer or updater feature is being introduced in this stage.

### Completed tasks

- Verified that the working application was already committed as `c951bc6` (`initial stable release`) with a clean working tree before feature work.
- The branch was `master` at that point. Created and switched to `feature/auto-update` before implementation and before the owner's later instruction reserving Git operations. No commit or remote write was made in Stage 1. This historical branch arrangement was superseded by the priority master-only consolidation above.
- Built and launched the original development application successfully before implementation. The launch used a private desktop, isolated settings/WebView2 data, and a read-only test clipboard provider; folder listing and preview readiness passed, with exit code 0.
- Inspected the reference repository at `D:\Tools\DynamicEye` (its GitHub project is spelled `DyanamicEye`). It uses Inno Setup 6, a separate packaging script, versioned payloads, a stable launcher, signed release manifests, and an out-of-process update/rollback helper.
- Confirmed Inno Setup 6 compiler availability at `C:\Program Files (x86)\Inno Setup 6\ISCC.exe`.
- Set aside the unverified Velopack prototype, restoring all modified application files to the committed baseline. Removed its opt-in SDK project, UI draft, and tool manifest from the active checkout by archiving them externally. No Velopack dependency remains in the active application project. The draft is not a completed or approved stage.
- Kept `publish.ps1` and the existing portable/development release untouched.

### Baseline behavior and compatibility boundaries

| Area | Existing behavior to preserve |
|---|---|
| Technology | .NET 8 WPF, Windows, win-x64; application assembly/executable `Fyltrix` / `Fyltrix.exe`, version 0.1.0. |
| Startup | `src/Fyltrix/App.cs`: install UI synchronization context, load settings, select theme, construct/show MainWindow, start the application loop. First command-line argument is an optional folder path. |
| Development | `dotnet build src/Fyltrix/Fyltrix.csproj -p:Platform=x64`; existing `publish.ps1 -Run` publishes a self-contained portable folder and starts its executable. No installer identity is required. |
| Portable executable | `<checkout>\artifacts\Fyltrix\Fyltrix.exe`. Existing files must not be replaced by installer experiments. |
| Development executable | `<checkout>\src\Fyltrix\bin\x64\Debug\net8.0-windows10.0.19041.0\Fyltrix.exe` for the explicit x64 build. |
| Roaming data | `%AppData%\FileLens`: settings, tabs, Favourites, Recent, toolbar/menu/IDE preferences, saved views/searches, tags, templates, automation, share records, and sync baselines. Retain the legacy folder and schemas. |
| Local data | `%LocalAppData%\FileLens`: WebView2, thumbnail, and archive caches. No relocation or migration is planned. |
| Shutdown | Existing tab-close confirmation, active sharing/background-tray choice, settings save, job cancellation, preview disposal, and automation cleanup must remain. An updater must not bypass cancellations or terminate active file work. |

### Reference files reviewed

- `packaging/installer/DynamicIsland.iss`
- `tools/Publish-Layout.ps1` and release/signing tooling references
- `docs/ADR/0030-release-layout.md`
- `docs/ADR/0031-per-user-installer.md`
- `docs/ADR/0034-signed-release-manifest.md`
- `docs/ADR/0035-updater-swap-and-rollback.md`
- `docs/ADR/0036-update-data-safety.md`

The reference is a design source, not a script to run against Fyltrix. Its application IDs, paths, signing keys, installer deletion rules, startup registrations, database recovery, and release automation must not be copied blindly. Inno Setup installs/uninstalls the application; secure GitHub discovery, package verification, safe handover, health checks, and rollback are separate components.

### Files created or modified

- Created: `docs/PROJECT_PROGRESS.md`.
- Application files temporarily changed by the superseded prototype were restored to their committed content: `src/Fyltrix/App.cs`, `src/Fyltrix/Fyltrix.csproj`, and `src/Fyltrix/UI/MainWindow.Tools.cs`.
- Prototype-only files were archived outside the checkout, not retained as feature code. No existing user file was deleted.

### Verification

- Initial baseline x64 build: passed.
- Initial baseline executable launch: passed; native title, folder listing, preview readiness, exit code 0.
- Build after removing the prototype: passed, with the existing nullable and WinForms DPI warnings.
- Full existing test suite after restoring baseline: **354 passed, 0 failed, 0 skipped**, duration 2 minutes 10 seconds. TRX: `stage1-test-results/stage1-baseline.trx` in the evidence directory.
- Final development executable launch after restoration: passed; folder listing and preview readiness, native Fyltrix title, exit code 0. Evidence: `D:\Tools\Fyltrix-migration-20261031-124758-4e9f904a\apphost-smoke-stage1-restored-baseline\verification.json`.
- Application source and `publish.ps1` match committed baseline `c951bc6`; personal roaming-profile and existing portable-release hashes match their pre-stage snapshots. Evidence: `stage1-preservation.json`.
- No installer compilation, installation, update application, rollback, signing validation, or GitHub Release operation was performed or claimed in Stage 1.

### Evidence and preservation

External evidence directory: `D:\Tools\Fyltrix-update-audit-20261031-132632-07ce6bc1`.

- `profile-before.sha256.json`: personal roaming-profile hashes only; no private profile contents are stored in this repository.
- `development-release-before.sha256.json`: hashes of the existing portable release.
- `shelved-velopack-draft`: archived prototype; not an installation.
- `stage1-test-results`: existing-suite TRX results.
- Initial private-desktop launch evidence is in `D:\Tools\Fyltrix-migration-20261031-124758-4e9f904a\apphost-smoke-auto-update-baseline`.

### Known issues and risks

- Fyltrix has no installed layout, updater, release-signing trust root, update health acknowledgement, or tested installer upgrade path yet. These remain future stages.
- The reference app has a different shutdown/data model; particularly its database recovery and process-control assumptions are not yet proven compatible with Fyltrix.
- Native Windows installer behavior, signing/SmartScreen, permissions, and uninstall/user-data preservation require dedicated sandbox tests. Compiler availability alone does not prove these behaviors.
- No public release, automatic install, new background service, or user-data migration is enabled.

## Planned stages — each requires a separate approval

1. Baseline protection and reference audit — completed and approved by the owner's "go next" instruction.
2. Separate release packaging/layout proof of concept — completed on `master`; 354 existing and 28 new tests pass. Waiting for Stage 3 approval.
3. Inno Setup per-user installer and sandbox manual installation/upgrade/uninstall tests, including user-data preservation.
4. Read-only GitHub release detection and signed-manifest/package verification, with local-feed and rejection tests.
5. Manual download and safe shutdown/handover, respecting running work, sharing, and cancelled closes.
6. Out-of-process version switch, health validation, rollback/failure recovery, and compatibility tests for saved data.
7. End-to-end regression verification and optional automation proposal. Fully automatic installation remains disabled unless separately approved after the gates pass.

Next action: the owner can review/commit the Stage 2 changes manually. Stop for explicit Stage 3 approval before adding or running an installer. The owner handles commits and all remote Git/release operations. All implementation uses `master` only; do not create another branch.
