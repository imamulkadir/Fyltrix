# PDF Annotation Interaction — Investigation

Status: **Stage 1 complete (investigation only). No production source changed. Awaiting owner approval.**
Date: 2026-11-15. `master` @ `acc3260`. WebView2 Runtime 157.0.4341.46, SDK 1.0.3351.48.

## Summary

| Question | Answer |
|---|---|
| Navigate to the exact annotation location? | **Yes, vertically, verified.** The native viewer's public UI Automation tree exposes each page's on-screen rectangle. With the annotation's `/Rect`, `CropBox` and `/Rotate` from PDFsharp, the annotation's position maps to within about 1–2 px. This was confirmed at default zoom, after zoom-in, after scrolling, on a 90° rotated page and on a cropped page. |
| Highlight its actual position? | **Conditional.** Vertical position is reliable. Horizontal position was correct at the default fit but **looked stale after zoom and after resize** in the off-screen test window. The highlight must therefore be drawn only when runtime consistency checks pass, and the horizontal case needs an on-screen check (owner approval required). |
| Floating popup near the annotation? | **Yes.** Same mechanism as the existing Comments flyout: a WPF `Popup` (its own HWND) above the WebView2 airspace. It's placed next to the mapped rectangle when geometry is trusted, otherwise anchored to the viewer's top edge. |
| Intercept native annotation-icon clicks? | **No, not reliably.** Sticky notes (`/Text`) do **not** appear in the accessibility tree at all. Highlights appear as "Highlight with comment" with an Invoke pattern, but there's no supported click notification. |
| Fyltrix-owned markers instead? | **Technically possible but not recommended now.** They would need continuous geometry tracking during native scroll, zoom and inertia via asynchronous UIA. Markers would drift or lag during motion and duplicate the native icons. |
| Guaranteed vs conditional | **Guaranteed:** exact comment identity, popup with correct text and document, page-level navigation. **Conditional on runtime checks:** vertical scroll-to-annotation, then the horizontal highlight. **Not offered:** native icon clicks. |

## Current architecture (verified in source)

- **Preview** shows a PDF in the native WebView2 viewer (`PreviewPane` navigates to the file URL). There is **no Comments UI in the normal Preview pane**; Comments exist only in Compare.
- **Compare** gives each PDF side a `PdfCommentsPanel` inside a `PdfCommentsFlyout` (a WPF `Popup` above the native airspace).
  - "Go to" raises `PageRequested(page, heights)`, and `NativePdfAccess.GoToPage` estimates a scroll percentage from the PDFsharp page heights.
  - That moves to near the page top, with a small fixed 2% in-page offset. It is not annotation-aware.
- **`NativePdfAccess`** runs one shared MTA worker for UI Automation. It finds the native PDF document's `ScrollPattern` under each WebView HWND, and handles scroll sync and the wheel mirror.
- **`PdfService.ReadComments`** extracts per annotation: page, type, author `/T`, text (`/Contents`, else plain text of `/RC`) and `/M`, with Popup children skipped.
  - It does **not** keep `/Rect`, `/QuadPoints`, `/NM`, the object reference, `/Popup`/`/IRT`, `CropBox`/`MediaBox` or `/Rotate`.
  - `PageHeights` uses `page.Height`.
  - Extraction is bounded (10,000 comments; truncation) and read-only.

## Prototype

Temporary probe: `PdfGeometryProbe.cs` and its outputs in the session scratchpad. It was kept out of the repo.

The probe used a disposable raw PDF with 4 pages (612×792):

| Page | Contents |
|---|---|
| 1 | Text "ALPHA", a sticky note `/Rect [300 650 320 670]`, and a highlight `/Rect [72 695 150 722]` with `/QuadPoints` |
| 2 | Two sticky notes, near the bottom (`[100 100 120 120]`) and the top (`[400 700 420 720]`) |
| 3 | `/Rotate 90`, with a sticky note |
| 4 | `/CropBox [50 50 562 742]`, with a sticky note |

It was shown in a real `PreviewPane` (off-screen window, isolated profile). The accessibility tree was read from a background MTA thread, as `NativePdfAccess` does.

