# PDF comment visibility and native scroll synchronization

> Historical first-stage report. The owner subsequently reported visible scrolling lag and rejected permanent bottom panels. [Native wheel synchronization and comments flyouts](PDF_NATIVE_SCROLLING_UI.md) supersedes that presentation and adds continuous-motion verification. The owner also added two notes to the original PDF; the hashes and one-note inventory below describe the earlier file revision.

## Scope and causes — 2026-11-03

This follow-up fixes the owner's two PDF Compare issues. It preserves native WebView2 PDF rendering and the preceding independent-window/focus fix. Installer, launcher, updater, dependencies, personal configuration and `publish.ps1 -Run` are unchanged. Earlier uncommitted marquee work and owner edits to AGENTS.md remain preserved separately.

The exact source samples were inspected read-only:

| PDF | Actual annotations relevant to this report |
| --- | --- |
| `file-sample_150kB.pdf` | Four pages; page 1 sticky note, author `imamu`, text `test comment`; separate Popup record and an unrelated Link |
| `file-sample_150kB - Copy.pdf` | Four pages; Link only, no comment text |

The first PDF stores its note in `/Contents` and rich `/RC` annotation metadata. The native viewer paints the note icon but did not expose its popup text when clicked in the investigated runtime. No application clipping fix or supported WebView2 popup switch was found. Microsoft's [WebView2 browser-feature documentation](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/browser-features) describes differences in PDF annotation capabilities; it does not guarantee this particular sticky-note interaction. We therefore use the explicitly authorized read-only Comments panel fallback. Clicking the native note icon itself is **not** claimed fixed.

The previous native-viewing stage deliberately disabled PDF synchronization because ordinary page JavaScript scroll reporting does not control the embedded native PDF. The new implementation uses the public Windows accessibility [ScrollPattern API](https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.scrollpattern) instead. It never inspects browser-internal PDF DOM in production or replaces pages with raster images.

## Behavior and implementation

