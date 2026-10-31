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
    private readonly PreviewPane _leftPane = new(showHeader: true, showPopOut: false);
    private readonly PreviewPane _rightPane = new(showHeader: true, showPopOut: false);
    private readonly PreviewPane _diffPane = new(showHeader: false, showPopOut: false);
    private readonly Grid _sideBySide = new();
    private readonly Button _btnSide, _btnDiff;
    private readonly TextBlock _leftLabel = new(), _rightLabel = new();
    private readonly CheckBox _syncScroll;
    private bool _diffMode;

    public CompareWindow(Window owner, string left, string right)
    {
        _left = left;
        _right = right;
        Owner = owner;
        Title = "Compare";
        Background = Theme.Brush("Bg1");
        var wa = SystemParameters.WorkArea;
        Width = Math.Min(1700, wa.Width * 0.9);
        Height = Math.Min(1000, wa.Height * 0.88);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SourceInitialized += (_, _) => Shell.UseDarkTitleBar(this);

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
        _syncScroll.Checked += (_, _) => _leftPane.RequestScrollReport(); // line the right side up with the left
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
        Grid.SetColumn(_rightPane, 2);
        _sideBySide.Children.Add(_leftPane);
        _sideBySide.Children.Add(splitter);
        _sideBySide.Children.Add(_rightPane);
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
        Closed += (_, _) => { _leftPane.DisposeWeb(); _rightPane.DisposeWeb(); _diffPane.DisposeWeb(); };

        bool bothText = FileKinds.IsTextLike(FileKinds.KindFor(left, false)) && FileKinds.IsTextLike(FileKinds.KindFor(right, false));
        _diffMode = bothText;
        Reload();
    }

    /// <summary>Whether scrolling one side scrolls the other (the "Sync scrolling" checkbox; on by default).</summary>
    public bool SyncScrolling { get => _syncScroll.IsChecked == true; set => _syncScroll.IsChecked = value; }

    internal PreviewPane LeftPane => _leftPane;
    internal PreviewPane RightPane => _rightPane;
    internal CheckBox SyncScrollCheck => _syncScroll;

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
        _leftLabel.Text = Path.GetFileName(_left);
        _leftLabel.ToolTip = _left;
        _rightLabel.Text = Path.GetFileName(_right);
        _rightLabel.ToolTip = _right;
        Title = $"Compare – {Path.GetFileName(_left)}  ↔  {Path.GetFileName(_right)}";
        _btnSide.Background = _diffMode ? Theme.Brush("Bg2") : Theme.Brush("Sel");
        _btnDiff.Background = _diffMode ? Theme.Brush("Sel") : Theme.Brush("Bg2");
        _syncScroll.IsEnabled = !_diffMode;
        _syncScroll.ToolTip = _diffMode ? "The text diff always scrolls both files together" : "Scroll both files together";

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
    }

    public void Release(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        _leftPane.ReleaseIfShowing(list);
        _rightPane.ReleaseIfShowing(list);
    }
}