### What the native viewer exposes (public UIA, raw view)

```text
Chrome_RenderWidgetHostHWND
 └ Document (RootWebArea) → object → Document 'probe.pdf'
    ├ ToolBar 'PDF bar' (zoom in/out, fit, page number, rotate, page view, find, print, save…)
    └ document-container → object (plugin) → Document 'PDF Document'  [Scroll pattern]
        └ … → Document '<untagged N page document…>'
            ├ Group 'Page 1' (region)  rect = page on screen
            │   └ Custom 'ALPHA' (LocalizedControlType "Highlight with comment", Invoke) → Text 'ALPHA'
            ├ Group 'Page 2' (region) → Text runs…
            ├ Group 'Page 3' (region, 861×667 = rotated)
            └ Group 'Page 4' (region, 558×754 = CropBox 512×692 × scale)
```

- Every page region exists with real bounds, including pages scrolled out of view (`IsOffscreen = true`).
- **Sticky notes (`/Text`) are not exposed.** Their comment text is not exposed anywhere (`HelpText`/`ItemStatus` are empty).
- The highlight is exposed, but its comment text isn't.
- The accessibility tree appeared within the first poll after the PDF loaded; the first query enables browser accessibility.

### Mapping accuracy

Scale `s` = page region width ÷ CropBox width (rotation-aware).

**Unrotated page:**
- Screen X = pageLeft + (x − cropLeft) · s
- Screen Y = pageTop + (cropTop − y) · s

**`/Rotate 90`:**
- X = pageLeft + (y − cropBottom) · s
- Y = pageTop + (x − cropLeft) · s

| Check | Predicted | Exposed | Error |
|---|---|---|---|
| Highlight, default fit (s = 1.0899) | x −31812.5, y −31845.7, w 85.0 | x −31812, y −31844, w 86 | ≤ 2 px |
| Rotated page 3 size | 861 × 667 | 861 × 667 | 0 |
| Rotated "GAMMA" (72,700) | x ≈ −31225…−31207, y −30099.5 | x −31235 (w 31), y −30099 | glyph box |
| Cropped page 4 size | 558 × 754 | 558 × 754 | 0 |
| Cropped "DELTA" x | −31781.5 | −31782 | < 1 px |
| After 2× zoom-in (s = 1.3333) | highlight x −31771, y −31827.7 | x −31771, y −31826 | ≤ 2 px (vertical) |
| After scroll to 50% | all rects shift by an equal Δy | Δy = 1501 for every element | 0 |
| After window resize 884 → 584 px | page should move or reflow | **page rects unchanged after 6 s**; vertical still tracked later scrolling | **horizontal stale/unverified** |

The page's left edge after zoom was 125 px inside the container; a centred layout predicts about 27 px. Either the viewer doesn't centre after zoom, or horizontal bounds go stale in an off-screen window. Off-screen WebView2 content can't be screenshotted, so **horizontal accuracy needs an owner-approved, visible on-screen check**.

### Cases where accurate positioning can fail

- Horizontal bounds after zoom or resize (above), until verified on screen.
- Two-page or spread layouts ("Page view"). Page regions still carry bounds, but this wasn't tested.
- `/Rotate 180`/`270` were not tested (formulas derived).
- Runtime structure changes: page regions are found by structure (the region children under the page document). Names ("Page 1") are localized and must not be relied on.
- Runtimes that don't expose page regions, or documents still loading.
- Very large documents: the first accessibility query builds the browser's PDF tree, which is a cost.

## Native icon click — evaluated and rejected for now

- Sticky-note icons have no accessibility node, so they can't be identified or hit-tested via UIA.
- Highlights have an Invoke-capable node, but there's no documented event when the user clicks one natively.
- Detection via global hooks, pixel recognition, internal DOM/CDP or plugin JavaScript is prohibited by the task, and wasn't used.
- **Fyltrix-owned markers:** possible only while the document is idle (positions re-read after scroll/zoom settle). They'd lag during native inertia scrolling and duplicate the native icons. Recommended as a later, optional stage once the core feature has been accepted on screen.

