# Project-directory relocation audit — 2026-10-31

The verified checkout is `D:\Tools\Filetryx`, as confirmed by the user. The previous checkout path was `D:\Tools\FileLens`; `D:\Tools\Fyltrix` was not present. No directory was renamed during this audit.

## Findings and minimal repairs

- Project references, resources, manifests, template resources, dependencies, and publishing inputs are relative to the project or script location. They did not require changes.
- `publish.ps1` derives its root from its own location. Test helpers and development generators locate their resources independently of the outer directory name.
- No installer/updater scripts, solution files, launch profiles, editor workspace configurations, CI configuration, or parent MSBuild/NuGet overrides requiring a path repair were found.
- Seven generated file-list/publish-output cache files still referenced the old checkout. Their originals were moved into an external backup and the active build outputs regenerated. NuGet restore refreshed generated restore metadata.
- An obsolete absolute working-directory example in `IMPLEMENTATION_STATUS.md` was replaced with an instruction to run from the checkout root. The README was subsequently rewritten with relative build commands and an implementation-checked feature guide.
- With the user's explicit approval, one restored tab and two Recent entries in the personal settings file were remapped only where corresponding destinations existed under the current root. A complete backup was made first. All other preferences and history entries were preserved, including list order and counts.

The external audit/backup directory is `D:\Tools\Fyltrix-path-audit-20261031-1f2b4c6d`. It contains `quarantined-caches.json`, the original cache files under `stale-cache-backup`, the private `settings-before.json` backup and hash, `settings-update.json`, a separate published build, and startup-check evidence. Personal settings contents are intentionally excluded from project documentation.

No application source, namespaces, classes, identifiers, configuration keys, dependencies, data-folder identities, or existing portable-release files were changed. No Git metadata was found in this checkout or its parent directories; none was created or modified.

## Verification

| Check | Result |
|---|---|
| Forced restore of the test project and dependencies | Passed |
| Full test suite | 346 passed, 0 failed, 0 skipped |
| Release placement-probe build | Passed |
| LogoGen build | Passed; generator not run |
| OfficeTemplates build | Passed; generator not run; existing nullable warning remains |
| Self-contained win-x64 Release publish to the audit directory | Passed; current portable release left intact |
| Native main-window startup from the renamed checkout, with `C:\` as working directory | Passed through the existing cloaked render probe; exit code 0 and screenshot produced |
| Restored tab destination | Current checkout exists and matches the remapped path |

The startup probe instantiated the real main window with isolated settings, WebView2 data, and a read-only clipboard provider. Its screenshot was inspected. This was an offscreen startup check, not a visible desktop launch of the published executable against the user's profile.

## Remaining references

| Reference | Treatment |
|---|---|
| Previous root in `REBRANDING_REPORT.md` and this report | Historical audit context; retained intentionally. |
| Previous root inside the external cache/settings backups | Recovery evidence outside the active checkout; retained intentionally. |
| `src/FileLens`, `FileLens.exe`, assembly/namespace/manifest names, resource names | Stable compatibility identities, independent of the checkout directory; unchanged. |
| `%AppData%\FileLens`, `%LocalAppData%\FileLens`, private preview origin, firewall rule identity | Preserve existing preferences, caches, and integrations; unchanged. |
| Example/test folders named FileLens | Sample data or compatibility coverage, not executable references to the previous checkout. |

No active build/configuration text references to the previous absolute root remained after regeneration. An already-open terminal or development session can retain the removed directory as its working directory outside project configuration; reopen the current checkout or change directory to `D:\Tools\Filetryx` in such sessions.
