using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using Fyltrix.Core;
using Fyltrix.Core.Archives;

namespace Fyltrix.UI;

/// <summary>Archive features: browse as a folder, extract, compress, preview entries.</summary>
public sealed partial class MainWindow
{
    private int _virtualPreviewVersion;

    public string? AskArchivePassword(string archiveFile, bool wasWrong) => Dialogs.AskPassword(this, Path.GetFileName(archiveFile), wasWrong);

    private static bool IsBrowsableArchive(FileItem item) => !item.IsDirectory && !item.IsVirtual && ArchiveFormats.IsArchive(item.FullPath);

    // ------------------------------------------------------------------ context menu

    private void AddArchiveMenuItems(ContextMenu m, FolderView view, IList<FileItem> sel)
    {
        if (view.IsArchive)
        {
            if (sel.Count == 0) return;
            var archive = view.ArchiveFile!;
            var folder = Path.GetDirectoryName(archive)!;
            m.Items.Add(new Separator());
            m.Items.Add(Theme.MenuItem($"Extract to '{Path.GetFileName(folder)}'", "",
                () => ExtractSelection(view, sel, folder)));
            m.Items.Add(Theme.MenuItem("Extract to…", "", () => ExtractSelection(view, sel, null)));
            return;
        }

        var archives = sel.Where(IsBrowsableArchive).ToList();
        var items = new List<object>();
        if (archives.Count == 1 && sel.Count == 1)
        {
            var a = archives[0].FullPath;
            var dir = Path.GetDirectoryName(a)!;
            var stem = ArchiveFormats.StripArchiveExtension(Path.GetFileName(a));
            items.Add(Theme.MenuItem("Extract here", "", () => _ = ExtractAsync(a, dir, null, ConflictPolicy.Ask, null, null, true, false)));
            items.Add(Theme.MenuItem($"Extract to '{stem}\\'", "", () => _ = ExtractAsync(a, FileOps.UniqueName(dir, stem), null, ConflictPolicy.Ask, null, null, true, false)));
            items.Add(Theme.MenuItem("Extract to…", "", () => ExtractWithDialog(a, null, null)));
            items.Add(Theme.MenuItem(Settings.OpenArchivesAsFolders ? "Browse contents" : "Open as folder", "", () => { view.Navigate(a); view.FocusList(); }));
        }
        else if (archives.Count > 1)
        {
            items.Add(Theme.MenuItem($"Extract each to its own folder ({archives.Count})", "", async () =>
            {
                foreach (var a in archives)
                {
                    var dir = Path.GetDirectoryName(a.FullPath)!;
                    var stem = ArchiveFormats.StripArchiveExtension(a.Name);
                    if (!await ExtractAsync(a.FullPath, FileOps.UniqueName(dir, stem), null, ConflictPolicy.Ask, null, null, true, false)) break;
                }
            }));
        }
        if (view.CanModify && sel.Count > 0 && sel.All(s => !s.IsDrive))
        {
            if (items.Count > 0) items.Add(new Separator());
            var zipName = SuggestArchiveName(view, sel) + ".zip";
            items.Add(Theme.MenuItem($"Add to '{zipName}'", "", () => _ = CompressAsync(sel.Select(s => s.FullPath).ToList(), new CompressRequest(
                FileOps.UniqueName(view.CurrentPath, zipName), ArchiveCreateFormat.Zip, null, System.IO.Compression.CompressionLevel.Optimal, null))));
            items.Add(Theme.MenuItem("Compress…", "", () => CompressWithDialog(view, sel)));
        }
        if (items.Count == 0) return;
        m.Items.Add(new Separator());
        m.Items.Add(Theme.SubMenu(archives.Count > 0 ? "Archive" : "Compress", "", items));
    }

    private static string SuggestArchiveName(FolderView view, IList<FileItem> sel)
    {
        if (sel.Count == 1) return sel[0].IsDirectory ? sel[0].Name : ArchiveFormats.StripArchiveExtension(sel[0].Name);
        var folder = Path.GetFileName(view.CurrentPath.TrimEnd('\\'));
        return string.IsNullOrEmpty(folder) ? "Archive" : folder;
    }

    // ------------------------------------------------------------------ extract

