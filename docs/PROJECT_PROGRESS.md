# Installation and update infrastructure progress

## Stage 2: Explorer-style numbered multi-item rename (2026-11-05)

Status: **Completed locally; owner review pending.** The owner pushed Stage 1 and explicitly approved continuing. Only Issue 2 is active; thumbnail performance/quality remains Stage 3 and has not been changed or benchmarked.

F2, toolbar Rename, context-menu Rename and palette Rename now share a simple common-base-name prompt for multiple real items in the current folder. The selection is captured in actual visible List.Items order, including sorting/grouping/filtering, independently of selection insertion order. Examples are Holiday (1).jpg and Holiday (2).png; each file retains its final extension and its case. Folders have no appended extension; extensionless/dot files retain the existing batch naming convention. Single-item F2 still starts the existing inline editor. Advanced Batch rename remains a separate Tools/context-menu/palette action with its customization identity and implementation unchanged; its misleading F2 hint is removed.

A small read-only NumberedRename planner snapshots sibling names once with case-insensitive comparison. Occupied numbers are skipped, including names held by other selected sources; exact self-names can remain unchanged. This avoids target/source cycles and overwrites, although numbering can contain gaps. All base names, generated component/path lengths, source existence, distinctness and common parent are checked before any mutation. User text is literal rather than an advanced template. Invalid/reserved-name validation follows [Microsoft's Windows naming rules](https://learn.microsoft.com/en-us/windows/win32/fileio/naming-a-file), including COM/LPT superscript device names.

Execution reuses the unchanged BatchRename two-pass engine and BatchRenameUndo; no second executor/undo manager was introduced. Previews release their selected paths before execution. Successful changes retain existing tag updates, selection refresh and undo; rejected/cancelled requests add no undo. The original folder/selection is revalidated after the prompt, and closing/navigation aborts safely. Failed execution reports its errors and the engine's rollback attempt without falsely claiming an unconditional cross-process transaction. Locked-source and late-target-conflict tests prove complete rollback in those exercised cases; rollback/undo cannot guarantee recovery against arbitrary concurrent external changes.

Verification: isolated Release builds succeeded with zero errors and only the existing SettingsWindow/FolderViewTests nullable and WFAC010 warnings. **67 focused cases passed, zero failures/skips** (`numbered-rename-final-focused.trx`), including mixed extensions, folders, case-insensitive existing/selected collisions, unchanged names, literal text, invalid/reserved names, overlong/stale/duplicate/cross-folder inputs, lock and late-target rollback, undo, all four command routes, Details/all four thumbnail sizes, descending/date-grouped visible order, cancellation, navigation and closure during prompting, and the existing Advanced Batch Rename UI smoke. The single-item check starts the existing inline editor and invokes its existing host commit/undo path; no real desktop key input is claimed. Early test compilation/event-driver mistakes were corrected before the final passing run.

Final full application regression suite: **566 passed, zero failures/skips, 3 minutes 24 seconds**, including all 37 new numbered-rename cases and the preserved Stage 1 Cut/Delete, selection, advanced tools, PDF, settings and update-integration cases. TRX: `%TEMP%\Fyltrix-numbered-rename-stage2-20261105\results\numbered-rename-stage2-full.trx`. Test/probe binaries use the conventional temporary directory layout expected by existing monitor-placement tests; no helper-discovery implementation changed. `git diff --check`, new-source whitespace checks and the exact 13-file Stage 2 scope check pass. No unrelated packaging/installer/updater project suite or thumbnail benchmark was rerun.

Tests use disposable files/settings and offscreen WPF windows, with window-scoped prompt/error substitutes for deterministic input and no real clipboard/Recycle Bin access. The shared production prompt component is reused. Actual foreground prompt/physical-F2 acceptance remains an owner check; no user files/preferences, installed copy or portable publish output were changed. No app/update/install/release infrastructure, dependencies or version changes; no Git mutations.

Performance: sibling names are enumerated once per explicit plan and held only for that operation; no per-keystroke filesystem scanning, polling, persistent caches or new background tasks. Execution follows the existing synchronous batch engine. No numerical responsiveness/CPU/memory improvement is claimed; thumbnail cold/warm benchmarks are deferred until Stage 3 approval.

Files: new `src/Fyltrix/Core/Tools/NumberedRename.cs`, `src/Fyltrix/UI/MainWindow.Rename.cs`, `tests/Fyltrix.Tests/NumberedRenameTests.cs`, `tests/Fyltrix.Tests/NumberedRenameUiTests.cs`; modified `src/Fyltrix/UI/MainWindow.Commands.cs`, `src/Fyltrix/UI/MainWindow.cs`, `src/Fyltrix/UI/MainWindow.Tools.cs`, `src/Fyltrix/UI/MainWindow.Palette.cs`, `tests/Fyltrix.Tests/ToolbarBehaviorTests.cs`, `README.md`, `CHANGELOG.md` (Unreleased only), `docs/PROJECT_PROGRESS.md`, `docs/IMPLEMENTATION_STATUS.md`.

Next owner action: review simple multi-item F2/Rename, collision numbering and Ctrl+Z with disposable files; verify single-item inline Rename and Tools -> Batch rename remain familiar. Suggested commit: `feat(rename): add Explorer-style numbered multi-file rename`. Stop for owner review before Stage 3 (thumbnail baseline, quality, priority/cancellation/cache work).

## Stage 1: multi-file Cut/Delete (2026-11-05)

Status: **Completed locally; owner review pending.** Scope follows the owner's three-issue attachment: only Issue 1 is active. Explorer-style numbered rename and thumbnail performance/quality changes are deferred until explicit approval.

Reproduced root cause: command availability required every selected item to pass a DELETE-access CreateFile probe before either Cut or Delete could run. One open file therefore disabled an otherwise valid mixed multi-selection. Three pre-fix keyboard/toolbar/menu regressions failed at this shared gate with their three-item selection still intact. The handlers already used full selection lists; there was no reason to replace Ctrl/Shift/marquee selection. Cut now records valid real paths without requiring immediate delete access; Delete lets the existing Windows Shell operation handle runtime locks/permissions and report failures. Drives, virtual/archive entries and stale/invalid selections remain blocked. Single-item Rename retains its existing stricter gate; numbered multi-rename is not implemented in this stage.

Cut and Delete capture one selection/view snapshot. Permanent deletion retains the default-No confirmation and revalidates the snapshot afterward; changing tabs during confirmation does not redirect the operation. Refresh targets the originating view. Partial deletion status reports actual missing paths/remaining count, and DeleteUndo records only successfully recycled paths. Tests inject window-scoped deletion/restore/confirmation boundaries; normal production paths still use the existing Shell/Recycling code, native progress/error UI and undo implementation.

Also fixed unconditional clipboard clearing after partial/cancelled cut-paste. Transfer returns its existing result, leaving skipped/failed source paths cut and updating their visual state. A newer clipboard selection made during Shell/conflict dialogs is preserved. Existing clipboard history, file-drop/PNG priority and native formats remain intact; no separate clipboard manager or dependencies were introduced.

Verification: Release application/test compilation succeeds with existing SettingsWindow/FolderViewTests nullable warnings and WFAC010 only. Initial focused run: 35 passed. Expanded file/toolbar/marquee/folder selection regression run: **71 passed, zero failed/skipped** (`multi-file-selection-regressions.trx`). New tests cover all three command routes, mixed files/folders, locked input, permanent confirmation/cancel, stale paths, changing tabs, partial deletion/undo, partial and cancelled moves, replaced clipboard contents, and real Shell multi-item move/undo. They use only disposable paths/settings, in-memory clipboard data and an isolated recycle store; they do not alter the owner's clipboard, real Recycle Bin, personal files or settings. The actual production Shell Recycle Bin dialog/ACL-denial UX and physical Ctrl/Shift input remain owner acceptance items; isolated tests do not claim those native visual checks passed.

Final full application regression suite: **529 passed, zero failures/skips, 3 minutes 32 seconds**, including all 18 new multi-file cases and the existing selection, clipboard, PDF, settings and update-integration cases. TRX: `%TEMP%\Fyltrix-multi-file-stage1-20261105\results\multi-file-stage1-full.trx`. Existing placement tests use their conventional temporary test/probe layout under that artifacts root; no helper-discovery code changed. Scoped diff whitespace/new-file checks pass, and the changed-file set is exactly the nine Stage 1 files listed below. No unrelated installer/updater/packaging suite or thumbnail benchmark was rerun because those implementations are untouched.

Performance: no thumbnail or preview changes, background polling, new workers or dependencies. Unneeded DELETE probes are no longer performed for multi-item command states. No numerical speed/CPU/memory improvement or thumbnail benchmark is claimed. Stage 3 must collect its own cold/warm baseline before optimization.

Files modified: `src/Fyltrix/UI/MainWindow.Commands.cs`, `src/Fyltrix/UI/MainWindow.cs`, `src/Fyltrix/UI/MainWindow.Clipboard.cs`, `src/Fyltrix/Core/UndoManager.cs`, new `tests/Fyltrix.Tests/MultiFileOperationsTests.cs`, `README.md`, `CHANGELOG.md` (Unreleased only), `docs/PROJECT_PROGRESS.md`, `docs/IMPLEMENTATION_STATUS.md`. Application version, installer/launcher/updater/release workflow and owner AGENTS.md remain unchanged. No Git mutation, application publish, personal-data migration or desktop input.

Next owner action: review Stage 1 with disposable files using Ctrl/Shift/marquee selection, Cut/Paste, Delete/Undo and Shift+Delete confirmation. Suggested commit: `fix(files): restore multi-selection cut and delete operations`. Stop for owner approval before Issue 2 (simple numbered multi-file rename); thumbnail work remains Stage 3.

## Release 0.1.9 uploaded metadata verified (2026-11-03)

Status: **Ready for owner publication; still a stable draft.** Downloaded both uploaded metadata files from the existing 0.1.9 draft. Manifest (325 bytes, SHA-256 `5d293f29e99ec14041398056d201128b9d9227694a6547931d226970d0e90d54`) and signature (88 bytes, SHA-256 `74b14f266244312f3d419e796ee88a3b9693f9aae3105d0a48e2e62b8db9e4a6`) match the previously signed local bytes exactly and their GitHub sizes/digests. All six expected assets are present. Original package/checksum asset IDs, sizes, digests, timestamps and metadata are unchanged, except GitHub rotated the temporary untagged download URL after the owner saved the draft; that URL change is not a package replacement. Notes match after line-ending/outer-whitespace normalization.

Re-ran the isolated harness against the downloaded metadata using the verifier assemblies actually shipped inside the release ZIP, production pins and no trust override: manifest and both packages passed (`None`); altered metadata was rejected as `InvalidSignature`. The harness models canonical post-publication metadata offline, so public-feed availability remains to be checked after publication. Evidence: `uploaded-metadata-verification.json`, `uploaded-shipped-verification.json`, `draft-uploaded.json` and `uploaded-71b516ff62424c2e8ee0f57be2967379` below `%TEMP%\Fyltrix-release-0.1.9-105e992f951e427190b505f52e802538`.

No application source, packages, signing keys, preferences or user data changed. No new signing, full-suite run, installer execution, remote mutation, commit/push/tag or publication occurred. Only this progress file, `docs/IMPLEMENTATION_STATUS.md` and `docs/RELEASING.md` have documentation updates; earlier uncommitted verification records are preserved. No runtime performance impact or new benchmark.

Next owner action: open the existing Fyltrix 0.1.9 draft, select **Set as latest release**, keep it stable (not prerelease), then **Publish release**. After the owner confirms publication, verify the public feed authenticates 0.1.9 from 0.1.8 and reports up to date from 0.1.9 without installing anything. Owner visual installation/PDF/selection acceptance and independent signing-key backup remain separate checks. Suggested documentation commit: `docs(release): record verified v0.1.9 uploaded metadata`. Stop for owner action.

## Release 0.1.9 draft verified and signed locally (2026-11-03)

Status: **Owner upload pending; draft remains unpublished.** The owner created/pushed annotated `v0.1.9`. Local HEAD, the peeled local tag, remote peeled tag and [successful tag workflow 41645690919](https://github.com/imamulkadir/Fyltrix/actions/runs/41645690919) all resolve to `85554a3f63a6c2a29b060cf22f89ae824b5e2241`. The tag run passed 511 application, 134 updater, 39 packaging, 10 installer-guard and 14 release-tool cases and built/attested both packages. No new full test suite was run locally in this verification stage.

Downloaded the actual four draft assets into `C:\Users\imamu\AppData\Local\Temp\Fyltrix-release-0.1.9-105e992f951e427190b505f52e802538`. All four sizes and GitHub SHA-256 digests match; both package checksum sidecars match their bytes. GitHub provenance verification passed for both packages, restricted to this repository/workflow, exact tag/source commit and non-self-hosted runners. Draft notes match the 0.1.9 changelog. ZIP SHA-256: `359abc7973c278678fe0836479e6e7e5bd749c8c11e21187558aa1f2c5bfe99f`; Setup SHA-256: `30c467f8a4a1ffafabf4403fea6b18a9a888c1fdda9e19425c7d55fedc8fd971`.

Built the existing standalone signing tool from the clean exact tagged checkout (zero errors/warnings), then used its pinned-key `sign-release` mode with the existing protected Fyltrix key. Created only local `signed/Fyltrix-release.json` (325 bytes) and `signed/Fyltrix-release.json.sig` (88 bytes). No key/password was displayed, exported to the workspace or uploaded; existing keys/backups/pins are unchanged.

An isolated temporary harness references `Fyltrix.Updates.dll` and `Fyltrix.Bootstrap.dll` extracted from the actual release ZIP. The shipped verifier accepted the manifest and both packages with its production pinned keys and no trust override (`None` means no verification failure), and rejected altered manifest bytes with `InvalidSignature`. It models canonical post-publication metadata offline; this is not a claim that the draft is available through the public update feed. The first harness attempt exposed the Windows PowerShell UTF-8 BOM in its diagnostic draft snapshot; switching that temporary reader to BOM-aware text loading resolved it, without product/manifest changes. Evidence: `package-digests.json`, `*.provenance.json`, `shipped-verification.json`, draft before/after snapshots and the temporary harness in the workspace.

Rechecked remote assets and notes after signing: unchanged, four assets, still a stable draft. No upload, publication, installer/application execution, user-data modification, Git mutation or new performance benchmark. Only `docs/PROJECT_PROGRESS.md`, `docs/IMPLEMENTATION_STATUS.md` and `docs/RELEASING.md` are changed by this stage.

Next owner action: open `C:\Users\imamu\AppData\Local\Temp\Fyltrix-release-0.1.9-105e992f951e427190b505f52e802538\signed`, upload both metadata files to the existing 0.1.9 draft without replacing the original four assets, save the draft, then request uploaded-byte verification before publishing. Native PDF/selection acceptance and independent signing-key backup remain owner checks. Suggested documentation commit: `docs(release): record verified v0.1.9 draft and local signing`. Stop for owner review.

## Release 0.1.9 preparation (2026-11-03)

Status: **Prepared locally; owner commit/push, candidate CI validation and tag pending.** Latest published release remains 0.1.8. Read-only GitHub verification confirms [manual validation 41641253759](https://github.com/imamulkadir/Fyltrix/actions/runs/41641253759) succeeded on current base commit `675a92ac4df7faa4c5a4bb6f2660325e8d207072`: application 511, updater 134, packaging 39, installer guards 10 and release tooling 14 tests passed; versioned ZIP and Production Inno installer compilation succeeded. That run validated the pre-bump 0.1.8 metadata, not a tagged 0.1.9 package.

Prepared `CHANGELOG.md` section `[0.1.9] - 2026-11-03`, keeping `[Unreleased]` for future work. Notes account for all shipped changes since 0.1.8: existing blank-space marquee selection, native interactive PDF Compare, per-document read-only comment flyouts, native wheel mirroring/comment navigation, independent Compare focus, actual README screenshots, supporting WebView2 SDK update and the IDE copy-completion regression fix. Native note-icon cards and exact frame lock remain explicitly excluded from the promises. Existing owner visual acceptance items, including marquee usability, remain review items rather than new acceptance claims.

Executed `tools/Set-ReleaseVersion.ps1 -Version 0.1.9`; only Version, FileVersion and AssemblyVersion changed in the application project. `tools/Test-Release.ps1 -Tag v0.1.9` passes. A fresh isolated Release build under `%TEMP%\Fyltrix-release-prep-0.1.9-20261103` succeeded with zero errors and the two existing application warnings (CS8602, WFAC010); built assembly/file versions are 0.1.9.0 and product version is 0.1.9 plus base-commit build metadata. All 14 release-tool Python tests passed again. The full application/updater/packaging suites were verified from the successful GitHub base run and were not rerun for these metadata/documentation-only changes. `git diff --check` passes.

Files modified in this preparation: `CHANGELOG.md`, `src/Fyltrix/Fyltrix.csproj`, `docs/RELEASING.md`, `docs/PROJECT_PROGRESS.md`, `docs/IMPLEMENTATION_STATUS.md`. No feature, installer/update logic, signing key, user preferences, personal data, or `publish.ps1 -Run` changes; no application/installer launch, signing, upload, Git mutation or workflow dispatch. No runtime performance change or new benchmark is claimed.

Next owner action: review the five files, commit `chore(release): prepare v0.1.9`, push `master`, then run Actions -> Release -> Run workflow -> master and wait for green. Only afterward create/push annotated tag `v0.1.9` yourself. The tag run creates an unsigned-metadata draft after build/test/provenance checks; use the existing local signing/upload verification process before explicitly publishing. Do not regenerate keys, move old tags or overwrite old assets. Stop for owner review.

## Release validation: IDE copy-completion race (2026-11-03)

Status: **Completed locally; owner push and fresh GitHub validation pending.** The owner's manually dispatched [failed workflow 41638192475](https://github.com/imamulkadir/Fyltrix/actions/runs/41638192475) ran commit `20cc45dfc9589c3b7d80ac273f1bf25dad157036`. Compilation and release preflight succeeded. Exactly one of 509 application tests failed: `IdeIntegrationTests.Launches_the_program_directly_with_the_folder`. It checked destination existence and immediately read the file while Robocopy still held its write handle, producing a sharing-violation IOException. This was a test synchronization race, not a compiler, signing or PDF test failure. The earlier November 2 workflow failure was a different WebView2 initialization timeout; this fix does not claim a universal cure for every possible CI failure.

Changes: the existing launch test now waits, within its original 15-second budget, for the complete expected contents to become readable. Missing/locked/incomplete output is retried locally every 25 ms; wrong contents still fail with a diagnostic. Two regression tests hold a real exclusive writer open and reject readable but incorrect contents. The actual IDE launch, special-character path assertions and workflow failure gates remain intact. Only `tests/Fyltrix.Tests/ContextMenuTests.cs`, this progress file and `docs/IMPLEMENTATION_STATUS.md` are modified. No application code, workflow, dependency, version, settings, user data or publish workflow changes.

Verification: Release compilation succeeds with existing warnings only. All 15 focused IDE cases pass; the actual Robocopy launch passed ten independent fresh-process repeats. Updater tests: 134 passed; packaging tests: 39 passed; Python installer guard tests: 10 passed; Python release-tool tests: 14 passed. Isolated packaging initially could not find its installer source outside the repository; copying the unchanged source fixture into the temporary ancestor layout restored the expected test context without editing packaging code/tests. All results are under `%TEMP%\Fyltrix-ci-copy-race-20261103`. No installer was run or release created.

Final full application Release suite: **511 passed, zero failures/skips, 3 minutes 22 seconds**, recorded in `results/ci-race-release-isolated.trx`. The first full local run passed 509 and failed two pre-existing UI cases (background-tab refresh timeout and thumbnail Shift+Down selection). Both passed together in a focused fresh process and in the final full run. Their intermittency is documented, not claimed fixed by the IDE-test change; no tests were removed, skipped, or wrapped in whole-test retries. The final run was started separately after the other validation jobs completed; concurrent load is a possible contributor, not a proven root cause. The real observed GitHub sharing violation is directly reproduced by the new exclusive-writer regression and addressed by the bounded completion check.

Performance: the wait returns as soon as expected output is readable, with no fixed 15-second sleep and no application polling. The ten real launch checks completed individually in approximately 95-995 ms under concurrent verification load. These are test observations, not an application benchmark.

Next owner action: review and commit the three files, push `master`, then start a **new** Release workflow using **Run workflow -> master**. Re-running the failed old commit will not include this fix. No version bump or new tag is needed for manual validation. No commit, push, tag, workflow dispatch or remote mutation was performed. Suggested commit: `test(ci): wait for completed IDE launch copy`. Stop for owner review.

## Native PDF wheel synchronization and comments flyouts (2026-11-03)

Status: **Completed locally; owner review pending.** The owner explicitly chose to retain native WebView2. Plain vertical mouse-wheel input is now mirrored before source dispatch through the supported host-input API, rather than waiting for accessibility notifications. WebView2 SDK changes from 1.0.2903.40 to the minimum stable supporting version, 1.0.3351.48. Other native navigation retains the existing accessibility fallback. Bottom Comments panels are replaced by per-document Comments/count buttons and read-only flyouts; all three current sample notes are readable. Native note icons still do not open full Edge-style cards on the tested runtime; the flyout is the supported Fyltrix presentation, not a claim to repair that browser UI.

Verification: isolated builds succeeded with zero errors and existing nullable/DPI warnings only. Focused verification passed 44 cases; the expanded final comment-navigation regression passed independently, and the **final full application suite passed 509 cases, zero failures/skips (3 minutes 27 seconds)**. Evidence: `%TEMP%\Fyltrix-native-pdf-smooth-20261103\results\native-smooth-complete-full.trx`. Owner-approved isolated desktop testing confirmed three readable notes, native text selection/drag recovery, independent Ctrl-wheel zoom, Sync-off behavior, moved-window geometry refresh and immediate comment navigation after a wheel gesture. Both native page selectors reached page 2. Original PDF SHA-256 values remain unchanged; real clipboard and personal settings were not modified.

Performance: sixteen actual wheel inputs produced exactly sixteen peer dispatches, measured at 0.032-0.129 ms in the recorded burst. A six-second recording of actual native PDF pixels contained 243 sampled frames; motion began within one sampled 33 ms interval for the first burst and in the same sampled frame for the second. Dispatch time is not end-to-end rendering time. A small final settle correction remains possible; exact compositor frame lock, scrollbar/touch smoothness, precision-touchpad hardware and mixed-DPI acceptance are not claimed. No idle scroll polling or new PDF engine was introduced.

Files: exact 15-file stage list and implementation/test evidence in [PDF_NATIVE_SCROLLING_UI.md](PDF_NATIVE_SCROLLING_UI.md). README and the previous PDF report now explain the flyout and superseded bottom-panel behavior. Earlier unrelated marquee/focus changes and owner AGENTS.md edits remain preserved. Existing settings, launcher/updater/installer and `publish.ps1 -Run` are unchanged; no Git mutations were performed.

Next owner action: close the development app, rebuild with the usual `publish.ps1 -Run`, and review physical wheel scrolling plus Comments flyouts. Suggested commit: `fix(pdf): mirror native wheel input and show comments on demand`. **Stop for owner review.**

## PDF comments and native synchronized scrolling - 2026-11-03

Status: **Completed locally; owner review pending.** This stage fixes only PDF Compare comment readability and synchronized scrolling. Original PDFs remain native WebView2 documents. Existing PDFsharp extracts read-only comments into separate left/right panels. Public Windows UI Automation ScrollPattern drives optional bidirectional relative-position synchronization across different page counts. One shared MTA apartment serializes accessibility registration/removal; each pane's native actions wait for acknowledgement, keep the latest goal and have at most three bounded retries. Source/target echoes are suppressed, stale document subscriptions refresh after reload, and startup/watchdog timers stop when idle. No PDF-internal browser structures, global input simulation, new dependencies or raster replacement are used in production.

Exact samples: the original `file-sample_150kB.pdf` has a page-1 sticky note by `imamu` containing `test comment`; the copy has no comment text. Native note-icon clicking did not expose the popup, so the explicitly authorized Comments panel fallback is used. The latest owner-approved desktop fixture visibly showed that text and the right-side empty state; both scroll directions, disable/re-enable independence, comment navigation to page 1 and a real wheel event passed. Original SHA-256 values remain unchanged. Disposable profiles/caches and a clipboard stub protected owner preferences and clipboard. Latest desktop evidence: `%TEMP%\Fyltrix-pdf-comments-final-serialized-20261103`.

Verification: isolated build succeeds with zero errors and existing warnings. Final focused run: **27 passed, zero failures/skips** (`pdf-sync-serialized-driver-final.trx`). Five further fresh-process native runs passed, exercising **15 alternating-side reloads**, bidirectional synchronization, disable/re-enable and event cleanup. Final complete application suite: **492 passed, zero failures/skips, 3 minutes 20 seconds** (`pdf-sync-serialized-final-full.trx`). This includes preserved earlier marquee tests. Evidence lives under `%TEMP%\Fyltrix-pdf-comments-sync-20261103\results`; conventional temporary test/probe paths preserve existing placement helper discovery. Earlier failed intermediate runs were investigated, not reported as passes. The final source driver is a separate hidden accessibility client that establishes only source input; assertions still verify actual source and product-driven target positions separately. Automated windows remain offscreen. Final punctuation-only reconnect-tooltip cleanup is followed by a rebuild/native handover smoke check; its result is recorded in the detailed report.

Files: exact list in [PDF_COMMENTS_SYNC.md](PDF_COMMENTS_SYNC.md), including source, regression tests, README and progress records. [NATIVE_PDF_COMPARE.md](NATIVE_PDF_COMPARE.md) remains the historical first-stage report and links to this follow-up. Earlier marquee/native-focus work and owner AGENTS.md edits are preserved separately. Installer, launcher, updates, dependencies, personal settings and `publish.ps1 -Run` remain unchanged; no Git mutations occurred.

Performance: final sample observations were 31 ms left-to-right and 403 ms right-to-left target observation waits (20 ms sampling), not a general latency guarantee or before/after CPU/memory benchmark. Parsing is off the UI thread; idle scroll polling is absent. Limits: comments are read in the panel, not a repaired native popup; scrolling is relative progress; comment page jumps estimate continuous vertical layout; native accessibility availability/runtime behavior still applies. Real clipboard copying, native Find match results and mixed-DPI acceptance are not newly claimed.

Next owner action: close the development app, rebuild using the usual `publish.ps1 -Run`, compare the originals, review the changes and commit manually if satisfied. Suggested commit: `fix(pdf): expose annotation comments and restore compare scroll synchronization`. **Stop for owner review.**

## Native PDF Compare and window activation (2026-11-03)

Status: **Implementation and automated verification completed; remaining native acceptance checks documented for owner review.** The owner's latest attachment explicitly replaced the earlier marquee task with these two Compare issues. Existing marquee changes and AGENTS.md edits remain preserved and must be reviewed separately; no additional marquee or annotation work was started under this request.

Confirmed causes: Compare enabled `ReportsScroll`, which selected page-image PDF rendering; its `Owner` assignment made it an owned modeless window despite using `Show()`. PDFs now use the same original-file/native WebView2 route as Preview. Native PDF synchronization is disabled with an explanatory tooltip; other comparison families retain existing synchronization. Compare is independent, uses existing taskbar identity/icon and initial monitor-aware placement, and retains opening-window shutdown/resource cleanup without ownership or focus-stealing handlers.

Files for this Compare stage: `src/Fyltrix/Preview/PreviewPane.cs`, `src/Fyltrix/UI/CompareWindow.cs`, `src/Fyltrix/UI/WindowPlacement.cs`, the legacy-route comment in `src/Fyltrix/Viewer/viewer.js`, `tests/Fyltrix.Tests/CompareScrollTests.cs`, new `tests/Fyltrix.Tests/NativePdfCompareTests.cs`, `tests/Fyltrix.PlacementProbe/Program.cs`, new `tests/Fyltrix.PlacementProbe/CompareDesktopProbe.cs`, `README.md`, `docs/NATIVE_PDF_COMPARE.md`, this file and `docs/IMPLEMENTATION_STATUS.md`. The selection changes in FolderView/MainWindow predate this scope and are not part of the Compare implementation.

Verification executed: isolated build succeeds with zero errors and three existing application/test warnings. Focused native/non-PDF comparison run passed 13 cases; subsequent six-case native edge/swap/ownership run passed, including unchanged source hashes for encrypted, malformed and 500-page fixtures. Final complete application suite: **481 passed, 0 failed, 0 skipped**, reported duration **2 minutes 51 seconds**, including the pre-existing paused marquee tests. TRX: `%TEMP%\Fyltrix-native-compare-20261103\results\native-compare-full-suite.trx`. The test/probe outputs use a conventional directory layout entirely under that temporary root because existing placement tests locate their helper by a `bin` path substitution; no unrelated test changes were needed. Scoped diff whitespace checks passed. Owner-approved desktop checks showed native PDF toolbars, actual text highlighting, independent left zoom/page 2 with right still page 1, main above restored/maximized Compare, and Alt+Tab switching. The test app closed normally; owner application windows and original profile were not closed/reset. No real clipboard copying, actual Find match success or individual taskbar preview selection is claimed.

Performance: the 500-page sample passes native-host handling and source preservation checks. Native Compare no longer invokes PDF page-to-bitmap rendering or its image-page scrolling machinery. No dedicated before/after Compare CPU/memory benchmark was run, so no numerical speed or memory improvement is claimed. Native UI interactions were responsive in the exercised desktop fixture. Existing non-PDF scroll regression cases pass.

Remaining checks and evidence: see [NATIVE_PDF_COMPARE.md](NATIVE_PDF_COMPARE.md). Installer, launcher, updates, dependencies, user preferences and `publish.ps1 -Run` are unchanged. Suggested commit: `fix(compare): restore native PDF viewing and normal window activation`. Stop for owner review; no Git mutations or publication were performed.

## Execution agreement

- Work one stage at a time, build/test it, report the evidence, and stop for explicit approval before the next stage.
- Use **`master` exclusively** for future Fyltrix development. Do not create feature, development, release, or temporary branches.
- **Latest standing instruction (2026-11-01): NEVER commit or push automatically. The owner ALWAYS performs those actions; inform them when changes are ready.** Earlier one-off authorizations for `0.1.1`/`0.1.2` preparation are historical and do not authorize any future commit/push. Tags, merges, release publication and repository-setting changes likewise remain owner-controlled. Read-only Git inspection and local file edits/tests are allowed.
- Preserve the current application, user profile, and `publish.ps1 -Run`. Installer experiments must use separate output/install directories and a distinct sandbox installer identity.
- Keep fully automatic installation disabled until manual installation, verification, rollback, and user-data preservation have passed, followed by explicit approval for any further rollout.

## README actual application screenshots (2026-11-03)

Status: **Completed.** Replaced all six commented screenshot placeholders with actual lossless PNG captures: main interface, large-icon view, real Markdown preview, advanced search, batch-rename preview, and Toolbar settings. Added descriptive captions/alt text and capture provenance. Existing README structure/features remain intact.

Captures use real Release-built 0.1.8 WPF components, demo files, disposable settings/caches, a stub clipboard and cloaked offscreen windows. Final capture confirmed unchanged foreground. The WebView2 document was captured by its real browser API and placed at its measured window rectangle; no UI was fabricated. Only rename previews were shown, with no rename/search operation executed. No installed application, real preferences, release assets or Git refs were changed.

Files created: six PNGs and capture notes under `docs/screenshots/`. Files modified: `README.md`, this progress file, `docs/IMPLEMENTATION_STATUS.md`. Temporary capture code/build output remains outside the checkout.

Verification: capture harness and application dependencies built successfully (final incremental build: zero warnings/errors; initial application compilation reported the existing CS8602/WFAC010 warnings). All six captures completed, were visually inspected, and decoded successfully. README GFM rendering, local image/link/anchor checks and diff whitespace checks passed. No full application regression suite or performance benchmark was rerun for image/documentation-only changes. Real production installation/taskbar/settings acceptance remains pending as previously documented.

Capture cleanup: automatic approval review blocked deletion of the capture-owned `D:\Fyltrix-Demo` directory under its command policy. It remains in place; no alternative deletion was attempted.

Next owner action: review the README, then commit/push these documentation assets if satisfied. No tag or release rebuild is needed. Suggested commit: `docs(readme): add actual application screenshots`. **Stop here.**

## Release 0.1.8 publication and README status (2026-11-02)

Status: **Published by owner; public update feed verified.** Read-only GitHub inspection confirms latest stable `v0.1.8`, published at `2026-11-02T17:46:12Z`, with all six asset sizes and SHA-256 digests unchanged from the verified draft. The existing release tool's live `check 0.1.6` returned `Available`, version `0.1.8`, verification `None` (no verification error). `check 0.1.8` returned `UpToDate`; no installation was performed. These checks fetched public metadata/signature using the existing pinned trust, without changing user preferences.

README now describes published taskbar behavior and the required one-time Setup/old-pin refresh. Its landing-page structure, feature list, dynamic badges and existing data paths are preserved. Release/current-status/taskbar documentation now reflects publication; previous verification entries remain historical records.

Files modified: `README.md`, `docs/PROJECT_PROGRESS.md`, `docs/IMPLEMENTATION_STATUS.md`, `docs/RELEASING.md`, `docs/WINDOWS_TASKBAR.md`.

Verification: live public feed checks above; README GFM rendering, local links/anchors, structural checks and eight live badge checks passed; `git diff --check` passed. No new application build or full regression suite was run for documentation-only changes; the tagged CI's previously verified 640 tests remain the application evidence. No application/Setup was launched, user settings changed, or Git/remote mutation performed.

**Next owner actions:** install the published Setup, refresh old taskbar pins from the updated Start Menu shortcut, check relaunch/grouping and preserved tabs/Favourites/toolbar/update preferences, and confirm Settings reports 0.1.8 as current. Real-screen acceptance remains pending. Genuine screenshots are optional future README work; placeholders stay commented out. No further release tag is needed for this documentation update. Suggested commit: `docs(release): reflect published v0.1.8 and taskbar guidance`. **Stop here.**

## Release 0.1.8 uploaded metadata verification (2026-11-02)

Status: **All six draft assets verified; owner publication pending.** The owner uploaded the two prepared public metadata files to the existing [0.1.8 draft](https://github.com/imamulkadir/Fyltrix/releases/tag/untagged-3daf1c261201d956b0cc). Read-only inspection found exactly the original ZIP/Setup and checksum sidecars plus `Fyltrix-release.json` and `.sig`, all uploaded, stable and still draft. Original asset IDs/API identities, sizes, digests, creation/update timestamps, types and labels match the previously provenance-verified packages. The draft save changed its temporary URL slug and normalized release-note line endings; notes match after CRLF/outer-whitespace normalization, with no substantive change.

Downloaded the uploaded manifest (325 bytes, SHA-256 `776cd1152e9160a253255756d4575497b1d51d4813a55ba28ba8c7b925251754`) and signature (88 bytes, SHA-256 `7c3577942fe93e90ae0b5966ac533255a40c60abe9e1c7c080a9b6e52845a166`). Both match GitHub's digest/size and the local verified files **byte for byte**. A single metadata network timeout was retried within the bounded download check; no content was regenerated/replaced. The shipped-default-pin, both-package and tamper-rejection verification already executed on these identical bytes remains applicable; no additional full suite/build/signing/installer run is claimed.

Evidence: `C:\Users\imamu\AppData\Local\Temp\Fyltrix-release-validation-0.1.8-20261102-231934\uploaded-20261102-234259`, referenced by `C:\Users\imamu\AppData\Local\Temp\Fyltrix-release-validation-0.1.8-20261102-231934\uploaded-current.txt`, including uploaded draft snapshot, downloaded metadata and `verification.json`. The owner committed earlier verification documents as `1ee75e8`; only those three documentation files differ from the exact `v0.1.8` source. No source, version, tag, private-key/password, live profile, installation or remote release mutation was performed during this check. No application/Setup executable was launched.

Files modified: this file, `docs/IMPLEMENTATION_STATUS.md`, `docs/RELEASING.md`. Suggested owner documentation commit: `docs(release): verify uploaded v0.1.8 metadata`.

**Next owner action:** publish the existing draft as the latest stable release, keeping all six verified assets intact, then report publication. Read-only public-feed authentication checks follow; production taskbar/pin/settings review remains a separate owner action. No new tag, signing run or workflow rerun is needed. The agent has not published anything. **Stop here.**

## Release 0.1.8 actual-draft verification and local metadata signing (2026-11-02)

Status: **Actual draft verified and metadata signed locally; owner upload/publication pending.** [Tag workflow 41484456231](https://github.com/imamulkadir/Fyltrix/actions/runs/41484456231) succeeded at `d9b874552bea6cfec32a99fbc47a8d6f5218d6b7`, matching clean master/HEAD and the local/remote peeled `v0.1.8` tag (annotated object `73ecd5d5a3614b3b63b09e0987112dd99ed1ff7c`). Both manual and tagged validation passed on the formerly failing image **20261030.294.1**. Actual tagged logs confirm **640 tests passed** (443 application, 134 updater, 39 packaging, 10 installer-builder, 14 release-tooling), plus packaging and provenance. No new local full-suite run is claimed for this signing/verification stage.

- Inspected the real stable draft [Fyltrix 0.1.8](https://github.com/imamulkadir/Fyltrix/releases/tag/untagged-55278320f08879f09703): exactly the ZIP, Production Setup and their checksum sidecars; notes match the tagged changelog. The connector cannot access the draft, so read-only inspection/download used authenticated GitHub CLI/API. A stalled CLI download was stopped by its exact owned workspace/process; bounded authenticated API downloads completed. Tokens were not printed or forwarded across asset-host redirects. Only fully downloaded, digest-checked `api-downloads` copies were used; earlier root-level CLI files are unused diagnostic remnants.
- All four asset sizes and GitHub SHA-256 digests match. Both strict checksum sidecars match. `gh attestation verify` passed for both actual packages, restricted to this repository, release workflow, `refs/tags/v0.1.8`, exact source digest and GitHub-hosted runners. ZIP SHA-256 **6f9127e9edb94130526d97bd6c988c44a653bd1ae564c5666a582d0f2c9333f0**, size 88,075,491; Setup SHA-256 **6bc4c89e3f534aed68c8ba04de0eb3c883839d988402f96b67d33ce71c9290de**, size 62,642,030. These are CI assets, not local preparation packages.
- Inspected the extracted actual ZIP read-only: pointer selects 0.1.8, launcher/payload executable/assembly and Setup versions are 0.1.8.0, MIT contents match, and test hooks/probes are excluded. Setup remains NotSigned for Windows Authenticode; updater metadata signing is separate.
- Built the existing standalone tool from exact clean tagged source outside the checkout (zero warnings/errors). Its guarded `sign-release` command checked the existing pinned P-256 public/private key match and wrote only local `signed/Fyltrix-release.json` and `.sig`. No key/password was printed, copied, regenerated, uploaded or changed. The completion helper was not invoked because it uploads files.
- A separate local console verifier referenced **the update and Bootstrap DLLs actually shipped in the attested ZIP**, verifying their loaded hashes. Its default production trust accepted the signature and both packages. Altered manifest, signature and package bytes, empty trust and draft metadata were rejected. Because the release remains a draft, these checks used an explicitly labelled offline projection of the eventual canonical public metadata; this is not a live public-feed check or remote publication. No application/Setup executable was launched or installed.

Manifest SHA-256: `776cd1152e9160a253255756d4575497b1d51d4813a55ba28ba8c7b925251754`. Signature SHA-256: `7c3577942fe93e90ae0b5966ac533255a40c60abe9e1c7c080a9b6e52845a166`. All **997** protected profile/installed/portable/signing-primary/backup hashes and Git refs match before/after. Final read-only draft inspection still shows four original assets, unchanged identities/digests/sizes/timestamps/notes and draft state; download counters are intentionally excluded because inspection itself can change them.

Files modified: this file, `docs/IMPLEMENTATION_STATUS.md`, `docs/RELEASING.md`. Evidence: `C:\Users\imamu\AppData\Local\Temp\Fyltrix-release-validation-0.1.8-20261102-231934`, pointer `%TEMP%\Fyltrix-release-validation-0.1.8-current.txt`. Signed files for owner upload: `C:\Users\imamu\AppData\Local\Temp\Fyltrix-release-validation-0.1.8-20261102-231934\signed`. Signing/provenance/shipped-verifier and preservation records are retained in that private workspace.

Suggested owner documentation commit: `docs(release): verify and sign v0.1.8 draft locally`. No commit, push, merge, tag change, upload, draft edit or publication was performed; only these documentation edits follow the clean-source signing checks.

**Next owner action:** upload exactly `Fyltrix-release.json` and `Fyltrix-release.json.sig` from that signed directory to the existing 0.1.8 draft; do not replace its packages/checksums. Keep it as a draft and report upload completion so the actual uploaded metadata can be checked against these verified bytes before publication. Do not rerun signing or create another tag. Real production taskbar/pin/update-through-pin review remains an owner action after publication. **Stop here.**

## Comparison browser-host initialization investigation and fixture update (2026-11-02)

Status: **Implemented and verified locally; the failing hosted image still needs confirmation.** [Manual run 41446270527 / job 125280017734](https://github.com/imamulkadir/Fyltrix/actions/runs/41446270527/job/125280017734) failed on preparation commit `f73f6faeac9a16a39c780620415bac6aa800a8cd`. The improved diagnostics identify `comparison WebView2 initialization`, not missing rendered Markdown: the controller never became ready within its 30-second deadline. The previous readiness change was insufficient. No new tag/version is needed for this failure: this was manual master validation of 0.1.8, and read-only remote inspection found no 0.1.8 tag.

Compared complete job setup logs: green manual runs 41430955550, 41440637685 and 41444405832 used `windows-2025-vs2026` image **20261021.278.1**; this failure used **20261030.294.1**. Official runner manifests list bundled Edge 155.0.4283.62 versus 156.0.4314.56 ([older manifest](https://github.com/actions/runner-images/blob/win25-vs2026/20261021.278/images/windows/Windows2025-VS2026-Readme.md), [newer manifest](https://github.com/actions/runner-images/blob/win25-vs2026/20261030.294/images/windows/Windows2025-VS2026-Readme.md)). These are Edge browser versions, **not measured hosted WebView2 runtime versions**. Image correlation does not establish an Edge/WebView2 regression. Local WebView2 is 156.0.4314.58, and the unchanged failing test also passed on a private desktop. Neither headless operation alone nor the exact hosted cause was reproduced locally.

Test-only changes remove demonstrable fixture weaknesses and provide the missing native diagnostics:

- Fixed 1400×900 off-screen comparison windows and explicit owner sizes replace dependence on the runner's monitor work area. Force layout and wait for WPF Loaded before testing browser controls.
- Normal scrolling scenarios wait for their initial visible browser controller(s) before changing display mode, avoiding a test setup that immediately hides an initializing diff controller. Existing 30-second initialization, 20-second content and 15-second scroll-layout limits are unchanged.
- Added a separate real-WebView2 regression that switches mode **without waiting for the initial preview**, then verifies both documents and all original bidirectional/disabled/re-enabled scroll assertions. Its captured initial host state was Loaded/visible with nonzero dimensions but not ready, so the fast-transition coverage was retained rather than skipped.
- Each test host gets its own browser profile under its already unique disposable settings root. The old fixed `%TEMP%\FyltrixTests-WebView2` path could reconnect separate processes/desktops to cached state or a still-exiting browser; the old directory and live application profile are not deleted or changed.
- TRX output now records actual runtime version, test profile and work area. Startup timeouts include pane/WebView Loaded/visible state, engine/controller state, real dimensions and visible WPF error/loading messages, including when no browser DOM exists yet.

Verification: **11 comparison cases passed on the normal desktop and all 11 passed on a separate private desktop** (22 focused executions with final profile isolation). Full **443 application cases**, 134 updater, 39 packaging, 14 release-tooling and 10 installer-builder tests passed: **640 total**. An earlier pre-profile-change focused run also passed but is not counted as final isolated-profile verification. All **991** protected profile/installed/portable hashes and Git refs match. Product code, dependencies, workflow, installer/update/profile identities, Windows security and signing stores are unchanged. No timeout increase, test skip, CI retry wrapper, runner pin or browser/security workaround was introduced. No performance/package rebuild was required for test-harness-only changes. Existing compiler warnings remain.

Files modified: `tests/Fyltrix.Tests/CompareScrollTests.cs`, `tests/Fyltrix.Tests/TestSetup.cs`, `CHANGELOG.md` (0.1.8 candidate notes), this file, `docs/IMPLEMENTATION_STATUS.md`, `docs/RELEASING.md`. Evidence: `C:\Users\imamu\AppData\Local\Temp\Fyltrix-ci-host-init-20261102-221141` (pointer `%TEMP%\Fyltrix-ci-host-init-current.txt`): green/failed hosted logs, unchanged private-desktop baseline TRX, updated normal/private/full TRX, captured host diagnostics and preservation records.

Suggested owner commit: `test(compare): isolate browser hosts and stabilize startup`.

**Next owner action:** review and commit/push these six files, then run Release manually on master. Verify the exact commit and runner image/runtime from that run before calling the hosted issue resolved. A pass on the older image alone does not establish the newer image is fixed. Keep application version 0.1.8; do not create a new version or tag yet, and preserve failed 0.1.7. After updated hosted validation, the owner can create the still-new 0.1.8 tag. No agent commit, push, tag, workflow dispatch, release mutation, signing or installed-application launch occurred. **Stop here.**

## Release 0.1.8 preparation after successful hosted test validation (2026-11-02)

Status: **Prepared and verified locally; owner commit/push and exact-preparation hosted validation pending.** Read-only inspection confirmed [manual workflow 41444405832](https://github.com/imamulkadir/Fyltrix/actions/runs/41444405832) succeeded on the owner's test-readiness commit `b9c77e08319cf8ef1021978e4cea44ada9b1832a`. Preparation began with a clean master at that commit, matching remote master. No local/remote 0.1.8 tag existed. Published latest remains 0.1.6; the failed 0.1.7 tag/source are preserved unchanged.

- Kept `Unreleased` and prepared `0.1.8`, dated 2026-11-02, with the taskbar fix, README refresh and test-readiness change. Carried forward the still-unpublished 0.1.7 improvements explicitly so the actual release notes describe changes since published 0.1.6. Retained one-time Setup/repin guidance, unsigned-executable caveats and pending visual taskbar checks; did not claim the exact earlier hosted failure was proven.
- Used the existing version helper and `Test-Release.ps1 -Tag v0.1.8`: Version 0.1.8, FileVersion/AssemblyVersion 0.1.8.0. No production runtime, workflow, installer ownership, profile or trust changes beyond these application version fields.
- All **639 fresh tests passed**: 442 application, 134 updater, 39 packaging, 14 release-tooling and 10 installer-builder. Isolated self-contained win-x64 payload/NativeAOT launcher and both Production/Sandbox installers built. Launcher, payload executable/assembly and both Setup numeric/file versions match 0.1.8.0. Directory/ZIP pointers select 0.1.8 and test probes/hooks are excluded. Existing nullable/high-DPI warnings remain.
- Fresh Sandbox Setup on a private desktop launched its real 0.1.8.0 payload through the root launcher. Actual shortcut/process/window identity and stable relaunch checks, listing/preview readiness, normal exit and uninstall passed. Production Setup was not run. All **991** protected profile/installed/portable hashes match, Sandbox registration/shortcut are absent, and Git refs are unchanged.

Files modified: `CHANGELOG.md`, `src/Fyltrix/Fyltrix.csproj`, this file, `docs/IMPLEMENTATION_STATUS.md`, `docs/RELEASING.md`. Evidence: `C:\Users\imamu\AppData\Local\Temp\Fyltrix-release-prep-0.1.8-20261102-183118` (pointer `%TEMP%\Fyltrix-release-prep-0.1.8-current.txt`), with full TRX/logs, version validation, package versions, native smoke and preservation checks. Local packages are verification outputs, not GitHub-attested release assets. No performance rerun for version/documentation-only preparation; the prior mixed measurements and actual taskbar acceptance remain owner checks in [WINDOWS_TASKBAR.md](WINDOWS_TASKBAR.md).

No commit, push, merge, tag change, workflow dispatch, signing/upload, publication or replacement of the installed/portable copy was performed. Existing user data, signing stores and `publish.ps1 -Run` remain untouched.

Suggested owner commit: `chore(release): prepare v0.1.8`.

**Next owner action:** review and commit/push these five files to master, manually run Actions → Release → Run workflow → master, and wait for green on that exact preparation commit. Then the owner creates/pushes a new `v0.1.8` tag without moving/removing 0.1.7. Verify the actual tagged draft packages/provenance before preparing signed metadata; publication and visual production taskbar/update-through-pin review remain owner actions. **Stop here.**

## Comparison preview readiness hardening after repeated tagged CI failure (2026-11-02)

Status: **Test change verified locally; hosted confirmation pending.** Attempt 2 of [tagged run 41434209477](https://github.com/imamulkadir/Fyltrix/actions/runs/41434209477) again failed the same `CompareScrollTests.Side_by_side_scrolls_together_and_can_be_switched_off` wait for `Left paragraph 300`: 441 application cases passed and one timed out. It stopped before package/attestation/draft creation. A separate green [manual master run 41440637685](https://github.com/imamulkadir/Fyltrix/actions/runs/41440637685) is not a successful tagged release. The prior one-retry recommendation has been exhausted; do not repeat the unchanged tag again to try to obtain green.

Changed only the comparison test's readiness helper, with no production application or workflow changes:

- Browser initialization gets its own bounded 30-second wait, matching the existing preview-host and wheel-test pattern. Previously it consumed part of the same 20 seconds used for document content.
- The unchanged 20-second content wait checks the expected fixture text in the viewer root/body DOM, rather than layout-dependent whole-body `innerText` on an off-screen window.
- A separate bounded 15-second check requires the real scroll hooks and enough laid-out scroll range for the 1500-pixel assertions. Existing bidirectional synchronization, independent scrolling when disabled and immediate alignment when enabled remain asserted.
- On timeout, TRX now contains the current file, document URL/readiness/visibility, text excerpt, scroll-hook presence and root geometry. This will distinguish startup, navigation/rendering and layout/hook failures on the next hosted attempt. No retries, skipped tests, weakened expected text or removed scroll assertions were introduced.

This addresses a test-readiness weakness; local results **do not prove the precise cause of either hosted failure or guarantee a green hosted run**. Reference contracts: [WebView2 initialization](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.wpf.webview2.ensurecorewebview2async) and the HTML standard's [rendered innerText](https://html.spec.whatwg.org/multipage/dom.html#the-innertext-idl-attribute).

Verification: the **10 comparison-scroll cases passed in three separate test-host runs** (30 focused executions), then the complete **442 application cases passed**. Fresh updater (134), packaging (39), release-tooling (14) and installer-builder (10) suites also passed: **639 total regression cases**, plus the focused runs. Compilation succeeded with the existing nullable/high-DPI warnings. All **991** protected installed/profile/portable file hashes and Git refs match. Application/installer/update/shutdown/persistence code, published 0.1.6, `publish.ps1 -Run`, signing stores and existing pins were not changed. No performance benchmark or package rebuild was repeated for a test-only change.

Files modified: `tests/Fyltrix.Tests/CompareScrollTests.cs`, `CHANGELOG.md` (Unreleased only), this file, `docs/IMPLEMENTATION_STATUS.md`, `docs/RELEASING.md`. Evidence: `C:\Users\imamu\AppData\Local\Temp\Fyltrix-compare-readiness-20261102-181620`; pointer `%TEMP%\Fyltrix-compare-readiness-current.txt`. No commit, push, merge, tag, workflow dispatch/rerun, signing, release publication or installed-application launch was performed.

Suggested owner commit: `test(compare): wait for preview initialization and layout`.

**Next owner action:** review, commit and push these five files to master, then run Actions → Release → Run workflow → master to validate the updated test. Do not retry `v0.1.7` expecting it to include an untagged fix. After hosted confirmation and approval, prepare a new release version/tag (next candidate 0.1.8), preserving the existing failed 0.1.7 tag. This stage intentionally leaves application version at 0.1.7 and does not begin the next release-preparation stage. **Stop here.**

## Release 0.1.7 tagged-workflow failure investigation (2026-11-02)

Status: **Investigated; one retry of the unchanged tagged run recommended, hosted result pending.** The owner committed preparation as `fa454b25abceaf3b2bbaf1cc82ff95ce709cc115`, passed manual [workflow 41430955550](https://github.com/imamulkadir/Fyltrix/actions/runs/41430955550), and created/pushed annotated `v0.1.7` (tag object `1a0e108a5f4f9d352a61bf274d7ec9d900ec98f6`). The tag and HEAD resolve to that same preparation commit. Tagged [workflow 41434209477](https://github.com/imamulkadir/Fyltrix/actions/runs/41434209477) failed in the application-test step: 441 passed, one failed.

Exact failure: `CompareScrollTests.Side_by_side_scrolls_together_and_can_be_switched_off`, `TimeoutException: Timed out waiting for: content Left paragraph 300`, at `WaitForText`'s 20-second content-readiness deadline. The other nine comparison-scroll cases, including Markdown wheel synchronization, passed on that runner. Packaging, attestation and draft creation were not reached. Read-only release inspection found no 0.1.7 draft/assets; published 0.1.6 remains intact.

Inspected the comparison/preview initialization and the test's serialized UI collection. Rebuilt and ran the same default Release test configuration as CI: all **10 comparison-scroll cases passed**, the exact failing case then passed **two additional separate test-host runs**, and the entire **442-test application suite passed** afterward. This is consistent with an intermittent preview-readiness failure; its precise cause (host load, browser initialization/navigation, or another intermittent condition) has **not** been established. Do not describe it as a confirmed product fix or a proven slow runner.

No application/test/workflow/version/changelog change is justified by the evidence so far. No tests were skipped, assertions removed, deadlines increased or retry wrappers added. No commit, push, tag movement, workflow rerun/dispatch, release mutation, signing or installation was performed. Source and tag remain unchanged. Only this investigation's three documentation files were edited; no performance benchmark or unrelated suite/package rebuild was repeated for documentation-only work. The full 639-suite/package verification from preparation remains historical evidence; this investigation freshly executed the 442 application cases and 12 focused cases, not all 639.

Files modified: `docs/PROJECT_PROGRESS.md`, `docs/IMPLEMENTATION_STATUS.md`, `docs/RELEASING.md`. Evidence: `C:\Users\imamu\AppData\Local\Temp\Fyltrix-ci-v0.1.7-20261102-172329` (pointer `%TEMP%\Fyltrix-ci-v0.1.7-current.txt`), containing failed-job logs, the downloaded hosted TRX/release notes, local focused/full TRX and logs.

Suggested optional owner documentation commit: `docs(ci): record v0.1.7 preview test timeout`. Committing these notes is not required before retrying the original run and does not change the existing tag.

**Next owner action:** open the failed tagged run and choose **Re-run jobs → Re-run failed jobs** once. Keep `v0.1.7` unchanged; no new version/tag/commit is required for this retry. Let every original test/package/provenance gate run normally. If it fails again, retain the new logs and investigate/harden the failing condition before more release attempts rather than repeatedly retrying or bypassing tests. On green, verify the actual draft assets/provenance before signing/publication. **Stop here; hosted success and release verification are still pending.**

## Release 0.1.7 preparation (2026-11-02)

Status: **Prepared and verified locally; owner commit/push and hosted validation pending.** Started from clean `master` at `561aa4ffae51cd0f98b1c6ae42c7794f90445a1c`, matching remote master. Published latest remains 0.1.6; no local or remote `v0.1.7` tag existed at preparation. The owner's successful manual [workflow 41424901741](https://github.com/imamulkadir/Fyltrix/actions/runs/41424901741) covers taskbar implementation commit `fd0067a`; the following commit changes README only. GitHub's connector rejected the workflow-history endpoint, so this read-only inspection used GitHub CLI.

- Kept `Unreleased` and moved the taskbar work into `0.1.7`, dated 2026-11-02. Added the README refresh and explicit one-time Setup/repin and unsigned-executable notes. Did not claim visual taskbar checks passed or include unrelated/new functionality.
- Ran the existing `Set-ReleaseVersion.ps1 -Version 0.1.7` and `Test-Release.ps1 -Tag v0.1.7`: application Version is 0.1.7, FileVersion/AssemblyVersion 0.1.7.0. Shared assembly/update/installer/profile identities remain unchanged.
- Re-ran all **639 tests**: 442 application, 134 updater, 39 packaging, 14 release-tooling and 10 installer-builder, all passed. Built isolated self-contained win-x64 payload and NativeAOT launcher; compiled both Production and Sandbox Setup. Packaged launcher/payload and both Setup versions are 0.1.7.0; ZIP layout/current pointer and test-hook exclusions passed. Existing nullable/high-DPI compiler warnings remain.
- Ran the newly compiled Sandbox Setup on a private desktop, launched its real versioned application through the native root launcher, and checked the actual shortcut/process/window AppUserModelID and stable relaunch properties. Version 0.1.7.0, listing/preview readiness, normal exit and clean uninstall passed. Production Setup was not executed.
- All **991** protected profile/installed/portable file hashes match before/after. Sandbox registration/shortcut are absent and Git refs unchanged. No commit, push, merge, tag, signing/upload, release mutation or replacement of the installed/portable copy occurred. `publish.ps1 -Run` is unchanged. Performance probes were not repeated for metadata/documentation-only preparation; mixed prior measurements and real-taskbar acceptance remain documented in [WINDOWS_TASKBAR.md](WINDOWS_TASKBAR.md).

Files modified: `CHANGELOG.md`, `src/Fyltrix/Fyltrix.csproj`, this file, `docs/IMPLEMENTATION_STATUS.md`, `docs/RELEASING.md`. Evidence: the directory named by `%TEMP%\Fyltrix-release-prep-0.1.7-current.txt` (this run: `C:\Users\imamu\AppData\Local\Temp\Fyltrix-release-prep-0.1.7-20261102-163419`), with TRX/logs, version validation, local packages, native verification and preservation records. These local packages are test outputs, not GitHub-attested release assets; later signing must use the actual tagged CI packages.

Suggested owner commit: `chore(release): prepare v0.1.7`.

**Next owner action:** review and commit/push the five preparation files to master yourself, then run Actions → Release → Run workflow → master and wait for green on that exact preparation commit. Do not tag before it passes. After approval, the owner creates/pushes `v0.1.7`; inspect the resulting draft's actual assets/provenance before local signing and publication. Production visual pinning/grouping, update-through-pin and responsiveness remain owner checks. **Stop here.**

## Windows taskbar identity and stable relaunch (2026-11-02)

Status: **Implemented and automated verification passed; manual Windows taskbar review pending.** Found the live Start Menu shortcut targeting the root launcher and the live taskbar pin targeting `app-0.1.6\Fyltrix.exe`, both without an AppUserModelID. Added shared version-independent production/Sandbox identities, distinct portable/development identities, early process identity, main-window identity plus stable root relaunch/icon properties, and matching Inno shortcut metadata. Native window properties are released on destruction. Installer AppId, updater identities/protocol, payload layout, settings and `publish.ps1 -Run` remain unchanged.

Verification: **639 tests passed** (442 application, 134 updater, 39 packaging, 14 release-tooling, 10 installer-builder). Published an isolated self-contained payload/NativeAOT launcher and compiled both installer modes. A private-desktop fresh Sandbox install/root launch verified the actual shortcut/process/window identity and root relaunch path, preview readiness, normal exit and clean uninstall. Production Setup was not run. Existing compiler warnings remain. Detailed preservation/performance results and manual check limits: [WINDOWS_TASKBAR.md](WINDOWS_TASKBAR.md).

Security inspection: downloaded Setup and installed launcher/payload are unsigned; downloaded Setup carries Internet-zone Mark-of-the-Web. The inspected installed executables do not. The taskbar identity fix does not remove reputation/security warnings; no security settings, streams, certificates or existing user pins were modified. Exact warning classification remains dependent on its message/event. Current code is still unreleased; the installed/published 0.1.6 was preserved.

Files created/modified: `src/Fyltrix.Bootstrap/WindowsAppIdentity.cs`, `src/Fyltrix.Launcher/Program.cs`, `src/Fyltrix/App.cs`, `src/Fyltrix/UI/TaskbarIdentity.cs`, `src/Fyltrix/UI/MainWindow.cs`, `packaging/installer/Fyltrix.iss`, focused packaging/application tests and the test-only startup-hook probe, `docs/WINDOWS_TASKBAR.md`, this file, `docs/IMPLEMENTATION_STATUS.md`, `README.md`, `CHANGELOG.md`.

Suggested owner commit: `fix(windows): unify launcher and application taskbar identity`.

**Next owner action:** review/commit/push when ready, prepare a new release through the existing process, install it and perform the one-time unpin/re-pin plus visual taskbar checks in the linked report. No commit, push, merge, tag or release was performed by the agent. **Stop here for manual review.**

## Release 0.1.6 published-feed verification and obsolete-workspace cleanup (2026-11-01)

Status: **Published feed verified; requested cleanup completed.** The owner published `v0.1.6` as latest. All six release assets remain present, with unchanged package and signed-metadata digests. Executed the existing CLI against the real public feed: `check 0.1.5` returned Available for authenticated 0.1.6; `check 0.1.6` returned UpToDate. These checks did not install or download a package.

At the owner's explicit request, deleted only the superseded **draft** release 0.1.3 (release ID 430411249, formerly `untagged-2d80b5738df1ffb14ac5`). Its Git tag was preserved. No published release was removed; 0.1.6 remains latest.

Inspected and removed all **28** obsolete `D:\Tools\Fyltrix-*` experiment/validation directories after checking their resolved paths, reparse points, running executable paths and registered installation paths. Before deletion, copied and hash-verified Git recovery bundles/patches, original source snapshots, profile backups, original portable artifacts, shelved updater work and compact verification evidence into the access-restricted **`D:\Fyltrix-Recovery`** directory. Approximately **27.5 GiB** of redundant file contents removed after allowing for the retained backups (logical sizes; physical free space may differ). Removed 23 stale pointer files targeting deleted roots. Historical evidence paths below are retired; retained reports and their source/destination mapping are in `D:\Fyltrix-Recovery\cleanup-20261101-223834\preserved.json`.

Verification: all 496 protected profile, signing-key/backup and active portable-file SHA-256 records match before/after. Git refs and the initially clean working tree were unchanged by cleanup; no commit/push/tag/merge was performed. The active `D:\Tools\Fyltrix` checkout, `FileLens-source.zip`, live data and signing stores remain in place. Filesystem check confirms no `Fyltrix-*` temporary directories remain in `D:\Tools`. Detailed inventory, copied-file verification, release snapshots and cleanup result: `D:\Fyltrix-Recovery\cleanup-20261101-223834`. No runtime code changed; the full application suite/build was not rerun for this maintenance operation, and Production Setup was not launched.

Files modified: `docs/PROJECT_PROGRESS.md`, `docs/IMPLEMENTATION_STATUS.md`, `docs/RELEASING.md`. Suggested owner commit: `docs(release): record published feed verification and workspace cleanup`.

**Next owner action:** review/install the published bootstrap setup when convenient and check Settings > Updates plus normal file-management behavior with the existing profile. Independent off-device key backup remains an owner responsibility. **Stop here; no further development stage started.**

## Release 0.1.6 uploaded-asset verification (2026-11-01)

Status: **All six draft assets verified; owner publication pending.** The owner uploaded the prepared manifest/signature. Read-only inspection confirms exactly the ZIP, Setup, their checksum sidecars, manifest and signature; no extra/missing assets. Downloaded the two uploaded files into a new subdirectory and confirmed byte-for-byte equality and matching GitHub SHA-256 digests/sizes against the previously shipped-verifier-tested local signature. Original package/checksum identities, sizes, hashes, timestamps and release notes are unchanged. The release is still a stable draft, not a live public update.

Evidence: the directory named in `D:\Tools\Fyltrix-release-validation-0.1.6-20261101-220400\uploaded-check-current.txt`, containing the current draft snapshot, downloaded metadata and `verification.json`. Existing provenance/default-pin/tamper verification remains applicable to these identical bytes. No signing, executable launch, runtime change or remote mutation was performed; no full-suite rerun is claimed for this read-only verification. Master has advanced to the owner's documentation commit `dad4a3d04dd6c185519869d623372df6fea89b56`; only the three verification documents differ from tagged source, and `v0.1.6` remains at `b4fa289063ab944fb872e6ff317f7c062af337b4`.

Files modified: this file, `docs/IMPLEMENTATION_STATUS.md`, `docs/RELEASING.md`. Suggested owner documentation commit: `docs(release): verify uploaded v0.1.6 assets`.

**Next owner action:** publish the existing draft as latest (do not replace assets or move tags). After the owner reports publication, verify the live public updater feed read-only, then perform the real bootstrap installation/application/settings review. The agent does not publish or run Production Setup. **Stop here.**

## Release 0.1.6 draft verification and local manifest signing (2026-11-01)

Status: **Verified and signed locally; owner upload/publication pending.** The owner committed/pushed preparation, ran manual validation and created/pushed annotated `v0.1.6`. Clean `master`/HEAD and local/remote peeled tag resolve to `b4fa289063ab944fb872e6ff317f7c062af337b4`; remote tag object is `c558f25dc568ee95cb0bf79fb32cce502e9ead3c`. [Tag workflow 41323312842](https://github.com/imamulkadir/Fyltrix/actions/runs/41323312842) succeeded for that exact source. No agent Git writes, workflow dispatches or release mutations. Evidence: `D:\Tools\Fyltrix-release-validation-0.1.6-20261101-220400` (pointer `D:\Tools\Fyltrix-release-validation-0.1.6-current.txt`).

- Downloaded the actual draft ZIP, Production Setup and two checksum sidecars. Names/states/sizes, strict checksum sidecars and GitHub asset SHA-256 digests match. `gh attestation verify` passed separately for both packages, restricted to this repository, `.github/workflows/release.yml`, `refs/tags/v0.1.6`, exact commit and non-self-hosted runners. These are the GitHub-built assets, not the earlier local preparation packages.
- ZIP SHA-256: `e90659c29aa6ca80a97848e980b1541494ec216de21f498735e719dff084dd01`; Setup SHA-256: `f7f1728ee768f0c0a9b199fae7bc7188f9b9c32f5f6e21cad38b91f2b7a31ce3`. Packaged launcher/payload/Setup file versions are `0.1.6.0`; ZIP pointer, current layout text, MIT contents and test-hook exclusion passed. Release notes match the tagged changelog. No executable/installer was run.
- Built the existing signing tool from clean exact tagged source into the external workspace (zero warnings/errors). Ran its guarded `sign-release` operation with the existing owner-local Fyltrix private/public key paths. It checks the production pin and matching P-256 private key. Generated only local `signed/Fyltrix-release.json` and `.sig`; no private key/password was printed, copied, regenerated, uploaded or changed. The owner-only completion helper was not invoked because it uploads assets.
- The actual `Fyltrix.Updates.dll`/Bootstrap DLL shipped in the provenance-verified ZIP authenticate the signature and both exact packages with their **default production pin**, without explicit test-key override. Modified manifest bytes return `InvalidSignature`; a same-size modified Setup copy returns `PackageMismatch`. Inspector output DLL hash matches the extracted shipped verifier. The public-key fingerprint remains `3665b71c7619dc19d986f94b7e46bd3b39618fb9c47bfa129254ae76fb955c0d`.
- This verification uses an explicitly labelled offline published-metadata preview, since GitHub draft metadata/URLs are not a published updater feed. It does **not** claim a live published update. The preview is not an upload asset. Final read-only checks confirm the original four draft assets/body remain unchanged and the release is still a draft. Both real profile files and all 488 portable files remain unchanged. Full tests were not rerun for this verification/documentation-only step; the actual tag workflow passed the existing test/build stages, and release preparation already recorded 630 local passes. No runtime source changed.

Signed upload assets (only these two files):

- `signed/Fyltrix-release.json`: 325 bytes, SHA-256 `9051aa0a1275771f178af9b753978cffab29d3dd9f19cb85524f855426736faa`.
- `signed/Fyltrix-release.json.sig`: 88 bytes, SHA-256 `9f69f0cbf5ca391be32055e8aefe6e5a73b0605200f2cc93867a1a027d2e17b2`.

Evidence includes draft snapshots, per-package provenance reports, package checksums, tool/signing logs, `shipped-verification.json`, negative-test evidence, preservation hashes and `verification.json`. Setup remains Windows Authenticode unsigned; updater signing does not change that. Independent off-device key backup and actual production bootstrap installation remain owner checks.

Files modified: this progress file, `docs/IMPLEMENTATION_STATUS.md`, `docs/RELEASING.md` (documentation only, after tagged-source signing). Suggested later owner documentation commit: `docs(release): record verified v0.1.6 draft`.

**Next owner action:** upload exactly the two local signed assets to the existing `v0.1.6` draft without clobbering anything, then request a read-only six-asset verification before publishing. Do not rerun the completion helper or replace packages/manifests; the prepared signature is already bound to the verified GitHub package bytes. Publication remains the owner's action. **Stop here.**

## Release 0.1.6 preparation (2026-11-01)

Status: **Completed locally; owner commit/push/validation/tag required before draft verification and signing.** Owner approved preparing the next release after the Stage 3 workflow turned green. Starting clean `master`, local HEAD and remote `master` match `e4968fc421683a8f9e445346497abc975aa321a8`; hosted [manual validation 41315863521](https://github.com/imamulkadir/Fyltrix/actions/runs/41315863521) succeeded for that exact commit. Read-only inspection confirmed no local/remote `v0.1.6` tag or release. Published `v0.1.5` and the older `v0.1.3` draft remain untouched. Evidence: `D:\Tools\Fyltrix-release-prep-0.1.6-20261101-204725` (pointer `D:\Tools\Fyltrix-release-prep-0.1.6-current.txt`).

- Moved completed, verified update features into `## [0.1.6] - 2026-11-01`, keeping Unreleased for future work. Existing `Set-ReleaseVersion.ps1` and `Test-Release.ps1 -Tag v0.1.6` passed; only the three application version fields changed to `0.1.6`/`0.1.6.0`.
- Corrected only the generated `LAYOUT.txt` description to reflect current update modes/defaults. Packaging behavior, installer identity, dependencies, signing pins and existing user data are unchanged. Static `automaticInstallationEnabled=false` build metadata continues to describe the unchanged Notify-only default, not removal of the opt-in capability.
- **441 application + 134 updater + 31 packaging + 14 release-tooling + 10 installer-builder tests passed: 630 total, no failures/skips.** Isolated self-contained ZIP/native launcher build and Production Inno compilation passed. Production Setup was never executed, and `publish.ps1 -Run`/`artifacts\Fyltrix` were not run/replaced. Existing nullable/high-DPI warnings remain recorded. No Git writes, tags, workflow dispatches, uploads or publication.
- Verified ZIP/installer checksum sidecars, matching `0.1.6.0` file versions on launcher/payload/Setup, expected ZIP pointer, corrected packaged notes, MIT text and exclusion of test hooks. Setup remains Authenticode `NotSigned`, consistent with the agreed signing model; updater-manifest signing is separate.
- Real packaged native root launcher → WPF payload startup, preview, normal close and custom preference persistence passed on a private desktop in a separate Sandbox-marked copy. Test profile, browser/thumbnail/archive caches and clipboard were isolated using existing probe plus an external test-only cache hook. Neither hook is in the ZIP/installer. No registry installation, Production Setup or live-user configuration changes.
- Final hashes match **both real profile files and all 488 portable files**. Release source changes are version metadata, notes and packaging-description text only; prior Stage 3 behavior/performance/lifecycle evidence remains applicable. No extra performance benchmark or production installation is claimed for release preparation.
- Owner must commit/push/tag before a new attested draft exists. Draft verification/local manifest signing can proceed only after that owner-created tag workflow completes; the completion helper uploads assets even without `-Publish`, so the agent must not invoke it.

Local validation artifacts (not upload/release assets): ZIP SHA-256 `b5c7f0ba62938e219561a3e986e9a8d12b7feba517fb962d93ce888e310cef09`; Setup SHA-256 `cc4a42cccc2db8bc0fafe2a3a9f7eff5734015bb2167581777f420347a83ce8c`. `verification.json`, TRX/logs, version metadata, `smoke/verification.json` and preservation hashes are retained. These local builds have no tagged GitHub provenance and must not be used as the attested draft assets. Signing-key paths were checked for existence only; private key/password contents were not read and no manifest was signed.

Files modified: `CHANGELOG.md`, `src/Fyltrix/Fyltrix.csproj`, `tools/Publish-Layout.ps1`, `docs/PROJECT_PROGRESS.md`, `docs/IMPLEMENTATION_STATUS.md`, `docs/RELEASING.md`. No runtime behavior, workflow, dependency or trust changes. Suggested owner commit: `chore(release): prepare v0.1.6`.

**Next owner actions:** review/commit/push these six files, run Release manually on `master` for the prepared commit, and wait for green. Then the owner creates/pushes annotated `v0.1.6`. Its tag-triggered workflow creates the attested draft; only then can the agent verify actual assets and sign a manifest locally. Upload/publication remain owner actions. Off-device key backup and real bootstrap installation review remain separate owner checks. **Stop here; no Git actions or tag created.**

## Notification plan — Stage 3: optional installation on safe exit (2026-11-01)

Status: **Completed locally; stopped for owner review and hosted validation.** Owner pushed Stage 2 and explicitly requested continuation; interruption left the tree clean. Starting `master`: `0f9dde13e756f5f7824b1ad5a10e345a8acdcf51` (owner's automatic-download commit), still unchanged. No Git writes/remote mutations. Evidence: `D:\Tools\Fyltrix-exit-stage3-20261101-193328` (pointer `D:\Tools\Fyltrix-exit-stage3-current.txt`).

- Enabled the existing opt-in Install on exit preference; Notify only remains the default. Reused authenticated downloads, normal-close vetoes, work/sharing/automation safeguards and the verified worker/trial/rollback pipeline. No new setting key or dependency.
- Interception runs only after ordinary Closing handlers accept; safety and preference are rechecked before handover. No forced update close, installation on availability/startup, automatic restart after exit installation, or exit installation after Windows SessionEnding suppression. Keeping shares in the tray and later background exit skip installation. A failed preparation retains the app/download and allows the next accepted close without retrying; previous handover/result evidence prevents automatic repeats. Normal manual Install and restart remains confirmed and restarts as before.
- Added an additive `--no-restart` worker flag/overload while preserving old APIs/arguments and manual notification behavior. Exit mode avoids modal failure notices after close, retaining result/journal evidence. Malformed/duplicate flags fail before normal GUI startup. Next-start status distinguishes an already-installed cached package from previous-attempt evidence without deleting packages or resetting preferences.

### Stage 3 verification

- **441 application, 134 updater, 31 packaging, 14 release-tooling and 10 installer-builder tests passed: 630 total, zero final failures/skips.** Latest application report: `complete-app.trx`; backend/packaging/Python reports: `final-*`. Earlier full runs (437 then 439 application tests) also passed as coverage was extended. First compile caught an unqualified test-hook `App` reference; corrected it before 49 focused passes. Existing nullable/high-DPI warnings remain recorded. Final current/candidate self-contained application and probe builds passed. Source version stays `0.1.5`; test candidate version overrides are external outputs only.
- **Five native lifecycle cases plus two post-update starts passed** (`native-verified/verification.json`). On private desktops and isolated Sandbox-marked layouts with spaces/Unicode paths: first verified actual manual update/restart, then safe exit commit without restart, failed-health rollback, late Closing cancellation and helper-start failure. All preserve custom preferences. Cancelled/start-failure cases retain the old version without close approval; health failure follows an approved close and rolls back code. Follow-up starts verify successful-update cache status and failed-attempt review/no automatic repeat. The native test uses a disposable in-memory signing key through explicit Sandbox-only test trust; it never reads/rotates/pins the production private key or publishes a package. Native hooks are not shipped. No Production Setup, registry installation or real Windows logoff/shutdown was performed; the SessionEnding wiring/suppression path is unit-tested.
- Reviewed the offscreen Settings screenshot with the enabled mode and explanatory text. UI coverage retains a single tab confirmation, normal/late vetoes, no-ready/other-mode closing, tracked work/tools/automation, sharing/tray behavior, duplicate close protection, failure fallback, opt-in persistence and unchanged defaults. Existing file-management tests remain green.
- **Five fresh baseline and five updated full resource benchmarks passed**, using the exact committed before probe, initialized default scheduler, synthetic caches/data, no public HTTP, no concurrent tests/builds and no runtime caps. Median startup: 1330.3 to 1324.5 ms; idle private memory: 88.2 to 88.0 MB; managed heap: 8.9 to 8.9 MB; idle CPU: 7.8 to 4.68 ms/s; after-work idle median stays zero. Navigation: 130.1 to 138.4 ms; 20k listing: 280.1 to 278.5 ms; search: 240.3 to 267.4 ms. All recorded timing ranges overlap; higher navigation/search medians are retained, not hidden. Host variability remains, and no speedup or zero-overhead guarantee is claimed. The later cache-status-only correction is verified by the final suite and native follow-ups; that restore branch is not exercised by this default no-ready-package benchmark.
- Preservation audit: **both real profile files and all 488 existing portable files unchanged**. No change to `publish.ps1`, project/dependency versions, central defaults/settings model, production pins/key, installer identity or application version. Existing development/installed copies were not replaced. Results, hashes, benchmarks and unchanged `master`/HEAD are consolidated in `final-verification.json`; full logs and initial diagnostic reports are retained.

### Stage 3 files

- Created `src/Fyltrix/UI/MainWindow.InstallOnExit.cs`; `tests/Fyltrix.Tests/InstallOnExitTests.cs`; `tests/Fyltrix.PlacementProbe/ExitUpdateStartupProbe.cs`; `tests/installer/verify_install_on_exit.py`.
- Modified `src/Fyltrix/App.cs`; `Core/ManualUpdateSession.cs`, `Core/UpdateNotifications.cs`; `UI/MainWindow.cs`, `UI/MainWindow.Updates.cs`, `UI/MainWindow.UpdateNotifications.cs`, `UI/SettingsWindow.Updates.cs`, `UI/UpdateWindow.cs`; `src/Fyltrix.Updates/UpdateApplier.cs`, `UpdateWorker.cs`.
- Modified `tests/Fyltrix.Tests/AutomaticDownloadTests.cs`, `UpdateNotificationTests.cs`; `tests/Fyltrix.Updates.Tests/ApplyTests.cs`, `HandoverTests.cs`; `tests/Fyltrix.PlacementProbe/InstallerStartupHook.cs`, `SandboxUpdateStartupProbe.cs`.
- Documentation: `README.md`, `CHANGELOG.md`, `docs/MANUAL_UPDATES.md`, `docs/APPLY_AND_RECOVERY.md`, `docs/IMPLEMENTATION_STATUS.md`, `docs/RELEASING.md`, this progress file. External evidence helpers/layouts are not repository/release assets.

Suggested commit: `feat(updater): install verified updates on safe exit`.

All three notification-plan implementation stages are locally complete. **Stop for owner review/commit/push and manual Release workflow validation on `master`, then separate approval for release preparation and production rollout. Do not create a tag yet.** Existing production-installation review and independent off-device signing backup remain owner steps. No version bump, tag or publication is authorized by this stage.

## Notification plan — Stage 2: optional download and manual installation (2026-11-01)

Status: **Completed locally; stopped for owner review and Stage 3 approval.** Owner reported Stage 1 hosted validation green and explicitly approved Stage 2. Starting clean `master`: `00d4806d7d776d8b4d7f92c6fa43206b935793ed` (owner's notification/preferences commit), still unchanged. No Git writes or remote mutations. Evidence: `D:\Tools\Fyltrix-download-stage2-20261101-180819` (pointer `D:\Tools\Fyltrix-download-stage2-current.txt`).

- Reused the existing authenticated downloader, staging, manual approval and safe handover. Notify only remains the default. Enabled only Automatic download/manual installation; install-on-exit remains disabled for Stage 3. No new dependency, key, application-version, installer or publish-workflow change.
- Added progress/cancellation and verified-ready status. Opting out/closing cancels automatic work; changing preferences does not cancel a manually requested download, and saving the same mode does not restart/cancel work. Failure does not retry in a tight loop; explicit manual retry remains available. Unsupported/development copies cannot download in place and leave installed-copy ready metadata intact.
- Persisted only an updater-owned stage leaf name, reauthenticating/hashing the saved package off the UI thread before offering it after restart, including Manual only. Invalid/tampered/obsolete data cannot enable installation. Ready downloads survive checks and normal restarts; ready-notice dismissal persists. Background work never saves/migrates/resets application preferences, prepares handover or requests shutdown.
- **414 application + 133 updater + 31 packaging + 14 release-tooling + 10 installer-builder tests passed: 602 total, zero final failures/skips.** Current final application report: `verified-app.trx` (414 passed); earlier full passing runs and failed diagnostic logs are retained. Self-contained application/probe builds passed with existing warnings. Reviewed offscreen progress and ready screenshots. UI coverage declines installation confirmation and verifies no handover or close occurs.
- **Five final native startup scenarios passed** in `native-verified/verification.json`: Notify only (one check, zero downloads), Manual only (zero checks/downloads), automatic verified download (one check/download), authenticated saved-package restore (no network/redownload) and tampered-package refusal. All run on private desktops in an external Sandbox-marked layout using the shipped default production pin and an offline fixture. Preview, normal exit, custom preferences and unchanged version pointer are asserted; no automatic installer/worker/handover or popup. No Production Setup or actual installation was performed in this stage.
- Diagnostic history: two early focused runs failed the same completion-state assertion; publishing the automatic-download completion task before invoking a synchronous/asynchronous provider fixed that race, followed by 47 focused passes and full-suite passes. A later code review caught development copies clearing an installed copy's cache reference; added a regression test and fixed the guard before final verification. One repeated native restore run timed out waiting for preview readiness. Notification tests now isolate each new private desktop's browser cache and include startup diagnostics; the final five-scenario rerun passed. Production preview/file-management code is unchanged, and the timeout is not claimed as a proven production updater defect.
- Preservation audit passed: **both personal profile files and all 488 existing portable files match the pre-change hashes**. Defaults/settings model, dependencies/project files, signing pins/key, source version, installer/backend worker and `publish.ps1` remain unchanged. No production install or deployed-application replacement. Source changes require a later owner-reviewed bootstrap release; published `v0.1.5` remains unchanged.

### Stage 2 performance and verification evidence

- **Five fresh baseline and five updated full resource benchmarks passed** on private desktops with the initialized default Notify-only scheduler, isolated synthetic data/cache and no public HTTP. No concurrent tests/builds or runtime heap caps during measurement. Median idle private memory: 88.4 to 89.6 MB; managed heap: 8.9 to 8.9 MB; idle CPU: 4.68 to 4.68 ms/s; after-work idle median remains zero. Navigation: 128.1 to 129.4 ms; 20k listing: 266.0 to 267.7 ms; search: 249.5 to 226.9 ms. Memory/navigation/listing ranges overlap. These are observations, not a zero-overhead or speedup claim.
- The first grouped benchmark showed a startup median increase, 1279.8 to 1540.6 ms, mostly before its window construction (`startupToBenchMainMs`: 455.3 to 694.8 ms). Constructor median was 320.9 to 313.9 ms; startup measured after fixture/setup was 827.3 to 845.8 ms. Investigated rather than ignoring that result: **three additional alternating full-benchmark pairs passed**, with variable pre-benchmark delays and similar warm constructor times. Original reports remain in `performance.json` and `paired-*.json`.
- **Actual App.Main startup comparison passed:** identical framework-dependent runtime/configuration, preserved exact-source before/after probe outputs copied to separate Sandbox-marked layouts, private desktops, fresh synthetic profiles/browser caches, Notify-only scheduling enabled through matching offline test hooks (both use a two-second test startup delay). One warm-up per build plus five measured starts per build, alternating order; real folder/preview readiness and normal close checked. Median **1424.7 to 1247.9 ms**, ranges **1203.1–1745.3** versus **1156.5–1602.9 ms**. The grouped benchmark delay was not reproduced as a consistent application-startup regression; host/timing variation remains. This is not a speedup guarantee. Evidence: `actual-startup/verification.json` and samples/logs. No production startup/preview code was changed to tune measurements.
- `final-verification.json` consolidates the executed 602 tests, native scenarios, measurements, protected hashes and unchanged `master`/HEAD. `preservation-final.json`, TRX/logs, native reports, screenshots and documentation-link/whitespace checks are retained. Actual production installation/update review and independent off-device signing-key backup remain owner steps. Optional cache-write failure/interruption may require redownload; completed staging directories are not automatically pruned. Interrupted update transactions retain the existing explicit recovery procedure.

### Stage 2 files

- Created `tests/Fyltrix.Tests/AutomaticDownloadTests.cs` (signed ZIP fixture and 18 isolated cases).
- Modified `src/Fyltrix/Core/ManualUpdateSession.cs`, `UpdateNotificationStore.cs`, `UpdateNotifications.cs`; `src/Fyltrix/UI/MainWindow.Palette.cs`, `MainWindow.UpdateNotifications.cs`, `SettingsWindow.cs`, `SettingsWindow.Updates.cs`, `UpdateWindow.cs`.
- Modified `tests/Fyltrix.Tests/UpdateNotificationTests.cs`; `tests/Fyltrix.PlacementProbe/NotificationStartupProbe.cs`, `InstallerStartupHook.cs`; `tests/installer/verify_notifications.py`.
- Documentation: `README.md`, `CHANGELOG.md`, `docs/MANUAL_UPDATES.md`, `docs/IMPLEMENTATION_STATUS.md`, `docs/RELEASING.md`, this file.

Suggested commit: `feat(updater): add optional automatic downloads with manual installation`.

Next owner steps: review/commit/push these changes yourself, then run the Release workflow manually on `master`. Do not create a tag yet. **Stage 3 — optional installation on a safe normal exit — requires separate approval. Stop here.**

## Notification plan — Stage 1: preferences, scheduling and in-app notices (2026-11-01)

Status: **Completed locally; stopped for owner review and next-stage approval.** The owner approved the simple-English plan, then requested continuation after interruption. Only this first stage was implemented, stopping before automatic download/installation. Starting clean `master`: `f1d191bc1145d612856f80c78d25079af5ad5f7b` (owner's manual-update integration commit). No Git writes or remote mutations.

- Approved new/missing update preference defaults to Notify only; Manual only remains selectable. Added Settings → Updates with current version, cached last check/verified available version, status, Check now and manual details/download entry. Future automatic-download/install choices are disabled; imported future values are preserved but safely act as notify-only. Existing toolbar/theme/tab/favourites preferences are retained; malformed new update values do not reset other preferences.
- One cancellable startup delay (60 seconds), then checks at six-hour intervals while the app is running; no service, startup registration or package download/install. Session/clock/delay boundaries are reused/injected in tests. Checks avoid active/prepared downloads and respect server backoff, including after restart. Public manifest authentication precedes notices; release notes remain unsigned plain text.
- Nonmodal dismissible banner, deduplicated per version with updater-only atomic metadata under `StagingArea.DefaultRoot/notification-state.json`. Background checks do not call `AppSettings.Save`. Corrupt/unrelated state files are not overwritten. Manual update controls and safe shutdown remain available. HTTP clients are lazy; trial starts and native test hooks do not schedule public checks. Fullscreen hides/restores the banner without changing file operations.
- First focused run: **28 passed**. Initial full application run: **395 passed, one failed** because a legacy toolbar test still expected eight Settings pages rather than the new ninth Updates page; updated that count while retaining its dialog/preview assertions. Earlier compilation caught and fixed the `Application.MainWindow` name collision. Host memory pressure prevented one PowerShell start; no user process was stopped. Builds/tests use isolated child-process GC limits/build-server settings where needed, without changing application defaults.

Evidence: `D:\Tools\Fyltrix-notifications-stage1-20261101-162426` (pointer `D:\Tools\Fyltrix-notifications-stage1-current.txt`). Final logs, TRX files, screenshots, benchmark reports and preservation audit are retained; `final-verification.json` consolidates executed results, protected hashes and the unchanged `master`/HEAD check. Documentation links and `git diff --check` pass. No production install, version bump, commit/push/tag/release action. Next planned stage after owner review: automatic download with manual installation; automatic installation on normal exit remains a later approval/test gate.

### Final notification-stage verification

- **397 application, 133 updater, 31 packaging, 14 release-tooling and 10 installer-builder tests passed: 585 total, zero final failures/skips.** Final application evidence is `complete-app.trx`; other final suites are `final-*` reports/logs. Debug/self-contained builds passed. Existing build warnings are recorded, not hidden. Future-mode controls, immediate Manual-only saving, preference round trips, invalid-value isolation, cancellation/backoff/restart, deduplication, UI layout and zero automatic downloads are covered.
- **Two native real-startup scenarios passed** (`native-notifications-verified/verification.json`): Notify only checks once, Manual only checks zero times; both download zero packages. Actual App.Main startup, offline production-pin verification, banner/dismissal, no popup, preview, normal close and preferences are exercised on a private desktop in an external Sandbox-marked layout. No Production Setup was run. The first native test harness failed because a test snapshot writer raced a reader; the writer now retries transient sharing failures. Only its exact-path-verified orphaned test payload PID was stopped; no existing user application process was touched. The final native run passed.
- **Five fresh benchmark runs passed**, compared with five reports from the immediately preceding committed manual-update runtime (the reused pre-change baseline, recorded in `benchmark-baseline.txt`). Median first listing: 1377.8 to 1285.7 ms; navigation: 133.9 to 127.3 ms; 20k listing: 288.7 to 281.8 ms; search: 247.9 to 256.9 ms. Timing ranges overlap. Idle private memory: 88.3 to 89.8 MB; managed heap: 8.6 to 8.9 MB; idle CPU: 1.56 to 3.12 ms/s with quantized/noisy samples, after-work idle median remains zero. After-preview-hidden private memory: 138.8 to 149.0 MB, with overlapping ranges. Host load differs; this is a regression observation with small measured memory overhead, not proof of speedup or zero overhead. Tests exercise an initialized scheduler without network traffic or disabling normal preview functionality.
- Preservation audit (`preservation-final.json`): **both personal profile files and all 488 existing portable files unchanged**. No dependency/project-file, `publish.ps1`, package/installer identity, executable trust pin, signing key or source-version change. The published `v0.1.5` remains unchanged and needs a later reviewed bootstrap release to gain this UI. Existing production installation review and independent off-device key backup remain owner steps.

### Notification-stage files

- Created: `src/Fyltrix/Core/UpdateModeConverter.cs`, `UpdateNotificationStore.cs`, `UpdateNotifications.cs`; `src/Fyltrix/UI/MainWindow.UpdateNotifications.cs`, `SettingsWindow.Updates.cs`; `tests/Fyltrix.Tests/UpdateNotificationTests.cs`; `tests/Fyltrix.PlacementProbe/NotificationStartupProbe.cs`; `tests/installer/verify_notifications.py`.
- Modified application files: `src/Fyltrix/App.cs`; `Core/AppSettings.cs`, `ApplicationDefaults.cs`, `ManualUpdateSession.cs`; `UI/MainWindow.cs`, `MainWindow.Media.cs`, `MainWindow.Palette.cs`, `SettingsWindow.cs`, `UpdateWindow.cs`.
- Modified tests/probes: `tests/Fyltrix.Tests/ManualUpdateTests.cs`, `SettingsCustomizationTests.cs`, `TestSetup.cs`, `ToolbarBehaviorTests.cs`; `tests/Fyltrix.PlacementProbe/Bench.cs`, `InstallerStartupHook.cs`.
- Documentation: `README.md`, `CHANGELOG.md`, `docs/MANUAL_UPDATES.md`, `docs/IMPLEMENTATION_STATUS.md`, `docs/RELEASING.md`, this progress file.

Suggested commit: `feat(updater): add update notifications and preferences`. **Stop here for owner review.** No automatic download/install stage has started.

## Stage 7B — Manual in-app update integration (2026-11-01)

Status: **Completed locally; stopped for owner review/CI/bootstrap rollout. Explicitly approved after v0.1.5 publication.** Working on `master`. The baseline source export records owner documentation checkpoint `713531e6b842e7426f77acd1421396d6b38f076a`; runtime source is unchanged from the published `31ee0f7c5c5b17f3a6e495771efdf1c07dffa326` baseline. Preserved the three pre-existing uncommitted release-verification documentation edits in an external snapshot before implementation. Existing development/installed copies and personal settings are protected; no Git writes or automatic installation.

- Reviewed and reused the 13 hash-verified deferred integration files preserved during the original release-preparation interruption. Added user-initiated update checking to Tools and the command palette, a lazy session/dialog, authenticated releases with plain-text notes, download progress/cancellation, installer fallback for unsupported/development copies, install confirmation and existing work-preserving close/handover/apply/health/restart behavior. No startup network check, scheduler, default settings change or new dependency. Release-note metadata itself is unsigned; no HTML rendering or signature claim is made for those notes.
- Added a session-level install-preparation guard so checks/downloads cannot overlap preparation, retained normal close cancellation, and added optional worker failure notification only after the exact successful-close token exists. Existing three-argument worker invocation remains supported. Test startup hooks remain test-only and Sandbox-gated.
- Early verification (retained diagnostic history): first ten session/dialog tests passed; eleven passed after cancellation/late-completion and layout coverage were added. Five baseline resource benchmarks passed on private desktops using a read-only Git source export, separate from the working copy. Benchmarks now isolate thumbnail caches as well as settings/browser data. A missing namespace in that test-only cache adjustment was corrected before baseline success. One concurrent build could not initialize the CLR under host memory pressure; sequential retry succeeded. Initial restored source timestamps caused an incremental stale-library compile; refreshed restored timestamps and rebuilt successfully. No verification failure is counted as a pass.
- Final self-contained/NativeAOT ZIPs and Sandbox/Production installer compilation passed. The first archived `0.1.0`/`0.1.1` fixtures could not start with the current probe compiled against `0.1.5`; retained the failed logs, added Setup diagnostics and derived versions safely in the driver. Rebuilt matched `0.1.5`/`0.1.6` fixtures. All four real update-window cases passed; strengthened restart coverage then passed all four again with an actual worker-launched restart. Confirmed authenticated download without auto-install, approved normal close, atomic commit, failed-health rollback, close cancellation, worker-start refusal, preview readiness, custom preferences and Sandbox uninstall cleanup. All windows were on private desktops; the shipped default production pin was used. Test hooks are not shipped. Source version remains `0.1.5`; all synthetic packages/manifests stay external and must never be published.

Evidence root: `D:\Tools\Fyltrix-stage7B-20261101-150654` (pointer: `D:\Tools\Fyltrix-stage7B-current.txt`). Baseline/final hashes, prior docs, failed diagnostic runs, TRX/logs, both five-run benchmark sets, `performance-comparison.json`, `preservation-final.json` and `restart-manual-integration/verification.json` are retained. No production installation/manual preference check is claimed; Sandbox evidence is separate from owner rollout. No tag, upload, publication, commit or push by the agent.

### Final Stage 7B verification and files

- **377 application, 133 updater, 31 packaging, 14 release-tooling and 10 installer-builder tests passed: 565 total, zero failures/skips in final runs.** Both installer modes compiled; only Sandbox was executed. Four final installed cases passed with the shipped production pin and offline feed: real worker-launched restart/preview/preferences, cancelled close, helper-start failure and failed-health rollback. Confirmed Sandbox registration/owned-file cleanup. No test hook or private-key material is packaged.
- **Five before/five after benchmarks passed.** Median first listing: 1475.9 → 1377.8 ms; idle private memory: 88.5 → 88.3 MB; navigation: 153.6 → 133.9 ms; 20k-file listing: 358.7 → 288.7 ms; search: 281.7 → 247.9 ms. Idle CPU median: 6.23 → 1.56 ms/s; after-work idle median stays 0 (one updated sample was 3.12 ms/s). No median regression observed. The first baseline includes cold fixture creation and host load varied; these are regression observations, not a speedup claim.
- Hash audit: personal profile files and **all 488 existing portable files unchanged**. No settings/default/schema/dependency/publish-script/installer-identity/firewall changes, source-version bump or signing-key rotation. Existing three-argument worker calls and the original one-parameter public launch API remain supported. Documentation/whitespace checks pass. Production rollout and off-device signing backup remain owner checks.
- Application/backend files: new `src/Fyltrix/Core/ManualUpdateSession.cs`, `UI/MainWindow.Updates.cs`, `UI/UpdateWindow.cs`; modified `src/Fyltrix/App.cs`, `UI/MainWindow.cs`, `UI/MainWindow.Palette.cs`, `UI/MainWindow.Tools.cs`, `src/Fyltrix.Updates/UpdateWorker.cs`.
- Test/probe files: new `tests/Fyltrix.Tests/ManualUpdateTests.cs`, `tests/Fyltrix.PlacementProbe/ManualUpdateStartupProbe.cs`, `tests/installer/verify_manual_update.py`; modified `tests/Fyltrix.PlacementProbe/Bench.cs`, `InstallerStartupHook.cs`, `SandboxUpdateStartupProbe.cs`, `tests/Fyltrix.Updates.Tests/HandoverTests.cs`.
- Documentation: `README.md`, `CHANGELOG.md`, `docs/MANUAL_UPDATES.md`, `docs/IMPLEMENTATION_STATUS.md`, `docs/RELEASING.md`, this progress file. The previous release-verification edits are preserved, not discarded.

Suggested commit: `feat(updater): add manual in-app update workflow`. Next: owner review/commit/push, manual GitHub validation, then explicit approval for a future bootstrap release. Do not change existing release assets or enable fully automatic installation. Interrupted transactions still use the existing documented explicit recovery flow. Stop here for owner review.

## v0.1.5 draft verification and local manifest signing (2026-11-01)

Status: **Owner published v0.1.5; live discovery/manifest authentication verified. Actual installation review pending. Stage 7B not started.** Owner approved proceeding with release validation. GitHub tag [run 41288980422](https://github.com/imamulkadir/Fyltrix/actions/runs/41288980422) succeeded. Clean `master`, local tag and live annotated remote tag resolved to `31ee0f7c5c5b17f3a6e495771efdf1c07dffa326` at validation start; the draft initially contained exactly ZIP/Setup and two checksum sidecars. No Git writes or remote mutations were performed by the agent.

- Downloaded the actual draft packages into `D:\Tools\Fyltrix-release-validation-0.1.5-20261101`. Both sizes, checksum sidecars and GitHub SHA-256 asset digests match. `gh attestation verify` passed for both, restricted to this repository, `.github/workflows/release.yml`, `refs/tags/v0.1.5`, exact commit, and non-self-hosted runners. Provenance reports are retained beside the packages. ZIP SHA-256: `0e1da1599e0c8c79b0c6d395f4cc70d1774792b08447bb1449a80b97fb660098`; Setup SHA-256: `efa1f772ed82ecbb6dcda5b4b5ecd32554def39ee57d6c431379b87616bdb8a3`.
- Built the existing standalone signing tool from the exact tagged source (zero errors) into that external workspace. Ran its pinned-key `sign-release` mode using the existing protected Fyltrix key paths. Created `signed/Fyltrix-release.json` and `.sig` locally; no key/password was printed, copied into packages or uploaded. The draft was rechecked unchanged after signing.
- Loaded the actual shipped `Fyltrix.Updates.dll`/`Fyltrix.Bootstrap.dll` from the provenance-verified ZIP into an isolated verification helper. The shipped default production pin authenticates the local manifest and both exact packages, without an explicit test-key override. Modified manifest bytes and a same-size modified Setup copy were rejected. Metadata for this check is explicitly a local preview of the expected published release; the draft is still unpublished and no live update availability is claimed.
- Archive pointer is `0.1.5` with no previous payload. Root launcher and application file versions are `0.1.5.0`. Packaged MIT text matches source after normalizing checkout line endings. A first raw byte comparison exposed only CRLF/LF differences; the normalized content check passed. Production Setup remains Authenticode `NotSigned`, as agreed under DyanamicEye's signing model. No EXE, installer, profile migration, application update or real desktop interaction was executed.
- Verification evidence: `verification.json`, both provenance reports, `draft-before.json`, checksums, signing-tool build log and external inspection helper. No runtime source changed, so the full suites were not repeated for this verification-only stage; the successful hosted tag build supplied those tests. No backup off this PC is claimed.
- After the owner reported publication, read-only GitHub inspection confirmed stable `v0.1.5` is published with all six expected package/checksum/manifest/signature assets. The actual CLI live `check 0.1.4` returned `Available`, version `0.1.5`, manifest verification `None` (the success enum), `installed:false`; live `check 0.1.5` returned `UpToDate`. Publication and public-feed authentication are now verified; this does not prove a production installation or in-app update. No download/install/update command was executed for these live checks.

Files modified: this file, `docs/RELEASING.md`, `docs/IMPLEMENTATION_STATUS.md` (status/documentation only). Existing `publish.ps1 -Run`, portable installation, user preferences, signing keys and Git tags/releases are unchanged. Suggested documentation commit: `docs(release): record verified v0.1.5 draft`.

Next owner actions: manually verify the newly downloaded `v0.1.5` installer/application, MIT agreement before directory selection, startup and preserved preferences. Windows warnings remain expected for this unsigned release. The agent must not run Production Setup. Stage 7B in-app manual update integration remains deferred until explicit approval after this release gate.

## Release preparation — 0.1.5 (2026-11-01)

Status: **Prepared and locally verified; owner commit/push and hosted validation of the additional process-test fix pending. No agent Git writes.** Read-only inspection confirms manual GitHub [run 41287003031](https://github.com/imamulkadir/Fyltrix/actions/runs/41287003031) succeeded on `master` at `94deb091d112f41ae67a2bf34c7750ca0c5a3bb6`, the owner's settings-test isolation fix. Starting working tree was clean. This supersedes the previous repair section's pending hosted-validation gate.

- Retained `Unreleased` and all earlier version sections. Added `0.1.5` dated `2026-11-01` with the verified settings-test fix and MIT agreement/package changes carried forward from the failed `v0.1.4` release attempt.
- Ran `Set-ReleaseVersion.ps1 -Version 0.1.5` and `Test-Release.ps1 -Tag v0.1.5`; validation passed. Application version is `0.1.5`, file/assembly versions `0.1.5.0`. Runtime behavior, dependencies, user data, keys, development publishing and the owner-local manifest-signing policy are unchanged. EXEs remain unsigned as in DyanamicEye; Windows warnings are not resolved by this release.
- The first local verification passed all 365 application tests but caught a separate updater test-process collision: 131 updater tests passed and one failed with a null `Process.MainModule` in the Sandbox apply guard while another class's handover probes were exiting. The failure report is retained, not hidden by retries. `ApplyTests` and `HandoverTests` now share the nonparallel `Updater processes` collection, because those tests inspect application process names system-wide. No updater runtime or assertion was changed.
- Final verification: **365 application, 132 updater, 31 packaging, 14 release-tooling and 10 installer-builder tests passed (552 cases, no failures/skips in final runs)**. The entire updater suite passed **five consecutive post-fix runs (660 executions)**. Release version/notes and Git whitespace checks pass. Application compilation passed with existing warnings; no installer was executed or portable output overwritten. No performance benchmark was repeated because runtime behavior is unchanged. The earlier GitHub run does not certify the newly added process-test collection; run manual validation after pushing this source.

Files modified: `CHANGELOG.md`, `src/Fyltrix/Fyltrix.csproj`, `tests/Fyltrix.Updates.Tests/ApplyTests.cs`, `HandoverTests.cs`, new `ProcessCollection.cs`, `docs/IMPLEMENTATION_STATUS.md`, this file. Evidence: `D:\Tools\Fyltrix-release-0.1.5-20261101-140910`. Next: owner review/commit/push, manual Release validation on `master`, then creation/push of annotated `v0.1.5` if green. Existing tags/releases remain unchanged; no agent commit, push, tag, remote dispatch or publication. Suggested commit: `chore(release): prepare v0.1.5`.

## v0.1.4 hosted failure — shared settings test race (2026-11-01)

Status: **Locally completed and verified; owner commit/push and hosted validation pending. No agent Git writes.** [Run 41285499553](https://github.com/imamulkadir/Fyltrix/actions/runs/41285499553), tag `v0.1.4` at `fe070b980440489e2bb2787c919fd301f32956bb`, failed `FolderViewStorageTests.Default_applies_only_to_folders_without_their_own_view_and_everything_persists`: saved view size expected 160, loaded 0. Hosted result: 364 passed, one failed, zero skipped; subsequent packaging/attestation/draft steps were skipped. This failure is unrelated to the MIT agreement or signing decision.

- Root cause: `FolderViewStorageTests` and `TemplateTests` both mutate static `AppSettings.Folder` but were outside the existing nonparallel `UI` collection. A focused local pre-fix run reproduced the collision on its first run: the template persistence assertion looked for `templates.json` under the restored baseline rather than its temporary directory. That baseline was still the personal profile; the file was absent and personal `settings.json` retained its prior last-write timestamp. No production installer was run.
- Added the existing `[Collection("UI")]` to both classes. No assertions/skips/retries/timeouts changed; unrelated test classes retain parallel execution. Added a unique temporary default settings directory in `TestSetup` and cleanup of that exact test-owned directory on process exit, preventing baseline restoration from ever reaching the personal profile.
- Files: `tests/Fyltrix.Tests/FolderViewTests.cs`, `TemplateTests.cs`, `TestSetup.cs`, `CHANGELOG.md`, `docs/IMPLEMENTATION_STATUS.md`, this file. No application runtime/dependency, version, settings, signing key, release workflow or `publish.ps1` changes. Evidence: `D:\Tools\Fyltrix-ci-0.1.4-race-20261101-134416`.
- Verification completed: **20 focused runs of 21 cases (420 executions)** passed; full Release suites passed **365 application, 132 updater, 31 packaging, 14 release-tooling and 10 installer-builder tests (552 total, no failures/skips)**. Version/notes validation and Git whitespace checks pass. Personal settings hash before/after verification matches, personal templates remain absent, and HEAD/tag remain at the owner's original commit. No performance benchmark was run because runtime code is unchanged; no packaging/installer rebuild was needed for these test-only changes. Hosted success is not claimed until the owner runs the fixed source on GitHub.

Next: owner reviews/commits/pushes, then runs manual validation on `master`. The existing tag remains unchanged and cannot pick up a fix on rerun; after green validation, prepare `0.1.5` and a new owner-created tag. Suggested commit: `fix(tests): isolate shared settings state`.

## Signing decision — follow DyanamicEye (2026-11-01)

Status: **Reference audit completed; existing signing model retained.** After requesting self-signed EXE signing, the owner directed the implementation to follow DyanamicEye's logic. Inspected `D:\Tools\DynamicEye\.github\workflows\release.yml`, `tools/Publish-Layout.ps1`, `tools/Complete-Release.ps1`, `packaging/installer/DynamicIsland.iss`, and ADRs 0034/0038. DyanamicEye builds unsigned EXEs, attests the packages, creates a draft, then has the owner sign the release manifest locally. ADR 0038 explicitly excludes Authenticode from that pipeline; its private signing key never goes to GitHub.

Fyltrix already follows that signing separation. No self-signed Windows certificate, EXE-signing implementation, repository secrets or trust-store changes were created. Update-manifest signing remains owner-local with the existing dedicated Fyltrix key; Windows publisher/SmartScreen warnings remain possible. This does not add the still-deferred application update UI.

Files modified: this progress file only. Verification: reference/source inspection, `Test-Release.ps1 -Tag v0.1.4` and Git whitespace checks; no runtime changes, so no new build/test-suite run. The prior v0.1.4 preparation passed all 552 tests. Starting clean `master`: `5043475` (owner's version-preparation commit); no agent Git writes. Next: owner review/commit/push this decision record, then owner-created `v0.1.4` tag to build the MIT-agreement release. Suggested commit: `docs(release): record DyanamicEye signing policy`.

## Release preparation — 0.1.4 (2026-11-01)

Status: **Prepared and locally verified; owner Git operations and next release pending. No agent Git writes.** Owner reported green manual validation; read-only GitHub inspection confirms workflow run [41284179540](https://github.com/imamulkadir/Fyltrix/actions/runs/41284179540) succeeded on `master` at `7e08ba133c2a4a3ea4c72d5ceed0ca1050a96fd7`, containing the MIT agreement changes. Starting working tree was clean.

- Retained `Unreleased` and moved the completed MIT agreement/package notes into `0.1.4`, dated `2026-11-01`.
- Ran `Set-ReleaseVersion.ps1 -Version 0.1.4` and `Test-Release.ps1 -Tag v0.1.4`; version/notes validation passed. Application version is `0.1.4`, file/assembly versions `0.1.4.0`. No runtime behavior, dependency, data path, signing key or development publish script changed.
- Authenticode remains unconfigured: this release does not resolve the Windows unsigned-app warning. Trusted publisher signing is separate from the existing update-manifest key. No certificate, security bypass or Windows policy change is introduced.
- Final Release verification passed: **365 application, 132 updater, 31 packaging, 14 release-tooling and 10 installer-builder tests (552 total, no failures/skips)**. Release version/notes and Git whitespace checks pass. No installer was executed or existing portable output overwritten for this version-only preparation. Performance measurements were not repeated because no runtime behavior changed.

Files: `CHANGELOG.md`, `src/Fyltrix/Fyltrix.csproj`, this progress file. Evidence: `D:\Tools\Fyltrix-release-0.1.4-verification`. Next: owner review/commit/push; leave the annotated `v0.1.4` tag uncreated if trusted Windows publisher signing is wanted before this release. Suggested commit: `chore(release): prepare v0.1.4`. No commit, push, tag, workflow dispatch or publication is performed by the agent.

## Installer agreement and Windows warning review (2026-11-01)

Status: **Locally completed and verified. Owner review/commit/push and a subsequent rebuilt release remain. No agent Git writes.** The owner reported Windows warning before Setup and no agreement before directory selection. The owner selected **MIT like DyanamicEye** after inspection found no existing Fyltrix application licence. Starting clean `master`: `e126199` (owner's `0.1.3` preparation). GitHub `v0.1.3` is currently a draft with ZIP/Setup/checksums; existing assets are not modified by this task.

- Added root `LICENSE` with the standard MIT terms and Fyltrix contributor copyright. Added the licence as `LICENSE.txt` in application build/publish outputs through the existing project; `publish.ps1` is unchanged.
- Added Inno `LicenseFile` pointing at the exact versioned payload's copied terms. Native interactive Setup now shows the agreement first, disables Next until acceptance, then proceeds to directory selection. Used native Inno behavior rather than a duplicate/custom agreement implementation. Silent-mode behavior and existing installer IDs, paths, settings/data and uninstall rules are preserved.
- Added `tests/installer/verify_license.py`: checks Sandbox identity/hash, runs the actual Setup wizard on its own private desktop, verifies MIT text/acceptance gating/page order, cancels, and confirms no installation/registry entry was created. No real desktop input, clipboard access or Production installation.
- Executed checks: Release application build passed (existing warnings only); build output, published payload and archived `LICENSE.txt` exactly match the root licence; fresh self-contained/NativeAOT ZIP layout, Sandbox installer and Production installer compile passed. Native wizard test **passed all agreement/gating/order/cancel checks**. Production Setup was compiled/inspected only and reports Authenticode **NotSigned**. Regression suites: **365 application, 132 updater, 31 packaging, 10 installer-builder and 14 release-tooling tests passed (552 total, no failures/skips)**. Git whitespace checks pass. Local HEAD and origin/master remain `e12619974202af2b8c7fe76857999b4ea48d5c4a`; only the listed source/docs/test files changed. No runtime source, `publish.ps1`, existing portable install, user settings, installer identities or data locations were modified. Performance benchmarking was not run: the application runtime is unchanged; this change adds a packaged text file and an installer page.
- The Windows warning is separate from the agreement. The existing P-256 key authenticates update manifests and is not an Authenticode publisher certificate. No certificate was purchased/configured, warning bypass added or Windows protection disabled. Valid Authenticode signing can establish publisher identity, but fresh signed binaries may still show SmartScreen reputation warnings; see [Microsoft](https://learn.microsoft.com/windows/apps/package-and-deploy/smartscreen-reputation).

Files: new `LICENSE`, `tests/installer/verify_license.py`; modified `packaging/installer/Fyltrix.iss`, `src/Fyltrix/Fyltrix.csproj`, `README.md`, `CHANGELOG.md`, `docs/INSTALLER.md`, `docs/IMPLEMENTATION_STATUS.md`, and this file. Source version stays `0.1.3`; the already downloaded/released installer is unchanged. Evidence: `D:\Tools\Fyltrix-installer-license-20261101-130204`.

Next: the owner reviews/commits/pushes and runs manual GitHub validation before preparing a future rebuilt release. Trusted Authenticode signing is a separate owner decision; it is not configured by this task. The agent does not commit, push, tag or publish. Suggested commit: `feat(installer): show MIT agreement before directory selection`.

## Second hosted release run — PowerShell child environment (2026-11-01)

Status: **Local fixes verified; owner commit/push and manual hosted validation pending. No agent Git writes.** Run `41279303260` for `v0.1.2` at `28b2f8c6e7bcbf7ac15dd49ffa2e4cc654814d41` passed **365 application, 132 updater and 31 packaging tests on GitHub**. The earlier WebP/DPI repairs therefore passed their hosted checks. The job then failed eight of nine Python installer guard tests because child Windows PowerShell could not find `Get-FileHash`; packaging, provenance and draft creation were again skipped.

Root cause: GitHub runs the parent step in PowerShell 7. Python inherits its `PSModulePath` and passes Core module paths to Windows PowerShell 5.1. Microsoft documents this exact intermediate-process failure and prescribes removing `PSModulePath` from the child environment: [about_PSModulePath](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_psmodulepath?view=powershell-7.5#starting-windows-powershell-from-powershell-7).

### Changes and executed checks

- `tests/installer/test_builder.py`: select the explicit Windows PowerShell executable and remove only `PSModulePath` (case-insensitively) from each child environment. All tamper/path/ownership checks remain active. Added a real child-process regression confirming a poisoned parent module path is absent in the child, `Get-FileHash` resolves, and the installer reaches its intended rejection.
- `tests/release/test_tools.py`: apply the same isolation to all Windows PowerShell child calls and add an inherited-path regression. Preserve the parent's environment and all other child variables; no registry/system configuration changes.
- `.github/workflows/release.yml`: add a manual `workflow_dispatch` validation path restricted to `master`. Derive/validate the current source version, run the existing tests/package compilation, skip provenance and draft creation on manual runs, and retain available reports/build files as seven-day workflow artifacts. Tag-triggered release behavior and signing policy are preserved. The new upload-artifact action is pinned to the reviewed official full commit SHA.
- Updated `CHANGELOG.md` under **Unreleased** and `docs/RELEASING.md`. Application version stays `0.1.2`; no new tag is needed to run manual validation after the owner pushes the changes. Existing tags are not moved or deleted.
- Local results: **10 installer guard tests and 14 release-tooling tests passed**, including new environment regressions. `actionlint 1.7.12` and whitespace checks passed. No app/runtime source or dependency changed; the .NET suite results above are from the actual hosted `v0.1.2` run, not a claimed rerun after these shell-only edits.

Evidence: `D:\Tools\Fyltrix-ci-shell-20261101-121647` (failed logs, Python test logs and `verification.json`). Hosted validation of these new edits has **not** run because the owner must commit/push them. No commit, push, tag, GitHub run dispatch, release creation/publication or production installation was performed by the agent.

Next: owner reviews/commits/pushes these six files (`.github/workflows/release.yml`, both Python test files, `CHANGELOG.md`, `docs/RELEASING.md`, this file), then runs **Actions → Release → Run workflow → master**. Check that run before deciding on the next release version/tag. Suggested commit: `fix(ci): isolate Windows PowerShell child environments`.

## First hosted release run — runner-dependent test failures (2026-11-01)

Status: **Fix verified locally; prepared for `v0.1.2` tagging after the authorized master push.** The owner pushed `v0.1.1`; hosted run `41277778348` compiled the application and passed tag/version/changelog validation, then failed five application tests (360 passed). Packaging, provenance and draft creation were correctly skipped, so no installer/release assets were produced. The owner explicitly approved preparing/pushing `v0.1.2` to `master` while leaving its tag to them and preserving `v0.1.1`.

Before the latest standing instruction above arrived, commit `bda0028553a29222db02e9ea2a65de79aee0bdc7` (`fix(ci): make release tests portable for v0.1.2`) had already been pushed to `master`; local/origin/live master matched and `v0.1.1` was unchanged. The owner then explicitly reserved **all future commits and pushes** to themselves. This rule clarification is a local, uncommitted documentation edit; do not commit or push it automatically, and do not undo/rewrite the existing commit without instructions.

- Four tests required a Windows WIC WebP decoder not available on the hosted Windows Server runner (`0x88982F8B`). WebP output is already encoded with the existing SkiaSharp dependency. The test-only fixture now decodes those outputs with Skia, checking the actual WebP format, dimensions, colors and exact lossless pixels; other formats continue through Windows codecs. Batch orientation, editor save/crop dimensions and original-file preservation assertions remain. No test is skipped or removed, and application decoding behavior/dependencies are unchanged.
- The toolbar test compared client-space coordinates to a requested 1300-pixel window width, although Windows constrained the runner's actual window/work area. It now compares to the actual content width in the same coordinate space, preserving the right-edge alignment and button-order checks at both requested sizes.
- Modified tests: `ApplicationDefaultsTests.cs`, `MediaFixtures.cs`, `MediaTests.cs`, `MediaUiTests.cs`, `ConversionTests.cs` under `tests/Fyltrix.Tests`. Focused Release verification: **26 passed**. Full application suite with the fixes: **365 passed, 0 failed, 0 skipped**. No application implementation or dependency changed.
- Added `CHANGELOG.md` section `0.1.2` dated `2026-11-01`, retaining `Unreleased` and the `0.1.1` history. Set the application Version to `0.1.2` and FileVersion/AssemblyVersion to `0.1.2.0` through the existing helper; `Test-Release.ps1 -Tag v0.1.2` passed. Final Release checks at `0.1.2`: **365 application, 132 update, 31 packaging, 13 release-tooling and nine installer-builder tests passed**. Test counts are preserved; no skips or gate bypasses were added. Reviewed changes are limited to five test files, the application version properties, changelog and this report.
- `v0.1.1` remains untouched: annotated tag object `66f2d5f229a8460fab4848aba8eb884316e97f0d`, commit `b0f267ca3ad72a9b739d97b52470756205e41476`. Re-running it would use the old test source. No tag, release or remote setting is changed by this repair; the new tag remains the owner's step.

Evidence: `D:\Tools\Fyltrix-ci-failure-20261101-114814`, including downloaded failed-run logs, local test results and final `verification.json`/`push-verification.json`. Commit message: `fix(ci): make release tests portable for v0.1.2`. Hosted success is not claimed; it requires the owner's new tag-triggered run containing the fixes.

## Release preparation — 0.1.1 (2026-11-01)

Status: **Prepared and verified for `v0.1.1` tagging.** The owner clarified that only the six listed changelog/version/Git preparation steps were intended, and authorized the agent to perform steps **1–5**, leaving tag creation/push (step 6) to the owner. This release-preparation changeset is authorized for commit/push to `master`; final remote commit/push confirmation is recorded externally and in the task response. Starting `master`/live remote: `e3b7602c060e157e17593ce5fedcfcf38ef5cae5`.

- Preserved the mistakenly started application-update integration externally, with all 13 changed/new source files, hashes and a binary tracked patch under `D:\Tools\Fyltrix-release-prep-20261101-111650\deferred-integration`. Restored only those agent-owned edits to the committed verified baseline. No existing work was discarded. This release does **not** include the unfinished in-app update UI; Stage 7B remains deferred.
- Kept `## [Unreleased]`; moved the existing verified release/signing/packaging/backend notes into `## [0.1.1] - 2026-11-01`. Notes explicitly retain the limitation that the in-app update action and automatic installation are not enabled.
- Ran `tools/Set-ReleaseVersion.ps1 -Version 0.1.1`: application Version is `0.1.1`, FileVersion and AssemblyVersion are `0.1.1.0`. Shared component identities, application functionality, signing keys, settings and existing portable files are unchanged.
- Ran `tools/Test-Release.ps1 -Tag v0.1.1` successfully. Final Release checks passed: **365 application, 132 update, 31 packaging, 13 release-tooling and nine installer-builder tests**, with no failures/skips in the final runs. Existing compiler warnings remain.
- The first application run exposed a branding test hard-coded to `0.1.0.0`. Updated that existing assertion to require agreement between assembly/file/informational version metadata, then reran the full suite successfully. Branding, protocol, firewall and icon assertions remain intact; no application functionality was changed.
- Reviewed changes are limited to `CHANGELOG.md`, `src/Fyltrix/Fyltrix.csproj`, `tests/Fyltrix.Tests/RebrandingTests.cs`, and this progress file. Whitespace/release checks pass, the archived integration hashes match, and no secret files are included. Packaging/hosted release execution is left to the owner's tag-triggered workflow; no new installer was run for this version-only preparation.
- No tag, GitHub release or production installation is created by this task. First hosted release-build/signing/publication validation remains after the owner's tag action.

Evidence: `D:\Tools\Fyltrix-release-prep-20261101-111650` (`verification.json`, test logs/TRX, deferred integration archive; final remote outcome in `push-verification.json`). Commit message: `chore(release): prepare v0.1.1`. Next action belongs to the owner: create and push `v0.1.1` (step 6). The unfinished integration remains outside this release.

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

## Stage 7A.1 — Dedicated signing trust and encrypted backup

Status: **Completed locally — stopped for Stage 7B approval (2026-11-01)**. The owner confirmed the tooling was pushed and requested key creation, public-key configuration, remaining integration and first release validation. Per the stage-by-stage agreement, this substage handles signing setup only and stops before Stage 7B application integration. The owner selected `D:\Fyltrix-Key-Backups` for the encrypted backup. Starting clean local/origin/live `master`: `4a00e5381d70a085fcfec832df6e335913ada358`. No Git writes or remote mutations.

### Completed tasks

- Added owner-only .NET 8 key setup and a PowerShell wrapper. Generate a new ECDSA P-256 key outside every checkout/application copy/legacy profile, refuse existing/overlapping/link destinations, apply protected current-account/SYSTEM ACLs before secret writes, create files exclusively and flush them. Retain partial outputs for review instead of deleting a key.
- Created the dedicated production key in `%LocalAppData%\Fyltrix-Signing`, with the owner's selected encrypted backup in `D:\Fyltrix-Key-Backups`. The backup uses password-encrypted PKCS#8 (AES-256-CBC, PBKDF2-SHA256, 600,000 iterations) and a random 256-bit recovery password held separately in the protected primary directory. The password and private key were never printed, placed in Git, copied into build outputs or supplied to GitHub.
- Verified actual backup decryption, matching public key, random sign/verify challenge and file permissions. This is a verified **local** backup; no independent/off-device copy or password-manager storage is claimed. Owner instructions are in `docs/SIGNING_SETUP.md`.
- Pinned only its public SPKI in `src/Fyltrix.Updates/ReleaseMetadata.cs`. SHA-256 fingerprint: `3665b71c7619dc19d986f94b7e46bd3b39618fb9c47bfa129254ae76fb955c0d`. No reference/test key reuse. Existing binaries are not replaced; they need a tested manual bootstrap to acquire the new trust. Automatic checks/download/install remain disabled and no application settings/defaults were changed.
- Kept no-trust tests meaningful by explicitly passing an empty trust set; the configured default now rejects unrelated signatures instead of pretending to have no pins. Added key-setup permission/restore/overwrite/path tests and production-public-key validity/unrelated-signer rejection. Existing offline signing remains compatible.

### Files created or modified

- Created `tools/Fyltrix.UpdateTool/SigningKeySetup.cs`, `tools/New-ReleaseSigningKey.ps1`, `docs/SIGNING_SETUP.md`.
- Modified `src/Fyltrix.Updates/ReleaseMetadata.cs`, `tools/Fyltrix.UpdateTool/Program.cs`.
- Modified `tests/Fyltrix.Updates.Tests/ToolTests.cs`, `ReleaseTests.cs`, `ClientTests.cs`, `DownloadTests.cs`.
- Updated `README.md`, `CHANGELOG.md`, `docs/UPDATES.md`, `MANUAL_UPDATES.md`, `APPLY_AND_RECOVERY.md`, `RELEASING.md`, `IMPLEMENTATION_STATUS.md`, and this file to distinguish configured source trust from older unpinned binaries and pending rollout.

### Executed verification

- Owner-tool build passed. Four focused CLI tests passed. Final Release suites: **132 update tests, 365 application tests and 31 packaging tests passed**, with zero failures/skips. A first update run found one remaining test assuming global empty pins; changed it to an explicit empty-trust fixture, preserving its no-network/no-staging assertion, then reran the full suite successfully. Windows permission checks in the new tests are explicitly guarded; no new analyzer warning remains in the final update build.
- Real production key generation/backup restore and permissions passed. Public metadata only: `signing-setup-public.json` in the external evidence root. No secret contents are in evidence reports.
- Fresh source-pinned self-contained/NativeAOT layout build and Production installer compilation passed; Setup was never executed. Used the actual production private key locally to sign the fresh ZIP/installer. A separate offline probe referencing the **actual packaged** update library accepted the signature using its default source pin with no test-key override, verified both packages, rejected changed manifest bytes, and checked the exact public fingerprint. Evidence: `payload-trust-verification.json`. Signed test artifacts stay external/unpublished.
- Actual native launcher and newly pinned WPF payload startup passed on a private desktop with synthetic profile/cache/clipboard: preview ready, normal close, exit zero. Only a separate Sandbox-marked test copy was launched; production outputs and the current development/portable copy were not installed or replaced. Evidence: `bootstrap-smoke.json`.
- Final Release totals: **132 update tests, 365 application tests, 31 packaging tests, 13 release-tooling tests, nine installer-builder tests**, all passed. Final preservation audit: personal roaming profile and all **488 existing portable files** unchanged; application/UI/launcher/installer/publish workflow sources unchanged; only intended release-policy/tool/test/docs changes. No secret-key/password files or PEM secret contents in Git changes. Documentation links and whitespace checks pass; `master`/origin remain at the starting commit. Evidence: `verification.json`.
- Not performed: independent/off-device backup, production installation, manual bootstrap rollout, in-app update integration, hosted Actions, live authenticated GitHub release or publication. No user desktop input, real clipboard, Recycle Bin or arbitrary process termination was used.

Evidence root: `D:\Tools\Fyltrix-signing-setup-20261101-013611`. Existing `publish.ps1 -Run`, file-management/UI behavior, user data and previous portable binaries are protected. Current application version stays `0.1.0`; no tag, commit, push, merge, release creation/publication or production installation is performed.

Known owner action: copy the encrypted backup to independent offline storage and store its recovery password separately; a local backup cannot establish recovery after loss of this PC. The agent does not request secrets in chat. Next stage after signing verification and explicit approval: Stage 7B manual application-update UI/workflow integration and regression. Tagging/first hosted release validation stays later, after the required tests and owner approval.

Suggested commit message: `feat(updater): configure dedicated release signing trust`.

## Stage 7A — Tag/changelog release automation

Status: **Completed locally — stopped for owner validation/next-stage approval (2026-11-01)**. The owner requested mostly automated releases matching DyanamicEye, authorizing this release-tooling substage. Starting checkout: clean `master` at `4d40c41` (owner's Stage 6 commit). Application-update UI integration remains a separate Stage 7B gate; it is not being implemented here. Hosted release execution is explicitly unverified.

### Implemented tasks

- Inspected DyanamicEye's actual `release.yml`, `CHANGELOG.md`, `Get-ReleaseNotes.ps1`, `Complete-Release.ps1` and ADR 0038. Reused its owner-pushed-tag → CI draft → local signing/publication model with Fyltrix's own package names, .NET 8 projects, installer builder and existing signing CLI. No reference application/key files were copied into Fyltrix.
- Added stable `vX.Y.Z` tag-triggered Windows release automation. Validate tag/master ancestry/all three application version fields and one nonempty matching changelog section before tests/builds. Run Release application/update/packaging tests, installer guards and release-tooling tests; package versioned ZIP and compile Production Inno Setup without running it. Attest exact package provenance, then create a draft with changelog notes/checksums. Full-SHA-pinned actions; scoped permissions; no release private-key secret or automatic publication.
- Added read-only release-note/tag validation and an explicit local version-preparation helper. Version preparation requires notes, refuses non-newer/inconsistent/ambiguous versions, atomically updates only the three application version fields and performs no Git writes. The actual application version remains `0.1.0`; no tag was created.
- Added owner-run completion: require clean `master` at the exact existing local/remote tag, validate stable draft assets, download/check hashes and repository/workflow/tag/commit-bound GitHub provenance, use the standalone CLI to sign with an application-pinned key, upload without overwriting, and publish only with explicit `-Publish`. Retain its private diagnostic workspace; never copy/upload the key or execute Setup. Existing/partial manifests require review rather than clobbering.
- Added `sign-release` CLI mode requiring pinned trust. Existing offline/test `sign` stays compatible. Production pins remain empty, so production signing refuses until the owner reviews a dedicated key and ships a tested bootstrap build.
- Added changelog, owner-facing release instructions and tests. Kept application behavior, data, `publish.ps1 -Run` and previous stages unchanged. No Git/remote/release mutation was performed.

### Files created or modified

- Created `.github/workflows/release.yml`, `CHANGELOG.md`.
- Created `tools/Get-ReleaseNotes.ps1`, `Test-Release.ps1`, `Set-ReleaseVersion.ps1`, `Complete-Release.ps1`.
- Modified only the existing standalone signing tool: `tools/Fyltrix.UpdateTool/Program.cs`; added its unpinned-production-key refusal test in `tests/Fyltrix.Updates.Tests/ToolTests.cs`.
- Created `tests/release/test_tools.py`, `tests/release/.gitignore`.
- Created `docs/RELEASING.md`; updated `README.md`, `docs/UPDATES.md`, `docs/IMPLEMENTATION_STATUS.md`, and this file.

### Executed verification and limitations

- **13 release-tooling tests passed**, including positive note extraction/version preparation, duplicate/missing/empty/oversized notes, headings inside fences, invalid/overflow tags, version mismatches/ambiguity, downgrade refusal, paths with spaces/Unicode, default paths from another working directory and completion refusing invalid tags before external operations.
- **129 Release update tests**, **31 Release packaging tests**, and **nine installer builder guards passed**. Existing signing/verification interoperability still passes; the new production mode refuses an unpinned key without writing output or changing inputs.
- `actionlint 1.7.12` passed the workflow. Downloaded into external evidence and checked against its upstream SHA-256 checksum; no application/test dependency added. Verified the three pinned official action revisions and Windows runner prerequisites against their primary sources.
- Initial script verification caught native Windows PowerShell default-path handling and null-string File.Replace binding; corrected both and reran all 13 release tests successfully. An initial full Release application run had one existing thumbnail keyboard-selection assertion fail (364 passed); the focused thumbnail suite then passed 4/4 without source/test changes. Final full Release rerun: **365 passed, 0 failed, 0 skipped**. Existing application code and thumbnail tests were not changed; occasional UI-test timing failures remain a first-hosted-run risk to investigate rather than bypass.
- Final isolated self-contained application/NativeAOT launcher/ZIP build passed; Production Inno installer compiled successfully and was **not executed**. This exercises the workflow's packaging commands locally without modifying the current portable output. Existing warnings remain; no claim of a warning-free build or a new interactive launch is made for this tooling-only stage.
- Final preservation audit passed: all **488 existing portable files**, personal roaming-profile hashes, all application/installer sources, `publish.ps1` and the production trust source are unchanged. Local `master`/origin heads remain at `4d40c41ccb22ba5dfd2a70863a8461e0c9444bcb`. Workflow lint, whitespace checks and documentation links pass. Evidence: `verification.json` in the evidence root below.

Evidence: `D:\Tools\Fyltrix-release-automation-20261101-011007`. No tag/push/draft/upload/publication or production-key configuration was executed. Hosted Actions, actual attestation verification and owner signing/upload/publication require an owner-approved first tagged run and remain unverified. Do not claim the complete application-update feature or Stage 7B is finished. Fully automatic installation remains disabled.

Suggested commit message: `feat(release): automate tag-built drafts with changelog notes`.

Next action: stop. Owner commits/pushes tooling and approves/performs the first tagged-run/key-bootstrap validation. Stage 7B manual application-update workflow integration/final regression requires explicit approval. No later implementation starts automatically.

## Stage 6 — Apply, isolated health, rollback and recovery

Status: **Completed — stopped for Stage 7 approval (2026-11-01)**, authorized by the owner's "proceed" after asking how many stages remained. Starting clean `master`/origin/live master: `4f29ed2d4b636b371c09bc683ded745da6ba7de5`. No commit, push, merge, tag, branch or release operation. Stage 7 has not started.

### Completed tasks

- Added protocol-1 capability and cooperative startup exclusion. Normal root launcher and direct payload startup hold shared leases; apply/installer/uninstaller take exclusive access. Pending journals block normal startup. Older layouts retain normal launch compatibility and require explicit manual bootstrap installation before managed updates.
- Reused the old self-contained application host for explicit apply/recovery modes before WPF/profile initialization; no separate runtime, service, scheduled task or NuGet dependency. The `Fyltrix.Bootstrap` class library shares the existing `Fyltrix.Launcher` layout namespace/API without an executable-reference publishing conflict. Component assembly identities remain stable while application/file/product versions are stamped per release.
- Added independently verified ZIP apply after the existing approved-close/process-wait boundary. Retain old code and stable launcher, refuse active processes/existing targets/unsafe paths/no-space, extract a new payload, and record an owner-bound exact path/hash inventory.
- Hold read-only live-profile leases; create separate snapshot/trial copies and isolated caches. Strict copied-settings loading fails instead of resetting defaults. Trial disables clipboard access/automation, uses an offscreen window and current-user PID/nonce/version-bound named pipe, waits for initial listing, and must close normally with exit zero. Only the worker-owned isolated trial may be terminated on failure.
- Recheck original profile, processes, code and exact pointer before atomic switching. Flush a transaction journal, retain old version as previous, finish committed metadata, and restart through the root launcher. Precommit failure restores exact code selection without writing live data. Failed trials do not automatically reopen potentially unvalidated live settings.
- Added explicit authenticated interruption recovery: precommit rollback or postcommit validation/metadata completion; unknown/modified state retains the journal. No guessing, forced user-app shutdown, data restoration or automatic staging/payload pruning.
- Updated Inno startup coordination, interrupted-transaction refusal, committed-version downgrade guard and uninstall inventory cleanup. Updater-added files are deleted only if recorded paths/hashes still match; modified/unknown files remain. Sandbox/Production identities and existing profile/cache storage remain unchanged.
- Added unit/integration tests, private-desktop installed Sandbox lifecycle harness and maintenance documentation. Production pins are empty and automatic installation remains disabled; no end-user update UI is wired yet.

### Files created or modified

- Shared bootstrap: `src/Fyltrix.Bootstrap/Fyltrix.Bootstrap.csproj`; `src/Fyltrix.Launcher/UpdateProtocol.cs`, `TrialConnection.cs`, `Program.cs`, `Fyltrix.Launcher.csproj`. Existing `ReleaseLayout.cs` source/API is reused unchanged.
- Apply library: `src/Fyltrix.Updates/ProfileSnapshot.cs`, `UpdateApplier.cs`, `UpdateRecovery.cs`, `UpdateTransaction.cs`, `UpdateWorker.cs`; existing `UpdateHandover.cs`, `PackageVerifier.cs`, `Fyltrix.Updates.csproj`.
- Application: `src/Fyltrix/App.cs`, `Fyltrix.csproj`, `Core/AppSettings.cs`, `Core/Archives/ArchiveBrowser.cs`, `UI/FolderView.cs`. Changes are restricted to worker/trial startup, strict copied-profile loading, isolated archive cache and listing readiness; file-management algorithms/defaults are unchanged.
- Packaging: `tools/Publish-Layout.ps1`, `tools/Build-Installer.ps1`, `packaging/installer/Fyltrix.iss`, `INSTALLATION.txt`.
- Tests: `tests/Fyltrix.Updates.Tests/ApplyTests.cs`, `HandoverTests.cs`; `tests/Fyltrix.HandoverProbe/TestKeys.cs`, `TrialStub.cs`, `Program.cs`, project file; `tests/Fyltrix.Packaging.Tests/StartupGateTests.cs`; `tests/Fyltrix.Tests/UpdateProfileTests.cs`; `tests/Fyltrix.PlacementProbe/SandboxUpdateStartupProbe.cs`, `InstallerStartupHook.cs`; `tests/installer/verify_apply.py`.
- Documentation: new `docs/APPLY_AND_RECOVERY.md`; updated `README.md`, `docs/IMPLEMENTATION_STATUS.md`, `INSTALLER.md`, `MANUAL_UPDATES.md`, `RELEASE_LAYOUT.md`, `UPDATES.md`, and this file.

### Executed verification

- Before application changes: x64 development build and private-desktop development/legacy-payload startup passed. Captured personal roaming-profile, existing portable-release and tracked-source hashes.
- Final application suite: **365 passed, 0 failed, 0 skipped**. Final update suite: **128 passed, 0 failed, 0 skipped**. Packaging suite: **31 passed, 0 failed, 0 skipped**. Installer builder guards: **9 passed**. Tests cover trial crash/timeout/wrong nonce, copied-profile mutation, locked/changed live data, busy gate/existing target, precommit failures, interrupted commit/switch recovery, changed committed candidate and startup exclusion.
- An earlier full application run failed two existing tests: thumbnail readiness timeout and reading an external robocopy output while still locked. Focused rerun **17/17** and final full **365/365** passed without changing those tests or application algorithms. The earlier run overlapped packaging; no performance-causality claim is made.
- Final x64 development build: passed with two existing warnings. NativeAOT/self-contained `0.1.0` and `0.1.1` release-layout builds: passed in fresh external output roots. Both Sandbox installers and the Production installer compiled; Production Setup was never executed. Post-change actual development and untouched legacy payloads launched with preview ready and exited zero on private desktops.
- Final installed apply harness: **four scenarios passed** using real native/WPF payloads, disposable local signing trust and synthetic settings. Verified successful commit; strict incompatible-trial-settings rollback; abrupt worker exit during pointer switch and after commit; journal-blocked direct startup; explicit signed recovery; actual restart on expected version with custom preferences/preview; live-profile byte preservation; unconfigured production-worker refusal while an app remains running; numeric downgrade refusal; uninstall of owned initial/updater payloads while retaining modified owned and unknown files. Tests are non-elevated and never execute Production Setup or change production trust.
- Final manual Inno lifecycle regression: **17 cases passed**, including protected/unowned/redirected targets, ownership/schema checks, locked pointer/running-app refusal, upgrade retention, actual startup/preferences/preview, downgrade refusal and uninstall cleanup. Sandbox registration/shortcut were removed and unknown files retained.
- Final preservation audit passed: personal roaming-profile files and all **488 existing portable-release files** match pre-stage hashes; every original tracked file remains; `publish.ps1` and production trust source are byte-identical. `master` HEAD/origin remain at the starting commit, whitespace checks and current documentation links pass. Evidence: `stage6-verification.json`. No user process, real clipboard, Recycle Bin, desktop input or production installer was used by tests.

### Corrections made during verification

- Initial executable-to-executable project references caused duplicate publish apphosts/runtimeconfig files (`NETSDK1152`); shared code now resides in a small class library. Stable component AssemblyVersion fixed an installed probe binding mismatch; fresh builds and actual launches passed.
- Fixed Inno API argument counts and compiler issues before successful compilation. Added safe repeated preparation/uninstall authorization cleanup guards.
- The first uninstall assertion raced Inno's temporary uninstaller: its bootstrap can exit before owned-file cleanup finishes. Inspection confirmed cleanup completed; the harness now waits for the inventory/payload cleanup, and subsequent four-scenario runs passed. Failed/intermediate packages and evidence remain outside the checkout rather than being overwritten.

### Evidence, risks and next stage

Evidence root: `D:\Tools\Fyltrix-stage6-20261031-234537-c4e7f8f6`. Final source builds are `packages-verified`; installed tests use `installer-acceptance-0.1.0`, `installer-verified-0.1.1`, `sandbox-acceptance` and `installer-lifecycle-final`. Production compilation is under `installer-production-compile-only`. TRX/logs are retained under `tests` and the evidence root. Earlier successful lifecycle evidence is also retained under `sandbox-lifecycle` and `sandbox-final`; incomplete attempts are not release artifacts.

Live data is never restored/overwritten by the worker. Snapshots/staging can contain private profile data and must not be published. Old/failed payloads and evidence are retained; partial interrupted extraction can require manual review. The launcher is retained during managed update; protocol upgrades need manual installer bootstrap. Registry display-version refresh is best effort. Strict settings validation deliberately rejects unknown fields instead of silently dropping them. Startup health does not certify every codec/integration or physical power loss.

Not performed: Production installation, owner-key/live-public-release authentication, Authenticode/SmartScreen, interactive wizard, a separate Windows account, physical power-loss testing, or a fresh five-run performance comparison. Existing portable output and `publish.ps1 -Run` are protected rather than rebuilt in place. Broader workflow/performance regression and the explicit user update action belong to Stage 7; fully automatic installation requires separate approval after all gates pass.

Suggested commit message: `feat(updater): add isolated health checks and recoverable update apply`.

Next stage, only after explicit approval: Stage 7, end-to-end manual workflow integration/regression and optional automation proposal. The owner handles all Git/release operations. Do not resume implementation automatically.

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
5. Manual staging and safe shutdown/handover — completed on `master`; approved for Stage 6 by the owner's "proceed" instruction.
6. Out-of-process apply, isolated health validation, rollback/recovery and saved-data preservation — completed on `master`; stopped for explicit Stage 7 approval. See the Stage 6 report above.
7. Release automation, signing setup and manual in-app workflow/regression are locally complete. The owner published `v0.1.5`; its public feed authenticates. Stage 7B is later source work, locally verified with 565 regression cases, four installed Sandbox cases and five-run before/after measurements. Owner review/CI, a new bootstrap release and actual production installation/update checks remain; independent off-device key backup is unconfirmed. Fully automatic installation stays disabled and needs separate approval.

Next action: stop after Stage 7B. Owner reviews/commits/pushes the new feature, runs manual Release validation on `master`, and approves a later version/changelog/bootstrap release. Existing published `v0.1.5` does not contain this UI; do not move its tag or overwrite its assets. The agent must never commit/push/tag/publish or enable fully automatic installation. Independent off-device signing backup and actual production rollout remain owner-controlled.
