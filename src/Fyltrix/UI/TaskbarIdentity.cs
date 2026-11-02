using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Fyltrix.Core;
using Fyltrix.Launcher;

namespace Fyltrix.UI;

/// <summary>Set window identity before visibility, including shell-created pins from running windows.</summary>
internal static class TaskbarIdentity
{
    internal static WindowsAppIdentity Current { get; } = WindowsAppIdentity.ForPayload(AppContext.BaseDirectory,
#if DEBUG
        development: true
#else
        development: false
#endif
    );

    internal static void InitializeProcess()
    {
        int result = Current.SetProcess();
        if (result < 0) Diagnostics.Log("Taskbar process identity", Marshal.GetExceptionForHR(result)!);
    }

    internal static void Apply(Window window, WindowsAppIdentity? identity = null)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            Write(hwnd, identity ?? Current);
            HwndSource.FromHwnd(hwnd)?.AddHook(OnWindowMessage);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            // A shell integration failure must not prevent file management/startup or update health checks.
            Diagnostics.Log("Taskbar window identity", ex);
        }
    }

    private static IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0002) // WM_DESTROY: release shell-owned property values before the HWND goes away.
        {
            try { Clear(hwnd); }
            catch (COMException ex) { Diagnostics.Log("Taskbar window cleanup", ex); }
        }
        return IntPtr.Zero;
    }

    internal static void Clear(IntPtr hwnd)
    {
        Guid iid = typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(hwnd, ref iid, out var store));
        try
        {
            foreach (uint id in new uint[] { 5, 2, 3, 4 })
            {
                var key = new PropertyKey { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = id };
                var empty = new PropVariant();
                Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref empty));
            }
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    internal static void Write(IntPtr hwnd, WindowsAppIdentity identity)
    {
        Guid iid = typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(hwnd, ref iid, out var store));
        try
        {
            // Shell documentation requires relaunch properties to precede the explicit window ID.
            Set(store, 2, identity.RelaunchCommand);
            Set(store, 3, identity.IconResource);
            Set(store, 4, identity.DisplayName);
            Set(store, 5, identity.Id);
            // Window property stores apply changes immediately; do not call Commit.
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    private static void Set(IPropertyStore store, uint id, string text)
    {
        var key = new PropertyKey { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = id };
        var value = new PropVariant { Type = 31, Pointer = Marshal.StringToCoTaskMemUni(text) };
        try { Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref value)); }
        finally { Marshal.FreeCoTaskMem(value.Pointer); }
    }

    // x64 PROPVARIANT layout; application targets x64 exclusively.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }
    [DllImport("shell32.dll")]
    internal static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("ole32.dll")]
    internal static extern int PropVariantClear(ref PropVariant value);
}
