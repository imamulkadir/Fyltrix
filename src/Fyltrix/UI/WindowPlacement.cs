using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Fyltrix.UI;

/// <summary>
/// Puts a popup window over its owner, in physical pixels on the owner's own monitor. WPF's Left/Top are in DIPs whose scale
/// depends on the monitor, and SystemParameters.WorkArea is always the primary monitor, so positions worked out that way land
/// on the primary screen (or off by the scale difference) when Fyltrix sits on a second monitor with different scaling.
/// </summary>
internal static class WindowPlacement
{
    /// <summary>Places <paramref name="w"/> over <paramref name="owner"/> once its handle exists: centred horizontally and
    /// <paramref name="top"/> DIPs below the owner's top edge (centred vertically when null). Call before Show.</summary>
    public static void OverOwner(Window w, Window owner, double? top)
    {
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        void Place()
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            var ownerHwnd = new WindowInteropHelper(owner).Handle;
            if (hwnd == IntPtr.Zero || ownerHwnd == IntPtr.Zero) return;
            var to = Position(ownerHwnd, w.ActualHeight > 0 ? w.ActualHeight : w.Height, w.ActualWidth > 0 ? w.ActualWidth : w.Width, top);
            SetWindowPos(hwnd, IntPtr.Zero, to.X, to.Y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }
        w.SourceInitialized += (_, _) => Place();
        // moving to a monitor with another scale resizes the window; centre it again at its new size
        w.DpiChanged += (_, _) => w.Dispatcher.BeginInvoke(Place, System.Windows.Threading.DispatcherPriority.Loaded);
        if (top == null) w.ContentRendered += (_, _) => Place(); // SizeToContent: the height is known only now
    }

    /// <summary>The owner's visible rectangle in physical pixels: the work area of its monitor when maximised (a maximised
    /// window hangs over the edges), otherwise its frame without the invisible resize borders.</summary>
    internal static RECT OwnerRect(IntPtr owner)
    {
        if (IsZoomed(owner)) return WorkArea(owner);
        if (DwmGetWindowAttribute(owner, DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<RECT>()) == 0 && r.Right > r.Left) return r;
        GetWindowRect(owner, out r);
        return r;
    }

    internal static RECT WorkArea(IntPtr hwnd)
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref mi);
        return mi.rcWork;
    }

    private static (int X, int Y) Position(IntPtr ownerHwnd, double heightDip, double widthDip, double? topDip)
    {
        var r = OwnerRect(ownerHwnd);
        uint dpi = GetDpiForWindow(ownerHwnd);
        double scale = (dpi == 0 ? 96 : dpi) / 96.0;
        int width = (int)Math.Round((double.IsNaN(widthDip) ? 0 : widthDip) * scale);
        int height = (int)Math.Round((double.IsNaN(heightDip) ? 0 : heightDip) * scale);
        int x = r.Left + (r.Right - r.Left - width) / 2;
        int y = topDip is { } t ? r.Top + (int)Math.Round(t * scale) : r.Top + (r.Bottom - r.Top - height) / 2;
        // keep it on the owner's monitor
        var work = WorkArea(ownerHwnd);
        x = Math.Max(work.Left, Math.Min(x, work.Right - width));
        y = Math.Max(work.Top, Math.Min(y, work.Bottom - Math.Max(height, 40)));
        return (x, y);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
}
