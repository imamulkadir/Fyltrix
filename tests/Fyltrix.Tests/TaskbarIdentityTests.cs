using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Fyltrix.Launcher;
using Fyltrix.UI;

namespace Fyltrix.Tests;

public sealed class TaskbarIdentityTests
{
    [Fact]
    public void Native_window_properties_match_shortcut_and_stable_launcher_for_multiple_windows()
    {
        Sta.Run(() =>
        {
            var identity = new WindowsAppIdentity(WindowsAppIdentity.ProductionId,
                @"C:\Fyltrix test & é\Fyltrix.exe", "Fyltrix");
            for (int i = 0; i < 2; i++)
            {
                var window = new Window { ShowInTaskbar = false };
                try
                {
                    // Create only the off-screen HWND; no visible window, taskbar mutation or input.
                    var hwnd = new WindowInteropHelper(window).EnsureHandle();
                    TaskbarIdentity.Write(hwnd, identity);
                    Guid iid = typeof(TaskbarIdentity.IPropertyStore).GUID;
                    Marshal.ThrowExceptionForHR(TaskbarIdentity.SHGetPropertyStoreForWindow(hwnd, ref iid, out var store));
                    try
                    {
                        foreach (var (id, expected) in new[] { (2u, identity.RelaunchCommand), (3u, identity.IconResource), (4u, identity.DisplayName), (5u, identity.Id) })
                        {
                            var key = new TaskbarIdentity.PropertyKey { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = id };
                            Marshal.ThrowExceptionForHR(store.GetValue(ref key, out var value));
                            try { Assert.Equal(expected, Marshal.PtrToStringUni(value.Pointer)); }
                            finally { TaskbarIdentity.PropVariantClear(ref value); }
                        }
                    }
                    finally { Marshal.ReleaseComObject(store); }
                }
                finally { TaskbarIdentity.Clear(new WindowInteropHelper(window).Handle); window.Close(); }
            }
        });
    }
}
