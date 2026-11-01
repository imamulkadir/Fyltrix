using System.Windows;
using System.Windows.Media.Imaging;
using Fyltrix.Core;
using Fyltrix.Core.Imaging;
using Fyltrix.Core.Media;

namespace Fyltrix.Tests;

[Collection("UI")] // WPF imaging objects are happiest on the shared STA thread
public class ImageServiceTests
{
    [Theory]
    [InlineData(1, false, 0, 0)]   // upright: red stays top-left
    [InlineData(2, false, 1, 0)]   // mirrored: red top-right
    [InlineData(3, false, 1, 1)]   // 180°: red bottom-right
    [InlineData(4, false, 0, 1)]   // flipped vertically: red bottom-left
    [InlineData(5, true, 0, 0)]    // transpose
    [InlineData(6, true, 1, 0)]    // rotate 90° CW: red top-right
    [InlineData(7, true, 1, 1)]    // transverse
    [InlineData(8, true, 0, 1)]    // rotate 270° CW: red bottom-left
    public void Exif_orientation_is_applied(int orientation, bool swapsSides, int redRight, int redBottom)
    {
        using var t = new TempDir();
        var path = t["o.jpg"];
        Sta.Run(() =>
        {
            MediaFixtures.JpegWithOrientation(path, (ushort)orientation);
            var img = ImageService.Load(path);
            Assert.Equal(swapsSides ? (20, 40) : (40, 20), (img.PixelWidth, img.PixelHeight));
            Assert.Equal(swapsSides ? (20, 40) : (40, 20), ImageService.PixelSize(path));
            int x = redRight == 1 ? img.PixelWidth - 3 : 2, y = redBottom == 1 ? img.PixelHeight - 3 : 2;
            Assert.True(MediaFixtures.IsRed(MediaFixtures.PixelAt(img, x, y)), $"expected red at ({x},{y})");
        });
    }

    [Theory]
    [InlineData(ImageFormat.Png)]
    [InlineData(ImageFormat.Jpeg)]
    [InlineData(ImageFormat.Bmp)]
    [InlineData(ImageFormat.Tiff)]
    [InlineData(ImageFormat.Gif)]
    [InlineData(ImageFormat.WebP)]
    [InlineData(ImageFormat.Ico)]
    public void Encode_round_trips_through_available_decoders(ImageFormat format)
    {
        using var t = new TempDir();
        Sta.Run(() =>
        {
            var src = MediaFixtures.Marker(64, 32);
            var path = t["out" + ImageService.Extension(format)];
            ImageService.Save(src, path, format, quality: 90);
            Assert.False(File.Exists(path + ".fl-partial"));
            if (format == ImageFormat.WebP)
            {
                var webp = MediaFixtures.DecodeWebP(File.ReadAllBytes(path));
                Assert.Equal((64, 32), (webp.PixelWidth, webp.PixelHeight));
                Assert.True(MediaFixtures.IsRed(MediaFixtures.PixelAt(webp, 3, 3)));
                Assert.False(MediaFixtures.IsRed(MediaFixtures.PixelAt(webp, 60, 28)));
                return;
            }
            var decoded = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (format == ImageFormat.Ico)
            {
                Assert.Equal(7, decoded.Frames.Count);
                Assert.Contains(decoded.Frames, f => f.PixelWidth == 256);
                return;
            }
            var frame = decoded.Frames[0];
            Assert.Equal((64, 32), (frame.PixelWidth, frame.PixelHeight));
            Assert.True(MediaFixtures.IsRed(MediaFixtures.PixelAt(frame, 3, 3)));
            Assert.False(MediaFixtures.IsRed(MediaFixtures.PixelAt(frame, 60, 28)));
        });
    }

    [Fact]
    public void Lossless_webp_is_exact()
    {
        Sta.Run(() =>
        {
            var src = MediaFixtures.Marker(16, 16);
            var bytes = ImageService.Encode(src, ImageFormat.WebP, 100);
            var frame = MediaFixtures.DecodeWebP(bytes);
            Assert.Equal(ImageService.ToBgra(src).pixels, ImageService.ToBgra(frame).pixels);
        });
    }

