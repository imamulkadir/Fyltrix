using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Fyltrix.UI;

namespace Fyltrix.Tests;

/// <summary>
/// The title bar is checked the way Windows itself asks the window: WM_NCHITTEST at screen points decides what dragging, resizing,
/// double-click and Snap Layouts do. The window stays off-screen and is never maximised for real (that would show it).
/// </summary>
[Collection("UI")]
public class TitleBarUiTests
{
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

    private const int WM_NCHITTEST = 0x84, WM_NCMOUSEMOVE = 0xA0, WM_NCLBUTTONDOWN = 0xA1, WM_NCLBUTTONUP = 0xA2, WM_NCMOUSELEAVE = 0x2A2;
    private const int HTCLIENT = 1, HTCAPTION = 2, HTMAXBUTTON = 9, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTBOTTOMRIGHT = 17;

    private static IntPtr Pack(Point screen) => (IntPtr)(((int)Math.Round(screen.Y) << 16) | ((int)Math.Round(screen.X) & 0xFFFF));

    /// <summary>What Windows would do at this point (x, y in window DIPs from the top-left of the window).</summary>
    private static int HitTest(Window w, double x, double y) =>
        (int)SendMessage(new WindowInteropHelper(w).Handle, WM_NCHITTEST, IntPtr.Zero, Pack(((UIElement)w.Content).PointToScreen(new Point(x, y))));

    private static int HitTest(Window w, FrameworkElement e) => HitTest(w, Center(w, e));

    private static int HitTest(Window w, Point p) => HitTest(w, p.X, p.Y);

    private static Point Center(Window w, FrameworkElement e) => e.TranslatePoint(new Point(e.ActualWidth / 2, e.ActualHeight / 2), (UIElement)w.Content);

