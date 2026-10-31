using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Fyltrix.Core;

namespace Fyltrix.UI;

/// <summary>
/// The search box: one rounded field holding the icon, the text, a clear/stop button and the scope switch.
/// Typing always filters the current folder instantly. With the scope "This folder and subfolders" (default),
/// Enter searches every subfolder (Ctrl+Enter: in a new tab); with "This folder only", Enter just keeps the filter.
/// </summary>
public sealed partial class MainWindow
{
    private Border _searchBox = null!;
    private Button _searchClear = null!;
    private Button _searchScope = null!;

    private bool SearchesSubfolders => Settings.SearchScope != SearchScope.CurrentFolder;

    private FrameworkElement BuildSearchBox()
    {
        // the text box itself has no border or background: the surrounding Border is the visible field,
        // so the icon and the text sit together with even spacing at every DPI
        var plain = new ControlTemplate(typeof(TextBox));
        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        host.SetValue(ScrollViewer.FocusableProperty, false);
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        plain.VisualTree = host;
        _search.Template = plain;
        _search.Background = System.Windows.Media.Brushes.Transparent;
        _search.BorderThickness = new Thickness(0);
        _search.Padding = new Thickness(0);
        _search.VerticalContentAlignment = VerticalAlignment.Center;
        _search.Margin = new Thickness(6, 0, 2, 0);
        AutomationPropertiesName(_search, "Search");
        _search.TextChanged += (_, _) => OnSearchTextChanged();
        _search.PreviewKeyDown += OnSearchKey;

        var icon = Theme.Icon("", 13, "Fg3");
        icon.Margin = new Thickness(10, 0, 0, 0);
        icon.IsHitTestVisible = false;

        _searchHint.Foreground = Theme.Brush("Fg3");
        _searchHint.IsHitTestVisible = false;
        _searchHint.VerticalAlignment = VerticalAlignment.Center;
        _searchHint.Margin = new Thickness(8, 0, 2, 0); // 2 px more than the text box: matches where typed text starts
        _searchHint.TextTrimming = TextTrimming.CharacterEllipsis;

        _searchClear = SearchBoxButton("", "Clear search (Esc)");
        _searchClear.Visibility = Visibility.Collapsed;
        _searchClear.Click += (_, _) =>
        {
            if (View is { IsSearching: true }) { View.CancelSearch(); SetStatus("Search stopped."); UpdateSearchChrome(); return; }
            ClearSearch();
        };

        _searchScope = SearchBoxButton("", "Where to search");
        _searchScope.Padding = new Thickness(8, 0, 11, 0);
        if (_searchScope.Content is StackPanel sp)
            sp.Children.Add(new TextBlock { Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.Brush("Fg2"), FontSize = 12 });
        _searchScope.Click += (_, _) => ShowSearchScopeMenu();

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(icon);
        Grid.SetColumn(_search, 1);
        Grid.SetColumn(_searchHint, 1);
        Grid.SetColumn(_searchClear, 2);
        Grid.SetColumn(_searchScope, 3);
        grid.Children.Add(_search);
        grid.Children.Add(_searchHint);
        grid.Children.Add(_searchClear);
        grid.Children.Add(new Border { Width = 1, Margin = new Thickness(0, 6, 0, 6), Background = Theme.Brush("Line"), HorizontalAlignment = HorizontalAlignment.Left });
        Grid.SetColumn(grid.Children[^1], 3);
        grid.Children.Add(_searchScope);

        _searchBox = new Border
        {
            Width = 340, MinHeight = 30, Margin = new Thickness(6, 6, 8, 6), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1),
            Background = Theme.Brush("Bg3"), BorderBrush = Theme.Brush("Line"), Child = grid, Cursor = Cursors.IBeam,
        };
        // clicking anywhere in the field (padding, icon) puts the caret in the text box
        _searchBox.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not TextBox) { _search.Focus(); e.Handled = true; } };
        _searchBox.IsKeyboardFocusWithinChanged += (_, _) => _searchBox.BorderBrush = Theme.Brush(_searchBox.IsKeyboardFocusWithin ? "Accent" : "Line");
        UpdateSearchChrome();
        return _searchBox;
    }

    private static void AutomationPropertiesName(DependencyObject d, string name) => System.Windows.Automation.AutomationProperties.SetName(d, name);

    private static Button SearchBoxButton(string glyph, string tip)
    {
        var b = Theme.SmallButton(glyph, "", tip);
        b.Background = System.Windows.Media.Brushes.Transparent;
        b.BorderThickness = new Thickness(0);
        b.Margin = new Thickness(0);
        b.Padding = new Thickness(7, 0, 7, 0);
        b.Cursor = Cursors.Arrow;
        b.VerticalAlignment = VerticalAlignment.Stretch;
        if (b.Content is StackPanel p && p.Children[0] is TextBlock t) { t.FontSize = 11; t.Foreground = Theme.Brush("Fg2"); }
        return b;
    }

    private void ShowSearchScopeMenu()
    {
        var menu = new ContextMenu { PlacementTarget = _searchScope, Placement = PlacementMode.Bottom };
        MenuItem Option(string text, SearchScope scope, string tip)
        {
            var mi = Theme.MenuItem(text, Settings.SearchScope == scope ? "" : null, () =>
            {
                Settings.SearchScope = scope;
                UpdateSearchChrome();
                SetStatus(scope == SearchScope.CurrentFolder ? "Search: this folder only (filters as you type)." : "Search: this folder and all subfolders (press Enter).");
                _search.Focus();
            });
            mi.ToolTip = tip;
            return mi;
        }
        menu.Items.Add(Option("This folder and subfolders", SearchScope.Subfolders, "Typing filters this folder at once; Enter searches every subfolder too. Ctrl+Enter opens the results in a new tab."));
        menu.Items.Add(Option("This folder only", SearchScope.CurrentFolder, "Typing filters the items in this folder. Nothing below it is searched."));
        menu.Items.Add(new Separator());
        menu.Items.Add(Theme.MenuItem("Advanced search…", "", AdvancedSearch, "Ctrl+Shift+F", CurrentFolder != null));
        menu.IsOpen = true;
    }

    /// <summary>Hint text, scope label and the clear/stop button follow the scope, the text and whether a search is running.</summary>
    private void UpdateSearchChrome()
    {
        if (_searchBox == null) return;
        bool sub = SearchesSubfolders;
        if (_searchScope.Content is StackPanel sp && sp.Children.Count > 1 && sp.Children[1] is TextBlock label)
            label.Text = sub ? "Subfolders" : "This folder";
        _searchScope.ToolTip = sub ? "Searching this folder and its subfolders (Enter). Click to change." : "Searching this folder only. Click to change.";
        _search.ToolTip = sub
            ? "Type to filter this folder; press Enter to search all subfolders too (wildcards * ? allowed). Ctrl+Enter: results in a new tab. Esc clears."
            : "Type to filter this folder. Esc clears.";

        bool searching = View is { IsSearching: true };
        _searchClear.Visibility = _search.Text.Length > 0 || View is { IsSearchResults: true } ? Visibility.Visible : Visibility.Collapsed;
        if (_searchClear.Content is StackPanel cp && cp.Children[0] is TextBlock glyph) glyph.Text = searching ? "" : "";
        _searchClear.ToolTip = searching ? "Stop searching" : "Clear search (Esc)";

        if (_search.Text.Length > 0) { _searchHint.Text = ""; return; }
        var name = View == null || View.IsThisPC ? null : View.IsSearchResults ? Path.GetFileName(View.CurrentPath.TrimEnd('\\')) : View.Title;
        if (string.IsNullOrEmpty(name)) name = View?.CurrentPath;
        _searchHint.Text = name == null ? "Search" : sub ? $"Search {name} and subfolders" : $"Filter {name}";
    }

    // ---- for tests: what typing + Enter / clicking does ----
    internal FrameworkElement SearchBoxElement => _searchBox;
    internal string SearchHintText => _searchHint.Text;
    internal string SearchScopeLabel => (_searchScope.Content as StackPanel)?.Children.OfType<TextBlock>().LastOrDefault()?.Text ?? "";
    internal void TypeSearch(string text) => _search.Text = text;
    internal void PressEnterInSearch(bool ctrl = false) => RunSearch(ctrl);
    internal void ClickSearchClearOrStop() => _searchClear.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    internal bool SearchStopVisible => _searchClear.IsVisible && Equals(_searchClear.ToolTip, "Stop searching");

    private void RunSearch(bool newTab)
    {
        if (View == null) return;
        var query = _search.Text.Trim();
        if (query.Length == 0) return;
        _filterTimer.Stop();
        if (!SearchesSubfolders) { View.SetFilter(query); return; }
        if (View.IsThisPC || View.IsArchive) { View.StartSearch(query); return; } // explains why it can't
        var root = View.CurrentPath;
        if (newTab)
        {
            _suppressSearchEvents = true;
            View.SetFilter("");
            _search.Text = "";
            _suppressSearchEvents = false;
            AddTab(root, activate: true);
        }
        View!.SetFilter("");
        View.StartSearch(query);
        _suppressSearchEvents = true;
        _search.Text = query;
        _suppressSearchEvents = false;
        UpdateSearchChrome();
    }

    private void ClearSearch()
    {
        if (View == null) return;
        _suppressSearchEvents = true;
        _search.Text = "";
        _suppressSearchEvents = false;
        View.SetFilter("");
        View.EndSearch();
        View.FocusList();
        UpdateSearchChrome();
    }
}
