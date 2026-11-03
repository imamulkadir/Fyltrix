using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Fyltrix.Core;
using Fyltrix.Preview;
using Fyltrix.UI;

namespace Fyltrix.PlacementProbe;

/// <summary>Explicitly invoked visible acceptance fixture. Never run as part of automated tests.</summary>
internal static class CompareDesktopProbe
{
    internal static int Run(string folder, string statusFile)
    {
        folder = Path.GetFullPath(folder);
        if (!folder.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(folder,"DESKTOP-TEST-ONLY"))) return 2;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        AppSettings.Folder = Path.Combine(folder,"settings");
        PreviewPane.WebDataFolder = Path.Combine(folder,"webview2");
        Core.Media.ThumbnailService.DiskCacheFolder = Path.Combine(folder,"thumbnails");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Theme.Apply(app);
        TaskbarIdentity.InitializeProcess();
        var main = new MainWindow(new AppSettings { PreviewVisible = false, ConfirmCloseTabs = false },folder)
        {
            Title = "Fyltrix Compare acceptance fixture", Width = 1450, Height = 900,
            Left = 0, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual,
        };
        var compare = new CompareWindow(main,Path.Combine(folder,"left.pdf"),Path.Combine(folder,"right.pdf"))
        { Width = 1200, Height = 800 };
        // Explicit desktop-fixture commands only. No timer/file polling and no production
        // app handler: write diagnostic replies below the browse directory, not into it.
        string control = Path.Combine(folder, "control");
        Directory.CreateDirectory(control);
        var trace = new System.Collections.Concurrent.ConcurrentQueue<string>();
        bool tracing = false;
        void SaveState()
        {
            IntPtr PopupHandle(PdfCommentsFlyout flyout) => flyout.Popup.IsOpen && flyout.Popup.Child != null
                ? (PresentationSource.FromVisual(flyout.Popup.Child) as HwndSource)?.Handle ?? IntPtr.Zero : IntPtr.Zero;
            File.WriteAllText(Path.Combine(control,"state.json"), JsonSerializer.Serialize(new
            {
                processId = Environment.ProcessId, left = compare.LeftPane.Web.Handle.ToInt64(), right = compare.RightPane.Web.Handle.ToInt64(),
                leftComments = compare.LeftComments.Comments, rightComments = compare.RightComments.Comments,
                leftPopup = PopupHandle(compare.LeftCommentsFlyout).ToInt64(), rightPopup = PopupHandle(compare.RightCommentsFlyout).ToInt64(),
                sync = compare.SyncScrolling, ready = compare.SyncScrollCheck.IsEnabled,
                forwarded = compare.PdfWheel?.ForwardedCount, dispatchMs = compare.PdfWheel?.LastDispatchMilliseconds,
                leftRouting = compare.LeftPane.HasNativePdfInput, rightRouting = compare.RightPane.HasNativePdfInput,
            }));
            if (tracing) File.WriteAllLines(Path.Combine(control,"trace.log"),trace);
        }
        main.Closed += (_,_) => app.Shutdown();
        main.Show();
        compare.Show();
        HwndSource.FromHwnd(new WindowInteropHelper(compare).Handle).AddHook((IntPtr hwnd, int message, IntPtr parameter, IntPtr value, ref bool handled) =>
        {
            if (message != 0x83D0) return IntPtr.Zero;
            handled = true;
            switch (parameter.ToInt32())
            {
                case 2: compare.LeftCommentsFlyout.Button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); break;
                case 3: compare.RightCommentsFlyout.Button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); break;
                case 4: compare.LeftCommentsFlyout.Close(); compare.RightCommentsFlyout.Close(); break;
                case 5: compare.SyncScrolling = false; break;
                case 6: compare.SyncScrolling = true; break;
                case 7: compare.LeftPane.Web.CoreWebView2.Reload(); break;
                case 8: compare.RightPane.Web.CoreWebView2.Reload(); break;
                case 9:
                    tracing = true; trace.Clear();
                    if (compare.PdfAccess != null) compare.PdfAccess.Trace = entry =>
                    { if (trace.Count < 10000) trace.Enqueue($"{System.Diagnostics.Stopwatch.GetTimestamp()} {entry}"); };
                    break;
            }
            SaveState();
            return IntPtr.Zero;
        });
        File.WriteAllText(statusFile,JsonSerializer.Serialize(new { processId = Environment.ProcessId,
            main = new WindowInteropHelper(main).Handle.ToInt64(), compare = new WindowInteropHelper(compare).Handle.ToInt64(), folder }));
        return app.Run();
    }
}
