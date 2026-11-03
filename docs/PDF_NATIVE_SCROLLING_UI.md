# Native PDF wheel synchronization and comments flyouts

## Scope and owner decision — 2026-11-03

The owner reported that the first synchronization implementation caught up visibly after scrolling, and rejected the permanent bottom Comments panels. The owner explicitly chose **Keep the native WebView2 viewer** over evaluating another PDF viewer. This stage preserves native PDF rendering, selection, zoom and navigation. It changes neither the installation/update system nor existing user configuration, and does not run `publish.ps1 -Run` or perform Git mutations.

Earlier tests established eventual position agreement; they did not establish smooth simultaneous motion. Those results remain historical evidence in [PDF_COMMENTS_SYNC.md](PDF_COMMENTS_SYNC.md). This stage adds actual wheel-gesture and captured-frame verification.

## Why this differs from full Microsoft Edge

WebView2 is based on Edge but does not expose its entire browser PDF interface. Microsoft's [feature comparison](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/browser-features) lists PDF annotation features as disabled/non-configurable, specifically describing drawing, inking and highlighting. That statement alone does not prove support or lack of support for every read-only sticky-note interaction. In the preceding local investigation, existing note icons painted while their associated text did not open reliably; no supported switch enabling the full Edge comments interface was found. This change does not claim to repair native note-icon popups or provide annotation editing.

The replacement Fyltrix Comments flyout is read-only and opens from each PDF's **Comments** button. It is a WPF popup above the native browser airspace, not an injected PDF interface or a permanent panel. The count, filename, page, type, author, text, empty/error state and navigation remain associated with that document. Opening it re-reads current annotation metadata; closing it leaves the PDF's full viewport available. Close, Escape, outside-click behavior and owner move/resize/close cleanup are provided without a forced foreground loop. No new settings or preference migration are introduced.

## Scrolling implementation

The previous path waited for accessibility scroll-property notifications and then sent an absolute position. Those notifications and actions are asynchronous. That can produce the visible follower delay even when final percentages agree.

The new path uses Microsoft's supported [AllowHostInputProcessing](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2controlleroptions.allowhostinputprocessing) API and the documented [host input message flow](https://github.com/MicrosoftEdge/WebView2Feedback/blob/main/specs/AllowHostInputProcessing.md). The existing WebView2 SDK moves from `1.0.2903.40` to `1.0.3351.48`, the minimum stable version exposing this API. No additional PDF library is added. The newer API was first compiled and exercised in an isolated temporary prototype before repository changes.

- Host input routing is opted into native PDF controllers in Compare only. Regular Preview, non-PDF Compare and other viewers keep their ordinary input mode. Replacing a PDF with another format recreates only the affected controller when the required input mode changes. Older runtimes that cannot enable this option retain accessibility fallback.
- A comparison-scoped `ComponentDispatcher.ThreadFilterMessage` handler observes plain vertical wheel input before normal source dispatch. It never consumes the source event. The peer receives a window-scoped `WM_MOUSEWHEEL` through a bounded `SendMessageTimeout`; no cursor movement, global mouse injection, synthetic click, browser DOM access or native window-class assumptions are used in production.
- Source HWND ancestry and the public native document viewport are checked. The destination is hit-tested inside the peer's public document bounds and must be a descendant of that peer WebView HWND. Covered, hidden, stale or unrelated windows are not sent input.
- Cached public scroll geometry scales wheel deltas for unequal scrollable document extents, retaining fractional high-resolution input and bounding overflow. Geometry refreshes on relevant native size/bounds changes and Compare move/resize; it is not queried synchronously in the input handler.
- While the native wheel gesture animates, delayed accessibility events update cached position but do not restart either animation with absolute-position writes. A gesture-local timer performs a final relative-position correction after 650 ms of quiet. It stops when idle. There is no continuous idle scroll polling.
- A new native click, key, modified wheel or active selection drag interrupts that wheel suppression and prefers the newly interacted pane temporarily. Passive mouse hovering does not cancel inertia. Ctrl-wheel stays an independent zoom operation; selection and dragging are not mirrored.
- The preceding public UI Automation fallback remains for navigation and inputs outside this fast path. Its coalescing, acknowledgement tracking, bounded retries, unsupported-runtime state and cleanup remain in place. The message filter is removed when Compare closes.

