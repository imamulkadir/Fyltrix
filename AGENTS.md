# Fyltrix: start here (for AI agents and new contributors)

Fyltrix is a tabbed file explorer for Windows with real file previews. It is C# / .NET 8 WPF, with every window built in code (no XAML files), on `net8.0-windows10.0.19041.0`, x64, with per-monitor-V2 DPI. Read this file first, then `docs/IMPLEMENTATION_STATUS.md`. Its "Current state" table and the round sections hold the full history and the reasoning behind each change.

## Status (2026-10-31)

- **Nothing is in progress.** Every round the user asked for is done: Round 1 (phases 0–6), Round 2, Rounds 3–3i, 4, 4b, 5, 6, 7 and 8.
- **Tests:** see `docs/IMPLEMENTATION_STATUS.md` for the latest verified run. Compatibility tests cover preserved legacy profiles and cache identities.
- **Waiting on the user to check on a real screen:** these can't be tested off-screen.
  1. The taskbar icon shows correctly right after start.
  2. Title bar: the Snap Layouts flyout on hovering Maximize, maximised edges, and moving between monitors.
  3. Right-click → Open with… opens over Fyltrix when it's on the second monitor.
  4. Paste a fresh Snipping Tool capture with the Paste button and Ctrl+V. Image formats and notifications are tested through an isolated clipboard; tests never read or write the real clipboard.
- **Local Git checkpoint:** `6952c6d` preserves the state before complete identity migration. No remote is configured. Commit or push only if the user asks.

## Build, run, test

```powershell
dotnet build src\Fyltrix\Fyltrix.csproj                     # debug build
dotnet test tests\Fyltrix.Tests\Fyltrix.Tests.csproj         # ~5 min; the UI tests run off-screen
powershell -ExecutionPolicy Bypass -File .\publish.ps1 -Run   # release build to artifacts\Fyltrix, then starts it
```

- `publish.ps1` deletes and rebuilds `artifacts\Fyltrix`. The user runs Fyltrix from there, and it may be open. Don't publish unless asked; if you do, the user must close Fyltrix first.
- Run one test class with `--filter "FullyQualifiedName~ClassName"`.
- Screenshots that tests take go to `%TEMP%\FyltrixTests-shots`. Look at them when you change the UI.
- `tests/Fyltrix.PlacementProbe` is a small Windows program with the same per-monitor DPI setting as Fyltrix. The test host can't have that setting, so tests start this probe for second-monitor checks. It also has:
  - `Fyltrix.PlacementProbe bench <dataDir> <out.json>`: the resource benchmark. Build it with `-c Release`. `BENCH_PREVIEW=0` starts with the preview pane off.
  - `Fyltrix.PlacementProbe render Light|Dark|System <folder> <out.png>`: a screenshot of the main window in a theme.

## Rules from the user (always follow)

1. **Don't touch the real desktop when testing.** No SendKeys, no real mouse input, nothing that takes focus or steals the foreground; the user may be in meetings. Use the off-screen in-process pattern in `tests/Fyltrix.Tests/StaTests.cs` (`Sta.Run`, `Sta.ShowOffscreen`, `Sta.Render`). For real monitor geometry, use the probe with DWM-cloaked windows. Anything that has to show on screen needs the user's explicit permission first.
2. **Tests must not use the real Recycle Bin or the real clipboard.** They must not listen on all network interfaces (that can trigger a firewall prompt) and must not write to the user's own settings. Point `AppSettings.Folder` at a temp folder; the test setup already keeps WebView2 data separate.
3. **"Done" means tested and working.** Verify every change with a test or a measurement before reporting it. If something can't be verified, say so plainly and tell the user how to check it.
4. **No regressions.** Run the full suite before reporting, and keep every existing feature working.
5. **Performance:** measure with the benchmark before and after. Keep only changes that measurably help and slow nothing down. Don't cap or throttle features just to make the numbers look smaller.
6. **Keep the docs current.** Add a new round section at the top of `docs/IMPLEMENTATION_STATUS.md`, update its Current state table, and update `README.md` whenever a feature changes.
7. **Defaults keep existing behaviour.** A new setting must not change anything until the user changes it.
8. **Paths with spaces and special characters must work.** Never build command lines by joining strings. Launch programs without a shell (`UseShellExecute=false`; quoting via `IdeCatalog.Quote`).
9. **Don't overwrite or delete anything you didn't create.** Look before deleting. Never touch the user's Fyltrix processes or files.

## Code map

