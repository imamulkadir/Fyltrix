# Windows taskbar identity and launch verification

Released in 0.1.8 on 2026-11-02. The published update feed is verified; the agent has not replaced the owner's installed application. Installing the published Setup to refresh shortcuts, refreshing old pins, and the real Windows taskbar review remain owner actions. The findings below record the original 0.1.6 installation.

## Findings

The stable launcher and versioned WPF payload previously had no explicit AppUserModelID. Inno's shortcuts also omitted it. On this machine the Start Menu shortcut correctly targets `%LocalAppData%\Programs\Fyltrix\Fyltrix.exe`, while the existing `Fyltrix (2).lnk` taskbar pin targets `app-0.1.6\Fyltrix.exe`. Both shortcuts lack an explicit identity. This explains the inconsistent grouping/relaunch paths; the payload pin also becomes stale when that version is retired.

## Implementation

- Production uses `Fyltrix.Desktop` across versions. Sandbox uses `Fyltrix.Desktop.Sandbox`. These are shell identifiers, independent of the unchanged installer AppIds and updater/profile identities.
- `WindowsAppIdentity` in the shared Bootstrap assembly resolves the root from the existing layout API. A bounded read of the existing installation marker selects the installed identity. Unknown/unmarked layouts use a portable identity derived from the canonical root path. Debug application builds use a separate development identity. Independent portable copies stay separate from the installed application and each other; their payload versions do not enter the identity.
- Launcher and application set their process identity before creating UI. Main windows set `System.AppUserModel.ID` before first visibility. Relaunch command, display name and icon point to the stable root executable, with a quoted executable path. Update-worker command lines and transient startup arguments never enter pins.
- Window property values use the native property store. Relaunch values are set before the ID, copied strings/COM resources are released, and values are cleared at `WM_DESTROY`. Shell integration failures are logged without preventing startup/file management.
- Both Inno shortcuts carry the matching mode-specific ID and retain the root target/working directory. Contract tests check the installer IDs against the shared runtime constants.
- No automatic pinning, unpinning, shortcut rewriting, taskbar cache deletion or Explorer restart. No new dependencies. No changes to `current.json`, version selection, startup exclusion, handover, trust, health checks, rollback, user data or `publish.ps1 -Run`.

