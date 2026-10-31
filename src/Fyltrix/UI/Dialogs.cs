using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Fyltrix.UI;

/// <summary>Base for Fyltrix dialogs: dark chrome, owner-centred, Esc closes, consistent spacing.</summary>
public class DialogWindow : Window
{
    public StackPanel Body { get; } = new() { Margin = new Thickness(20, 16, 20, 8) };
    public StackPanel Buttons { get; } = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 8, 20, 16) };

    public DialogWindow(Window? owner, string title, double width = 520)
    {
        if (owner != null) Owner = owner;
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Background = Theme.Brush("Bg2");
        Foreground = Theme.Brush("Fg1");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        SourceInitialized += (_, _) => Shell.UseDarkTitleBar(this);
        var root = new DockPanel();
        DockPanel.SetDock(Buttons, Dock.Bottom);
        root.Children.Add(Buttons);
        root.Children.Add(Body);
        Content = root;
    }

    public void AddContent(UIElement e) => Body.Children.Add(e);

    public Button AddButton(string text, Action onClick, bool primary = false, bool cancel = false)
    {
        var b = Dialogs.Button(text, primary);
        b.IsDefault = primary;
        b.IsCancel = cancel;
        b.Click += (_, _) => onClick();
        Buttons.Children.Add(b);
        return b;
    }
}

/// <summary>Base for resizable tool windows (batch rename, duplicates, folder sizes…).</summary>
public class ToolWindow : Window
{
    public ToolWindow(Window? owner, string title, double width, double height)
    {
        if (owner != null) Owner = owner;
        Title = title;
        Icon = AppIcon.ForWindow();
        Width = width;
        Height = height;
        MinWidth = 520;
        MinHeight = 360;
        WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        Background = Theme.Brush("Bg1");
        Foreground = Theme.Brush("Fg1");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        SourceInitialized += (_, _) => { Shell.UseDarkTitleBar(this); AppIcon.ApplyToHwnd(this); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && e.OriginalSource is not TextBox) { Close(); e.Handled = true; } };
    }

    /// <summary>A GridView column bound to a property, optionally right-aligned or coloured from another property.</summary>
    public static GridViewColumn Column(string header, string property, double width, bool right = false, string? brushProperty = null, string brush = "Fg1")
    {
        var tb = new FrameworkElementFactory(typeof(TextBlock));
        tb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(property));
        tb.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        if (brushProperty != null) tb.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(brushProperty));
        else tb.SetValue(TextBlock.ForegroundProperty, Theme.Brush(brush));
        if (right) tb.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        return new GridViewColumn { Header = header, Width = width, CellTemplate = new DataTemplate { VisualTree = tb } };
    }

    public static ListView List(params GridViewColumn[] columns)
    {
        var grid = new GridView();
        foreach (var c in columns) grid.Columns.Add(c);
        var lv = new ListView { View = grid, SelectionMode = SelectionMode.Extended };
        VirtualizingPanel.SetIsVirtualizing(lv, true);
        VirtualizingPanel.SetVirtualizationMode(lv, VirtualizationMode.Recycling);
        return lv;
    }
}

public static class Dialogs
{
    public static Button Button(string text, bool primary = false) => new()
    {
        Content = text, MinWidth = 88, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(14, 6, 14, 6),
        Background = primary ? Theme.Brush("Sel") : Theme.Brush("Bg3"),
    };

    public static TextBlock Heading(string text) => new()
    {
        Text = text, FontWeight = FontWeights.SemiBold, FontSize = 14, Foreground = Theme.Brush("Fg1"),
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
    };

