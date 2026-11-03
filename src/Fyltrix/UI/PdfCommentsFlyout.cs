using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Fyltrix.Preview;

namespace Fyltrix.UI;

/// <summary>Per-document read-only comments, shown on demand above the native browser airspace.</summary>
internal sealed class PdfCommentsFlyout : IDisposable
{
    private readonly Window _window;
    private readonly PdfCommentsPanel _panel;
    private readonly Func<string> _path;
    internal Button Button { get; }
    internal Popup Popup { get; }

    internal PdfCommentsFlyout(Window window, PreviewPane pane, PdfCommentsPanel panel, Func<string> path)
    {
        _window = window; _panel = panel; _path = path;
        Button = Theme.SmallButton("\uE90A", "Comments", "Show PDF comments");
        pane.AddHeaderAction(Button);
        var close = Theme.SmallButton("\uE711", "", "Close comments");
        var header = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        header.Children.Add(close);
        var content = new DockPanel();
        DockPanel.SetDock(header, Dock.Top); content.Children.Add(header); content.Children.Add(panel);
        Popup = new Popup
        {
            PlacementTarget = Button, Placement = PlacementMode.Bottom, StaysOpen = false,
            AllowsTransparency = true, PopupAnimation = PopupAnimation.None,
            Child = new Border { Width = 370, Padding = new Thickness(6), Background = Theme.Brush("Bg2"),
                BorderBrush = Theme.Brush("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = content },
        };
        Button.Click += Toggle;
        close.Click += (_, _) => { Close(); Button.Focus(); };
        content.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true; Close(); Button.Focus();
        };
        Popup.Opened += (_, _) => close.Focus();
        panel.StateChanged += Update;
        window.LocationChanged += CloseOnChange;
        window.SizeChanged += CloseOnChange;
        window.StateChanged += CloseOnChange;
        Update(this, EventArgs.Empty);
    }

    private void Toggle(object sender, RoutedEventArgs args)
    {
        if (Popup.IsOpen) { Close(); return; }
        bool navigation = _panel.NavigationAvailable;
        _panel.ShowFile(_path()); // pick up comments added since the document was first opened
        _panel.NavigationAvailable = navigation;
        Popup.HorizontalOffset = Math.Min(0, Button.ActualWidth - 370);
        Popup.IsOpen = true;
    }

    private void Update(object? sender, EventArgs args)
    {
        Button.Visibility = _panel.Visibility;
        Theme.SetButtonText(Button, _panel.Comments.Count == 0 ? "Comments" : $"Comments ({_panel.Comments.Count})");
        Button.ToolTip = "Show comments for " + System.IO.Path.GetFileName(_path());
        if (Button.Visibility != Visibility.Visible) Close();
    }

    private void CloseOnChange(object? sender, EventArgs args) => Close();
    internal void Close() => Popup.IsOpen = false;

    public void Dispose()
    {
        Close();
        _panel.StopLoading();
        _panel.StateChanged -= Update;
        Button.Click -= Toggle;
        _window.LocationChanged -= CloseOnChange;
        _window.SizeChanged -= CloseOnChange;
        _window.StateChanged -= CloseOnChange;
    }
}
