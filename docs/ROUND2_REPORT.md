# FileLens — round 2: bug fixes, stability and UI

Date: 2026-10-29. Brief: MKV crash, LAN sharing and share sessions, search bar and recursive search, thumbnail grid, logo, central diagnostics.

## 1. Bug investigation

### MKV preview crash (critical)

- **Evidence.** Windows Error Reporting logged four `FileLens.exe` crashes today. All had exception code `0xE0000008` at the same offset in `KERNELBASE.dll`, and the .NET event log showed `SEHException` in `DispatchMessage`. `0xE0000008` is the code Chromium raises when it runs out of memory. WebView2 runs part of Chromium inside the FileLens process, so that failure ends FileLens.
- **Reproduced.** Previewing a generated 3.7 GB 1080p MKV crashed the off-screen test host within about 2 seconds, with the same code at the same address. Memory reached about 1 GB first.
- **Root cause.** `Preview/PreviewServer.cs` answered every request for a file with the whole file and ignored HTTP `Range`. Media players ask for byte ranges, and an MKV player also jumps to the end of the file to read its index. WebView2 copies a custom response into memory, so a multi-GB answer exhausted it.
- **FFmpeg isn't involved.** Previews never use FFmpeg. WebView2 (Chromium codecs) plays MKV, with Windows Media Foundation as the fallback.
- **Fix in `PreviewServer`:**
  - Range requests get `206 Partial Content` in 4 MB chunks, plus `Accept-Ranges`.
  - Bad ranges get `416`.
  - `HEAD` requests no longer return a body. The viewer's size check used to download the whole file.
- **Fixes for related defects found while testing:**
  - **Black picture.** MKVs with a video codec Chromium can't show (MPEG-4 Part 2/Xvid) played sound over a black 0×0 picture and raised no error. The viewer now detects this and falls back to the Windows player (`viewer.js`).
  - **Endless "Opening…".** For some damaged files, Media Foundation never reports success or failure. The Windows player now gives up after 15 seconds and shows a message.
  - **Failure screen.** Empty files get their own message, MF's misleading "Cannot find the media file" is reworded, and there's an *Open in default player* button.
  - **Stale fallback.** A late "can't play" message from the previous file could switch the new file to the Windows player. Messages now carry their file URL.
- **Verified:**
  - The 3.7 GB MKV opens at 1920×1080 and plays from 8:00 onwards with memory steady at about 240 MB.
  - Regression sweep, all with no crash:
    - Played in the web player: H.264, HEVC, VP9/Opus, H.264 10-bit and H.264+AC3 in MKV, plus MP4, MOV and WebM.
    - Played in the Windows player: MPEG-4 Part 2 MKV and AVI.
    - MP3 plays.
    - Empty and garbage MKVs show a message; a truncated MKV plays what it can.
  - Tests: `MediaUiTests` (MKV play/seek, fallback, damaged files with rapid switching, 5 GB range test) and `DiagnosticsTests` (range parsing).

### LAN sharing

- **Root causes on this PC:**
  1. **Wrong address in the link.** It used the first adapter Windows lists. Here that is the WSL virtual adapter (`172.20.192.1`), so the link and QR code pointed at an address phones and other PCs can't reach. The real adapters are Ethernet `192.168.0.226` and Wi-Fi `192.168.0.144`.
  2. **New random port every time**, so no firewall rule could target it.
  3. **No firewall rule** existed for FileLens.
  4. **The window owned the share.** Closing it stopped sharing, and nothing remembered active shares.
- **Firewall on this PC.** The Ethernet network is on the **Public** firewall profile, inbound connections are blocked by default, and Intune sets `Mdm\PublicProfile\AllowLocalPolicyMerge=0`. That means Windows ignores any allow rule created on this PC for that profile. Other devices therefore can't connect here until the network is marked Private or IT allows it, and FileLens now says so in the Share window.
- **Fix: one share host (`Core/Sharing/ShareHost.cs`):**
  - **Port and links.** One server on a fixed port (8765, then 8766–8774, then any free port). Links look like `http://<LAN IP>:8765/share/<token>/`.
  - **Network exposure.** It listens only while something is shared. It binds to the network only while a "local network" share is active, and refuses non-private addresses.
