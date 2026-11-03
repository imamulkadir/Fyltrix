# Native PDF comparison and independent windows

> Historical report of the first native-PDF/focus stage. The subsequent [PDF comments and synchronization fix](PDF_COMMENTS_SYNC.md) supersedes the disabled-PDF-sync behavior and annotation limitations described below. Earlier test results remain historical evidence.

## Scope (2026-11-03)

This change addresses only native PDF viewing in Compare and normal Compare/main-window activation. It introduces no PDF engine, annotation feature, settings migration, installer, launcher or updater change. The owner separately authorized desktop testing of these fixes.

Earlier marquee work was already uncommitted when this request replaced its scope. That work is paused and preserved, including a recovery copy under `%TEMP%\Fyltrix-marquee-paused-20261103`. It is not part of this Compare fix. The owner's AGENTS.md edits are also preserved. Review those existing changes separately before committing; no Git mutations were performed.

## Confirmed causes and implementation

- `PreviewPane.ShowNow` previously selected the `pdfpages` image renderer whenever `ReportsScroll` was enabled. Compare enables scroll reporting, so it bypassed the ordinary native PDF path. PDFs now always navigate to their original read-only file URL through the existing WebView2/PreviewServer architecture, exactly as regular Preview does. PdfService rendering, image conversion and legacy explicit API routes remain available for their existing purposes.
- Native PDF panes disable scroll reporting and stop any previous scroll-report polling. Compare disables Sync scrolling with a tooltip explaining that native viewers operate independently. The checkbox retains its previous choice for non-PDF replacement documents. Other rendered comparison formats keep their existing synchronization logic.
- Compare was opened using `Show()` but assigned its main window as `Owner`. It was therefore an owned modeless window. The constructor now retains the opening window only for initial monitor/DPI-aware placement and lifetime cleanup; it does not assign WPF/native ownership or Topmost.
- Initial placement reuses `WindowPlacement`'s physical-pixel calculation without activation/Z-order changes. Explicit/offscreen placement remains respected. There is no recurring activation workaround or forced foreground loop.
- Compare applies the existing Fyltrix window icon and TaskbarIdentity rather than inventing an identifier. Closing the opening window closes Compare and disposes its preview controls; closing Compare unsubscribes the lifetime handler. Existing MainWindow tracking remains in place.

The native PDF UI is provided by the installed WebView2 runtime, including its supported text/search/zoom/navigation/annotation behavior. Fyltrix does not replace those controls or claim support for annotation types beyond regular Preview. See Microsoft's [PDF toolbar setting reference](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2settings.hiddenpdftoolbaritems) for the native toolbar API.

## Verification

The dedicated tests use real WebView2 controls with disposable profiles and original PDFs. They check application/pdf documents and original URLs, absence of the custom image-page DOM, independent controllers/zoom state, native toolbar/control availability, swap behavior, source hashes, managed/native ownership, non-topmost styles, and closing/disposal. Fixtures cover valid documents, a password-protected document, malformed PDF data and a 500-page document. Navigation/MIME checks for damaged/encrypted documents do not assert that their native error/password UI was visually accepted.

The old PDF image-page wheel-synchronization case is replaced by native PDF tests. The existing Markdown, HTML, code, JSON, CSV, Word and spreadsheet scroll tests are retained. Final isolated build succeeds with existing warnings. The complete application suite passed **481 tests, zero failures/skips**, in 2 minutes 51 seconds. This includes the pre-existing paused marquee tests. Evidence: `%TEMP%\Fyltrix-native-compare-20261103\results\native-compare-full-suite.trx`. A conventional temporary test/probe layout preserves the existing monitor-test helper discovery; no changes to that unrelated test implementation were made.

The explicit `compare-desktop` placement-probe mode requires a marked folder under the Windows temporary directory and is never invoked by automated tests. It uses disposable settings, WebView2/thumbnail storage and the probe's existing clipboard stub. The owner-approved desktop run demonstrated:

- Both original PDF documents and their native toolbars visible side by side.
- Native left zoom visibly changed only the left document.
- Actual keyboard entry in the left page control displayed page 2 while the right stayed on page 1.
- Visible PDF text highlighting through the native accessibility text range; actual clipboard copying was deliberately not exercised.
- Clicking the test main window brought it above Compare. Alt+Tab switched between them, including bringing main above a maximized Compare. Restore/minimize operations were exercised on the known test window.
- The taskbar exposed one separate development group containing both test windows, alongside the untouched production group. Selecting individual taskbar previews was not conclusively verified.

Foreground/process guards refused input/capture when VS Code became foreground. Only the known isolated fixture was closed afterward; the owner's application windows were not closed. The fixture briefly navigated through its own sidebar during a focus click; no personal file was opened or modified. Its saved history belongs only to the disposable test profile.

Local desktop evidence remains under `%TEMP%\Fyltrix-compare-desktop-20261103`; browser/test evidence is under `%TEMP%\Fyltrix-native-compare-20261103`. These internal diagnostic screenshots are not promotional/public assets.

## Remaining acceptance checks

- Confirm actual clipboard copying from each PDF without losing the owner's clipboard contents; this run did not change/read the real clipboard.
- Verify end-to-end native Find match results. Its native search popup opened, but query matching was not conclusively accepted.
- Select each individual taskbar preview and verify activation. Confirm mixed-DPI/multiple-monitor placement on a machine with those displays.
- Visually inspect encrypted/corrupt PDF messages and representative existing annotation/comment rendering against regular Preview. No annotation enhancements are included.
- File replacement continues through the existing picker; direct desktop picker acceptance was not performed.

No automatic commit, push, branch, tag, merge, release or installation is authorized. Stop for owner review after this implementation stage.
