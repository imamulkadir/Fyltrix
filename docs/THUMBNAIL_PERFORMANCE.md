# Thumbnail loading, quality and lifecycle verification

## Scope - 2026-11-05

The owner approved Stage 3 after pushing the two completed file-operation stages. Changes are limited to the existing thumbnail pipeline and its tests/documentation. No installer, launcher, updater, release workflow, settings values, source images or preview rendering routes changed. The legacy cache location and default budgets are preserved. No Git mutations or desktop input were performed.

## Follow-up: List/Details image thumbnails (2026-11-05)

The owner reported generic document icons in image rows. Inspection confirmed that the Details name template bound only to `FileItem.Icon`; it never requested thumbnail content. It now reuses `ThumbnailImage` at the existing 16-DIP row size with an image-only mode. Actual image files use the shared asynchronous, bounded, DPI-aware cache/loader and its stale-result and hidden/unloaded-row cancellation. Folders, non-image files and virtual archive entries retain existing icon behavior. No new workers, image library, settings or file writes were introduced.

The existing UI regression now checks cold Details image loading and return from the grid, real 2:1 image content, unchanged non-image/folder behavior and preserved selection. Release build and 31 focused tests pass; full application regression: **591 passed, zero failures/skips**, in 3 minutes 33 seconds (`results/details-full.trx`). The timing table below predates this follow-up and measures thumbnail-grid workflows, not the newly enabled Details thumbnail path. No new Details speedup or memory percentage is claimed. Owner visual review remains required.

## Observed causes and changes

- The initial 16-pixel icon was displayed at 102.4 DIPs in the large tile: **6.4x raster enlargement** at the measured 100% DPI. Cached, frozen vector drawings from the existing icon font now provide the placeholder; usable small icons are never drawn above their native pixel dimensions. Finished bitmaps keep their proportions and physical-pixel size caps.
- Size, item, unload, hidden-tab and DPI changes advance the control's request generation. A callback queued before a change must match that generation and exact pending request before displaying. Collapsed tabs cancel requests and release displayed bitmap references; showing them again reuses valid cache entries.
- A bounded, coalesced request/job queue runs on the existing three below-normal STA workers. Identical live requests share one producer. Visible requests precede prefetch; viewport priority changes use a direct tile/container association rather than inherited property propagation through every descendant. Obsolete queued subscriptions are removed promptly. Pending subscriptions are capped at 800, and idle workers wait for signals rather than polling.
- The existing path/size/last-write/length LRU now reuses an adequate larger bucket; it never substitutes a smaller bucket for a larger request. Physical DPI requests cover all four sizes, including 256 DIPs at 500% scaling. Shared memory remains budgeted at 192 MiB. Separate test-service limits stay configurable.
- Native thumbnail extraction is still attempted before fallback, including installed third-party thumbnail handlers. Suitable fallback type icons share the same bounded LRU; per-instance icon types and folders retain independent retrieval. The icon-only fallback avoids repeating thumbnail extraction. Existing preview callers retain the original Shell API path and flags.
- FFmpeg fallback checks cancellation while waiting for its owned process, stops that process on cancellation/deadline and removes its unique temporary output. Disk cache trimming now runs after new frames as well as initialization, retaining the 256 MiB default and a 4,096-frame count cap. Live temporary outputs are excluded from trimming. Native bitmap/COM handles still release through existing finally blocks.

Microsoft documents why [Shell image extraction belongs off the UI thread](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ishellitemimagefactory-getimage). No new decoder package, persistent service, process-isolation architecture, source-image rewrite or global input hook was introduced.

## Measurements

Before changing application code, the opt-in cloaked PlacementProbe benchmark recorded `baseline-1.json` through `baseline-3.json` and retained its compiled application DLL. The first run included first-time Windows thumbnail extraction (887 ms for the 72-image batch); later initial app-cold runs were 269-277 ms. Windows' shared thumbnail cache was not cleared or controlled.

For the final comparison, identical updated benchmark-harness binaries ran alternately against that preserved, hash-checked old application DLL and the final new DLL. This also measured an explicit return to a smaller requested size. Three fresh-process runs per implementation used 72 generated 1024x768 PNGs, 5,000 generated text files, disposable settings, a read-only clipboard stub, fixed 1x native DPI and cloaked WPF windows. The core benchmark service used a 96 MiB budget. Numbers below are **median [minimum, maximum]**, not general performance guarantees.

