using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Fyltrix.Launcher;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            using var startup = UpdateProtocol.EnterShared(AppContext.BaseDirectory);
            var payload = ReleaseLayout.Resolve(AppContext.BaseDirectory);
            if (payload == null) return Error("The release layout is missing or invalid. Re-extract the complete package or reinstall Fyltrix. Your settings have not been changed.");
            using var process = Process.Start(ReleaseLayout.StartInfo(payload, args, Environment.CurrentDirectory));
            if (process == null) return Error("Windows could not start Fyltrix.");
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (IOException) when (File.Exists(Path.Combine(AppContext.BaseDirectory, UpdateProtocol.JournalFile)))
        {
            return Error("An interrupted Fyltrix update needs recovery before startup. Use the documented update recovery command. Your settings have not been rewritten.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return Error("Windows could not start the Fyltrix payload. Files may be missing, inaccessible, or blocked by security policy. Your settings have not been changed.");
        }
    }

    private static int Error(string message)
    {
        MessageBoxW(IntPtr.Zero, message, "Fyltrix could not start", 0x10);
        return 1;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
