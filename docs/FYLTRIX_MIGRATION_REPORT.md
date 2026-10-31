# Fyltrix identity migration and compatibility audit — 2026-10-31

The project now builds, publishes, and launches as **Fyltrix** from `D:\Tools\Fyltrix`. Projects, namespaces, assemblies, executable names, resource filenames, generators, publishing, and current documentation were migrated together. Existing functionality and user-data stores were retained.

## Checkpoint and recovery

The checkout had no Git repository. A local repository was initialized and checkpoint commit **`6952c6d`** was created before editing. No remote was added, renamed, or contacted. Final migration changes are left uncommitted for review.

External backup and verification directory:

```text
D:\Tools\Fyltrix-migration-20261031-124758-4e9f904a
```

It contains `source-before`, the original generated directories and complete portable release under `generated-before`, a private profile backup, pre-migration reference classifications, hashes, test results, startup evidence, and benchmark results. No original user data or release files were deleted. Private profile contents are not included in repository reports.

## Names and paths

| Component | Previous | Current |
|---|---|---|
| Main project | `src/FileLens/FileLens.csproj` | `src/Fyltrix/Fyltrix.csproj` |
| Tests | `tests/FileLens.Tests/FileLens.Tests.csproj` | `tests/Fyltrix.Tests/Fyltrix.Tests.csproj` |
| Placement/benchmark helper | `tests/FileLens.PlacementProbe/FileLens.PlacementProbe.csproj` | `tests/Fyltrix.PlacementProbe/Fyltrix.PlacementProbe.csproj` |
| C# namespaces | `FileLens.*` | `Fyltrix.*` |
| Application assembly | `FileLens.dll` | `Fyltrix.dll` |
| Executable | `FileLens.exe` | `Fyltrix.exe` |
| Portable output | `artifacts/FileLens` | `artifacts/Fyltrix` |
| Icon and SVG | `Assets/FileLens.ico`, `FileLens.svg` | `Assets/Fyltrix.ico`, `Fyltrix.svg` |
| PNG assets | `FileLens-{size}.png` | `Fyltrix-{size}.png`, same nine sizes |

All files within the three renamed project directories moved with them, including fixtures, viewer libraries, and templates. **172 file paths changed**. Their complete old/new mapping is in [FYLTRIX_MIGRATION_AUDIT.json](FYLTRIX_MIGRATION_AUDIT.json), together with all **153 existing files whose contents changed**, before/after hashes, and the two new report files. Unchanged third-party libraries and templates are distinguished from modified files.

Changes include namespace imports and fully qualified types; root/startup/assembly names; project and friend-assembly references; icon resource lookups; helper executable discovery; generator output names; temporary test/probe names; remaining source comments and visible executable text; `publish.ps1`; README; contributor-guide paths; current implementation-status sections; and compatibility tests. Historical round reports were preserved.

The icon and all nine PNG files remain byte-identical. The SVG changes only its title metadata. No artwork was regenerated. Dependency/package versions, assembly/file versions (`0.1.0.0`), application defaults, configuration keys, and JSON schemas are unchanged. There were no solution, XAML, installer/updater, launch-profile, or CI files requiring coordinated edits.

## Build and publishing

Run from the project root:

```powershell
dotnet build .\src\Fyltrix\Fyltrix.csproj -p:Platform=x64
dotnet test .\tests\Fyltrix.Tests\Fyltrix.Tests.csproj -p:Platform=x64
powershell -ExecutionPolicy Bypass -File .\publish.ps1 -Run
```

`publish.ps1` derives paths from its own location, publishes to `artifacts\Fyltrix`, and launches `Fyltrix.exe` for `-Run`. Output-directory cleanup is checked to stay within the checkout. `-SingleFile` remains supported. Generated folders were archived before clean builds, rather than subjected to source replacements.

The final normal package contains **488 files**, including `Fyltrix.exe` and `Fyltrix.dll`, with no legacy-named application files. The new publish process does not generate `FileLens.exe`. The single-file publish was also verified and retained separately in the external audit directory. Windows executable metadata reports Fyltrix for ProductName, FileDescription, and CompanyName.

## Settings and persistent data