## Proposed implementation (after approval)

**Stage 2 — data model and popup**

- `PdfService.ReadComments` gains, per comment:
  - **Stable id:** `/NM`, else the indirect object number and generation, else page and index.
  - **Geometry:** `/Rect` (normalized; invalid or zero-size rects marked as unusable), `/QuadPoints` for markup types, the `/Popup`/`/IRT` association, and `/CreationDate`.
  - **Per page:** CropBox (falls back to MediaBox), `/Rotate` (normalized to 0/90/180/270) and the displayed size.
- Existing `PdfComment` and `PageHeights` users keep working: these are new optional fields.
- New `PdfAnnotationPopup`: a small themed card in a WPF `Popup`.
  - Shows type, author, page and date.
  - The comment is a selectable read-only `TextBox`, scrollable when long.
  - Dismissed by Close, Escape, a click outside, another comment, document change, window move/resize, or Compare/Preview close.
  - Not `Topmost`, and doesn't steal focus from the main window.
- Selecting a comment opens the popup for that exact comment and document.

**Stage 3 — navigation and highlight**

- New geometry helper (pure, unit-tested): PDF rect → page-relative fraction, for each rotation and CropBox.
- `NativePdfAccess` gains `RevealAnnotation(side, page, rect)` on its existing MTA worker:
  1. Find the page regions (structure-based; count must equal the PDF page count; each aspect ratio must match the rotation/CropBox-aware page size within 2%).
  2. Compute the annotation's document Y and scroll it to the vertical centre through the existing `ScrollPattern`.
  3. Re-read the page bounds (bounded retries, no fixed sleeps) and confirm the target is inside the viewport.
  4. Report one of: **Exact** (vertical confirmed, horizontal passes the checks), **Vertical** (horizontal untrusted), or **Page** (page regions unavailable). Page falls back to today's estimate.
- The highlight is a click-through transparent border (a separate WPF `Popup`, not `Topmost`), drawn **only** for Exact. It's removed after about 2.5 s or on any scroll, zoom, document or window change. The popup is anchored next to the rect for Exact, beside the viewer edge at the annotation's height for Vertical, and at the viewer top for Page. Its header states which level was achieved.
- Stale requests are cancelled by a generation counter (new selection, file change, close).
- **Preview:** add the same `PdfCommentsFlyout` (Comments button) to the main Preview pane for PDFs only. The UIA/geometry work starts only when the user opens Comments or selects one, so ordinary browsing pays nothing.
- **Compare:**
  - Each side keeps its own panel, popup and request generation.
  - The existing sync rule is kept: navigation aligns the peer by relative position, but the popup and highlight appear only on the selected side.
  - Swap and replace clear popups.

**Stage 4 — markers (optional, separate approval).** Only after on-screen acceptance of Stage 3.

## Files to modify

- `src/Fyltrix/Core/Pdf/PdfService.cs`
- new `src/Fyltrix/Core/Pdf/PdfAnnotationGeometry.cs`
- `src/Fyltrix/UI/PdfCommentsPanel.cs`
- `src/Fyltrix/UI/PdfCommentsFlyout.cs`
- new `src/Fyltrix/UI/PdfAnnotationPopup.cs`
- `src/Fyltrix/UI/NativePdfAccess.cs` (new reveal method on the existing worker; no change to sync paths)
- `src/Fyltrix/UI/CompareWindow.cs`
- `src/Fyltrix/UI/MainWindow*.cs` (Preview Comments button) or `src/Fyltrix/Preview/PreviewPane.cs` hook
- tests: `PdfCommentTests` (+ geometry), `PdfCommentSyncTests` / new `PdfAnnotationNavigationTests`, `NativePdfCompareTests` regression

## Regression risks

