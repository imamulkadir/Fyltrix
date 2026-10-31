using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Fyltrix.Core;
using Fyltrix.Core.Tools;
using Fyltrix.Preview;

namespace Fyltrix.UI;

/// <summary>Find identical files (verified with SHA-256), review them with a preview, and recycle the extra copies.</summary>
public sealed class DuplicatesWindow : ToolWindow
{
    public sealed class Row : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private bool _checked;
        public required DuplicateGroup Group { get; init; }
        public DuplicateFile? File { get; init; }
        public bool IsHeader => File == null;
        public bool Checked { get => _checked; set { _checked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Checked))); } }
        public string Name => File == null ? $"{Group.Files.Count} identical files · {FileOps.FormatSize(Group.Size)} each" : Path.GetFileName(File.Path);
        public string Folder => File == null ? $"{FileOps.FormatSize(Group.Wasted)} reclaimable · SHA-256 {Group.Sha256.Substring(0, 12)}…" : Path.GetDirectoryName(File.Path) ?? "";
        public string Modified => File?.Modified.ToString("g") ?? "";
        public FontWeight Weight => IsHeader ? FontWeights.SemiBold : FontWeights.Normal;
        public Visibility CheckVisibility => IsHeader ? Visibility.Hidden : Visibility.Visible;
    }

    private readonly TextBox _folders = new();
    private readonly CheckBox _recursive = new() { Content = "Include subfolders", IsChecked = true }, _hidden = new() { Content = "Include hidden files", Margin = new Thickness(14, 0, 0, 0) };
    private readonly TextBox _minSize = new() { Text = "1", Width = 70 }, _exts = new() { Width = 160 };
    private readonly Button _scan, _remove;
    private readonly ProgressBar _bar = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _status = new() { Foreground = Theme.Brush("Fg2"), Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _selection = new() { Foreground = Theme.Brush("Fg2"), VerticalAlignment = VerticalAlignment.Center };
    private readonly ListView _list;
    private readonly PreviewPane _preview = new(showHeader: true, showPopOut: false);
    private List<Row> _rows = new();
    private CancellationTokenSource? _cts;

    /// <summary>Raised after files went to the Recycle Bin (paths, time of deletion) so the host can offer Undo.</summary>
    public event EventHandler<(List<string> paths, DateTime when)>? Recycled;

    public DuplicatesWindow(Window owner, IEnumerable<string> folders) : base(owner, "Find duplicate files", 1250, 780)
    {
        _folders.Text = string.Join("; ", folders);
        var root = new DockPanel { Margin = new Thickness(16) };

        var top = new StackPanel();
        var folderRow = new DockPanel();
        var browse = Dialogs.Button("Add folder…");
        browse.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Add a folder to search", Multiselect = true };
            if (dlg.ShowDialog(this) == true) _folders.Text = string.Join("; ", Folders().Concat(dlg.FolderNames).Distinct(StringComparer.OrdinalIgnoreCase));
        };
        _scan = Dialogs.Button("Find duplicates", primary: true);
        _scan.Click += async (_, _) => await ScanAsync();
        DockPanel.SetDock(_scan, Dock.Right);
        DockPanel.SetDock(browse, Dock.Right);
        folderRow.Children.Add(_scan);
        folderRow.Children.Add(browse);
        folderRow.Children.Add(_folders);
        top.Children.Add(Dialogs.Field("Folders (separate several with ;)", folderRow));
        var opts = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        opts.Children.Add(_recursive);
        opts.Children.Add(_hidden);
        opts.Children.Add(new TextBlock { Text = "Minimum size (KB)", Foreground = Theme.Brush("Fg2"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 6, 0) });
        opts.Children.Add(_minSize);
        opts.Children.Add(new TextBlock { Text = "Only extensions", Foreground = Theme.Brush("Fg2"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 6, 0) });
        opts.Children.Add(_exts);
        _exts.ToolTip = "e.g. jpg png mp4 — leave empty for all files";
        top.Children.Add(opts);
        top.Children.Add(_bar);
        top.Children.Add(_status);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        _remove = Dialogs.Button("Move selected to Recycle Bin", primary: true);
        _remove.IsEnabled = false;
        _remove.Click += (_, _) => RemoveSelected();
        DockPanel.SetDock(_remove, Dock.Right);
        bottom.Children.Add(_remove);
        var helpers = new StackPanel { Orientation = Orientation.Horizontal };
        void Helper(string text, Func<IEnumerable<DuplicateFile>, DuplicateFile> keep)
        {
            var b = Dialogs.Button(text);
            b.Margin = new Thickness(0, 0, 6, 0);
            b.Click += (_, _) => SelectAllBut(keep);
            helpers.Children.Add(b);
        }
        Helper("Keep newest", f => f.OrderByDescending(x => x.Modified).First());
        Helper("Keep oldest", f => f.OrderBy(x => x.Modified).First());
        Helper("Keep shortest path", f => f.OrderBy(x => x.Path.Length).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).First());
        var none = Dialogs.Button("Select none");
        none.Margin = new Thickness(0, 0, 12, 0);
        none.Click += (_, _) => { foreach (var r in _rows) r.Checked = false; UpdateSelection(); };
        helpers.Children.Add(none);
        helpers.Children.Add(_selection);
        bottom.Children.Add(helpers);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        var check = new FrameworkElementFactory(typeof(CheckBox));
        check.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(nameof(Row.Checked)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        check.SetBinding(VisibilityProperty, new Binding(nameof(Row.CheckVisibility)));
        check.AddHandler(System.Windows.Controls.Primitives.ToggleButton.ClickEvent, new RoutedEventHandler((_, _) => UpdateSelection()));
        var nameCol = Column("Name", nameof(Row.Name), 320);
        ((FrameworkElementFactory)nameCol.CellTemplate.VisualTree).SetBinding(TextBlock.FontWeightProperty, new Binding(nameof(Row.Weight)));
        _list = List(new GridViewColumn { Header = "", Width = 34, CellTemplate = new DataTemplate { VisualTree = check } },
            nameCol, Column("Folder", nameof(Row.Folder), 420, brush: "Fg2"), Column("Modified", nameof(Row.Modified), 140, brush: "Fg2"));
        _list.SelectionMode = SelectionMode.Single;
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is Row { File: { } f }) _preview.Show(f.Path);
            else _preview.Clear();
        };
        _list.MouseDoubleClick += (_, _) => { if (_list.SelectedItem is Row { File: { } f }) Shell.Open(f.Path); };

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        body.Children.Add(_list);
        var split = new GridSplitter { HorizontalAlignment = HorizontalAlignment.Stretch, Background = Theme.Brush("Bg1") };
        Grid.SetColumn(split, 1);
        body.Children.Add(split);
        var pv = new Border { Child = _preview, BorderBrush = Theme.Brush("Line"), BorderThickness = new Thickness(1) };
        Grid.SetColumn(pv, 2);
        body.Children.Add(pv);
        root.Children.Add(body);
        Content = root;

        Closed += (_, _) => { _cts?.Cancel(); _preview.DisposeWeb(); };
    }

    internal IReadOnlyList<Row> Rows => _rows;

    private List<string> Folders() => _folders.Text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    internal async Task ScanAsync()
    {
        if (_cts != null) { _cts.Cancel(); return; }
        var folders = Folders();
        var missing = folders.Where(f => !Directory.Exists(f)).ToList();
        if (folders.Count == 0 || missing.Count > 0) { _status.Text = folders.Count == 0 ? "Choose at least one folder." : $"Not found: {string.Join(", ", missing)}"; return; }
        double.TryParse(_minSize.Text, out var kb);
        var options = new DuplicateOptions
        {
            Recursive = _recursive.IsChecked == true, IncludeHidden = _hidden.IsChecked == true, MinSize = Math.Max(1, (long)(kb * 1024)),
            Extensions = _exts.Text.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries),
        };
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _scan.Content = "Cancel";
        _bar.Visibility = Visibility.Visible;
        _bar.IsIndeterminate = true;
        _preview.Clear();
        var progress = new Progress<OperationProgress>(p =>
        {
            _status.Text = p.Status + (p.Detail != null ? " " + p.Detail : "");
            if (p.Fraction < 0) _bar.IsIndeterminate = true; else { _bar.IsIndeterminate = false; _bar.Value = p.Fraction * 100; }
        });
        try
        {
            var result = await Task.Run(() => DuplicateFinder.Find(folders, options, progress, ct), ct);
            Show(result);
        }
        catch (OperationCanceledException) { _status.Text = "Stopped."; }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _scan.Content = "Find duplicates";
            _bar.Visibility = Visibility.Collapsed;
        }
    }

    private void Show(DuplicateResult r)
    {
        _rows = new List<Row>();
        foreach (var g in r.Groups)
        {
            _rows.Add(new Row { Group = g });
            foreach (var f in g.Files) _rows.Add(new Row { Group = g, File = f });
        }
        _list.ItemsSource = _rows;
        long wasted = r.Groups.Sum(g => g.Wasted);
        _status.Text = r.Groups.Count == 0
            ? $"No duplicates among {r.FilesScanned:N0} files."
            : $"{r.Groups.Count:N0} sets of identical files among {r.FilesScanned:N0} scanned · {FileOps.FormatSize(wasted)} could be freed"
              + (r.HardLinksIgnored > 0 ? $" · {r.HardLinksIgnored} hard link(s) ignored (same file)" : "")
              + (r.Errors.Count > 0 ? $" · {r.Errors.Count} file(s) couldn't be read" : "");
        UpdateSelection();
    }

    internal void SelectAllBut(Func<IEnumerable<DuplicateFile>, DuplicateFile> keep)
    {
        foreach (var group in _rows.Where(r => !r.IsHeader).GroupBy(r => r.Group))
        {
            var k = keep(group.Select(r => r.File!));
            foreach (var r in group) r.Checked = r.File != k;
        }
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var sel = _rows.Where(r => r.Checked && r.File != null).ToList();
        _selection.Text = sel.Count == 0 ? "" : $"{sel.Count} selected · {FileOps.FormatSize(sel.Sum(r => r.Group.Size))}";
        _remove.IsEnabled = sel.Count > 0;
    }

    internal void RemoveSelected(bool confirm = true)
    {
        var sel = _rows.Where(r => r.Checked && r.File != null).ToList();
        if (sel.Count == 0) return;
        var wholeGroups = sel.GroupBy(r => r.Group).Where(g => g.Count() >= g.Key.Files.Count).ToList();
        if (wholeGroups.Count > 0)
        {
            Dialogs.Error(this, "Duplicates", $"Every copy is selected in {wholeGroups.Count} set(s). Keep at least one copy of each file.");
            return;
        }
        if (confirm && !Dialogs.Confirm(this, "Move to Recycle Bin",
                $"Move {sel.Count} duplicate file(s) ({FileOps.FormatSize(sel.Sum(r => r.Group.Size))}) to the Recycle Bin?\nOne copy of each file stays where it is.", defaultNo: false))
            return;
        var paths = sel.Select(r => r.File!.Path).ToList();
        _preview.Clear();
        var when = DateTime.UtcNow;
        FileOps.Delete(paths, permanent: false, new System.Windows.Interop.WindowInteropHelper(this).Handle);
        var gone = paths.Where(p => !File.Exists(p)).ToList();
        if (gone.Count > 0) Recycled?.Invoke(this, (gone, when));
        var goneSet = new HashSet<string>(gone, StringComparer.OrdinalIgnoreCase);
        // rebuild the view without removed files; drop sets that no longer have duplicates
        var groups = _rows.Where(r => r.IsHeader).Select(r => r.Group)
            .Select(g => g with { Files = g.Files.Where(f => !goneSet.Contains(f.Path)).ToList() })
            .Where(g => g.Files.Count > 1).ToList();
        var result = new DuplicateResult();
        result.Groups.AddRange(groups);
        Show(result);
        _status.Text = $"Moved {gone.Count} file(s) to the Recycle Bin." + (gone.Count < paths.Count ? $" {paths.Count - gone.Count} couldn't be moved." : "");
    }
}
