# Fyltrix rebranding verification — 2026-10-31

Fyltrix is the new display name. Launch the same executable at `D:\Tools\Filetryx\artifacts\FileLens\FileLens.exe`. The application, its logo, feature set, executable identity and data locations remain the same.

The workspace was already named `D:\Tools\Filetryx` when this task began; the previously supplied `D:\Tools\FileLens` path no longer existed. No project directories were renamed during this task.

## Checkpoints and release update

- Source checkpoint, created before edits: `D:\Tools\Fyltrix-rebrand-backup-20261031-113955-d9174001`. It includes source, tests, tools and documentation; generated build folders are excluded.
- Original complete portable release: `D:\Tools\Fyltrix-rebrand-work-20261031-b107b3d4\original-package`.
- The clean package and a copy of the existing release upgraded in place have identical **488-file** application inventories. Exactly **two application files differ** from the original release: `FileLens.exe` and `FileLens.dll`.
- Those two files were replaced in the existing `artifacts\FileLens` folder after checking that the release was not running and its files still matched the backup. Other release files were left alone. The final release hashes match the tested package. No application was opened on the user's desktop or launched against the real profile for testing.

## Branding changes

`FileLens.csproj` now supplies Fyltrix product, title, company and description metadata. `Core/ProductBranding.cs` reads and caches the assembly product name for display strings. The Windows executable reports **ProductName = Fyltrix** and **FileDescription = Fyltrix**.

Main/native window titles, About, Settings labels, close confirmations, command-palette category, tray labels/notifications, application error titles, diagnostics headers, conversion/archive/tag explanations, sharing UI and sharing-page footer now use Fyltrix. The embedded viewer's HTML title is Fyltrix viewer. Ordinary document/folder names supplied by users are never rebranded; the compatibility test deliberately restores a folder and Favourite named FileLens.

No file-operation, navigation, search, preview, comparison, clipboard, toolbar, settings, IDE, drive or sharing behavior was changed. The application defaults and settings model are byte-identical to the checkpoint.

## Preserved compatibility references

| Reference | Why it remains |
|---|---|
| `FileLens.exe`, `FileLens.dll`, `artifacts\FileLens` | Existing launch paths, portable upgrade layout and assembly binding remain compatible. There is no second `Fyltrix.exe`. |
| `FileLens` namespaces, project names, root namespace, startup object, friend assemblies, resource keys | Internal identifiers are independent of the display name. |
| Manifest identity `FileLens.app`, assembly/file versions `0.1.0.0` | Identity/version changes are outside this branding-only update. The manifest itself is unchanged. |
| `%AppData%\FileLens` | Settings, Favourites, Recent history, tabs, views, tags, templates, custom actions, automation, IDE preferences, sharing sessions and synchronization data keep their original stores and schemas. No migration is introduced. |
| `%LocalAppData%\FileLens` | WebView2, archive and thumbnail caches keep their established locations. |
| `https://filelens.app` | Private WebView2 origin/protocol dependency; changing it would be a functional change. |
| Firewall rule name `FileLens file sharing` | Persistent rule identity is used by existing rule lookup/removal. **The technical name can still appear in Windows Firewall**, intentionally, to avoid changing networking behavior or duplicating rules. Display explanations use Fyltrix. |
| `Assets/FileLens.ico`, SVG/PNG asset names, SVG title and logo-generator names | Logo/icon files and their resource identities are unchanged. The generator was not run. All logo and Office template assets match the checkpoint. |
| Temporary staging/cache prefixes, debugger thread label, test namespaces/fixtures and historical documentation | Technical or historical references, not current application display branding. |
| Actual FileLens paths/filename shown in log-folder help and firewall confirmation | They describe the real compatibility path or executable rather than a new product name. |

The actual user's `settings.json` SHA-256 hash is unchanged from the beginning of the task. A separate rich legacy profile was loaded and saved in a temporary `AppData\FileLens` directory; every supplied value round-tripped unchanged, with no new Fyltrix configuration directory. Restored tabs, Favourites, Recent entries, per-folder views, window preferences, toolbar/menu choices, custom actions, saved searches and IDE entries were exercised without reading or modifying the real user's preferences.

## Build, package and regression checks

- Debug build: passed.
- Release build: passed.
- Self-contained Windows x64 publish: passed for clean and portable-upgrade test folders.
- Full suite: **346 passed, 0 failed, 0 skipped**.
- Published-package checks: **3 rebranding tests passed against each package**, loading the actual published Release assembly and dependencies rather than substituting the Debug application assembly.
- Native `GetWindowText` and managed window title both end with Fyltrix. Settings text and the shared-page footer use the new name.
- The icon embedded-resource hash remains `77AE6883F66A7FD51A9E0B81C51A2E1AEE7974229FC4E6DB1519DD737479D907`. **14 logo/template files** and **21 protected implementation/identity files** were verified unchanged.
- Off-screen screenshots inspected: `%TEMP%\FileLensTests-shots\fyltrix-restored-tabs.png` and `fyltrix-settings.png`.
- Existing nullable/DPI build warnings remain; no unrelated warning fixes were made.

