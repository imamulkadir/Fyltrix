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

## Stage 5 — Manual staging and safe shutdown/handover

Status: **Completed — stopped for Stage 6 approval.** Authorized by the owner's "pushed, proceed", followed by "continue please from where you left off" during verification. Starting branch was clean `master` at `e9998306c9fe70c3957ca9a207ceabe0ea3e4d96`, matching origin and live GitHub master. No Git write, branch, release, installer, or production trust operation was performed.

### Completed tasks

- Added explicit authenticated package download and offline staging. Manifest trust/newer-version gates run before package requests or directory creation. Reused the Stage 4 HTTPS redirect policy and the existing package verifier through the same exclusive staging handle.
- Stream into a unique token-owned stage with a 64 KiB buffer, signed-length limits, content/hash verification, 200 MiB disk reserve, 30-second inactivity timeout, caller cancellation, and bounded progress updates. Successful stages retain exact signed evidence; failures clean only their own known files. No automatic pruning or profile migration.
- Default updater-only storage is `%LocalAppData%\Fyltrix\Updates`; existing `%AppData%\FileLens` settings and `%LocalAppData%\FileLens` caches retain their identities. Reject application/development/data overlaps, junctions, and unsafe staging paths; expand 8.3 aliases before protected-path comparisons.
- Added bounded handover preparation/consumption. Capture launcher and payload process identities/start times, reverify evidence/package, wait without killing, reject changed layout/request/process identity, and return a read-only verified package lease. No version switch, extraction, executable launch, installation, or rollback is implemented.
- Added close approval only after successful normal window closure and a final cancellation check, using a flushed temporary token and atomic rename. Cancelled closes remain unapproved even if request removal is blocked. The helper never treats process exit alone as authorization.
- Added an internal MainWindow update-close seam, retaining ordinary tab confirmation, cancellation, settings save and cleanup. It refuses active jobs, pastes, synchronous operations, sharing/background mode, automation and open tools. Tiny work scopes cover conversion/image/PDF/manual automation and automation-engine writes that can outlive their windows/engine. Ordinary closing behavior is unchanged; no application update UI or startup hook is enabled for end users.
- Extended the standalone CLI with explicit `download`, `stage-local`, `prepare`, and readiness-only `wait` commands. Production pins remain empty and fail closed. Test trust is explicit and never added to application defaults.

### Files created or modified

- Created `src/Fyltrix.Updates/StagingArea.cs`, `UpdateDownloader.cs`, and `UpdateHandover.cs`; updated that project's file, shared HTTP transport, verifier, release metadata and signed-evidence model.
- Created `src/Fyltrix/Core/UpdateWork.cs` and `src/Fyltrix/UI/MainWindow.UpdateShutdown.cs`.
- Added only work-tracking scopes/update-only close guards to existing `Core/Automation/AutomationEngine.cs`, `UI/MainWindow.cs`, `AutomationWindow.cs`, `ConvertWindow.cs`, `ImageToolsWindow.cs`, and `PdfToolsWindow.cs`. Existing file-management algorithms/dependencies/defaults were not rewritten.
- Updated `tools/Fyltrix.UpdateTool/Program.cs`.
- Created `tests/Fyltrix.HandoverProbe/Fyltrix.HandoverProbe.csproj` and `Program.cs`, plus update-suite `DownloadTests.cs` and `HandoverTests.cs`; updated its project and CLI interoperability test.
- Created `tests/Fyltrix.Tests/UpdateShutdownTests.cs`; synchronized the existing sharing-expiry test with its actual notification/listener-completion conditions.
- Extended the test-only PlacementProbe project/startup hook and added `UpdateHandoverStartupProbe.cs` to exercise approval on the real application's UI thread. This dependency/hook is never shipped in the application payload.
- Created `docs/MANUAL_UPDATES.md`; updated `README.md`, `docs/UPDATES.md`, `docs/IMPLEMENTATION_STATUS.md`, and this file.

### Executed verification

