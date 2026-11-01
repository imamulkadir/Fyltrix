using Fyltrix.Core;
using Fyltrix.Core.Archives;

namespace Fyltrix.UI;

/// <summary>Command palette (Ctrl+Shift+P) and Settings.</summary>
public sealed partial class MainWindow
{
    internal CommandPalette ShowPalette() => ShowPaletteForTest(null);

    /// <summary><paramref name="created"/> gets the palette's window handle before it is shown (tests hide it with DWM cloaking).</summary>
    internal CommandPalette ShowPaletteForTest(Action<IntPtr>? created)
    {
        var p = new CommandPalette(this, PaletteEntries);
        if (created != null) p.SourceInitialized += (_, _) => created(new System.Windows.Interop.WindowInteropHelper(p).Handle);
        p.Show();
        return p;
    }

    private void OpenSettings() => OpenSettings(null);
    private void OpenSettings(string? tab)
    {
        var before = (Settings.ShowHidden, Settings.ShowExtensions, Settings.SearchFinds, Settings.Theme);
        var dlg = new SettingsWindow(this, Settings, _templates, WindowsNewTypes(), SettingsHostFor());
        if (tab != null) dlg.SelectTab(tab);
        bool saved = (SettingsDialogForTest != null ? SettingsDialogForTest(dlg) : dlg.ShowDialog()) == true;
        foreach (var t in _tabs) ApplyView(t.View); // a new default view, or "Reset all folder views" (which applies even on Cancel)
        if (!saved) return;
        ApplySettings(before.ShowHidden != Settings.ShowHidden || before.ShowExtensions != Settings.ShowExtensions,
                      before.SearchFinds != Settings.SearchFinds);
        SetStatus(before.Theme != Settings.Theme ? $"Settings saved. The new theme applies the next time {ProductBranding.Name} starts." : "Settings saved.");
    }

    internal Func<SettingsWindow, bool?>? SettingsDialogForTest { get; set; }

    /// <summary>What Settings needs from this window: toolbar and command catalogues, and actions that act at once.</summary>
    internal SettingsHost SettingsHostFor() => new(
        ToolbarButtons,
        ToolbarCommandChoices(),
        View is { IsThisPC: false, IsSearchResults: false } v ? v.Title : null,
        () => View != null && Settings.HasOwnView(ViewKey(View)),
        ResetFolderView,
        () => ClearRecent(),
        () => ClearFavourites())
        {
            ReadUpdateStatus = ReadUpdatesStatus,
            CheckForUpdates = CheckUpdatesNowAsync,
            ShowUpdateDetails = () => OpenUpdates(),
            SubscribeUpdateStatus = handler => updateStatusChanged += handler,
            UnsubscribeUpdateStatus = handler => updateStatusChanged -= handler
        };

    /// <summary>Puts saved settings into effect without a restart (the theme is the exception: it is chosen at startup).</summary>
    internal void ApplySettings(bool relist = true, bool refilter = true)
    {
        ReloadTemplates(); // New-menu visibility applies to the next menu
        FileItem.HideExtensions = !Settings.ShowExtensions;
        if (relist) foreach (var t in _tabs) _ = t.View.Refresh(); // names with/without extensions, hidden files
        if (refilter && View != null && !View.IsSearchResults) View.SetFilter(View.Filter);
        foreach (var t in _tabs) UpdateTabHeader(t);
        ApplyToolbar();
        RebuildSidebar();
        UpdateSearchChrome();
        UpdateCommands();
        ApplyUpdatePreferences();
    }

    internal IEnumerable<PaletteEntry> PaletteEntries(string text) => PaletteEntries(text, null);

