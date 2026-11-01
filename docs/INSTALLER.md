# Per-user Inno Setup installer — Stage 3

The installer is separate from portable publishing and the [explicit update backend](APPLY_AND_RECOVERY.md). `publish.ps1 -Run` keeps its existing behavior. Building an installer never runs it, launches Fyltrix, installs dependencies, or publishes a release. Automatic installation remains disabled.

## Compile

Use Windows, Inno Setup 6, and an intact release directory produced by [Publish-Layout.ps1](RELEASE_LAYOUT.md). The release contains `layout`, its ZIP/checksum, and `build-info.json`.

```powershell
# Sandbox is the default. Use a fresh output directory for each compilation.
powershell -ExecutionPolicy Bypass -File .\tools\Build-Installer.ps1 `
    -ReleaseDirectory 'D:\Fyltrix packages\0.1.0'

# Compile the production identity only; this command does not install it.
powershell -ExecutionPolicy Bypass -File .\tools\Build-Installer.ps1 `
    -ReleaseDirectory 'D:\Fyltrix packages\0.1.0' -Mode Production
```

Optional parameters: `-OutputDirectory` and `-IsccPath`. Default output is `installer-Sandbox` or `installer-Production` inside the release directory. Existing output directories are refused. Output includes Setup, a SHA-256 sidecar, `installer-build.json`, and the installation ownership marker used by Setup.

The builder compares every layout file with its ZIP and checks the ZIP against both recorded checksums. Unexpected files, modified payloads, unsafe paths, and source/output junctions are refused. Current builds require the protocol-aware layout produced by the current packaging script. This detects inconsistent build inputs; it does **not** authenticate a publisher. These installers are unsigned; [signed release verification](UPDATES.md) is separate from Windows Authenticode signing.

## Identities and paths

| Mode | Permanent AppId | Default installation directory | Start menu name |
|---|---|---|---|
| Sandbox | `Fyltrix.Sandbox.PerUser` | `%LocalAppData%\Programs\Fyltrix Installer Sandbox` | Fyltrix Installer Sandbox |
| Production | `Fyltrix.PerUser` | `%LocalAppData%\Programs\Fyltrix` | Fyltrix |

Setup requires no elevation and registers under the current user's Windows uninstall key. Both identities use the stable root `Fyltrix.exe` shortcut target and versioned `app-<version>` payloads. The desktop shortcut is optional and unchecked by default. Setup does not register startup tasks or replace file associations.

Choose an empty installation directory. Setup refuses existing unowned files, development checkouts, nested application copies, application-data overlaps, and junction/symlink paths. An existing owned directory must have the matching `.fyltrix-install.ini` AppId, mode, and schema. An identity already registered at another directory cannot be relocated by installing over it.

Sandbox identity isolates installation files, registration, and shortcuts. **A normally launched sandbox application still uses the existing application profile.** Only the test startup hook described below redirects that profile. Do not launch experiments against personal settings expecting the installer identity to provide profile isolation.

## Agreement and Windows publisher warnings

Interactive Setup displays the owner's selected [MIT licence](../LICENSE) before the destination-directory page. Next remains disabled until the user accepts; cancelling before installation creates no installation or registration. The project copies the same source terms into application build/publish outputs as `LICENSE.txt`, and Inno reads that copy from the validated versioned payload. Standard explicitly requested silent installation retains Inno's silent-mode behavior. Existing release assets do not change when source is edited; this applies to a subsequent rebuilt installer.

The release-signing key authenticates the update manifest/packages. It is **not an Authenticode certificate** and does not establish a Windows verified-publisher identity. Current Setup executables are `NotSigned`; downloaded files can trigger SmartScreen reputation or unknown-publisher warnings. A trusted Authenticode signing identity is a separate setup requirement, and even a newly signed file can still warn while reputation develops. No certificate is purchased/generated, Windows security policy changed, or warning bypass implemented by these installer-page changes.

