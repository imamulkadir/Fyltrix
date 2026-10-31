using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Fyltrix.Core;

// One boundary for native clipboard access; tests substitute an in-memory clipboard.
internal interface IFileClipboard
{
    IDataObject? Read();
    BitmapSource? ReadImage();
    void Write(IDataObject data);
    void Clear();
}

internal sealed class WindowsFileClipboard : IFileClipboard
{
    public IDataObject? Read() => Clipboard.GetDataObject();
    public BitmapSource? ReadImage()
    {
        var image = Clipboard.GetImage();
        if (image != null) return image;
        // Some native producers advertise only CF_DIBV5/CF_DIB, without WPF's Bitmap format.
        if (!OpenClipboard(IntPtr.Zero)) throw new ExternalException("The clipboard is busy.");
        try
        {
            var handle = GetClipboardData(17);
            if (handle == IntPtr.Zero) handle = GetClipboardData(8);
            if (handle == IntPtr.Zero) return null;
            int size = checked((int)GlobalSize(handle).ToUInt64());
            var address = GlobalLock(handle);
            if (address == IntPtr.Zero) return null;
            try
            {
                var bytes = new byte[size];
                Marshal.Copy(address, bytes, 0, size);
                return ClipboardImageFile.DecodeDib(bytes);
            }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
    }
    public void Write(IDataObject data) => Clipboard.SetDataObject(data, true);
    public void Clear() => Clipboard.Clear();
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
}

internal static class FileAccessPolicy
{
    // Opening a directory with FILE_ADD_FILE tests effective access, without creating a probe file.
    internal static bool WritableDirectory(string path) => Directory.Exists(path) && Access(path, 2);
    internal static bool Readable(string path) => Access(path, 0x80000000);
    internal static bool Deletable(string path) => Access(path, 0x10000);
    private static bool Access(string path, uint access)
    {
        using var handle = CreateFile(path, access, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        return !handle.IsInvalid;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string path, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);
}

internal static class ClipboardImageFile
{
    internal static bool HasImage(IDataObject data) => data.GetDataPresent("PNG") || data.GetDataPresent(DataFormats.Bitmap)
        || data.GetDataPresent(DataFormats.Dib) || data.GetDataPresent(DataFormats.GetDataFormat(17).Name);

    internal static BitmapSource DecodeDib(byte[] dib)
    {
        if (dib.Length < 40) throw new IOException("Invalid clipboard bitmap header.");
        int headerSize = BitConverter.ToInt32(dib, 0);
        int bits = BitConverter.ToUInt16(dib, 14);
        int compression = BitConverter.ToInt32(dib, 16);
        int colors = BitConverter.ToInt32(dib, 32);
        if (headerSize < 40 || headerSize > dib.Length || colors < 0) throw new IOException("Invalid clipboard bitmap header.");
        if (colors == 0 && bits <= 8) colors = 1 << bits;
        int offset = checked(14 + headerSize + colors * 4 + (headerSize == 40 && compression == 3 ? 12 : 0));
        using var bmp = new MemoryStream();
        using (var writer = new BinaryWriter(bmp, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((ushort)0x4d42); writer.Write(checked(dib.Length + 14)); writer.Write(0); writer.Write(offset); writer.Write(dib);
        }
        bmp.Position = 0;
        var bitmap = BitmapDecoder.Create(bmp, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        bitmap.Freeze();
        return bitmap;
    }

    // Capture on the STA; encode and write on a worker. Prefer original PNG bytes (no re-encoding).
    internal static (byte[]? Png, BitmapSource? Bitmap) Capture(IDataObject data, IFileClipboard clipboard)
    {
        if (data.GetDataPresent("PNG"))
        {
            var raw = data.GetData("PNG");
            byte[]? bytes = raw as byte[];
            if (raw is Stream stream)
            {
                if (stream.CanSeek) stream.Position = 0;
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                bytes = copy.ToArray();
            }
            if (bytes is { Length: > 0 })
            {
                using var input = new MemoryStream(bytes);
                _ = new PngBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                return (bytes, null);
            }
        }
        var bitmap = clipboard.ReadImage();
        if (bitmap == null) throw new IOException("The clipboard image could not be read.");
        bitmap = bitmap.Clone();
        bitmap.Freeze();
        return (null, bitmap);
    }

    internal static string Save(string directory, byte[]? png, BitmapSource? bitmap, DateTime when)
    {
        var stem = "Screenshot " + when.ToString("yyyy-MM-dd HH-mm-ss");
        for (int i = 0; ; i++)
        {
            var path = Path.Combine(directory, stem + (i == 0 ? "" : $" ({i + 1})") + ".png");
            FileStream output;
            try { output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
            catch (IOException) when (File.Exists(path) || Directory.Exists(path)) { continue; }
            try
            {
                using (output)
                {
                    if (png != null) output.Write(png);
                    else
                    {
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap!));
                        encoder.Save(output);
                    }
                }
                return path;
            }
            catch { output.Dispose(); File.Delete(path); throw; } // only the file just created by this operation
        }
    }
}
