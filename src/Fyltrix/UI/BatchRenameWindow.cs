using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Fyltrix.Core;
using Fyltrix.Core.Tools;

namespace Fyltrix.UI;

/// <summary>Rename many items with a live before/after preview. Conflicts are shown before anything changes.</summary>
public sealed class BatchRenameWindow : ToolWindow
{
    public sealed class Row
    {
        public required RenamePlanItem Item { get; init; }
        public string Old => Item.OldName;
        public string New => Item.NewName;
        public string Status => Item.Status switch
        {
            RenameStatus.Ok => "✓",
            RenameStatus.Unchanged => "unchanged",
            _ => Item.Message ?? Item.Status.ToString(),
        };
        public Brush StatusBrush => Theme.Brush(Item.Status switch { RenameStatus.Ok => "Accent", RenameStatus.Unchanged => "Fg3", _ => "Danger" });
    }

    private readonly IReadOnlyList<string> _sources;
    private readonly ListView _preview;
    private readonly TextBlock _summary = new() { Foreground = Theme.Brush("Fg2"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly Button _rename;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private List<RenamePlanItem> _plan = new();

    private readonly TextBox _template = new() { Text = "{name}" }, _find = new(), _replace = new(), _remove = new(), _prefix = new(), _suffix = new(),
        _ext = new(), _start = new() { Text = "1", Width = 60 }, _step = new() { Text = "1", Width = 60 };
    private readonly CheckBox _regex = new() { Content = "Regular expression" }, _matchCase = new() { Content = "Match case", Margin = new Thickness(12, 0, 0, 0) },
        _lowerExt = new() { Content = "Lower-case extension", Margin = new Thickness(0, 6, 0, 0) },
        _folders = new() { Content = "Rename folders too", IsChecked = true, Margin = new Thickness(0, 6, 0, 0) };
    private readonly ComboBox _case, _spaces, _extMode;

    /// <summary>Raised after a rename with the items that changed (old → new).</summary>
    public event EventHandler<RenameResult>? Renamed;

    public BatchRenameWindow(Window owner, IReadOnlyList<string> sources) : base(owner, $"Rename {sources.Count} items", 1150, 720)
    {
        _sources = sources;
        _case = Dialogs.Combo(new[] { ("Keep", CaseChange.None), ("lower case", CaseChange.Lower), ("UPPER CASE", CaseChange.Upper), ("Title Case", CaseChange.Title), ("Sentence case", CaseChange.Sentence) }, CaseChange.None);
        _spaces = Dialogs.Combo(new[] { ("Keep spaces", (string?)null), ("Replace with _", "_"), ("Replace with -", "-"), ("Replace with .", "."), ("Remove spaces", "") }, null);
        _extMode = Dialogs.Combo(new[] { ("Keep extension", 0), ("Change to:", 1), ("Remove extension", 2) }, 0);

        var root = new Grid { Margin = new Thickness(16) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var form = new StackPanel();
        form.Children.Add(Dialogs.Field("New name — {name} original, {n} or {n:000} counter, {date:yyyy-MM-dd} modified, {created:…}, {folder}, {ext}", _template));
        var findRow = new Grid();
        findRow.ColumnDefinitions.Add(new ColumnDefinition());
        findRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        findRow.ColumnDefinitions.Add(new ColumnDefinition());
        var findField = Dialogs.Field("Find", _find);
        var replField = Dialogs.Field("Replace with", _replace);
        Grid.SetColumn(replField, 2);
        findRow.Children.Add(findField);
        findRow.Children.Add(replField);
        form.Children.Add(findRow);
        var opts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 6) };
        opts.Children.Add(_regex);
        opts.Children.Add(_matchCase);
        form.Children.Add(opts);
        form.Children.Add(Dialogs.Field("Remove these characters", _remove));
        var twoCombos = new Grid();
        twoCombos.ColumnDefinitions.Add(new ColumnDefinition());
        twoCombos.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        twoCombos.ColumnDefinitions.Add(new ColumnDefinition());
        var spacesField = Dialogs.Field("Spaces", _spaces);
        var caseField = Dialogs.Field("Case", _case);
        Grid.SetColumn(caseField, 2);
        twoCombos.Children.Add(spacesField);
        twoCombos.Children.Add(caseField);
        form.Children.Add(twoCombos);
        var affix = new Grid();
        affix.ColumnDefinitions.Add(new ColumnDefinition());
        affix.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        affix.ColumnDefinitions.Add(new ColumnDefinition());
        var pre = Dialogs.Field("Prefix", _prefix);
        var suf = Dialogs.Field("Suffix", _suffix);
        Grid.SetColumn(suf, 2);
        affix.Children.Add(pre);
        affix.Children.Add(suf);
        form.Children.Add(affix);
        var extRow = new Grid();
        extRow.ColumnDefinitions.Add(new ColumnDefinition());
        extRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        extRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        var extField = Dialogs.Field("Extension", _extMode);
        var extBox = Dialogs.Field(" ", _ext);
        Grid.SetColumn(extBox, 2);
        extRow.Children.Add(extField);
        extRow.Children.Add(extBox);
        form.Children.Add(extRow);
        form.Children.Add(_lowerExt);
        var counter = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        counter.Children.Add(Dialogs.Field("Counter starts at", _start));
        counter.Children.Add(new Border { Width = 12 });
        counter.Children.Add(Dialogs.Field("Step", _step));
        form.Children.Add(counter);
        form.Children.Add(_folders);
        var formScroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(formScroll);

        _preview = List(Column("Current name", nameof(Row.Old), 260), Column("New name", nameof(Row.New), 280, brush: "Fg1"),
            Column("", nameof(Row.Status), 220, brushProperty: nameof(Row.StatusBrush)));
        Grid.SetColumn(_preview, 2);
        root.Children.Add(_preview);

        var bottom = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        _rename = Dialogs.Button("Rename", primary: true);
        _rename.IsDefault = true;
        _rename.Click += (_, _) => DoRename();
        var cancel = Dialogs.Button("Close");
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(_rename);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        bottom.Children.Add(_summary);
        Grid.SetRow(bottom, 1);
        Grid.SetColumnSpan(bottom, 3);
        root.Children.Add(bottom);
        Content = root;

        foreach (var tb in new[] { _template, _find, _replace, _remove, _prefix, _suffix, _ext, _start, _step }) tb.TextChanged += (_, _) => Schedule();
        foreach (var cb in new[] { _regex, _matchCase, _lowerExt, _folders }) { cb.Checked += (_, _) => Schedule(); cb.Unchecked += (_, _) => Schedule(); }
        foreach (var co in new[] { _case, _spaces, _extMode }) co.SelectionChanged += (_, _) => Schedule();
        _debounce.Tick += (_, _) => { _debounce.Stop(); Recompute(); };
        Loaded += (_, _) => { Recompute(); _template.Focus(); _template.SelectAll(); };
    }

    private void Schedule() { _debounce.Stop(); _debounce.Start(); }

    internal RenameRules Rules()
    {
        int.TryParse(_start.Text, out var start);
        if (!int.TryParse(_step.Text, out var step)) step = 1;
        int extMode = Dialogs.Selected<int>(_extMode);
        _ext.IsEnabled = extMode == 1;
        return new RenameRules
        {
            Template = _template.Text, Find = _find.Text, Replace = _replace.Text, UseRegex = _regex.IsChecked == true, MatchCase = _matchCase.IsChecked == true,
            RemoveCharacters = _remove.Text, SpaceReplacement = Dialogs.Selected<string?>(_spaces), Case = Dialogs.Selected<CaseChange>(_case),
            Prefix = _prefix.Text, Suffix = _suffix.Text, NewExtension = extMode switch { 1 => _ext.Text, 2 => "", _ => null },
            LowercaseExtension = _lowerExt.IsChecked == true, CounterStart = start, CounterStep = step, ApplyToFolders = _folders.IsChecked == true,
        };
    }

    internal void SetTemplate(string template) { _template.Text = template; Recompute(); }

    internal void Recompute()
    {
        _plan = BatchRename.Plan(_sources, Rules());
        _preview.ItemsSource = _plan.Select(p => new Row { Item = p }).ToList();
        int ok = _plan.Count(p => p.Status == RenameStatus.Ok);
        int bad = _plan.Count(p => p.Status is RenameStatus.Conflict or RenameStatus.Invalid);
        _summary.Text = $"{ok} of {_plan.Count} will be renamed" + (bad > 0 ? $" · {bad} can't be (see the reasons on the right) and will be left alone" : "") + ".";
        _summary.Foreground = Theme.Brush(bad > 0 ? "Danger" : "Fg2");
        _rename.IsEnabled = ok > 0;
        _rename.Content = ok > 0 ? $"Rename {ok}" : "Rename";
    }

    internal void DoRename()
    {
        Recompute(); // the folder may have changed since the preview was drawn
        var result = BatchRename.Execute(_plan);
        if (result.Errors.Count > 0)
            Dialogs.Error(this, "Rename", (result.RolledBack ? "Nothing was renamed; every item was put back.\n\n" : "") + string.Join("\n", result.Errors.Take(10)));
        if (result.Renamed.Count > 0) Renamed?.Invoke(this, result);
        if (result.Errors.Count == 0) Close();
        else Recompute();
    }
}