    private void ExtractWithDialog(string archive, IList<FileItem>? selection, FolderView? view)
    {
        var dir = Path.GetDirectoryName(archive)!;
        var stem = ArchiveFormats.StripArchiveExtension(Path.GetFileName(archive));
        bool encrypted;
        try { encrypted = ArchiveFormats.Detect(archive) is ArchiveKind.Zip or ArchiveKind.SevenZip or ArchiveKind.Rar && ArchiveService.NeedsPassword(ArchiveBrowser.GetEntries(archive)); }
        catch (ArchivePasswordException) { encrypted = true; }
        catch (Exception ex) { Dialogs.Error(this, "Extract", "This archive can't be read: " + ex.Message); return; }
        var dlg = new ExtractDialog(this, Path.GetFileName(archive), selection == null ? Path.Combine(dir, stem) : dir, encrypted,
            ArchiveBrowser.KnownPassword(archive), selection?.Count ?? 0);
        if (dlg.ShowDialog() != true || dlg.Result is not { } r) return;
        if (r.Password != null) ArchiveBrowser.RememberPassword(archive, r.Password);
        _ = ExtractAsync(archive, r.Destination, r.Password, r.Conflicts, selection?.Select(s => s.ArchiveKey!).ToList(),
            view?.ArchiveInner, r.PreserveTimestamps, r.OpenWhenDone);
    }

    private void ExtractSelection(FolderView view, IList<FileItem> sel, string? destination)
    {
        if (destination == null) { ExtractWithDialog(view.ArchiveFile!, sel, view); return; }
        _ = ExtractAsync(view.ArchiveFile!, destination, null, ConflictPolicy.Ask, sel.Select(s => s.ArchiveKey!).ToList(), view.ArchiveInner, true, false);
    }

    /// <summary>Extracts in the background. Returns false when cancelled or failed.</summary>
    private async Task<bool> ExtractAsync(string archive, string destination, string? password, ConflictPolicy policy,
        IReadOnlyCollection<string>? entries, string? stripPrefix, bool preserveTimes, bool openWhenDone)
    {
        var name = Path.GetFileName(archive);
        password ??= ArchiveBrowser.KnownPassword(archive);
        var zone = ZoneInfo.Read(archive);
        var limits = Settings.ArchiveLimits();
        ExtractResult result;
        while (true)
        {
            var pw = password;
            Func<string, (ConflictChoice, bool)> ask = policy switch
            {
                ConflictPolicy.KeepBoth => _ => (ConflictChoice.KeepBoth, true),
                ConflictPolicy.Skip => _ => (ConflictChoice.Skip, true),
                ConflictPolicy.Replace => _ => (ConflictChoice.Replace, true),
                _ => target => Dispatcher.Invoke(() => ConflictDialog.Ask(this, target)),
            };
            try
            {
                ReleasePreviews(new[] { destination });
                result = await JobWindow.RunAsync(this, $"Extracting {name}", (p, ct) => ArchiveService.Extract(archive, destination, new ExtractOptions
                {
                    Password = pw, Entries = entries, StripPrefix = stripPrefix, PreserveTimestamps = preserveTimes,
                    AskConflict = ask, Limits = limits, ZoneIdentifier = zone,
                }, p, ct));
                break;
            }
            catch (ArchivePasswordException ex)
            {
                password = Dialogs.AskPassword(this, name, ex.WasWrong);
                if (password == null) return false;
                ArchiveBrowser.RememberPassword(archive, password);
            }
            catch (Exception ex)
            {
                LogError(ex);
                Dialogs.Error(this, "Extract", $"'{name}' couldn't be extracted.\n\n{ex.Message}");
                return false;
            }
        }

        if (result.CreatedRoots.Count > 0 || result.Replaced.Count > 0)
            _undo.Push(new CreatedUndo($"Extract '{name}'", result.CreatedRoots, result.Replaced));
        var parent = Path.GetDirectoryName(destination.TrimEnd('\\'));
        RefreshFolders(new[] { destination, parent ?? destination });
        if (View != null && !View.IsArchive && result.CreatedRoots.Count > 0)
        {
            var here = View.CurrentPath.TrimEnd('\\');
            var select = result.CreatedRoots.Where(r => string.Equals(Path.GetDirectoryName(r), here, StringComparison.OrdinalIgnoreCase)).ToList();
            if (select.Count > 0) _ = View.Refresh(select);
        }
        ReportProblems("Extract", result.Errors, result.Skipped);
        var summary = $"Extracted {result.Files:N0} file{(result.Files == 1 ? "" : "s")} ({FileOps.FormatSize(result.Bytes)}) from '{name}'.";
        SetStatus(result.Cancelled ? "Extraction cancelled. " + summary : summary);
        if (openWhenDone && !result.Cancelled && Directory.Exists(destination) && View != null) { View.Navigate(destination); View.FocusList(); }
        return !result.Cancelled && result.Errors.Count == 0;
    }

