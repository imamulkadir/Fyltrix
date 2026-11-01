using System.Runtime.CompilerServices;

namespace Fyltrix.Tests;

internal static class TestSetup
{
    /// <summary>Before any test: WebView2 gets a profile folder of its own, so a Fyltrix the user has open (same default
    /// folder, so the same browser process) can't block or be disturbed by the previews the tests open.</summary>
    [ModuleInitializer]
    internal static void Init()
    {
        // Even the restored baseline must be disposable: a test must never fall back to the real profile.
        var settings = Path.Combine(Path.GetTempPath(), "FyltrixTests-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(settings);
        Fyltrix.Core.AppSettings.Folder = settings;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(settings, recursive: true); } catch { /* test-owned cleanup only */ }
        };
        Fyltrix.Preview.PreviewPane.WebDataFolder = Path.Combine(Path.GetTempPath(), "FyltrixTests-WebView2");
        Fyltrix.UI.MainWindow.ClipboardFactory = () => new MemoryFileClipboard();
        Fyltrix.UI.MainWindow.NotificationsFactoryForTests = session => new Fyltrix.Core.UpdateNotifications(session,
            new Fyltrix.Core.UpdateNotificationStore(Path.Combine(Fyltrix.Core.AppSettings.Folder, "test-update-metadata")));
    }
}

internal sealed class MemoryFileClipboard : Fyltrix.Core.IFileClipboard
{
    internal System.Windows.IDataObject? Data;
    internal System.Windows.Media.Imaging.BitmapSource? Image;
    internal bool Busy;
    public System.Windows.IDataObject? Read() => Busy ? throw new System.Runtime.InteropServices.ExternalException("Clipboard busy") : Data;
    public System.Windows.Media.Imaging.BitmapSource? ReadImage() => Image ?? Data?.GetData(System.Windows.DataFormats.Bitmap) as System.Windows.Media.Imaging.BitmapSource;
    public void Write(System.Windows.IDataObject data) => Data = data;
    public void Clear() { Data = null; Image = null; }
}
