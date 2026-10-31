using System.Windows;
using System.Windows.Controls;

namespace Fyltrix.UI;

/// <summary>A right-click menu action the user can hide (Settings → Context menu). It matches the menu items it stands for by
/// their text, since some carry details ("Extract to 'Photos\'", "Merge 3 PDFs…").</summary>
public sealed record MenuAction(string Id, string Name, Func<string, bool> Matches);

/// <summary>The right-click actions that can be hidden. Open and Refresh always stay.</summary>
public static class ContextMenuActions
{
    private static Func<string, bool> Is(params string[] headers) => h => headers.Any(x => string.Equals(h, x, StringComparison.OrdinalIgnoreCase));
    private static Func<string, bool> Starts(params string[] prefixes) => h => prefixes.Any(p => h.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    public static readonly IReadOnlyList<MenuAction> All = new MenuAction[]
    {
        new("newtab", "Open in new tab", Is("Open in new tab")),
        new("openwith", "Open with…", Is("Open with…", "Open with default app")),
        new("ide", "Open in IDE (VS Code, IntelliJ…)", h => h.StartsWith("Open in ", StringComparison.OrdinalIgnoreCase)
                                                        && !Is("Open in new tab", "Open in File Explorer")(h)),
        new("explorer", "Show / open in File Explorer", Is("Show in File Explorer", "Open in File Explorer")),
        new("terminal", "Open terminal here", Is("Open terminal here")),
        new("location", "Open file location", Is("Open file location")),
        new("quickview", "Quick view", Is("Quick view")),
        new("compare", "Compare", Starts("Compare ")),
        new("cut", "Cut", Is("Cut")),
        new("copy", "Copy", Is("Copy")),
        new("paste", "Paste", Is("Paste", "Paste into folder")),
        new("copypath", "Copy path", Is("Copy path", "Copy folder path", "Copy archive path")),
        new("rename", "Rename", Is("Rename")),
        new("delete", "Delete", Is("Delete")),
        new("deletepermanently", "Delete permanently", Is("Delete permanently")),
        new("unblock", "Unblock (downloaded-file mark)", Starts("Unblock")),
        new("favourite", "Add to Favourites", Is("Add to Favourites", "Add all to Favourites", "Add folder to Favourites", "Remove from Favourites")),
        new("compress", "Compress / Archive", Is("Compress", "Archive")),
        new("extract", "Extract", Starts("Extract ")),
        new("tags", "Tags and labels", Is("Tags…", "Label")),
        new("batchrename", "Batch rename…", Is("Batch rename…")),
        new("duplicates", "Find duplicates…", Starts("Find duplicates")),
        new("foldersizes", "Folder sizes…", Is("Folder sizes…")),
        new("checksums", "Checksums…", Is("Checksums…")),
        new("convert", "Convert…", Is("Convert…")),
        new("pdf", "PDF tools", h => Is("PDF tools…")(h) || h.StartsWith("Merge ", StringComparison.OrdinalIgnoreCase) && h.Contains("PDF")),
        new("share", "Share on this network…", Is("Share on this network…")),
        new("actions", "Custom actions", Is("Actions")),
        new("advancedsearch", "Advanced search here…", Is("Advanced search here…")),
        new("properties", "Properties", Is("Properties")),
    };

    public static string? IdFor(string header) => All.FirstOrDefault(a => a.Matches(header))?.Id;
}

public sealed partial class MainWindow
{
    /// <summary>Takes out the actions hidden in Settings, then tidies separators (none first, last or twice in a row).</summary>
    internal ContextMenu FilterMenu(ContextMenu menu)
    {
        if (Settings.HiddenMenuActions.Count > 0)
        {
            var hidden = new HashSet<string>(Settings.HiddenMenuActions, StringComparer.OrdinalIgnoreCase);
            foreach (var mi in menu.Items.OfType<MenuItem>().ToList())
                if (mi.Header is string h && ContextMenuActions.IdFor(h) is { } id && hidden.Contains(id))
                    menu.Items.Remove(mi);
        }
        object? previous = null;
        foreach (var item in menu.Items.Cast<object>().ToList())
        {
            if (item is Separator && (previous == null || previous is Separator)) menu.Items.Remove(item);
            else previous = item;
        }
        while (menu.Items.Count > 0 && menu.Items[^1] is Separator) menu.Items.RemoveAt(menu.Items.Count - 1);
        return menu;
    }