    [Fact]
    public void Transforms_change_dimensions_as_expected()
    {
        Sta.Run(() =>
        {
            var src = MediaFixtures.Marker(40, 20);
            Assert.Equal((20, 40), Dim(ImageService.Rotate(src, 90)));
            Assert.Equal((40, 20), Dim(ImageService.Rotate(src, -180)));
            Assert.Equal((10, 5), Dim(ImageService.Resize(src, 10, 5)));
            Assert.Equal((15, 8), Dim(ImageService.Crop(src, new Int32Rect(5, 2, 15, 8))));
            Assert.Equal((5, 3), Dim(ImageService.Crop(src, new Int32Rect(35, 17, 100, 100)))); // clamped to the image
            var flipped = ImageService.Flip(src, horizontal: true);
            Assert.True(MediaFixtures.IsRed(MediaFixtures.PixelAt(flipped, 38, 2)));
        });
        static (int, int) Dim(BitmapSource b) => (b.PixelWidth, b.PixelHeight);
    }

    [Fact]
    public void Resave_keeps_exif_and_resets_orientation()
    {
        using var t = new TempDir();
        Sta.Run(() =>
        {
            MediaFixtures.JpegWithOrientation(t["in.jpg"], 6);
            var upright = ImageService.Load(t["in.jpg"]);
            ImageService.Save(upright, t["out.jpg"], ImageFormat.Jpeg, 95, metadataFrom: t["in.jpg"]);
            var frame = BitmapDecoder.Create(new Uri(t["out.jpg"]), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            var md = (BitmapMetadata)frame.Metadata;
            Assert.Equal("TestCam 3000", md.GetQuery("/app1/ifd/{ushort=272}") as string);
            Assert.Equal((ushort)1, md.GetQuery("/app1/ifd/{ushort=274}"));
            // and loading the copy gives the same upright picture
            Assert.Equal((20, 40), ImageService.PixelSize(t["out.jpg"]));
        });
    }

    [Fact]
    public void Large_images_can_be_decoded_downscaled()
    {
        using var t = new TempDir();
        Sta.Run(() =>
        {
            MediaFixtures.Png(t["big.png"], MediaFixtures.Marker(4000, 2000));
            var small = ImageService.Load(t["big.png"], maxSide: 500);
            Assert.Equal(500, small.PixelWidth);
            Assert.Equal(250, small.PixelHeight);
        });
    }

    [Fact]
    public void Shell_thumbnails_and_icons_are_never_upside_down()
    {
        using var t = new TempDir();
        Sta.Run(() => MediaFixtures.JpegWithOrientation(t["p.jpg"], 1, MediaFixtures.Marker(400, 200)));
        Directory.CreateDirectory(t["folder"]);
        File.WriteAllText(t["doc.pdf"], "not a real pdf: the shell gives its type icon");
        // the shell's row order varies (fresh vs cached, size): ask several sizes, twice each
        foreach (var size in new[] { 48, 64, 96, 128, 256 })
            for (int round = 0; round < 2; round++)
            {
                var photo = ShellThumbnails.Get(t["p.jpg"], size)!;
                Sta.Run(() => Assert.True(MediaFixtures.IsRed(MediaFixtures.PixelAt(photo, 2, 2)), $"photo upside down at {size} px"));

                // folder icon: the tab sticks up at the top left, so just below the top the left is opaque and the right is not
                var folder = ShellThumbnails.Get(t["folder"], size, thumbnailOnly: false)!;
                Sta.Run(() =>
                {
                    int w = folder.PixelWidth, h = folder.PixelHeight, y = (int)(h * 0.19);
                    Assert.True(Alpha(folder, (int)(w * 0.2), y) > 128 && Alpha(folder, (int)(w * 0.8), y) < 64, $"folder icon upside down at {size} px");
                    Assert.True(Alpha(folder, 0, 0) < 64, "icon keeps its transparent background");
                });
                Assert.NotNull(ShellThumbnails.Get(t["doc.pdf"], size, thumbnailOnly: false));
            }
    }

    private static byte Alpha(System.Windows.Media.Imaging.BitmapSource b, int x, int y)
    {
        var px = new byte[4];
        b.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), px, 4, 0);
        return px[3];
    }

    [Fact]
    public void Shell_thumbnail_and_properties_for_an_image()
    {
        using var t = new TempDir();
        Sta.Run(() => MediaFixtures.JpegWithOrientation(t["p.jpg"], 1, MediaFixtures.Marker(400, 200)));
        var thumb = ShellThumbnails.Get(t["p.jpg"], 128);
        Assert.NotNull(thumb);
        Assert.True(thumb!.PixelWidth <= 400 && thumb.PixelWidth > 0);
        Sta.Run(() => Assert.True(MediaFixtures.IsRed(MediaFixtures.PixelAt(thumb, 2, 2)), "thumbnail must not be upside down"));

        var info = ShellProperties.Read(t["p.jpg"], ShellProperties.Group.Image);
        Assert.Contains(info.Items, i => i.Label == "Dimensions" && i.Value.Contains("400"));
        Assert.Contains(info.Items, i => i.Label == "Camera model" && i.Value == "TestCam 3000");
    }
}

