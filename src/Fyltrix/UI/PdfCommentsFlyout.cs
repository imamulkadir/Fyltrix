using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Fyltrix.Core;
using Fyltrix.Core.Pdf;
using Fyltrix.Preview;

namespace Fyltrix.UI;

/// <summary>Per-document read-only comments, shown on demand above the native browser airspace.</summary>
internal sealed class PdfCommentsFlyout : IDisposable
{
    private readonly Window _window;
    private readonly PdfCommentsPanel _panel;
    private readonly Func<string> _path;
    private readonly PreviewPane _pane;
    private readonly bool _hideWhenEmpty;
    internal Button Button { get; }
    internal PdfCommentsPanel Panel => _panel;
    internal Popup Popup { get; }
    /// <summary>The card showing the chosen comment, next to this document's viewer.</summary>
    internal PdfAnnotationPopup Card { get; }
    /// <summary>Raised after the card opened for a chosen comment (precise navigation hooks in here).</summary>
    internal event Action<PdfComment>? CommentShown;

    /// <param name="hideWhenEmpty">Preview pane: no button for PDFs without comments, so the header stays uncluttered.</param>
    internal PdfCommentsFlyout(Window window, PreviewPane pane, PdfCommentsPanel panel, Func<string> path, bool hideWhenEmpty = false)
    {
        _window = window; _panel = panel; _path = path; _pane = pane; _hideWhenEmpty = hideWhenEmpty;
        Card = new PdfAnnotationPopup(pane);
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
        panel.CommentRequested += ShowComment;
        pane.IsVisibleChanged += PaneVisibilityChanged;
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

    /// <summary>Shows a chosen comment: the list makes room for the card, which belongs to this document only.</summary>
    private void ShowComment(PdfComment comment)
    {
        Close();
        Card.Show(comment, _path(), locationNote: _panel.NavigationAvailable
            ? "Scrolled to the comment's page; the exact position isn't marked."
            : "Use the PDF's own page controls to see this page.");
        CommentShown?.Invoke(comment);
    }

    /// <summary>Preview pane: shows the comments of the file now previewed (null/non-PDF hides the button). Large PDFs
    /// are only read when the comments are opened, so ordinary browsing never pays for parsing them.</summary>
    internal void ShowDocument(string? path)
    {
        Card.Close();
        if (string.IsNullOrEmpty(path) || FileKinds.KindFor(path, false) != PreviewKind.Pdf) { _panel.ShowFile(""); return; }
        bool small;
        try { small = new System.IO.FileInfo(path).Length <= AutoReadLimit; } catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { small = false; }
        _panel.ShowFile(path, load: small);
    }

    /// <summary>Largest PDF whose comments are read as soon as it is previewed (tests lower it).</summary>
    internal static long AutoReadLimit = 30L * 1024 * 1024;

    private void PaneVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!_pane.IsVisible) { Close(); Card.Close(); }
    }

    private void Update(object? sender, EventArgs args)
    {
        // A card for another document (or for a document that is gone) never stays open.
        if (Card.DocumentPath != null && !string.Equals(Card.DocumentPath, _path(), StringComparison.OrdinalIgnoreCase)) Card.Close();
        bool empty = _panel.HasResult && _panel.Comments.Count == 0;
        Button.Visibility = _panel.Visibility == Visibility.Visible && !(_hideWhenEmpty && empty) ? Visibility.Visible : Visibility.Collapsed;
        Theme.SetButtonText(Button, _panel.Comments.Count == 0 ? "Comments" : $"Comments ({_panel.Comments.Count})");
        Button.ToolTip = "Show comments for " + System.IO.Path.GetFileName(_path());
        if (Button.Visibility != Visibility.Visible) Close();
    }

    private void CloseOnChange(object? sender, EventArgs args) { Close(); Card.Close(); }
    internal void Close() => Popup.IsOpen = false;

    public void Dispose()
    {
        Close();
        Card.Dispose();
        _panel.CommentRequested -= ShowComment;
        _pane.IsVisibleChanged -= PaneVisibilityChanged;
        _panel.StopLoading();
        _panel.StateChanged -= Update;
        Button.Click -= Toggle;
        _window.LocationChanged -= CloseOnChange;
        _window.SizeChanged -= CloseOnChange;
        _window.StateChanged -= CloseOnChange;
    }
}