- Final update suite: **113 passed, 0 failed, 0 skipped**, covering authenticated transfer/offline CLI staging, size/hash corruption, stalls, cancellation, no-space refusal, protected/junction paths, staging evidence, process waiting, PID reuse, changed inputs, cancellation during close, locked-request cancellation, and a package lease that denies writers.
- Final application suite: **361 passed, 0 failed, 0 skipped** (354 existing + seven new shutdown tests). Packaging suite: **28 passed**; installer builder guards: **9 passed**. New WPF tests prove tab/event cancellation, active-job/share/automation/tool refusal, modal-state recheck, image saves after tool closure, and writes after automation disposal. Real clipboard, Recycle Bin, desktop input and LAN listeners were not used.
- An initial full run had one existing sharing-expiry assertion fail after observing `Expired` but before its asynchronous `Changed` notification. Confirmed the ordering in unchanged sharing source; the test now waits for all already-asserted outcomes. Focused recheck and full rerun passed. Initial transfer-fault fixtures and a junction command-quoting issue were corrected; final tests create and reject a real junction. Early compile errors were fixed before successful builds.
- Existing x64 development build, independent update tool/library, Release probe, and isolated self-contained release-layout builds: **passed**, with the existing application nullable/DPI warnings. An interrupted initial packaging attempt was retained; fresh output roots were used instead of overwriting partial output. Final application source was packaged separately under `release-verified/0.1.0`.
- Final actual-app integration: a real **85,113,586-byte** `0.1.1` archive was signed with a disposable key and staged through the actual CLI. Started the final `0.1.0` native launcher/WPF payload on a private desktop with synthetic profile/browser/clipboard data. The application itself prepared the request and called the guarded normal-close/atomic-approval method on its UI thread; a separate CLI helper verified readiness after both processes exited. The layout remained byte-identical, custom preferences/preview worked, and launcher exit was 0. No package was extracted, applied, installed, or restarted.
- An earlier external test bridge wrote approval after the application exited and was correctly refused. Replaced that test bridge with the actual in-application UI-thread sequence above; no waiting-for-approval bypass was added. The final report explicitly records `approvalWrittenOnApplicationUiThread: true`.
- Additional private-desktop smoke checks: current x64 development binaries launched/listed/initialized preview and accepted guarded close; the updated probe also worked with an untouched older Stage 2 payload using the legacy close path. Both exited 0.
- One local real-archive copy/verification run took about 0.6 seconds; this is local staging throughput, not an internet or startup benchmark. Buffers/progress are bounded; no new idle task, polling loop, startup networking or renderer was added. Existing source dependency lists, app startup, `publish.ps1`, portable release and personal roaming-profile hashes were preserved.

### Evidence and remaining gates

External evidence: `D:\Tools\Fyltrix-stage5-20261031-205214-600f070b`. Final reports: `tests/stage5-updates-complete.trx`, `tests/stage5-app-complete.trx`, `tests/stage5-packaging.trx`, `real-handover-verification.json`, `development-and-legacy-smoke.json`, and `stage5-verification.json`. The final real-app run is `real-handover-ui`; earlier attempts are retained as evidence, not counted as passing runs. Disposable private keys were removed; no production key was generated/pinned.

The CLI and internal shutdown seam are developer/manual boundaries, not an end-user update UI. Production downloads remain unavailable without a reviewed owner signing pin. Stage 6 must implement application-directory coordination, apply/switch, health validation, rollback and abort recovery; readiness alone is not installation safety. A process starting after the final scan must still be handled at that stage. Fully automatic installation, live authenticated releases, signing/SmartScreen, power-loss recovery and production installer rollout have not passed their later gates. Existing installed/development/portable copies were not replaced and no personal settings were migrated.

Suggested commit message: `feat(updater): add verified staging and safe shutdown handover`.

Next stage, only after explicit approval: Stage 6, out-of-process apply/version switch, health validation, rollback/failure recovery and saved-data compatibility tests. No implementation of that stage has started.

## Stage 4 — Read-only release discovery and signed verification

