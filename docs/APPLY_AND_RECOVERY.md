# Verified apply and recovery — Stage 6

This stage adds the update backend to the existing self-contained application. Inno Setup remains the separate installer; no update framework, service, scheduled task, automatic check, or automatic installation is added. The current source pins the dedicated owner-managed Fyltrix public key, and production workers continue to refuse untrusted packages. Earlier builds without pins require manual bootstrap installation. The end-user update action and broader end-to-end approval belong to Stage 7.

## Compatibility boundary

Both the installed layout and candidate ZIP must declare the exact `UPDATE_PROTOCOL` contract (`Fyltrix.Update/1` and `FileLens.Profile/1`) and include `startup.lock`. Older layouts still launch normally, but need an explicit manual bootstrap installation before managed updates. The stable native launcher is retained during an update; changing its protocol requires a reviewed installer upgrade.

`Fyltrix.Bootstrap` shares the existing `Fyltrix.Launcher` layout API, startup gate and health-pipe protocol without an executable project reference. Bootstrap/Updates assembly identities stay at `1.0.0.0`; application and file/product versions are stamped by packaging. This avoids incompatible component binding and duplicate executable publish outputs. No NuGet dependency was added.

Normal protocol-aware launcher and payload processes hold shared startup leases. The worker and Inno installer/uninstaller need exclusive access. An outstanding transaction journal blocks startup, including direct payload launch. Legacy development/portable startup without the capability retains its existing behavior. `publish.ps1 -Run` is unchanged.

## Explicit handover and apply

1. Use the [authenticated staging and normal-close boundary](MANUAL_UPDATES.md). A prepared request is inert; the actual application's successful normal close must create its approval token. Cancelled closes and active work remain authoritative.
2. Start a worker using `UpdateWorker.StartInfo(prepared)` from the old self-contained payload. It independently verifies signed stage evidence, approval, request/process identities and package bytes, then waits for the recorded launcher/payloads to exit. It never kills an existing application.
3. Acquire the startup gate, refuse other Fyltrix/FileLens processes, existing transaction/target directories, redirected paths and insufficient space. Managed apply accepts the versioned ZIP, not Setup. Setup assets can be authenticated separately for manual installation.
4. Hold read-only leases on the live roaming-profile files and create separate snapshot and trial copies in the owned stage. Refuse links, inaccessible files, more than 10,000 files or 256 MiB. Never restore, migrate or overwrite the live profile.
5. Flush a bounded transaction journal, extract only the new versioned payload with safe paths and exclusive new files, and record its exact paths/SHA-256 hashes for later uninstall. Keep old payloads and the stable root launcher intact.
6. Launch only the candidate as a bounded isolated trial. A current-user named pipe checks Windows process IDs, nonce, version and profile contract. The trial uses copied settings and separate WebView2/thumbnail/archive caches, an empty clipboard provider, disabled automation and an offscreen window. It must strictly load settings, finish initial folder listing, acknowledge readiness, close normally and exit zero. A failed/timed-out trial is the only process tree the worker may terminate.
7. Recheck live-profile contents, processes, old code hashes and exact pointer bytes. Atomically replace `current.json` only after health succeeds; retain the old version as `previous`. Record commit before finishing installation metadata. Release the gate, then restart through the root launcher.

The health check proves startup/settings/listing compatibility. It does not certify every preview codec, integration, external resource or user action. Copied profiles can contain personal data: retained stage snapshots should be treated as private backups, not release assets.

## Failure and recovery

A precommit failure restores the exact previous pointer if the recorded code and pointer still match. It leaves live data untouched and retains the candidate/evidence for diagnosis. It does not automatically reopen potentially incompatible live settings; explicitly restart the previous version after reviewing the result. Unexpected modifications retain the journal and require recovery instead of guessing.

The existing old-payload executable exposes internal worker modes before any WPF/profile startup:

```powershell
# Developer/maintenance interface, only with an authenticated, close-approved handover.
& 'D:\Fyltrix installation\app-0.1.0\Fyltrix.exe' `
    --fyltrix-update-worker 'D:\Update staging\stage-<token>\handover.json' '<token>'

# After interruption, use that SAME old payload, request and token.
& 'D:\Fyltrix installation\app-0.1.0\Fyltrix.exe' `
    --fyltrix-update-recover 'D:\Update staging\stage-<token>\handover.json' '<token>'
```

These are backend interfaces, not a complete end-user workflow. They accept no test-key option; production trust must already be configured by a separately reviewed source change. The example token is a placeholder for the original 32-character request nonce. Do not manufacture approval, delete a journal to bypass recovery, or edit the pointer manually.

Recovery reauthenticates retained signed evidence and approval, verifies ownership, protocol, journal, exact pointer and old-code hashes, and takes the exclusive gate. Before commit it restores the original pointer; after commit it verifies the complete signed candidate and finishes metadata without reverting healthy code. Changed committed files or unknown pointer state are refused and the journal remains. Recovery does not launch the application or write its profile.

Worker exit codes: `0` committed, `3` rolled back, `2` refused or recovery required. Where the stage can be safely accessed, `update-result.json` contains the status/reason. An exception before a safe result write may produce only exit code 2. Retain the original stage and old payload until recovery is complete.

## Installer ownership and retained files

`update-files.ini` is a bounded, owner-bound UTF-16 INI inventory of updater-added payload paths/hashes. Inno uninstall supplements its normal log with this list, removes only unchanged recorded updater files, and keeps modified or unknown files. No wildcard cleanup is used. The ownership marker tracks committed numeric version, so a retained older launcher cannot allow a manual installer downgrade. HKCU DisplayVersion refresh is best effort and only targets the matching registered installation.

Successful/failed payloads, snapshots and staging are retained; automatic pruning is not implemented. An interrupted partial extraction can leave generated pending files for manual review. Settings stay in `%AppData%\FileLens`, existing caches stay in `%LocalAppData%\FileLens`, and updater staging remains separate in `%LocalAppData%\Fyltrix\Updates`.

## Verification

Run the update, packaging and application test projects, then build two isolated layouts, their Sandbox installers and the Release PlacementProbe. For real installed apply/rollback/recovery testing:

```powershell
python .\tests\installer\verify_apply.py `
    --old-release 'D:\Packages\0.1.0' --new-release 'D:\Packages\0.1.1' `
    --installer 'D:\Sandbox installer\Fyltrix-Sandbox-Setup-win-x64.exe' `
    --probe '.\tests\Fyltrix.PlacementProbe\bin\Release\net8.0-windows10.0.19041.0\Fyltrix.PlacementProbe.dll' `
    --work-root 'D:\Fresh apply evidence'
```

Run non-elevated with no Sandbox registration and a new external work directory. The test validates Sandbox artifacts, creates disposable signing trust, deletes its private key before apply, uses synthetic profiles/clipboard and private desktops, and never executes Production Setup. The test-only hook/key override is not shipped. Scenarios cover success, incompatible trial settings, interruption during pointer switch, interruption after commit, explicit recovery, real restart/preferences/preview, production-trust refusal, downgrade refusal and uninstall retaining modified/unknown files.

Executed evidence and limitations are recorded in [PROJECT_PROGRESS.md](PROJECT_PROGRESS.md). Abrupt worker exits are tested; physical power loss, all filesystem/storage failure modes, Authenticode/SmartScreen, a separate Windows account and a live trusted public release are not certified by these tests. Automatic installation remains disabled.
