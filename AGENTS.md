# AGENTS.md — Fyltrix Development Instructions

## 1. Project Overview

**Project:** Fyltrix  

**Tagline:** Your Files. Your Rules.  

**Repository:** https://github.com/imamulkadir/Fyltrix  

**Primary branch:** `master`  

**Framework:** C# / .NET 8 / WPF  

**Target:** Windows 10 and Windows 11, x64  

**License:** MIT

Fyltrix is a native Windows file manager featuring multi-tab navigation, advanced file operations, rich previews, comparison tools, search, automation, and customizable workspace controls.

The application uses programmatic WPF UI construction rather than XAML.

### Primary Engineering Goals

1. Native Windows behavior and consistency.

2. Fast, responsive file navigation.

3. Reliable file operations with strong data safety.

4. Minimal resource consumption.

5. Consistent UI and keyboard interactions.

6. Backward compatibility with existing user settings.

7. Safe installation, updates, and rollback.

8. Clean, maintainable, testable code.

**Do not sacrifice existing functionality to introduce new features.**

---

## 2. Mandatory Repository Inspection

Before implementing any task:

1. Read this file completely.

2. Read `docs/PROJECT_PROGRESS.md`.

3. Read `docs/IMPLEMENTATION_STATUS.md`.

4. Inspect the relevant application code and tests.

5. Check the current Git status and branch.

6. Identify whether the requested behavior is already partially implemented.

7. Create a focused implementation and verification plan.

Do not trust historical status notes without checking more recent records.

The project evolves frequently. Current source code and recent verified progress records take precedence over outdated descriptions.

---

## 3. Current Repository Status

As of November 3, 2026:

\- The latest verified published release is `v0.1.8`.

\- The repository's primary branch is `master`.

\- Portable and installer distributions are supported.

\- Versioned application payloads and a stable launcher are implemented.

\- Update verification, installation handover, and rollback infrastructure exist.

\- Manual and configurable automatic update functionality is available.

\- Windows taskbar identity fixes have been implemented.

\- The latest release CI reported 640 passing automated tests.

\- Real application screenshots are included in the README.

\- Production taskbar behavior and representative installed-app workflows still require owner acceptance.

These details are a historical snapshot, not permanent constants.

For future tasks, verify the current release, source, tests, and unresolved work before reporting project status.

---

## 4. Git and Repository Rules

**The repository uses `master` exclusively.**

Strict requirements:

\- Do not create feature branches.

\- Do not create development or release branches.

\- Do not create temporary branches.

\- Do not create pull requests automatically.

\- Do not commit changes automatically.

\- Do not push to GitHub automatically.

\- Do not merge branches automatically.

\- Do not create or move tags.

\- Do not publish GitHub Releases.

\- Do not modify repository settings.

The project owner performs all Git mutations manually.

Allowed actions:

\- Inspect Git status and history.

\- Read repository files.

\- Edit locally authorized files.

\- Build and test changes.

\- Review diffs.

\- Suggest Conventional Commit messages.

Never reset, force-push, discard, or overwrite unrelated work.

After each completed implementation stage, report the changes and stop.

---

## 5. Development Workflow

Implement changes using the following sequence.

### Stage 1 — Investigation

\- Understand the user request.

\- Locate the relevant source files.

\- Inspect existing implementations.

\- Identify related dependencies.

\- Identify regression risks.

\- Determine the smallest safe change.

### Stage 2 — Implementation

\- Modify only the relevant functionality.

\- Reuse existing architecture and components.

\- Follow existing coding conventions.

\- Avoid unnecessary abstraction.

\- Avoid unrelated refactoring.

### Stage 3 — Verification

\- Build the affected projects.

\- Run focused tests.

\- Run the full relevant regression suite.

\- Verify affected user interactions.

\- Measure performance where relevant.

\- Report anything that remains unverified.

### Stage 4 — Documentation

Update the relevant documentation when behavior changes:

