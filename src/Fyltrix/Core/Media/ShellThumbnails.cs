using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Fyltrix.Core.Media;

/// <summary>
/// Thumbnails from the Windows shell (IShellItemImageFactory): album art for music, frames for video, previews
/// for images and documents — whatever the installed thumbnail handlers provide, from Explorer's cache when warm.
/// </summary>
public static class ShellThumbnails
{
    /// <param name="thumbnailOnly">True: return null instead of falling back to the file-type icon.</param>
    public static BitmapSource? Get(string path, int size, bool thumbnailOnly = true)
    {
        IShellItemImageFactory? factory = null;
        IntPtr hbm = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory) != 0 || factory == null) return null;
            int flags = SIIGBF_BIGGERSIZEOK | (thumbnailOnly ? SIIGBF_THUMBNAILONLY : 0);
            if (factory.GetImage(new SIZE { cx = size, cy = size }, flags, out hbm) != 0 || hbm == IntPtr.Zero) return null;
            var bmp = FromHBitmap(hbm, out _);
            // Folder previews sometimes come back flattened onto a white or black square (opaque corners). Pictures
            // and video frames are fine opaque; for a folder, use its icon instead, which is always transparent around it.
            if (bmp != null && Directory.Exists(path) && OpaqueCorners(bmp))
            {
                DeleteObject(hbm);
                hbm = IntPtr.Zero;
                if (factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY, out hbm) == 0 && hbm != IntPtr.Zero)
                    bmp = FromHBitmap(hbm, out _) ?? bmp;
            }
            return bmp;
        }
        catch (COMException) { return null; }
        finally
        {
            if (hbm != IntPtr.Zero) DeleteObject(hbm);
            if (factory != null) Marshal.ReleaseComObject(factory);
        }
    }

    private static bool OpaqueCorners(BitmapSource b)
    {
        int w = b.PixelWidth, h = b.PixelHeight;
        var px = new byte[4];
        foreach (var (x, y) in new[] { (0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1) })
        {
            b.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
            if (px[3] < 250) return false;
        }
        return true;
    }

    public static byte[]? GetPng(string path, int size, bool thumbnailOnly = true)
    {
        var bmp = Get(path, size, thumbnailOnly);
        if (bmp == null) return null;
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Copies the bitmap as 32-bit top-down BGRA, keeping its alpha channel (CreateBitmapSourceFromHBitmap drops it).
    /// The shell returns some bitmaps top-down and some bottom-up (it varies even for the same file, e.g. fresh vs
    /// from the thumbnail cache), and the header GetObject reports says "bottom-up" either way. Reading the raw bits
    /// therefore turned icons and some thumbnails upside down; GetDIBits knows the real row order and always hands
    /// back top-down rows.
    /// </summary>
    internal static BitmapSource? FromHBitmap(IntPtr hbm, out bool hadAlpha)
    {
        hadAlpha = false;
        var bm = new BITMAP();
        if (GetObjectBitmap(hbm, Marshal.SizeOf<BITMAP>(), ref bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight == 0) return null;
        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
        var bi = new BITMAPINFOHEADER { biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var pixels = new byte[w * h * 4];
        var dc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(dc, hbm, 0, (uint)h, pixels, ref bi, 0) != h) return null;
        }
        finally { ReleaseDC(IntPtr.Zero, dc); }

        bool anyAlpha = false, anyColour = false;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] != 0) { anyAlpha = true; break; }
            if (pixels[i] != 0 || pixels[i + 1] != 0 || pixels[i + 2] != 0) anyColour = true;
        }
        hadAlpha = anyAlpha;
        if (!anyAlpha)
        {
            if (!anyColour) return null; // handlers without a picture (e.g. audio with no album art) return a blank bitmap
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255; // a picture without an alpha channel is opaque
        }
        // Most shell bitmaps are premultiplied, but some folder previews are straight alpha (transparent pixels still
        // carry colour, which premultiplied drawing turns into a white or black square). In premultiplied data no
        // colour channel can exceed its alpha, so that tells the two apart.
        bool straight = false;
        if (anyAlpha)
            for (int i = 0; i < pixels.Length && !straight; i += 4)
            {
                byte a = pixels[i + 3];
                straight = pixels[i] > a || pixels[i + 1] > a || pixels[i + 2] > a;
            }
        var src = BitmapSource.Create(w, h, 96, 96, straight ? PixelFormats.Bgra32 : PixelFormats.Pbgra32, null, pixels, w * 4);
        src.Freeze();
        return src;
    }

    private const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4, SIIGBF_THUMBNAILONLY = 0x8;

    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }


    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? ppv);

    [DllImport("gdi32.dll", EntryPoint = "GetObject")] private static extern int GetObjectBitmap(IntPtr h, int c, ref BITMAP pv);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bi, uint usage);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
}
