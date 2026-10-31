using System.Windows;
using System.Windows.Controls;
using Fyltrix.Core;
using Fyltrix.Core.Tools;

namespace Fyltrix.UI;

/// <summary>Advanced search: builds <see cref="SearchCriteria"/>; filters can be saved and reused.</summary>
public sealed class SearchDialog : DialogWindow
{
    public SearchCriteria? Result { get; private set; }
    private readonly AppSettings _settings;
    private readonly TextBox _name = new(), _exts = new(), _minSize = new(), _maxSize = new(), _content = new(), _tag = new();
    private readonly ComboBox _nameMode, _type, _sizeUnit, _saved;
    private readonly TextBox _modAfter = DateBox(), _modBefore = DateBox(), _createdAfter = DateBox(), _createdBefore = DateBox();

    private static TextBox DateBox() => new() { ToolTip = "A date, e.g. " + new DateTime(2024, 6, 1).ToString("d") + " (leave empty for no limit)" };

    private static string DateText(DateTime? d) => d?.ToString("d") ?? "";
    private readonly CheckBox _case = new() { Content = "Match case" }, _contentRx = new() { Content = "Regular expression", Margin = new Thickness(0, 4, 0, 0) },
        _hidden = new() { Content = "Include hidden and system items", Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _err = Dialogs.Note("", "Danger");

    public SearchDialog(Window owner, AppSettings settings, string folder, SearchCriteria? initial = null) : base(owner, "Advanced search", 640)
    {
        _settings = settings;
        _nameMode = Dialogs.Combo(new[] { ("contains", NameMatch.Contains), ("matches wildcard (* ?)", NameMatch.Wildcard), ("matches regular expression", NameMatch.Regex) }, NameMatch.Contains);
        _type = Dialogs.Combo(new[] { ("Files and folders", ItemTypeFilter.All), ("Files only", ItemTypeFilter.FilesOnly), ("Folders only", ItemTypeFilter.FoldersOnly) }, ItemTypeFilter.All);
        _sizeUnit = Dialogs.Combo(new[] { ("KB", 1024L), ("MB", 1024L * 1024), ("GB", 1024L * 1024 * 1024) }, 1024L * 1024);
        _saved = new ComboBox { MinWidth = 220 };
        RefreshSaved();

        AddContent(Dialogs.Note($"Searches inside {folder} and all its subfolders."));
        var savedRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        savedRow.Children.Add(new TextBlock { Text = "Saved filters", Foreground = Theme.Brush("Fg2"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        savedRow.Children.Add(_saved);
        var del = Dialogs.Button("Delete");
        del.Click += (_, _) => { if (_saved.SelectedItem is ComboBoxItem { Tag: SearchCriteria s }) { _settings.SavedSearches.Remove(s); _settings.Save(); RefreshSaved(); } };
        savedRow.Children.Add(del);
        AddContent(savedRow);
        _saved.SelectionChanged += (_, _) => { if (_saved.SelectedItem is ComboBoxItem { Tag: SearchCriteria s }) Load(s); };

        AddContent(Row(("Name", Stack(_nameMode, _name, _case)), ("Type", _type)));
        AddContent(Row(("Extensions (e.g. jpg png)", _exts), ("Size between", Sizes())));
        AddContent(Row(("Modified after", _modAfter), ("Modified before", _modBefore)));
        AddContent(Row(("Created after", _createdAfter), ("Created before", _createdBefore)));
        AddContent(Row(("Containing text (text files up to 32 MB)", Stack(_content, _contentRx)), ($"With {ProductBranding.Name} tag", _tag)));
        AddContent(_hidden);
        AddContent(_err);

        AddButton("Search", () => { if (Build() is { } c) { Result = c; DialogResult = true; } }, primary: true);
        AddButton("Save filter…", SaveFilter);
        AddButton("Cancel", () => DialogResult = false, cancel: true);
        if (initial != null) Load(initial);
        Loaded += (_, _) => _name.Focus();
    }

    private static StackPanel Stack(params UIElement[] items)
    {
        var sp = new StackPanel();
        foreach (var i in items) { if (i is FrameworkElement fe) fe.Margin = new Thickness(0, 0, 0, 4); sp.Children.Add(i); }
        return sp;
    }

    private StackPanel Sizes()
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        _minSize.Width = 70;
        _maxSize.Width = 70;
        sp.Children.Add(_minSize);
        sp.Children.Add(new TextBlock { Text = " – ", VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.Brush("Fg2") });
        sp.Children.Add(_maxSize);
        _sizeUnit.Margin = new Thickness(6, 0, 0, 0);
        _sizeUnit.Width = 64;
        sp.Children.Add(_sizeUnit);
        return sp;
    }

    private static Grid Row(params (string label, UIElement control)[] cells)
    {
        var g = new Grid();
        for (int i = 0; i < cells.Length; i++)
        {
            if (i > 0) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var f = Dialogs.Field(cells[i].label, cells[i].control);
            Grid.SetColumn(f, i * 2);
            g.Children.Add(f);
        }
        return g;
    }

    private void RefreshSaved()
    {
        _saved.Items.Clear();
        _saved.Items.Add(new ComboBoxItem { Content = _settings.SavedSearches.Count == 0 ? "(none yet)" : "Choose…", IsEnabled = false });
        foreach (var s in _settings.SavedSearches) _saved.Items.Add(new ComboBoxItem { Content = s.Title, Tag = s });
        _saved.SelectedIndex = 0;
    }

    private void Load(SearchCriteria c)
    {
        _name.Text = c.Name;
        foreach (ComboBoxItem i in _nameMode.Items) if (i.Tag is NameMatch m && m == c.NameMode) _nameMode.SelectedItem = i;
        foreach (ComboBoxItem i in _type.Items) if (i.Tag is ItemTypeFilter t && t == c.Type) _type.SelectedItem = i;
        _case.IsChecked = c.MatchCase;
        _exts.Text = c.Extensions;
        long unit = 1024L * 1024;
        _minSize.Text = c.MinSize is { } mn ? (mn / (double)unit).ToString("0.###") : "";
        _maxSize.Text = c.MaxSize is { } mx ? (mx / (double)unit).ToString("0.###") : "";
        foreach (ComboBoxItem i in _sizeUnit.Items) if (i.Tag is long u && u == unit) _sizeUnit.SelectedItem = i;
        _modAfter.Text = DateText(c.ModifiedAfter);
        _modBefore.Text = DateText(c.ModifiedBefore?.AddDays(-1));
        _createdAfter.Text = DateText(c.CreatedAfter);
        _createdBefore.Text = DateText(c.CreatedBefore?.AddDays(-1));
        _content.Text = c.Content;
        _contentRx.IsChecked = c.ContentRegex;
        _tag.Text = c.Tag;
        _hidden.IsChecked = c.IncludeHidden;
    }

    internal SearchCriteria? Build()
    {
        long unit = Dialogs.Selected<long>(_sizeUnit);
        long? Size(TextBox b) => double.TryParse(b.Text.Trim(), out var v) && v >= 0 ? (long)(v * unit) : null;
        bool badDate = false;
        DateTime? Date(TextBox b)
        {
            var s = b.Text.Trim();
            if (s.Length == 0) return null;
            if (DateTime.TryParse(s, out var d)) return d.Date;
            badDate = true;
            return null;
        }
        var c = new SearchCriteria
        {
            Name = _name.Text.Trim(), NameMode = Dialogs.Selected<NameMatch>(_nameMode), MatchCase = _case.IsChecked == true, Type = Dialogs.Selected<ItemTypeFilter>(_type),
            Extensions = _exts.Text.Trim(), MinSize = Size(_minSize), MaxSize = Size(_maxSize),
            ModifiedAfter = Date(_modAfter), ModifiedBefore = Date(_modBefore)?.AddDays(1),
            CreatedAfter = Date(_createdAfter), CreatedBefore = Date(_createdBefore)?.AddDays(1),
            Content = _content.Text, ContentRegex = _contentRx.IsChecked == true, Tag = _tag.Text.Trim(), IncludeHidden = _hidden.IsChecked == true,
        };
        if (badDate) { _err.Text = "One of the dates isn't a valid date."; return null; }
        if ((_minSize.Text.Trim().Length > 0 && c.MinSize == null) || (_maxSize.Text.Trim().Length > 0 && c.MaxSize == null)) { _err.Text = "Sizes must be numbers."; return null; }
        var problem = c.Compile(out _);
        if (problem != null) { _err.Text = problem; return null; }
        c.Title = c.Describe();
        return c;
    }

    private void SaveFilter()
    {
        if (Build() is not { } c) return;
        var name = Dialogs.Prompt(this, "Save filter", "Name for this filter", c.Describe(), n => string.IsNullOrWhiteSpace(n) ? "Enter a name." : null);
        if (name == null) return;
        c.Title = name.Trim();
        _settings.SavedSearches.RemoveAll(s => s.Title.Equals(c.Title, StringComparison.OrdinalIgnoreCase));
        _settings.SavedSearches.Add(c);
        _settings.Save();
        RefreshSaved();
    }
}
