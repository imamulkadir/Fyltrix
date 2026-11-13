# Preview Reliability Investigation

Status: **Phase 1 complete (investigation only). No production source changed. Awaiting owner approval of the fix plan.**
Date: 2026-11-13. Repository: `master` @ `6a479d9`, latest published release `v0.1.10`.

## Executive Summary

A user installed `v0.1.10` on another PC. Previews worked at first, then stopped appearing in the Preview pane. Compare also failed. The portable ZIP showed the same problem.

The **trigger on that PC is not confirmed**. We have no logs from it yet. What is **confirmed**, by reproduction on this machine, is that Fyltrix has **no recovery** from WebView2 engine failures. Two defects in `PreviewPane` turn a single engine failure into a preview that stays broken until restart:

1. **Browser-process death is never recovered (Confirmed, reproduced).** `ProcessFailed` only posts a status message. After the WebView2 browser process exits, the pane still reports itself as ready. Every later file selection throws `InvalidOperationException: The WebView control is no longer valid because the browser process crashed`. In production, that exception goes to the "something went wrong" dialog and `error.log`, and the Preview pane never recovers.
2. **One failed initialisation disables every preview in the process (Confirmed by fault injection).** The static `_envTask` caches a faulted `Task` forever. `_failed` is also sticky per pane. One transient environment or controller failure therefore breaks the Preview pane, and every Compare, Quick View or Duplicates pane opened later fails with "The preview engine couldn't start" until Fyltrix restarts.

A third weakness is **Confirmed**. A hung page renderer leaves its pane stuck permanently. No `ProcessFailed` fires without user input, and later navigations in that pane do not complete.

Packaging was checked: the published ZIP contains the WebView2 loader and wrappers, and the viewer assets are embedded in `Fyltrix.dll`. **Packaging is not the cause.** The installer and the ZIP run the same binaries. They also share the same WebView2 profile folder and the same system WebView2 Runtime, which is why both behave the same on one machine.

Separate security finding: the "private" preview origin `https://filelens.app` is a **real, publicly resolving domain owned by a third party**. Today every request is intercepted, but any request that ever escapes interception would go to that server with a local path in the URL.

## Scope

Inspected: `PreviewPane.cs`, `PreviewServer.cs`, `PreviewApi.cs`, `PreviewUrls.cs`, `CompareWindow.cs`, `NativePdfAccess.cs` (threading), `App.cs` (exception handling), `Diagnostics.cs`, `MainWindow` preview lifecycle (`SetPreviewVisible`, `ReleaseEngine` timer, `Closed`), `Fyltrix.csproj` (resource embedding, WebView2 SDK 1.0.3351.48), `tools/Publish-Layout.ps1`, the published `v0.1.10` ZIP, and the existing preview tests. `PdfService`, `ShellPreviewHost` and `viewer.js` were reviewed only where they relate to the failure paths.

## Preview Architecture

```text
                 Process-wide (static)                                  Per machine / per user
  ┌───────────────────────────────────────────┐        ┌──────────────────────────────────────────────┐
  │ PreviewPane._envTask  (cached forever)    │──────▶ │ WebView2 Runtime (Evergreen, system-wide)    │
  │ PreviewPane.WebDataFolder                 │──────▶ │ %LOCALAPPDATA%\FileLens\WebView2 (profile)   │
  │ PreviewServer.Resources (embedded viewer) │        │  shared by installed AND portable Fyltrix    │
  └───────────────────────────────────────────┘        │  ONE browser process per profile+options     │
          │ one environment ⇒ one browser process       └──────────────────────────────────────────────┘
  ┌───────┴───────────────┬───────────────────────┬────────────────────┬──────────────────┐
  MainWindow._preview     CompareWindow left/right  CompareWindow diff   QuickView          Duplicates
  (PreviewPane, per window) (nativePdfInput:true)   (PreviewPane)        (PreviewPane)      (PreviewPane)
     │ each pane: own WebView2 control + CoreWebView2 controller + own renderer (normally)
     │ PreviewServer.Attach(core): WebResourceRequested filter on https://filelens.app/*
     ▼
  /viewer/* → embedded HTML/JS/CSS   /fs/* → local file (Range: 4 MB chunks)   /api/* → PreviewApi (worker thread)
```

