using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Fyltrix.Core;
using Fyltrix.UI;

namespace Fyltrix.PlacementProbe;

/// <summary>
/// Opens a Fyltrix popup over an owner window on the secondary monitor and writes where everything ended up (physical
/// pixels) to a JSON file. The owner and the palette are DWM-cloaked: invisible and never activated, while Windows still gives
/// them their real position and DPI.
///   Fyltrix.PlacementProbe palette|dialog|tool|compare normal|max &lt;work folder&gt; &lt;result.json&gt;
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        MainWindow.ClipboardFactory = () => new ProbeClipboard();
        if (args.Length >= 3 && args[0] == "bench") return Bench.Run(args[1], args[2], System.Diagnostics.Process.GetCurrentProcess().StartTime);
        if (args.Length >= 4 && args[0] == "render") return RenderTheme(Enum.Parse<ThemeMode>(args[1]), args[2], args[3]);
        var result = new Dictionary<string, object?>();
        try
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher)); // as App.Main does
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Theme.Apply(app);
            Run(args[0], args[1] == "max", args[2], result, args[3]);
        }
        catch (Exception ex) { result["error"] = ex.ToString(); }
        File.WriteAllText(args[3], JsonSerializer.Serialize(result));
        return 0;
    }

    private sealed class ProbeClipboard : IFileClipboard
    {
        public IDataObject? Read() => null;
        public System.Windows.Media.Imaging.BitmapSource? ReadImage() => null;
        public void Write(IDataObject data) => throw new InvalidOperationException("Probe clipboard is read-only.");
        public void Clear() => throw new InvalidOperationException("Probe clipboard is read-only.");
    }

    private static void Run(string scenario, bool maximized, string work, Dictionary<string, object?> result, string outFile)
    {
        var monitors = Monitors();
        result["monitors"] = monitors.Select(m => new { bounds = m.Bounds.ToArray(), work = m.Work.ToArray(), m.Primary, m.Dpi }).ToList();
        if (monitors.Count < 2 && scenario != "paletteclick") { result["skipped"] = "one monitor"; return; }
        var mon = monitors.FirstOrDefault(m => !m.Primary);
        if (monitors.Count < 2) mon = monitors[0];

        Window owner;
        if (scenario is "palette" or "paletteclick")
        {
            AppSettings.Folder = Path.Combine(work, "_settings");
            owner = new MainWindow(new AppSettings { PreviewVisible = false }, work);
        }
        else owner = new Window { Title = "Owner" };
        var at = new Rect(mon.Work.Left + 150, mon.Work.Top + 100,
            mon.Work.Left + 150 + Math.Min(1300, mon.Work.Width - 300), mon.Work.Top + 100 + Math.Min(800, mon.Work.Height - 200));
        ShowCloaked(owner, at);
        if (maximized) MaximizeQuietly(owner, mon.Work);
        var ownerHwnd = new WindowInteropHelper(owner).Handle;
        result["ownerZoomed"] = IsZoomed(ownerHwnd);
        result["ownerHwnd"] = $"0x{ownerHwnd:X}";
        result["ownerDpi"] = GetDpiForWindow(ownerHwnd);
        result["owner"] = WindowRect(ownerHwnd).ToArray();
        result["visible"] = (maximized ? mon.Work : WindowRect(ownerHwnd)).ToArray();
        result["monitor"] = mon.Bounds.ToArray();
        result["monitorWork"] = mon.Work.ToArray();

        if (scenario == "paletteclick") { PaletteClickAway((MainWindow)owner, result); owner.Close(); return; }

        Window p = scenario switch
        {
            "palette" => new CommandPalette(owner, _ => Array.Empty<PaletteEntry>()),
            "dialog" => Dialog(owner),
            "tool" => new ToolWindow(owner, "Tool", 900, 600),
            "compare" => new CompareWindow(owner, Write(work, "left.txt"), Write(work, "right.txt")),
            _ => throw new ArgumentException("unknown scenario " + scenario),
        };
        p.SourceInitialized += (_, _) => Cloak(new WindowInteropHelper(p).Handle);
        p.ShowActivated = false;
        p.Show();
        DoEvents(800);
        var h = new WindowInteropHelper(p).Handle;
        result["popup"] = WindowRect(h).ToArray();
        result["popupDpi"] = GetDpiForWindow(h);
        p.Close();
        owner.Close();
    }

    /// <summary>The three ways the palette closes — a click on the app outside it, Esc, running a command — each must leave the
    /// app active. Activation is moved with SetActiveWindow inside this process: no foreground rights needed, nothing outside
    /// the process is touched. Records the active window after each.</summary>
    private static void PaletteClickAway(MainWindow owner, Dictionary<string, object?> result)
    {
        var log = new List<string>();
        var ownerH = new WindowInteropHelper(owner).Handle;
        IntPtr paletteH = IntPtr.Zero;
        string Name(IntPtr h) => h == ownerH ? "app" : h == IntPtr.Zero ? "none" : h == paletteH ? "palette" : $"other 0x{h:X}";
        owner.Activated += (_, _) => log.Add("app activated");
        owner.Deactivated += (_, _) => log.Add("app deactivated");
        SetActiveWindow(ownerH);
        DoEvents(300);

        CommandPalette Open()
        {
            var p = new CommandPalette(owner, _ => new[] { new PaletteEntry("Harmless test command", "Test", () => log.Add("command ran")) });
            p.SourceInitialized += (_, _) => { paletteH = new WindowInteropHelper(p).Handle; Cloak(paletteH); };
            p.Closed += (_, _) => log.Add("palette closed");
            p.Show();
            for (int i = 0; i < 30 && GetActiveWindow() != paletteH; i++) DoEvents(100); // a busy machine is slower
            DoEvents(200);
            log.Add("palette open, active: " + Name(GetActiveWindow()));
            return p;
        }
        string After(string what, CommandPalette p)
        {
            for (int i = 0; i < 30 && p.IsVisible; i++) DoEvents(100);
            DoEvents(500); // anything that would still move activation has happened by now
            var active = Name(GetActiveWindow());
            log.Add($"after {what}: active {active}, palette {(p.IsVisible ? "open" : "closed")}");
            return active + (p.IsVisible ? " (palette still open)" : "");
        }

        var p1 = Open();
        log.Add("-- click on the app --");
        SetActiveWindow(ownerH);
        result["afterClickAway"] = After("click away", p1);

        var p2 = Open();
        log.Add("-- Esc --");
        var target = (IInputElement)Keyboard.FocusedElement ?? p2;
        var source = PresentationSource.FromVisual(p2)!;
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        result["afterEscape"] = After("Esc", p2);

        var p3 = Open();
        log.Add("-- run a command --");
        p3.RunSelected();
        result["afterCommand"] = After("command", p3);
        result["commandRan"] = log.Contains("command ran");
        result["appMinimized"] = IsIconic(ownerH);
        result["log"] = log.ToList();
    }

    /// <summary>The main window in a theme (chosen at process start, as Fyltrix does), saved as a PNG.</summary>
    private static int RenderTheme(ThemeMode mode, string folder, string png)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Theme.Use(mode);
        Theme.Apply(app);
        AppSettings.Folder = Path.Combine(folder, "_settings");
        Fyltrix.Preview.PreviewPane.WebDataFolder = Path.Combine(folder, "_webview2");
        var w = new MainWindow(new AppSettings { PreviewVisible = true, WindowWidth = 1300, WindowHeight = 760, Favourites = new() { folder } }, folder);
        w.ShowActivated = false;
        w.ShowInTaskbar = false;
        w.SourceInitialized += (_, _) => Cloak(new WindowInteropHelper(w).Handle);
        w.Show();
        DoEvents(2500);
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(w);
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(w.ActualWidth * dpi.DpiScaleX), (int)(w.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(w);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using (var fs = File.Create(png)) enc.Save(fs);
        w.Close();
        return 0;
    }

    private static Window Dialog(Window owner)
    {
        var d = new DialogWindow(owner, "Dialog");
        d.AddContent(new System.Windows.Controls.TextBlock { Text = "Some text", Height = 120 });
        d.AddButton("OK", d.Close, primary: true);
        return d;
    }

    private static string Write(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, string.Join("\n", Enumerable.Range(1, 50).Select(i => name + " " + i)));
        return path;
    }

    internal readonly record struct Rect(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public int[] ToArray() => new[] { Left, Top, Right, Bottom };
    }

    private static List<(Rect Bounds, Rect Work, bool Primary, uint Dpi)> Monitors()
    {
        var list = new List<(Rect, Rect, bool, uint)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) =>
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(h, ref mi);
            GetDpiForMonitor(h, 0, out uint dpi, out _);
            list.Add((mi.rcMonitor, mi.rcWork, (mi.dwFlags & 1) != 0, dpi));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static Rect WindowRect(IntPtr h)
    {
        GetWindowRect(h, out var r);
        return r;
    }

    private static void Cloak(IntPtr h)
    {
        int on = 1;
        DwmSetWindowAttribute(h, DWMWA_CLOAK, ref on, 4);
    }

    /// <summary>Shows <paramref name="w"/> cloaked (invisible, not activated) at a physical-pixel rectangle.</summary>
    private static void ShowCloaked(Window w, Rect at)
    {
        w.ShowActivated = false;
        w.ShowInTaskbar = false;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(w).Handle;
            Cloak(h);
            SetWindowPos(h, IntPtr.Zero, at.Left, at.Top, at.Width, at.Height, SWP_NOZORDER | SWP_NOACTIVATE);
        };
        w.Show();
        DoEvents(500);
        // WPF may resize the window for the monitor's DPI after the move; put it exactly where it was asked to be
        SetWindowPos(new WindowInteropHelper(w).Handle, IntPtr.Zero, at.Left, at.Top, at.Width, at.Height, SWP_NOZORDER | SWP_NOACTIVATE);
        DoEvents(300);
    }

    /// <summary>Maximises a shown window on its monitor without activating it (ShowWindow(SW_MAXIMIZE) always activates):
    /// the WS_MAXIMIZE style plus the maximised bounds, which hang over the work area by the resize frame.</summary>
    private static void MaximizeQuietly(Window w, Rect work)
    {
        var h = new WindowInteropHelper(w).Handle;
        SetWindowLong(h, GWL_STYLE, GetWindowLong(h, GWL_STYLE) | WS_MAXIMIZE);
        const int over = 8;
        SetWindowPos(h, IntPtr.Zero, work.Left - over, work.Top - over, work.Width + 2 * over, work.Height + 2 * over, SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        DoEvents(300);
    }

    private static void DoEvents(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public Rect rcMonitor, rcWork; public uint dwFlags; }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);
    private const int DWMWA_CLOAK = 13, GWL_STYLE = -16, WS_MAXIMIZE = 0x01000000;
    private const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SetActiveWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