    private IEnumerable<PaletteEntry> PaletteEntries(string text, CommandState? sharedState, bool includePlaces = true)
    {
        var view = View;
        var sel = view?.SelectedItems.Where(i => !i.IsDrive && !i.IsVirtual).ToList() ?? new List<FileItem>();
        var files = sel.Where(s => !s.IsDirectory).Select(s => s.FullPath).ToList();
        bool canModify = view?.CanModify == true;
        var available = sharedState ?? Availability();
        var list = new List<PaletteEntry>();
        void Cmd(string title, string category, Action run, string? gesture = null, bool when = true, string? detail = null)
        {
            if (when) list.Add(new PaletteEntry(title, category, run, gesture, detail));
        }

        // typed path → go there
        var typed = text.Trim().Trim('"');
        if (typed.Length > 2 && (typed.Contains('\\') || typed.Contains(':') || typed.StartsWith('%')))
        {
            var path = FolderView.NormalizePath(typed);
            if (FolderView.Exists(path)) Cmd($"Go to {path}", "Go to", () => { view?.Navigate(path); view?.FocusList(); });
            else if (File.Exists(path)) Cmd($"Show {Path.GetFileName(path)}", "Go to", () => view?.Navigate(Path.GetDirectoryName(path)!, select: path));
        }

        // navigation and view
        Cmd("Back", "Navigate", () => view?.GoBack(), "Alt+Left", view?.CanGoBack == true);
        Cmd("Forward", "Navigate", () => view?.GoForward(), "Alt+Right", view?.CanGoForward == true);
        Cmd("Up one level", "Navigate", () => view?.GoUp(), "Alt+Up", view?.CanGoUp == true);
        Cmd("Refresh", "View", RefreshActive, "F5");
        Cmd("New tab", "Tabs", OpenNewTab, "Ctrl+T");
        Cmd("Close tab", "Tabs", () => { if (_active != null) CloseTab(_active); }, "Ctrl+W");
        Cmd(Settings.PreviewVisible ? "Hide preview pane" : "Show preview pane", "View", () => SetPreviewVisible(!Settings.PreviewVisible), "Ctrl+P");
        Cmd(Settings.ShowHidden ? "Hide hidden files" : "Show hidden files", "View", ToggleHidden, "Ctrl+H");
        foreach (var (name, size, keys) in ViewChoices) Cmd($"{name} (this folder)", "View", () => SetView(size), keys, available.View);
        Cmd("Reset this folder's view", "View", ResetFolderView, null, view != null && Settings.HasOwnView(ViewKey(view)));
        Cmd("Quick view", "View", () => ToggleQuickView(null), "Space", available.QuickView);
        Cmd("Focus address bar", "Navigate", () => { _address.Focus(); _address.SelectAll(); }, "Ctrl+L");
        Cmd("Search this folder", "Search", () => { _search.Focus(); _search.SelectAll(); }, "Ctrl+F");
        Cmd("Advanced search…", "Search", AdvancedSearch, "Ctrl+Shift+F", canModify);
        foreach (var s in Settings.SavedSearches)
        {
            var saved = s;
            Cmd($"Run saved search: {s.Title}", "Search", () => { if (View is { CanModify: true } v) { v.StartSearch(saved, saved.Title); v.FocusList(); } }, null, canModify);
        }

        // files
        Cmd("New folder", "New", NewFolder, "Ctrl+Shift+N", available.New);
        foreach (var t in _templates.Where(t => t.Enabled))
        {
            var template = t;
            Cmd($"New {t.Name}", "New", () => CreateFromTemplate(template), t.Extension, available.New);
        }
        Cmd("Manage New-menu templates…", "New", ManageTemplates);
        Cmd("Rename", "Edit", RenameSelection, "F2", available.Rename);
        Cmd("Batch rename…", "Edit", BatchRenameSelection, "F2", available.Delete);
        Cmd("Copy", "Edit", () => CopySelection(false), "Ctrl+C", available.Copy);
        Cmd("Cut", "Edit", () => CopySelection(true), "Ctrl+X", available.Cut);
        Cmd("Paste", "Edit", Paste, "Ctrl+V", available.Paste);
        Cmd("Delete to Recycle Bin", "Edit", () => DeleteSelection(false), "Del", available.Delete);
        Cmd("Copy path", "Edit", () => SetClipboardText(string.Join(Environment.NewLine, sel.Select(s => s.FullPath))), "Ctrl+Shift+C", sel.Count > 0);
        Cmd(_undo.CanUndo ? $"Undo: {_undo.NextDescription}" : "Undo", "Edit", Undo, "Ctrl+Z", _undo.CanUndo);
        Cmd("Share on this network…", "Tools", () => ShareWith(sel.Select(s => s.FullPath)), null, sel.Count > 0);
        Cmd("Active shares…", "Tools", OpenShares);
        Cmd("Check for updates…", "Application", () => OpenUpdates());
        Cmd("Tags…", "Edit", () => EditTags(sel.Select(s => s.FullPath).ToList()), null, sel.Count > 0);
        Cmd("Properties", "Edit", () => { if (view?.FocusedItem is { IsVirtual: false } f) Shell.Properties(f.FullPath, this); }, "Alt+Enter", view?.FocusedItem != null);

        // tools
        Cmd("Find duplicate files…", "Tools", () => FindDuplicates(canModify ? new[] { view!.CurrentPath } : Array.Empty<string>()));
        Cmd("Folder sizes…", "Tools", () => FolderSizes(canModify ? view!.CurrentPath : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Cmd("Compare folders…", "Tools", () => CompareFolders(canModify ? view!.CurrentPath : "", ""));
        Cmd("Compare files", "Tools", CompareSelection, "Ctrl+D", available.Compare);
        Cmd("Checksums…", "Tools", () => Checksums(files), null, files.Count > 0);
        Cmd("Convert…", "Tools", () => Convert(files), null, files.Count > 0 && files.All(f => Core.Imaging.ImageService.CanDecode(f) || FileKinds.KindFor(f, false) is PreviewKind.Media or PreviewKind.NativeMedia));
        Cmd("PDF tools…", "Tools", () => OpenPdfTools(files[0]), null, files.Count == 1 && files[0].EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
        Cmd("Merge PDFs…", "Tools", () => MergePdfs(files), null, files.Count > 1 && files.All(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)));
        Cmd("Compress…", "Archive", () => CompressWithDialog(view!, sel), null, canModify && sel.Count > 0);
        Cmd("Extract to…", "Archive", () => ExtractWithDialog(files[0], null, null), null, files.Count == 1 && ArchiveFormats.IsArchive(files[0]));
        Cmd("Open terminal here", "Tools", () => Shell.OpenTerminal(view!.CurrentPath), null, canModify);
        var ideFolder = sel.Count == 1 && sel[0].IsDirectory ? sel[0].FullPath : canModify ? view!.CurrentPath : null;
        if (ideFolder != null)
            foreach (var ide in IdeCatalog.MenuEntries(Settings.Ides))
            {
                var e = ide;
                Cmd("Open in " + e.Name, "IDE", () => OpenInIde(e, ideFolder), null, true, Path.GetFileName(ideFolder.TrimEnd('\\')));
            }
        Cmd("Open in File Explorer", "Tools", () => Shell.ShowInExplorer(view!.CurrentPath), null, canModify);
        Cmd("Settings…", ProductBranding.Name, OpenSettings);
        var actionItems = sel.Select(s => (s.FullPath, s.IsDirectory)).ToList();
        foreach (var act in Settings.CustomActions.Where(a => a.AppliesTo(actionItems)))
        {
            var a = act;
            Cmd(a.Name, "Action", () => RunCustomAction(a, actionItems.Select(i => i.FullPath).ToList()));
        }
        foreach (var clip in _clipHistory) { var e = clip; Cmd($"Copy again: {e.Title}", "Clipboard", () => RestoreClipboard(e), null, true, e.When.ToString("t")); }
        Cmd("Automation rules…", "Tools", OpenAutomation);
        Cmd("Add this folder to Favourites", "Places", () => AddFavourite(view!.CurrentPath), null, canModify);

        // places
        if (includePlaces)
        {
            foreach (var f in Settings.Favourites) Place(f, "Favourite");
            foreach (var r in Settings.RecentFolders.Where(Directory.Exists)) Place(r, "Recent");
            foreach (var (name, path) in new[]
                 {
                     ("Home", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), ("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.Desktop)),
                     ("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)), ("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
                     ("Downloads", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")),
                 })
                if (Directory.Exists(path)) Place(path, "Place", name);
            foreach (var d in DriveInfo.GetDrives()) Place(d.Name, "Drive", d.Name.TrimEnd('\\'));
        }
        return list.GroupBy(e => e.Category + "|" + e.Title).Select(g => g.First());

        void Place(string path, string category, string? title = null)
        {
            var name = title ?? (Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : path);
            list.Add(new PaletteEntry(name, category, () => { view?.Navigate(path); view?.FocusList(); }, null, path));
        }
    }
}
