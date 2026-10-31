# FileLens — feature expansion report

Date: 2026-10-29 · Scope: Phases 0–6 of the modernization brief · Result: every requested feature group is implemented; 185 automated tests pass. Exceptions are listed under "Not verified" and "Known limitations".

## 1. Starting point (Phase 0 audit)

The baseline was a working .NET 8 WPF + WebView2 file explorer. It built cleanly and had no tests.

| Feature | Status before | Implementation found | Gap | Action |
|---|---|---|---|---|
| Tabs, history, session restore | Fully implemented | `MainWindow`, `FolderView`, settings.json | — | Preserved |
| Copy/move/delete/rename, conflicts, undo | Fully implemented | `FileOps` (SHFileOperation), `UndoManager`, `ConflictDialog` | — | Preserved and reused (Undo now also covers extract, compress, new files, batch rename, duplicate removal, conversions) |
| Drag & drop, Explorer clipboard | Fully implemented | `FolderView`, `MainWindow` | — | Preserved; drag-out from archives added |
| Virtualized list, debounced refresh | Fully implemented | VirtualizingPanel + FileSystemWatcher | — | Preserved |
| Native file-type icons | Fully implemented | `ShellInfo` | Not used in menus | Reused for the New menu and tool windows |
| New menu | Partially implemented | Folder + empty .txt | Templates, Office, custom, ShellNew | Enhanced |
| Archives | Not implemented | (archives previewed as hex) | Everything | Implemented |
| Image preview | Partially implemented | `<img>` with click-to-zoom | Zoom/pan/rotate, EXIF, TIFF/HEIC, editing | Enhanced |
| Animated GIF/WebP/APNG | Implemented by Chromium | `<img>` | Pause, frame step, speed | Enhanced (existing renderer kept for stills) |
| Video/audio | Partially implemented | `<video>/<audio>` for 7 extensions | More formats, metadata, speed, subtitles | Enhanced + native fallback |
| PDF | Preview only | Edge PDF viewer | Page operations | Utilities added; preview unchanged |
| Search | Partially implemented | Recursive wildcard name search | Size/date/content/regex/tags, saved filters | Enhanced (same results view) |
| Favourites | Fully implemented | Sidebar | Tags, labels, recents | Added alongside |
| File compare | Fully implemented | `CompareWindow` | Folder compare/sync | Added; file compare unchanged |
| Batch rename, duplicates, folder sizes, checksums, conversion, palette, automation, sharing, clipboard history | Not implemented | — | Everything | Implemented |

Keyboard shortcuts, layout, theme, context-menu structure and settings were all preserved. New shortcuts were added only where they were free: Ctrl+Shift+P (palette; checked before Ctrl+P), Ctrl+Shift+F (advanced search), and F2 on a multi-selection (batch rename).

## 2. Feature completion matrix

