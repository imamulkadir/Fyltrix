using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.Xz;
using SharpCompress.Readers;

namespace Fyltrix.Core.Archives;

public sealed record ArchiveEntryInfo(string Path, bool IsDirectory, long Size, long CompressedSize, DateTime? Modified, bool IsEncrypted, bool IsLink)
{
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('/'));
}

/// <summary>Guards against decompression bombs. Values are configurable in settings.</summary>
public sealed class ArchiveLimits
{
    public long MaxTotalBytes { get; init; } = 64L * 1024 * 1024 * 1024;
    public long MaxEntryBytes { get; init; } = 32L * 1024 * 1024 * 1024;
    public int MaxEntries { get; init; } = 1_000_000;
    /// <summary>Uncompressed : archive-size ratio considered hostile once more than <see cref="RatioCheckAfterBytes"/> were written.</summary>
    public double MaxRatio { get; init; } = 1000;
    public long RatioCheckAfterBytes { get; init; } = 1L * 1024 * 1024 * 1024;
}

public sealed class ArchivePasswordException : Exception
{
    public bool WasWrong { get; }
    public ArchivePasswordException(bool wasWrong, Exception? inner = null)
        : base(wasWrong ? "The password is incorrect." : "This archive is password protected.", inner) => WasWrong = wasWrong;
}

public sealed class ExtractOptions
{
    public string? Password { get; init; }
    /// <summary>Entry paths ('/'-separated) to extract; folders include their contents. Null = everything.</summary>
    public IReadOnlyCollection<string>? Entries { get; init; }
    /// <summary>Removed from the front of every entry path (e.g. the folder being browsed inside the archive).</summary>
    public string? StripPrefix { get; init; }
    public bool PreserveTimestamps { get; init; } = true;
    /// <summary>Called (on the worker thread) when a file already exists. Null = keep both.</summary>
    public Func<string, (ConflictChoice choice, bool applyToAll)>? AskConflict { get; init; }
    public ArchiveLimits Limits { get; init; } = new();
    /// <summary>Zone.Identifier content copied onto every extracted file (keeps SmartScreen warnings for downloads).</summary>
    public string? ZoneIdentifier { get; init; }
}

public sealed class ExtractResult
{
    public int Files { get; set; }
    public int Folders { get; set; }
    public long Bytes { get; set; }
    /// <summary>Items that did not exist before and whose parent was not created by this extraction (what Undo removes).</summary>
    public List<string> CreatedRoots { get; } = new();
    public List<(string original, DateTime when)> Replaced { get; } = new();
    public List<string> Skipped { get; } = new();
    public List<string> Errors { get; } = new();
    public bool Cancelled { get; set; }
}

public sealed class CreateOptions
{
    public ArchiveCreateFormat Format { get; init; } = ArchiveCreateFormat.Zip;
    public CompressionLevel Level { get; init; } = CompressionLevel.Optimal;
}

/// <summary>
/// Reads archives with SharpCompress and writes them with the .NET built-ins (ZipArchive, TarWriter, GZipStream).
/// Every extracted path is checked against the destination root (Zip Slip), links are never created,
/// and size/ratio limits stop decompression bombs. Output goes to temporary files that are moved into place.
/// </summary>
public static class ArchiveService
{
    private const string PartialSuffix = ".fl-partial";

    // ===================================================================== listing

    public static List<ArchiveEntryInfo> List(string archivePath, string? password = null, CancellationToken ct = default)
    {
        var kind = ArchiveFormats.Detect(archivePath) ?? throw new NotSupportedException("This file type isn't a supported archive.");
        var list = new List<ArchiveEntryInfo>();
        try
        {
            switch (kind)
            {
                case ArchiveKind.GZip or ArchiveKind.BZip2 or ArchiveKind.Xz:
                {
                    var fi = new FileInfo(archivePath);
                    long size = kind == ArchiveKind.GZip ? GzipDeclaredSize(archivePath) : -1;
                    list.Add(new ArchiveEntryInfo(SingleEntryName(archivePath), false, size, fi.Length, fi.LastWriteTime, false, false));
                    break;
                }
                case ArchiveKind.Tar or ArchiveKind.CompressedTar:
                {
                    // headers only: TarReader skips the data of entries we don't open
                    foreach (var e in Sequential(archivePath, password, _ => { }, ct))
                        list.Add(new ArchiveEntryInfo(e.Key, e.IsDirectory, e.IsDirectory ? 0 : e.Size, -1, e.Modified, false, e.LinkTarget != null));
                    break;
                }
                default:
                {
                    using var archive = OpenArchive(kind, archivePath, null, ReaderOpts(password));
                    foreach (var e in archive.Entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (e.Key is { Length: > 0 }) list.Add(ToInfo(e));
                    }
                    break;
                }
            }
        }
        catch (Exception ex) when (IsPasswordProblem(ex, password)) { throw new ArchivePasswordException(password != null, ex); }
        return AddImpliedFolders(list);
    }

