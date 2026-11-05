using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Fyltrix.Core;
using Fyltrix.Core.Media;
using Fyltrix.UI;

namespace Fyltrix.PlacementProbe;

/// <summary>Opt-in, cloaked real-component benchmark. All generated data/profiles stay below the supplied root.</summary>
internal static class ThumbnailBench
{
    internal static int Run(string root, string output, bool capture = false)
    {
        var report = new Dictionary<string, object>();
        try
        {
            Directory.CreateDirectory(root);
            AppSettings.Folder = Path.Combine(root, "settings-" + Environment.ProcessId);
            ThumbnailService.DiskCacheFolder = Path.Combine(root, "thumb-cache");
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Theme.Apply(app);
            string pictures = Path.Combine(root, "pictures"); Directory.CreateDirectory(pictures);
            if (!File.Exists(Path.Combine(pictures, "image000.png")))
            {
                var pixels = new byte[1024 * 768 * 4];
                for (int y = 0; y < 768; y++) for (int x = 0; x < 1024; x++)
                { int i = (y * 1024 + x) * 4; pixels[i] = (byte)(x % 251); pixels[i + 1] = (byte)(y % 251); pixels[i + 2] = (byte)((x / 16 + y / 16) % 2 * 255); pixels[i + 3] = 255; }
                var bitmap = BitmapSource.Create(1024, 768, 96, 96, PixelFormats.Bgra32, null, pixels, 1024 * 4);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var memory = new MemoryStream(); encoder.Save(memory);
                for (int i = 0; i < 72; i++) File.WriteAllBytes(Path.Combine(pictures, $"image{i:000}.png"), memory.ToArray());
            }
            var files = Directory.GetFiles(pictures).Select(p => new FileInfo(p)).ToArray();
            var process = Process.GetCurrentProcess(); var cpu = process.TotalProcessorTime;
            using (var service = new ThumbnailService(3, 96L * 1024 * 1024))
            {
                double Load(int size)
                {
                    using var done = new CountdownEvent(files.Length);
                    int failures = 0; var watch = Stopwatch.StartNew();
                    foreach (var f in files) service.Request(new ThumbnailRequest { Path = f.FullName, Size = size,
                        Modified = f.LastWriteTime, Length = f.Length, Done = bitmap => { if (bitmap == null) Interlocked.Increment(ref failures); done.Signal(); } });
                    if (!done.Wait(60000)) throw new TimeoutException("Thumbnail batch");
                    if (failures != 0) throw new InvalidOperationException("Missing native thumbnails: " + failures);
                    return watch.Elapsed.TotalMilliseconds;
                }
                report["cold256Ms"] = Load(256);
                report["warm256Ms"] = Load(256);
                report["size512Ms"] = Load(512);
                report["largerCacheHitsFor96"] = files.Count(f => service.TryGetCached(f.FullName, 96, f.LastWriteTime, f.Length) != null);
                report["resizeBack96Ms"] = Load(96);
                report["cacheBytes"] = service.CachedBytes;
            }

            var item = FileItem.FromInfo(files[0]); item.LoadShellInfo();
            var tile = new ThumbnailImage { Item = item, Size = 256 };
            var tileWindow = new Window { Content = tile, Width = 300, Height = 300, ShowActivated = false, ShowInTaskbar = false };
            tile.Loaded += (_, _) =>
            {
                var images = tile.Children.OfType<Image>().Where(i => i.Source is BitmapSource && i.Visibility == Visibility.Visible).ToArray();
                report["placeholderMaxUpscale"] = images.Select(i => i.Width * VisualTreeHelper.GetDpi(tile).DpiScaleX / ((BitmapSource)i.Source).PixelWidth).DefaultIfEmpty(1).Max();
                if (capture) Capture(tile, output + ".placeholder.png");
            };
            CloakedShow(tileWindow);
            Wait(() => tile.HasThumbnail, "single tile");
            report["nativeDpiScale"] = VisualTreeHelper.GetDpi(tile).DpiScaleX;
            if (capture) Capture(tile, output + ".loaded.png");
            tileWindow.Close();

            string many = Path.Combine(root, "many"); Directory.CreateDirectory(many);
            if (Directory.GetFiles(many).Length == 0)
                for (int i = 0; i < 5000; i++) File.WriteAllText(Path.Combine(many, $"file{i:0000}.txt"), "demo");
            var watch = Stopwatch.StartNew();
            var window = new MainWindow(new AppSettings { PreviewVisible = false, ViewTileSize = 160, WindowWidth = 1300, WindowHeight = 800 }, pictures);
            CloakedShow(window);
            try
            {
                Wait(() => window.View?.StartupListingReady == true && Tiles(window.View).Count > 0, "grid listing");
                report["gridListedMs"] = watch.Elapsed.TotalMilliseconds;
                if (capture) Capture(window, output + ".loading-grid.png");
                Wait(() => Tiles(window.View!).All(t => t.HasThumbnail), "initial realized thumbnails");
                report["gridThumbnailsMs"] = watch.Elapsed.TotalMilliseconds;
                window.View!.Navigate(many); Wait(() => window.View.StartupListingReady && window.View.CurrentPath == many, "large listing");
                watch.Restart();
                for (int i = 0; i < 20; i++) { window.View.TilePanel!.PageDown(); Pump(5); }
                report["twentyPageScrollMs"] = watch.Elapsed.TotalMilliseconds;
                report["realizedAfterScroll"] = Tiles(window.View).Count;
                watch.Restart(); window.View.Navigate(pictures);
                Wait(() => window.View.StartupListingReady && window.View.CurrentPath == pictures && Tiles(window.View).Count > 0 && Tiles(window.View).All(t => t.HasThumbnail), "warm return");
                report["warmReturnMs"] = watch.Elapsed.TotalMilliseconds;
                if (capture) Capture(window, output + ".grid.png");
            }
            finally { window.Close(); }
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); process.Refresh();
            report["processCpuMs"] = (process.TotalProcessorTime - cpu).TotalMilliseconds;
            report["privateBytes"] = process.PrivateMemorySize64;
            report["managedBytes"] = GC.GetTotalMemory(false);
            report["conditions"] = "72 generated 1024x768 PNGs; 5000 text files; app cache cold, Windows Shell cache uncontrolled; cloaked WPF; no desktop input";
            File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex) { report["error"] = ex.ToString(); File.WriteAllText(output, JsonSerializer.Serialize(report)); return 1; }
    }
    private static List<ThumbnailImage> Tiles(FolderView view) => VisualTree.FindDescendants<ThumbnailImage>(view.List).ToList();
    private static void Capture(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        int width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth)), height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawRectangle(Theme.Brush("Bg1"), null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(element) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, width, height) }, null, new Rect(0, 0, width, height));
        }
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static void CloakedShow(Window w)
    {
        w.SourceInitialized += (_, _) => { int cloak = 1; DwmSetWindowAttribute(new WindowInteropHelper(w).Handle, 13, ref cloak, 4); };
        w.Show();
    }
    private static void Wait(Func<bool> condition, string phase)
    { var watch = Stopwatch.StartNew(); while (!condition()) { if (watch.ElapsedMilliseconds > 60000) throw new TimeoutException(phase); Pump(10); } }
    private static void Pump(int milliseconds)
    { var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) }; timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame); }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
