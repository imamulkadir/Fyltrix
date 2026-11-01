using System.Windows;
using System.Windows.Controls;
using Fyltrix.Core;
using Fyltrix.Core.Templates;

namespace Fyltrix.UI;

/// <summary>What the Settings window needs from the main window: catalogues to choose from and actions that act at once.</summary>
public sealed record SettingsHost(
    IReadOnlyList<(string Id, string Name)> ToolbarButtons,
    IReadOnlyList<string> ToolbarCommands,
    string? CurrentFolder,
    Func<bool> CurrentFolderHasView,
    Action ResetCurrentFolderView,
    Action ClearRecent,
    Action ClearFavourites)
{
    public Func<string>? ReadUpdateStatus { get; init; }
    public Func<Task>? CheckForUpdates { get; init; }
    public Action? ShowUpdateDetails { get; init; }
    public Action<Action>? SubscribeUpdateStatus { get; init; }
    public Action<Action>? UnsubscribeUpdateStatus { get; init; }
}

/// <summary>Fyltrix settings, by area: Appearance, Navigation, Sidebar, Views, Context menu, Toolbar, Search, Advanced.
/// Everything applies when Save is pressed (the theme at the next start); the Clear / Reset buttons act at once.</summary>
public sealed partial class SettingsWindow : DialogWindow
{
    private readonly AppSettings _settings;
    private readonly SettingsHost? _host;
    private readonly List<IdeEntry> _ides;
    private readonly ListBox _ideList = new() { Height = 170 };
    private readonly Button _ideEdit, _ideRemove;
    private readonly List<FileTemplate> _templates;
    private readonly IReadOnlyList<ShellNewEntry> _windowsTypes;

    /// <summary>Keyed by template id, or "win:" + extension for a Windows-registered type.</summary>
    internal Dictionary<string, CheckBox> NewTypeChecks { get; } = new();
    internal IReadOnlyList<IdeEntry> IdeItems => _ides;
    internal TabControl Tabs { get; } = new();
    internal ComboBox DefaultView { get; }
    internal Button ResetViewsButton { get; }
    /// <summary>Asks before "Reset all folder views" (tests answer it themselves).</summary>
    internal Func<int, bool> ConfirmResetViews { get; set; }
    /// <summary>Validates and saves everything (what the Save button does before closing).</summary>
    internal Func<bool> TrySave { get; }
    internal Action RestoreToolbarDefaults { get; private set; } = () => { };

    internal void SelectTab(string header) => Tabs.SelectedItem = Tabs.Items.OfType<TabItem>().First(t => (string)t.Header == header);