Status: **Completed — stopped for Stage 5 approval.** Authorized by the owner's "pushed, continue" instruction after Stage 3. Clean starting branch: `master` at `b66adc2c9bb29cffca43958f11058595bf6b48f0`; local, origin tracking, and live remote master matched. No Git write or release operation was performed.

### Completed tasks

- Inspected DynamicEye's signed-manifest, release-source, version, and package-validation implementation as a design reference. Used .NET's built-in cryptography/HTTP/ZIP libraries and Fyltrix's existing layout version/pointer parser; added no runtime package dependency.
- Added a separate `Fyltrix.Updates` library: one-shot stable GitHub Releases discovery, SemVer precedence, strict/bounded repository/tag/asset metadata, ETags, rate-limit reporting, cancellation, timeout, and bounded HTTPS asset redirects. No background scheduler or profile persistence.
- Added exact-byte ECDSA NIST P-256/SHA-256/IEEE P1363 signed-manifest verification. Bind product/schema/version, package names, sizes, hashes, and GitHub asset metadata. Reject duplicate/unknown fields, wrong keys/curves, tampering, Sandbox names, and unsupported packages.
- Added read-only verification of supplied package bytes through one file handle, followed by basic PE or bounded versioned-layout ZIP validation. No package is downloaded, extracted, executed, or installed.
- Added a standalone owner/developer CLI for live/offline checks, explicitly keyed offline verification, and local manifest signing. Signing requires a matching owner-supplied public/private key pair outside Git and new outputs; it never uploads, publishes, or configures application pins.
- The production pin list remains empty. No Fyltrix production key was created; no reference/test key was pinned. Without a pin, manifest authentication fails closed before any manifest asset requests. The WPF application/startup/settings do not reference this functionality yet.

### Files created or modified

- Created `src/Fyltrix.Updates/Fyltrix.Updates.csproj`, `ReleaseVersion.cs`, `ReleaseMetadata.cs`, `SignedManifest.cs`, `GitHubReleaseClient.cs`, and `PackageVerifier.cs`.
- Created `tools/Fyltrix.UpdateTool/Fyltrix.UpdateTool.csproj` and `Program.cs`.
- Created `tests/Fyltrix.Updates.Tests/Fyltrix.Updates.Tests.csproj`, `Fixtures.cs`, `ReleaseTests.cs`, `ClientTests.cs`, `PackageTests.cs`, and `ToolTests.cs`.
- Created `docs/UPDATES.md`; updated `README.md`, `docs/IMPLEMENTATION_STATUS.md`, and this progress file.

### Executed verification

- New CLI/library build: **passed**, zero warnings/errors. An initial CLI compile error from passing a span across an await was corrected before testing; final builds pass.
- Update suite: **86 passed, 0 failed, 0 skipped**. Includes actual CLI signing/check/verification interoperability, repeated-output and key-mismatch refusal, wrong signatures/DER/curve, strict manifest/metadata rejection, SemVer large numeric prereleases, altered bytes/lengths, corrupt/unsafe ZIPs, ETags, HTTP errors/rate limits, redirect bounds, response-size bounds without Content-Length, stalled-body timeout, and caller cancellation. No real clipboard/profile or network listener is used.
- Full existing application suite: **354 passed, 0 failed, 0 skipped**, duration 2 minutes 8 seconds. Packaging suite: **28 passed, 0 failed, 0 skipped**. Existing installer builder guards: **9 passed**.
- Existing development x64 build: **passed**, with the existing nullable and WinForms DPI warnings. No application code, dependencies, defaults, publish workflow, or installer source changed.
- Live read-only CLI check against the actual GitHub repository returned **NoRelease**. No release was created, and no authenticated live update is claimed.
- Additional external integration: locally signed and verified the actual **85,112,902-byte Stage 2 `0.1.0` archive** with a disposable P-256 test key. Actual CLI verification passed signature, length, SHA-256, and layout checks. Archive bytes were read only; the temporary private key was deleted afterward. Nothing was extracted or installed.
- Final preservation audit compares all original tracked source/tool/test files, personal roaming-profile hashes, and existing portable-release hashes with the pre-stage snapshot. Only deliberate documentation edits are allowed among original tracked files. `publish.ps1`, original sources and dependencies remain unchanged; work remains uncommitted on `master` for the owner.