- **Fix: share sessions:**
  - **Identity.** Each shared item is identified by its NTFS file ID, so a renamed or moved file (same drive) is still recognized and served. Files with the same name are told apart.
  - **Already shared.** Sharing an item again shows the existing share: status, link, time left, end time (original and extended), Copy / Extend / Stop.
  - **Closing the window doesn't stop sharing.** *Tools → Active shares…* lists every share.
  - **Expiry.** Expiry is checked every second. It invalidates the token, cuts off downloads in progress, and releases the port when nothing is left.
  - **Persistence.** Shares are saved to `shares.json`, with the token encrypted by DPAPI for the current Windows user. After a restart they show as *Paused* and are never served automatically. *Resume* restarts the server and only counts the share as active once its own link answers. Expired shares never come back.
- **Fix: closing FileLens with active shares** asks one question:
  - **Keep sharing in the background:** the window hides and a notification-area icon offers *Active shares*, *Open FileLens* and *Stop sharing and exit*. FileLens exits on its own when the last share ends.
  - **Stop sharing and exit.**
  - **Cancel.**
- **Fix: addresses and firewall (`ShareNetwork.cs`):**
  - **Address ranking.** Real adapters with a gateway come first. Virtual, VPN and Bluetooth adapters and 169.254 addresses come last. The window lets you pick another address and refreshes when the network changes.
  - **Read-only firewall check, no admin needed.** It looks at the current profile, the default inbound action, local allow rules for FileLens.exe or the port, and Group Policy/Intune "ignore local rules".
  - **Optional allow rule.** With UAC consent it adds one narrow rule: inbound TCP, the share port only, FileLens.exe only, local subnet only, Private/Domain profiles only (never Public). FileLens never turns the firewall off and never opens ports on the router.
- **Verified:**
  - Tests cover:
    - Serving and traversal attacks.
    - Ranges and ZIP downloads.
    - Expiry, plus stop during a 512 MB download.
    - Rename recognition and extend.
    - Restart, resume and expired shares.
    - Address ranking and firewall rule arguments.
    - The UI flow: share for 15 minutes, close, reopen shows the same link with 14 min left, Shares list, stop invalidates the link.
    - Closing the main window: keep (served while hidden, exits when the share ends) and stop.
  - Opt-in network check (`FL_LAN_CHECK=1`, run once): link `http://192.168.0.226:8765/share/…` fetched through the LAN address, and a "this PC only" share refused from the LAN address.

### Search bar gap and "search only looks in this folder"

- **Root cause.** Recursive search already existed, but only on Enter, and nothing on screen said so. The box was a bordered TextBox with an icon laid over it, so the icon and the text didn't read as one field.
- **Fix (`UI/MainWindow.Search.cs`):**
  - **One field.** A single rounded field holds the icon, a borderless text box, a clear/stop button and a scope switch. Spacing is even at any DPI, and the border lights up on focus.
  - **Scope switch.** *Subfolders* (default: Enter searches all subfolders, Ctrl+Enter opens results in a new tab) or *This folder* (filter only).
  - **Clearer hint and status**, for example "Search Projects and subfolders".
  - **Stop button** while a search runs.
  - **Unchanged:** the quick filter as you type, and the existing search engine. The engine runs in the background, can be cancelled, shows results as they arrive, skips folders it can't open and junctions/links (no loops), caps at 10,000 results, and matches files and folders. Results keep the Folder column and the usual context menu, including *Open file location*.
- **Verified (`SearchUiTests`), with the brief's `Projects` example:**
  - `logo`, `App`, a file three folders down, the folder `ProjectB` and `*.md` are all found.
  - A folder result opens and *Open file location* works.
  - *This folder* scope and Ctrl+Enter behave as described.
  - A 12,000-file search stops at once and the UI thread stays responsive.

### Thumbnail grid