\- `docs/PROJECT_PROGRESS.md`

\- `docs/IMPLEMENTATION_STATUS.md`

\- `README.md`, when public functionality changes.

Preserve historical records rather than rewriting them.

### Stage 5 — Handover

Provide:

\- Completed work.

\- Files changed.

\- Tests executed.

\- Test results.

\- Known limitations.

\- Regression risks.

\- Suggested Conventional Commit message.

\- Next recommended action.

**Stop and wait for owner approval.**

Do not automatically begin another stage.

---

## 6. Application Architecture

### Entry Point

`src/Fyltrix/App.cs`

Responsible for application startup, settings, themes, and initialization.

Preserve the existing initialization sequence and UI synchronization context.

### Main Window

`src/Fyltrix/UI/MainWindow*.cs`

Partial classes organize navigation, commands, customization, search, updates, tools, sharing, and other application features.

Avoid placing unrelated functionality in the same partial class.

### File and Folder Listing

Primary files:

\- `src/Fyltrix/UI/FolderView.cs`

\- `src/Fyltrix/UI/FolderView.Tiles.cs`

\- `src/Fyltrix/UI/FolderView.Groups.cs`

\- `src/Fyltrix/UI/VirtualizingWrapPanel.cs`

These implement file selection, navigation, sorting, grouping, virtualization, keyboard navigation, and drag-and-drop.

Treat file-list interactions as critical application functionality.

### Settings

\- `src/Fyltrix/Core/AppSettings.cs`

\- `src/Fyltrix/Core/ApplicationDefaults.cs`

\- `src/Fyltrix/UI/SettingsWindow.cs`

Preserve existing settings and saved user preferences.

New configuration options must have safe defaults.

### Previews

Inspect the existing `Preview` and `Viewer` components.

Preserve lazy loading, preview cleanup, and unsupported-format fallback behavior.

### Installation and Updates

Relevant components include:

\- `src/Fyltrix.Launcher/`

\- `src/Fyltrix.Updates/`

\- `packaging/installer/`

\- `tools/Publish-Layout.ps1`

\- `.github/workflows/release.yml`

Preserve the stable launcher, versioned payloads, verified packages, signed update metadata, health checks, rollback, and user-data protection.

Do not replace the installation architecture for unrelated feature requests.

---

## 7. Development Priority 1 — Blank-Space Marquee Selection

**Status:** Missing: hold the left mouse button (LMB) on empty file-list background and drag to select multiple files.

**Already implemented and confirmed working by the owner:**
- Ctrl+click toggles individual file selection.
- Shift+click selects a continuous range.
- Standard single selection, keyboard selection and file drag-and-drop exist.

**Do not reimplement, replace, or change these working interactions.** Test them for regressions only.

### Expected marquee behavior

1. Press and hold LMB on genuinely empty file-list space, including blank space to the right of Details columns where the existing hit-test considers it background.
2. Move beyond the Windows drag threshold to begin an Explorer-style translucent selection rectangle with a visible border.
3. Dynamically select the items intersecting the marquee; releasing LMB finalizes selection and removes the overlay.
4. Work in Details, all four thumbnail sizes, grouped, sorted, and filtered listings.
5. Normal marquee replaces the initial selection. Ctrl+marquee uses a stable snapshot to toggle intersected items without repeated toggling. Any Shift+marquee semantics must be deliberately defined and tested without changing working Shift+click behavior.
6. Escape, lost capture, navigation, disposal, or failure cancels the marquee, restores the initial selection where appropriate, and cleans up mouse capture and overlays.
7. Auto-scroll near viewport edges at a bounded rate while maintaining correct selection; stop immediately when the gesture ends.
8. Preserve selected-item counts, focus, command states, keyboard accessibility, and virtualized-list responsiveness.
9. Starting on a file item must keep the existing file drag-and-drop behavior, not create a marquee. Do not modify files or initiate a move/copy during background selection.

