using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Fyltrix.Core.Media;

/// <summary>A pending thumbnail. The grid cancels it when the tile scrolls away before a worker gets to it.</summary>
public sealed class ThumbnailRequest
{
    public required string Path { get; init; }
    public required int Size { get; init; }
    public DateTime Modified { get; init; }
    public long Length { get; init; }
    public bool IsVideo { get; init; }
    public required Action<BitmapSource?> Done { get; init; }
    private volatile bool _cancelled;
    private volatile bool _visible = true;
    internal Action? Changed;
    public bool Cancelled => _cancelled;
    public bool IsVisible { get => _visible; set { _visible = value; Changed?.Invoke(); } }
    public void Cancel() { _cancelled = true; Changed?.Invoke(); }
}

/// <summary>
/// Thumbnails for the grid view. Windows' thumbnail providers do the work (photos, video frames, PDF and Office
/// pages, album art; jumbo file-type icons otherwise) and Windows keeps its own disk cache of them. On top of that:
///  • a memory cache (LRU, bounded by pixel bytes) keyed by path + size + last-write time + length, so going back to
///    a folder doesn't regenerate anything and a changed file gets a fresh thumbnail;
///  • a bounded, coalesced queue on a few worker threads, prioritizing visible tiles over prefetch;
///  • for videos Windows can't thumbnail (e.g. no codec extension), one frame from FFmpeg when it is installed,
///    cached on disk under %LocalAppData%\Fyltrix\Thumbs (bounded, oldest removed first).
/// </summary>
public sealed class ThumbnailService : IDisposable
{
    private readonly object _gate = new();
    private sealed class Job
    {
        internal required string Key;
        internal required ThumbnailRequest Producer;
        internal readonly List<ThumbnailRequest> Subscribers = new();
        internal LinkedListNode<Job>? Node;
        internal bool Visible => Subscribers.Any(r => r.IsVisible);
    }
    private readonly LinkedList<Job> _queue = new();
    private readonly Dictionary<string, Job> _pending = new();
    private readonly Func<ThumbnailRequest, BitmapSource?> _make;
    private int _subscriptions, _working;
    private volatile bool _disposed;
    private readonly Dictionary<string, LinkedListNode<(string key, BitmapSource bmp, long bytes)>> _cache = new();
    private readonly LinkedList<(string key, BitmapSource bmp, long bytes)> _lru = new();
    private readonly long _budget;
    private long _cachedBytes;
    private const int MaxQueue = 800;
    internal int PendingCount { get { lock (_gate) return _subscriptions; } }
    internal int WorkingCount { get { lock (_gate) return _working; } }

    public static string DiskCacheFolder { get; set; } = Launcher.DataFolders.Thumbs;
    public static long DiskCacheBudgetBytes { get; set; } = 256L * 1024 * 1024;
    /// <summary>ffmpeg.exe for video frames Windows can't make; null = don't use FFmpeg.</summary>
    public static Func<string?> FfmpegPath { get; set; } = () => null;
    public static ThumbnailService Shared { get; } = new(workers: 3, memoryBudgetBytes: 192L * 1024 * 1024);

    public long CachedBytes { get { lock (_gate) return _cachedBytes; } }
    public int CachedCount { get { lock (_gate) return _cache.Count; } }

    public ThumbnailService(int workers, long memoryBudgetBytes) : this(workers, memoryBudgetBytes, null, true) { }

    internal ThumbnailService(int workers, long memoryBudgetBytes, Func<ThumbnailRequest, BitmapSource?>? make, bool trimDisk = false)
    {
        if (workers is < 1 or > 8 || memoryBudgetBytes <= 0) throw new ArgumentOutOfRangeException();
        _budget = memoryBudgetBytes;
        _make = make ?? Make;
        for (int i = 0; i < workers; i++)
        {
            var t = new Thread(Work) { IsBackground = true, Name = "Fyltrix thumbnails", Priority = ThreadPriority.BelowNormal };
            t.SetApartmentState(ApartmentState.STA); // some thumbnail providers expect an STA
            t.Start();
        }
        if (trimDisk) Task.Run(TrimDiskCache);
    }

    /// <summary>Thumbnail sizes are rounded up to a few steps so every zoom level doesn't create its own copy.</summary>
    public static int Bucket(int pixels) => pixels <= 64 ? 64 : pixels <= 128 ? 128 : pixels <= 256 ? 256 : pixels <= 512 ? 512 : pixels <= 1024 ? 1024 : 2048;

    private static string Key(string path, int size, DateTime modified, long length) =>
        $"{path.ToUpperInvariant()}|{size}|{modified.Ticks}|{length}";