- **Was missing:** only a Details view existed.
- **New:**
  - **View button and keys.** Details, or thumbnails at 48/96/160/256 px. Ctrl+Shift+1–4 and Ctrl+Shift+6 work as in Explorer, Ctrl+wheel changes the size, and the choice is saved for all tabs (`ViewTileSize`).
  - **Same list.** The same ListView is used, so selection, Ctrl/Shift-click, drag and drop, context menus, double-click and inline rename work unchanged.
  - **Speed.** A new `VirtualizingWrapPanel` creates only the visible rows and recycles them. Arrow keys, Home/End and PageUp/PageDown move through the grid.
  - **Thumbnails.** `ThumbnailService` gets them from Windows (photos, video frames, PDF/Office pages, large icons), from the visible tiles only, newest first, cancelled when a tile scrolls away.
  - **Caching.** A memory cache of up to 192 MB is keyed by path, size, last-write time and length, so a changed file gets a new thumbnail and revisiting a folder costs nothing. Windows keeps its own disk cache. FFmpeg makes frames for videos Windows can't thumbnail, cached in `%LocalAppData%\FileLens\Thumbs` (max 256 MB, oldest removed first).
- **Verified (`ThumbnailGridTests`):**
  - Real thumbnails: a 2:1 image comes back 2:1, and the MKV shows a 16:9 frame.
  - Switching views keeps the selection, and revisiting a folder hits the cache.
  - With 5,000 files, at most 400 tiles exist at any time, End reaches the last file in under 2 s, and scrolling to the top stays bounded.
  - Cache eviction and invalidation, cancelled requests, and FFmpeg frames with the disk cache and its cleanup.

### Follow-up: upside-down and boxed icons in the grid

Reported with screenshots after the first build of the grid: PDF, text and folder icons were upside down, and some folders sat on a white or black square.

- **Cause 1: rows read in the wrong order.** `ShellThumbnails` copied the bitmap's raw rows and assumed they run top to bottom. Probing showed Windows returns some bitmaps top-down and others bottom-up, even for the same file (fresh vs from its thumbnail cache, or a different size). The header Windows reports looks the same either way.
  - **Fix:** copy through `GetDIBits` asking for top-down rows. GDI knows the real row order, so the copy is always the right way up. This also removed a fallback (`CreateBitmapSourceFromHBitmap`) that dropped transparency.
- **Cause 2: two kinds of bad backgrounds on some folder previews:**
  - **Straight alpha.** The preview uses straight (non-premultiplied) alpha: transparent pixels still hold white colour values. It is now detected (a colour value greater than its alpha) and drawn as straight alpha.
  - **Flattened.** The preview was flattened onto an opaque black square. For a folder with four opaque corners, FileLens now uses the folder icon instead.
- **Verified:**
  - A new test checks a photo and the folder icon at 48–256 px, twice each, for orientation and a transparent background.
  - Your `Desktop\Ticket` folder, rendered off-screen (read-only), shows every icon correctly at 160 and 48 px.

### Logo

- **Design.** An original logo: a yellow folder with a blue magnifying lens, no text, readable at 16 px and on light and dark backgrounds.
- **Source.** `tools/LogoGen` draws it once and writes `src/FileLens/Assets/FileLens.svg` (source), `FileLens.ico` (16, 20, 24, 32, 40, 48, 64, 96, 128, 256) and PNGs at 16–1024 px.
- **Where it's used:**
  - The exe icon (`ApplicationIcon`): Explorer, taskbar pins and shortcuts.
  - The main window and tool windows: title bar, taskbar, Alt+Tab.
  - The notification-area icon.
  - A new *Tools → About FileLens* window.
- **No installer exists**, so there was nothing to brand there.
- **Verified.** In a release publish, the icon extracted from `FileLens.exe` matches `FileLens.ico` pixel for pixel at 32 px, and the window icon is set (test).

### Diagnostics

- **`Core/Diagnostics.cs`.** A single place that writes `%AppData%\FileLens\error.log` and rolls it over at 2 MB. Each entry has the time, version, component, operation, exception type, message, stack and inner exceptions. Share tokens are masked, and the logger never throws.
- **Crash reports** go to `crashes\crash-*.txt` (newest 20 kept) when the process is about to die.
- **`App` hooks:** `AppDomain.UnhandledException` (crash report), `TaskScheduler.UnobservedTaskException` (logged and observed) and the UI dispatcher (logged, with a friendly message).
- **Logged:** preview-server failures, media fallbacks and failures, sharing errors and thumbnail errors.
- **Not caught by design.** Native out-of-memory faults can't be caught in .NET. The fix is removing their cause, as done above.