Microsoft documents the shared-ID requirement in [Application User Model IDs](https://learn.microsoft.com/en-us/windows/win32/shell/appids), the paired relaunch properties in [RelaunchCommand](https://learn.microsoft.com/en-us/windows/win32/properties/props-system-appusermodel-relaunchcommand), and native property lifetime in [SHGetPropertyStoreForWindow](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shgetpropertystoreforwindow). The installer uses Inno's existing [AppUserModelID shortcut parameter](https://jrsoftware.org/ishelp/topic_iconssection.htm).

## Automated verification

- 442 application tests, 134 updater tests, 39 packaging tests, 14 release-tooling tests and 10 installer-builder tests passed: **639 total**. Native tests read actual window properties for multiple HWNDs; layout tests exercise different payload versions, path quoting, portable/development separation and malformed markers.
- Isolated win-x64 self-contained payload and NativeAOT root launcher published successfully. Both Production and Sandbox Inno installers compiled. Production Setup was not executed. Existing unrelated nullable/high-DPI compiler warnings remain.
- A fresh Sandbox installer ran on a private Windows desktop with a separate destination and disposable profile/clipboard/cache. The compiled Start Menu shortcut's actual property store reported `Fyltrix.Desktop.Sandbox`. Launch through the native root executable created the real WPF window: process ID and window ID matched that shortcut, and the window relaunch/icon properties named the stable root executable. Preview readiness, normal close and Sandbox uninstall passed; its registry/shortcut were removed afterward.
- The Sandbox smoke test verifies native identity plumbing, not Explorer's visual grouping or a human taskbar pin. Version-independent relaunch is covered through layout tests; an actual newly released production update from an existing pin still needs owner review.
- Evidence is in the directory named by `%TEMP%\Fyltrix-taskbar-current.txt`, including TRX results, installer logs, `native-smoke-2\taskbar-verification.json`, security/shortcut inspection and protected-file hashes. No user-installed application was launched or replaced during these tests.
- All **991 protected SHA-256 records** matched after verification: live roaming profile, installed production files and the existing portable output. Sandbox registry and shortcut absence were checked after uninstall. The checkout remains on `master`; no Git writes were performed.

Resource benchmark samples used the preserved before-build and a separate after-build, with disposable data/cache/profile folders. The final sequential runs reported:

| Measurement | Before | After, first run | After, warmed repeat |
|---|---:|---:|---:|
| Startup to first listing | 1367.8 ms | 18943.6 ms | 1892.1 ms |
| Time before benchmark Main | 454.5 ms | 17967.3 ms | 648.3 ms |
| Window construction | 327.3 ms | 372.0 ms | 502.0 ms |
| Idle private memory | 88.8 MB | 89.4 MB | 90.3 MB |
| Idle CPU | 1.56 ms/s | 0 ms/s | 3.12 ms/s |
| Navigation median, 30 navigations | 132.8 ms | 125.5 ms | 157.0 ms |
| Search median, three searches | 190.3 ms | 198.1 ms | 215.3 ms |

An earlier cold baseline also spent 23.7 seconds before benchmark Main. The long first-start delays occur before window identity is applied; their cause was not established. The remaining samples vary and some after measurements are slower. These limited measurements do **not** prove equal or improved performance, and no such claim is made. A representative on-screen startup/responsiveness review remains part of owner acceptance. The implementation adds bounded startup metadata reads and one set of native properties per main window, with no timer, polling or recurring background work.

## Security warning investigation

Read-only inspection found the downloaded Setup, installed root launcher and installed versioned payload **NotSigned**. Downloaded Setup has Internet-zone Mark-of-the-Web (`ZoneId=3`); the installed launcher/payload have no Zone.Identifier stream. Neither shortcut inspection nor testing removes or changes that information. Shortcut targets are existing files in the normal per-user installation; the taskbar target is version-specific rather than the stable entry point.

An Internet-downloaded unsigned installer is consistent with a SmartScreen reputation prompt. Changing AppUserModelID or targeting the root launcher does not establish executable reputation. The ECDSA updater signature protects update authenticity; it is not Windows Authenticode signing. A self-signed certificate also does not establish public publisher trust. Production distribution needs an appropriate trusted code-signing/reputation strategy for Setup and application executables; signing alone does not promise an immediate warning-free reputation. See Microsoft's [SmartScreen developer guidance](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation).

Smart App Control, Defender antivirus and enterprise AppLocker/WDAC policy are separate checks. Recent local event inspection found no matching installed-application block in Code Integrity; the AppLocker query returned no events. A Defender event concerned failure to upload an old temporary installer-build file for analysis, not evidence of a current installed-app block. These limited logs cannot rule out every policy or diagnose every warning. For a remaining warning, record its exact title/message, the affected executable path and the matching Protection History/Code Integrity event. Microsoft's [Smart App Control overview](https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/overview) describes its trust requirements.

No certificate installation, security exclusion, policy change, Mark-of-the-Web removal, security bypass or live signing was performed.

## Owner review after installing a release containing this fix

1. Install the new reviewed Setup while the installed application is closed. It refreshes its owned Start Menu shortcut. An in-app payload update alone does not rewrite pre-existing shell shortcuts or pins.
2. Unpin the old Fyltrix taskbar entry once. Launch from the refreshed Start Menu shortcut and pin it again. Repeat pinning from the running main window to verify that path too. Existing pins are Windows-owned copies and do not automatically acquire new metadata.
3. Close and reopen through Start Menu, optional desktop shortcut and taskbar. Confirm one grouped taskbar entry; open multiple main windows and check grouping. Verify the pinned target is the stable root `Fyltrix.exe`, not `app-<version>\Fyltrix.exe`.
4. Apply a later verified release and repeat launch through the existing pin. Check normal health/rollback behavior using the existing recovery procedure, without forcing failure in the live installation.
5. Run a separate portable copy and confirm a separate taskbar group. Check tabs, Favourites, toolbar preferences and ordinary file operations retain their settings.

The real taskbar/Start Menu visual checks and production update-through-pin check are **not yet performed**. The agent does not manipulate the owner's taskbar, foreground windows or installed configuration.