No profile-directory migration was necessary. Both old and new versions use the existing `%AppData%\FileLens` and `%LocalAppData%\FileLens` stores. This preserves settings, templates, tags, automation, sharing records, synchronization baselines, cached data, toolbar/menu/IDE preferences, views, Favourites, history, and window/tab state. Share-token encryption remains tied to the same Windows account.

The entire existing roaming profile was backed up before changes. During application verification its hashes remained unchanged. Following the user's earlier authorization to remap existing destinations, the later checkout rename was repaired in exactly three string values: `OpenTabs[0]`, `RecentFolders[0]`, and `RecentFolders[1]`. Each destination exists under `D:\Tools\Fyltrix`. All other JSON values, list order/counts, and other profile files were preserved. No personal paths or history were copied into application defaults.

Legacy-profile tests round-trip explicit saved preferences and restore tabs, views, Favourites, Recent, toolbar choices, and IDE settings. Actual executable-startup probes verify the legacy default profile path before redirecting the test process to an isolated profile.

Archive cleanup now recognizes both `FileLens` and `Fyltrix` processes, as well as the current helper/test process. The new version therefore preserves cache entries belonging to a running legacy executable. This adds no service, polling loop, or startup task.

## Windows integrations

The read-only scan found no matching Desktop, Start Menu, or pinned shortcuts in the inspected locations and no matching application/startup/uninstall/App Paths entries in the inspected registry keys. The code has no application-specific AUMID, installed file-association registration, installer product registration, updater, or single-instance mechanism to migrate. Windows preview-handler, IDE, and file-association discovery remain unchanged.

Three Windows firewall rules still target the removed legacy executable path: the application's narrow TCP 8765 sharing rule and two broader legacy FileLens entries. Their identities and policy were left intact during testing; no UAC prompt, rule deletion, or elevated firewall write was performed.

The existing **Allow through firewall** action now checks for its exact owned rule name and updates that rule to the current executable path after the normal user confirmation/UAC flow. If no owned rule exists, it adds one. Updating retains inbound TCP, the selected local port, local-subnet scope, and Private/Domain profiles. It does not add a duplicate named rule or silently broaden access. Read-only rule lookup and command generation are tested; the installed `netsh` help confirms the update-command syntax. If rule inspection fails, the action stops instead of guessing.

The two broad legacy entries are not repurposed automatically. They may be reviewed in Windows Defender Firewall. Organization-managed policy/Public-network restrictions can still block LAN sharing even after an executable-path update. An administrator-approved policy write and an actual cross-device connection were not verified.

## Executed verification

| Check | Result |
|---|---|
| Dependency restore after project renames | Passed |
| Clean/rebuild of application, tests, and placement helper | Passed |
| Full final suite | **354 passed, 0 failed, 0 skipped**; 2 minutes 9 seconds |
| Release placement helper | Passed |
| LogoGen and OfficeTemplates builds | Passed; generators not run against project assets |
| Normal publish through `publish.ps1` | Passed |
| Single-file publish through `publish.ps1 -SingleFile` | Passed |
| Published normal `Fyltrix.exe` startup | Passed; folder listing, native window title, assembly/type identity, preview readiness, exit code 0 |
| Published single-file `Fyltrix.exe` startup | Passed; same checks, exit code 0 |
| `publish.ps1 -Run` branch | Passed; intercepted launcher verified the exact target and started that executable on a private desktop |
| Icon/PNG and third-party library byte comparisons | Passed |
| Dependency-version comparisons | Passed |
| Legacy settings/default-profile compatibility | Passed |
| Final source-reference audit | No unexpected references |

Startup checks launched the real published executables on a private desktop with an external, test-only .NET startup hook to isolate settings, WebView2 data, thumbnails, and clipboard access. The hook is not part of the published application. The normal desktop, real clipboard, and personal profile were not used for those launches.

The suite includes temporary-file operations and undo, navigation/tabs, search, previews/Quick View/comparison, toolbar/menu availability, image paste through an isolated clipboard, views/settings, IDE arguments/detection, tags, archives, folder synchronization, automation, and loopback sharing. No claim is made that every optional codec, external editor, or LAN environment was exercised.

