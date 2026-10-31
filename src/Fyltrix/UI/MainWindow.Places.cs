using Fyltrix.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Fyltrix.UI;

/// <summary>Favourites and Recent: add, remove one, clear all. Only Fyltrix' own lists change — never the folders.</summary>
public sealed partial class MainWindow
{
    private Button? _favButton;

    internal bool IsFavourite(string path) => Settings.Favourites.Any(f => Core.AppSettings.SameFolder(f, path));

    private void ToggleFavourite(string path)
    {
        if (IsFavourite(path)) RemoveFavourite(path);
        else AddFavourite(path);
    }

    internal void RemoveFavourite(string path)
    {
        if (Settings.Favourites.RemoveAll(f => Core.AppSettings.SameFolder(f, path)) == 0) return;
        Settings.Save();
        PlacesChanged();
        SetStatus($"Removed '{Path.GetFileName(path.TrimEnd('\\'))}' from Favourites (the folder itself is unchanged).");
    }

    internal void ClearFavourites(bool confirm = true)
    {
        if (Settings.Favourites.Count == 0) return;
        if (confirm && !Dialogs.Confirm(this, "Clear Favourites",
                $"Remove all {Settings.Favourites.Count} favourites?\n\nOnly the list is cleared; the folders themselves are not touched.")) return;
        Settings.Favourites.Clear();
        Settings.Save();
        PlacesChanged();
        SetStatus("Favourites cleared.");
    }

    internal void RemoveRecent(string path)
    {
        if (Settings.RecentFolders.RemoveAll(f => Core.AppSettings.SameFolder(f, path)) == 0) return;
        Settings.Save();
        PlacesChanged();
    }

    internal void ClearRecent(bool confirm = true)
    {
        if (Settings.RecentFolders.Count == 0) return;
        if (confirm && !Dialogs.Confirm(this, "Clear Recent", $"Clear the list of recent folders?\n\nOnly {ProductBranding.Name}'s history is cleared; no files or folders are touched.")) return;
        Settings.RecentFolders.Clear();
        Settings.Save();
        PlacesChanged();
        SetStatus("Recent cleared.");
    }

    /// <summary>Favourites or Recent changed: redraw the sidebar and the toolbar star.</summary>
    private void PlacesChanged()
    {
        RebuildSidebar();
        UpdateFavouriteButton();
    }

    /// <summary>The toolbar star is filled (and removes) when the open folder is a favourite, outlined (and adds) otherwise.</summary>
    private void UpdateFavouriteButton()
    {
        if (_favButton == null) return;
        var path = View?.CurrentPath;
        bool canFavourite = View != null && !View.IsThisPC && !View.IsSearchResults && !View.IsArchive;
        bool on = canFavourite && IsFavourite(path!);
        _favButton.IsEnabled = canFavourite;
        if (_favButton.Content is StackPanel { Children: [TextBlock icon, ..] })
        {
            icon.Text = on ? LocationIcons.Favourite : LocationIcons.FavouriteOutline;
            icon.Foreground = Theme.Brush(on ? LocationIcons.Brush : "Fg1");
        }
        _favButton.ToolTip = on ? "Remove this folder from Favourites" : "Add this folder to Favourites";
    }

    internal Button? FavouriteButton => _favButton;

    // ---------- benchmark / test hooks ----------
    internal Preview.PreviewPane PreviewForTest => _preview;
    internal int TabCountForTest => _tabs.Count;
    internal void CloseTabForTest(int index) => CloseTab(_tabs[index]);
    internal void SetPreviewVisibleForTest(bool visible) => SetPreviewVisible(visible);

    internal List<Visibility> TabIconsVisibility => _tabs.Select(t => t.Icon.Visibility).ToList();

    /// <summary>Tests: each tab's icon glyph and brush.</summary>
    internal List<(string Glyph, Brush Brush)> TabIcons => _tabs.Select(t => (t.Icon.Text, t.Icon.Foreground)).ToList();
    internal (string Glyph, Brush Brush) ActiveTabIcon => (_active!.Icon.Text, _active.Icon.Foreground);

