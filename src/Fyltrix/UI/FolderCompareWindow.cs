using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fyltrix.Core;
using Fyltrix.Core.Tools;

namespace Fyltrix.UI;

/// <summary>Compare two folders and synchronise them one way or both ways, with every action reviewable first.</summary>
public sealed class FolderCompareWindow : ToolWindow
{
    public sealed class Row
    {
        public required DiffItem Item { get; init; }
        public string Path => Item.Path + (Item.IsDirectory ? "\\" : "");
        public string Left => Describe(Item.Left, Item.IsDirectory);
        public string Right => Describe(Item.Right, Item.IsDirectory);
        public string Status => Item.Kind switch
        {
            DiffKind.Same => "identical",
            DiffKind.OnlyLeft => "only on the left",
            DiffKind.OnlyRight => "only on the right",
            DiffKind.LeftNewer => "left is newer",
            DiffKind.RightNewer => "right is newer",
            DiffKind.Different => "different content",
            DiffKind.Conflict => "changed on both sides",
            DiffKind.DeletedRight => "deleted on the right",
            DiffKind.DeletedLeft => "deleted on the left",
            _ => Item.Kind.ToString(),
        };
        public string Action => Item.Action switch
        {
            SyncAction.CopyToRight => "copy →",
            SyncAction.CopyToLeft => "← copy",
            SyncAction.DeleteRight => "delete on right",
            SyncAction.DeleteLeft => "delete on left",
            SyncAction.Skip => "skip (decide)",
            _ => "",
        };
        public Brush ActionBrush => Theme.Brush(Item.Action switch
        {
            SyncAction.DeleteLeft or SyncAction.DeleteRight => "Danger",
            SyncAction.Skip => "Star",
            SyncAction.None => "Fg3",
            _ => "Accent",
        });

        private static string Describe(FileStamp? s, bool dir) =>
            s == null ? "—" : dir ? "folder" : $"{FileOps.FormatSize(s.Size)} · {s.ModifiedUtc.ToLocalTime():g}";
    }