The user has explicitly limited the feature request to **blank-space LMB hold-and-drag selection**. Avoid unrelated selection refactoring.

---

## 8. Marquee Selection Technical Guidance

### Mouse interaction state machine

Implement the missing background gesture using a small, testable interaction state machine, integrating with existing handlers instead of replacing native WPF selection.

```text
Idle
 ├── Mouse down on item → PendingFileDrag
 │    ├── Movement threshold exceeded → Existing FileDrag
 │    └── Mouse released → Existing click selection
 │
 └── Mouse down on background → PendingMarquee
      ├── Movement threshold exceeded → MarqueeSelecting
      │    ├── Pointer moves → Update selection rectangle
      │    ├── Edge reached → Auto-scroll
      │    ├── Escape → Restore previous selection
      │    └── Mouse released → Finalize selection
      └── Mouse released → Existing background click

All states → Idle after completion or cancellation.
```

### Relevant source

- `src/Fyltrix/UI/FolderView.cs`: `SelectionMode.Extended`, `ItemAt`, `OnPreviewMouseDown`, `OnPreviewMouseUp`, `OnPreviewMouseMove`, `SelectedItems`, `DragDrop.DoDragDrop`.
- `src/Fyltrix/UI/FolderView.Tiles.cs`: existing Ctrl/Shift keyboard and thumbnail selection behavior.
- `src/Fyltrix/UI/FolderView.Groups.cs`: grouped listing behavior.
- `src/Fyltrix/UI/VirtualizingWrapPanel.cs`: tile layout and virtualization.

### Implementation requirements

- Preserve existing Ctrl-click, Shift-click, double-click, context menus, keyboard navigation, file drag/drop, inline rename and selection-aware operations.
- Determine item vs background using existing `ItemAt` rules, not a broad `ListViewItem` bounding box in Details mode.
- Apply `SystemParameters.MinimumHorizontalDragDistance` / `MinimumVerticalDragDistance` before starting marquee or file drag.
- Render the overlay without intercepting mouse or scrollbar input; use correct coordinate transformations with scrolling and DPI.
- Capture mouse only when appropriate; reliably release capture and cancel timers/auto-scroll on every exit path.
- Use a selection snapshot and index/range-aware hit testing; avoid full visual-tree traversal and repeated selection toggling on every pointer move.
- Handle scrolling and virtualization without eagerly realizing every row or tile in large directories.
- Add focused offscreen tests for gesture state changes and selection outcomes. Obtain owner approval before foreground desktop automation.

Do not build a separate selection system when the existing `ListView.SelectedItems` model is sufficient.

---

## 9. Marquee Selection Verification

Focused tests must cover blank-space mouse-down vs item mouse-down, drag threshold, live rectangle, selection on release, escape cancellation, lost capture, Ctrl+marquee snapshot toggling, edge auto-scroll, sort/filter/group changes, and virtualized folders. Cover Details and every thumbnail size.

**Regression tests (not new functionality):** Ctrl-click add/remove, Shift-click range in both directions, normal single/double-click, right-click behavior, keyboard selection, drag selected files, drag onto folder/tab/outside app, context menus, inline/batch rename, Compare, Preview and selection-aware toolbar commands.

Run focused tests and the relevant existing regression suite. State explicitly which native visual behaviors require manual acceptance; a passing unit test does not prove screen-level marquee usability.

---

## Development Priority 2 — PDF Comments and Annotations

**Status:** Reported missing in both the ordinary PDF Preview pane and PDF Compare window. Begin only after the owner approves Priority 1 completion.

### Current implementation and investigation