    /// <summary>Many archives omit folder entries; add them so the tree can be browsed.</summary>
    private static List<ArchiveEntryInfo> AddImpliedFolders(List<ArchiveEntryInfo> list)
    {
        var dirs = new HashSet<string>(list.Where(e => e.IsDirectory).Select(e => e.Path), StringComparer.OrdinalIgnoreCase);
        var extra = new List<ArchiveEntryInfo>();
        foreach (var e in list)
        {
            var p = e.Path;
            int i;
            while ((i = p.LastIndexOf('/')) > 0)
            {
                p = p.Substring(0, i);
                if (dirs.Add(p)) extra.Add(new ArchiveEntryInfo(p, true, 0, 0, null, false, false));
            }
        }
        list.AddRange(extra);
        return list;
    }

    private static ArchiveEntryInfo ToInfo(IEntry e)
    {
        var path = NormalizeKey(e.Key!);
        return new ArchiveEntryInfo(path, e.IsDirectory, e.IsDirectory ? 0 : e.Size, e.CompressedSize, e.LastModifiedTime, e.IsEncrypted,
            !string.IsNullOrEmpty(e.LinkTarget));
    }

    /// <summary>'/' separators, no trailing slash, no leading "./". A leading '/' is kept so absolute entries are rejected, not silently relocated.</summary>
    public static string NormalizeKey(string key)
    {
        key = key.Replace('\\', '/').TrimEnd('/');
        while (key.StartsWith("./", StringComparison.Ordinal)) key = key.Substring(2);
        return key;
    }

    private static string SingleEntryName(string archivePath)
    {
        var name = ArchiveFormats.StripArchiveExtension(Path.GetFileName(archivePath));
        return string.IsNullOrWhiteSpace(name) ? "data" : name;
    }