    /// <param name="templates">The New menu's templates (copies are edited); null loads them.</param>
    /// <param name="windowsTypes">Windows' New types not covered by a template.</param>
    /// <param name="host">The main window's catalogues and actions (null in some tests: those parts are then left out).</param>
    public SettingsWindow(Window owner, AppSettings s, IReadOnlyList<FileTemplate>? templates = null, IReadOnlyList<ShellNewEntry>? windowsTypes = null,
        SettingsHost? host = null)
        : base(owner, "Settings", 760)
    {
        _settings = s;
        _host = host;
        _ides = IdeCatalog.Merge(s.Ides, IdeCatalog.DetectedWait());
        _templates = (templates ?? FileTemplates.Load()).Select(t => t.Clone()).ToList();
        _windowsTypes = windowsTypes ?? Array.Empty<ShellNewEntry>();
        var err = Dialogs.Note("", "Danger");
        var saves = new List<Func<string?>>(); // each page adds how it saves; a returned text is a problem to show

        // ---------------------------------------------------------------- Appearance
        var look = Page("Appearance");
        var theme = Dialogs.Combo(new[] { ("Dark", ThemeMode.Dark), ("Light", ThemeMode.Light), ("Same as Windows", ThemeMode.System) }, s.Theme);
        theme.Width = 220; theme.HorizontalAlignment = HorizontalAlignment.Left;
        look.Children.Add(Dialogs.Field($"Theme (applies the next time {ProductBranding.Name} starts)", theme));
        var ext = Check(look, "Show file extensions (report.pdf rather than report)", s.ShowExtensions);
        var hidden = Check(look, "Show hidden files", s.ShowHidden);
        var tabIcons = Check(look, "Show folder icons in tabs", s.ShowTabIcons);
        var previewStart = Dialogs.Combo(new[] { ($"As it was when {ProductBranding.Name} closed", PreviewStartup.Remember), ("Always shown", PreviewStartup.Show), ("Always hidden", PreviewStartup.Hide) }, s.PreviewAtStartup);
        previewStart.Width = 260; previewStart.HorizontalAlignment = HorizontalAlignment.Left;
        look.Children.Add(Spaced(Dialogs.Field($"Preview pane when {ProductBranding.Name} starts", previewStart)));
        look.Children.Add(Dialogs.Note("A hidden preview pane gives its memory back after half a minute; showing it again takes about a second the first time."));
        saves.Add(() =>
        {
            s.Theme = Dialogs.Selected<ThemeMode>(theme);
            s.ShowExtensions = ext.IsChecked == true;
            s.ShowHidden = hidden.IsChecked == true;
            s.ShowTabIcons = tabIcons.IsChecked == true;
            s.PreviewAtStartup = Dialogs.Selected<PreviewStartup>(previewStart);
            return null;
        });

        // ---------------------------------------------------------------- Navigation and tabs
        var nav = Page("Navigation");
        var restore = Check(nav, $"Reopen the tabs that were open when {ProductBranding.Name} closed", s.RestoreTabs);
        var startup = new ComboBox { IsEditable = true, Width = 360, HorizontalAlignment = HorizontalAlignment.Left, Text = StartupText(s.StartupFolder) };
        StartupFolderBox = startup;
        foreach (var c in new[] { "Desktop", "This PC", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), AppSettings.DownloadsFolder,
                                  Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) })
            startup.Items.Add(c);
        var browse = Dialogs.Button("Browse…");
        browse.Margin = new Thickness(8, 0, 0, 0);
        browse.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = $"Start {ProductBranding.Name} in" };
            if (dlg.ShowDialog(this) == true) startup.Text = dlg.FolderName;
        };
        var startRow = new StackPanel { Orientation = Orientation.Horizontal };
        startRow.Children.Add(startup);
        startRow.Children.Add(browse);
        nav.Children.Add(Spaced(Dialogs.Field("Start in (when tabs aren't reopened, or none were open)", startRow)));
        var newTab = Dialogs.Combo(new[] { ("The folder of the current tab", NewTabTarget.CurrentFolder), ("The start folder above", NewTabTarget.StartupFolder), ("This PC", NewTabTarget.ThisPC) }, s.NewTabOpens);
        newTab.Width = 260; newTab.HorizontalAlignment = HorizontalAlignment.Left;
        nav.Children.Add(Dialogs.Field("New tabs open in", newTab));
        var confirmTabs = Check(nav, "Ask before closing several tabs at once (Close other tabs, or closing the window)", s.ConfirmCloseTabs);
        saves.Add(() =>
        {
            var text = startup.Text.Trim();
            string folder = text is "" or "Desktop" ? "" : text.Equals("This PC", StringComparison.OrdinalIgnoreCase) ? "This PC" : text;
            if (folder.Length > 0 && folder != "This PC" && !Directory.Exists(folder)) return $"The start folder '{text}' doesn't exist.";
            s.StartupFolder = folder;
            s.RestoreTabs = restore.IsChecked == true;
            s.NewTabOpens = Dialogs.Selected<NewTabTarget>(newTab);
            s.ConfirmCloseTabs = confirmTabs.IsChecked == true;
            return null;
        });

        // ---------------------------------------------------------------- Sidebar: Quick access, Favourites, Recent
        var side = Page("Sidebar");
        side.Children.Add(Dialogs.Heading("Sections"));
        var showQuick = Check(side, "Quick access", s.ShowQuickAccess, first: true);
        var showFav = Check(side, "Favourites", s.ShowFavourites);
        var showRecent = Check(side, "Recent", s.ShowRecent);
        var remember = Check(side, "Keep sections folded up after a restart (click a section's title to fold it)", s.RememberSidebarState);
        side.Children.Add(Spaced(Dialogs.Heading("Quick access")));
        var placeChecks = new List<(string Name, CheckBox Box)>();
        var places = new WrapPanel();
        foreach (var p in LocationIcons.QuickAccess())
        {
            var cb = new CheckBox { Content = p.Name, IsChecked = !s.HiddenQuickAccess.Contains(p.Name, StringComparer.OrdinalIgnoreCase), Margin = new Thickness(0, 4, 18, 4), MinWidth = 110 };
            placeChecks.Add((p.Name, cb));
            places.Children.Add(cb);
        }
        side.Children.Add(places);
        side.Children.Add(Spaced(Dialogs.Heading("Recent and Favourites")));
        var track = Check(side, "Remember the folders I visit (the Recent list)", s.TrackRecent, first: true);
        if (host != null)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            var clearRecent = Dialogs.Button("Clear Recent…");
            clearRecent.Click += (_, _) => host.ClearRecent();
            var clearFav = Dialogs.Button("Clear all Favourites…");
            clearFav.Margin = new Thickness(8, 0, 0, 0);
            clearFav.Click += (_, _) => host.ClearFavourites();
            buttons.Children.Add(clearRecent);
            buttons.Children.Add(clearFav);
            side.Children.Add(buttons);
            side.Children.Add(Dialogs.Note($"Both ask first, and only clear {ProductBranding.Name}'s lists: no file or folder is touched."));
        }
        saves.Add(() =>
        {
            s.ShowQuickAccess = showQuick.IsChecked == true;
            s.ShowFavourites = showFav.IsChecked == true;
            s.ShowRecent = showRecent.IsChecked == true;
            s.RememberSidebarState = remember.IsChecked == true;
            s.HiddenQuickAccess = placeChecks.Where(c => c.Box.IsChecked != true).Select(c => c.Name).ToList();
            s.TrackRecent = track.IsChecked == true;
            return null;
        });

        // ---------------------------------------------------------------- Views
        var views = Page("Views");
        DefaultView = Dialogs.Combo(new[] { ("Details", 0), ("Small icons", 48), ("Medium icons", 96), ("Large icons", 160), ("Extra large icons", 256) },
            AppSettings.IsView(s.ViewTileSize) ? s.ViewTileSize : 0);
        DefaultView.Width = 200;
        DefaultView.HorizontalAlignment = HorizontalAlignment.Left;
        views.Children.Add(Dialogs.Field("Default view", DefaultView));
        var perFolder = Check(views, "Remember a view for each folder (otherwise the default view is used everywhere)", s.RememberFolderViews);
        views.Children.Add(Dialogs.Note("Each folder keeps the view you choose for it (View menu, Ctrl+Shift+1…6, Ctrl + mouse wheel). " +
                                        "Folders you haven't set use the default; changing the default doesn't change them. Saved views are kept while this is off."));
        var resets = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        if (host?.CurrentFolder != null)
        {
            var resetHere = Dialogs.Button($"Reset the view of '{host.CurrentFolder}'");
            resetHere.IsEnabled = host.CurrentFolderHasView();
            resetHere.Click += (_, _) => { host.ResetCurrentFolderView(); resetHere.IsEnabled = host.CurrentFolderHasView(); ResetLabel(); };
            resetHere.Margin = new Thickness(0, 0, 8, 0);
            resets.Children.Add(resetHere);
        }
        ResetViewsButton = Dialogs.Button("");
        resets.Children.Add(ResetViewsButton);
        views.Children.Add(resets);
        void ResetLabel()
        {
            int n = s.FolderViews.Count;
            ResetViewsButton.Content = n == 0 ? "Reset all folder views (none saved)" : $"Reset all folder views ({n} saved)…";
            ResetViewsButton.IsEnabled = n > 0;
        }
        ConfirmResetViews = n => Dialogs.Confirm(this, "Reset all folder views",
            $"Forget the view of all {n} folders that have one? They will all use the default view.\n\nOnly {ProductBranding.Name}'s settings change; no files or folders are touched.");
        ResetViewsButton.Click += (_, _) =>
        {
            int n = s.FolderViews.Count;
            if (n == 0 || !ConfirmResetViews(n)) return;
            s.ResetAllFolderViews();
            s.Save(); // right away, like the button says (Cancel doesn't bring them back)
            ResetLabel();
        };
        ResetLabel();
        views.Children.Add(Spaced(Dialogs.Heading("Sorting")));
        var sortCol = Dialogs.Combo(new[] { ("Name", "Name"), ("Date modified", "Modified"), ("Type", "Type"), ("Size", "Size") }, s.DefaultSort);
        sortCol.Width = 180;
        var sortDir = Dialogs.Combo(new[] { ("Ascending (A→Z, oldest, smallest first)", true), ("Descending (Z→A, newest, largest first)", false) }, s.DefaultSortAscending);
        sortDir.Width = 300;
        sortDir.Margin = new Thickness(8, 0, 0, 0);
        var sortRow = new StackPanel { Orientation = Orientation.Horizontal };
        sortRow.Children.Add(sortCol);
        sortRow.Children.Add(sortDir);
        views.Children.Add(Dialogs.Field("Sort folders by (new tabs; a column header changes it for that tab)", sortRow));
        saves.Add(() =>
        {
            s.ViewTileSize = DefaultView.SelectedItem is ComboBoxItem { Tag: int v } ? v : 0; // folders with their own view keep it
            s.RememberFolderViews = perFolder.IsChecked == true;
            s.DefaultSort = Dialogs.Selected<string>(sortCol);
            s.DefaultSortAscending = Dialogs.Selected<bool>(sortDir);
            return null;
        });

        // ---------------------------------------------------------------- Context menu
        var menu = Page("Context menu");
        menu.Children.Add(Dialogs.Heading("Actions"));
        menu.Children.Add(Dialogs.Note("Untick an action to leave it out of right-click menus. Open and Refresh are always there."));
        var actionChecks = new List<(string Id, CheckBox Box)>();
        var actionsPanel = new WrapPanel();
        foreach (var a in ContextMenuActions.All)
        {
            var cb = new CheckBox { Content = a.Name, IsChecked = !s.HiddenMenuActions.Contains(a.Id, StringComparer.OrdinalIgnoreCase), Margin = new Thickness(0, 3, 12, 3), Width = 230 };
            actionChecks.Add((a.Id, cb));
            actionsPanel.Children.Add(cb);
        }
        menu.Children.Add(actionsPanel);
        MenuActionChecks = actionChecks.ToDictionary(c => c.Id, c => c.Box);
        menu.Children.Add(Spaced(Dialogs.Heading("Open in IDE")));
        menu.Children.Add(Dialogs.Note("Shown when you right-click a folder, an empty area or a sidebar place. Installed IDEs are found automatically; " +
                                       "untick one to hide it. Add any other editor by its .exe."));
        var ideRow = new DockPanel();
        var ideSide = new StackPanel { Width = 120, Margin = new Thickness(8, 0, 0, 0) };
        Button Side(string text, Action a)
        {
            var b = Dialogs.Button(text);
            b.Margin = new Thickness(0, 0, 0, 6);
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.Click += (_, _) => a();
            ideSide.Children.Add(b);
            return b;
        }
        Side("Add…", () => EditIde(null));
        _ideEdit = Side("Edit…", () => { if (SelectedIde is { } e) EditIde(e); });
        _ideRemove = Side("Remove", RemoveIde);
        Side("Detect again", DetectAgain);
        DockPanel.SetDock(ideSide, Dock.Right);
        ideRow.Children.Add(ideSide);
        ideRow.Children.Add(_ideList);
        menu.Children.Add(ideRow);
        ScrollViewer.SetHorizontalScrollBarVisibility(_ideList, ScrollBarVisibility.Disabled); // long paths are trimmed, with a tooltip
        _ideList.SelectionChanged += (_, _) => UpdateIdeButtons();
        _ideList.MouseDoubleClick += (_, _) => { if (SelectedIde is { } e) EditIde(e); };
        menu.Children.Add(Spaced(Dialogs.Heading("New menu")));
        menu.Children.Add(Dialogs.Note("Ticked file types appear under New. This only hides menu entries; creating files works the same. " +
                                       "Order and custom templates: New → Manage templates…"));
        menu.Children.Add(BuildNewTypesList());
        var restoreMenu = Dialogs.Button("Restore default context menu");
        restoreMenu.HorizontalAlignment = HorizontalAlignment.Left;
        restoreMenu.Margin = new Thickness(0, 12, 0, 0);
        restoreMenu.Click += (_, _) => RestoreDefaultMenu();
        menu.Children.Add(restoreMenu);
        menu.Children.Add(Dialogs.Note("Ticks every action, IDE and file type again (custom IDEs you added stay). Applies when you press Save."));
        saves.Add(() =>
        {
            s.HiddenMenuActions = actionChecks.Where(c => c.Box.IsChecked != true).Select(c => c.Id).ToList();
            return SaveContextMenu(out var problem) ? null : problem;
        });

        // ---------------------------------------------------------------- Toolbar
        var bar = Page("Toolbar");
        var operationLabels = Check(bar, "Show labels for Cut, Copy, Paste, Rename and Delete", s.ShowFileOperationLabels, first: true);
        var secondaryLabels = Check(bar, "Show labels for secondary toolbar buttons", s.ShowSecondaryToolbarLabels);
        bar.Children.Add(Dialogs.Note("New always keeps its label. Settings stays immediately before Preview on the right."));
        bar.Children.Add(Dialogs.Note("Back, Forward, Up, Refresh and the address bar are always there."));
        var buttonChecks = new List<(string Id, CheckBox Box)>();
        var extras = new ListBox { Height = 120 };
        if (host != null)
        {
            bar.Children.Add(Dialogs.Heading("Buttons"));
            var bp = new WrapPanel();
            foreach (var (id, name) in host.ToolbarButtons)
            {
                var cb = new CheckBox { Content = name, IsChecked = !s.HiddenToolbarButtons.Contains(id, StringComparer.OrdinalIgnoreCase), Margin = new Thickness(0, 3, 12, 3), Width = 220 };
                buttonChecks.Add((id, cb));
                bp.Children.Add(cb);
            }
            bar.Children.Add(bp);
            ToolbarChecks = buttonChecks.ToDictionary(c => c.Id, c => c.Box);

            bar.Children.Add(Spaced(Dialogs.Heading("More buttons")));
            bar.Children.Add(Dialogs.Note("Add any command (the same ones as the command palette, Ctrl+Shift+P) as a button at the end of the toolbar."));
            foreach (var t in s.ToolbarExtras) extras.Items.Add(t);
            var choose = new ComboBox { Width = 320, IsEditable = false };
            foreach (var c in host.ToolbarCommands) choose.Items.Add(c);
            var add = Dialogs.Button("Add");
            add.Margin = new Thickness(8, 0, 0, 0);
            add.Click += (_, _) => { if (choose.SelectedItem is string c && !extras.Items.Contains(c)) extras.Items.Add(c); };
            var addRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 6) };
            addRow.Children.Add(choose);
            addRow.Children.Add(add);
            bar.Children.Add(addRow);
            var extraRow = new DockPanel();
            var extraSide = new StackPanel { Width = 110, Margin = new Thickness(8, 0, 0, 0) };
            void Move(int d)
            {
                int i = extras.SelectedIndex, j = i + d;
                if (i < 0 || j < 0 || j >= extras.Items.Count) return;
                var item = extras.Items[i];
                extras.Items.RemoveAt(i);
                extras.Items.Insert(j, item);
                extras.SelectedIndex = j;
            }
            foreach (var (text, act) in new (string, Action)[] { ("Remove", () => { if (extras.SelectedIndex >= 0) extras.Items.RemoveAt(extras.SelectedIndex); }), ("Move up", () => Move(-1)), ("Move down", () => Move(1)) })
            {
                var b = Dialogs.Button(text);
                b.Margin = new Thickness(0, 0, 0, 6);
                b.HorizontalAlignment = HorizontalAlignment.Stretch;
                b.Click += (_, _) => act();
                extraSide.Children.Add(b);
            }
            DockPanel.SetDock(extraSide, Dock.Right);
            extraRow.Children.Add(extraSide);
            extraRow.Children.Add(extras);
            bar.Children.Add(extraRow);
            ToolbarExtrasList = extras;
            ToolbarCommandChoice = choose;
            var restoreBar = Dialogs.Button("Restore default toolbar");
            restoreBar.HorizontalAlignment = HorizontalAlignment.Left;
            restoreBar.Margin = new Thickness(0, 12, 0, 0);
            RestoreToolbarDefaults = () =>
            {
                var defaults = ApplicationDefaults.Create();
                operationLabels.IsChecked = defaults.ShowFileOperationLabels;
                secondaryLabels.IsChecked = defaults.ShowSecondaryToolbarLabels;
                foreach (var (id, cb) in buttonChecks) cb.IsChecked = !defaults.HiddenToolbarButtons.Contains(id, StringComparer.OrdinalIgnoreCase);
                extras.Items.Clear();
                foreach (var title in defaults.ToolbarExtras) extras.Items.Add(title);
            };
            restoreBar.Click += (_, _) => RestoreToolbarDefaults();
            bar.Children.Add(restoreBar);
        }
        saves.Add(() =>
        {
            s.ShowFileOperationLabels = operationLabels.IsChecked == true;
            s.ShowSecondaryToolbarLabels = secondaryLabels.IsChecked == true;
            if (host == null) return null;
            s.HiddenToolbarButtons = buttonChecks.Where(c => c.Box.IsChecked != true).Select(c => c.Id).ToList();
            s.ToolbarExtras = extras.Items.OfType<string>().ToList();
            return null;
        });

        // ---------------------------------------------------------------- Search
        var search = Page("Search");
        var subfolders = Check(search, "Search subfolders when Enter is pressed (off: the search box only filters the open folder)", s.SearchScope == SearchScope.Subfolders, first: true);
        var finds = Dialogs.Combo(new[] { ("Files and folders", Core.Tools.ItemTypeFilter.All), ("Files only", Core.Tools.ItemTypeFilter.FilesOnly), ("Folders only", Core.Tools.ItemTypeFilter.FoldersOnly) }, s.SearchFinds);
        finds.Width = 220; finds.HorizontalAlignment = HorizontalAlignment.Left;
        search.Children.Add(Spaced(Dialogs.Field("The search box finds", finds)));
        var live = Check(search, "Filter the open folder while typing (off: only when Enter is pressed)", s.FilterWhileTyping);
        search.Children.Add(Dialogs.Note($"{ProductBranding.Name} keeps no search history. Saved searches (Tools → Advanced search) are kept until you delete them there."));
        saves.Add(() =>
        {
            s.SearchScope = subfolders.IsChecked == true ? SearchScope.Subfolders : SearchScope.CurrentFolder;
            s.SearchFinds = Dialogs.Selected<Core.Tools.ItemTypeFilter>(finds);
            s.FilterWhileTyping = live.IsChecked == true;
            return null;
        });

        // ---------------------------------------------------------------- Advanced: archives, previews, conversion
        var adv = Page("Advanced");
        var openArchives = Check(adv, "Open archives (ZIP, 7z, RAR, TAR…) like folders on double-click", s.OpenArchivesAsFolders, first: true);
        var scripts = Check(adv, "Allow scripts in HTML previews (only for files you trust)", s.HtmlScripts);
        var maxGb = new TextBox { Text = s.ArchiveMaxTotalGB.ToString("0.##"), Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
        var ratio = new TextBox { Text = s.ArchiveMaxRatio.ToString("0"), Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
        var previewMb = new TextBox { Text = s.ArchivePreviewMaxMB.ToString(), Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
        var ffmpeg = new TextBox { Text = s.FfmpegPath };
        adv.Children.Add(Spaced(Dialogs.Heading("Archive safety limits")));
        adv.Children.Add(Dialogs.Field("Stop extracting after this many GB", maxGb));
        adv.Children.Add(Dialogs.Field("Stop when an archive expands more than this many times its size (decompression bombs)", ratio));
        adv.Children.Add(Dialogs.Field("Largest archive entry to extract just for a preview (MB)", previewMb));
        adv.Children.Add(Spaced(Dialogs.Heading("Conversion")));
        adv.Children.Add(Dialogs.Field("FFmpeg (ffmpeg.exe or its folder; empty = find it automatically)", Dialogs.FilePicker(ffmpeg, this, "Choose ffmpeg.exe", "ffmpeg.exe|ffmpeg.exe|Programs|*.exe")));
        saves.Insert(0, () =>
        {
            if (!double.TryParse(maxGb.Text, out var gb) || gb <= 0 || !double.TryParse(ratio.Text, out var r) || r < 2 || !int.TryParse(previewMb.Text, out var mb) || mb < 1)
            {
                SelectTab("Advanced");
                return "Limits must be positive numbers (the ratio at least 2).";
            }
            s.OpenArchivesAsFolders = openArchives.IsChecked == true;
            s.HtmlScripts = scripts.IsChecked == true;
            s.ArchiveMaxTotalGB = gb;
            s.ArchiveMaxRatio = r;
            s.ArchivePreviewMaxMB = mb;
            s.FfmpegPath = ffmpeg.Text.Trim();
            return null;
        });

        Tabs.Height = 520;
        BuildUpdatesPage(s, host, saves);
        AddContent(Tabs);
        AddContent(err);
        TrySave = () =>
        {
            foreach (var save in saves)
                if (save() is { } problem) { err.Text = problem; return false; }
            s.Save();
            return true;
        };
        AddButton("Save", () => { if (TrySave()) DialogResult = true; }, primary: true);
        AddButton("Cancel", () => DialogResult = false, cancel: true);
        RefreshIdes();
    }

    internal ComboBox StartupFolderBox { get; private set; } = null!;
    internal Dictionary<string, CheckBox> MenuActionChecks { get; private set; } = new();
    internal Dictionary<string, CheckBox> ToolbarChecks { get; private set; } = new();
    internal ListBox? ToolbarExtrasList { get; private set; }
    internal ComboBox? ToolbarCommandChoice { get; private set; }

    private static string StartupText(string folder) => folder.Length == 0 ? "Desktop" : folder;

    /// <summary>A tab page (scrolls when it is taller than the window).</summary>
    private StackPanel Page(string header)
    {
        var panel = new StackPanel { Margin = new Thickness(4, 10, 12, 8) };
        Tabs.Items.Add(new TabItem { Header = header, Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } });
        return panel;
    }

    private static CheckBox Check(Panel page, string text, bool value, bool first = false)
    {
        var cb = new CheckBox { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, IsChecked = value, Margin = new Thickness(0, first ? 2 : 8, 0, 0) };
        page.Children.Add(cb);
        return cb;
    }

    private static FrameworkElement Spaced(FrameworkElement e)
    {
        e.Margin = new Thickness(e.Margin.Left, Math.Max(e.Margin.Top, 14), e.Margin.Right, e.Margin.Bottom);
        return e;
    }

    /// <summary>"Restore default context menu": every action, IDE and New-menu type ticked again (custom IDEs stay).</summary>
    internal void RestoreDefaultMenu()
    {
        foreach (var cb in MenuActionChecks.Values) cb.IsChecked = true;
        foreach (var cb in NewTypeChecks.Values) cb.IsChecked = true;
        foreach (var e in _ides) e.Enabled = true;
        RefreshIdes();
    }

    // ------------------------------------------------------------------ IDEs

    private IdeEntry? SelectedIde => _ideList.SelectedItem is ListBoxItem { Tag: IdeEntry e } ? e : null;

    private void UpdateIdeButtons()
    {
        _ideEdit.IsEnabled = SelectedIde != null;
        _ideRemove.IsEnabled = SelectedIde is { IsCustom: true };
    }

    private void RefreshIdes(IdeEntry? select = null)
    {
        select ??= SelectedIde;
        _ideList.Items.Clear();
        if (_ides.Count == 0)
            _ideList.Items.Add(new ListBoxItem { IsEnabled = false, Content = new TextBlock { Text = "No IDE found. Use Add… to set one up.", Foreground = Theme.Brush("Fg3") } });
        foreach (var e in _ides)
        {
            var entry = e;
            var check = new CheckBox { Content = e.Name, IsChecked = e.Enabled, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            check.Checked += (_, _) => entry.Enabled = true;
            check.Unchecked += (_, _) => entry.Enabled = false;
            var kind = new TextBlock
            {
                Text = !e.IsConfigured ? "Not found" : e.IsCustom ? "Custom" : "Detected",
                Foreground = Theme.Brush(e.IsConfigured ? "Fg3" : "Danger"), Margin = new Thickness(8, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            var top = new DockPanel();
            DockPanel.SetDock(kind, Dock.Right);
            top.Children.Add(kind);
            top.Children.Add(check);
            var row = new StackPanel { Margin = new Thickness(2, 3, 2, 3) };
            row.Children.Add(top);
            row.Children.Add(new TextBlock
            {
                Text = $"{e.Program}   {e.Arguments}", Foreground = Theme.Brush("Fg3"), FontSize = 11.5, Margin = new Thickness(22, 1, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = $"{e.Program} {e.Arguments}",
            });
            var item = new ListBoxItem { Content = row, Tag = e };
            _ideList.Items.Add(item);
            if (ReferenceEquals(e, select)) _ideList.SelectedItem = item;
        }
        UpdateIdeButtons();
    }

    private void RemoveIde()
    {
        if (SelectedIde is not { IsCustom: true } e) return;
        _ides.Remove(e);
        RefreshIdes();
    }

    private void DetectAgain()
    {
        IdeCatalog.StartDetection(again: true);
        var before = _ides.Count;
        var merged = IdeCatalog.Merge(_ides, IdeCatalog.DetectedWait());
        // keep the edited objects (checkbox state) and append what is new
        for (int i = 0; i < _ides.Count; i++) _ides[i].Program = merged[i].Program;
        _ides.AddRange(merged.Skip(_ides.Count));
        RefreshIdes();
        int added = _ides.Count - before;
        MessageBox.Show(this, added == 0 ? "No new IDEs were found." : $"Found {added} new IDE(s).", "Detect IDEs", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Adds a custom IDE (or replaces an edited one) after the same checks the editor makes. Returns the problem, or null.</summary>
    internal string? AddOrReplaceIde(IdeEntry edited, IdeEntry? original)
    {
        if (edited.Validate() is { } problem) return problem;
        var program = edited.ResolvedProgram;
        if (_ides.Any(o => !ReferenceEquals(o, original) && string.Equals(o.ResolvedProgram, program, StringComparison.OrdinalIgnoreCase)
                           && o.Arguments.Trim() == edited.Arguments.Trim()))
            return "That program with these arguments is already in the list.";
        if (original != null) _ides[_ides.IndexOf(original)] = edited;
        else _ides.Add(edited);
        RefreshIdes(edited);
        return null;
    }

    private void EditIde(IdeEntry? original)
    {
        var e = original?.Clone() ?? new IdeEntry { Id = "custom-" + Guid.NewGuid().ToString("N"), IsCustom = true };
        var d = new DialogWindow(this, original == null ? "Add IDE" : "Edit " + original.Name, 560);
        var name = new TextBox { Text = e.Name };
        var program = new TextBox { Text = e.Program };
        var args = new TextBox { Text = e.Arguments };
        d.AddContent(Dialogs.Field("Name in the menu", name));
        d.AddContent(Dialogs.Field("Program (the IDE's .exe)", Dialogs.FilePicker(program, d, "Choose the IDE", "Programs|*.exe|Batch files|*.cmd;*.bat|All files|*.*")));
        d.AddContent(Dialogs.Field("Arguments ({path} = the folder, quoted automatically; {name} = its name)", args));
        d.AddContent(Dialogs.Note("Prefer the .exe: a .cmd/.bat launcher runs through the command prompt and can't open folders whose names contain & % ^ ! ( )."));
        var err = Dialogs.Note("", "Danger");
        d.AddContent(err);
        d.AddButton("OK", () =>
        {
            e.Name = name.Text.Trim();
            e.Program = program.Text.Trim().Trim('"');
            e.Arguments = string.IsNullOrWhiteSpace(args.Text) ? "{path}" : args.Text.Trim();
            e.Enabled = original?.Enabled ?? true;
            if (AddOrReplaceIde(e, original) is { } problem) { err.Text = problem; return; }
            d.DialogResult = true;
        }, primary: true);
        d.AddButton("Cancel", () => d.DialogResult = false, cancel: true);
        d.ShowDialog();
    }

    // ------------------------------------------------------------------ New menu types

    private ListBox BuildNewTypesList()
    {
        var list = new ListBox { Height = 230 };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        void Header(string text) => list.Items.Add(new ListBoxItem
        {
            IsEnabled = false, Focusable = false,
            Content = new TextBlock { Text = text.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Theme.Brush("Fg3"), Margin = new Thickness(0, 6, 0, 2) },
        });
        void Row(string key, string name, string ext, bool isChecked)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new Image { Source = ShellInfo.Icon("file" + ext, false, assumeType: true), Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0) });
            sp.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = "  " + ext, Foreground = Theme.Brush("Fg3"), VerticalAlignment = VerticalAlignment.Center });
            var check = new CheckBox { Content = sp, IsChecked = isChecked, Margin = new Thickness(2, 1, 2, 1) };
            NewTypeChecks[key] = check;
            list.Items.Add(new ListBoxItem { Content = check, Focusable = false });
        }
        string? category = null;
        foreach (var t in _templates.OrderBy(t => t.Order))
        {
            if (t.Category != category) { Header(t.Category); category = t.Category; }
            Row(t.Id, t.Name, t.Extension, t.Enabled);
        }
        if (_windowsTypes.Count > 0)
        {
            Header("Other types (from Windows)");
            var hidden = new HashSet<string>(_settings.HiddenNewTypes, StringComparer.OrdinalIgnoreCase);
            foreach (var w in _windowsTypes.OrderBy(w => w.Name, StringComparer.CurrentCultureIgnoreCase))
                if (!NewTypeChecks.ContainsKey("win:" + w.Extension.ToLowerInvariant()))
                    Row("win:" + w.Extension.ToLowerInvariant(), w.Name, w.Extension, !hidden.Contains(w.Extension));
        }
        return list;
    }

    private bool SaveContextMenu(out string problem)
    {
        problem = "";
        _settings.Ides = _ides.Select(i => i.Clone()).ToList();

        bool templatesChanged = false;
        foreach (var t in _templates)
            if (NewTypeChecks.TryGetValue(t.Id, out var c) && (c.IsChecked == true) != t.Enabled) { t.Enabled = c.IsChecked == true; templatesChanged = true; }
        if (templatesChanged)
        {
            try { FileTemplates.Save(_templates); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problem = "The New menu couldn't be saved: " + ex.Message; return false; }
        }

        // keep hidden types Windows no longer lists (e.g. an app was reinstalled later), replace the ones shown here
        var shown = new HashSet<string>(_windowsTypes.Select(w => w.Extension), StringComparer.OrdinalIgnoreCase);
        var hiddenNow = _settings.HiddenNewTypes.Where(h => !shown.Contains(h)).ToList();
        foreach (var w in _windowsTypes)
            if (NewTypeChecks.TryGetValue("win:" + w.Extension.ToLowerInvariant(), out var c) && c.IsChecked != true
                && !hiddenNow.Contains(w.Extension, StringComparer.OrdinalIgnoreCase))
                hiddenNow.Add(w.Extension);
        _settings.HiddenNewTypes = hiddenNow;
        return true;
    }
}
