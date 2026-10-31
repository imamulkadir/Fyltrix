using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fyltrix.Core;
using Fyltrix.Core.Templates;

namespace Fyltrix.UI;

/// <summary>Manage the New menu: show/hide, reorder, recategorise, and add or edit custom templates.</summary>
public sealed class TemplatesWindow : Window
{
    private readonly List<FileTemplate> _items;
    private readonly ListBox _list = new() { Height = 380 };

    public TemplatesWindow(Window owner, IEnumerable<FileTemplate> current)
    {
        _items = current.Select(t => t.Clone()).ToList();
        Owner = owner;
        Title = "New menu templates";
        Width = 720;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Theme.Brush("Bg2");
        Foreground = Theme.Brush("Fg1");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        SourceInitialized += (_, _) => Shell.UseDarkTitleBar(this);

        var root = new DockPanel { Margin = new Thickness(20, 16, 20, 16) };
        var intro = Dialogs.Note("Ticked templates appear in the New menu, in this order. Built-in templates can be hidden, moved or recategorised; " +
                                 "use Duplicate to make an editable copy.");
        DockPanel.SetDock(intro, Dock.Top);
        root.Children.Add(intro);

        var side = new StackPanel { Width = 150, Margin = new Thickness(12, 0, 0, 0) };
        Button Side(string text, Action a)
        {
            var b = Dialogs.Button(text);
            b.Margin = new Thickness(0, 0, 0, 6);
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.Click += (_, _) => a();
            side.Children.Add(b);
            return b;
        }
        Side("Move up", () => Move(-1));
        Side("Move down", () => Move(+1));
        side.Children.Add(new Border { Height = 10 });
        Side("Add…", Add);
        var edit = Side("Edit…", Edit);
        Side("Duplicate", Duplicate);
        var delete = Side("Delete", Delete);
        side.Children.Add(new Border { Height = 10 });
        Side("Restore defaults", RestoreDefaults);
        DockPanel.SetDock(side, Dock.Right);
        root.Children.Add(side);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var save = Dialogs.Button("Save", primary: true);
        save.IsDefault = true;
        save.Click += (_, _) =>
        {
            try { FileTemplates.Save(_items); DialogResult = true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Dialogs.Error(this, "Templates", ex.Message); }
        };
        var cancel = Dialogs.Button("Cancel");
        cancel.IsCancel = true;
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(_list);
        Content = root;

        _list.SelectionChanged += (_, _) =>
        {
            var t = Selected;
            edit.IsEnabled = t is { IsBuiltIn: false };
            delete.IsEnabled = t is { IsBuiltIn: false };
        };
        _list.MouseDoubleClick += (_, _) => { if (Selected is { IsBuiltIn: false }) Edit(); };
        Refresh(0);
    }

    private FileTemplate? Selected => _list.SelectedItem is FrameworkElement { Tag: FileTemplate t } ? t : null;

