# Per-user Inno Setup installer — Stage 3

The installer is separate from portable publishing and the future updater. `publish.ps1 -Run` keeps its existing behavior. Building an installer never runs it, launches Fyltrix, installs dependencies, or publishes a release. Automatic installation remains disabled.

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

The builder compares every layout file with its ZIP and checks the ZIP against both recorded checksums. Unexpected files, modified payloads, unsafe paths, and source/output junctions are refused. This detects inconsistent build inputs; it does **not** authenticate a publisher. These installers are unsigned. Signing and authenticated release verification remain future work.

## Identities and paths

| Mode | Permanent AppId | Default installation directory | Start menu name |
|---|---|---|---|
| Sandbox | `Fyltrix.Sandbox.PerUser` | `%LocalAppData%\Programs\Fyltrix Installer Sandbox` | Fyltrix Installer Sandbox |
| Production | `Fyltrix.PerUser` | `%LocalAppData%\Programs\Fyltrix` | Fyltrix |

Setup requires no elevation and registers under the current user's Windows uninstall key. Both identities use the stable root `Fyltrix.exe` shortcut target and versioned `app-<version>` payloads. The desktop shortcut is optional and unchecked by default. Setup does not register startup tasks or replace file associations.

Choose an empty installation directory. Setup refuses existing unowned files, development checkouts, nested application copies, application-data overlaps, and junction/symlink paths. An existing owned directory must have the matching `.fyltrix-install.ini` AppId, mode, and schema. An identity already registered at another directory cannot be relocated by installing over it.

Sandbox identity isolates installation files, registration, and shortcuts. **A normally launched sandbox application still uses the existing application profile.** Only the test startup hook described below redirects that profile. Do not launch experiments against personal settings expecting the installer identity to provide profile isolation.

## Upgrade and uninstall behavior

Close the installed copy yourself before upgrading or uninstalling. Setup does not force-close applications. It checks the installation tree for locked or nonwritable files and refuses to continue when it cannot obtain exclusive access. Development and portable copies outside this installation are not closed.

An upgrade copies the new versioned payload, then the root launcher and version pointer. Earlier payloads remain recorded in the appended uninstall log. An older numeric Windows binary version is refused. This check does not order prereleases sharing the same numeric version.

This stage provides ordinary Inno installation behavior, **not** application-health rollback, atomic version switching, or tested recovery from power loss. The installer is not yet suitable for unattended updater handover. Runtime shutdown coordination and verified rollback are separate stages.

Uninstall removes files recorded by Inno, including payloads recorded by prior upgrades. There are no wildcard deletion rules. Untracked files in the installation tree remain, and can leave directories behind. A missing/mismatched ownership marker, redirected tree, or busy file prevents uninstall rather than relaxing the checks.

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

Stage 3 tested two Sandbox versions through silent native installation on this Windows machine. The production variant was compiled but not installed. Interactive wizard/optional desktop-shortcut selection, a separate standard-user account, signing/SmartScreen, power-loss recovery, and health rollback have not been verified. See [PROJECT_PROGRESS.md](PROJECT_PROGRESS.md) for executed results and approval gates.

Relevant Inno documentation: [per-user privileges](https://jrsoftware.org/ishelp/topic_setup_privilegesrequired.htm), [application closing](https://jrsoftware.org/ishelp/topic_setup_closeapplications.htm), [script event guards](https://jrsoftware.org/ishelp/topic_scriptevents.htm), and [appended uninstall logs](https://jrsoftware.org/ishelp/topic_setup_uninstalllogmode.htm).
