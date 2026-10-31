using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Fyltrix.Core;
using Fyltrix.Core.Media;
using Fyltrix.UI;

namespace Fyltrix.Tests;

[Collection("UI")]
public class ThumbnailGridTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static List<ThumbnailImage> Tiles(FolderView view) =>
        VisualTree.FindDescendants<ThumbnailImage>(view.List).ToList();

    private static ThumbnailImage TileFor(FolderView view, string name) =>
        Tiles(view).First(t => t.Item?.Name == name);

    [Fact]
    public void Thumbnails_show_real_pictures_and_switching_views_keeps_the_selection()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(t["pics"]);
        MediaFixtures.Png(t["pics/wide.png"], MediaFixtures.Marker(400, 200));
        MediaFixtures.JpegWithOrientation(t["pics/photo.jpg"], 1, MediaFixtures.Marker(300, 300));
        File.Copy(Fixture("h264_aac.mkv"), t["pics/clip.mkv"]);
        t.File("pics/notes.txt", "text");
        Directory.CreateDirectory(t["pics/folder"]);
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t["pics"]);
            try
            {
                var view = w.View!;
                // select two items in Details, then switch to large thumbnails
                view.SelectPaths(new[] { t["pics/wide.png"], t["pics/notes.txt"] });
                w.SetView(160);
                Assert.Equal(ViewMode.Tiles, view.Mode);
                Assert.Equal(160, w.Settings.ViewFor(Fyltrix.Core.AppSettings.ViewKey(t["pics"]))); // remembered for this folder
                Assert.Equal(0, w.Settings.ViewTileSize); // the default is unchanged
                Sta.WaitUntil(() => Tiles(view).Count == 5, "tiles realized after switching at runtime");
                Assert.Equal(new[] { "notes.txt", "wide.png" }, view.SelectedItems.Select(i => i.Name).OrderBy(n => n));

                // real thumbnails: the 2:1 picture comes back 2:1, the 16:9 video comes back as a video frame
                Sta.WaitUntil(() => TileFor(view, "wide.png").HasThumbnail && TileFor(view, "photo.jpg").HasThumbnail, "image thumbnails", 20000);
                var wide = ThumbnailOf(TileFor(view, "wide.png"));
                Assert.InRange(wide.PixelWidth / (double)wide.PixelHeight, 1.9, 2.1);
                Sta.WaitUntil(() => TileFor(view, "clip.mkv").HasThumbnail, "video thumbnail", 30000);
                var clip = ThumbnailOf(TileFor(view, "clip.mkv"));
                Assert.InRange(clip.PixelWidth / (double)clip.PixelHeight, 1.6, 1.9); // a frame, not a square icon
                Sta.WaitUntil(() => TileFor(view, "notes.txt").HasThumbnail && TileFor(view, "folder").HasThumbnail, "icons for the rest", 20000);
                Sta.Render(w, "thumbnail-grid");

                // back to Details: same selection, no errors
                w.SetView(0);
                Assert.Equal(ViewMode.Details, view.Mode);
                Assert.Equal(new[] { "notes.txt", "wide.png" }, view.SelectedItems.Select(i => i.Name).OrderBy(n => n));
                Assert.Empty(Tiles(view));

                // returning to the folder in tile view uses the cache (no new work for unchanged files)
                w.SetView(96);
                view.Navigate(t.Path);
                Sta.WaitUntil(() => view.CurrentPath == t.Path, "away");
                view.Navigate(t["pics"]);
                Sta.WaitUntil(() => view.CurrentPath == t["pics"] && Tiles(view).Count == 5, "back");
                Sta.WaitUntil(() => TileFor(view, "wide.png").HasThumbnail, "cached thumbnail");
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(view).DpiScaleX;
                var wi = view.Items.Single(i => i.Name == "wide.png");
                Assert.NotNull(ThumbnailService.Shared.TryGetCached(wi.FullPath, (int)Math.Ceiling(96 * dpi), wi.Modified, wi.Size));
            }
            finally { w.SetView(0); w.Close(); }
        });
    }

    private static BitmapSource ThumbnailOf(ThumbnailImage tile) =>
        (BitmapSource)tile.Children.OfType<Image>().Last().Source;

    [Fact]
    public void Thousands_of_files_stay_virtualized_and_keyboard_moves_through_the_grid()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(t["many"]);
        for (int i = 0; i < 5000; i++) File.WriteAllText(t[$"many/file{i:0000}.txt"], "");
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t["many"], s => s.ViewTileSize = 96);
            try
            {
                var view = w.View!;
                Assert.Equal(ViewMode.Tiles, view.Mode); // the saved preference applies on start
                Sta.WaitUntil(() => view.TilePanel != null && Tiles(view).Count > 0, "grid shown");
                var panel = view.TilePanel!;
                int realized = Tiles(view).Count;
                Assert.InRange(realized, 1, 400); // only the rows on screen, not 5,000 tiles
                int cols = panel.Columns;
                Assert.True(cols > 1);

                // keyboard: Right moves one tile, Down one row, End to the last item (which gets realized)
                view.SelectPaths(new[] { t["many/file0000.txt"] });
                Sta.DoEvents(100);
                Press(view, Key.Right);
                Assert.Equal("file0001.txt", view.FocusedItem!.Name);
                Press(view, Key.Down);
                Assert.Equal($"file{1 + cols:0000}.txt", view.FocusedItem!.Name);
                Press(view, Key.Down, ModifierKeys.Shift);
                Assert.Equal(cols + 1, view.SelectedItems.Count);

                var sw = Stopwatch.StartNew();
                Press(view, Key.End);
                Sta.DoEvents(50);
                Assert.Equal("file4999.txt", view.FocusedItem!.Name);
                Assert.True(sw.ElapsedMilliseconds < 2000, $"End took {sw.ElapsedMilliseconds} ms");
                Assert.Contains(Tiles(view), x => x.Item?.Name == "file4999.txt");
                Assert.InRange(Tiles(view).Count, 1, 400);

                // scrolling back up through the whole folder stays bounded too
                int pages = 0;
                while (panel.VerticalOffset > 0 && pages++ < 1000)
                {
                    panel.PageUp();
                    Sta.DoEvents(1);
                    Assert.InRange(Tiles(view).Count, 1, 400);
                }
                Assert.Equal(0, panel.VerticalOffset);
                Press(view, Key.Home);
                Assert.Equal("file0000.txt", view.FocusedItem!.Name);
            }
            finally { w.SetView(0); w.Close(); }
        });
    }

    private static void Press(FolderView view, Key key, ModifierKeys mods = ModifierKeys.None)
    {
        // raise the grid's key handling directly (no input is sent to the desktop); modifiers via a test hook
        FolderView.TestModifiers = mods;
        try
        {
            var source = PresentationSource.FromVisual(view.List) ?? throw new InvalidOperationException("no presentation source");
            var e = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = UIElement.PreviewKeyDownEvent };
            view.List.RaiseEvent(e);
        }
        finally { FolderView.TestModifiers = null; }
        Sta.DoEvents(30);
    }

    [Fact]
    public void Memory_cache_is_bounded_and_keyed_by_change_time()
    {
        using var t = new TempDir();
        MediaFixtures.Png(t["a.png"], MediaFixtures.Marker(200, 100));
        var svc = new ThumbnailService(workers: 1, memoryBudgetBytes: 40_000); // room for one 128 × 64 thumbnail (32 KB)
        var fi = new FileInfo(t["a.png"]);
        BitmapSource? got = null;
        using var done = new ManualResetEventSlim();
        svc.Request(new ThumbnailRequest { Path = fi.FullName, Size = 100, Modified = fi.LastWriteTime, Length = fi.Length, Done = b => { got = b; done.Set(); } });
        Assert.True(done.Wait(15000));
        Assert.NotNull(got);
        Assert.NotNull(svc.TryGetCached(fi.FullName, 100, fi.LastWriteTime, fi.Length));
        Assert.Null(svc.TryGetCached(fi.FullName, 100, fi.LastWriteTime.AddSeconds(1), fi.Length)); // changed file: miss
        Assert.True(svc.CachedBytes <= 40_000);

        MediaFixtures.Png(t["b.png"], MediaFixtures.Marker(200, 100));
        var fb = new FileInfo(t["b.png"]);
        done.Reset();
        svc.Request(new ThumbnailRequest { Path = fb.FullName, Size = 100, Modified = fb.LastWriteTime, Length = fb.Length, Done = _ => done.Set() });
        Assert.True(done.Wait(15000));
        Assert.Equal(1, svc.CachedCount); // the older one was evicted
        Assert.Null(svc.TryGetCached(fi.FullName, 100, fi.LastWriteTime, fi.Length));

        // a cancelled request is skipped
        var cancelled = new ThumbnailRequest { Path = fi.FullName, Size = 100, Modified = fi.LastWriteTime, Length = fi.Length, Done = _ => throw new InvalidOperationException("should not run") };
        cancelled.Cancel();
        svc.Request(cancelled);
        Thread.Sleep(300);
        svc.Dispose();
    }

    [Fact]
    public void Ffmpeg_extracts_a_video_frame_into_the_bounded_disk_cache()
    {
        var ffmpeg = Core.Conversion.Ffmpeg.Locate(null);
        if (ffmpeg == null) return; // FFmpeg is optional
        using var t = new TempDir();
        File.Copy(Fixture("mpeg4_mp3.mkv"), t["v.mkv"]);
        var old = (ThumbnailService.DiskCacheFolder, ThumbnailService.FfmpegPath);
        ThumbnailService.DiskCacheFolder = t["cache"];
        ThumbnailService.FfmpegPath = () => ffmpeg;
        try
        {
            var fi = new FileInfo(t["v.mkv"]);
            var frame = ThumbnailService.FfmpegFrame(fi.FullName, 128, fi.LastWriteTime, fi.Length);
            Assert.NotNull(frame);
            Assert.Equal(128, frame!.PixelWidth); // 160x90 scaled to 128 wide
            Assert.Single(Directory.GetFiles(t["cache"], "*.png"));
            // the second time it comes from the disk cache, even with FFmpeg gone
            ThumbnailService.FfmpegPath = () => ffmpeg;
            File.Delete(t["v.mkv"]);
            Assert.NotNull(ThumbnailService.FfmpegFrame(fi.FullName, 128, fi.LastWriteTime, fi.Length));
            ThumbnailService.DiskCacheBudgetBytes = 1;
            ThumbnailService.TrimDiskCache();
            Assert.Empty(Directory.GetFiles(t["cache"], "*.png"));
        }
        finally
        {
            (ThumbnailService.DiskCacheFolder, ThumbnailService.FfmpegPath) = old;
            ThumbnailService.DiskCacheBudgetBytes = 256L * 1024 * 1024;
        }
    }
}