## Exact updated samples

The owner changed the original PDF after the first stage. Its current contents were inspected read-only using existing PDFsharp:

| File | Actual comments |
| --- | --- |
| `file-sample_150kB.pdf` | Page 1: `test comment` and `second commeny`; page 2: `3rd comment`. All three are sticky notes by `imamu`. |
| `file-sample_150kB - Copy.pdf` | No comment text; the existing unrelated Link is not fabricated as a comment. |

Stage-start and post-desktop SHA-256 values match:

- Original: `91d2ab1817c0ba7bf4a1b9559e5d352095ff012be2cc4831cdfdd4ea4a9afb72`
- Copy: `38c9792d725c45dd431699e6a3b0f0f8e17c63c9ac7331387ee30dcc6e42a511`

These replace neither the owner's files nor the historical first-stage hash evidence.

## Verification and measurements

Builds and automated tests use isolated output below `%TEMP%\Fyltrix-native-pdf-smooth-20261103`. An initial focused run passed 38 cases and a full application run passed 503 cases. New interruption rules increased the focused run to **44 passing cases**; the final full application suite passed **509 cases, 0 failed, 0 skipped**, including geometry refresh and explicit comment-navigation regressions (3 minutes 27 seconds). Existing application/test warnings are the SettingsWindow/FolderViewTests nullable warnings and the Windows Forms DPI manifest warning; no new warning has been observed.

The tests cover ordinary versus Ctrl/Shift/button-modified wheel input, negative monitor coordinates, fractional input, extreme document extents, passive hovering versus active input, PDF/non-PDF controller replacement, comments data/closed flyout layout, existing native controls, unequal-length relative synchronization, reload/cleanup, and existing non-PDF synchronization. The final result is recorded in `results/native-smooth-complete-full.trx`. The final expanded comment-navigation case also passed independently in `results/native-comment-explicit-navigation.trx`.

Owner-approved desktop testing used disposable settings/caches, a clipboard stub and fresh byte-identical sample copies. It demonstrated:

- Native PDF controls remain visible and the bottom panels are gone.
- All three comments are readable in the left flyout; the right flyout has a useful empty state.
- Invoking the third note's navigation button displayed page 2 in both native viewers. A final check moved Compare, scrolled the right pane and immediately chose the left third comment: both native page selectors settled on page 2. Comment navigation now explicitly aligns the peer even if the source is already at the requested position or popup focus prevents a fresh native scroll event. The expanded regression covers that already-selected-page case.
- Clicking the actual native page-2 note icon still did not open a native comment card on WebView2 runtime `156.0.4314.58`; `native-note-click-current-runtime.png` records this limitation.
- Sixteen real guarded wheel inputs (eight per direction/pane) produced exactly sixteen forwarded peer inputs, with no forwarding feedback loop.
- The recorded burst's dispatches were sub-millisecond. The full application initially measured 0.256 ms; the recorded burst ranged from **0.032 to 0.129 ms**, with its last dispatch at 0.056 ms. These are input-dispatch times, not total render latency guarantees.
- The usable recording contains actual native PDF pixels: motion onset was within one **sampled 33 ms frame interval** for the first burst and in the same sampled frame for the second. A pixel-difference analysis found 74 moving sampled frames and two single-pane movement frames: the first onset interval and a later small settle correction. This is evidence of overlapping motion, not proof of compositor-level frame lock.
- With Sync disabled, the left stayed at 31.7297% while the right moved from 31.7260% to 35.5530%; forwarded count stayed unchanged. Re-enabling resumed alignment.
- Native text highlighting was visibly captured; a selection/drag out to the fixture title bar was followed by a successful native wheel operation. The unrelated source application/profile was not exercised.
- Ctrl-wheel produced no forwarded input; left view size changed from 19.5886% to 15.6181%, while right stayed at 19.6247%.
- Real clipboard copying was not exercised; the clipboard and original PDFs were left unchanged. Only the known test fixture was closed.

