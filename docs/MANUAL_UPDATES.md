# Manual application updates, staging and safe handover

Stage 5 extends the separate [release verification library and tool](UPDATES.md). Its download/staging and verified handover APIs do not apply an update. Stage 6 adds a separate [verified apply, isolated health, rollback and recovery backend](APPLY_AND_RECOVERY.md). Stage 7B connects them to the manual interface described below. This interface is new source work after published `v0.1.5`; that existing release does not gain new controls remotely.

The approved notification plan adds scheduled checks and an update-mode preference. Stage 2 enables optional automatic downloads with manual installation; Stage 3 adds opt-in installation on a safe normal exit. There is no service or startup registration. Notify only remains the default, and `publish.ps1 -Run` is unchanged.

## Settings and automatic notifications — notification plan, Stage 1

**Settings → Updates** shows current version, last check, last verified available version and current status. It offers Check now plus the existing manual release-notes/download/install window. Saving an update preference applies it immediately; editing then cancelling Settings does not apply it.

- **Notify only** is the owner-approved default for new/missing update preferences. A single cancellable delay waits 60 seconds after startup, then checks only when the last attempt is at least six hours old. Checks run while Fyltrix is open, respect saved GitHub backoff across restarts, and avoid interrupting active/prepared downloads.
- **Manual only** schedules no checks. The user can still choose Check now.
- **Automatic download, manual installation** checks on the same schedule and downloads a newer authenticated ZIP into separate updater storage when the running copy supports in-place updates. Enabling this option can also download a release already verified in this session. It never starts an installer or closes the application automatically.
- **Automatic download and install on exit** downloads on the same schedule, then applies a verified package only on an accepted, safe normal close. Selecting/saving this mode is the opt-in permission; it does not install on startup or download completion. The application stays closed after installation.

A new authenticated update produces a nonmodal banner with View update, Later and Settings. Notification history suppresses repeated availability notices for the same version across normal restarts; the available version remains visible in Settings. Notify only checks metadata/signatures without downloading package payloads. Automatic download changes the banner to downloading, then ready to install after verification; readiness is a separate notice from availability. Notes are unsigned plain text.

The new `UpdateMode` preference uses the existing settings file, with other preferences preserved; malformed update-mode values fall back to Notify only without resetting other settings. Check time, backoff and notification history are separate updater-only data in `%LocalAppData%\Fyltrix\Updates\notification-state.json`. Background checks never call `AppSettings.Save`. State files use a product/schema marker and atomic writes; unowned/corrupt files are left untouched. No data is migrated. As with any optional cache, a failed history write/crash may cause a later repeat notice.

## Automatic download with manual installation — notification plan, Stage 2

Choose **Automatic download, manual installation** in Settings > Updates and save. Progress appears in the status there and in the update dialog. Settings offers **Cancel download** during an active transfer. Switching to Notify only/Manual only cancels an automatic transfer; changing update mode does not cancel a manually requested download. Saving an unrelated preference does not restart checks or cancel the transfer. Closing the update dialog explicitly cancels its current check/download, including an automatic transfer, as its help text explains. In this mode normal application exit cancels ongoing transfers and never installs.

Downloads use the existing signed-manifest, bounded-transfer, free-space, package hash and ZIP-layout checks. Failure/cancellation does not install or repeatedly retry: use the manual Download action to retry now, or wait for the next scheduled check. Automatic download is unavailable to unsupported/development layouts even when selected; those copies retain the manual installer link. The default remains Notify only.

After verification, choose **View update**, then **Install and restart** and confirm when ready. A ready package is retained rather than discarded by Check now; scheduled checks pause while it is ready. Both manual and automatic downloads record only their owned `stage-<id>` leaf name in updater metadata. At a later start, including Manual only, the package is reauthenticated and hashed off the UI thread before installation becomes available. Absolute/traversing names, untrusted/modified packages and versions at or below the running version cannot enable installation. The saved pointer is cleared when invalid; package files are not deleted. A dismissed ready notice stays dismissed after a normal restart when history was saved; Settings still shows the ready state. If history cannot be written or the process is interrupted before it is saved, manual redownload may be necessary. Existing settings/profile files are never moved, restored or reset by this work.

## Optional installation on safe exit — notification plan, Stage 3

Choose **Automatic download and install on exit** and save. It retains the same authenticated download, cancellation and saved-package checks. A ready banner explains that installation is waiting for a safe exit. Manual **Install and restart** remains available and still asks for confirmation.

On a normal close, the existing tab/sharing choices and all ordinary Closing handlers run first. Cancellation authorizes no preparation. A ready supported-layout package can then use the existing worker only if there are no owned dialogs/tools, tracked writes/jobs/pastes, enabled/running automation, active shares or background sharing. Busy/unsupported/no-ready cases retain ordinary close behavior and skip the update; they are not forced closed by the updater. Keeping shares in the tray or later tray-driven exit does not install. Windows SessionEnding suppresses the exit update and cancels pending preparation; a forced process termination has no accepted-close token.

