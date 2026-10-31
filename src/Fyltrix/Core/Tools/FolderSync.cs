using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fyltrix.Core.Tools;

public enum SyncMode { LeftToRight, RightToLeft, TwoWay }

public enum DiffKind
{
    Same, OnlyLeft, OnlyRight, LeftNewer, RightNewer,
    /// <summary>Same timestamp but different size or content.</summary>
    Different,
    /// <summary>Two-way: both sides changed since the last sync.</summary>
    Conflict,
    /// <summary>Two-way: existed on both sides at the last sync, now missing on the right / left.</summary>
    DeletedRight, DeletedLeft,
}

public enum SyncAction { None, CopyToRight, CopyToLeft, DeleteRight, DeleteLeft, Skip }

public sealed record FileStamp(long Size, DateTime ModifiedUtc);

public sealed class DiffItem
{
    public required string Path { get; init; }
    public bool IsDirectory { get; init; }
    public FileStamp? Left { get; init; }
    public FileStamp? Right { get; init; }
    public DiffKind Kind { get; set; }
    public SyncAction Action { get; set; }
}

public sealed class CompareOptions
{
    public bool Recursive { get; init; } = true;
    public bool IncludeHidden { get; init; }
    /// <summary>Hash files whose size and time match (slow, but catches silent changes).</summary>
    public bool VerifyContent { get; init; }
    /// <summary>FAT/exFAT store times with 2-second precision.</summary>
    public TimeSpan TimeTolerance { get; init; } = TimeSpan.FromSeconds(2);
}

public sealed class SyncOptions
{
    /// <summary>One-way: also remove items that only exist on the target (a mirror). Two-way: apply deletions made since the last sync.</summary>
    public bool PropagateDeletions { get; init; }
    /// <summary>One-way: replace target files even when the target copy is newer.</summary>
    public bool OverwriteNewer { get; init; }
}

public sealed class SyncResult
{
    public int CopiedToRight { get; set; }
    public int CopiedToLeft { get; set; }
    public int Recycled { get; set; }
    public List<string> Errors { get; } = new();
    public bool Cancelled { get; set; }
}

