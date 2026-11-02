# README screenshots

Captured on 2026-11-03 from a Release build of the actual Fyltrix 0.1.8 WPF components on Windows, using the existing `Fyltrix.PlacementProbe` friend-assembly interface. Current checkout `80079b6` carries the released application source plus documentation updates; no application source was modified for these images.

All six PNG files are lossless. The main, file-view and preview captures are 1440 x 900. Dialog captures contain their real client UI rather than native window title bars. The demo workspace is `D:\Fyltrix-Demo`; its files were created specifically for these screenshots. No example rename was applied.

The temporary capture harness uses cloaked, offscreen windows with activation/taskbar visibility disabled, a disposable settings/WebView2/thumbnail profile, a stub clipboard and suppressed automatic update checks. The final run confirmed that the foreground window was unchanged. The This PC sidebar section is collapsed using the existing setting to keep machine-specific drive details out of the exported images.

WPF is captured with `RenderTargetBitmap`. Since WPF capture excludes native WebView2 surfaces, the preview browser is captured with its real `CoreWebView2.CapturePreviewAsync` and placed at the browser's measured rectangle in the same window. No UI text, controls, results or decorations are fabricated. There are no generated mockups or stock application images.

| File | Actual screen |
| --- | --- |
| `main-window.png` | Main window, three tabs, demo folder in Details view |
| `file-views.png` | Same folder in large-icon view |
| `preview.png` | Selected Markdown file and actual rendered preview |
| `advanced-search.png` | Advanced search with illustrative filters; search not executed |
| `file-tools.png` | Batch-rename plan for four demo documents; rename not executed |
| `settings.png` | Toolbar settings with the real main-window toolbar catalog |

To refresh, use isolated sample data and profiles with the existing offscreen/cloaked capture patterns documented in `AGENTS.md`. Inspect every exported image for private paths/content and complete controls before replacing these files. Never capture the owner's live workspace, clipboard or preferences.

The demo directory remains at `D:\Fyltrix-Demo` because automatic approval review blocked its cleanup. Temporary capture code/evidence is outside the repository under the current Windows user's Temp folder.