    public BitmapSource? TryGetCached(string path, int size, DateTime modified, long length)
    {
        lock (_gate)
        {
            for (int bucket = Bucket(size); bucket <= 2048; bucket *= 2)
                if (_cache.TryGetValue(Key(path, bucket, modified, length), out var node))
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    return node.Value.bmp;
                }
            return null;
        }
    }

    public void Request(ThumbnailRequest r)
    {
        BitmapSource? hit;
        lock (_gate)
        {
            if (_disposed || r.Cancelled) { r.Cancel(); return; }
            hit = TryGetCached(r.Path, r.Size, r.Modified, r.Length);
            if (hit == null)
            {
                while (_subscriptions >= MaxQueue)
                {
                    var victim = _queue.LastOrDefault(j => !j.Visible) ?? _queue.Last?.Value ?? _pending.Values.First();
                    var oldest = victim.Subscribers[0]; oldest.Changed = null; oldest.Cancel();
                    Update(victim);
                }
                string key = Key(r.Path, Bucket(r.Size), r.Modified, r.Length);
                if (!_pending.TryGetValue(key, out var job) || job.Producer.Cancelled)
                {
                    job = new Job { Key = key, Producer = new ThumbnailRequest { Path = r.Path, Size = Bucket(r.Size), Modified = r.Modified, Length = r.Length, IsVideo = r.IsVideo, Done = _ => { } } };
                    _pending[key] = job;
                    job.Node = _queue.AddFirst(job);
                }
                job.Subscribers.Add(r); _subscriptions++;
                r.Changed = () => { lock (_gate) Update(job); };
                Update(job); // includes cancellation racing with subscription
                Monitor.PulseAll(_gate);
            }
        }
        if (hit != null && !r.Cancelled) Deliver(r, hit);
    }

    private void Update(Job job)
    {
        if (_disposed) return;
        int removed = job.Subscribers.RemoveAll(r => { if (!r.Cancelled) return false; r.Changed = null; return true; });
        _subscriptions -= removed;
        if (job.Subscribers.Count != 0) return;
        job.Producer.Cancel();
        if (job.Node != null) { _queue.Remove(job.Node); job.Node = null; }
        if (_pending.TryGetValue(job.Key, out var current) && ReferenceEquals(current, job)) _pending.Remove(job.Key);
    }

    private static void Deliver(ThumbnailRequest request, BitmapSource? bitmap)
    {
        try { if (!request.Cancelled) request.Done(bitmap); }
        catch (Exception ex) { Diagnostics.Log("Thumbnail callback", ex, request.Path); }
    }

    private void Work()
    {
        while (true)
        {
            Job job;
            lock (_gate)
            {
                while (_queue.Count == 0 && !_disposed) Monitor.Wait(_gate);
                if (_disposed) return;
                job = _queue.FirstOrDefault(j => j.Visible) ?? _queue.First!.Value;
                _queue.Remove(job.Node!); job.Node = null;
                _working++;
            }
            var r = job.Producer;
            BitmapSource? bmp = null;
            try
            {
                if (!r.Cancelled) bmp = TryGetCached(r.Path, r.Size, r.Modified, r.Length) ?? _make(r);
            }
            catch (Exception ex) { Diagnostics.Log("Thumbnails", ex, r.Path); }
            ThumbnailRequest[] subscribers;
            lock (_gate)
            {
                if (!_disposed && !r.Cancelled && bmp != null) Store(job.Key, bmp);
                subscribers = job.Subscribers.ToArray(); job.Subscribers.Clear();
                _subscriptions -= subscribers.Length;
                foreach (var subscriber in subscribers) subscriber.Changed = null;
                if (_pending.TryGetValue(job.Key, out var current) && ReferenceEquals(current, job)) _pending.Remove(job.Key);
            }
            if (!_disposed) foreach (var subscriber in subscribers) Deliver(subscriber, bmp);
            lock (_gate) _working--;
        }
    }

    private BitmapSource? Make(ThumbnailRequest r)
    {
        int size = Bucket(r.Size);
        var bmp = ShellThumbnails.Get(r.Path, size, thumbnailOnly: true);
        if (bmp == null && r.IsVideo && !r.Cancelled) bmp = FfmpegFrame(r.Path, size, r.Modified, r.Length, r);
        if (bmp == null && !r.Cancelled)
        {
            string? iconKey = !Directory.Exists(r.Path) && !ShellInfo.UsesPerFileIcon(r.Path)
                ? "@icon:" + Path.GetExtension(r.Path) : null;
            bmp = iconKey == null ? null : TryGetCached(iconKey, size, DateTime.MinValue, 0);
            bmp ??= ShellThumbnails.GetIcon(r.Path, size);
            if (bmp != null && iconKey != null) Store(Key(iconKey, size, DateTime.MinValue, 0), Shrink(bmp, size));
        }
        return bmp == null ? null : Shrink(bmp, size);
    }

    /// <summary>Providers may hand back more pixels than asked for; keep only what the tile can show.</summary>
    private static BitmapSource Shrink(BitmapSource bmp, int size)
    {
        double scale = Math.Min(1.0, (double)size / Math.Max(bmp.PixelWidth, bmp.PixelHeight));
        if (scale >= 0.999) return bmp;
        var scaled = new TransformedBitmap(bmp, new ScaleTransform(scale, scale));
        var copy = new WriteableBitmap(scaled); // owns its pixels; the big original can be collected
        copy.Freeze();
        return copy;
    }

    private void Store(string key, BitmapSource bmp)
    {
        long bytes = (long)bmp.PixelWidth * bmp.PixelHeight * 4;
        lock (_gate)
        {
            if (_disposed) return;
            if (_cache.TryGetValue(key, out var old)) { _lru.Remove(old); _cachedBytes -= old.Value.bytes; _cache.Remove(key); }
            var node = _lru.AddFirst((key, bmp, bytes));
            _cache[key] = node;
            _cachedBytes += bytes;
            while (_cachedBytes > _budget && _lru.Last != null)
            {
                var last = _lru.Last;
                _lru.RemoveLast();
                _cache.Remove(last.Value.key);
                _cachedBytes -= last.Value.bytes;
            }
        }
    }

    public void ClearMemory()
    {
        lock (_gate) { _cache.Clear(); _lru.Clear(); _cachedBytes = 0; }
    }

    // ---------------------------------------------------------------- FFmpeg frames (disk cached)

    internal static BitmapSource? FfmpegFrame(string path, int size, DateTime modified, long length, ThumbnailRequest? request = null)
    {
        if (request?.Cancelled == true) return null;
        var ffmpeg = FfmpegPath();
        if (ffmpeg == null) return null;
        var name = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Key(path, size, modified, length)))) + ".png";
        var cached = System.IO.Path.Combine(DiskCacheFolder, name);
        if (!File.Exists(cached))
        {
            Directory.CreateDirectory(DiskCacheFolder);
            var tmp = cached + "." + Guid.NewGuid().ToString("N") + ".tmp.png";
            try
            {
                // A cancelled/replaced tile must not monopolize a video worker.
                if (!RunFfmpeg(ffmpeg, path, tmp, size, "3", request) && !RunFfmpeg(ffmpeg, path, tmp, size, "0", request)) return null;
                if (request?.Cancelled == true) return null;
                File.Move(tmp, cached, overwrite: false);
                TrimDiskCache();
            }
            catch (IOException) { if (!File.Exists(cached)) return null; }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
        try
        {
            File.SetLastAccessTimeUtc(cached, DateTime.UtcNow);
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.UriSource = new Uri(cached);
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException) { return null; }
    }

    private static bool RunFfmpeg(string ffmpeg, string input, string output, int size, string seek, ThumbnailRequest? request)
    {
        if (request?.Cancelled == true) return false;
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-ss", seek, "-i", input, "-frames:v", "1",
                                  "-vf", $"scale='min({size},iw)':-2", "-f", "image2", "-c:v", "png", "-y", output })
            psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return false;
            _ = p.StandardError.ReadToEndAsync();
            _ = p.StandardOutput.ReadToEndAsync();
            var watch = Stopwatch.StartNew();
            while (!p.WaitForExit(50))
                if (request?.Cancelled == true || watch.ElapsedMilliseconds >= 15000)
                { try { p.Kill(true); p.WaitForExit(2000); } catch (InvalidOperationException) { } return false; }
            return p.ExitCode == 0 && File.Exists(output) && new FileInfo(output).Length > 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    /// <summary>Keeps the FFmpeg frame cache under its budget, removing the least recently used first.</summary>
    public static void TrimDiskCache()
    {
        try
        {
            var dir = new DirectoryInfo(DiskCacheFolder);
            if (!dir.Exists) return;
            var files = dir.GetFiles("*.png").Where(f => !f.Name.Contains(".tmp.")).OrderByDescending(f => f.LastAccessTimeUtc > f.LastWriteTimeUtc ? f.LastAccessTimeUtc : f.LastWriteTimeUtc).ToList();
            long total = 0;
            int count = 0;
            foreach (var f in files)
            {
                total += f.Length;
                if (total > DiskCacheBudgetBytes || ++count > 4096) { try { f.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var job in _pending.Values.ToArray())
            {
                job.Producer.Cancel();
                foreach (var r in job.Subscribers) { r.Changed = null; r.Cancel(); }
                job.Subscribers.Clear();
            }
            _pending.Clear(); _queue.Clear(); _subscriptions = 0;
            ClearMemory(); Monitor.PulseAll(_gate);
        }
    }
}