| Area            | Where                                                                                                                                                                                                                                                                             |
| --------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Entry point     | `src/Fyltrix/App.cs` (loads settings, picks the theme, installs the UI synchronisation context before the window is built; keep that order)                                                                                                                                      |
| Settings model  | `Core/AppSettings.cs`, stored in `%AppData%\FileLens\settings.json`; `Core/ApplicationDefaults.cs` holds the approved portable baseline for theme/toolbar. New properties use the existing behaviour unless the user explicitly requests a new default. |
| Command states / clipboard | `UI/MainWindow.Commands.cs` (shared availability, guarded shortcuts, clipboard notifications), `Core/ClipboardFiles.cs` (clipboard boundary, access checks, image capture/PNG writes). Tests and probes substitute the clipboard boundary. |
| Main window     | `UI/MainWindow*.cs` partials: `.TitleBar`, `.Places` (Favourites, Recent, sidebar sections), `.Customize` (context-menu filter, toolbar), `.View` (per-folder views), `.Search`, `.Palette` (command palette, Settings wiring), `.Ide`, `.Media`, `.Sharing`, `.Tools` and others |
| Folder list     | `UI/FolderView.cs` (+ `.Tiles` thumbnails, `.Groups` date headings), `UI/VirtualizingWrapPanel.cs`                                                                                                                                                                                |
| Settings window | `UI/SettingsWindow.cs` (8 pages; `SettingsHost` gives it the main window's lists and actions; `MainWindow.ApplySettings()` applies changes on Save)                                                                                                                               |
| Previews        | `Preview/PreviewPane.cs` (WebView2 started on demand and released when the pane is hidden), `PreviewServer`/`PreviewApi` (files served to the viewer through a private internal address), `Viewer/viewer.js` (libraries loaded on demand with `need()`)                           |
| Look and feel   | `UI/Theme.cs` (dark and light colour sets, chosen once at startup), `UI/LocationIcons.cs` (one icon per place), `UI/WindowPlacement.cs` (popups on the app's own monitor)                                                                                                         |
| Tools           | `tools/LogoGen` (logo and `.ico`), `tools/OfficeTemplates`                                                                                                                                                                                                                        |

## Things that will bite you

- **Icon glyphs:** some source files contain icon-font glyphs as raw private-use characters, not `\uXXXX` escapes, so exact-text edits that include them fail. Match on the code around them, or use `char.ConvertFromUtf32(0xE8B7)` (as `LocationIcons` does).
- **Escapes in scripts:** passing code with `\n`, `\u` or backslashes through shell heredocs or inline Python gets the escapes rewritten. Prefer the Edit tool, or write a script file first.
- **Short paths:** `%TEMP%` may be in 8.3 form (`IMAMUL~1`). Fyltrix shows long paths, so compare paths with `AppSettings.SameFolder` or expand them first.
- **UI-thread awaits:** `await` code relies on a UI synchronisation context. App.Main installs one; probes and harnesses must do the same.
- **Off-screen renders:** WPF renders (`Sta.Render`) don't include WebView2 content. Use `CoreWebView2.CapturePreviewAsync` for that, or read the page with `ExecuteScriptAsync`.
- **Racy tests:** after navigating, wait for the new folder's items, not just "any items"; the previous folder's items are still there for a moment.
- **Windows 11 Open with:** the "Open with" dialog (`OpenWith.exe`) only draws for the foreground app, so it can't be tested from a background process.
- **Timing noise:** idle CPU readings come in steps of 15.6 ms. Startup time varies ±0.5 s between runs, so compare medians of 5 or more runs.

## Known limitations

- The theme applies at the next start.
- The preview engine takes about 1 s to restart if the pane has been hidden for 30 s or more.
- Compare shows PDFs as page images: they scroll together, but their text can't be selected there.
- Search stops at 100,000 results.
- Per-folder views and tags don't follow folders moved outside Fyltrix.
- Real FFmpeg conversion is untested (FFmpeg isn't installed here).
- Sharing to another device is untested, because the corporate (Intune) policy on this PC blocks local network access.
- The full list is under "Known limitations" in `docs/IMPLEMENTATION_STATUS.md`.

## Future scope (only if the user asks)

- An Explorer-style **List** view; Fyltrix currently has Details plus 4 icon sizes.
- Live theme switching without a restart.
- An in-app "Open with" picker (fully testable, but it couldn't set Windows' "Always use this app").
- A separate window for search results.
- A cross-device sharing test on a Private network.
- Optional further optimisations, each needing benchmark proof first:
  - Incremental folder updates instead of re-reading the whole folder when files change.
  - Lighter checks that recent folders still exist.

## Working style the user expects

- Plain-language summaries: what changed, how it was verified (numbers or screenshots), and what still needs checking.
- When a request is ambiguous, give a recommendation instead of a list of options. Ask only when a choice really is the user's to make.
