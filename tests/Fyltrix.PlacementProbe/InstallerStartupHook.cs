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
            }
            else if (watch.Elapsed > TimeSpan.FromSeconds(45))
            {
                timer.Stop(); File.WriteAllText(Path.Combine(root, "probe-error.txt"), "Application startup timed out."); Application.Current?.Shutdown(3);
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
