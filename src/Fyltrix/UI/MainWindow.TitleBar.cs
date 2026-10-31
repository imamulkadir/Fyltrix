using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace Fyltrix.UI;

/// <summary>
/// Tabs in the title bar, like Windows 11 File Explorer. The window keeps its native frame through WPF's WindowChrome, so
/// dragging, edge/corner resizing, Aero Snap, Win+arrows, Alt+Space, double-click to maximise and the system menu are all
/// Windows' own. On top of that: Snap Layouts on the maximise button (it answers WM_NCHITTEST with HTMAXBUTTON, as
/// Microsoft documents for apps that draw their own caption buttons) and an inset while maximised (a maximised window's
/// frame lies off-screen, and with the frame drawn as client area the content would be cut off at the edges).
/// </summary>
public sealed partial class MainWindow
{
    internal const double TitleBarHeight = 40;

    private readonly Border _frame = new();
    private readonly ScrollViewer _tabScroll = new();
    private Button _maxButton = null!, _overflowButton = null!;
    private Button _newTabButton = null!;
    private bool _maxHover, _maxPressed;
    private WindowChrome? _windowChrome;

    private static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    private UIElement BuildTitleBar()
    {
        var bar = new Grid { Height = TitleBarHeight, Background = Theme.Brush("Bg0") };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // tabs, then + right after the last tab, then the overflow list; whatever is left of the row is caption (drag area)
        _tabScroll.Content = _tabStrip;
        _tabScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
        _tabScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _tabScroll.Focusable = false;
        _tabScroll.PreviewMouseWheel += (_, e) => { _tabScroll.ScrollToHorizontalOffset(_tabScroll.HorizontalOffset - e.Delta / 3.0); e.Handled = true; };
        _tabScroll.ScrollChanged += (_, _) => UpdateTabOverflow();
        _tabStrip.VerticalAlignment = VerticalAlignment.Bottom;

        _newTabButton = Theme.SmallButton("", "", "New tab (Ctrl+T)");
        _newTabButton.Click += (_, _) => OpenNewTab();
        _newTabButton.VerticalAlignment = VerticalAlignment.Center;
        _newTabButton.Margin = new Thickness(4, 2, 0, 0);
        AutomationProperties.SetName(_newTabButton, "New tab");

        _overflowButton = Theme.SmallButton("", "", "All tabs");
        _overflowButton.Click += (_, _) => ShowTabList();
        _overflowButton.VerticalAlignment = VerticalAlignment.Center;
        _overflowButton.Margin = new Thickness(2, 2, 0, 0);
        _overflowButton.Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(_overflowButton, "All tabs");

        var tabs = new TabBarPanel { Margin = new Thickness(8, 0, 0, 0), ReservedDragWidth = 48 };
        tabs.Children.Add(_tabScroll);
        tabs.Children.Add(_newTabButton);
        tabs.Children.Add(_overflowButton);
        foreach (UIElement e in tabs.Children) WindowChrome.SetIsHitTestVisibleInChrome(e, true);
        bar.Children.Add(tabs);

        // caption buttons (Windows 11 sizes: 46 × the bar height; glyphs from Segoe Fluent Icons / MDL2)
        var caption = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumn(caption, 1);
        var min = CaptionButton("", "Minimize", () => SystemCommands.MinimizeWindow(this));
        _maxButton = CaptionButton("", "Maximize", ToggleMaximize);
        var close = CaptionButton("", "Close", () => SystemCommands.CloseWindow(this), isClose: true);
        caption.Children.Add(min);
        caption.Children.Add(_maxButton);
        caption.Children.Add(close);
        bar.Children.Add(caption);

        StateChanged += (_, _) => UpdateWindowFrame();
        DpiChanged += (_, _) => UpdateWindowFrame();
        MouseMove += (_, _) => { if (_maxHover) SetMaxHover(false); }; // the pointer is in the client area, so not on the maximise button
        SourceInitialized += (_, _) =>
        {
            HwndSource.FromHwnd(Hwnd)?.AddHook(TitleBarHook);
            if (IsWindows11)
            {
                int round = 2; // DWMWCP_ROUND
                Core.Native.DwmSetWindowAttribute(Hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
            }
            UpdateWindowFrame();
        };
        return bar;
    }

    /// <summary>Applies the custom frame (called from the constructor, and again after the preview's full screen ends).</summary>
    private void ApplyWindowChrome()
    {
        _windowChrome ??= new WindowChrome
        {
            CaptionHeight = TitleBarHeight,
            ResizeBorderThickness = SystemParameters.WindowResizeBorderThickness,
            // a 1-pixel strip of DWM frame at the bottom keeps the system drop shadow (it is painted over by the status bar)
            GlassFrameThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
            NonClientFrameEdges = NonClientFrameEdges.None,
        };
        WindowChrome.SetWindowChrome(this, _windowChrome);
    }

    private void RemoveWindowChrome() => WindowChrome.SetWindowChrome(this, null);

    private Button CaptionButton(string glyph, string name, Action onClick, bool isClose = false)
    {
        var icon = new TextBlock
        {
            Text = glyph, FontFamily = new FontFamily(Theme.IconFont), FontSize = 10, Foreground = Theme.Brush("Fg1"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var face = new Border { Background = Brushes.Transparent, Child = icon };
        var b = new Button
        {
            Content = face, Width = 46, Height = TitleBarHeight, Focusable = false, ToolTip = name,
            Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) },
        };
        AutomationProperties.SetName(b, name);
        WindowChrome.SetIsHitTestVisibleInChrome(b, true);
        var hover = isClose ? new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)) : Theme.Brush("Hover");
        var pressed = isClose ? new SolidColorBrush(Color.FromRgb(0xB2, 0x27, 0x1A)) : Theme.Brush("Bg3");
        void Paint()
        {
            bool over = b.IsMouseOver || (b == _maxButton && _maxHover);
            bool down = b.IsPressed || (b == _maxButton && _maxPressed);
            face.Background = down ? pressed : over ? hover : Brushes.Transparent;
            icon.Foreground = isClose && over ? Brushes.White : Theme.Brush(IsActive ? "Fg1" : "Fg3");
        }
        b.MouseEnter += (_, _) => Paint();
        b.MouseLeave += (_, _) => Paint();
        b.PreviewMouseLeftButtonDown += (_, _) => Paint();
        b.PreviewMouseLeftButtonUp += (_, _) => Paint();
        Activated += (_, _) => Paint();
        Deactivated += (_, _) => Paint();
        b.Tag = (Action)Paint;
        b.Click += (_, _) => onClick();
        return b;
    }

    private void RepaintMax() => (_maxButton.Tag as Action)?.Invoke();

    private void SetMaxHover(bool on)
    {
        if (_maxHover == on) return;
        _maxHover = on;
        if (!on) _maxPressed = false;
        RepaintMax();
    }

    internal Button MaxButton => _maxButton;
    internal Button OverflowButton => _overflowButton;
    internal Button NewTabButton => _newTabButton;
    internal void AddTabForTest(string path) => AddTab(path, activate: true);

    /// <summary>Tests: what maximise/restore does (the real one changes the window state on screen).</summary>
    internal Action? ToggleMaximizeForTest;

    private void ToggleMaximize()
    {
        if (ToggleMaximizeForTest != null) { ToggleMaximizeForTest(); return; }
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    /// <summary>Glyph and name of the maximise button, and the inset that keeps a maximised window's content on screen.</summary>
    private void UpdateWindowFrame()
    {
        bool max = WindowState == WindowState.Maximized && _fullScreen == null;
        _frame.Padding = max ? MaximizedInset() : new Thickness(0);
        // Windows 11 draws a thin border around the window itself; Windows 10 doesn't, so draw one while not maximised
        _frame.BorderThickness = !IsWindows11 && !max && _fullScreen == null ? new Thickness(1) : new Thickness(0);
        _frame.BorderBrush = Theme.Brush("Line");
        if (_maxButton == null) return;
        ((TextBlock)((Border)_maxButton.Content).Child).Text = max ? "" : "";
        var name = max ? "Restore" : "Maximize";
        _maxButton.ToolTip = name;
        AutomationProperties.SetName(_maxButton, name);
    }

    /// <summary>The part of a maximised window that lies outside the monitor: the sizing frame plus padding, at this window's DPI.</summary>
    internal Thickness MaximizedInset()
    {
        var hwnd = Hwnd;
        uint dpi = hwnd != IntPtr.Zero ? GetDpiForWindow(hwnd) : 0;
        if (dpi == 0) dpi = 96;
        double scale = dpi / 96.0;
        double x = (GetSystemMetricsForDpi(SM_CXSIZEFRAME, dpi) + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi)) / scale;
        double y = (GetSystemMetricsForDpi(SM_CYSIZEFRAME, dpi) + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi)) / scale;
        return new Thickness(x, y, x, y);
    }

    // ------------------------------------------------------------------ Snap Layouts on the maximise button

    private IntPtr TitleBarHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                if (IsOverMaxButton(lParam)) { handled = true; return (IntPtr)HTMAXBUTTON; }
                break;
            case WM_NCMOUSEMOVE:
                SetMaxHover(wParam.ToInt32() == HTMAXBUTTON);
                if (_maxHover)
                {
                    var tme = new TRACKMOUSEEVENT { cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = TME_LEAVE | TME_NONCLIENT, hwndTrack = hwnd };
                    TrackMouseEvent(ref tme);
                }
                break;
            case WM_NCMOUSELEAVE:
                SetMaxHover(false);
                break;
            case WM_NCLBUTTONDOWN:
            case WM_NCLBUTTONDBLCLK:
                if (wParam.ToInt32() == HTMAXBUTTON)
                {
                    // handled here, or DefWindowProc would draw and track a classic Windows 95-style button
                    _maxPressed = true;
                    RepaintMax();
                    handled = true;
                    return IntPtr.Zero;
                }
                break;
            case WM_NCLBUTTONUP:
                if (wParam.ToInt32() == HTMAXBUTTON)
                {
                    bool click = _maxPressed;
                    _maxPressed = false;
                    RepaintMax();
                    handled = true;
                    if (click) ToggleMaximize();
                    return IntPtr.Zero;
                }
                _maxPressed = false;
                break;
        }
        return IntPtr.Zero;
    }

    private bool IsOverMaxButton(IntPtr lParam)
    {
        if (_maxButton == null || !_maxButton.IsVisible || _fullScreen != null || PresentationSource.FromVisual(_maxButton) == null) return false;
        int x = unchecked((short)(long)lParam), y = unchecked((short)((long)lParam >> 16));
        var topLeft = _maxButton.PointToScreen(new Point(0, 0));
        var bottomRight = _maxButton.PointToScreen(new Point(_maxButton.ActualWidth, _maxButton.ActualHeight));
        return x >= topLeft.X && x < bottomRight.X && y >= topLeft.Y && y < bottomRight.Y;
    }

    // ------------------------------------------------------------------ tab overflow

    private void UpdateTabOverflow()
    {
        bool overflow = _tabScroll.ExtentWidth > _tabScroll.ViewportWidth + 0.5;
        var v = overflow ? Visibility.Visible : Visibility.Collapsed;
        if (_overflowButton.Visibility != v) _overflowButton.Visibility = v;
    }

    private void ShowTabList()
    {
        var menu = new ContextMenu { PlacementTarget = _overflowButton, Placement = PlacementMode.Bottom };
        foreach (var t in _tabs)
        {
            var tab = t;
            var mi = Theme.MenuItem(tab.View.Title, tab == _active ? "" : null, () => ActivateTab(tab));
            mi.ToolTip = tab.Header.ToolTip;
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    /// <summary>Tabs, then the + and overflow buttons right after them. The tabs get what is left of the row (minus a strip kept free for
    /// dragging the window) and scroll when they don't fit, so + is always visible next to the last tab.</summary>
    private sealed class TabBarPanel : Panel
    {
        public double ReservedDragWidth { get; set; }

        protected override Size MeasureOverride(Size available)
        {
            double fixedWidth = 0, height = 0;
            for (int i = 1; i < InternalChildren.Count; i++)
            {
                InternalChildren[i].Measure(new Size(double.PositiveInfinity, available.Height));
                fixedWidth += InternalChildren[i].DesiredSize.Width;
                height = Math.Max(height, InternalChildren[i].DesiredSize.Height);
            }
            double room = double.IsInfinity(available.Width) ? double.PositiveInfinity : Math.Max(0, available.Width - fixedWidth - ReservedDragWidth);
            if (InternalChildren.Count > 0)
            {
                InternalChildren[0].Measure(new Size(room, available.Height));
                height = Math.Max(height, InternalChildren[0].DesiredSize.Height);
            }
            double width = InternalChildren.Cast<UIElement>().Sum(c => c.DesiredSize.Width);
            return new Size(double.IsInfinity(available.Width) ? width : Math.Min(width, available.Width), double.IsInfinity(available.Height) ? height : available.Height);
        }

        protected override Size ArrangeOverride(Size final)
        {
            double x = 0;
            foreach (UIElement c in InternalChildren)
            {
                double w = c.DesiredSize.Width;
                c.Arrange(new Rect(x, 0, w, final.Height));
                x += w;
            }
            return final;
        }
    }

    // ------------------------------------------------------------------ Win32

    private const int WM_NCHITTEST = 0x0084, WM_NCMOUSEMOVE = 0x00A0, WM_NCLBUTTONDOWN = 0x00A1, WM_NCLBUTTONUP = 0x00A2, WM_NCLBUTTONDBLCLK = 0x00A3,
        WM_NCMOUSELEAVE = 0x02A2, HTMAXBUTTON = 9, TME_LEAVE = 0x2, TME_NONCLIENT = 0x10,
        SM_CXSIZEFRAME = 32, SM_CYSIZEFRAME = 33, SM_CXPADDEDBORDER = 92;

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT { public int cbSize; public uint dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }

    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT e);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
