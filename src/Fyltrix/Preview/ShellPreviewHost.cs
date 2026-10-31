using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Fyltrix.Core;
using WinForms = System.Windows.Forms;

namespace Fyltrix.Preview;

/// <summary>
/// Hosts a Windows preview handler (the same COM components Explorer's preview pane uses,
/// e.g. Office's PowerPoint / Outlook previewers) inside a WinForms panel. Used for formats
/// the built-in viewer doesn't render. Explorer's "could harm your computer" block is an
/// Explorer policy and does not apply here.
/// </summary>
public sealed class ShellPreviewHost : WinForms.Panel
{
    private object? _handlerObj;
    private IPreviewHandler? _handler;
    private Stream? _stream;

    public string? CurrentPath { get; private set; }

    public ShellPreviewHost()
    {
        BackColor = UI.Theme.DrawingColor("Bg1");
        Resize += (_, _) => UpdateHandlerBounds();
    }

    public static Guid? FindHandler(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return null;
        uint size = 64;
        var buf = new char[size];
        int hr = Native.AssocQueryString(Native.ASSOCF_INIT_DEFAULTTOSTAR | Native.ASSOCF_NOTRUNCATE, Native.ASSOCSTR_SHELLEXTENSION,
            ext, "{8895b1c6-b41f-4c1c-a562-0d564250836f}", buf, ref size);
        if (hr != 0) return null;
        var s = new string(buf, 0, (int)Math.Max(0, size - 1)).TrimEnd('\0');
        return Guid.TryParse(s, out var g) ? g : null;
    }

    /// <summary>Shows the file. Returns an error message, or null on success.</summary>
    public string? Show(string path)
    {
        Unload();
        var clsid = FindHandler(path);
        if (clsid == null) return "No Windows preview handler is registered for this file type.";
        try
        {
            var type = Type.GetTypeFromCLSID(clsid.Value, throwOnError: true)!;
            _handlerObj = Activator.CreateInstance(type);
            if (_handlerObj is IInitializeWithFile withFile)
            {
                withFile.Initialize(path, 0 /* STGM_READ */);
            }
            else if (_handlerObj is IInitializeWithStream withStream)
            {
                _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                withStream.Initialize(new ManagedIStream(_stream), 0);
            }
            else
            {
                Unload();
                return "The preview handler for this type can't be hosted outside Explorer.";
            }
            _handler = (IPreviewHandler)_handlerObj;
            var r = ClientRect();
            _handler.SetWindow(Handle, ref r);
            _handler.DoPreview();
            CurrentPath = path;
            return null;
        }
        catch (Exception ex)
        {
            Unload();
            return "The Windows preview handler failed: " + ex.Message;
        }
    }

    private RECT ClientRect() => new() { left = 0, top = 0, right = Math.Max(1, ClientSize.Width), bottom = Math.Max(1, ClientSize.Height) };

    private void UpdateHandlerBounds()
    {
        if (_handler == null) return;
        try { var r = ClientRect(); _handler.SetRect(ref r); } catch { }
    }

    public void Unload()
    {
        CurrentPath = null;
        if (_handler != null) { try { _handler.Unload(); } catch { } }
        if (_handlerObj != null && Marshal.IsComObject(_handlerObj)) { try { Marshal.FinalReleaseComObject(_handlerObj); } catch { } }
        _handler = null;
        _handlerObj = null;
        _stream?.Dispose();
        _stream = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Unload();
        base.Dispose(disposing);
    }

    // ---------------- COM interop ----------------
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int ptX; public int ptY; }

    [ComImport, Guid("8895b1c6-b41f-4c1c-a562-0d564250836f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPreviewHandler
    {
        void SetWindow(IntPtr hwnd, ref RECT rect);
        void SetRect(ref RECT rect);
        void DoPreview();
        void Unload();
        void SetFocus();
        void QueryFocus(out IntPtr phwnd);
        [PreserveSig] uint TranslateAccelerator(ref MSG pmsg);
    }

    [ComImport, Guid("b7d14566-0509-4cce-a71f-0a554233bd9b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithFile
    {
        void Initialize([MarshalAs(UnmanagedType.LPWStr)] string pszFilePath, uint grfMode);
    }

    [ComImport, Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithStream
    {
        void Initialize(IStream pstream, uint grfMode);
    }

    /// <summary>Minimal read-only COM IStream over a .NET stream.</summary>
    private sealed class ManagedIStream : IStream
    {
        private readonly Stream _s;
        public ManagedIStream(Stream s) => _s = s;

        public void Read(byte[] pv, int cb, IntPtr pcbRead)
        {
            int total = 0;
            while (total < cb)
            {
                int n = _s.Read(pv, total, cb - total);
                if (n <= 0) break;
                total += n;
            }
            if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, total);
        }

        public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
        {
            long pos = _s.Seek(dlibMove, (SeekOrigin)dwOrigin);
            if (plibNewPosition != IntPtr.Zero) Marshal.WriteInt64(plibNewPosition, pos);
        }

        public void Stat(out System.Runtime.InteropServices.ComTypes.STATSTG pstatstg, int grfStatFlag)
        {
            pstatstg = new System.Runtime.InteropServices.ComTypes.STATSTG { type = 2 /* STGTY_STREAM */, cbSize = _s.Length, grfMode = 0 };
        }

        public void Write(byte[] pv, int cb, IntPtr pcbWritten) => throw new COMException("Read-only", unchecked((int)0x80030005));
        public void SetSize(long libNewSize) => throw new COMException("Read-only", unchecked((int)0x80030005));
        public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten) => throw new NotSupportedException();
        public void Commit(int grfCommitFlags) { }
        public void Revert() { }
        public void LockRegion(long libOffset, long cb, int dwLockType) { }
        public void UnlockRegion(long libOffset, long cb, int dwLockType) { }
        public void Clone(out IStream ppstm) => throw new NotSupportedException();
    }
}