**The "preview server" is not a socket.** No port, listener or loopback binding exists. Requests are answered in-process through `CoreWebView2.WebResourceRequested` with a deferral. Disk work runs on the thread pool, and the response is created on the UI thread. This rules out the "server stopped / stale port" hypothesis by construction.

The lifecycle for one file:

1. `Show(path)` is debounced 90 ms, then `Load()`.
2. `Load()` picks the kind and calls `Navigate()`.
3. If the pane isn't ready, the URL is held as `_pendingUrl` and `InitAsync()` runs.
4. `InitAsync()` reuses `_envTask`, calls `EnsureCoreWebView2Async`, then `Attach`, sets up handlers, and replays the pending URL.
5. If the pane is ready, `CoreWebView2.Navigate(url)` is called directly.

`ReleaseEngine()` disposes the control and creates a new one. It runs 30 s after the main Preview pane is hidden, and in Compare whenever a pane switches between PDF and non-PDF, because the host-input option differs. `DisposeWeb()` runs on window close.

| Component | Scope |
|---|---|
| `_envTask`, `WebDataFolder`, viewer resource map | process-static |
| WebView2 browser process | per profile folder + options: shared by every pane, and by every Fyltrix process (installed or portable) using the same profile |
| WebView2 control/controller, `_ready`/`_failed`/`_initializing`, handlers | per pane |
| Renderer process | normally per pane; Chromium may share one under process-limit pressure |
| `NativePdfAccess` (UIA) | per Compare window, on its own thread |

## Confirmed Observations

Evidence comes from temporary offscreen probes in an isolated profile. Only WebView2 processes started by the test host itself were terminated. Raw output: `probe-results-all.txt` in the session scratchpad. The probe source was kept out of the repository.

| # | Scenario | Result |
|---|---|---|
| P1 | 100 sequential previews (txt/json/md/cs), one pane | **100/100** OK, 13.9 s total (~139 ms each), WebView2 processes 5 → 5, managed working set 154 MB. No degradation or leak on this machine. |
| P2 | Kill the browser process after a successful preview | `ProcessFailed: BrowserProcessExited`, `BrowserProcessExited kind=Failed`. Pane still `ready=True`. Next selection: **`InvalidOperationException` from `PreviewPane.Navigate` (line 463)**, raised as a dispatcher-unhandled exception. Retry fails identically. `Clear()` also throws. **A new pane on the cached environment works.** A fresh environment on the same profile also works. |
| P3 | Kill the renderer | `RenderProcessExited`. Pane is blank until the next selection, and the next selection **recovers**. |
| P4 | `ReleaseEngine()` (pane hidden > 30 s) | Browser exits gracefully after ~6 s. Re-show with the cached environment **works**. |
| P5 | Inject one failed environment task | Pane `failed=True`, "The preview engine couldn't start". **A new pane in the same process also fails.** After the cause is removed, the original pane **still fails** because `_failed` is sticky. **Nothing is written to `error.log`.** |
| P6 | Hang the renderer (`for(;;){}`) | Same pane: the next preview **never appears** (20 s), and **no `ProcessFailed`** fires. A new pane works. |
| — | Existing `PreviewUiTests`, `CompareScrollTests`, `NativePdfCompareTests`, `NativePdfInputTests`, `MediaUiTests` | **45/45 passed** on this machine. |
| — | Published `Fyltrix-0.1.10-win-x64.zip` | SHA-256 matches the published `.sha256`. Contains `WebView2Loader.dll` (root and `runtimes/win-x64/native`), the Core/Wpf/WinForms wrappers, and all 12 viewer resources embedded in `Fyltrix.dll`. |
| — | Dev PC | WebView2 Runtime 157.0.4341.46. No `%AppData%\FileLens\error.log` and no crash reports. |
| — | `filelens.app` DNS | Resolves publicly (Cloudflare A/AAAA). |

Diagnostic gaps found in the source:

- `ProcessFailed` is not logged (no kind, reason, exit code or process).
- `BrowserProcessExited` is not observed.
- `InitAsync` failures are shown but not logged.
- Navigation failures (`WebErrorStatus`) are not logged.
- The WebView2 Runtime version is never recorded.

## Hypotheses