    public static TextBlock Note(string text, string brush = "Fg2") => new()
    {
        Text = text, Foreground = Theme.Brush(brush), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8),
    };

    public static TextBlock Label(string text) => new()
    {
        Text = text, Foreground = Theme.Brush("Fg2"), Margin = new Thickness(0, 6, 0, 4), TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>A label above a control.</summary>
    public static StackPanel Field(string label, UIElement control)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        sp.Children.Add(Label(label));
        sp.Children.Add(control);
        return sp;
    }

    public static ComboBox Combo<T>(IEnumerable<(string text, T value)> items, T selected)
    {
        var cb = new ComboBox();
        foreach (var (text, value) in items)
        {
            var item = new ComboBoxItem { Content = text, Tag = value };
            cb.Items.Add(item);
            if (EqualityComparer<T>.Default.Equals(value, selected)) cb.SelectedItem = item;
        }
        if (cb.SelectedIndex < 0 && cb.Items.Count > 0) cb.SelectedIndex = 0;
        return cb;
    }

    public static T Selected<T>(ComboBox cb) => cb.SelectedItem is ComboBoxItem { Tag: T v } ? v : default!;

    /// <summary>Text box with a Browse… button that picks a folder.</summary>
    public static DockPanel FolderPicker(TextBox box, Window owner, string title)
    {
        var browse = Button("Browse…");
        browse.Margin = new Thickness(6, 0, 0, 0);
        browse.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = title };
            if (Directory.Exists(box.Text)) dlg.InitialDirectory = box.Text;
            if (dlg.ShowDialog(owner) == true) box.Text = dlg.FolderName;
        };
        var dp = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        dp.Children.Add(browse);
        dp.Children.Add(box);
        return dp;
    }

    /// <summary>Text box with a Browse… button that picks a file.</summary>
    public static DockPanel FilePicker(TextBox box, Window owner, string title, string filter = "All files|*.*", bool save = false)
    {
        var browse = Button("Browse…");
        browse.Margin = new Thickness(6, 0, 0, 0);
        browse.Click += (_, _) =>
        {
            Microsoft.Win32.FileDialog dlg = save ? new Microsoft.Win32.SaveFileDialog() : new Microsoft.Win32.OpenFileDialog();
            dlg.Title = title;
            dlg.Filter = filter;
            try
            {
                var dir = Path.GetDirectoryName(box.Text);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) { dlg.InitialDirectory = dir; dlg.FileName = Path.GetFileName(box.Text); }
            }
            catch (ArgumentException) { }
            if (dlg.ShowDialog(owner) == true) box.Text = dlg.FileName;
        };
        var dp = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        dp.Children.Add(browse);
        dp.Children.Add(box);
        return dp;
    }

    public static void Error(Window? owner, string title, string message) =>
        MessageBox.Show(owner ?? Application.Current.MainWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public static bool Confirm(Window? owner, string title, string message, bool defaultNo = true) =>
        MessageBox.Show(owner ?? Application.Current.MainWindow, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question,
            defaultNo ? MessageBoxResult.No : MessageBoxResult.Yes) == MessageBoxResult.Yes;

    /// <summary>Simple one-line text prompt. Returns null when cancelled.</summary>
    public static string? Prompt(Window owner, string title, string label, string initial = "", Func<string, string?>? validate = null)
    {
        var d = new DialogWindow(owner, title, 440);
        var box = new TextBox { Text = initial };
        var err = Note("", "Danger");
        err.Visibility = Visibility.Collapsed;
        string? result = null;
        d.AddContent(Field(label, box));
        d.AddContent(err);
        d.AddButton("OK", () =>
        {
            var v = validate?.Invoke(box.Text);
            if (v != null) { err.Text = v; err.Visibility = Visibility.Visible; return; }
            result = box.Text;
            d.DialogResult = true;
        }, primary: true);
        d.AddButton("Cancel", () => d.DialogResult = false, cancel: true);
        d.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return d.ShowDialog() == true ? result : null;
    }

    /// <summary>Password prompt for encrypted archives/PDFs. Returns null when cancelled.</summary>
    public static string? AskPassword(Window owner, string itemName, bool wasWrong)
    {
        var d = new DialogWindow(owner, "Password required", 420);
        d.AddContent(Heading($"'{itemName}' is password protected."));
        if (wasWrong) d.AddContent(Note("That password didn't work. Try again.", "Danger"));
        var box = new PasswordBox();
        d.AddContent(Field("Password", box));
        string? result = null;
        d.AddButton("OK", () => { result = box.Password; d.DialogResult = true; }, primary: true);
        d.AddButton("Cancel", () => d.DialogResult = false, cancel: true);
        d.Loaded += (_, _) => box.Focus();
        return d.ShowDialog() == true ? result : null;
    }
}
