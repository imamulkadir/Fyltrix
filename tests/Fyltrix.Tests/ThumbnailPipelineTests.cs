using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fyltrix.Core;
using Fyltrix.Core.Media;
using Fyltrix.UI;

namespace Fyltrix.Tests;

[Collection("UI")]
public class ThumbnailPipelineTests
{
    private static BitmapSource Bitmap(int size)
    { var b = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, new byte[size * size * 4], size * 4); b.Freeze(); return b; }
    private static ThumbnailRequest Request(string path, Action<BitmapSource?> done, bool visible = true, int size = 64) =>
        new() { Path = path, Size = size, Modified = new DateTime(2026, 1, 1), Length = 12, IsVisible = visible, Done = done };

    [Fact]
    public void Identical_live_requests_share_one_decode_and_cancelled_subscribers_are_ignored()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        int decodes = 0, completed = 0, cancelledCallbacks = 0;
        var bitmap = Bitmap(64);
        using var service = new ThumbnailService(1, 1_000_000, _ =>
        { Interlocked.Increment(ref decodes); entered.Set(); release.Wait(5000); return bitmap; });
        using var done = new CountdownEvent(10);
        service.Request(Request("same.png", _ => { Interlocked.Increment(ref completed); done.Signal(); }));
        Assert.True(entered.Wait(5000));
        try
        {
            for (int i = 1; i < 10; i++) service.Request(Request("same.png", b => { Assert.Same(bitmap, b); Interlocked.Increment(ref completed); done.Signal(); }));
            var cancelled = Request("same.png", _ => Interlocked.Increment(ref cancelledCallbacks));
            service.Request(cancelled); cancelled.Cancel();
            Assert.Equal(10, service.PendingCount);
        }
        finally { release.Set(); }
        Assert.True(done.Wait(5000)); Assert.Equal(1, decodes); Assert.Equal(10, completed);
        var cachedCancelled = Request("same.png", _ => Interlocked.Increment(ref cancelledCallbacks));
        cachedCancelled.Cancel(); service.Request(cachedCancelled);
        Assert.Equal(1, decodes); Assert.Equal(0, cancelledCallbacks);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Visible_and_newly_promoted_requests_precede_prefetch(bool promote)
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var finished = new CountdownEvent(4);
        var order = new List<string>(); var bitmap = Bitmap(64);
        using var service = new ThumbnailService(1, 1_000_000, request =>
        {
            lock (order) order.Add(request.Path);
            if (request.Path == "block") { entered.Set(); release.Wait(5000); }
            return bitmap;
        });
        service.Request(Request("block", _ => finished.Signal())); Assert.True(entered.Wait(5000));
        try
        {
            var first = Request("prefetch-first", _ => finished.Signal(), false);
            service.Request(first);
            service.Request(Request("visible", _ => finished.Signal(), !promote));
            service.Request(Request("prefetch-last", _ => finished.Signal(), false));
            if (promote) first.IsVisible = true;
        }
        finally { release.Set(); }
        Assert.True(finished.Wait(5000));
        Assert.Equal(promote ? "prefetch-first" : "visible", order[1]);
    }

    [Fact]
    public void Fast_scroll_queue_is_bounded_and_disposal_cancels_obsolete_work()
    {
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var bitmap = Bitmap(64); int decodes = 0, callbacks = 0;
        using var service = new ThumbnailService(1, 1_000_000, _ => { Interlocked.Increment(ref decodes); entered.Set(); release.Wait(5000); return bitmap; });
        service.Request(Request("block", _ => Interlocked.Increment(ref callbacks))); Assert.True(entered.Wait(5000));
        try
        {
            var requests = Enumerable.Range(0, 2000).Select(i => Request("prefetch" + i, _ => Interlocked.Increment(ref callbacks), false)).ToArray();
            foreach (var r in requests) service.Request(r);
            Assert.InRange(service.PendingCount, 1, 800);
            Assert.Contains(requests, r => r.Cancelled);
            service.Dispose(); Assert.Equal(0, service.PendingCount);
            Assert.All(requests, r => Assert.True(r.Cancelled));
        }
        finally { release.Set(); }
        Assert.True(SpinWait.SpinUntil(() => service.WorkingCount == 0, 5000));
        Assert.Equal(1, decodes); Assert.Equal(0, callbacks);
    }

    [Fact]
    public void Larger_images_are_reused_but_smaller_or_stale_entries_are_not()
    {
        int decodes = 0;
        using var service = new ThumbnailService(1, 2_000_000, request => { Interlocked.Increment(ref decodes); return Bitmap(request.Size); });
        using var done = new ManualResetEventSlim();
        var first = Request("photo", _ => done.Set(), size: 256); service.Request(first); Assert.True(done.Wait(5000));
        Assert.NotNull(service.TryGetCached(first.Path, 96, first.Modified, first.Length));
        Assert.Null(service.TryGetCached(first.Path, 512, first.Modified, first.Length));
        Assert.Null(service.TryGetCached(first.Path, 96, first.Modified.AddTicks(1), first.Length));
        Assert.Null(service.TryGetCached(first.Path, 96, first.Modified, first.Length + 1));
        done.Reset(); service.Request(Request("photo", _ => done.Set(), size: 96)); Assert.True(done.Wait(5000));
        Assert.Equal(1, decodes); Assert.InRange(service.CachedBytes, 1, 2_000_000);
    }

    [Theory]
    [InlineData(48, 1)] [InlineData(48, 1.25)] [InlineData(48, 2)] [InlineData(48, 4)]
    [InlineData(96, 1)] [InlineData(96, 1.25)] [InlineData(96, 2)] [InlineData(96, 4)]
    [InlineData(160, 1)] [InlineData(160, 1.25)] [InlineData(160, 2)] [InlineData(160, 4)]
    [InlineData(256, 1)] [InlineData(256, 1.25)] [InlineData(256, 2)] [InlineData(256, 4)] [InlineData(256, 5)]
    public void Requests_include_physical_DPI_without_rounding_down(double size, double dpi)
    {
        int physical = ThumbnailImage.PixelsFor(size, dpi);
        Assert.Equal((int)Math.Ceiling(size * dpi), physical);
        Assert.True(ThumbnailService.Bucket(physical) >= physical);
    }

    [Fact]
    public void Old_completion_queued_before_a_size_change_never_displays_low_resolution_pixels()
    {
        using var temp = new TempDir(); temp.File("photo.png");
        using var releaseOld = new ManualResetEventSlim();
        int producerThread = 0;
        using var service = new ThumbnailService(1, 2_000_000, request =>
        {
            if (request.Size == 64) { producerThread = Environment.CurrentManagedThreadId; releaseOld.Wait(5000); }
            return Bitmap(request.Size);
        });
        Sta.Run(() =>
        {
            var tile = new ThumbnailImage { Service = service, Item = FileItem.FromInfo(new FileInfo(temp["photo.png"])), Size = 48 };
            var window = new Window { Content = tile, Width = 300, Height = 300 };
            using var posted = new ManualResetEventSlim();
            System.Windows.Threading.DispatcherHookEventHandler hook = (_, e) =>
            { if (Environment.CurrentManagedThreadId == Volatile.Read(ref producerThread) && e.Operation.Priority == System.Windows.Threading.DispatcherPriority.Normal) posted.Set(); };
            try
            {
                Sta.ShowOffscreen(window);
                Sta.WaitUntil(() => tile.IsLoaded, "tile loaded");
                tile.Dispatcher.Hooks.OperationPosted += hook;
                // Only this worker can signal the hook. The UI waits without pumping,
                // so the old completion is definitely queued, never displayed yet.
                releaseOld.Set();
                Assert.True(posted.Wait(5000));
                tile.Dispatcher.Hooks.OperationPosted -= hook;
                var shown = new List<int>();
                var image = tile.Children.OfType<Image>().Last();
                var descriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image));
                EventHandler changed = (_, _) => { if (image.Source is BitmapSource b) shown.Add(b.PixelWidth); };
                descriptor.AddValueChanged(image, changed);
                try
                {
                    tile.Size = 256;
                    Sta.WaitUntil(() => tile.HasThumbnail && image.Source is BitmapSource b && b.PixelWidth == 256, "new-sized thumbnail");
                    Assert.NotEmpty(shown); Assert.All(shown, width => Assert.Equal(256, width));
                    Assert.InRange(image.MaxWidth, 1, 256.1);
                }
                finally { descriptor.RemoveValueChanged(image, changed); }
            }
            finally { releaseOld.Set(); tile.Dispatcher.Hooks.OperationPosted -= hook; window.Close(); }
        });
    }

    [Fact]
    public async Task Cancelling_a_blocked_ffmpeg_frame_stops_the_owned_process_and_removes_temporary_output()
    {
        var ffmpeg = Fyltrix.Core.Conversion.Ffmpeg.Locate(null);
        if (ffmpeg == null) return; // optional application dependency, as in existing media tests
        using var temp = new TempDir();
        var previous = (ThumbnailService.DiskCacheFolder, ThumbnailService.FfmpegPath);
        string name = "Fyltrix-thumb-test-" + Guid.NewGuid().ToString("N");
        using var pipe = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut, 1,
            System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
        var request = Request(@"\\.\pipe\" + name, _ => { });
        ThumbnailService.DiskCacheFolder = temp["cache"];
        ThumbnailService.FfmpegPath = () => ffmpeg;
        var load = Task.Run(() => ThumbnailService.FfmpegFrame(request.Path, 128, request.Modified, 0, request));
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await pipe.WaitForConnectionAsync(timeout.Token);
            request.Cancel();
            Assert.Null(await load.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Empty(Directory.GetFiles(temp["cache"], "*.tmp.png"));
            Assert.Empty(Directory.GetFiles(temp["cache"], "*.png"));
        }
        finally
        {
            request.Cancel();
            try { await load.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally { (ThumbnailService.DiskCacheFolder, ThumbnailService.FfmpegPath) = previous; }
        }
    }

    [Fact]
    public void Hidden_tabs_cancel_pending_images_release_displayed_pixels_and_reuse_cache_on_return()
    {
        using var temp = new TempDir(); temp.File("photo.png");
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        int decodes = 0;
        using var service = new ThumbnailService(1, 1_000_000, request =>
        { if (Interlocked.Increment(ref decodes) == 1) { entered.Set(); release.Wait(5000); } return Bitmap(request.Size); });
        Sta.Run(() =>
        {
            var tile = new ThumbnailImage { Service = service, Item = FileItem.FromInfo(new FileInfo(temp["photo.png"])) };
            var pane = new Grid(); pane.Children.Add(tile);
            var window = new Window { Content = pane, Width = 300, Height = 300 };
            try
            {
                Sta.ShowOffscreen(window); Sta.WaitUntil(() => tile.IsLoaded, "hidden-tab fixture loaded");
                Assert.True(entered.Wait(5000));
                pane.Visibility = Visibility.Collapsed;
                Assert.Equal(0, service.PendingCount);
                release.Set(); Assert.True(SpinWait.SpinUntil(() => service.WorkingCount == 0, 5000));
                Assert.False(tile.HasThumbnail);
                pane.Visibility = Visibility.Visible;
                Sta.WaitUntil(() => tile.HasThumbnail, "tab shown again");
                Assert.Equal(2, decodes);
                pane.Visibility = Visibility.Collapsed; Assert.False(tile.HasThumbnail);
                pane.Visibility = Visibility.Visible; Sta.WaitUntil(() => tile.HasThumbnail, "cached tab return");
                Assert.Equal(2, decodes);
            }
            finally { release.Set(); window.Close(); }
        });
    }
}