- `src/Fyltrix/Preview/PreviewPane.cs`: normal PDF preview uses WebView2/native PDF viewing; Compare requests `pdfpages` for custom rendering.
- `src/Fyltrix/Viewer/viewer.js`: `renderPdfPages` displays Compare's rendered pages.
- `src/Fyltrix/Core/Pdf/PdfService.cs`: uses PDFsharp for document operations and Windows `Windows.Data.Pdf` for page rendering.
- `src/Fyltrix/Preview/PreviewApi.cs` and `src/Fyltrix/UI/CompareWindow.cs`: preview API and side-by-side comparison integration.
- `tests/Fyltrix.Tests/PdfTests.cs`, `tests/Fyltrix.Tests/PreviewUiTests.cs`, and `tests/Fyltrix.Tests/CompareScrollTests.cs`: existing test coverage.

Audit these code paths and reproduce the issue with controlled PDFs. Do not assume all annotation types are rendered by the native viewer or by `Windows.Data.Pdf` bitmaps. Distinguish **visible annotation appearance** from **associated comment text/metadata**, which may be stored separately from page content.

### Required behavior

1. Show supported existing PDF markups (highlights, underlines, strikeouts, free text, ink, shapes, sticky-note indicators) in the Preview pane and on the correct side/page of Compare.
2. Make associated comment text inspectable even when it is not painted onto the page. Prefer a reusable, accessible Comments/Annotations list with page navigation where required.
3. Surface available author, time, annotation type and page information without inventing missing metadata. Indicate unsupported types clearly rather than silently claiming support.
4. Preserve zoom, page alignment, synchronized scrolling, source-file integrity and existing comparison interactions.
5. Keep PDF annotations read-only. Never modify, flatten, delete or silently rewrite source PDFs.
6. Treat PDF content as untrusted: sanitize comment text, do not execute annotation actions/scripts, and do not follow external links or fetch resources automatically.
7. Reuse existing PDF components when adequate. Introduce a new parser/renderer only with a concrete compatibility, performance and security justification.
8. Keep any compare-side annotation presentation visibly associated with the proper document and page; do not imply that annotation metadata is a pixel difference unless explicitly implemented.

### Verification

Test PDFs containing highlights with comments, sticky notes with popup text, free-text boxes, ink/shapes, multiple annotations per page, different left/right comments, multi-page documents, and PDFs without annotations. Include malformed annotation data and encrypted PDFs. Test correct rendering/inspection, comment-to-page navigation, zoom, resizing and Compare scrolling. Verify source files remain byte-for-byte unchanged.

Report supported and unsupported annotation types and any manual checks. Do not claim full compatibility without evidence.

---

## 10. Testing and Desktop Safety

The owner may be using the Windows desktop while development is underway.

Do not:

\- Steal keyboard focus.

\- Send mouse/keyboard input to the active desktop.

\- Modify the real clipboard.

\- Modify the real Recycle Bin.

\- Change user settings.

\- Close the owner's application windows.

\- Trigger security dialogs without permission.

Use isolated test data and offscreen WPF tests.

Relevant test infrastructure:

\- `tests/Fyltrix.Tests/`

\- `tests/Fyltrix.Tests/StaTests.cs`

\- `tests/Fyltrix.PlacementProbe/`

Use disposable settings and test folders.

Real desktop testing requires explicit owner approval.

Never claim manual acceptance has passed unless it was performed.

---

## 11. Build and Verification Commands

Build the application:

```powershell

dotnet build .\src\Fyltrix\Fyltrix.csproj -p:Platform=x64

```

Run focused tests:

```powershell

dotnet test .\tests\Fyltrix.Tests\Fyltrix.Tests.csproj -p:Platform=x64 --filter "FullyQualifiedName~FolderView"

```

Run the application test suite:

```powershell

dotnet test .\tests\Fyltrix.Tests\Fyltrix.Tests.csproj -p:Platform=x64

```

Run the relevant updater, packaging, and installer test projects when those subsystems change.

Do not use `publish.ps1 -Run` merely to verify compilation. It rebuilds the portable output and launches the application.

Use isolated build and test outputs where existing application files may otherwise be overwritten.

---

## 12. Performance Requirements

Fyltrix must remain responsive when browsing folders containing thousands of files.