    private void Refresh(int select)
    {
        _list.Items.Clear();
        foreach (var t in _items)
        {
            var check = new CheckBox { IsChecked = t.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var template = t;
            check.Checked += (_, _) => template.Enabled = true;
            check.Unchecked += (_, _) => template.Enabled = false;
            var row = new DockPanel { Tag = t };
            row.Children.Add(check);
            DockPanel.SetDock(check, Dock.Left);
            var icon = new Image { Source = ShellInfo.Icon("file" + t.Extension, false, assumeType: true), Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            var cat = new TextBlock { Text = t.Category, Foreground = Theme.Brush("Fg3"), Width = 110, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(cat, Dock.Right);
            row.Children.Add(cat);
            var ext = new TextBlock { Text = t.Extension, Foreground = Theme.Brush("Fg2"), Width = 70, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(ext, Dock.Right);
            row.Children.Add(ext);
            row.Children.Add(new TextBlock { Text = t.Name + (t.IsBuiltIn ? "" : "  (custom)"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            _list.Items.Add(row);
        }
        if (_list.Items.Count > 0) _list.SelectedIndex = Math.Clamp(select, 0, _list.Items.Count - 1);
    }

    private void Move(int delta)
    {
        int i = _list.SelectedIndex, j = i + delta;
        if (i < 0 || j < 0 || j >= _items.Count) return;
        (_items[i], _items[j]) = (_items[j], _items[i]);
        Refresh(j);
    }

    private void Add()
    {
        var t = new FileTemplate { Name = "", Extension = ".txt", Category = "Custom", Content = "" };
        if (!new TemplateEditor(this, t, isNew: true).ShowDialog().GetValueOrDefault()) return;
        _items.Add(t);
        Refresh(_items.Count - 1);
    }

    private void Edit()
    {
        if (Selected is not { IsBuiltIn: false } t) return;
        var copy = t.Clone();
        if (!new TemplateEditor(this, copy, isNew: false).ShowDialog().GetValueOrDefault()) return;
        _items[_items.IndexOf(t)] = copy;
        Refresh(_list.SelectedIndex);
    }

    private void Duplicate()
    {
        if (Selected is not { } t) return;
        var copy = t.Clone();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.IsBuiltIn = false;
        copy.Name = t.Name + " (copy)";
        if (t.Resource != null)
        {
            Dialogs.Error(this, "Templates", "Office templates are binary documents and can't be edited as text.");
            return;
        }
        if (!new TemplateEditor(this, copy, isNew: true).ShowDialog().GetValueOrDefault()) return;
        _items.Insert(_list.SelectedIndex + 1, copy);
        Refresh(_list.SelectedIndex + 1);
    }

    private void Delete()
    {
        if (Selected is not { IsBuiltIn: false } t) return;
        if (!Dialogs.Confirm(this, "Delete template", $"Delete the template '{t.Name}'?")) return;
        int i = _items.IndexOf(t);
        _items.Remove(t);
        Refresh(i);
    }

    private void RestoreDefaults()
    {
        if (!Dialogs.Confirm(this, "Restore defaults", "Restore the built-in templates and remove your custom ones?")) return;
        _items.Clear();
        _items.AddRange(FileTemplates.BuiltIns());
        Refresh(0);
    }
}

/// <summary>Edits one custom template.</summary>
public sealed class TemplateEditor : DialogWindow
{
    public TemplateEditor(Window owner, FileTemplate t, bool isNew) : base(owner, isNew ? "New template" : "Edit template", 620)
    {
        var name = new TextBox { Text = t.Name };
        var ext = new TextBox { Text = t.Extension };
        var cat = new TextBox { Text = t.Category };
        var baseName = new TextBox { Text = t.BaseName };
        var content = new TextBox
        {
            Text = t.Content ?? "", AcceptsReturn = true, AcceptsTab = true, Height = 220, TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"), VerticalContentAlignment = VerticalAlignment.Top,
        };
        var lf = new CheckBox { Content = "Unix line endings (LF)", IsChecked = t.UnixNewlines, Margin = new Thickness(0, 8, 0, 0) };
        var bom = new CheckBox { Content = "Write a UTF-8 byte order mark (needed by Excel for CSV, PowerShell 5 for .ps1)", IsChecked = t.Utf8Bom, Margin = new Thickness(0, 8, 0, 0) };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        var nameField = Dialogs.Field("Name in the menu", name);
        var extField = Dialogs.Field("Extension", ext);
        Grid.SetColumn(extField, 2);
        grid.Children.Add(nameField);
        grid.Children.Add(extField);
        AddContent(grid);
        var grid2 = new Grid();
        grid2.ColumnDefinitions.Add(new ColumnDefinition());
        grid2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid2.ColumnDefinitions.Add(new ColumnDefinition());
        var catField = Dialogs.Field("Category", cat);
        var baseField = Dialogs.Field("Default file name (without extension)", baseName);
        Grid.SetColumn(baseField, 2);
        grid2.Children.Add(catField);
        grid2.Children.Add(baseField);
        AddContent(grid2);
        AddContent(Dialogs.Field("Content — {{name}} is replaced by the file name, {{identifier}} by a code-safe version (MyButton)", content));
        AddContent(lf);
        AddContent(bom);
        var err = Dialogs.Note("", "Danger");
        AddContent(err);

        AddButton("OK", () =>
        {
            var e = ext.Text.Trim();
            if (e.Length > 0 && !e.StartsWith('.')) e = "." + e;
            var candidate = t.Clone();
            candidate.Name = name.Text.Trim();
            candidate.Extension = e;
            candidate.Category = string.IsNullOrWhiteSpace(cat.Text) ? "Custom" : cat.Text.Trim();
            candidate.BaseName = baseName.Text.Trim();
            candidate.Content = content.Text;
            var problem = FileTemplates.ValidateCustom(candidate);
            if (problem != null) { err.Text = problem; return; }
            t.Name = candidate.Name;
            t.Extension = candidate.Extension;
            t.Category = candidate.Category;
            t.BaseName = candidate.BaseName;
            t.Content = candidate.Content;
            t.UnixNewlines = lf.IsChecked == true;
            t.Utf8Bom = bom.IsChecked == true;
            t.IsBuiltIn = false;
            DialogResult = true;
        }, primary: true);
        AddButton("Cancel", () => DialogResult = false, cancel: true);
        Loaded += (_, _) => name.Focus();
    }
}