- Each PDF side has its own clearly labeled Comments panel. Available page, type, author and plain comment text are shown; absent comments and read/password errors have explicit states. Native annotation appearance remains controlled by WebView2.
- Existing PDFsharp reads original annotation dictionaries without saving them. `/Contents` is preferred; `/RC` is converted to plain text with DTDs and external XML resources prohibited. Popup children are excluded to avoid duplicate parent comments; annotations without comment text are not fabricated. Unknown types are labeled as other annotations.
- Metadata extraction runs off the UI thread, supports cancellation and ignores stale results after file replacement. A per-panel gate prevents overlapping parser work. Virtualized rows contain read-only text controls, never active HTML, annotation scripts or links. Metadata is bounded independently of native viewing (10,000 comments, 100,000 displayed characters per comment/author field, 1,000,000 rich-text input characters).
- **Sync scrolling** uses relative vertical percentage, works in both directions and supports unequal document lengths. Turning it off preserves independent scrolling; re-enabling aligns the right PDF to the left. Zoom remains independent.
- One shared MTA worker (following Microsoft's [UI Automation threading guidance](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-threading)) discovers each native scrollable document under the two WebView HWNDs. It registers document-specific scroll-property handlers and a comparison-window structure handler to rediscover replaced accessibility documents. Top-level PDF navigation refreshes the affected document subscription while preserving pending acknowledgements on the other side. Generic browser documents without vertical scrolling are not treated as ready PDF scroll providers.
- Scroll updates are coalesced, compared with a bounded, approximately one-physical-pixel tolerance (using native client bounds when accessibility screen bounds are empty) and protected against reflected events. Pending native scroll actions are tracked until their matching change event arrives (with bounded expiry), so a delayed acknowledgement of an earlier alignment cannot reverse a newer scroll. Change-event values are retained because a native provider snapshot can lag its event. Target writes are serialized until acknowledgement; only the newest requested target position is kept. A bounded watchdog retries an unacknowledged action at most three times and stops when idle; an unresponsive runtime gets an explanatory disabled state instead of an endless retry loop. Startup retries are bounded to ten seconds; afterward discovery and scrolling are event-driven, with no continuous scroll/clipboard polling or global input simulation. Closing/replacing the comparison detaches its events, stops its startup timer and releases its document references without blocking the UI thread. The single background apartment remains idle for subsequent comparisons until process exit, avoiding overlapping registration/removal across worker threads.
- **Go to page** uses page heights and the native scroll interface to estimate the page position in continuous vertical layout. It is disabled when that interface is unavailable. It is not a PDF-internal page API; native page controls remain available for exact navigation, rotated pages and alternative layouts.

## Verification

The isolated application/test build succeeded with zero errors and the existing SettingsWindow nullable, Windows Forms DPI-manifest and FolderViewTests nullable warnings. The final focused run passed **27 tests, zero failures/skips**, covering comment extraction, safe rich text, popup/empty/long metadata, distinct comments on either side, stale load cancellation, native viewing, unequal page counts, both scroll directions, disable/re-enable, three consecutive browser reloads alternating between sides/reconnection, event cleanup, swap, source preservation, difficult PDFs and existing non-PDF synchronization.

Final complete application suite: **492 passed, zero failures/skips**, in **3 minutes 20 seconds**. Five additional consecutive fresh-process native scroll runs passed (15 alternating-side reloads in total). Earlier pre-fix passes and failures remain historical diagnostics; they are not substituted for this final run. A final punctuation-only reconnect-tooltip cleanup was followed by an isolated application/test rebuild (zero errors, existing warnings) and a passing native handover smoke check (one case, three reloads).

TRX evidence is under `%TEMP%\Fyltrix-pdf-comments-sync-20261103\results`:

- `pdf-sync-serialized-driver-final.trx`
- `pdf-sync-serialized-final-full.trx`
- `pdf-sync-driver-final-repeat-1.trx` through `pdf-sync-driver-final-repeat-5.trx`
- `pdf-sync-final-handover.trx` (final tooltip cleanup rebuild/native check)

The final source-input helper uses a separate hidden Windows PowerShell accessibility client to establish only the requested source-pane position, with bounded retries when native input itself is ignored. It never sets or retries the expected target pane. Actual source and target positions are asserted separately. All final automated test windows remain offscreen; no real clipboard or global input is used. Earlier in-process, cloaked and visible diagnostic approaches are retained only as historical failure evidence.

The full run uses a conventional test/probe directory layout inside the same temporary root because existing placement tests discover their helper by a `bin` path substitution. No unrelated placement-test implementation was changed. Earlier intermediate runs exposed reload failures; those failures were retained as diagnostic evidence rather than reported as passes.

Owner-approved desktop testing ran the actual application components in a fresh marked temporary folder, with byte-identical sample copies and disposable settings, browser/thumbnail caches and a clipboard stub. The final screenshot visibly shows the native PDFs, note indicator, readable `test comment` and the other document's empty state. Public accessibility operations verified left-to-right movement to 42%, right-to-left to 78%, disabled independence, re-enable alignment, comment navigation to page 1 and one real right-pane mouse-wheel scroll. Measured target-observation waits were 31 ms and 403 ms respectively, with 20 ms sampling; these observations are not a general latency guarantee or a before/after CPU/memory benchmark.

Final desktop evidence: `%TEMP%\Fyltrix-pdf-comments-final-serialized-20261103\final-verification.json` and `final-comments-and-sync.png`. Only the known test fixture was closed. Original SHA-256 values still match:

- `file-sample_150kB.pdf`: `38752a73c206b8bb19923ff9c2828306c1f5e47a2e10cd9cd993aa8943e44291`
- `file-sample_150kB - Copy.pdf`: `38c9792d725c45dd431699e6a3b0f0f8e17c63c9ac7331387ee30dcc6e42a511`

## Limits and owner acceptance

The fix exposes comments in the panel; it does not add annotation editing or promise native note popups. Metadata tests cover sticky notes, highlights, free text, ink and unknown types; they do not prove visual rendering of every annotation appearance. Password-protected comments cannot be extracted without a parser password, independently of the native viewer's password prompt. Malformed/unreadable metadata reports a state while the native viewer remains available.

Synchronization depends on the installed runtime exposing a public scrollable document. If unavailable (including an initially loaded document fully fitting the viewport), the checkbox is disabled with an explanation. It synchronizes relative progress, not semantic page correspondence or pixel-identical layouts. Native page navigation, search, selection and rendering remain in WebView2. Existing native control/zoom tests pass; visible native text selection and independent page/zoom controls were exercised in the preceding desktop stage. Real clipboard copying was not repeated because the owner's clipboard must remain untouched. Native Find match success, mixed-DPI placement and unusual rotated/spread-page navigation remain owner acceptance checks, not claimed passes.

## Files in this follow-up

- `src/Fyltrix/Core/Pdf/PdfService.cs`
- `src/Fyltrix/Preview/PreviewPane.cs`
- `src/Fyltrix/UI/CompareWindow.cs`
- `src/Fyltrix/UI/NativePdfAccess.cs` (new)
- `src/Fyltrix/UI/PdfCommentsPanel.cs` (new)
- `tests/Fyltrix.Tests/PdfCommentTests.cs` (new)
- `tests/Fyltrix.Tests/PdfCommentSyncTests.cs` (new)
- `tests/Fyltrix.Tests/NativePdfCompareTests.cs` (extends the preceding native-viewer stage)
- `README.md`
- `docs/NATIVE_PDF_COMPARE.md` (historical-report cross-reference)
- `docs/PDF_COMMENTS_SYNC.md` (this report)
- `docs/PROJECT_PROGRESS.md`
- `docs/IMPLEMENTATION_STATUS.md`

Suggested owner commit: `fix(pdf): expose annotation comments and restore compare scroll synchronization`. No Git mutation, installation or publication was performed. Rebuild with the usual `publish.ps1 -Run` after closing the development app, then compare the two originals for owner acceptance. Stop for review.