For interaction-heavy changes:

\- Keep pointer-move processing lightweight.

\- Avoid unnecessary filesystem enumeration.

\- Avoid blocking the UI thread.

\- Respect WPF virtualization.

\- Minimize layout invalidations.

\- Avoid allocating large temporary collections per mouse move.

\- Reuse existing selection information.

\- Preserve scrolling responsiveness.

\- Check performance before and after changes.

Do not introduce artificial limitations to hide inefficient implementations.

Report measurable changes without claiming improvements unsupported by evidence.

---

## 13. Compatibility and Data Safety

Preserve all existing user settings and personal configuration.

Some internal paths retain the historical `FileLens` identifier for backward compatibility.

Do not rename or migrate these automatically.

Legacy identifiers may also exist in preview origins, manifest compatibility fields, or firewall integration.

Distinguish intentional compatibility identifiers from obsolete product branding.

User-visible product branding must remain **Fyltrix**.

Never rename legacy storage or security identifiers without a dedicated, reviewed migration plan.

---

## 14. Release and Update Safety

The production launcher and versioned payload architecture must remain intact.

Preserve:

\- The stable launcher.

\- Current/previous version pointers.

\- Installer identity.

\- Update signing and verification.

\- Download validation.

\- Safe update application.

\- Health checks.

\- Automatic rollback.

\- Personal data preservation.

\- Taskbar relaunch identity.

\- Portable build compatibility.

No feature implementation should alter release signing, installer ownership, or automatic updates unless explicitly required.

Never create or publish a release without owner approval.

---

## 15. Documentation Rules

Maintain accurate documentation.

For completed work:

\- Update relevant progress records.

\- Document new functionality.

\- Record actual verification evidence.

\- Record known limitations.

\- Avoid outdated version claims.

\- Preserve historical reports.

Do not unnecessarily rewrite unrelated documentation.

Update the public README when new user-facing functionality becomes available.

---

## 16. Definition of Done

A feature is complete only when:

1. The requested behavior is implemented.

2. Relevant edge cases are covered.

3. Focused tests pass.

4. Applicable regression tests pass.

5. Existing behavior remains intact.

6. Performance is acceptable.

7. No user data is damaged.

8. Documentation is updated.

9. Remaining manual verification is clearly identified.

10. The owner receives a concise implementation report.

Unexecuted tests must never be reported as passing.

---

## 17. Required Completion Report

After completing the current stage, respond using:

**Stage:** Name and number

**Status:** Completed / Partially Completed / Blocked

**Summary:** What changed

**Files Modified:** Exact file list

**Verification:** Tests run and results

**Performance:** Measurements where relevant

**Risks:** Known limitations and unresolved cases

**Documentation:** Files updated

**Suggested Commit Message:**

`feat(selection): add blank-space marquee selection`

**Next Action:** What the owner should review

**STOP. Wait for explicit approval.**

The owner will review and perform the commit manually.

---

## 18. Immediate Next Tasks — Sequential Approval

**Priority 1: Blank-space LMB rubber-band selection.** Investigate and implement only the missing background marquee gesture. Ctrl-click and Shift-click already work: preserve them and cover them with regression tests. Implement the specified state machine, overlay, selection update, safe cancellation and bounded auto-scroll across Details and thumbnail views. Verify preservation of existing file dragging and selection-aware actions. Update progress documents, suggest `feat(selection): add blank-space marquee selection`, and **stop for owner approval**.

**Priority 2: PDF annotation/comment visibility.** Only after approval for Priority 1, investigate PDF annotation data and rendering in normal Preview and Compare. Add visible markups and an inspectable comment experience as supported, verify representative PDFs and existing comparison behavior, document limitations, suggest `feat(pdf): show annotations and comments in preview and compare`, and **stop for owner approval**.

Do not modify unrelated features, create branches, commit, push, tag, or release. The owner handles all Git mutations.
