using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Fyltrix.UI;

/// <summary>View modes: Details, or thumbnails at four sizes. Each folder keeps its own (Settings.FolderViews); folders without
/// one use the default (Settings.ViewTileSize; 0 = Details). Tabs showing the same folder show it the same way.</summary>
public sealed partial class MainWindow
{
    private void ShowSortMenu(UIElement anchor)
    {
        if (!CanExecute("sort") || View is not { } view) return;
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (var (label, column) in new[] { ("Name", SortColumn.Name), ("Date modified", SortColumn.Modified), ("Type", SortColumn.Type), ("Size", SortColumn.Size) })
        {
            var col = column;
            menu.Items.Add(Theme.MenuItem(label, view.Sort == col ? "\uE73E" : null, () => { if (CanExecute("sort")) view.SortBy(col, view.Sort == col ? !view.Ascending : true); }));
        }
        menu.IsOpen = true;
    }

    private static readonly (string name, int size, string keys)[] ViewChoices =
    {
        ("Extra large icons", 256, "Ctrl+Shift+1"),
        ("Large icons", 160, "Ctrl+Shift+2"),
        ("Medium icons", 96, "Ctrl+Shift+3"),
        ("Small icons", 48, "Ctrl+Shift+4"),
        ("Details", 0, "Ctrl+Shift+6"),
    };