    private static Button Caption(MainWindow w, string name) =>
        VisualTree.FindDescendants<Button>(w).Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == name);

    [Fact]
    public void Tabs_and_window_buttons_share_one_row_with_native_drag_resize_and_snap()
    {
        using var t = new TempDir();
        t.File("a.txt");
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t.Path);
            try
            {
                Sta.DoEvents(200);
                Assert.NotNull(WindowChrome.GetWindowChrome(w));
                Assert.Equal(WindowStyle.SingleBorderWindow, w.WindowStyle); // the native frame stays (snap, Win+arrows, Alt+Space, shadow)

                var min = Caption(w, "Minimize");
                var close = Caption(w, "Close");
                var max = w.MaxButton;
                Assert.Equal("Maximize", System.Windows.Automation.AutomationProperties.GetName(max));
                var tabHeader = VisualTree.FindDescendants<Border>(w).Last(b => System.Windows.Automation.AutomationProperties.GetName(b).StartsWith("Tab: "));
                var root = (UIElement)w.Content;

                // one row: tab, + and the window buttons have the same vertical band at the very top
                double Top(FrameworkElement e) => e.TranslatePoint(new Point(0, 0), root).Y;
                double Bottom(FrameworkElement e) => e.TranslatePoint(new Point(0, e.ActualHeight), root).Y;
                Assert.Equal(0, Top(close), 1);
                Assert.True(Bottom(tabHeader) <= MainWindow.TitleBarHeight + 0.5 && Top(tabHeader) > 0);
                Assert.True(Center(w, w.NewTabButton).Y < MainWindow.TitleBarHeight);
                // window buttons at the far right, + right after the last tab
                Assert.Equal(root.RenderSize.Width, close.TranslatePoint(new Point(close.ActualWidth, 0), root).X, 1);
                var tabRight = tabHeader.TranslatePoint(new Point(tabHeader.ActualWidth, 0), root).X;
                var plusLeft = w.NewTabButton.TranslatePoint(new Point(0, 0), root).X;
                Assert.InRange(plusLeft - tabRight, 0, 12);

                // what Windows does at each point
                Assert.Equal(HTMAXBUTTON, HitTest(w, max));                      // Snap Layouts flyout on hover (Windows 11)
                Assert.Equal(HTCLIENT, HitTest(w, min));
                Assert.Equal(HTCLIENT, HitTest(w, close));
                Assert.Equal(HTCLIENT, HitTest(w, tabHeader));
                Assert.Equal(HTCLIENT, HitTest(w, w.NewTabButton));
                var gap = new Point(min.TranslatePoint(new Point(0, 0), root).X - 20, 20);
                Assert.Equal(HTCAPTION, HitTest(w, gap));                        // empty title bar: drag, double-click maximises, right-click system menu
                Assert.Equal(HTCAPTION, HitTest(w, plusLeft + 60, 20));
                Assert.Equal(HTCLIENT, HitTest(w, 300, MainWindow.TitleBarHeight + 20)); // address bar row below is ordinary content
                Assert.Equal(HTLEFT, HitTest(w, 1, 300));                        // resizing from every edge and corner
                Assert.Equal(HTRIGHT, HitTest(w, root.RenderSize.Width - 2, 300));
                Assert.Equal(HTTOP, HitTest(w, gap.X, 1));
                Assert.Equal(HTTOPLEFT, HitTest(w, 1, 1));
                Assert.Equal(HTBOTTOMRIGHT, HitTest(w, root.RenderSize.Width - 2, root.RenderSize.Height - 2));

                // the maximise button acts on Windows' non-client mouse messages (it is HTMAXBUTTON, so WPF never sees the click)
                int toggles = 0;
                w.ToggleMaximizeForTest = () => toggles++;
                var hwnd = new WindowInteropHelper(w).Handle;
                var at = Pack(((UIElement)w.Content).PointToScreen(Center(w, max)));
                SendMessage(hwnd, WM_NCMOUSEMOVE, (IntPtr)HTMAXBUTTON, at);
                var face = (Border)max.Content;
                Assert.Equal(((SolidColorBrush)Theme.Brush("Hover")).Color, ((SolidColorBrush)face.Background).Color);
                Sta.Render(w, "titlebar-max-hover");
                SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTMAXBUTTON, at);
                Assert.Equal(0, toggles);
                SendMessage(hwnd, WM_NCLBUTTONUP, (IntPtr)HTMAXBUTTON, at);
                Assert.Equal(1, toggles);
                SendMessage(hwnd, WM_NCLBUTTONUP, (IntPtr)HTMAXBUTTON, at); // a release without a press (drag in from elsewhere) does nothing
                Assert.Equal(1, toggles);
                SendMessage(hwnd, WM_NCMOUSELEAVE, IntPtr.Zero, IntPtr.Zero);
                Assert.Equal(Colors.Transparent, ((SolidColorBrush)face.Background).Color);
                // keyboard / UI Automation still works through the button itself
                max.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Equal(2, toggles);

                Sta.Render(w, "titlebar");
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Many_tabs_scroll_and_keep_the_new_tab_button_and_a_drag_area()
    {
        using var t = new TempDir();
        t.File("a.txt");
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t.Path);
            try
            {
                Assert.Equal(Visibility.Collapsed, w.OverflowButton.Visibility);
                for (int i = 0; i < 20; i++) w.AddTabForTest(t.Dir($"Folder number {i:00}"));
                Sta.WaitUntil(() => w.OverflowButton.Visibility == Visibility.Visible, "overflow button");
                Sta.DoEvents(200);
                var root = (UIElement)w.Content;
                var min = Caption(w, "Minimize");
                double minLeft = min.TranslatePoint(new Point(0, 0), root).X;
                double plusRight = w.NewTabButton.TranslatePoint(new Point(w.NewTabButton.ActualWidth, 0), root).X;
                double overflowRight = w.OverflowButton.TranslatePoint(new Point(w.OverflowButton.ActualWidth, 0), root).X;
                Assert.True(plusRight < minLeft && overflowRight < minLeft, "+ and the tab list stay visible");
                // the active (last) tab is scrolled fully into view, ending at the + button
                var last = VisualTree.FindDescendants<Border>(w).Last(b => System.Windows.Automation.AutomationProperties.GetName(b).StartsWith("Tab: "));
                double lastRight = last.TranslatePoint(new Point(last.ActualWidth, 0), root).X;
                double plusLeft = w.NewTabButton.TranslatePoint(new Point(0, 0), root).X;
                Assert.InRange(plusLeft - lastRight, -0.5, 12);
                Assert.True(minLeft - overflowRight >= 40, "a strip of title bar stays free for dragging");
                Assert.Equal(HTCAPTION, HitTest(w, (overflowRight + minLeft) / 2, 20));
                Assert.Equal(HTCLIENT, HitTest(w, w.NewTabButton));
                Assert.Equal(HTCLIENT, HitTest(w, w.OverflowButton));
                Sta.Render(w, "titlebar-overflow");
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Maximised_inset_matches_the_off_screen_frame_at_the_window_DPI()
    {
        using var t = new TempDir();
        t.File("a.txt");
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t.Path);
            try
            {
                var inset = w.MaximizedInset();
                // the sizing frame + padded border (8 px at 100 % on Windows 10/11); always positive and the same on all sides
                Assert.InRange(inset.Left, 4, 20);
                Assert.Equal(inset.Left, inset.Right);
                Assert.Equal(inset.Top, inset.Bottom);
            }
            finally { w.Close(); }
        });
    }
}