The initial attempt to run the test host inside a self-contained publish folder used a framework-dependent native test host, which incorrectly treated the package directory as its .NET installation. The verification harness was corrected to use the installed dotnet host without replacing any published assembly. Both package test runs then passed; this was a test-harness issue, not an application startup failure.

Five sequential resource benchmark runs before and after, with the same isolated dataset, preview on, cloaked windows and a private clipboard provider:

| Median measurement | Before | Fyltrix |
|---|---:|---:|
| Startup to listing | 1.341 s | 1.212 s |
| Folder navigation | 133.3 ms | 132.1 ms |
| 20,000-file folder | 274.3 ms | 270.8 ms |
| Complete 9,330-result search | 252.6 ms | 241.3 ms |
| Initial app working set | 146.4 MB | 145.6 MB |
| Initial handles | 787 | 787 |
| Initial GC heap | 8.5 MB | 8.4 MB |
| Idle file operations | 0 | 0 |
| Whole-run disk writes | 1.3 MB | 1.3 MB |
| WebView2 children after hiding preview | 0 | 0 |

These figures show no measured regression; small differences are ordinary timing/resource variability, not an optimization claim. No features were capped or throttled. Raw runs and medians are in [REBRANDING_BENCHMARK.json](REBRANDING_BENCHMARK.json); the compatibility/hash audit is [REBRANDING_AUDIT.json](REBRANDING_AUDIT.json).

## Installation identity and remaining manual checks

This repository distributes a portable folder through `publish.ps1`; it contains **no MSI/MSIX/Inno/WiX installer, registered updater, upgrade GUID or installer shortcut-generation mechanism**. No matching installed-app entry, Run-key registration, Desktop/Start Menu/Startup/pinned shortcut, or scheduled startup task was found on this machine. Consequently an installer-engine upgrade/uninstall test is not applicable. The existing-folder portable upgrade was verified, with its filename, file layout and data paths preserved and no installation/shortcut/association registration added.

The real taskbar/Alt+Tab rendering, tray balloons, any shortcuts kept outside the standard inspected locations, and interactive About presentation still merit a hands-on check. Native title text, executable metadata, display-string sites and unchanged icon resources are verified; tests deliberately avoid taking desktop focus. Optional single-file publishing was not separately exercised; its filename, publishing switches and logic were left unchanged.

## Files changed

Display text changes are confined to the reviewed application-string sites. Build-generated `bin`/`obj` files are not part of this source list.

```text
README.md
publish.ps1                         (branding comments only; executable/output paths unchanged)
docs/IMPLEMENTATION_STATUS.md
docs/REBRANDING_REPORT.md
docs/REBRANDING_AUDIT.json
docs/REBRANDING_BENCHMARK.json
src/FileLens/FileLens.csproj         (display/version-resource metadata only)
src/FileLens/App.cs
src/FileLens/Core/ProductBranding.cs (new cached display-name accessor)
src/FileLens/Core/Diagnostics.cs
src/FileLens/Core/Sharing/ShareHost.cs
src/FileLens/Core/Sharing/ShareNetwork.cs
src/FileLens/Preview/PreviewPane.cs
src/FileLens/Viewer/viewer.html
src/FileLens/UI/AppIcon.cs
src/FileLens/UI/ArchiveDialogs.cs
src/FileLens/UI/AutomationWindow.cs
src/FileLens/UI/ChecksumWindow.cs
src/FileLens/UI/ConvertWindow.cs
src/FileLens/UI/FolderSizeWindow.cs
src/FileLens/UI/MainWindow.cs
src/FileLens/UI/MainWindow.Palette.cs
src/FileLens/UI/MainWindow.Places.cs
src/FileLens/UI/MainWindow.Sharing.cs
src/FileLens/UI/MainWindow.Tools.cs
src/FileLens/UI/SearchDialog.cs
src/FileLens/UI/SettingsWindow.cs
src/FileLens/UI/ShareWindow.cs
src/FileLens/UI/SharesWindow.cs
src/FileLens/UI/Shell.cs
tests/FileLens.Tests/DiagnosticsTests.cs
tests/FileLens.Tests/RebrandingTests.cs
tests/FileLens.Tests/ShareServerTests.cs
artifacts/FileLens/FileLens.exe      (existing release updated in place)
artifacts/FileLens/FileLens.dll      (existing release updated in place)
```
