using System.Runtime.InteropServices;
using System.Windows.Interop;
using Fyltrix.UI;

namespace Fyltrix.PlacementProbe;

internal static class TaskbarIdentityProbe
{
    internal static Dictionary<string, string?> Capture(MainWindow window)
    {
        var result = new Dictionary<string, string?>();
        Marshal.ThrowExceptionForHR(GetCurrentProcessExplicitAppUserModelID(out var processId));
        try { result["ProcessId"] = Marshal.PtrToStringUni(processId); }
        finally { Marshal.FreeCoTaskMem(processId); }
        Guid iid = typeof(TaskbarIdentity.IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(TaskbarIdentity.SHGetPropertyStoreForWindow(new WindowInteropHelper(window).Handle, ref iid, out var store));
        try
        {
            foreach (var (id, name) in new[] { (2u, "RelaunchCommand"), (3u, "IconResource"), (4u, "DisplayName"), (5u, "WindowId") })
            {
                var key = new TaskbarIdentity.PropertyKey { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = id };
                Marshal.ThrowExceptionForHR(store.GetValue(ref key, out var value));
                try { result[name] = Marshal.PtrToStringUni(value.Pointer); }
                finally { TaskbarIdentity.PropVariantClear(ref value); }
            }
        }
        finally { Marshal.ReleaseComObject(store); }
        return result;
    }

    [DllImport("shell32.dll")]
    private static extern int GetCurrentProcessExplicitAppUserModelID(out IntPtr appId);
}
