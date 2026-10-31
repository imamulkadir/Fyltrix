using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Fyltrix.Core;
using Fyltrix.Core.Archives;
using Fyltrix.Preview;

namespace Fyltrix.UI;

public sealed partial class MainWindow : Window, IFolderHost
{
    private sealed class Tab
    {
        public required FolderView View { get; init; }
        public required Border Header { get; init; }
        public required TextBlock Title { get; init; }
        public required TextBlock Icon { get; init; }
    }

    public AppSettings Settings { get; }
    private readonly UndoManager _undo = new();
    private readonly List<Tab> _tabs = new();
    private Tab? _active;

    // layout
    private readonly StackPanel _tabStrip = new() { Orientation = Orientation.Horizontal };
    private readonly Grid _content = new();
    private readonly TextBox _address = new();
    private readonly TextBox _search = new();
    private readonly TextBlock _searchHint = new();
    private readonly StackPanel _sidebar = new();
    private readonly PreviewPane _preview = new();
    private readonly ColumnDefinition _sidebarCol = new(), _previewCol = new(), _previewSplitCol = new(), _sidebarSplitCol = new(), _listCol = new();
    /// <summary>Tab strip, navigation, command and status bars: hidden while the preview is fullscreen.</summary>
    private readonly List<UIElement> _chrome = new();
    private readonly TextBlock _statusLeft = new(), _statusRight = new();
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(7) };
    private readonly DispatcherTimer _filterTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private Button _btnBack = null!, _btnForward = null!, _btnUp = null!, _btnUndo = null!, _btnPreview = null!;

    private QuickViewWindow? _quickView;
    private readonly List<CompareWindow> _compareWindows = new();
    private readonly HashSet<string> _cutPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _suppressSearchEvents;

    public MainWindow(AppSettings settings, string? startPath)
    {
        CtorBodyStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        Settings = settings;
        PreviewPane.Settings = settings;
        FileItem.HideExtensions = !settings.ShowExtensions;
        if (!settings.RememberSidebarState) settings.CollapsedSections.Clear();
        Title = ProductBranding.Name;
        Icon = AppIcon.ForWindow();
        Background = Theme.Brush("Bg0");
        Foreground = Theme.Brush("Fg1");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        Width = Math.Max(700, settings.WindowWidth);
        Height = Math.Max(450, settings.WindowHeight);
        if (!double.IsNaN(settings.WindowLeft) && !double.IsNaN(settings.WindowTop) && IsOnScreen(settings.WindowLeft, settings.WindowTop))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = settings.WindowLeft;
            Top = settings.WindowTop;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (settings.WindowMaximized) WindowState = WindowState.Maximized;

        Mark("settings/window");
        ApplyWindowChrome();
        Content = BuildLayout();
        ApplyToolbar();
        Mark("layout");
        SourceInitialized += (_, _) => { Shell.UseDarkTitleBar(this); AppIcon.ApplyToHwnd(this); StartClipboardNotifications(); };
        Activated += (_, _) => { RefreshClipboardState(); CheckDestination(); };
        PreviewKeyDown += OnKeyDown;
        Closing += (_, e) =>
        {
            if (!_exitForReal && Settings.ConfirmCloseTabs && _tabs.Count > 1 && !ConfirmCloseTabs(_tabs.Count, closingWindow: true)) { e.Cancel = true; return; }
            if (KeepSharingInsteadOfClosing(e)) return;
            SaveSettings();
        };
        Closed += (_, _) =>
        {
            StopClipboardNotifications();
            DisposeSharing();
            JobWindow.CancelAll();
            _quickView?.Close();
            foreach (var w in _compareWindows.ToList()) w.Close();
            foreach (var t in _tabs) t.View.Close();
            _preview.DisposeWeb();
            _automation?.Dispose();
            ArchiveBrowser.CleanupCache(includeOwn: true);
        };
        Task.Run(() =>
        {
            ArchiveBrowser.CleanupCache(includeOwn: false);
            _ = SystemTar.CanWrite7z; // probe tar.exe once, off the UI thread
        });
        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); _statusRight.Text = ""; };
        _filterTimer.Tick += (_, _) => { _filterTimer.Stop(); if (_active != null && !_active.View.IsSearchResults) _active.View.SetFilter(_search.Text); };
        _undo.Changed += (_, _) => UpdateCommands();
        _preview.PopOutRequested += (_, path) => ToggleQuickView(path);
        _preview.StatusMessage += (_, msg) => SetStatus(msg);
        _preview.SpacePressed += (_, _) => ToggleQuickView(null);
        _preview.StepRequested += (_, d) => StepSelection(d);
        _preview.ImageToolsRequested += (_, p) => OpenImageTools(p);
        _preview.FullScreenChanged += (_, on) => SetPreviewFullScreen(on);

        Mark("events");
        // tabs: restore last session, then open the start path (command line) if given
        var paths = settings.RestoreTabs ? settings.OpenTabs.Where(FolderView.Exists).ToList() : new List<string>();
        if (paths.Count == 0) paths.Add(StartupFolder());
        foreach (var p in paths) AddTab(p, activate: false);
        if (startPath != null)
        {
            var sp = FolderView.NormalizePath(startPath);
            if (File.Exists(sp)) AddTab(Path.GetDirectoryName(sp)!, activate: true, select: sp);
            else if (Directory.Exists(sp)) AddTab(sp, activate: true);
            else ActivateTab(_tabs[Math.Clamp(settings.ActiveTab, 0, _tabs.Count - 1)]);
        }
        else ActivateTab(_tabs[Math.Clamp(settings.RestoreTabs ? settings.ActiveTab : 0, 0, _tabs.Count - 1)]);

        Mark("tabs");
        RebuildSidebar();
        Mark("sidebar");
        SetPreviewVisible(settings.PreviewAtStartup switch
        {
            PreviewStartup.Show => true,
            PreviewStartup.Hide => false,
            _ => settings.PreviewVisible,
        });
        Mark("preview");
        StartShellNewDiscovery();
        IdeCatalog.StartDetection();
        Mark("shellnew+ide");
        RestartAutomation();
        Mark("automation");
        InitSharing();
        Mark("sharing");
        Core.Tools.TagStore.Shared.Changed += (_, _) => Dispatcher.BeginInvoke(new Action(RefreshTags));
    }

    /// <summary>When the constructor body began, after the field initialisers (the benchmark reports the split).</summary>
    internal static long CtorBodyStarted;
    /// <summary>Time spent in each part of the constructor (the benchmark reports it).</summary>
    internal readonly List<(string Step, double Ms)> StartupMarks = new();
    private readonly System.Diagnostics.Stopwatch _startupClock = System.Diagnostics.Stopwatch.StartNew();
    private void Mark(string step) { StartupMarks.Add((step, Math.Round(_startupClock.Elapsed.TotalMilliseconds, 1))); _startupClock.Restart(); }

    private static bool IsOnScreen(double left, double top) =>
        left > SystemParameters.VirtualScreenLeft - 50 && top > SystemParameters.VirtualScreenTop - 50 &&
        left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
        top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100;

    private IntPtr Hwnd => new WindowInteropHelper(this).Handle;
    internal FolderView? View => _active?.View;
    internal UndoManager UndoStack => _undo;

    // =====================================================================================
    // Layout
    // =====================================================================================

    private UIElement BuildLayout()
    {
        var root = new DockPanel { LastChildFill = true };

        // ----- title bar: tabs and window buttons on one row (MainWindow.TitleBar.cs) -----
        var titleBar = BuildTitleBar();
        DockPanel.SetDock(titleBar, Dock.Top);
        root.Children.Add(titleBar);
        _chrome.Add(titleBar);

        // ----- navigation bar -----
        var nav = new DockPanel { Background = Theme.Brush("Bg1"), LastChildFill = true };
        var navButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 6, 4, 6) };
        _btnBack = Theme.SmallButton("\uE72B", "", "Back (Alt+Left)");
        _btnForward = Theme.SmallButton("\uE72A", "", "Forward (Alt+Right)");
        _btnUp = Theme.SmallButton("\uE74A", "", "Up (Alt+Up)");
        var refresh = Theme.SmallButton("\uE72C", "", "Refresh (F5)");
        _btnBack.Click += (_, _) => View?.GoBack();
        _btnForward.Click += (_, _) => View?.GoForward();
        _btnUp.Click += (_, _) => View?.GoUp();
        refresh.Click += (_, _) => RefreshActive();
        foreach (var b in new[] { _btnBack, _btnForward, _btnUp, refresh }) navButtons.Children.Add(b);
        DockPanel.SetDock(navButtons, Dock.Left);
        nav.Children.Add(navButtons);

        var searchBox = BuildSearchBox();
        DockPanel.SetDock(searchBox, Dock.Right);
        nav.Children.Add(searchBox);

        _address.Margin = new Thickness(4, 6, 0, 6);
        _address.Padding = new Thickness(8, 4, 8, 4);
        _address.KeyDown += OnAddressKey;
        _address.GotKeyboardFocus += (_, _) => _address.Dispatcher.BeginInvoke(new Action(_address.SelectAll), DispatcherPriority.Input);
        _address.LostKeyboardFocus += (_, _) => UpdateAddress();
        nav.Children.Add(_address);
        DockPanel.SetDock(nav, Dock.Top);
        root.Children.Add(nav);
        _chrome.Add(nav);

        // ----- command bar -----
        var commandBar = new DockPanel { Background = Theme.Brush("Bg1"), Margin = new Thickness(6, 0, 6, 6) };
        var rightCommands = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(rightCommands, Dock.Right);
        commandBar.Children.Add(rightCommands);
        var cmd = new WrapPanel();
        commandBar.Children.Add(cmd);
        // every button has an id, so Settings → Toolbar can hide it (hidden buttons are only collapsed: the code that
        // enables them or changes their icon keeps working)
        void Cmd(string id, string name, string glyph, string label, string tip, Action act, Action<Button>? keep = null)
        {
            if (label.Length == 0) label = name;
            var b = Theme.SmallButton(glyph, label, tip);
            b.MinWidth = 34;
            b.MinHeight = 30;
            b.HorizontalContentAlignment = HorizontalAlignment.Center;
            System.Windows.Automation.AutomationProperties.SetName(b, name);
            ToolTipService.SetShowOnDisabled(b, true);
            b.Click += (_, _) => { if (CanExecute(id)) act(); };
            keep?.Invoke(b);
            if (id is "preview" or "settings") rightCommands.Children.Add(b);
            else cmd.Children.Add(b);
            _toolbar.Add((id, name, b));
        }
        void Sep()
        {
            var sep = new Border { Width = 1, Height = 20, Margin = new Thickness(6, 0, 6, 0), Background = Theme.Brush("Line") };
            cmd.Children.Add(sep);
            _toolbar.Add((null, "", sep));
        }
        Cmd("newfolder", "New folder", "\uE8F4", "New folder", "New folder (Ctrl+Shift+N)", NewFolder);
        Button? newBtn = null;
        Cmd("new", "New (file from a template)", "\uE710", "New", "Create a new file from a template", () => ShowNewMenu(newBtn!), b => newBtn = b);
        Sep();
        Cmd("cut", "Cut", "\uE8C6", "", "Cut (Ctrl+X)", () => CopySelection(cut: true));
        Cmd("copy", "Copy", "\uE8C8", "", "Copy (Ctrl+C)", () => CopySelection(cut: false));
        Cmd("paste", "Paste", "\uE77F", "", "Paste (Ctrl+V)", Paste);
        Cmd("rename", "Rename", "\uE8AC", "", "Rename (F2)", RenameSelection);
        Cmd("delete", "Delete", "\uE74D", "", "Delete to Recycle Bin (Del). Shift+Del deletes permanently.", () => DeleteSelection(permanent: false));
        Sep();
        Cmd("undo", "Undo", "\uE7A7", "Undo", "Undo (Ctrl+Z)", Undo, b => _btnUndo = b);
        Sep();
        Button? toolsBtn = null;
        Cmd("tools", "Tools menu", "\uE90F", "Tools", "Batch rename, duplicates, folder sizes, search, checksums…", () => ShowToolsMenu(toolsBtn!), b => toolsBtn = b);
        Sep();
        Button? sortBtn = null;
        Cmd("sort", "Sort", "\uE8CB", "Sort", "Sort files and folders", () => ShowSortMenu(sortBtn!), b => sortBtn = b);
        Button? viewBtn = null;
        Cmd("view", "View menu", "\uE8FD", "View", "Details or thumbnails (Ctrl+Shift+1\u20264, Ctrl+Shift+6; Ctrl+mouse wheel)", () => ShowViewMenu(viewBtn!), b => viewBtn = b);
        Cmd("quickview", "Quick view", "\uE7B3", "Quick view", "Quick View (Space)", () => ToggleQuickView(null));
        Cmd("compare", "Compare", "\uE8AB", "Compare", "Compare two files (Ctrl+D)", CompareSelection);
        Sep();
        Cmd("favourite", "Favourite (star)", LocationIcons.FavouriteOutline, "", "Add this folder to Favourites", () => { if (View != null) ToggleFavourite(View.CurrentPath); }, b => _favButton = b);
        Cmd("explorer", "Open in File Explorer", "\uE838", "", "Open this folder in File Explorer", () => { if (View != null && View.CurrentPath != FolderView.ThisPC) Shell.ShowInExplorer(View.CurrentPath); });
        Cmd("terminal", "Open a terminal here", "\uE756", "", "Open a terminal here", () => { if (View != null && View.CurrentPath != FolderView.ThisPC) Shell.OpenTerminal(View.CurrentPath); });
        Sep();
        // the folder's own right-click menu, reachable even when items fill the whole list
        Button? moreBtn = null;
        Cmd("more", "More actions (…)", "\uE712", "", "More actions for this folder (New, Paste, Refresh\u2026)", () => View?.ShowBackgroundMenu(moreBtn!), b => { moreBtn = b; _folderMenuButton = b; System.Windows.Automation.AutomationProperties.SetName(b, "More actions for this folder"); });
        cmd.Children.Add(_toolbarExtras); // commands added in Settings → Toolbar
        Cmd("settings", "Settings", "\uE713", "Settings", "Settings", OpenSettings);
        Cmd("preview", "Preview pane", "\uE8A1", "Preview", "Show/hide the preview pane (Ctrl+P)", () => SetPreviewVisible(!Settings.PreviewVisible), b => _btnPreview = b);
        DockPanel.SetDock(commandBar, Dock.Top);
        root.Children.Add(commandBar);
        _chrome.Add(commandBar);

        // ----- status bar -----
        var status = new DockPanel { Background = Theme.Brush("Bg0"), Height = 26 };
        _statusLeft.Foreground = Theme.Brush("Fg2");
        _statusLeft.Margin = new Thickness(12, 0, 0, 0);
        _statusLeft.VerticalAlignment = VerticalAlignment.Center;
        _statusRight.Foreground = Theme.Brush("Fg2");
        _statusRight.Margin = new Thickness(0, 0, 12, 0);
        _statusRight.VerticalAlignment = VerticalAlignment.Center;
        _statusRight.TextTrimming = TextTrimming.CharacterEllipsis;
        DockPanel.SetDock(_statusLeft, Dock.Left);
        status.Children.Add(_statusLeft);
        _statusRight.HorizontalAlignment = HorizontalAlignment.Right;
        status.Children.Add(_statusRight);
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);
        _chrome.Add(status);

        // ----- body: sidebar | list | preview -----
        var body = new Grid();
        _sidebarCol.Width = new GridLength(Math.Clamp(Settings.SidebarWidth, 140, 500));
        _sidebarCol.MinWidth = 120;
        body.ColumnDefinitions.Add(_sidebarCol);
        _sidebarSplitCol.Width = new GridLength(4);
        body.ColumnDefinitions.Add(_sidebarSplitCol);
        _listCol.Width = new GridLength(1, GridUnitType.Star);
        _listCol.MinWidth = 260;
        body.ColumnDefinitions.Add(_listCol);
        _previewSplitCol.Width = new GridLength(4);
        body.ColumnDefinitions.Add(_previewSplitCol);
        _previewCol.Width = new GridLength(Math.Clamp(Settings.PreviewWidth, 250, 2000));
        _previewCol.MinWidth = 0;
        body.ColumnDefinitions.Add(_previewCol);

        var sideScroll = new ScrollViewer { Content = _sidebar, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Theme.Brush("Bg0") };
        _sidebar.Margin = new Thickness(6, 4, 6, 12);
        body.Children.Add(sideScroll);

        var split1 = new GridSplitter { HorizontalAlignment = HorizontalAlignment.Stretch, Background = Theme.Brush("Bg0") };
        Grid.SetColumn(split1, 1);
        body.Children.Add(split1);

        Grid.SetColumn(_content, 2);
        body.Children.Add(new Border { Child = _content, BorderBrush = Theme.Brush("Line"), BorderThickness = new Thickness(1, 1, 1, 0) });
        Grid.SetColumn(body.Children[^1], 2);

        var split2 = new GridSplitter { HorizontalAlignment = HorizontalAlignment.Stretch, Background = Theme.Brush("Bg0") };
        Grid.SetColumn(split2, 3);
        body.Children.Add(split2);
        Grid.SetColumn(_preview, 4);
        body.Children.Add(new Border { Child = _preview, BorderBrush = Theme.Brush("Line"), BorderThickness = new Thickness(1, 1, 0, 0) });
        Grid.SetColumn(body.Children[^1], 4);

        root.Children.Add(body);
        _frame.Child = root;
        return _frame;
    }

    // =====================================================================================
    // Tabs
    // =====================================================================================

    private Tab AddTab(string path, bool activate, string? select = null)
    {
        var title = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 0, 10, 0) };
        var close = new Button { Content = Theme.Icon("\uE711", 9, "Fg2"), Padding = new Thickness(5), ToolTip = "Close tab (Ctrl+W)", VerticalAlignment = VerticalAlignment.Center, Focusable = false };
        var icon = Theme.Icon(LocationIcons.Folder, 13, LocationIcons.Brush); // set per location in UpdateTabHeader
        var sp = new Grid();
        sp.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sp.ColumnDefinitions.Add(new ColumnDefinition());
        sp.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(title, 1);
        Grid.SetColumn(close, 2);
        System.Windows.Automation.AutomationProperties.SetName(close, "Close tab");
        sp.Children.Add(icon);
        sp.Children.Add(title);
        sp.Children.Add(close);
        var header = new Border
        {
            Child = sp, Padding = new Thickness(12, 6, 6, 6), Margin = new Thickness(0, 0, 2, 0), CornerRadius = new CornerRadius(8, 8, 0, 0),
            Background = Brushes.Transparent, Width = 220, MinWidth = 120, MaxWidth = 260,
        };

        Tab? tab = null;
        var view = new FolderView(this, path, select);
        tab = new Tab { View = view, Header = header, Title = title, Icon = icon };
        view.Visibility = Visibility.Collapsed;
        view.TitleChanged += (_, _) => UpdateTabHeader(tab);
        view.ItemsLoaded += (_, _) => { MarkCut(view); if (view.IsThisPC) SetSidebarDrives(view.Items); if (View == view) CheckDestination(); };
        HookView(view);
        _tabs.Add(tab);
        _tabStrip.Children.Add(header);
        _content.Children.Add(view);
        UpdateTabHeader(tab);

        close.Click += (_, _) => CloseTab(tab);
        header.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not DependencyObject d || VisualTree.FindAncestor<Button>(d) == null) ActivateTab(tab); };
        header.MouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Middle) CloseTab(tab); };
        header.MouseEnter += (_, _) => { if (tab != _active) header.Background = Theme.Brush("Hover"); };
        header.MouseLeave += (_, _) => { if (tab != _active) header.Background = Brushes.Transparent; };
        header.MouseRightButtonUp += (_, e) =>
        {
            var m = new ContextMenu();
            m.Items.Add(Theme.MenuItem("Duplicate tab", "\uE8C8", () => AddTab(tab.View.CurrentPath, true)));
            m.Items.Add(Theme.MenuItem("Close tab", "\uE711", () => CloseTab(tab), "Ctrl+W"));
            m.Items.Add(Theme.MenuItem("Close other tabs", null, () => CloseOtherTabs(tab), enabled: _tabs.Count > 1));
            m.PlacementTarget = header;
            m.IsOpen = true;
            e.Handled = true;
        };
        header.AllowDrop = true;
        header.DragOver += (_, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) { ActivateTab(tab); } };

        if (activate) ActivateTab(tab);
        return tab;
    }

    private void UpdateTabHeader(Tab tab)
    {
        tab.Title.Text = tab.View.Title;
        tab.Icon.Visibility = Settings.ShowTabIcons ? Visibility.Visible : Visibility.Collapsed;
        // the same icon the sidebar shows for this place (Downloads, Documents…, a drive, or the folder icon)
        var (glyph, brush) = LocationIcons.For(tab.View.CurrentPath, tab.View.IsThisPC, tab.View.IsSearchResults, tab.View.IsArchive);
        tab.Icon.Text = glyph;
        tab.Icon.Foreground = Theme.Brush(brush);
        System.Windows.Automation.AutomationProperties.SetName(tab.Header, "Tab: " + tab.View.Title);
        tab.Header.ToolTip = tab.View.IsSearchResults ? $"Search for '{tab.View.SearchQuery}' in {tab.View.CurrentPath}" : (tab.View.CurrentPath == FolderView.ThisPC ? "This PC" : tab.View.CurrentPath);
        if (tab == _active) { Title = tab.View.Title + $" – {ProductBranding.Name}"; UpdateAddress(); }
    }

    private void ActivateTab(Tab tab)
    {
        if (_active == tab) return;
        _active = tab;
        foreach (var t in _tabs)
        {
            bool on = t == tab;
            t.View.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            t.Header.Background = on ? Theme.Brush("Bg1") : t.Header.IsMouseOver ? Theme.Brush("Hover") : Brushes.Transparent;
            t.Title.Foreground = on ? Theme.Brush("Fg1") : Theme.Brush("Fg2");
        }
        Title = tab.View.Title + $" – {ProductBranding.Name}";
        _suppressSearchEvents = true;
        _search.Text = tab.View.IsSearchResults ? tab.View.SearchQuery : tab.View.Filter;
        _suppressSearchEvents = false;
        UpdateAddress();
        UpdateCommands();
        CheckDestination();
        OnSelectionChanged(tab.View);
        tab.View.FocusList();
        // after layout: a new tab has no size yet, and scrolling it in now would leave it half hidden behind the + button
        Dispatcher.BeginInvoke(new Action(() => tab.Header.BringIntoView()), DispatcherPriority.Loaded);
    }

    /// <summary>The folder Fyltrix starts in (Settings → startup location): a folder, This PC, or the Desktop.</summary>
    internal string StartupFolder()
    {
        var f = Settings.StartupFolder?.Trim() ?? "";
        if (f.Equals("This PC", StringComparison.OrdinalIgnoreCase)) return FolderView.ThisPC;
        if (f.Length > 0 && Directory.Exists(f)) return f;
        return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
    }

    /// <summary>Where a new tab opens (Settings → new tabs open in…).</summary>
    internal string NewTabPath() => Settings.NewTabOpens switch
    {
        NewTabTarget.StartupFolder => StartupFolder(),
        NewTabTarget.ThisPC => FolderView.ThisPC,
        _ => View?.CurrentPath ?? FolderView.ThisPC,
    };

    internal void OpenNewTab() => AddTab(NewTabPath(), activate: true);

    /// <summary>Asks before several tabs close at once (Settings → confirm). Tests answer through <see cref="ConfirmForTest"/>.</summary>
    private bool ConfirmCloseTabs(int count, bool closingWindow) =>
        ConfirmForTest?.Invoke(count) ?? Dialogs.Confirm(this, closingWindow ? $"Close {ProductBranding.Name}" : "Close tabs",
            closingWindow ? $"Close {ProductBranding.Name} and its {count} tabs?" : $"Close {count} other tab{(count == 1 ? "" : "s")}?", defaultNo: false);

    internal Func<int, bool>? ConfirmForTest { get; set; }

    private void CloseOtherTabs(Tab keep)
    {
        var others = _tabs.Where(t => t != keep).ToList();
        if (others.Count == 0) return;
        if (Settings.ConfirmCloseTabs && others.Count > 1 && !ConfirmCloseTabs(others.Count, closingWindow: false)) return;
        foreach (var t in others) CloseTab(t);
    }

    internal void CloseOtherTabsForTest() => CloseOtherTabs(_active!);

    private void CloseTab(Tab tab)
    {
        if (_tabs.Count == 1)
        {
            tab.View.Navigate(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
            return;
        }
        int idx = _tabs.IndexOf(tab);
        tab.View.Close();
        _tabs.Remove(tab);
        _tabStrip.Children.Remove(tab.Header);
        _content.Children.Remove(tab.View);
        if (_active == tab)
        {
            _active = null;
            ActivateTab(_tabs[Math.Min(idx, _tabs.Count - 1)]);
        }
    }

    private void CycleTab(int delta)
    {
        if (_active == null || _tabs.Count < 2) return;
        int i = (_tabs.IndexOf(_active) + delta + _tabs.Count) % _tabs.Count;
        ActivateTab(_tabs[i]);
    }

    // =====================================================================================
    // Sidebar
    // =====================================================================================

    /// <summary>The drives under This PC in the sidebar, with their usage once known. Never read on the UI thread: drive
    /// queries can take seconds (network, sleeping USB disks), so they run in the background with This PC's time limit.</summary>
    private List<FileItem>? _driveCache;
    private int _driveRefresh;
    private Button? _folderMenuButton;
    internal Button? FolderMenuButton => _folderMenuButton;

    private void RebuildSidebar(bool refreshDrives = false)
    {
        if (_driveCache == null)
        {
            // names straight away (no disk access), sizes and labels when the background query answers
            _driveCache = DriveInfo.GetDrives().Select(d => new FileItem(d.Name, true, -1, DateTime.MinValue, false, $"Drive ({d.Name.TrimEnd(Path.DirectorySeparatorChar)})") { IsDrive = true }).ToList();
            refreshDrives = true;
        }
        if (refreshDrives) _ = RefreshSidebarDrives();

        _sidebar.Children.Clear();
        if (Settings.ShowQuickAccess && Section("Quick access"))
            foreach (var place in LocationIcons.QuickAccess().Where(p => !Settings.HiddenQuickAccess.Contains(p.Name, StringComparer.OrdinalIgnoreCase)))
                SidebarItem(place.Name, place.Path, place.Glyph, place.Brush);

        if (Settings.ShowFavourites && Section("Favourites", "Clear all Favourites…", Settings.Favourites.Count > 0 ? () => ClearFavourites() : null))
            AddFavouriteRows();

        AddRecentAndTagSections();

        if (Section("This PC"))
        {
            SidebarItem("This PC", FolderView.ThisPC, LocationIcons.ThisPC, "Fg2");
            foreach (var d in _driveCache)
                SidebarItem(d.Name, d.FullPath, LocationIcons.Drive, "Fg2", drive: d);
        }
    }

    private void AddFavouriteRows()
    {
        if (Settings.Favourites.Count == 0)
            _sidebar.Children.Add(new TextBlock { Text = "Right-click a folder \u2192 Add to Favourites", Foreground = Theme.Brush("Fg3"), FontSize = 12, Margin = new Thickness(10, 2, 4, 6), TextWrapping = TextWrapping.Wrap });
        for (int i = 0; i < Settings.Favourites.Count; i++)
        {
            var fav = Settings.Favourites[i];
            int index = i;
            SidebarItem(Path.GetFileName(fav.TrimEnd('\\')) is { Length: > 0 } n ? n : fav, fav, LocationIcons.Favourite, LocationIcons.Brush, isFavourite: true, favIndex: index);
        }
    }

    private async Task RefreshSidebarDrives()
    {
        int version = ++_driveRefresh;
        var drives = await Task.Run(FolderView.Drives);
        if (version != _driveRefresh) return; // a newer refresh (or This PC's own list) came in meanwhile
        SetSidebarDrives(drives);
    }

    /// <summary>The sidebar's drives from a fresh list (This PC passes the one it just loaded, so opening or refreshing This PC
    /// updates the sidebar too, without asking the drives twice).</summary>
    private void SetSidebarDrives(IEnumerable<FileItem> drives)
    {
        _driveRefresh++;
        _driveCache = drives.Where(d => d.IsDrive).ToList();
        RebuildSidebar();
    }

    private void SidebarItem(string text, string path, string glyph, string glyphBrush, bool isFavourite = false, int favIndex = -1, bool isRecent = false, FileItem? drive = null)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = Theme.Icon(glyph, 14, glyphBrush);
        icon.Width = 20;
        sp.Children.Add(icon);
        sp.Children.Add(new TextBlock { Text = text, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Theme.Brush("Fg1") });
        UIElement content = sp;
        if (drive is { HasUsage: true })
        {
            // a drive: its usage under the name, like This PC (exact figures in the tooltip)
            var usage = new StackPanel { Margin = new Thickness(26, 4, 0, 1) };
            usage.Children.Add(FolderView.NewUsageBar(drive, 4));
            usage.Children.Add(new TextBlock { Text = drive.UsageText, Foreground = Theme.Brush("Fg3"), FontSize = 11, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            content = new StackPanel { Children = { sp, usage } };
        }
        var row = new Border { Child = content, Padding = new Thickness(8, 5, 8, 5), CornerRadius = new CornerRadius(4), Background = Brushes.Transparent, ToolTip = drive is { HasUsage: true } ? drive.Tip : path == FolderView.ThisPC ? "This PC" : path, Cursor = Cursors.Hand, AllowDrop = path != FolderView.ThisPC };
        row.MouseEnter += (_, _) => row.Background = Theme.Brush("Hover");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) => { if (View != null) { View.Navigate(path); View.FocusList(); } };
        row.MouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Middle) AddTab(path, activate: true); };
        row.MouseRightButtonUp += (_, e) =>
        {
            var m = new ContextMenu();
            m.Items.Add(Theme.MenuItem("Open", "\uE8E5", () => View?.Navigate(path)));
            m.Items.Add(Theme.MenuItem("Open in new tab", "\uE8A7", () => AddTab(path, activate: true)));
            if (path != FolderView.ThisPC)
            {
                m.Items.Add(Theme.MenuItem("Open in File Explorer", "\uE838", () => Shell.ShowInExplorer(path)));
                AddIdeMenuItem(m, path);
            }
            if (isFavourite)
            {
                m.Items.Add(new Separator());
                m.Items.Add(Theme.MenuItem("Move up", "\uE70E", () => MoveFavourite(favIndex, -1), enabled: favIndex > 0));
                m.Items.Add(Theme.MenuItem("Move down", "\uE70D", () => MoveFavourite(favIndex, +1), enabled: favIndex < Settings.Favourites.Count - 1));
                m.Items.Add(Theme.MenuItem("Remove from Favourites", LocationIcons.FavouriteOutline, () => RemoveFavourite(path)));
                m.Items.Add(Theme.MenuItem("Clear all Favourites…", LocationIcons.Clear, () => ClearFavourites()));
            }
            else if (path != FolderView.ThisPC)
            {
                m.Items.Add(new Separator());
                m.Items.Add(IsFavourite(path)
                    ? Theme.MenuItem("Remove from Favourites", LocationIcons.FavouriteOutline, () => RemoveFavourite(path))
                    : Theme.MenuItem("Add to Favourites", LocationIcons.FavouriteOutline, () => AddFavourite(path)));
            }
            if (isRecent)
            {
                m.Items.Add(Theme.MenuItem("Remove from Recent", LocationIcons.Clear, () => RemoveRecent(path)));
                m.Items.Add(Theme.MenuItem("Clear Recent…", LocationIcons.Clear, () => ClearRecent()));
            }
            FilterMenu(m); // actions hidden in Settings
            m.PlacementTarget = row;
            m.IsOpen = true;
            e.Handled = true;
        };
        row.DragOver += (_, e) =>
        {
            e.Handled = true;
            e.Effects = DragDropEffects.None;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                e.Effects = FolderView.ChooseEffect(e, files, path);
        };
        row.Drop += (_, e) =>
        {
            e.Handled = true;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0 && View != null)
                DropFiles(View, files, path, FolderView.ChooseEffect(e, files, path));
        };
        _sidebar.Children.Add(row);
    }

    private void AddFavourite(string path)
    {
        if (path == FolderView.ThisPC || !Directory.Exists(path)) return;
        if (!IsFavourite(path)) Settings.Favourites.Add(path);
        Settings.Save();
        PlacesChanged();
        SetStatus($"Added '{Path.GetFileName(path.TrimEnd('\\'))}' to Favourites.");
    }

    private void MoveFavourite(int index, int delta)
    {
        int j = index + delta;
        if (index < 0 || j < 0 || j >= Settings.Favourites.Count) return;
        (Settings.Favourites[index], Settings.Favourites[j]) = (Settings.Favourites[j], Settings.Favourites[index]);
        Settings.Save();
        RebuildSidebar();
    }

    // =====================================================================================
    // IFolderHost
    // =====================================================================================

    public void OnSelectionChanged(FolderView view)
    {
        if (_active == null || view != _active.View) return;
        UpdateToolbarStates();
        var sel = view.SelectedItems;
        int count = view.Items.Count;
        string text = view.IsThisPC ? $"{count} drive{(count == 1 ? "" : "s")}" : $"{count:N0} item{(count == 1 ? "" : "s")}";
        if (view.IsSearchResults && view.IsSearching) text = $"Searching… {count:N0} found";
        if (sel.Count > 0)
        {
            text += $"   |   {sel.Count:N0} selected";
            var files = sel.Where(i => !i.IsDirectory).ToList();
            if (files.Count > 0) text += $"  {FileOps.FormatSize(files.Sum(f => f.Size))}";
            // tags of the selection (there is no Tags column; the list shows them as chips after the name)
            var tags = sel.SelectMany(i => i.Tags).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
            if (tags.Count > 0) text += "   |   " + string.Join("  ", tags.Select(t => "#" + t));
        }
        _statusLeft.Text = text;
        UpdateSearchChrome(); // the stop button turns back into "clear" when a search finishes

        var focused = sel.Count == 1 ? sel[0] : (sel.Count > 1 ? view.FocusedItem : null);
        if (focused is { IsVirtual: true })
        {
            if (Settings.PreviewVisible || _quickView != null) PreviewVirtual(focused, quickView: false);
            return;
        }
        ++_virtualPreviewVersion;
        string? path = focused?.FullPath;
        if (Settings.PreviewVisible) _preview.Show(path);
        if (_quickView != null && path != null && File.Exists(path)) _quickView.ShowPath(path);
    }

    public void OnNavigated(FolderView view)
    {
        if (!view.IsSearchResults && !view.IsThisPC && !view.IsArchive && Directory.Exists(view.CurrentPath))
        {
            var before = string.Join("|", Settings.RecentFolders.Take(6));
            Settings.AddRecent(view.CurrentPath);
            if (before != string.Join("|", Settings.RecentFolders.Take(6))) RebuildSidebar();
        }
        if (_active == null || view != _active.View) return;
        UpdateAddress();
        UpdateCommands();
        CheckDestination();
        if (!view.IsSearchResults && !_suppressSearchEvents && _search.Text.Length > 0)
        {
            _suppressSearchEvents = true;
            _search.Text = "";
            _suppressSearchEvents = false;
        }
        UpdateSearchHint();
    }

    public void OpenItem(FolderView view, FileItem item)
    {
        if (item.IsDirectory) { view.Navigate(item.FullPath); view.FocusList(); }
        else if (item.IsVirtual) OpenVirtual(item);
        else if (Settings.OpenArchivesAsFolders && ArchiveFormats.IsArchive(item.FullPath)) { view.Navigate(item.FullPath); view.FocusList(); }
        else Shell.Open(item.FullPath);
    }

    public void CommitRename(FolderView view, FileItem item, string newName)
    {
        var err = FileOps.ValidateName(newName);
        if (err != null) { MessageBox.Show(this, err, "Rename", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        ReleasePreviews(new[] { item.FullPath });
        try
        {
            var oldPath = item.FullPath;
            var newPath = FileOps.Rename(oldPath, newName);
            if (!item.IsDirectory && !string.Equals(Path.GetExtension(oldPath), Path.GetExtension(newPath), StringComparison.OrdinalIgnoreCase))
                SetStatus("Heads up: you changed the file extension.");
            _undo.Push(new RenameUndo(oldPath, newPath));
            Core.Tools.TagStore.Shared.Move(oldPath, newPath);
            if (item.IsDirectory) { Settings.MoveFolderViews(oldPath, newPath); Settings.Save(); } // the folder keeps its view
            _ = view.Refresh(new[] { newPath });
            RefreshOtherViews(view, Path.GetDirectoryName(newPath));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Rename", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void DropFiles(FolderView view, string[] files, string targetDir, DragDropEffects effect)
    {
        if (effect is not (DragDropEffects.Copy or DragDropEffects.Move)) return;
        Dispatcher.BeginInvoke(new Action(() => Transfer(files, targetDir, effect == DragDropEffects.Move)));
    }

    public void SetStatus(string message)
    {
        _statusRight.Text = message;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    // =====================================================================================
    // Context menu
    // =====================================================================================

    public ContextMenu BuildContextMenu(FolderView view, IList<FileItem> sel)
    {
        RefreshClipboardState();
        var menu = FilterMenu(BuildContextMenuCore(view, sel));
        ApplyMenuAvailability(menu, view, sel);
        return menu;
    }

    private ContextMenu BuildContextMenuCore(FolderView view, IList<FileItem> sel)
    {
        var m = new ContextMenu();
        var files = sel.Where(i => !i.IsDirectory).ToList();
        bool canPaste = Availability(view, sel, executing: true).Paste;

        if (sel.Count == 0 && view.IsArchive)
        {
            var archive = view.ArchiveFile!;
            var dir = Path.GetDirectoryName(archive)!;
            m.Items.Add(Theme.MenuItem("Extract all here", "\uE8B7", () => _ = ExtractAsync(archive, dir, null, ConflictPolicy.Ask, null, null, true, false)));
            m.Items.Add(Theme.MenuItem("Extract all to…", "\uE8B7", () => ExtractWithDialog(archive, null, null)));
            m.Items.Add(new Separator());
            m.Items.Add(Theme.MenuItem("Refresh", "\uE72C", RefreshActive, "F5"));
            m.Items.Add(Theme.MenuItem("Close archive", "\uE74A", () => { view.Navigate(dir, select: archive); view.FocusList(); }));
            m.Items.Add(Theme.MenuItem("Copy archive path", "\uE8C8", () => SetClipboardText(archive)));
            return m;
        }

        if (sel.Count == 0)
        {
            bool real = view.CanModify;
            m.Items.Add(Theme.MenuItem("New folder", "\uE8F4", NewFolder, "Ctrl+Shift+N", real));
            m.Items.Add(Theme.SubMenu("New", "\uE710", real ? NewMenuItems() : Array.Empty<object>(), real));
            m.Items.Add(Theme.MenuItem("Paste", "\uE77F", Paste, "Ctrl+V", canPaste));
            m.Items.Add(Theme.MenuItem(_undo.CanUndo ? "Undo: " + Shorten(_undo.NextDescription!) : "Undo", "\uE7A7", Undo, "Ctrl+Z", _undo.CanUndo));
            m.Items.Add(new Separator());
            m.Items.Add(Theme.MenuItem("Refresh", "\uE72C", RefreshActive, "F5"));
            m.Items.Add(Theme.MenuItem(Settings.ShowHidden ? "Hide hidden files" : "Show hidden files", "\uE890", ToggleHidden, "Ctrl+H"));
            if (real)
            {
                m.Items.Add(new Separator());
                m.Items.Add(Theme.MenuItem("Copy folder path", "\uE8C8", () => SetClipboardText(view.CurrentPath)));
                m.Items.Add(Theme.MenuItem("Add folder to Favourites", "\uE734", () => AddFavourite(view.CurrentPath)));
                m.Items.Add(Theme.MenuItem("Advanced search here…", "\uE721", AdvancedSearch, "Ctrl+Shift+F"));
                m.Items.Add(Theme.MenuItem("Find duplicates here…", "\uE8C8", () => FindDuplicates(new[] { view.CurrentPath })));
                m.Items.Add(Theme.MenuItem("Folder sizes…", "\uEDA2", () => FolderSizes(view.CurrentPath)));
                m.Items.Add(Theme.MenuItem("Open in File Explorer", "\uE838", () => Shell.ShowInExplorer(view.CurrentPath)));
                m.Items.Add(Theme.MenuItem("Open terminal here", "\uE756", () => Shell.OpenTerminal(view.CurrentPath)));
                AddIdeMenuItem(m, view.CurrentPath);
                m.Items.Add(Theme.MenuItem("Properties", "\uE946", () => Shell.Properties(view.CurrentPath, this)));
            }
            return m;
        }

        var first = sel[0];
        var open = Theme.MenuItem("Open", "\uE8E5", () => OpenItem(view, first), "Enter");
        open.FontWeight = FontWeights.SemiBold;
        m.Items.Add(open);
        if (sel.Count == 1 && first.IsDirectory)
        {
            m.Items.Add(Theme.MenuItem("Open in new tab", "\uE8A7", () => AddTab(first.FullPath, activate: true)));
            if (!first.IsVirtual && !view.IsArchive) AddIdeMenuItem(m, first.FullPath);
        }
        if (sel.Count == 1 && !first.IsDirectory && !first.IsVirtual)
            m.Items.Add(Theme.MenuItem("Open with…", "\uE7AC", () => Shell.OpenWith(first.FullPath, this)));
        if (sel.Count == 1 && IsBrowsableArchive(first) && Settings.OpenArchivesAsFolders)
            m.Items.Add(Theme.MenuItem("Open with default app", "\uE8A7", () => Shell.Open(first.FullPath)));
        if (view.IsSearchResults)
            m.Items.Add(Theme.MenuItem("Open file location", "\uE838", () => { var p = first.FullPath; view.Navigate(first.Folder, select: p); }));
        if (files.Count >= 1)
            m.Items.Add(Theme.MenuItem("Quick view", "\uE7B3", () => ToggleQuickView(files[0].FullPath), "Space"));
        if (view.IsArchive)
        {
            m.Items.Add(new Separator());
            m.Items.Add(Theme.MenuItem("Copy", "\uE8C8", () => CopyVirtual(view, sel), "Ctrl+C"));
            m.Items.Add(Theme.MenuItem("Copy path", "\uE71B", () => SetClipboardText(string.Join(Environment.NewLine, sel.Select(s => s.FullPath))), "Ctrl+Shift+C"));
            AddArchiveMenuItems(m, view, sel);
            return m;
        }
        if (files.Count == 2 && sel.Count == 2)
            m.Items.Add(Theme.MenuItem("Compare these two", "\uE8AB", CompareSelection, "Ctrl+D"));

        if (!first.IsDrive)
        {
            m.Items.Add(new Separator());
            m.Items.Add(Theme.MenuItem("Cut", "\uE8C6", () => CopySelection(true), "Ctrl+X"));
            m.Items.Add(Theme.MenuItem("Copy", "\uE8C8", () => CopySelection(false), "Ctrl+C"));
            if (sel.Count == 1 && first.IsDirectory && (_clipboardFiles || _clipboardImage))
                m.Items.Add(Theme.MenuItem("Paste into folder", "\uE77F", () => PasteInto(first.FullPath)));
        }
        m.Items.Add(Theme.MenuItem("Copy path", "\uE71B", () => SetClipboardText(string.Join(Environment.NewLine, sel.Select(s => s.FullPath))), "Ctrl+Shift+C"));

        if (!first.IsDrive)
        {
            m.Items.Add(new Separator());
            m.Items.Add(Theme.MenuItem("Rename", "\uE8AC", RenameSelection, "F2", sel.Count == 1));
            m.Items.Add(Theme.MenuItem("Delete", "\uE74D", () => DeleteSelection(false), "Del"));
            m.Items.Add(Theme.MenuItem("Delete permanently", "\uE74D", () => DeleteSelection(true), "Shift+Del"));
        }

        m.Items.Add(new Separator());
        bool anyBlocked = files.Take(200).Any(f => { try { return Native.HasMarkOfTheWeb(f.FullPath); } catch { return false; } });
        if (anyBlocked)
            m.Items.Add(Theme.MenuItem("Unblock (downloaded-file mark)", "\uEA18", () => UnblockSelection()));
        if (sel.All(s => s.IsDirectory))
            m.Items.Add(Theme.MenuItem(sel.Count == 1 ? "Add to Favourites" : "Add all to Favourites", "\uE734", () => { foreach (var s in sel) AddFavourite(s.FullPath); }));
        if (!first.IsDrive)
            m.Items.Add(Theme.MenuItem("Show in File Explorer", "\uE838", () => Shell.ShowInExplorer(first.FullPath)));
        m.Items.Add(Theme.MenuItem("Properties", "\uE946", () => Shell.Properties(first.FullPath, this), "Alt+Enter"));
        AddArchiveMenuItems(m, view, sel);
        AddToolMenuItems(m, view, sel);
        return m;
    }

    private static string Shorten(string s) => s.Length > 50 ? s.Substring(0, 47) + "…" : s;

    // =====================================================================================
    // Commands
    // =====================================================================================

    private void UpdateAddress()
    {
        if (View == null || _address.IsKeyboardFocused) return;
        _address.Text = View.IsSearchResults ? $"Search results in {View.CurrentPath}" : (View.CurrentPath == FolderView.ThisPC ? "This PC" : View.CurrentPath);
        UpdateSearchHint();
    }

    private void UpdateSearchHint() => UpdateSearchChrome();

    private void UpdateCommands()
    {
        if (View == null) return;
        _btnBack.IsEnabled = View.CanGoBack;
        _btnForward.IsEnabled = View.CanGoForward;
        _btnUp.IsEnabled = View.CanGoUp;
        _btnUndo.IsEnabled = _undo.CanUndo;
        UpdateToolbarStates();
        _btnUndo.ToolTip = _undo.CanUndo ? $"Undo: {_undo.NextDescription} (Ctrl+Z)" : "Nothing to undo";
        UpdateFavouriteButton();
    }

    internal string StatusText => _statusLeft.Text;
    internal void RefreshActiveForTest() => RefreshActive();

    private void RefreshActive()
    {
        if (View == null) return;
        if (View.IsSearchResults) View.StartSearch(View.SearchQuery);
        else _ = View.Refresh();
    }

    private void RefreshOtherViews(FolderView except, string? folder)
    {
        if (folder == null) return;
        foreach (var t in _tabs)
            if (t.View != except && string.Equals(t.View.CurrentPath, folder, StringComparison.OrdinalIgnoreCase))
                _ = t.View.Refresh();
    }

    private void RefreshFolders(IEnumerable<string> folders)
    {
        var set = new HashSet<string>(folders.Select(f => f.TrimEnd('\\')), StringComparer.OrdinalIgnoreCase);
        foreach (var t in _tabs)
            if (set.Contains(t.View.CurrentPath.TrimEnd('\\'))) _ = t.View.Refresh();
    }

    private void ReleasePreviews(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        _preview.ReleaseIfShowing(list);
        _quickView?.Release(list);
        foreach (var w in _compareWindows) w.Release(list);
    }

    private void ToggleHidden()
    {
        Settings.ShowHidden = !Settings.ShowHidden;
        foreach (var t in _tabs) _ = t.View.Refresh();
        SetStatus(Settings.ShowHidden ? "Showing hidden files." : "Hidden files are hidden.");
    }

    private void SetClipboardText(string text)
    {
        try { _clipboard.Write(new DataObject(DataFormats.UnicodeText, text)); RefreshClipboardState(); } catch { }
    }

    private void CopySelection(bool cut)
    {
        if (View == null || !CanExecute(cut ? "cut" : "copy")) return;
        var sel = View.SelectedItems.Where(i => !i.IsDrive).ToList();
        if (sel.Count == 0) return;
        if (View.IsArchive)
        {
            if (cut) { SetStatus("Archives are read-only here. Use Copy, or extract the files first."); return; }
            CopyVirtual(View, sel);
            return;
        }
        var paths = new StringCollection();
        paths.AddRange(sel.Select(s => s.FullPath).ToArray());
        var data = new DataObject();
        data.SetFileDropList(paths);
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
        try { _clipboard.Write(data); RefreshClipboardState(); }
        catch (Exception ex) { SetStatus("Clipboard is busy: " + ex.Message); return; }

        _cutPaths.Clear();
        if (cut) foreach (var s in sel) _cutPaths.Add(s.FullPath);
        foreach (var t in _tabs) MarkCut(t.View);
        RememberClipboard(sel.Select(s => s.FullPath).ToList(), cut);
        SetStatus($"{(cut ? "Cut" : "Copied")} {sel.Count} item{(sel.Count == 1 ? "" : "s")}.");
    }

    private void MarkCut(FolderView view)
    {
        foreach (var i in view.Items) i.IsCut = _cutPaths.Count > 0 && _cutPaths.Contains(i.FullPath);
    }

    private void Paste()
    {
        if (View == null || !CanExecute("paste")) return;
        if (View.IsArchive) { SetStatus("Archives are read-only here. Extract the archive to add or change files."); return; }
        if (!View.CanModify) return;
        PasteInto(View.CurrentPath);
    }

    internal Task PasteTask { get; private set; } = Task.CompletedTask;
    private void PasteInto(string targetDir) => PasteTask = PasteIntoAsync(targetDir);

    private async Task PasteIntoAsync(string targetDir)
    {
        if (!FileAccessPolicy.WritableDirectory(targetDir)) return;
        string[] files;
        bool move = false;
        try
        {
            var data = _clipboard.Read();
            if (data == null) return;
            // File drops take priority over images, including when the advertised file data is invalid.
            if (!data.GetDataPresent(DataFormats.FileDrop))
            {
                if (!ClipboardImageFile.HasImage(data)) return;
                var image = ClipboardImageFile.Capture(data, _clipboard);
                var when = DateTime.Now;
                var path = await Task.Run(() => ClipboardImageFile.Save(targetDir, image.Png, image.Bitmap, when));
                if (_commandsClosed) return;
                _undo.Push(new CreatedUndo($"Paste '{Path.GetFileName(path)}'", new[] { path }));
                foreach (var tab in _tabs.ToList())
                    if (AppSettings.SameFolder(tab.View.CurrentPath, targetDir)) await tab.View.Refresh(new[] { path });
                SetStatus($"Saved '{Path.GetFileName(path)}'.");
                return;
            }
            files = data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>();
            if (files.Length == 0 || files.Any(p => !File.Exists(p) && !Directory.Exists(p))) { SetStatus("The clipboard files are no longer available."); return; }
            if (data?.GetData("Preferred DropEffect") is MemoryStream ms && ms.Length >= 4)
            {
                var buf = new byte[4];
                ms.Position = 0;
                ms.Read(buf, 0, 4);
                move = (BitConverter.ToInt32(buf, 0) & 2) != 0;
            }
        }
        catch (Exception ex) { SetStatus("Couldn't read the clipboard: " + ex.Message); return; }

        Transfer(files, targetDir, move);
        if (move)
        {
            try { _clipboard.Clear(); RefreshClipboardState(); } catch { }
            _cutPaths.Clear();
            foreach (var t in _tabs) MarkCut(t.View);
        }
    }

    private void Transfer(IList<string> files, string targetDir, bool move)
    {
        if (files.Count == 0) return;
        if (move) ReleasePreviews(files);
        var result = FileOps.Transfer(files, targetDir, move, Hwnd, target => ConflictDialog.Ask(this, target));
        foreach (var (from, to) in result.Moved) Core.Tools.TagStore.Shared.Move(from, to);

        var verb = move ? "Move" : "Copy";
        int done = move ? result.Moved.Count : result.Created.Count;
        if (done > 0 || result.Replaced.Count > 0)
        {
            var desc = $"{verb} {done} item{(done == 1 ? "" : "s")} to '{Path.GetFileName(targetDir.TrimEnd('\\'))}'";
            if (move) _undo.Push(new MoveUndo(desc, result.Moved, result.Replaced));
            else _undo.Push(new CreatedUndo(desc, result.Created, result.Replaced));
        }

        var affected = new List<string> { targetDir };
        if (move) affected.AddRange(result.Moved.Select(m => Path.GetDirectoryName(m.from) ?? ""));
        RefreshFolders(affected);
        if (View != null && string.Equals(View.CurrentPath.TrimEnd('\\'), targetDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            _ = View.Refresh(move ? result.Moved.Select(m => m.to) : result.Created);

        if (result.Errors.Count > 0)
            MessageBox.Show(this, string.Join("\n", result.Errors), verb, MessageBoxButton.OK, MessageBoxImage.Warning);
        SetStatus(result.Cancelled ? $"{verb} cancelled after {done} item{(done == 1 ? "" : "s")}." : $"{(move ? "Moved" : "Copied")} {done} item{(done == 1 ? "" : "s")}.");
    }

    private void DeleteSelection(bool permanent)
    {
        if (View == null || !CanExecute("delete")) return;
        if (View.IsArchive) { SetStatus("Archives are read-only here. Items inside them can't be deleted."); return; }
        var sel = View.SelectedItems.Where(i => !i.IsDrive).ToList();
        if (sel.Count == 0) return;
        if (permanent)
        {
            var what = sel.Count == 1 ? $"'{sel[0].Name}'" : $"these {sel.Count} items";
            if (MessageBox.Show(this, $"Permanently delete {what}?\nThis can't be undone.", "Delete permanently",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        }

        // pick the item to select afterwards (like Explorer)
        var items = View.Items.ToList();
        int firstIndex = items.IndexOf(sel[0]);
        var remaining = items.Where(i => !sel.Contains(i)).ToList();
        string? next = remaining.Count == 0 ? null : remaining[Math.Clamp(firstIndex, 0, remaining.Count - 1)].FullPath;

        var paths = sel.Select(s => s.FullPath).ToList();
        ReleasePreviews(paths);
        var when = DateTime.UtcNow;
        bool ok = FileOps.Delete(paths, permanent, Hwnd);
        var gone = paths.Where(p => !FileOps.Exists(p)).ToList();
        if (!permanent && gone.Count > 0) _undo.Push(new DeleteUndo(gone, when));
        _ = View.Refresh(next != null ? new[] { next } : Array.Empty<string>());
        RefreshOtherViews(View, View.CurrentPath);
        SetStatus(ok
            ? $"{(permanent ? "Permanently deleted" : "Moved to Recycle Bin:")} {gone.Count} item{(gone.Count == 1 ? "" : "s")}.{(permanent ? "" : "  Ctrl+Z to undo.")}"
            : $"Delete stopped. {gone.Count} item{(gone.Count == 1 ? "" : "s")} deleted.");
    }

    private void Undo()
    {
        if (!_undo.CanUndo) { SetStatus("Nothing to undo."); return; }
        _preview.Clear();
        _quickView?.Close();
        var (action, error) = _undo.Undo(Hwnd);
        if (action == null) return;
        RefreshFolders(action.AffectedFolders);
        if (error != null) MessageBox.Show(this, error, "Undo", MessageBoxButton.OK, MessageBoxImage.Warning);
        else SetStatus("Undone: " + action.Description);
    }

    private async void NewFolder()
    {
        if (View == null || !CanExecute("newfolder")) return;
        try
        {
            var view = View;
            var path = FileOps.NewFolder(view.CurrentPath);
            _undo.Push(new CreatedUndo($"Create folder '{Path.GetFileName(path)}'", new[] { path }));
            await view.Refresh(new[] { path });
            view.BeginRename();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "New folder", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void UnblockSelection()
    {
        if (View == null) return;
        int n = 0;
        foreach (var f in View.SelectedItems.Where(i => !i.IsDirectory))
            if (Native.HasMarkOfTheWeb(f.FullPath) && Native.Unblock(f.FullPath)) n++;
        SetStatus($"Unblocked {n} file{(n == 1 ? "" : "s")}.");
        _preview.Reload();
    }

    private void SetPreviewVisible(bool visible)
    {
        if (!visible && Settings.PreviewVisible && _previewCol.ActualWidth > 50) Settings.PreviewWidth = _previewCol.ActualWidth;
        Settings.PreviewVisible = visible;
        _previewCol.Width = visible ? new GridLength(Math.Clamp(Settings.PreviewWidth, 250, 2000)) : new GridLength(0);
        _previewCol.MinWidth = visible ? 200 : 0;
        _previewSplitCol.Width = new GridLength(visible ? 4 : 0);
        _preview.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (_btnPreview != null) _btnPreview.Background = visible ? Theme.Brush("Bg3") : Brushes.Transparent;
        if (visible && View != null) OnSelectionChanged(View);
        else
        {
            _preview.Clear();
            // Hidden for a while: give the preview engine's memory back (~130-500 MB in WebView2's processes). Not at once, so
            // hiding and showing the pane again soon stays instant; when shown later the engine starts again (~1 s).
            _releasePreview ??= new DispatcherTimer(PreviewReleaseAfter, DispatcherPriority.Background, (_, _) =>
            {
                _releasePreview!.Stop();
                if (!Settings.PreviewVisible) _preview.ReleaseEngine();
            }, Dispatcher);
            _releasePreview.Stop();
            _releasePreview.Start();
        }
        if (visible) _releasePreview?.Stop();
    }

    private DispatcherTimer? _releasePreview;
    /// <summary>How long a hidden preview pane keeps its engine (the benchmark and tests shorten it).</summary>
    internal static TimeSpan PreviewReleaseAfter = TimeSpan.FromSeconds(30);

    private void ToggleQuickView(string? path)
    {
        if (_quickView != null && path == null) { _quickView.Close(); return; }
        if (!CanExecute("quickview")) return;
        if (path != null && !File.Exists(path) && View?.IsArchive != true) return;
        if (path == null && View?.FocusedItem is { IsVirtual: true, IsDirectory: false } virt)
        {
            if (_quickView == null)
            {
                _quickView = new QuickViewWindow(this);
                _quickView.Closed += (_, _) => { _quickView = null; Activate(); View?.FocusList(); };
                _quickView.StepRequested += (_, d) => StepSelection(d);
                _quickView.ImageToolsRequested += (_, p) => OpenImageTools(p);
                _quickView.Show();
            }
            PreviewVirtual(virt, quickView: true);
            return;
        }
        path ??= View?.SelectedItems.FirstOrDefault(i => !i.IsDirectory)?.FullPath ?? View?.FocusedItem?.FullPath;
        if (path == null) return;
        if (_quickView == null)
        {
            _quickView = new QuickViewWindow(this);
            _quickView.Closed += (_, _) => { _quickView = null; Activate(); View?.FocusList(); };
            _quickView.StepRequested += (_, d) => StepSelection(d);
            _quickView.ImageToolsRequested += (_, p) => OpenImageTools(p);
            _quickView.Show();
        }
        _quickView.ShowPath(path);
    }

    private void StepSelection(int delta)
    {
        if (View == null) return;
        var list = View.List;
        int count = list.Items.Count;
        if (count == 0) return;
        int i = Math.Clamp(list.SelectedIndex + delta, 0, count - 1);
        list.SelectedIndex = i;
        list.ScrollIntoView(list.SelectedItem);
    }

    private void CompareSelection()
    {
        if (View == null || !CanExecute("compare")) return;
        if (View.IsArchive) { SetStatus("Extract the files first to compare them."); return; }
        var files = View.SelectedItems.Where(i => !i.IsDirectory).Select(i => i.FullPath).ToList();
        string left = files[0], right = files[1];

        var w = new CompareWindow(this, left, right);
        _compareWindows.Add(w);
        w.Closed += (_, _) => _compareWindows.Remove(w);
        w.Show();
    }

    // =====================================================================================
    // Address bar & search
    // =====================================================================================

    private void OnAddressKey(object sender, KeyEventArgs e)
    {
        if (View == null) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            var text = _address.Text.Trim().Trim('"');
            var path = FolderView.NormalizePath(text);
            if (path != FolderView.ThisPC && File.Exists(path))
            {
                View.Navigate(Path.GetDirectoryName(path)!, select: path);
            }
            else if (FolderView.Exists(path))
            {
                View.Navigate(path);
            }
            else
            {
                SetStatus($"Can't find '{text}'.");
                return;
            }
            View.FocusList();
            UpdateAddress();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            View.FocusList();
            UpdateAddress();
        }
    }

    private void OnSearchTextChanged()
    {
        UpdateSearchHint();
        if (_suppressSearchEvents || View == null) return;
        if (View.IsSearchResults) return; // results stay until Enter / Esc
        _filterTimer.Stop();
        if (!Settings.FilterWhileTyping) { if (_search.Text.Length == 0) View.SetFilter(""); return; } // Enter filters / searches
        _filterTimer.Start();
        if (_search.Text.Length > 0 && SearchesSubfolders && !View.IsThisPC && !View.IsArchive)
            SetStatus("Filtering this folder. Press Enter to search all subfolders too.");
    }

    private void OnSearchKey(object sender, KeyEventArgs e)
    {
        if (View == null) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            RunSearch(newTab: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            ClearSearch();
        }
        else if (e.Key == Key.Down)
        {
            e.Handled = true;
            View.FocusList();
        }
    }

    // =====================================================================================
    // Keyboard
    // =====================================================================================

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool ctrl = (mods & ModifierKeys.Control) != 0, shift = (mods & ModifierKeys.Shift) != 0, alt = (mods & ModifierKeys.Alt) != 0;
        // typing in a text box, or working inside a preview (copying text from a PDF etc.)
        bool inText = Keyboard.FocusedElement is TextBox ||
                      (Keyboard.FocusedElement is DependencyObject fd && VisualTree.FindAncestor<PreviewPane>(fd) != null);

        // ---- global shortcuts (work even while typing) ----
        if (ctrl && key == Key.T) { OpenNewTab(); e.Handled = true; return; }
        if (ctrl && key == Key.W) { if (_active != null) CloseTab(_active); e.Handled = true; return; }
        if (ctrl && key == Key.Tab) { CycleTab(shift ? -1 : 1); e.Handled = true; return; }
        if (ctrl && !shift && key >= Key.D1 && key <= Key.D9) { int i = key - Key.D1; if (i < _tabs.Count) ActivateTab(_tabs[i]); e.Handled = true; return; }
        if (!inText && HandleViewKeys(key, ctrl, shift)) { e.Handled = true; return; }
        if ((ctrl && key == Key.L) || (alt && key == Key.D) || key == Key.F4 && !alt) { _address.Focus(); _address.SelectAll(); e.Handled = true; return; }
        if ((ctrl && key == Key.F) || key == Key.F3 || (ctrl && key == Key.E)) { _search.Focus(); _search.SelectAll(); e.Handled = true; return; }
        if (alt && key == Key.Left || key == Key.BrowserBack) { View?.GoBack(); e.Handled = true; return; }
        if (alt && key == Key.Right || key == Key.BrowserForward) { View?.GoForward(); e.Handled = true; return; }
        if (alt && key == Key.Up) { View?.GoUp(); e.Handled = true; return; }
        if (key == Key.F5) { RefreshActive(); e.Handled = true; return; }
        if (ctrl && shift && key == Key.P) { ShowPalette(); e.Handled = true; return; }
        if (ctrl && shift && key == Key.F) { AdvancedSearch(); e.Handled = true; return; }
        if (ctrl && key == Key.P) { SetPreviewVisible(!Settings.PreviewVisible); e.Handled = true; return; }

        if (inText || View == null) return;

        // ---- file list shortcuts ----
        if (HandleFileShortcut(key, ctrl, shift)) { e.Handled = true; return; }
        if (ctrl && shift && key == Key.C) { SetClipboardText(string.Join(Environment.NewLine, View.SelectedItems.Select(s => s.FullPath))); SetStatus("Path copied."); e.Handled = true; }
        else if (ctrl && key == Key.Z) { Undo(); e.Handled = true; }
        else if (ctrl && key == Key.H) { ToggleHidden(); e.Handled = true; }
        else if (key == Key.Back) { View.GoUp(); e.Handled = true; }
        else if (alt && key == Key.Enter) { var f = View.FocusedItem; if (f != null && !f.IsVirtual) Shell.Properties(f.FullPath, this); e.Handled = true; }
        else if (key == Key.Enter && !ctrl && !alt)
        {
            var sel = View.SelectedItems;
            if (sel.Count == 1) OpenItem(View, sel[0]);
            else foreach (var f in sel.Where(s => !s.IsDirectory && !s.IsVirtual).Take(10)) Shell.Open(f.FullPath);
            e.Handled = true;
        }
        else if (key == Key.Escape)
        {
            if (_quickView != null) _quickView.Close();
            else if (View.IsSearchResults) { _search.Text = ""; View.EndSearch(); }
            else if (_search.Text.Length > 0) { _search.Text = ""; View.SetFilter(""); }
            else View.List.SelectedItems.Clear();
            e.Handled = true;
        }
        else if (key == Key.Apps || (shift && key == Key.F10))
        {
            var target = View.FocusedItem != null ? View.List.ItemContainerGenerator.ContainerFromItem(View.FocusedItem) as UIElement : null;
            View.ShowContextMenu(target ?? View.List);
            e.Handled = true;
        }
    }

    // =====================================================================================
    // Settings
    // =====================================================================================

    private void SaveSettings()
    {
        Settings.WindowMaximized = WindowState == WindowState.Maximized;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!bounds.IsEmpty)
        {
            Settings.WindowLeft = bounds.Left;
            Settings.WindowTop = bounds.Top;
            Settings.WindowWidth = bounds.Width;
            Settings.WindowHeight = bounds.Height;
        }
        Settings.SidebarWidth = _sidebarCol.ActualWidth;
        if (Settings.PreviewVisible && _previewCol.ActualWidth > 50) Settings.PreviewWidth = _previewCol.ActualWidth;
        Settings.OpenTabs = _tabs.Select(t => t.View.CurrentPath).ToList();
        Settings.ActiveTab = _active != null ? _tabs.IndexOf(_active) : 0;
        Settings.Save();
    }
}