    /// <summary>Lists errors and skipped items without flooding the screen.</summary>
    private void ReportProblems(string title, IList<string> errors, IList<string> skipped)
    {
        if (errors.Count == 0 && skipped.Count == 0) return;
        var lines = new List<string>();
        if (errors.Count > 0) { lines.Add($"{errors.Count} problem{(errors.Count == 1 ? "" : "s")}:"); lines.AddRange(errors.Take(15)); if (errors.Count > 15) lines.Add($"…and {errors.Count - 15} more."); }
        if (skipped.Count > 0)
        {
            if (lines.Count > 0) lines.Add("");
            lines.Add($"{skipped.Count} skipped:");
            lines.AddRange(skipped.Take(15));
            if (skipped.Count > 15) lines.Add($"…and {skipped.Count - 15} more.");
        }
        Dialogs.Error(this, title, string.Join("\n", lines));
    }

    // ------------------------------------------------------------------ compress

    private void CompressWithDialog(FolderView view, IList<FileItem> sel)
    {
        bool single = sel.Count == 1 && !sel[0].IsDirectory;
        var dlg = new CompressDialog(this, SuggestArchiveName(view, sel) + ".zip", view.CurrentPath, single);
        if (dlg.ShowDialog() == true && dlg.Result is { } r) _ = CompressAsync(sel.Select(s => s.FullPath).ToList(), r);
    }

    private async Task CompressAsync(IList<string> sources, CompressRequest r)
    {
        var name = Path.GetFileName(r.ArchivePath);
        try
        {
            await JobWindow.RunAsync(this, $"Compressing to {name}", (p, ct) =>
            {
                if (r.System is { } sys)
                {
                    p.Report(new OperationProgress($"Compressing to {name}", -1, "Using Windows tar.exe"));
                    SystemTar.Create(sources, r.ArchivePath, sys, r.Password, ct);
                }
                else ArchiveService.Create(sources, r.ArchivePath, new CreateOptions { Format = r.Managed ?? ArchiveCreateFormat.Zip, Level = r.Level }, p, ct);
            });
        }
        catch (OperationCanceledException) { SetStatus("Compression cancelled."); return; }
        catch (Exception ex)
        {
            LogError(ex);
            Dialogs.Error(this, "Compress", $"'{name}' couldn't be created.\n\n{ex.Message}");
            return;
        }
        _undo.Push(new CreatedUndo($"Create '{name}'", new[] { r.ArchivePath }));
        var dir = Path.GetDirectoryName(r.ArchivePath)!;
        RefreshFolders(new[] { dir });
        if (View != null && string.Equals(View.CurrentPath.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            _ = View.Refresh(new[] { r.ArchivePath });
        SetStatus($"Created '{name}' ({FileOps.FormatSize(new FileInfo(r.ArchivePath).Length)}).");
    }

    // ------------------------------------------------------------------ entries: preview, open, copy, drag

    private async void PreviewVirtual(FileItem item, bool quickView)
    {
        int v = ++_virtualPreviewVersion;
        var archiveName = Path.GetFileName(item.ArchiveFile);
        if (item.IsDirectory) { _preview.ShowMessage(item.Name, $"Folder inside {archiveName}"); return; }
        long max = (long)Settings.ArchivePreviewMaxMB * 1024 * 1024;
        if (item.Size > max)
        {
            _preview.ShowMessage(item.Name, $"{FileOps.FormatSize(item.Size)} inside {archiveName}. That's too large to preview from the archive; extract it first.");
            return;
        }
        var path = await ResolveVirtualAsync(item, showProgress: false, () => v == _virtualPreviewVersion,
            () => _preview.ShowMessage(item.Name, "Extracting for preview…"));
        if (path == null || v != _virtualPreviewVersion) return;
        if (Settings.PreviewVisible) _preview.Show(path, delayMs: 0);
        if (quickView || _quickView != null) _quickView?.ShowPath(path);
    }

    /// <summary>Gets a local copy of an archive entry (asks for the password when needed). Null on failure.</summary>
    private async Task<string?> ResolveVirtualAsync(FileItem item, bool showProgress, Func<bool>? stillWanted = null, Action? onSlow = null)
    {
        var limits = Settings.ArchiveLimits();
        bool wrong = false;
        while (true)
        {
            try
            {
                Func<CancellationToken, string> work = ct => ArchiveBrowser.ExtractToCache(item.ArchiveFile!, item.ArchiveKey!, limits, ct);
                if (showProgress) return await JobWindow.RunAsync(this, $"Extracting {item.Name}", (_, ct) => work(ct));
                var task = Task.Run(() => work(CancellationToken.None));
                if (onSlow != null && await Task.WhenAny(task, Task.Delay(150)) != task) onSlow();
                return await task;
            }
            catch (ArchivePasswordException ex)
            {
                if (stillWanted != null && !stillWanted()) return null;
                var pw = Dialogs.AskPassword(this, Path.GetFileName(item.ArchiveFile!), wrong || ex.WasWrong);
                if (pw == null) return null;
                ArchiveBrowser.RememberPassword(item.ArchiveFile!, pw);
                wrong = true;
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex)
            {
                if (stillWanted == null || stillWanted())
                {
                    if (showProgress) Dialogs.Error(this, "Archive", $"'{item.Name}' couldn't be extracted.\n\n{ex.Message}");
                    else _preview.ShowMessage(item.Name, "Couldn't extract this entry: " + ex.Message);
                }
                return null;
            }
        }
    }

    private async void OpenVirtual(FileItem item)
    {
        var path = await ResolveVirtualAsync(item, showProgress: true);
        if (path == null) return;
        if (ArchiveFormats.IsArchive(path) && Settings.OpenArchivesAsFolders && View != null) { View.Navigate(path); View.FocusList(); return; }
        Shell.Open(path);
        SetStatus($"Opened a read-only temporary copy of '{item.Name}'. Extract it to keep changes.");
    }

    /// <summary>Copy from inside an archive: extract the selection to a staging folder and put that on the clipboard.</summary>
    private async void CopyVirtual(FolderView view, IList<FileItem> sel)
    {
        var archive = view.ArchiveFile!;
        var staging = ArchiveBrowser.NewStagingFolder();
        var ok = await ExtractAsync(archive, staging, null, ConflictPolicy.KeepBoth, sel.Select(s => s.ArchiveKey!).ToList(), view.ArchiveInner, true, false);
        var paths = sel.Select(s => Path.Combine(staging, s.Name)).Where(FileOps.Exists).ToArray();
        if (!ok && paths.Length == 0) return;
        var sc = new StringCollection();
        sc.AddRange(paths);
        var data = new DataObject();
        data.SetFileDropList(sc);
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(5)));
        try { _clipboard.Write(data); RefreshClipboardState(); }
        catch (Exception ex) { SetStatus("Clipboard is busy: " + ex.Message); return; }
        _cutPaths.Clear();
        foreach (var t in _tabs) MarkCut(t.View);
        SetStatus($"Copied {paths.Length} item{(paths.Length == 1 ? "" : "s")} from the archive. Paste them anywhere.");
    }