Preparation rechecks the running layout, package and opt-in/safety state before closing through the existing guarded handover. Tab confirmation is not asked twice, but later close vetoes and work beginning during preparation remain authoritative. Repeated close requests are ignored while preparation runs. A failure leaves Fyltrix open with a postponed status; the next accepted close exits without retrying. Previous handover/result evidence also prevents an automatic repeat; review the explicit recovery procedure for interrupted attempts instead of deleting evidence.

The exit worker receives `--no-restart` and omits `--notify`: success keeps the app closed, and failure cannot introduce a modal warning after the user has exited. The existing `update-result.json` and transaction evidence record failures. A failed isolated health trial rolls back code; live preferences are never restored/reset. Manual worker calls retain their old restart/notification behavior and argument/API compatibility. No background installer runs merely because a release is available.

## In-app workflow

1. Open **Tools → Check for updates**, or find **Check for updates** in the command palette (`Ctrl+Shift+P`). Opening the dialog does not contact GitHub.
2. Click **Check for updates**. Only newer stable published releases with an authenticated manifest and a supported ZIP can enable downloading. Release notes are plain text. Rate limits are respected without background retries.
3. Click **Download**, or use the automatically downloaded package when that preference is enabled. Progress is displayed; closing the dialog cancels an active check/download. A completed download is not installation approval. It stays available when the dialog is reopened and can be restored after restart following fresh verification.
4. Finish file operations, close other dialogs/tools, stop sharing and turn off automation before choosing **Install and restart**. Confirm installation explicitly. The existing normal-close confirmation/cancellation remains authoritative; active work is not killed to force an update.
5. The old payload runs the existing worker, independently revalidates the package and close approval, trials the candidate with copied settings/separate caches, then commits and requests restart. Failed health validation keeps the old version selected; live preferences are never restored from the trial copy. Worker failures after approved close may show a notification. See [apply/recovery](APPLY_AND_RECOVERY.md) for interrupted-transaction recovery.

Copies without the supported versioned layout, including development checkouts and ordinary `publish.ps1` output, can check releases and read plain-text notes but cannot install in place. The dialog links to GitHub Releases for a manual installer. Existing published `v0.1.5` users need a later reviewed bootstrap installer containing this interface before they can use it. Package signatures authenticate package bytes; GitHub release notes are separate, unsigned metadata and are never rendered as HTML.

Checks/downloads cannot overlap, and install preparation blocks further session operations. Failed/cancelled close leaves the application open and retains the verified download; the worker may take up to 30 seconds to exit before retrying. Completed staging directories are not automatically pruned. No executable-signing certificate is introduced; Windows publisher warnings remain separate from update-manifest authentication.

## Manual downloads

`UpdateDownloader.DownloadAsync` requires a newer stable release and authenticates its manifest using application-pinned keys **before** requesting package bytes or creating staging directories. The current source pins the dedicated owner-managed Fyltrix public key; unknown signatures remain refused. Earlier builds without pins return `NoTrustedKey` and need manual bootstrap installation. The private key is never shipped; no test/reference key is pinned. See [SIGNING_SETUP.md](SIGNING_SETUP.md).

The downloader reuses Stage 4's HTTPS/redirect policy. It checks available disk space against the signed package size plus 200 MiB, then streams to a new `.partial` file using a 64 KiB buffer. It rejects contradictory Content-Length, early EOF, excess bytes, invalid hashes and invalid PE/ZIP contents. Unknown Content-Length is supported. There is a 30-second inactivity timeout, not a whole-download deadline; caller cancellation propagates separately. Progress reports are limited to roughly ten per second plus completion.

The same exclusive file handle is used to write and verify the complete package. Only after verification succeeds is the partial file renamed without overwriting an existing file. The exact signed manifest/signature and bounded release metadata are saved beside it for independent verification by a later process. Neither progress nor a filename is an installation authorization.

## Separate staging data

Default root: `%LocalAppData%\Fyltrix\Updates`. This is new updater-only storage. Existing settings stay in `%AppData%\FileLens`, and existing caches stay in `%LocalAppData%\FileLens`; nothing is moved or reset.

Each operation gets a unique directory:

```text
stage-<random-token>/
  .fyltrix-update-stage       Ownership token
  Fyltrix-<version>-win-x64.zip  One verified package (or the supported Setup asset)
  release.json               Metadata required to bind the signed assets
  Fyltrix-release.json       Exact signed bytes
  Fyltrix-release.json.sig
  handover.json              Created only by explicit preparation
  close-approved            Created only after the close callback confirms actual closure
```

Staging rejects relative/UNC paths, drive roots, application-data overlaps, development checkouts, existing application copies, and junction/symlink traversal. Windows 8.3 aliases are expanded before protected-path comparisons. Cancellation/failure removes only known files in the operation's token-verified directory; unrelated files/directories and earlier successful stages are not pruned. If ownership or cleanup cannot be verified, the remaining files are retained rather than deleting an arbitrary tree. Interrupted staging is not resumed automatically.

## Application shutdown boundary

