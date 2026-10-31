# Fyltrix

Fyltrix is a tabbed file explorer for Windows with integrated previews, file and folder comparison, archive browsing, search, conversion tools, automation, and temporary network sharing. It is built with C# and .NET 8 WPF and runs without administrator rights for everyday use.

Formerly **FileLens**, the application now builds and launches as **Fyltrix**: `Fyltrix.exe`, `Fyltrix.dll`, and the `src/Fyltrix` project. Existing `%AppData%\FileLens` settings and `%LocalAppData%\FileLens` caches are reused without resetting preferences. The private preview origin, manifest identity, and existing firewall-rule identity are retained for compatibility.

## Contents

- [Requirements](#requirements)
- [Build and run](#build-and-run)
- [Feature guide](#feature-guide)
- [Settings and defaults](#settings-and-defaults)
- [Keyboard shortcuts](#keyboard-shortcuts)
- [Application data and diagnostics](#application-data-and-diagnostics)
- [Development and verification](#development-and-verification)
- [Project structure](#project-structure)
- [Implementation reports](#implementation-reports)

## Requirements

| Component | Purpose |
|---|---|
| Windows 10 or Windows 11, x64 | WPF desktop application. The project declares Windows 10 version 1809 as its minimum platform version. |
| Microsoft Edge WebView2 Runtime | Embedded document, code, image, and media previews. Install separately if absent. |
| .NET 8 SDK or a compatible newer SDK | Building from source. The publish script can install .NET 8 for the current user if needed. |
| FFmpeg, optional | Audio/video conversion and fallback video thumbnails. Configure its path in Settings > Advanced or make it discoverable on PATH. Fyltrix does not download FFmpeg. |
| Windows image/media codecs, optional | HEIC, camera RAW, and some media streams require installed codecs. Recognizing an extension does not guarantee decoding. |
| Registered Windows preview handlers, optional | PowerPoint, legacy Word, Outlook messages, and other shell-preview formats. |
| Windows `tar.exe`, optional | Additional archive capabilities, including 7z creation and AES-encrypted ZIP creation, when supported by the installed version. |

The portable publish is self-contained, so a separate .NET runtime is unnecessary. WebView2 and optional codecs, handlers, and FFmpeg remain separate dependencies. Adding a firewall rule for LAN sharing requires administrator approval.

## Build and run

Run these commands in PowerShell from the checkout's root directory. The outer directory can be renamed without changing project paths.

```powershell
# Publish a portable release folder.
powershell -ExecutionPolicy Bypass -File .\publish.ps1

# Publish and start the application.
powershell -ExecutionPolicy Bypass -File .\publish.ps1 -Run

# Publish a single executable; native libraries extract when needed.
powershell -ExecutionPolicy Bypass -File .\publish.ps1 -SingleFile
```

Output: `artifacts\Fyltrix\Fyltrix.exe`. For a normal folder publish, copy the **entire** `artifacts\Fyltrix` folder to the desired location. The publish script recreates that output folder on each run.

Open a specific folder using an existing build:

```powershell
.\artifacts\Fyltrix\Fyltrix.exe "C:\Projects"
```

Build and run directly from source with an installed SDK:

```powershell
dotnet build .\src\Fyltrix\Fyltrix.csproj -p:Platform=x64
dotnet run --project .\src\Fyltrix\Fyltrix.csproj -p:Platform=x64
```

## Feature guide

### Navigation, tabs, and window layout

- Browse local folders, connected drives, and supported archive contents.
- Navigate Back, Forward, Up, or to a typed address; refresh the active location and toggle hidden files.
- Create, duplicate, switch, and close tabs; open folders in new tabs or close other tabs from the tab menu.
- Restore open tabs and the active tab on startup, or choose a startup folder. New tabs can open in the current folder, startup folder, or This PC.
- Close tabs through their right-aligned close buttons, middle click, or shortcuts. Long names use an ellipsis with the full location in a tooltip.
- Show location-specific icons in tabs and use the overflow tab list when space runs out.
- Use a Windows 11-style title bar with the new-tab button beside tabs, native dragging, double-click maximize, resize borders, system menu, and a Snap Layouts hit target.
- Resize the sidebar and preview pane; window geometry and pane sizes are remembered.
- Open files with their default application, choose **Open with**, view native Properties, open the containing folder, launch a terminal, or open the location in Windows File Explorer.

### Sidebar and This PC

- **Quick access:** Home, Desktop, Downloads, Documents, Pictures, Music, and Videos.
- **Favourites:** add/remove folders, reorder with Move up/down, open in new tabs, and clear after confirmation. The optional toolbar star also toggles the current folder's Favourite status.
- **Recent:** revisit folders, remove individual entries, clear the list, or stop recording new visits.
- **Tags:** select a tag to list associated items.
- **This PC:** drives with capacity bars, free/total space, and detailed tooltips. Low space is highlighted; slow/disconnected drives do not indefinitely block the listing.
- Drop files onto sidebar folder destinations to copy or move them.
- Show/hide sections and individual Quick access places. Fold section headings and optionally remember the collapsed state.

### File operations and clipboard

- Create folders and files; cut, copy, paste, rename, and delete one or multiple items where supported.
- Rename a single item inline. F2 with multiple items opens batch rename.
- Delete to the Recycle Bin or use Shift+Delete for permanent deletion.
- Drag and drop files to copy or move them, including to folder and sidebar destinations. Hovering a tab during a file drag activates it.
- Interoperate with Windows File Explorer's file/folder clipboard data.
- Copy selected paths or the current folder/archive path.
- Resolve transfer conflicts with Replace, Keep both, Skip, or Cancel, optionally applying a choice to all remaining conflicts; follow operation progress.
- Undo supported copy, move, rename, batch rename, creation, extraction, compression, conversion, and Recycle Bin deletion operations with Ctrl+Z. Permanent deletion cannot be undone.
- Restore earlier file sets through **Tools > Clipboard history** or the command palette. History contains up to 15 file sets copied/cut in the current session and is not saved on exit. Restored entries are copied rather than cut.
- Remove the downloaded-file mark with **Unblock**, when present.
- Open the folder background menu from empty space or the toolbar's **...** button, even when the file list is full.

Toolbar buttons, context menus, the palette, and file-operation shortcuts share command-availability checks. Selection count, item type, clipboard contents, destination permissions, and whether items still exist determine availability. Disabled toolbar buttons remain visible and inactive.

### Paste screenshots and clipboard images

Copy an image using Windows Snipping Tool or another compatible application, open a writable folder, then click **Paste** or press **Ctrl+V**.

- Support clipboard PNG data and standard bitmap/image data.
- Save PNG files named like `Screenshot 2026-10-31 14-30-00.png`, resolving conflicts without overwriting existing files.
- Retain original PNG bytes where available; encode bitmap data losslessly as PNG. Encoding and writing run off the UI thread.
- Show the created image in the file list immediately.
- **File/folder data takes priority over image data** when both exist. Invalid file-drop data does not silently fall back to saving an image.
- React to Windows clipboard-change notifications, with bounded retries when the clipboard is busy.

### New menu and templates

The **+ New** button and folder background menu offer folders, built-in/custom templates, and eligible Windows-registered New-menu types.

| Category | Built-in templates |
|---|---|
| General | Text, Markdown, CSV, JSON, XML, YAML, INI, log |
| Web | HTML, CSS, JavaScript, React JSX, SVG |
| Programming | Python, C#, PowerShell, shell, SQL |
| Office | Valid blank Word `.docx`, Excel `.xlsx`, and PowerPoint `.pptx` documents |

Use **Manage templates** to add, edit, duplicate, delete, reorder, enable/disable, or restore templates. Custom templates support categories, default names, text content, name/identifier substitutions, Unix line endings, and a UTF-8 byte-order-mark option. Settings > Context menu controls which templates and registered types appear; hiding a type does not delete its template or change Windows associations. Templates are also available in the command palette.

### Views, sorting, and thumbnails

- Details view with Name, Date modified, Type, and Size columns, concise type names, and a Type column sized to its values.
- Thumbnail views at 48, 96, 160, and 256 pixels.
- Windows thumbnails for supported photos, videos, PDFs, and Office documents; FFmpeg fallback video thumbnails when available, file icons otherwise.
- Sorting through the toolbar or column headers, with configurable default column/direction.
- Separate remembered views per folder, across tabs and restarts, or one default view everywhere.
- Reset the current folder's view or all saved folder views.
- Group by date modified in Details and thumbnail views. Downloads is grouped by default; enable it elsewhere through View.
- Date groups: Today, Yesterday, Earlier this week, Last week, Earlier this month, Last month, Earlier this year, A long time ago. Column sorting applies within groups.
- Change thumbnail size with Ctrl+mouse wheel or shortcuts.
- Virtualized lists, bounded thumbnail caches, and background thumbnail workers.

### Preview pane and supported formats

Select a file to preview it beside the file list. Toggle the pane with the rightmost **Preview** button or Ctrl+P.

| Content | Preview features |
|---|---|
| PDF | Embedded PDF viewing; page rendering also supports comparison. |
| HTML / XHTML | Rendered page; scripts disabled by default with an explicit scripts option. |
| Markdown | Rendered document and source toggle. |
| JSON family | Expandable tree, expand/collapse all, raw view. Recognizes JSON, GeoJSON, JSONC, and HAR; parsing depends on valid content. |
| Text / code | Text display, syntax highlighting for recognized languages, and word wrap. |
| CSV / TSV | Table preview with a header-row toggle. |
| Word | Rendered `.docx`, `.docm`, `.dotx`, zoom, and fit width. |
| Spreadsheets | Sheet selection and table preview for `.xlsx`, `.xlsm`, `.xls`, `.xlsb`, `.ods`. |
| Images | Zoom, pan, fit, actual size, rotate, fullscreen, metadata/EXIF, previous/next, Copy, Edit. |
| Animated images | GIF/WebP/APNG playback, pause, frame stepping, speed controls where supported by the decoder. |
| Audio / video | Playback, seeking, speed, metadata/codec details, fullscreen video, audio album art, and waveform where supported. Nearby `.srt`/`.vtt` subtitles are detected. |
| Archives | Filterable contents list and archive-entry actions. |
| Windows preview handlers | PowerPoint, legacy `.doc`, Outlook `.msg`, RTF, EML, Visio, OneNote, MHT/MHTML when a suitable handler is registered. |
| Other files | Text/binary detection and hex preview for unknown binaries. |

**Code highlighting** recognizes XML/XAML and related project/config files; C/C++/C#, Java, Kotlin, Python, JavaScript/TypeScript/JSX/TSX, CSS/SCSS/Less, SQL, YAML, INI/TOML/properties, shell/PowerShell/batch, Go, Rust, Ruby, PHP, Swift, VB, Gradle/Groovy, Dockerfile, diff/patch, GraphQL, R, and supported template formats.

**Recognized image extensions** include PNG, JPEG/JFIF, GIF, BMP/DIB, WebP, ICO, AVIF, APNG, SVG, TIFF, HEIC/HEIF/HIF, JPEG XR, DDS, and camera RAW families DNG, CR2/CR3, NEF/NRW, ARW/SR2, ORF, RW2, RAF, PEF, SRW. Less common formats require Windows codecs.

**Recognized media** includes MP4/M4V, WebM, MOV, MKV, OGV, MP3, WAV, OGG/OGA, M4A/AAC, FLAC, Opus, WEBA. Windows Media Foundation handles additional families such as AVI, WMV/ASF, MPEG, MPEG transport streams, 3GP, FLV, VOB, WTV, WMA, AIFF, AC3 where codecs are installed. Playback depends on the streams inside the file. An **Open in default player** fallback is available for unsupported media.

Large previews use limits to keep the application responsive: text loading is capped at 20 MB, highlighting is skipped for large text, tables show up to 5,000 rows, and waveform decoding is limited to smaller audio files.

### Quick View

- Select exactly one supported file and press **Space** or use Quick View for a larger preview.
- Navigate files with arrow keys; close with Space/Escape when the embedded player is not consuming those keys.
- Reuse the main pane's document, image, media, archive, and hex preview infrastructure.
- Folders and multiple selections do not activate Quick View.

### File comparison

Select exactly two compatible files, then use **Compare** or Ctrl+D.

- Render side by side, swap sides, or choose replacement files.
- Show line-by-line text differences for compatible text, code, JSON, XML, CSV, Markdown, HTML, and related content.
- Show only changes, ignore whitespace, and normalize JSON key order for JSON comparisons.
- Compare compatible files with different extensions. Compatibility follows preview families: text, images, media, Word, spreadsheets, PDF pairs, and files using the same registered Windows preview handler.
- Synchronize scrolling for supported rendered content, including PDFs. Images and PowerPoint do not use synchronized scrolling.
- See an explanatory tooltip when Compare is unavailable. Folders, incompatible types, and selections other than two files are excluded.

### Folder comparison and synchronization

Use **Tools > Compare folders**, or select two folders and use the context menu.

- Compare recursively or at the top level, optionally including hidden files and verifying contents.
- Identify identical items, files on only one side, newer versions, differing content, and conflicts.
- Synchronize left to right, right to left, or both ways.
- Review proposed actions before applying them; override individual items to copy, skip, or delete.
- Optionally apply deletions or replace newer destination files. Two-way sync uses saved baselines to identify changes since the previous sync.
- Send replaced/removed files to the Recycle Bin.

### Archives

- Browse ZIP/ZIPX, 7z, RAR, TAR, compressed TAR, GZ, BZ2, and XZ contents like folders.
- Recognize TAR.GZ/TGZ, TAR.BZ2/TBZ/TBZ2, TAR.XZ/TXZ, TAR.ZST, and TAR.LZ variants. Successful extraction depends on the compression method and available reader/fallback support.
- Preview or open entries, copy or drag items out, and extract here, into a named folder, or to a chosen destination.
- Supply passwords for supported encrypted archives and resolve extraction conflicts.
- Create ZIP, TAR, TAR.GZ, TAR.BZ2, and single-file GZ archives, with compression settings where supported.
- Create 7z or password-protected AES-256 ZIP archives when Windows `tar.exe` supports them. AES ZIP output may require another archive tool rather than File Explorer to open.
- Enforce extraction-size, expansion-ratio, and preview-size limits, with checks against unsafe paths and links.
- Choose whether double-clicking archives browses them as folders in Settings > Advanced.

### Search

- Filter the current folder while typing, with an option to disable live filtering.
- Press Enter to search recursively in **Subfolders** scope; use **This folder** for local scope.
- Press Ctrl+Enter for results in a new tab; stop active searches when needed.
- Use `*`/`?` wildcards; choose files, folders, or both.
- Use normal file actions, path copying, and **Open file location** on results.
- Open **Advanced search** with Ctrl+Shift+F: name contains/wildcard/regex, case sensitivity, extensions, size range, creation/modification dates, content text/regex, tags, hidden/system items.
- Content search examines text files up to 32 MB.
- Save, reuse, and delete advanced-search filters; run saved searches through the palette.

### Batch rename, duplicates, folder sizes, and checksums

| Tool | Features |
|---|---|
| Batch rename | Live plan; templates; counters with start/step/padding; find/replace or regex; character removal; space replacement; case changes; prefixes/suffixes; extension replacement/lowercasing; optional folder renaming; invalid-name/conflict detection. Tokens: `{name}`, `{n:000}`, `{ext}`, `{folder}`, `{date:yyyy-MM-dd}`, `{created:yyyy-MM-dd}`. |
| Duplicate finder | Scan multiple folders with subfolder, hidden-file, minimum-size, and extension options; verify content with SHA-256; select duplicates while keeping the newest, oldest, or shortest-path copy; move selected duplicates to the Recycle Bin. |
| Folder sizes | Analyze disk usage and explore a folder/file treemap. |
| Checksums | SHA-256, SHA-512, SHA-1, MD5; compare expected values; copy results; verify checksum files; save `.sha256` files. SHA-1/MD5 support legacy checksum compatibility. |

### Tags and color labels

- Apply/remove tags and color labels from the context menu.
- Show tag chips beside names and in selection status; show labels as color indicators.
- Browse tagged items in the sidebar or filter tags in advanced search.
- Preserve associations for renames/moves performed within Fyltrix.
- Clean up entries for missing files from Tools.

### Image editing and conversion

- Open **Edit** from image previews to rotate, flip horizontally/vertically, crop, or resize.
- Keep proportions, enter pixel dimensions, or use percentage resizing shortcuts.
- Undo/reset edits before saving to a new file beside the original.
- Batch-convert supported images to PNG, JPEG, WebP, TIFF, BMP, GIF, ICO.
- Choose quality, output size, and lossless WebP where offered; preserve EXIF where supported.

### Audio and video conversion

With FFmpeg installed, select compatible media and choose **Convert**.

- Video presets: MP4, WebM, MKV, MOV, AVI, animated GIF.
- Audio presets: MP3, M4A/AAC, FLAC, WAV, OGG Vorbis, Opus.
- Extract audio from video with audio-only presets.
- Choose quality, output folder, and maximum video resolution; monitor progress or cancel.
- Output capabilities depend on the installed FFmpeg build.

### PDF tools

- View page thumbnails and document information.
- Reorder, rotate, or remove pages from a new composition.
- Extract selected pages, split into page groups, or export pages as PNG.
- Merge PDFs; convert one image or combine multiple images into a PDF.
- Save edited documents as new files, preserving originals.

### Command palette, IDE integration, and custom actions

- **Ctrl+Shift+P:** find commands, create files from templates, run saved searches, restore clipboard sets, or jump to a Favourite, Recent folder, drive, or typed path.
- Detect Visual Studio Code, Cursor, VSCodium, Windsurf, Zed, JetBrains IDEs, Android Studio, Visual Studio, Sublime Text.
- Enable/disable IDE entries, detect again, or add custom executables/arguments through Settings > Context menu. `{path}` supplies the folder.
- Launch IDEs from folder/background/sidebar menus or the palette.
- Define **Custom actions** with program, arguments, extension filters, and file/folder applicability; reorder them and run through the context menu or palette.
- Action tokens: `{path}`, `{paths}`, `{folder}`, `{name}`. `{path}` runs once per item; `{paths}` supplies the selection together.

### Automation

Automation is optional and **disabled by default**. Rules run while Fyltrix is open.

- Trigger on new files, new/changed files, intervals, or manual execution.
- Watch folders with optional subfolders and extension, wildcard-name, and size filters.
- Move, copy, rename, extract, compress to ZIP, convert images, organize into date folders, or run a configured program.
- Optionally move originals after processing.
- Dry-run proposed work and inspect the activity log.
- Wait for files to settle, ignore common partial-download names, and avoid repeatedly processing the engine's own outputs.

### Temporary sharing

Use **Share on this network** for selected files or folders. Shares are read-only and served by this computer.

- Temporary token-protected HTTP links and QR codes, with automatic expiry.
- This-computer-only access by default; explicit opt-in for LAN access.
- Network-address selection and firewall/Public-network diagnostics. Default port 8765, with fallback ports when needed.
- Copy, extend, or stop existing shares; manage sessions through **Tools > Active shares**.
- Closing the share dialog leaves sharing active. On exit, choose to stop shares or keep them running in the background with a notification-area icon until they expire.
- Persist session records with tokens encrypted for the current Windows account. Interrupted sessions require explicit resumption.
- Offer a narrowly scoped firewall rule with administrator approval when policy permits. Organization-managed network policy can still block access.

## Settings and defaults

Open Settings using the gear immediately before the rightmost Preview button, Tools, or the palette.

| Page | Options |
|---|---|
| Appearance | Dark/Light/System theme; extensions; hidden files; tab icons; preview startup behavior. |
| Navigation | Restore tabs; startup folder; new-tab destination; multiple-tab close confirmation. |
| Sidebar | Sections and Quick access places; remembered collapsed state; Recent tracking; clear Recent/Favourites. |
| Views | Default view; per-folder memory; reset current/all folder views; default sorting. |
| Context menu | Action visibility; detected/custom IDEs; New-menu types; restore default menu. Open and Refresh remain available. |
| Toolbar | Button visibility; extra catalog commands; separate operation/secondary labels; restore default toolbar. Navigation/address controls and Settings stay accessible. |
| Search | Enter-to-search subfolders; files/folders/both; live filtering. |
| Advanced | Archive browsing; HTML scripts; archive extraction/preview limits; FFmpeg path. |

**Save** applies settings without restarting, except theme changes, which apply on the next start. Clear/reset actions for lists and folder views take effect immediately as indicated in the dialog.

Fresh installations and **Restore default toolbar** use this baseline:

- **+ New:** icon and label.
- **Cut, Copy, Paste, Rename, Delete:** icons only with tooltips.
- Secondary actions: icons only; operation and secondary labels can be enabled independently.
- **Settings** immediately before **Preview**; Preview is rightmost.
- Share actions use a share-arrow icon.
- Separate New folder, Favourite, Explorer, Terminal buttons hidden; commands remain available elsewhere. Active shares is included as an extra toolbar command.

Other defaults include System theme, extensions shown, hidden files hidden, restored tabs, remembered folder views, HTML scripts off, and automation off. Defaults live in `Core/ApplicationDefaults.cs` and `Core/AppSettings.cs`. Explicit saved preferences take precedence; missing properties use application defaults. Personal paths, Favourites, history, and window placement are not captured as application-wide defaults.

## Keyboard shortcuts

Shortcuts apply to the active tab and respect command availability. Text fields and embedded viewers can consume their own keys.

| Shortcut | Action |
|---|---|
| Ctrl+T / Ctrl+W | New / close tab |
| Ctrl+Tab / Ctrl+Shift+Tab | Next / previous tab |
| Ctrl+1 through Ctrl+9 | Corresponding tab |
| Alt+Left / Alt+Right | Back / Forward |
| Alt+Up or Backspace | Up one level |
| F5 | Refresh |
| Ctrl+L, Alt+D, F4 | Address bar |
| Ctrl+F, Ctrl+E, F3 | Search box |
| Enter / Ctrl+Enter in search | Search / search in a new tab |
| Ctrl+Shift+F | Advanced search |
| Ctrl+Shift+P | Command palette |
| Ctrl+P | Toggle preview |
| Space | Quick View |
| Ctrl+D | Compare two compatible files |
| Ctrl+X / Ctrl+C / Ctrl+V | Cut / copy / paste |
| Ctrl+Shift+C | Copy selected paths |
| Ctrl+Shift+N | New folder |
| F2 | Rename / batch rename |
| Delete / Shift+Delete | Recycle / permanent delete |
| Ctrl+Z | Undo supported operation |
| Ctrl+H | Hidden files |
| Alt+Enter | Properties |
| Shift+F10 or Menu key | Context menu |
| Ctrl+Shift+1 / 2 / 3 / 4 | Thumbnails: 256 / 160 / 96 / 48 px |
| Ctrl+Shift+6 | Details view |
| Ctrl+mouse wheel | Thumbnail size |

In image previews: `+`/`-` zoom, `0` fit, `1` actual size, `R` rotate, `F` fullscreen, Left/Right previous/next. Animation controls include `K` play/pause and `,`/`.` frame stepping. Quick View supports arrows to navigate and Escape to close or leave fullscreen.

## Application data and diagnostics

Portable describes deployment: preferences live in the current Windows user's profile. The legacy **FileLens** folders preserve existing preferences after rebranding.

| Location | Contents |
|---|---|
| `%AppData%\FileLens\settings.json` | Settings, tabs, Favourites, Recent, saved searches, views, custom actions, IDE configuration. |
| `%AppData%\FileLens\templates.json` | Template definitions/customization. |
| `%AppData%\FileLens\tags.json` | Tags and labels. |
| `%AppData%\FileLens\automation.json`, `automation.log` | Rules and activity. |
| `%AppData%\FileLens\sync\` | Sync baselines. |
| `%AppData%\FileLens\shares.json` | Share sessions with Windows-account-encrypted tokens. |
| `%AppData%\FileLens\error.log`, `crashes\` | Rolling log and crash reports. Share links are masked. |
| `%LocalAppData%\FileLens\WebView2` | Preview browser data/cache. |
| `%LocalAppData%\FileLens\Thumbs` | FFmpeg thumbnail cache, bounded to 256 MB. |
| `%LocalAppData%\FileLens\ArchiveCache` | Temporary archive previews, cleaned on exit. |

**Tools > About Fyltrix** provides access to the diagnostics folder. Back up data before manually editing settings. Moving the executable or checkout does not require renaming these folders. Older shortcuts targeting `FileLens.exe` must be pointed at the new `Fyltrix.exe`; the new publish does not generate a legacy launcher. Legacy release files are preserved in the external migration backup. Existing program-scoped firewall rules may need reauthorization for the new executable path. The sharing dialog updates its existing named rule instead of adding a duplicate; broader legacy rules are left untouched.

## Development and verification

The separate [release-layout tooling](docs/RELEASE_LAYOUT.md) builds an isolated versioned payload and native launcher. The Stage 3 [Inno Setup installer](docs/INSTALLER.md) adds per-user installation, with a separate Sandbox identity for testing. Neither build script runs an installer or replaces `publish.ps1 -Run`; automatic updates remain disabled. Stage approvals and current installation/update work are tracked in [PROJECT_PROGRESS.md](docs/PROJECT_PROGRESS.md).

Stage 4 adds separate [release discovery and signed package verification tooling](docs/UPDATES.md), with offline test feeds. It is not connected to application startup or settings. Production signing trust remains unconfigured.

Stage 5 adds explicit [manual download/staging and safe handover tooling](docs/MANUAL_UPDATES.md), plus internal shutdown guards that preserve active work and existing close confirmations. Stage 6 adds the [verified apply, isolated health trial, rollback and recovery backend](docs/APPLY_AND_RECOVERY.md), with installed Sandbox tests and owned-file uninstall cleanup. The application update UI and final end-to-end approval remain Stage 7 work. No automatic updates are enabled; production verification/download/apply require an owner-supplied signing key to be pinned first.

```powershell
dotnet restore .\tests\Fyltrix.Tests\Fyltrix.Tests.csproj
dotnet test .\tests\Fyltrix.Tests\Fyltrix.Tests.csproj -p:Platform=x64
```

The latest verified application suite has **365 passing tests**, covering core operations, offscreen UI behavior, safe update closing and strict copied-profile compatibility. Separate suites cover release verification/staging/apply/recovery and packaging. UI tests render real windows without taking desktop focus; clipboard tests use isolated providers. Tests do not certify every codec, shell handler, or network environment.

`tests/Fyltrix.PlacementProbe` provides native placement checks, theme renders, and resource benchmarks. Build it before use:

```powershell
dotnet build .\tests\Fyltrix.PlacementProbe\Fyltrix.PlacementProbe.csproj -c Release
.\tests\Fyltrix.PlacementProbe\bin\Release\net8.0-windows10.0.19041.0\Fyltrix.PlacementProbe.exe render System "C:\TestData" "C:\TestOutput\fyltrix.png"
.\tests\Fyltrix.PlacementProbe\bin\Release\net8.0-windows10.0.19041.0\Fyltrix.PlacementProbe.exe bench "C:\TestData" "C:\TestOutput\benchmark.json"
```

Create directories first and use dedicated test data: the benchmark creates a data set, and render writes isolated settings/WebView2 data under its supplied folder.

Optional checks: `FL_MEDIA=<folder>` previews files and writes `sweep.log` there; `FL_LAN_CHECK=1` checks a share through this computer's LAN address. Cross-device sharing, native Snap flyouts, taskbar appearance, and third-party Open with placement still benefit from a manual check on the target machine.

## Project structure

```text
src/Fyltrix/
  App.cs                 Startup, settings, theme, crash handlers
  Assets/                Existing SVG/ICO/PNG logo
  Core/                  File operations, undo, settings/defaults, shell integration
    Archives/            Archive reading, extraction, creation, browsing
    Templates/           Templates and Windows ShellNew discovery
    Imaging/             Image decoding, transforms, encoding
    Media/               Metadata, subtitles, thumbnails, caches
    Pdf/                 PDF page operations and rendering
    Conversion/          FFmpeg discovery, presets, conversion
    Tools/               Search, rename, duplicates, sizes, hashes, tags, sync
    Automation/          Rules, engine, persistence, activity
    Sharing/             Sessions, HTTP host, network/firewall checks
  UI/                    Main window, folder views, settings, tool dialogs
  Preview/               WebView2 pane, preview API/server, native/shell hosting
  Viewer/                Embedded HTML/CSS/JS viewer and bundled libraries
  Templates/             Blank Office documents
tests/Fyltrix.Tests/    xUnit core and offscreen UI tests
tests/Fyltrix.PlacementProbe/
                        Placement, screenshot, benchmark helper
tools/LogoGen/           Existing logo generator
tools/OfficeTemplates/   Office template generator/validator
publish.ps1              Portable win-x64 publishing
docs/                    Reports and verification evidence
```

Dependencies include WebView2, SharpCompress, SkiaSharp, PDFsharp, QRCoder. The embedded viewer includes marked, DOMPurify, highlight.js, docx-preview, SheetJS, PapaParse, JSZip, jsdiff, loaded as needed. Package versions live in the project file.

## Implementation reports

- [Current implementation status](docs/IMPLEMENTATION_STATUS.md)
- [Initial feature implementation](docs/IMPLEMENTATION_REPORT.md)
- [Round 2 fixes and verification](docs/ROUND2_REPORT.md)
- [Rebranding and compatibility identities](docs/REBRANDING_REPORT.md)
- [Project-directory relocation audit](docs/PROJECT_RELOCATION_REPORT.md)
- [Complete identity migration and compatibility audit](docs/FYLTRIX_MIGRATION_REPORT.md)
- [Round 7 benchmark](docs/ROUND7_BENCHMARK.json) and [Round 8 benchmark](docs/ROUND8_BENCHMARK.json)

Reports preserve development history. Historical paths and old display names are not required checkout locations or instructions to rename internal identifiers.
