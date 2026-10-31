using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Fyltrix.Core.Imaging;

public enum ImageFormat { Png, Jpeg, Bmp, Tiff, Gif, WebP, Ico }

/// <summary>
/// Decoding, transforming and encoding images with WIC (the codecs Windows provides, including the HEIF,
/// WebP and RAW extensions when installed). WebP output uses SkiaSharp, loaded only when needed.
/// Every BitmapSource returned is frozen, so it can cross threads.
/// </summary>
public static class ImageService
{
    /// <summary>Extensions Chromium can't display but WIC usually can; the viewer gets a PNG rendition of these.</summary>
    public static readonly HashSet<string> NeedsTranscode = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tif", ".tiff", ".heic", ".heif", ".hif", ".jxr", ".wdp", ".hdp", ".dds",
        ".dng", ".cr2", ".cr3", ".nef", ".nrw", ".arw", ".sr2", ".orf", ".rw2", ".raf", ".pef", ".srw",
    };

    public static readonly HashSet<string> Decodable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".bmp", ".dib", ".gif", ".ico", ".webp", ".avif",
        ".tif", ".tiff", ".heic", ".heif", ".hif", ".jxr", ".wdp", ".hdp", ".dds",
        ".dng", ".cr2", ".cr3", ".nef", ".nrw", ".arw", ".sr2", ".orf", ".rw2", ".raf", ".pef", ".srw",
    };

    public static bool CanDecode(string path) => Decodable.Contains(Path.GetExtension(path));

    /// <summary>Pixel size without decoding the image (reads the header).</summary>
    public static (int width, int height) PixelSize(string path)
    {
        using var fs = OpenRead(path);
        var frame = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
        int o = Orientation(frame);
        return o >= 5 ? (frame.PixelHeight, frame.PixelWidth) : (frame.PixelWidth, frame.PixelHeight);
    }

    /// <summary>
    /// Decodes the first frame, upright (EXIF orientation applied). With <paramref name="maxSide"/> the image is
    /// scaled down while decoding, so huge files don't need a full-size copy in memory.
    /// </summary>
    public static BitmapSource Load(string path, int? maxSide = null)
    {
        int orientation;
        int w, h;
        using (var fs = OpenRead(path))
        {
            var probe = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            orientation = Orientation(probe);
            w = probe.PixelWidth;
            h = probe.PixelHeight;
        }

        BitmapSource bmp;
        using (var fs = OpenRead(path))
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad; // fully decoded before the stream closes
            bi.StreamSource = fs;
            if (maxSide is { } m && Math.Max(w, h) > m)
            {
                if (w >= h) bi.DecodePixelWidth = m; else bi.DecodePixelHeight = m;
            }
            bi.EndInit();
            bmp = bi;
        }
        bmp = ApplyOrientation(bmp, orientation);
        if (!bmp.IsFrozen) bmp.Freeze();
        return bmp;
    }

    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);

    /// <summary>EXIF orientation 1..8 (1 = upright).</summary>
    public static int Orientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata md)
            {
                foreach (var q in new[] { "System.Photo.Orientation", "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
                {
                    if (md.ContainsQuery(q) && md.GetQuery(q) is ushort o && o is >= 1 and <= 8) return o;
                }
            }
        }
        catch (NotSupportedException) { }
        catch (InvalidOperationException) { }
        catch (ArgumentException) { }
        return 1;
    }

    public static BitmapSource ApplyOrientation(BitmapSource src, int orientation)
    {
        // EXIF: mirror horizontally first (2,4,5,7), then rotate clockwise
        bool mirror = orientation is 2 or 4 or 5 or 7;
        int rotate = orientation switch { 3 or 4 => 180, 5 or 8 => 270, 6 or 7 => 90, _ => 0 };
        var result = src;
        if (mirror) result = Frozen(new TransformedBitmap(result, new ScaleTransform(-1, 1)));
        if (rotate != 0) result = Frozen(new TransformedBitmap(result, new RotateTransform(rotate)));
        return result;
    }

    // ------------------------------------------------------------------ transforms

    public static BitmapSource Rotate(BitmapSource src, int degrees) => Frozen(new TransformedBitmap(src, new RotateTransform(((degrees % 360) + 360) % 360)));

    public static BitmapSource Flip(BitmapSource src, bool horizontal) => Frozen(new TransformedBitmap(src, new ScaleTransform(horizontal ? -1 : 1, horizontal ? 1 : -1)));

    public static BitmapSource Crop(BitmapSource src, Int32Rect rect)
    {
        var r = new Int32Rect(Math.Clamp(rect.X, 0, src.PixelWidth - 1), Math.Clamp(rect.Y, 0, src.PixelHeight - 1), 0, 0);
        r.Width = Math.Clamp(rect.Width, 1, src.PixelWidth - r.X);
        r.Height = Math.Clamp(rect.Height, 1, src.PixelHeight - r.Y);
        return Frozen(new CroppedBitmap(src, r));
    }

    /// <summary>High-quality resize (Fant filtering via a render pass).</summary>
    public static BitmapSource Resize(BitmapSource src, int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == src.PixelWidth && height == src.PixelHeight) return src;
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.Fant);
        using (var dc = dv.RenderOpen()) dc.DrawImage(src, new Rect(0, 0, width, height));
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        return Frozen(rtb);
    }

    private static BitmapSource Frozen(BitmapSource b) { b.Freeze(); return b; }

    // ------------------------------------------------------------------ encoding

    public static string Extension(ImageFormat f) => f switch
    {
        ImageFormat.Png => ".png", ImageFormat.Jpeg => ".jpg", ImageFormat.Bmp => ".bmp", ImageFormat.Tiff => ".tif",
        ImageFormat.Gif => ".gif", ImageFormat.WebP => ".webp", ImageFormat.Ico => ".ico", _ => ".png",
    };

    public static ImageFormat? FormatFromExtension(string ext) => ext.ToLowerInvariant() switch
    {
        ".png" => ImageFormat.Png, ".jpg" or ".jpeg" or ".jpe" or ".jfif" => ImageFormat.Jpeg, ".bmp" or ".dib" => ImageFormat.Bmp,
        ".tif" or ".tiff" => ImageFormat.Tiff, ".gif" => ImageFormat.Gif, ".webp" => ImageFormat.WebP, ".ico" => ImageFormat.Ico, _ => null,
    };

    public static bool IsLossy(ImageFormat f) => f is ImageFormat.Jpeg or ImageFormat.WebP;

    /// <param name="quality">1..100 for JPEG/WebP; 100 + WebP = lossless.</param>
    /// <param name="metadataFrom">Copy EXIF/XMP from this file when it has the same container format (orientation reset to upright).</param>
    public static byte[] Encode(BitmapSource src, ImageFormat format, int quality = 92, string? metadataFrom = null)
    {
        quality = Math.Clamp(quality, 1, 100);
        if (format == ImageFormat.WebP) return WebPEncoder.Encode(ToBgra(src), quality);
        if (format == ImageFormat.Ico) return EncodeIco(src);
        BitmapEncoder NewEncoder() => format switch
        {
            ImageFormat.Jpeg => new JpegBitmapEncoder { QualityLevel = quality },
            ImageFormat.Bmp => new BmpBitmapEncoder(),
            ImageFormat.Tiff => new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw },
            ImageFormat.Gif => new GifBitmapEncoder(),
            _ => new PngBitmapEncoder(),
        };
        // JPEG and BMP have no alpha: flatten onto white instead of letting transparency turn black
        var frame = format is ImageFormat.Jpeg or ImageFormat.Bmp ? Flatten(src) : src;
        var metadata = metadataFrom != null && FormatFromExtension(Path.GetExtension(metadataFrom)) == format ? CopyMetadata(metadataFrom) : null;
        if (metadata != null)
        {
            try
            {
                var enc = NewEncoder();
                enc.Frames.Add(BitmapFrame.Create(frame, null, metadata, null));
                using var ms = new MemoryStream();
                enc.Save(ms);
                return ms.ToArray();
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException)
            {
                // some metadata layouts can't be re-written by the encoder: fall back to pixels only
            }
        }
        var plain = NewEncoder();
        plain.Frames.Add(BitmapFrame.Create(frame));
        using var output = new MemoryStream();
        plain.Save(output);
        return output.ToArray();
    }

    private static BitmapMetadata? CopyMetadata(string path)
    {
        try
        {
            using var fs = OpenRead(path);
            var frame = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            if (frame.Metadata is not BitmapMetadata md) return null;
            var copy = md.Clone();
            foreach (var q in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
                if (copy.ContainsQuery(q)) copy.SetQuery(q, (ushort)1);
            return copy;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException or IOException or FileFormatException)
        {
            return null;
        }
    }

    public static void Save(BitmapSource src, string path, ImageFormat format, int quality = 92, string? metadataFrom = null)
    {
        var bytes = Encode(src, format, quality, metadataFrom);
        var temp = path + ".fl-partial";
        File.WriteAllBytes(temp, bytes);
        try { File.Move(temp, path); }
        catch { File.Delete(temp); throw; }
    }

    public static byte[] EncodePng(BitmapSource src) => Encode(src, ImageFormat.Png);

    private static BitmapSource Flatten(BitmapSource src)
    {
        if (!src.Format.Equals(PixelFormats.Bgra32) && !src.Format.Equals(PixelFormats.Pbgra32) && !src.Format.Equals(PixelFormats.Prgba64)
            && !src.Format.Equals(PixelFormats.Rgba64) && !src.Format.Equals(PixelFormats.Rgba128Float) && !src.Format.Equals(PixelFormats.Prgba128Float))
            return src;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var r = new Rect(0, 0, src.PixelWidth, src.PixelHeight);
            dc.DrawRectangle(Brushes.White, null, r);
            dc.DrawImage(src, r);
        }
        var rtb = new RenderTargetBitmap(src.PixelWidth, src.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return new FormatConvertedBitmap(rtb, PixelFormats.Bgr32, null, 0);
    }

    /// <summary>Straight-alpha BGRA pixels.</summary>
    public static (int width, int height, byte[] pixels) ToBgra(BitmapSource src)
    {
        var conv = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int stride = conv.PixelWidth * 4;
        var px = new byte[stride * conv.PixelHeight];
        conv.CopyPixels(px, stride, 0);
        return (conv.PixelWidth, conv.PixelHeight, px);
    }

    /// <summary>Multi-size .ico (16–256 px) with PNG-compressed entries, as Windows Vista+ uses.</summary>
    private static byte[] EncodeIco(BitmapSource src)
    {
        var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
        // square the image on a transparent canvas first so icons aren't distorted
        int side = Math.Max(src.PixelWidth, src.PixelHeight);
        var pngs = sizes.Select(s =>
        {
            double scale = (double)s / side;
            int w = Math.Max(1, (int)Math.Round(src.PixelWidth * scale)), h = Math.Max(1, (int)Math.Round(src.PixelHeight * scale));
            var dv = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.Fant);
            using (var dc = dv.RenderOpen()) dc.DrawImage(src, new Rect((s - w) / 2.0, (s - h) / 2.0, w, h));
            var rtb = new RenderTargetBitmap(s, s, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            return (s, png: EncodePng(rtb));
        }).ToList();

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write((ushort)0);
        bw.Write((ushort)1);
        bw.Write((ushort)pngs.Count);
        int offset = 6 + 16 * pngs.Count;
        foreach (var (s, png) in pngs)
        {
            bw.Write((byte)(s >= 256 ? 0 : s));
            bw.Write((byte)(s >= 256 ? 0 : s));
            bw.Write((byte)0);
            bw.Write((byte)0);
            bw.Write((ushort)1);
            bw.Write((ushort)32);
            bw.Write(png.Length);
            bw.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in pngs) bw.Write(png);
        bw.Flush();
        return ms.ToArray();
    }
}

/// <summary>Kept separate so SkiaSharp (and its native library) only loads when WebP output is actually requested.</summary>
internal static class WebPEncoder
{
    public static byte[] Encode((int width, int height, byte[] pixels) img, int quality)
    {
        var info = new SkiaSharp.SKImageInfo(img.width, img.height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Unpremul);
        using var bmp = new SkiaSharp.SKBitmap(info);
        System.Runtime.InteropServices.Marshal.Copy(img.pixels, 0, bmp.GetPixels(), img.pixels.Length);
        using var pixmap = bmp.PeekPixels();
        var options = quality >= 100
            ? new SkiaSharp.SKWebpEncoderOptions(SkiaSharp.SKWebpEncoderCompression.Lossless, 75)
            : new SkiaSharp.SKWebpEncoderOptions(SkiaSharp.SKWebpEncoderCompression.Lossy, quality);
        using var data = pixmap.Encode(options) ?? throw new InvalidOperationException("WebP encoding failed.");
        return data.ToArray();
    }
}