`MainWindow.TryCloseForUpdate` is an internal seam for the later explicit update action. It refuses to close when:

- A file job, paste, transfer, delete/undo, conversion, image save, PDF export, or automation write is still active.
- Automation remains enabled or its engine is active.
- Shares remain active or the application is sharing in the background.
- Another tool/dialog is open or the window is disabled by an interaction.

Work is tracked until its asynchronous continuation finishes, even if a tool window or automation engine has been closed/disposed. The guard does not cancel jobs, stop shares, disable automation, or change preferences. The user finishes work and closes tools or disables automation through existing controls.

When preflight succeeds, the method calls normal `Window.Close()`. Existing tab confirmation and `Closing` cancellation remain authoritative. Safety is checked again after a tab confirmation, because its modal loop can allow state changes. Acceptance means the real `Closed` event occurred; hiding a window is insufficient. `Application.Shutdown` and process termination are not used.

## Handover boundary

`UpdateHandover.PrepareAsync` rechecks staged evidence and package bytes, validates the existing versioned layout/current payload, and requires a newer target. It records identities for all inspectable Fyltrix launcher/payload instances in that layout, including process start time to detect PID reuse. Missing payloads, fallback layouts, redirected paths, and an existing request are refused. Preparation alone is inert.

`RequestCloseAsync` invokes the caller's normal-close callback on its existing synchronization context. Only after actual close acceptance and a final cancellation check does it flush and atomically rename the small `close-approved` token synchronously, before the application message loop can end. A rejected/cancelled close leaves the download available for retry and attempts to remove only its unchanged request. Even if that request cannot be removed, it remains unapproved and cannot become ready. Cancellation arriving during a close confirmation cannot approve the handover; if the window already closed, it can be reopened normally.

`WaitForReadyAsync` is the out-of-process consumer boundary. It independently checks bounded request/evidence files and signing trust, obtains a read-only package lease that denies writers/deletion on Windows, verifies the package again, then waits for all recorded processes to exit using `WaitForExitAsync`. Timeout defaults to 30 seconds; cancellation/timeout never kill a process. It checks process identities, unchanged request/approval/current layout, and scans once more for a newly started instance of this layout before returning `ReadyHandover`.

The returned lease must be disposed by its consumer. **Readiness is not installation, rollback, or a lock on the entire application directory.** The Stage 6 consumer revalidates the held package, obtains an exclusive cooperative startup gate, preserves user data and implements recovery. The staging/readiness APIs themselves do not launch an installer or executable.

## Tool commands

Build the tool as described in [UPDATES.md](UPDATES.md):

```powershell
$tool = '.\tools\Fyltrix.UpdateTool\bin\Debug\net8.0\Fyltrix.UpdateTool.dll'

# Explicit live discovery and verified ZIP download. No installation.
dotnet $tool download 0.1.0 'D:\Fyltrix update staging'

# Offline copy through the same staging/verification path, with explicit owner/test trust.
dotnet $tool stage-local 0.1.0 'D:\Update tests\release.json' `
    'D:\Update tests\Fyltrix-release.json' 'D:\Update tests\Fyltrix-release.json.sig' `
    'D:\Update tests\Fyltrix-0.1.1-win-x64.zip' 'D:\Update tests\public-test.txt' `
    'D:\Fyltrix update staging'
```

`stage-local` never contacts metadata URLs and cannot configure production pins. `prepare <staging-directory> <layout-root> [test-public-key.txt]` creates an inert request for a running versioned layout. `wait <handover.json> [test-public-key.txt]` exercises the readiness consumer, reports JSON, disposes the lease and exits without applying anything. The optional key is explicitly owner/test trust; the default uses production pins. These commands do not close the application or manufacture approval. Without the normal-close callback's approval, waiting cannot produce a ready result. They are developer seams, not an end-user update workflow yet.

## Verification and limits

The update suite covers authenticated transport/copy, truncated/oversized/corrupted responses, wrong lengths, disk-space refusal, cancellation, stalls, protected/junction paths, staged-evidence validation, real test-owned launcher/payload processes, PID reuse, timeout, cancelled close, locked request cleanup, held package leases, and changed layout/manifest/package inputs. WPF tests cover real offscreen window closing and ongoing work, including image saves after tool closure and writes after automation disposal. Tests use temporary profiles, isolated clipboard providers, loopback-only shares when needed, and private desktops for executable smoke tests.

A private-desktop integration stages a real release ZIP with a disposable key, starts the actual native launcher and WPF payload, calls the normal update-close seam, and verifies a separate helper reports readiness only after both exit. It asserts the layout and synthetic preferences remain intact. It does not install a new version or certify upgrade/rollback compatibility.

Stage 6 separately verifies installed Sandbox apply, interrupted recovery and health rollback; see [APPLY_AND_RECOVERY.md](APPLY_AND_RECOVERY.md). Production signing trust, real authenticated live releases, application update UI and automatic installation remain gated. The Production installation and existing portable/development copy are not replaced. Executed results and evidence: [PROJECT_PROGRESS.md](PROJECT_PROGRESS.md).