| Feature | Status | What was done | Verification |
|---|---|---|---|
| ZIP/TAR/GZ/BZ2/XZ/7z/RAR extraction | Completed | `ArchiveService` + Extract here / to named folder / to… / selected entries; progress, cancel, conflict policy, timestamps, MOTW propagation | Unit tests (round-trips, encrypted ZIP, 7z, tar.xz) + UI test |
| Archive security | Completed | Zip Slip rejection (`..`, absolute, drive, ADS), no link creation, junction refusal, total/entry/ratio bomb limits, partial-file cleanup, Recycle Bin for replaced files | Unit tests with hostile archives |
| Archive creation | Completed | ZIP (level), TAR, TAR.GZ, TAR.BZ2, GZ; 7z and AES-256 ZIP through Windows tar.exe when available | Unit tests |
| Browse archives as folders | Completed | Virtual paths, preview/open/copy/drag entries without full extraction, password prompt, read-only guards | UI tests |
| Smart New menu | Completed | 21 built-in templates (incl. schema-valid DOCX/XLSX/PPTX), shell icons, ShellNew discovery, custom templates manager, name-first for code templates, inline rename | Unit tests + Office files opened in the installed Office preview handlers |
| Video/audio preview | Completed | WebView2 for MP4/WebM/MKV/MOV/Ogg/MP3/WAV/FLAC/AAC/M4A/Opus (codec probe); Media Foundation player for AVI/WMV/MPEG/TS/3GP/WMA/AIFF and on decode failure; speed, fullscreen, subtitles (SRT/VTT), metadata, album art, waveform | UI tests (WAV, generated AVI, fallback) |
| Animated images | Completed | ImageDecoder player: pause, frame step, speed, frame count | UI test |
| Image viewer + editor | Completed | Zoom/pan/fit/100%/rotate/fullscreen/prev-next/EXIF/copy; TIFF/HEIC/RAW/JXR via a WIC rendition; editor with rotate/flip/crop/resize/convert (PNG/JPEG/WebP/TIFF/BMP/GIF/ICO), EXIF kept, always a new file | Unit + UI tests (all 8 EXIF orientations, all encoders) |
| Batch rename | Completed | Tokens, find/replace (regex), case, remove chars, spaces, prefix/suffix, extension, counter, dates; live preview; conflicts; two-phase + rollback; Undo | Unit + UI tests |
| Duplicate finder | Completed | Size → head/tail fingerprint → full SHA-256; hard links excluded; preview; keep newest/oldest/shortest helpers; Recycle Bin + Undo; refuses to remove every copy | Unit + UI tests |
| Folder size analyzer | Completed | Tree with %, drill-down, treemap, largest files, junctions not followed, cancel | Unit + UI tests |
| Advanced search | Completed | Name (contains/wildcard/regex), extension, size, created/modified dates, text content, tags, type, hidden; saved filters | Unit + UI tests |
| Checksums | Completed | SHA-256/512 (+ SHA-1/MD5 labelled legacy), streaming, compare pasted value, verify/write .sha256 files | Unit + UI tests |
| Tags, colour labels, recents | Completed | Local `tags.json` (files untouched), follow FileLens renames/moves, sidebar sections, tag search | Unit + UI tests |
| Image conversion | Completed | Batch, resize option, EXIF kept | UI test |
| Audio/video conversion | Completed (unverified) | FFmpeg presets, quality, resolution, progress, cancel, batch; FFmpeg detected, never downloaded | Args/parsing/error path tested; FFmpeg isn't installed here |
| PDF utilities | Completed | Page thumbnails, reorder, rotate, remove, extract, split, merge, images→PDF, PDF→PNG, metadata, encrypted/signed handling | Unit + UI tests |
| Folder compare & sync | Completed | One-way update/mirror, two-way with baseline + conflict resolution, Recycle Bin for replaced/deleted files, never silent deletes | Unit + UI tests |
| Command palette | Completed | Ctrl+Shift+P: commands, templates, saved searches, places, typed paths, clipboard history, custom actions | Unit + UI tests |
| Automation engine | Completed | Opt-in rules (new/changed/interval/manual; ext/name/size conditions; move/copy/rename/extract/zip/convert/by-date/run program); stable-file wait; partial-download skip; loop prevention; dry run; log | Unit tests incl. a simulated browser download |
| Local file sharing | Completed | Token links, localhost by default, LAN opt-in with warning, expiry, private-network-only, path validation, Range, folder ZIP, QR code | Unit tests incl. traversal attempts; UI test |
| Custom actions | Completed | User context-menu commands with placeholders, no shell | Unit + UI tests |
| Clipboard history | Completed | Session list in Tools menu and palette | Manual review only (a test would overwrite the real clipboard) |
| UX improvements | Completed | Non-blocking progress windows, inapplicable toolbar buttons disabled, live recents, drive-label cache, preview served off the UI thread, Settings window | Covered by UI tests |

## 3. Architecture changes

- **Core** (no UI): `Archives`, `Templates`, `Media`, `Imaging`, `Pdf`, `Conversion`, `Tools`, `Automation`, `Sharing`, plus `Fuzzy` and `CustomActions`. Long operations take `IProgress<OperationProgress>` and a `CancellationToken`.
- **UI**: `MainWindow` is now a partial class split by feature (`.Archives`, `.New`, `.Media`, `.Tools`, `.Palette`, `.Automation`, `.Clipboard`). `JobWindow` runs work in the background and shows a cancellable progress window only after 350 ms. `DialogWindow`/`ToolWindow` give consistent dark dialogs. The theme gained the control styles the new windows need.
- **Preview**: `PreviewServer` now serves requests off the UI thread (WebView2 deferrals) and survives a preview closing mid-request. New `/api/` endpoints provide archive listings, metadata, thumbnails and image renditions.
- **Tests**: `tests/FileLens.Tests` (xUnit, 185 tests). UI tests run the real windows off-screen and non-activated, on a shared STA dispatcher. WebView2 content is checked through script evaluation and `CapturePreviewAsync`.

## 4. Dependencies added

