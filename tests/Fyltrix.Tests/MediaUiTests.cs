using System.Windows;
using Fyltrix.Preview;

namespace Fyltrix.Tests;

[Collection("UI")]
public class MediaUiTests
{
    private static NativeMediaPlayer Native(PreviewPane pane) => pane.NativePlayerForTest; // made on first use

    private static T? FindChild<T>(DependencyObject d) where T : DependencyObject
    {
        foreach (var c in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>())
        {
            if (c is T t) return t;
            if (FindChild<T>(c) is { } r) return r;
        }
        return null;
    }

    [Fact]
    public void Image_viewer_shows_zoom_tools_and_details()
    {
        using var t = new TempDir();
        Sta.Run(() =>
        {
            MediaFixtures.JpegWithOrientation(t["photo.jpg"], 6, MediaFixtures.Marker(400, 200));
            using var h = new PreviewUiTests.Host();
            var text = h.ShowAndWaitForText(t["photo.jpg"], s => s.Contains("Fit"));
            Assert.Contains("200 × 400 px", text); // Chromium applies EXIF orientation too
            Assert.Contains("100%", text);
            // zoom via the keyboard handler and read back the scale shown in the toolbar
            h.Eval("document.dispatchEvent(new KeyboardEvent('keydown', {key: '1'})); ''");
            Sta.WaitUntil(() => h.Eval("document.querySelector('.zoom').textContent") == "100%", "actual size");
            h.Eval("document.dispatchEvent(new KeyboardEvent('keydown', {key: '+'})); ''");
            Sta.WaitUntil(() => h.Eval("document.querySelector('.zoom').textContent") == "125%", "zoom in");
            Sta.WaitUntil(() => h.Eval("document.querySelector('.info').innerText").Contains("TestCam 3000"), "EXIF in details", 10000);
            h.Capture("viewer-image");
        });
    }

