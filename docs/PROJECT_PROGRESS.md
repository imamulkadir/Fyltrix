# Installation and update infrastructure progress

Last updated: 2026-10-31.

## Execution agreement

- Work one stage at a time, build/test it, report the evidence, and stop for explicit approval before the next stage.
- Use **`master` exclusively** for future Fyltrix development. Do not create feature, development, release, or temporary branches.
- No commits, pushes, merges, tags, remote changes, or GitHub Release creation/publication. The owner handles Git operations.
- Preserve the current application, user profile, and `publish.ps1 -Run`. Installer experiments must use separate output/install directories and a distinct sandbox installer identity.
- Keep fully automatic installation disabled until manual installation, verification, rollback, and user-data preservation have passed, followed by explicit approval for any further rollout.

## Priority task — Master-only branch consolidation

Status: **Local consolidation completed; remote branch deletion awaits explicit approval. Update-system development is stopped.**

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

### Pending GitHub action

The remote `feature/auto-update` branch still exists at `ca66593f58381fe179cbac8502ea2aef2c8666d7`. Its work is already incorporated into `master`. Per the owner's requirement to stop before remote/destructive GitHub actions, it has not been deleted automatically. The owner can delete this branch from GitHub's Branches page, or explicitly approve deletion of this exact remote branch after a fresh containment/head check. Do not delete `master`, reopen/close the already-merged PR, or change the default branch.

No commit, push, merge, tag, force-push, reset, GitHub Release operation, PR mutation, remote deletion, or default-branch change was performed during this cleanup. Fetching and local fast-forward/switch/fully-merged-branch deletion were the only Git mutations.

### Verification and current status

- Git ancestry, tree equality, commit-head synchronization, working-file preservation, local branch list, remote branch list, and GitHub default/PR status verified.
- Application source, dependencies, user data, and `publish.ps1 -Run` are unchanged. No application build or test rerun was needed for this Git/documentation-only task; Stage 1's executed result remains 354 passing tests.
- Stage 2 is paused. Complete and verify remote branch cleanup first, then wait for an explicit instruction to resume development directly on `master`.
- Suggested commit message for this document update: `docs(workflow): record master-only branch consolidation`.

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
2. Separate release packaging/layout proof of concept, preserving `publish.ps1 -Run`; approved, but paused before implementation by the priority Git cleanup. Resume only on `master` after cleanup and an explicit resume instruction.
3. Inno Setup per-user installer and sandbox manual installation/upgrade/uninstall tests, including user-data preservation.
4. Read-only GitHub release detection and signed-manifest/package verification, with local-feed and rejection tests.
5. Manual download and safe shutdown/handover, respecting running work, sharing, and cancelled closes.
6. Out-of-process version switch, health validation, rollback/failure recovery, and compatibility tests for saved data.
7. End-to-end regression verification and optional automation proposal. Fully automatic installation remains disabled unless separately approved after the gates pass.

Next action: obtain approval for the remaining remote feature-branch deletion (or verify the owner's manual deletion). Do not resume Stage 2 automatically. All future implementation uses `master` only.