/// <summary>
/// Folder comparison and synchronisation. Nothing is deleted outright: files that would be overwritten or removed
/// are handed to a recycle callback (the Recycle Bin in the app). Deletions are only proposed when asked for.
/// </summary>
public static class FolderSync
{
    public static Dictionary<string, (bool isDir, FileStamp stamp)> Snapshot(string root, CompareOptions o, CancellationToken ct = default)
    {
        var map = new Dictionary<string, (bool, FileStamp)>(StringComparer.OrdinalIgnoreCase);
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = o.Recursive, IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | (o.IncludeHidden ? 0 : FileAttributes.Hidden | FileAttributes.System),
        };
        foreach (var info in new DirectoryInfo(root).EnumerateFileSystemInfos("*", opts))
        {
            ct.ThrowIfCancellationRequested();
            if (info.Name.EndsWith(".fl-partial", StringComparison.OrdinalIgnoreCase)) continue;
            bool dir = (info.Attributes & FileAttributes.Directory) != 0;
            map[System.IO.Path.GetRelativePath(root, info.FullName)] = (dir, new FileStamp(dir ? 0 : ((FileInfo)info).Length, info.LastWriteTimeUtc));
        }
        return map;
    }

    public static List<DiffItem> Compare(string left, string right, CompareOptions o, Baseline? baseline = null,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(new OperationProgress("Reading the left folder…"));
        var l = Snapshot(left, o, ct);
        progress?.Report(new OperationProgress("Reading the right folder…"));
        var r = Snapshot(right, o, ct);
        var items = new List<DiffItem>();
        var all = l.Keys.Union(r.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        int done = 0;
        foreach (var rel in all)
        {
            ct.ThrowIfCancellationRequested();
            if (++done % 200 == 0) progress?.Report(new OperationProgress("Comparing…", (double)done / all.Count, rel));
            bool inL = l.TryGetValue(rel, out var le), inR = r.TryGetValue(rel, out var re);
            var item = new DiffItem { Path = rel, IsDirectory = (inL ? le.isDir : re.isDir), Left = inL ? le.stamp : null, Right = inR ? re.stamp : null };
            if (inL && inR && le.isDir != re.isDir) { item.Kind = DiffKind.Conflict; items.Add(item); continue; } // file vs folder
            var b = baseline?.Get(rel);
            if (inL && !inR) item.Kind = b is { InRight: true } && Same(b.Left, le.stamp, o) ? DiffKind.DeletedRight : DiffKind.OnlyLeft;
            else if (!inL && inR) item.Kind = b is { InLeft: true } && Same(b.Right, re.stamp, o) ? DiffKind.DeletedLeft : DiffKind.OnlyRight;
            else if (item.IsDirectory) item.Kind = DiffKind.Same;
            else item.Kind = CompareFiles(System.IO.Path.Combine(left, rel), System.IO.Path.Combine(right, rel), le.stamp, re.stamp, b, o);
            items.Add(item);
        }
        return items;
    }

    private static bool Same(FileStamp? a, FileStamp? b, CompareOptions o) =>
        a != null && b != null && a.Size == b.Size && (a.ModifiedUtc - b.ModifiedUtc).Duration() <= o.TimeTolerance;

    private static DiffKind CompareFiles(string lp, string rp, FileStamp l, FileStamp r, Baseline.Entry? b, CompareOptions o)
    {
        if (Same(l, r, o))
        {
            if (!o.VerifyContent || l.Size == 0) return DiffKind.Same;
            return FileHasher.Sha256(lp) == FileHasher.Sha256(rp) ? DiffKind.Same : DiffKind.Different;
        }
        if (b != null && b.InLeft && b.InRight)
        {
            bool lChanged = !Same(b.Left, l, o), rChanged = !Same(b.Right, r, o);
            if (lChanged && rChanged) return DiffKind.Conflict;
            if (lChanged) return DiffKind.LeftNewer;
            if (rChanged) return DiffKind.RightNewer;
        }
        var diff = l.ModifiedUtc - r.ModifiedUtc;
        if (diff.Duration() <= o.TimeTolerance) return DiffKind.Different;
        return diff > TimeSpan.Zero ? DiffKind.LeftNewer : DiffKind.RightNewer;
    }

    /// <summary>Proposes an action for every item. Users can change any of them before running.</summary>
    public static void Plan(IEnumerable<DiffItem> items, SyncMode mode, SyncOptions o)
    {
        foreach (var i in items)
        {
            i.Action = (mode, i.Kind) switch
            {
                (_, DiffKind.Same) => SyncAction.None,
                (SyncMode.LeftToRight, DiffKind.OnlyLeft or DiffKind.LeftNewer or DiffKind.Different or DiffKind.DeletedRight) => SyncAction.CopyToRight,
                (SyncMode.LeftToRight, DiffKind.RightNewer or DiffKind.Conflict) => o.OverwriteNewer ? SyncAction.CopyToRight : SyncAction.Skip,
                (SyncMode.LeftToRight, DiffKind.OnlyRight or DiffKind.DeletedLeft) => o.PropagateDeletions ? SyncAction.DeleteRight : SyncAction.None,
                (SyncMode.RightToLeft, DiffKind.OnlyRight or DiffKind.RightNewer or DiffKind.Different or DiffKind.DeletedLeft) => SyncAction.CopyToLeft,
                (SyncMode.RightToLeft, DiffKind.LeftNewer or DiffKind.Conflict) => o.OverwriteNewer ? SyncAction.CopyToLeft : SyncAction.Skip,
                (SyncMode.RightToLeft, DiffKind.OnlyLeft or DiffKind.DeletedRight) => o.PropagateDeletions ? SyncAction.DeleteLeft : SyncAction.None,
                (SyncMode.TwoWay, DiffKind.OnlyLeft or DiffKind.LeftNewer) => SyncAction.CopyToRight,
                (SyncMode.TwoWay, DiffKind.OnlyRight or DiffKind.RightNewer) => SyncAction.CopyToLeft,
                (SyncMode.TwoWay, DiffKind.DeletedRight) => o.PropagateDeletions ? SyncAction.DeleteLeft : SyncAction.CopyToRight,
                (SyncMode.TwoWay, DiffKind.DeletedLeft) => o.PropagateDeletions ? SyncAction.DeleteRight : SyncAction.CopyToLeft,
                _ => SyncAction.Skip, // two-way Different / Conflict: the user decides
            };
        }
    }

    public static SyncResult Execute(string left, string right, IReadOnlyList<DiffItem> items, Func<IList<string>, bool> recycle,
        IProgress<OperationProgress>? progress = null, CancellationToken ct = default)
    {
        var result = new SyncResult();
        var work = items.Where(i => i.Action is not (SyncAction.None or SyncAction.Skip)).ToList();
        long total = Math.Max(1, work.Sum(i => (i.Action == SyncAction.CopyToRight ? i.Left?.Size : i.Action == SyncAction.CopyToLeft ? i.Right?.Size : 0) ?? 0)), done = 0;
        // folders first (parents before children), deletions last and deepest first
        var ordered = work.Where(i => i.Action is SyncAction.CopyToLeft or SyncAction.CopyToRight).OrderBy(i => i.IsDirectory ? 0 : 1).ThenBy(i => i.Path.Length)
            .Concat(work.Where(i => i.Action is SyncAction.DeleteLeft or SyncAction.DeleteRight).OrderByDescending(i => i.Path.Length)).ToList();
        try
        {
            foreach (var i in ordered)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new OperationProgress("Synchronising…", (double)done / total, i.Path));
                try
                {
                    switch (i.Action)
                    {
                        case SyncAction.CopyToRight:
                            Copy(System.IO.Path.Combine(left, i.Path), System.IO.Path.Combine(right, i.Path), i.IsDirectory, recycle);
                            result.CopiedToRight++;
                            done += i.Left?.Size ?? 0;
                            break;
                        case SyncAction.CopyToLeft:
                            Copy(System.IO.Path.Combine(right, i.Path), System.IO.Path.Combine(left, i.Path), i.IsDirectory, recycle);
                            result.CopiedToLeft++;
                            done += i.Right?.Size ?? 0;
                            break;
                        case SyncAction.DeleteRight or SyncAction.DeleteLeft:
                            var target = System.IO.Path.Combine(i.Action == SyncAction.DeleteRight ? right : left, i.Path);
                            if (!File.Exists(target) && !Directory.Exists(target)) break; // already gone with its folder
                            if (!recycle(new[] { target })) throw new IOException("Couldn't move it to the Recycle Bin.");
                            result.Recycled++;
                            break;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    result.Errors.Add($"{i.Path}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { result.Cancelled = true; }
        return result;
    }

    private static void Copy(string from, string to, bool isDir, Func<IList<string>, bool> recycle)
    {
        if (isDir) { Directory.CreateDirectory(to); return; }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to)!);
        var temp = to + ".fl-partial";
        try
        {
            File.Copy(from, temp, overwrite: true); // keeps the modified time
            if (File.Exists(to) && !recycle(new[] { to })) throw new IOException("Couldn't move the old copy to the Recycle Bin.");
            File.Move(temp, to);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    // ------------------------------------------------------------------ two-way baseline

    /// <summary>State of both folders after the last sync, kept in %AppData%\FileLens\sync.</summary>
    public sealed class Baseline
    {
        public sealed class Entry
        {
            public bool InLeft { get; set; }
            public bool InRight { get; set; }
            public FileStamp? Left { get; set; }
            public FileStamp? Right { get; set; }
        }

        public Dictionary<string, Entry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public DateTime TakenUtc { get; set; }

        public Entry? Get(string rel) => Entries.TryGetValue(rel, out var e) ? e : null;

        private static string FileFor(string left, string right)
        {
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(left).ToLowerInvariant() + "|" + System.IO.Path.GetFullPath(right).ToLowerInvariant())), 0, 10);
            return System.IO.Path.Combine(AppSettings.Folder, "sync", key + ".json");
        }

        public static Baseline? Load(string left, string right)
        {
            try
            {
                var f = FileFor(left, right);
                if (!File.Exists(f)) return null;
                var b = JsonSerializer.Deserialize<Baseline>(File.ReadAllText(f));
                if (b != null) b.Entries = new Dictionary<string, Entry>(b.Entries, StringComparer.OrdinalIgnoreCase);
                return b;
            }
            catch (JsonException) { return null; }
            catch (IOException) { return null; }
        }

        public static void Save(string left, string right, CompareOptions o)
        {
            var l = Snapshot(left, o);
            var r = Snapshot(right, o);
            var b = new Baseline { TakenUtc = DateTime.UtcNow };
            foreach (var k in l.Keys.Union(r.Keys, StringComparer.OrdinalIgnoreCase))
            {
                bool inL = l.TryGetValue(k, out var le), inR = r.TryGetValue(k, out var re);
                b.Entries[k] = new Entry { InLeft = inL, InRight = inR, Left = inL ? le.stamp : null, Right = inR ? re.stamp : null };
            }
            var f = FileFor(left, right);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(f)!);
            File.WriteAllText(f, JsonSerializer.Serialize(b));
        }
    }
}
