using System.Windows;
using Fyltrix.Core;
using Fyltrix.Core.Conversion;
using Fyltrix.Core.Imaging;
using Fyltrix.UI;

namespace Fyltrix.Tests;

public class FfmpegTests
{
    [Fact]
    public void Builds_expected_arguments()
    {
        var mp4 = Ffmpeg.BuildArgs("in.mkv", "out.tmp", Ffmpeg.Preset("mp4")!, MediaQuality.High, 720);
        Assert.Equal(new[] { "-hide_banner", "-nostdin", "-y", "-i", "in.mkv" }, mp4.Take(5));
        Assert.Contains("libx264", mp4);
        Assert.Equal("18", mp4[mp4.IndexOf("-crf") + 1]);
        Assert.Equal("scale=-2:'min(720,ih)'", mp4[mp4.IndexOf("-vf") + 1]);
        Assert.Equal(new[] { "-f", "mp4", "out.tmp" }, mp4.TakeLast(3));

        var mp3 = Ffmpeg.BuildArgs("a.wav", "o", Ffmpeg.Preset("mp3")!, MediaQuality.Small, null);
        Assert.Contains("-vn", mp3);
        Assert.DoesNotContain("-vf", mp3);
        Assert.Equal("6", mp3[mp3.IndexOf("-q:a") + 1]);
        Assert.Equal("ipod", Ffmpeg.BuildArgs("a", "o", Ffmpeg.Preset("m4a")!, MediaQuality.Balanced, null).TakeLast(2).First());
        Assert.Contains(Ffmpeg.BuildArgs("a", "o", Ffmpeg.Preset("gif")!, MediaQuality.Balanced, null), a => a.Contains("palettegen"));
        foreach (var p in Ffmpeg.VideoPresets.Concat(Ffmpeg.AudioPresets)) Assert.NotEmpty(Ffmpeg.BuildArgs("i", "o", p, MediaQuality.Balanced, 480));
    }

    [Fact]
    public void Parses_progress_lines()
    {
        Assert.Equal(62.03, Ffmpeg.ParseDuration("  Duration: 00:01:02.03, start: 0.000000, bitrate: 1411 kb/s")!.Value.TotalSeconds, 3);
        Assert.Equal(3723.5, Ffmpeg.ParseTime("frame=  120 fps= 30 q=28.0 size=  512kB time=01:02:03.50 bitrate= 400.0kbits/s")!.Value.TotalSeconds, 3);
        Assert.Null(Ffmpeg.ParseTime("Stream #0:0: Video: h264"));
    }

    [Fact]
    public void Locate_uses_configured_path_or_folder()
    {
        using var t = new TempDir();
        var fake = t.File("bin/ffmpeg.exe", "not really");
        Assert.Equal(fake, Ffmpeg.Locate(fake));
        Assert.Equal(fake, Ffmpeg.Locate(t["bin"]));
        Assert.NotEqual(t["nowhere/ffmpeg.exe"], Ffmpeg.Locate(t["nowhere/ffmpeg.exe"]));
    }

    [Fact]
    public async Task Failing_converter_reports_error_and_leaves_no_partial_file()
    {
        using var t = new TempDir();
        var input = t.File("in.wav", "x");
        var fakeFfmpeg = Path.Combine(Environment.SystemDirectory, "where.exe"); // exits with an error for these arguments
        await Assert.ThrowsAsync<IOException>(() => Ffmpeg.ConvertAsync(fakeFfmpeg, input, t["out.mp3"], Ffmpeg.Preset("mp3")!, MediaQuality.Balanced, null, null, CancellationToken.None));
        Assert.False(File.Exists(t["out.mp3"]));
        Assert.Single(Directory.GetFiles(t.Path)); // only the input
    }
}

[Collection("UI")]
public class ConvertUiTests
{
    [Fact]
    public void Converts_images_in_a_batch()
    {
        using var t = new TempDir();
        Sta.Run(() =>
        {
            MediaFixtures.Png(t["a.png"], MediaFixtures.Marker(400, 200));
            MediaFixtures.JpegWithOrientation(t["b.jpg"], 6, MediaFixtures.Marker(400, 200));
            var owner = new Window();
            Sta.ShowOffscreen(owner);
            AppSettings.Folder = t.Dir("settings");
            var w = new ConvertWindow(owner, new AppSettings(), new[] { t["a.png"], t["b.jpg"] });
            var created = new List<string>();
            w.FilesCreated += (_, f) => created.AddRange(f);
            Sta.ShowOffscreen(w);
            try
            {
                w.SelectFormat(ImageFormat.WebP);
                var run = w.RunAsync();
                Sta.WaitUntil(() => run.IsCompleted, "conversion");
                Assert.Equal(new[] { t["a.webp"], t["b.webp"] }, created);
                Assert.All(w.Rows, r => Assert.StartsWith("✓", r.Result));
                var webp = MediaFixtures.DecodeWebP(File.ReadAllBytes(t["b.webp"]));
                Assert.Equal((200, 400), (webp.PixelWidth, webp.PixelHeight)); // EXIF orientation applied before encoding
                Sta.Render(w, "convert");
            }
            finally { w.Close(); owner.Close(); }
        });
    }
}
