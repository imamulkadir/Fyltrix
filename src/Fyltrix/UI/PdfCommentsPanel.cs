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
        var page = new FrameworkElementFactory(typeof(Button));
        page.SetValue(ContentControl.ContentProperty, "Go to page");
        page.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Left);
        page.SetValue(ToolTipProperty, "Scroll to this comment's page position. Native page controls remain available.");
        page.SetBinding(IsEnabledProperty, new Binding(nameof(NavigationAvailable)) { Source = this });
        page.AddHandler(Button.ClickEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is FrameworkElement { DataContext: PdfComment comment } && _navigationAvailable)
                PageRequested?.Invoke(comment.Page, _heights);
        }));
        item.AppendChild(page);
        _comments.ItemTemplate = new DataTemplate { VisualTree = item };
        Children.Add(_comments);
        Unloaded += (_, _) => StopLoading();
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

    internal void ShowFile(string path)
    {
        int version = ++_version;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        Comments = Array.Empty<PdfComment>();
        _comments.ItemsSource = Comments;
        _heights = Array.Empty<double>();
        NavigationAvailable = false;
        bool pdf = FileKinds.KindFor(path, false) == PreviewKind.Pdf;
        Visibility = pdf ? Visibility.Visible : Visibility.Collapsed;
        if (!pdf) { LoadTask = Task.CompletedTask; StateChanged?.Invoke(this, EventArgs.Empty); return; }
        _heading.Text = "Comments — " + Path.GetFileName(path);
        AutomationProperties.SetName(_comments, _heading.Text);
        _heading.ToolTip = path;
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
            _comments.ItemsSource = Comments;
            _state.Text = result.Partial ? "Some annotation metadata was unreadable or exceeded the metadata limits."
                : Comments.Count == 0 ? "No comments in this document." : $"{Comments.Count} comment{(Comments.Count == 1 ? "" : "s")} · read-only";
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (version != _version) return;
            _state.Text = ex is PdfPasswordException
                ? "Comments are password-protected. The native viewer can still request the PDF password."
                : "Comments could not be read. The native PDF viewer remains available.";
            Diagnostics.Log("PDF comments", ex, path);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
