using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Fyltrix.Core.Archives;

/// <summary>
/// Lets the file list treat an archive as a read-only folder. Virtual paths look like real ones:
/// <c>C:\Downloads\photos.zip\2024\beach.jpg</c>. Listings are cached per archive (invalidated when the file
/// changes); entries are extracted on demand into a per-process cache for previewing and opening.
/// </summary>
public static class ArchiveBrowser
{
    private sealed record Listing(DateTime ModifiedUtc, long Size, List<ArchiveEntryInfo> Entries);

    private static readonly Dictionary<string, Listing> Listings = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> Passwords = new(StringComparer.OrdinalIgnoreCase); // session only, never saved
    private static readonly object Gate = new();

    public static string CacheBase { get; private set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileLens", "ArchiveCache");
    public static string CacheRoot { get; private set; } = Path.Combine(CacheBase, Environment.ProcessId.ToString());
    internal static void SetTrialCache(string folder) { CacheBase = Path.GetFullPath(folder); CacheRoot = Path.Combine(CacheBase, Environment.ProcessId.ToString()); }

    /// <summary>Splits a virtual path into the archive file and the folder inside it ('/'-separated, "" = root).</summary>
    public static bool TryParse(string path, out string archiveFile, out string inner)
    {
        archiveFile = "";
        inner = "";
        if (string.IsNullOrEmpty(path) || path.Length < 4) return false;
        if (File.Exists(path))
        {
            if (!ArchiveFormats.IsArchive(path)) return false;
            archiveFile = path;
            return true;
        }
        for (int i = path.LastIndexOf('\\'); i > 2; i = path.LastIndexOf('\\', i - 1))
        {
            var prefix = path.Substring(0, i);
            if (Directory.Exists(prefix)) return false;
            if (File.Exists(prefix))
            {
                if (!ArchiveFormats.IsArchive(prefix)) return false;
                archiveFile = prefix;
                inner = path.Substring(i + 1).Replace('\\', '/').Trim('/');
                return true;
            }
        }
        return false;
    }

    public static string VirtualPath(string archiveFile, string entryKey) =>
        archiveFile.TrimEnd('\\') + "\\" + entryKey.Replace('/', '\\');

    public static string? KnownPassword(string archiveFile)
    {
        lock (Gate) return Passwords.TryGetValue(archiveFile, out var p) ? p : null;
    }

    public static void RememberPassword(string archiveFile, string password)
    {
        lock (Gate) Passwords[archiveFile] = password;
    }

    public static List<ArchiveEntryInfo> GetEntries(string archiveFile, CancellationToken ct = default)
    {
        var fi = new FileInfo(archiveFile);
        lock (Gate)
        {
            if (Listings.TryGetValue(archiveFile, out var l) && l.ModifiedUtc == fi.LastWriteTimeUtc && l.Size == fi.Length) return l.Entries;
        }
        var entries = ArchiveService.List(archiveFile, KnownPassword(archiveFile), ct);
        lock (Gate)
        {
            Listings[archiveFile] = new Listing(fi.LastWriteTimeUtc, fi.Length, entries);
            if (Listings.Count > 20) Listings.Remove(Listings.Keys.First(k => !k.Equals(archiveFile, StringComparison.OrdinalIgnoreCase)));
        }
        return entries;
    }

    /// <summary>Direct children of <paramref name="inner"/>. Throws if that folder isn't in the archive.</summary>
    public static List<ArchiveEntryInfo> Children(IReadOnlyList<ArchiveEntryInfo> entries, string inner)
    {
        inner = inner.Trim('/');
        if (inner.Length > 0 && !entries.Any(e => e.IsDirectory && e.Path.Equals(inner, StringComparison.OrdinalIgnoreCase)))
            throw new DirectoryNotFoundException($"'{inner}' is not a folder in this archive.");
        var prefix = inner.Length == 0 ? "" : inner + "/";
        var byName = new Dictionary<string, ArchiveEntryInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            if (e.Path.Length <= prefix.Length || !e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = e.Path.Substring(prefix.Length);
            if (rest.Contains('/')) continue;
            // a later duplicate (archives can be appended to) replaces an earlier one, as extraction would
            if (!byName.TryGetValue(rest, out var existing) || !e.IsDirectory || existing.IsDirectory) byName[rest] = e;
        }
        return byName.Values.ToList();
    }

    private static string ArchiveId(string archiveFile)
    {
        var fi = new FileInfo(archiveFile);
        var raw = $"{archiveFile.ToLowerInvariant()}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)), 0, 8);
    }

    /// <summary>Extracts one entry into the cache (or reuses it) and returns the local path. The copy is marked read-only.</summary>
    public static string ExtractToCache(string archiveFile, string entryKey, ArchiveLimits limits, CancellationToken ct = default)
    {
        var dir = Path.Combine(CacheRoot, ArchiveId(archiveFile));
        var target = ArchiveService.ResolveSafePath(dir, entryKey, out var reason) ?? throw new IOException($"'{entryKey}': {reason}");
        if (File.Exists(target)) return target;
        var temp = target + ".dl";
        if (File.Exists(temp)) File.Delete(temp);
        ArchiveService.ExtractEntryToFile(archiveFile, entryKey, temp, KnownPassword(archiveFile), limits, ct);
        ZoneInfo.CopyMark(archiveFile, temp);
        File.Move(temp, target);
        File.SetAttributes(target, File.GetAttributes(target) | FileAttributes.ReadOnly);
        return target;
    }

    /// <summary>A fresh folder inside the cache for extracting a selection (clipboard, drag and drop).</summary>
    public static string NewStagingFolder()
    {
        var dir = Path.Combine(CacheRoot, "out-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Removes this process's cache (on exit) and caches left behind by instances that are no longer running.</summary>
    public static void CleanupCache(bool includeOwn)
    {
        if (!Directory.Exists(CacheBase)) return;
        foreach (var dir in Directory.EnumerateDirectories(CacheBase))
        {
            bool own = dir.Equals(CacheRoot, StringComparison.OrdinalIgnoreCase);
            if (own && !includeOwn) continue;
            if (!own && int.TryParse(Path.GetFileName(dir), out var pid) && IsRunningApplication(pid)) continue;
            DeleteTree(dir);
        }
    }

    private static bool IsRunningApplication(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return IsApplicationProcessName(p.ProcessName, Process.GetCurrentProcess().ProcessName);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    // Old and new executables share this cache. Never remove a running legacy instance's entries during an upgrade.
    internal static bool IsApplicationProcessName(string name, string currentName) =>
        name.Equals(currentName, StringComparison.OrdinalIgnoreCase)
        || name.Equals("Fyltrix", StringComparison.OrdinalIgnoreCase)
        || name.Equals("FileLens", StringComparison.OrdinalIgnoreCase);

    private static void DeleteTree(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            Directory.Delete(dir, true);
        }
        catch (IOException) { /* a cached file is still open in another app; it goes next time */ }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>"Downloaded from the internet" marks (Zone.Identifier streams) travel with extracted files, as in Explorer.</summary>
public static class ZoneInfo
{
    public static string? Read(string path)
    {
        try { return File.Exists(path + ":Zone.Identifier") ? File.ReadAllText(path + ":Zone.Identifier") : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static void Write(string path, string? zone)
    {
        if (string.IsNullOrEmpty(zone)) return;
        try { File.WriteAllText(path + ":Zone.Identifier", zone); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void CopyMark(string from, string to) => Write(to, Read(from));
}
