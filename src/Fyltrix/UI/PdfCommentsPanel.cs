using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Automation;
using Fyltrix.Core;
using Fyltrix.Core.Pdf;

namespace Fyltrix.UI;

/// <summary>Read-only annotation metadata next to the native document it belongs to.</summary>
internal sealed class PdfCommentsPanel : DockPanel
{
    private readonly TextBlock _heading = new(), _state = new();
    private readonly ListBox _comments = new();
    private int _version;
    private CancellationTokenSource? _loadCancellation;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private bool _navigationAvailable;
    private IReadOnlyList<double> _heights = Array.Empty<double>();
    internal event Action<int, IReadOnlyList<double>>? PageRequested;
    /// <summary>The user chose a comment (Show button, double-click or Enter): show it, and navigate when possible.</summary>
    internal event Action<PdfComment>? CommentRequested;
    /// <summary>Visible box and rotation of every page of the loaded document (empty until loaded).</summary>
    internal IReadOnlyList<PdfPageGeometry> PageGeometry { get; private set; } = Array.Empty<PdfPageGeometry>();
    internal IReadOnlyList<double> PageHeights => _heights;
    /// <summary>False while a PDF's comments have not been read yet (large files in the Preview pane are read on demand).</summary>
    internal bool HasResult { get; private set; }
    internal ListBox CommentList => _comments;
    // Where the user was in the list, kept per document so reopening it (or a re-read after the file changed)
    // returns to the same place with the last shown comment selected.
    private string? _path;
    private (DateTime, long)? _stamp;
    private string? _keepId;
    private double _keepOffset = -1;
    internal event EventHandler? StateChanged;
    internal IReadOnlyList<PdfComment> Comments { get; private set; } = Array.Empty<PdfComment>();
    internal string Status => _state.Text;
    internal Task LoadTask { get; private set; } = Task.CompletedTask;

