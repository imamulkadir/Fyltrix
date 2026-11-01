# Manual application updates, staging and safe handover

Stage 5 extends the separate [release verification library and tool](UPDATES.md). Its download/staging and verified handover APIs do not apply an update. Stage 6 adds a separate [verified apply, isolated health, rollback and recovery backend](APPLY_AND_RECOVERY.md). Stage 7B connects them to the manual interface described below. This interface is new source work after published `v0.1.5`; that existing release does not gain new controls remotely.

There is no startup scheduler, service, new settings key, or automatic check/download/install. Ordinary closing, active-work guards and `publish.ps1 -Run` retain their behavior.

## In-app workflow

1. Open **Tools → Check for updates**, or find **Check for updates** in the command palette (`Ctrl+Shift+P`). Opening the dialog does not contact GitHub.
2. Click **Check for updates**. Only newer stable published releases with an authenticated manifest and a supported ZIP can enable downloading. Release notes are plain text. Rate limits are respected without background retries.
3. Click **Download**. Progress is displayed; closing the dialog cancels an active check/download. A completed download is not installation approval. It stays available in the same application session when the dialog is reopened.
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
