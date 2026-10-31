using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fyltrix.Core;

namespace Fyltrix.UI;

/// <summary>The Fyltrix logo (Assets/Fyltrix.ico, made by tools/LogoGen) for windows, the notification area and the About box.</summary>
public static class AppIcon
{
    private static BitmapFrame? _window;

    private static Stream? Open() => typeof(AppIcon).Assembly.GetManifestResourceStream("Assets/Fyltrix.ico");

    /// <summary>The multi-size icon for Window.Icon (Windows picks the right size for the title bar, taskbar and Alt+Tab).</summary>
    public static ImageSource? ForWindow()
    {
        if (_window != null) return _window;
        using var s = Open();
        if (s == null) return null;
        var ms = new MemoryStream();
        s.CopyTo(ms);
        ms.Position = 0;
        _window = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return _window;
    }

    /// <summary>The largest image in the icon (256 px), for the About box.</summary>
    public static BitmapSource? Large()
    {
        using var s = Open();
        if (s == null) return null;
        var dec = new IconBitmapDecoder(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return dec.Frames.OrderByDescending(f => f.PixelWidth).First();
    }

    /// <summary>A notification-area icon at the system's small-icon size (DPI aware).</summary>
    public static System.Drawing.Icon ForTray()
    {
        using var s = Open();
        if (s == null) return System.Drawing.SystemIcons.Application;
        return new System.Drawing.Icon(s, System.Windows.Forms.SystemInformation.SmallIconSize);
    }

    private static readonly Dictionary<int, IntPtr> Handles = new();

    /// <summary>
    /// Gives the window's class its icon too, at the sizes Windows asks for at the window's DPI. WPF only answers WM_GETICON;
    /// its window class has no icon. When the UI thread is busy just after the window appears (opening tabs, starting
    /// previews), the taskbar's WM_GETICON times out and it falls back to the class icon — which was empty, so the button
    /// showed a generic icon until something (a click, minimise/restore) made the taskbar ask again.
    /// Call from SourceInitialized (before the window is shown).
    /// </summary>
    public static void ApplyToHwnd(Window window)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            uint dpi = GetDpiForWindow(hwnd);
            if (dpi == 0) dpi = 96;
            IntPtr big = Handle(GetSystemMetricsForDpi(SM_CXICON, dpi)), small = Handle(GetSystemMetricsForDpi(SM_CXSMICON, dpi));
            if (big == IntPtr.Zero || small == IntPtr.Zero) return;
            SetClassLongPtr(hwnd, GCLP_HICON, big);
            SetClassLongPtr(hwnd, GCLP_HICONSM, small);
            SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_BIG, big);
            SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_SMALL, small);
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException or EntryPointNotFoundException) { Diagnostics.Log("Window icon", ex); }
    }

    /// <summary>An icon handle of the given size, made once from the embedded .ico and kept for the life of the process
    /// (the class and window keep using it, so it must never be destroyed).</summary>
    private static IntPtr Handle(int size)
    {
        if (size <= 0) size = 32;
        lock (Handles)
        {
            if (Handles.TryGetValue(size, out var h)) return h;
            using var s = Open();
            if (s == null) return IntPtr.Zero;
            using var icon = new System.Drawing.Icon(s, size, size);
            h = CopyIcon(icon.Handle); // our own copy: the Icon object's handle dies with it
            Handles[size] = h;
            return h;
        }
    }

    internal static IntPtr ClassIcon(Window window, bool small) =>
        GetClassLongPtr(new System.Windows.Interop.WindowInteropHelper(window).Handle, small ? GCLP_HICONSM : GCLP_HICON);

    private const int GCLP_HICON = -14, GCLP_HICONSM = -34, SM_CXICON = 11, SM_CXSMICON = 49, WM_SETICON = 0x0080, ICON_SMALL = 0, ICON_BIG = 1;

    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW")] private static extern IntPtr SetClassLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] private static extern IntPtr GetClassLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern IntPtr CopyIcon(IntPtr icon);

    public static void ShowAbout(Window owner)
    {
        var d = new DialogWindow(owner, $"About {ProductBranding.Name}", 460);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Image { Source = Large(), Width = 72, Height = 72, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top });
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = ProductBranding.Name, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Theme.Brush("Fg1") });
        text.Children.Add(Dialogs.Note($"Version {Diagnostics.Version}"));
        text.Children.Add(Dialogs.Note("A tabbed file explorer with real previews. Runs without admin rights."));
        text.Children.Add(Dialogs.Note($"Settings and logs: {AppSettings.Folder}"));
        row.Children.Add(text);
        d.AddContent(row);
        d.AddButton("Open log folder", () => Shell.Open(AppSettings.Folder));
        d.AddButton("Close", () => d.Close(), primary: true, cancel: true);
        d.ShowDialog();
    }
}