    // ------------------------------------------------------------------ toolbar

    /// <summary>The toolbar's buttons and separators in order (id null for a separator), so Settings can hide some.</summary>
    private readonly List<(string? Id, string Name, FrameworkElement Element)> _toolbar = new();
    private readonly StackPanel _toolbarExtras = new() { Orientation = Orientation.Horizontal };

    /// <summary>The toolbar buttons Settings lists (id, name).</summary>
    internal IReadOnlyList<(string Id, string Name)> ToolbarButtons => _toolbar.Where(t => t.Id != null && t.Id != "settings").Select(t => (t.Id!, t.Name)).ToList();

    /// <summary>Commands that can be added to the toolbar: the command palette's (every existing command), by title.</summary>
    internal IReadOnlyList<string> ToolbarCommandChoices() =>
        PaletteEntries("").Where(e => e.Category is not ("Go to" or "Place" or "Drive" or "Recent" or "Favourite"))
            .Select(e => e.Title).Distinct().OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>Shows and hides toolbar buttons as Settings says, and puts the extra commands at the end. Back, Forward, Up,
    /// Refresh and the address bar are in the navigation bar and always there.</summary>
    internal void ApplyToolbar()
    {
        var hidden = new HashSet<string>(Settings.HiddenToolbarButtons, StringComparer.OrdinalIgnoreCase);
        foreach (var (id, _, el) in _toolbar)
            if (id != null)
            {
                el.Visibility = id != "settings" && hidden.Contains(id) ? Visibility.Collapsed : Visibility.Visible;
                if (el is Button button) ApplyToolbarLabel(button, id);
            }
        // a separator shows only between two visible groups
        bool seenButton = false;
        FrameworkElement? pendingSep = null;
        foreach (var (id, _, el) in _toolbar)
        {
            if (id == null)
            {
                el.Visibility = Visibility.Collapsed;
                if (seenButton) pendingSep = el;
                continue;
            }
            if (el.Visibility != Visibility.Visible) continue;
            if (pendingSep != null) { pendingSep.Visibility = Visibility.Visible; pendingSep = null; }
            seenButton = true;
        }

        _toolbarExtras.Children.Clear();
        foreach (var title in Settings.ToolbarExtras)
        {
            bool share = title is "Active shares…" or "Share on this network…";
            var b = Theme.SmallButton(share ? "\uE72D" : "\uE710", title, share ? "Share" : title + " (added in Settings → Toolbar)");
            b.Tag = title;
            b.MinWidth = 34;
            b.MinHeight = 30;
            b.HorizontalContentAlignment = HorizontalAlignment.Center;
            System.Windows.Automation.AutomationProperties.SetName(b, title);
            ApplyToolbarLabel(b, "extra");
            var t = title;
            b.Click += (_, _) => RunCommand(t);
            _toolbarExtras.Children.Add(b);
        }
        _toolbarExtras.Visibility = _toolbarExtras.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateToolbarStates();
    }

    private void ApplyToolbarLabel(Button button, string id)
    {
        if (button.Content is StackPanel panel && panel.Children.Count > 1)
            panel.Children[1].Visibility = Settings.ShowToolbarLabel(id) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Runs a command palette command by its title, as it applies right now (selection, folder…).</summary>
    internal void RunCommand(string title)
    {
        var entry = PaletteEntries("").FirstOrDefault(e => string.Equals(e.Title, title, StringComparison.OrdinalIgnoreCase));
        if (entry == null) { SetStatus($"'{title}' isn't available here."); return; }
        entry.Run();
    }

    internal IReadOnlyList<(string? Id, FrameworkElement Element)> ToolbarForTest => _toolbar.Select(t => (t.Id, t.Element)).ToList();
    internal StackPanel ToolbarExtrasForTest => _toolbarExtras;
}