| Package | Licence | Why |
|---|---|---|
| SharpCompress 1.0.0 | MIT | Read ZIP/ZIPX/7z/RAR and BZip2/XZ/Zstd/LZip streams |
| SkiaSharp 3.119.1 (+ NativeAssets.Win32) | MIT | WebP encoding only (WIC has no WebP encoder); loaded on first use |
| PDFsharp 6.2.4 | MIT | PDF page operations |
| QRCoder 1.6.0 | MIT | QR codes for share links |
| Windows SDK projection (TFM `net8.0-windows10.0.19041.0`) | Microsoft | Windows' built-in PDF renderer; minimum OS stays Windows 10 1809 |
| DocumentFormat.OpenXml 3.5.1 | MIT | Build-time only (`tools/OfficeTemplates`); not shipped |

Built-in Windows components used, with nothing bundled: tar.exe (libarchive), Media Foundation, WIC codecs (HEIF/WebP/RAW extensions when installed), the shell property system and thumbnails, and Office preview handlers when present.

Release publish (self-contained, win-x64) is 203 MB. All native files are present (`libSkiaSharp.dll`, `WebView2Loader.dll`).

## 5. Supported formats

- **Archives** — read: ZIP, ZIPX, 7Z, RAR (v1.5–v5), TAR, TAR.GZ/TGZ, TAR.BZ2, TAR.XZ, TAR.ZST, TAR.LZ, GZ, BZ2, XZ. Write: ZIP, TAR, TAR.GZ, TAR.BZ2, GZ; 7Z and AES-256 ZIP through Windows tar.exe.
- **Images** — view: JPEG, PNG, GIF, BMP, WebP, AVIF, ICO, SVG (as `<img>`, so scripts never run), APNG, plus TIFF, HEIC/HEIF, JXR, DDS and camera RAW through Windows codecs. Write: PNG, JPEG, WebP, TIFF, BMP, GIF, ICO.
- **Video/audio** — WebView2: MP4, M4V, MOV, WebM, MKV, OGV, MP3, WAV, FLAC, AAC, M4A, OGG, OPUS. Media Foundation: AVI, WMV, ASF, MPG/MPEG, TS/M2TS/MTS, 3GP, VOB, WTV, WMA, AIFF, AC3. FLV only if a codec pack is installed.
- **Documents** — unchanged previews, plus archive listing. PDF operations and images→PDF.

## 6. Configuration and data locations

All under `%AppData%\FileLens\`:

| File | Contents |
|---|---|
| `settings.json` | New keys: archive limits, `OpenArchivesAsFolders`, `FfmpegPath`, `AutomationEnabled`, `SavedSearches`, `RecentFolders`, `CustomActions` |
| `templates.json` | New-menu templates |
| `tags.json` | Tags and colour labels |
| `automation.json`, `automation.log` | Automation rules and their log |
| `sync\*.json` | Two-way sync baselines |

The archive entry cache lives in `%LocalAppData%\FileLens\ArchiveCache\<pid>` and is removed on exit and at the next start.

FFmpeg is optional and never downloaded. Install it with `winget install Gyan.FFmpeg`, or set its path in Settings.

## 7. Not verified

- Audio/video conversion with a real FFmpeg (it isn't installed on the development machine).
- LAN sharing to another device (binding beyond localhost was deliberately not exercised in tests, to avoid firewall prompts).
- A manual pass of the full GUI on a real desktop. Every feature was exercised through off-screen UI tests and rendered screenshots; a short hands-on check is still recommended.

## 8. Known limitations

- Encrypted archive creation is AES-256 ZIP only and goes through tar.exe (the password is passed on its command line). Explorer can't open AES ZIPs, though 7-Zip, WinRAR and FileLens can. RAR is extract-only.
- Folder sizes count hard links per link (the duplicate finder recognises them). Tags don't follow moves made outside FileLens and aren't copied with copies.
- PDF outputs are saved unencrypted. Any edit invalidates digital signatures (the UI warns first). Bookmarks pointing at removed pages are dropped.
- HEVC/AV1/HEIF need the corresponding Windows extensions. Media Foundation can't play FLV without a codec pack.
- Automation runs only while FileLens is open. Sharing uses plain HTTP and ends when its window closes or the share expires.

## 9. Recommendations

1. Do a 15-minute hands-on pass of the new menus and windows, then publish with `publish.ps1`.
2. Put the project under git: it isn't a repository yet, and these changes are large.
3. Consider a thumbnail (grid) view mode. `ShellThumbnails` already provides the images.
4. If FFmpeg is installed, add an integration test that converts a generated WAV and AVI.