| Hypothesis | Evidence / reproduction | Conclusion | Confidence |
|---|---|---|---|
| H1 Browser-process death leaves the main Preview permanently broken | P2 | **Confirmed mechanism** (trigger on the friend's PC unconfirmed) | High for the mechanism |
| H2 One transient init failure disables all previews (Preview + Compare) until restart | P5 + source (`_envTask ??=` never reset; `_failed` never cleared) | **Confirmed mechanism** (fault injected) | High for the mechanism |
| H3 Hung renderer freezes a pane | P6 | **Confirmed** for that pane; does not explain Compare on its own | High |
| H4 Local preview server stops or uses stale ports | Not a socket server (source) | **Rejected** | High |
| H5 Missing packaged assets / dev-environment leakage | ZIP inspection; resources embedded; no absolute paths in preview code | **Rejected** | High |
| H6 Resource leak or degradation over repeated use | P1: 100 previews, flat process count | **Rejected on this machine**; low-RAM machines are untested | Medium |
| H7 Renderer crash permanently breaks preview | P3 recovers on the next selection | **Rejected** (it causes a blank pane until reselection) | High |
| H8 Environment-specific trigger on the friend's PC (WebView2 Runtime version, GPU driver, low RAM, security software) kills the browser or blocks init, possibly repeatedly | No data from that PC | **Unresolved** | — |
| H9 Requests escape interception and reach the real `filelens.app` | Domain is real; no escape observed on runtime 157 | **Unresolved risk**; security finding | Low as the cause |
| H10 `NativePdfAccess` UIA blocks Compare | Runs on a dedicated thread with exception guards | **Not supported** | Medium |

## Root Cause

**Classification: Strongly supported (mechanism confirmed; trigger on the affected PC inconclusive).**

- **Defective component:** `src/Fyltrix/Preview/PreviewPane.cs`, the engine lifecycle.
- **Mechanism A (H1):**
  1. When the WebView2 browser process exits, the WPF control becomes permanently invalid.
  2. `PreviewPane` handles `ProcessFailed` only with a status message.
  3. `_ready` stays `true`, so `Navigate`/`ShowInfo`/`ShowNative` dereference `_web.CoreWebView2` and throw.
  4. Nothing replaces the dead control. The pane is dead until Fyltrix restarts.
- **Mechanism B (H2):**
  1. `GetEnvironment()` caches the first `CreateAsync` task, including a faulted one.
  2. `InitAsync` sets `_failed = true` permanently.
  3. One transient failure (for example a busy or locked profile, a runtime update mid-session, or a security-software interruption) therefore disables Preview *and* every Compare pane created later.
  4. Nothing is logged.
- **Why recovery doesn't happen:** there is no recovery path. No pane is recreated, the environment is never invalidated, and failure is never cleared.
- **Why Compare can fail too:** Compare uses the same `PreviewPane` class and the same cached environment and browser process. Mechanism B fails Compare directly. Mechanism A kills every open pane at once, including Compare windows that are open at the time. A new Compare after a single crash works (P2), so a failing Compare implies either B or a **recurring** browser failure on that machine.
- **Why another machine:** the trigger is environmental. The WebView2 Runtime is system-wide and evergreen, so the version differs per PC. GPU drivers, memory and endpoint security also differ. Fyltrix's lack of recovery turns any such failure into a permanent loss of preview.

## Why It Works on the Development PC

The trigger does not occur here. Over 100 consecutive previews and 45 UI tests, there were no browser failures, no init failures and no process growth. The runtime is current (157.0.4341.46). There is no `error.log`. This is evidence that the dev PC does not hit the trigger. It is **not** evidence that the code is resilient. P2 and P5 show the code would fail the same way here if the trigger occurred.

## Why It Fails on Another PC

**Unknown trigger.** These candidates need that PC's data:

- WebView2 Runtime version or a mid-session runtime update
- GPU or driver crashes escalating to the browser process
- memory pressure
- security software terminating or blocking `msedgewebview2.exe`
- profile folder permission or lock problems

Whichever it is, the confirmed mechanisms explain why preview **stays** broken.

## Installer and Portable Comparison

Both run identical `app-<version>\Fyltrix.exe/.dll` binaries. Both use the same system WebView2 Runtime. Both use the **same** profile folder `%LOCALAPPDATA%\FileLens\WebView2` (`WebDataFolder` is not distribution-specific), so a ZIP copy running beside the installed copy also shares the **same browser process**. An environmental trigger and the missing recovery are therefore common to both distributions. The only differences found are the install location and launcher, and neither touches preview.

## Proposed Fix

All production changes are in `src/Fyltrix/Preview/PreviewPane.cs` unless noted.

1. **Detect a dead engine and recover it (bounded).**
   - Handle `ProcessFailed` by kind:
     - `BrowserProcessExited`: mark the pane not ready, bump `_engine`, replace and dispose the control (reusing the `ReleaseEngine` path), then re-show `_currentPath`.
     - `RenderProcessExited` / `FrameRenderProcessExited`: re-navigate to the current URL.
     - `RenderProcessUnresponsive`: recreate the pane's controller.
     - GPU/utility kinds: log only.
   - Automatic recoveries are bounded (for example 3 within 60 s). After that, show a clear message: "The preview engine stopped repeatedly" + the cause category + a **Retry** button.
2. **Never dereference an invalid control.** A single `TryCore()` guard is used by `Navigate`, `ShowInfo`, `ShowNative`, `FullScreenEnded` and the scroll helpers. If the control is invalid, it schedules recovery instead of throwing into the dispatcher. That stops the error dialogs.
3. **No sticky failure.**
   - `GetEnvironment()` discards a faulted or cancelled cached task, so the next attempt creates a fresh environment on the same profile. P2 shows this works.
   - `_failed` becomes a retryable state: the next file selection or the Retry button re-initialises, with bounded attempts.
   - "Runtime not found" keeps its existing message.
4. **Hung-renderer watchdog.**
   - If an issued navigation receives no `NavigationStarting`/`NavigationCompleted` within a bounded time (for example 15 s, tied to that navigation, not a sleep), recreate this pane's controller once.
   - If that also fails, show the retryable message.
   - PDF/media streaming is unaffected because it continues after `NavigationCompleted`.
5. **Diagnostics without document data** (`Diagnostics.Info`/`Log`; file *extension and kind only*, no paths or content):
   - runtime version, once per session (`env.BrowserVersionString`)
   - `ProcessFailed` kind/reason/exit code/process description
   - `BrowserProcessExited` kind
   - init failure type/HRESULT
   - failed navigations (`WebErrorStatus`)
   - recovery attempts and outcomes
6. **Compare** (`CompareWindow.cs`): no new logic expected. `NativePdfLoaded` → `RefreshPdfAccess` already reconnects when the control handle changes. This will be verified, with a minimal reconnect hook only if a test shows it's needed.
7. **Tests:** new `tests/Fyltrix.Tests/PreviewRecoveryTests.cs`, offscreen with an isolated profile, terminating only test-owned WebView2 processes. Cases:
   - browser-process kill → the same pane recovers and shows the next file without unhandled exceptions
   - renderer kill → the current file re-renders
   - hung renderer → the watchdog recovers
   - injected environment failure → error shown, then Retry/the next selection succeeds, and new panes succeed
   - bounded recovery stops after N attempts and shows the message
   - Compare left/right recover after a browser kill, and PDF access reconnects
   - `ReleaseEngine`/reshow is unchanged
   - 100-preview regression with flat process count

   Then the existing preview suites and the full application suite.

**Not proposed in this fix (needs a separate owner decision):** moving the preview origin off `filelens.app` to a reserved, non-resolvable host. It is pinned by `RebrandingTests` as a compatibility identifier. AGENTS.md §13 requires a reviewed migration. No origin-scoped storage was found, so the migration risk looks low.

## Alternatives Considered

| Alternative | Why not preferred |
|---|---|
| Show the "engine restarted" message only | Current behaviour. Does not recover. |
| Recreate the environment on every failure | P2 shows the cached environment survives a crash. Recreating needlessly could start a second browser on the same profile with mismatched options. |
| Switch PDFs to bitmap rendering | Forbidden. Loses native PDF features and doesn't address the engine lifecycle. |
| Per-pane profile folders | More processes and memory. Breaks the shared-engine efficiency. Doesn't fix the lack of recovery. |
| Kill `msedgewebview2` processes | Unsafe. It may be shared with another Fyltrix instance. |
| Fixed delays / infinite retries | Disallowed. They hide rather than fix. |

## Regression Risks

- Recovery re-navigation could fire during an intentional `ReleaseEngine` or disposal. Mitigation: the `_engine` generation guard and the disposed flag.
- Compare native PDF accessibility sync must reconnect to the new control handle.
- Media/native player and shell-handler panes must not be disturbed. Recovery affects only the WebView surface.
- Watchdog false positives on very slow machines. Mitigation: a generous bound and one retry before giving up.
- Fewer error dialogs is the intended behaviour, and the errors will appear in `error.log` instead.

## Verification Plan

1. **Automated:** new recovery tests, existing preview suites (45 now), full `Fyltrix.Tests` suite, then updater/packaging suites as regression checks (unchanged subsystems).
2. **Runtime:** before/after timings with the P1 harness (first preview, 100 previews, process count). Compare open/close ×20.
3. **Packaging:** isolated `Publish-Layout` build (no `publish.ps1 -Run`). Confirm loader, wrappers and embedded viewer.
4. **Cross-machine:** clean Windows Sandbox / fresh-profile runs only if available and approved. **Friend's PC:** the opt-in procedure below, before and after the fix.

## Release Implications

No new dependencies and no packaging change are expected. The WebView2 SDK stays at 1.0.3351.48, and all APIs used already exist in it. A patch release (next candidate `0.1.11`) would be prepared only after separate approval.

## Remaining Evidence Required

Owner-mediated and opt-in. Nothing is uploaded automatically. From the affected PC:

1. `%AppData%\FileLens\error.log` (and `error.old.log`, `crashes\*.txt`). These contain timestamps, Fyltrix version, exception types and stack traces, and may contain previewed file paths, so review or redact before sharing. With Mechanism A, entries read `UI · InvalidOperationException: The WebView control is no longer valid because the browser process crashed`.
2. What the user saw: a "something went wrong" dialog (A), "The preview engine couldn't start" (B), the "preview engine restarted" status text, or simply a blank pane (hung renderer).
3. WebView2 Runtime version: `reg query "HKLM\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" /v pv` (or the HKCU equivalent).
4. Windows version/build, RAM, GPU model.
5. Event Viewer → Windows Logs → Application: *Application Error*/*Windows Error Reporting* entries naming `msedgewebview2.exe` or `Fyltrix.exe` around the failure time (faulting module and exception code only).
6. Whether installed and portable Fyltrix ran at the same time, and whether any third-party antivirus is installed.

---

## Phase 2 — Implementation (2026-11-13)

Status: **Implemented and verified on the development PC. Cross-machine acceptance (affected PC, clean environments) is pending.**

The owner approved the fix and asked that the product be **Fyltrix everywhere, not FileLens**. Applied in this stage:

### Preview engine recovery (`src/Fyltrix/Preview/PreviewPane.cs`)

- **Lost engine is replaced, not reused.**
  - `ProcessFailed` with `BrowserProcessExited`, `RenderProcessExited` or `RenderProcessUnresponsive` replaces the pane's WebView2 control, outside the failing control's own event. The file that was showing is then navigated to again.
  - GPU/utility failures are logged only, because the runtime restarts those processes itself.
- **No direct access to a dead control.**
  - `TryCore()` is used by `Navigate`, `ShowInfo`/`ShowNative` (`BlankWeb`) and `FullScreenEnded`. It treats "The WebView control is no longer valid…" as a lost engine instead of letting it reach the error dialog.
- **Hang watchdog.**
  - Every preview navigation, and every engine start that has a preview waiting, arms a bounded timer (`NavigationTimeout`, 30 s).
  - The timer is disarmed when the new page is committed (`ContentLoading`) or when the navigation completes or fails.
  - Waiting for `ContentLoading` rather than full page load means an HTML file whose remote images are slow is not mistaken for a hang.
- **Bounded recovery.**
  - At most `MaxRecoveries` (3) automatic recoveries per `RecoveryWindow` (60 s) per pane.
  - After that, the pane shows "The preview stopped working" with a **Retry** button. Retry resets the allowance.
- **No sticky failure.**
  - `GetEnvironment()` creates the shared environment again if the cached task faulted or was cancelled.
  - A failed start replaces the control and shows a retryable message (the "Runtime not found" text now says to click Retry instead of restarting).
  - Selecting another file, or starting a text diff, retries.
- **Diagnostics** go to `error.log` as `Preview engine` entries. They record the failure kind, reason, exit code and process description, page-load `WebErrorStatus`, recovery events, start exceptions, and the WebView2 Runtime version. Only the **file kind** is recorded, never paths or contents. Nothing is written on a healthy run.
- Disposal (`DisposeWeb`) blocks any further recovery. Compare needed no change: `NativePdfLoaded` → `RefreshPdfAccess` re-attaches native PDF access to the replaced controls, and the test below proves it.

### Fyltrix-branded preview origin (`src/Fyltrix/Core/PreviewUrls.cs`)

- The preview host changed from `filelens.app` (a real third-party domain) to **`fyltrix.example`**. That name is under the RFC 2606 reserved TLD, so it can never resolve on a network.
- No origin-scoped storage existed, so nothing needed migrating. `RebrandingTests` now asserts the new host and that the origin contains no "filelens".

### Legacy `FileLens` identifiers intentionally NOT renamed in this stage

These hold user data or are part of the install, update and rollback contract (AGENTS.md §13). Renaming them without a migration would lose existing users' settings or break update rollback, so they need a separate, approved migration stage:

- `%AppData%\FileLens` (settings, tags, automation, templates, shares, sync state, `error.log`) and `%LocalAppData%\FileLens` (WebView2 profile, thumbnails, archive cache)
- installer data-dir handling (`packaging/installer/Fyltrix.iss`)
- update profile paths and the `FileLens.Profile/1` capability (`Fyltrix.Updates`, `Fyltrix.Launcher`, `tools/Publish-Layout.ps1`)
- legacy `FileLens` process names in update shutdown
- the firewall rule `FileLens file sharing`
- `app.manifest` assembly identity `FileLens.app`

### Tests (`tests/Fyltrix.Tests/PreviewRecoveryTests.cs`, new)

All tests run offscreen with the test host's isolated profile. Before killing any process, each test asserts that the profile is under `%TEMP%`.

| Test | Proves |
|---|---|
| `Browser_process_crash_is_recovered_in_the_same_pane` | After the browser process is killed, the current file comes back by itself, the next file previews, and **0** unhandled dispatcher exceptions occur |
| `Renderer_crash_shows_the_current_file_again` | A renderer crash re-shows the file without the user reselecting it |
| `Hung_preview_page_is_recovered_by_the_watchdog` | A hung page (`for(;;){}`) is replaced and the selected file shows |
| `Failed_engine_start_is_logged_shown_and_retried` | One environment failure is logged, a new pane in the same process works, and selecting a file in the failed pane recovers it |
| `Repeated_crashes_stop_automatic_recovery_and_offer_retry` | 3 automatic recoveries, then a failure message, a log entry, and Retry works |
| `Compare_pdf_panes_recover_after_browser_crash` | Both Compare PDF sides recover and native PDF access re-attaches to the new controls |

### Verification

- **New tests:** 6/6 in three separate test-host runs.
- **Preview-related suites** (`PreviewRecoveryTests`, `PreviewUiTests`, `CompareScrollTests`, `NativePdfCompareTests`, `NativePdfInputTests`, `MediaUiTests`, `RebrandingTests`): **61/61** on the final build.
- **Full application suite:** **597/597** on the final build (also 597/597 before the final `ContentLoading` refinement).
- **Probes rerun after the fix** (temporary, not in the repo):
  - P2 browser kill: same pane recovers, 0 unhandled exceptions. Before the fix it was permanently dead.
  - P3 renderer kill: recovers.
- **Performance:** 100 sequential previews, interleaved against an untouched `HEAD` export:

  | | Run 1 | Run 2 | Run 3 | Median |
  |---|---|---|---|---|
  | Before | 17.8 s | 14.8 s | 14.2 s | 14.8 s |
  | After | 19.2 s | 14.9 s | 14.2 s | 14.9 s |

  WebView2 processes stay at 5 after 100 previews, and managed working set is 150–156 MB on both sides. **No measurable regression.**

### Remaining risks and unverified behaviour

- **The trigger on the affected PC is still unknown.** The fix makes Fyltrix recover from it and log it, but if the trigger recurs constantly, the user will see the Retry message instead of a preview. The new log entries will then identify the cause.
- **Cross-machine acceptance pending:** affected PC, clean Windows 10/11, fresh profile.
- **Not covered by automated tests:**
  - an HTML preview whose page commits but whose own script then hangs (recovered at the next selection, not immediately)
  - the watchdog with a remote-image HTML file on a slow network
  - real GPU-driver or antivirus-induced failures
- Updater, packaging and installer suites were not rerun, because those subsystems are unchanged.
