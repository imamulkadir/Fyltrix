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
    /// <summary>Outline around a comment whose position was confirmed exactly.</summary>
    internal PdfAnnotationHighlight Highlight { get; }
    /// <summary>A reveal scrolled the native viewer (Compare aligns the other side).</summary>
    internal event Action? NativeScrolled;
    /// <summary>A reveal is about to scroll this document natively (Compare marks this side as the user's choice, so
    /// synchronization follows it instead of pulling it back).</summary>
    internal event Action? NativeScrolling;
    internal Task RevealTask { get; private set; } = Task.CompletedTask;
    internal RevealResult? LastReveal { get; private set; }
    private CancellationTokenSource? _reveal;
    /// <summary>Raised after the card opened for a chosen comment (precise navigation hooks in here).</summary>
    internal event Action<PdfComment>? CommentShown;

    /// <param name="hideWhenEmpty">Preview pane: no button for PDFs without comments, so the header stays uncluttered.</param>
    internal PdfCommentsFlyout(Window window, PreviewPane pane, PdfCommentsPanel panel, Func<string> path, bool hideWhenEmpty = false)
    {
        _window = window; _panel = panel; _path = path; _pane = pane; _hideWhenEmpty = hideWhenEmpty;
        Card = new PdfAnnotationPopup(pane);
        Highlight = new PdfAnnotationHighlight(pane);
        Card.Popup.Closed += (_, _) => { if (Card.Comment == null) StopReveal(); }; // closed by the user (not re-placed)
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
        Popup.Opened += (_, _) => { close.Focus(); _panel.RestorePosition(); };
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
        var path = _path();
        if (!_panel.IsCurrent(path))
        {
            bool navigation = _panel.NavigationAvailable;
            _panel.ShowFile(path); // pick up comments added since the document was read (it changed on disk)
            _panel.NavigationAvailable = navigation;
        }
        Popup.HorizontalOffset = Math.Min(0, Button.ActualWidth - 370);
        Popup.IsOpen = true;
        _ = PrewarmWhenRead();
    }

    /// <summary>Shows a chosen comment: the list makes room for the card, which belongs to this document only.</summary>
    private void ShowComment(PdfComment comment)
    {
        Close();
        StopReveal();
        var path = _path();
        Card.Show(comment, path, locationNote: "Finding the comment…");
        var cancellation = _reveal = new CancellationTokenSource();
        RevealTask = RevealAsync(comment, path, cancellation.Token);
        CommentShown?.Invoke(comment);
    }

    /// <summary>Scrolls the native viewer to the comment and places the card (and, when exact, the outline) by it.
    /// A newer comment, another document or a closed card makes the result obsolete; it is then ignored.</summary>
    private async Task RevealAsync(PdfComment comment, string path, CancellationToken cancellation)
    {
        var result = new RevealResult(RevealLevel.Unavailable, null, Rect.Empty, "the native PDF viewer isn't showing this document");
        if (_pane.ShowsNativePdf && string.Equals(_pane.CurrentPath, path, StringComparison.OrdinalIgnoreCase) && _panel.PageGeometry.Count > 0)
        {
            NativeScrolling?.Invoke();
            result = await NativePdfLocator.RevealAsync(_pane.Web.Handle, comment.Page, comment.Target, _panel.PageGeometry, cancellation);
        }
        if (cancellation.IsCancellationRequested || !ReferenceEquals(Card.Comment, comment) || !string.Equals(Card.DocumentPath, path, StringComparison.OrdinalIgnoreCase)) return;
        LastReveal = result;
        if (result.Level >= RevealLevel.Vertical) NativeScrolled?.Invoke();
        else _panel.RequestPage(comment); // Compare's page estimate (no-op where page navigation isn't available)

        var area = result.Target is { } screen ? ToViewer(screen) : null;
        switch (result.Level)
        {
            case RevealLevel.Exact when area is { } exact:
                Card.Show(comment, path, exact);
                Highlight.Show(exact);
                break;
            case RevealLevel.Vertical or RevealLevel.Exact when area is { } band:
                // Beside the viewer's right edge at the comment's height; nothing is outlined.
                Card.Show(comment, path, new Rect(Math.Max(0, _pane.ActualWidth - 2), band.Y, 0, band.Height),
                    "Scrolled to the comment. Its horizontal position couldn't be confirmed, so it isn't outlined.");
                break;
            default:
                Card.Show(comment, path, locationNote: _panel.NavigationAvailable
                    ? "Scrolled to the comment's page; its exact position couldn't be confirmed."
                    : $"Go to page {comment.Page} with the PDF's own page controls; the comment's position couldn't be confirmed.");
                Diagnostics.Info("PDF comments", $"reveal fell back ({result.Level}: {result.Detail})");
                break;
        }
    }

    /// <summary>Screen pixels (from accessibility) to device-independent units relative to the viewer.</summary>
    private Rect? ToViewer(Rect screen)
    {
        try
        {
            if (PresentationSource.FromVisual(_pane) == null) return null;
            var a = _pane.PointFromScreen(new Point(screen.Left, screen.Top));
            var b = _pane.PointFromScreen(new Point(screen.Right, screen.Bottom));
            return new Rect(a, b);
        }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>Once the comments are read, start the viewer's page-layout data so choosing a comment is quick.</summary>
    private async Task PrewarmWhenRead()
    {
        var path = _path();
        await _panel.LoadTask;
        if (_pane.ShowsNativePdf && string.Equals(_pane.CurrentPath, path, StringComparison.OrdinalIgnoreCase) && _panel.PageGeometry.Count > 0)
            NativePdfLocator.Prewarm(_pane.Web.Handle, _panel.PageGeometry);
    }

    private void StopReveal()
    {
        _reveal?.Cancel();
        _reveal = null;
        Highlight.Hide();
    }

    /// <summary>Preview pane: shows the comments of the file now previewed (null/non-PDF hides the button). Large PDFs
    /// are only read when the comments are opened, so ordinary browsing never pays for parsing them.</summary>
    internal void ShowDocument(string? path)
    {
        StopReveal();
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
        if (!_pane.IsVisible) { Close(); StopReveal(); Card.Close(); }
    }

    private void Update(object? sender, EventArgs args)
    {
        // A card for another document (or for a document that is gone) never stays open.
        if (Card.DocumentPath != null && !string.Equals(Card.DocumentPath, _path(), StringComparison.OrdinalIgnoreCase)) { StopReveal(); Card.Close(); }
        bool empty = _panel.HasResult && _panel.Comments.Count == 0;
        Button.Visibility = _panel.Visibility == Visibility.Visible && !(_hideWhenEmpty && empty) ? Visibility.Visible : Visibility.Collapsed;
        Theme.SetButtonText(Button, _panel.Comments.Count == 0 ? "Comments" : $"Comments ({_panel.Comments.Count})");
        Button.ToolTip = "Show comments for " + System.IO.Path.GetFileName(_path());
        if (Button.Visibility != Visibility.Visible) Close();
    }

    private void CloseOnChange(object? sender, EventArgs args) { Close(); StopReveal(); Card.Close(); }
    internal void Close() => Popup.IsOpen = false;

    public void Dispose()
    {
        Close();
        StopReveal();
        Highlight.Dispose();
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
