using System.Security.Principal;
using System.Text;

namespace Fyltrix.Core;

/// <summary>
/// Restores items from the current user's Recycle Bin by reading the $I metadata files
/// (that is where Windows records each item's original path). Used by Undo after a delete.
/// </summary>
public static class RecycleBin
{
    public sealed record Entry(string InfoFile, string DataPath, string OriginalPath, DateTime DeletedUtc, bool IsDirectory);

    /// <summary>Parses a $I file (Windows Vista+ v1 and Windows 10+ v2 formats).</summary>
    public static (string path, DateTime deletedUtc)? ParseInfo(byte[] data)
    {
        if (data.Length < 24) return null;
        long version = BitConverter.ToInt64(data, 0);
        long fileTime = BitConverter.ToInt64(data, 16);
        DateTime deleted;
        try { deleted = DateTime.FromFileTimeUtc(fileTime); } catch { deleted = DateTime.MinValue; }

        string path;
        if (version == 2)
        {
            if (data.Length < 28) return null;
            int chars = BitConverter.ToInt32(data, 24);
            int bytes = Math.Min(chars * 2, data.Length - 28);
            if (bytes <= 0) return null;
            path = Encoding.Unicode.GetString(data, 28, bytes);
        }
        else if (version == 1)
        {
            int bytes = Math.Min(520, data.Length - 24);
            path = Encoding.Unicode.GetString(data, 24, bytes);
        }
        else return null;

        int nul = path.IndexOf('\0');
        if (nul >= 0) path = path.Substring(0, nul);
        return string.IsNullOrWhiteSpace(path) ? null : (path, deleted);
    }

    private static string? BinFolderFor(string originalPath)
    {
        var root = Path.GetPathRoot(originalPath);
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\")) return null; // network shares have no Recycle Bin
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (sid == null) return null;
        var dir = Path.Combine(root, "$Recycle.Bin", sid);
        return Directory.Exists(dir) ? dir : null;
    }

    /// <summary>Finds the most recently recycled item that came from <paramref name="originalPath"/>.</summary>
    public static Entry? Find(string originalPath, DateTime notBeforeUtc)
    {
        var bin = BinFolderFor(originalPath);
        if (bin == null) return null;
        Entry? best = null;
        foreach (var info in Directory.EnumerateFiles(bin, "$I*"))
        {
            (string path, DateTime deletedUtc)? parsed;
            try { parsed = ParseInfo(File.ReadAllBytes(info)); } catch { continue; }
            if (parsed == null) continue;
            if (!string.Equals(parsed.Value.path, originalPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (parsed.Value.deletedUtc < notBeforeUtc) continue;

            var name = Path.GetFileName(info);
            var data = Path.Combine(bin, "$R" + name.Substring(2));
            bool isDir = Directory.Exists(data);
            if (!isDir && !File.Exists(data)) continue;
            if (best == null || parsed.Value.deletedUtc > best.DeletedUtc)
                best = new Entry(info, data, parsed.Value.path, parsed.Value.deletedUtc, isDir);
        }
        return best;
    }

    /// <summary>Moves a recycled item back to where it came from. Returns an error message or null.</summary>
    public static string? Restore(string originalPath, DateTime notBeforeUtc)
        => Restore(originalPath, Find(originalPath, notBeforeUtc.AddSeconds(-5)));

    /// <summary>Restores a specific entry (found earlier) to its original path.</summary>
    public static string? Restore(string originalPath, Entry? entry)
    {
        if (entry == null || !(File.Exists(entry.DataPath) || Directory.Exists(entry.DataPath)))
            return $"'{Path.GetFileName(originalPath)}' was not found in the Recycle Bin.";
        if (File.Exists(originalPath) || Directory.Exists(originalPath))
            return $"Can't restore '{originalPath}': something with that name already exists.";
        try
        {
            var parent = Path.GetDirectoryName(originalPath);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            if (entry.IsDirectory) Directory.Move(entry.DataPath, originalPath);
            else File.Move(entry.DataPath, originalPath);
            try { File.Delete(entry.InfoFile); } catch { /* harmless leftover */ }
            return null;
        }
        catch (Exception ex)
        {
            return $"Couldn't restore '{originalPath}': {ex.Message}";
        }
    }
}