    private readonly TextBox _left = new(), _right = new();
    private readonly ComboBox _mode;
    private readonly CheckBox _subfolders = new() { Content = "Subfolders", IsChecked = true },
        _hidden = new() { Content = "Hidden files", Margin = new Thickness(14, 0, 0, 0) },
        _verify = new() { Content = "Compare contents (slower)", Margin = new Thickness(14, 0, 0, 0) },
        _deletions = new() { Content = "Apply deletions", Margin = new Thickness(14, 0, 0, 0), ToolTip = "One-way: remove items the source doesn't have (mirror). Two-way: repeat deletions made since the last sync. Removed items go to the Recycle Bin." },
        _overwriteNewer = new() { Content = "Replace newer files on the target", Margin = new Thickness(14, 0, 0, 0) },
        _showSame = new() { Content = "Show identical", Margin = new Thickness(14, 0, 0, 0) };
    private readonly ListView _list;
    private readonly TextBlock _summary = new() { Foreground = Theme.Brush("Fg2"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly Button _sync;
    private List<DiffItem> _items = new();
    private string _cmpLeft = "", _cmpRight = "";

    public event EventHandler<IReadOnlyList<string>>? FoldersChanged;

    public FolderCompareWindow(Window owner, string left, string right) : base(owner, "Compare folders", 1300, 820)
    {
        _left.Text = left;
        _right.Text = right;
        _mode = Dialogs.Combo(new[] { ("Left → Right (update the right side)", SyncMode.LeftToRight), ("Right → Left (update the left side)", SyncMode.RightToLeft), ("Both ways", SyncMode.TwoWay) }, SyncMode.LeftToRight);

        var top = new StackPanel();
        var paths = new Grid();
        paths.ColumnDefinitions.Add(new ColumnDefinition());
        paths.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        paths.ColumnDefinitions.Add(new ColumnDefinition());
        var lf = Dialogs.Field("Left folder", Dialogs.FolderPicker(_left, this, "Choose the left folder"));
        var rf = Dialogs.Field("Right folder", Dialogs.FolderPicker(_right, this, "Choose the right folder"));
        Grid.SetColumn(rf, 2);
        paths.Children.Add(lf);
        paths.Children.Add(rf);
        top.Children.Add(paths);
        var opts = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        _mode.Width = 300;
        _mode.Margin = new Thickness(0, 0, 14, 6);
        opts.Children.Add(_mode);
        foreach (var cb in new[] { _subfolders, _hidden, _verify, _deletions, _overwriteNewer, _showSame }) { cb.VerticalAlignment = VerticalAlignment.Center; opts.Children.Add(cb); }
        var compare = Dialogs.Button("Compare", primary: true);
        compare.Margin = new Thickness(14, 0, 0, 0);
        compare.Click += async (_, _) => await CompareAsync();
        opts.Children.Add(compare);
        top.Children.Add(opts);

        _list = List(Column("Item", nameof(Row.Path), 380), Column("Left", nameof(Row.Left), 200, brush: "Fg2"), Column("Status", nameof(Row.Status), 170),
            Column("Right", nameof(Row.Right), 200, brush: "Fg2"), Column("Action", nameof(Row.Action), 140, brushProperty: nameof(Row.ActionBrush)));
        _list.ContextMenu = new ContextMenu();
        _list.ContextMenuOpening += (_, e) =>
        {
            var rows = _list.SelectedItems.Cast<Row>().ToList();
            _list.ContextMenu.Items.Clear();
            if (rows.Count == 0) { e.Handled = true; return; }
            void Act(string text, SyncAction a) => _list.ContextMenu.Items.Add(Theme.MenuItem(text, null, () => SetAction(rows, a)));
            Act("Copy left → right", SyncAction.CopyToRight);
            Act("Copy right → left", SyncAction.CopyToLeft);
            Act("Skip", SyncAction.Skip);
            _list.ContextMenu.Items.Add(new Separator());
            Act("Delete on the right (Recycle Bin)", SyncAction.DeleteRight);
            Act("Delete on the left (Recycle Bin)", SyncAction.DeleteLeft);
        };

        _sync = Dialogs.Button("Synchronise…", primary: true);
        _sync.IsEnabled = false;
        _sync.Click += async (_, _) => await SyncAsync(confirm: true);
        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(_sync, Dock.Right);
        bottom.Children.Add(_sync);
        bottom.Children.Add(_summary);

        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(bottom);
        root.Children.Add(_list);
        Content = root;

        _showSame.Checked += (_, _) => ShowRows();
        _showSame.Unchecked += (_, _) => ShowRows();
        foreach (var cb in new[] { _deletions, _overwriteNewer }) { cb.Checked += (_, _) => Replan(); cb.Unchecked += (_, _) => Replan(); }
        _mode.SelectionChanged += (_, _) => Replan();
        Loaded += async (_, _) => { if (Directory.Exists(left) && Directory.Exists(right)) await CompareAsync(); };
    }

    internal IReadOnlyList<DiffItem> Items => _items;
    internal void SetMode(SyncMode m) { foreach (ComboBoxItem i in _mode.Items) if (i.Tag is SyncMode x && x == m) _mode.SelectedItem = i; }

    private CompareOptions Options() => new()
    {
        Recursive = _subfolders.IsChecked == true, IncludeHidden = _hidden.IsChecked == true, VerifyContent = _verify.IsChecked == true,
    };

    internal async Task CompareAsync()
    {
        var l = _left.Text.Trim().Trim('"');
        var r = _right.Text.Trim().Trim('"');
        if (!Directory.Exists(l) || !Directory.Exists(r)) { _summary.Text = "Choose two existing folders."; return; }
        if (FileOpsSameOrNested(l, r)) { _summary.Text = "The folders are the same or one is inside the other; choose two separate folders."; return; }
        var opts = Options();
        var baseline = FolderSync.Baseline.Load(l, r);
        try
        {
            _items = await JobWindow.RunAsync(this, "Comparing folders", (p, ct) => FolderSync.Compare(l, r, opts, baseline, p, ct));
        }
        catch (OperationCanceledException) { _summary.Text = "Stopped."; return; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _summary.Text = ex.Message; return; }
        _cmpLeft = l;
        _cmpRight = r;
        Replan();
    }

    private static bool FileOpsSameOrNested(string a, string b)
    {
        var x = Path.GetFullPath(a).TrimEnd('\\') + "\\";
        var y = Path.GetFullPath(b).TrimEnd('\\') + "\\";
        return x.StartsWith(y, StringComparison.OrdinalIgnoreCase) || y.StartsWith(x, StringComparison.OrdinalIgnoreCase);
    }

    private void Replan()
    {
        FolderSync.Plan(_items, Dialogs.Selected<SyncMode>(_mode), new SyncOptions { PropagateDeletions = _deletions.IsChecked == true, OverwriteNewer = _overwriteNewer.IsChecked == true });
        ShowRows();
    }

    private void SetAction(List<Row> rows, SyncAction a)
    {
        foreach (var r in rows)
        {
            bool possible = a switch
            {
                SyncAction.CopyToRight or SyncAction.DeleteLeft => r.Item.Left != null,
                SyncAction.CopyToLeft or SyncAction.DeleteRight => r.Item.Right != null,
                _ => true,
            };
            if (a is SyncAction.DeleteLeft or SyncAction.DeleteRight) possible &= a == SyncAction.DeleteLeft ? r.Item.Left != null : r.Item.Right != null;
            if (possible) r.Item.Action = a;
        }
        ShowRows();
    }

    private void ShowRows()
    {
        var shown = _items.Where(i => _showSame.IsChecked == true || i.Kind != DiffKind.Same).Select(i => new Row { Item = i }).ToList();
        _list.ItemsSource = shown;
        int toR = _items.Count(i => i.Action == SyncAction.CopyToRight), toL = _items.Count(i => i.Action == SyncAction.CopyToLeft);
        int del = _items.Count(i => i.Action is SyncAction.DeleteLeft or SyncAction.DeleteRight), skip = _items.Count(i => i.Action == SyncAction.Skip);
        int same = _items.Count(i => i.Kind == DiffKind.Same);
        _summary.Text = _items.Count == 0 ? "" :
            $"{same:N0} identical · plan: {toR} copy →, {toL} ← copy, {del} delete (to Recycle Bin)" + (skip > 0 ? $" · {skip} need a decision (right-click to choose)" : "");
        _sync.IsEnabled = toR + toL + del > 0;
    }

    internal async Task<SyncResult?> SyncAsync(bool confirm, Func<IList<string>, bool>? recycle = null)
    {
        int toR = _items.Count(i => i.Action == SyncAction.CopyToRight), toL = _items.Count(i => i.Action == SyncAction.CopyToLeft);
        int del = _items.Count(i => i.Action is SyncAction.DeleteLeft or SyncAction.DeleteRight);
        if (confirm && !Dialogs.Confirm(this, "Synchronise",
                $"Copy {toR} item(s) to the right and {toL} to the left" + (del > 0 ? $", and move {del} item(s) to the Recycle Bin" : "") +
                ".\nFiles that get replaced go to the Recycle Bin first.\n\nContinue?", defaultNo: false))
            return null;
        var l = _cmpLeft;
        var r = _cmpRight;
        var items = _items;
        recycle ??= paths => FileOps.Delete(paths, permanent: false, IntPtr.Zero, silent: true);
        var opts = Options();
        bool twoWay = Dialogs.Selected<SyncMode>(_mode) == SyncMode.TwoWay;
        SyncResult result;
        try
        {
            result = await JobWindow.RunAsync(this, "Synchronising", (p, ct) =>
            {
                var res = FolderSync.Execute(l, r, items, recycle, p, ct);
                if (!res.Cancelled && twoWay) FolderSync.Baseline.Save(l, r, opts);
                return res;
            });
        }
        catch (OperationCanceledException) { _summary.Text = "Stopped."; return null; }
        FoldersChanged?.Invoke(this, new[] { l, r });
        if (result.Errors.Count > 0) Dialogs.Error(this, "Synchronise", string.Join("\n", result.Errors.Take(15)));
        await CompareAsync();
        _summary.Text = $"Copied {result.CopiedToRight} → and {result.CopiedToLeft} ←, recycled {result.Recycled}" +
                        (result.Errors.Count > 0 ? $", {result.Errors.Count} problem(s)" : "") + (result.Cancelled ? " (cancelled)" : "") + ". " + _summary.Text;
        return result;
    }
}