### Evidence and remaining gates

External evidence: `D:\Tools\Fyltrix-stage4-20261031-173257-288d5257`. Test reports: `tests/stage4-updates-final.trx`, `tests/stage4-app.trx`, and `tests/stage4-packaging.trx`. Actual-archive result: `real-archive-verification.json`. Final preservation/build/test summary: `stage4-verification.json`. The external one-off integration runner is evidence, not application tooling.

This stage does not provide an in-app update UI, download/staging, coordinated shutdown, installer handover, health rollback, or automatic installation. Stable releases only; checksums/basic PE structure do not replace publisher authentication or compatibility tests. Before production verification can succeed, the owner must independently create/protect a Fyltrix signing key and supply its public SPKI value for reviewed pinning. No private key should enter Git or chat. Authenticode/SmartScreen and a real authenticated published release remain untested. The existing installer/development installations were not replaced or rerun in this read-only stage.

Suggested commit message: `feat(updater): add signed GitHub release verification`.

Next stage, only after explicit approval: Stage 5, manual package download/staging and safe shutdown/handover that respects active work, sharing, and cancelled closes. Stage 6 still owns health validation and rollback. Fully automatic installation remains disabled.

## Stage 3 — Per-user Inno Setup installer

Status: **Completed — stopped for Stage 4 approval.** Authorized by the owner's "proceed next" after Stage 2, with "continue" during verification. Baseline: clean `master` at `778aa9b5bb34569b009ca190aea6b26596c88000`. No commit, push, merge, tag, branch creation, or remote/release mutation was performed.

### Completed tasks

- Added a separate Inno Setup 6 per-user installer and compile-only builder. Sandbox is the default; production has a different permanent AppId, installation folder, registration, and shortcut name. No elevation, automatic launch, automatic installation, startup task, or file-association change is introduced.
- Validate the complete Stage 2 layout against its ZIP and recorded checksums before compilation. Reject modified/extra files, redirected source paths, protected outputs, and existing output destinations.
- Guard installation/uninstall with ownership identity/schema checks, protected-data/development-directory checks, junction rejection, locked-file checks, same-identity relocation refusal, shortcut ownership checks, and numeric binary-downgrade refusal. Applications are never force-closed.
- Install versioned payloads before updating the root launcher/pointer; retain earlier payloads in an appended uninstall log. Uninstall removes logged binaries while retaining unknown files and all application-data directories. No wildcard deletion or user-data migration is present.
- Added a test-only startup hook in the existing probe and a Windows lifecycle harness. Actual native installer/application processes run on private desktops, with synthetic preferences, isolated browser/cache paths, and an isolated clipboard provider. The hook is not shipped in installer payloads.
- Preserved application sources, dependencies/defaults, `publish.ps1`, existing portable-release files, and personal roaming-profile bytes. Normal sandbox application launches still share the existing application profile; only the test hook provides profile isolation.

### Files created or modified

- Created `packaging/installer/Fyltrix.iss` and `packaging/installer/INSTALLATION.txt`.
- Created `tools/Build-Installer.ps1`.
- Created `tests/installer/test_builder.py`, `tests/installer/verify_installer.py`, its local Python-cache `.gitignore`, and `tests/Fyltrix.PlacementProbe/InstallerStartupHook.cs`.
- Updated `tests/Fyltrix.Tests/PaletteTests.cs` with a condition wait for the existing asynchronous writable-destination check before asserting the New command. No production command behavior changed.
- Created `docs/INSTALLER.md`; updated `README.md`, `docs/RELEASE_LAYOUT.md`, and this progress file.

### Executed verification

