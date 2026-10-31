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
    public bool Cancelled => _cancelled;
    public void Cancel() => _cancelled = true;
}

/// <summary>
/// Thumbnails for the grid view. Windows' thumbnail providers do the work (photos, video frames, PDF and Office
/// pages, album art; jumbo file-type icons otherwise) and Windows keeps its own disk cache of them. On top of that:
///  • a memory cache (LRU, bounded by pixel bytes) keyed by path + size + last-write time + length, so going back to
///    a folder doesn't regenerate anything and a changed file gets a fresh thumbnail;
///  • a few worker threads taking the newest request first (what is on screen now), skipping cancelled ones;
///  • for videos Windows can't thumbnail (e.g. no codec extension), one frame from FFmpeg when it is installed,
///    cached on disk under %LocalAppData%\FileLens\Thumbs (bounded, oldest removed first).
/// </summary>
public sealed class ThumbnailService : IDisposable
{
    public static ThumbnailService Shared { get; } = new(workers: 3, memoryBudgetBytes: 192L * 1024 * 1024);

    private readonly object _gate = new();
    private readonly LinkedList<ThumbnailRequest> _queue = new(); // newest first
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, LinkedListNode<(string key, BitmapSource bmp, long bytes)>> _cache = new();
    private readonly LinkedList<(string key, BitmapSource bmp, long bytes)> _lru = new();
    private readonly long _budget;
    private long _cachedBytes;
    private const int MaxQueue = 800;

    public static string DiskCacheFolder { get; set; } = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileLens", "Thumbs");
    public static long DiskCacheBudgetBytes { get; set; } = 256L * 1024 * 1024;
    /// <summary>ffmpeg.exe for video frames Windows can't make; null = don't use FFmpeg.</summary>
    public static Func<string?> FfmpegPath { get; set; } = () => null;

    public long CachedBytes { get { lock (_gate) return _cachedBytes; } }
    public int CachedCount { get { lock (_gate) return _cache.Count; } }

    public ThumbnailService(int workers, long memoryBudgetBytes)
    {
        _budget = memoryBudgetBytes;
        for (int i = 0; i < workers; i++)
        {
            var t = new Thread(Work) { IsBackground = true, Name = "Fyltrix thumbnails", Priority = ThreadPriority.BelowNormal };
            t.SetApartmentState(ApartmentState.STA); // some thumbnail providers expect an STA
            t.Start();
        }
        Task.Run(TrimDiskCache);
    }

    /// <summary>Thumbnail sizes are rounded up to a few steps so every zoom level doesn't create its own copy.</summary>
    public static int Bucket(int pixels) => pixels <= 64 ? 64 : pixels <= 128 ? 128 : pixels <= 256 ? 256 : 512;

    private static string Key(string path, int size, DateTime modified, long length) =>
        $"{path.ToUpperInvariant()}|{size}|{modified.Ticks}|{length}";

    public BitmapSource? TryGetCached(string path, int size, DateTime modified, long length)
    {
        lock (_gate)
        {
            if (!_cache.TryGetValue(Key(path, Bucket(size), modified, length), out var node)) return null;
            _lru.Remove(node);
            _lru.AddFirst(node);
            return node.Value.bmp;
        }
    }

    public void Request(ThumbnailRequest r)
    {
        if (TryGetCached(r.Path, r.Size, r.Modified, r.Length) is { } hit) { r.Done(hit); return; }
        lock (_gate)
        {
            _queue.AddFirst(r);
            // a fast scroll through thousands of files: the oldest requests are off screen by now
            while (_queue.Count > MaxQueue) { _queue.Last!.Value.Cancel(); _queue.RemoveLast(); }
        }
        _signal.Release();
    }

    private void Work()
    {
        while (!_stop.IsCancellationRequested)
        {
            try { _signal.Wait(_stop.Token); } catch (OperationCanceledException) { return; }
            ThumbnailRequest? r;
            lock (_gate)
            {
                r = _queue.First?.Value;
                if (r != null) _queue.RemoveFirst();
            }
            if (r == null || r.Cancelled) continue;
            BitmapSource? bmp = null;
            try
            {
                bmp = TryGetCached(r.Path, r.Size, r.Modified, r.Length) ?? Make(r);
                if (bmp != null) Store(Key(r.Path, Bucket(r.Size), r.Modified, r.Length), bmp);
            }
            catch (Exception ex) { Diagnostics.Log("Thumbnails", ex, r.Path); }
            if (!r.Cancelled) r.Done(bmp);
        }
    }

    private static BitmapSource? Make(ThumbnailRequest r)
    {
        int size = Bucket(r.Size);
        var bmp = ShellThumbnails.Get(r.Path, size, thumbnailOnly: true);
        if (bmp == null && r.IsVideo && !r.Cancelled) bmp = FfmpegFrame(r.Path, size, r.Modified, r.Length);
        if (bmp == null && !r.Cancelled) bmp = ShellThumbnails.Get(r.Path, size, thumbnailOnly: false); // the file-type icon
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

    internal static BitmapSource? FfmpegFrame(string path, int size, DateTime modified, long length)
    {
        var ffmpeg = FfmpegPath();
        if (ffmpeg == null) return null;
        var name = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Key(path, size, modified, length)))) + ".png";
        var cached = System.IO.Path.Combine(DiskCacheFolder, name);
        if (!File.Exists(cached))
        {
            Directory.CreateDirectory(DiskCacheFolder);
            var tmp = cached + ".tmp.png";
            // a few seconds in skips black lead-in frames; very short clips fall back to the first frame
            if (!RunFfmpeg(ffmpeg, path, tmp, size, "3") && !RunFfmpeg(ffmpeg, path, tmp, size, "0")) return null;
            try { File.Move(tmp, cached, overwrite: true); }
            catch (IOException) { return null; }
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

    private static bool RunFfmpeg(string ffmpeg, string input, string output, int size, string seek)
    {
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
            if (!p.WaitForExit(15_000)) { try { p.Kill(true); } catch (InvalidOperationException) { } return false; }
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
            var files = dir.GetFiles("*.png").OrderByDescending(f => f.LastAccessTimeUtc > f.LastWriteTimeUtc ? f.LastAccessTimeUtc : f.LastWriteTimeUtc).ToList();
            long total = 0;
            foreach (var f in files)
            {
                total += f.Length;
                if (total > DiskCacheBudgetBytes || f.Name.Contains(".tmp.")) { try { f.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose() => _stop.Cancel();
}