    /// <summary>gzip stores the uncompressed size (mod 2^32) in its last four bytes.</summary>
    private static long GzipDeclaredSize(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < 18) return -1;
            fs.Seek(-4, SeekOrigin.End);
            var b = new byte[4];
            fs.ReadExactly(b);
            return BitConverter.ToUInt32(b, 0);
        }
        catch (IOException) { return -1; }
    }

    private static ReaderOptions ReaderOpts(string? password) => new() { Password = password, LookForHeader = false };

    private static bool IsPasswordProblem(Exception ex, string? password)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is ArchivePasswordException or NotSupportedException or OperationLimitException) return false;
            if (e is SharpCompress.Common.CryptographicException || e is System.Security.Cryptography.CryptographicException) return true;
            var m = e.Message;
            if (m.Contains("password", StringComparison.OrdinalIgnoreCase) || m.Contains("encrypt", StringComparison.OrdinalIgnoreCase)) return true;
            if (password != null && (m.Contains("crc", StringComparison.OrdinalIgnoreCase) || m.Contains("checksum", StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    // ===================================================================== extraction

    /// <summary>One entry as it streams past during sequential extraction.</summary>
    private sealed record StreamedEntry(string Key, bool IsDirectory, long Size, DateTime? Modified, bool IsEncrypted, string? LinkTarget, Func<Stream> Open);

    /// <summary>Walks entries in archive order (fast for solid 7z/RAR). <paramref name="position"/> reports 0..1.</summary>
    private static IEnumerable<StreamedEntry> Sequential(string archivePath, string? password, Action<double> position, CancellationToken ct)
    {
        var kind = ArchiveFormats.Detect(archivePath) ?? throw new NotSupportedException("This file type isn't a supported archive.");
        using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16);
        long length = Math.Max(1, fs.Length);

        if (ArchiveFormats.IsSingleFile(kind))
        {
            var fi = new FileInfo(archivePath);
            yield return new StreamedEntry(SingleEntryName(archivePath), false, -1, fi.LastWriteTime, false, null, () => kind switch
            {
                ArchiveKind.GZip => new GZipStream(fs, System.IO.Compression.CompressionMode.Decompress, leaveOpen: true),
                ArchiveKind.BZip2 => new BZip2Stream(fs, SharpCompress.Compressors.CompressionMode.Decompress, true),
                _ => new XZStream(fs),
            });
            position((double)fs.Position / length);
            yield break;
        }

        if (kind is ArchiveKind.Tar or ArchiveKind.CompressedTar)
        {
            // System.Formats.Tar understands PAX, GNU, ustar and v7 headers (SharpCompress mis-reads PAX).
            using var data = kind == ArchiveKind.Tar ? (Stream)new NonClosingStream(fs) : OpenTarDecompressor(archivePath, fs);
            using var tar = new System.Formats.Tar.TarReader(data, leaveOpen: true);
            System.Formats.Tar.TarEntry? te;
            while ((te = tar.GetNextEntry(copyData: false)) != null)
            {
                ct.ThrowIfCancellationRequested();
                position((double)fs.Position / length);
                var key = NormalizeKey(te.Name);
                if (key.Length == 0) continue;
                var type = te.EntryType;
                bool isDir = type == System.Formats.Tar.TarEntryType.Directory;
                bool isLink = type is System.Formats.Tar.TarEntryType.SymbolicLink or System.Formats.Tar.TarEntryType.HardLink;
                bool isFile = type is System.Formats.Tar.TarEntryType.RegularFile or System.Formats.Tar.TarEntryType.V7RegularFile
                    or System.Formats.Tar.TarEntryType.ContiguousFile;
                if (!isDir && !isLink && !isFile) continue; // pax/gnu metadata, devices, fifos
                var entry = te;
                yield return new StreamedEntry(key, isDir, isFile ? te.Length : 0, te.ModificationTime.LocalDateTime, false,
                    isLink ? (string.IsNullOrEmpty(te.LinkName) ? "?" : te.LinkName) : null,
                    () => entry.DataStream is { } s ? new NonClosingStream(s) : Stream.Null);
            }
            yield break;
        }

        var opts = ReaderOpts(password);
        opts.LeaveStreamOpen = true;
        using var archive = OpenArchive(kind, archivePath, fs, opts);
        if (archive.IsSolid || archive.Type == ArchiveType.SevenZip)
        {
            // solid archives must be decompressed in order; random access would restart for every entry
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                ct.ThrowIfCancellationRequested();
                var e = reader.Entry;
                position((double)fs.Position / length);
                if (string.IsNullOrEmpty(e.Key)) continue;
                yield return new StreamedEntry(NormalizeKey(e.Key), e.IsDirectory, e.Size, e.LastModifiedTime, e.IsEncrypted, NullIfEmpty(e.LinkTarget),
                    () => reader.OpenEntryStream());
            }
            yield break;
        }

        long total = Math.Max(1, archive.TotalUncompressSize), done = 0;
        foreach (var e in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            position((double)done / total);
            done += e.IsDirectory ? 0 : Math.Max(0, e.Size);
            if (string.IsNullOrEmpty(e.Key)) continue;
            var entry = e;
            yield return new StreamedEntry(NormalizeKey(e.Key), e.IsDirectory, e.Size, e.LastModifiedTime, e.IsEncrypted, NullIfEmpty(e.LinkTarget),
                () => entry.OpenEntryStream());
        }
    }

    /// <summary>Opens by known type: auto-detection fails on some valid archives (e.g. encrypted ZIPs written by libarchive).</summary>
    private static IArchive OpenArchive(ArchiveKind kind, string path, Stream? stream, ReaderOptions opts) => kind switch
    {
        ArchiveKind.Zip => stream != null ? SharpCompress.Archives.Zip.ZipArchive.Open(stream, opts) : SharpCompress.Archives.Zip.ZipArchive.Open(path, opts),
        ArchiveKind.SevenZip => stream != null ? SharpCompress.Archives.SevenZip.SevenZipArchive.Open(stream, opts) : SharpCompress.Archives.SevenZip.SevenZipArchive.Open(path, opts),
        // by path, so multi-volume sets (.part1.rar, .r00) are found
        ArchiveKind.Rar => SharpCompress.Archives.Rar.RarArchive.Open(path, opts),
        _ => stream != null ? ArchiveFactory.Open(stream, opts) : ArchiveFactory.Open(path, opts),
    };

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static Stream OpenTarDecompressor(string archivePath, Stream fs)
    {
        var name = Path.GetFileName(archivePath).ToLowerInvariant();
        var inner = new NonClosingStream(fs);
        if (name.EndsWith(".tar.gz") || name.EndsWith(".tgz")) return new GZipStream(inner, System.IO.Compression.CompressionMode.Decompress);
        if (name.EndsWith(".tar.bz2") || name.EndsWith(".tbz2") || name.EndsWith(".tbz")) return new BZip2Stream(inner, SharpCompress.Compressors.CompressionMode.Decompress, true);
        if (name.EndsWith(".tar.xz") || name.EndsWith(".txz")) return new XZStream(inner);
        if (name.EndsWith(".tar.zst")) return new SharpCompress.Compressors.ZStandard.DecompressionStream(inner, 1 << 17, false, false);
        if (name.EndsWith(".tar.lz")) return new SharpCompress.Compressors.LZMA.LZipStream(inner, SharpCompress.Compressors.CompressionMode.Decompress);
        throw new NotSupportedException("Unknown TAR compression.");
    }

    /// <summary>Lets inner readers dispose their view without closing the shared file stream.</summary>
    private sealed class NonClosingStream : Stream
    {
        private readonly Stream _s;
        public NonClosingStream(Stream s) => _s = s;
        public override bool CanRead => _s.CanRead;
        public override bool CanSeek => _s.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _s.Length;
        public override long Position { get => _s.Position; set => _s.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _s.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _s.Read(buffer);
        public override long Seek(long offset, SeekOrigin origin) => _s.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { }
    }

    public static ExtractResult Extract(string archivePath, string destDir, ExtractOptions options,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        var result = new ExtractResult();
        var limits = options.Limits;
        Directory.CreateDirectory(destDir);
        var root = Path.GetFullPath(destDir).TrimEnd('\\') + "\\";
        long archiveSize = Math.Max(1, new FileInfo(archivePath).Length);

        var selection = options.Entries?.Select(NormalizeKey).Where(s => s.Length > 0).ToList();
        var prefix = string.IsNullOrEmpty(options.StripPrefix) ? "" : NormalizeKey(options.StripPrefix) + "/";
        var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirTimes = new List<(string path, DateTime time)>();
        ConflictChoice? remembered = null;
        int entries = 0;
        double pos = 0;
        var name = Path.GetFileName(archivePath);
        string? staging = null;

        void RemoveCreated()
        {
            FinishRoots(result, created);
            foreach (var p in result.CreatedRoots)
            {
                try { if (Directory.Exists(p)) Directory.Delete(p, true); else TryDelete(p); }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        try
        {
            IEnumerable<StreamedEntry> source;
            if (ArchiveFormats.Detect(archivePath) == ArchiveKind.Zip && options.Password != null && ZipNeedsSystemTar(archivePath, options.Password))
            {
                // SharpCompress can't decrypt streamed (data-descriptor) ZIP entries; Windows' libarchive can.
                CheckDeclaredSize(archivePath, options.Password, limits);
                progress?.Report(new OperationProgress($"Decrypting {name}", -1));
                staging = Path.Combine(root, ".~fl-staging-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);
                File.SetAttributes(staging, FileAttributes.Directory | FileAttributes.Hidden);
                SystemTar.ExtractAll(archivePath, staging, options.Password, ct);
                source = StagedEntries(staging, p => pos = p, ct);
            }
            else source = Sequential(archivePath, options.Password, p => pos = p, ct);

            foreach (var entry in source)
            {
                ct.ThrowIfCancellationRequested();
                if (++entries > limits.MaxEntries)
                    throw new OperationLimitException($"The archive has more than {limits.MaxEntries:N0} entries, the safety limit.");
                if (selection != null && !selection.Any(s => entry.Key.Equals(s, StringComparison.OrdinalIgnoreCase) ||
                                                             entry.Key.StartsWith(s + "/", StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (entry.IsEncrypted && options.Password == null) throw new ArchivePasswordException(false);

                var rel = entry.Key;
                if (prefix.Length > 0)
                {
                    if (!(rel + "/").StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    rel = rel.Length > prefix.Length ? rel.Substring(prefix.Length) : "";
                    if (rel.Length == 0) continue;
                }
                if (!string.IsNullOrEmpty(entry.LinkTarget))
                {
                    result.Skipped.Add($"{entry.Key}: symbolic/hard links are not extracted.");
                    continue;
                }
                var target = ResolveSafePath(root, rel, out var reason);
                if (target == null)
                {
                    result.Skipped.Add($"{entry.Key}: {reason}");
                    continue;
                }
                progress?.Report(new OperationProgress($"Extracting {name}", pos, rel));

                if (entry.IsDirectory)
                {
                    if (File.Exists(target)) { result.Errors.Add($"{rel}: a file with that name is in the way."); continue; }
                    CreateDirectoryTracked(root, target, created);
                    result.Folders++;
                    if (options.PreserveTimestamps && entry.Modified is { } dm) dirTimes.Add((target, dm));
                    continue;
                }

                var dir = Path.GetDirectoryName(target)!;
                if (File.Exists(dir)) { result.Errors.Add($"{rel}: a file with the folder's name is in the way."); continue; }
                try
                {
                    CreateDirectoryTracked(root, dir, created);
                    EnsureNoReparsePoints(root, dir);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    result.Errors.Add($"{rel}: {ex.Message}");
                    continue;
                }

                bool existed = File.Exists(target) || Directory.Exists(target);
                bool replace = false;
                if (existed)
                {
                    var choice = remembered ?? (options.AskConflict != null ? ConflictChoice.Skip : ConflictChoice.KeepBoth);
                    if (remembered == null && options.AskConflict != null)
                    {
                        var (c, all) = options.AskConflict(target);
                        choice = c;
                        if (all) remembered = c;
                    }
                    if (choice == ConflictChoice.Cancel) { result.Cancelled = true; break; }
                    if (choice == ConflictChoice.Skip) { result.Skipped.Add($"{rel}: already exists."); continue; }
                    if (choice == ConflictChoice.KeepBoth || Directory.Exists(target)) { target = FileOps.UniqueName(dir, Path.GetFileName(target)); existed = false; }
                    else replace = true;
                }

                long written;
                DateTime? replacedAt;
                try { written = WriteEntry(entry, target, replace, limits, result, archiveSize, ct, out replacedAt); }
                catch (UnauthorizedAccessException ex) { result.Errors.Add($"{rel}: {ex.Message}"); continue; }
                catch (IOException ex) when (IsDestinationError(ex)) { result.Errors.Add($"{rel}: {ex.Message}"); continue; }
                if (replacedAt != null) result.Replaced.Add((target, replacedAt.Value));
                if (!existed) created.Add(target);
                ZoneInfo.Write(target, options.ZoneIdentifier);
                result.Files++;
                result.Bytes += written;
                if (options.PreserveTimestamps && entry.Modified is { } fm)
                {
                    try { File.SetLastWriteTime(target, fm); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
        }
        catch (OperationCanceledException) { result.Cancelled = true; }
        catch (ArchivePasswordException)
        {
            // Nothing useful was produced with a wrong/missing password: remove what this run created.
            RemoveCreated();
            throw;
        }
        catch (Exception ex) when (IsPasswordProblem(ex, options.Password) && ex is not OperationLimitException)
        {
            RemoveCreated();
            throw new ArchivePasswordException(options.Password != null, ex);
        }
        catch (OperationLimitException ex) { result.Errors.Add(ex.Message); }
        catch (Exception ex) when (ex is SharpCompressException or InvalidDataException or IOException or NotSupportedException
                                       or ArgumentException or IndexOutOfRangeException or EndOfStreamException)
        {
            result.Errors.Add($"The archive couldn't be read completely ({ex.Message}). It may be damaged or use an unsupported method.");
        }
        finally
        {
            foreach (var (p, t) in dirTimes)
            {
                try { Directory.SetLastWriteTime(p, t); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            if (staging != null)
            {
                try { Directory.Delete(staging, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        FinishRoots(result, created);
        return result;
    }

    private static bool ZipNeedsSystemTar(string archivePath, string password)
    {
        if (!SystemTar.IsAvailable) return false;
        using var zip = SharpCompress.Archives.Zip.ZipArchive.Open(archivePath, ReaderOpts(password));
        var e = zip.Entries.FirstOrDefault(x => x.IsEncrypted && !x.IsDirectory);
        if (e == null) return false;
        try
        {
            using var s = e.OpenEntryStream();
            s.ReadByte();
            return false;
        }
        catch (NotSupportedException) { return true; }
        catch (Exception ex) when (ex is SharpCompressException or InvalidDataException or IOException
                                       or System.Security.Cryptography.CryptographicException) { return false; }
    }

    /// <summary>The staging route can't meter bytes while libarchive writes, so check the central directory first.</summary>
    private static void CheckDeclaredSize(string archivePath, string password, ArchiveLimits limits)
    {
        using var zip = SharpCompress.Archives.Zip.ZipArchive.Open(archivePath, ReaderOpts(password));
        long total = 0;
        int count = 0;
        foreach (var e in zip.Entries)
        {
            total += Math.Max(0, e.Size);
            if (++count > limits.MaxEntries) throw new OperationLimitException($"The archive has more than {limits.MaxEntries:N0} entries, the safety limit.");
        }
        if (total > limits.MaxTotalBytes) throw new OperationLimitException($"The archive declares {FileOps.FormatSize(total)}, beyond the {FileOps.FormatSize(limits.MaxTotalBytes)} safety limit.");
    }

    /// <summary>Files libarchive put in the staging folder, presented as archive entries. Links are reported, never followed.</summary>
    private static IEnumerable<StreamedEntry> StagedEntries(string staging, Action<double> position, CancellationToken ct)
    {
        var all = new List<FileSystemInfo>();
        var stack = new Stack<string>();
        stack.Push(staging);
        while (stack.Count > 0)
        {
            foreach (var info in new DirectoryInfo(stack.Pop()).EnumerateFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();
                all.Add(info);
                if ((info.Attributes & FileAttributes.Directory) != 0 && (info.Attributes & FileAttributes.ReparsePoint) == 0) stack.Push(info.FullName);
            }
        }
        for (int i = 0; i < all.Count; i++)
        {
            var info = all[i];
            position((double)i / Math.Max(1, all.Count));
            bool link = (info.Attributes & FileAttributes.ReparsePoint) != 0;
            bool dir = (info.Attributes & FileAttributes.Directory) != 0 && !link;
            var path = info.FullName;
            yield return new StreamedEntry(Path.GetRelativePath(staging, path).Replace('\\', '/'), dir, dir || link ? 0 : ((FileInfo)info).Length,
                info.LastWriteTime, false, link ? "link" : null,
                () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16));
        }
    }

    /// <summary>Write-side failures (locked, full disk, access) affect one entry; read-side ones mean the archive is broken.</summary>
    private static bool IsDestinationError(IOException ex)
    {
        if (ex is PathTooLongException or DirectoryNotFoundException or DriveNotFoundException) return true;
        int code = ex.HResult & 0xFFFF;
        return code is 5 or 32 or 33 or 80 or 112 or 183 or 206 || ex.Message.Contains("Recycle Bin", StringComparison.Ordinal);
    }

    private static void FinishRoots(ExtractResult result, HashSet<string> created)
    {
        result.CreatedRoots.Clear();
        foreach (var p in created)
        {
            var parent = Path.GetDirectoryName(p);
            if (parent == null || !created.Contains(parent)) result.CreatedRoots.Add(p);
        }
    }

    private static void CreateDirectoryTracked(string root, string dir, HashSet<string> created)
    {
        var full = Path.GetFullPath(dir);
        var missing = new Stack<string>();
        for (var d = full; d != null && d.Length >= root.Length - 1 && !Directory.Exists(d); d = Path.GetDirectoryName(d))
            missing.Push(d);
        foreach (var d in missing)
        {
            Directory.CreateDirectory(d);
            created.Add(d);
        }
    }

    /// <summary>Refuses to write through junctions/symlinks that already exist inside the destination.</summary>
    private static void EnsureNoReparsePoints(string root, string dir)
    {
        var rootTrim = root.TrimEnd('\\');
        for (var d = Path.GetFullPath(dir); d != null && d.Length > rootTrim.Length; d = Path.GetDirectoryName(d))
        {
            var attrs = File.GetAttributes(d);
            if ((attrs & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"'{d}' is a link to another location; extracting through it is not allowed.");
        }
    }

    private static long WriteEntry(StreamedEntry entry, string target, bool replace, ArchiveLimits limits, ExtractResult result,
        long archiveSize, CancellationToken ct, out DateTime? replacedAt)
    {
        replacedAt = null;
        var temp = target + PartialSuffix;
        long written = 0;
        try
        {
            using (var input = entry.Open())
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                var buffer = new byte[1 << 16];
                int n;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, n);
                    written += n;
                    long total = result.Bytes + written;
                    if (written > limits.MaxEntryBytes)
                        throw new OperationLimitException($"'{entry.Key}' expands beyond the {FileOps.FormatSize(limits.MaxEntryBytes)} per-file safety limit.");
                    if (total > limits.MaxTotalBytes)
                        throw new OperationLimitException($"The archive expands beyond the {FileOps.FormatSize(limits.MaxTotalBytes)} safety limit.");
                    if (total > limits.RatioCheckAfterBytes && total / (double)archiveSize > limits.MaxRatio)
                        throw new OperationLimitException($"The archive expands more than {limits.MaxRatio:N0}× its size; this looks like a decompression bomb.");
                }
            }
            if (replace && File.Exists(target))
            {
                replacedAt = DateTime.UtcNow;
                if (!FileOps.Delete(new[] { target }, permanent: false, IntPtr.Zero, silent: true))
                    throw new IOException($"Couldn't move the existing '{Path.GetFileName(target)}' to the Recycle Bin.");
            }
            File.Move(temp, target, overwrite: false);
            return written;
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Maps an entry path to a file system path inside <paramref name="root"/>, or returns null (with a reason)
    /// for absolute paths, drive letters, ".." segments and alternate data streams.
    /// Invalid characters and reserved device names are made safe rather than rejected.
    /// </summary>
    public static string? ResolveSafePath(string root, string entryPath, out string? reason)
    {
        reason = null;
        var key = entryPath.Replace('\\', '/');
        if (key.StartsWith('/') || (key.Length >= 2 && key[1] == ':'))
        {
            reason = "absolute paths are not allowed.";
            return null;
        }
        var parts = new List<string>();
        foreach (var raw in key.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw == ".") continue;
            if (raw == "..") { reason = "paths that climb out of the destination ('..') are not allowed."; return null; }
            if (raw.Contains(':')) { reason = "names with ':' (alternate data streams) are not allowed."; return null; }
            var seg = new string(raw.Select(c => c < 32 || FileOps.InvalidNameChars.Contains(c) ? '_' : c).ToArray()).TrimEnd(' ', '.');
            if (seg.Length == 0) seg = "_";
            var stem = seg.Split('.')[0];
            if (ReservedNames.Contains(stem)) seg = "_" + seg;
            parts.Add(seg);
        }
        if (parts.Count == 0) { reason = "the entry has no name."; return null; }
        var rootFull = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        var full = Path.GetFullPath(Path.Combine(rootFull, Path.Combine(parts.ToArray())));
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            reason = "the path resolves outside the destination.";
            return null;
        }
        return full;
    }

    /// <summary>Extracts one entry to a file (used for previewing and opening entries).</summary>
    public static void ExtractEntryToFile(string archivePath, string entryPath, string targetFile, string? password,
        ArchiveLimits limits, CancellationToken ct = default)
    {
        var key = NormalizeKey(entryPath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
        var dummy = new ExtractResult();
        long archiveSize = Math.Max(1, new FileInfo(archivePath).Length);
        if (ArchiveFormats.Detect(archivePath) == ArchiveKind.Zip && password != null && ZipNeedsSystemTar(archivePath, password))
        {
            var staging = Path.Combine(Path.GetTempPath(), "Fyltrix-staging-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(staging);
                SystemTar.ExtractAll(archivePath, staging, password, ct, new[] { key });
                var staged = ResolveSafePath(staging, key, out var why) ?? throw new IOException(why);
                if (!File.Exists(staged) || (File.GetAttributes(staged) & FileAttributes.ReparsePoint) != 0)
                    throw new FileNotFoundException($"'{entryPath}' was not found in the archive.");
                if (new FileInfo(staged).Length > limits.MaxEntryBytes) throw new OperationLimitException("The entry is larger than the per-file safety limit.");
                File.Copy(staged, targetFile, overwrite: false);
                return;
            }
            finally
            {
                try { Directory.Delete(staging, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        try
        {
            foreach (var entry in Sequential(archivePath, password, _ => { }, ct))
            {
                if (!entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase) || entry.IsDirectory) continue;
                if (!string.IsNullOrEmpty(entry.LinkTarget)) throw new IOException("This entry is a link, not a file.");
                WriteEntry(entry, targetFile, replace: false, limits, dummy, archiveSize, ct, out _);
                if (entry.Modified is { } m) File.SetLastWriteTime(targetFile, m);
                return;
            }
        }
        catch (Exception ex) when (IsPasswordProblem(ex, password) && ex is not OperationLimitException)
        {
            throw new ArchivePasswordException(password != null, ex);
        }
        throw new FileNotFoundException($"'{entryPath}' was not found in the archive.");
    }

    public static bool NeedsPassword(IEnumerable<ArchiveEntryInfo> entries) => entries.Any(e => e.IsEncrypted);

    // ===================================================================== creation

    public static void Create(IList<string> sources, string archivePath, CreateOptions options,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        if (sources.Count == 0) throw new ArgumentException("Nothing to compress.");
        if (options.Format == ArchiveCreateFormat.GZip && (sources.Count != 1 || !File.Exists(sources[0])))
            throw new NotSupportedException("gzip (.gz) compresses exactly one file. Use ZIP or TAR.GZ for folders or several files.");

        var outFull = Path.GetFullPath(archivePath);
        var dir = Path.GetDirectoryName(outFull)!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".~{Guid.NewGuid():N}{PartialSuffix}");
        var exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { outFull, temp };

        var items = CollectItems(sources, exclude, ct);
        long totalBytes = Math.Max(1, items.Where(i => !i.isDir).Sum(i => i.size));
        long done = 0;
        var name = Path.GetFileName(archivePath);
        void Report(string entry, long add)
        {
            done += add;
            progress?.Report(new OperationProgress($"Compressing to {name}", (double)done / totalBytes, entry));
        }

        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                switch (options.Format)
                {
                    case ArchiveCreateFormat.Zip:
                        WriteZip(fs, items, options.Level, Report, ct);
                        break;
                    case ArchiveCreateFormat.Tar:
                        WriteTar(fs, items, Report, ct);
                        break;
                    case ArchiveCreateFormat.TarGz:
                        using (var gz = new GZipStream(fs, options.Level, leaveOpen: true)) WriteTar(gz, items, Report, ct);
                        break;
                    case ArchiveCreateFormat.TarBz2:
                        using (var bz = new BZip2Stream(fs, SharpCompress.Compressors.CompressionMode.Compress, false)) WriteTar(bz, items, Report, ct);
                        break;
                    case ArchiveCreateFormat.GZip:
                        using (var gz = new GZipStream(fs, options.Level, leaveOpen: true))
                        using (var input = OpenShared(items[0].path))
                            CopyWithProgress(input, gz, n => Report(items[0].entry, n), ct);
                        break;
                }
            }
            if (File.Exists(outFull) || Directory.Exists(outFull)) throw new IOException($"'{Path.GetFileName(outFull)}' already exists.");
            File.Move(temp, outFull);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static List<(string path, string entry, bool isDir, long size, DateTime modified)> CollectItems(
        IList<string> sources, HashSet<string> exclude, CancellationToken ct)
    {
        var list = new List<(string, string, bool, long, DateTime)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in sources)
        {
            var src = Path.GetFullPath(raw.TrimEnd('\\'));
            var baseName = Path.GetFileName(src);
            if (string.IsNullOrEmpty(baseName)) throw new NotSupportedException("A whole drive can't be compressed; pick folders inside it.");
            if (!names.Add(baseName)) throw new IOException($"Two selected items are both named '{baseName}'.");
            if (File.Exists(src))
            {
                var fi = new FileInfo(src);
                list.Add((src, baseName, false, fi.Length, fi.LastWriteTime));
                continue;
            }
            if (!Directory.Exists(src)) throw new FileNotFoundException($"'{src}' no longer exists.");
            list.Add((src, baseName + "/", true, 0, Directory.GetLastWriteTime(src)));
            var stack = new Stack<string>();
            stack.Push(src);
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var d = stack.Pop();
                foreach (var info in new DirectoryInfo(d).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }))
                {
                    if (exclude.Contains(info.FullName)) continue;
                    var rel = baseName + "/" + Path.GetRelativePath(src, info.FullName).Replace('\\', '/');
                    bool isDir = (info.Attributes & FileAttributes.Directory) != 0;
                    if (isDir)
                    {
                        list.Add((info.FullName, rel + "/", true, 0, info.LastWriteTime));
                        // never follow junctions/symlinked folders: they can loop or leave the selection
                        if ((info.Attributes & FileAttributes.ReparsePoint) == 0) stack.Push(info.FullName);
                    }
                    else list.Add((info.FullName, rel, false, ((FileInfo)info).Length, info.LastWriteTime));
                }
            }
        }
        return list;
    }

    private static void WriteZip(Stream output, List<(string path, string entry, bool isDir, long size, DateTime modified)> items,
        CompressionLevel level, Action<string, long> report, CancellationToken ct)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: System.Text.Encoding.UTF8);
        foreach (var it in items)
        {
            ct.ThrowIfCancellationRequested();
            var e = zip.CreateEntry(it.entry, it.isDir ? CompressionLevel.NoCompression : level);
            e.LastWriteTime = ClampZipTime(it.modified);
            if (it.isDir) continue;
            using var input = OpenShared(it.path);
            using var es = e.Open();
            CopyWithProgress(input, es, n => report(it.entry, n), ct);
        }
    }

    private static DateTimeOffset ClampZipTime(DateTime t) =>
        t.Year < 1980 ? new DateTime(1980, 1, 1) : t.Year > 2107 ? new DateTime(2107, 12, 31) : t;

    private static void WriteTar(Stream output, List<(string path, string entry, bool isDir, long size, DateTime modified)> items,
        Action<string, long> report, CancellationToken ct)
    {
        using var tar = new System.Formats.Tar.TarWriter(output, System.Formats.Tar.TarEntryFormat.Pax, leaveOpen: true);
        foreach (var it in items)
        {
            ct.ThrowIfCancellationRequested();
            if (it.isDir)
            {
                tar.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.Directory, it.entry) { ModificationTime = it.modified });
                continue;
            }
            using var input = OpenShared(it.path);
            var counting = new ProgressStream(input, n => report(it.entry, n), ct);
            tar.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, it.entry)
            {
                ModificationTime = it.modified,
                DataStream = counting,
            });
        }
    }

    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);

    private static void CopyWithProgress(Stream input, Stream output, Action<long> onBytes, CancellationToken ct)
    {
        var buffer = new byte[1 << 16];
        int n;
        while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            output.Write(buffer, 0, n);
            onBytes(n);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Read-only pass-through that reports bytes read and honours cancellation (TarWriter reads the data stream itself).</summary>
    private sealed class ProgressStream : Stream
    {
        private readonly Stream _inner;
        private readonly Action<long> _onRead;
        private readonly CancellationToken _ct;
        public ProgressStream(Stream inner, Action<long> onRead, CancellationToken ct) { _inner = inner; _onRead = onRead; _ct = ct; }
        public override bool CanRead => true;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            _ct.ThrowIfCancellationRequested();
            int n = _inner.Read(buffer, offset, count);
            if (n > 0) _onRead(n);
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