## 2. Implementation summary

| Item | Classification |
|---|---|
| MKV crash | **Fixed** (root cause) |
| MKV preview behaviour, failure UI, Media Foundation timeout, 0×0 fallback | **Fixed / Enhanced** |
| FFmpeg discovery (PATH, WinGet packages and links, configured path) | **Already implemented**, reused for thumbnails |
| LAN sharing connectivity (address, port, binding) | **Fixed** |
| Firewall diagnosis + optional least-privilege rule | **Newly implemented** |
| Persistent share sessions, existing-share view, extend/stop/resume, Active shares | **Newly implemented** |
| Background sharing with notification-area icon | **Newly implemented** |
| Search bar layout | **Fixed** |
| Recursive search | **Already implemented**, **Enhanced** (scope, discoverability, stop, new-tab results) |
| Separate search-results window | **Partially completed**: results open in the current tab or a new tab (Ctrl+Enter); no separate window |
| Thumbnail grid, sizes, cache, virtualization, keyboard, persistence | **Newly implemented** |
| "List" view mode (Explorer's column-wrapped list) | **Not implemented** (optional in the brief) |
| Logo and integration | **Newly implemented** |
| Central diagnostics | **Newly implemented** |

## 3. Build and test results

- **Build:** `net8.0-windows10.0.19041.0`, x64, Debug and Release. 0 errors. One warning, `WFAC010` (high-DPI settings in `app.manifest`), predates this round.
- **Tests:** 218 xUnit tests (up from 185) in `tests/FileLens.Tests`, all passing. The new and rewritten ones are `MediaUiTests` (MKV), `DiagnosticsTests`, `ShareServerTests` (rewritten for the share host), `Phase5UiTests` (share window), `SharingLifecycleUiTests`, `SearchUiTests` and `ThumbnailGridTests`.
- **Opt-in checks:** `FL_LAN_CHECK=1` (passed once on this PC) and `FL_MEDIA=<folder>` (media sweep, passed).
- **Release publish** (self-contained win-x64, to a scratch folder so `artifacts\FileLens` wasn't touched): 203 MB, native DLLs present, exe icon verified.

## 4. Dependencies

No new packages or runtimes. New code uses only what was already there:
- Windows Firewall COM (`HNetCfg.FwPolicy2`), read-only.
- `netsh`, only when the user approves the UAC prompt.
- DPAPI (`ProtectedData`, part of the Windows Desktop runtime).
- WinForms `NotifyIcon` (WinForms was already referenced).
- Windows shell thumbnails.
- A user-installed FFmpeg, which stays optional.

The logo generator (`tools/LogoGen`) is a build-time tool and isn't shipped.

## 5. Remaining limitations and manual checks

- **Opening a link from another device is unverified.** This PC's network uses the Public firewall profile with Intune ignoring local rules, so other devices are blocked here whatever FileLens does. To verify, use a network marked Private where local rules apply. Open the share window and, if asked, approve *Allow through firewall…*, then open the link or QR code on a phone or PC on the same Wi-Fi. Guest Wi-Fi with client isolation, or a VPN on either device, can still block it, and the window says so.
- **Admin rights.** The firewall rule needs administrator approval (UAC). Your account isn't an administrator, so this needs an admin's credentials or IT.
- **Shares end with the process.** While FileLens is in the background (notification-area icon) shares keep running. Once the process exits they stop, and after a restart they show as *Paused* until resumed. No separate background service was added.
- **Downloads in progress** are cut off when a share is stopped or expires.
- **Thumbnails** depend on Windows' providers. HEVC/AV1 frames need the Windows video extensions or FFmpeg, and PDF/Office pages need a provider (Edge/Office usually install one).
- **The search results window is the main window**, in the current or a new tab. There is no separate results window.
- **A hands-on pass on the real desktop is still recommended.** Every feature was exercised in-process off-screen, with rendered screenshots.
- **One test-side incident.** An early run of the new search test opened a temporary `README.md` with its default app, a test bug since fixed. If an unexpected `README.md` appeared in an editor today, that was the cause, and it can be closed.
