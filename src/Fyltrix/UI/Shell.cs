using System.Diagnostics;
using System.Windows;
using Fyltrix.Core;

namespace Fyltrix.UI;

/// <summary>Launching things through Windows (default apps, Explorer, terminal).</summary>
public static class Shell
{
    public static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, ProductBranding.Name, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    /// <summary>Windows' "Open with" dialog, owned by <paramref name="owner"/> so it opens over Fyltrix on Fyltrix' monitor
    /// (rundll32's OpenAs_RunDLL has no owner, so Windows put it on the primary screen).</summary>
    public static void OpenWith(string path, Window? owner)
    {
        var hwnd = owner != null ? new System.Windows.Interop.WindowInteropHelper(owner).Handle : IntPtr.Zero;
        var info = new OPENASINFO { pcszFile = path, oaifInFlags = OAIF_ALLOW_REGISTRATION | OAIF_REGISTER_EXT | OAIF_EXEC };
        int hr = SHOpenWithDialog(hwnd, ref info);
        if (hr >= 0 || hr == HRESULT_CANCELLED) return;
        Diagnostics.Info("Open with", $"SHOpenWithDialog failed: 0x{hr:X8}", path);
        var text = $"Windows couldn't show the Open with dialog (0x{hr:X8}).";
        if (owner != null) MessageBox.Show(owner, text, ProductBranding.Name, MessageBoxButton.OK, MessageBoxImage.Warning); // over the app, not on the primary screen
        else MessageBox.Show(text, ProductBranding.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct OPENASINFO
    {
        public string pcszFile;
        public string? pcszClass;
        public int oaifInFlags;
    }

    private const int OAIF_ALLOW_REGISTRATION = 0x1, OAIF_REGISTER_EXT = 0x2, OAIF_EXEC = 0x4;
    private const int HRESULT_CANCELLED = unchecked((int)0x800704C7);

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHOpenWithDialog(IntPtr parent, ref OPENASINFO info);

    public static void ShowInExplorer(string path)
    {
        try
        {
            if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
            else Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false });
        }
        catch { }
    }

    public static void OpenTerminal(string folder)
    {
        try
        {
            // Windows Terminal if available, otherwise PowerShell
            try
            {
                var psi = new ProcessStartInfo("wt.exe") { UseShellExecute = false };
                psi.ArgumentList.Add("-d");
                psi.ArgumentList.Add(folder);
                Process.Start(psi);
                return;
            }
            catch { }
            Process.Start(new ProcessStartInfo("powershell.exe") { WorkingDirectory = folder, UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, ProductBranding.Name, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    public static void Properties(string path, Window owner)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(owner).Handle;
        Native.ShowProperties(path, hwnd);
    }

    public static void UseDarkTitleBar(Window window)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            int on = Theme.IsLight ? 0 : 1; // the window frame follows the theme
            if (Native.DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                Native.DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int)); // older Windows 10 builds
        }
        catch { }
    }
}
