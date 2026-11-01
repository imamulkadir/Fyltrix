using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Fyltrix.Core;
using Fyltrix.Core.Media;
using Fyltrix.Preview;
using Fyltrix.UI;

// Test-only .NET startup hook. Never shipped in a Fyltrix layout or installer.
public static class StartupHook
{
    public static void Initialize()
    {
        var root = Environment.GetEnvironmentVariable("FYLTRIX_INSTALLER_TEST_ROOT");
        if (root == null || !Path.IsPathFullyQualified(root) || !File.Exists(Path.Combine(root, ".fyltrix-installer-test")))
            throw new InvalidOperationException("An explicitly authorized installer test workspace is required.");
        var installed = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
        if (!File.ReadAllText(Path.Combine(installed, ".fyltrix-install.ini")).Contains("AppId=Fyltrix.Sandbox.PerUser"))
            throw new InvalidOperationException("The startup hook may run only in the sandbox installation.");
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FileLens");
        if (AppSettings.Folder != expected) throw new InvalidOperationException("The legacy profile default changed.");
        AppSettings.Folder = Path.Combine(root, "profile");
        PreviewPane.WebDataFolder = Path.Combine(root, "webview2");
        ThumbnailService.DiskCacheFolder = Path.Combine(root, "thumbs");
        MainWindow.ClipboardFactory = () => new IsolatedClipboard();
        MainWindow.SuppressAutomaticChecksForTests = true;
        MainWindow.NotificationsFactoryForTests = session => new UpdateNotifications(session, new UpdateNotificationStore(Path.Combine(root, "notification-state")));
        if (Environment.GetEnvironmentVariable("FYLTRIX_SANDBOX_UPDATE_WORKER") == "1")
        {
            string dependencyFolder = Path.GetDirectoryName(typeof(StartupHook).Assembly.Location)!;
            foreach (var name in new[] { "Fyltrix.Bootstrap", "Fyltrix.Updates" })
                System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(dependencyFolder, name + ".dll"));
            Environment.Exit(SandboxUpdateStartupProbe.Run(root));
            return;
        }
        Action<MainWindow>? handoverTick = null;
        var notificationData = Environment.GetEnvironmentVariable("FYLTRIX_NOTIFICATION_TEST_DATA");
        if (notificationData != null)
        {
            // Each PrivateProcess uses a different desktop. Do not reconnect to a browser still exiting on its predecessor's desktop.
            PreviewPane.WebDataFolder = Path.Combine(root, "webview2-" + Guid.NewGuid().ToString("N"));
            var notificationProbe = new NotificationStartupProbe(root, notificationData);
            MainWindow.SuppressAutomaticChecksForTests = false;
            MainWindow.NotificationsFactoryForTests = notificationProbe.Create;
            handoverTick = notificationProbe.Tick;
        }
        if (Environment.GetEnvironmentVariable("FYLTRIX_MANUAL_UPDATE_DATA") != null)
            handoverTick = ManualUpdateStartupProbe.Create(root);
        var stage = Environment.GetEnvironmentVariable("FYLTRIX_HANDOVER_TEST_STAGE");
        if (stage != null)
        {
            // Test-only dependencies are loaded from the probe, never added to the application payload.
            string probeFolder = Path.GetDirectoryName(typeof(StartupHook).Assembly.Location)!;
            foreach (var name in new[] { "Fyltrix.Bootstrap", "Fyltrix.Updates" })
                System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(probeFolder, name + ".dll"));
            handoverTick = UpdateHandoverStartupProbe.Create(root, stage,
                Environment.GetEnvironmentVariable("FYLTRIX_HANDOVER_TEST_KEY") ?? throw new InvalidOperationException("Explicit test public key required."));
        }
        var watch = Stopwatch.StartNew();
        bool ready = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            var window = Application.Current?.MainWindow as MainWindow;
            if (window != null && window.View?.Items.Any(i => i.Name == "Smoke.txt") == true && window.PreviewForTest.IsReady)
            {
                if (!ready)
                {
                    var preferences = window.Settings;
                    var result = new { success = true, executable = Environment.ProcessPath, version = typeof(MainWindow).Assembly.GetName().Version!.ToString(), title = window.Title,
                        defaultProfile = expected, isolatedProfile = AppSettings.Folder, previewReady = true, preferences.ShowHidden, preferences.ShowExtensions,
                        preferences.ShowFileOperationLabels, preferences.ShowSecondaryToolbarLabels, preferences.DefaultSort, preferences.Favourites };
                    File.WriteAllText(Path.Combine(root, "ready.json"), JsonSerializer.Serialize(result));
                    ready = true;
                }
                if (File.Exists(Path.Combine(root, "release-app")) || watch.Elapsed > TimeSpan.FromSeconds(90))
                {
                    timer.Stop(); window.Settings.ConfirmCloseTabs = false; window.Close();
                }
                else if (handoverTick != null) handoverTick(window);
                else if (File.Exists(Path.Combine(root, "release-for-update")) && !File.Exists(Path.Combine(root, "update-close.json")))
                {
                    // Reflection keeps Stage 3's probe usable with older payloads lacking this Stage 5 seam.
                    var close = typeof(MainWindow).GetMethod("TryCloseForUpdate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    bool accepted = close != null && (bool)close.Invoke(window, null)!;
                    if (accepted) timer.Stop();
                    File.WriteAllText(Path.Combine(root, "update-close.json"), JsonSerializer.Serialize(new { accepted }));
                }
            }
            else if (watch.Elapsed > TimeSpan.FromSeconds(45))
            {
                timer.Stop();
                File.WriteAllText(Path.Combine(root, "probe-error.txt"), JsonSerializer.Serialize(new
                {
                    error = "Application startup timed out.", folder = window?.View?.CurrentPath,
                    items = window?.View?.Items.Select(item => item.Name).ToArray(), previewReady = window?.PreviewForTest.IsReady,
                    browserCache = PreviewPane.WebDataFolder
                }));
                Application.Current?.Shutdown(3);
            }
        };
        timer.Start();
    }

    private sealed class IsolatedClipboard : IFileClipboard
    {
        public IDataObject? Read() => null;
        public BitmapSource? ReadImage() => null;
        public void Write(IDataObject data) => throw new InvalidOperationException("The installer probe cannot write to the real clipboard.");
        public void Clear() => throw new InvalidOperationException("The installer probe cannot clear the real clipboard.");
    }
}
