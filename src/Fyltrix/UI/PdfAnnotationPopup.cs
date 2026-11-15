using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Fyltrix.Core.Pdf;

namespace Fyltrix.UI;

/// <summary>
/// A small read-only card for one PDF comment and its replies, shown above the native PDF viewer (a WPF popup has its
/// own window, so it can sit over WebView2). Annotation text is shown as plain, selectable text only — never as markup,
/// links or actions. Without a known on-screen position it is anchored to the viewer's top-right edge.
/// </summary>
internal sealed class PdfAnnotationPopup : IDisposable
{
    internal const double CardWidth = 340;
    private readonly FrameworkElement _viewer;
    private readonly TextBlock _title = new(), _meta = new(), _location = new(), _document = new();
    private readonly TextBox _text = ReadOnlyText(14);
    private readonly StackPanel _replies = new();
    private readonly Button _close;
    internal Popup Popup { get; }
    internal PdfComment? Comment { get; private set; }
    internal string? DocumentPath { get; private set; }
    internal string LocationNote => _location.Text;
    internal IReadOnlyList<string> ReplyTexts => _replies.Children.OfType<StackPanel>().Select(p => ((TextBox)p.Children[1]).Text).ToList();

    internal PdfAnnotationPopup(FrameworkElement viewer)
    {
        _viewer = viewer;
        _close = Theme.SmallButton("", "", "Close comment (Esc)");
        _close.VerticalAlignment = VerticalAlignment.Top;
        _close.Click += (_, _) => Close();
        _title.FontWeight = FontWeights.SemiBold; _title.Foreground = Theme.Brush("Fg1"); _title.TextWrapping = TextWrapping.Wrap;
        _title.VerticalAlignment = VerticalAlignment.Center;
        foreach (var t in new[] { _meta, _document, _location }) { t.Foreground = Theme.Brush("Fg2"); t.TextWrapping = TextWrapping.Wrap; t.FontSize = 11.5; }
        _location.FontStyle = FontStyles.Italic;

        var header = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_close, Dock.Right); header.Children.Add(_close); header.Children.Add(_title);
        var body = new StackPanel();
        body.Children.Add(header);
        body.Children.Add(_meta);
        body.Children.Add(_document);
        body.Children.Add(_location);
        _text.Margin = new Thickness(0, 8, 0, 0);
        body.Children.Add(_text);
        _replies.Margin = new Thickness(10, 6, 0, 0);
        body.Children.Add(_replies);
        var scroll = new ScrollViewer { Content = body, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var card = new Border
        {
            Width = CardWidth, Padding = new Thickness(12, 8, 8, 10), Margin = new Thickness(8), // margin leaves room for the shadow
            Background = Theme.Brush("Bg2"), BorderBrush = Theme.Brush("Line"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Child = scroll,
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.35, Color = Colors.Black },
        };
        System.Windows.Automation.AutomationProperties.SetName(card, "PDF comment");
        Popup = new Popup
        {
            PlacementTarget = viewer, Placement = PlacementMode.Relative, StaysOpen = false,
            AllowsTransparency = true, PopupAnimation = PopupAnimation.None, Child = card,
        };
        card.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true; Close();
        };
        Popup.Opened += (_, _) => _close.Focus(); // so Escape works; focus stays within Fyltrix
    }

    private static TextBox ReadOnlyText(double size) => new()
    {
        IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Padding = new Thickness(0),
        Background = Brushes.Transparent, Foreground = Theme.Brush("Fg1"), FontSize = size, IsTabStop = true,
    };

    /// <summary>
    /// Shows <paramref name="comment"/> of <paramref name="documentPath"/>. <paramref name="target"/> is where the comment
    /// is on screen (device-independent units relative to the viewer) when known exactly; the card is placed beside it.
    /// <paramref name="locationNote"/> says how precisely the document was positioned (never claims more than achieved).
    /// </summary>
    internal void Show(PdfComment comment, string documentPath, Rect? target = null, string? locationNote = null)
    {
        Comment = comment;
        DocumentPath = documentPath;
        _title.Text = $"{comment.Type} · Page {comment.Page}";
        var when = comment.Modified ?? comment.Created;
        _meta.Text = string.Join(" · ", new[] { comment.Author, when?.ToString("g") ?? "" }.Where(s => s.Length > 0));
        _meta.Visibility = _meta.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _document.Text = Path.GetFileName(documentPath);
        _location.Text = locationNote ?? "";
        _location.Visibility = _location.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _text.Text = comment.Text.Length == 0 ? "(No comment text; see the replies.)" : comment.Text;
        _replies.Children.Clear();
        foreach (var reply in comment.Replies)
        {
            var item = new StackPanel { Margin = new Thickness(0, 4, 0, 2) };
            var head = new TextBlock { Foreground = Theme.Brush("Fg2"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
                Text = "↳ Reply" + string.Concat(new[] { reply.Author, (reply.Modified ?? reply.Created)?.ToString("g") ?? "" }.Where(s => s.Length > 0).Select(s => " · " + s)) };
            item.Children.Add(head);
            var text = ReadOnlyText(13); text.Text = reply.Text;
            item.Children.Add(text);
            _replies.Children.Add(item);
        }
        _replies.Visibility = comment.Replies.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        Popup.IsOpen = false; // reposition when another comment is chosen
        if (target is { } t && !t.IsEmpty)
        {
            // Beside the comment: to its right when there is room inside the viewer, otherwise to its left.
            double outer = CardWidth + 16;
            double x = t.Right + 8 + outer <= _viewer.ActualWidth ? t.Right + 4 : Math.Max(0, t.Left - outer - 4);
            Popup.Placement = PlacementMode.Relative;
            Popup.HorizontalOffset = x;
            Popup.VerticalOffset = Math.Max(0, t.Top - 12);
        }
        else
        {
            Popup.Placement = PlacementMode.Relative;
            Popup.HorizontalOffset = Math.Max(0, _viewer.ActualWidth - CardWidth - 28);
            Popup.VerticalOffset = 44; // below the native viewer toolbar
        }
        Popup.IsOpen = true; // WPF keeps a popup inside the screen's work area
    }

    internal void Close()
    {
        Popup.IsOpen = false;
        Comment = null;
        DocumentPath = null;
    }

    public void Dispose() => Close();
}
