using System.Windows;
using System.Windows.Controls;
using Fyltrix.Core;

namespace Fyltrix.UI;

/// <summary>Manage the commands shown under Actions in the context menu.</summary>
public sealed class CustomActionsWindow : DialogWindow
{
    private readonly AppSettings _settings;
    private readonly List<CustomAction> _items;
    private readonly ListBox _list = new() { Height = 220 };

    public CustomActionsWindow(Window owner, AppSettings settings) : base(owner, "Custom actions", 600)
    {
        _settings = settings;
        _items = settings.CustomActions.Select(Clone).ToList();
        AddContent(Dialogs.Note("Your own commands for the right-click menu, e.g. \"Open in VS Code\" (program code.cmd, arguments \"{path}\"). " +
                                "{path} runs once per item, {paths} once for all of them; {folder} and {name} are also available."));
        var row = new DockPanel();
        var side = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
        void B(string t, Action a) { var b = Dialogs.Button(t); b.Margin = new Thickness(0, 0, 0, 6); b.Click += (_, _) => a(); side.Children.Add(b); }
        B("Add…", () => Edit(null));
        B("Edit…", () => { if (_list.SelectedIndex >= 0) Edit(_list.SelectedIndex); });
        B("Delete", () => { if (_list.SelectedIndex >= 0) { _items.RemoveAt(_list.SelectedIndex); Refresh(); } });
        B("Move up", () => Move(-1));
        B("Move down", () => Move(1));
        DockPanel.SetDock(side, Dock.Right);
        row.Children.Add(side);
        row.Children.Add(_list);
        AddContent(row);
        _list.MouseDoubleClick += (_, _) => { if (_list.SelectedIndex >= 0) Edit(_list.SelectedIndex); };
        AddButton("Save", () => { _settings.CustomActions = _items; _settings.Save(); DialogResult = true; }, primary: true);
        AddButton("Cancel", () => DialogResult = false, cancel: true);
        Refresh();
    }

    private static CustomAction Clone(CustomAction a) => new()
    {
        Name = a.Name, Program = a.Program, Arguments = a.Arguments, Extensions = a.Extensions, ForFiles = a.ForFiles, ForFolders = a.ForFolders,
    };

    private void Refresh()
    {
        int sel = _list.SelectedIndex;
        _list.Items.Clear();
        foreach (var a in _items) _list.Items.Add($"{a.Name}   —   {a.Program} {a.Arguments}");
        _list.SelectedIndex = Math.Min(sel, _items.Count - 1);
    }

    private void Move(int d)
    {
        int i = _list.SelectedIndex, j = i + d;
        if (i < 0 || j < 0 || j >= _items.Count) return;
        (_items[i], _items[j]) = (_items[j], _items[i]);
        Refresh();
        _list.SelectedIndex = j;
    }

    private void Edit(int? index)
    {
        var a = index is { } i ? Clone(_items[i]) : new CustomAction();
        var d = new DialogWindow(this, index == null ? "New action" : "Edit action", 560);
        var name = new TextBox { Text = a.Name };
        var program = new TextBox { Text = a.Program };
        var args = new TextBox { Text = a.Arguments };
        var exts = new TextBox { Text = a.Extensions };
        var files = new CheckBox { Content = "Files", IsChecked = a.ForFiles };
        var folders = new CheckBox { Content = "Folders", IsChecked = a.ForFolders, Margin = new Thickness(14, 0, 0, 0) };
        d.AddContent(Dialogs.Field("Name in the menu", name));
        d.AddContent(Dialogs.Field("Program (full path, or a name on PATH such as notepad.exe)", Dialogs.FilePicker(program, d, "Choose the program", "Programs|*.exe;*.cmd;*.bat|All files|*.*")));
        d.AddContent(Dialogs.Field("Arguments", args));
        d.AddContent(Dialogs.Field("Only for these file types (empty = all)", exts));
        var applies = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        applies.Children.Add(files);
        applies.Children.Add(folders);
        d.AddContent(applies);
        var err = Dialogs.Note("", "Danger");
        d.AddContent(err);
        d.AddButton("OK", () =>
        {
            a.Name = name.Text.Trim();
            a.Program = program.Text.Trim().Trim('"');
            a.Arguments = args.Text;
            a.Extensions = exts.Text.Trim();
            a.ForFiles = files.IsChecked == true;
            a.ForFolders = folders.IsChecked == true;
            if (a.Validate() is { } problem) { err.Text = problem; return; }
            if (!Dialogs.Confirm(d, "Custom action", $"'{a.Name}' will start\n{CustomAction.Resolve(a.Program) ?? a.Program}\nwhen you choose it. Only use programs you trust.\n\nSave?", defaultNo: false)) return;
            d.DialogResult = true;
        }, primary: true);
        d.AddButton("Cancel", () => d.DialogResult = false, cancel: true);
        if (d.ShowDialog() != true) return;
        if (index is { } k) _items[k] = a; else _items.Add(a);
        Refresh();
    }
}