- The first UIA query on a large PDF builds the browser's accessibility tree (CPU and memory spike). Mitigation: only on explicit comment use; measure on a 300-page PDF.
- Accessibility being enabled may change native performance slightly; Compare already enables it.
- The highlight and popup HWNDs over WebView2 can interfere with focus or Z-order. Mitigation: no activation, click-through highlight, close on owner move/resize.
- Runtime updates may change the tree structure. Mitigation: structural detection plus aspect/count validation, falling back to page level automatically.
- Compare sync feedback when navigating. Mitigation: reuse `GoToPage`'s existing `PreferNativeInput` and `Synchronize` path.

## Testing strategy

- **Unit:** rect normalization (inverted, zero, NaN, huge), all rotations, CropBox offsets, QuadPoints union, stable ids (`/NM`, object reference, duplicates), popup-child deduplication, malformed and encrypted inputs.
- **Off-screen WebView2 integration:** page-region discovery and validation, vertical reveal for a page-1, a later-page and a near-bottom note, two notes on one page resolving to different positions, rotated and cropped pages, after zoom-in, cancellation by file change, fallback when regions are hidden (simulated), Compare left/right/swap/replace with sync on and off, and popup content and ownership.
- **Regression:** existing PDF, compare-scroll, native-input and preview suites, plus the full application suite.
- **On-screen (owner approval required):** a visible Preview and Compare with the probe PDF to verify horizontal highlight placement after zoom and resize, at 100%/150% scaling and on two monitors if available. Disposable copies only.

## Remaining evidence required

- Whether horizontal page bounds are correct in a visible window after zoom and resize (this decides whether a horizontal highlight is shown, or only a vertical band).
- Behavior on older WebView2 runtimes; only 157 is tested.

## Owner-supplied real document test (2026-11-15)

File: `PEC_ZZ00ABB0RC0_BPSA_it - RELU 1.pdf` (269,370 bytes). Tested from a **copy only**; the original's SHA-256 is `5778cde6f407779235d9a5bd0213becceba6611bb8165dd7851e75f5a7045447` before and after. Probe: `RealPdfAnnotationProbe.cs` (session scratchpad, not in the repo). Off-screen preview, isolated profile.

| Item | Result |
|---|---|
| Pages | 3 × A4 (595.3 × 841.9 pt), no `/Rotate`, no CropBox offset |
| Annotations | 290: StrikeOut 48, Caret 48, Text (sticky) 47, Popup 143, Link 4. All 143 non-Popup carry `/NM` ids; no invalid `/Rect`s; 48 have `/QuadPoints` |
| Comment structure | 48 replace-text edits (Caret plus a StrikeOut grouped via `/IRT` + `/RT /Group`). 46 sticky notes are **replies** (`/IRT` → Caret), mostly "done". 1 stand-alone note. No `/State` review statuses |
| Current extraction | `PdfService.ReadComments`: 95 flat comments in 73 ms (Caret 48, Sticky note 47); about half are "done" replies without their parent |
| Viewer page regions | 3/3 exposed 2.5 s after load; all pass the 2% aspect check |
| Annotation nodes exposed | None. The 11 "graphic" nodes are page images (logo/banner at the same place on pages 1 and 3), 4 are links. Sticky notes, carets and strikeouts have **no** accessibility node |
| Reveal (scroll to annotation) | **28/28** brought fully into view: top, middle and bottom comment of every page plus extras, the whole set run twice. Centred to 0 px except where the document's top or bottom edge prevents centring (197–241 px). Median 548 ms, max 901 ms per reveal (including bounded settle polling) |

Conclusions for this file:
- Exact vertical navigation and in-view confirmation work.
- Bounds are self-consistent (they track scroll exactly). Independent on-screen pixel confirmation and horizontal accuracy after zoom/resize remain unverified, as above.
- Native icon clicks still can't be detected.
- The data model must **thread replies** (`/IRT`) under their parent and treat a replace-text group (Caret + StrikeOut) as one item. Otherwise the list and popup are dominated by "done" replies.

## Stage 2 — implemented (2026-11-15)

The comment model, reply/group threading, page geometry, the pure mapping helper, the comment card and the Preview Comments button are implemented. Details and test evidence: `docs/PROJECT_PROGRESS.md` ("PDF annotations Stage 2"). Stage 3 (exact reveal, highlight and card beside the annotation) still needs owner approval.