- Compiled Sandbox installers for `0.1.0` and `0.1.1-stage2`, plus a Production installer for `0.1.1-stage2`. Setup hashes match their sidecars/build records; Windows numeric versions are `0.1.0.0` and `0.1.1.0`. **The Production installer was compiled only, never executed.**
- **17 lifecycle cases passed**, running non-elevated: protected development/profile/unowned/other-identity destinations; initial installation; wrong ownership and unsupported-schema uninstall refusal; junction and locked-pointer refusal; real installed application startup/preferences/preview for both versions; running-app upgrade/uninstall refusal without terminating the application; relocation refusal; upgrade with old payload retained; numeric downgrade refusal; and final uninstall.
- Also asserted HKCU-only sandbox registration, Start menu target/working directory, stable launcher behavior, synthetic preference preservation, unknown root/nested-file retention, and removal of logged payloads, sandbox registration, and sandbox shortcut after uninstall.
- **9 builder guard tests passed**, including actual source-junction rejection, ZIP/payload tampering, unexpected profile files, pointer mismatch, protected/nested outputs, and existing-output preservation.
- Initial full application run: **353 passed, 1 failed**. The palette assertion raced the existing background destination-permission check while installer verification ran concurrently. Added the test-only condition wait above. Focused recheck: **1 passed**. Final full suite: **354 passed, 0 failed, 0 skipped**. Packaging suite: **28 passed, 0 failed, 0 skipped**.
- Existing development x64 build: **passed**, with the existing nullable and WinForms DPI warnings. The probe Release build also passed. Both installed application versions launched, listed the test folder, initialized preview, loaded custom preferences, and closed normally.
- Final hash audit: personal roaming profile and existing portable release unchanged; every original `src/` file and `publish.ps1` unchanged. Git branch remains `master`. No experimental installed copy or sandbox registration/shortcut remains; intentionally retained test sentinel files stay under the external evidence directory.

### Evidence and limits

Evidence directory: `D:\Tools\Fyltrix-stage3-20261031-142110-f3d497a0`. Summary: `stage3-verification.json`; lifecycle results/logs: `sandbox-complete/verification.json`; final test TRX files: `tests/stage3-app-final.trx`, `tests/stage3-packaging.trx`, and `tests/stage3-palette-recheck.trx`. Final installers are in `final-v1`, `final-v2`, and `final-production-not-installed`. Earlier failed attempts/logs are retained, not presented as passing verification.

Installation/upgrade/uninstall were exercised through native silent Setup, not manual interaction with wizard pages. Optional desktop-shortcut selection and a separate standard-user account were not tested. Production identity installation, signing/SmartScreen, power-loss recovery, application-health rollback, prerelease ordering within one numeric version, and safe updater handover remain unverified or unimplemented. Retaining old payloads is not automatic rollback. Checksum consistency is not publisher authentication. The exclusive-file preflight does not replace future coordinated shutdown against a concurrent application launch. No production installer rollout or fully automatic installation is enabled.

Suggested commit message: `feat(installer): add safe per-user Inno Setup installation`.

Next stage, only after explicit approval: Stage 4, read-only GitHub Releases discovery and signed-manifest/package verification, including local-feed and rejection tests. No implementation of that stage has started.

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
2. Separate release packaging/layout proof of concept — completed on `master`; approved for Stage 3 by the owner's "proceed next" instruction.
3. Inno Setup per-user installer — completed on `master`; approved for Stage 4 by the owner's "pushed, continue" instruction. Production installer compiled only.
4. Read-only GitHub release detection and signed-manifest/package verification — completed on `master`; approved for Stage 5 by the owner's "pushed, proceed" instruction. Production trust remains unconfigured.
5. Manual staging and safe shutdown/handover — completed on `master`; 113 update tests, 361 application tests, real-process/UI-thread handover and preservation checks passed. Waiting for Stage 6 approval.
6. Out-of-process version switch, health validation, rollback/failure recovery, and compatibility tests for saved data.
7. End-to-end regression verification and optional automation proposal. Fully automatic installation remains disabled unless separately approved after the gates pass.

Next action: the owner can review/commit Stage 5 manually. Stop for explicit Stage 6 approval before implementing apply/version switching, health validation or rollback. The owner handles commits and all remote Git/release operations. All implementation uses `master` only; do not create another branch.
