using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Fyltrix.Preview;

namespace Fyltrix.UI;

/// <summary>Forwards a real, pane-local wheel input before native PDF scrolling begins.</summary>
internal sealed class NativePdfWheelMirror : IDisposable
{
    private readonly PreviewPane[] _panes;
    private readonly Func<NativePdfAccess?> _access;
    private readonly Func<bool> _enabled;
    private readonly double[] _residue = new double[2];
    private bool _forwarding, _disposed;
    internal int ForwardedCount { get; private set; }
    internal double LastDispatchMilliseconds { get; private set; }

    internal NativePdfWheelMirror(PreviewPane left, PreviewPane right, Func<NativePdfAccess?> access, Func<bool> enabled)
    {
        _panes = new[] { left, right };
        _access = access;
        _enabled = enabled;
        ComponentDispatcher.ThreadFilterMessage += Filter;
    }

    internal static bool PlainWheel(int message, IntPtr parameter) => message == 0x020A && (parameter.ToInt64() & 0xffff) == 0;
    internal static bool InterruptsWheel(int message, IntPtr parameter) => message is 0x0201 or 0x0202 or 0x0204 or 0x0207 or 0x0100 or 0x0104 or 0x0246 ||
        message == 0x020A || message == 0x0200 && (parameter.ToInt64() & 1) != 0;

    internal static Point ScreenPoint(IntPtr parameter) => new(
        unchecked((short)(parameter.ToInt64() & 0xffff)), unchecked((short)((parameter.ToInt64() >> 16) & 0xffff)));

    internal static int ScaleDelta(int delta, double ratio, ref double residue)
    {
        double scaled = delta * ratio + residue;
        int result = (int)Math.Clamp(Math.Truncate(scaled), short.MinValue, short.MaxValue);
        residue = scaled - result;
        // Discard extreme overflow instead of accumulating an unbounded future gesture.
        if (result is short.MinValue or short.MaxValue) residue = 0;
        return result;
    }

    private void Filter(ref MSG message, ref bool handled)
    {
        if (_disposed || _forwarding || handled) return;
        bool wheel = PlainWheel(message.message, message.wParam);
        if (!wheel && !InterruptsWheel(message.message, message.wParam) || !_enabled()) return;
        var access = _access();
        if (access == null) return;
        for (int source = 0; source < 2; source++)
        {
            var pane = _panes[source];
            if (!pane.HasNativePdfInput || !IsChild(pane.Web.Handle, message.hwnd)) continue;
            if (!wheel) { access.PreferNativeInput(source); return; }
            if (!_panes[1-source].HasNativePdfInput || !access.TryWheelGeometry(source, out var from, out var to)) return;
            if (!from!.Bounds.Contains(ScreenPoint(message.lParam))) return; // native toolbar/menus keep their input

            var center = new NativePoint((int)(to!.Bounds.Left + to.Bounds.Width/2), (int)(to.Bounds.Top + to.Bounds.Height/2));
            IntPtr target = WindowFromPoint(center);
            if (!IsChild(_panes[1-source].Web.Handle, target)) return; // covered, hidden, stale or another application

            int delta = unchecked((short)((message.wParam.ToInt64() >> 16) & 0xffff));
            int scaled = ScaleDelta(delta, to.ScrollableHeight/from.ScrollableHeight, ref _residue[source]);
            if (delta == 0) return;
            access.BeginNativeWheel(source);
            if (scaled == 0) return; // preserve high-resolution fractional input for the following event
            IntPtr parameter = new(unchecked((int)((uint)(ushort)(short)scaled << 16)));
            IntPtr position = new(unchecked((int)((uint)(ushort)center.Y << 16 | (ushort)center.X)));
            var watch = Stopwatch.StartNew();
            _forwarding = true;
            try
            {
                // A sent window message bypasses the posted-input filter. There is no global
                // mouse injection, cursor movement, activation or synthetic click/capture.
                if (SendMessageTimeout(target, 0x020A, parameter, position, 2, 30, out _) != IntPtr.Zero)
                {
                    ForwardedCount++;
                    LastDispatchMilliseconds = watch.Elapsed.TotalMilliseconds;
                    access.Trace?.Invoke($"native wheel {source}: {delta}->{scaled}, dispatch {LastDispatchMilliseconds:F3}ms");
                }
                else access.CancelNativeWheel(); // keep ordinary accessibility fallback on timeout
            }
            finally { _forwarding = false; }
            return; // leave the source input untouched for WebView2's normal dispatch
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ComponentDispatcher.ThreadFilterMessage -= Filter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; public NativePoint(int x, int y) { X=x; Y=y; } }
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr parameter,
        IntPtr position, uint flags, uint timeout, out IntPtr result);
}