    [Fact]
    public void Tiff_is_shown_through_a_png_rendition()
    {
        using var t = new TempDir();
        Sta.Run(() =>
        {
            var enc = new System.Windows.Media.Imaging.TiffBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(MediaFixtures.Marker(120, 60)));
            using (var fs = File.Create(t["scan.tiff"])) enc.Save(fs);
            using var h = new PreviewUiTests.Host();
            var text = h.ShowAndWaitForText(t["scan.tiff"], s => s.Contains("120 × 60"));
            Assert.Contains("/api/image", h.Eval("document.querySelector('.stage img').src"));
        });
    }

    [Fact]
    public void Animated_gif_gets_frame_controls()
    {
        using var t = new TempDir();
        Sta.Run(() =>
        {
            MediaFixtures.AnimatedGif(t["anim.gif"], 3);
            using var h = new PreviewUiTests.Host();
            var text = h.ShowAndWaitForText(t["anim.gif"], s => s.Contains("/ 3"));
            Assert.Contains("Frame", text);
            // pause, then step forward by one frame with the '.' key
            h.Eval("document.dispatchEvent(new KeyboardEvent('keydown', {key: 'k'})); ''");
            var before = h.Eval("[...document.querySelectorAll('.stat')].map(s => s.textContent).find(s => s.startsWith('Frame'))");
            h.Eval("document.dispatchEvent(new KeyboardEvent('keydown', {key: '.'})); ''");
            Sta.WaitUntil(() => h.Eval("[...document.querySelectorAll('.stat')].map(s => s.textContent).find(s => s.startsWith('Frame'))") != before, "frame step");
            h.Capture("viewer-gif");
        });
    }

    [Fact]
    public void Audio_shows_player_and_waveform()
    {
        using var t = new TempDir();
        MediaFixtures.Wav(t["tone.wav"], 2.0);
        Sta.Run(() =>
        {
            using var h = new PreviewUiTests.Host();
            h.ShowAndWaitForText(t["tone.wav"], s => s.Contains("0:02"));
            Assert.Equal("true", h.Eval("String(!!document.querySelector('audio'))"));
            Sta.WaitUntil(() => h.Eval("String(document.querySelector('canvas.wave')?.width > 0)") == "true", "waveform");
            h.Capture("viewer-audio");
        });
    }

    [Fact]
    public void Avi_plays_in_the_native_media_foundation_player()
    {
        using var t = new TempDir();
        MediaFixtures.Avi(t["clip.avi"]);
        Sta.Run(() =>
        {
            using var h = new PreviewUiTests.Host();
            h.Pane.Show(t["clip.avi"], delayMs: 0);
            var native = Native(h.Pane);
            Sta.WaitUntil(() => native.IsVisible && native.CurrentPath == t["clip.avi"], "native player visible");
            var media = FindChild<System.Windows.Controls.MediaElement>(native)!;
            Sta.WaitUntil(() => media.NaturalVideoWidth == 64, "media opened", 15000);
            Assert.Equal(48, media.NaturalVideoHeight);
            Assert.True(Fyltrix.Core.FileAccessPolicy.Readable(t["clip.avi"]));
            Assert.True(Fyltrix.Core.FileAccessPolicy.Deletable(t["clip.avi"])); // preview must not disable Rename/Delete
            Sta.Render(h.Window, "native-avi");

            // switching to another file releases the media file
            h.Pane.Show(t["clip.avi"] + ".missing", delayMs: 0);
            Sta.WaitUntil(() => native.CurrentPath == null, "player closed");
            File.Delete(t["clip.avi"]);
        });
    }

    [Fact]
    public void Undecodable_web_video_falls_back_to_native_player()
    {
        using var t = new TempDir();
        File.WriteAllBytes(t["broken.webm"], new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 1, 2, 3, 4, 5, 6, 7, 8 });
        Sta.Run(() =>
        {
            using var h = new PreviewUiTests.Host();
            h.Pane.Show(t["broken.webm"], delayMs: 0);
            var native = Native(h.Pane);
            Sta.WaitUntil(() => native.IsVisible, "fallback to native player", 15000);
            var msg = FindChild<System.Windows.Controls.Border>(native);
            Sta.WaitUntil(() => FindAllText(native).Any(s => s.StartsWith("Windows can't play this file")), "native failure message", 15000);
        });
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static string VideoState(PreviewUiTests.Host h) =>
        h.Eval("(() => { const v = document.querySelector('video'); return v ? v.readyState + ' ' + v.videoWidth + 'x' + v.videoHeight + ' err=' + (v.error ? v.error.code : '') : 'none'; })()");

    [Fact]
    public void Mkv_h264_plays_in_the_web_player()
    {
        using var t = new TempDir();
        File.Copy(Fixture("h264_aac.mkv"), t["clip.mkv"]);
        Sta.Run(() =>
        {
            using var h = new PreviewUiTests.Host();
            h.Pane.Show(t["clip.mkv"], delayMs: 0);
            Sta.WaitUntil(() => VideoState(h).StartsWith("4 160x90") || VideoState(h).StartsWith("3 160x90"), "MKV decoded", 20000);
            // play, seek and pause through the element the controls drive
            h.Eval("(() => { const v = document.querySelector('video'); v.muted = true; v.play(); return ''; })()");
            Sta.WaitUntil(() => double.Parse(h.Eval("String(document.querySelector('video').currentTime)"), System.Globalization.CultureInfo.InvariantCulture) > 0.2, "playing");
            h.Eval("(() => { const v = document.querySelector('video'); v.pause(); v.currentTime = 1.5; return ''; })()");
            Sta.WaitUntil(() => h.Eval("String(document.querySelector('video').currentTime)").StartsWith("1.5"), "seek");
            Assert.False(Native(h.Pane).IsVisible);
        });
    }

    [Fact]
    public void Mkv_with_a_codec_chromium_cannot_show_falls_back_to_media_foundation()
    {
        using var t = new TempDir();
        File.Copy(Fixture("mpeg4_mp3.mkv"), t["xvid.mkv"]);
        Sta.Run(() =>
        {
            using var h = new PreviewUiTests.Host();
            h.Pane.Show(t["xvid.mkv"], delayMs: 0);
            var native = Native(h.Pane);
            Sta.WaitUntil(() => native.IsVisible && native.CurrentPath == t["xvid.mkv"], "fallback to the native player", 20000);
            var media = FindChild<System.Windows.Controls.MediaElement>(native)!;
            Sta.WaitUntil(() => media.NaturalVideoWidth == 160 || FindAllText(native).Any(s => s.StartsWith("Windows can't play")), "native player opened or failed", 20000);
            Assert.Equal(160, media.NaturalVideoWidth);
        });
    }

    [Fact]
    public void Damaged_and_empty_mkv_files_fail_gracefully_and_rapid_switching_is_safe()
    {
        using var t = new TempDir();
        File.Copy(Fixture("h264_aac.mkv"), t["good.mkv"]);
        File.WriteAllBytes(t["empty.mkv"], Array.Empty<byte>());
        File.WriteAllBytes(t["garbage.mkv"], Enumerable.Range(0, 50_000).Select(i => (byte)(i * 7919 % 251)).ToArray());
        var good = File.ReadAllBytes(t["good.mkv"]);
        File.WriteAllBytes(t["truncated.mkv"], good[..(good.Length / 3)]);
        Sta.Run(() =>
        {
            using var h = new PreviewUiTests.Host();
            var native = Native(h.Pane);
            foreach (var bad in new[] { "empty.mkv", "garbage.mkv" })
            {
                h.Pane.Show(t[bad], delayMs: 0);
                Sta.WaitUntil(() => native.IsVisible && FindAllText(native).Any(s => s.StartsWith("Windows can't play this file"))
                                    && FindAll<System.Windows.Controls.Button>(native).Any(b => b.Content as string == "Open in default player" && b.IsVisible),
                    "failure message for " + bad, 30000);
                Sta.Render(h.Window, "native-failure-" + bad);
            }
            // a truncated file still shows what it can, or a message; either way nothing throws
            h.Pane.Show(t["truncated.mkv"], delayMs: 0);
            Sta.DoEvents(3000);

            // switch quickly between files of every kind; the last one must win and the old players must be released
            var all = new[] { "good.mkv", "garbage.mkv", "empty.mkv", "truncated.mkv" };
            for (int i = 0; i < 24; i++) { h.Pane.Show(t[all[i % all.Length]], delayMs: 0); Sta.DoEvents(i % 3 == 0 ? 400 : 60); }
            h.Pane.Show(t["good.mkv"], delayMs: 0);
            Sta.WaitUntil(() => !native.IsVisible && VideoState(h).Contains("160x90"), "last file shown", 20000);
            Assert.Null(native.CurrentPath);
            // the files can be deleted: nothing holds them open
            foreach (var f in all.Where(f => f != "good.mkv")) File.Delete(t[f]);
        });
    }

    [Fact]
    public void Preview_server_answers_ranges_of_huge_files_in_bounded_chunks()
    {
        using var t = new TempDir();
        using (var fs = File.Create(t["huge.mkv"])) fs.SetLength(5L * 1024 * 1024 * 1024); // 5 GB, nothing written
        Sta.Run(() =>
        {
            using var h = new PreviewUiTests.Host();
            File.WriteAllText(t["a.txt"], "hello");
            h.ShowAndWaitForText(t["a.txt"], s => s.Contains("hello")); // any page on the preview origin
            var src = Fyltrix.Core.PreviewUrls.FileUrl(t["huge.mkv"]);
            string Fetch(string range) =>
                $"(async () => {{ const r = await fetch('{src}', {{ headers: {{ Range: '{range}' }} }}); const b = await r.arrayBuffer(); window.__r = r.status + ' ' + r.headers.get('Content-Range') + ' ' + b.byteLength; }})(); ''";
            foreach (var (range, expect) in new[]
            {
                ("bytes=0-", $"206 bytes 0-4194303/5368709120 4194304"),
                ("bytes=5368709000-", $"206 bytes 5368709000-5368709119/5368709120 120"),
                ("bytes=-100", $"206 bytes 5368709020-5368709119/5368709120 100"),
                ("bytes=9999999999-", "416"),
            })
            {
                h.Eval("window.__r = ''; ''");
                h.Eval(Fetch(range));
                Sta.WaitUntil(() => h.Eval("window.__r") != "", "fetch " + range);
                Assert.StartsWith(expect, h.Eval("window.__r"));
            }
            h.Eval($"(async () => {{ const r = await fetch('{src}', {{ method: 'HEAD' }}); window.__r = r.status + ' ' + r.headers.get('X-File-Size') + ' ' + r.headers.get('Accept-Ranges'); }})(); ''");
            Sta.WaitUntil(() => h.Eval("window.__r").StartsWith("200"), "HEAD");
            Assert.Equal("200 5368709120 bytes", h.Eval("window.__r"));
        });
    }

    [Fact]
    public void Image_editor_rotates_crops_and_saves_a_new_file()
    {
        using var t = new TempDir();
        Sta.Run(() =>
        {
            MediaFixtures.Png(t["pic.png"], MediaFixtures.Marker(200, 100));
            var original = File.ReadAllBytes(t["pic.png"]);
            var owner = new Window();
            Sta.ShowOffscreen(owner);
            var w = new Fyltrix.UI.ImageToolsWindow(owner, t["pic.png"]);
            string? saved = null;
            w.Saved += (_, p) => saved = p;
            Sta.ShowOffscreen(w);
            try
            {
                Sta.WaitUntil(() => w.Current != null, "image loaded");
                w.Apply(b => Fyltrix.Core.Imaging.ImageService.Rotate(b, 90));
                Assert.Equal((100, 200), (w.Current!.PixelWidth, w.Current.PixelHeight));
                w.SetCropRect(new Int32Rect(10, 20, 50, 60));
                w.ApplyCropForTest();
                Assert.Equal((50, 60), (w.Current!.PixelWidth, w.Current.PixelHeight));
                w.SelectFormat(Fyltrix.Core.Imaging.ImageFormat.WebP);
                var task = w.SaveAsync();
                Sta.WaitUntil(() => task.IsCompleted, "save");
                Assert.Equal(t["pic (edited).webp"], task.Result);
                Assert.Equal(t["pic (edited).webp"], saved);
                var frame = System.Windows.Media.Imaging.BitmapDecoder.Create(new Uri(saved!), System.Windows.Media.Imaging.BitmapCreateOptions.None,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
                Assert.Equal((50, 60), (frame.PixelWidth, frame.PixelHeight));
                Assert.Equal(original, File.ReadAllBytes(t["pic.png"])); // the original is never touched
            }
            finally { w.Close(); owner.Close(); }
        });
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject d) where T : DependencyObject
    {
        foreach (var c in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>())
        {
            if (c is T t) yield return t;
            foreach (var x in FindAll<T>(c)) yield return x;
        }
    }

    private static IEnumerable<string> FindAllText(DependencyObject d)
    {
        foreach (var c in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>())
        {
            if (c is System.Windows.Controls.TextBlock tb && tb.Text != null) yield return tb.Text;
            foreach (var s in FindAllText(c)) yield return s;
        }
    }
}