    public DataObject? PrepareVirtualDrag(FolderView view, IList<FileItem> items)
    {
        const long maxDrag = 200L * 1024 * 1024;
        if (items.Any(i => i.IsDirectory) || items.Sum(i => Math.Max(0, i.Size)) > maxDrag)
        {
            SetStatus("Drag files up to 200 MB out of an archive. For folders or larger files use Copy (Ctrl+C) or Extract.");
            return null;
        }
        var limits = Settings.ArchiveLimits();
        var paths = new List<string>();
        try
        {
            var staging = ArchiveBrowser.NewStagingFolder();
            foreach (var i in items)
            {
                var target = Path.Combine(staging, i.Name);
                Task.Run(() =>
                {
                    ArchiveService.ExtractEntryToFile(i.ArchiveFile!, i.ArchiveKey!, target, ArchiveBrowser.KnownPassword(i.ArchiveFile!), limits);
                    ZoneInfo.CopyMark(i.ArchiveFile!, target);
                }).GetAwaiter().GetResult();
                paths.Add(target);
            }
        }
        catch (ArchivePasswordException) { SetStatus("Open one of the files first to enter the archive's password, then drag again."); return null; }
        catch (Exception ex) { SetStatus("Couldn't extract for dragging: " + ex.Message); return null; }
        var sc = new StringCollection();
        sc.AddRange(paths.ToArray());
        var data = new DataObject();
        data.SetFileDropList(sc);
        return data;
    }

    private static void LogError(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.AppendAllText(Path.Combine(AppSettings.Folder, "error.log"), $"[{DateTime.Now:u}] {ex}\n\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