An initial new test incorrectly assumed the shared UI-test process still held the production settings path; existing UI helpers intentionally replace it. That test was corrected to check an immutable cache location, while the production profile default is verified in fresh executable processes. The final suite also includes regression coverage for mixed-version cache cleanup and firewall-rule updates. Existing nullable and WinForms high-DPI warnings remain; no new build errors remain.

## Performance evidence

The existing benchmark ran against a shared fixed data set with 20,000 files, 9,330 expected search results, previews, idle sampling, and repeated tab cycles. All runs returned the expected item/result counts and released preview child processes after work. No runtime dependency or background service was added.

An initial cold baseline included data-set creation. An initial warm pair also showed startup/memory variation, with an audit scan overlapping part of that pair. Two further pairs were run without concurrent builds, tests, or scans. The following values are medians of those final two runs per version; all seven raw runs and the sampling notes are retained in the audit JSON.

| Measurement | Baseline | Fyltrix |
|---|---:|---:|
| Process start to folder listed | 1,320.95 ms | 1,414.10 ms |
| Main-window construction | 350.19 ms | 362.64 ms |
| Idle private memory | 90.55 MB | 87.85 MB |
| Idle working set | 146.45 MB | 146.50 MB |
| Idle CPU time per second | 0.00 ms | 0.78 ms |
| Ordinary navigation, median | 133.45 ms | 134.90 ms |
| 20,000-file listing, median | 286.65 ms | 300.45 ms |
| Recursive search, median | 253.75 ms | 253.50 ms |
| Private memory after work | 150.25 MB | 152.10 MB |
| Managed heap after work | 10.45 MB | 10.40 MB |
| Preview child processes after cleanup | 0 | 0 |

These small desktop samples do not establish statistical performance equivalence. Startup was about 93 ms slower in the final sample; navigation/search and memory were close, with no persistent preview processes or sustained additional background activity observed. The rename introduces no startup work beyond the existing cache cleanup's compatibility-name comparisons; firewall lookup occurs only when the user asks to authorize sharing. No unrelated optimization was attempted to mask measurement differences.

## Intentional remaining legacy references

| Reference | Reason |
|---|---|
| `%AppData%\FileLens`, `%LocalAppData%\FileLens` | Keep existing user data and caches directly accessible; no unnecessary migration. |
| `https://filelens.app` | Private WebView2 origin used throughout previews; preserve origin/cache/protocol compatibility. |
| Manifest `FileLens.app` | Preserve the existing activation compatibility identity. Product, assembly, executable and namespace names are Fyltrix. |
| `FileLens file sharing` | Stable firewall-rule lookup/update identity; the technical name can still appear in Windows Firewall and rule-management instructions. |
| Legacy process name and test fixture folders | Protect running old-version caches and prove user folders named FileLens are not renamed. |
| Historical reports, audit mappings, and Git checkpoint | Preserve development history and recovery evidence. |

No unintended legacy namespaces, project references, resource filenames, executable filenames, or active build paths remain. The complete match classification is in the audit JSON. Generated binaries contain intentional compatibility strings; generated source/configuration filenames and publish filenames use the new identity.

## Remaining compatibility limits

- The three stale system firewall rules are preserved. LAN access through the new executable requires the existing administrator-approved sharing flow or an administrator's policy update; elevated modification and cross-device connectivity remain unverified.
- No matching shortcuts were found here. On another installation, old shortcuts/pins, external scripts, or third-party registrations targeting `FileLens.exe` must be retargeted to `artifacts\Fyltrix\Fyltrix.exe`. The new package intentionally has no legacy executable shim. No installer upgrade path exists to test in this project.
- Prefer closing the old executable before using the new release. Both versions share the profile, and an unmodified old version does not know the new process name when cleaning shared caches. The migration adds no concurrent-profile-write arbitration.
- Native Snap flyouts, taskbar grouping/pin migration, third-party Open with placement, and additional codec/device combinations remain manual checks. Native title/metadata/icon resources and existing offscreen placement tests passed.
- Benchmark timing differences are reported above; the small sample cannot rule out a minor startup regression on this machine.