public class SubtitleTests
{
    [Fact]
    public void Parses_srt()
    {
        var cues = Subtitles.Parse("1\r\n00:00:01,000 --> 00:00:02,500\r\nHello <i>world</i>\r\n\r\n2\r\n00:00:03,000 --> 00:00:04,000\r\nLine one\r\nLine two\r\n");
        Assert.Equal(2, cues.Count);
        Assert.Equal("Hello world", cues[0].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(2500), cues[0].End);
        Assert.Equal("Line one\nLine two", cues[1].Text);
        Assert.Equal("Hello world", Subtitles.At(cues, TimeSpan.FromSeconds(1.2)));
        Assert.Null(Subtitles.At(cues, TimeSpan.FromSeconds(2.7)));
        Assert.Null(Subtitles.At(cues, TimeSpan.FromSeconds(0.5)));
    }

    [Fact]
    public void Parses_vtt_with_hours_and_short_times()
    {
        var cues = Subtitles.Parse("WEBVTT\n\n01:00:00.000 --> 01:00:01.000 align:start\nLate cue\n\n00:05.000 --> 00:06.000\nShort form\n");
        Assert.Equal(2, cues.Count);
        Assert.Equal("Short form", cues[0].Text);
        Assert.Equal(TimeSpan.FromHours(1), cues[1].Start);
    }

    [Fact]
    public void Finds_sidecar_files()
    {
        using var t = new TempDir();
        var video = t.File("movie.mp4");
        Assert.Null(Subtitles.FindSidecar(video));
        t.File("movie.en.srt", "x");
        Assert.Equal(t["movie.en.srt"], Subtitles.FindSidecar(video));
        t.File("movie.vtt", "x");
        Assert.Equal(t["movie.vtt"], Subtitles.FindSidecar(video));
    }
}

public class FileKindTests
{
    [Theory]
    [InlineData("a.mkv", PreviewKind.Media)]
    [InlineData("a.flac", PreviewKind.Media)]
    [InlineData("a.avi", PreviewKind.NativeMedia)]
    [InlineData("a.wmv", PreviewKind.NativeMedia)]
    [InlineData("a.wma", PreviewKind.NativeMedia)]
    [InlineData("a.heic", PreviewKind.Image)]
    [InlineData("a.tiff", PreviewKind.Image)]
    [InlineData("a.zip", PreviewKind.Archive)]
    [InlineData("a.tar.gz", PreviewKind.Archive)]
    [InlineData("a.svg", PreviewKind.Svg)]
    [InlineData("a.pdf", PreviewKind.Pdf)]
    public void Kinds_by_extension(string name, PreviewKind kind) => Assert.Equal(kind, FileKinds.KindFor(name, false));

    [Fact]
    public void Ts_is_typescript_unless_it_is_an_mpeg_transport_stream()
    {
        using var t = new TempDir();
        var code = t.File("app.ts", "export const x: number = 1;\n");
        Assert.Equal(PreviewKind.Code, FileKinds.KindFor(code, false));
        Assert.Equal("typescript", FileKinds.LanguageFor(code));
        var video = t["clip.ts"];
        var bytes = new byte[188 * 4];
        for (int i = 0; i < bytes.Length; i += 188) bytes[i] = 0x47;
        File.WriteAllBytes(video, bytes);
        Assert.Equal(PreviewKind.NativeMedia, FileKinds.KindFor(video, false));
    }
}