| Measurement | Before | After |
| --- | ---: | ---: |
| 72 unique images, app-cache cold (ms) | 237.612 [235.352, 251.370] | 242.084 [234.931, 330.879] |
| 72 same-size cached requests (ms) | 0.451 [0.451, 0.458] | 0.479 [0.457, 0.489] |
| 72 cached images resized back to smaller request (ms) | 146.461 [144.970, 201.283] | 0.143 [0.135, 0.150] |
| Initial real grid listing (ms) | 383.140 [381.196, 388.284] | 399.363 [397.087, 493.932] |
| Initial realized grid thumbnails ready (ms) | 455.391 [452.610, 460.415] | 472.655 [469.392, 538.105] |
| 20 PageDown operations in 5,000-file grid (ms) | 824.503 [759.951, 880.498] | 920.091 [773.901, 986.646] |
| Return to picture folder (ms) | 109.249 [93.601, 133.544] | 109.503 [107.273, 133.541] |
| Host process CPU time (ms) | 6187.500 [4828.125, 6265.625] | 5812.500 [5453.125, 6031.250] |
| Private bytes after GC (MiB) | 226.547 [201.723, 229.980] | 119.711 [108.398, 122.910] |
| Managed bytes after GC (MiB) | 6.389 [6.300, 6.457] | 6.261 [6.243, 6.270] |

The reliable improvements are qualitative placeholder sharpness, **72/72 larger-cache hits versus 0/72**, and removal of regeneration for the measured smaller-size batch. Unique-image cold decoding was effectively unchanged. Grid readiness and scrolling were not consistently faster: this final sample's scrolling median was about 96 ms slower over 20 pages (about 4.8 ms/page), with overlapping run ranges. The extra placeholder/lifecycle bookkeeping has a cost; no universal navigation or scrolling speedup is claimed. Earlier slower variants were investigated; inherited-property propagation and repeated text-placeholder layout were replaced before the final measurements. All intermediate evidence remains retained.

CPU is host process CPU time, not a system-wide utilization/GPU/provider-host measurement. Private/managed bytes are sampled after explicit GC and include a disposed core test service plus the live grid. The lower private-byte result reflects cache/resource cleanup and shared icons; it is not a claim that every ordinary Fyltrix session uses half as much RAM. Realized controls remained 30 in both versions of this 5,000-file scrolling fixture. The benchmark is a representative short workflow, not a long-session memory-leak soak or physical scroll-animation acceptance test.

Raw final evidence: `%TEMP%\Fyltrix-thumbnails-stage3-20261105\handover-{baseline,changed}-{1,2,3}.json`. Initial/intermediate results, the old build and the same-harness baseline are retained below that root. Actual WPF captures are `handover-visual.json.placeholder.png`, `.loading-grid.png`, `.loaded.png`, and `.grid.png`; these are diagnostic demo captures, not new marketing screenshots.

## Verification

Release builds pass with existing application/test nullable and WFAC010 warnings only. Final focused thumbnail run: **31 passed, zero failures/skips**, `results/thumbnail-final-verified.trx`. New tests exercise coalescing, subscriber cancellation (including cache hits), visible promotion, 2,000-request queue pressure, worker disposal, larger-cache reuse, timestamp/length invalidation, all four tile sizes at 1x/1.25x/2x/4x DPI and 256 DIPs also at 5x, a deliberately queued old completion before resize, hidden-tab cancellation/cache return, and cancellation of a real FFmpeg process blocked on a private test-owned named pipe. Existing real-image/video/icon/aspect-ratio, disk-budget and 5,000-file virtualization/keyboard tests pass in that focused run.

Final full application regression suite: **591 passed, zero failures/skips**, in 3 minutes 32 seconds (`results/thumbnail-stage3-handover-full.trx`).

Native provider calls already executing cannot safely be forcibly aborted; their obsolete results are discarded and concurrency remains bounded. Unsupported codec/provider behavior still uses normal fallback. Actual mixed-monitor DPI moves, physical scrolling perception, remote/network folders and a broad codec/provider matrix remain owner acceptance checks. No live clipboard, real Recycle Bin, personal configuration or installed app was modified. `publish.ps1 -Run` and the application version remain unchanged.

## Files in this stage

- `src/Fyltrix/Core/Media/ThumbnailService.cs`
- `src/Fyltrix/Core/Media/ShellThumbnails.cs`
- `src/Fyltrix/Core/ShellInfo.cs`
- `src/Fyltrix/UI/FolderView.cs`
- `src/Fyltrix/UI/ThumbnailImage.cs`
- `src/Fyltrix/UI/VirtualizingWrapPanel.cs`
- `tests/Fyltrix.PlacementProbe/Program.cs`
- `tests/Fyltrix.PlacementProbe/ThumbnailBench.cs` (new)
- `tests/Fyltrix.Tests/ThumbnailGridTests.cs`
- `tests/Fyltrix.Tests/ThumbnailPipelineTests.cs` (new)
- `README.md`
- `CHANGELOG.md` (Unreleased only)
- `docs/THUMBNAIL_PERFORMANCE.md` (this report, new)
- `docs/PROJECT_PROGRESS.md`
- `docs/IMPLEMENTATION_STATUS.md`

Suggested commit: `perf(thumbnails): optimize loading, caching and image quality`. Stop for owner review.
