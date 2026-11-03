using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Fyltrix.Core;
using Fyltrix.Preview;

namespace Fyltrix.UI;

/// <summary>Two files next to each other (rendered), or a line-by-line text diff.</summary>
public sealed class CompareWindow : Window
{
    /// <summary>The same text diff and rendered families this window actually supports; extensions may differ.</summary>
    internal static bool Compatible(string left, string right)
    {
        var a = FileKinds.KindFor(left, false);
        var b = FileKinds.KindFor(right, false);
        if (FileKinds.IsTextLike(a) && FileKinds.IsTextLike(b))
            return (a != PreviewKind.Auto || IsText(left)) && (b != PreviewKind.Auto || IsText(right));
        if (a is PreviewKind.Image or PreviewKind.Svg && b is PreviewKind.Image or PreviewKind.Svg) return true;
        if (a is PreviewKind.Media or PreviewKind.NativeMedia && b is PreviewKind.Media or PreviewKind.NativeMedia) return true;
        if (a == PreviewKind.ShellHandler && b == PreviewKind.ShellHandler)
            return ShellPreviewHost.FindHandler(left) is { } handler && ShellPreviewHost.FindHandler(right) == handler;
        return a == b && a is PreviewKind.Pdf or PreviewKind.Image or PreviewKind.Docx or PreviewKind.Xlsx;
    }

