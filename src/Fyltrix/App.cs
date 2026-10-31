using System.Windows;
using System.Windows.Threading;
using Fyltrix.Core;
using Fyltrix.UI;

namespace Fyltrix;

public sealed class App : Application
{
    [STAThread]
    public static void Main(string[] args)
    {
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
        app.DispatcherUnhandledException += OnUnhandled;
        var settings = AppSettings.Load();
        Theme.Use(settings.Theme); // the palette is fixed for this run (Settings says a restart applies a new one)
        Theme.Apply(app);
        var window = new MainWindow(settings, args.Length > 0 ? args[0] : null);
        app.MainWindow = window;
        window.Show();
        app.Run();
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
