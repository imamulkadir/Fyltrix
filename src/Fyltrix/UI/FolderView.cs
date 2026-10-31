using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Fyltrix.Core;
using Fyltrix.Core.Archives;

namespace Fyltrix.UI;

public interface IFolderHost
{
    AppSettings Settings { get; }
    void OnSelectionChanged(FolderView view);
    void OnNavigated(FolderView view);
    void OpenItem(FolderView view, FileItem item);
    void CommitRename(FolderView view, FileItem item, string newName);
    void DropFiles(FolderView view, string[] files, string targetDir, DragDropEffects effect);
    ContextMenu BuildContextMenu(FolderView view, IList<FileItem> selection);
    void SetStatus(string message);
    /// <summary>Asks for an archive password (null = cancelled).</summary>
    string? AskArchivePassword(string archiveFile, bool wasWrong);
    /// <summary>Extracts archive entries so they can be dragged out; null when not possible.</summary>
    DataObject? PrepareVirtualDrag(FolderView view, IList<FileItem> items);
}

public enum SortColumn { Name, Modified, Type, Size, Folder }

/// <summary>One tab's folder listing: navigation history, sorting, filtering, search, rename, drag and drop.</summary>
public sealed partial class FolderView : Grid
{
    public const string ThisPC = "";

    private readonly IFolderHost _host;
    public ListView List { get; } = new();
    private readonly GridView _grid = new();
    private readonly GridViewColumn _folderColumn;
    private readonly TextBlock _empty = new();
    private readonly Dictionary<SortColumn, GridViewColumnHeader> _headers = new();

    private readonly Stack<string> _back = new();
    private readonly Stack<string> _forward = new();
    private int _loadVersion;
    private FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _refreshTimer;
    private bool _changedWhileHidden;
    private CancellationTokenSource? _searchCts;
    private string _filter = "";
    private bool _closed;
    public string Filter => _filter;

    public ObservableCollection<FileItem> Items { get; private set; } = new();
    public string CurrentPath { get; private set; } = ThisPC;
    public bool IsSearchResults { get; private set; }
    public string SearchQuery { get; private set; } = "";
    public bool IsSearching { get; private set; }
    public SortColumn Sort { get; private set; } = SortColumn.Name;
    public bool Ascending { get; private set; } = true;
    public string? LoadError { get; private set; }

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;
    public bool CanGoUp => CurrentPath != ThisPC || IsSearchResults;
    public bool IsThisPC => CurrentPath == ThisPC && !IsSearchResults;

    /// <summary>Set while browsing inside an archive: the archive file, and the folder within it ("" = root).</summary>
    public string? ArchiveFile { get; private set; }
    public string ArchiveInner { get; private set; } = "";
    public bool IsArchive => ArchiveFile != null && !IsSearchResults;
    /// <summary>False for This PC, search results and archives, where new items, paste, rename and delete make no sense.</summary>
    public bool CanModify => !IsThisPC && !IsSearchResults && !IsArchive;

    private void UpdateArchiveState()
    {
        if (CurrentPath != ThisPC && !Directory.Exists(CurrentPath) && ArchiveBrowser.TryParse(CurrentPath, out var a, out var inner))
        {
            ArchiveFile = a;
            ArchiveInner = inner;
        }
        else
        {
            ArchiveFile = null;
            ArchiveInner = "";
        }
    }

    public static bool Exists(string path) => path == ThisPC || Directory.Exists(path) || ArchiveBrowser.TryParse(path, out _, out _);

    public string Title => IsSearchResults ? $"Search: {SearchQuery}"
        : CurrentPath == ThisPC ? "This PC"
        : Path.GetFileName(CurrentPath.TrimEnd('\\')) is { Length: > 0 } n ? n : CurrentPath;

    public IList<FileItem> SelectedItems => List.SelectedItems.Cast<FileItem>().ToList();
    public FileItem? FocusedItem => List.SelectedItem as FileItem;

    public event EventHandler? TitleChanged;

    /// <summary>Called when the view moves to another folder (or to search results), before its items load: the host puts the
    /// folder's own view mode on (see MainWindow.ApplyView), so a folder never shows briefly in the previous folder's view.</summary>
    public Action<FolderView>? LocationChanging { get; set; }
    public event EventHandler? ItemsLoaded;