    internal static bool IsText(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> bytes = stackalloc byte[4096];
            int n = file.Read(bytes);
            // UTF-16 BOMs are text too; otherwise reject binary control bytes.
            if (n >= 2 && (bytes[0] == 0xff && bytes[1] == 0xfe || bytes[0] == 0xfe && bytes[1] == 0xff)) return true;
            foreach (byte value in bytes[..n]) if (value < 32 && value is not (9 or 10 or 13 or 12)) return false;
            return true;
        }
        catch { return false; }
    }
    private string _left, _right;
    private readonly PreviewPane _leftPane = new(showHeader: true, showPopOut: false, nativePdfInput: true);
    private readonly PreviewPane _rightPane = new(showHeader: true, showPopOut: false, nativePdfInput: true);
    private readonly PreviewPane _diffPane = new(showHeader: false, showPopOut: false);
    private readonly Grid _sideBySide = new();
    private readonly Button _btnSide, _btnDiff;
    private readonly TextBlock _leftLabel = new(), _rightLabel = new();
    private readonly CheckBox _syncScroll;
    private bool _diffMode;
    private bool _nativePdf, _closed;
    private readonly PdfCommentsPanel _leftComments = new(), _rightComments = new();
    private readonly PdfCommentsFlyout _leftFlyout, _rightFlyout;
    private NativePdfWheelMirror? _pdfWheel;
    private NativePdfAccess? _pdfAccess;
    private int _pdfGeneration;

    public CompareWindow(Window owner, string left, string right)
    {
        _left = left;
        _right = right;
        // Use the opening window for initial placement/lifetime only. WPF ownership would
        // keep Compare above it even when the user activates the directory window.
        Title = "Compare";
        Background = Theme.Brush("Bg1");
        var wa = SystemParameters.WorkArea;
        Width = Math.Min(1700, wa.Width * 0.9);
        Height = Math.Min(1000, wa.Height * 0.88);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Icon = AppIcon.ForWindow();
        void PlaceInitially()
        {
            // Respect explicit/offscreen placement. No activation or ongoing Z-order changes.
            if (WindowStartupLocation != WindowStartupLocation.Manual && WindowState == WindowState.Normal)
                WindowPlacement.CenterOn(this, owner);
        }
        SourceInitialized += (_, _) => { Shell.UseDarkTitleBar(this); TaskbarIdentity.Apply(this); AppIcon.ApplyToHwnd(this); PlaceInitially(); };
        Loaded += (_, _) => PlaceInitially(); // after the destination monitor's DPI has been applied
        Loaded += (_, _) =>
        {
            _pdfWheel ??= new NativePdfWheelMirror(_leftPane, _rightPane, () => _pdfAccess, () => _nativePdf && SyncScrolling);
            ConnectPdfAccess();
        };
        LocationChanged += (_, _) => _pdfAccess?.RefreshGeometry();
        SizeChanged += (_, _) => _pdfAccess?.RefreshGeometry();
        void OpeningWindowClosed(object? sender, EventArgs args) { if (!_closed) Close(); }
        owner.Closed += OpeningWindowClosed;

        var root = new DockPanel();
        // ---------- toolbar ----------
        var bar = new DockPanel { Background = Theme.Brush("Bg2"), Height = 42, LastChildFill = true };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        _btnSide = Theme.SmallButton("\uE8A9", "Side by side", "Show both files rendered next to each other");
        _btnDiff = Theme.SmallButton("\uE8AB", "Text diff", "Line-by-line differences (text, JSON, XML, CSV, code…)");
        var swap = Theme.SmallButton("\uE8AB", "Swap", "Swap left and right");
        _btnSide.Click += (_, _) => SetMode(false);
        _btnDiff.Click += (_, _) => SetMode(true);
        swap.Click += (_, _) => { (_left, _right) = (_right, _left); Reload(); };
        // side by side: scrolling one file scrolls the other to the same place (the text diff is one table, so it always does)
        _syncScroll = new CheckBox
        {
            Content = "Sync scrolling", IsChecked = true, Foreground = Theme.Brush("Fg1"), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0), ToolTip = "Scroll both files together",
        };
        ToolTipService.SetShowOnDisabled(_syncScroll, true);
        _syncScroll.Checked += (_, _) =>
        {
            if (_nativePdf) _pdfAccess?.EnableSync(true);
            else if (SyncScrolling) _leftPane.RequestScrollReport();
        };
        _syncScroll.Unchecked += (_, _) => _pdfAccess?.EnableSync(false);
        tools.Children.Add(_syncScroll);
        tools.Children.Add(_btnSide);
        tools.Children.Add(_btnDiff);
        tools.Children.Add(swap);
        DockPanel.SetDock(tools, Dock.Right);
        bar.Children.Add(tools);

        var files = new Grid { Margin = new Thickness(8, 0, 8, 0) };
        files.ColumnDefinitions.Add(new ColumnDefinition());
        files.ColumnDefinitions.Add(new ColumnDefinition());
        var lp = FilePicker(_leftLabel, isLeft: true);
        var rp = FilePicker(_rightLabel, isLeft: false);
        Grid.SetColumn(rp, 1);
        files.Children.Add(lp);
        files.Children.Add(rp);
        bar.Children.Add(files);
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(new Border { Child = bar, BorderBrush = Theme.Brush("Line"), BorderThickness = new Thickness(0, 0, 0, 1) });
        DockPanel.SetDock(root.Children[0], Dock.Top);

        // ---------- body ----------
        var body = new Grid();
        _sideBySide.ColumnDefinitions.Add(new ColumnDefinition());
        _sideBySide.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        _sideBySide.ColumnDefinitions.Add(new ColumnDefinition());
        var splitter = new GridSplitter { HorizontalAlignment = HorizontalAlignment.Stretch, Background = Theme.Brush("Line") };
        Grid.SetColumn(splitter, 1);
        _leftFlyout = new PdfCommentsFlyout(this, _leftPane, _leftComments, () => _left);
        _rightFlyout = new PdfCommentsFlyout(this, _rightPane, _rightComments, () => _right);
        Grid.SetColumn(_rightPane, 2);
        _sideBySide.Children.Add(_leftPane);
        _sideBySide.Children.Add(splitter);
        _sideBySide.Children.Add(_rightPane);
        _leftComments.PageRequested += (page, heights) => _pdfAccess?.GoToPage(0, page, heights);
        _rightComments.PageRequested += (page, heights) => _pdfAccess?.GoToPage(1, page, heights);
        _leftPane.NativePdfLoaded += (_, _) => RefreshPdfAccess(0);
        _rightPane.NativePdfLoaded += (_, _) => RefreshPdfAccess(1);
        body.Children.Add(_sideBySide);
        body.Children.Add(_diffPane);
        root.Children.Add(body);
        Content = root;

        _leftPane.ReportsScroll = _rightPane.ReportsScroll = true;
        _leftPane.Scrolled += (_, p) => { if (SyncScrolling) _rightPane.ScrollTo(p); };
        _rightPane.Scrolled += (_, p) => { if (SyncScrolling) _leftPane.ScrollTo(p); };

        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        _diffPane.EscapePressed += (_, _) => Close();
        _leftPane.EscapePressed += (_, _) => Close();
        _rightPane.EscapePressed += (_, _) => Close();
        Closed += (_, _) =>
        {
            _closed = true;
            ++_pdfGeneration;
            _pdfWheel?.Dispose();
            _pdfAccess?.Dispose();
            _leftFlyout.Dispose(); _rightFlyout.Dispose();
            owner.Closed -= OpeningWindowClosed;
            _leftPane.DisposeWeb(); _rightPane.DisposeWeb(); _diffPane.DisposeWeb();
        };

        bool bothText = FileKinds.IsTextLike(FileKinds.KindFor(left, false)) && FileKinds.IsTextLike(FileKinds.KindFor(right, false));
        _diffMode = bothText;
        Reload();
    }

    /// <summary>Whether scrolling one side scrolls the other (the "Sync scrolling" checkbox; on by default).</summary>
    public bool SyncScrolling { get => (!_nativePdf || _syncScroll.IsEnabled) && _syncScroll.IsChecked == true; set => _syncScroll.IsChecked = value; }

    internal PreviewPane LeftPane => _leftPane;
    internal PreviewPane RightPane => _rightPane;
    internal CheckBox SyncScrollCheck => _syncScroll;
    internal PdfCommentsPanel LeftComments => _leftComments;
    internal PdfCommentsPanel RightComments => _rightComments;
    internal NativePdfAccess? PdfAccess => _pdfAccess;
    internal NativePdfWheelMirror? PdfWheel => _pdfWheel;
    internal PdfCommentsFlyout LeftCommentsFlyout => _leftFlyout;
    internal PdfCommentsFlyout RightCommentsFlyout => _rightFlyout;

    private FrameworkElement FilePicker(TextBlock label, bool isLeft)
    {
        label.Foreground = Theme.Brush("Fg1");
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(6, 0, 0, 0);
        var change = Theme.SmallButton("\uE8E5", "", "Choose a different file");
        change.Click += (_, _) =>
        {
            var current = isLeft ? _left : _right;
            var dlg = new Microsoft.Win32.OpenFileDialog { InitialDirectory = Path.GetDirectoryName(current), Title = "Choose a file to compare" };
            if (dlg.ShowDialog(this) == true)
            {
                if (isLeft) _left = dlg.FileName; else _right = dlg.FileName;
                Reload();
            }
        };
        var dp = new DockPanel { Margin = new Thickness(0, 0, 8, 0) };
        DockPanel.SetDock(change, Dock.Left);
        dp.Children.Add(change);
        dp.Children.Add(label);
        return dp;
    }

    internal void SetMode(bool diff)
    {
        _diffMode = diff;
        Reload();
    }

    private void Reload()
    {
        _leftFlyout.Close(); _rightFlyout.Close();
        _leftLabel.Text = Path.GetFileName(_left);
        _leftLabel.ToolTip = _left;
        _rightLabel.Text = Path.GetFileName(_right);
        _rightLabel.ToolTip = _right;
        Title = $"Compare – {Path.GetFileName(_left)}  ↔  {Path.GetFileName(_right)}";
        _btnSide.Background = _diffMode ? Theme.Brush("Bg2") : Theme.Brush("Sel");
        _btnDiff.Background = _diffMode ? Theme.Brush("Sel") : Theme.Brush("Bg2");
        _nativePdf = !_diffMode && (FileKinds.KindFor(_left, false) == PreviewKind.Pdf || FileKinds.KindFor(_right, false) == PreviewKind.Pdf);
        _leftPane.ReportsScroll = FileKinds.KindFor(_left, false) != PreviewKind.Pdf;
        _rightPane.ReportsScroll = FileKinds.KindFor(_right, false) != PreviewKind.Pdf;
        _syncScroll.IsEnabled = !_diffMode && !_nativePdf;
        _syncScroll.ToolTip = _nativePdf
            ? "Connecting native PDF accessibility scrolling…"
            : _diffMode ? "The text diff always scrolls both files together" : "Scroll both files together";

        if (_diffMode)
        {
            _sideBySide.Visibility = Visibility.Collapsed;
            _diffPane.Visibility = Visibility.Visible;
            _diffPane.ShowDiff(_left, _right);
        }
        else
        {
            _diffPane.Visibility = Visibility.Collapsed;
            _sideBySide.Visibility = Visibility.Visible;
            _leftPane.Show(_left, delayMs: 0);
            _rightPane.Show(_right, delayMs: 0);
        }
        _leftComments.ShowFile(_diffMode ? "" : _left);
        _rightComments.ShowFile(_diffMode ? "" : _right);
        ConnectPdfAccess();
    }

    private void RefreshPdfAccess(int side)
    {
        if (_closed || !_nativePdf) return;
        if (_pdfAccess == null) { ConnectPdfAccess(); return; }
        bool left = FileKinds.KindFor(_left, false) == PreviewKind.Pdf;
        bool right = FileKinds.KindFor(_right, false) == PreviewKind.Pdf;
        if (!_pdfAccess.MatchesHandles(left ? _leftPane.Web.Handle : IntPtr.Zero, right ? _rightPane.Web.Handle : IntPtr.Zero))
        { ConnectPdfAccess(); return; }
        _syncScroll.IsEnabled = false;
        _syncScroll.ToolTip = "Reconnecting native PDF accessibility scrolling...";
        _pdfAccess.Refresh(side);
        _pdfAccess.EnableSync(_syncScroll.IsChecked == true);
    }

    private void ConnectPdfAccess()
    {
        int generation = ++_pdfGeneration;
        _pdfAccess?.Dispose();
        _pdfAccess = null;
        if (!_nativePdf || !IsLoaded || _closed) return;
        _syncScroll.IsEnabled = false;
        _syncScroll.ToolTip = "Connecting native PDF accessibility scrolling…";
        bool left = FileKinds.KindFor(_left, false) == PreviewKind.Pdf;
        bool right = FileKinds.KindFor(_right, false) == PreviewKind.Pdf;
        _pdfAccess = new NativePdfAccess(new System.Windows.Interop.WindowInteropHelper(this).Handle,
            left ? _leftPane.Web.Handle : IntPtr.Zero, right ? _rightPane.Web.Handle : IntPtr.Zero,
            (a, b, error) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_closed || generation != _pdfGeneration) return;
                _leftComments.NavigationAvailable = a;
                _rightComments.NavigationAvailable = b;
                _syncScroll.IsEnabled = left && right && a && b && error == null;
                _syncScroll.ToolTip = error ?? (_syncScroll.IsEnabled
                    ? "Synchronize native PDFs by relative vertical position. Different page counts are supported."
                    : "Native PDF scroll interfaces are not ready for both documents.");
            })));
        _pdfAccess.EnableSync(_syncScroll.IsChecked == true && left && right);
    }

    public void Release(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        _leftPane.ReleaseIfShowing(list);
        _rightPane.ReleaseIfShowing(list);
    }
}