    /// <summary>A sidebar section title, with an optional small action at its right (e.g. Clear).</summary>
    /// <summary>Adds a section title (click it to fold the section up). Returns whether the section is open, i.e. whether its
    /// rows should be added.</summary>
    private bool Section(string text, string? actionTip = null, Action? action = null)
    {
        bool open = !Settings.CollapsedSections.Contains(text, StringComparer.OrdinalIgnoreCase);
        var title = new TextBlock
        {
            Text = text.ToUpperInvariant(), Foreground = Theme.Brush("Fg3"), FontSize = 11, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var chevron = Theme.Icon(open ? "\uE70D" : "\uE76C", 8, "Fg3");
        chevron.Width = 14;
        chevron.VerticalAlignment = VerticalAlignment.Center;
        var row = new DockPanel { Margin = new Thickness(4, 10, 2, open ? 0 : 2), LastChildFill = true, Background = Brushes.Transparent, Cursor = Cursors.Hand, MinHeight = 22 };
        row.ToolTip = open ? $"Hide the {text} list" : $"Show the {text} list";
        DockPanel.SetDock(chevron, Dock.Left);
        row.Children.Add(chevron);
        row.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && VisualTree.FindAncestor<Button>(d) != null) return; // the Clear button
            ToggleSection(text);
            e.Handled = true;
        };
        if (action == null || !open)
        {
            row.Children.Add(title);
            _sidebar.Children.Add(row);
            return open;
        }
        var clear = new Button
        {
            Content = Theme.Icon(LocationIcons.Clear, 11, "Fg3"), ToolTip = actionTip, Padding = new Thickness(5, 3, 5, 3),
            Focusable = false, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand,
        };
        System.Windows.Automation.AutomationProperties.SetName(clear, actionTip?.TrimEnd('…') ?? "");
        clear.Click += (_, _) => action();
        DockPanel.SetDock(clear, Dock.Right);
        row.Children.Add(clear);
        row.Children.Add(title);
        // always there, quiet until the pointer is over the section
        clear.Opacity = 0.55;
        row.Background = Brushes.Transparent;
        row.MouseEnter += (_, _) => clear.Opacity = 1;
        row.MouseLeave += (_, _) => clear.Opacity = 0.55;
        _sidebar.Children.Add(row);
        return open;
    }

    /// <summary>Folds a sidebar section up or opens it again (remembered when "Remember sidebar state" is on).</summary>
    internal void ToggleSection(string section)
    {
        if (Settings.CollapsedSections.RemoveAll(s => string.Equals(s, section, StringComparison.OrdinalIgnoreCase)) == 0)
            Settings.CollapsedSections.Add(section);
        if (Settings.RememberSidebarState) Settings.Save();
        RebuildSidebar();
    }

    internal void RebuildSidebarForTest() => RebuildSidebar();

    /// <summary>Tests: the sidebar's section titles, top to bottom.</summary>
    internal List<string> SidebarSections => VisualTree.FindDescendants<TextBlock>(_sidebar).Where(t => t.FontSize == 11 && t.FontWeight == FontWeights.SemiBold).Select(t => t.Text).ToList();

    /// <summary>Tests: the sidebar's rows (icon glyph, text) under a section, top to bottom.</summary>
    internal List<(string Glyph, string Text, Brush Brush)> SidebarRows(string section)
    {
        var rows = new List<(string, string, Brush)>();
        bool inside = false;
        foreach (UIElement child in _sidebar.Children)
        {
            var heading = child is TextBlock { FontSize: 11 } t ? t.Text : child is DockPanel dp && dp.Children.OfType<TextBlock>().FirstOrDefault(x => x.FontSize == 11) is { } t2 ? t2.Text : null;
            if (heading != null) { inside = heading == section.ToUpperInvariant(); continue; }
            var line = child is Border { Child: StackPanel { Orientation: Orientation.Vertical, Children: [StackPanel first, ..] } } ? first : (child as Border)?.Child as StackPanel;
            if (inside && line is { Children: [TextBlock icon, TextBlock text, ..] })
                rows.Add((icon.Text, text.Text, icon.Foreground));
        }
        return rows;
    }

    /// <summary>Tests: the usage bar and text under a drive in the sidebar (null when it shows none), and the row's tooltip.</summary>
    internal (ProgressBar Bar, string Text, object? Tip)? SidebarDriveUsage(string name)
    {
        foreach (var row in _sidebar.Children.OfType<Border>())
            if (row.Child is StackPanel { Orientation: Orientation.Vertical, Children: [StackPanel { Children: [_, TextBlock t, ..] }, StackPanel usage] } && t.Text == name)
                return (usage.Children.OfType<ProgressBar>().Single(), usage.Children.OfType<TextBlock>().Single().Text, row.ToolTip);
        return null;
    }

    internal Task RefreshSidebarDrivesForTest() => RefreshSidebarDrives();

    /// <summary>Tests: the section's clear button.</summary>
    internal Button? SectionAction(string section) =>
        _sidebar.Children.OfType<DockPanel>().FirstOrDefault(d => d.Children.OfType<TextBlock>().Any(t => t.FontSize == 11 && t.Text == section.ToUpperInvariant()))?.Children.OfType<Button>().FirstOrDefault();
}
