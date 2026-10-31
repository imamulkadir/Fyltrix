using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Ellipse = System.Windows.Shapes.Ellipse;
using Fyltrix.Core;
using Fyltrix.Core.Tools;

namespace Fyltrix.UI;

/// <summary>Productivity tools: batch rename, duplicates, folder sizes, advanced search, checksums, tags and labels.</summary>
public sealed partial class MainWindow
{
    private string? CurrentFolder => View is { CanModify: true } v ? v.CurrentPath : null;

    private List<object> ToolsMenuItems()
    {
        var sel = View?.SelectedItems.Where(i => !i.IsDrive && !i.IsVirtual).ToList() ?? new List<FileItem>();
        var folder = CurrentFolder;
        return new List<object>
        {
            Theme.MenuItem("Advanced search…", "", AdvancedSearch, "Ctrl+Shift+F", folder != null),
            Theme.MenuItem("Batch rename…", "", () => BatchRenameSelection(), "F2", sel.Count > 0),
            new Separator(),
            Theme.MenuItem("Find duplicate files…", "", () => FindDuplicates(folder != null ? new[] { folder } : Array.Empty<string>())),
            Theme.MenuItem("Folder sizes…", "", () => FolderSizes(folder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))),
            Theme.MenuItem("Compare folders…", "\uE8AB", () => CompareFolders(folder ?? "", "")),
            Theme.MenuItem("Automation…", "\uE945", OpenAutomation),
            Theme.MenuItem("Active shares…", "\uE72D", OpenShares),
            Theme.MenuItem("Custom actions…", "\uE945", () => { if (new CustomActionsWindow(this, Settings).ShowDialog() == true) SetStatus("Custom actions saved."); }),
            ClipboardHistoryMenu(),
            Theme.MenuItem("Checksums…", "", () => Checksums(sel.Where(s => !s.IsDirectory).Select(s => s.FullPath).ToList()), null, sel.Any(s => !s.IsDirectory)),
            new Separator(),
            Theme.MenuItem("Clean up tags of missing files", "", () => SetStatus($"Removed tags of {TagStore.Shared.RemoveMissing()} missing item(s).")),
            new Separator(),
            Theme.MenuItem("Command palette…", "\uE721", () => ShowPalette(), "Ctrl+Shift+P"),
            Theme.MenuItem("Settings…", "\uE713", OpenSettings),
            Theme.MenuItem($"About {ProductBranding.Name}", "\uE946", () => AppIcon.ShowAbout(this)),
        };
    }

    private void ShowToolsMenu(UIElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (var i in ToolsMenuItems()) menu.Items.Add(i);
        menu.IsOpen = true;
    }

    /// <summary>Selection context menu: label, tags, rename, checksum, sizes.</summary>
    private void AddToolMenuItems(ContextMenu m, FolderView view, IList<FileItem> sel)
    {
        var real = sel.Where(s => !s.IsDrive && !s.IsVirtual).ToList();
        if (real.Count == 0) return;
        m.Items.Add(new Separator());
        var labels = Enum.GetValues<ColorLabel>().Select(l => (object)LabelItem(l, real)).ToList();
        m.Items.Add(Theme.SubMenu("Label", "", labels));
        var actionItems = real.Select(r => (r.FullPath, r.IsDirectory)).ToList();
        var actions = Settings.CustomActions.Where(a => a.AppliesTo(actionItems)).ToList();
        if (actions.Count > 0)
            m.Items.Add(Theme.SubMenu("Actions", "\uE945", actions.Select(a => (object)Theme.MenuItem(a.Name, null, () => RunCustomAction(a, actionItems.Select(i => i.FullPath).ToList())))));
        m.Items.Add(Theme.MenuItem("Tags…", "", () => EditTags(real.Select(r => r.FullPath).ToList())));
        if (view.CanModify && real.Count > 1) m.Items.Add(Theme.MenuItem("Batch rename…", "", () => BatchRenameSelection(), "F2"));
        m.Items.Add(Theme.MenuItem("Share on this network…", "\uE72D", () => ShareWith(real.Select(r => r.FullPath))));
        if (real.Any(r => !r.IsDirectory)) m.Items.Add(Theme.MenuItem("Checksums…", "", () => Checksums(real.Where(r => !r.IsDirectory).Select(r => r.FullPath).ToList())));
        var pdfs = real.Where(r => !r.IsDirectory && r.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)).Select(r => r.FullPath).ToList();
        if (pdfs.Count == 1 && real.Count == 1) m.Items.Add(Theme.MenuItem("PDF tools…", "", () => OpenPdfTools(pdfs[0])));
        if (pdfs.Count >= 2) m.Items.Add(Theme.MenuItem($"Merge {pdfs.Count} PDFs…", "", () => MergePdfs(pdfs)));
        var convertible = real.Where(r => !r.IsDirectory && (Core.Imaging.ImageService.CanDecode(r.FullPath)
            || FileKinds.KindFor(r.FullPath, false) is PreviewKind.Media or PreviewKind.NativeMedia)).Select(r => r.FullPath).ToList();
        if (convertible.Count > 0 && convertible.Count == real.Count) m.Items.Add(Theme.MenuItem("Convert…", "", () => Convert(convertible)));
        var images = real.Where(r => !r.IsDirectory && Core.Imaging.ImageService.CanDecode(r.FullPath)).Select(r => r.FullPath).ToList();
        if (images.Count > 0 && view.CanModify) m.Items.Add(Theme.MenuItem(images.Count == 1 ? "Convert to PDF" : $"Combine {images.Count} images into a PDF", "", () => ImagesToPdf(images)));
        if (real.All(r => r.IsDirectory))
        {
            m.Items.Add(Theme.MenuItem("Find duplicates in…", "", () => FindDuplicates(real.Select(r => r.FullPath).ToList())));
            if (real.Count == 1) m.Items.Add(Theme.MenuItem("Folder sizes…", "", () => FolderSizes(real[0].FullPath)));
            if (real.Count == 2) m.Items.Add(Theme.MenuItem("Compare these folders", "\uE8AB", () => CompareFolders(real[0].FullPath, real[1].FullPath)));
        }
    }

    private MenuItem LabelItem(ColorLabel label, List<FileItem> items)
    {
        var color = TagStore.Color(label);
        var mi = new MenuItem { Header = label == ColorLabel.None ? "No label" : label.ToString() };
        if (color != null) mi.Icon = new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)) };
        mi.Click += (_, _) =>
        {
            TagStore.Shared.SetLabel(items.Select(i => i.FullPath), label);
            SetStatus(label == ColorLabel.None ? "Label removed." : $"Labelled {items.Count} item(s) {label.ToString().ToLowerInvariant()}.");
        };
        return mi;
    }

    private void EditTags(List<string> paths)
    {
        if (new TagsDialog(this, paths).ShowDialog() == true) SetStatus("Tags saved.");
    }

    /// <summary>Re-reads labels and tags for everything on screen (after any change to the tag store).</summary>
    private void RefreshTags()
    {
        foreach (var t in _tabs)
            foreach (var i in t.View.Items) i.ApplyTags(TagStore.Shared.Get(i.FullPath));
        RebuildSidebar();
    }

    // ------------------------------------------------------------------ tools

    private void BatchRenameSelection()
    {
        if (!CanExecute("delete")) return;
        if (View is not { CanModify: true } view) { SetStatus("Batch rename works in a folder (not in search results, archives or This PC)."); return; }
        var sel = view.SelectedItems.Where(i => !i.IsDrive).Select(i => i.FullPath).ToList();
        if (sel.Count == 0) { SetStatus("Select the items to rename first."); return; }
        var w = new BatchRenameWindow(this, sel);
        w.Renamed += (_, result) =>
        {
            ReleasePreviews(result.Renamed.Select(r => r.from));
            foreach (var (from, to) in result.Renamed) TagStore.Shared.Move(from, to);
            _undo.Push(new BatchRenameUndo(result.Renamed));
            _ = view.Refresh(result.Renamed.Select(r => r.to));
            RefreshOtherViews(view, view.CurrentPath);
            SetStatus($"Renamed {result.Renamed.Count} item(s). Ctrl+Z to undo.");
        };
        w.ShowDialog();
    }

    private void FindDuplicates(IEnumerable<string> folders)
    {
        var w = new DuplicatesWindow(this, folders);
        w.Recycled += (_, e) =>
        {
            ReleasePreviews(e.paths);
            _undo.Push(new DeleteUndo(e.paths, e.when));
            RefreshFolders(e.paths.Select(p => Path.GetDirectoryName(p) ?? p));
        };
        w.Show();
    }

    private void FolderSizes(string folder)
    {
        var w = new FolderSizeWindow(this, folder);
        w.OpenRequested += (_, path) =>
        {
            if (View == null) return;
            if (Directory.Exists(path)) View.Navigate(path);
            else View.Navigate(Path.GetDirectoryName(path)!, select: path);
            Activate();
        };
        w.Show();
    }

    private void Checksums(List<string> files)
    {
        if (files.Count == 0) { SetStatus("Select one or more files first."); return; }
        new ChecksumWindow(this, files).Show();
    }

    /// <summary>Records new files for Undo and shows them.</summary>
    private void FilesCreated(IReadOnlyList<string> files, string description)
    {
        if (files.Count == 0) return;
        _undo.Push(new CreatedUndo(description, files));
        var dirs = files.Select(f => Path.GetDirectoryName(f)!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        RefreshFolders(dirs);
        if (View != null && dirs.Any(d => string.Equals(d.TrimEnd('\\'), View.CurrentPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
            _ = View.Refresh(files);
    }

    private void OpenPdfTools(string pdf)
    {
        var w = new PdfToolsWindow(this, pdf);
        w.FilesCreated += (_, files) => FilesCreated(files, files.Count == 1 ? $"Create '{Path.GetFileName(files[0])}'" : $"Create {files.Count} files from '{Path.GetFileName(pdf)}'");
        w.Show();
    }

    internal void CompareFolders(string left, string right)
    {
        var w = new FolderCompareWindow(this, left, right);
        w.FoldersChanged += (_, dirs) => RefreshFolders(dirs);
        w.Show();
    }

    private void Convert(List<string> files)
    {
        var w = new ConvertWindow(this, Settings, files);
        w.FilesCreated += (_, created) => FilesCreated(created, created.Count == 1 ? $"Create '{Path.GetFileName(created[0])}'" : $"Convert {created.Count} files");
        w.Show();
    }

    private void MergePdfs(List<string> pdfs)
    {
        var d = new PdfMergeDialog(this, pdfs);
        if (d.ShowDialog() == true && d.Output != null)
        {
            FilesCreated(new[] { d.Output }, $"Create '{Path.GetFileName(d.Output)}'");
            SetStatus($"Merged {pdfs.Count} PDFs into '{Path.GetFileName(d.Output)}'.");
        }
    }

    private async void ImagesToPdf(List<string> images)
    {
        var dir = Path.GetDirectoryName(images[0])!;
        var output = FileOps.UniqueName(dir, (images.Count == 1 ? Path.GetFileNameWithoutExtension(images[0]) : "Images") + ".pdf");
        try
        {
            await JobWindow.RunAsync(this, "Creating PDF", (p, ct) =>
            {
                p.Report(new OperationProgress($"Adding {images.Count} image(s)…"));
                Core.Pdf.PdfService.ImagesToPdf(images, output, ct);
            });
            FilesCreated(new[] { output }, $"Create '{Path.GetFileName(output)}'");
            SetStatus($"Created '{Path.GetFileName(output)}'.");
        }
        catch (OperationCanceledException) { SetStatus("Cancelled."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or InvalidOperationException)
        {
            Dialogs.Error(this, "Convert to PDF", ex.Message);
        }
    }

    private void RunCustomAction(CustomAction a, List<string> paths)
    {
        try
        {
            a.Run(paths);
            SetStatus($"Started '{a.Name}'.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Dialogs.Error(this, a.Name, "The program couldn't be started: " + ex.Message);
        }
    }

    private SearchCriteria? _lastSearch;

    private void AdvancedSearch()
    {
        if (View == null || View.IsThisPC || View.IsArchive) { SetStatus("Open a folder first; the search covers it and its subfolders."); return; }
        if (View.IsSearchResults) View.EndSearch();
        var dlg = new SearchDialog(this, Settings, View.CurrentPath, _lastSearch);
        if (dlg.ShowDialog() != true || dlg.Result is not { } c) return;
        _lastSearch = c;
        View.StartSearch(c, c.Title);
        View.FocusList();
    }

    private void ShowTagged(string title, IEnumerable<string> paths)
    {
        if (View == null) return;
        View.ShowResults(title, paths.ToList());
    }

    // ------------------------------------------------------------------ sidebar: recent places and tags

    private void AddRecentAndTagSections()
    {
        var recents = Settings.ShowRecent ? Settings.RecentFolders.Where(Directory.Exists).Take(6).ToList() : new List<string>();
        if (recents.Count > 0 && Section("Recent", "Clear Recent…", () => ClearRecent()))
        {
            foreach (var r in recents)
                SidebarItem(Path.GetFileName(r.TrimEnd('\\')) is { Length: > 0 } n ? n : r, r, LocationIcons.Recent, "Fg2", isRecent: true);
        }
        var tags = TagStore.Shared.AllTags();
        var labels = Enum.GetValues<ColorLabel>().Where(l => l != ColorLabel.None && TagStore.Shared.PathsWithLabel(l).Count > 0).ToList();
        if (tags.Count == 0 && labels.Count == 0) return;
        if (!Section("Tags")) return;
        foreach (var l in labels)
        {
            var label = l;
            TagRow(label.ToString(), TagStore.Color(label), () => ShowTagged($"Label: {label}", TagStore.Shared.PathsWithLabel(label)));
        }
        foreach (var t in tags)
        {
            var tag = t;
            TagRow("#" + tag, null, () => ShowTagged($"Tag: {tag}", TagStore.Shared.PathsWithTag(tag)));
        }
    }

    private void TagRow(string text, string? color, Action onClick)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        UIElement mark = color != null
            ? new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)), Margin = new Thickness(5, 0, 5, 0) }
            : Theme.Icon("", 13, "Fg3");
        if (mark is FrameworkElement fe && color == null) fe.Width = 20;
        sp.Children.Add(mark);
        sp.Children.Add(new TextBlock { Text = text, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.Brush("Fg1") });
        var row = new Border { Child = sp, Padding = new Thickness(8, 5, 8, 5), CornerRadius = new CornerRadius(4), Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand };
        row.MouseEnter += (_, _) => row.Background = Theme.Brush("Hover");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) => onClick();
        _sidebar.Children.Add(row);
    }
}
