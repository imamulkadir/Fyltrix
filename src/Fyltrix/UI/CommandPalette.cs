using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Fyltrix.Core;

namespace Fyltrix.UI;

public sealed record PaletteEntry(string Title, string Category, Action Run, string? Gesture = null, string? Detail = null);

/// <summary>Keyboard-first launcher (Ctrl+Shift+P): every command, template and place, filtered as you type.</summary>
public sealed class CommandPalette : Window
{
    private readonly TextBox _box = new() { FontSize = 15, Padding = new Thickness(10, 6, 10, 6), BorderThickness = new Thickness(0) };
    private readonly ListBox _list = new() { BorderThickness = new Thickness(0), MaxHeight = 420, Background = Theme.Brush("Bg2") };
    private readonly Func<string, IEnumerable<PaletteEntry>> _source;
    private bool _closing;

    /// <param name="source">All entries for the given text (the text lets the host add "Go to <path>").</param>
    public CommandPalette(Window owner, Func<string, IEnumerable<PaletteEntry>> source)
    {
        _source = source;
        Owner = owner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.Height;
        Width = 640;
        Background = Theme.Brush("Line");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        WindowPlacement.OverOwner(this, owner, top: 70); // over the app, on whichever monitor it is on

        var stack = new StackPanel { Margin = new Thickness(1), Background = Theme.Brush("Bg2") };
        _box.Background = Theme.Brush("Bg3");
        var hint = new TextBlock { Text = "Type a command, a template, a folder name or a path", Foreground = Theme.Brush("Fg3"), Margin = new Thickness(14, 0, 0, 0), IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
        var boxGrid = new Grid();
        boxGrid.Children.Add(_box);
        boxGrid.Children.Add(hint);
        _box.TextChanged += (_, _) => { hint.Visibility = _box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Refresh(); };
        stack.Children.Add(boxGrid);
        stack.Children.Add(_list);
        Content = stack;

        _box.PreviewKeyDown += OnKey;
        _list.MouseUp += (_, _) => RunSelected();
        // Closing while Windows is still moving activation (e.g. to the app the user clicked) leaves no active window, so
        // Windows activated some other app and Fyltrix dropped behind it for a moment. Close once the switch is done instead.
        Deactivated += (_, _) => Dispatcher.BeginInvoke(SafeClose, System.Windows.Threading.DispatcherPriority.Input);
        Loaded += (_, _) => { Refresh(); _box.Focus(); };
    }

    internal IReadOnlyList<PaletteEntry> Visible => _list.Items.Cast<FrameworkElement>().Select(f => (PaletteEntry)f.Tag).ToList();
    internal void SetText(string t) => _box.Text = t;

    /// <summary>Closes the palette and leaves the app active. If the palette still has activation (Esc, running a command),
    /// the app gets it first: closing an active window leaves the choice to Windows, which can pick another app.</summary>
    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        if (IsActive) Owner?.Activate();
        Close();
    }

    internal void Refresh()
    {
        var text = _box.Text;
        var ranked = _source(text)
            .Select(e => (e, score: Fuzzy.Score(text, e.Title) ?? (Fuzzy.Score(text, e.Category + " " + e.Title) - 20)))
            .Where(x => x.score != null)
            .OrderByDescending(x => x.score).ThenBy(x => x.e.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(60).ToList();
        _list.Items.Clear();
        foreach (var (e, _) in ranked) _list.Items.Add(Row(e));
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
    }

    private static FrameworkElement Row(PaletteEntry e)
    {
        var dp = new DockPanel { Tag = e, Margin = new Thickness(4, 1, 4, 1) };
        var right = new TextBlock { Text = e.Gesture ?? "", Foreground = Theme.Brush("Fg3"), Margin = new Thickness(12, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(right, Dock.Right);
        dp.Children.Add(right);
        var cat = new TextBlock { Text = e.Category, Foreground = Theme.Brush("Fg3"), Width = 96, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(cat, Dock.Left);
        dp.Children.Add(cat);
        var title = new StackPanel();
        title.Children.Add(new TextBlock { Text = e.Title, Foreground = Theme.Brush("Fg1"), TextTrimming = TextTrimming.CharacterEllipsis });
        if (e.Detail != null) title.Children.Add(new TextBlock { Text = e.Detail, Foreground = Theme.Brush("Fg3"), FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis });
        dp.Children.Add(title);
        return dp;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: SafeClose(); e.Handled = true; break;
            case Key.Enter: RunSelected(); e.Handled = true; break;
            case Key.Down: Move(1); e.Handled = true; break;
            case Key.Up: Move(-1); e.Handled = true; break;
            case Key.PageDown: Move(8); e.Handled = true; break;
            case Key.PageUp: Move(-8); e.Handled = true; break;
        }
    }

    private void Move(int d)
    {
        if (_list.Items.Count == 0) return;
        _list.SelectedIndex = Math.Clamp(_list.SelectedIndex + d, 0, _list.Items.Count - 1);
        _list.ScrollIntoView(_list.SelectedItem);
    }

    internal void RunSelected()
    {
        if (_list.SelectedItem is not FrameworkElement { Tag: PaletteEntry entry }) return;
        SafeClose();
        Owner?.Dispatcher.BeginInvoke(entry.Run);
    }
}