References: [Inno LicenseFile and page order](https://jrsoftware.org/ishelp/topic_setup_licensefile.htm), [Microsoft SmartScreen reputation](https://learn.microsoft.com/windows/apps/package-and-deploy/smartscreen-reputation).

To check the actual wizard without installing anything, compile a Sandbox installer and run non-elevated:

```powershell
python .\tests\installer\verify_license.py `
    --installer 'D:\Sandbox output\Fyltrix-Sandbox-Setup-win-x64.exe' `
    --work-root 'D:\Fresh agreement verification'
```

The harness validates Sandbox identity/checksum, opens only its own private desktop, verifies the MIT text/acceptance gate/directory-page order, then cancels and checks no installation was created. It does not run Production Setup or interact with the user's desktop.

## Upgrade and uninstall behavior

Close the installed copy yourself before upgrading or uninstalling. Setup does not force-close applications. It checks the installation tree for locked or nonwritable files and refuses to continue when it cannot obtain exclusive access. Development and portable copies outside this installation are not closed.

An upgrade copies the new versioned payload, then the root launcher and version pointer. Earlier payloads remain recorded in the appended uninstall log. An older numeric Windows binary version is refused. This check does not order prereleases sharing the same numeric version.

Inno upgrades retain ordinary installer semantics; copied-profile health validation, atomic pointer switching and journal recovery belong to the separate Stage 6 backend. Setup/uninstall now take the same exclusive startup gate and refuse pending update journals. The committed numeric version in the ownership marker also prevents downgrade when the stable launcher was retained by an update. Installer completion refreshes that marker. Older installations need an explicit manual bootstrap installer before managed updates; do not add a capability file to an old payload by hand. Physical power-loss recovery and unattended installation are not certified.

Uninstall removes files recorded by Inno, including payloads recorded by prior upgrades. It also validates the bounded owner-bound `update-files.ini` inventory and removes updater-added files only when their recorded hashes still match. Modified updater-owned and unknown files remain and can leave directories behind. There are no wildcard deletion rules. A missing/mismatched ownership marker, redirected tree, busy file or invalid inventory prevents uninstall rather than relaxing the checks.

Settings and persisted data remain at `%AppData%\FileLens`; caches remain at `%LocalAppData%\FileLens`. Neither installer mode relocates, migrates, resets, or removes them. No machine-specific settings, Recent entries, Favourites, or personal paths are bundled. Existing program-scoped firewall permissions can require reauthorization when an executable path changes; this installer does not change firewall policy. WebView2 and optional codecs retain their existing prerequisite behavior.

## Reproduce verification

Run fast builder guard tests on Windows with Python 3.11 or newer:

```powershell
python -m unittest discover -s tests/installer -p test_builder.py -v
```

For the lifecycle test, compile two **Sandbox** installers from different numeric release versions and build the existing probe:

```powershell
dotnet build .\tests\Fyltrix.PlacementProbe\Fyltrix.PlacementProbe.csproj -c Release
python .\tests\installer\verify_installer.py `
    --old 'D:\Installer evidence\v1\Fyltrix-Sandbox-Setup-win-x64.exe' `
    --new 'D:\Installer evidence\v2\Fyltrix-Sandbox-Setup-win-x64.exe' `
    --probe '.\tests\Fyltrix.PlacementProbe\bin\Release\net8.0-windows10.0.19041.0\Fyltrix.PlacementProbe.dll' `
    --work-root 'D:\Installer evidence\fresh-lifecycle-run'
```

Run non-elevated, with no existing Sandbox registration/shortcut and a new external work directory. The harness validates Sandbox identity/checksums before executing anything. It runs native Setup and the actual installed application on a private desktop, using a scoped .NET startup hook, synthetic profile, WebView2 data, and clipboard provider. The hook is test-only and is not shipped in the application payload. The harness does not run production installers.

Checks include protected/unowned destinations, identity/schema validation, junctions, locked files, running-app refusal, shortcut targeting, numeric downgrade refusal, upgrade retention, actual application startup/preferences/preview, uninstall cleanup, and retention of unknown files and personal profile hashes. Logs and `verification.json` stay in the supplied work directory. Test-added unknown files deliberately remain after uninstall.

Stage 3 tested two Sandbox versions through silent native installation on this Windows machine. Stage 6 adds installed apply/rollback/interrupted-worker recovery and inventory-cleanup tests. The production variant is compiled only, never installed by these tests. Interactive wizard/optional desktop-shortcut selection, a separate standard-user account, signing/SmartScreen and physical power-loss recovery remain unverified. See [PROJECT_PROGRESS.md](PROJECT_PROGRESS.md) for executed results and approval gates.

Relevant Inno documentation: [per-user privileges](https://jrsoftware.org/ishelp/topic_setup_privilegesrequired.htm), [application closing](https://jrsoftware.org/ishelp/topic_setup_closeapplications.htm), [script event guards](https://jrsoftware.org/ishelp/topic_scriptevents.htm), and [appended uninstall logs](https://jrsoftware.org/ishelp/topic_setup_uninstalllogmode.htm).
