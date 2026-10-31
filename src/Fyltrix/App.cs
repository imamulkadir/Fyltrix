using System.Windows;
using System.Windows.Threading;
using Fyltrix.Core;
using Fyltrix.UI;
using Fyltrix.Launcher;
using Fyltrix.Updates;

namespace Fyltrix;

public sealed class App : Application
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length == 3 && args[0] is UpdateWorker.ApplyArgument or UpdateWorker.RecoverArgument)
        {
            Environment.ExitCode = UpdateWorker.RunAsync(args[1], args[2], args[0] == UpdateWorker.RecoverArgument).GetAwaiter().GetResult();
            return;
        }
        TrialConnection? trial;
        IDisposable? startup = null;
        try
        {
            trial = TrialConnection.Connect(AppContext.BaseDirectory);
            if (trial == null && UpdateProtocol.PayloadRoot(AppContext.BaseDirectory) is { } root)
            {
                startup = UpdateProtocol.EnterShared(root);
                if (UpdateProtocol.Supported(root) && ReleaseLayout.Resolve(root)?.Executable.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) != true)
                    throw new System.IO.IOException("This payload is not the selected version.");
            }
            if (trial != null)
            {
                var core = trial.Version.Split('-')[0].Split('.').Select(int.Parse).ToArray();
                var binary = typeof(App).Assembly.GetName().Version!;
                if (binary.Major != core[0] || binary.Minor != core[1] || binary.Build != core[2]) throw new System.IO.IOException("Trial binary version mismatch.");
                AppSettings.Folder = trial.Profile;
                Preview.PreviewPane.WebDataFolder = System.IO.Path.Combine(trial.Cache, "WebView2");
                Core.Media.ThumbnailService.DiskCacheFolder = System.IO.Path.Combine(trial.Cache, "Thumbs");
                Core.Archives.ArchiveBrowser.SetTrialCache(System.IO.Path.Combine(trial.Cache, "Archives"));
                UI.MainWindow.ClipboardFactory = () => new TrialClipboard();
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException)
        { startup?.Dispose(); Environment.ExitCode = 4; return; }
        using var startupLease = startup;
        using var trialLease = trial;
        // Anything that would end the process is written to a crash report first (native faults included,
        // when the runtime surfaces them as SEHException on the way out).
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Diagnostics.Crash(e.IsTerminating ? "Unhandled (fatal)" : "Unhandled", ex);
        };
        // A faulted fire-and-forget task must not go unnoticed (or, with stricter settings, end the process).
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Diagnostics.Log("Background task", e.Exception);
            e.SetObserved();
        };

        // The window is built before app.Run() starts the message loop; awaits during construction (folder listings, the
        // preview) must come back to the UI thread, so WPF's synchronisation context goes in first. (It used to be installed
        // as a side effect of creating a WinForms control in the preview pane, which is now made only when needed.)
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var app = new App { ShutdownMode = ShutdownMode.OnMainWindowClose };
        if (trial == null) app.DispatcherUnhandledException += OnUnhandled;
        else app.DispatcherUnhandledException += (_, e) => { e.Handled = true; app.Shutdown(4); };
        AppSettings settings;
        try { settings = trial == null ? AppSettings.Load() : AppSettings.LoadForUpdateTrial(); }
        catch { Environment.ExitCode = 4; return; }
        if (trial != null) { settings.AutomationEnabled = false; settings.ConfirmCloseTabs = false; }
        Theme.Use(settings.Theme); // the palette is fixed for this run (Settings says a restart applies a new one)
        Theme.Apply(app);
        var window = new MainWindow(settings, args.Length > 0 ? args[0] : null);
        app.MainWindow = window;
        if (trial != null)
        {
            window.ShowActivated = false; window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual; window.WindowState = WindowState.Normal;
            window.Left = -32000; window.Top = -32000;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            bool sent = false;
            timer.Tick += async (_, _) =>
            {
                if (sent || window.View?.StartupListingReady != true) return;
                sent = true; timer.Stop();
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(75));
                    await trial.ConfirmAndWaitForCloseAsync(deadline.Token);
                    window.Close();
                }
                catch { app.Shutdown(4); }
            };
            timer.Start();
        }
        window.Show();
        app.Run();
    }

    private sealed class TrialClipboard : IFileClipboard
    {
        public IDataObject? Read() => null;
        public System.Windows.Media.Imaging.BitmapSource? ReadImage() => null;
        public void Write(IDataObject data) => throw new InvalidOperationException("Clipboard is disabled during update health checks.");
        public void Clear() => throw new InvalidOperationException("Clipboard is disabled during update health checks.");
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Diagnostics.Log("UI", e.Exception);
        try
        {
            MessageBox.Show(e.Exception.Message + $"\n\n(Details were saved to {Diagnostics.LogPath})", $"{ProductBranding.Name} – something went wrong",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (InvalidOperationException) { /* no message loop any more (shutting down) */ }
        e.Handled = true;
    }
}
