using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Fyltrix.UI;

namespace Fyltrix.Tests;

/// <summary>
/// All WPF tests run on one long-lived STA thread that owns the Application (theme resources live there).
/// Windows are created off-screen and never activated, so tests don't take focus or send input anywhere.
/// </summary>
public static class Sta
{
    private static readonly Lazy<Dispatcher> D = new(() =>
    {
        Dispatcher? d = null;
        using var ready = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            d = Dispatcher.CurrentDispatcher;
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Theme.Apply(app);
            ready.Set();
            Dispatcher.Run();
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        ready.Wait();
        return d!;
    });

    public static string ShotsDir { get; } = Path.Combine(Path.GetTempPath(), "FyltrixTests-shots");

    public static void Run(Action action)
    {
        Exception? error = null;
        D.Value.Invoke(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    /// <summary>Processes UI work (bindings, async continuations, timers) for a while.</summary>
    public static void DoEvents(int ms = 50)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    public static void WaitUntil(Func<bool> condition, string what, int timeoutMs = 15000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Timed out waiting for: " + what);
            DoEvents(40);
        }
    }

    /// <summary>Renders a visual to PNG (WPF content only; WebView2/HWND content is not captured).</summary>
    public static string Render(FrameworkElement element, string name, double scale = 1)
    {
        Directory.CreateDirectory(ShotsDir);
        element.UpdateLayout();
        int w = Math.Max(1, (int)(element.ActualWidth * scale)), h = Math.Max(1, (int)(element.ActualHeight * scale));
        // scale > 1 renders like a high-DPI screen (vector content stays sharp)
        var rtb = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) dc.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        var file = Path.Combine(ShotsDir, name + ".png");
        using (var fs = File.Create(file)) enc.Save(fs);
        return file;
    }

    /// <summary>Positions a window far off-screen without activating it.</summary>
    public static void ShowOffscreen(Window w)
    {
        w.ShowActivated = false;
        w.ShowInTaskbar = false;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.WindowState = WindowState.Normal;
        w.Left = -32000;
        w.Top = -32000;
        w.Show();
    }
}

[CollectionDefinition("UI", DisableParallelization = true)]
public class UiCollection { }

[Collection("UI")]
public class ThemeTests
{
    [Fact]
    public void All_theme_snippets_parse()
    {
        Sta.Run(() =>
        {
            Assert.True(Theme.SnippetErrors.Count == 0, string.Join("\n", Theme.SnippetErrors));
            Assert.True(Application.Current.Resources.MergedDictionaries.Count >= 10);
            Assert.NotNull(Application.Current.TryFindResource(typeof(System.Windows.Controls.ComboBox)));
        });
    }
}
