using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Fyltrix.Core;
using Fyltrix.Core.Tools;

namespace Fyltrix.UI;

/// <summary>What takes up the space: sizes per folder with drill-down, a treemap and the largest files.</summary>
public sealed class FolderSizeWindow : ToolWindow
{
    public sealed class Row
    {
        public required SizeNode Node { get; init; }
        public string Name => Node.Name + (Node.IsLink ? "  (link, not followed)" : Node.Inaccessible ? "  (access denied)" : "");
        public string Size => Node.IsLink ? "" : FileOps.FormatSize(Node.Size);
        public double Percent => Node.ShareOfParent * 100;
        public string PercentText => Node.IsLink ? "" : $"{Percent:0.0}%";
        public string Files => Node.IsDirectory ? Node.Files.ToString("N0") : "";
        public string Folders => Node.IsDirectory ? Node.Folders.ToString("N0") : "";
        public ImageSource? Icon => ShellInfo.Icon(Node.Path, Node.IsDirectory, assumeType: true);
    }

    public sealed class FileRow
    {
        public required string Path { get; init; }
        public long Bytes { get; init; }
        public string Name => System.IO.Path.GetFileName(Path);
        public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
        public string Size => FileOps.FormatSize(Bytes);
    }

    private readonly TextBox _path = new();
    private readonly Button _analyze, _up;
    private readonly TextBlock _crumb = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _status = new() { Foreground = Theme.Brush("Fg2"), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ProgressBar _bar = new() { IsIndeterminate = true, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
    private readonly ListView _list, _largest;
    private readonly Canvas _map = new() { ClipToBounds = true, Background = Theme.Brush("Bg0") };
    private FolderReport? _report;
    private SizeNode? _current;
    private CancellationTokenSource? _cts;

    /// <summary>Asks the main window to open a folder (or show a file in its folder).</summary>
    public event EventHandler<string>? OpenRequested;

    public FolderSizeWindow(Window owner, string folder) : base(owner, "Folder sizes", 1250, 800)
    {
        _path.Text = folder;
        var root = new DockPanel { Margin = new Thickness(16) };
        var top = new StackPanel();
        var row = new DockPanel();
        _analyze = Dialogs.Button("Analyze", primary: true);
        _analyze.Click += async (_, _) => await AnalyzeAsync();
        var browse = Dialogs.Button("Browse…");
        browse.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder to analyze" };
            if (dlg.ShowDialog(this) == true) { _path.Text = dlg.FolderName; _ = AnalyzeAsync(); }
        };
        DockPanel.SetDock(_analyze, Dock.Right);
        DockPanel.SetDock(browse, Dock.Right);
        row.Children.Add(_analyze);
        row.Children.Add(browse);
        row.Children.Add(_path);
        top.Children.Add(row);
        top.Children.Add(_bar);
        var nav = new DockPanel { Margin = new Thickness(0, 10, 0, 6) };
        _up = Theme.SmallButton("", "Up", "Back to the parent folder");
        _up.Click += (_, _) => { if (_current?.Parent != null) ShowNode(_current.Parent); };
        _up.IsEnabled = false;
        DockPanel.SetDock(_up, Dock.Left);
        nav.Children.Add(_up);
        _crumb.VerticalAlignment = VerticalAlignment.Center;
        _crumb.Margin = new Thickness(8, 0, 0, 0);
        nav.Children.Add(_crumb);
        top.Children.Add(nav);
        top.Children.Add(_status);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        // percentage bar column
        var barCell = new FrameworkElementFactory(typeof(Grid));
        var track = new FrameworkElementFactory(typeof(Border));
        track.SetValue(Border.BackgroundProperty, Theme.Brush("Bg3"));
        track.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
        track.SetValue(HeightProperty, 8.0);
        barCell.AppendChild(track);
        var fill = new FrameworkElementFactory(typeof(Border));
        fill.SetValue(Border.BackgroundProperty, Theme.Brush("Accent"));
        fill.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
        fill.SetValue(HeightProperty, 8.0);
        fill.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Left);
        fill.SetBinding(WidthProperty, new Binding(nameof(Row.Percent)) { Converter = new PercentToWidth(110) });
        barCell.AppendChild(fill);
        var nameCell = new FrameworkElementFactory(typeof(StackPanel));
        nameCell.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var icon = new FrameworkElementFactory(typeof(Image));
        icon.SetBinding(Image.SourceProperty, new Binding(nameof(Row.Icon)));
        icon.SetValue(WidthProperty, 16.0);
        icon.SetValue(HeightProperty, 16.0);
        icon.SetValue(MarginProperty, new Thickness(0, 0, 6, 0));
        nameCell.AppendChild(icon);
        var nameText = new FrameworkElementFactory(typeof(TextBlock));
        nameText.SetBinding(TextBlock.TextProperty, new Binding(nameof(Row.Name)));
        nameCell.AppendChild(nameText);
        _list = List(new GridViewColumn { Header = "Name", Width = 300, CellTemplate = new DataTemplate { VisualTree = nameCell } },
            Column("Size", nameof(Row.Size), 90, right: true), new GridViewColumn { Header = "", Width = 120, CellTemplate = new DataTemplate { VisualTree = barCell } },
            Column("%", nameof(Row.PercentText), 60, right: true, brush: "Fg2"), Column("Files", nameof(Row.Files), 80, right: true, brush: "Fg2"),
            Column("Folders", nameof(Row.Folders), 70, right: true, brush: "Fg2"));
        _list.SelectionMode = SelectionMode.Single;
        _list.MouseDoubleClick += (_, _) => { if (_list.SelectedItem is Row r) Drill(r.Node); };
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _list.SelectedItem is Row r) Drill(r.Node);
            if (e.Key == Key.Back && _current?.Parent != null) ShowNode(_current.Parent);
        };
        _list.ContextMenu = new ContextMenu();
        _list.ContextMenuOpening += (_, e) =>
        {
            _list.ContextMenu.Items.Clear();
            if (_list.SelectedItem is not Row r) { e.Handled = true; return; }
            _list.ContextMenu.Items.Add(Theme.MenuItem(r.Node.IsDirectory ? $"Open in {ProductBranding.Name}" : $"Show in {ProductBranding.Name}", "", () => OpenRequested?.Invoke(this, r.Node.Path)));
            _list.ContextMenu.Items.Add(Theme.MenuItem("Show in File Explorer", "", () => Shell.ShowInExplorer(r.Node.Path)));
        };

        _largest = List(Column("File", nameof(FileRow.Name), 300), Column("Size", nameof(FileRow.Size), 90, right: true), Column("Folder", nameof(FileRow.Folder), 500, brush: "Fg2"));
        _largest.MouseDoubleClick += (_, _) => { if (_largest.SelectedItem is FileRow f) OpenRequested?.Invoke(this, f.Path); };

        var tabs = new TabControl { Background = Theme.Brush("Bg1"), BorderThickness = new Thickness(0) };
        tabs.Items.Add(new TabItem { Header = "Folders", Content = _list });
        tabs.Items.Add(new TabItem { Header = "Largest files", Content = _largest });

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(11, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(9, GridUnitType.Star) });
        body.Children.Add(tabs);
        var mapBorder = new Border { Child = _map, BorderBrush = Theme.Brush("Line"), BorderThickness = new Thickness(1) };
        Grid.SetColumn(mapBorder, 2);
        body.Children.Add(mapBorder);
        root.Children.Add(body);
        Content = root;

        _map.SizeChanged += (_, _) => DrawMap();
        Closed += (_, _) => _cts?.Cancel();
        Loaded += async (_, _) => { if (Directory.Exists(_path.Text)) await AnalyzeAsync(); };
    }

    internal SizeNode? Current => _current;

    internal async Task AnalyzeAsync()
    {
        if (_cts != null) { _cts.Cancel(); return; }
        var folder = _path.Text.Trim().Trim('"');
        if (!Directory.Exists(folder)) { _status.Text = "That folder doesn't exist."; return; }
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _analyze.Content = "Cancel";
        _bar.Visibility = Visibility.Visible;
        var progress = new Progress<OperationProgress>(p => _status.Text = p.Status + " " + p.Detail);
        try
        {
            _report = await Task.Run(() => FolderAnalyzer.Analyze(folder, 200, progress, ct), ct);
            _largest.ItemsSource = _report.LargestFiles.Select(f => new FileRow { Path = f.path, Bytes = f.size }).ToList();
            ShowNode(_report.Root);
        }
        catch (OperationCanceledException) { _status.Text = "Stopped."; }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _analyze.Content = "Analyze";
            _bar.Visibility = Visibility.Collapsed;
        }
    }

    private void Drill(SizeNode node)
    {
        if (node.IsDirectory && !node.IsLink && node.Children.Count > 0) ShowNode(node);
        else if (!node.IsDirectory) OpenRequested?.Invoke(this, node.Path);
    }

    internal void ShowNode(SizeNode node)
    {
        _current = node;
        _crumb.Text = node.Path;
        _up.IsEnabled = node.Parent != null;
        _list.ItemsSource = node.Children.Select(c => new Row { Node = c }).ToList();
        var r = _report!;
        _status.Text = $"{FileOps.FormatSize(node.Size)} in {node.Files:N0} files and {node.Folders:N0} folders"
                       + (r.InaccessibleFolders > 0 ? $" · {r.InaccessibleFolders} folder(s) couldn't be read" : "")
                       + (r.LinksSkipped > 0 ? $" · {r.LinksSkipped} link(s) not followed" : "");
        DrawMap();
    }

    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x3B, 0x6E, 0xA8), Color.FromRgb(0x2F, 0x8F, 0x83), Color.FromRgb(0x7A, 0x5C, 0xB0), Color.FromRgb(0xA8, 0x6B, 0x3B),
        Color.FromRgb(0x4F, 0x8A, 0x3C), Color.FromRgb(0xA8, 0x4B, 0x6E), Color.FromRgb(0x5C, 0x6F, 0x8A), Color.FromRgb(0x8A, 0x7A, 0x3C),
    };

    private void DrawMap()
    {
        _map.Children.Clear();
        if (_current == null || _map.ActualWidth < 10 || _map.ActualHeight < 10) return;
        var nodes = _current.Children.Where(c => c.Size > 0 && !c.IsLink).Take(300).ToList();
        var rects = FolderAnalyzer.Treemap(nodes.Select(n => n.Size).ToList(), _map.ActualWidth, _map.ActualHeight);
        for (int i = 0; i < nodes.Count; i++)
        {
            var (x, y, w, h) = rects[i];
            if (w < 1 || h < 1) continue;
            var n = nodes[i];
            var color = n.IsDirectory ? Palette[i % Palette.Length] : Color.FromRgb(0x55, 0x55, 0x60);
            var cell = new Border
            {
                Width = Math.Max(0, w - 1), Height = Math.Max(0, h - 1), Background = new SolidColorBrush(color), BorderBrush = Theme.Brush("Bg0"),
                BorderThickness = new Thickness(0, 0, 1, 1), ToolTip = $"{n.Name}\n{FileOps.FormatSize(n.Size)} ({n.ShareOfParent:P1})", Cursor = n.IsDirectory ? Cursors.Hand : null,
            };
            if (w > 60 && h > 22)
                cell.Child = new TextBlock
                {
                    Text = $"{n.Name}\n{FileOps.FormatSize(n.Size)}", Foreground = Brushes.White, FontSize = 11.5, Margin = new Thickness(4, 2, 2, 2),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
            cell.MouseLeftButtonUp += (_, _) => Drill(n);
            Canvas.SetLeft(cell, x);
            Canvas.SetTop(cell, y);
            _map.Children.Add(cell);
        }
    }

    private sealed class PercentToWidth : IValueConverter
    {
        private readonly double _max;
        public PercentToWidth(double max) => _max = max;
        public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c) => value is double d ? Math.Clamp(d, 0, 100) / 100 * _max : 0.0;
        public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }
}