    internal PdfCommentsPanel()
    {
        Height = 380;
        Visibility = Visibility.Collapsed;
        Background = Theme.Brush("Bg2");
        LastChildFill = true;
        _heading.FontWeight = FontWeights.SemiBold;
        _heading.Foreground = Theme.Brush("Fg1");
        _heading.Margin = new Thickness(10, 5, 10, 3);
        SetDock(_heading, Dock.Top); Children.Add(_heading);
        _state.Foreground = Theme.Brush("Fg2");
        _state.Margin = new Thickness(10, 2, 10, 3);
        _state.TextWrapping = TextWrapping.Wrap;
        SetDock(_state, Dock.Top); Children.Add(_state);
        _comments.Background = Theme.Brush("Bg2");
        _comments.BorderThickness = new Thickness(0);
        _comments.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(AutomationProperties.NameProperty, new Binding(nameof(PdfComment.Heading))));
        _comments.ItemContainerStyle = itemStyle;
        ScrollViewer.SetHorizontalScrollBarVisibility(_comments, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(_comments, true);
        VirtualizingPanel.SetVirtualizationMode(_comments, VirtualizationMode.Recycling);
        // Construct ordinary WPF text controls: annotation text never becomes HTML/XAML or a link.
        var item = new FrameworkElementFactory(typeof(StackPanel));
        item.SetValue(MarginProperty, new Thickness(6, 3, 6, 6));
        var title = new FrameworkElementFactory(typeof(TextBlock));
        title.SetBinding(TextBlock.TextProperty, new Binding(nameof(PdfComment.Heading)));
        title.SetValue(TextBlock.ForegroundProperty, Theme.Brush("Fg2"));
        item.AppendChild(title);
        var text = new FrameworkElementFactory(typeof(TextBox));
        text.SetBinding(TextBox.TextProperty, new Binding(nameof(PdfComment.Text)) { Mode = BindingMode.OneWay });
        text.SetValue(TextBox.IsReadOnlyProperty, true);
        text.SetValue(TextBox.TextWrappingProperty, TextWrapping.Wrap);
        text.SetValue(TextBox.BorderThicknessProperty, new Thickness(0));
        text.SetValue(TextBox.BackgroundProperty, Theme.Brush("Bg2"));
        text.SetValue(TextBox.ForegroundProperty, Theme.Brush("Fg1"));
        item.AppendChild(text);
        // Replies (/IRT) are threaded under the comment they answer, never listed as separate comments.
        var replies = new FrameworkElementFactory(typeof(TextBlock));
        replies.SetBinding(TextBlock.TextProperty, new Binding(nameof(PdfComment.RepliesSummary)));
        replies.SetValue(TextBlock.ForegroundProperty, Theme.Brush("Fg2"));
        replies.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        replies.SetValue(MarginProperty, new Thickness(10, 0, 0, 4));
        var hideEmpty = new Style(typeof(TextBlock));
        hideEmpty.Triggers.Add(new DataTrigger { Binding = new Binding(nameof(PdfComment.RepliesSummary)), Value = "", Setters = { new Setter(VisibilityProperty, Visibility.Collapsed) } });
        replies.SetValue(StyleProperty, hideEmpty);
        item.AppendChild(replies);
        var show = new FrameworkElementFactory(typeof(Button));
        show.SetValue(ContentControl.ContentProperty, "Show");
        show.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Left);
        show.SetValue(ToolTipProperty, "Show this comment and scroll to it. Native page controls remain available.");
        show.AddHandler(Button.ClickEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is FrameworkElement { DataContext: PdfComment comment }) Request(comment);
        }));
        item.AppendChild(show);
        _comments.ItemTemplate = new DataTemplate { VisualTree = item };
        Children.Add(_comments);
        _comments.MouseDoubleClick += (_, _) => { if (_comments.SelectedItem is PdfComment c) Request(c); };
        _comments.KeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Enter || _comments.SelectedItem is not PdfComment c) return;
            e.Handled = true; Request(c);
        };
        // No cancellation on Unloaded: this panel lives in a popup, which unloads it (late, from a dispatcher timer)
        // whenever the list closes; that discarded the read of the document selected next. Its owner stops loading on
        // dispose, and every new document supersedes the previous read.
        // Remember where the user puts the list: their own scrolling (the list's scroller, with unchanged content
        // size, so a re-layout after the items were replaced doesn't count) and the comment they select.
        _comments.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (e.OriginalSource is ScrollViewer own && own.TemplatedParent == _comments && HasResult && Comments.Count > 0
                && e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0 && e.VerticalChange != 0)
                _keepOffset = e.VerticalOffset;
        }));
        _comments.SelectionChanged += (_, _) => { if (_comments.SelectedItem is PdfComment selected) _keepId = selected.Id; };
    }

    /// <summary>Shows a comment (its card finds and reveals it in the native viewer).</summary>
    internal void Request(PdfComment comment)
    {
        _comments.SelectedItem = comment;
        RememberPosition();
        CommentRequested?.Invoke(comment);
    }

    /// <summary>True when <paramref name="path"/>'s comments are already read and the file hasn't changed since.</summary>
    internal bool IsCurrent(string path) => HasResult && string.Equals(_path, path, StringComparison.OrdinalIgnoreCase) && Stamp(path) == _stamp;

    private static (DateTime, long)? Stamp(string path)
    {
        try { var f = new FileInfo(path); return f.Exists ? (f.LastWriteTimeUtc, f.Length) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    private ScrollViewer? Scroller() => VisualTree.FindDescendants<ScrollViewer>(_comments).FirstOrDefault();

    /// <summary>Notes the list's scroll position and selected comment (when a comment is shown).</summary>
    internal void RememberPosition()
    {
        if (Comments.Count == 0) return;
        if (Scroller() is { } scroller) _keepOffset = scroller.VerticalOffset;
        if (_comments.SelectedItem is PdfComment selected) _keepId = selected.Id;
    }

    /// <summary>Returns the list to the remembered place once it has been laid out (the comment is matched by its
    /// stable id, so this also works after the document was re-read).</summary>
    internal void RestorePosition()
    {
        // Applied after layout, then once more when the dispatcher is idle: a virtualized list re-anchors in its own
        // deferred layout passes (notably after its items were replaced while closed) and could override one attempt.
        foreach (var priority in new[] { System.Windows.Threading.DispatcherPriority.Loaded, System.Windows.Threading.DispatcherPriority.ContextIdle })
            Dispatcher.BeginInvoke(priority, new Action(ApplyPosition));
    }

    private void ApplyPosition()
    {
        if (Comments.Count == 0 || !IsVisible) return;
        if (_keepId != null && Comments.FirstOrDefault(c => c.Id == _keepId) is { } keep) _comments.SelectedItem = keep;
        _comments.UpdateLayout();
        if (_keepOffset >= 0 && Scroller() is { } scroller) scroller.ScrollToVerticalOffset(_keepOffset);
        else if (_comments.SelectedItem != null) _comments.ScrollIntoView(_comments.SelectedItem);
        else Scroller()?.ScrollToTop(); // a document opened for the first time starts at its first comment
        _comments.UpdateLayout();
    }

    /// <summary>Page-level navigation (Compare's estimate), used when the exact position can't be revealed.</summary>
    internal void RequestPage(PdfComment comment)
    {
        if (_navigationAvailable) PageRequested?.Invoke(comment.Page, _heights);
    }

    private string RepliesNote()
    {
        int replies = Comments.Sum(c => c.Replies.Count);
        return replies == 0 ? "" : $" and {replies} repl{(replies == 1 ? "y" : "ies")}";
    }

    internal void StopLoading() { ++_version; _loadCancellation?.Cancel(); }

    // Dependency property keeps already-realized navigation buttons in sync with native readiness.
    internal static readonly DependencyProperty NavigationAvailableProperty = DependencyProperty.Register(nameof(NavigationAvailable),
        typeof(bool), typeof(PdfCommentsPanel), new PropertyMetadata(false));
    public bool NavigationAvailable
    {
        get => (bool)GetValue(NavigationAvailableProperty);
        set { _navigationAvailable = value; SetValue(NavigationAvailableProperty, value); }
    }

    internal void ShowFile(string path) => ShowFile(path, load: true);

    /// <summary>Shows the comments of <paramref name="path"/>. With <paramref name="load"/> false a PDF is only marked
    /// (nothing is read) until the comments are opened.</summary>
    internal void ShowFile(string path, bool load)
    {
        int version = ++_version;
        bool sameDocument = string.Equals(_path, path, StringComparison.OrdinalIgnoreCase);
        if (!sameDocument) { _keepId = null; _keepOffset = -1; } // another document: start at the top
        _path = path;
        _stamp = null;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        // Re-reading the same document keeps its current list on screen until the new one arrives: emptying it would
        // reset the virtualized list, which then jumps to its end.
        if (!sameDocument || !load)
        {
            Comments = Array.Empty<PdfComment>();
            _comments.ItemsSource = Comments;
        }
        _heights = Array.Empty<double>();
        PageGeometry = Array.Empty<PdfPageGeometry>();
        HasResult = false;
        NavigationAvailable = false;
        bool pdf = FileKinds.KindFor(path, false) == PreviewKind.Pdf;
        Visibility = pdf ? Visibility.Visible : Visibility.Collapsed;
        if (!pdf) { LoadTask = Task.CompletedTask; StateChanged?.Invoke(this, EventArgs.Empty); return; }
        _heading.Text = "Comments — " + Path.GetFileName(path);
        AutomationProperties.SetName(_comments, _heading.Text);
        _heading.ToolTip = path;
        if (!load) { _state.Text = "Open Comments to read this document's comments."; LoadTask = Task.CompletedTask; StateChanged?.Invoke(this, EventArgs.Empty); return; }
        _state.Text = "Reading comments…";
        LoadTask = Load(path, version, _loadCancellation.Token);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task Load(string path, int version, CancellationToken cancellation)
    {
        try
        {
            await _loadGate.WaitAsync(cancellation);
            PdfComments result;
            try { result = await Task.Run(() => PdfService.ReadComments(path, cancellation: cancellation), cancellation); }
            finally { _loadGate.Release(); }
            if (version != _version) return;
            Comments = result.Items;
            _heights = result.PageHeights;
            PageGeometry = result.Pages;
            _stamp = Stamp(path);
            HasResult = true;
            RestorePosition();
            _comments.ItemsSource = Comments;
            // One malformed annotation must not hide how many comments were read.
            _state.Text = (Comments.Count == 0 ? "No comments in this document." : $"{Comments.Count} comment{(Comments.Count == 1 ? "" : "s")}{RepliesNote()} · read-only")
                + (result.Partial ? (Comments.Count == 0 ? " " : ". ") + "Some annotation metadata was unreadable or exceeded the metadata limits." : "");
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (version != _version) return;
            HasResult = true;
            _state.Text = ex is PdfPasswordException
                ? "Comments are password-protected. The native viewer can still request the PDF password."
                : "Comments could not be read. The native PDF viewer remains available.";
            Diagnostics.Log("PDF comments", ex, path);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