    private void ShowViewMenu(UIElement anchor)
    {
        var menu = BuildViewMenu();
        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>The View menu for the open folder: its current view ticked.</summary>
    internal ContextMenu BuildViewMenu()
    {
        var view = View;
        int current = view != null ? Settings.ViewFor(ViewKey(view)) : Settings.ViewTileSize;
        bool own = view != null && Settings.HasOwnView(ViewKey(view));
        var menu = new ContextMenu();
        foreach (var (name, size, keys) in ViewChoices)
            menu.Items.Add(Theme.MenuItem(name, current == size ? "" : null, () => SetView(size), keys));
        menu.Items.Add(new Separator());
        menu.Items.Add(GroupByDateMenuItem());
        menu.Items.Add(new Separator());
        menu.Items.Add(Theme.MenuItem($"Reset this folder's view (default: {ViewName(Settings.ViewTileSize)})", "", ResetFolderView, enabled: own));
        menu.Items.Add(new MenuItem { Header = "Tip: Ctrl + mouse wheel changes the size", IsEnabled = false });
        return menu;
    }

    internal static string ViewName(int size) => ViewChoices.FirstOrDefault(c => c.size == size).name ?? "Details";

    /// <summary>The key the open location's view is kept under (search results share one).</summary>
    internal static string ViewKey(FolderView view) => view.IsSearchResults ? Core.AppSettings.SearchViewKey : Core.AppSettings.ViewKey(view.CurrentPath);

    /// <summary>Sets the open folder's view (0 = Details; otherwise the thumbnail size) and remembers it for that folder only.</summary>
    internal void SetView(int tileSize)
    {
        if (View == null || !CanExecute("view") || !Core.AppSettings.IsView(tileSize)) return;
        if (!Settings.RememberFolderViews)
        {
            // per-folder views switched off: the view is one setting for every folder, as before
            Settings.ViewTileSize = tileSize;
            Settings.Save();
            foreach (var t in _tabs) ApplyView(t.View);
            SetStatus($"{ViewName(tileSize)} for all folders.");
            return;
        }
        var key = ViewKey(View);
        Settings.SetFolderView(key, tileSize);
        Settings.Save();
        ApplyViewTo(key);
        SetStatus($"{ViewName(tileSize)} for this folder.");
    }

    /// <summary>The open folder goes back to the default view.</summary>
    internal void ResetFolderView()
    {
        if (View == null) return;
        var key = ViewKey(View);
        if (!Settings.ResetFolderView(key)) return;
        Settings.Save();
        ApplyViewTo(key);
        SetStatus($"This folder uses the default view again ({ViewName(Settings.ViewTileSize)}).");
    }

    /// <summary>Settings → "Reset all folder views": every folder follows the default again. Only Fyltrix' settings change.</summary>
    internal void ResetAllFolderViews()
    {
        Settings.ResetAllFolderViews();
        Settings.Save();
        foreach (var t in _tabs) ApplyView(t.View);
        SetStatus("All folders use the default view again.");
    }

    /// <summary>Re-applies the view in every tab showing this folder (other tabs are left alone; nothing navigates or reloads).</summary>
    private void ApplyViewTo(string key)
    {
        foreach (var t in _tabs.Where(t => string.Equals(ViewKey(t.View), key, StringComparison.OrdinalIgnoreCase)))
            ApplyView(t.View);
    }

    /// <summary>"Group by date modified" for the open folder (Details and thumbnails; remembered per folder).</summary>
    internal MenuItem GroupByDateMenuItem()
    {
        var view = View;
        bool can = view is { CanGroupByDate: true };
        bool on = view != null && can && Settings.IsGroupedByDate(view.CurrentPath);
        return Theme.MenuItem("Group by date modified", on ? "" : null, () => SetGroupByDate(view!.CurrentPath, !on), enabled: can);
    }

    internal void SetGroupByDate(string folder, bool on)
    {
        Settings.SetGroupedByDate(folder, on);
        Settings.Save();
        foreach (var t in _tabs.Where(t => Core.AppSettings.SameFolder(t.View.CurrentPath, folder)))
            t.View.RefreshGrouping();
        SetStatus(on ? "Grouped by date modified (Today, Yesterday, Last week…)." : "No longer grouped by date.");
    }

    /// <summary>Shows a tab the way its folder is set (its own view, or the default).</summary>
    private void ApplyView(FolderView view)
    {
        int size = Settings.ViewFor(ViewKey(view));
        view.SetViewMode(size == 0 ? ViewMode.Details : ViewMode.Tiles, size == 0 ? view.TileSize : size);
    }

    internal void ReapplyViewsForTest() { foreach (var t in _tabs) ApplyView(t.View); }
    internal IReadOnlyList<FolderView> TabViews => _tabs.Select(t => t.View).ToList();
    internal void ActivateTabForTest(int index) => ActivateTab(_tabs[index]);

    /// <summary>Tests: the view (0 = Details, else the thumbnail size) a tab is showing.</summary>
    internal static int ShownView(FolderView view) => view.Mode == ViewMode.Details ? 0 : view.TileSize;

    private string? _ffmpegForThumbs;
    private DateTime _ffmpegCheckedUtc;

    /// <summary>FFmpeg for video frames Windows can't thumbnail; looked up at most once a minute (it may be installed while Fyltrix runs).</summary>
    private string? FfmpegForThumbnails()
    {
        lock (ViewChoices) // called from the thumbnail workers
        {
            if (_ffmpegForThumbs != null && File.Exists(_ffmpegForThumbs)) return _ffmpegForThumbs;
            if (DateTime.UtcNow - _ffmpegCheckedUtc < TimeSpan.FromMinutes(1)) return null;
            _ffmpegCheckedUtc = DateTime.UtcNow;
            return _ffmpegForThumbs = Core.Conversion.Ffmpeg.Locate(Settings.FfmpegPath);
        }
    }

    private void HookView(FolderView view)
    {
        view.GroupByDateFor = folder => Settings.IsGroupedByDate(folder);
        Core.Media.ThumbnailService.FfmpegPath = FfmpegForThumbnails;
        view.LocationChanging = ApplyView; // each folder opens in its own view
        ApplyView(view);
        view.TileSizeChanged += (_, size) => // Ctrl + mouse wheel: for this folder only
        {
            if (!Settings.RememberFolderViews) { Settings.ViewTileSize = size; Settings.Save(); foreach (var t in _tabs.Where(t => t.View != view)) ApplyView(t.View); return; }
            var key = ViewKey(view);
            Settings.SetFolderView(key, size);
            Settings.Save();
            foreach (var t in _tabs.Where(t => t.View != view && string.Equals(ViewKey(t.View), key, StringComparison.OrdinalIgnoreCase)))
                ApplyView(t.View);
        };
    }

    private bool HandleViewKeys(System.Windows.Input.Key key, bool ctrl, bool shift)
    {
        if (!ctrl || !shift) return false;
        int size = key switch
        {
            System.Windows.Input.Key.D1 => 256,
            System.Windows.Input.Key.D2 => 160,
            System.Windows.Input.Key.D3 => 96,
            System.Windows.Input.Key.D4 => 48,
            System.Windows.Input.Key.D6 => 0,
            _ => -1,
        };
        if (size < 0) return false;
        SetView(size);
        return true;
    }
}