    public FolderView(IFolderHost host, string path, string? select = null)
    {
        _host = host;
        Background = Theme.Brush("Bg1");
        // Settings → default sorting (a column header click changes it for this tab)
        if (host?.Settings is { } st && Enum.TryParse<SortColumn>(st.DefaultSort, true, out var sortCol) && sortCol != SortColumn.Folder)
        {
            Sort = sortCol;
            Ascending = st.DefaultSortAscending;
        }

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            if (Items.Any(i => i.IsEditing)) { _refreshTimer.Start(); return; } // don't kill an inline rename
            if (IsSearchResults || _closed) return;
            // a tab in the background doesn't re-read its folder on every change (a busy Downloads folder in a hidden tab
            // kept listing it again and again): it notes the change and refreshes once when it is shown
            if (!IsVisible) { _changedWhileHidden = true; return; }
            _ = Refresh();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible || !_changedWhileHidden || _closed) return;
            _changedWhileHidden = false;
            if (!IsSearchResults) _ = Refresh();
        };

        // ---------- columns ----------
        _grid.AllowsColumnReorder = true;
        _grid.Columns.Add(MakeColumn("Name", SortColumn.Name, 320, NameTemplate()));
        _grid.Columns.Add(MakeColumn("Date modified", SortColumn.Modified, 150, TextTemplate(nameof(FileItem.ModifiedText), "Fg2")));
        _typeColumn = MakeColumn("Type", SortColumn.Type, 170, TextTemplate(nameof(FileItem.TypeName), "Fg2"));
        _grid.Columns.Add(_typeColumn);
        _grid.Columns.Add(MakeColumn("Size", SortColumn.Size, 90, TextTemplate(nameof(FileItem.SizeText), "Fg2", right: true)));
        // tags are chips after the name (NameTemplate), not a column of their own
        _folderColumn = MakeColumn("Folder", SortColumn.Folder, 320, TextTemplate(nameof(FileItem.Folder), "Fg3"));

        List.View = _grid;
        List.SelectionMode = SelectionMode.Extended;
        List.AllowDrop = true;
        List.Padding = new Thickness(4, 0, 4, 0);
        VirtualizingPanel.SetIsVirtualizing(List, true);
        VirtualizingPanel.SetVirtualizationMode(List, VirtualizationMode.Recycling);
        ScrollViewer.SetIsDeferredScrollingEnabled(List, false);
        TextSearch.SetTextPath(List, nameof(FileItem.Name));
        List.SelectionChanged += (_, _) => _host.OnSelectionChanged(this);
        List.MouseDoubleClick += OnDoubleClick;
        List.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnHeaderClick));
        List.PreviewMouseLeftButtonDown += OnPreviewMouseDown;
        List.PreviewMouseLeftButtonUp += OnPreviewMouseUp;
        List.PreviewMouseMove += OnPreviewMouseMove;
        List.PreviewMouseRightButtonDown += OnRightDown;
        List.MouseRightButtonUp += OnRightUp;
        List.MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.XButton1) { GoBack(); e.Handled = true; }
            else if (e.ChangedButton == MouseButton.XButton2) { GoForward(); e.Handled = true; }
        };
        List.DragOver += OnDragOver;
        List.DragEnter += OnDragOver;
        List.Drop += OnDrop;
        Children.Add(List);

        _empty.Foreground = Theme.Brush("Fg3");
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Top;
        _empty.Margin = new Thickness(0, 70, 0, 0);
        _empty.IsHitTestVisible = false;
        _empty.TextWrapping = TextWrapping.Wrap;
        _empty.TextAlignment = TextAlignment.Center;
        Children.Add(_empty);

        UpdateHeaders();
        Navigate(path, addHistory: false, select: select);
    }

    // =====================================================================================
    // Columns / templates
    // =====================================================================================

    private GridViewColumn MakeColumn(string title, SortColumn sort, double width, DataTemplate template)
    {
        var header = new GridViewColumnHeader { Content = title, Tag = sort };
        _headers[sort] = header;
        return new GridViewColumn { Header = header, Width = width, CellTemplate = template };
    }

    private static DataTemplate TextTemplate(string property, string brush, bool right = false)
    {
        var tb = new FrameworkElementFactory(typeof(TextBlock));
        tb.SetBinding(TextBlock.TextProperty, new Binding(property));
        tb.SetValue(TextBlock.ForegroundProperty, Theme.Brush(brush));
        tb.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        if (right) tb.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        tb.SetBinding(UIElement.OpacityProperty, new Binding(nameof(FileItem.ItemOpacity)));
        return new DataTemplate { VisualTree = tb };
    }

    private DataTemplate NameTemplate()
    {
        var root = new FrameworkElementFactory(typeof(DockPanel));
        root.SetValue(DockPanel.LastChildFillProperty, true);
        root.SetBinding(UIElement.OpacityProperty, new Binding(nameof(FileItem.ItemOpacity)));

        var dot = new FrameworkElementFactory(typeof(System.Windows.Shapes.Ellipse));
        dot.SetValue(FrameworkElement.WidthProperty, 8.0);
        dot.SetValue(FrameworkElement.HeightProperty, 8.0);
        dot.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 4, 0));
        dot.SetValue(DockPanel.DockProperty, Dock.Left);
        dot.SetBinding(System.Windows.Shapes.Shape.FillProperty, new Binding(nameof(FileItem.LabelBrush)));
        dot.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(FileItem.HasLabel)) { Converter = new BoolToVisibility() });
        root.AppendChild(dot);

        var img = new FrameworkElementFactory(typeof(Image));
        img.SetBinding(Image.SourceProperty, new Binding(nameof(FileItem.Icon)));
        img.SetValue(FrameworkElement.WidthProperty, 16.0);
        img.SetValue(FrameworkElement.HeightProperty, 16.0);
        img.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 0, 7, 0));
        img.SetValue(DockPanel.DockProperty, Dock.Left);
        img.SetValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.HighQuality);
        root.AppendChild(img);

        var cell = new FrameworkElementFactory(typeof(Grid));
        var line = new FrameworkElementFactory(typeof(DockPanel));
        line.SetValue(DockPanel.LastChildFillProperty, true);
        line.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        line.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(FileItem.IsEditing)) { Converter = new BoolToVisibility { Invert = true } });
        var chips = TagChips();
        chips.SetValue(DockPanel.DockProperty, Dock.Right);
        line.AppendChild(chips);
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(FileItem.DisplayName)));
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        line.AppendChild(text);
        cell.AppendChild(line);

        var box = new FrameworkElementFactory(typeof(TextBox));
        box.SetValue(FrameworkElement.TagProperty, "rename");
        box.SetValue(Control.PaddingProperty, new Thickness(2, 0, 2, 0));
        box.SetValue(FrameworkElement.MarginProperty, new Thickness(-3, -2, 0, -2));
        box.SetBinding(TextBox.TextProperty, new Binding(nameof(FileItem.EditName)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        box.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(FileItem.IsEditing)) { Converter = new BoolToVisibility() });
        box.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnRenameKey));
        box.AddHandler(UIElement.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnRenameLostFocus));
        cell.AppendChild(box);

        // drives (This PC): a usage bar and "120 GB free of 476 GB" under the name
        var column = new FrameworkElementFactory(typeof(StackPanel));
        column.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        column.AppendChild(cell);
        var usage = new FrameworkElementFactory(typeof(StackPanel));
        usage.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        usage.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 3));
        usage.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(FileItem.HasUsage)) { Converter = new BoolToVisibility() });
        var bar = UsageBar();
        bar.SetValue(FrameworkElement.WidthProperty, 140.0); // bar + "609 GB free of 952 GB" fit the default Name column
        usage.AppendChild(bar);
        var free = new FrameworkElementFactory(typeof(TextBlock));
        free.SetBinding(TextBlock.TextProperty, new Binding(nameof(FileItem.UsageText)));
        free.SetValue(TextBlock.ForegroundProperty, Theme.Brush("Fg2"));
        free.SetValue(TextBlock.FontSizeProperty, 11.5);
        free.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 0, 0, 0));
        free.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        usage.AppendChild(free);
        column.AppendChild(usage);
        root.AppendChild(column);
        root.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(FileItem.UsageTip)));

        return new DataTemplate { VisualTree = root };
    }

    /// <summary>A drive's used space as a thin bar: filled part used, track free; red when under 10 % is free.</summary>
    internal static FrameworkElementFactory UsageBar()
    {
        var bar = new FrameworkElementFactory(typeof(ProgressBar));
        bar.SetValue(RangeBase.MinimumProperty, 0.0);
        bar.SetValue(RangeBase.MaximumProperty, 1.0);
        bar.SetBinding(RangeBase.ValueProperty, new Binding(nameof(FileItem.UsedFraction)) { Mode = BindingMode.OneWay });
        bar.SetBinding(Control.ForegroundProperty, new Binding(nameof(FileItem.IsLowSpace)) { Converter = new LowSpaceBrush() });
        bar.SetValue(Control.BackgroundProperty, Theme.Brush("Line"));
        bar.SetValue(FrameworkElement.HeightProperty, 6.0);
        bar.SetValue(UIElement.FocusableProperty, false);
        bar.SetValue(UIElement.IsHitTestVisibleProperty, false); // clicks go to the row
        bar.SetValue(Control.TemplateProperty, UsageBarTemplate ??= MakeUsageBarTemplate());
        return bar;
    }

    private static ControlTemplate? UsageBarTemplate;

    /// <summary>A usage bar for one drive outside the list (the sidebar), the same look as in This PC.</summary>
    internal static ProgressBar NewUsageBar(FileItem drive, double height) => new()
    {
        Minimum = 0, Maximum = 1, Value = drive.UsedFraction, Height = height, Focusable = false, IsHitTestVisible = false,
        Foreground = Theme.Brush(drive.IsLowSpace ? "Danger" : "Accent"), Background = Theme.Brush("Line"),
        Template = UsageBarTemplate ??= MakeUsageBarTemplate(),
    };

    private static ControlTemplate MakeUsageBarTemplate()
    {
        // like the theme's progress bar, but the fill takes the bar's Foreground (blue, or red when space is low)
        var grid = new FrameworkElementFactory(typeof(Grid));
        var track = new FrameworkElementFactory(typeof(Border), "PART_Track");
        track.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        track.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        grid.AppendChild(track);
        var fill = new FrameworkElementFactory(typeof(Border), "PART_Indicator");
        fill.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        fill.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        fill.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
        grid.AppendChild(fill);
        var t = new ControlTemplate(typeof(ProgressBar)) { VisualTree = grid };
        t.Seal();
        return t;
    }

    /// <summary>The item's tags as small rounded chips (#tag), e.g. after the name in Details view.</summary>
    internal static FrameworkElementFactory TagChips()
    {
        var chips = new FrameworkElementFactory(typeof(ItemsControl));
        chips.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(FileItem.Tags)));
        chips.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(FileItem.HasTags)) { Converter = new BoolToVisibility() });
        chips.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        chips.SetValue(UIElement.FocusableProperty, false);
        chips.SetValue(UIElement.IsHitTestVisibleProperty, false); // clicks go to the row
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        chips.SetValue(ItemsControl.ItemsPanelProperty, new ItemsPanelTemplate(panel));
        var chip = new FrameworkElementFactory(typeof(Border));
        chip.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        chip.SetValue(Border.BackgroundProperty, Theme.Brush("Bg3"));
        chip.SetValue(Border.BorderBrushProperty, Theme.Brush("Line"));
        chip.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        chip.SetValue(Border.PaddingProperty, new Thickness(6, 0, 6, 1));
        chip.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 0, 0, 0));
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new Binding { StringFormat = "#{0}" });
        label.SetValue(TextBlock.FontSizeProperty, 11.0);
        label.SetValue(TextBlock.ForegroundProperty, Theme.Brush("Accent"));
        chip.AppendChild(label);
        chips.SetValue(ItemsControl.ItemTemplateProperty, new DataTemplate { VisualTree = chip });
        return chips;
    }

    private readonly GridViewColumn _typeColumn;
    private double _typeWidth;
    private readonly Dictionary<string, double> _typeTextWidths = new();

    /// <summary>The Type column is as wide as its longest value (and its header), not a fixed guess.</summary>
    private void FitTypeColumn(IEnumerable<FileItem> items, bool reset)
    {
        if (reset) _typeWidth = 0;
        var dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var face = new Typeface(List.FontFamily, List.FontStyle, List.FontWeight, List.FontStretch);
        double Measure(string s)
        {
            if (_typeTextWidths.TryGetValue(s, out var w)) return w;
            w = new FormattedText(s, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, List.FontSize, Brushes.Black, dip).WidthIncludingTrailingWhitespace;
            return _typeTextWidths[s] = w;
        }
        double widest = _typeWidth;
        foreach (var name in items.Select(i => i.TypeName).Distinct()) if (!string.IsNullOrEmpty(name)) widest = Math.Max(widest, Measure(name));
        widest = Math.Max(widest, Measure("Type  ▲") + 4); // the header with its sort arrow
        _typeWidth = widest;
        double width = Math.Ceiling(widest + 22); // cell margins + a little air
        if (Math.Abs(_typeColumn.Width - width) > 0.5) _typeColumn.Width = width;
    }

    private void UpdateHeaders()
    {
        var names = new Dictionary<SortColumn, string>
        {
            [SortColumn.Name] = "Name", [SortColumn.Modified] = "Date modified", [SortColumn.Type] = "Type",
            [SortColumn.Size] = "Size", [SortColumn.Folder] = "Folder",
        };
        foreach (var (col, header) in _headers)
            header.Content = names[col] + (col == Sort ? (Ascending ? "  ▲" : "  ▼") : "");
    }

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader h || h.Tag is not SortColumn col) return;
        if (Sort == col) Ascending = !Ascending;
        else { Sort = col; Ascending = col != SortColumn.Modified; }
        UpdateHeaders();
        var selected = SelectedItems.Select(i => i.FullPath).ToList();
        SetItems(SortItems(Items.ToList()), selected);
    }

    /// <summary>Sorts as a click on the column header would (tests).</summary>
    internal void SortBy(SortColumn col, bool ascending)
    {
        Sort = col;
        Ascending = ascending;
        UpdateHeaders();
        SetItems(SortItems(Items.ToList()), SelectedItems.Select(i => i.FullPath).ToList());
    }

    private List<FileItem> SortItems(List<FileItem> items)
    {
        _groupNow = DateTime.Now;
        int Cmp(FileItem a, FileItem b)
        {
            int g = GroupRankCompare(a, b);
            if (g != 0) return g;
            if (a.IsDirectory != b.IsDirectory && Sort != SortColumn.Folder) return a.IsDirectory ? -1 : 1;
            int r = Sort switch
            {
                SortColumn.Modified => a.Modified.CompareTo(b.Modified),
                SortColumn.Type => string.Compare(a.TypeName, b.TypeName, StringComparison.CurrentCultureIgnoreCase),
                SortColumn.Size => a.Size.CompareTo(b.Size),
                SortColumn.Folder => string.Compare(a.Folder, b.Folder, StringComparison.CurrentCultureIgnoreCase),
                _ => 0,
            };
            if (r == 0) r = NameCompare(a.Name, b.Name);
            return Ascending ? r : -r;
        }
        items.Sort(Cmp);
        return items;
    }

    private static bool _logicalAvailable = true;
    private static int NameCompare(string a, string b)
    {
        if (_logicalAvailable)
        {
            try { return Native.StrCmpLogicalW(a, b); }
            catch { _logicalAvailable = false; }
        }
        return string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);
    }

    // =====================================================================================
    // Navigation & loading
    // =====================================================================================

    public void Navigate(string path, bool addHistory = true, string? select = null)
    {
        path = NormalizePath(path);
        if (!Exists(path))
        {
            _host.SetStatus($"Can't find '{path}'.");
            return;
        }
        CancelSearch();
        if (addHistory && !IsSearchResults && !string.Equals(path, CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            _back.Push(CurrentPath);
            _forward.Clear();
        }
        else if (addHistory && IsSearchResults) _forward.Clear();
        bool sameFolder = string.Equals(path, CurrentPath, StringComparison.OrdinalIgnoreCase) && !IsSearchResults;
        IsSearchResults = false;
        SearchQuery = "";
        ShowFolderColumn(false);
        CurrentPath = path;
        UpdateArchiveState();
        if (!sameFolder) _filter = "";
        LocationChanging?.Invoke(this);
        _ = LoadAsync(select != null ? new[] { select } : null, resetScroll: !sameFolder);
        TitleChanged?.Invoke(this, EventArgs.Empty);
        _host.OnNavigated(this);
    }

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Equals("This PC", StringComparison.OrdinalIgnoreCase)) return ThisPC;
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (path.Length == 2 && path[1] == ':') path += "\\";
        try { path = Path.GetFullPath(path); } catch { }
        if (path.Length > 3 && path.EndsWith('\\')) path = path.TrimEnd('\\');
        return path;
    }

    public void GoBack()
    {
        if (_back.Count == 0) return;
        _forward.Push(CurrentPath);
        var target = _back.Pop();
        NavigateNoHistory(target, select: null);
    }

    public void GoForward()
    {
        if (_forward.Count == 0) return;
        _back.Push(CurrentPath);
        NavigateNoHistory(_forward.Pop(), select: null);
    }

    public void GoUp()
    {
        if (IsSearchResults) { EndSearch(); return; }
        if (CurrentPath == ThisPC) return;
        var parent = Path.GetDirectoryName(CurrentPath.TrimEnd('\\'));
        var child = CurrentPath;
        Navigate(string.IsNullOrEmpty(parent) ? ThisPC : parent, select: child);
    }

    private void NavigateNoHistory(string path, string? select)
    {
        if (!Exists(path)) { _host.SetStatus($"'{path}' no longer exists."); return; }
        CancelSearch();
        IsSearchResults = false;
        ShowFolderColumn(false);
        CurrentPath = path;
        UpdateArchiveState();
        _filter = "";
        LocationChanging?.Invoke(this);
        _ = LoadAsync(select != null ? new[] { select } : null, resetScroll: true);
        TitleChanged?.Invoke(this, EventArgs.Empty);
        _host.OnNavigated(this);
    }

    public Task Refresh(IEnumerable<string>? select = null)
    {
        if (IsSearchResults) return Task.CompletedTask;
        bool explicitSelect = select != null;
        var keep = select?.ToList() ?? SelectedItems.Select(i => i.FullPath).ToList();
        return LoadAsync(keep, resetScroll: false, keepScroll: !explicitSelect);
    }

    private async Task LoadAsync(IList<string>? select, bool resetScroll, bool keepScroll = false)
    {
        int v = ++_loadVersion;
        var path = CurrentPath;
        bool showHidden = _host.Settings.ShowHidden;
        var archive = ArchiveFile;
        var inner = ArchiveInner;
        LoadError = null;
        List<FileItem> items;
        while (true)
        {
            try
            {
                items = await Task.Run(() =>
                {
                    var list = path == ThisPC ? Drives() : archive != null ? ArchiveItems(archive, inner) : Enumerate(path, showHidden);
                    // icons, type names and tags here too, not on the UI thread (per-file icons of .exe/.lnk read the file)
                    var tags = Core.Tools.TagStore.Shared;
                    foreach (var it in list) { it.LoadShellInfo(); it.ApplyTags(tags.Get(it.FullPath)); }
                    return list;
                });
            }
            catch (ArchivePasswordException ex)
            {
                if (v != _loadVersion || _closed) return;
                var pw = _host.AskArchivePassword(archive!, ex.WasWrong);
                if (pw != null) { ArchiveBrowser.RememberPassword(archive!, pw); continue; }
                items = new();
                LoadError = "This archive is password protected.";
            }
            catch (UnauthorizedAccessException) { items = new(); LoadError = "Access denied."; }
            catch (Exception ex) { items = new(); LoadError = archive != null ? "This archive can't be opened: " + ex.Message : ex.Message; }
            break;
        }
        if (v != _loadVersion || _closed || IsSearchResults) return;

        var scroller = keepScroll ? VisualTree.FindDescendant<ScrollViewer>(List) : null;
        double offset = scroller?.VerticalOffset ?? 0;
        SetItems(SortItems(items), select, scrollToSelection: !keepScroll);
        if (scroller != null && offset > 0)
            _ = Dispatcher.BeginInvoke(new Action(() => scroller.ScrollToVerticalOffset(offset)), DispatcherPriority.Loaded);
        if (resetScroll && Items.Count > 0 && (select == null || select.Count == 0))
            List.ScrollIntoView(Items[0]);
        StartWatcher(path);
        _host.OnSelectionChanged(this);
    }

    private static List<FileItem> Enumerate(string path, bool showHidden)
    {
        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = showHidden ? 0 : (FileAttributes.Hidden | FileAttributes.System),
            RecurseSubdirectories = false,
        };
        var dir = new DirectoryInfo(path);
        // Force an access check so "Access denied" surfaces instead of an empty list
        using (var e = Directory.EnumerateFileSystemEntries(path).GetEnumerator()) { e.MoveNext(); }
        var list = new List<FileItem>();
        foreach (var info in dir.EnumerateFileSystemInfos("*", opts))
        {
            try { list.Add(FileItem.FromInfo(info)); } catch { }
        }
        return list;
    }

    private static List<FileItem> ArchiveItems(string archive, string inner)
    {
        var entries = ArchiveBrowser.GetEntries(archive);
        return ArchiveBrowser.Children(entries, inner).Select(e => FileItem.FromArchiveEntry(archive, e)).ToList();
    }

    /// <summary>How long This PC waits for a drive's size before showing it without one (a disconnected network drive or a
    /// sleeping USB disk can take many seconds to answer).</summary>
    internal static int DriveTimeoutMs = 3000;

    /// <summary>The drives for This PC, each with its capacity and free space when it answers in time. Every drive is asked
    /// on its own task, so one slow drive delays the page by at most <see cref="DriveTimeoutMs"/> and never hides the others.</summary>
    internal static List<FileItem> Drives()
    {
        var drives = DriveInfo.GetDrives();
        var queries = drives.Select(d => Task.Run(() =>
        {
            try
            {
                if (!d.IsReady) return (Ready: false, Label: "", Total: -1L, Free: -1L);
                return (Ready: true, Label: d.VolumeLabel, Total: d.TotalSize, Free: d.AvailableFreeSpace);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (Ready: false, Label: "", Total: -1L, Free: -1L); }
        })).ToArray();
        try { Task.WaitAll(queries, DriveTimeoutMs); } catch (AggregateException) { }

        var list = new List<FileItem>();
        for (int i = 0; i < drives.Length; i++)
        {
            var d = drives[i];
            string type = d.DriveType switch
            {
                DriveType.Fixed => "Local Disk", DriveType.Removable => "Removable Disk", DriveType.Network => "Network Drive",
                DriveType.CDRom => "CD Drive", _ => "Drive",
            };
            var q = queries[i].IsCompletedSuccessfully ? queries[i].Result : (Ready: false, Label: "", Total: -1L, Free: -1L);
            var letter = d.Name.TrimEnd(Path.DirectorySeparatorChar);
            var item = new FileItem(d.Name, true, -1, DateTime.MinValue, false,
                $"{(string.IsNullOrWhiteSpace(q.Label) ? type : q.Label)} ({letter})")
            {
                IsDrive = true, DriveTotal = q.Ready ? q.Total : -1, DriveFree = q.Ready ? q.Free : -1,
            };
            item.TypeName = !queries[i].IsCompleted ? type + " · not responding" : q.Ready ? type : type + " · not available";
            list.Add(item);
        }
        return list;
    }

    private void SetItems(List<FileItem> items, IEnumerable<string>? select, bool scrollToSelection = true)
    {
        Items = new ObservableCollection<FileItem>(items);
        List.ItemsSource = Items;
        ApplyFilter();
        ApplyGrouping();
        FitTypeColumn(items, reset: true);
        if (select != null)
        {
            var set = new HashSet<string>(select, StringComparer.OrdinalIgnoreCase);
            var toSelect = Items.Where(i => set.Contains(i.FullPath)).ToList();
            List.SelectedItems.Clear();
            foreach (var i in toSelect) List.SelectedItems.Add(i);
            if (toSelect.Count > 0)
            {
                if (scrollToSelection) List.ScrollIntoView(toSelect[0]);
                if (List.IsKeyboardFocusWithin || scrollToSelection) FocusItem(toSelect[0]);
            }
        }
        UpdateEmpty();
        ItemsLoaded?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateEmpty()
    {
        var view = CollectionViewSource.GetDefaultView(Items);
        bool any = view != null && !view.IsEmpty;
        if (LoadError != null) _empty.Text = LoadError;
        else if (IsSearchResults) _empty.Text = any ? "" : IsSearching ? "Searching…" : "No matches.";
        else if (!any) _empty.Text = _filter.Length > 0 ? "No items match your filter." : "This folder is empty.";
        else _empty.Text = "";
        _empty.Visibility = string.IsNullOrEmpty(_empty.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    public void FocusItem(FileItem item)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (List.ItemContainerGenerator.ContainerFromItem(item) is ListViewItem c) c.Focus();
        }), DispatcherPriority.Loaded);
    }

    public void FocusList()
    {
        if (List.SelectedItem is FileItem f) FocusItem(f);
        else if (Items.Count > 0) Dispatcher.BeginInvoke(new Action(() =>
        {
            if (List.ItemContainerGenerator.ContainerFromIndex(0) is ListViewItem c) c.Focus(); else List.Focus();
        }), DispatcherPriority.Loaded);
        else List.Focus();
    }

    public void SelectPaths(IEnumerable<string> paths)
    {
        var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        List.SelectedItems.Clear();
        FileItem? first = null;
        foreach (var i in Items.Where(i => set.Contains(i.FullPath)))
        {
            List.SelectedItems.Add(i);
            first ??= i;
        }
        if (first != null) { List.ScrollIntoView(first); FocusItem(first); }
    }

    private void StartWatcher(string path)
    {
        _watcher?.Dispose();
        _watcher = null;
        if (path == ThisPC || IsSearchResults) return;
        try
        {
            // inside an archive: watch the archive file itself so the listing follows changes to it
            var watchDir = IsArchive ? Path.GetDirectoryName(ArchiveFile!)! : path;
            _watcher = new FileSystemWatcher(watchDir)
            {
                Filter = IsArchive ? Path.GetFileName(ArchiveFile!) : "*",
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            FileSystemEventHandler h = (_, _) => Dispatcher.BeginInvoke(new Action(() => { _refreshTimer.Stop(); _refreshTimer.Start(); }));
            _watcher.Created += h;
            _watcher.Deleted += h;
            _watcher.Changed += h;
            _watcher.Renamed += (s, e) => h(s, e);
            _watcher.Error += (_, _) => Dispatcher.BeginInvoke(new Action(() => { _refreshTimer.Stop(); _refreshTimer.Start(); }));
            _watcher.EnableRaisingEvents = true;
        }
        catch { _watcher = null; }
    }

    public void Close()
    {
        _closed = true;
        _loadVersion++;
        CancelSearch();
        _watcher?.Dispose();
        _watcher = null;
        _refreshTimer.Stop();
    }

    // =====================================================================================
    // Filter & search
    // =====================================================================================

    public void SetFilter(string text)
    {
        _filter = text ?? "";
        ApplyFilter();
        UpdateEmpty();
    }

    private void ApplyFilter()
    {
        var view = CollectionViewSource.GetDefaultView(Items);
        if (view == null) return;
        if (string.IsNullOrEmpty(_filter) || IsSearchResults) view.Filter = null;
        else
        {
            var f = _filter;
            var finds = _host.Settings.SearchFinds; // the filter respects "Search finds: files / folders / both" too
            view.Filter = o => o is FileItem i && i.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
                               && (finds == Core.Tools.ItemTypeFilter.All || (finds == Core.Tools.ItemTypeFilter.FoldersOnly) == i.IsDirectory);
        }
    }

    private void ShowFolderColumn(bool show)
    {
        bool has = _grid.Columns.Contains(_folderColumn);
        if (show && !has) _grid.Columns.Insert(1, _folderColumn);
        if (!show && has) _grid.Columns.Remove(_folderColumn);
    }

    /// <summary>Recursive search under the current folder. Supports * and ? wildcards.</summary>
    public void StartSearch(string query)
    {
        query = query.Trim();
        if (query.Length == 0) return;
        StartSearch(new Core.Tools.SearchCriteria { Name = query, NameMode = Core.Tools.NameMatch.Wildcard, IncludeHidden = _host.Settings.ShowHidden, Type = _host.Settings.SearchFinds }, query);
    }

    /// <summary>Advanced recursive search (name, size, dates, content, tags…) with results shown like a folder.</summary>
    public void StartSearch(Core.Tools.SearchCriteria criteria, string label)
    {
        if (CurrentPath == ThisPC) { _host.SetStatus("Open a folder or drive first, then search inside it."); return; }
        if (IsArchive) { _host.SetStatus("Searching subfolders inside an archive isn't supported. Type to filter this folder instead."); return; }
        var error = criteria.Compile(out var matcher);
        if (error != null) { _host.SetStatus(error); return; }
        BeginResults(label);
        IsSearching = true;
        UpdateEmpty();

        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var root = CurrentPath;
        // plain name searches let the file system do the matching (much faster); everything else is checked per item
        bool simple = criteria.NameMode == Core.Tools.NameMatch.Wildcard && criteria.Name.Length > 0;
        var pattern = !simple ? "*" : criteria.Name.IndexOfAny(new[] { '*', '?' }) >= 0 ? criteria.Name : "*" + criteria.Name + "*";
        var target = Items;
        const int Max = 100_000; // only a guard against runaway searches (a whole drive); lists of this size stay fast

        Task.Run(() =>
        {
            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MatchCasing = criteria.MatchCase ? MatchCasing.CaseSensitive : MatchCasing.CaseInsensitive,
                MatchType = MatchType.Win32,
                AttributesToSkip = criteria.IncludeHidden ? FileAttributes.ReparsePoint : (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint),
            };
            var batch = new List<FileItem>();
            int total = 0, checkedCount = 0;
            var last = DateTime.UtcNow;
            try
            {
                foreach (var info in new DirectoryInfo(root).EnumerateFileSystemInfos(pattern, opts))
                {
                    if (cts.IsCancellationRequested) return;
                    checkedCount++;
                    if (!matcher!.MatchesMetadata(info)) continue;
                    if (matcher.NeedsContent && !matcher.MatchesContent(info.FullName, cts.Token)) continue;
                    try { batch.Add(FileItem.FromInfo(info)); } catch { continue; }
                    total++;
                    if (batch.Count >= 200 || (DateTime.UtcNow - last).TotalMilliseconds > 250)
                    {
                        Flush(batch, total, false);
                        batch = new List<FileItem>();
                        last = DateTime.UtcNow;
                    }
                    if (total >= Max) break;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Dispatcher.BeginInvoke(new Action(() => _host.SetStatus("Search stopped: " + ex.Message))); }
            Flush(batch, total, true);
        }, cts.Token);

        void Flush(List<FileItem> items, int total, bool done)
        {
            // called on the search thread: icons, types and tags are filled in here, the UI thread only adds the rows
            foreach (var it in items) { it.LoadShellInfo(); it.ApplyTags(Core.Tools.TagStore.Shared.Get(it.FullPath)); }
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (cts.IsCancellationRequested || !ReferenceEquals(target, Items)) return;
                foreach (var it in items) Items.Add(it);
                FitTypeColumn(items, reset: false);
                ItemsLoaded?.Invoke(this, EventArgs.Empty);
                if (done)
                {
                    IsSearching = false;
                    _host.SetStatus(total >= Max ? $"Showing the first {Max:N0} matches." : $"Search finished: {total:N0} match{(total == 1 ? "" : "es")}.");
                }
                UpdateEmpty();
                _host.OnSelectionChanged(this);
            }));
        }
    }

    /// <summary>Shows a fixed list of items (e.g. everything with a tag) as results.</summary>
    public void ShowResults(string label, IEnumerable<string> paths)
    {
        if (IsArchive || CurrentPath == ThisPC) NavigateNoHistory(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), null);
        BeginResults(label);
        foreach (var p in paths)
        {
            try
            {
                FileSystemInfo info = Directory.Exists(p) ? new DirectoryInfo(p) : new FileInfo(p);
                if (!info.Exists) continue;
                var it = FileItem.FromInfo(info);
                it.LoadShellInfo();
                it.ApplyTags(Core.Tools.TagStore.Shared.Get(p));
                Items.Add(it);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        FitTypeColumn(Items, reset: true);
        ItemsLoaded?.Invoke(this, EventArgs.Empty);
        UpdateEmpty();
        _host.OnSelectionChanged(this);
    }

    private void BeginResults(string label)
    {
        CancelSearch();
        _loadVersion++;
        if (!IsSearchResults) { _back.Push(CurrentPath); _forward.Clear(); }
        IsSearchResults = true;
        SearchQuery = label;
        _filter = "";
        LocationChanging?.Invoke(this);
        _watcher?.Dispose();
        _watcher = null;
        ShowFolderColumn(true);
        FitTypeColumn(Array.Empty<FileItem>(), reset: true); // grows as results arrive
        LoadError = null;
        Items = new ObservableCollection<FileItem>();
        List.ItemsSource = Items;
        ApplyFilter();
        ApplyGrouping();
        UpdateEmpty();
        TitleChanged?.Invoke(this, EventArgs.Empty);
        _host.OnNavigated(this);
    }
    public void CancelSearch()
    {
        _searchCts?.Cancel();
        _searchCts = null;
        IsSearching = false;
    }

    public void EndSearch()
    {
        if (!IsSearchResults) return;
        CancelSearch();
        IsSearchResults = false;
        SearchQuery = "";
        ShowFolderColumn(false);
        if (_back.Count > 0 && string.Equals(_back.Peek(), CurrentPath, StringComparison.OrdinalIgnoreCase)) _back.Pop();
        LocationChanging?.Invoke(this);
        _ = LoadAsync(null, resetScroll: true);
        TitleChanged?.Invoke(this, EventArgs.Empty);
        _host.OnNavigated(this);
    }

    // =====================================================================================
    // Rename (inline, like Explorer)
    // =====================================================================================

    public void BeginRename()
    {
        if (List.SelectedItem is not FileItem item || item.IsDrive || item.IsVirtual) return;
        if (List.SelectedItems.Count > 1) { List.SelectedItems.Clear(); List.SelectedItem = item; }
        item.EditName = item.Name;
        item.IsEditing = true;
        List.ScrollIntoView(item);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (List.ItemContainerGenerator.ContainerFromItem(item) is not ListViewItem c) { item.IsEditing = false; return; }
            var box = VisualTree.FindDescendant<TextBox>(c, t => Equals(t.Tag, "rename"));
            if (box == null) { item.IsEditing = false; return; }
            box.Focus();
            Keyboard.Focus(box);
            int stem = item.IsDirectory ? item.Name.Length : Path.GetFileNameWithoutExtension(item.Name).Length;
            if (stem == 0) stem = item.Name.Length;
            box.Select(0, stem);
        }), DispatcherPriority.Loaded);
    }

    private void OnRenameKey(object sender, KeyEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not FileItem item) return;
        if (e.Key == Key.Enter) { e.Handled = true; Commit(item); FocusItem(item); }
        else if (e.Key == Key.Escape) { e.Handled = true; item.IsEditing = false; FocusItem(item); }
    }

    private void OnRenameLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is FileItem item) Commit(item);
    }

    private void Commit(FileItem item)
    {
        if (!item.IsEditing) return;
        item.IsEditing = false;
        var newName = item.EditName.Trim();
        if (newName.Length == 0 || newName == item.Name) return;
        _host.CommitRename(this, item, newName);
    }

    // =====================================================================================
    // Mouse: open, empty-area click, drag & drop, context menu
    // =====================================================================================

    /// <summary>
    /// The item under the pointer, or null for empty space. In Details view a row's container is as wide as the list, but only
    /// its columns count as the item (as in Explorer): the space to the right of the last column is background, so the
    /// folder's own menu can be opened next to any row instead of only below the last one.
    /// </summary>
    /// <param name="inList">The pointer position relative to <see cref="List"/> (from the event, not the live cursor).</param>
    private FileItem? ItemAt(DependencyObject? source, Point inList)
    {
        var lvi = VisualTree.FindAncestor<ListViewItem>(source);
        if (lvi == null) return null;
        if (Mode == ViewMode.Details && VisualTree.FindDescendant<GridViewRowPresenter>(lvi) is { } row)
        {
            double columnsRight = _grid.Columns.Sum(c => c.ActualWidth);
            if (List.TranslatePoint(inList, row).X > columnsRight) return null;
        }
        return lvi.DataContext as FileItem;
    }

    /// <summary>Tests: what a right-click at this point (relative to the list) selects; the menu then shows for <see cref="SelectedItems"/>.</summary>
    internal FileItem? RightClickForTest(Point inList)
    {
        var item = ItemAt(List.InputHitTest(inList) as DependencyObject, inList);
        SelectForRightClick(item);
        return item;
    }

    /// <summary>The background menu: the host's menu for an empty selection (the selection itself is left alone).</summary>
    internal ContextMenu BuildBackgroundMenu() => _host.BuildContextMenu(this, new List<FileItem>());

    /// <summary>The menu for the open folder itself (New, Paste, Refresh…), whatever is selected; for the toolbar's … button.</summary>
    public void ShowBackgroundMenu(UIElement placementTarget)
    {
        var menu = BuildBackgroundMenu();
        menu.PlacementTarget = placementTarget;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private static bool IsChrome(DependencyObject? source) =>
        VisualTree.FindAncestor<ScrollBar>(source) != null || VisualTree.FindAncestor<GridViewColumnHeader>(source) != null;

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var item = ItemAt(e.OriginalSource as DependencyObject, e.GetPosition(List));
        if (item != null && !item.IsEditing) _host.OpenItem(this, item);
    }

    private Point _dragStart;
    private FileItem? _dragItem;
    private FileItem? _pendingSingle;
    private bool _dragging;

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var src = e.OriginalSource as DependencyObject;
        if (IsChrome(src) || VisualTree.FindAncestor<TextBox>(src) != null) { _dragItem = null; return; }
        var item = ItemAt(src, e.GetPosition(List));
        _dragStart = e.GetPosition(List);
        _dragItem = item;
        _pendingSingle = null;
        bool modifiers = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
        if (item == null)
        {
            if (!modifiers) List.SelectedItems.Clear();
            List.Focus();
            return;
        }
        if (e.ClickCount == 1 && !modifiers && List.SelectedItems.Contains(item) && List.SelectedItems.Count > 1)
        {
            // keep the multi-selection so it can be dragged; collapse to one item on mouse up
            _pendingSingle = item;
            e.Handled = true;
        }
    }

    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_pendingSingle != null && !_dragging)
        {
            var item = _pendingSingle;
            List.SelectedItems.Clear();
            List.SelectedItem = item;
            FocusItem(item);
        }
        _pendingSingle = null;
        _dragItem = null;
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem == null || _dragging || _dragItem.IsEditing) return;
        var pos = e.GetPosition(List);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var dragItems = SelectedItems.Where(i => !i.IsDrive).ToList();
        if (!dragItems.Contains(_dragItem) && !_dragItem.IsDrive) dragItems = new() { _dragItem };
        if (dragItems.Count == 0) return;
        DataObject data;
        if (dragItems.Any(i => i.IsVirtual))
        {
            _dragItem = null;
            var prepared = _host.PrepareVirtualDrag(this, dragItems);
            if (prepared == null) return;
            data = prepared;
        }
        else
        {
            data = new DataObject();
            var sc = new StringCollection();
            sc.AddRange(dragItems.Select(i => i.FullPath).ToArray());
            data.SetFileDropList(sc);
        }
        _dragging = true;
        var allowed = dragItems.Any(i => i.IsVirtual) ? DragDropEffects.Copy : DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link;
        try { DragDrop.DoDragDrop(List, data, allowed); }
        catch { }
        finally { _dragging = false; _dragItem = null; _pendingSingle = null; }
    }

    private string? DropTarget(DragEventArgs e, out FileItem? overItem)
    {
        overItem = ItemAt(e.OriginalSource as DependencyObject, e.GetPosition(List));
        if (overItem != null && overItem.IsDirectory && !overItem.IsVirtual) return overItem.FullPath;
        if (!CanModify) return null;
        return CurrentPath;
    }

    public static DragDropEffects ChooseEffect(DragEventArgs e, string[] files, string target)
    {
        if ((e.KeyStates & DragDropKeyStates.ControlKey) != 0) return DragDropEffects.Copy;
        if ((e.KeyStates & DragDropKeyStates.ShiftKey) != 0) return DragDropEffects.Move;
        var tr = Path.GetPathRoot(target);
        bool sameDrive = files.All(f => string.Equals(Path.GetPathRoot(f), tr, StringComparison.OrdinalIgnoreCase));
        return sameDrive ? DragDropEffects.Move : DragDropEffects.Copy;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        var target = DropTarget(e, out var over);
        if (files == null || target == null) return;
        if (files.Any(f => string.Equals(f.TrimEnd('\\'), target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) return;
        if (over == null && files.All(f => string.Equals(Path.GetDirectoryName(f), target, StringComparison.OrdinalIgnoreCase))) return;
        e.Effects = ChooseEffect(e, files, target);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var target = DropTarget(e, out var over);
        if (target == null) return;
        if (files.Any(f => string.Equals(f.TrimEnd('\\'), target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) return;
        if (over == null && files.All(f => string.Equals(Path.GetDirectoryName(f), target, StringComparison.OrdinalIgnoreCase))) return;
        _host.DropFiles(this, files, target, ChooseEffect(e, files, target));
    }

    private void OnRightDown(object sender, MouseButtonEventArgs e)
    {
        var src = e.OriginalSource as DependencyObject;
        if (IsChrome(src)) return;
        SelectForRightClick(ItemAt(src, e.GetPosition(List)));
    }

    /// <summary>Right-click on an item keeps a selection that includes it (multi-select menus), otherwise selects just it;
    /// on empty space it clears the selection, so the menu is the folder's own.</summary>
    private void SelectForRightClick(FileItem? item)
    {
        if (item == null) { List.SelectedItems.Clear(); List.Focus(); }
        else if (!List.SelectedItems.Contains(item)) { List.SelectedItems.Clear(); List.SelectedItem = item; }
    }

    private void OnRightUp(object sender, MouseButtonEventArgs e)
    {
        if (IsChrome(e.OriginalSource as DependencyObject)) return;
        e.Handled = true;
        ShowContextMenu(null);
    }

    public void ShowContextMenu(UIElement? placementTarget)
    {
        var menu = _host.BuildContextMenu(this, SelectedItems);
        menu.PlacementTarget = placementTarget ?? List;
        menu.Placement = placementTarget == null ? PlacementMode.MousePoint : PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
