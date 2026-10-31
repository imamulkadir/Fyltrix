using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Fyltrix.Core.Tools;

public sealed class DuplicateOptions
{
    public bool Recursive { get; init; } = true;
    public long MinSize { get; init; } = 1;
    public bool IncludeHidden { get; init; }
    /// <summary>Extensions to include (".jpg"); empty = all.</summary>
    public IReadOnlyCollection<string> Extensions { get; init; } = Array.Empty<string>();
}

public sealed record DuplicateFile(string Path, DateTime Modified);
public sealed record DuplicateGroup(long Size, string Sha256, List<DuplicateFile> Files)
{
    public long Wasted => Size * (Files.Count - 1);
}

public sealed class DuplicateResult
{
    public List<DuplicateGroup> Groups { get; } = new();
    public int FilesScanned { get; set; }
    public long BytesHashed { get; set; }
    public int HardLinksIgnored { get; set; }
    public List<string> Errors { get; } = new();
}

/// <summary>
/// Finds files with identical content: same size → same head/tail fingerprint (a shortcut only) → same full
/// SHA-256. Hard links to one file are not duplicates. Junctions and symlinked folders are not followed.
/// Nothing is deleted here.
/// </summary>
public static class DuplicateFinder
{
    private const int EdgeBytes = 64 * 1024;

    public static DuplicateResult Find(IEnumerable<string> roots, DuplicateOptions options, IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        var result = new DuplicateResult();
        var exts = new HashSet<string>(options.Extensions.Select(e => e.StartsWith('.') ? e : "." + e), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bySize = new Dictionary<long, List<FileInfo>>();

        progress?.Report(new OperationProgress("Listing files…"));
        foreach (var root in roots.Select(r => Path.GetFullPath(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = options.Recursive, IgnoreInaccessible = true, ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint | (options.IncludeHidden ? 0 : FileAttributes.Hidden | FileAttributes.System),
            };
            IEnumerable<FileInfo> files;
            try { files = new DirectoryInfo(root).EnumerateFiles("*", opts); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { result.Errors.Add($"{root}: {ex.Message}"); continue; }
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                if (!seen.Add(f.FullName)) continue;
                long len;
                try { len = f.Length; } catch (IOException) { continue; }
                if (len < Math.Max(1, options.MinSize)) continue;
                if (exts.Count > 0 && !exts.Contains(f.Extension)) continue;
                result.FilesScanned++;
                if (!bySize.TryGetValue(len, out var list)) bySize[len] = list = new List<FileInfo>();
                list.Add(f);
                if (result.FilesScanned % 500 == 0) progress?.Report(new OperationProgress("Listing files…", -1, $"{result.FilesScanned:N0} files"));
            }
        }

        // hard links: keep one path per physical file
        var candidates = new List<List<FileInfo>>();
        foreach (var group in bySize.Values.Where(g => g.Count > 1))
        {
            var unique = new List<FileInfo>();
            var ids = new HashSet<(uint, ulong)>();
            foreach (var f in group)
            {
                var id = Native.FileIdentity(f.FullName);
                if (id is { } i && !ids.Add((i.volume, i.index))) { result.HardLinksIgnored++; continue; }
                unique.Add(f);
            }
            if (unique.Count > 1) candidates.Add(unique);
        }

        long totalBytes = candidates.Sum(g => g.Sum(f => Math.Min(f.Length, 2L * EdgeBytes))) + 1;
        long done = 0;
        void Advance(long n, string status)
        {
            var d = Interlocked.Add(ref done, n);
            progress?.Report(new OperationProgress(status, Math.Min(1, (double)d / totalBytes)));
        }

        // stage 2: cheap fingerprint to drop most non-duplicates without reading whole files
        var stage2 = new List<List<FileInfo>>();
        foreach (var group in candidates)
        {
            var byEdge = new Dictionary<string, List<FileInfo>>();
            foreach (var f in group)
            {
                ct.ThrowIfCancellationRequested();
                string? key = EdgeFingerprint(f, result);
                Advance(Math.Min(f.Length, 2L * EdgeBytes), "Comparing files…");
                if (key == null) continue;
                if (!byEdge.TryGetValue(key, out var l)) byEdge[key] = l = new List<FileInfo>();
                l.Add(f);
            }
            stage2.AddRange(byEdge.Values.Where(l => l.Count > 1));
        }

        // stage 3: full SHA-256 confirms (files up to 128 KB were already read completely)
        totalBytes = stage2.Sum(g => g.Sum(f => f.Length)) + 1;
        done = 0;
        var hashes = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var all = stage2.SelectMany(g => g).ToList();
        Parallel.ForEach(all, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct }, f =>
        {
            try
            {
                hashes[f.FullName] = FileHasher.Sha256(f.FullName, n => Advance(n, "Verifying duplicates (SHA-256)…"), ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lock (result.Errors) result.Errors.Add($"{f.FullName}: {ex.Message}");
            }
        });
        result.BytesHashed = done;

        foreach (var group in stage2)
        {
            foreach (var same in group.Where(f => hashes.ContainsKey(f.FullName)).GroupBy(f => hashes[f.FullName]).Where(g => g.Count() > 1))
            {
                result.Groups.Add(new DuplicateGroup(same.First().Length, same.Key,
                    same.Select(f => new DuplicateFile(f.FullName, f.LastWriteTime)).OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList()));
            }
        }
        result.Groups.Sort((a, b) => b.Wasted.CompareTo(a.Wasted));
        return result;
    }

    private static string? EdgeFingerprint(FileInfo f, DuplicateResult result)
    {
        try
        {
            using var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buf = new byte[EdgeBytes];
            int n = fs.ReadAtLeast(buf, Math.Min(EdgeBytes, (int)Math.Min(int.MaxValue, fs.Length)), throwOnEndOfStream: false);
            h.AppendData(buf, 0, n);
            if (fs.Length > 2L * EdgeBytes)
            {
                fs.Seek(-EdgeBytes, SeekOrigin.End);
                n = fs.ReadAtLeast(buf, EdgeBytes, throwOnEndOfStream: false);
                h.AppendData(buf, 0, n);
            }
            else if (fs.Length > EdgeBytes)
            {
                n = fs.ReadAtLeast(buf, (int)(fs.Length - EdgeBytes), throwOnEndOfStream: false);
                h.AppendData(buf, 0, n);
            }
            return Convert.ToHexString(h.GetHashAndReset());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            lock (result.Errors) result.Errors.Add($"{f.FullName}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Which files to select for removal, keeping one per group by the given rule.</summary>
    public static List<string> SelectAllBut(IEnumerable<DuplicateGroup> groups, Func<IEnumerable<DuplicateFile>, DuplicateFile> keep) =>
        groups.SelectMany(g => { var k = keep(g.Files); return g.Files.Where(f => f != k).Select(f => f.Path); }).ToList();
}
