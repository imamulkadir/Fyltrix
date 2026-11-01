using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Fyltrix.Tests;

/// <summary>Generates small, real media files for tests (no binary fixtures in the repo).</summary>
public static class MediaFixtures
{
    /// <summary>Verify encoded WebP independently of optional Windows Store WIC codecs.</summary>
    public static BitmapSource DecodeWebP(byte[] bytes)
    {
        using var stream = new SkiaSharp.SKMemoryStream(bytes);
        using var codec = SkiaSharp.SKCodec.Create(stream);
        Assert.NotNull(codec);
        Assert.Equal(SkiaSharp.SKEncodedImageFormat.Webp, codec.EncodedFormat);
        var info = new SkiaSharp.SKImageInfo(codec.Info.Width, codec.Info.Height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Unpremul);
        using var bitmap = new SkiaSharp.SKBitmap(info);
        Assert.Equal(SkiaSharp.SKCodecResult.Success, codec.GetPixels(info, bitmap.GetPixels()));
        var frame = BitmapSource.Create(info.Width, info.Height, 96, 96, PixelFormats.Bgra32, null, bitmap.GetPixels(), bitmap.ByteCount, bitmap.RowBytes);
        frame.Freeze(); return frame;
    }

    /// <summary>Image whose top-left quadrant is red and the rest blue: orientation is checkable by pixel.</summary>
    public static BitmapSource Marker(int w = 40, int h = 20)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bool red = x < w / 2 && y < h / 2;
                px[i] = (byte)(red ? 0 : 255); px[i + 1] = 0; px[i + 2] = (byte)(red ? 255 : 0); px[i + 3] = 255;
            }
        var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        b.Freeze();
        return b;
    }

    public static (byte b, byte g, byte r) PixelAt(BitmapSource src, int x, int y)
    {
        var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        var px = new byte[4];
        conv.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return (px[0], px[1], px[2]);
    }

    public static bool IsRed((byte b, byte g, byte r) p) => p.r > 200 && p.b < 60;

    /// <summary>JPEG with an EXIF orientation tag and camera model.</summary>
    public static void JpegWithOrientation(string path, ushort orientation, BitmapSource? src = null)
    {
        src ??= Marker();
        var md = new BitmapMetadata("jpg");
        md.SetQuery("/app1/ifd/{ushort=274}", orientation);
        md.SetQuery("/app1/ifd/{ushort=272}", "TestCam 3000");
        var enc = new JpegBitmapEncoder { QualityLevel = 100 };
        enc.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(src, PixelFormats.Bgr24, null, 0), null, md, null));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    public static void Png(string path, BitmapSource? src = null)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src ?? Marker()));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    /// <summary>Multi-frame GIF (each frame a different colour).</summary>
    public static void AnimatedGif(string path, int frames = 3)
    {
        var enc = new GifBitmapEncoder();
        var colors = new[] { Colors.Red, Colors.Lime, Colors.Blue, Colors.Yellow };
        for (int f = 0; f < frames; f++)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawRectangle(new SolidColorBrush(colors[f % colors.Length]), null, new Rect(0, 0, 32, 32));
            var rtb = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            enc.Frames.Add(BitmapFrame.Create(rtb));
        }
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    /// <summary>16-bit mono PCM sine wave.</summary>
    public static void Wav(string path, double seconds = 1.0, int rate = 8000)
    {
        int samples = (int)(seconds * rate);
        using var bw = new BinaryWriter(File.Create(path));
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + samples * 2);
        bw.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        bw.Write(16); bw.Write((short)1); bw.Write((short)1); bw.Write(rate); bw.Write(rate * 2); bw.Write((short)2); bw.Write((short)16);
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(samples * 2);
        for (int i = 0; i < samples; i++) bw.Write((short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 12000));
    }

    /// <summary>Uncompressed RGB24 AVI (Media Foundation plays it; Chromium doesn't).</summary>
    public static void Avi(string path, int w = 64, int h = 48, int frames = 10, int fps = 10)
    {
        int stride = w * 3, frameSize = stride * h;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        void Four(string s) => bw.Write(Encoding.ASCII.GetBytes(s));
        Four("RIFF"); long riffSize = ms.Position; bw.Write(0); Four("AVI ");
        Four("LIST"); long hdrlSize = ms.Position; bw.Write(0); Four("hdrl");
        Four("avih"); bw.Write(56);
        bw.Write(1_000_000 / fps); bw.Write(frameSize * fps); bw.Write(0); bw.Write(0x10); bw.Write(frames); bw.Write(0); bw.Write(1);
        bw.Write(frameSize); bw.Write(w); bw.Write(h); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
        Four("LIST"); long strlSize = ms.Position; bw.Write(0); Four("strl");
        Four("strh"); bw.Write(56);
        Four("vids"); Four("DIB "); bw.Write(0); bw.Write((short)0); bw.Write((short)0); bw.Write(0); bw.Write(1); bw.Write(fps); bw.Write(0); bw.Write(frames);
        bw.Write(frameSize); bw.Write(-1); bw.Write(0); bw.Write((short)0); bw.Write((short)0); bw.Write((short)w); bw.Write((short)h);
        Four("strf"); bw.Write(40);
        bw.Write(40); bw.Write(w); bw.Write(h); bw.Write((short)1); bw.Write((short)24); bw.Write(0); bw.Write(frameSize); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
        Patch(bw, strlSize); Patch(bw, hdrlSize);
        Four("LIST"); long moviSize = ms.Position; bw.Write(0); long moviStart = ms.Position; Four("movi");
        var offsets = new List<long>();
        for (int f = 0; f < frames; f++)
        {
            offsets.Add(ms.Position - moviStart);
            Four("00db"); bw.Write(frameSize);
            var frame = new byte[frameSize];
            for (int i = 0; i < frameSize; i += 3) { frame[i] = (byte)(f * 25); frame[i + 1] = 128; frame[i + 2] = (byte)(255 - f * 25); }
            bw.Write(frame);
        }
        Patch(bw, moviSize);
        Four("idx1"); bw.Write(frames * 16);
        foreach (var o in offsets) { Four("00db"); bw.Write(0x10); bw.Write((int)o); bw.Write(frameSize); }
        Patch(bw, riffSize);
        bw.Flush();
        File.WriteAllBytes(path, ms.ToArray());
    }

    private static void Patch(BinaryWriter bw, long sizePos)
    {
        long end = bw.BaseStream.Position;
        bw.BaseStream.Position = sizePos;
        bw.Write((int)(end - sizePos - 4));
        bw.BaseStream.Position = end;
    }
}