Desktop evidence: `%TEMP%\Fyltrix-native-pdf-smooth-desktop-20261103`, including `comments-flyout.png`, `right-comments-empty.png`, `third-comment-page2.png`, `native-text-selection.png`, and `control\motion-result.json`, `controls-result.json`, `move-interruption-result.json`, `frame-motion-analysis.json`, `native-wheel-screen-verified.mp4`. The usable six-second video contains 243 sampled frames. Earlier window-DC captures omitted native child surfaces, and a GPU capture failed to finish; these are explicitly excluded from visual acceptance. The usable recording cropped the known fixture region with input/foreground guards and is private diagnostic evidence, not a public README asset.

The explicit PlacementProbe desktop mode now has an on-demand, local test-window message interface for state/trace queries and GUI actions. It uses no timer/command-file polling and writes replies below the fixture's control directory. This handler is not part of the shipped application. Existing placement tests still locate their helper through a conventional temporary test/probe layout, with no changes to their implementation.

## Practical limits and owner review

Keep expectations specific: the fast path covers plain vertical `WM_MOUSEWHEEL` input, including compatible high-resolution wheel messages. Native scrollbar/thumb dragging, direct touch/pointer scrolling, keyboard navigation and unsupported runtime paths still depend on the accessibility fallback. They are not claimed frame-synchronous. Two independent native renderer/compositor processes cannot be promised exact frame lock through these APIs. The final relative correction can make a small settle adjustment after a gesture, and unequal document lengths/zoom levels do not imply semantic page matching.

The comments flyout is a supported Fyltrix presentation of extracted metadata. It does **not** make clicking a native note icon open Edge's own card. Exact browser-style native popup behavior remains a runtime limitation under the owner's instruction to keep native WebView2. Comment-page navigation still estimates continuous vertical position; rotated/spread layouts use native page controls for exact placement. Password-protected/unreadable metadata retains its explanatory state without replacing native rendering.

No broad before/after CPU/memory benchmark, real clipboard-copy test, precision-touchpad hardware test or mixed-DPI acceptance is claimed. The owner should rebuild through the unchanged development workflow and review physical wheel scrolling and the Comments flyouts before committing.

## Files in this stage

- `src/Fyltrix/Fyltrix.csproj`
- `src/Fyltrix/Preview/PreviewPane.cs`
- `src/Fyltrix/UI/CompareWindow.cs`
- `src/Fyltrix/UI/NativePdfAccess.cs`
- `src/Fyltrix/UI/NativePdfWheelMirror.cs` (new)
- `src/Fyltrix/UI/PdfCommentsPanel.cs`
- `src/Fyltrix/UI/PdfCommentsFlyout.cs` (new)
- `tests/Fyltrix.Tests/NativePdfInputTests.cs` (new)
- `tests/Fyltrix.Tests/PdfCommentSyncTests.cs`
- `tests/Fyltrix.PlacementProbe/CompareDesktopProbe.cs`
- `README.md`
- `docs/PDF_COMMENTS_SYNC.md` (historical-report cross-reference)
- `docs/PDF_NATIVE_SCROLLING_UI.md` (this report)
- `docs/PROJECT_PROGRESS.md`
- `docs/IMPLEMENTATION_STATUS.md`

Suggested commit: `fix(pdf): mirror native wheel input and show comments on demand`. Stop for owner review; no commit/push/tag/release was performed.
